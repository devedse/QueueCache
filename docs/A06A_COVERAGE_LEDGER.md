# A06a verification coverage ledger

Updated: 2026-09-24. This ledger maps remaining correctness and usability claims
to current code, maintained evidence and release gates. It does not turn a missing
check into a failure or a scoped PASS into general qualification. Detailed run
evidence remains in [RAM_FIRST_IMPLEMENTATION_TRACKER.md](RAM_FIRST_IMPLEMENTATION_TRACKER.md).

| Claim / path | Owning code | Maintained proof / evidence | Current status | Gate |
|---|---|---|---|---|
| Paging-marked reads observe newest cached sectors | `cachepolicy.h`; `writecache.cpp::Read` | No mixed-path installed proof; early bypass skips dirty bytes | Source defect; implementation pending T075 | T075/T079 before next active-C: experiment |
| Paging-marked writes cannot be overwritten by an older drain | `writecache.cpp::Write`, drainer; `cacheblocks.inl::InvalidateCleanRange` | Clean-only invalidation does not reconcile dirty/in-flight versions | Source defect; implementation pending T076 | T076/T079 before next active-C: experiment |
| Paging progresses during capacity/lower waits with bounded resources | Request service, cache waits and lower completion | Plan-31 zero counters cannot establish this property | Open T077; do not equate unreachable counters with proof | T077/T079 before next active-C: experiment |
| System worker identity and every mode's released-cache bytes are checked | `SystemFileScenarios`, `SystemImageScenarios`, verification worker/runner | Plan-31 actual role transition untested; only last image selected for final oracle | Partial; repair T078, exact proof T079 | A08; implementation-first revision 4 |
| Fitting full/sector-valid Fast admission avoids lower I/O | `writecache.cpp` `Write`; `sectorcoverage.h` | `policies`: diagnostics-V2 lower-attempt and RAM/disk byte oracles | Passed on 0.4.57.1 for 512-byte sectors, parallelism 1/2/4, retention off/on | Preserve through A13 |
| Fitting writes and cached reads progress during normal Idle drain | Foreground/drainer; `CacheScenarios`; plan-16 `drain-decision` | `policies` 60-second foreground/background case; seeded parallelism 1/2/4 comparison contract | Fitting case passed on 0.4.57.1; exact installed 0.4.67.1 comparison completed 24/24, with tuning decision still open | T050; A11 |
| Deferred age, Idle and watermark triggers occur at their contract boundaries | `cachepolicy.h`; `Drainer` | Native truth table; plan-14 `pressure` VM scenario | Passed on exact installed 0.4.64.1 | Preserve through A13 |
| Automatic and Fixed 50/100 write pools apply backpressure without overwriting data | Capacity loop; `QcWriteLimit` | Plan-14 `pressure` writes 80 MiB through a 64 MiB cache and verifies disk bytes | Passed on exact installed 0.4.64.1 | Preserve through A13 |
| Fixed 0 uses the explicit ordered quota fallback | `writecache.cpp` quota barrier | Plan-14 `pressure` checks no write ownership/capacity waits, quota-barrier increase and final bytes | Passed on exact installed 0.4.64.1 | Preserve through A13 |
| Single request larger than its write quota/capacity | `writecache.cpp` quota fallback | No focused maintained request-boundary test | Open | T056 production-supported scope |
| Lower-write completion failure retains dirty data and retry persists it | Completion/retry; `FileTests` coalescing mode | A06 run `74640fa5-9964-4068-bbf5-d47d36376a8d` | Passed on 0.4.57.1 | Preserve through A13 |
| Explicit lower-flush failure is visible and recoverable | Barrier/flush completion; `FileTests` flush-policy mode | A06 run `b8efd1c5-4860-4966-bd8c-cc1ceea9de9f` | Passed on 0.4.57.1 | Preserve through A13 |
| Failure in a later sparse segment preserves every acknowledged segment | Sparse drainer selection/completion | No deterministic maintained interleaving | Open | T052 before A09 |
| New same-range data replaces an old in-flight version without stale reads | Slot version/pin/retirement paths | Plan-17 `policies` adds an exact 512-byte before/after in-flight observation and newest RAM/disk bytes; timing still does not force the completion interleaving | Partial; split-CLI Q: smoke passed, exact-build and controlled ordering pending | T052 before A09; T056 production |
| Allocation failures 6/7 leave finite, recoverable state | Allocation hooks in `writecache.cpp` | Hooks exist; retired wrapper mapping records the gap | Open | T053 if reachable for A07; otherwise enforced restriction, then T056 |
| Queued/capacity-wait cancellation completes once and preserves neighboring data | CSQ/request worker/capacity loop; `WriteTests.CancelledWrite` | Raw disposable-region scenario exists outside the supported runner | Partial implementation, no current maintained VM result | T053 if reachable for A07; T056 production |
| Strict/explicit flush succeeds only after required persistence | Barriers and lower flush completion | Strict oracles and lower-flush fault pass; queued later-write ordering unforced | Partial | T052 before A09; cutoff claim remains item 11/T056 |
| Restoration reaches an honest clean configuration boundary | `VerificationWorker.FlushForRestoration` | A02 Q1/Q32 cleanup regressions | Passed for secondary-disk runner | Preserve; A08 defines busy-C: semantics |
| Policy admission starts from a clean, idle lower-data boundary | `SectorScenarios`; runner preparation | One 0.4.57.1 run preserved INCOMPLETE, fresh run passed; intervening activity source unproven | Diagnosis/preparation evidence open | T049 before changing the predicate |
| Active system disk, paging, memory pressure, shutdown/power/PnP | `driver.cpp`; cache lifecycle | Usage accounting/ordering, plan-26 paging progress and plan-27 normal activation/registration/power-resume implementation | Partial; exact active Fast passed, while packaged normal Fast/Strict, saved restart and lifecycle proof remain open | A07-A09; T053/T056; T070-T074 |
| Registration can be recovered without a working installed CLI/driver | Packaging recovery scripts | Host independence checks only | Partial | T054 before A09; A10/A14 full servicing |
| File-level TRIM guard/reuse and range ordering | Filesystem/storage/driver TRIM path | Attached/unfiltered probes both return Win32 326 | Unsupported probe recorded; correctness path unexercised | Enforce support scope or qualify in T056 |
| Native 4Kn behavior | Sector coverage and filesystem I/O | Current sector scenario explicitly rejects non-512 logical sectors | Unqualified | Exclude/enforce or qualify in A13/T056 |

Review follow-ups: T067 records the pre-A01 C: large-image crash and subsequent
application failures (cause unknown). T068's source-level usage-notification/Enable
race is repaired, but installed-driver paths remain to be proved. T069 identified
coarse timer-completion checks, watermark attribution and unsampled reservation
gaps in `pressure`; plan 14 closed that measurement gap on exact installed
0.4.64.1. T067/T068 remain as defined in the handover and gate their corresponding
C: readiness claims.

Plan 15 stopped at case 4/24 on 0.4.66.1 because NTFS metadata ownership varied
within its documented 64 KiB bound. Restoration passed and the incomplete run is
retained privately. Plan 16 completed 24/24 on exact installed 0.4.67.1 with
clean restoration. Its measured parallelism trade-off is recorded in the tracker;
the alpha tuning/acceptance decision remains open. Allocation/cancellation work
follows the A07 reachable-path map so it targets actual system-disk risk.
