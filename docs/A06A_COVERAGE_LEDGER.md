# A06a verification coverage ledger

Updated: 2026-09-22. This ledger maps remaining correctness and usability claims
to current code, maintained evidence and release gates. It does not turn a missing
check into a failure or a scoped PASS into general qualification. Detailed run
evidence remains in [RAM_FIRST_IMPLEMENTATION_TRACKER.md](RAM_FIRST_IMPLEMENTATION_TRACKER.md).

| Claim / path | Owning code | Maintained proof / evidence | Current status | Gate |
|---|---|---|---|---|
| Fitting full/sector-valid Fast admission avoids lower I/O | `writecache.cpp` `Write`; `sectorcoverage.h` | `policies`: diagnostics-V2 lower-attempt and RAM/disk byte oracles | Passed on 0.4.57.1 for 512-byte sectors, parallelism 1/2/4, retention off/on | Preserve through A13 |
| Fitting writes and cached reads progress during normal Idle drain | Foreground/drainer; `CacheScenarios` | `policies` 60-second `foreground-background` case | Passed on 0.4.57.1; no capacity pressure or cold misses | T050/T051; A11 |
| Deferred age, Idle and watermark triggers occur at their contract boundaries | `cachepolicy.h`; `Drainer` | Native truth table; plan-12 `pressure` VM scenario | Native proof exists; maintained VM scenario implemented, VM run pending | T051 before A09 |
| Automatic and Fixed 50/100 write pools apply backpressure without overwriting data | Capacity loop; `QcWriteLimit` | Plan-12 `pressure` writes 80 MiB through a 64 MiB cache and verifies disk bytes | Implemented, VM run pending | T051 before A09 |
| Fixed 0 uses the explicit ordered quota fallback | `writecache.cpp` quota barrier | Plan-12 `pressure` checks no capacity waits, quota-barrier increase and final bytes | Implemented, VM run pending | T051 before A09 |
| Single request larger than its write quota/capacity | `writecache.cpp` quota fallback | No focused maintained request-boundary test | Open | T056 production-supported scope |
| Lower-write completion failure retains dirty data and retry persists it | Completion/retry; `FileTests` coalescing mode | A06 run `74640fa5-9964-4068-bbf5-d47d36376a8d` | Passed on 0.4.57.1 | Preserve through A13 |
| Explicit lower-flush failure is visible and recoverable | Barrier/flush completion; `FileTests` flush-policy mode | A06 run `b8efd1c5-4860-4966-bd8c-cc1ceea9de9f` | Passed on 0.4.57.1 | Preserve through A13 |
| Failure in a later sparse segment preserves every acknowledged segment | Sparse drainer selection/completion | No deterministic maintained interleaving | Open | T052 before A09 |
| New same-range data replaces an old in-flight version without stale reads | Slot version/pin/retirement paths | `policies` observes in-flight state and exact bytes; timing does not force the exact interleaving | Partial | T052 before A09; T056 production |
| Allocation failures 6/7 leave finite, recoverable state | Allocation hooks in `writecache.cpp` | Hooks exist; retired wrapper mapping records the gap | Open | T053 if reachable for A07; otherwise enforced restriction, then T056 |
| Queued/capacity-wait cancellation completes once and preserves neighboring data | CSQ/request worker/capacity loop; `WriteTests.CancelledWrite` | Raw disposable-region scenario exists outside the supported runner | Partial implementation, no current maintained VM result | T053 if reachable for A07; T056 production |
| Strict/explicit flush succeeds only after required persistence | Barriers and lower flush completion | Strict oracles and lower-flush fault pass; queued later-write ordering unforced | Partial | T052 before A09; cutoff claim remains item 11/T056 |
| Restoration reaches an honest clean configuration boundary | `VerificationWorker.FlushForRestoration` | A02 Q1/Q32 cleanup regressions | Passed for secondary-disk runner | Preserve; A08 defines busy-C: semantics |
| Policy admission starts from a clean, idle lower-data boundary | `SectorScenarios`; runner preparation | One 0.4.57.1 run preserved INCOMPLETE, fresh run passed; intervening activity source unproven | Diagnosis/preparation evidence open | T049 before changing the predicate |
| Active system disk, paging, memory pressure, shutdown/power/PnP | `driver.cpp`; cache lifecycle | T023 operation map; usage-path counter/ordering and fail-closed Enable restriction implemented | Partial; installed-driver proof and actual support remain open | A07-A09; T053/T056 |
| Registration can be recovered without a working installed CLI/driver | Packaging recovery scripts | Host independence checks only | Partial | T054 before A09; A10/A14 full servicing |
| File-level TRIM guard/reuse and range ordering | Filesystem/storage/driver TRIM path | Attached/unfiltered probes both return Win32 326 | Unsupported probe recorded; correctness path unexercised | Enforce support scope or qualify in T056 |
| Native 4Kn behavior | Sector coverage and filesystem I/O | Current sector scenario explicitly rejects non-512 logical sectors | Unqualified | Exclude/enforce or qualify in A13/T056 |

The plan-12 managed Release build and host-safe runner contracts pass. The next
step is the exact installed-build `pressure` VM run. A VM failure keeps T051 open
and is repaired in the owning path before C: activation. Allocation/cancellation
work follows the A07 reachable-path map so it targets actual system-disk risk.
