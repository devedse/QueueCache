# RAM-first cache contract and implementation plan

Historical design audit, 2026-09-17. Execution is now authorized and tracked in
RAM_FIRST_IMPLEMENTATION_TRACKER.md; consult its per-item status. Supersedes the prioritization in WRITE_PERFORMANCE_TRAJECTORY.md, not
its preserved evidence. The most recent write baseline is incomplete: 31 measured
cases, case 32 failed observer readiness, successful restoration. No complete-run
medians or causal timing-on/off claim may be derived from it.

## Product principle

In explicitly selected volatile Fast mode, a valid supported write that fits the
available admission budget must not require lower-device reads, writes or flushes
before acknowledgement. Commit its bytes and ordering metadata to owned RAM, then
acknowledge. Fully cached reads likewise should not depend on unrelated disk work.
Background draining must not impose global foreground waits merely because it is
running. Priority is write admission, cached reads, then background draining, with
bounded fairness rather than indefinite read/drainer starvation.

This is **no incidental foreground drains**, not **no durability boundaries**.
Retain Strict mode, explicit Flush now, release/disable, safe removal and shutdown
semantics. Capacity/backpressure and real errors cannot be hidden. Fast-mode
acknowledgement means volatile ownership, not durable storage; it deliberately
changes application flush/write-through guarantees and must not be presented as
equivalent to Strict mode. Never silently weaken Strict to improve scores.

The disk-disconnected analogy becomes a deterministic test: with an online device
identity, no in-flight disk operations, ample capacity and a no-early-drain policy,
write-only requests complete and cached reads return exact bytes while lower data
I/O is gated. Count attempted lower reads/writes/flushes, not just completions.
Physical surprise removal is different: Windows tears down the device/volume;
do not promise that applications can continue using the removed drive. Filesystem
operations may also need uncached metadata reads. Offline virtual-volume support
is a separate architecture/product decision, not a filter optimization.

## Timer semantics and scope

Today MaxAgeMs is capped at 300000 in driver, management and UI. QcShouldDrain
combines pressure, Eager, dirty age, Idle and forced triggers. Merely raising age
to one hour will NOT provide an hour without disk writes. Design an explicit
deferred/manual policy with independently documented permitted triggers, including
capacity emergency, explicit user flush and lifecycle/durability boundaries.
Distinguish maximum dirty age (start draining by an age, subject to device progress)
from minimum residence time (do not drain earlier absent named exceptions).
Specify first-dirty versus last-overwrite age; default to first-dirty for maximum
age to prevent repeated overwrites postponing draining forever. Eager remains an
immediate background policy, not an implicit foreground barrier.

## Ordered audit and work packages

Percentages below are low-confidence planning hypotheses, NOT measured promises.
They apply only to the named metric and do not add together. Zero is included where
benefit depends on exercising a path. Do not approve a change based on these ranges.

