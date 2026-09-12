# Observer and cache-protection verification instructions

## Assignment

Verify the observer-isolation, Automatic read-protection and bypass-miss retry
changes after the user installs the CI release. Do not change the driver while
measuring. Report failures first; do not claim a performance improvement from a
successful build. See [CONCURRENCY_VERIFICATION.md](CONCURRENCY_VERIFICATION.md)
for ownership and ordering invariants.

Use the disposable **Q:** volume only. Do not format, change partitions, enable
OS-disk caching, run raw write tests on mounted Q:, or reboot during the current-
boot test batch. Installation/reboot happens before this assignment. Device-removal
and Driver Verifier tests are separate lifecycle follow-ups, not part of the
unattended first batch.

## 1. Preflight and capture

1. Record the CI commit, package hash/build-info, installed CLI path/hash, actual
   **loaded driver path/hash**, boot time and runtime cache instance. A newer
   installer or file in Program Files does not prove the loaded driver is new.
2. Confirm Q:'s physical disk identity, size, sector size, free space and non-OS
   placement. Stop if the mapping differs from the intended disposable disk.
3. Capture all current runtime options, budget, enabled state, timing and fault/
   delay hooks, plus saved startup profiles. Do not alter saved profiles. Use
   `--runtime-only` for policy mutations.
4. Resolve the installed qcache and DiskSpd paths. Read their help; do not assume
   a different DiskSpd version has identical output or exit-code conventions.
5. Use a fresh results directory outside the source tree (or ignored `.lab/`).
   Keep seeded correctness data separate from benchmark files. Run sequentially
   from one detached owner if SSH disconnects terminate children. Only terminate
   processes/tasks created by that owner. Give each case a deadline.

Useful command checks, in elevated PowerShell:

```powershell
qcache policy status Q: --json
qcache developer performance Q:
qcache developer performance Q: --timing true
qcache policy apply Q: --budget-mib 1024 --preset Fast --allocation Fixed --write-percent 50 --drain Eager --runtime-only
```

Use 1024 MiB, not an assumed 4096 MiB budget. Stop on allocation failure rather
than silently continuing with a different configuration. Performance ABI v2 is
408 bytes; compatibility with the older 192-byte response is intentional. State
flag 2048 alone does not distinguish this release from earlier diagnostic builds.

## 2. Correctness gates before performance

Use deterministic per-region/per-generation byte patterns and unbuffered aligned
file I/O. Verify exact bytes immediately and again after explicit flush plus
clean-cache drop. A throughput benchmark is not a correctness test.

| Case | Required assertion |
|---|---|
| Older overlapping write then read | Exact and partial overlap cannot return the older pattern after the write completes. Adjacent non-overlapping hot reads may bypass. |
| Flush/TRIM/policy fence | A later hot read cannot pass a real queued fence. Include cancellation of a queued fence and reuse of trimmed regions. |
| In-flight overwrite | Write A, delay its drain, overwrite with B; B survives immediate and post-drain verification. Repeat drain parallelism 1/2/4. |
| Miss reinsertion and cancellation | Cold/partial reads complete through normal admission after progress, or cancel exactly once. Queue drains; subsequent I/O succeeds. |
| Nested reads with retention disabled | A lower read plus a nested RAM hit must not lose a pinned version when draining completes. Verify both buffers and eventual retirement. |
| Observation under pressure | Run repeated inventory alongside the same seeded reader/writer workload. Correct bytes and completed queries; no driver errors or stuck requests. |
| Automatic allocation boundaries | Empty read pool permits borrowing; resident read demand below/above half capacity remains bounded; overwrite retained reads correctly. Fixed 25/50/100% behavior remains unchanged. |
| Large-request admission | Test the protection formula at, below and above half payload, and at full payload. No dirty eviction or impossible admission. If the storage stack splits requests, record actual driver request lengths: file size does not establish this coverage. Use host-level logic tests for unreachable sizes. |

The older local `Test-Concurrency.ps1` is **not a trusted packaged test suite**.
Its CancelReads helper puts the x64 OVERLAPPED offset at byte 0 instead of byte
16. Fix/review that helper before reuse, including native-call results, finite
waits, cancellation completion before buffer release, and restoration on failure.
Do not reinterpret its previous passing count as full scenario coverage. Ordinary
CLI file tests are useful smoke checks, not substitutes for the interleavings above.

Stop and preserve evidence on byte mismatch, new driver error, unexpected disk
identity, hang or failed restoration. Do not proceed to headline speed numbers.

## 3. Focused performance batch

