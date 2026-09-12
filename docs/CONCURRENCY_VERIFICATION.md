# Concurrency implementation and verification handoff

Implementation date: 2026-09-12. Runtime verification is deliberately delegated;
compilation is not a correctness or performance result. Do not overwrite the
historical baseline in PERFORMANCE.md with predicted improvements.

## What changed

| Area | Implementation | Important boundary |
|---|---|---|
| Capacity stalls | The request worker services independent, fully cached reads while its current write waits for space. | A full dirty cache still throttles writes. |
| Cache misses | A pending lower read no longer holds the cache mutex. While waiting for completion, the worker can service independent RAM-hit reads. | This does not create parallel foreground writers or parallel cache-miss reads. |
| Ordering | Selective CSQ dequeue checks the active blocked write and every earlier queued write for overlapping ranges. Any non-read/write request stops the scan. | Flush, TRIM, policy, power and PnP requests are fences; later reads must not bypass them. |
| Ownership | Slots have pins, a Filling state and deferred retirement. A drained version needed by an ongoing read remains indexed until the final pin is released. | A pin protects the exact buffer version, not merely the disk address. |
| Copies | Foreground write/read-hit payloads are copied outside the mutex in batches of up to 64 blocks. Drainers copy pinned versions into staging outside the mutex. | Read-miss admission still copies individual clean blocks under the mutex. Metadata/index work remains serialized. |
| Drain locality | Start with the oldest eligible dirty block; search backwards by at most one batch, then gather forward by disk address. | No gaps are bridged, no newer version can pass an older in-flight version, no default parallelism increase. |
| Small-request work | Avoid re-signalling an already-signalled notification event; correct Fixed-quota preflight counting when overwriting retained clean writes. | Full-request preflight and snapshot publication remain; no claimed removal of the small-write scaling ceiling. |
| Observability | Separate versioned performance IOCTL, opt-in lock/drain timing, queue counters, active phase, bypass-hit/miss counters. | Existing state/options ABI sizes remain unchanged. |
| Desktop | Operations and test cancellation are per disk. Sampling continues during operations; the driver phase and queue depth are displayed. | Same-disk mutations remain serialized. Configuration transactions across processes still use the existing management gate. |
| Benchmarking | Native failures are checked, zero-completion latency is null, true medians are calculated, coalescing accounts for dirty/discard deltas. Whole-invocation counter windows are labelled separately from DiskSpd score windows. | Counts are device-wide, not per process; the script does not prove byte integrity. |
| Persistence | `policy apply/pause/resume/remove --runtime-only` leaves saved startup settings untouched. The harness restores all original options and enabled state. | The default command semantics are unchanged. `--save` and `--runtime-only` cannot be combined. |

### Scheduler bounds and invariants to review

1. There is still **one foreground request owner per device**. This is a bounded
   cooperative read lane, not a multi-worker/range-lock rewrite.
2. A lane pass considers at most 64 queued entries, completes at most eight reads,
   and accepts reads no larger than 1 MiB. A read behind a fence, beyond that scan
   window, overlapping an older write, or missing any cached block stays ordered.
3. A miss selected for the lane is reinserted at the queue head. It crosses only
   previously checked reads/non-overlapping writes. CSQ owns cancellation after
   reinsertion; code must not touch the IRP after successful insertion.
4. A pending lower read is **not inspected** by the selector. Its completion event
   controls lifetime; the callback receives null instead of its active IRP.
5. All hit versions of an ordinary mixed hit/miss read are pinned before releasing
   the mutex. Nested lane reads may touch/promote those entries, but cannot free
   their buffers. No foreground write can create a newer version during that read.
6. A write preflights the whole request before publishing any block. Filling blocks
   are invisible to drain selection. The request is acknowledged only after all
   its payload copies finish and Filling is cleared.
7. Drainers pin and mark exact versions in flight before releasing the mutex.
   New writes use a separate slot when the existing version is pinned/in flight.
   Successful old-version completion cannot retire or overwrite the newer version.
8. No metadata mutex is held over lower read I/O, lower writes, lower Flush, or
   capacity waits. Snapshot polling never joins the request queue.
9. No policy defaults, boot caching defaults, version numbers or saved profiles
   are changed by this performance work.

## Verification order

Use disposable Q: only for mutation/fault/performance tests. Do not format it or
enable OS-disk caching as part of this handoff. Keep all raw evidence under `.lab/`
or an external results directory, not in the installer or source tree.

