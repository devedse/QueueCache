# QueueCache private alpha implementation handover

Revised: 2026-09-23, planning revision 3. Audience: the executing agent and project
owner. End goal: a production-ready QueueCache product. A01-A12 deliver the first
controlled milestone: a private recoverable-VM alpha including Fast caching on the
physical disk backing C:. A13-A16 define the subsequent production qualification
and release gates. None of these planned gates is a readiness verdict.
The executable verification contract is plan 30. Corrected plan-14 `pressure`
passed on exact installed 0.4.64.1. The first plan-15 T050 run on 0.4.66.1
stopped at case 4/24 on a verifier assumption about NTFS metadata, with clean
restoration. Plan 16 corrected that assumption; its exact-build 0.4.67.1 VM run
completed 24/24 with clean restoration. The tuning decision remains open; see
the tracker for measurements and limitations. Plan 17 adds a partial T052
overlap observation and an A07/T026 last-boundary identity recheck; exact-build
0.4.69.1 policy verification passed. Plan 18 adds guarded read-only C:
preflight. Plan 19 adds a bounded owned-file oracle and read-only post-restart
check; both passed on the VM with C: caching disabled. Active C: is not qualified.

Immediate priority: investigate T067, the owner's BMP/Paint/Photos BSOD. Follow
the focused sequence below before resuming general A08 framework expansion or
T050 performance tuning. Exact 0.4.82.1 plan-26 active Fast passed with full bytes,
zero paging mapping/capacity failures and clean restoration. Plan 27 promotes that
policy into normal Enable/public Apply and adds separate Fast/Strict cases; exact
installation of this newer implementation is pending.

## 1. Start here

| Order | Action | Result required before proceeding |
|---|---|---|
| 1 | Read root `AGENTS.md`, this document, `docs/ALPHA_PRODUCT_DECISIONS.md`, and `docs/RAM_FIRST_IMPLEMENTATION_TRACKER.md`. | Understand accepted volatility, preserved correctness requirements, and current evidence. |
| 2 | Inspect Git status/diffs and current HEAD. Do not reset the worktree. | Identify prior uncommitted restoration work, decision documents, and unrelated owner changes. |
| 3 | Read the current status and execute the T067 investigation sequence below. | Reuse existing evidence; produce crash-capture readiness, a focused code review and the concrete prerequisites for controlled active-C: reproduction. A01-A06's scoped passes do not require restarting those tasks. |
| 4 | Follow the revised dependencies in section 5, one small tested change at a time. | Close pre-C: safety and recovery gates before activation; retain separate implementation and verification status. |

This document defines the release sequence and a dated status summary. The
[RAM-first tracker](RAM_FIRST_IMPLEMENTATION_TRACKER.md) is the authoritative
execution/status source for both A-steps and the original 15 optimization items.
Update the dated summary when status changes; the tracker owns detailed evidence.
The decision and cleanup documents define product direction and inventory;
this handover defines the execution sequence. It authorizes no Git history rewrite or automatic
destructive VM recovery. Observe current owner authorization before deployment,
reboot, driver removal or destructive fault experiments.

### Immediate execution sequence: investigate the C: BSOD

The recent work mainly improved verification infrastructure. It produced a
useful uncached C: baseline, but did not reproduce or diagnose the reported
crash. The next implementation run must advance that investigation directly.
Use existing task IDs; the following phases split T067 into concrete work rather
than creating another general framework milestone.

| Order / tasks | Concrete work | Deliverable before moving on |
|---|---|---|
| 1 / T067, T032, T054 | Verify current dump type/location, pagefile or dedicated dump requirements, free disk space, and how to retrieve evidence if the guest cannot boot. Record loaded driver hash/source, matching symbols, Windows/controller details, RAM, cache budget/policy and relevant application versions. Reuse the owner's snapshot/console confirmation; establish the remaining recovery rehearsal. | A usable capture/retrieval procedure and exact remaining recovery blockers. Distinguish configured capture from capture actually exercised. The old snapshot has no surviving dump/Event 1001; do not repeatedly search it without new evidence or wait indefinitely for unavailable old settings. |
| 2 / T067, T023, T052-T053, T068 | Review the incident-relevant driver paths: paging/mapped-file I/O, allocation failure, buffer/MDL/pin lifetime during outstanding I/O, old/new data during drain and overwrite, cancellation/teardown, and usage-notification/activation ordering. Tie each concern to owning code and an observable failure. Repair concrete defects and run focused regressions, using Q: where possible. | A short findings table: code location, failure mechanism or hypothesis, supporting evidence, fix/check and unresolved gap. Separate confirmed defects from possible causes of the historical BSOD. Avoid a whole-driver rewrite or an unbounded audit. |
| 3 / T024-T027, T030-T031, T054 | Complete the exact prerequisites for one bounded active-C: reproduction: safe handling of reachable paging paths, recovery, conservative test budget, and truthful error/persistence checks. | Run the guarded candidate first so failures retain strong evidence. This is sequencing, not product policy: after it passes, remove the ordinary boot/system/paging activation blocks under T070-T074. |
| 4 / T067, T029, T033-T035 | Run a bounded large-BMP baseline with caching off, then the same recorded workflow with active C: caching once phase 3 permits it. Use roughly the reported 350 MB image where guest headroom permits; record the actual dimensions/bytes. Exercise create/open in Paint, edit/save, then open in Photos promptly after save; also compare opening after an explicit completed drain. Record operations/timestamps, memory and dirty/in-flight/error state without synthetic faults on C:. | Separate immutable records for uncached and cached attempts, exact configuration and actual active routing. Preserve the original image/expected data separately; use deterministic byte checks for the maintained file scenario and a recorded application workflow for Paint/Photos. An application save need not preserve the original file hash. Do not claim a cached attempt from an uncached pass. |
| 5 / T067, T035-T037 | On a crash, preserve the dump, symbols, logs and latest test state before reinstall/rollback; analyze the stop code/stack and owning lifetime/order path. On a byte mismatch or hang, preserve equivalent evidence and investigate that failure. Fix the responsible code and repeat the triggering case plus affected regressions. | Root-cause evidence and fix/regression when reproduced. If not reproduced, report the exact attempted conditions and remaining hypothesis; choose the next bounded discriminating test. Successful attempts do not close the historical incident or establish production readiness. |

The original crash and damage after reboot are separate questions. Loss of
pending Fast writes could explain later damage, but cannot establish why the
initial BSOD occurred. Missing historical evidence may prevent proving that a
newly found defect caused that exact old incident; record that distinction.
Start with the current identified candidate. Testing an older build is a separate
comparison decision based on recovered identity and recovery readiness.

Snapshot and hypervisor access are already authorized/reported in this session.
Use those facts without repeatedly requesting the same confirmation. Actual
destructive rollback, driver removal or crash injection still follows existing
authorization and evidence-preservation rules. Unknown historical root cause
blocks a readiness claim, not a controlled investigation designed to find it.

Limit new verification code to a named capture, reproduction, error-reporting or
regression need from these phases. Reuse `qcache developer verify`; do not add a
parallel runner. Do not expand or repeat the completed uncached 64 MiB baseline
unless a relevant change warrants it. The large-BMP baseline is different and
has not been done. Defer generic A08 polish, T050 tuning, broad benchmarks,
unrelated cleanup and full production qualification. Preserve required targeted
tests and the existing baseline requirement if a fix affects performance.

At each implementation handoff, report what moved the crash investigation
forward, confirmed defects versus hypotheses, the remaining activation blockers,
and the next concrete test. Include the A01-A16 table with this run's changes
and plain-language application benefits. Test-framework repairs must be labelled
as such; do not credit them as driver fixes or BSOD resolution.

### Current T067 execution checkpoint: 2026-09-23

Phases 1-3 have produced a concrete blocker rather than an active-C: pass.
The 8 GiB VM has no surviving historical dump or BugCheck event. Ordinary Q:
pagefile configurations at 10 GiB, 8 GiB and 4 GiB all caused Windows to create
a temporary 7.75 GiB pagefile on C: instead. A dedicated dump file on Q: registered
there, but also left two combined usage registrations on C:. Removing all
pagefiles, disabling crash dumping, and confirming that firmware makes hibernation
and Fast Startup unavailable still left C: at combined count 2. Do not bypass it.

The next driver/controller candidate extends diagnostics without weakening the
gate: it reports paging, hibernation and dump registrations separately while the
legacy combined count remains authoritative. Install/reboot that candidate, read
the three C:/Q: values, and then decide the smallest support or enforced-prerequisite
change. This is now the next T023/T025/T068 exit check.

Plan 22 is the current reproduction contract. Plan 21 superseded the first active reproduction contract before it ran on the
VM. `system-active-image` exclusively leases C:, captures a disabled/released
baseline, permits only an internal runtime 256..512 MiB Fast configuration, writes
a deterministic 349 MiB 32-bit BMP under its unique owned directory, commits its
oracle to the other disk first, proves that the active cache accepted at least the
complete image, and verifies every byte at separate application-flush,
administrative-flush and post-release boundaries. Because Windows can write to C:
immediately after a flush returns, the boundary requires a returned flush plus
unchanged routed, error-free state rather than a perpetual global zero-dirty
snapshot. A passed case cannot restore successfully without the final oracle/image
evidence, and manual recovery uses the same exclusive system-disk lease and
restoration operation. The separate `system-image-baseline` runs the identical
image I/O with caching disabled and pass-through; plan 22 permits that control case
to record existing system usage paths without treating them as an active-cache
hazard. Plan 26's active case accepts reconciled paging-only registrations while
still rejecting hibernation/dump paths for the first bounded run. Public Apply
currently rejects boot/system disks, but T070-T074 remove that temporary split
after the guarded result. This advances
the controlled experiment; it is not a QueueCache crash fix and cannot resolve
T067 without VM evidence.

Exact VM evidence now exists for the split plan-22 CLI on installed 0.4.75.1.
The disabled/pass-through 349 MiB baseline run
`QueueCache-Verify-20260923-140039-8c9ee24872d14b6b82639e5590c9bbb7`
completed 1/1 with full-byte oracle agreement and unchanged clean C: state. The
paired active request
`QueueCache-Verify-20260923-140550-e8e582d46f5f46658a039d4ce29e0ed9`
was rejected before capture or mutation because the kernel still reports two
paging-type usage paths. The next implementation focus is therefore T024/T053
paging-path ownership, nonpageable progress and overlap ordering—not weakening
the runner or repeating the same registry experiment.

Plan 23 was the diagnostic driver candidate for that focus. Diagnostics V4 preserves the
V3 current counts and adds, per notification type, in/out requests, successful
and failed completions, and the last requesting PID. Recording covers direct
completion, queued worker completion, cancellation and admission rejection. It
does not enable caching on a paging path or claim the PID names a file; its exit
check is an exact installed/rebooted lifecycle snapshot that explains whether
`Paging=2` represents two accepted outstanding paths or a driver imbalance.

