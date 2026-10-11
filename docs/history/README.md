# Historical records

Dated investigation reports, the former implementation tracker and earlier design
records. They are kept for their measurements and reasoning and are **not
maintained**: statements about "current" code, versions or plans refer to the date
of each document. Current behavior is described in the documents one level up;
current decisions are in [performance findings](../PERFORMANCE_FINDINGS.md) and
gaps in [known issues](../KNOWN_ISSUES.md). Completed plans and handovers were
removed; Git history keeps them.

| Document | What it records |
|---|---|
| [RAM_FIRST_IMPLEMENTATION_TRACKER.md](RAM_FIRST_IMPLEMENTATION_TRACKER.md) | Item-by-item implementation and verification log, 2026-09 to 2026-10-11 |
| [DEVELOPER_VERIFICATION.md](DEVELOPER_VERIFICATION.md) | Detailed per-suite contracts and their plan history, up to plan 119 |
| [RAM_FIRST_PERFORMANCE_PLAN.md](RAM_FIRST_PERFORMANCE_PLAN.md) | The original RAM-first cache contract audit (2026-09-17) |
| [ALPHA_PRODUCT_DECISIONS.md](ALPHA_PRODUCT_DECISIONS.md) | Private-alpha audience, Fast/C: decisions and accepted risks (2026-09-20/23) |
| [T085_APPLICATION_CACHING_DESIGN.md](T085_APPLICATION_CACHING_DESIGN.md) | Caching ordinary application and memory-mapped writes (2026-09-25) |
| [SYSTEM_DISK_OPERATION_MAP.md](SYSTEM_DISK_OPERATION_MAP.md) | Paths that reach the filter on the system disk (2026-09-24/28) |
| [DRIVE_DISCONNECT_OWNERSHIP_AUDIT.md](DRIVE_DISCONNECT_OWNERSHIP_AUDIT.md), [DRIVE_DISCONNECT_DELIVERY_SUMMARY.md](DRIVE_DISCONNECT_DELIVERY_SUMMARY.md) | Eject and surprise-removal handling (2026-09-30) |
| [MANAGED_DISKS_FOUNDATION_VERIFICATION_20261003.md](MANAGED_DISKS_FOUNDATION_VERIFICATION_20261003.md) | Shared allocator refactor checks before managed disks |
| [PERFORMANCE_INVESTIGATION_20260930.md](PERFORMANCE_INVESTIGATION_20260930.md), [SEQUENTIAL_PEAK_REPRODUCTION_20260930.md](SEQUENTIAL_PEAK_REPRODUCTION_20260930.md), [WINDOWS_EJECT_AND_CDM_20260930.md](WINDOWS_EJECT_AND_CDM_20260930.md) | Driver Verifier versus scores, sequential peaks, Windows eject (2026-09-30) |
| [WRITE_PERFORMANCE_TRAJECTORY.md](WRITE_PERFORMANCE_TRAJECTORY.md), [WRITE_PERFORMANCE_20261010.md](WRITE_PERFORMANCE_20261010.md) | Small-write work and the PR #8 72-case write matrix |
| [CACHE_LAYOUT_INVESTIGATION_20261008.md](CACHE_LAYOUT_INVESTIGATION_20261008.md) | Allocation history, chunk allocator and copy flags |
| [SUSTAINED_CACHE_VALIDATION_20261009.md](SUSTAINED_CACHE_VALIDATION_20261009.md) | Concurrency, 30-minute sustained use, map cost, read recall |
| [RAM_READ_DIAGNOSTICS_20261010.md](RAM_READ_DIAGNOSTICS_20261010.md) | Plans 115-117: attribution, cached priority/affinity and coordination counters that led to asynchronous split reads |
| [CALLER_BACKOFF_20261010.md](CALLER_BACKOFF_20261010.md), [PRIORITY_COST_20261010.md](PRIORITY_COST_20261010.md), [RAM_READ_SCHEDULING_20261010.md](RAM_READ_SCHEDULING_20261010.md) | Caller backoff, process priority, and the rejected RAM read queue |