Run at least two reversed-order passes of the observer matrix, preferably three
repeats per condition. Hold configuration, working set, timing mode and workload
constant. Compare medians and spread; shared VM storage previously varied by >2x.

| Matrix | Setup and cases | Evidence sought |
|---|---|---|
| Observer isolation (primary regression) | Fixed50, 1 GiB cache, Eager, Fast, 25 ms lower-write delay. 256 MiB warmed reader set, 2 GiB separate writer file. Reader 4 KiB QD8 T2; writer 1 MiB QD8 T1. Cases: no observer, fresh-handle telemetry every 500 ms, full disk inventory, both. | Known observation queries no longer produce scheduling fences. Reader IOPS/tails remain near the no-observer control with positive writer/drain progress. Record remaining unknown fence codes; do not whitelist them blindly. |
| Real desktop | Same load, desktop closed/open, plus manual Refresh disks during load. | Telemetry remains live. Automatic inventory does not run every telemetry tick; manual refresh works. New-disk discovery may wait up to the two-minute fallback. |
| Automatic protection | Same hot-reader workload and delay, Automatic versus Fixed50, including no-delay controls. | Raw read-miss deltas, clean-read occupancy, evictions, reader p99.9/max and writer/drain progress. The hot set should survive write-only pressure when it fits the protected allowance. Read churn may still evict it. |
| Cold and mixed reads | Independent hot reader plus cold/partially cached reads while the writer throttles. | Repeated hit-only failures are reduced; normal misses eventually complete. No cold-read latency guarantee is claimed. Counters alone cannot prove the per-owner bound without request/epoch attribution. |
| Write/drain regression | Small writes QD1 T1, QD32 T1 and QD8 T4; sequential and random drains; working set larger than budget. | Application-accepted bytes, successful drained bytes, residual dirty data, CPU and reader latency. Investigate repeatable >10% regressions, accounting for protection's deliberate capacity tradeoff. |

