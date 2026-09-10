# Performance and concurrency plan

Ordered work plan for the foreground/background interference and small-request cost problems.
Written to be executed by one implementing agent (or person) phase by phase. The measurements
behind it are in [performance baseline and method](PERFORMANCE.md); the runtime semantics being
preserved are in [cache policies](CACHE_POLICIES.md).

**Design goal being added:** foreground/background interference is now an explicit requirement,
not a side effect of thread count.

> A RAM-cache hit, or a cached write with capacity available, must not wait for unrelated disk
> I/O, nor for lengthy background work holding a shared lock.

What that requirement deliberately does **not** promise: identical speed regardless of
background activity. Foreground and background paths still share CPU and memory bandwidth, a
full dirty cache must still throttle writers, and a real Flush must still wait for storage. The
distinction to optimize is **unavoidable resource sharing versus avoidable serialization**.

## 1. What the baseline settled, and what it did not

| Finding | Conclusion supported | Conclusion **not** supported yet |
|---|---|---|
| Cached random writes barely scale with queue depth | There is a substantial processing bottleneck, and the single request worker in `driver/qcache/lab.cpp` is a strong architectural constraint | How much time goes to locks, events, bookkeeping, scheduling or background interference |
| Four threads slower than one at equal total queue depth | Extra submitting threads did not help in this run | The size or repeatability of that penalty; it is one sample |
| Little's law matches measured latency | Outstanding requests, throughput and latency are mutually consistent | That latency is "100% queueing"; Little's law holds for parallel systems too |
| ~35 µs per completed random write | It is the reciprocal of aggregate throughput for that cell | That it is 35 µs of CPU inside the driver, or that an assumed memcpy cost proves "99% fixed overhead" |
| Eager, Balanced and Idle within 9% at 4 KiB | No demonstrated policy winner | That the larger sequential differences come from policy; order, workload history and host drift were uncontrolled |
| Zero throttling and zero eviction | Capacity-pressure paths were never exercised | That no foreground/background overlap happened — Eager drains while foreground traffic runs; the experiment just could not isolate it |
| Random drain 6.6 MiB/s | A serious practical problem for sustained random writes and for Flush duration | How that splits between small lower writes, submission depth, storage latency and driver scheduling |
| Clean data survived the whole run | Good evidence the earlier invalidation defect does not recur in this workload | Comprehensive validation of every invalidation, TRIM and media-changing path |

Also treat large write numbers as **application-acknowledged throughput**, never disk
throughput. Repeatedly replacing the same cached blocks is legitimate caching behaviour and a
completely different workload from writing new data larger than RAM.

**Consequence for the plan:** there is enough evidence to justify improving the architecture,
and not enough to blame a specific lock, event or drain policy. So the order changes: stable
block ownership and moving copies out of locks remain essential, but they are no longer first.
Measure, then reduce obvious per-request cost, then fix draining, then rebuild ownership, then
reschedule. Do **not** open with a multithreading rewrite.

## Phase order at a glance

| Phase | Goal | Gate to the next phase |
|---|---|---|
| 1 | Make waiting and contention observable | A slow operation can be explained as queue wait, capacity wait, lock contention or lower-device time |
| 2 | Controlled interference and capacity baseline | Repeated results separate policy differences from run-to-run variation |
| 3 | Reduce avoidable per-request overhead | Repeatable small-request gain with unchanged correctness |
| 4 | Improve random draining | Fewer lower requests or better drain throughput, no starvation |
| 5 | Stable block ownership, short critical sections | Delayed lower I/O no longer blocks unrelated RAM operations |
| 6 | Dependency-aware foreground scheduling | A full-cache writer no longer blocks an independent RAM-hit reader |

## Phase 1 — Instrument the existing architecture

Change no caching behaviour. Make time attributable.

| Add | Purpose |
|---|---|
| Request queue depth and oldest queued request age | Show requests waiting behind other work |
| Active request type and operation phase | Distinguish read miss, capacity wait, drain barrier and lower flush |
| Queue-wait and request-processing histograms | Separate waiting before execution from executing |
| Foreground and drainer mutex wait/hold times | Quantify shared-lock contention directly |
| Drain batch-size distribution and actual outstanding lower writes | Explain why random draining emits small writes |
| Wake signals requested versus useful worker wake-ups | Establish whether signalling matters before changing it |
| Per-interval accepted, drained, coalesced and read-miss bytes | Make benchmark intervals interpretable |

Implementation guidance:

- Extend diagnostics behind a **version and capability flag**; never break the existing contract.
- Bounded counters and histograms only. Do not log per-I/O records.
- Aggregate per worker where possible to avoid adding the contention being measured.
- Expensive timing must be **optional** (off by default), and its own overhead measured.
- Keep the existing independent telemetry query path: reporting must not queue behind data I/O.

