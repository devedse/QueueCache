# QueueCache private alpha implementation handover

Date: 2026-09-20. Status: implementation plan, not an alpha readiness verdict.
Audience: the next coding agent and project owner. Deliverable: one coherent
QueueCache product for a small, recoverable-VM alpha, including Fast caching on
the physical disk backing C:. This is not a public production certification plan.

## 1. Start here

| Order | Action | Result required before proceeding |
|---|---|---|
| 1 | Read root `AGENTS.md`, this document, `docs/ALPHA_PRODUCT_DECISIONS.md`, and `docs/RAM_FIRST_IMPLEMENTATION_TRACKER.md`. | Understand accepted volatility, preserved correctness requirements, and current evidence. |
| 2 | Inspect Git status/diffs and current HEAD. Do not reset the worktree. | Identify prior uncommitted restoration work, decision documents, and unrelated owner changes. |
| 3 | Begin A01 below. Do not reinstall drivers, enable C:, run a broad matrix, or start cleanup blindly. | Establish a reproducible starting checkpoint and access to exact prior evidence. |
| 4 | Execute A01-A12 in dependency order, one small tested change at a time. | Each task records implementation separately from verification; no manufactured PASS. |

This document is the alpha execution sequence. The RAM-first tracker remains
the source of truth for the original 15 optimization work items. Update both
when a task changes those items; do not create conflicting progress lists.
The decision and cleanup documents define product direction and inventory;
this handover defines the execution sequence. It authorizes no Git history rewrite or automatic
destructive VM recovery. Observe current owner authorization before deployment,
reboot, driver removal or destructive fault experiments.

## 2. Product requirements and decisions

| ID | Requirement / decision | Implementation interpretation |
|---|---|---|
| REQ-01 | Private alpha for owner, colleagues and friends on recoverable VMs. | No valuable sole-copy data; document tested environments and gaps. Public signing/certification and a broad hardware matrix are later milestones. |
| REQ-02 | Fast is the product default, including C:. | Preserve explicit volatility acknowledgement and Strict as a choice. Do not substitute a Strict-only or secondary-disk-only final alpha. Driver initialization can remain disabled/safe until an explicitly saved profile is applied. |
| REQ-03 | Abrupt-loss volatility is accepted. | A power loss/crash can lose acknowledged RAM data and damage the filesystem. Do not promise persistence for Fast application flushes. This does not permit silent normal-operation corruption, stale reads, lost dirty data after an I/O error, or false success from an explicit administrative flush. |
| REQ-04 | Seconds-scale background draining, not a default one-hour hold. | Use a short configurable trigger. Proposed alpha default: existing Idle policy, 5,000 ms dirty-age trigger, 250 ms idle trigger, 40/80 watermarks, 256 KiB batches, parallelism 1. This is a proposed concrete baseline, not a previously approved timing value. Validate it in A03; do not change existing saved choices. |
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

Optional one-hour Deferred behavior is not the alpha default. Its real one-hour
soak need not block the alpha if explicitly outside the alpha support claim;
either hide/label that optional mode as unqualified or qualify it separately.
Do not delete it incidentally or pretend its soak has passed.

## 3. Evidence checkpoint, not assumptions

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

Paths below describe the pre-cleanup tree. Update this map as files are renamed.

