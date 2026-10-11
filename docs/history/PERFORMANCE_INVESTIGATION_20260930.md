# Performance investigation — 2026-09-30

## Comparison limits and observed drop

The previous 0.4.219.1 measurements used CrystalDiskMark 9.0.3's bundled
DiskSpd on a prewarmed 1 GiB file, three repetitions, Q: 2 GiB Fast/Idle,
**Driver Verifier off**. The new 0.4.249.1 measurement used the actual CDM GUI's
Default profile, five repetitions and **Verifier 0x209bb on**. Same disk, cache
budget and DiskSpd binary; preparation, scoring and verification state differ.
This is not a controlled driver-regression comparison.

| Shape | Earlier read MB/s range | Latest read MB/s | Earlier write MB/s range | Latest write MB/s |
|---|---:|---:|---:|---:|
| SEQ 1 MiB Q8 T1 | 36571–37177 | 13188.047 | 20607–21392 | 13724.662 |
| SEQ 1 MiB Q1 T1 | 12563–14392 | 8165.327 | 13234–13756 | 6934.433 |
| RND 4 KiB Q32 T1 | 1576–1638 | 77.030 | 1555–1648 | 69.959 |
| RND 4 KiB Q1 T1 | 1230–1328 | 115.577 | 1023–1073 | 94.561 |

Random throughput is roughly 91–96% below the earlier best values. The drop is
large; attributing all of it to instrumentation without a matched experiment
would be premature.

## Read-only diagnosis

Loaded module is still `QueueCache-0.4.249.1-A31F6D68C608.sys`, Verifier load
1/unload 0. Both querysettings and runtime query show 0x209bb. Runtime query
reported 377,351 Verifier trims at one observation, with no deliberately failed
allocations or force-pending/low-resource/delay-fuzzing flags. This trim count is
boot-wide, not a benchmark-window count. The VM still has four vCPUs and 16 GiB
configured RAM, with about 11 GiB guest free memory at the initial inspection.

Detailed timing is off. Q: remains healthy, 2 GiB Fast/Idle, with no competing
CDM/DiskSpd/qcache process before the focused run. At initial observation there
were zero cache errors and zero capacity waits. Boot-lifetime RAM-hit counters,
caller-path counters and copy-offload counters advance; they do not assign a
specific path to each of the preceding GUI score windows. No claim is made that
lifetime counters prove full residency during every CDM score.

The driver-source diff since the earlier filesystem checkpoint leaves the
read/write copy algorithms, caller-path default and copy-offload scheduling
unchanged. Recent changes concern removal admission and PnP query/cancel state.
Normal dispatch adds a Gone check; management controls add a short remove-pending
lock check. Source inspection provides no clear explanation for a tenfold random
slowdown and is not proof that the code cannot regress.

Verifier is the leading hypothesis, supported by the different verification
state, active costly checks, observed trims and earlier repository measurements
that already showed substantial Verifier overhead. Microsoft documents that a
loaded verified driver cannot be fully unverified without reboot, and that
Windows 11 restricts which options can change live:
[Using Volatile Settings](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/using-volatile-settings).
Therefore a partially disabled live configuration would not establish a genuine
Verifier-off comparison.

## Focused maintained measurement

The supported runner was used with the same SHA-256-verified CDM DiskSpd binary
`7281BF6DA6C03797016EDDF2E8AAEC4C644AE893D403D57A030B7E2E14B61079`:

```powershell
qcache developer verify Q: --suite write-performance --budget-mib 2048 --diskspd "C:\Tools\CrystalDiskMark9_0_3\CdmResource\DiskSpd\DiskSpd64.exe" --case-filter random-write-q1-Idle-timingFalse --repeats 3 --duration-seconds 5 --output C:\QueueCache-Results
```

Exact run: `QueueCache-Verify-20260930-184356-d3b405509bc34e79aca0c0a643827b0b`.
This is a selected three-case diagnostic, not the complete 72-case matrix or an
exact reproduction of CDM GUI preparation/scoring. Counter differences include
warmup and process close; they are separate from XML score windows.

The run finished **COMPLETED 3/3** at 18:54:34 UTC, with successful independent
restoration. Exact FINISHED/status/SUMMARY/results/log, all 369 nonempty raw files,
owned process/exit records, readiness and interval coverage were inspected and
preserved. All owned workers exited successfully. The controlling SSH transport
hit its local 10-minute timeout during restoration; the remote runner continued
and completed normally. Completion is established by its final reports, not the
transport exit.