Exit criterion: a report can say *why* an operation was slow, instead of that it was slow.

## Phase 2 — Focused, repeatable measurement

Use `developer/scripts/Measure-Performance.ps1` (see [PERFORMANCE.md](PERFORMANCE.md)); extend it
rather than inventing another matrix. Keep the experiment set small:

| Experiment | Design | Primary result |
|---|---|---|
| Small-request overhead | Warm cached 4 KiB reads and writes at T1/QD1, T1/QD32, T4/QD8 | Throughput, CPU, queue wait, processing time |
| Foreground/background interference | Hot reader on one file, independent writer on another; reader alone versus reader plus writer | Reader p95/p99 and confirmed hit rate |
| Capacity pressure | Fresh data larger than the usable payload | Throttle duration, dirty occupancy, reader responsiveness |
| Controlled slow storage | Repeat interference with the supported lower-write delay hook | Whether RAM operations inherit lower-I/O delay |
| Random-drain efficiency | Random write phase, then timed explicit drain | Drain MiB/s, batch sizes, lower-write count and depth |
| Flush during load | Flush while producers keep running | Queue wait, drain time, lower-flush time, completion behaviour |

Non-negotiable harness rules:

- Interleave configurations, repeat at least three times, report **median and spread**.
- Keep total outstanding I/O equal when comparing thread counts.
- Record driver version, exact configuration and storage conditions with every row.
- Capture counter deltas per measured interval.
- Start each measurement from a comparable state, including dirty occupancy and warm-cache state.
- Separate files for reader and writer in interference tests.
- **Verify the hot reader stayed a RAM-hit workload**; otherwise label the row as including
  misses and eviction.
- Restore settings and remove delay injection reliably, including after a failure.
- A larger file is not automatically capacity pressure: confirm the run touched enough distinct
  blocks to exceed the payload, and treat `ThrottleWaits == 0` as a failed experiment.

Exit criterion: repeated results distinguish policy differences from run-to-run variation, and at
least one test demonstrably exercises background overlap and capacity pressure.

## Phase 3 — Reduce small-request overhead

Improve the 4 KiB path without changing ordering and without adding foreground workers. Each
candidate is a separate commit, and each must be justified by Phase 1 data. Do not assume all of
them are worthwhile.

| Candidate | Direction | Guardrail |
|---|---|---|
| Excessive worker signalling | Signal on relevant state transitions instead of waking workers for every write | No lost wake-ups; age deadlines and capacity waits must still make progress |
| Frequent snapshot publication | Keep critical transitions immediately visible; consider bounded batching of non-critical counters | Active state, failures and completed controls must never look stale or wrong |
| Repeated write preflight | Reserve capacity in batches instead of rescanning the whole request after each eviction | Whole-request admission preserved; fixed/automatic quotas still correct |
| Hot-path recency updates | Bounded or deferred recency maintenance if profiling shows a cost | Approximate eviction must never affect dirty-data correctness |
| Redundant bookkeeping | Remove work demonstrably repeated per request | Diagnostics and accounting invariants preserved |

Exit criterion: repeatable small-request throughput or CPU improvement, with byte correctness,
ordering and memory bounds unchanged.

## Phase 4 — Improve random-drain efficiency

Higher priority than originally planned: 6.6 MiB/s random drain directly sets how long dirty data
occupies RAM and how long Flush takes.

Draining currently walks the dirty FIFO in age order and gathers whatever happens to be adjacent.
Investigate **address-local drain selection**, keeping age and fairness as separate inputs:

1. Maintain an efficient way to locate nearby eligible dirty blocks.
2. Select bounded address-local groups per drain operation.
3. Preserve same-address version ordering.
4. Guarantee old dirty blocks cannot be skipped forever.
5. Respect Flush and TRIM boundaries.
6. Compare parallelism 1, 2 and 4 before changing the default.

Two traps: a larger batch setting does not produce larger batches unless the workload contains
adjacent eligible blocks, and the search for those blocks must not become expensive work under
the shared lock.

Exit criterion: fewer lower requests and/or better random-drain throughput, without unacceptable
foreground latency and without starving old blocks.

## Phase 5 — Stable block ownership and short critical sections

The foundation for every later concurrency change. Ownership rules first, locking changes second.

Required ownership rules:

- A reader or drainer can **pin an exact block version**.
- Pinned payload cannot be modified or recycled.
- An overwrite **publishes a newer version** when the existing version cannot safely be modified.
- Completing an older write must never mark a newer version clean.
- Same-address lower writes must not complete in an order that leaves older data on disk.
- All retained versions and staging resources count against the memory budget.
- Preallocate bounded staging so draining can progress when foreground capacity is exhausted.
- Dirty data survives write failure; Pause, Remove, TRIM and shutdown respect outstanding
  references.
