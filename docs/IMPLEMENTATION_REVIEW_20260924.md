# Implementation review and next repair slice

Reviewed 2026-09-24 at source `30dd36e`. Scope: changes since `89436a8`,
especially `9580c7e`, `f77ccf7`, `0cbb04b`, `d8c0771`, `b189d98` and
`33543d3`, plus their current callers and documentation. This is a source and
evidence review, not a new kernel qualification run. The tracker owns status.
The host management/protocol/verification regression harness passed during this
review. Documentation link and whitespace checks passed; no driver or verifier
source was changed by the review commit.

## What should be kept

- `9580c7e` repairs the two earlier coherence defects: paging-marked reads use
  resident sector overlays; paging-marked writes drain older overlapping versions
  and fence the direct lower write. Paging data is not admitted as new RAM data.
  The foreground owner, pins and existing version machinery are reused.
- The same change verifies each Fast/Strict image after release and every passed
  image during final restoration, and shares recorded system-target comparison.
  These address actual omissions in the earlier verifier.
- `0cbb04b` permits the existing file/restart workflow with active system usage
  paths and checks an admission delta. It supplies preparation, not restart proof.
- CI-built plan 37 passed focused Q: coherence and policy cases against loaded
  0.4.92.1. Exact 0.4.92.1 passed bounded active C: Fast/Strict image oracles.
  Preserve those byte checks and raw results. Their scope is narrower than the
  complete ownership/progress contract.

## Findings and required disposition

Severity describes the release consequence, not a claim that a crash reproduced.

| Finding | Evidence and consequence | Required work |
|---|---|---|
| R1 / high: page-in progress remains conditional | `writecache.cpp::Read` calls `OriginalIo(c, irp, false)` for a paging read with no resident range, even for the normal top-level request. `OriginalIo` can service reads on some other waits, but `QcCacheTryPagingReadProgress` synchronously invokes another `Read(...false,false)` on that same worker. One nested request bounds recursion, not completion time. A slow independent lower page-in can occupy the worker even after its original blocker completes; a second required page-in cannot use that service lane. The currently observed 72 cooperative completions prove that one lane is reachable, not absence of a dependency cycle. No current VM deadlock is claimed. | T082: distinguish top-level from nested service and account for every blocking wait. Prove the required dependency can complete; introduce a bounded independently owned service/completion path where synchronous service cannot provide that guarantee. Do not just add recursion or longer timeouts. |
| R2 / high: the new overlap test does not force an already-submitted old lower write | `Drainer` increments `InFlightBytes`, releases the mutex, and sleeps for `DelayMs` BEFORE calling `LowerIo`. `RunObservedPagingOverlap` observes that counter during a 2-second delay. Thus it establishes selected/pinned old data, not lower-stack submission. Its 1,536-byte size and device-wide overlap count also do not uniquely identify the owned file/range; mapping preparation may consume the delay. | T083: add a bounded range/request-specific gate and phase evidence at the real submission/completion boundary. Keep the existing case as an observed selected-write regression. Add the missing later cached C, partial/failure/cancel/dependency orders to the maintained runner. |
| R3 / high: equal aggregate counts are insufficient admission proof | `SectorScenarios.VerifyAdmissionAttempts` now accepts nonzero lower writes when routed paging request/completion deltas match. That is a useful explanation candidate, but counters can straddle the window and refer to different requests; equality alone cannot assign a lower write to the owned operation. The implementation's message admits this, while the surrounding check still reports admission PASS. The counters live in each disk's `QC_CACHE`, so “process-wide” is also the wrong scope: they aggregate requests from all processes on that device. | T084: preserve byte/policy PASS, separate an unproven zero-lower-I/O assertion, and attribute attempts at their source/request boundary. Require no lower I/O for the owned supported fitting Fast write. Test cross-window requests and unrelated traffic; never subtract counts as a causal proof. |
| R4 / high product gap: normal application acceleration is not established | All `IRP_PAGING_IO` writes still use ordered lower I/O and paging misses are not retained. This includes some ordinary file-cache/mapped-file traffic, not just pagefile data. Avoiding pagefile duplication is intentional, but a successful Paint/Photos run cannot establish that its image was admitted to QueueCache RAM or that reopening benefited from this cache. | T085: characterize ordinary buffered, mapped and unbuffered paths in one bounded Q: case, then implement the remaining supported application admission/retention behavior with the T082 progress contract. Never classify pagefile sectors from PID, usage count or the flag alone. If reliable classification needs a new architecture, bring that concrete design back for review before building it. Do not silently narrow the RAM-first product promise. |
| R5 / medium: range draining stalls unrelated drain selection and reduces batching | While `RangeDrain=true` and `RangeForward=false`, the drainer filter skips every non-overlapping block. While `RangeForward=true`, it allows unrelated blocks but still forces `batchBlocks=1`. This preserves ordering but the documentation overstates unrelated progress, and frequent paging writes can impair unrelated persistence/latency. | T082 includes the scheduling correction: prioritize the overlap without freezing all other eligible ranges; retain normal safe batching outside the fence. Test an unrelated eligible range while the targeted range is held, and preserve oldest-version order. |
| R6 / high evidence gap: application capture and experiment identity were incomplete | The owner used `baseline.bmp` for both edits, not the separate `cached.bmp`. No save/open interval trace was running. CrashDumpEnabled is 0. Q: also has an active 2 GiB cache and CrystalDiskMark/DiskSpd workload, so this was not a 512 MiB-only or isolated experiment. | T086/T080: preserve this successful owner observation with its actual conditions. Before further pressure/reproduction work, arrange working dump collection, exact file/operation times and off-target evidence using the maintained capture facilities; coordinate the competing workload. Do not repeat the same successful manual action merely to obtain another green result. |
| R7 / medium: current documentation contradicts the tracker | The operation map and coverage ledger still call the driver undeployed, the handover's current table names 0.4.87.1, and known issues claims a diagnosed closure that the evidence does not establish. Policy text also obscures paging admission limitations. | T087: corrected in this review commit. Keep dated historical checkpoints intact and label them historical; current summaries refer to the tracker and this review. Runtime output wording and changed verifier verdicts belong to T084 and a new verification-plan version. |