| Repetition | Write MB/s (decimal) | IOPS | Write p99 ms | Caller writes during collection | Queued requests during collection | New capacity waits / errors |
|---|---:|---:|---:|---:|---:|---:|
| 1 | 95.930 | 23420.36 | 0.143 | 230468 | 1016 | 0 / 0 |
| 2 | 91.862 | 22427.15 | 0.197 | 230405 | 1024 | 0 / 0 |
| 3 | 94.529 | 23078.44 | 0.453 | 231577 | 1021 | 0 / 0 |

Median write speed is **94.529 MB/s**, close to the GUI's **94.561 MB/s**.
This independently reproduces the low random-write speed with a working caller
path and no new capacity waits or errors during collection. It does not isolate
Verifier overhead from other costs. Timing was off throughout these samples.
All readiness files report true; 52/51/51 samples cover their recorded intervals,
with maximum gaps 0.233/0.237/0.235 seconds, below the two-second contract.
Before/after request counters include warmup/close and telemetry observations;
they must not be divided by the five-second scoring duration to infer score IOPS.

Preparation and between-case drains were slow; final restoration took 113 seconds.
That is backend/drain-phase evidence, separate from the random score window.
The observed original cache state had zero capacity waits; workload preparation
added 13,943 lifetime waits before the first score snapshot. Those remained
unchanged throughout all three collection intervals. They do not establish a
capacity-related score regression.

At this initial checkpoint the Verifier-off matched comparison was pending; the
approved follow-up below supplies it. No driver source or defaults
were changed to improve a score, and the disposable removal disk remains detached.

## Approved restart and Verifier-off measurements

The owner authorized restarting the VM and restoring Verifier afterwards. Q: was
drained before `verifier /reset`; a planned Windows restart then completed.
Runtime `verifier /query` reports no verified drivers. Native loaded-module
enumeration (with debug privilege scoped to the inspecting process) reports the
same 0.4.249.1 module and exact SHA-256 recorded above; this is loaded-image-path
evidence, not an inference from a version string. The same saved Q: GUID/disk
identity and 2048 MiB Fast/Idle profile restored successfully. No driver was
installed or upgraded.

### Actual CDM GUI: same Default profile, five 1 GiB runs

Both GUI runs used CDM 9.0.3 x64, five repetitions, 1 GiB, Q:, Admin, five-second
measurement and interval. The off run followed the approved reboot; therefore
boot/cache history is an additional difference. Q: usage rose from 31 to 37 GiB
because the maintained diagnostic workload files are retained.

| Workload | Verifier on read MB/s | Verifier off read MB/s | Verifier on write MB/s | Verifier off write MB/s | Read increase | Write increase |
|---|---:|---:|---:|---:|---:|---:|
| SEQ 1 MiB Q8 T1 | 13188.047 | 23366.587 | 13724.662 | 18452.028 | 1.77× | 1.34× |
| SEQ 1 MiB Q1 T1 | 8165.327 | 15422.490 | 6934.433 | 12919.344 | 1.89× | 1.86× |
| RND 4 KiB Q32 T1 | 77.030 | 1790.783 | 69.959 | 1402.940 | 23.25× | 20.05× |
| RND 4 KiB Q1 T1 | 115.577 | 1327.235 | 94.561 | 1073.774 | 11.48× | 11.36× |

| Workload | Off read IOPS | Off write IOPS | Off read latency µs | Off write latency µs |
|---|---:|---:|---:|---:|
| SEQ 1 MiB Q8 T1 | 22284.1 | 17597.2 | 167.37 | 278.17 |
| SEQ 1 MiB Q1 T1 | 14708.0 | 12320.8 | 67.72 | 80.73 |
| RND 4 KiB Q32 T1 | 437202.9 | 342514.6 | 37.50 | 9.44 |
| RND 4 KiB Q1 T1 | 324032.0 | 262151.9 | 3.01 | 3.74 |

The complete export is preserved at
`C:\QueueCache-Results\CDM-Default-VerifierOff-20260930\result.txt` and privately
on the controlling host. Export timestamp: 22:07:04 guest local / 20:07:04 UTC.
The completed All-button screenshot and clipboard export were inspected. The app
was closed and temporary interactive launch/export tasks removed.

### Matching maintained random-write Q1 diagnostic

The identical command, budget, case filter, repetitions, five-second duration,
plan-74 locally published runner and DiskSpd hash were reused. Exact off run:
`QueueCache-Verify-20260930-200812-c19de70a9d634c3f9fe237964eb0d2d7`.
It completed 3/3 with successful independent restoration at 20:10:41 UTC.

| Repetition | Off write MB/s | Off IOPS | Off write p99 ms | New capacity waits / errors |
|---|---:|---:|---:|---:|
| 1 | 973.255 | 237611.18 | 0.016 | 0 / 0 |
| 2 | 973.638 | 237704.59 | 0.015 | 0 / 0 |
| 3 | 949.334 | 231771.06 | 0.016 | 0 / 0 |