| Responsibility | Concrete starting point | Important boundary |
|---|---|---|
| Active request dispatch, queue, direct admission, PnP/power | `driver/qcache/lab.cpp`: `RequestWorker`, `ServiceCachedReads`, usage notifications | Active product code despite its name. Forwarding paging/hibernate/dump notifications is not proof of active caching safety. |
| Cache storage, reads/writes, drainer, barriers | `driver/qcache/writecache.cpp`: `Drainer`, `QcCacheBarrier`, `QcCacheProcess`; `writecache.h` | Preserve newest-version reads, in-flight ownership, sparse sectors and ordered completion. |
| Policy and native contracts | `driver/qcache/cachepolicy.h`, `cachepolicy-check.h`, `readselection-check.h`, `abi-check.cpp`, `qcstats.h` | Keep default policy/capability/ABI definitions coherent with management. |
| Configuration and target validation | `src/QueueCache.Operations/CacheConfiguration.cs`, `DiskTarget.cs` | `ConfigurationManager.Apply` is shared; `WaitForHealthyState` is not a zero-dirty predicate. Do not change it to mask test cleanup. |
| File integrity and volume flush | `src/QueueCache.Developer/FileTests/Runner.cs`: existing file modes and `CheckedVolume` | Existing advanced modes reject OS writes; some inject delay/faults. Do not simply remove guards. |
| Raw device scenarios | `src/QueueCache.Developer/WriteTests/Runner.cs` | Destructive fixed-region writes belong only on explicitly disposable non-OS RAW disks. Never C:. |
| Maintained orchestration | `src/QueueCache.Developer/Verification`: `VerificationRunner`, `VerificationWorker`, `VerificationPlan`, `OwnedProcess` | Reuse worker ownership, ready observers, trace records, deadlines and recovery. CLI only binds arguments. |
| Host-safe contracts | `tests/QueueCache.Management.Tests/VerificationRunnerTests.cs` and existing console harness | Custom `dotnet run` test harness, not an assumed `dotnet test` project. |
| Frontend and policy presentation | `src/QueueCache.Cli`, `src/QueueCache.Desktop`, `src/QueueCache.Management`; desktop tests | Both frontends use shared operations; don't shell from UI into a second app. |
| Product build | `build/Build.ps1`, `driver/qcache/QueueCache.Driver.vcxproj`, `.github/workflows/githubactionsbuilds.yml` | CI selects `LabWriteCache`; no-switch build currently selects legacy. Fix during A04. |
| Setup and update | `packaging/QueueCache.iss`, `Install-Driver.ps1`, `Update-QueueCache.ps1`, build packaging tests | Class-filter installation; service/metadata migration requires compatibility, not search-and-replace. |

## 5. Ordered execution plan

All rows start pending. A task is complete only after its listed evidence is
recorded, not after code compiles. Reuse neighboring helpers/tests; no new
standalone executables or duplicate orchestration. Before each edit state a local
hypothesis and cheapest discriminating check; run that check immediately after
the edit. Fix the touched slice before widening scope.

| Step | Dependencies | Deliverable | Requirements | Exit / stop rule |
|---|---|---|---|---|
| A01 | None | Reproducible handover checkpoint | REQ-10,13,14 | Prior work/evidence identified; host tests pass or exact unrelated failures recorded. |
| A02 | A01 | Explain/fix drain and cleanup failure | REQ-05,06,10,13 | Focused Q1/Q32 cleanup regression passes, or a documented external limitation is explicitly accepted by owner. No silent timeout relaxation. |
| A03 | A02 | Fast and short-drain product defaults | REQ-02,03,04,05 | New-task defaults coherent; saved settings preserved; foreground correctness checked while draining. |
| A04 | A03 | Single current driver build, obsolete code removed | REQ-07,08,14 | Native Debug/Release and managed builds reference only current implementation; ABI maintained. |
| A05 | A04 | One developer CLI and reviewed scripts | REQ-07,09 | Unique useful checks retained, duplicate wrappers removed, setup/offline recovery retained. |
| A06 | A05 | Secondary-disk focused correctness checkpoint | REQ-03,05,10,11 | Required tests pass with byte oracles; any new normal-operation corruption stops rollout. |
| A07 | A06 | Explicit C:-disk implementation contract and safeguards | REQ-01,02,03,10 | Active system-disk path is accounted for in code; no guard-removal shortcut. |
| A08 | A07 | C:-safe verification workflow in existing runner | REQ-01,02,07,10,13 | Host tests prove OS/raw/fault separation and correct busy-volume result semantics before VM use. |
| A09 | A08 | Disposable-VM C: Fast validation | REQ-01,02,03,10 | Normal operation and normal restart byte checks pass; supported power/pagefile scope explicit. |
| A10 | A09 | Reliable single installer and current docs | REQ-01,07,08,09,12,14 | Fresh install, upgrade-failure handling, upgrade/uninstall/recovery checked; no misleading old docs. |
| A11 | A10 | Final-candidate performance and bounded endurance | REQ-04,05,10,13 | Complete selected matrices, no unexplained significant regression or integrity failure; no exhaustive certification claim. |
| A12 | A11 | Private alpha handoff and reporting loop | REQ-01,02,03,11,12,13 | Owner accepts evidence/limitations; exact candidate frozen and participant instructions ready. |

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