| Order | Area and current evidence | Proposed change / decision | Potential effect and required proof |
|---|---|---|---|
| 1 | Observer startup failed before any telemetry file existed; precise native call unknown. Barrier reasons/lengths are missing. | Durable worker startup-stage breadcrumbs; versioned write-barrier reason/size/alignment counters; expose all drain triggers. Preserve readiness validation/timeouts. | 0% intended throughput gain; unlocks attribution. Fault/hang each stage in host-safe tests and verify useful failure output/restoration. |
| 2 | No policy implements the requested hour-long hold; age cap five minutes. | Define deferred/manual trigger contract, extend bounds coherently through wire validation/CLI/UI/profiles, document age semantics and errors. | 0% intrinsic RAM-copy gain; avoids early disk interference. Zero-lower-I/O gated test before expiry, then explicit expiry/pressure/flush tests. |
| 3 | Write() invokes QcCacheBarrier for 4 KiB-partial/unaligned writes. That drains unrelated data. Stalled samples had active writes, drain phase and no capacity waits. Exact trigger still unproven. | Prefer sector-valid/dirty masks in blocks, or a sector-granular representation. A 512-byte write owns only those bytes; never read missing bytes merely to acknowledge it. Merge reads with disk only for missing sectors. Drain valid dirty sectors without overwriting unknown neighbours. Evaluate memory overhead before choosing layout. | Main candidate: 0–260% on a 22 MB/s-like Q1 workload IF stalls explain the gap to observed ~80 MB/s; this is an illustrative recovery envelope, not forecast. Healthy ~80 MB/s cells may gain 0%. Test partial/full/overlapping sectors and disk oracle. |
| 4 | Same Write() fallback for request larger than write quota, disabled-with-dirty and zero length; strict write-through shares branch. | Split admission for large requests with explicit completion/error/cancellation semantics; handle valid zero-length requests without drain; keep disabled/Strict behaviour intentional. Requests larger than capacity cannot satisfy all-in-RAM admission without waiting. | 0–20% for workloads that trigger fallback; ordinary fitting 4 KiB 0%. Boundary sizes, quotas 0/100%, mixed cancellation, disk-sector alignment. |
| 5 | Hotplug query was a read-selection fence. GET fix already pushed in 4dacd5e, not baseline driver. Other observations can still serialize through the worker. | Verify the fix; audit explicitly read-only controls and admit only proven-safe queries. Distinguish ordering fence from actual drain and from clean-cache invalidation. | Isolated write throughput likely 0%; conditional 0–100%+ hot-reader throughput under affected control traffic. Focused flush/control tests; GET safe, SET/TRIM/pass-through still ordered. |
| 6 | In-flight/pinned versions require extra slots; one mutex protects metadata; writes and drainers copy/share metadata paths. | Independent immutable drain versions, bounded reserve for rewrites, narrow metadata ownership and no device waits under global cache locks. Avoid unsafe slot recycling. | Hypothesis 0–30% loaded small-write throughput / better tails. Slow drain + same-sector overwrite, parallelism 1/2/4, allocation failures, pin retirement and fault tests. |
| 7 | RequestWorker remains serial; capacity waits and barriers hold its foreground position. Read bypass is bounded/cooperative. | Separate pending-capacity admission from servicing independent ready requests; range/order fences and bounded fairness. No bypass of overlapping older writes or true durability boundaries. | 0–50%+ mixed throughput; little Q1-only gain when no waits. Deep queues, cancellation/reinsertion, overlapping/nonoverlapping requests and starvation checks. |
| 8 | Fixed quotas and Automatic read protection can cause pressure despite apparent free total budget. | Expose admission budget separately from total free RAM. Explicit Fixed must respect its split; Automatic may borrow clean capacity before blocking. Reserve transient versioning overhead without silently stealing Fixed quota. | 0–20% pressured workloads; fitting resident workloads near 0%. Quotas, memory pressure, giant requests, shared multi-disk budget. |
| 9 | Small writes still do lookup/preflight, snapshots and synchronization per request. Wake coalescing already exists. | Profile fixed work, batch/defer noncritical publication, remove redundant lookups/signals; keep errors/control state fresh and bounded telemetry latency. | Low-confidence 5–25% on healthy CPU-limited 4 KiB writes; may be 0 if scheduler/disk dominates. Timing-off headline runs, timing-on attribution, CPU/request, Q1/Q32 and reader regression. |
| 10 | TryTrim waits for ALL in-flight writes; partial/unsupported shapes fall back to broad drain. | Per-range discard generations/tombstones, overlap-only in-flight coordination and deferred lower TRIM where semantics allow. Never allow an old in-flight write to resurrect discarded/newer data. Unknown actions remain conservative. | Isolated CDM write score usually 0%; 0–50%+ under concurrent deletes/TRIM. Partial/overlap/reuse, range bounds/counts, failed TRIM, delayed old writes. |
| 11 | Explicit flush/disable/release/retry use QcCacheBarrier. Strict application flush does too. Management options require disabled clean state. | Keep required durability. Consider epoch/cutoff flush so new unrelated writes need not make a flush unbounded; live policy-only changes where safe; transactional resize. Retry must report failure honestly. | 0% isolated score; 0–50%+ concurrent throughput. Prove cutoff contents durable before success; test concurrent post-cutoff writes, failed flush, resize and cancellation. |
| 12 | Cold reads still wait for disk, competing with random drains; prior mixed Q1 was 1.21 vs 7.81 MB/s uncached. | Foreground-aware lower-I/O scheduling, adaptive bounded drain depth/batching; prioritize cache admission and hits without starving cold reads or eventual drain. | Conditional 0–500% mixed recovery envelope; no predicted 4 KiB RAM-only gain. Cold read + writer with storage latency, sustained over-budget workloads and disk drain throughput. |
| 13 | Multiple foreground misses remain serialized; deep queue scanning costs remain. | Only if residual profiles justify: indexed ready-read selection, then independent-range workers with explicit flush/TRIM ordering. Not a blind thread-count increase. | 0–100%+ QD32 on fast storage; Q1 generally 0%. Hardware-specific; race/fault/cancel and ordered-byte tests mandatory. |
| 14 | Power/shutdown/PnP and worker teardown call broad barriers. Surprise removal cannot successfully drain a missing disk. | Audit IRP minor/type handling separately; drain safe boundaries, fail/retain/report appropriately on loss, never wait indefinitely or report lost volatile data durable. OS disk/paging/hibernate are distinct high-risk validation. | 0% benchmark gain; lifecycle correctness and availability. Dedicated VM lifecycle tests, not automatic recovery/reboot in a benchmark. |
| 15 | UI hit/eviction counters are lifetime, not current test; evidence windows include warmup and close. | Windowed stats plus explicit lifetime labels, trigger/wait visibility, byte ownership vs persistence; record exact score intervals and compare identical binary/settings. | 0% driver gain; prevents wrong optimization decisions. UI/CLI parity, stale samples and accounting tests. |

