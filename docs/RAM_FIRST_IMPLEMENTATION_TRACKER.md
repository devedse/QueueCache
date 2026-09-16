# RAM-first cache: contract, implementation tracker and verification

Last updated: 2026-09-17. This is the authoritative execution tracker. Detailed
audit/rationale: [RAM_FIRST_PERFORMANCE_PLAN.md](RAM_FIRST_PERFORMANCE_PLAN.md).
Statuses distinguish source implementation from VM verification. No performance
gain is claimed until measured. Keep each row current in the implementing commit.

## Firm contract

In explicitly selected volatile Fast/deferred mode, while the device remains
present and sufficient admission RAM is available, acknowledging a valid supported
write must not depend on lower-device reads, writes or flushes. Partial writes,
background activity and implementation limitations are NOT exceptions. Own the
bytes and ordering metadata in RAM before acknowledgement. Background scheduling
must not impose a global foreground drain. Priority: write admission, cached reads,
then background drain, with bounded fairness for other work.

Named boundaries, not an open-ended "unless unavoidable" escape clause:

* Capacity exhausted by pending/versioned data: wait, reject, or acquire memory;
  never overwrite acknowledged data. Explicit Fixed write quotas remain meaningful.
* Explicit Flush now and Strict durability requests must wait for persistence;
  this need not stop all later unrelated write admission.
* Cache removal/shutdown must persist, retain elsewhere or explicitly discard
  pending data. Never silently free acknowledged volatile bytes.
* Device removal/failure cannot be disguised as success; continuing an offline
  Windows volume is a separate architecture, not promised by a storage filter.
* Cold reads may require disk access but must not force unrelated dirty data out.

One-hour deferral means no scheduled lower writes before first-dirty age reaches
one hour, except capacity, explicit durability and lifecycle boundaries. Idle and
high-watermark triggers must not secretly shorten this policy. A maximum dirty-age
trigger is not a deadline by which slow storage has necessarily persisted data.
Fast RAM acknowledgement is explicitly volatile, never equivalent to Strict.

## Ordered worklist

Percentages are low-confidence hypotheses for the specified workload, NOT promises,
not additive, and include zero where the path might not occur. Recovery envelopes
use illustrative observed slow/healthy numbers rather than guaranteed causal gains.