| Order | Check | Evidence / acceptance condition |
|---|---|---|
| 1 | Identify exact package, commit, loaded driver and CLI. Install/reboot if required. | Record hashes/build info and runtime instance; do not benchmark an old loaded driver with new UI alone. |
| 2 | Run host-only protocol/UI regression executables and inspect scheduler invariants above. | All cases pass, including a pending Flush while another disk remains configurable and sampling continues. |
| 3 | Smoke-test telemetry and runtime-only persistence. | Performance capability advertised; JSON decodes; current state remains independently readable; saved profiles identical before/after runtime-only apply, pause, resume and remove. |
| 4 | Deterministic byte-integrity and ownership tests below. | Exact bytes, no stale reads, leaked pins, stuck Filling entries, double completion or memory errors. |
| 5 | Fixed-allocation hot-reader interference, including 25 ms delayed draining. | Positive bypass-hit count, verified resident hot set, nonzero writer progress and throttling; reader p99 no longer tracks one-second stalls. Report ratios, not an invented guarantee. |
| 6 | A cold reader plus an independent hot reader. | The hot reader makes progress while a lower read is outstanding; no stale data in mixed hit/miss reads. |
| 7 | Capacity, random draining, small-request and parallelism matrix. | Repeatable results with errors zero; separate application acceptance from disk drain throughput and residual dirty bytes. |
| 8 | Flush/UI/lifecycle and recovery. | Queued Flush becomes drain/low-level Flush and completes after older writes; UI continues sampling; cancellation/removal clean up ownership. |
| 9 | Restore and report. | Delay/fault/timing hooks restored, original budget/options/enabled state verified, saved profiles unchanged; report failures explicitly. |

### Host-only checks (no VM required)

```powershell
dotnet build QueueCache.Managed.slnx -c Release
dotnet run --project tests/QueueCache.Management.Tests -c Release --no-build
dotnet run --project tests/QueueCache.Desktop.Tests -c Release --no-build
```

Build the write-cache driver in both Debug and Release with the repository's WDK
build configuration. Compile-time ABI assertions must continue to pass. Run Driver
Verifier against the test driver on the disposable VM, not the development host;
record the chosen checks and retain crash dumps if any test fails.

### New commands

```powershell
qcache developer performance Q:
qcache developer performance Q: --timing true
qcache developer performance Q: --timing false
qcache policy apply Q: --budget-mib 1024 --preset Fast --allocation Fixed --write-percent 50 --drain Eager --runtime-only
qcache policy pause Q: --runtime-only
qcache policy resume Q: --runtime-only
```

Detailed timing is disabled initially. Capture timings in separate diagnostic
runs; compare throughput with timing disabled on both builds. Turning timing off
does not reset accumulated counters. Old drivers must reject the new command in
the managed capability check, without being sent an unknown IOCTL.

Performance wire contract: version 1, 192 bytes, advertised by state flag 2048.
Durations are QPC ticks; divide by Frequency. Queue and cache samples use separate
short locks and are not a single atomic transaction with V3 state. QueueWaitTicks
includes queue departures due to cancellation and repeated lane attempts;
QueuedRequests counts original admissions only. OldestQueuedTicks currently means
the **head request's current queue-residence age**, not a full-queue minimum after
reinsertion. CapacityWaitTicks includes useful lane work while a writer is blocked.
LowerIoTicks currently measures drainer lower-write time, including injected delay,
not all lower I/O. DrainBytes/DrainBatches count attempts, including failed attempts;
use normal DrainedBytes for successful disk acceptance. Lock counters are aggregate
across foreground and drainer threads; they are not latency percentiles or CPU time.

### Deterministic correctness scenarios

Use seeded patterns and exact comparison, not only DiskSpd scores. Prefer unique
files; use raw-region developer tests only on their explicitly required empty RAW
disposable target. Existing `developer write-tests` must NOT be pointed at mounted Q:.