That exit check completed on exact installed/rebooted 0.4.78.1. C: reported two
paging in requests, two successes, no failures or removals, and last requester
`smss.exe` (PID 556 on that boot). The counter is balanced: Windows has two
accepted outstanding path registrations even though no pagefile, swapfile or
hiberfile is visible. Review then found that the filter did not yet veto
query-stop/query-remove or expose `PNP_DEVICE_NOT_DISABLEABLE` while such a path
was active. Plan 24 implements those PnP requirements and makes lifecycle
reconciliation mandatory in guarded system verification. After its install/boot
proof, the remaining A07 work is the actual paging-I/O forward-progress and
dirty-overlap ordering design—not another attempt to erase or bypass the count.

That plan-24 proof passed on exact installed/rebooted 0.4.79.1. Windows exposes
the C: disk as started and healthy but not disableable, and the two registrations
still reconcile. Plan 25 adds Diagnostics V5 paging read/write request and byte
counters at dispatch, including disabled pass-through. The supported 349 MiB
baseline now records before/after snapshots and a process-wide delta. The delta
may include unrelated Windows traffic and is evidence for the next design, not
permission to enable caching.

Plan 25 then completed on exact installed 0.4.80.1. Run
`QueueCache-Verify-20260923-172845-86c7a49e6f1445178f9ca455390a8ae9`
passed all 365,953,024 bytes with C: disabled and observed 983 paging reads plus
427 paging writes in 13.5 seconds. Plan 26 therefore rejects a barrier-per-paging-
request design and instead adds reserved paging-write admission, high-priority
MDL mapping, and safe paging-read miss service while an ordinary write is blocked.
Only the guarded verifier can request this experimental paging enablement; it
requires paging-only registrations, Fast mode and a 256..512 MiB budget. Normal
Enable/public Apply stay restricted in plan 26. Exact 0.4.82.1 then passed that
experiment. Plan 27 removes those restrictions, accepts all reconciled system-path
types, uses public Apply, and adds separate Fast/Strict cases.

Memory pressure remains a plausible historical contributor because the old
reported setup may have used a 4 GiB cache on an 8 GiB guest while Paint/Photos
decoded a large image. It is not proven. Management now preserves the greater of
2 GiB or 25% of physical RAM for Windows/applications, and the active experiment
uses at most 512 MiB. No performance-affecting driver policy changed.

## 2. Product requirements and decisions

| ID | Requirement / decision | Implementation interpretation |
|---|---|---|
| REQ-01 | Private alpha for owner, colleagues and friends on recoverable VMs. | No valuable sole-copy data; document tested environments and gaps. Public signing/certification and a broad hardware matrix are later milestones. |
| REQ-02 | Fast is the product default, including C:. | Preserve explicit volatility acknowledgement and Strict as a choice. Do not substitute a Strict-only or secondary-disk-only final alpha. Driver initialization can remain disabled/safe until an explicitly saved profile is applied. |
| REQ-03 | Abrupt-loss volatility is accepted. | A power loss/crash can lose acknowledged RAM data and damage the filesystem. Do not promise persistence for Fast application flushes. This does not permit silent normal-operation corruption, stale reads, lost dirty data after an I/O error, or false success from an explicit administrative flush. |
| REQ-04 | Seconds-scale background draining, not a default one-hour hold. | Implemented A03 baseline: Idle, 5,000 ms dirty-age trigger, 250 ms idle trigger, 40/80 watermarks, 256 KiB batches, parallelism 1. Foreground/background checks passed on 0.4.57.1. Saved choices remain preserved; a trigger is not a persistence deadline. |
| REQ-05 | Foreground fitting RAM writes and cached reads take priority. | Normal draining runs concurrently with minimal interference; reduce background aggressiveness only where evidence justifies it. Preserve capacity backpressure, bounded drain progress, ordering and explicit durability/lifecycle boundaries. Disk-miss reads are not promised RAM latency. |
| REQ-06 | Resolve/explain long drain and cleanup failures. | Distinguish slow progress, stalled lower I/O, continual admissions and driver overhead with counters. No blind retries, larger deadlines merely to pass, or weakened final checks. |
| REQ-07 | One installer, one current driver, one `qcache.exe`. | Developer diagnostics/scenarios live under `qcache developer` and the shared developer library. No separate lab edition, test executable, private orchestration script or parallel driver service. |
| REQ-08 | Remove obsolete implementation and present-day references. | Delete legacy source/build/tool paths from the maintained tree after dependency checks. No in-tree historical archive. Do not rewrite Git history or delete remote branches as incidental cleanup. |
| REQ-09 | Review PowerShell individually. | Consolidate duplicated runtime scenarios into the CLI. Keep appropriate build/signing/setup/updater/offline recovery scripts; do not force recovery to depend on a functioning driver or installed CLI. |
| REQ-10 | Focused alpha quality checks, not exhaustive certification. | Reuse passed evidence for unchanged paths. Add bounded deterministic tests for changed paths and essential C:-specific behavior. Stop on known normal-operation corruption, unsafe ordering, boot failure or unresolved recovery failure. |
| REQ-11 | TRIM work is postponed. | Keep conservative handling. Record the existing unsupported probe honestly; do not require range-aware TRIM optimization or claim it passed. |
| REQ-12 | Current docs, understandable diagnostics and safe servicing. | Explain Fast risks, pending persistence, long operations, setup failure and recovery without historical-engine narratives. Fix/test partially updated installations. |
| REQ-13 | Faithful measurements and reporting. | Immutable run IDs, exact loaded identity and tool hashes, ready observers, raw evidence, separate timing modes, and no comparison to outside screenshots. Include all 15 optimization rows after completing a step. |
| REQ-14 | Preserve current implementation materials and legal obligations. | Keep the tracker, needed design/evidence notes, unrelated research, private raw results and applicable attribution. Remove obsolete legal references only after retained-source provenance review. |
| REQ-15 | Production readiness extends beyond the private alpha. | Define supported environments, close safety gaps, validate security/servicing, qualify a frozen candidate and stage release with recovery/support. No production claim from a short VM test or test-signed alpha package. |

Optional one-hour Deferred behavior is not the alpha default. Its real one-hour
soak need not block the alpha if explicitly outside the alpha support claim;
either hide/label that optional mode as unqualified or qualify it separately.
Do not delete it incidentally or pretend its soak has passed.

## 3. Evidence checkpoint, not assumptions

### Current status, 2026-09-23

TRUE means this step's stated scope is complete; PARTIAL means some deliverables
exist but its exit gate is open; FALSE means the step is not delivered. It does not
mean the entire driver is production-qualified. Source/verification details and
immutable run IDs are in the tracker. New planning tasks below are all pending.

| Step | Status | What changed or exists | Evidence and remaining boundary | Benefit |
|---|---|---|---|---|
| A01 | TRUE | Preserved work and established the starting checkpoint. | Host checks and exact prior failures/recoveries recorded. | Future decisions use identifiable code and trustworthy evidence. |
| A02 | TRUE, scoped | Fixed runner restoration ordering; added V3 drain timing. | Q1/Q32 cleanup regressions completed on 0.4.51.1. About 99.4% of one observed drain was in lower-I/O wait. Throughput cause within that path is still open in T050. | Reliable cleanup and an actionable location for performance investigation. |
| A03 | TRUE, scoped | Coherent Fast/Idle defaults in native/managed/CLI/UI; saved choices preserved. | 0.4.57.1 policy run passed 30 checks, including 60 seconds of writes/reads during drain. Plan-14 capacity and timer-boundary qualification passed on 0.4.64.1 through T051/T069. | Useful default behavior with measured foreground responsiveness. |
| A04 | TRUE | Removed obsolete engine/build/tools; one current driver. | Native builds, CI and installed-build quick/policy checks passed. Compatibility service/schema names remain deliberately. | Fixes and tests apply to one shipped implementation. |
| A05 | TRUE, scoped | One developer CLI; duplicate wrappers removed; independent recovery script retained. | Host packaging checks passed; actual offline recovery rehearsal remains T054/A10. | Repeatable tests and a recovery route that can be tested without a working CLI. |
| A06 | TRUE, scoped | Secondary-disk byte and lower-write/lower-flush recovery checks passed. | 512-byte-sector Q:, 0.4.57.1. One incomplete admission-precondition run preserved. Allocation/cancel/capacity/deterministic race gaps remain. | Confidence in exercised data paths before expanding exposure. |
| A06a | PARTIAL | T049 ledger, plan-14 pressure proof and plan-16 T050 `drain-decision` contract implemented; plan-17 `policies` adds observed overlap; T051/T069 are complete. Recovery prevalidation is strengthened. | Exact installed 0.4.64.1 pressure, 0.4.67.1 drain and 0.4.69.1 overlap policy checks passed. Copied-hive recovery dry run passed; T050 tuning, controlled T052-T053 and actual T054 recovery remain. | Prevents known gaps and performance questions from disappearing behind completed labels. |
| A07 | PARTIAL | Normal Enable/public Apply accept system paths. **Changed this run:** after the exact 0.4.83.1 pagefile failure, paging data stays ordered but bypasses RAM caching and new usage registration takes one persistence boundary without disabling routing. | Exact 0.4.83.1 normal Fast passed; the correction needs packaged pagefile/restart proof. Hibernation/Fast Startup are unavailable on this VM. | Makes C: use the product path without using scarce RAM to cache swapped-out RAM. |
| A08 | PARTIAL | Guarded byte/restart and 349 MiB contracts exist. **Changed this run:** Plan 28 gives Fast and Strict separate immutable image/oracle paths. | 0.4.83.1 Fast passed with 368,731,648 accepted bytes; Strict was safely stopped by the shared-path guard before the fix. Fixed two-case execution remains. | Proves normal activation without allowing one case to reuse another's evidence. |
| A09 | PARTIAL | Public UI/CLI and identity-bound saved startup accept C:. **Changed this run:** saved Fast plus a 4 GiB C: pagefile reproduced process corruption after reboot; evidence was preserved and the VM restored stable. | Paging-bypass retest, saved-profile bytes and available power transitions remain. Dump configuration alone did not produce a dump registration. | Turns a vague historical concern into a concrete pagefile regression and fix gate. |
| A10 | PARTIAL | Cleanup, packaging and recovery foundations exist; recovery now prevalidates all disk keys before mutation and identifies `-WhatIf` as a dry run. | Installed 0.4.70.1 script changed a disposable copied SYSTEM hive as expected; real offline/Safe Mode boot recovery, install/upgrade failure/uninstall matrix and final docs remain. | Installation and maintenance failures have a tested way out. |
| A11 | PARTIAL | Runner and historical measurements exist. | Final-candidate comparisons, full 72-case collection and bounded smoke remain. | Establishes usable performance and catches longer-running defects. |
| A12 | FALSE | Private-alpha freeze and participant release pending. | Owner reviews candidate and limitations after preceding gates. | Controlled real-user feedback with an identifiable recoverable build. |
| A13 | FALSE | Production support contract and safety coverage pending. | Planned scope/invariant/gap closure; no broad hardware claim. | Defines exactly where the product can safely be used. |
| A14 | FALSE | Security and production servicing qualification pending. | Management/update trust, distribution/signing route and recovery testing required. | Protects privileged driver access and dependable deployment. |
| A15 | FALSE | Production environment/endurance qualification pending. | Frozen candidate tested against A13 support matrix and longer workloads. | Tests reliability beyond a single short VM session. |
| A16 | FALSE | Production release and support gates pending. | Staged rollout, diagnostics, rollback and release decision required. | Makes failures diagnosable and releases supportable. |