### A07. Engineer active C:-disk support

Do not infer C: support from class-filter attachment or the presence of a C:
card in the UI. The cache operates on a physical disk, including other partitions
and system roles on that disk. Do not assume that moving the pagefile alone makes
all OS-disk risks disappear.

| Task | Do this, in order | Pass condition / stop rule |
|---|---|---|
| T023 | Trace active dispatch for paging, hibernation, dump, shutdown, power, PnP, direct I/O and saved-profile startup. Record for each whether it is queued, cached, bypassed, drained or unsupported, including allocation/IRQL/lifetime constraints. | A concrete operation table tied to current code, not a broad architecture essay. Forwarding usage notification is not enough. |
| T024 | Implement the smallest safe active-system-disk policy. Establish ordered handling of special I/O, nonpageable resources/progress under memory pressure, and shutdown/normal restart persistence while lower storage remains available. | Fast ordinary writes on C: remain the product goal. A bypass for paging or another special request cannot ignore overlapping newer dirty data or create stale reads. No global off switch masquerading as C: support. |
| T025 | Explicitly support and test hibernation/Fast Startup/dump behavior, or define an enforced alpha prerequisite that prevents the unsupported operation/configuration and makes the limitation visible. | Do not silently disable Windows features or rely only on prose when an unsafe active path remains reachable. If a safe restriction is impractical, implement the path before C: rollout. |
| T026 | Make saved-profile activation validate disk identity, capabilities, memory budget and previous fault state; avoid guessing PhysicalDrive0. Verify suspend/resume and startup failure leave a usable, clearly reported state. | No saved setting silently activates a different disk; no automatic replay of a fault as a transient drain. Target disappearance/identity change fails clearly. |
| T027 | Run local native/managed checks and package the candidate through existing build/signing flow. Freeze exact SYS hash and source identity. | No installation on the developer host. VM access/recovery prerequisites must be confirmed before A09. |

### A08. Add a separately guarded C:-safe verification workflow

This workflow does not exist yet. Any new suite name/flag is to be implemented
and documented, not a command the next agent may assume currently works. Keep it
inside `qcache developer verify` and the same worker infrastructure.

