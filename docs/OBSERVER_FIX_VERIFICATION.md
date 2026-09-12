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

## 4. Restoration and handoff

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