| Scenario | Required interleaving and assertion |
|---|---|
| Blocked writer + hot read | Fill a small write quota, delay draining, issue an independent cached read. It completes before enough capacity is freed for the blocked write. |
| Overlapping read | Queue a newer write to a region behind the blocked request, then read that region. The read must not bypass the older queued write and return the previous cached version. Test exact overlap, partial overlap and adjacent non-overlap. |
| Control fence | Queue write → Flush/TRIM/policy control → hot read. The later read must not pass the control. Also test cancellation of the queued control. |
| Lane miss | Select a non-overlapping read whose data is not fully resident. It returns to the normal queue, does not issue a nested lower read and eventually completes normally. |
| Nested hit during miss | Begin a mixed hit/miss read; let a drainer finish its pinned dirty portion, with retention disabled. Service another read of that pinned data; both buffers must contain the correct bytes and the slot must retire after its last pin. |
| In-flight overwrite | Delay a lower write of pattern A; overwrite the same blocks with B. Verify B immediately and again after explicit Flush and cache removal. Repeat with 1/2/4 drainers. |
| Partial/unaligned-to-cache-block | Use sector-aligned 512-byte writes on a 512-sector disk, including boundaries. Verify untouched bytes and ordering through the fallback barrier. |
| Failure/retry | Inject lower failure and short completion using existing hooks. Dirty bytes remain accounted for; retry preserves the newest version; no slot is reused while pinned. |
| Cancellation | Cancel queued reads, a selected lane read, a capacity-blocked writer and a lower read. Every IRP completes at most once; removal locks and pins balance; the device remains usable. |
| Scan bounds | Use queues above 64 entries and reads above 1 MiB. Confirm correct fallback and no starvation of the owned write; record the expected reduced bypass coverage. |
| Retention/quotas | Automatic and Fixed 25/50/100%; retention/promotion on/off. Read quota must not be exceeded when pinned entries cannot be evicted. Rewriting retained clean write blocks must not double-count occupancy. |
| TRIM/reuse | Trim dirty/clean ranges and reuse them; no old bytes reappear through retained slots. Include partial-range fallback. |

### First performance run

Use the new CLI even when comparing against the old compatible driver, because
`--runtime-only` is a new CLI feature. Record the initial state/profile separately.
The last reported VM budget was 1024 MiB; do not silently force 4096 MiB if RAM
preflight rejects it.

```powershell
.\developer\scripts\Measure-Performance.ps1 `
  -Volume Q: -DiskSpd 'C:\tools\diskspd.exe' `
  -BudgetMiB 1024 -ReadSetMiB 256 -CapacitySetMiB 4096 `
  -Allocation Fixed -WritePercent 50 -Configs Eager,Idle `
  -Experiments interference,slow-storage -Repeats 3 -Duration 10 `
  -OutputDirectory '<results-directory>\perf-concurrency-fixed50'
```

Resolve the actual DiskSpd executable path first. Run detached if SSH child lifetime
requires it; retain the task's exit status and remove only the task you created.
Repeat with Automatic to measure eviction separately. Then run small-request,
capacity, random-drain, drain-parallelism and flush-under-load. Compare baseline and
candidate in interleaved/repeated sessions, recording CPU use and storage drift.

For the crucial resident-hot-read case, require unrounded miss deltas to be zero
or explicitly report nonzero misses; rounded `0.0 MiB` is not proof. Record reader
IOPS/p50/p95/p99/max, writer progress, dirty occupancy, throttle waits, bypass reads,
lock timing and actual drained bytes. Check that the writer overlaps the full
reader measurement window. The harness's device counters include warmup, whereas
DiskSpd scores do not; use synchronised sampling if exact time attribution is needed.

Suggested performance gate: at least an order-of-magnitude p99 improvement over
the Fixed-allocation ~120 ms / delayed ~1000 ms baseline, with writer progress and
byte integrity intact. This is a target for acceptance, **not a measured result**.
Investigate >10% repeatable regressions in unloaded reads/writes or successful drain
throughput. Sparse random dirty sets may not benefit from adjacency gathering.

## Remaining architectural work, not claimed by this change

- Multiple foreground write workers with per-range dependencies and barrier epochs.
- Parallel cache-miss reads and writes bypassing unrelated pending lower reads.
- Indexed/range-aware scheduling beyond the bounded queue scan; no universal
  latency bound is promised for arbitrary queue depth or explicit fences.
- Full-request preflight/eviction optimisation and read-fill copies outside locks.
- Per-operation latency histograms/ETW attribution and perfectly aligned benchmark
  measurement windows. The new counters are a diagnostic starting point.

Do not change defaults, remove ordering fences or declare OS/paging safety merely
because the small hot-set benchmark improves.