- Keep the existing staging-buffer copy approach. Custom zero-copy I/O is unnecessary complexity
  at this stage.

Then restructure the drainer to: **select and pin under the lock → release → copy and perform
disk I/O → brief metadata update → unpin.** Apply the same shape to foreground writes
(*reserve the whole request → copy outside metadata locks → publish coherently*; a failed
allocation must never leave half a write published) and to reads (*find and pin → copy outside
locks → release*).

For read misses, the required cached overlays must be preserved for the duration of the lower
read. A drainer may retire a block the read still needs, and the configuration `Generation`
counter does **not** solve this: it is not a per-block data-version counter.

Only after this works should splitting metadata locking by address range be considered.
Otherwise the result is several complicated locks around an incorrect lifetime model.

Exit criterion: with lower I/O deliberately delayed, unrelated RAM operations do not wait on the
cache mutex, and overwrite/readback plus failure-retention tests still pass.

## Phase 6 — Dependency-aware foreground scheduling

Only after Phase 5. A capacity-blocked write must stop occupying the sole execution path, but
later requests may bypass it **only** where dependencies allow:

| Later request | May bypass a capacity-blocked write? |
|---|---|
| Read of an independent cached range | Potentially yes |
| Read overlapping that write | No |
| Flush covering earlier requests | No; it is an ordering boundary |
| TRIM, Remove, configuration transition | Only with explicitly defined dependencies |

Required design elements: per-device request ordering identifiers; dependency tracking for
overlapping address ranges; explicit boundaries for Flush, TRIM, disable and removal; cancel-safe
ownership while requests are parked; bounded fairness so writes and controls cannot starve; exact
IRP completion and lifetime accounting.

Keep a conservative Flush boundary initially — complete earlier work, drain, lower-flush, then
admit later writes. Admitting later writes during a Flush is a separate epoch/version design.

Exit criterion: a full-cache writer does not block an independent RAM-hit reader, while
overlapping reads still return correctly ordered data. **Multiple foreground workers and a direct
RAM-hit dispatch path come after this, not before.**

## Keeping Eager honest

Do not "fix" Eager by quietly delaying work until the disk is idle. Separate the two decisions:

- **Policy** (Eager/Balanced/Idle): should dirty data become *eligible* to drain?
- **Scheduling**: how much background work runs concurrently, and how progress is balanced
  against foreground latency.

Normal draining must avoid monopolizing metadata locks. Under capacity pressure, enough draining
must be guaranteed for writers to progress. Explicit Flush must force completion. More drain
threads or bigger batches can make contention worse, so they stay measured tuning parameters, not
defaults changed on intuition.

## Acceptance scenarios

Prove all of these before calling the redesign complete.

| Scenario | Required outcome |
|---|---|
| Hot RAM reads while lower writes are deliberately slow | Reads continue without waiting for lower-write latency |
| Cached writes with capacity available during draining | No disk-length waits caused by shared cache locks |
| Repeated overwrite of an in-flight block | Correct newest contents in RAM and eventually on disk |
| Mixed cached/disk read while a drain completes | Correct bytes; no stale overlay, no reused buffer |
| Full cache under continuous writes | Writers throttle, eligible independent reads continue, draining cannot starve |
| Flush while producers remain active | Clear queued/draining/flushing stages and a precise completion boundary |
| Eager versus Idle | Compared on latency, CPU and actually-drained bytes, not headline throughput |

## Rules for the implementing agent

- One conceptual performance change per commit.
- Preserve protocol compatibility, or version the extension explicitly.
- Data-plane scheduling and ownership stay in native code; C# owns control, reporting and
  benchmarks.
- Never change policy semantics just to improve a score.
- Never claim an improvement from a single sample.
- Always report foreground latency **and** drain progress: starving the drainer is not an
  optimization.
- Re-run the targeted correctness tests whenever ownership, ordering or cancellation changes.
- Do not mix this work with installer restructuring or unrelated UI changes.

Recommended first assignment: **Phase 1 plus the focused Phase 2 runs**, then pick the first
Phase 3 optimization from that data. The Phase 4 address-local drain scheduler can be specified
in parallel, since it is largely independent of the instrumentation work.

## Independent UI work

Not blocked by any phase above: replace the desktop's global busy flag with **per-disk operation
state** — waiting to start, draining pending writes, flushing storage, completed, failed. Other
disks must stay usable and telemetry must keep updating during a long operation, and closing a
progress display must not suggest the driver operation was cancelled. Worth doing even when the
underlying wait is legitimate.