Latest recorded installed candidate is 0.4.83.1 from `05d18b0`, SYS SHA-256
`754C4894E7BD5688EF493AF923C5B84923A86EDCD7DD41636736B0833142AFB8`.
It proved the normal Fast active case, then failed the configured-pagefile saved-
startup exercise with repeatable process corruption. It is not a C: release
candidate. The paging-bypass correction is newer and not yet installed. See
the tracker for each earlier release's scoped evidence.
The recorded VM end state is C: disabled/released, no saved QueueCache profile,
and the test pagefile/dump changes reverted; Windows created its usual temporary
512 MiB pagefile. Recheck live identity/state before another workload.

### What A02 proves and how it changes the work

A02 separated late writes after a completed flush from slow progress inside a
flush. The restoration sequence now submits filesystem buffers, flushes the cache,
disables/drains remaining admissions, then reapplies the saved settings and checks
the result. This fixes the test/configuration boundary; it does not implement a
new concurrent cutoff-flush algorithm in the driver or eliminate future cleanup
failures from unrelated external writes.

The diagnostic drain persisted 658,882,560 bytes using 53,892 writes in 69.476
seconds. Lower-I/O time was 69.052 seconds, versus 0.028 selecting, 0.103 copying
and 0.041 retiring. This identifies the measured wait, not its ultimate cause.
The lower path includes the Windows/virtual-storage stack, queueing and storage;
request size, address pattern and driver-selected concurrency can affect it.
There is no matched uncached drain proof that the physical disk is the unavoidable
limit, and no measured drain speedup attributable to the diagnostic counters.

T050 therefore brings the optimization decision forward: compare the relevant
request sizes, address distribution, persistence semantics and existing parallelism
before deciding on batching or scheduling edits. Make an evidenced fix when needed,
or record the measured operational limit and its product consequence. A11 remains
the final acceptance run, not the first opportunity to fix an identified problem.
Any newly reproduced corruption, ordering, hang or recovery defect takes priority.
Revision 3 pauses further T050 tuning while the immediate T067 investigation
proceeds. Its completed measurements remain available and its final decision
remains an open alpha task.

### Historical starting checkpoint, 2026-09-20

The following table and sections 3.1-3.3 preserve the original starting evidence.
They are not current installed-state instructions. Later tracker entries supersede
their pending-work statements without changing original failure verdicts.

| Item | Recorded state at handover | What the next agent must not infer |
|---|---|---|
| Last recorded committed checkpoint | `76b93db`, after optimization source `948ea1c9c75236c4d59b6c1f2d757d5dd234b18f`. Recheck HEAD. | Do not assume the current worktree is clean or identical to that commit. |
| Installed test driver | 0.4.46.1, signed CI 35470024426. SHA-256 `90C50E7AD991E3EB0E9D7FFFC38C5B1CEA80935124B2CDEB6C2560980DD65060`. | A version string or installed package alone does not prove the loaded driver. |
| Last test target | Q:, non-OS NTFS secondary disk, 2 GiB cache, Fast/Idle, 512-byte logical sectors. C: caching was disabled. | Q: results do not qualify active C: caching or 4Kn. Rediscover disk identity rather than copying a disk number. |
| Last recorded VM condition | Separate recovery succeeded; original Q: settings restored, dirty/in-flight/errors zero, no workload processes remained. | Recheck live state before another run; this is not a permanent assertion. |
| Current source changes | Plan-10 restoration candidate in `VerificationWorker.cs`, plan version in `VerificationPlan.cs`, runner contracts, verification guide and tracker. | Candidate is not fully accepted or deployed through a new product installer. Do not overwrite it with a fresh rewrite. |
| Private owner material | Research copies of `docs/PERFORMANCE.md`, `docs/PERFORMANCE_PLAN.md` and older handoff/session notes are archived under ignored `.lab/private-handover-20260920/`, preserving relative paths. The two tracked research documents were restored to HEAD with owner approval after hash-verified archival. | Preserve the archive; do not stage, publish or delete it as cleanup. The five unfinished restoration source/test/doc files remain in the worktree. |
| Current host checks | Management console tests, publish and touched-file diagnostics passed for the candidate. | Local success is not kernel/VM validation. |
| Current corruption evidence | No reproduced unfixed normal-operation corruption defect identified in the reviewed current-engine evidence. | Not an exhaustive audit or proof that defects do not exist. Historical findings are not current-engine findings. |

### 3.1 Measured performance checkpoint

Matched 0.4.45.1 to 0.4.46.1, same DiskSpd, three timing-off repeats:

| Metric | Before | After | Interpretation |
|---|---:|---:|---|
| Random 4 KiB Q1 median | 20,997.7 IOPS | 23,043.1 IOPS | +9.74%; about 86.01 to 94.38 decimal MB/s. |
| Random 4 KiB Q32 median | 27,305.2 IOPS | 74,532.77 IOPS | +172.96%; about 111.84 to 305.29 decimal MB/s. |
| Q1 median p99 | 0.077 ms | 0.067 ms | Tail improved in the focused comparison. |
| Q32 median p99 | 1.554 ms | 0.192 ms | Tail improved in the focused comparison. |

These are qualified focused gains. Original comparison runs had late-dirty
cleanup failures and separate successful recoveries. Timing-on Q32 was much
faster than timing-off for unresolved reasons; never combine those scores or
present the diagnostic score as release throughput. Slot-reuse alone previously
showed Q1 -3.61% and Q32 +1.62%, inconclusive; do not credit it with the later win.
No external benchmark, product name or screenshot belongs in release claims.

### 3.2 What "restoration failure" means

| Term / observation | Plain explanation and technical consequence |
|---|---|
| Restoration | The test runner returns temporary cache configuration/timing to saved values, then checks state/profiles/errors and zero pending bytes. It is not file recovery or VM snapshot restoration. |
| Late dirty bytes | A write can resume after the explicit flush returns and before the next snapshot. Seeing 12 KiB dirty then does not prove the earlier flush violated durability. Boundary snapshots alone cannot place the admission inside the barrier. |
| Explicit control flush | The current foreground request worker orders it ahead of later writes; direct I/O admission is closed for control changes and older direct I/O is awaited. Normal background draining is different and can coexist with new admissions. |
| Continuous write hypothesis | Possible during background draining, but the observed failed Q1 main drain had unchanged accepted bytes while dirty bytes fell. Continuous refilling does not explain that observation. |
| Five-minute failure | The runner's 300-second restore-worker deadline expired. Q1 began with about 727 MiB dirty and ended observation near 2.7 MiB, with progress and zero reported errors. Approximate 2.3-2.4 MiB/s effective drain does not identify hardware versus driver cost. |
| Temporary admission freeze | Useful at explicit pause/configuration boundaries, not a default rule for normal background draining. A pre-drain must be bounded if foreground writes never stop. Never globally freeze Windows, reject acknowledged data or equate disabled caching with rejected disk writes. |

The current candidate sequence is filesystem-volume flush -> main cache flush ->
Disable/drain remaining admissions -> reapply saved configuration -> strict final
comparison. Disable clears clean residency. It does not preserve cache warmth,
and external writes after re-enable can still dirty the cache. Do not use the
secondary-disk zero-dirty cleanup predicate unchanged on a live OS disk.

### 3.3 Exact restoration evidence to inspect first

Private archive: `.lab/restore-plan10-20260920/evidence`; 735 files copied.
Four raw XML scores and 313 telemetry samples validated, maximum sample gap
0.2557627 seconds. Reuse this exact root, not unrelated historical runs.

| Probe | Exact run ID | Verdict |
|---|---|---|
| Volume flush then cache flush | `QueueCache-Verify-20260920-005026-2a28d7936a134c3cbc8770c97d8540c0` | Failed cleanup: 12,288 dirty bytes. |
| Volume flush then direct Disable | `QueueCache-Verify-20260920-005613-c6391ccd1cea46d4a4d87075a4777d76` | Restore timeout while still progressing; no new admissions after disable. |
| Final sequence Q32 | `QueueCache-Verify-20260920-010716-391acebd187e435a85335857178a9aaf` | COMPLETED; flush left 12,288 bytes, disable reached zero; restored settings clean in 100.9 seconds. |
| Final sequence Q1 | `QueueCache-Verify-20260920-012051-3eef2ddb4db84be8af7a4281a3f2fe08` | Restore timeout in main flush before Disable; accepted bytes unchanged, final dirty 2,818,048 bytes. |
| Separate recoveries | `QueueCache-Verify-20260920-005441-84a1088b19804a25acf14ece1df1c883`; `QueueCache-Verify-20260920-010536-10449276690d4c3fa9ea06cc0a2bbdb9`; `QueueCache-Verify-20260920-013257-a43ef34f73ec42a38f3b62132f548fe5` | RESTORED; these never relabel the original failed runs. |

Final probe CLI is under `.lab/restore-plan10-20260920/controller-final`.
Developer DLL SHA-256: `B3CD2C29944D010F8B956272605A474B9446B6B011365758A630778737E71544`.
All 212 published files matched the VM copy. Hash the DLL and package, not just
the unchanged apphost EXE when differentiating managed-only builds.

## 4. Owning code map

Paths below describe the current implementation. Update the map as files move.

| Responsibility | Concrete starting point | Important boundary |
|---|---|---|
| Active request dispatch, queue, direct admission, PnP/power | `driver/qcache/driver.cpp`: `RequestWorker`, `ServiceCachedReads`, usage notifications | Forwarding paging/hibernate/dump notifications while inactive is not proof of active caching safety. |
| Cache storage, reads/writes, drainer, barriers | `driver/qcache/writecache.cpp`: `Drainer`, `QcCacheBarrier`, `QcCacheProcess`; `writecache.h` | Preserve newest-version reads, in-flight ownership, sparse sectors and ordered completion. |
| Policy and native contracts | `driver/qcache/cachepolicy.h`, `cachepolicy-check.h`, `readselection-check.h`, `abi-check.cpp`, `qcstats.h` | Keep default policy/capability/ABI definitions coherent with management. |
| Configuration and target validation | `src/QueueCache.Operations/CacheConfiguration.cs`, `DiskTarget.cs` | `ConfigurationManager.Apply` is shared; `WaitForHealthyState` is not a zero-dirty predicate. Do not change it to mask test cleanup. |
| File integrity and volume flush | `src/QueueCache.Developer/FileTests/Runner.cs`: existing file modes and `CheckedVolume` | Existing advanced modes reject OS writes; some inject delay/faults. Do not simply remove guards. |
| Raw device scenarios | `src/QueueCache.Developer/WriteTests/Runner.cs` | Destructive fixed-region writes belong only on explicitly disposable non-OS RAW disks. Never C:. |
| Maintained orchestration | `src/QueueCache.Developer/Verification`: `VerificationRunner`, `VerificationWorker`, `VerificationPlan`, `OwnedProcess` | Reuse worker ownership, ready observers, trace records, deadlines and recovery. CLI only binds arguments. |
| Host-safe contracts | `tests/QueueCache.Management.Tests/VerificationRunnerTests.cs` and existing console harness | Custom `dotnet run` test harness, not an assumed `dotnet test` project. |
| Frontend and policy presentation | `src/QueueCache.Cli`, `src/QueueCache.Desktop`, `src/QueueCache.Management`; desktop tests | Both frontends use shared operations; don't shell from UI into a second app. |
| Product build | `build/Build.ps1`, `driver/qcache/QueueCache.Driver.vcxproj`, `.github/workflows/githubactionsbuilds.yml` | A04 leaves one current driver path; the no-switch build and CI select it. `qcachelab` remains only as the installed compatibility identity. |
| Setup and update | `packaging/QueueCache.iss`, `Install-Driver.ps1`, `Update-QueueCache.ps1`, build packaging tests | Class-filter installation; service/metadata migration requires compatibility, not search-and-replace. |