| Task | Do this, in order | Pass condition / stop rule |
|---|---|---|
| T028 | Add a typed system-volume file-only scenario with explicit opt-in and recorded expected physical identity/size/volume. Require off-target output storage and a recoverable-VM acknowledgement. Create only a unique test directory and bounded files. | Current non-OS suites retain their guards. No raw writes, formatting, TRIM probes, synthetic faults, broad registry changes or automatic reboot in the C: scenario. |
| T029 | Reuse deterministic file creation/read/overwrite/oracle helpers without inheriting unsafe advanced modes. Store independently computed expected seed/range hashes off the target before the operation being tested. Support a separate read-only post-restart verification phase. | Byte oracle survives a guest restart; expected values are not reconstructed from potentially wrong returned data. Checks are limited to owned test files. |
| T030 | Define clean-state semantics for a continuously busy OS disk. Verify explicit persistence at a controlled boundary and recorded saved settings, not perpetual global DirtyBytes==0 after re-enable. Keep ordinary secondary-disk restoration checks unchanged. | Host tests model writes before/after the boundary, exact errors and configuration checks. A busy C: is not a manufactured cleanup failure or an excuse to ignore real persistence errors. |
| T031 | Test identity mismatch, omitted opt-in, OS target passed to raw/fault mode, output on same target, missing observer, timeout, child-process ownership and interrupted post-restart verification. | All unsafe/ambiguous requests fail before mutation. No unknown process termination or weakened ready/coverage checks. |

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
| H4 | Host: `MSBuild.exe driver/qcache/QueueCache.Driver.vcxproj /t:Build /p:Configuration=Release /p:Platform=x64 /p:LabPassThrough=true /p:LabSerialized=true /p:LabWriteCache=true /v:minimal` | Locate installed MSBuild/WDK first; use current build pipeline for package/CI. Also validate Debug for native cleanup. Do not manually bump application versions; CI owns them. |
| H5 | Host: existing `build/Test-PackageLayout.ps1`, `Test-Packaging.ps1`, `Test-RegistryFilters.ps1`, `Test-Cli.ps1` | Read each parameter block before invocation; run relevant host-safe tests. Do not guess switches or execute a VM-mutating script locally. |
| V1 | Elevated test VM: `qcache developer verify Q: --suite write-performance --budget-mib 2048 --repeats 3 --case-filter random-write-q1-Idle-timingFalse --preparation-flush-seconds 600 --diskspd <verified-diskspd.exe> --output <off-target-results-root>` | Verified clean non-OS target only; replace Q: with discovered target. Same binary/hash before and after. |
| V2 | Same as V1 with `--case-filter random-write-q32-Idle-timingFalse` | Q32 means queue depth 32, one workload thread. It is not a corruption test by itself. |
| V3 | Elevated test VM: `qcache developer verify Q: --suite quick --output <off-target-results-root>` | Existing non-OS checks remain enabled. |
| V4 | Elevated test VM: `qcache developer verify Q: --suite policies --output <off-target-results-root>` | Runs sector/admission/oracle scenarios and temporary policies. No pre-existing fault/delay hooks or competing tests. |
| V5 | Elevated test VM: `qcache developer verify Q: --suite write-performance --budget-mib 2048 --repeats 3 --preparation-flush-seconds 600 --diskspd <verified-diskspd.exe> --output <off-target-results-root>` | Full 72-case milestone; do not combine incomplete repetitions. |
| V6 | Elevated test VM: `qcache developer verify Q: --suite flush-interference --repeats 2 --diskspd <verified-diskspd.exe> --output <off-target-results-root>` | Secondary disposable disk only; this suite uses controlled delay. Required only for relevant touched behavior. |
| V7 | Elevated test VM: `qcache developer verify-status <exact-run-directory>` | Read status; not a substitute for raw evidence or completion marker. |
| V8 | Elevated test VM: `qcache developer verify-recover <exact-failed-run-directory>` | First inspect failure/fault/trace, verify disk identity and all owned processes exited. Recovery has its own verdict and must not relabel the original run. Stop if recovery fails. |
| C1 | Not available yet: A08 C:-safe suite and read-only post-restart mode | Implement and test first, then replace this placeholder with verified help/command examples. Never run V1-V6 against C: by stripping guards. |

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
| `CONCURRENCY_VERIFICATION.md`, `FAST_FLUSH_READ_VERIFICATION.md`, `OBSERVER_FIX_VERIFICATION.md`, `PROGRESSIVE_SELECTION_VERIFICATION.md` | Keep until useful tests/reasoning and open items are transferred, then consolidate. |
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
| REQ-06 | T002, T004-T008 | Explained/fixed exact failure, host contracts and Q1/Q32 completion. |
| REQ-07 | T012-T018, T028-T031, T038-T039 | One build/installer/driver/CLI and maintained scenarios. |
| REQ-08 | T012-T015, T040-T041 | Obsolete source removed, current docs and build references valid. |
| REQ-09 | T016-T018, T038-T039 | Script replacement mapping and independent recovery path. |
| REQ-10 | T001-T003, T019-T037, T044, T046 | Focused correctness, C: lifecycle evidence and explicit gaps. |
| REQ-11 | T019, T040, T046 | TRIM deferred, conservative handling retained, no false PASS. |
| REQ-12 | T038-T041, T046-T048 | Tested servicing failures and understandable current instructions. |
| REQ-13 | T001-T008, T028-T031, T042-T048 | Frozen identities, immutable evidence, correct metrics and full progress reports. |
| REQ-14 | T001-T003, T012-T018, T041 | Owner work/evidence preserved and accurate legal notices. |

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