Median increases from 94.529 to **973.255 MB/s (10.30×)**. Caller-path writes
advance in every collection interval; all three have zero new capacity waits or
errors. Readiness is true; 52/51/51 samples cover the recorded workload intervals,
with maximum gaps 0.231/0.232/0.242 seconds. All final reports and 369 nonempty raw
files, control traces, recovery/restoration state and worker exits were inspected
and preserved. Every owned child exited successfully. Final restored Q: is clean
and active with its original configuration. These selected measurements are not
the full performance matrix and MEASURED is not an acceptance threshold verdict.

### Interpretation

The restart/Verifier-off experiment strongly supports Verifier instrumentation
as the main cause of the large random-I/O slowdown. Random Q1 GUI numbers return
to the previous range; Q32 reads exceed it, while Q32 writes remain below the
previous 1555–1648 MB/s range. Sequential Q8 read/write remain below the historical
prewarmed CLI numbers (approximately 37/21 GB/s). Different preparation/scoring
and VM/host conditions still prevent a precise historical regression verdict.
Verifier alone should not be claimed to explain every remaining difference.

### What Driver Verifier does and how to control it

Verifier is Windows' driver testing tool. It checks kernel-driver memory use,
locking, execution-level rules and I/O behavior; violations can intentionally
stop Windows to expose the bug. These extra checks can impose substantial
overhead. QueueCache uses it for correctness tests; performance comparisons
should record its state explicitly. See
[Microsoft's Driver Verifier guide](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/driver-verifier).

Run commands from an elevated terminal. To disable it, run `verifier /reset` and
restart Windows. To enable standard checks for one installed driver, run
`verifier /standard /driver QueueCache-0.4.249.1-A31F6D68C608.sys` and restart.
Use the actual installed driver's filename after an upgrade. `verifier /query`
shows current runtime verification; `verifier /querysettings` shows configured
settings, which may still require a restart. The reset command can return a
nonzero reboot-required result after successfully clearing the configuration;
the configured state and post-restart runtime state were both checked here.

### Remaining sequential Q8 write measurement

Because queued sequential GUI scores remained below history, the maintained
runner measured only `sequential-write-q8-Idle-timingFalse`, with the same budget,
DiskSpd hash, three repetitions and five-second scoring duration. Exact run:
`QueueCache-Verify-20260930-201147-2e87df2dd0534faaa041ab030fcd2a07`.
It completed 3/3 with clean restoration at 20:14:05 UTC.

| Repetition | Off write MB/s | Off IOPS | Write p99 ms | Copy offloads during collection | New capacity waits / errors |
|---|---:|---:|---:|---:|---:|
| 1 | 15781.173 | 15050.10 | 0.587 | 151123 | 0 / 0 |
| 2 | 14107.219 | 13453.69 | 0.557 | 141036 | 0 / 0 |
| 3 | 15449.857 | 14734.13 | 0.600 | 152451 | 0 / 0 |

All three readiness files are true; 51 samples per repetition cover the exact
intervals, with maximum gaps 0.235/0.240/0.242 seconds. Final reports, all 369
nonempty raw files, control traces, recovery/restored state and successful owned
worker exits were read and preserved. Counter differences include warmup/close,
not just the scoring span. This establishes that large-write copy offloading
works, without capacity/error stalls during collection. It does not explain
the remaining throughput gap. Historical custom CLI preparation, observer cost
and VM/host conditions are still uncontrolled; no older driver was installed
for a same-session bisect. The sequential historical gap remains an open
performance investigation, not a proven regression in the removal code.

### Restoration after measurement

After both off runs, Q: was flushed before restoring the original Verifier
configuration: `/flags 0x209bb /driver QueueCache-0.4.249.1-A31F6D68C608.sys`,
with `/bootmode resetonbootfail`. A second approved planned restart was issued.
After the second restart, runtime `verifier /query` confirmed 0x209bb and the
same module, load 1/unload 0. The original reset-on-boot-failure policy was restored.
The first startup observation found the saved Q: profile intact but its runtime
cache not yet active; the supported `qcache policy restore` was used to reapply
that saved profile. Final cache state is recorded below. No cache defaults or driver
source changed. The disposable eject-test backing disk stayed detached.

Final checks: Q: is active Fast/Idle with its original 2048 MiB budget, zero
dirty/in-flight bytes, zero errors and an unchanged saved profile. Runtime
Verifier is 0x209bb with the intended CI module loaded once and never unloaded
in this boot. No benchmark processes or temporary CDM tasks remain. A read-only
Proxmox configuration check confirms virtio2 absent and the eject-test backing
disk preserved in unused storage. The final restart was for restoring Verifier,
not recovery of the incomplete removal case. Its earlier raw evidence remains
unchanged and data survival has not become verified.