## 5. Ordered execution plan

Current status is summarized in section 3. A task is complete only after its listed evidence is
recorded, not after code compiles. Reuse neighboring helpers/tests; no new
standalone executables or duplicate orchestration. Before each edit state a local
hypothesis and cheapest discriminating check; run that check immediately after
the edit. Fix the touched slice before widening scope.

An investigation must finish with a concrete disposition: implement and verify a
fix, enforce a support restriction, or retain the behavior with measured evidence
and a stated acceptance rationale. Assign any residual gap to a task/release gate.
"We understand it" alone cannot close an exposed correctness defect or a failed
usability requirement. Avoid reopening completed research without new evidence.

| Step | Dependencies | Deliverable | Requirements | Exit / stop rule |
|---|---|---|---|---|
| A01 | None | Reproducible handover checkpoint | REQ-10,13,14 | Prior work/evidence identified; host tests pass or exact unrelated failures recorded. |
| A02 | A01 | Explain/fix drain and cleanup failure | REQ-05,06,10,13 | Focused Q1/Q32 cleanup regression passes, or a documented external limitation is explicitly accepted by owner. No silent timeout relaxation. |
| A03 | A02 | Fast and short-drain product defaults | REQ-02,03,04,05 | New-task defaults coherent; saved settings preserved; foreground correctness checked while draining. |
| A04 | A03 | Single current driver build, obsolete code removed | REQ-07,08,14 | Native Debug/Release and managed builds reference only current implementation; ABI maintained. |
| A05 | A04 | One developer CLI and reviewed scripts | REQ-07,09 | Unique useful checks retained, duplicate wrappers removed, setup/offline recovery retained. |
| A06 | A05 | Secondary-disk focused correctness checkpoint | REQ-03,05,10,11 | Required tests pass with byte oracles; any new normal-operation corruption stops rollout. |
| A06a | A06; informed by A07 design | Close critical coverage gaps and act on drain findings | REQ-03,05,06,10,12 | T049-T054 have explicit evidence/dispositions; required safety/recovery gates precede C: activation. |
| A07 | A06 | Explicit C:-disk implementation contract and safeguards | REQ-01,02,03,10 | Active system-disk path is accounted for in code; no guard-removal shortcut. |
| A08 | A07 | C:-safe verification workflow in existing runner | REQ-01,02,07,10,13 | Host tests prove OS/raw/fault separation and correct busy-volume result semantics before VM use. |
| A09 | A07, A08, A06a safety/recovery gates | Disposable-VM C: Fast validation | REQ-01,02,03,10 | Normal operation and normal restart byte checks pass; supported power/pagefile scope explicit. |
| A10 | A05 foundations; finish after A09 | Reliable single installer and current docs | REQ-01,07,08,09,12,14 | Rehearse recovery before A09 through T054; finish fresh install, upgrade-failure handling, upgrade/uninstall and final docs on the candidate. |
| A11 | A10 | Final-candidate performance and bounded endurance | REQ-04,05,10,13 | Complete selected matrices, no unexplained significant regression or integrity failure; no exhaustive certification claim. |
| A12 | A11 | Private alpha handoff and reporting loop | REQ-01,02,03,11,12,13 | Owner accepts evidence/limitations; exact candidate frozen and participant instructions ready. |
| A13 | Design starts during A07; closure uses A12 feedback | Production support and safety contract | REQ-03,05,10,11,15 | Every exposed supported path has proof or an enforced exclusion; normal-operation safety gaps cannot be waived as volatility. |
| A14 | A10 and A13 scope | Security and production servicing | REQ-07,09,12,15 | Privileged interfaces, update trust, signing/distribution and recovery qualified for the supported scope. |
| A15 | A13, A14; frozen candidate | Environment and endurance qualification | REQ-03,05,10,13,15 | Declared matrix and longer stress/lifecycle checks complete; failures resolved and affected checks rerun. |
| A16 | A12 feedback, A13-A15 | Production release and support | REQ-12,13,15 | Owner accepts release evidence, support procedure and staged rollout/rollback readiness. |

Planning revision 3 gives the immediate T067 sequence precedence over general
task-number order. Whole-step completion and production release gates remain;
only prerequisites relevant to safe controlled reproduction block that experiment.
Record any deferred subtask explicitly, with its original release gate intact.
Dependency order is not a requirement to serialize all research. Begin A07's code
mapping while designing A06a checks; use the resulting system-disk contract to
finish A08. Rehearse external recovery before the first A09 activation. A13 support
scope and A14 security design should inform implementation early. A11/A15 run on
stable candidates after relevant fixes, so expensive qualification is not repeatedly
invalidated by planned code changes. No additional agents are required by this plan.

### A01. Preserve and verify the starting point

| Task | Do this, in order | Check / evidence |
|---|---|---|
| T001 | Inspect status and scoped diffs. Read the uncommitted `FlushForRestoration` sequence and its contract tests. Identify which edits are ours versus owner research. | Record HEAD and touched files; do not revert anything or commit all files indiscriminately. |
| T002 | Run command H1 below. Read exact Q1/Q32 evidence from section 3.3, especially restore worker stderr, exit records, control trace, recovery result and boundary snapshots. | Preserve original verdicts and identify last completed stage. |
| T003 | Before remote work, use the existing secure connection method without reading/printing credentials; confirm elevated VM session, clean non-OS target, owned/competing processes absent, loaded immutable driver/hash and actual device stack. | Missing access is a blocker to VM work, not permission to run workloads on the development host. No secrets or personal VM addresses in public docs. |

### A02. Diagnose the long drain and finalize cleanup

| Task | Do this, in order | Check / evidence |
|---|---|---|
| T004 | Parse the exact failed Q1 control trace in time order using a JSON parser. Tabulate elapsed time, dirty/in-flight bytes, accepted/drained deltas, lower attempts/completions, batch bytes, errors, phase and oldest queued age. | Distinguish no-progress periods from slow completion. If accepted bytes are flat, do not attribute the delay to continuous refill. Lifetime counters are not score-window counters. |
| T005 | Trace `QcCacheBarrier` -> `Drainer` -> lower-I/O completion and slot retirement. Compare observed batches/throughput with matching idle/noncached drain evidence if available. | Name one supported cause hypothesis: slow lower completion, small batches, selection/retirement CPU, scheduler wait, or admission after return. Do not guess a lock rewrite. |
| T006 | If existing timing cannot discriminate, add narrowly scoped phase/batch/wait evidence to the current diagnostic contract and maintained runner. Keep timing-off/on separated; version changed measurement contracts. | Host ABI/parsing tests and native build before deployment. Missing fields remain missing, not zero. No always-on expensive profiling by accident. |
| T007 | Fix only the identified path, or document a measured external storage limit and ask the owner for an explicit operational policy if no code defect exists. Preserve real flush completion and failure semantics. | Never report success while dirty data required by the explicit boundary remains unpersisted. If a timeout policy changes by agreement, version/document it and preserve old failures; do not present that as a performance fix. |
| T008 | Keep or refine volume flush -> main flush -> Disable -> saved configuration based on evidence. Test failure propagation at every stage. Explain that Disable clears clean residency and re-enable permits new external writes. | H1 plus focused V1/V2 runs. Both reach completion with original settings/errors checked. If still failing, stop A02 and report exact blocker rather than repeat blindly. |

### A03. Set coherent alpha defaults and RAM-first behavior

| Task | Do this, in order | Check / evidence |
|---|---|---|
| T009 | Locate defaults in native policy, `CacheConfiguration`, CLI new-task binding and desktop editor. Use Fast for new tasks with explicit volatility consent. Preserve Strict and previously saved profiles. | Host/UI tests compare new defaults and saved-profile round trips. No automatic cache enablement merely because installer ran. |
| T010 | Implement/confirm the proposed Idle/5-second baseline in section 2, documenting any evidence-based adjustment. Keep cold startup disabled until a valid opted-in profile is applied. | Verify dirty-age and idle triggers, explicit flush overrides and capacity limits. No one-hour default; age is a start trigger, not completion SLA. |
| T011 | Use existing parallelism/retention cases and one sustained fitting-write + cached-read case while draining. Only change drain scheduling if measured foreground stalls demand it. | Accepted RAM writes/cached reads remain correct; no incidental whole-cache drain. Record foreground latency, capacity waits and nonzero background progress. Below capacity, background activity alone must not make admission depend on lower I/O. |

### A04. Remove obsolete source/build paths safely

| Task | Do this, in order | Check / evidence |
|---|---|---|
| T012 | Follow `docs/REPOSITORY_CLEANUP.md`: remove legacy build branch and its `ioctl.cpp`, `mainwdm.cpp`, `partialirp.cpp`, `queue.cpp`, `read.cpp`, `thread.cpp`, `write.cpp` project items/source after reference checks. Preserve shared `qcstats.h` and actual dependencies. | Current cache is the no-switch default. Debug/Release native and ABI/static checks pass. No in-tree legacy archive. |
| T013 | Remove `legacy/qcachecmd`, `legacy/scsichk`, `legacy/scsilog`, their solution entries and obsolete source-control bindings. Check retained headers/resources and licensing provenance. | Solutions load; no retained build references deleted paths; legal notices match retained source. No Git history rewrite/remote branch deletion. |
| T014 | Simplify active compile variants and give active dispatch/source/build flags product-oriented names. Preserve inactive forwarding, serialized ordering and diagnostic hooks. | Separate behavior-preserving rename from functional changes. Build/tests and a clean VM policy regression before calling it equivalent. |
| T015 | Keep existing service/binary identity for compatibility unless a coordinated migration is implemented and tested in A10. Remove product-facing "separate lab edition" wording. | One loaded driver/service path, not two simultaneous filters. Schema/metadata consumers agree even if an internal compatibility key remains temporarily. |

### A05. Consolidate developer tooling and scripts

| Task | Do this, in order | Check / evidence |
|---|---|---|
| T016 | Inventory each mode in `Measure-Performance.ps1`, `Test-CacheFaults.ps1`, `Test-LabReads.ps1` against existing developer commands/suites. Create a source-to-replacement table before deleting wrappers. | Every useful unique scenario is retained or explicitly deferred with reason; no silently lost fault/byte oracle. |
| T017 | Move missing reusable scenarios into `src/QueueCache.Developer`, expose through existing CLI bindings, add contracts to management tests, then remove duplicate wrappers. | One executable/worker model; no private script runner, extra installer or duplicated runtime. |
| T018 | Retire `Manage-Lab.ps1` only after current class-filter setup/offline recovery covers required actions. Keep build/signing/update/setup scripts when they have a distinct lifecycle purpose. Remove generated-only old test directories locally if no needed evidence is there. | Packaging/reference checks; offline recovery can work when guest CLI/driver is unusable. Do not delete `.lab` evidence or unknown owner files. |