| # | Area / change | Expected improvement / metric | Implementation status | Verification status / required checks |
|---|---|---|---|---|
| 1 | Barrier reason/size/alignment attribution and durable observer-startup breadcrumbs | 0% directly; enables attribution | Partial: worker startup stages logged/flushed to stderr; barrier attribution pending | Local build/contracts; startup fault/hang tests and VM attribution pending, no relaxed readiness |
| 2 | Explicit Deferred policy with one-hour bounds, no idle/watermark early drain | 0% intrinsic copy gain; removes early interference | Implemented: driver/management/CLI/UI, capability-gated; existing partial-write barriers still apply | Native truth table and managed/UI checks; real one-hour soak pending |
| 3 | Cache sector-valid partial writes without reading disk or draining whole cache | 0–260% affected Q1 recovery envelope (~22 to ~80 MB/s); healthy path may gain 0% | In design | Seeded partial/full overlaps, unchanged neighbours, pinned/in-flight versions, RAM and disk byte oracle |
| 4 | Zero-length/oversized/quota fallback handling | 0–20% affected cases; ordinary fitting 4 KiB often 0% | Partial: valid zero-length writes return without draining; oversized/quota work pending | Native compile; VM no-I/O and request/quota/failure/cancel tests pending |
| 5 | Proven-safe observation/query fences | Isolated writes ~0%; affected hot-reader traffic 0–100%+ | Hotplug GET allowlist implemented in 4dacd5e | Native compile passed; focused VM comparison pending; SET remains fenced |
| 6 | Independent drain versions, bounded copy/metadata locking, transient reserves | 0–30% writes during draining | Partial: existing pins and unlocked copies; further work pending | Slow lower I/O + overwrites, parallelism 1/2/4, allocation failure and lifetime checks |
| 7 | Independent ready-request service around capacity waits/fences | 0–50%+ mixed throughput; unstalled Q1 little gain | Partial: cooperative read lane exists; general admission work pending | Deep queues, ordering, cancel/reinsert, starvation |
| 8 | Admission budget clarity and Automatic clean-space borrowing | 0–20% under pressure; fitting cases ~0% | Pending | Fixed 0/50/100%, Automatic, transient versions, multi-disk budget |
| 9 | Per-4KiB lookup/publication/synchronization overhead | Hypothesis 5–25% CPU-limited; 0% if waits dominate | Pending; wake coalescing already exists | CPU/request, timing on/off, Q1/Q32, snapshot freshness |
| 10 | Range-aware TRIM instead of broad drain/in-flight waits | Isolated writes ~0%; concurrent delete workloads 0–50%+ | Pending | Partial ranges, reuse, overlapping old writes, malformed/failed requests |
| 11 | Cutoff flush, safe live policy changes, transactional resize | Isolated writes 0%; concurrent workloads 0–50%+ | Pending | Exact durable cutoff, concurrent writes, Strict flush, failure/cancel/resize |
| 12 | Foreground cold-read versus drain scheduling | 0–500% mixed recovery envelope; no RAM-only promise | Pending | Mixed/cold + slow disk, sustained capacity pressure, bounded drain progress |
| 13 | Indexed ready selection / independent-range workers if still justified | 0–100%+ high QD; Q1 usually 0% | Deferred until remaining profiles justify redesign | Range ordering, barriers, cancellation, faults, cross-thread lifetime |
| 14 | Power/shutdown/PnP/removal boundaries | 0% throughput; reliability | Pending audit | Dedicated disposable VM lifecycle tests; no automatic destructive recovery |
| 15 | Windowed UI statistics, trigger/wait visibility and faithful evidence windows | 0% driver gain; trustworthy analysis | Partial: repeatable write suite/report exists; UI work pending | UI/CLI parity, sample staleness, interval/lifetime labels |

## Execution and verification gates

Implement data-path/policy changes first with local correctness checks; expand
performance scenarios afterward. Do not deploy an unverified kernel rewrite just
to collect a speed number. Keep commits independently reviewable.

1. Native compile/static policy/storage checks and managed contract tests accompany
   each implementation. Desktop tests accompany frontend changes.
2. Build a maintained RAM-admission scenario, not another private orchestration
   script: online identity, bounded lower-data-I/O gate, enough RAM and no prior
   in-flight work. Supported writes and cached reads complete with ZERO lower read,
   write or flush attempts. Filesystem cold metadata reads are a separate case.
3. Verify deterministic exact bytes from RAM and again after drain/drop-clean.
   Include all sector offsets, overlapping writes, in-flight old/new versions,
   retention on/off, cancellation, faults, retry, TRIM/reuse, allocation failure.
4. Preserve Strict and explicit-flush durability. Exercise capacity boundary and
   fairness: prioritization must not hide starvation or overwrite pending data.
5. Reuse `qcache developer verify --suite write-performance --budget-mib 2048`
   with the same DiskSpd hash. Fresh complete 72-case baseline, three repeats,
   alternating modes, timing on/off, Q1/Q32 and sequential Q1/Q8. Never merge the
   incomplete prior run into complete-run medians. Collect tail latency and
   zero-completion windows, CPU, memory overhead and admitted versus drained bytes.
6. Focused flush-interference/cold-read tests after relevant changes; full suite
   and lifecycle at milestones. Investigate repeatable >5% healthy-throughput loss
   or >10% tail regression beyond measured VM spread. No manufactured PASS from
   missing data, relaxed timeouts or weaker durability.

## Evidence checkpoint

Baseline driver de76288 / 0.4.37.1; CLI focused suite added by 36969d1. Baseline
20260916-213221 stopped at case 32 observer readiness; 31 measured cases only.
Restoration succeeded, dirty/error zero. Healthy timing-off random Q1 samples were
~80–83 MB/s; several other cells stalled, with active write/drain phase and no
capacity throttles. Exact broad-barrier trigger is not yet captured. These facts
justify investigation, not a complete before/after performance claim.
