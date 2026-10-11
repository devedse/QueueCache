# Write-performance reference, 2026-10-10

The maintained `write-performance` suite completed **72/72** on signed CI
0.4.476.1, with read recall enabled. Collection, driver health checks,
telemetry readiness/coverage and restoration succeeded. This establishes the
current reference; it does not prove the cause of a cross-day score difference.

The VM recorded run
`QueueCache-Verify-20261009-220533-0439cff369ce45c8b577e23951b77317`,
22:05:33–22:59:38 UTC on 2026-10-09. The approximately 54-minute duration includes
preparation, explicit drains between cases, and restoration. Each DiskSpd score
window was ten seconds; several intervening flushes took roughly 45–53 seconds.

## Identity and measurement contract

- Loaded filter SHA-256:
  `88880997C11D27A7B0C89A53CBF1471F97CF06DBA46B2540C969544BB99D945A`.
- Loaded RAM provider SHA-256:
  `2F6CA136A0FB96F1C3BD946639AC3DB2EC8880D0B28D1DE0F2B9A73BFB5178D1`.
- CI source `56530c59e7f8ad310ee1b5666cef71890ef01985`; the native sources
  are unchanged by PR #8's subsequent UI and runner-cleanup commits.
- CrystalDiskMark 9.0.3's DiskSpd 2.2 x64, SHA-256
  `7281BF6DA6C03797016EDDF2E8AAEC4C644AE893D403D57A030B7E2E14B61079`.
  The historical reference used the same executable/hash.
- Plan 104, `--suite write-performance --budget-mib 2048 --repeats 3
  --duration-seconds 10`, no case filter. The 72-case workload contract is unchanged
  from the plan-94 reference on 0.4.426.1.
- One thread, fitting 1 GiB files, random 4 KiB Q1/32 and sequential 1 MiB Q1/8,
  Off/Eager/Idle, detailed timing off/on, DiskSpd `-Zr`. This is distinct from the
  concurrency suite's precomputed `-Z1M` buffer.
- Elevated normal-priority foreground command, Verifier off, last-access updates
  disabled, Q: on clean non-OS physical disk 1. UI and competing benchmarks were
  stopped; delay/fault hooks were cleared and read recall was on before the run.

All 72 raw outputs contain valid XML with only the recognized CDM score trailer,
successful exits and empty error output. Every readiness record is present;
telemetry brackets every recorded interval without exceeding its two-second gap
limit. Driver instance/generation and error counts remain stable within each case.
Restoration returned the original enabled 2 GiB policy, timing off, with zero dirty
and in-flight bytes and no driver error. Raw outputs, process records, control
traces and restoration evidence remain private in the exact run directory.

DiskSpd scores exclude its five-second warm-up. Driver before/after counters and
telemetry intervals include process start-up and warm-up; they are not exact
DiskSpd score-window counters.

## Comparison and limits

The earlier complete reference is
`QueueCache-Verify-20261008-173325-f4a1f886dddd474297c62dc1a60535c0`
on 0.4.426.1. The table contains three repetitions per configuration, with median
throughput, current throughput range and median write p99.

Cached random-write medians are 1–7% higher; sequential Q1 medians are 29–32%
higher. Sequential Q8 medians are 5–14% lower, while current p99 is lower and
repetition ranges overlap the older ones. **A Q8 throughput regression caused by
this PR is not established.** The uncached disk itself is much slower: timing-off
sequential Q8 falls from 243.46 to 84.92 MiB/s and random Q1 from 4.70 to 1.02
MiB/s. Eager/Idle write-back interacts with that disk during measurement.

The old allocator's installer cannot inspect the newer RAM-provider state and
refuses a downgrade; no controlled old/current reinstall comparison was obtained.
The allocator and RAM-hit copy flags already preceded PR #8. Its existing
same-build RAM-only concurrency check found writes within 0.6% of the preceding
build ([sustained validation](SUSTAINED_CACHE_VALIDATION_20261009.md)). Keep the
historical Q8 difference visible, use this complete run as the new reference, and
require a controlled same-session comparison before changing allocator policy.

The UI was closed throughout scoring. This run does not measure the new
100 ms refresh/full-screen map rendering cost. The installed runner predates
automatic workload cleanup; its exact workload folder was removed separately
after completion and evidence preservation.


| Workload | Cache | Detailed timing | 0.4.426.1 median MiB/s | 0.4.476.1 median MiB/s | Current min–max MiB/s | Current p99 ms |
|---|---|---|---:|---:|---:|---:|
| RND4K Q1T1 | Off | off | 4.70 | 1.02 | 0.91–1.03 | 19.990 |
| RND4K Q1T1 | Off | on | 4.62 | 0.96 | 0.88–1.03 | 24.299 |
| RND4K Q1T1 | Eager | off | 923.05 | 939.48 | 928.60–941.83 | 0.014 |
| RND4K Q1T1 | Eager | on | 859.88 | 868.39 | 863.20–870.77 | 0.015 |
| RND4K Q1T1 | Idle | off | 911.42 | 931.51 | 922.23–931.99 | 0.014 |
| RND4K Q1T1 | Idle | on | 860.28 | 869.98 | 866.86–875.18 | 0.014 |
| RND4K Q32T1 | Off | off | 8.23 | 3.22 | 2.34–4.19 | 193.821 |
| RND4K Q32T1 | Off | on | 7.91 | 3.23 | 2.54–3.30 | 276.828 |
| RND4K Q32T1 | Eager | off | 1,202.27 | 1,244.17 | 1,208.99–1,274.78 | 0.073 |
| RND4K Q32T1 | Eager | on | 1,118.08 | 1,145.46 | 1,114.10–1,170.61 | 0.171 |
| RND4K Q32T1 | Idle | off | 1,233.79 | 1,258.00 | 1,200.57–1,266.41 | 0.075 |
| RND4K Q32T1 | Idle | on | 1,089.44 | 1,169.85 | 1,141.12–1,200.77 | 0.165 |
| SEQ1M Q1T1 | Off | off | 107.19 | 48.05 | 39.86–53.75 | 96.977 |
| SEQ1M Q1T1 | Off | on | 106.79 | 43.26 | 42.96–56.24 | 86.071 |
| SEQ1M Q1T1 | Eager | off | 6,673.83 | 8,687.71 | 8,447.35–8,713.89 | 0.180 |
| SEQ1M Q1T1 | Eager | on | 6,616.08 | 8,555.84 | 8,485.51–8,601.30 | 0.185 |
| SEQ1M Q1T1 | Idle | off | 6,694.51 | 8,717.38 | 8,237.96–8,738.66 | 0.175 |
| SEQ1M Q1T1 | Idle | on | 6,526.67 | 8,585.31 | 8,508.09–8,672.43 | 0.176 |
| SEQ1M Q8T1 | Off | off | 243.46 | 84.92 | 82.32–92.61 | 315.159 |
| SEQ1M Q8T1 | Off | on | 245.35 | 89.51 | 73.13–96.30 | 366.042 |
| SEQ1M Q8T1 | Eager | off | 15,349.85 | 13,952.95 | 13,767.53–14,814.19 | 0.425 |
| SEQ1M Q8T1 | Eager | on | 14,631.37 | 13,220.48 | 12,843.96–14,283.42 | 0.427 |
| SEQ1M Q8T1 | Idle | off | 15,129.27 | 14,410.79 | 12,555.14–14,561.44 | 0.444 |
| SEQ1M Q8T1 | Idle | on | 15,403.70 | 13,294.41 | 13,016.38–13,389.41 | 0.432 |