### A06. Focused secondary-disk integrity and failure checks

The tests below are separate from a benchmark. High Q32 IOPS is not a byte oracle.
Run fault/delay scenarios on the disposable secondary disk only; never carry a
fault selector or diagnostic delay into a C: experiment.

| Task | Do this, in order | Pass condition / stop rule |
|---|---|---|
| T019 | Run maintained `quick` and `policies` suites (V3/V4). Inspect all worker checks, including sector offsets, crossing/full writes, newest-overwrite reads, retained reads and disabled-cache disk comparison. | All mandatory byte checks pass, no unexpected errors. Existing 512-byte sector scenarios must not be labelled 4Kn-qualified. TRIM unsupported result is recorded separately. |
| T020 | For modified admission/drain paths, reuse sector/overwrite helpers: deterministic seed and independent expected bytes; write disjoint and overlapping ranges, read newest data in RAM, explicitly drain, disable or drop clean appropriately, reopen/read and compare again. Repeat existing parallelism 1/2/4 and retention off/on cases. | Exact bytes match at both stages; record whether an in-flight old version was actually observed. Do not call a timing-dependent race deterministic. |
| T021 | Inventory existing fault/flush/coalescing modes before invoking them. On non-OS disposable storage, test at least lower-write failure and explicit lower-flush failure for touched paths, using existing hooks. | Error visible, dirty data retained, no false success. Explicit authorized recovery/retry followed by independent disk-byte verification. If recovery fails, stop; no reboot/discard as cleanup. |
| T022 | If changed code touches pins, allocation or cancellation, add bounded deterministic host/native checks or maintained scenarios for those exact paths. Do not expand into every theoretical race. | No double completion, use-after-free, leaked lifetime or overwritten in-flight data in the exercised paths. Unexercised risks remain listed. |

### A06a. Turn the evidence into bounded fixes and pre-C: gates

These are new tasks, not retroactive claims that A06 ran them. Preserve T001-T048
and existing verdicts. A06 proved its scoped secondary-disk checkpoint; completing
that checkpoint alone is insufficient evidence for the remaining safety properties.
Use maintained scenarios and host/native checks at the layer that can control the
ordering. A host mock is not evidence that the kernel honored the same ordering.

| Task | Work and reason | Exit evidence and position in sequence |
|---|---|---|
| T049 | Build and maintain [the coverage ledger](A06A_COVERAGE_LEDGER.md) for admission, sparse writes, old/new versions, Strict/explicit flush, capacity, allocation, cancellation, timers and lifecycle. Map each to current source, test, exact evidence and the release it blocks. Inspect the policy precondition failure; its filesystem source remains unproven. | No gap disappears because a suite has a top-level PASS. Before the next VM suite, record the failed boundary state/counters and check supported preparation. Any bounded preparation belongs before the observation window, preserves attempts and fail-fast fault behavior, and cannot silently discard failed attempts or retry until green. |
| T050 | Decide whether drain behavior needs a change using A02's lower-I/O evidence. Define acceptable drain completion/progress and foreground tail behavior for the alpha workload before comparing. On Q:, compare existing parallelism 1/2/4 with the same seeded dirty distribution, bytes, budget, batch limit and durability; include fitting traffic and cold reads. Use a matched uncached file-only control where meaningful; start with three repetitions per selected condition and keep timing-off scores separate from attribution. | A bounded decision before A09: an evidenced small fix plus regressions, or a documented measured limit acceptable for the declared alpha workload. Report drain time, request sizes, attempted/completed lower I/O, foreground tails, CPU and pending bytes. Unmatched control semantics cannot prove a hardware limit. Extend investigation only for an unresolved discriminating question; do not rewrite scheduling speculatively. Final acceptance is T042-T044. |
| T051 | Verify capacity/backpressure and actual trigger boundaries. Use a bounded file workload larger than a deliberately small cache budget, Fixed 0/50/100% and Automatic where supported, repeated hot writes, and independent bytes. Check first-dirty age, idle and watermark eligibility through native contracts and observed VM behavior. | Before C: activation, prove bounded reservations, eventual progress, no overwritten acknowledged data and correct bytes after drain. Capacity waits are expected only in the pressure case. A 60-second fitting hot-set test cannot substitute for capacity or exact trigger checks. Record timer tolerance and distinguish eligibility from completion. |
| T052 | Add deterministic tests for the critical ownership and persistence boundaries: old version draining while new data replaces it, failure in a later sparse segment, retained newest reads and retry; explicit/Strict flush completion ordering against queued later writes. | Before C: activation, show the intended interleaving, visible failure, retained data, successful authorized retry and independent disk bytes. Reuse existing delay/fault hooks where they suffice; any new hook must be bounded, target-guarded and integrated with the maintained runner. Timing-dependent in-flight observations alone do not close this task. |
| T053 | Use the A07 map to select bounded allocation/pin/cancellation tests for reachable paths under memory pressure or teardown. Inventory existing faults 6/7 and cancellation behavior before adding anything. Repair findings in the owning code. | Before C: activation, close paths necessary for paging/progress/teardown, or enforce a supported restriction that actually prevents them. Check single completion, lifetime/reclamation, retained dirty ownership and finite recovery. Remaining production gaps go to T056 explicitly; no blanket exemption merely because the code was unchanged. |
| T054 | Bring the essential part of T039 forward: validate registration backup and independently rehearse offline/Safe Mode recovery in the disposable environment, with accessible console and restorable snapshot. | PARTIAL: a filter-free historical backup passed both copied-SYSTEM-hive dry-run and actual copied-hive edit checks on 2026-09-22; a missing disk-key backup was rejected before restore actions. The newest backup still contains the filter. The owner confirmed console/snapshot access, but a real offline/Safe Mode boot recovery and snapshot-restore proof remain before A09 activation. Full servicing qualification still belongs to A10/A14. |

T050 initially uses existing tuning controls. Add missing collection to the supported
runner, version its measurement contract and preserve existing raw results. Before
a performance-affecting driver edit, obtain the complete baseline required by the
tracker; use focused matched checks during iteration. Do not run the full matrix
merely for this documentation revision or for a read-only design audit. A confirmed
safety defect preempts performance comparison and must be fixed first.

### A07. Engineer active C:-disk support

Review follow-ups T067-T069 below are release gates alongside the existing tasks.
Their numbers extend the task list without renumbering earlier work.

| Task / owner | Work and reason | Exit evidence / status |
|---|---|---|
| T067 / A07, A06a; validation in A08/A09 | Investigate the reported pre-A01 C: incident: cache enabled, Steam game installed, roughly 350 MB BMP edited/saved in Paint, then possibly opened in Photos while draining; memory-related BSOD, followed by QueueCache and Photos failing to launch. Reinstall restored QueueCache; snapshot rollback restored the VM. Exact version, mode, budget, stop code and drain state are unknown. Treat these as observations, not a diagnosed cause. | OPEN. Recover exact build/source/hash, VM RAM/cache budget, policy, pagefile/dump settings and any surviving dump/Event Log before reproduction. Snapshot rollback may have removed evidence; record that honestly. Separate the initial crash from post-crash lost-write damage. Inspect paging/mapped-file reads, MDL/pin/slot lifetime, resource pressure and concurrent drain/overwrite. Reproduce only in the recoverable VM after the existing recovery prerequisites, first using maintained file/pressure scenarios, then a bounded large-image workflow with off-target evidence. Do not waive an unexplained normal-operation BSOD as Fast-mode volatility. Close with a root-cause fix/regression, or an explicit unresolved-incident release restriction; no active-C: readiness claim while unexplained. |
| T068 / A07, before relying on T025 restrictions | Repair and deterministically test usage-notification/Enable synchronization in `437ee42`. Dispatch read `Routing` under `QueueLock`, unlocked, then incremented the count, allowing concurrent Enable to observe zero. | PARTIAL. Dispatch now reserves an in-path count while holding the same routing lock that Enable holds through its count check and Enabled transition. Queued rejection/cancellation and lower failure roll the reservation back; successful out-path completion decrements it. A compile-time interleaving contract and native builds pass. Management separately rejects boot/system disks even at count zero. Exact installed-driver notification success/failure/cancellation evidence is still required; this is not a diagnosed cause of the older T067 incident. |
| T069 / A06a, completes T051 measurement contract | Tighten the original plan-12 `pressure` evidence before claiming exact trigger boundaries or bounded reservations. | COMPLETE in plan 14. The exact installed 0.4.64.1 run `QueueCache-Verify-20260922-130140-5a38d389f7694633bb31d489d7fa816b` passed Deferred, Idle, watermark, Automatic, Fixed50, Fixed100 and Fixed0 with independent persisted-byte checks. Telemetry readiness and control trace were present; restoration returned Q: to active 2 GiB Fast/Idle with zero dirty/in-flight bytes and errors. The earlier plan-13 run remains preserved as incomplete. |
| T070-T074 / A07-A09 | Converge the guarded paging-capable candidate into the normal product path: ordinary Enable, public Apply, desktop/CLI, saved startup, new usage registrations, hibernation/Fast Startup and dump behavior. | C: is selectable and behaves like any other disk without an unsupported warning. Fast retains its normal volatility acknowledgement; backend checks and lifecycle tests prove correctness. See the tracker for the per-task exit evidence. |

Do not infer C: support from class-filter attachment or the presence of a C:
card in the UI. The cache operates on a physical disk, including other partitions
and system roles on that disk. Do not assume that moving the pagefile alone makes
all OS-disk risks disappear.

| Task | Do this, in order | Pass condition / stop rule |
|---|---|---|
| T023 | Trace active dispatch for paging, hibernation, dump, shutdown, power, PnP, direct I/O and saved-profile startup. Maintain the concrete [system-disk operation map](SYSTEM_DISK_OPERATION_MAP.md), including whether each path is queued, cached, bypassed, drained or unsupported and its allocation/IRQL/lifetime constraints. | The current table is tied to owning code and records remaining gates. Paging/hibernation/dump notification counting and ordering are implemented; forwarding alone is no longer treated as support. |
| T024 | Implement the smallest safe active-system-disk policy. Establish ordered handling of special I/O, nonpageable resources/progress under memory pressure, and shutdown/normal restart persistence while lower storage remains available. | Fast ordinary writes on C: remain the product goal. A bypass for paging or another special request cannot ignore overlapping newer dirty data or create stale reads. No global off switch masquerading as C: support. |
| T025 | Explicitly support and test hibernation, Fast Startup and dump behavior. A permanent activation prerequisite or user warning is no longer an acceptable product outcome. | The Windows features coexist with active C: caching with defined ordering and truthful persistence/error behavior. |
| T026 | Make saved-profile activation validate disk identity, capabilities, memory budget and previous fault state; avoid guessing PhysicalDrive0. Verify suspend/resume and startup failure leave a usable, clearly reported state. | No saved setting silently activates a different disk; no automatic replay of a fault as a transient drain. Target disappearance/identity change fails clearly. |
| T027 | Run local native/managed checks and package the candidate through existing build/signing flow. Freeze exact SYS hash and source identity. | No installation on the developer host. VM access/recovery prerequisites must be confirmed before A09. |