For every reader measurement, prove the writer overlaps the full measured window.
Warm the hot set before measuring. Compare reader-alone and reader-with-writer.
Capture p50/p95/p99/**p99.9 and max**, IOPS, throughput, zero-completion cases,
raw misses, bypass attempts/completions/misses, fence/scan rejects, queue depth,
phase, dirty/clean occupancy, throttle waits and actual successful draining.

The local observer harness (`Run-ObserverProbe.ps1` plus `ObserverLoop.ps1`) is
ignored experimental material, **not shipped in the repository or installer**.
If present, review its paths and configuration and reuse it for the exact matrix.
Otherwise construct that matrix from the specification above. Its inventory
observer uses Get-Disk, Win32_DiskDrive and Get-Partition, waiting ten seconds
after completion before querying again. Log query duration: this is not a fixed
ten-second start-to-start rate and is not the real UI's current duty cycle.

The committed general harness can cover the allocation/interference follow-up:

```powershell
# Run in a PowerShell script, with verified executable and fresh results paths.
$diskspdPath = '<verified DiskSpd executable>'
$resultsRoot = '<fresh results directory>'
foreach ($allocation in @('Fixed', 'Automatic')) {
    & .\developer\scripts\Measure-Performance.ps1 `
        -Volume Q: -DiskSpd $diskspdPath -BudgetMiB 1024 `
        -ReadSetMiB 256 -CapacitySetMiB 4096 `
        -Allocation $allocation -WritePercent 50 -Configs Eager `
        -Experiments interference,slow-storage -Repeats 3 -Duration 10 `
        -OutputDirectory (Join-Path $resultsRoot $allocation)
}
```

This example is not the observer matrix. Reverse allocation order in a second
pass if comparing policies. Review the harness against the installed DiskSpd:
the CDM-bundled tool may return its numeric score as exit code; parse complete
results and stderr rather than treating every nonzero code as failure. Its
`3-nines` label means p99.9. Device counters and DiskSpd measurement windows must
be aligned or clearly labelled as different. Timing counters add overhead: keep
timing identical for comparisons and include timing-disabled throughput controls.

Suggested acceptance target (not a guarantee): no order-of-magnitude collapse
from inventory, and no return of approximately one-second hot-reader tails.
Investigate a repeatable >20% observer/control throughput difference or >2x tail
increase. Report uncertainty, not a binary speed claim from one sample.

## 4. Measurements for architecture decisions

The acceptance batch above answers whether these fixes work. It does not by
itself select the next architecture. Add the probes below to the same detached
runner, after correctness gates pass. Prepare the runner once and collect evidence
without changing implementation between cases. Use three repeats with reversed
or interleaved order, fixed seeds and explicit timeouts. Record skipped probes
and reasons instead of silently expanding scope or inventing results.

Priority: cached write admission, then cached reads, then background draining,
subject to ordering and sufficient drain progress. Evaluate steady-state pressure
separately from RAM bursts: finite RAM cannot sustain unique writes faster than
the disk indefinitely. No design should be selected to conceal that constraint.

### Additional bounded probes

Use 1 GiB budget where possible, 10-second measurement windows after warmup, and
disjoint files/ranges except in explicit coherence tests. Confirm residency using
unrounded counters, not file size. Record actual usable payload capacity.

| ID | Probe | Configuration and evidence |
|---|---|---|
| D1 | Small-request scaling without capacity pressure | Warm a disjoint 64 MiB working set. Test 4 KiB read-only and overwrite-only at T1/QD1, T1/QD8, T1/QD32 and T4/QD8 (QD is per thread). Compare Eager/Idle with matched residency and timing disabled; repeat representative cases with timing enabled. Capture total/per-core CPU, IOPS, tails, queue depth, accepted/drained bytes, coalescing and lock counters. Confirm zero capacity waits or label the case pressure-contaminated. |
| D2 | Cold-read interference without write pressure | Fixed50; keep a 64 MiB hot set resident. Run hot reader alone, then alongside a cold reader over a disjoint set larger than cache. Verify actual misses. Repeat with a simultaneous writer only after the no-writer comparison. Capture reader-specific latency and throughput plus driver phases. The lab lower-write delay is NOT a controlled lower-read delay; do not describe it as one. |
| D3 | Queue-limit sensitivity | With a verified hot set and capacity-blocked writer, vary total offered outstanding requests around 32, 64 and 128, then vary reader request size 4 KiB/1 MiB/2 MiB. Record actual driver request sizes where observable because the stack may split them. Correlate scan-limit, size-rejection, no-candidate and fence deltas with successful bypasses and tails. Do not infer that offered depth equals instantaneous queued depth. |
| D4 | Drain tradeoffs | Identical seeded sequential versus sparse-random dirty sets; test batch 256/1024 KiB and parallelism 1/2/4. Hold policy constant. Measure explicit drain-to-zero separately from concurrent reader/writer tests. Capture successful drained bytes, attempted batches, average bytes per batch, CPU, reader tails and writer acceptance. Test Eager/Idle separately with matched starting state, not mixed into every batch cell. |
| D5 | Allocation demand and transitions | Compare Automatic and Fixed25/50. Hot sets approximately 10%, 40% and 70% of usable payload; unique-write set greater than budget. Run write-only, hot-read plus write, then a bounded phase change from hot set A to disjoint B. Track clean-read/write occupancy, raw hits/misses, eviction rate, accepted/drained bytes and tails over time. Distinguish resident-read protection from learning a new cold hot set while RAM is dirty. |
| D6 | Copy versus metadata cost | Compare resident 4 KiB, 64 KiB and 1 MiB requests at equal total depth with/without active draining. Capture CPU and lock timing; use a separate short CPU-sampling trace if tooling is available. Attribute samples to preflight/index operations, copying, publication, wakeups and scheduling. Counter totals alone cannot separate those costs. Do not enable unbounded tracing for the whole run. |
| D7 | Inventory discovery behavior | Record automatic scan start/completion times and manual-refresh completion during load. If an additional disposable disk is already available, test arrival/drive-letter changes with user coordination; do not add/remove VM hardware automatically. Without that setup, mark actual hotplug coverage unavailable. |

Keep runtime bounded: estimate duration before starting; use per-case and overall
deadlines and always reserve time for restoration. If the full matrix cannot fit,
run acceptance plus D1/D2/D4/D5 first and clearly mark D3/D6/D7 pending. A missing
trace or unavailable hotplug setup is a reason to defer a decision, not a failed
driver result. Fault/lifecycle experiments must not overlap performance probes.

### Decision rules for the open items

For each recommendation, cite case IDs and measured effect size/spread. An
improvement smaller than repeat-to-repeat variation is inconclusive. The >10%
regression, >20% observer difference and >2x tail criteria in this document are
investigation triggers, not statistical proof or universal product requirements.

| Open item | Evidence required | Decision and remaining design work |
|---|---|---|
| Native device-change notifications | D7 and UI inventory timing. | This is primarily a discovery/UX improvement, not contingent on a speed win. Implement if prompt automatic arrival/letter-change updates are needed. First choose disk/volume notification coverage, lifetime ownership, debounce and rescan-on-missed-event fallback. Do not make live cache-state trust depend on inventory events. |
| Independent cold-read execution | D2 shows unrelated resident readers/writers delayed while a lower read owns the foreground worker, after excluding fences and evictions. | Prioritize asynchronous miss handling if that interference persists. First specify range dependencies, pinned overlay versions, cancellation/remove ownership, bounded outstanding I/O and flush/TRIM ordering. A benchmark cannot supply this correctness design. |
| Parallel write admission | D1 scaling plateaus without pressure while queue latency grows; D6 attributes cost to serialized admission rather than the lower device. | Optimize fixed work first if it dominates. Choose multi-owner admission only if the remaining ceiling matters for target hardware. Require per-range version publication, dependency/barrier epochs, cancellation and bounded memory before implementation. Little's law consistency alone does NOT prove where latency is spent. |
| Better queue selection | D3 shows limits/repeated scans correlate with lost useful bypass work, without real ordering conflicts. | Prefer an eligible-read index or progress-driven scheduling if scans are costly. Do not merely raise limits: bound spinlock time and preserve all older overlapping writes/fences. If actual fences dominate, classify their semantics instead. |
| Lower per-write overhead | D1/D6 separate CPU spent in preflight, metadata, publication, copying and notifications. | Target the largest evidenced cost with one contained change at a time. Without stack attribution, record the hypothesis and request a short trace; aggregate lock time cannot justify a specific rewrite. |
| Faster draining | D4 proves batching/concurrency gains in actual disk acceptance while foreground latency and write admission remain acceptable. | Select batch/parallelism only from the throughput-versus-latency tradeoff. Keep defaults if gains are noisy or harm foreground work. Sparse writes that lack adjacency may need address-aware scheduling, with starvation and same-range version ordering explicitly designed. |
| Smarter Automatic allocation | D5 shows which demand transitions lose useful reads or unnecessarily throttle writes versus Fixed controls. | Retain the current heuristic if it is stable. Otherwise propose adaptive protection with explicit minimum write progress, bounds and hysteresis. Do not optimize for one hot-set size. Uncached future demand cannot be reserved by evicting acknowledged dirty writes. |
| Sharding or reader/writer locks | D6 traces show metadata lock contention remains substantial after fixed-work and scheduling improvements; D1 shows useful independent work available. | Shard by block/range only with cross-shard barrier/eviction/accounting rules. Consider reader/writer locks only if the hit path can actually be read-only: current hits update recency, statistics and pins. Neither a lock-free design nor separate RAM pools is automatically faster or safer. |
| Separate read/write RAM pools | D5 demonstrates a protection problem not solved by logical quotas, or traces show allocator/metadata interference that physical partitioning would remove. | Prefer the unified versioned store with logical protection unless evidence favors partitioning. Separate pools must still serve newest dirty data to reads and manage promotion without stale duplicates or excessive copying. Fixed quotas are not equivalent to independent execution. |
| Boot/power lifecycle hardening | Separate planned boot/paging, shutdown, sleep/hibernate, dump and device-removal tests on a recoverable VM. | Current-boot results can prioritize this work but cannot certify it. Establish recovery access and a separate test window before invasive lifecycle tests. Do not infer OS-disk safety from Q: benchmarks. |

### Required decision output

Add `DECISIONS.md` beside the raw results, with one row per open item:

`item | implement next / defer / insufficient evidence | evidence case IDs |
measured benefit or bottleneck | confounders | design prerequisite | smallest next change`

Rank the next three changes using the stated foreground priority and measured
impact, not implementation novelty. Include any tradeoff where faster writes cost
read residency or faster draining hurts foreground latency. Separate measured
facts, source-code reasoning and hypotheses. It is valid to recommend no new
architecture yet. Preserve all correctness invariants even if the preferred
performance design requires further investigation.

## 5. Restoration and handoff

The runner must use try/finally and restore original delay/fault/timing hooks,
budget, every policy field and enabled state. Verify saved profiles are unchanged.
Restore only after owned workloads finish; never free buffers while native I/O
is outstanding. On timeout, record failure and whether cleanup itself completed.

Write `RESULTS.md`, raw outputs and `status.json` in the results directory. The
runner should mark status COMPLETED only after restoration passes; otherwise
FAILED, including cleanup failures. The user can check that status file without
repeated agent polling. These filenames are a runner requirement, not evidence
that a run has already been launched.

Report exact build identity; pass/fail/skipped per scenario; medians and spread;
remaining bottleneck with supporting counters; and final restored state. Keep
raw VM data private. Do not push test artifacts or change defaults based solely
on the largest RAM/coalescing throughput score.
