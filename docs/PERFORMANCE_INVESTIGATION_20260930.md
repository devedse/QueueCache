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
qcache developer verify Q: --suite write-performance --budget-mib 2048 --diskspd "C:\Users\davyd\Desktop\CrystalDiskMark9_0_3\CdmResource\DiskSpd\DiskSpd64.exe" --case-filter random-write-q1-Idle-timingFalse --repeats 3 --duration-seconds 5 --output C:\QueueCache-Results
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

The Verifier-off matched comparison remains pending. No driver source or defaults
were changed to improve a score, and the disposable removal disk remains detached.