T023 must also identify every remaining path where a supported fitting Fast write
or fully cached read waits on unrelated lower I/O. Record its trigger and whether
it is an allowed capacity/durability/lifecycle boundary. Repair any exposed contract
violation before the corresponding release; do not reclassify an implementation
limitation as a permitted exception. T051 is complete; use T052-T053 for the
remaining ordering/lifetime proofs.

### A08. Add a separately guarded C:-safe verification workflow

Plan-22 `system-preflight`, `system-files`, `system-post-restart`, the uncached
`system-image-baseline` and the guarded `system-active-image` case exist;
their C: baseline used caching disabled. Finish only the extensions needed for
the immediate T067 investigation first. New behavior must be implemented and
documented inside `qcache developer verify` and the same worker infrastructure.

| Task | Do this, in order | Pass condition / stop rule |
|---|---|---|
| T028 | Add a typed system-volume file-only scenario with explicit opt-in and recorded expected physical identity/size/volume. Require off-target output storage and a recoverable-VM acknowledgement. Create only a unique test directory and bounded files. | Current non-OS suites retain their guards. No raw writes, formatting, TRIM probes, synthetic faults, broad registry changes or automatic reboot in the C: scenario. |
| T029 | Reuse deterministic file creation/read/overwrite/oracle helpers without inheriting unsafe advanced modes. Store independently computed expected seed/range hashes off the target before the operation being tested. Support a separate read-only post-restart verification phase. | Byte oracle survives a guest restart; expected values are not reconstructed from potentially wrong returned data. Checks are limited to owned test files. |
| T030 | Define clean-state semantics for a continuously busy OS disk. Verify explicit persistence at a controlled boundary and recorded saved settings, not perpetual global DirtyBytes==0 after re-enable. Keep ordinary secondary-disk restoration checks unchanged. | Host tests model writes before/after the boundary, exact errors and configuration checks. A busy C: is not a manufactured cleanup failure or an excuse to ignore real persistence errors. |
| T031 | Test identity mismatch, omitted opt-in, OS target passed to raw/fault mode, output on same target, missing observer, timeout, child-process ownership and interrupted post-restart verification. | All unsafe/ambiguous requests fail before mutation. No unknown process termination or weakened ready/coverage checks. |

T029 now has plan-19 implementation and a completed split-CLI VM baseline:
64 MiB owned-file create/overwrite and independently recorded Q: oracle,
followed by read-only verification after a normal reboot. C: caching was off
throughout. The first attempt was incomplete because of a runner lease bug;
the corrected 1/1 create and 1/1 restart runs are separate complete evidence
in the tracker. T030 and active-C: validation remain open. Do not label this
baseline as proof of a dirty restart, an administrative persistence boundary,
or resolution of the reported BSOD.

Plan 22 adds the corrected baseline/active-image implementation described in the
checkpoint above. Host tests and the split-CLI disabled baseline pass. The active
request was rejected before mutation because the current combined usage-path
count remains nonzero. That rejection is a required safety result, not an
incomplete runner workaround. Exact packaged plan-22 proof remains open.

### A09. Validate C: Fast in a disposable VM

| Task | Do this, in order | Pass condition / stop rule |
|---|---|---|
| T032 | Confirm restorable whole-VM snapshot, accessible hypervisor console, external recovery instructions/registration backup and no valuable data. Record Windows, virtual controller, sector sizes, pagefile/hibernate/dump/BitLocker state and free memory. | Snapshot/recovery availability must be positively confirmed, not assumed. No speculative host/hypervisor changes. |
| T033 | Install the exact candidate with caching disabled; normal reboot only after approved clean-state preflight. Verify actual loaded hash/stack, system usability and inactive forwarding before cache activation. | Boot/install failure stops test. Do not force-reset or silently restore a snapshot; preserve logs and involve owner for destructive recovery. |
| T034 | Enable an explicitly acknowledged Fast profile on the verified disk backing C: using A07 safeguards. Use conservative RAM budget leaving Windows headroom; do not copy the 2 GiB Q: budget onto a smaller guest automatically. | Pagefile and ordinary filesystem activity work; status shows actual active routing, not merely a saved profile. |
| T035 | Run A08 bounded file scenarios: create/read, overwrite overlapping ranges, cached reads during draining and explicit administrative flush. Repeat while normal OS activity occurs; include a bounded memory-pressure exercise within available headroom. | Independent bytes match, no driver/storage errors or hangs, bounded resource use and continuing drain progress. Observe startup/task/UI behavior too. |
| T036 | Prepare owned test files and off-target oracle, close test handles, confirm no synthetic hook remains, then perform an owner-approved normal restart with pending data if the maintained scenario proves it exists. After boot, run the read-only verification and inspect startup profile behavior. | Exact bytes verified; preparation alone is not PASS. If no dirty bytes were observed, report a clean-restart test, not a dirty-restart test. |
| T037 | Exercise the declared supported sleep/resume and hibernation/Fast Startup behavior, plus configured paging/dump restrictions. Check normal shutdown and restart with the driver active. | Supported paths work; unsupported paths are visibly prevented as agreed. No forced power-cut survival claim. Crash/power-loss experiments are optional separate owner-authorized destructive tests, not a required persistence test for Fast. |

### A10. Finish servicing and current-product documentation

| Task | Do this, in order | Pass condition / stop rule |
|---|---|---|
| T038 | Reproduce installer application-file lock/failure in a disposable VM; inspect `QueueCache.iss` and setup/update orchestration. Define preflight/staging/rollback behavior so mixed frontend/driver files are not presented as successful. | No killing user applications automatically. Failed update reports exact installed/loaded identities and actionable recovery. If rollback is claimed, verify all affected files. |
| T039 | Verify one fresh install, existing-alpha upgrade, failure handling, normal-reboot activation, uninstall with safe pending-data handling and offline recovery instructions. Coordinate service/metadata renames here if needed. | No duplicate driver/filter registration; unrelated filters preserved; no automatic discard of dirty data. Installer still installs one current product with developer commands included. |
| T040 | Rewrite README, current known issues and policy guide; align developer guide/CLI help/UI terms. Remove historical code references and stale contradictory instructions, but preserve functional upgrade cleanup and legal notices. | Fast default/C: alpha scope/volatility clear; known defects separate from risks/deferred work; actual commands tested. No claim that current code inherits every historical-engine bug. |
| T041 | Keep implementation docs listed in section 9; reconcile rather than overwrite owner research. Update packaging's explicitly copied doc list and tests when docs move/disappear. | Package links/content valid; no private run logs, credentials, session transcripts or generated research accidentally shipped. |

### A11. Final-candidate performance and bounded endurance

| Task | Do this, in order | Pass condition / stop rule |
|---|---|---|
| T042 | On the secondary disk, first run matched V1/V2 three-repeat Q1/Q32 checks if driver behavior changed. Freeze CLI/DiskSpd hash and settings across comparisons. Do not call unmatched historical runs a before/after pair. | Investigate repeatable >5% throughput or >10% tail regression beyond VM spread; owner disposition required, not an automatic correctness verdict. |
| T043 | At the final milestone, run the complete 72-case `write-performance` suite once on a frozen candidate using the existing three-repeat contract; not after each documentation change. Run focused flush-interference after scheduling/barrier changes. | All required collection and cleanup completes. `MEASURED` is not automatically performance acceptance; absent cases/samples remain incomplete. No need for the huge `full` suite by default. |
| T044 | Run a proposed bounded 30-minute mixed read/write alpha smoke on the secondary disk and a bounded C:-safe session through A08, with known-file verification after drain/reopen. Add duration support to the maintained typed scenario if needed, not a private loop script. | No byte mismatches, new errors, unbounded memory growth or stopped drain under capacity pressure. Record exact duration; this is an alpha smoke test, not endurance certification. |
| T045 | Measure C: performance only with the new safe file-only workflow, never by removing the existing OS guard from the old suite. Compare cache off/on on the same VM with same files/settings/tool; note OS background activity. | No guaranteed numerical C: gain inferred from Q: or a screenshot. Report sustained/drain behavior and latency as well as RAM write-return speed. |

### A12. Freeze and distribute the private alpha

| Task | Do this, in order | Pass condition / stop rule |
|---|---|---|
| T046 | Produce final decision table: verified features, known defects, accepted risks, deferred checks, exact candidate hash/source and tested VM configurations. | No known unresolved normal-operation corruption or boot/recovery failure. Long-drain behavior and install failure handling have explicit resolution/disposition. |
| T047 | Package one installer, current instructions and support checklist. Explain Fast volatility, VM snapshots, setup prerequisites, C: limitations, disable/drain behavior, and collection of status/logs without secrets. | Test signing/VM security prerequisites are explicit; no broad production-security certification claim. Never tell participants to force reboot a faulted dirty cache as routine recovery. |
| T048 | Owner approves private participant release. Record each report with build identity, workload, environment, reproduction and evidence. Prioritize reproducible corruption/boot failures over speed; add regressions to maintained tests. | Rollout can pause on a blocker. Finish with the full 15-row optimization overview and alpha task statuses, not only a throughput headline. |

### A13. Define and close the production support contract

Define scope early enough to shape A07 and A14; close qualification after alpha
feedback. The remaining 15 optimization ideas are not all mandatory rewrites.
Correctness and the advertised RAM-first contract are mandatory for exposed paths;
optional speed improvements remain evidence-driven.

| Task | Deliverable | Exit evidence |
|---|---|---|
| T055 | Specify supported Windows versions, architectures, controllers, filesystems, sector sizes, disk roles, encryption, multi-disk budgets, power modes and Fast/Strict guarantees. Specify unsupported combinations and their detection/enforcement. | A testable support matrix and explicit production acceptance criteria, including latency/drain usability and recovery expectations, agreed before final qualification. Scope may initially be narrow; no support inferred from filter attachment. |
| T056 | Close the T049 ledger for supported paths: bounded lower-I/O admission proof, capacity/large requests/quotas, sparse and versioned writes, pin/allocation/cancel lifetime, Strict/control ordering, startup/lifecycle and multi-disk accounting. | Each mandatory row has deterministic tests where needed plus appropriate driver evidence. Include fail/progress/cleanup invariants. Label unsupported 4Kn/TRIM probes honestly; implement range-aware TRIM only if required, but verify the existing handling on configurations that advertise TRIM support. |
| T057 | Resolve optional feature scope: Deferred/one-hour behavior, power/hibernate/dump modes and any remaining configuration features visible to users. | Qualify the claimed behavior or enforce/label its exclusion; unsupported safety-critical paths cannot remain reachable through CLI, UI or saved-profile restore. Preserve Fast volatility disclosure and explicit administrative persistence guarantees. |

### A14. Qualify security and production servicing

These tasks define work to perform, not claims about current Microsoft distribution
requirements. At execution, verify current official requirements for the chosen
Windows support/distribution route; test signing alone is not the production gate.