## Verification gates (maintain the existing CLI, no new orchestration harness)

1. Host-safe contracts: policy truth table including deferred age/pressure; overflow
   and boundary validation; suite IDs, options, startup errors, partial outputs,
   restoration, cancellation and strict XML parsing. Synthetic-clock tests for the
   one-hour policy, plus one real unattended hour-long soak before acceptance.
2. Add a focused maintained `ram-admission` suite (proposed, not implemented).
   Online target, lower data-I/O gate/counters, workload fits payload plus metadata
   and version reserves, no prior in-flight I/O, no OS cache. Gate must be bounded
   with independent cleanup. Full/partial/unaligned-to-4K but sector-valid writes
   and cached reads must finish while lower attempts remain zero. Cold reads are a
   separate negative control. Test direct block admission separately from filesystem
   activity, which may need cold metadata. Only explicit disposable ranges, never
   uncontrolled raw writes to mounted filesystem sectors.
3. Byte correctness: deterministic seeded sectors, unchanged neighbours, same-range
   overwrite while older version drains, partial reads across valid/invalid sectors,
   parallelism 1/2/4, failures/retry, cancellation and reclamation. Check from RAM,
   then drain, drop clean and reread exact disk bytes. Implement missing historical
   concurrency cases in the supported runner; do not claim quick/policies cover them.
4. Preserve Strict and explicit Flush now contracts. Verify lower flush completion
   precedes durability success; Fast mode remains visibly volatile. TRIM/reuse and
   control GET/SET ordering get separate positive and negative tests.
5. Run `write-performance --budget-mib 2048` with same DiskSpd hash, 3+ repetitions,
   matching queue depths, timing on/off, phase traces and unique raw evidence.
   Fix collection before a fresh complete baseline; retain but do not merge the
   failed batch. If observer overhead suspected, add a defined observer comparison
   rather than silently removing readiness/coverage validation.
6. Focused flush-interference + cold/mixed/drain pressure regressions after relevant
   changes. Add named focused selection to the maintained runner if needed instead
   of rerunning full for every small patch. Track p99/p99.9/max and no-completion
   windows, not just throughput. Storage drift: alternate configurations and report
   median/min/max, CPU, admitted/drained bytes and RAM footprint. Do not add proposed
   percentage gains or call coalesced RAM bytes physical disk throughput.
7. Whole full suite and dedicated lifecycle pass at milestones. Requirements:
   exact bytes, no errors/deadlock/leak/starvation, ownership/lifetime safety,
   complete evidence and verified restoration. No hidden weaker durability,
   missing fields as zero, deadline removal or swallowed faults to manufacture PASS.
   Proposed regression gate: investigate >5% median healthy-path slowdown or >10%
   tail increase when repeatable beyond observed spread; inconclusive results require
   more repetitions, not automatic pass/fail from a noisy VM.

## Sources and audited entry points

- driver/qcache/writecache.cpp: Write, Read, Drainer, QcCacheBarrier, Control,
  TryTrim and QcCacheProcess.
- driver/qcache/driver.cpp: RequestWorker, queue selection, PnP/remove lifetime.
- driver/qcache/cachepolicy.h: QcValidOptions, QcWriteLimit, QcShouldDrain.
- src/QueueCache.Management/CacheOptions.cs and desktop CacheSettingsWindow.cs:
  maximum-age validation/controls.
- Windows flush contract: https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/irp-mj-flush-buffers
- Surprise removal: https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/handling-an-irp-mn-surprise-removal-request

This is an exhaustive worklist for the currently audited paths and known findings,
not a claim that unobserved storage-stack behaviour or all future bugs are known.