## Disposition update: plan-38 source candidate

Source only. Native Release/Debug builds and host contracts pass; nothing below
is installed or VM-verified.

- R1 (T082): the synchronous page-in path is replaced for the common case. A
  paging read that needs lower I/O goes to a per-disk paging-read thread from
  both the top-level worker and the service lane; neither waits for its lower
  completion. The thread depends only on the cache mutex and lower completion.
  Ownership, pin lifetime, lock order and the wait graph are in the
  [operation map](SYSTEM_DISK_OPERATION_MAP.md#paging-read-ownership-and-wait-order-plan-38-t082).
  Remaining synchronous cases are reads over 1 MiB, a full 64-entry table and
  the bounded lane during destructive/control requests. A forced installed
  dependency test is still required before T082 is complete.
- R3 (T084): Diagnostics V8 attributes every lower attempt to its issuing path.
  Admission verdicts no longer subtract aggregate counts; see
  [plan 38](DEVELOPER_VERIFICATION.md#suites-plan-version-38).
- R5: the fence forces only overlapping versions. Unrelated dirty data keeps its
  own drain policy (the earlier code, and Luna's first attempt, forced it through
  `RangeDrain`) and normal batching; batches never mix fenced and unrelated blocks.
  Luna's uncommitted drainer edit had unbalanced conditions and did not compile.
- R2, R4, R6 are unchanged: T083 submission gate, T085 application admission and
  T086 capture remain open.

## Actual application observation

The owner reports two successful Paint edits and Photos opens of `baseline.bmp`:
red with C: caching disabled, yellow after C: was enabled. The screenshot shows
the yellow/red edited image open in Photos. The second file modification is
2026-09-24 15:22:29 UTC (first save 15:19:52 UTC), after the temporary 512 MiB
Fast/Idle Apply. The file is 365,948,982 bytes after Paint's save; `cached.bmp`
remains unchanged. Paint is 11.2605.81.0; Photos is 2026.11080.24002.0.

Read-only follow-up found C: active, clean, no cache errors or routed failures,
and no new critical/error System or Application events in the inspected window.
The loaded service still names the immutable 0.4.92.1 SYS. Across the earlier
post-Apply snapshot and first review snapshot, C: AcceptedBytes increased by
622,592 bytes, while routed reads/writes and cooperative completions increased.
Those are device-wide observations across a broad interval, not image attribution.
They do not show the roughly 349 MiB save entering QueueCache RAM.

Q: was active with a 2 GiB budget and CrystalDiskMark/DiskSpd present. Total cache
reservation was 2.5 GiB; available physical memory was about 2.33 GiB. The live
512 MiB C: pagefile reported 85 MiB usage (98 MiB peak). These are snapshots,
not peak memory or proof of an imposed paging dependency during the save.
Dump capture was disabled. No VM configuration or cache state was changed during
this review. C: and Q: remain active; do not assume a clean verification baseline.

Verdict: ordinary application save/open did not reproduce the reported failure.
Exact save-to-open delay, image-range pending state, independent post-drain bytes,
and the separately requested post-administrative-drain Photos attempt are absent.
T080 is PARTIAL, not FALSE and not complete. Historical BSOD cause remains unknown.

## Execution order and acceptance

1. T082 with the minimum T083 control needed to expose its dependency: repair
   request progress and range scheduling first. Model queue/active/lower-owned/
   completed states, remove locks, MDL lifetime, cancellation and single completion.
   For each wait name the event owner and why it cannot depend on the waiting worker.
2. T083 and T084: verify the repaired source at the real boundaries and make
   admission verdicts truthful. One maintained runner; no extra executable or
   private orchestration. Gate use is bounded and restricted to the disposable
   non-OS target, disarmed on completion/failure; no mounted-filesystem raw writes.
   Required orders: A dirty -> paging read; A lower-submitted -> paging B;
   B outstanding -> later cached C; partial neighbors; failed/short later sparse
   segment; cancellation; required page-in while capacity/lower work waits.
   Record request/range identity and phase acknowledgements, compare independent
   active/released bytes, and verify failure preserves acknowledged dirty ownership.
3. T085: finish the application RAM-admission contract after progress is sound.
   Do not reopen the old bypass defect or reintroduce the failed unbounded paging
   admission design. Keep Strict and explicit durability, bounded memory and
   pressure fallback. Prove ordinary file workloads exercise the advertised path.
4. T086/T080: finish capture and the missing post-drain application comparison.
   Then T081: create the existing owned restart oracle while C: is active, perform
   a coordinated normal reboot, verify bytes and restored profile; separately
   run bounded memory pressure with measured paging activity and total cache budget.
   Normal reboot and pressure have separate verdicts. Do not require the lost
   historical crash to recur before closing independently demonstrated repairs.
5. Resume T068 registration/failure ordering, T054 real offline recovery and the
   supported lifecycle cases. T050 drain tuning follows correctness repairs;
   A10 servicing and A11 final performance/smoke precede A12 alpha release.
   A13-A16 remain production gates. Unavailable power modes need an appropriate
   test environment; they need not block the bounded application investigation.

Each implementation handoff must list changed behavior, a focused host/native
check, exact VM evidence or the required installation, and remaining failed or
unexercised paths. Do not call T082/T085 implemented merely because a test was
added. Do not run the broad performance matrix during this repair slice.

Windows context: ordinary file data can use the Windows system cache and mapped
views. See Microsoft's [file caching documentation](https://learn.microsoft.com/en-us/windows/win32/fileio/file-caching)
and [file-backed sections](https://learn.microsoft.com/windows-hardware/drivers/kernel/file-backed-and-page-file-backed-sections).
The specific QueueCache consequences above follow from the reviewed source.