| Task | Deliverable | Exit evidence |
|---|---|---|
| T058 | Review privileged IOCTL access, request validation/overflow, memory handling, service/profile/registry ACLs and diagnostic fault/delay exposure. Make ordinary status access and administrative mutations obey an explicit privilege model. | Negative tests for unauthorized/malformed requests and fixes for findings; unsafe hooks cannot be armed by ordinary users or accidentally carried into normal use. Sensitive diagnostics are redacted. |
| T059 | Define and implement the applicable production signing/distribution route, artifact provenance, update authenticity and supported platform-security compatibility. | Verify applicable official requirements, complete required signing/qualification and test the actual installation route. Protect signing material; do not solve distribution by instructing general users to weaken system security. |
| T060 | Extend A10 to interrupted installs/updates, file locks, mixed versions, failed activation, upgrade/downgrade compatibility, uninstall with pending data and independent recovery. | A failure leaves a usable or explicitly recoverable installation with truthful installed/loaded identity. Verify actual rollback contents if rollback is claimed. No automatic dirty-data discard; validate recovery after realistic failures. |

### A15. Qualify a frozen production candidate

| Task | Deliverable | Exit evidence |
|---|---|---|
| T061 | Execute the supported A13 environment matrix, including physical hardware if claimed, storage faults, low memory, sustained capacity pressure and declared lifecycle operations. Select relevant native analysis/driver verification tools for the disposable test environments. | Exact candidate and environment identities, byte oracles, bounded resources/progress and resolved crashes/hangs. Deliberately disruptive tests require the appropriate disposable environment and owner coordination; never add them to ordinary C: file verification. |
| T062 | Extend maintained workload duration and distribution for longer endurance, concurrent workloads, repeated lifecycle/servicing and disk error/recovery tests. Set durations/repetition counts and acceptance thresholds before running. | Complete evidence and memory/error/progress trends across the agreed duration; a 30-minute alpha smoke cannot count as production endurance. Abrupt-loss experiments, if authorized, check declared failure/recovery semantics without promising Fast RAM persistence. |
| T063 | Freeze and run final performance/usability qualification with the same tools and settings, using T050 findings and supported workload mixes. | No unexplained significant regression; foreground responsiveness, sustained capacity behavior, drain/disable/restart times and UI progress are acceptable. Any fix invalidates affected evidence and triggers proportionate requalification. |

### A16. Release and support the product

| Task | Deliverable | Exit evidence |
|---|---|---|
| T064 | Produce a release evidence table, supported configurations, known limitations, version compatibility and user recovery instructions. Add bounded diagnostic/support collection with secrets excluded and truthful pending-data/error reporting. | A support case can identify the running driver, reproduce the issue and collect useful evidence without exposing private data or changing the storage state unexpectedly. |
| T065 | Exercise staged rollout, upgrade rollback/recovery and an incident procedure with stop criteria for corruption, boot failure, hangs and unexplained persistence failure. | Maintainers can halt distribution and guide tested recovery; collecting feedback is not a substitute for resolving a known safety defect. |
| T066 | Review A13-A15 acceptance, unresolved defects, release artifacts and ownership of support; obtain the owner's production release decision. | Exact signed artifact/source frozen and release scope published. A12 private-alpha approval does not imply approval for general production distribution. |

## 6. Command reference and preconditions

These commands describe the current tree. After A04 update renamed switches and
paths in this table and verify the replacement; do not leave obsolete examples.
Prefer repository tools; RTK was unavailable in the prior environment, so direct
commands were used. Check availability once rather than repeatedly failing it.

| ID | Where / command | Preconditions and interpretation |
|---|---|---|
| H1 | Host: `dotnet run --project tests/QueueCache.Management.Tests -c Release` | Host-safe console contracts; no driver or workload-disk access. |
| H2 | Host: `dotnet run --project tests/QueueCache.Desktop.Tests -c Release` | Required for frontend/default presentation changes. |
| H3 | Host: `dotnet publish src/QueueCache.Cli -c Release -r win-x64 --self-contained true -o <fresh-private-controller-directory>` | Unique output per candidate; verify all published file hashes after copy. Not a driver deployment. |
| H4 | Host: `MSBuild.exe driver/qcache/QueueCache.Driver.vcxproj /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal` | Locate installed MSBuild/WDK first; use current build pipeline for package/CI. Also validate Debug for native cleanup. Do not manually bump application versions; CI owns them. |
| H5 | Host: existing `build/Test-PackageLayout.ps1`, `Test-Packaging.ps1`, `Test-RegistryFilters.ps1`, `Test-Cli.ps1` | Read each parameter block before invocation; run relevant host-safe tests. Do not guess switches or execute a VM-mutating script locally. |
| V1 | Elevated test VM: `qcache developer verify Q: --suite write-performance --budget-mib 2048 --repeats 3 --case-filter random-write-q1-Idle-timingFalse --preparation-flush-seconds 600 --diskspd <verified-diskspd.exe> --output <off-target-results-root>` | Verified clean non-OS target only; replace Q: with discovered target. Same binary/hash before and after. |
| V2 | Same as V1 with `--case-filter random-write-q32-Idle-timingFalse` | Q32 means queue depth 32, one workload thread. It is not a corruption test by itself. |
| V3 | Elevated test VM: `qcache developer verify Q: --suite quick --output <off-target-results-root>` | Existing non-OS checks remain enabled. |
| V4 | Elevated test VM: `qcache developer verify Q: --suite policies --output <off-target-results-root>` | Runs sector/admission/oracle scenarios and temporary policies. No pre-existing fault/delay hooks or competing tests. |
| V4a | Elevated test VM: `qcache developer verify Q: --suite pressure --output <off-target-results-root>` | Plan-14 focused trigger/capacity suite on the clean non-OS disk. No DiskSpd. Temporarily uses a 64 MiB cache and delay hook; restores the saved runtime configuration. Not included in `full`. |
| V4b | Elevated test VM: `qcache developer verify Q: --suite drain-decision --budget-mib 1024 --repeats 3 --diskspd <verified-diskspd.exe> --output <off-target-results-root>` | Plan-16 focused T050 matrix: 24 seeded drain/control cases for fitting writes and cold reads at parallelism 1/2/4. Keep the same DiskSpd hash; inspect raw XML, telemetry, snapshots and `*-drain.json`. Not included in `full`. |
| V5 | Elevated test VM: `qcache developer verify Q: --suite write-performance --budget-mib 2048 --repeats 3 --preparation-flush-seconds 600 --diskspd <verified-diskspd.exe> --output <off-target-results-root>` | Full 72-case milestone; do not combine incomplete repetitions. |
| V6 | Elevated test VM: `qcache developer verify Q: --suite flush-interference --repeats 2 --diskspd <verified-diskspd.exe> --output <off-target-results-root>` | Secondary disposable disk only; this suite uses controlled delay. Required only for relevant touched behavior. |
| V7 | Elevated test VM: `qcache developer verify-status <exact-run-directory>` | Read status; not a substitute for raw evidence or completion marker. |
| V8 | Elevated test VM: `qcache developer verify-recover <exact-failed-run-directory>` | First inspect failure/fault/trace, verify disk identity and all owned processes exited. Recovery has its own verdict and must not relabel the original run. Stop if recovery fails. |
| C0 | Elevated recoverable VM: `qcache developer verify C: --suite system-preflight --recoverable-vm --system-instance <exact-PnP-ID> --system-bytes <exact-disk-size> --output <existing-directory-on-other-physical-disk>` | Plan-18 read-only inventory/state check. Requires expected identity and off-target output; no C: file writes or cache activation. Split-CLI VM proof exists. |
| C1 | Same guards/output, `--suite system-files` | Plan-19 bounded 64 MiB owned-file create/overwrite and off-target oracle; split-CLI VM proof passed with C: disabled. It does not activate the cache. Never run V1-V6 against C: by stripping guards. |
| C2 | After a separately approved normal restart, same guards/output, `--suite system-post-restart --oracle <prior-run-on-other-disk>\oracle.json` | Plan-19 read-only byte check passed with C: disabled. It is not a dirty-cache restart or active-C: persistence verdict. |

The previously used CDM DiskSpd binary hash is
`7281BF6DA6C03797016EDDF2E8AAEC4C644AE893D403D57A030B7E2E14B61079`.
Microsoft DiskSpd XML mode is also supported, but a comparison must keep the
same binary/hash. No tool downloads or substitutions hidden in a benchmark.

## 7. Evidence, failure handling and alpha exit criteria

### 7.1 Concrete maintained-test recipes

These are specifications for reuse/extension of existing scenario helpers, not
new standalone scripts. Implement missing capabilities in A06/A08 and add host
contract coverage before VM execution. Proposed bounds below can be reduced for
a small VM only if the report records the change and makes no matched-performance
claim. Use a deterministic existing pattern generator and record its version and
seed; compute expected bytes independently of cache reads.

| Recipe | Ordered procedure | Required oracle / evidence |
|---|---|---|
| R1: basic owned-file integrity, secondary disk then C:-safe mode | 1. Create a unique run directory and a 64 MiB file with recorded seed 104729. 2. Close/reopen and compare all bytes. 3. Overwrite the same 1 MiB range starting at 8 MiB twice with seeds 104759 then 104761. 4. Read immediately and compare to the independently maintained expected image. 5. Close handles, flush filesystem buffers then perform explicit administrative persistence through the shared API. 6. On Q:, disable/drop-clean as the existing oracle requires; on C:, use only A07/A08-supported controls, not a copied raw helper. 7. Reopen with unbuffered reads and compare the entire file, including untouched guards. | Independent expected content plus final SHA-256 stored off-target before final verification. Buffered rereads alone are insufficient evidence of physical-disk contents. Distinguish a current-volume byte check from independent persisted-byte proof if active cache cannot safely be bypassed. |
| R2: partial-sector and in-flight overwrite regression | 1. Run existing `policies` sector scenario on 512-byte logical-sector Q:. 2. Cover all eight 512-byte positions of a 4 KiB block and a cross-block write with neighboring guards. 3. Reuse the controlled 25 ms delay to exercise old/new versions at parallelism 1/2/4 and retention off/on. 4. Compare newest RAM bytes before drain and persisted bytes after drain/disabled-cache read. 5. Inspect lower-attempt counters and whether the intended in-flight condition was actually observed. | Existing scenario assertions plus exact before/after counters and bytes. Keep synthetic delay/fault work off C:. Do not claim unobserved races or unsupported 4Kn as passed. |
| R3: normal background drain under foreground traffic | 1. On Q:, use a fitting hot set no larger than one quarter of the configured budget. 2. Run deterministic writes for 60 seconds with cached reads of known current ranges; serialize conflicting test operations or record a deterministic ordering so the oracle is unambiguous. 3. Collect ready 200 ms telemetry, accepted/drained deltas, dirty capacity, errors and read/write latency. 4. Stop the writer, close owned handles and verify all owned bytes after persistence. | No false mismatches from an ambiguous test oracle. Foreground completes, background makes progress when work is eligible, memory remains bounded. Record capacity waits separately; capacity exhaustion is an allowed reason to wait. |
| R4: explicit flush while later writes are queued | 1. In a maintained deterministic secondary-disk test, admit pattern A to range X. 2. Issue explicit administrative flush and hold lower completion with an existing controlled hook if needed. 3. Submit pattern B afterward to a distinct range Y with known submission ordering. 4. Observe that flush cannot report success before its required lower work completes. 5. Release the hook, finish both operations and explicitly persist B before final full-file comparison. | Prove A is covered by the first barrier; do not require the cache to remain globally empty after B is allowed to resume. Do not claim cutoff-flush semantics unless the implementation actually defines and tests a sequence cutoff. Fault variant is secondary disk only. |
| R5: C: normal-restart integrity | 1. Use A08 mode to create the R1 file and off-target oracle. 2. Write a fresh deterministic 2 MiB prefix with a distinct recorded seed; close owned handles. 3. Capture dirty state without synthetic delay on C:. 4. Perform only the approved normal restart. 5. After startup, check actual loaded identity/profile and run read-only unbuffered comparison against the saved oracle. | If the candidate supports safe temporary cache bypass, use it for independent disk verification; otherwise record what layer was verified. Dirty state must have been observed to call this a dirty-restart test. No forced power-cut guarantee. |
| R6: bounded alpha smoke | 1. Extend R1/R3 helpers to operate on a fixed 256 MiB owned-file set for 30 minutes with deterministic seeded updates and periodic owned-range checks. 2. Collect bounded telemetry with start/end memory and error state. 3. Close, persist and verify all files. 4. Run through the secondary suite and separately through the C:-safe mode, never synthetic fault mode on C:. | Exact duration, seed, operation count, hashes, peak dirty/reserved memory, error deltas, maximum observed drain stall and final state. No whole-volume scan or deletion of non-owned files. |

The 30-minute duration and file bounds are proposed alpha checks, not claims
about completed testing or a demand for exhaustive certification. Existing
passed scenarios need not be reimplemented under a new name.

### 7.2 Failure and result rules

| Situation | Required action / interpretation |
|---|---|
| Normal verify finished | Require `FINISHED.txt`; read `status.json`, `SUMMARY.md`, `results.json`, `run.log`, raw worker stdout/stderr/exit/PIDs, telemetry ready record, `*-interval.json` and raw score XML. Inspect restore boundary/mismatch evidence if applicable. |
| Recovery finished | Read `recovery-result.json`, restore worker reply, trace and log. Recovery directories do not use the normal FINISHED/status layout. |
| Missing readiness/sample/field | Fail the affected proof; never substitute zero or accept missing telemetry. Validate actual sample coverage and gaps against the recorded interval. |
| Performance scoring | Separate process-wide counter interval (includes warmup/close) from score window. Zero-completion latency is N/A. Timing-on is diagnostic, not interchangeable with timing-off. |
| Timeout | Determine operation and last progress. Inspect owned process records; no arbitrary process kill, deadline inflation or whole-VM reset. A killed worker does not by itself prove an in-kernel operation ended. |
| Device/fault/identity change | Stop. Never treat faults as transient draining; never discard dirty data or auto-retry writes blindly. |
| New byte mismatch / boot failure | Stop alpha progression, preserve evidence, repair the controlling code and add a focused regression. Accepted power-loss volatility is not permission to waive normal-operation corruption. |
| Alpha evidence complete | Owner reviews A01-A12 dispositions, scope restrictions and residual risks. No full public qualification claim. Deferred speculative optimizations are not blockers by themselves. |

## 8. Original 15-step overview at handover

TRUE means the stated scoped implementation exists, not that every safety gate
passed or all future optimization is done. Partial broader work is FALSE.
Expected ranges are speculative, workload-specific and not additive.

| # | Area | Implemented | Expected gain | Measured / verified | Alpha treatment |
|---|---|---|---|---|---|
| 1 | Barrier/lower-I/O attribution | TRUE | 0% direct | Deployed counters and focused checks passed. | Preserve; extend only for A02 evidence gaps. |
| 2 | Explicit Deferred policy | TRUE | Removes premature draining; no fixed gain | Short checks passed; real one-hour soak pending. | Not default; optional unsupported mode must be labelled/scoped. |
| 3 | RAM-first partial-sector admission | TRUE | 0-260% affected recovery envelope | Zero-attempt admission and byte oracles passed; no isolated speed percentage. | Preserve; touched-path checks in A06. |
| 4 | Zero-length/oversized/quota handling | FALSE, partial | 0-20% affected cases | Zero-length fast path exists; broader fallback work pending. | Fix correctness blockers, defer speculative speed work. |
| 5 | Safe observation/query bypass | TRUE, scoped | ~0% writes; 0-100%+ affected readers | Hotplug GET allowlist; no isolated gain. | Preserve conservative unknown-control ordering. |
| 6 | Versions/locking/transient reserves | FALSE, partial | 0-30% during drain | Pins/unlocked copies, overwrite checks exist; race/fault gaps. | Target changes and critical failures, not a complete redesign. |
| 7 | Independent ready-request service | FALSE, partial | 0-50%+ mixed | Cooperative read lane; general service pending. | Only extend if alpha blocker measured. |
| 8 | Admission budget/Automatic borrowing improvements | FALSE | 0-20% under pressure | Existing allocation behavior exists; planned improvements not complete. | Preserve memory safety/backpressure; defer optimization. |
| 9 | Per-4KiB synchronization overhead | TRUE, current optimizations | Original hypothesis 5-25% | Latest matched Q1 +9.74%, Q32 +172.96%; qualified. | Preserve checkpoint, investigate regressions not speculative rewrites. |
| 10 | Range-aware TRIM | FALSE | ~0% writes; 0-50%+ delete workloads | Probe Win32 326 with/without filter. | Explicitly postponed. |
| 11 | Cutoff flush/live policy/transactional resize | FALSE | ~0% isolated; 0-50%+ concurrent | Not implemented by runner cleanup. | Implement only necessary safe boundaries; no cutoff claim without sequence/byte proof. |
| 12 | Cold-read/drain scheduling | FALSE | 0-500% affected mixed recovery envelope | No isolated result. | Evidence-driven only if required for alpha usability. |
| 13 | Indexed selection/independent workers | FALSE, conditional | 0-100%+ high QD | Deferred. | Not an alpha prerequisite. |
| 14 | Power/shutdown/PnP/system-disk qualification | FALSE | 0% throughput | Dedicated active C: work outstanding. | Critical scoped work A07-A09. |
| 15 | Statistics/evidence/restoration | FALSE, partial | 0% driver gain | Runner exists; Q32 cleanup passed; Q1 timeout; UI work pending. | A02/A08/A10; cosmetic UI expansion can wait. |

After completing any step, reproduce this full list with updated TRUE/FALSE,
expected versus obtained gains, deployment status and remaining verification.
Also report the A-task just completed, tests/run IDs, open blockers and next task.
Do not call a documentation-only handover a completed optimization step.

## 9. Cleanup and keep-for-now boundaries

| Category | Disposition |
|---|---|
| Legacy engine and command/logger tools | Remove in A04; no need for a historical source archive in the current tree. Preserve only dependencies still used by current code. |
| Active lab-named dispatch/cache/diagnostic functionality | Keep implementation, simplify names/build paths. One product driver and CLI, no separate diagnostic installation. |
| PowerShell | Review individually in A05; keep build/signing/setup/updater/offline recovery where appropriate. |
| Historical README/known-issues narrative | Replace with current product facts in A10. Source-history details need not remain in user documentation. |
| `RAM_FIRST_IMPLEMENTATION_TRACKER.md`, `RAM_FIRST_PERFORMANCE_PLAN.md`, `WRITE_PERFORMANCE_TRAJECTORY.md` | Keep for ongoing implementation/evidence. |
| Historical verification notes under `docs/secondary_docs/` | Keep `CONCURRENCY_VERIFICATION.md`, `FAST_FLUSH_READ_VERIFICATION.md`, `OBSERVER_FIX_VERIFICATION.md` and `PROGRESSIVE_SELECTION_VERIFICATION.md` until useful checks/open items are transferred; then consolidate. Current execution documents remain directly in `docs/`. |
| Owner's modified research copies and private handoff/session notes | Preserved under ignored `.lab/private-handover-20260920/` with original relative paths; reconcile explicitly, not bulk delete or stage. Tracked performance documents remain at their checked-in versions. |
| `DEVELOPER_VERIFICATION.md`, developer README, current decision/cleanup records and this plan | Maintain as current engineering material; avoid shipping internal evidence by accident. |
| Licenses/notices | Keep applicable obligations; update by actual retained provenance, not by assumptions about a new engine. |
| Frozen binaries, PDBs, exact raw runs | Keep privately while supporting investigations and recoveries; never commit credentials or VM keys. |

## 10. Requirements and task coverage

| Requirement | Tasks | Evidence of completion |
|---|---|---|
| REQ-01 | T023-T037, T046-T048 | Recoverable-VM preflight, safe C: runs, participant scope/approval. |
| REQ-02 | T009-T010, T023-T037 | Fast new-task defaults and actual active C: Fast verification. |
| REQ-03 | T008-T011, T019-T022, T024-T037, T040, T046-T047 | Explicit barriers, byte/error checks and honest volatility wording. |
| REQ-04 | T010-T011, T042-T045 | Short trigger policy and measured drain/foreground behavior. |
| REQ-05 | T004-T011, T019-T022, T042-T045 | Attribution, capacity correctness, foreground latency and drain progress. |
| REQ-06 | T002, T004-T008, T050, T063 | Explained/fixed exact failure, actionable lower-I/O decision and measured release drain usability. |
| REQ-07 | T012-T018, T028-T031, T038-T039 | One build/installer/driver/CLI and maintained scenarios. |
| REQ-08 | T012-T015, T040-T041 | Obsolete source removed, current docs and build references valid. |
| REQ-09 | T016-T018, T038-T039 | Script replacement mapping and independent recovery path. |
| REQ-10 | T001-T003, T019-T037, T044, T046, T049-T054, T056, T061-T062 | Focused correctness, pre-C: capacity/ordering/lifetime/recovery gates and supported-scope qualification. |
| REQ-11 | T019, T040, T046 | TRIM deferred, conservative handling retained, no false PASS. |
| REQ-12 | T038-T041, T046-T048 | Tested servicing failures and understandable current instructions. |
| REQ-13 | T001-T008, T028-T031, T042-T048 | Frozen identities, immutable evidence, correct metrics and full progress reports. |
| REQ-14 | T001-T003, T012-T018, T041 | Owner work/evidence preserved and accurate legal notices. |
| REQ-15 | T055-T066 | Production support contract, safety/security/servicing qualification and controlled release. |

## 11. Agent completion record template

Use this table in the tracker for each completed A-step; keep raw evidence private.

| Field | Fill with |
|---|---|
| Step / tasks | Axx and exact Txxx IDs completed or blocked. |
| Source | Commit, dirty state and exact relevant files; no unrelated staging. |
| Implementation | Concrete behavior added/removed; distinguish proposal from deployed code. |
| Verification | Exact commands, exit/results, driver/CLI/tool hashes and immutable run IDs. |
| Failures | Original verdicts, root-cause evidence, recovery result and unresolved limitations. |
| Performance | Matched comparison only; separate timing modes and score/counter windows. |
| VM end state | Owned processes stopped, hooks disposition, pending bytes/errors, configuration and loaded identity; do not assume cleanup succeeded. |
| Next | Next dependency-ready task and any owner decision needed. Include full 15-step overview. |
| Disposition | Finding -> verified fix, enforced restriction or measured accepted behavior; map remaining gaps to task/release gate. Do not leave a known defect as an unowned research note. |
