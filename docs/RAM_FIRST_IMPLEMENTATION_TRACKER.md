# RAM-first cache: contract, implementation tracker and verification

Last updated: 2026-10-09. This is the authoritative execution tracker. Detailed
audit/rationale: [RAM_FIRST_PERFORMANCE_PLAN.md](RAM_FIRST_PERFORMANCE_PLAN.md).
Statuses distinguish source implementation from VM verification. No performance
gain is claimed until measured. Keep each row current in the implementing commit.

**Current status, 2026-09-27 (installed 0.4.148.1 `97b8f4f`, tools 0.4.149.1 plan 57).**
Fast mode is the product focus. On 2026-09-26/27: the saved-profile shutdown
deadlock was fixed and soaked under Driver Verifier; Driver Verifier passes found no
other violation; a failed lower IRP allocation no longer faults the cache; settings
changes apply fully or restore the previous settings; paging-file I/O bypasses the
worker; application read misses are kept with scan-resistant insertion; TRIM is
range-aware (not VM-verified: no discard-capable disk); status output separates
live values from totals; default drain parallelism is 2. On 0.4.148.1 under Driver
Verifier every maintained suite (`quick`, `policies`, `pressure`,
`paging-coherence`, `ordering-faults`, `app-write-profile`) and a 10-cycle
saved-profile restart soak pass; 0.4.146.1 passed the 20-cycle soak. See
"Normal-use batch, 2026-09-27" below and NEXT_PHASE_PLAN.md.

**Previous status, 2026-09-25 (installed 0.4.117.1, tools plan 47).** T082-T086 are
implemented and VM-verified: paging-read offload, submitted-order and fault orders,
lower-attempt attribution, application paging admission with per-request paging-file
recognition, page-backed cache memory, C: active restarts (one runtime-only, four
saved-profile cycles) under bounded memory pressure, and an operator Paint/Photos
session with a post-drain byte match. Found and fixed on the way: bugcheck 0x7A while
restarting with dirty C: data (0.4.111.1). The saved-profile shutdown hang is
diagnosed from an NMI kernel dump (2026-09-26, see T081 and KNOWN_ISSUES): a
deadlock between the request worker forwarding a paging-path usage notification
and a paging read queued behind it. The first fix (0.4.125.1) passed a 20-cycle
soak but was insufficient: Driver Verifier reproduced the hang on its first
restart. The second fix (0.4.128.1, lower call from a work item) passed 20/20
cycles under Driver Verifier. The
next phase is planned in [NEXT_PHASE_PLAN.md](NEXT_PHASE_PLAN.md). At the owner's
request the VM's raw run directories, workloads and old builds were deleted on
2026-09-25; run IDs cited below remain as references, their summaries recorded here
are the retained evidence. Older dated notes
below are history.

Crash found and fixed, 2026-09-25: installed 0.4.104.1 bugchecked twice (0x7E,
divide-by-zero) during `ordering-faults`. The minidump pins it to
`FullyResident` (T082 offload check) indexing a released, zero-capacity cache.
Fixed with a zero-capacity guard; see KNOWN_ISSUES. Plan-40 `ordering-faults`
passed twice on 0.4.104.1 before the second crash
(`QueueCache-Verify-20260924-223800-f0198d85...`, `...-224059-4384bf62...`):
a failed old drain stops the waiting newer paging write before submission and
keeps its dirty version, a short last sparse run keeps the whole version, and a
cancelled capacity-blocked write admits nothing. The fixed build needs the same
suites repeated without a crash.
Fixed build 0.4.106.1 (`8f9e3b6`, driver SHA-256 `AC96981408A196A8...D07D4`) after a
normal reboot: `ordering-faults` completed five consecutive times on one boot
(`QueueCache-Verify-20260924-230144-9557d2ba...` through `...-230329-c12e1fb4...`;
the crash had occurred on the third), then `paging-coherence` (`...-230355-4c4113b7...`),
`policies` (`...-230425-503eb07b...`) and `pressure` (`...-230611-3f05c5e7...`) all
COMPLETED with clean restoration. Each ordering-faults run accepted exactly its
two recorded lab errors. There was no reboot or new minidump.

Plan 39 source candidate, 2026-09-24 (not installed): Diagnostics V9 lab range
gate plus paging-coherence stages for T083 submitted-order, later cached C and
the T082 capacity-blocked page-in dependency. See DEVELOPER_VERIFICATION plan 39.

Plan 38 source candidate, 2026-09-24 (installed as 0.4.99.1; see evidence below):
T082 moves every paging read that needs lower I/O (at most 1 MiB, 64 queued)
off the sole request worker onto one per-disk paging-read thread that depends only
on the cache mutex and lower completion. Offloaded reads overlay and unpin the
exact versions they pinned. Writes wait only for overlapping offloaded reads;
slot-retiring/state-changing requests wait for all of them and block new
offloads. A paging-write fence now forces only its own overlap; unrelated dirty
data keeps its own drain policy and normal batch size. T084 adds Diagnostics V8
lower-attempt source attribution (generated, forwarded non-paging, forwarded
paging) plus offload counters; admission checks fail on QueueCache-generated I/O,
stay unproven on forwarded non-paging I/O and no longer use aggregate matching.
Native Release/Debug builds and host contracts pass. Luna's uncommitted
range-drain edit did not compile and could drain Deferred/Idle data early; both
were corrected before commit. See the review's disposition section.

Installed plan-38 evidence (2026-09-24): CI build `03120ef` (0.4.99.1, driver
SHA-256 `48FA0BE1DE4F28FAB4C6BE0042E6AEF6058892736D32A170ABFB19D8DF274468`) loaded
after a normal reboot; the saved Q: profile restored (task result 0, about four
minutes after boot). On Q: with no competing workload, each run had `FINISHED.txt`,
1/1 COMPLETED and clean restoration:
`QueueCache-Verify-20260924-200833-bb76571cf81e4a6391e900e4b17b01ea` (`paging-coherence`:
both stages PASS, one overlap wait, newest/guard bytes active and released),
`QueueCache-Verify-20260924-200916-72edc6167d5544ddbf3edf093ee0b349` (`policies`) and
`QueueCache-Verify-20260924-201111-1ccf58616ae7429090b38c6573bd3c5b` (`pressure`).
All 13 source-attributed zero-lower-I/O checks PASSED. Each window had exactly one
forwarded paging write and zero generated, forwarded non-paging, other-read or
flush attempts. That explains the one extra lower write that stopped the plan-34
policy runs. Trigger timings counted drainer writes only
(deferred-age 1138.6 ms, idle 902.6 ms, watermark on crossing). Afterwards
Q: V8 showed 33 offloaded paging reads (33 completed, 0 failed, max queued 2), 6
idle waits by control requests and 38 routed reads. This shows the offload path
running, not the forced T082 dependency proof.

Plan 37 source candidate, 2026-09-24: range-coherent paging-marked reads and
targeted overlapping-write drain/fence in `writecache.cpp`; cooperative paging
read service while blocked on lower I/O, capacity or a drain boundary; Diagnostics
V7 routed request/completion/failure/overlap counters; per-case and final
post-release image oracles, default retention/promotion and shared stable target
identity checks. Native Release build and host management/runner contracts pass.
The maintained `paging-coherence` suite now exercises an owned non-OS file through
unbuffered and mapped access; it records the limits of process-wide counters and
does not replace the forced T079 ordering cases.
Plan 34 also allows an existing system-file oracle to be created while the saved
C: cache is active with reconciled usage paths; it requires an observed acceptance
delta before calling that phase an active-write result. The reboot/read-only
phase and bounded pressure exercise remain separate installed checks.
The VM loaded exact 0.4.92.1 (`99866FCDCDFCDCADA55AADA52666EC837FA51AC7DC8BA1A6809CAB0E5D0ED1AA`).
Its plan-34 Q: `paging-coherence` and `quick` runs passed with clean restoration.
The first plan-34 `policies` sector-admission window twice saw one extra lower
write; both runs were incomplete and restored cleanly. Plan 35 then passed all
six sector checks but found its separate fitting foreground admission still used
the old all-I/O assertion. Plan 36 applies the same exact successful paging-write
match there; a local plan-36 verifier completed the policy suite against loaded
0.4.92.1. Plan 37 adds an observed old sparse in-flight/mapped-overwrite case;
its local verifier run passed. Exact installed 0.4.92.1 also passed both guarded
349 MiB active-C: Fast/Strict image cases. The post-drain app comparison, active-created restart,
memory pressure and fault/cancellation/dependency proof remain; the historical
BSOD cause is unknown.

Planning revision 5 review at `30dd36e`: see
[findings and repair instructions](IMPLEMENTATION_REVIEW_20260924.md).
The owner successfully edited the same `baseline.bmp` twice (uncached red,
cached yellow), then opened it in Photos; the second save is 15:22:29 UTC.
C: remained active and error-free at review. This is an ordinary application
observation, not proof that the image was still pending in QueueCache RAM.
Dump capture was disabled and Q: also had a 2 GiB cache/benchmark active.
T080 is now PARTIAL. T082-T086 own remaining progress/scheduling, actual
submission-order proof, admission attribution, buffered/mapped RAM behavior,
capture and application/restart/pressure follow-up. T087 documentation is done.
Review made no VM configuration changes; both caches remain active.

Exact 0.4.92.1 Q: evidence (all on the separate 512-byte-sector non-OS disk):
`QueueCache-Verify-20260924-104944-4b0e616dc4ba42778129a6edbb8baecd`
completed the mapped/unbuffered case with matching active and released bytes,
9 routed paging reads, 8 routed writes and one process-wide overlap wait;
`QueueCache-Verify-20260924-105122-37cc0798f79342b383bcd317d5226ad9`
completed `quick`. Both had `FINISHED.txt`, complete status and clean restoration.
`QueueCache-Verify-20260924-105203-c53e56acff894b29b7184b642fe0e849`
and `QueueCache-Verify-20260924-105642-c6c9087503084f2199d1ebb88c7f4ca2`
stopped at the first policy sector-admission window: lower read/write/flush
attempts changed by 0/1/0. They were INCOMPLETE, not PASS; restoration succeeded
and Q: returned to disabled/released, clean, error-free state. A five-second idle
window between them had no Q: lower or routed paging I/O. The new plan-35 check
will record whether an equal successful paging-marked write occurred in that
exact window; process-wide equality will remain scoped evidence, not attribution
to the owned file or proof of a forced ordering.
Plan-35 tool 0.4.93.1 from `d8c0771`, paired only for verification with the
unchanged loaded 0.4.92.1 driver, ran
`QueueCache-Verify-20260924-111003-ef33faa899fc4292a7932b84c75d00f5`.
All six sector admission/disk-oracle pairs and the observed in-flight ordinary
replacement passed. The next fitting foreground check failed at its first-write
boundary: lower attempts changed 0/2/0, but that caller had not supplied V7
routing snapshots, so attribution was unavailable. The run was INCOMPLETE and
restored cleanly. No 60-second fitting verdict exists from it.

Local plan-36 verifier with the unchanged loaded 0.4.92.1 driver completed
`policies` in `QueueCache-Verify-20260924-111331-59b8983e41bb4b2086782a24363165c0`:
all six sector modes, the observed ordinary replacement and the full 60-second
fitting workload passed. It ran 640,678 serialized 64 KiB write/read pairs with
zero capacity waits; persisted bytes matched after Disable. The first fitting
admission had one lower write matched by one successful routed paging write,
which is process-wide attribution only. `FINISHED.txt`, 1/1 COMPLETED and clean
restoration were checked. A local plan-37 verifier completed
`QueueCache-Verify-20260924-111751-cfad088eb4b940cdbb83cdfebecbf788`:
an isolated 1,536-byte in-flight sparse write, three successful routed paging
writes, one overlap wait, and newest live/released bytes with untouched guards.
It too restored Q: cleanly; the subsequent CI-built confirmation is below.

CI-built 0.4.95.1 plan-37 tooling (source `33543d3`) confirmed both focused
Q: cases against the unchanged loaded 0.4.92.1 driver. `paging-coherence`
completed in `QueueCache-Verify-20260924-145544-b0d3844ff8e64839bd703be83d9d74ff`:
the observed 1,536-byte in-flight write, three successful routed paging writes,
one overlap wait, active/released latest bytes and guards all passed. `policies`
completed in `QueueCache-Verify-20260924-145656-915f1436a6344d8cac35caaf4307f8d3`:
six sector admissions, observed in-flight replacement, the 60-second fitting
window and six policy configurations passed. Both have `FINISHED.txt`, 1/1
COMPLETED, ready control tracing, no restoration failure and Q: disabled,
released, clean and error-free afterward. These are focused successes, not
exact-range/fault/cancellation or C: application proof.

Exact installed 0.4.92.1 completed read-only C: preflight in
`QueueCache-Verify-20260924-110300-f78df30fced34a69ad8132f4e92b9b30`,
then guarded `system-active-image` in
`QueueCache-Verify-20260924-111839-14e143c2057a469fbf617057aaf60436`.
Fast and Strict each accepted the complete 365,953,024-byte BMP with a 512 MiB
runtime budget. All bytes matched the independent Q: oracle while active,
after administrative flush, after each Release and during final restoration.
The run had `FINISHED.txt`, 2/2 COMPLETED, no restoration failure and final C:
disabled, released, clean and error-free. Paging-marked traffic occurred, but
neither a forced dependency nor Paint/Photos was exercised.

## Current release execution status: 2026-09-24

The end goal is production readiness. A01-A12 are the private VM alpha milestone;
A13-A16 cover production qualification and release. The detailed task definitions,
benefits and dependencies are in
[PRIVATE_ALPHA_IMPLEMENTATION_HANDOVER.md](PRIVATE_ALPHA_IMPLEMENTATION_HANDOVER.md).
This tracker owns current status and evidence. TRUE means complete for the named
scope; PARTIAL means some deliverables exist but the gate remains open; FALSE means
not delivered. Completion of an A-step does not complete every original optimization
row below. The source verification contract is plan 37; the latest CI-installed
driver is 0.4.92.1/plan 34, with CI-built plan-37 verifier runs as labelled.
Historical plan-14 `pressure`
passed on exact installed 0.4.64.1. The first plan-15 T050 run on 0.4.66.1
stopped at case 4/24 on a metadata oracle with clean restoration. Plan 16
corrected that assumption. Its exact-build 0.4.67.1 three-repeat VM run
completed 24/24 with clean restoration; the tuning decision remains open.
Plan 17 adds an observed in-flight replacement case to `policies`; exact installed
0.4.69.1 VM proof passed. Plan 18 adds a read-only guarded C: preflight, not a
file workload or active-system-disk qualification. Plan 19 adds bounded owned-file
creation with an off-disk oracle and a separate read-only post-restart check;
both passed on the VM with C: caching disabled. Plan 20 added the first narrowly
guarded active-C: workload. Review found that its success contract could miss
cache admission, treat normal live-OS dirty bytes as a flush failure, and allow
final image evidence to be absent. Plan 21 corrects those gaps and adds the
matching uncached 349 MiB baseline. Plan 22 permits that disabled/pass-through
baseline to run while recording existing system usage paths; only active caching
requires them to be clear. Its split-CLI baseline and negative active-gate runs
completed on the VM against exact installed 0.4.75.1. Neither plan closes T052
ordering or T067. Plan 23 added kernel notification lifecycle evidence without
weakening the activation gate and exact installed 0.4.78.1 resolved the counter
question. Exact installed 0.4.79.1 proved plan 24's associated PnP stop/remove and
device-state policy. Plan 25 measured paging I/O safely with caching disabled.
Plan 26 adds the bounded paging forward-progress safeguards and guarded enablement
needed for the first active experiment. That exact 0.4.82.1 experiment passed.
Plan 27 removes the product activation split and runs both Fast and Strict through
normal Apply. Plan 28 gives those cases separate immutable artifacts; plan 29
requires paging data to bypass RAM admission/read service. Exact
0.4.83.1 proved the normal Fast case, then exposed two blockers: the Strict case
initially reused Fast's owned path, and a saved Fast profile with a configured C:
pagefile was followed after reboot by repeatable qcache/CoreCLR and unrelated Edge
process corruption. Both failures and clean restoration evidence were preserved.
Plan 30 keeps the post-restart physical identity check exact while allowing the
expected paging-role transition caused by adding or removing a pagefile.
The first exact 0.4.86.1 attempt then stopped before its image write because the
Plan-28 per-case directory suffix was not accepted by the owned-path validator;
plan 31 accepts only the exact Fast/Strict suffixes and keeps all other paths rejected.
Exact installed 0.4.87.1 then completed both active-image cases and the configured
pagefile saved-startup regression without the earlier process corruption.

| Step / tasks | Status | Implementation | Verification / remaining boundary |
|---|---|---|---|
| A01 / T001-T003 | TRUE | Starting work and evidence preserved. | Host checks and VM identity/state checkpoint recorded. |
| A02 / T004-T008 | TRUE, scoped | Restoration sequencing and V3 drain attribution delivered. | Focused Q1/Q32 cleanup completed on 0.4.51.1. Lower-I/O dominance does not prove a hardware limit; the optimization decision is now T050. |
| A03 / T009-T011 | TRUE, scoped | Consistent Fast/Idle defaults and saved-profile preservation. | 0.4.57.1 fitting foreground/background case passed. Plan-14 exact trigger/capacity pressure qualification passed on 0.4.64.1 through T051/T069. |
| A04 / T012-T015 | TRUE | One current driver/build path; obsolete implementation removed. | Native/CI builds and installed-build quick/policy regression passed. |
| A05 / T016-T018 | TRUE, scoped | Developer CLI consolidation and independent recovery implementation. | Host packaging checks and a copied-hive recovery dry run passed; actual offline/Safe Mode recovery remains T054/A10. |
| A06 / T019-T022 | TRUE, scoped | Existing secondary-disk scenarios and repaired coalescing oracle used. | Quick/policy and lower-write/lower-flush failure recovery passed on 0.4.57.1. T022's changed-path condition was not general lifetime qualification. |
| A06a / T049-T054, T069 | PARTIAL (T054 postponed by owner) | T050 decided: default parallelism 2 (plan 56). T053: lower IRP allocation retry (0.4.131.1), release-under-load race, paging-write map-failure fallback, direct-write failure; in-flight paging cancellation recorded as not reachable. Driver Verifier standard passes on every suite. | VERIFIED on 0.4.131.1-0.4.148.1 under Driver Verifier (plans 48-57). Open: T052 exact flush-cutoff ordering under concurrency; physical device removal (hot-unplug) and T054 offline/Safe Mode recovery postponed by owner (2026-09-27). |
| A07 / T023-T027, T067-T068, T070-T077, T082/T085 | TRUE, scoped | Paging coherence, normal C: activation, paging-read offload, application paging admission, page-backed memory, shutdown deadlock fix (work-item lower calls), paging-file I/O bypassing the worker (plan 55), application read-miss retention with scan-resistant insertion (plan 56). | VERIFIED on 0.4.146.1/0.4.148.1: 20-cycle and 10-cycle Driver Verifier restart soaks, every recognised paging-file request bypassed the worker, mapped read misses kept (2016/2048 blocks) and re-read from RAM. Remaining limits: application paging reads over 1 MiB or with a full 64-entry table stay synchronous; the pre-alpha Paint/Photos BSOD is recorded as possibly fixed. |
| A08 / T028-T031, T078-T079, T083-T084 | TRUE, scoped | Per-case/final image oracles, lab range gate (V9), lower-attempt attribution (V8), lab faults 8-11 and Diagnostics V11-V13. | `ordering-faults` (8 stages) and `paging-coherence` PASS under Driver Verifier on 0.4.148.1. Remaining: paging-role transitions (adding/removing a pagefile while active). |
| A09 / T032-T037, T073-T074, T080-T081, T086 | TRUE, scoped | Saved C: profile restore, active dirty restarts, operator application session, shutdown deadlock fixed (0.4.128.1). | 20/20 (0.4.128.1, 0.4.146.1) and 10/10 (0.4.148.1) saved-profile restart soaks under Driver Verifier; Strict restart 10/10. Sleep/resume and hibernate postponed by owner (the VM offers no sleep state). |
| A10 / T038-T041 | PARTIAL | Settings changes now apply fully or restore the previous settings (fake-device contract tests). Setup/recovery foundations and documentation exist. | Rollback not forced on the VM. Offline/Safe Mode recovery postponed by owner. Remaining: install/upgrade/uninstall failure matrix and final user docs. |
| A11 / T042-T045 | PARTIAL | New baselines with Microsoft DiskSpd 2.3 on 0.4.148.1: 72-case `write-performance`, `drain-decision`, `app-write-profile`. | Idle vs Off: random Q1 ~18x, Q32 ~19x, sequential Q1 ~43x, Q8 ~32x. Remaining: measure read-retention benefit, explain the random Q32 gap to historical CDM runs, `full` matrix, bounded endurance. |
| A12 / T046-T048 | FALSE | Private-alpha freeze and reporting handoff pending. | Participant release approval not recorded. |
| A13 / T055-T057 | FALSE | Production support contract and safety gap closure planned. | Support scope can be designed during A07; qualification pending. |
| A14 / T058-T060 | FALSE | Security, distribution/signing and production servicing planned. | Actual trust/privilege/servicing evidence pending. |
| A15 / T061-T063 | FALSE | Environment/endurance/final performance qualification planned. | Frozen-candidate support matrix and long-run evidence pending. |
| A16 / T064-T066 | FALSE | Production release/support process planned. | Support collection, staged rollout, recovery and owner release decision pending. |

### Volume filter batch, 2026-09-28 (branch `volume-filter`, PR #2)

QueueCache caches volumes instead of disks ([VOLUME_FILTER.md](VOLUME_FILTER.md)).
Verification on the VM (16 GB since 2026-09-28) with standard Driver Verifier on
0.4.219.1 (`7a18a57`) unless stated. Lab disk: `qcache developer lab-disk` VHDX with
V: and W: (NTFS) and X: (unformatted).

| Item | Implementation | Verification |
|---|---|---|
| V1 volume registration | Topmost Volume-class upper filter; installer backup version 3; no disk-class or per-disk registration; no migration code (owner decision) (`d7033f9`, `d61af51`). | `volumes/volume-registration` PASS on V: and Q:; uninstall drained Q:'s 2 GiB cache, removed only QueueCache from the class list, Windows booted without the filter; reinstall restored Q:'s saved profile at startup (0.4.213.1 -> 0.4.217.1); `Recover-Registration.ps1` restored backups on copies of the SYSTEM hive. |
| V2 boot | Length/sector size read on first need, never from start-up (`e7150b7`). | Every volume, including C:, boots (0.4.187.1 onward); saved-C:-profile soak: see V9. |
| V3 per-volume profiles, CLI, desktop | Profiles named by volume GUID; `qcache volume list`; one desktop card per volume grouped by disk; RAW volumes can be flushed/removed only (`b9680c2`, `7134514`). | Host and desktop fixture tests; desktop checked on the VM console (0.4.191.1); `volumes/volume-shared-disk` profile check PASS. |
| V4 raw disk commands | Nothing to implement: they no longer reach the filter. | `volumes/volume-raw-disk-commands` PASS on the VirtIO disk (Q:) and the VHDX (V:): descriptor, geometry and SCSI INQUIRY with 16 MiB pending and 8 MiB clean; nothing drained, flushed or evicted. |
| V5 shared disk | One cache per volume (unchanged driver design). | `volumes/volume-shared-disk` PASS: V: 256 MiB and W: 128 MiB accept only their own data; flushing V: leaves W:'s pending data. |
| V6 resize | Length re-read per management request and before judging a request beyond the known end (`6ae1e0c`). | Before the fix extending W: past its size failed (Invalid Parameter). `volumes/volume-resize` PASS: shrink 1 GiB and extend back with 32 MiB pending; driver length follows; bytes exact. |
| V7 shadow copies | Media-changing controls forwarded without holding the worker (`fcc0c4c`); only flush-and-hold drains; other volsnap controls bypass the queue (`0909d93`, `7a18a57`). | Found a system-wide freeze (0.4.207.1, local kernel debugger: volsnap's diff-area write queued behind the waiting worker) and then 10 s hold time-outs. `volumes/volume-snapshot` PASS on V: and Q:: the snapshot holds the 32 MiB that were pending in RAM, exact. |
| V8 TRIM | Unchanged driver (`97b8f4f`). | `trim-cache` PASS on the VHDX: pending dropped, clean released, TRIM during a drain accounted for. |
| V10 other file systems | NTFS-only guard removed; FAT32/exFAT no-journal warning; cluster-area offset for FAT test ranges and page files; `lab-disk create --file-system` (`f009b3f`, `27cfcf9`). | FAT32 and ReFS (Dev Drive) lab disks on 0.4.229.1 under Driver Verifier: every maintained suite PASS (snapshot/resize/TRIM SKIP where Windows lacks the feature). Found and fixed: FAT snapshot is unsupported (SKIP), ReFS reports 2 TRIM ranges, ReFS takes a volume offline after injected write errors (remount). Open: fewer caller-thread requests on ReFS (performance, KNOWN_ISSUES). |
| V9 suites on a volume | Maintained suites unchanged; raw tests (`developer test`, `write-tests`) address a volume. | V: `quick`, `policies`, `paging-coherence`, `ordering-faults`, `app-write-profile`, `pressure` PASS; Q: `policies` PASS; `write-tests` (base, concurrent, toggle, write-through, verify) and `developer test` PASS on X:/V:. Saved-C:-profile restart soak (512 MiB C: profile, system-files / paging recognition / post-restart byte check each cycle): 10/10 PASS under Driver Verifier; in 2 cycles paging recognition was UNEXERCISED (with 16 GB the applied pressure caused no page-file I/O). CrystalDiskMark 9.0.3 DiskSpd on Q: (2 GiB Fast/Idle, Verifier off, 3 runs): SEQ1M Q8T1 36.6-37.2 / 20.6-21.4 GB/s, SEQ1M Q1T1 12.6-14.4 / 13.2-13.8 GB/s, RND4K Q32T1 1,576-1,638 / 1,555-1,648 MB/s, RND4K Q1T1 1,230-1,328 / 1,023-1,073 MB/s (disk filter 0.4.169.1: 36.4-36.8/21.0-21.6 GB/s, RND4K Q1 1,004-1,019/851-858 MB/s). |

### Normal-use batch, 2026-09-27

| Item | Implementation | Verification |
|---|---|---|
| N1 settings apply | Apply completes or restores the previous preset, options, size and state; the error says exactly what is left if even that fails (`3b33de7`). Plan 63 forces it on the driver (`a9b98ef`). | Fake-device contract tests for every failure point. VERIFIED on 0.4.169.1 under Driver Verifier: `settings-rollback/lab-fault-6` and `-7` PASS (restored 64 MiB Fast with its options, data intact, same change applied afterwards). |
| N2 read caching | WITHDRAWN for paging reads (plan 60, `4f28450`): keeping application paging read misses served another block's data on C: (KNOWN_ISSUES). Unbuffered read misses are still kept when the buffer repeats no page; bimodal (scan-resistant) insertion stays. | Plan 56's `mapped-read-retained` check passed on 0.4.148.1 but used a file with no pages already resident, so it could not see the dummy page. Plan 60 checks that nothing is kept. |
| N3 fixed reservation | Design decision recorded: no shrinking. | n/a |
| N4 drain defaults | Default parallelism 2 (plan 56). Bounded flush dropped after analysis. | Two `drain-decision` runs (0.4.139.1, 0.4.148.1). |
| N5 paging-file I/O | Recognised paging-file requests forwarded from dispatch, bypassing the worker; Diagnostics V12 counter (plan 55, `8833f67`/`cb29d5a`). | VERIFIED on 0.4.146.1: every recognised request bypassed the worker in all 20 Driver Verifier soak cycles. |
| N6 TRIM | Any number of sector-aligned ranges; trimmed unwritten sectors discarded, partly trimmed blocks keep their other sectors; conservative path only for unknown flags/malformed input (`97b8f4f`). | VERIFIED on a VHDX (`trim-cache`, plan 64, 0.4.211.1-0.4.219.1 under Driver Verifier). SATA test disk T: (2026-09-29): retrim through a Fast cache with 112 MiB pending kept every byte (file-level TRIM unavailable: not thin-provisioned). The VM's VirtIO disks still cannot TRIM (Windows 11 + VirtIO SCSI GET LBA STATUS problem). See KNOWN_ISSUES. |
| N7 special requests | Pass-through after shutdown/power-down (existing), work-item lower calls for PnP/shutdown/disk controls, plus N5. | Driver Verifier soaks above. |
| N8 status display | CLI and desktop label live values vs totals since boot; stale desktop data greyed out (existing). | Desktop fixture tests pass; desktop visuals not checked on the VM. |

### Fast-mode request-path batch, 2026-09-27

Measured with CrystalDiskMark 9.0.3's DiskSpd in CrystalDiskMark's four shapes,
Q: 2 GiB Fast/Idle, Driver Verifier off (details and before/after table:
[WRITE_PERFORMANCE_TRAJECTORY.md](WRITE_PERFORMANCE_TRAJECTORY.md)). Correctness
runs used standard Driver Verifier on installed 0.4.162.1 (`98a7d03`) and, after the P6
fix, 0.4.165.1 (`882a477`) and 0.4.166.1 (`233b501`).

| Item | Implementation | Verification |
|---|---|---|
| P1 caller-thread service | RAM hits and fitting writes on an otherwise idle disk served on the submitting thread; runtime switch `developer performance --caller-path`; Diagnostics V14 (`32f4239`, 0.4.153.1). | RND4K Q1: 100 -> 978 MB/s read, 79 -> 854 MB/s write. Plan 58 `foreground-background`: 1,203,326 of 1,205,844 requests on the caller thread, 0 declined, persisted bytes verified (Verifier on, `...-224226-7300593f...`). |
| P2 idle drainers | Drainers above the parallelism no longer clear the shared wake event (`029d081`, 0.4.154.1). | RND4K Q32 writes during write-back: about 35,000 -> 300,000 IOPS. Explains the "random Q32 gap". |
| P3 deep queues on the worker | Probe every 1024th candidate; a candidate finding the worker busy keeps the next 256 there (`ba8de51`, `117586e`). | RND4K Q32 read 1,489-1,523 MB/s with the caller path on (was 890-969 with it always used). |
| P4 parallel copies | Reads of 256 KiB+ fully in RAM and payload copies of fitting writes that size run on three offloaded-request threads; pinned reads copy with one lock release; cache lock is an exclusive push lock; Diagnostics V15/V16 (`9527891`, `df4904f`, `346be2f`, `f3000a7`). | SEQ1M Q8: 14.4 -> 36.4 GB/s read, 14.0 -> 20.7 GB/s write (0.4.159.1 = `df4904f` pinned copies: 23 GB/s; 0.4.161.1 push lock and write copies: 36/21 GB/s). Plan 59 `parallel-copies` PASS under Verifier: 13,848 1 MiB writes with read-back and 1,630 concurrent reads each returned one whole version; 15,389 read and 13,846 write copies offloaded; last versions persisted (`...-225146-6a9aab31...`). |
| P5 worker spin | Idle worker polls 30 us before sleeping (`98a7d03`, 0.4.162.1). | RND4K Q32 write 1,240-1,285 MB/s (about 200,000 IOPS -> 310,000). |
| P6 page-in corruption fix | Paging read misses are never kept; ordinary misses only when the buffer repeats no physical page; Diagnostics V17; plans 60-61 (`4f28450`, `882a477`, `233b501`). See KNOWN_ISSUES. | 0.4.162.1 with a 512 MiB C: cache: `qcache` crashed and 11 of 192 DLLs read wrong (disk intact). 0.4.164.1: 5/5 pressure runs clean; 1,654 page-ins repeated a page. 0.4.165.1 under Verifier: `program-files-match-disk` PASS 3/3 (191 files, 10 processes), 10/10 saved-C:-profile restart cycles PASS (each with that check), `mapped-read-not-kept` PASS; `policies` found 8 MiB read misses no longer kept (fixed in `233b501`). 0.4.166.1 under Verifier: `policies` 39/39 PASS, `paging-coherence` PASS, `program-files-match-disk` PASS 2/2; CrystalDiskMark rows unchanged (SEQ1M Q8 35.2-37.1/20.8-21.4 GB/s, RND4K Q1 944-972/840-856 MB/s). |
| P7 read-miss isolation | A kept read miss is read into a driver-owned buffer first (plan 62, `112d51d`). | `policies/read-miss-isolation` FAILED on 0.4.166.1 (16 of 16 MiB re-read held another thread's bytes) and PASSED on 0.4.169.1 under Verifier (4,096 blocks kept during 4,550 buffer overwrites). 0.4.169.1 under Verifier: all six Q: suites pass (`policies` 42/42), C: program-file check 2/2; CrystalDiskMark rows unchanged (SEQ1M Q8 36.4-36.8/21.0-21.6 GB/s, RND4K Q1 1,004-1,019/851-858 MB/s). |
| All | | Under Verifier on 0.4.162.1: `quick`, `policies`, `pressure`, `paging-coherence`, `ordering-faults`, `app-write-profile` every check PASS (the four standing `quick` SKIPs: no TRIM-capable disk, excluded cases); Windows host tests pass. The first saved-C: soak on 0.4.162.1 found P6 instead. On 0.4.165.1 the same suites pass except `policies` (above). Not covered: multi-threaded (T4) performance rows, the maintained `write-performance` baseline, physical hardware. |

### Revision 4 implementation queue: 2026-09-24 review correction

The review of `89436a8` found an unresolved normal-operation coherence gap in
the all-`IRP_PAGING_IO` bypass added in `56924e6`. This flag also occurs for
ordinary cached/mapped files; it is not proof of pagefile-only data. Reads skip
newer dirty cache bytes, and direct writes do not reconcile overlapping dirty or
in-flight versions. Foreground request ordering alone does not order the drainer.
This is a source-level defect finding, not proof of the historical BSOD cause.

The implementation instructions and acceptance cases are in
[handover revision 4](PRIVATE_ALPHA_IMPLEMENTATION_HANDOVER.md#revision-4-implementation-first-correction-t075-t081),
completed through revision 5's T082-T086 and the review above.
That sequence supersedes historical next-step instructions below. Plan 39 is the
current source contract; installed VM driver 0.4.92.1 ran plan 34-37 tools. The Q: case
is a focused pass, not the forced-ordering gate. A07-A09 remain PARTIAL.
A01-A06 retain historical scoped passes, not certification of the changed bypass.

| New task / owner | Implementation status | Verification status | Benefit / next action |
|---|---|---|---|
| T075 / A07 | Installed candidate: resident-sector overlay without new paging read retention | Exact 0.4.92.1 Q: mapped/unbuffered and C: image bytes passed; forced partial/failure paths pending | Read the newest saved bytes, including partial cache hits; check exact VM ordering. |
| T076 / A07 | Installed candidate: range-targeted older-version drain and direct-write fence | Local plan-37 Q: observed isolated 1,536-byte old in-flight write, mapped overwrite, overlap wait and newest/guard bytes; fault/cancellation and exact-range attribution remain | Prevent old background writes from overwriting a newer save; prove exact forced order. |
| T077 / A07 | Installed candidate: cooperative paging read service and V7 routed outcomes | No forced paging/capacity/lower-wait dependency proof | Keep Windows paging responsive; check actual progress under pressure. |
| T078 / A08 | Installed: shared worker identity and per-case/final oracles; host contracts pass | Exact 0.4.92.1 Fast/Strict active image and final post-release oracles passed; actual paging-role transition and restart oracle remain | Ensure each Fast/Strict and restart result proves what it says. |
| T079 / A06a/A07-A08 | PARTIAL: Q: mapped/unbuffered case passed; plans 35-36 add paging-aware policy attribution; plan 37 observes a sparse old in-flight/mapped overlap | CI-built plan-37 full policy and observed overlap passed, all cleanly restored. Process-wide counters cannot prove exact range ownership; fault/cancel/dependency cases remain | Close remaining focused ordering paths without calling the observed overlap full proof. |
| T080 / A09/T067 | VERIFIED on installed 0.4.117.1 with plan-47 `system-app-session` (`QueueCache-Verify-20260925-122427-ef6a4774...`), 2026-09-25: deterministic 349 MiB BMP written and verified uncached, runtime-only C: Fast 1 GiB Deferred (10-minute age); the owner edited and saved it in Paint (saved 365,948,982 bytes at 12:32:59 UTC) and saw the edit in Photos. 527 one-second samples: cache always enabled, 0 errors, 429 MB accepted, 421 MB dirty and 0 drained when the owner finished, so Photos read the edit while it existed only in RAM. The saved bytes hashed identically while cached and after flush/disable/release (SHA-256 3FF7922B...); no dump or bugcheck. Earlier: owner completed uncached and cached edits of the same baseline BMP and opened Photos | Successful app observation plus save timestamp/screenshot; no exact open interval, target-range pending proof or post-drain comparison. Dump capture disabled; Q: benchmark/cache also active | T086 finishes capture/post-drain comparison; preserve this non-reproduction without calling the BSOD fixed. |
| T081 / A09 | VERIFIED on installed 0.4.117.1 (`d4f1b5d`), 2026-09-25, one cycle. Runtime-only C: Fast 1 GiB, Deferred, 1 h dirty age; plan-46 tools. `system-files` (`QueueCache-Verify-20260925-115651-c08aa260...`, oracle on Q:) accepted 71 MB for the owned file; `system-paging-recognition` under the active cache committed 3712 MiB against 3449 MiB available with a fixed 4 GiB pagefile: 2626/2908 paging reads/writes, 340 paging-file requests recognised, 0 misses, cache healthy. Normal restart with 104,268,800 dirty bytes, 0 drained (`pre-reboot-b.json`); no bugcheck/41/6008 events or dump; `system-post-restart` (`QueueCache-Verify-20260925-115957-b5147e91...`) matched every byte. **Found and fixed on the way:** on 0.4.111.1 the same restart bugchecked 0x7A (`STATUS_DEVICE_NOT_READY` page-in of win32kbase for exiting dwm.exe) because the post-shutdown suspended state failed later requests; the owned file still matched (drain finished first). Earlier 512 MiB system-managed pagefile could not produce pagefile traffic under the commit cap. **Saved-profile cycles (same day, same build, plan-46 tools):** C: saved Fast 1 GiB Deferred profile (Q: profile removed for memory headroom). Four cycles of `system-files` + `system-paging-recognition` (3712-3904 MiB committed against 3401-3632 MiB available; 72-378 paging-file requests recognised, 0 misses) + normal restart with 94-137 MB dirty + startup-task restore + `system-post-restart`: every cycle restored C: active and matched every byte (post-restart runs `...-130949-3bed78d2...`, `...-131221-f5e5d328...`, `...-131505-a70f16bb...`, `...-131737-cbaa28a2...`). The first saved-profile attempt instead hung on "Restarting" for 20+ minutes (83 MB dirty) and was reset: its C: writes since the previous boot were lost (expected Fast volatility), `chkdsk C: /scan` clean, cause unknown, no dump. Kernel dump plus NMI crash are now configured for a recurrence. | One VM. **Hang diagnosed 2026-09-26:** installed 0.4.124.1 (`a4238d0`, driver SHA-256 `DE421344...69C7`), same saved profile with a 1 h age, host-driven restart soak running `system-files` + `system-paging-recognition` + restart + `system-post-restart`. Cycles 1-2 passed (70/82 s restarts, 96.6/110.0 MiB dirty, bytes matched). Cycle 3 (208.8 MiB dirty) hung; the owner injected an NMI. Kernel dump: shutdown's registry page-path teardown sent a paging usage notification; C:'s request worker waited in `OriginalIo` while ACPI's paged-out usage-notification handler needed a pagefile read queued behind that worker. **First fix INSUFFICIENT** (0.4.125.1, `42b5ddd`, driver SHA-256 `46B7BCF9...6329`): `OriginalIo` serviced queued paging reads while waiting on a forwarded PnP or shutdown request, but the fault occurs inside `IoCallDriver` on the worker thread, before that wait. Its soak passed 20/20 cycles only because ACPI's handler stayed resident: 87.7-110.6 MiB dirty at each restart, 69-73 MB of the owned file admitted, paging-file I/O recognised in every pressure run (3-40 requests, 0 misses), 40-41 s restarts, every `system-post-restart` byte check PASS, no 41/6008/1001 events or minidumps (`QueueCache-Verify-20260926-174811-d0f88434...` through `...-182008-b46d5faa...`). Under Driver Verifier standard checks (pass A Q: suites all PASS first) the first restart hung again; Verifier bugchecked 0xC4/0x115 with the identical `OriginalIo+0x98` stack. After each crash `chkdsk C: /scan` found no problems. **Second fix VERIFIED on installed 0.4.128.1** (`19b6192`, driver SHA-256 `D8DFD196...854D`): PnP/shutdown lower calls run from a preallocated work item (`QcForwardOffWorker`, compile-time checked) while the worker services paging reads. Under Driver Verifier standard checks the soak passed 20/20 cycles: 94.8-133.9 MiB dirty at each restart, 20/20 `system-file-create`, `system-paging-recognition` (paging-file I/O every cycle) and `system-file-verify` PASS, 0 events 41/6008/1001 (runs from `QueueCache-Verify-20260926-2107...` to `...-2208...` in `Q:\QueueCache-Soak`; cycles 17-20 driven after a controller restart, cycle 16's post-restart check run manually). **Strict restart VERIFIED** on the same build under Driver Verifier: saved C: Strict 1 GiB Deferred profile, 10/10 cycles, 70.0-72.6 MB of the owned file admitted, 0-6.5 MiB dirty at restart (Strict flushes drained it), paging-file I/O every cycle, every post-restart byte matched (`~/QueueCache-Evidence/soak-logs/soak-0.4.128.1-strict-verifier.log`). Sleep/resume, hibernate and Fast Startup are unavailable on this VM (`powercfg /a`). | Sleep/resume and hibernate on a VM with S3/S4 enabled (owner-side hypervisor change). |

### Revision 5 follow-up implementation status

| Task | Implementation | Verification / next concrete result |
|---|---|---|
| T082 / A07 | INSTALLED (0.4.99.1 offload; 0.4.101.1 current): top-level and service-lane paging reads needing lower I/O run on a per-disk paging-read thread with exact-version pins; writes wait only for overlapping offloads; destructive requests wait for all; the fence forces only its overlap and unrelated batches keep policy/size. Remaining synchronous fallback: reads over 1 MiB, a full 64-entry table, and service during destructive/control requests. | FORCED DEPENDENCY PASSED on 0.4.101.1 (run `QueueCache-Verify-20260924-214451-6058809968374c149677a21997018845`): with a 32 MiB writer capacity-blocked behind a 16 MiB cache, 4 uncached page faults returned correct bytes (slowest 0.8 ms), with 3/3 offloaded paging reads completing while the writer was still blocked. Remaining: slow-lower-read page-in, memory-pressure and C: paging-role evidence (A09). |
| T083 / A06a/A07-A08 | INSTALLED (0.4.101.1, verifier `1d6dbe3`): one-shot lab range gate at the owned block's disk offset holds a drain after its real lower completion; Diagnostics V9 sequences; later cached C. | PASSED on 0.4.101.1 (same run): gate sequence old submit 1 < lower completion 2 < direct paging write waiting 3 < old retirement 4 < direct submit 5 < direct completion 6; the mapped flush took 2,180 ms against a 2,000 ms hold; newest bytes B, then later cached C active and after release. The first plan-39 run (`...-213420-797a9cbe...`) stopped cleanly because the gate had been armed with a file offset; `1d6dbe3` resolves the disk offset. `policies` also completed on 0.4.101.1 (`...-214519-eb322f68...`). Fault orders PASSED on 0.4.106.1 (`ordering-faults` x5): a failed old drain stops the newer waiting paging write before submission and keeps its dirty version; a short last sparse run keeps the whole version; a cancelled capacity-blocked write admits nothing. Direct paging write failure VERIFIED (plans 49/50, driver 0.4.133.1 `e67dcc1` SHA-256 `ECBD7D10...8C57` with 0.4.134.1 tools, Driver Verifier standard): lab fault 10 reports a forced direct paging write as failed after it reached the disk; the mapped flush reported the error, the cache stayed healthy, and a later save matched after release, in two consecutive `ordering-faults` runs (`QueueCache-Verify-20260927-070632-3f1b61c2...`, `...-070650-7e4d94af...`; all six stages PASS). The earlier plan-49 run stopped INCOMPLETE on a Deferred recovery race in the test, fixed in plan 50. **Cancellation of an in-flight paging request: NOT REACHABLE, disposition by review (2026-09-27).** Windows' memory manager issues paging I/O and does not cancel it, and user mode cannot cancel another component's IRP. Queued paging reads use the same cancel-safe queue path that `cancel-capacity-blocked-write` exercises; offloaded paging reads deliberately have no cancel routine and `Read` rejects `irp->Cancel` at start. No maintained scenario can reach this path; revisit only with a kernel test driver. |
| T084 / A08 | INSTALLED and VERIFIED (plan 38, 0.4.99.1+): driver records each lower attempt's issuing path (V8); byte PASS and zero-lower-I/O verdicts are separate checks; generated writes/flushes fail, forwarded non-paging I/O is SKIP (unproven, makes the run incomplete), forwarded paging I/O is attributed to other activity. Drain-trigger timing uses generated writes. Without V8 any lower attempt is SKIP. | VERIFIED on installed 0.4.99.1: all 13 zero-lower-I/O checks in `policies`/`pressure` PASSED with zero generated/non-paging attempts; each window's single extra write was a forwarded paging write. Per-request identity is still not recorded; a non-paging request from another process yields SKIP, never PASS. Runtime counter wording now says device-wide. |
| T085 / A07/A11 | IMPLEMENTED; Q: and C: recognition VERIFIED on installed 0.4.111.1 (`850c5ce`), plans 43-46 ([T085 design](T085_APPLICATION_CACHING_DESIGN.md)): paging-marked application traffic (originating file object found, not `FsRtlIsPagingFile`, not force-direct) uses normal RAM admission; paging files and unknown origin stay direct. Cache payload moved from nonpaged pool to page-backed MDL slabs. 0.4.110.1 used only the current-stack file object, found none below the volume (Q: 668, C: 52897 `NoFileObject`) and admitted nothing; 0.4.111.1 takes the request's original file object or the split request's master. **VM evidence (2026-09-25):** plan-43 `app-write-profile` PASS (`QueueCache-Verify-20260925-091216-3904671a...`): 256 of 256 MiB admitted and 0 forwarded in every mode; buffered-flush application wait 115 ms (was 9.1 s), mapped 219 ms (was 3.7 s); 1 GiB Configure added 21.7 MiB nonpaged pool; bytes matched after release. Plan-46 guarded C: `system-paging-recognition` PASS (`QueueCache-Verify-20260925-112121-52ac4b52...`): pagefile.sys and swapfile.sys reference extents, 4160 MiB bounded pressure, 59 paging-file requests recognised, 0 without file object, 0 reference misses. Regression on the same driver: `ordering-faults` (plan 43), `policies` and `pressure` (plan 44), `paging-coherence` (plan 45) PASS. Plan 44/45 test changes: admitted NTFS metadata/zero-fill write-back now shares drain intervals, so owned files are written fully first. Remaining: read misses are not retained (the budget stays fixed by design, 2026-09-27), and active C: application caching needs T081 pressure/restart proof. Before the change: all paging-marked writes bypass new RAM admission and misses are not retained. Measured on 0.4.106.1 with plan-41 `app-write-profile` (`QueueCache-Verify-20260925-062302-a0513f1b...`): 0 of 256 MiB admitted in buffered-close, buffered-flush (9.1 s application wait) and mapped modes; all arrived as forwarded paging writes | Characterize and implement supported ordinary buffered/mapped application caching after progress repair. |
| T086 / A09 | CAPTURE IMPLEMENTED (plan 47 `system-app-session`, see T080 evidence); single session, runtime-only profile. Earlier: successful owner Paint/Photos observation recorded; no capture or missing post-drain/restart/pressure results | Restore capture readiness and complete those separate checks with exact file/environment evidence. |
| T087 / docs | COMPLETE in planning revision 5 | Current docs reconciled; code/output verdict changes remain T084. |

Implement T075-T077 as one coherent driver slice with narrow tests; include T078,
build/package, then run T079 before T080/T081. No broad suite or performance
matrix before the correctness fix. No whole-cache barrier per paging request,
blanket old-code rollback, C: activation ban, softened oracle, or bigger timeout
as a substitute. Remaining registration/power/recovery tasks keep their original
release gates; unavailable VM power modes do not block the bounded app experiment.

Plan-31 evidence limitations (preserve original run verdicts):

- The image writer/reader used unbuffered deterministic I/O, not Paint, Photos,
  mapping or decoding, and did not establish image-range dirty state at open.
- Both modes checked bytes while active; final restoration selected only the last
  passed image (Strict). Fast lacks its own post-release disk-byte proof.
- Image cases disabled retention/promotion, unlike normal product defaults.
- Restart preparation created the 64 MiB file with caching inactive. It proves
  pre-existing bytes and startup usability, not persistence of cached new writes.
  Both recorded targets already had `IsPaging=true`, so no role change was tested.
- Preflight allows paging-role change; both actual scenario workers still use
  full-record target equality. T078 must repair all consumers, not just the helper.
- Zero reserve/serviced-miss counters and mapping/capacity counters behind the
  early bypass cannot demonstrate paging completion or absence of a dependency.
- The 0.4.83.1 process failure did not recur in the short 0.4.87.1 observations;
  its root cause was not captured. Do not call it a diagnosed/closed defect.

### Earlier next actions (historical; revision 5 above takes precedence)

Planning revision 3, 2026-09-23: the owner prioritizes reproducing and fixing the
reported large-BMP/Paint/Photos BSOD. Recent iterations mostly advanced the
verification framework. The earlier 64 MiB uncached C: file baseline is useful
and complete for its scope, but has not reproduced or diagnosed T067. The detailed immediate sequence
in the [handover](PRIVATE_ALPHA_IMPLEMENTATION_HANDOVER.md#revision-4-implementation-first-correction-t075-t081)
now governs execution. Existing A/T IDs and release gates remain. Plan 21 advances
only the A08 reproduction contract; it does not promote A08 or resolve T067.

1. **Capture and recovery readiness (T067/T032/T054).** Check dump/pagefile/free
   space and retrieval after failed boot; record exact loaded build/symbols,
   guest RAM, cache settings and application versions. Reuse the owner's
   snapshot/console confirmation and finish the concrete independent recovery
   prerequisite. No historical dump/Event 1001 survived the inspected snapshot;
   do not wait for unavailable old evidence before investigating current code.
2. **Focused driver investigation (T067/T023/T052/T053/T068).** Trace memory and
   buffer ownership, paging/mapped-file I/O, overlapping drain/overwrite,
   cancellation and activation ordering. Record code locations, confirmed
   defects versus hypotheses, and focused tests. Fix concrete findings; use Q:
   for controlled fault/order checks where possible. The current candidate adds
   per-type usage-path telemetry because the combined count cannot explain two
   persistent C: registrations. Framework repairs alone
   do not diagnose the historical BSOD.
3. **Finite active-C: blocker list (T024-T027/T030-T031/T054).** Identify and
   implement the exact safe activation, reachable-path, memory-budget,
   error/persistence and recovery requirements for the proposed reproduction.
   Each blocker needs an owning code path and an exit check. Required ordering,
   lifetime and notification proof remains; do not strip restrictions or demand
   completion of every A06a/A08/A10/production task before the experiment.
4. **Staged reproduction (T067/T029/T033-T035).** Perform the recorded roughly
   350 MB BMP/Paint/save/Photos workflow with caching off, then with active C:
   caching after the preceding prerequisites. Record actual image size, memory
   headroom, settings and timestamps; compare opening promptly after save with
   opening after explicit drain. The existing 64 MiB file check is not this
   large-image baseline. Plan 21 now supplies matching uncached and active 349 MiB
   cases, but both must reject C: until the usage-path result is understood. Preserve
   independent deterministic byte checks and
   distinguish them from application-generated image bytes.
5. **Evidence to fix (T067/T035-T037).** Preserve dumps/logs/symbols before
   reinstall or rollback; diagnose the initial crash separately from later
   lost-write damage. Repair and retest the triggering path. A non-reproduction
   records its conditions and next discriminating test; it does not close T067.
   Unknown historical cause blocks readiness, not controlled investigation.

Defer T050 tuning (keep shipped parallelism 1), general A08 framework expansion,
unrelated cleanup and broad qualification while this investigation is active.
Add verification code only for a named reproduction/capture/regression need in
the existing runner. Reuse completed evidence unless a relevant change requires
retesting. At each handoff, show the remaining concrete activation blockers and
the full A01-A16 table with changed-this-run marks and plain-language benefits.
After investigation/fixes, resume remaining alpha tasks and A13-A16 production
qualification; their unresolved requirements have not been waived.

### Focused T067 progress: 2026-09-23

- The inspected snapshot contains no historical minidump, `MEMORY.DMP` or BugCheck
  event, so the old crash cannot be diagnosed from surviving evidence.
- The current VM has 8 GiB RAM, a 100 GiB C: disk and separate 200 GiB Q: disk.
  Installed 0.4.70.1 reports C: disabled and clean. Paint is 11.2605.81.0 and
  Photos is 2026.11080.24002.0.
- Three ordinary Q: pagefile attempts (10 GiB, 8 GiB and 4 GiB) all fell back to
  a temporary 7.75 GiB C: pagefile without a useful Windows error event. A Q:
  dedicated dump file registered Q: but still left two combined registrations
  on C:. With all pagefiles absent, crash dumping disabled and hibernation/Fast
  Startup unavailable, C: still reports count 2 while Q: reports 0.
- Therefore pagefile relocation is not treated as the activation fix. Exact
  installed 0.4.78.1 proved the two counts are successful paging registrations,
  and exact installed 0.4.79.1 proved the matching not-disableable PnP policy.
  Plan 25 observes actual paging reads/writes in pass-through before any active
  caching policy is chosen; activation remains blocked by design.
- A historical 4 GiB cache on this 8 GiB guest could have left too little memory
  for Windows plus a decoded 350 MiB image. This remains a plausible hypothesis,
  not a diagnosed cause. New management admission preserves at least 2 GiB or
  25% of physical RAM, and the reproduction is capped at 512 MiB.
- Crash capture and active C: currently conflict: Windows' dump configuration
  keeps C: in the protected usage path, while disabling dumps removes that
  evidence source without clearing the unexplained count. The snapshot/console
  remains recovery protection, but a cached crash must not be triggered until
  the per-type result determines the next safe step.

Plan 25 records real `IRP_PAGING_IO` request/byte counts before routing selection,
including disabled pass-through. The maintained 349 MiB baseline saves immutable
before/after diagnostics and their delta. The process-wide window may include
unrelated Windows traffic; it is a discriminator for nonpageable forward progress
and dirty-overlap ordering, not workload-only attribution or C: enablement.

That discriminator completed on exact installed 0.4.80.1 in run
`QueueCache-Verify-20260923-172845-86c7a49e6f1445178f9ca455390a8ae9`.
The case passed all 365,953,024 deterministic bytes with caching disabled. During
the 13.5-second window the device saw 983 paging reads (29,969,408 bytes) and 427
paging writes (5,344,256 bytes). Paging is therefore normal bidirectional traffic
on this C: path; a whole-cache barrier or disable on every paging request is not a
viable design.

Plan 26 implemented the first bounded active candidate. Ordinary writes cannot
consume a paging reserve of up to 64 MiB; paging MDLs request high-priority system
mapping; and a non-overlapping paging-read miss can use the lower device while the
sole foreground owner is capacity-blocked. A distinct enable action is accepted
only for the recoverable verifier in that build. Exact 0.4.82.1 then passed the
active 349 MiB workload with a 512 MiB Fast cache: all bytes matched twice,
366,888,960 bytes were accepted, the 64 MiB reserve covered observed paging, and
there were zero mapping failures or paging capacity waits. Restoration succeeded.

Plan 27 promotes that mechanism into the product. Normal Enable and public Apply
accept boot/system/paging targets. The legacy action value remains only as an ABI-
compatible alias. The original implementation drained only dirty excess needed
to establish its reserve and did not disable the cache. Device
power-down drains and remembers active state; successful D0 resume restores it.
The maintained active suite now runs Fast and Strict separately through public
Apply and releases C: cleanly between cases. The first exact 0.4.83.1 run proved
Fast but revealed that Strict reused Fast's retained image/oracle path; Plan 28
assigns unique immutable artifacts to each case.

The subsequent 0.4.83.1 saved-profile reboot with a fixed 4 GiB C: pagefile was
not safe: qcache/CoreCLR repeatedly faulted with stack overflow/access violations,
and an unrelated Edge updater also faulted. Removing the profile, restoring the
pagefile/dump settings and rebooting returned the VM to stable pass-through. The
candidate correction does not cache `IRP_PAGING_IO` data at all. It keeps those
requests ordered through the foreground worker and establishes a one-time drain,
lower flush and clean-cache invalidation when a new paging/hibernation/dump path
registers, while leaving cache routing enabled. Exact packaged retest is mandatory.

### Normal C: activation convergence (decision 2026-09-23)

This is required product work, not an optional relaxation of testing standards:

| Task | Required change | Exit evidence |
|---|---|---|
| T070 / A07 | Make ordinary kernel Enable select the paging-capable policy and retire the verifier-only activation split. | IMPLEMENTED in plan 27; legacy action 12 is an identical compatibility alias. Exact 0.4.87.1 normal Fast/Strict Apply proof passed. |
| T071 / A07 | Remove boot/system/paging rejection from public Apply while retaining disk identity, available-memory, policy, live-driver and error-state validation. | IMPLEMENTED; CLI and desktop share public Apply and the attach message has no C:-unsupported warning. Exact installed CLI/public-path proof passed; interactive desktop click remains A09 UI coverage. |
| T072 / A07-A09 | Do not disable a healthy active cache merely because a system usage path registers. Define ordered paging, hibernation/Fast Startup and crash-dump behavior. | REVISED after 0.4.83.1: paging bypass plus registration boundary. Exact fixed-pagefile/dump saved-startup proof passed on 0.4.87.1; active dynamic registration and unavailable hibernation/Fast Startup remain. |
| T073 / A09 | Enable saved C: profiles through the same identity-bound startup restore used by other disks. | IMPLEMENTED and exact 0.4.87.1 startup task completed successfully with active-state and post-restart byte proof. |
| T074 / A08-A09 | Keep system-test identity, off-target evidence, lease and recovery controls, but test the public path as well as the lower-level candidate. | IMPLEMENTED; exact Plan-31 Fast/Strict public Apply and per-case release completed, while the broader lifecycle campaign remains A09. |

### Plan-31 exact C: checkpoint: 2026-09-24

Exact installed 0.4.87.1 came from `7ff1381`; the loaded driver was
`QueueCache-0.4.87.1-09553FB6327C.sys`, SHA-256
`09553FB6327CF992EC2175B17AC07F5E2C329BCEDC78CA2EC4BB366B709F7DC3`.
Run `QueueCache-Verify-20260924-045431-b85c9a03afc94e6b9c8f7d1ffa501d99`
completed 2/2. Fast and Strict accepted 365,977,600 and 365,957,120 bytes,
respectively; each matched the complete 349 MiB off-target oracle before and
after administrative flush, recorded zero paging mapping failures/capacity waits,
zero paging reads serviced from RAM and zero paging reserve, then released C:
cleanly. Final system restoration passed.

A fresh 64 MiB create run
`QueueCache-Verify-20260924-045548-c469270475044086961e3afd8135392f`
completed before applying a saved 512 MiB Fast profile. After configuring a fixed
4 GiB C: pagefile plus kernel-dump target and rebooting, the startup task completed
successfully and the cache was active/routed with `Paging=4`, `Dump=1` and no
driver error. Post-restart run
`QueueCache-Verify-20260924-045930-94c0721be30a4e6fb7ad670cd8b6bf9d`
matched every owned byte. Repeated observations showed continuing paging traffic,
zero mapping failures/capacity waits/serviced paging misses and no new qcache,
CoreCLR, Edge or other application fault. Two WER submissions referenced an old
September 11 ResourceTimeout dump and were not current crashes.

Cleanup used normal `qcache policy remove C:`, restored the recorded pagefile and
dump values, and rebooted. The final state is C: disabled/released/error-free,
profiles `[]`, manual pagefile setting `C:\pagefile.sys 0 0` with its usual
512 MiB allocation, crash dumping disabled with Q: paths restored, and no new
Application Error, BugCheck or critical kernel event. Hibernation/Fast Startup
remain unavailable in this VM; dynamic in-path registration while already active,
pending-dirty restart, low-memory/fault/cancellation and the interactive app
workflow are not claimed by this checkpoint.

Disk-role labels may remain factual inventory information. They must not disable
the C: controls or imply that the user accepts an unknown correctness defect.
Fast's normal volatile-data acknowledgement remains because it describes the
chosen durability contract equally on C: and data disks.

The fresh complete 72-case baseline requirement still applies before a
performance-affecting driver edit; focused comparisons guide iterations. A read-only
audit or documentation change does not require a broad run. Safety failures preempt
benchmarking. A06a is new work; its addition preserves A01-A06's original verdicts
while preventing those scoped passes from being treated as general qualification.

## Firm contract

In explicitly selected volatile Fast/deferred mode, while the device remains
present and sufficient admission RAM is available, acknowledging a valid supported
write must not depend on lower-device reads, writes or flushes. Partial writes,
background activity and implementation limitations are NOT exceptions. Own the
bytes and ordering metadata in RAM before acknowledgement. Background scheduling
must not impose a global foreground drain. Priority: write admission, cached reads,
then background drain, with bounded fairness for other work.

Named boundaries, not an open-ended "unless unavoidable" escape clause:

* Capacity exhausted by pending/versioned data: wait, reject, or acquire memory;
  never overwrite acknowledged data. Explicit Fixed write quotas remain meaningful.
* Explicit Flush now and Strict durability requests must wait for persistence;
  this need not stop all later unrelated write admission.
* Cache removal/shutdown must persist, retain elsewhere or explicitly discard
  pending data. Never silently free acknowledged volatile bytes.
* Device removal/failure cannot be disguised as success; continuing an offline
  Windows volume is a separate architecture, not promised by a storage filter.
* Cold reads may require disk access but must not force unrelated dirty data out.

One-hour deferral means no scheduled lower writes before first-dirty age reaches
one hour, except capacity, explicit durability and lifecycle boundaries. Idle and
high-watermark triggers must not secretly shorten this policy. A maximum dirty-age
trigger is not a deadline by which slow storage has necessarily persisted data.
Fast RAM acknowledgement is explicitly volatile, never equivalent to Strict.

## Ordered worklist

Percentages are low-confidence hypotheses for the specified workload, NOT promises,
not additive, and include zero where the path might not occur. Recovery envelopes
use illustrative observed slow/healthy numbers rather than guaranteed causal gains.

| # | Area / change | Expected improvement / metric | Implementation status | Verification status / required checks |
|---|---|---|---|---|
| 1 | Barrier reason/size/alignment attribution and durable observer-startup breadcrumbs | 0% directly; enables attribution | Implemented in f67080f / 0.4.41.1: live lower-attempt counters and nine barrier reasons with last request details; worker startup breadcrumbs, primary timeout preservation, preparation deadline and restoration mismatch evidence | Debug/Release CI and host contracts passed; plan-8 VM admission and positive lower-counter checks passed. Restoration mismatch snapshots identified late dirty bytes in both focused plan-9 baseline runs; separate recoveries passed. No automatic performance acceptance |
| 2 | Explicit Deferred policy with one-hour bounds, no idle/watermark early drain | 0% intrinsic copy gain; removes early interference | Implemented: driver/management/CLI/UI, capability-gated; sector-valid partial admission deployed | Native truth table and managed/UI checks; short deferred sector-admission VM checks passed; real one-hour soak pending |
| 3 | Cache sector-valid partial writes without reading disk or draining whole cache | 0–260% affected Q1 recovery envelope (~22 to ~80 MB/s); healthy path may gain 0% | Sector ownership deployed as e8b37be / 0.4.40.1; plan-8 maintained scenario adds partial/full admission attempt checks and positive drain/disk-read counter checks | Plan-11 VM zero-attempt admission and byte oracles passed again on 0.4.57.1 at parallelism 1/2/4, retention off/on. Lower-write failure/retry and persisted-hash recovery passed. Bounded lower-I/O gate, deterministic sparse failure and allocation/cancellation lifetime checks remain open; see T049/T052/T053/T056 |
| 4 | Zero-length/oversized/quota fallback handling | 0–20% affected cases; ordinary fitting 4 KiB often 0% | Partial: valid zero-length writes return without draining; oversized/quota work pending | Native compile; VM no-I/O and request/quota/failure/cancel tests pending |
| 5 | Proven-safe observation/query fences | Isolated writes ~0%; affected hot-reader traffic 0–100%+ | Hotplug GET allowlist implemented in 4dacd5e | Native compile and VM policy retention checks across metadata/discovery passed; focused performance comparison pending; SET remains fenced |
| 6 | Independent drain versions, bounded copy/metadata locking, transient reserves | 0–30% writes during draining | Partial: existing pins and unlocked copies; further work pending | VM full/partial overwrite byte oracles passed with parallelism 1/2/4, retention off/on and actual in-flight observations. Coalesced lower-write failure retained dirty data and passed retry/persisted-hash recovery on 0.4.57.1; deterministic allocation, cancellation and remaining lifetime checks stay open |
| 7 | Independent ready-request service around capacity waits/fences | 0–50%+ mixed throughput; unstalled Q1 little gain | Partial: cooperative read lane exists; general admission work pending | Deep queues, ordering, cancel/reinsert, starvation |
| 8 | Admission budget clarity and Automatic clean-space borrowing | 0–20% under pressure; fitting cases ~0% | Pending | Fixed 0/50/100%, Automatic, transient versions, multi-disk budget |
| 9 | Per-4KiB lookup/publication/synchronization overhead | Hypothesis 5–25% CPU-limited; 0% if waits dominate | b63e14b / 0.4.42.1: single-block slot reuse. 360ab9a, deployed in 0.4.45.1: policy-gated foreground wakes. 948ea1c / 0.4.46.1: suppress redundant foreground wakes while the sole drainer is busy | 0.4.46.1 loaded/hash verified; native/host checks, signed CI and VM policy/admission/byte oracles passed. Three timing-off repeats: Q1 median +9.74%, Q32 +172.96%; p99 improved. Qualified focused gain, not full acceptance: original late-dirty restoration failures remain, separate recoveries passed; full matrix and remaining lifetime/fault checks open |
| 10 | Range-aware TRIM instead of broad drain/in-flight waits | Isolated writes ~0%; concurrent delete workloads 0–50%+ | Range-aware implementation pending; maintained plan-6 driver-independent file probe added after plan-5 routing comparison | Matched attached/unfiltered VM probes both return Win32 326; Q: live stack without QueueCache verified. Same driver/configuration restored and verified after reboot. Partial ranges, reuse, overlapping old writes, malformed/failed requests remain unverified |
| 11 | Cutoff flush, safe live policy changes, transactional resize | Isolated writes 0%; concurrent workloads 0–50%+ | Pending | Exact durable cutoff, concurrent writes, Strict flush, failure/cancel/resize |
| 12 | Foreground cold-read versus drain scheduling | 0–500% mixed recovery envelope; no RAM-only promise | Plan 11 adds the bounded fitting-write/cached-read case under normal Idle draining; cold-miss scheduling remains pending | Plan-11 VM case passed on 0.4.57.1: 612,810 serialized 64 KiB write/read pairs in 60 seconds, zero capacity waits, unchanged initial lower attempts, exact persisted bytes and nonzero background progress. Mixed cold misses and sustained capacity pressure remain open |
| 13 | Indexed ready selection / independent-range workers if still justified | 0–100%+ high QD; Q1 usually 0% | Deferred until remaining profiles justify redesign | Range ordering, barriers, cancellation, faults, cross-thread lifetime |
| 14 | Power/shutdown/PnP/removal boundaries | 0% throughput; reliability | Pending audit | Dedicated disposable VM lifecycle tests; no automatic destructive recovery |
| 15 | Windowed UI statistics, trigger/wait visibility and faithful evidence windows | 0% driver gain; trustworthy analysis | Partial: repeatable write suite/report exists; plan-10 restoration records volume/flush/disable boundaries and closes late admission before restoring settings. Plan 11 records the sustained policy case's foreground latency, capacity waits, bounded occupancy and background progress. Performance V3 appends cumulative drain selection/copy/retirement timings while preserving V1/V2 wire responses; UI work pending | Host sequencing/failure contracts and V1/V2/V3 decode checks passed; native Debug/Release builds passed. V3 focused attribution and Q1/Q32 cleanup regressions completed; the plan-11 policy case passed on 0.4.57.1. UI/CLI parity, sample staleness and interval/lifetime labels remain pending |

## Historical optimization order (2026-09-19)

The current release sequence above and handover revision 3 supersede this ordering.
Preserve the rationale and evidence requirements below. Do not restart completed
attribution/default work or require every speculative optimization before alpha.

The row numbers above are stable work-item IDs, not a requirement to finish every
diagnostic before implementing anything. Use the following batches to prioritize
resident small-write speed first, then loaded/mixed workloads. Potential gains are
not measured rankings. A reproducible data-loss, ordering or lifecycle failure
preempts this order.

| Priority | Existing IDs | Deliverable and stop condition |
|---|---|---|
| 1 | 1, measurement portion of 15 | Add the smallest versioned barrier/lower-I/O-attempt counters needed to identify a foreground stall and prove admission. Fix observer startup only if the focused reproduction still fails; do not weaken readiness. Stop investigating once a test identifies the controlling wait/path. |
| 2 | 3, safety portion of 6 | Finish accepting the deployed partial-write implementation: bounded lower-I/O gate and maintained RAM-admission scenario; deterministic old/new bytes, sparse failure/retry, pins/allocation/cancel checks. Repair failures in this slice immediately rather than expand the audit. Preserve Strict/explicit-flush checks. |
| 3 | 2 | Verify Deferred trigger boundaries and first-dirty age; run one real one-hour soak on the VM with no competing workload. Local implementation work may continue during the soak. |
| 4 | 5, 4, 8 | Remove confirmed incidental foreground waits: proven-safe queries, fitting-request fallback problems, and Automatic clean-space borrowing. Respect Fixed quotas; requests larger than available RAM still need backpressure. Implement one cause per comparison. |
| 5 | 9 | Optimize measured per-request CPU/lookup/publication overhead on healthy fitting 4 KiB writes. If waits dominate instead, move directly to their owning item; do not speculate about lock-free rewrites. |
| 6 | remaining 6, 12, 7 | Improve writes while draining and mixed/cold-read latency: bounded version reserves/locking, lower-I/O scheduling, then independent ready-request service. Require observed contention before adding workers or scheduler complexity. |
| 7 | 11 | Cutoff flush and safe live configuration/resize, with exact durability and cancellation tests. Do not trade Strict semantics for throughput. |
| 8 | 10 | Range-aware TRIM once delete/TRIM stalls are reproduced and a supported oracle is available. Current Win32 326 is a verification gap, not justification to rewrite the kernel path. No detach/reboot/raw mounted-volume TRIM as automatic investigation. |
| 9 | remaining 15 | Finish windowed UI statistics and wait/trigger visibility. Minimal trustworthy measurement labels belong in priority 1; cosmetic/UI expansion does not block driver improvements. |
| 10 | 14 | Dedicated full/lifecycle acceptance milestone. Relevant lifecycle/teardown safety checks also accompany each earlier ownership/scheduling change; this is not permission to defer known safety defects. |
| 11 | 13, conditional | Indexed selection or independent-range workers only if residual high-QD profiles justify them after the simpler improvements. Otherwise leave this item deferred. |

Keep the loop small: one local hypothesis, one discriminating check, one scoped
implementation, host checks, then the focused VM regression. Reuse the maintained
runner, exact run IDs and this tracker; no new private orchestration or repeated
whole-repository audits. Record only changed status, evidence and the next blocker.
Do not rerun broad matrices after documentation-only or diagnostic-only changes.

Before the first further performance-affecting edit, freeze the accepted current
build and obtain a fresh complete 72-case `write-performance --budget-mib 2048`
baseline with the same DiskSpd hash and three repetitions. The old 31-case batch
cannot substitute for it. Use focused matched cases during iteration (add maintained
selection if needed), and repeat the complete matrix at performance milestones.
Never compare the two TRIM diagnostic elapsed times as a performance benchmark.
Investigate repeatable >5% healthy-throughput or >10% tail regressions beyond VM
spread. Attribution and observed admission proof are now deployed. The first
single-slot lookup experiment below did not establish a speedup. The deployed
policy-gated wake candidate passed focused correctness; clean-observer attribution
identified renewed contention once draining starts. Busy-drainer candidate 948ea1c
is deployed as hash-verified 0.4.46.1, passed focused correctness, and improved
all three timing-off Q1/Q32 samples beyond their baseline ranges. Preserve this
checkpoint. The plan-10 restoration sequence and V3 attribution have now closed
the recurring late-dirty cleanup blocker on the current driver without weakening
its predicate. Next set coherent alpha defaults, then complete the remaining
bounded-gate/fault/lifetime checks and full-matrix milestone. Residual Q1 overhead and the timing-on/off Q32 discrepancy
need attribution before another synchronization change; the timing-on score is
not a substitute for the timing-off comparison.
Timing-on data is diagnostic, not directly comparable to timing-off scores. Do not
repeat the whole matrix, remove the slow sample, or add scheduler complexity without
an identified controlling cost. Remaining bounded-gate/fault/lifetime checks stay open.

## Execution and verification gates

Implement data-path/policy changes first with local correctness checks; expand
performance scenarios afterward. Do not deploy an unverified kernel rewrite just
to collect a speed number. Keep commits independently reviewable.

1. Native compile/static policy/storage checks and managed contract tests accompany
   each implementation. Desktop tests accompany frontend changes.
2. Build a maintained RAM-admission scenario, not another private orchestration
   script: online identity, bounded lower-data-I/O gate, enough RAM and no prior
   in-flight work. Supported writes and cached reads complete with ZERO lower read,
   write or flush attempts. Filesystem cold metadata reads are a separate case.
3. Verify deterministic exact bytes from RAM and again after drain/drop-clean.
   Include all sector offsets, overlapping writes, in-flight old/new versions,
   retention on/off, cancellation, faults, retry, TRIM/reuse, allocation failure.
4. Preserve Strict and explicit-flush durability. Exercise capacity boundary and
   fairness: prioritization must not hide starvation or overwrite pending data.
5. Reuse `qcache developer verify --suite write-performance --budget-mib 2048`
   with the same DiskSpd hash. Fresh complete 72-case baseline, three repeats,
   alternating modes, timing on/off, Q1/Q32 and sequential Q1/Q8. Never merge the
   incomplete prior run into complete-run medians. Collect tail latency and
   zero-completion windows, CPU, memory overhead and admitted versus drained bytes.
6. Focused flush-interference/cold-read tests after relevant changes; full suite
   and lifecycle at milestones. Investigate repeatable >5% healthy-throughput loss
   or >10% tail regression beyond measured VM spread. No manufactured PASS from
   missing data, relaxed timeouts or weaker durability.

## Evidence checkpoint

### Private-alpha A01 handover checkpoint: 2026-09-20

A01/T001-T003 is complete at HEAD
`838a63ee4a56f450eb51ff39b9f8176afd9c0a91`. The index was empty and the only
preserved candidate edits were the plan-10 restoration changes in
`VerificationPlan.cs`, `VerificationWorker.cs`, `VerificationRunnerTests.cs`,
`DEVELOPER_VERIFICATION.md` and this tracker. The host-safe management harness
passed. Exact retained Q32 evidence remains `COMPLETED`; exact Q1 evidence remains
`RESTORATION_FAILED` in the main cache flush before `after-cache-flush`. Its later
supported recovery is separately `RESTORED` and does not change the failed verdict.

The read-only elevated VM preflight found no QueueCache, DiskSpd or CrystalDiskMark
processes. Q: is Basic partition 2 on non-boot/non-system Disk 1. The running Boot
driver hashes to
`90C50E7AD991E3EB0E9D7FFFC38C5B1CEA80935124B2CDEB6C2560980DD65060`, and the
actual stack is `partmgr/qcachelab/disk/vioscsi`. Q: reported Active with the saved
enabled 2 GiB Fast/Idle configuration, zero dirty and in-flight bytes, and zero
error counters. No workload, cache mutation, install, reboot or C: action was
performed. A02 evidence and instrumentation follow below.

### Restoration boundary follow-up: 2026-09-20

Implementation: managed runner only, plan 10. Restoration now submits filesystem
buffers through the existing identity-checked volume handle, performs the main
cache flush, disables/drains remaining admissions, and reapplies saved settings.
Four state boundaries are retained. No kernel changes, retry-until-clean loop,
weakened zero-dirty/error predicate or extended restoration deadline. Disable
invalidates clean residency; this is outside the score window. Host-safe management
contracts cover ordering, late admissions and fail-fast propagation at all three
operations. Publish and editor diagnostics passed.

Verification used loaded 0.4.46.1 and the same CDM DiskSpd hash as above. All runs
are separate single-case probes under `ManualRuns/restore-plan10-20260920`, not a
performance comparison or complete matrix. Earlier candidate binaries are retained
in separate directories. Final runner `QueueCache.Developer.dll` SHA-256:
`B3CD2C29944D010F8B956272605A474B9446B6B011365758A630778737E71544`;
all 212 published files matched the VM copy.

| Probe / exact run ID | Result and interpretation |
|---|---|
| Volume then flush only: `QueueCache-Verify-20260920-005026-2a28d7936a134c3cbc8770c97d8540c0` | RESTORATION_FAILED, 12,288 dirty bytes. Accepted bytes increased by 53,248 across the volume-flush snapshots and another 12,288 across the cache-flush snapshots. These observations do not place admission inside the barrier; writes can resume after it returns, before the next snapshot. Volume flush alone is insufficient; the exact filesystem source is not proven. |
| Volume then disable only: `QueueCache-Verify-20260920-005613-c6391ccd1cea46d4a4d87075a4777d76` | RESTORATION_FAILED at 300-second deadline. No new admissions after disable; dirty bytes still decreased to 20,480 with 4,096 in flight in the final observer sample, errors zero. No deadlock claim or causal drain-speed conclusion. |
| Final Q32: `QueueCache-Verify-20260920-010716-391acebd187e435a85335857178a9aaf` | COMPLETED, 1/1 MEASURED. Main flush left 12,288 dirty bytes; disable reached zero dirty/in-flight with admission off. Saved enabled 2 GiB Fast/Idle settings, profiles, timing and error checks passed; restore took 100.9 seconds. |
| Final Q1: `QueueCache-Verify-20260920-012051-3eef2ddb4db84be8af7a4281a3f2fe08` | RESTORATION_FAILED, 1/1 MEASURED. Main flush exceeded 300 seconds before reaching disable. Final samples decreased from 3,170,304 to 2,818,048 dirty bytes, with no new admissions or errors. Separate slow-drain blocker, not a passing restoration. |

After confirming owned workers exited, supported separate recovery runs returned
RESTORED with clean original settings and no errors:
`QueueCache-Verify-20260920-005441-84a1088b19804a25acf14ece1df1c883`,
`QueueCache-Verify-20260920-010536-10449276690d4c3fa9ea06cc0a2bbdb9`, and
`QueueCache-Verify-20260920-013257-a43ef34f73ec42a38f3b62132f548fe5` respectively.
Original failed verdicts are unchanged. No full matrix, one-hour soak, new driver
installation or lifecycle/fault acceptance is claimed. Next isolate the slow main
drain before broader acceptance; the late-admission boundary is verified only in
the focused Q32 run and clean recoveries so far.

### Private-alpha A02 slow-drain attribution: 2026-09-20

T004 parsed the exact failed Q1 restoration trace
`QueueCache-Verify-20260920-012051-3eef2ddb4db84be8af7a4281a3f2fe08`.
The main flush drained 759,386,112 bytes in 309.614 seconds (2.3391 MiB/s) through
51,335 batches averaging 14,792.7 bytes. Accepted bytes did not grow during this
interval, errors remained zero, and progress continued without a long stall. This
is a 300-second deadline overrun, not evidence of deadlock. The later supported
recovery remains separate and does not change the failed verdict.

T005 compared three retained timing-enabled random-Q1 runs. They consistently
drained about 1.23-1.54 MiB/s with roughly 10-11 KiB per lower write. Existing
`LowerIoTicks` accounted for 46.6-49.2% of observed wall time. The supported local
hypothesis is therefore fragmented/random residency producing many small serialized
synchronous lower writes, with substantial remaining time in unmeasured drain
selection, payload copy, cache-lock reacquisition and retirement bookkeeping. The
evidence does not establish quadratic selection cost or justify a scheduler rewrite.

T006 adds only attribution counters, not a drain behavior change. Performance V3
appends cumulative QPC ticks for drain selection, copy and retirement; lower-I/O
timing now ends before retirement lock reacquisition so the phases do not overlap.
The 192-byte V1 and 408-byte V2 responses remain supported, and V3 is 432 bytes.
The host-safe management harness passed V1/V2 compatibility and explicit V3 value
decoding. Current-driver native Debug and Release builds passed with the repository
lab-write-cache flags. No driver was installed and no VM run was made, so phase
attribution and any optimization remain pending. The next discriminating check is
a focused timing-enabled random-Q1 drain with these counters; do not extend the
restoration deadline or alter durability/zero-dirty predicates.

### Private-alpha A02 completion: 2026-09-21

A02/T004-T008 is complete on signed build 0.4.51.1 from
`ec07e3663f375decfa5c68b75a633b88b68fd53c`. The loaded driver SHA-256 was
`738241F362DE8B1D2799C7F33688D6F9742658F57E49656E1ED9AF41EFD55605`; the
CDM DiskSpd SHA-256 remained
`7281BF6DA6C03797016EDDF2E8AAEC4C644AE893D403D57A030B7E2E14B61079`.
Preflight confirmed an elevated session, no competing workloads, clean Q: on
non-boot/non-system Disk 1, 512-byte logical/physical sectors and the saved 2 GiB
Fast/Idle configuration. No driver installation or reboot was performed.

The timing-enabled discriminating run
`QueueCache-Verify-20260921-124642-bed8a5e9d4694a58a4fcdd101bce9ea4`
completed and restored in 69.5 seconds. During the observed main drain it persisted
658,882,560 bytes through 53,892 lower writes in 69.476 seconds. V3 attributed
69.052 seconds to serialized lower I/O, versus 0.028 seconds selecting, 0.103
seconds copying and 0.041 seconds retiring. Lower completion therefore accounted
for about 99.4% of wall time with parallelism 1; there is no evidence here for a
selection/retirement optimization or timeout relaxation. The case score was
20,972.43 IOPS with 0.126 ms write p99, but timing-on is diagnostic and is not a
release throughput comparison. Telemetry contained 343 samples with a maximum
0.227-second gap.

Current-driver timing-off cleanup regressions also completed without changing the
300-second restoration deadline or zero-dirty/error predicate:

| Case / exact run ID | Result |
|---|---|
| Q1: `QueueCache-Verify-20260921-125419-3397469d5cc94c6f9e569ad2949e45f9` | COMPLETED, 1/1 MEASURED; 22,512.29 IOPS, 0.096 ms write p99; restoration completed in 57.0 seconds. |
| Q32: `QueueCache-Verify-20260921-125649-9def04fc12ca461a9d37aeb436e08d3a` | COMPLETED, 1/1 MEASURED; 90,389.01 IOPS, 0.161 ms write p99; restoration completed in 29.2 seconds. |

Both runs retained `FINISHED.txt`, status, summary, results, run log, worker
records, ready telemetry, interval files, raw XML and all restoration boundaries.
Filesystem flush -> cache flush -> Disable -> saved configuration reached zero
dirty/in-flight bytes with no error and restored the original enabled Fast/Idle
settings. These single repetitions close the cleanup regression, not the later
three-repeat performance acceptance matrix. Earlier failed runs retain their
original verdicts. The next dependency-ready step is A03.

### Private-alpha A03 implementation: 2026-09-21

New tasks now use one coherent Fast/Idle baseline in the native driver, managed
contract, CLI and desktop: 5,000 ms first-dirty age, 250 ms write-idle trigger,
40/80 write-pool watermarks, 256 KiB batches and one lower write. Fast still
requires explicit volatile-flush acceptance. Driver initialization remains
disabled, so installation alone does not activate a cache; startup restoration
applies only a validated, previously opted-in profile. State reconstruction and
saved-profile serialization preserve explicit existing Strict/Fast and drain
choices, including Eager.

Verification plan 11 extends the maintained `policies` worker rather than adding
a private script. Its 60-second 8 MiB hot-set case runs below a 64 MiB budget,
serializes deterministic writes and immediate cached reads, proves the first
admission has unchanged lower-attempt counters, and records p99/maximum foreground
latency, capacity waits, dirty occupancy and nonzero background drain progress.
Disable then provides an independent persisted-byte oracle. Host/native/UI checks
are separate from VM evidence.

### Private-alpha A03 verification: 2026-09-22

A03/T009-T011 is complete on the installed and rebooted 0.4.57.1 candidate from
`a07013f7f474b4b9018254fdb8b4ac4a4b2809bf`. The loaded driver was
`System32\drivers\QueueCache-0.4.57.1-A2C4BCB38F57.sys`, SHA-256
`A2C4BCB38F57C1D6D6185EC4E74451C8EABD4DF9F262AD965F8D30DE60233935F`.
Preflight confirmed an elevated session, the disposable non-boot/non-system
200 GiB Disk 1 / Q: target, 512-byte logical/physical sectors, no competing
QueueCache/DiskSpd/UI workload, cleared synthetic hooks and the saved 2 GiB
Fast/Idle profile with volatile flush acceptance.

Maintained plan-11 `policies` run
`QueueCache-Verify-20260921-220242-6fd1c8aa060140bc899cd8a775420cf9`
finished **COMPLETED**, 1/1 PASS, with restoration success. All 30 detailed checks
passed. The sustained case completed 612,810 serialized 64 KiB write/read pairs
over 60 seconds while Idle draining made progress. Write p99/max was
0.085/10.230 ms and read p99/max was 0.076/4.638 ms. It admitted
40,161,263,616 bytes, drained 119,005,184 bytes in the observation, served
40,161,116,160 read-hit bytes, recorded zero capacity waits, and bounded dirty
occupancy at 8,523,776 of 61,865,984 bytes. The initial fitting admission left
lower read/write/flush attempts unchanged; lower-write attempts later increased
by 470 as background work progressed. Disable and direct disk reads verified the
persisted bytes. These are correctness/interference observations, not a throughput
acceptance claim or cold-miss/capacity-pressure coverage.

### Private-alpha A04 implementation: 2026-09-21

The native project now has one current-cache source path and it is the no-switch
default used by CI. Obsolete legacy engine sources, legacy-only headers/projects,
three historical utilities and Team Foundation bindings were removed after active
include/reference checks. The native solution now contains only the current x64
driver. Active compile flags and dispatch symbols use product-oriented names;
inactive devices still forward normally and cache transitions retain the serialized
worker, ABI and diagnostics.

The installed `qcachelab` binary/service, `LabAllowedDriverKey` registry value and
fixed `labWriteCache` manifest field remain temporary upgrade-compatibility
surfaces. They do not select another implementation. Product documentation and
licensing notices now describe the current tree rather than the deleted engine.
Native Debug/Release and solution builds pass locally; host, UI, CLI and packaging
checks are recorded separately.

### Private-alpha A04 verification: 2026-09-22

A04/T012-T015 is complete. CI built, tested and published the single current-driver
0.4.57.1 candidate from the source identity above; the installed/rebooted VM loaded
that exact registered driver path and hash. The same final-candidate `policies` run
passed all sector, retention, policy and disk-oracle checks with the renamed active
path. Maintained `quick` run
`QueueCache-Verify-20260921-220027-26913a5995ea459e86255ee5ee0ac0b2`
also finished **COMPLETED**, 1/1 PASS, and restored cleanly. Its byte checks passed
for live write/overwrite reads, 2,048 random 4 KiB overwrites, explicit
flush/reopen, copy/rename hashes and owned-file deletion. File-level TRIM returned
Win32 326 and remains an explicit SKIP; this does not qualify TRIM or 4Kn behavior.

### Private-alpha A05 implementation: 2026-09-21

The four historical runtime wrappers were inventoried mode-by-mode and removed.
Current performance, read attachment, lower-write completion failure and lower-flush
failure coverage maps to the maintained developer CLI and verification runner; the
developer guide records each replacement. Raw short-write/completion modes 1/2/5,
allocation faults 6/7, forced capacity exhaustion and drain-parallelism performance
remain explicit deferred gaps rather than implied passes.

The legacy per-device installer was replaced by the single class-filter installer.
Before setup changes registration it already writes a versioned backup outside the
application directory. `packaging/Recover-Registration.ps1` now restores that exact
backup without a functioning driver or CLI, refuses live-driver registry rewriting,
supports Safe Mode or an offline SYSTEM hive and never reboots. Packaging syntax
and independence checks cover this recovery surface. Three ignored old test
directories contain only regenerable `bin`/`obj` output; no `.lab`, raw evidence or
unknown owner files were removed.

### Private-alpha A06 completion: 2026-09-22

A06/T019-T022 is complete for the code changed in A03-A05 on the same exact
0.4.57.1 candidate and disposable 512-byte-sector Q: disk. T019/T020 are covered
by the final-candidate `quick` and `policies` runs above. The policy run passed six
sector variants (parallelism 1/2/4, retention off/on): every admission byte oracle
passed with lower read/write/flush attempts unchanged, every disabled-cache disk
oracle matched, and every variant actually observed in-flight state. Six policy
configurations also passed warm-read, retention where applicable, and final disk
oracles. The first immediate policy attempt,
`QueueCache-Verify-20260921-220101-aec8fd1cdcf346a8b8c888bf40004cd5`,
is preserved as **INCOMPLETE**: `sectors/p2/retainFalse` did not obtain its required
clean/idle admission boundary after the preceding workloads. Its independent
restoration succeeded. The later fresh run reached the boundary and completed; the
incomplete verdict was not relabelled or merged. The source of the intervening
activity was not established; temporal proximity to prior workloads is not proof
of causation. T049 carries the missing boundary diagnosis/preparation evidence.

T021 used the maintained developer file tests after explicitly clearing fault and
delay hooks and temporarily applying Strict at runtime without modifying the saved
Fast profile. Evidence directory
`A06-Faults-4c35385f9b4141e592d9c9e6bcde3be7` contains two exit-zero PASS results:

* Coalesced lower-write completion fault, run
  `74640fa5-9964-4068-bbf5-d47d36376a8d`: the injected error was visible, dirty
  bytes remained owned, Retry succeeded, Disable established the persistence
  boundary, invariants returned clean and SHA-256
  `38F05DE1D835184FEAAEEC960DA866D29BD383D5FFDA0513007BAC35B3765856`
  matched from disk.
* Explicit lower-flush fault, run
  `b8efd1c5-4860-4966-bd8c-cc1ceea9de9f`: failure/fault was observed, recovery
  completed, Strict was restored, dirty/in-flight bytes returned to zero and the
  independently recorded source/copy hashes matched.

The synthetic checks raised the cumulative lifetime error counter from zero to two
as expected, while final `LastError` was zero and the cache was not faulted. Saved
profile restoration then returned Q: to active 2 GiB Fast/Idle with zero dirty and
in-flight bytes, the exact original options and volatile-flush acceptance. T022 did
not require new scenarios because A03-A05 and the verifier-order repair did not
change pin, allocation or cancellation behavior. Allocation faults 6/7,
cancellation, remaining pin/lifetime interleavings, capacity exhaustion, 4Kn and
TRIM guards/reuse remain explicit gaps; they are not implied passes. Raw evidence
is retained privately under `.lab/a06-0457-20260921` and is not committed.

Planning revision 2 adds A06a/T049-T054 before active C: validation. The historical
T022 applicability decision remains scoped to the A03-A05 changes; T053 now requires
the relevant memory-pressure/lifetime proof for the larger system-disk exposure.
The 60-second hot-set result is not proof of capacity exhaustion, deterministic
old/new interleaving, every timer boundary or absent lower-I/O dependencies under
all workloads. T050 makes the lower-I/O optimization decision explicit before the
final performance stage; A13-A16 retain production safety/security/qualification
work beyond the private alpha. No new implementation or VM result is claimed by
this planning update.

### A06a T051/T069 exact pressure qualification: 2026-09-22

Plan-14 run
`QueueCache-Verify-20260922-130140-5a38d389f7694633bb31d489d7fa816b`
completed on installed 0.4.64.1 from
`bd60725121df4572fb4f77936170cefac60dcf0c`. The running and packaged SYS hashes
both matched
`1EA460969A068E047D6B11BE9C828ECEED9DB229C08A2F8A9E531E60E2A0819E`.
Deferred first-dirty age, Idle last-write timing, Balanced high watermark,
Automatic, Fixed 50%, Fixed 100% and Fixed 0% all passed. Every allocation case
verified its independent 80 MiB expected image after Disable. Fixed 0 recorded
zero dirty, in-flight and write-owned payload while permitting 16 read-cache
slots, with 1,281 ordered quota-barrier/lower-write attempts and no capacity wait.

`FINISHED.txt`, `status.json`, `SUMMARY.md`, `results.json` and `run.log` agree on
COMPLETED/PASS. Telemetry readiness was true, the control trace was nonempty and
there was no failure/restoration-failure marker or log error. Restoration returned
Q: to active 2 GiB Fast/Idle with zero dirty/in-flight bytes and errors. The 43-file
raw evidence set is retained privately as `pressure-plan14-0641-exact-results`.
This closes T051/T069, not T050 or the allocation/cancellation/lifecycle and
recovery work in T052-T054. It makes no C: readiness claim.

### A06a T050 exact drain comparison: measured, decision open, 2026-09-22

Plan-16 run
`QueueCache-Verify-20260922-193654-4921265ec92346c8bc77b0a63a587dbb`
completed 24/24 on elevated VM Q: with the exact installed 0.4.67.1 package
from `37911d9` (CI 35773052990). The loaded driver path names SYS SHA-256
`400748178A1BDD3D3875F57B394FA49A58EBFE81501F698738006DF157686B14`;
the x64 CDM DiskSpd SHA-256 remained
`7281BF6DA6C03797016EDDF2E8AAEC4C644AE893D403D57A030B7E2E14B61079`.
Q: was disk 1, nonboot/non-system, with the pagefile on C:. Each of the 18 drain
conditions seeded the same 256 MiB payload plus 4-16 KiB recorded metadata;
seed lower-write/flush attempts and measured capacity waits were zero. Every
case was MEASURED, with its readiness handshake, interval, telemetry and raw
DiskSpd XML; all 24 immutable IDs matched the manifest. The completion marker,
status, summary, results and log agree, with no failure marker or log error.
Restoration returned the original active 2 GiB Fast/Idle profile with zero
dirty/in-flight bytes and errors. The 3,920-file raw set (13,955,363 bytes) is
retained privately as `drain-plan16-0671-exact-completed`.

| Workload | No pending drain, median IOPS | Drain p1, median IOPS | Drain p2, median IOPS | Drain p4, median IOPS |
|---|---:|---:|---:|---:|
| Fitting random writes | 24,409 | 1,405 | 4,092 | 9,291 |
| Cold random reads | 3,179 (uncached control) | 2,328 | 2,385 | 2,495 |

The fitting-write p4 results ranged from 2,162 to 10,409 IOPS across three
repetitions; p1 ranged from 1,371 to 4,027. Median explicit drain flush time was
15.0/9.5/6.7 seconds at p1/p2/p4 for fitting writes and 4.5/2.4/2.4 seconds
for cold reads. Median scored write p99 was 0.076/0.072/0.067 ms at p1/p2/p4;
cold-read p99 was 0.674/0.792/0.672 ms. These score-window tails are not
whole-operation drain latency. Parallel lower-I/O tick sums are not wall time.
The uncached cold-read control is not a matched drain, and the no-pending-drain
write control does not establish a physical-disk limit.

Decision: do not change the shipped p1 default yet. More workers showed a
promising fitting-write/flush trade-off but substantial variation, while the
cold-read result was modest. T050's measurement gate is complete; its alpha
acceptability/tuning decision remains open. Define bounded drain progress and
foreground-tail expectations for the declared alpha workload, then perform a
focused discriminating request-shape/concurrency check only if needed. Any
performance-affecting driver edit requires the fresh 72-case baseline and
focused before/after regression. This does not advance C: readiness.

### T050 re-measurement on 0.4.139.1: 2026-09-27

Plan-54 `drain-decision` (`QueueCache-Verify-20260927-145101-97fadb3f...`,
24/24 COMPLETED, Driver Verifier off, Microsoft DiskSpd 2.3 SHA-256
`DD4E57E1...FAEA2`, Q: 1 GiB budget, 256 MiB seeded payload). Medians of three:

| Workload during an explicit flush | Control IOPS | p1 | p2 | p4 |
|---|---:|---:|---:|---:|
| Fitting random 4 KiB writes (IOPS) | 22,194 | 1,460 | 4,173 | 2,790 |
| Flush seconds (writes) | - | 15.9 | 15.9 | 15.3 |
| Cold random reads (IOPS) | 3,096 | 2,416 | 2,200 | 2,407 |
| Flush seconds (reads) | - | 3.9 | 2.5 | 2.5 |

Finding: the flush barrier waits until *all* dirty data is drained, including
random 4 KiB writes admitted after the flush started (46-145 MiB extra, drained
as 11,600-27,700 single-block batches), so its duration follows the foreground
write rate rather than the 256 MiB it was asked to persist (drained in 2.5-4 s in
the read cases). Parallelism 2 shortens the payload drain by about a third versus
1; 4 adds lower-I/O queueing (about double the lower-I/O time) without being
faster on this VM. Owner decision (2026-09-27): Fast mode is the focus. Bounding
a flush was dropped: the worker admits no writes during a barrier, and the extra
data came from Windows' own file cache (DiskSpd was not run with `-Su`). Default
parallelism changed to 2 in plan 56.

Repeat on installed 0.4.148.1 (`97b8f4f`, `QueueCache-Verify-20260927-172014-d9482a17...`,
24/24, Driver Verifier off, same DiskSpd): fitting writes during the flush
3,906 / 6,673 / 8,295 IOPS at p1/p2/p4 (control 23,685); flush 28.7 / 7.5 / 6.7 s
(p4 ranged 5.5-16.9 s); cold reads 2,630 / 2,262 / 2,406 IOPS (control 3,158),
flush 3.7 / 2.7 / 2.5 s. Parallelism 2 was the most consistent. Plan-57
`app-write-profile` on the same build (`...-181437-35acd310...`): 256/256 MiB
admitted in every mode; buffered flush 107 ms, mapped flush 184 ms (uncached
before T085: 9.1 s and 3.7 s); a 1 GiB cache added 21.1 MiB of nonpaged pool.
Write matrix: see WRITE_PERFORMANCE_TRAJECTORY.md (new baseline, 2026-09-27).

### A06a T052 and A07 T026 source checkpoint: 2026-09-22

Plan 17 adds a bounded `policies` observation of a same-range 512-byte overwrite
while exactly 512 bytes are reported in flight before and after the new write,
excluding a 4 KiB metadata write as a false observation.
It compares the newest RAM read and the disabled-cache disk read with independent
expected bytes. The existing two-second delay is cleared and the original
configuration restored by the supported runner. This strengthens a previously
incidental overlap check but does not identify the exact old-version completion
after the new admission; T052's forced ordering, later sparse-segment failure,
Strict/explicit flush cutoff and retry proof remain open. Host-safe tests and
self-contained CLI publish pass. A split-CLI/installed-0.4.67.1 preliminary
`policies` run `QueueCache-Verify-20260922-201030-23a0ab480300417a925bb0c0aff32301`
completed 1/1 and restored cleanly, but its new check accepted 4,096 in-flight
bytes: this could have been metadata and is not overlap evidence. Its raw 43-file
run is retained privately as `plan17-preliminary-policies-ambiguous4096`.
After tightening the oracle to exactly 512 bytes, preliminary run
`QueueCache-Verify-20260922-201430-a177b8107f4547f0a05d4a1a7ee1f8ee`
completed 1/1 with 31 policy checks passing. It recorded 512/512 in-flight
bytes before/after replacement, newest RAM and disabled-cache disk bytes, and
clean restoration to the original active 2 GiB Fast/Idle profile with zero
dirty/in-flight bytes and errors. The 43-file raw set is retained privately as
`plan17-preliminary-policies-exact512`. This is split-CLI evidence, not the
exact packaged plan-17 build or the fully controlled T052 interleaving.

For A07/T026, `ConfigurationManager.Apply` now calls `DiskTarget.ValidateCurrent`
instead of checking only the volume extent. This rechecks the PnP identity and
NTFS mount immediately before opening the physical disk for a configuration
change, including saved-profile restore. The existing host identity/replacement
contracts pass. The preliminary policy run exercised Apply on Q: with the new
managed code, but saved-profile restore and the exact packaged plan-17 build
remain unverified.
This does not lift the boot/system/paging restriction or imply C: support.

### Snapshot-era pre-C: checkpoint: 2026-09-22

The owner reported creating a VM snapshot and installing/rebooting 0.4.69.1.
The elevated VM reported loaded SYS SHA-256
`544C3211332317A478FA71426C3CDD22E0E3E579C01BC4CA3B57BA184824ACC9`,
matching the installed package; CLI source identity was `a20d589`. C: was
disk 0, boot/system, 100 GiB, with a 512 MiB pagefile, cache disabled,
zero budget/dirty/in-flight/errors. Q: was non-OS disk 1, active 2 GiB
Fast/Idle, initially clean. No minidump, `MEMORY.DMP` or System BugCheck
1001 event survived in this snapshot, so T067's old stop code/root cause
remain unknown. Snapshot existence was reported by the owner, but hypervisor
console access and a successful snapshot restore were not independently checked.

Exact installed 0.4.69.1 plan-17 `policies` run
`QueueCache-Verify-20260922-203515-6e3b41080fe34c4099633c29153fde9b`
finished `COMPLETED`, 1/1 case and 31/31 policy checks. The overlap check saw
exactly 512/512 in-flight bytes before/after replacement and matching newest
RAM and disabled-cache disk bytes. Final restoration returned active 2 GiB Q:
with zero dirty/in-flight/errors. Raw run evidence is retained privately under
`plan17-exact-0691`; this does not prove the forced T052 failure/ordering cases.

For T054, the VM's newest registration backup still contains `qcachelab`, so
it is not an emergency filter-removal choice. An older backup
`Registration-before-2dd7df3bd16f4eabaaeebae8309810ba.json` has class
`UpperFilters=[partmgr]`, empty per-device filter lists and a demand-start
legacy service entry. Against a copied live SYSTEM hive, the candidate recovery
script's `-WhatIf` exited zero and unloaded its temporary hive; a deliberately
missing recorded disk key exited one before even the class-filter dry-run action.
Evidence is private under `recovery-preflight-20260922`. This is only a dry-run
and does not prove Safe Mode/offline recovery, bootability, or use of that old
backup as the final rollback choice. The actual recovery rehearsal remains a
hard gate before C: activation.

### Plan-18 guarded C: observation and recovery-copy checkpoint: 2026-09-22

The owner confirmed external hypervisor console access and reported a restorable
snapshot. The VM booted exact installed 0.4.70.1 (`eb22dd4`), with loaded and
packaged SYS SHA-256
`263F082BD781ADCFE54A56C5FE2FA6AA13FE2F43307E68A7AB27D2A71294541F`.
C: remained disabled with zero budget/dirty/in-flight/errors. The installed
recovery script actually restored an independently saved *copy* of the SYSTEM
hive from the filter-free historical backup. Audit of that copy showed class
`UpperFilters=[partmgr]`, empty recorded per-device filters and demand-start
service; live class filters still contained `qcachelab,partmgr`. This proves the
script's offline-hive edit path on a copy, not an offline/Safe Mode boot or a
successful VM restore. The sensitive hive copies were deleted after the audit;
non-sensitive output remains private as `recovery-copy-0701`.

Plan 18 introduces the read-only `system-preflight` suite with explicit expected
identity/size, recoverable-VM acknowledgement, and a pre-run separate-physical-
disk output guard. Existing non-OS suites retain their guards. Host-safe runner
contracts and managed builds passed. A split managed CLI against installed
0.4.70.1 completed
`QueueCache-Verify-20260922-214442-4c1bd67cf2904b7b894048801bfe02c1`
1/1 on C: with report on Q: disk 1. It observed C: disk 0 as boot/system and
cache disabled/clean, recorded no cache/workload mutation, and finished with
`FINISHED.txt`, status, summary, results and log. The split CLI is not the exact
packaged plan-18 build. Wrong expected identity and same-physical-disk output
both exited nonzero before a new run directory was created. No C: file workload
or active caching was attempted.

### Plan-19 bounded C: file and normal-restart checkpoint: 2026-09-23

The existing runner now has separate `system-files` and `system-post-restart`
phases. Both require recoverable-VM acknowledgement, exact physical-disk PnP
identity and size, and output/oracle storage on a different physical disk. The
first phase writes only a new 64 MiB owned file on C:, with a 1 MiB range
overwritten twice, and commits an independently calculated seed/SHA oracle to Q:
before writing the file. It flushes file buffers and checks live bytes. The
second phase only reads and verifies that file against the Q: oracle after a
normal restart. Neither phase configures the cache, arms fault/delay hooks,
issues TRIM/raw I/O, or performs a restart. The user-authorized normal VM
restart was a separate controlled action after the first phase completed and
C:/Q: showed no dirty/in-flight bytes or errors.

Host-safe management/runner contracts passed. The first split-CLI create
attempt, `QueueCache-Verify-20260922-235924-8aaade11c1b949c38cfaf5abc17fd3ac`,
produced passing worker bytes but did **not** write `FINISHED.txt` because its
thread-affine named mutex was released on a different async continuation. It
is preserved as incomplete, not combined with subsequent results. The runner
now uses a non-thread-affine named semaphore. With that correction, exact VM
run `QueueCache-Verify-20260923-000122-e2d2ba7372f94c5abce9fbb14f79f8b3`
finished `COMPLETED`, 1/1 PASS, with no restoration failure. The off-disk oracle
records SHA-256 `0AF2FE22628983E75B088BF638C70E06E2146B4A099C2B362341CA83AE52EE438`.
After the normal restart, run
`QueueCache-Verify-20260923-000703-3e41770fd12e45d6b165a5505ca6bdab`
finished `COMPLETED`, 1/1 PASS; every 64 MiB byte matched through unbuffered
reads. Its before/after C: snapshots show cache disabled, zero budget/dirty/
in-flight/errors and no lower writes; Q: returned enabled and clean after its
saved-profile startup delay. `FINISHED.txt`, status, summary, results, run log
and worker evidence were inspected for the completed runs. All raw output stays
private on the VM's Q: disk.

The split managed CLI ran against installed 0.4.70.1, not an exact packaged
plan-19 release. The result proves a safe, repeatable *uncached* C: baseline and
post-normal-restart byte check. It does not prove explicit persistence with a
busy cached OS volume, dirty restart survival, boot path safety, or the cause
of the earlier BMP/Photos crash. C: remains disabled; A08 stays partial and
A09 remains blocked by the A06a/A07/A10 recovery and safety gates.

| # | A02 completion overview | Current disposition |
|---|---|---|
| 1 | Barrier/lower-I/O attribution | Implemented and retained; exact lower-attempt evidence remains required. |
| 2 | Deferred policy | Implemented; real one-hour soak remains outside A02. |
| 3 | RAM-first partial-sector admission | Implemented and previously VM-verified; remaining fault/lifetime work moves to A06. |
| 4 | Zero-length/oversized/quota handling | Partial; no A02 change. |
| 5 | Safe observation/query bypass | Implemented for the scoped allowlist; no A02 change. |
| 6 | Versions/locking/transient reserves | Partial; no speculative redesign justified by A02 timings. |
| 7 | Independent ready-request service | Partial; no A02 change. |
| 8 | Admission budget improvements | Pending; no A02 change. |
| 9 | Per-4KiB synchronization overhead | Existing qualified gain preserved; no new performance claim. |
| 10 | Range-aware TRIM | Pending and explicitly postponed. |
| 11 | Cutoff flush/live policy/resize | Pending; plan-10 cleanup does not claim cutoff semantics. |
| 12 | Cold-read/drain scheduling | Pending; lower storage controlled this focused drain. |
| 13 | Indexed selection/independent workers | Deferred; V3 evidence does not justify it. |
| 14 | Power/shutdown/PnP/system disk | Pending A07-A09. |
| 15 | Statistics/evidence/restoration | V3 deployed and A02 restoration verified; UI/windowed presentation remains partial. |

### Busy single-drainer follow-up: 2026-09-19

The user installed/rebooted into 0.4.45.1 (ee33ea7, successful CI 35465390379).
Loaded immutable driver and installed package SHA-256 both match
465051174800D8BB331B588E102E263375DAC0B06BCED8D9172B0EFE1FB7CD2C.
Q: stack is partmgr/qcachelab/disk/vioscsi; startup restoration returned zero,
Q: was clean with original 2 GiB Fast/Idle settings. CIM confirmed no competing
desktop/CDM/DiskSpd/qcache processes. Frozen plan-9 CLI and DiskSpd hashes match.
This resolves the previous installation blocker; the older observer-present
diagnostic remains qualified, not silently promoted to a clean baseline.

Policies run `QueueCache-Verify-20260919-205754-7f622cca4f0f438d9cf02c53fdbfdbd2`
COMPLETED with clean restoration. All six sector variants passed zero lower-attempt
admission and sparse/full disk-byte oracles, including delayed in-flight overwrites;
all six policy configurations passed. This verifies the first wake predicate,
not yet the follow-up below.

Q32 timing-on run `QueueCache-Verify-20260919-205828-41dd93191f5c4645ae7b169481712e5d`
measured 27172.9 IOPS, p99 1.574 ms. Sampled process interval: 1265624 queued
requests, 252384 wakes, 4545442 lock acquisitions, aggregate lock wait 10.9523166 s,
hold 2.9894455 s, zero capacity waits. About one-second telemetry buckets show
119k-268k requests/s with no repeated wakes before the 5-second age threshold;
after draining begins, roughly 25k-26k requests/s with a wake per request and
about one second of aggregate lock wait per second. Warmup is part of this interval,
not the score: do not claim a 268k scored result or isolate CPU cost from these counters.
Original RESTORATION_FAILED remains: only 10752 dirty bytes, zero errors/in-flight.
Separate recovery `QueueCache-Verify-20260919-210314-998e74b49e7e44e4913eaaf853e82ea4`
returned RESTORED with original clean settings.

Follow-up hypothesis: when Parallelism=1 and InFlightBytes>0 under the cache mutex,
the sole drainer already owns work and rechecks pending data on completion. A
foreground wake cannot accelerate that drainer, but wakes inactive workers into
mutex contention. QcShouldWakeAfterWrite now suppresses only that redundant wake;
multi-drainer behavior, timer, completion, control/barrier and capacity wake sites
remain unchanged. The existing drain predicate still updates pressure hysteresis.
Native Release compile plus exact policy/parallelism/forced/age/hysteresis assertions
and host-safe management contracts passed. Commit 948ea1c9c75236c4d59b6c1f2d757d5dd234b18f
passed signed CI 35470024426 (0.4.46.1). All 450 payload manifest hashes match;
driver SHA-256 is 90C50E7AD991E3EB0E9D7FFFC38C5B1CEA80935124B2CDEB6C2560980DD65060,
test-certificate thumbprint B8738B37CB8F034006D1683BFCEDE42B6683CD46.
Installer exited zero, driver setup succeeded and immutable staged binary matches.
After explicit Q: flush and clean Q:/disabled C: state checks, a normal reboot was
completed. Running immutable driver path/hash and Q: stack were verified;
startup restoration returned zero, original Q: settings were clean and C: remained
disabled. No competing processes were present.

Before-follow-up Q1 run `QueueCache-Verify-20260919-210424-0e6e435ecfda43fcac80bf0db1c518bd`
collected 3/3 timing-off samples: 21198.30, 20997.70, 19842.86 IOPS; p99
0.076, 0.077, 0.082 ms. Original RESTORATION_FAILED: only 25088 dirty bytes,
zero errors/in-flight, original timing/settings. Separate recovery
`QueueCache-Verify-20260919-211759-5cb84ec9b34b4bdc84d43e457c3fdf74` returned RESTORED.

Before-follow-up Q32 run `QueueCache-Verify-20260919-211844-9bf55eb5b48b4ace943bf6699319790a`
collected 3/3 timing-off samples: 27940.06, 27190.60, 27305.20 IOPS; p99
1.554, 1.560, 1.545 ms. Original RESTORATION_FAILED: only 31232 dirty bytes,
zero errors/in-flight, original timing/settings. Separate recovery
`QueueCache-Verify-20260919-212912-a042f593a4044129898fd4064ed6c722` returned RESTORED.
Both recovery replies, readiness, control traces and logs were checked; original
failed verdicts are unchanged. All seven current diagnostic/baseline XML scores
and recognized CDM trailers match results; all 532 telemetry samples have ready
handshakes, complete process-interval coverage and continuous required counters.
Maximum sample gap 0.6522993 s; no interval capacity waits or sampled driver errors.
Exact VM evidence root: `ManualRuns/policy-wake-045-20260919`.

#### Loaded 0.4.46.1 results

Policies run `QueueCache-Verify-20260919-213333-e5584b97e4404d2e9ed4d9ce85b628c3`
COMPLETED with clean restoration. All six sector variants passed unchanged lower
read/write/flush attempt checks and disk-byte oracles with delayed in-flight
overwrites; all six policy configurations passed. This does not cover every
ordering, fault or lifecycle path.

Timing-on Q32 run `QueueCache-Verify-20260919-213404-7fdaf248799d46fa894898344b778e03`
measured 174862.48 IOPS, p99 0.187 ms. Its sampled process interval recorded
2500950 queued requests, 1674 wakes, 1671 drain batches, 7511413 lock acquisitions,
1.2494448 s aggregate lock wait and 2.0018236 s hold. Baseline interval recorded
252384 wakes and 10.9523166 s wait. Draining continued with zero capacity waits;
the wake-per-write contention hypothesis is supported. These include warmup and
are not score-window CPU measurements. The much higher timing-on Q32 score than
timing-off below remains unexplained; do not combine modes or headline that score
as the release comparison.

Matched timing-off runs use the unchanged plan-9 CLI and CDM DiskSpd hashes,
2 GiB budget, fitting 1 GiB random 4 KiB file, Idle/parallelism 1, five-second
warmup and ten-second requested score window. Three complete repeats per group:

| Queue | 0.4.45.1 IOPS median [min, max] | 0.4.46.1 IOPS median [min, max] | Change | Decimal MB/s before -> after | Write p99 ms median before -> after |
|---|---:|---:|---:|---:|---:|
| Q1 | 20997.70 [19842.86, 21198.30] | 23043.10 [22719.98, 23474.63] | +9.74% | 86.01 -> 94.38 | 0.077 -> 0.067 |
| Q32 | 27305.20 [27190.60, 27940.06] | 74532.77 [72992.20, 74812.77] | +172.96% | 111.84 -> 305.29 | 1.554 -> 0.192 |

Q1 run `QueueCache-Verify-20260919-213825-ae3dc7de5321435497abdccbf034f1c7`:
22719.98 / 23474.63 / 23043.10 IOPS, p99 0.068 / 0.066 / 0.067 ms.
Q32 run `QueueCache-Verify-20260919-215134-2c871519f23f47728bff8996dcf4503b`:
74812.77 / 74532.77 / 72992.20 IOPS, p99 0.192 / 0.189 / 0.204 ms.
No slow sample was removed. This is a qualified focused measured improvement,
not full performance acceptance or durable disk throughput.

All three after-runs retain RESTORATION_FAILED: only late dirty bytes
14336 / 28672 / 30720 respectively, with zero errors/in-flight and restored
timing/settings. Separate supported recoveries, after process absence checks:

* Diagnostic: `QueueCache-Verify-20260919-213817-4bcbeb807b2a4b39b7f8cd2d09af2d89`.
* Q1: `QueueCache-Verify-20260919-215127-e0d764f97ad344d1acdb2cdf5fdea721`.
* Q32: `QueueCache-Verify-20260919-215811-e21bd36bd05440bf974a7db884279b1c`.

All returned RESTORED; replies, readiness, traces and logs confirm clean original
settings/timing. The late-write source is not established. All 14 before/after
XML scores and recognized trailers plus 1064 telemetry samples were validated;
maximum gap 0.6522993 s (after-only 0.2974603 s), complete interval coverage,
required counters present/monotonic, zero interval capacity waits or driver errors.
Final live Q: clean 2 GiB Fast/Idle, C: disabled/budget zero, no owned or competing
workload processes. Frozen tool hashes reconfirmed. Raw evidence is retained
privately under `.lab/policy-wake-045-20260919/evidence`. Full matrix, one-hour
soak and remaining deterministic fault/lifetime checks were not run in this
focused iteration; they remain open rather than being inferred from the gain.

### Policy-gated write wakes: 2026-09-19

The priority remains fitting random 4 KiB writes at Q1/Q32, not sequential scores.
On hash-verified 0.4.42.1, with CDM and the desktop observer still present,
plan-9 diagnostic run `QueueCache-Verify-20260919-192811-b362af0b8e4f434b9f19c69b399e7abe`
collected one Q32 Idle timing-on case (25433 IOPS, 1.669 ms write p99).
Its 15.318813-second sampled process interval recorded 344924 queued requests,
335145 wake signals, 2105795 lock acquisitions, 18.4145805 seconds aggregate
cross-thread lock wait, 3.1454909 seconds lock hold and zero capacity waits.
These are interval/lifetime-counter deltas, not isolated score-window costs or
CPU time; they do not separately measure copy/publication time. Raw XML score,
readiness and all 75 telemetry samples were checked (maximum gap 0.255625 seconds,
zero sampled error delta). The initial claim that the applications had closed was
incorrect: a later CIM check found the same PIDs (6172 and 7544). The earlier
Get-Process-by-name remote check returned no rows and was not reliable proof.
This is observer-present diagnostic evidence, not a clean comparison baseline.

Local hypothesis: unconditional foreground wake signals make all four allocated
drainer threads contend for the cache mutex even when Idle policy is not ready
and only one drainer is configured. Candidate change: use the exact existing
QcShouldDrain predicate at write completion with idle age zero; preserve all
other wake sites, timer checks, Eager/age/watermark/forced triggers and the
drainer's final eligibility check. No admission, byte ownership or durability
rule is relaxed. Native Release build and compile-time hot-Idle/hysteresis/age/
forced policy checks passed; host-safe management/runner contracts passed.
Commit 360ab9aa74af9b1d434f9a5ad414e0847c5d2e0f passed CI 35464860844:
Debug/Release builds, host-safe/CLI/package checks and signing. Signed 0.4.44.1
driver SHA-256 is D2D9794B6F061D59C9ADF9D973BE7632771E981E8B3D07369BFF7839F499680B;
all 450 package manifest entries were verified. Certificate thumbprint is
DAF02E2D3CDAB895F358B67DC183DC6FD40383F9 (disposable test certificate).
Installation exited 5 and rolled back before driver setup: QueueCache.Desktop
held Avalonia.Base.dll open in another session. Normal CloseMainWindow requests
returned false for both verified application PIDs; neither was killed, and no
reboot was attempted. Loaded 0.4.42.1 identity, its unchanged next-boot service
path, clean original Q: settings and disabled/zero-budget C: were rechecked.
Despite the installer reporting rollback, installed application build metadata
now says 0.4.44.1; this is a partial application update, not a complete rollback
or a deployed candidate driver. Finish the supported installer after closing the
desktop application. VM candidate verification is blocked until those windows close.
Repeat the clean Q32 timing-on baseline, then deploy: wakes and lock waits should
drop substantially. Compare maintained timing-off Q1/Q32 cases afterward; no gain
is claimed yet.

The diagnostic's original verdict remains RESTORATION_FAILED: only DirtyBytes
27648 mismatched, with zero errors/in-flight bytes and original settings/timing.
After process-stop confirmation, independent recovery
`QueueCache-Verify-20260919-193450-4a2ad9936e1a4b61a9ce58e42eccd0d5` returned
RESTORED. Private evidence: `.lab/small-write-attribution-20260919/evidence`.

### Attribution candidate: plan 8, 2026-09-19

VM verification completed after the user installed/rebooted 0.4.41.1 / f67080f.
Driverquery reports the running immutable image
`QueueCache-0.4.41.1-436150AEFACC.sys`; its SHA-256 matches the installed package:
`436150AEFACC0021F20241463EC82117695D9EFAEC89EDBBEA7400828C4392AD`.
Q: attachment and V2 responses are confirmed; C: remains disabled/zero budget.
Focused policies run `QueueCache-Verify-20260919-163316-ce4caabaa68241699d744154b7d1ae15`
completed with no restoration failure. All six sector admission variants
(parallelism 1/2/4, retention off/on) recorded exactly unchanged lower read/write/
flush attempts. Explicit drain/disk-read positive checks, sparse disk-byte oracles,
128 delayed partial/full overwrites per variant and six policy configurations
passed. All six overwrite cases observed in-flight data. Restoration returned
the original 2 GiB Fast/Idle settings, timing off, zero pending bytes/errors.
This is observed no-attempt proof, not the remaining bounded-gate/fault/lifetime
or one-hour-soak acceptance.

Next optimization candidate (item 9): retain the admitted slot index for writes
contained in one 4 KiB block. This removes two duplicate hash lookups during
copy/publication; preflight and reservation lookups remain. Filling already
excludes those slots from drain selection, and foreground publication is serialized.
Multi-block writes keep the existing path. Local Release native build passed;
focused VM correctness passed and the comparison below does not establish a gain.
Plan 9 adds explicit maintained case selection so matched three-repeat random
Q1/Q32 Idle timing-off comparisons need not repeat the full matrix during iteration.
Commit `b63e14baa4f4e2463588d834941e61b25ba2ac4b` passed CI run `35456903742`
(Debug/Release builds, host-safe tests, CLI/package checks and signing), producing
0.4.42.1. All 450 signed-package manifest entries were verified; driver SHA-256
is `DE0E8E3EDF3E1E235A46DC5667BBC0B9322ADD7960E658C1739F85C931BEF733`.
The supported installer succeeded on the clean VM, followed by an explicit Q:
flush and normal reboot. Driverquery confirmed the running immutable candidate
image with the exact hash above and Q: stack partmgr/qcachelab/disk/vioscsi.
Startup policy restoration returned success and clean 2 GiB Fast/Idle; C: stayed
disabled with zero budget. The VM currently has this candidate loaded.

Matched pre-change plan-9 runs on 0.4.41.1, using the same side-by-side CLI,
CDM DiskSpd hash, 2048 MiB budget and 600-second preparation deadline:
Q1 `QueueCache-Verify-20260919-163631-3665b246a87b4dbfb5d1c903aaa41de1`
(IOPS 21595.80, 21375.10, 21705.20), and Q32
`QueueCache-Verify-20260919-165040-786d9792986e4a6e9784cd24963717c2`
(26671.33, 26700.50, 26557.84). Both collected 3/3 MEASURED but failed the
unchanged final zero-dirty predicate: 4096 and 20992 newly dirty bytes respectively.
Snapshots show no other restoration mismatch, zero in-flight bytes and zero errors.
After process-stop checks, separate supported recoveries
`QueueCache-Verify-20260919-165022-be9e3c20bc104a9698817262f05e60c0` and
`QueueCache-Verify-20260919-170300-bce6927e78dd49bf82b29b0e65de5e9e` passed.
Original verdicts remain unchanged; these are qualified measurements, not clean
acceptance runs. No candidate driver was installed during these measurements.
All six raw XML scores match their recorded results. Their 457 telemetry samples
cover the recorded process intervals with readiness confirmed and maximum gap
0.290293 seconds; capacity-wait and error deltas are zero. These counters describe
the process intervals, not the 10-second score windows. Baseline/recovery evidence
is archived privately in `.lab/slot-reuse-plan9-20260919/evidence` (870 files).

Candidate policies run
`QueueCache-Verify-20260919-171502-4178f405ca8841d3b2a2f67e8a04e69e`
completed with clean restoration. All six sector variants passed unchanged
lower-attempt admission, sparse/full byte oracles and delayed overwrites with
in-flight data observed. The six policy configurations also passed.

Candidate Q1 run `QueueCache-Verify-20260919-171534-a20c5d776df64c919d8e4e116513b310`
and Q32 run `QueueCache-Verify-20260919-172931-b10719903c9d4641a598bf36208383d8`
each collected 3/3 MEASURED. Both retained RESTORATION_FAILED: only DirtyBytes
was mismatched (25088 and 10752 respectively), with zero in-flight bytes/errors
and unchanged settings/profiles/timing. Separate supported recoveries
`QueueCache-Verify-20260919-172920-7bb924463bbd4d708e84edc535fc504a` and
`QueueCache-Verify-20260919-174330-07783ce4a6d040438005221543768a68` returned
RESTORED after owned-process checks. This recurring late-dirty behavior predates
the candidate; its source is not established and the final predicate was not relaxed.

| Focused group | Before IOPS, median [range] | Candidate IOPS, median [range] | Median change | Write p99 median, before/candidate |
|---|---|---|---|---|
| Random Q1, Idle, timing off | 21595.80 [21375.10, 21705.20] | 20815.80 [16591.60, 21214.29] | -3.61% | 0.075 / 0.082 ms |
| Random Q32, Idle, timing off | 26671.33 [26557.84, 26700.50] | 27102.90 [26554.30, 27277.22] | +1.62% | 1.574 / 1.562 ms |

The third candidate Q1 sample is retained: 16591.60 IOPS, 0.131 ms p99. All
twelve raw XML scores/tails and 916 telemetry samples passed the recorded readiness
and process-interval coverage checks (maximum gap 0.290293 seconds). Candidate
intervals have zero capacity-wait/error deltas. The slow Q1 interval has 3524
completed lower writes versus 2868/2784 in its peers; this does not prove causation
or isolate score-window drain interference. The candidate is correctness-verified
for the focused scenario, but performance acceptance is withheld. Fewer lookups
alone is not a measured optimization win; the Q1 result requires attribution
before claiming healthy-path improvement. This was a sequential before/after
comparison, not an interleaved rollback crossover or full-matrix acceptance.
All current identity, baseline, candidate and recovery evidence is archived in
the same private directory (1784 files); no test process remains running.

Implementation: diagnostics V2 adds live lower read/write/flush submission
counters (including direct inactive forwarding) and nine barrier reason counts
with last reason/major/code/offset/length. The original 80-byte V1 reply remains
available on the same IOCTL; new clients show unavailable attribution as null on
old drivers. No admission, ordering, scheduling or durability behavior changed.
The existing sector scenario now requires unchanged attempt counts across fitting
partial/full writes and cached reads, and verifies positive counters for the
subsequent explicit drain/flush/disk oracle. Plan 8 versions this stronger test.

Local verification: Release native build passed with zero warnings/errors;
management/runner/ABI and desktop fixture tests passed. V1/V2 wire decoding,
missing counters, each changed counter and reset/reversed counters are covered.
The focused VM `policies` results are recorded above. This does not
complete the bounded lower-I/O gate, deterministic allocation/fault/cancellation
tests, full Strict/lifecycle coverage or the one-hour soak.

### Fresh write baseline: incomplete, 2026-09-19

The next agreed gate was attempted on the restored, hash-verified 0.4.40.1 driver:
`write-performance --budget-mib 2048 --repeats 3`, default 10-second score windows,
CDM 9.0.3's x64 DiskSpd SHA-256
`7281BF6DA6C03797016EDDF2E8AAEC4C644AE893D403D57A030B7E2E14B61079`.
Run `QueueCache-Verify-20260919-014223-992df2c2db0f4bce887fe0d6ba58ff1e`
ended **INCOMPLETE**, with three MEASURED cases and one FAIL out of 72 expected.
Do not combine these rows with earlier runs or report medians/performance acceptance.
Full private evidence is retained in `.lab/write-baseline-20260919` and the VM's
matching `ManualRuns\write-baseline-20260919` directory.

The fourth case failed during its explicit pre-workload flush, before its score
window. Worker 00074 reached driver control dispatch and exceeded the existing
180-second deadline. Its exit record says exited / -1; an additional output-pipe
cleanup timeout masked the primary failure in the old runner's final message.
The runner now retains the primary deadline/cancellation/non-exit failure and
records a separate `.pipe-failure.json` when cleanup also times out. Deterministic
host-safe regressions cover these combined failures and fatal pipe-only failures.
At that checkpoint the source fix was not yet staged and no deadline had been
increased. The subsequent authorized plan-7 deployment below includes this fix.

Control trace 00072 contains 938 samples over 190.108 seconds, maximum gap
0.237 seconds. Dirty data decreases from 679.136 MiB to 13.121 MiB; drained bytes
increase by 666.016 MiB (about 3.50 MiB/s), with 51,836 lower-write completions,
no newly accepted bytes and zero driver errors. The last phase is dirty-data
draining (3), not lower-flush waiting (4). This is continuing slow drain progress,
not evidence of a deadlock. It is outside the DiskSpd score window and its
completion counts do not prove lower-I/O attempt behavior.

Independent restoration completed in about 12 seconds with observer readiness,
original 2 GiB enabled Fast/Idle options, unchanged profiles/timing, zero
dirty/in-flight bytes and zero errors. A subsequent live check found no workload
processes and confirmed the clean original runtime state. No driver changed.

**Authorized next gate:** on 2026-09-19 the user explicitly approved changing the
deadline and continuing. Plan 7 adds `--preparation-flush-seconds` (default 180,
bounded 180..3600); this VM's new baseline uses 600. The value is recorded in the
manifest and log and extends the paired preparation-control observer lifetime.
The 72-case matrix, 10-second score windows, 45-second readiness handshake,
coverage requirements, other control timeouts and independent 300-second
restoration deadline remain unchanged. Host-safe contracts and self-contained
CLI publish passed; the updated CLI is staged separately from the installed
driver. Do not merge the incomplete plan-6 rows with the new run. Kernel
instrumentation remains pending behind the complete baseline gate.

The new run is
`QueueCache-Verify-20260919-101745-c820e051450f4f3b8f511f42a9aaf595`, in the VM's
`ManualRuns\write-baseline-plan7-20260919` directory. The foreground SSH
connection ended after about 48 minutes, but coordinator PID 688 and its owned
workers were verified alive with the exact command line. Persistent progress
advanced through 28 measured cases into case 29, without restarting the run.
The 600-second flush timeout is visible in raw progress; the old case-4 failure
point was passed. It finished at 12:11:56 UTC with **72/72 MEASURED and
RESTORATION_FAILED**, not a clean-run PASS. All 72 unique IDs match the manifest,
with three repetitions in each of 24 groups. Every raw DiskSpd XML matches the
reported byte count, operation count, measured seconds and write p99. Readiness,
normal child exits, full process-interval coverage and expected mode/timing were
checked for all cases. All 5,430 workload telemetry samples have zero driver
errors and stable instance 2; maximum sample gap is 0.6437385 seconds (limit 2).
The original status/summary/finished marker must remain unchanged.
The complete private archive is `.lab/write-baseline-plan7-20260919-evidence`:
9,128 files, 101,208,027 bytes including separate recovery evidence. Robocopy
reported zero failed/mismatched files; local inventory and completion/recovery
records were checked after the copy finished.

Final restoration did not time out: worker 01517 failed its composite state
check after about 28 seconds. Trace 01515's final sample has 10,752 dirty bytes,
zero in-flight bytes and timing disabled; accepted bytes increased by 10,752
after the main drain. This supports late writes after Flush as the reason for
the clean-state failure, but the old worker did not retain its exact failed
comparison snapshot. Do not label their source definitively or weaken the
zero-dirty requirement. Later read-only checks found a clean healthy cache,
unchanged saved profile/options/timing, and no workload processes.

The runner now reports every mismatched field with expected/actual values and
writes a `.reply.json.mismatch.json` failure snapshot. All original predicates
remain enforced; no retry, deadline or workload contract changed. Host-safe
regressions cover each predicate, simultaneous profile/timing differences and
option value equality; publish and editor diagnostics passed.

After confirming no competing processes, the supported `verify-recover` on
the exact run passed in separate recovery
`QueueCache-Verify-20260919-121942-1c94768f67904aea9ad75a2a36602713`.
Its `recovery-result.json` says RESTORED and its worker reply verifies the
original Fast/Idle 2 GiB configuration, profile, timing off, instance 2 and
zero dirty/in-flight bytes/errors. The VM is ready for the next focused run;
"restoration" here means runtime cleanup, not a VM snapshot rollback.

The complete measurement collection is a qualified comparison reference, not
an automatically accepted performance milestone or a restoration-clean run.
Medians and three-repeat ranges are in
[WRITE_PERFORMANCE_TRAJECTORY.md](WRITE_PERFORMANCE_TRAJECTORY.md). No partial
run was merged, no performance gain is claimed, and no kernel changed. Keep
the restoration caveat attached to comparisons; priority 1 attribution and
priority 2 admission proof remain the next implementation work.

### VirtIO and volume retrim follow-up, 2026-09-19

Both installed VirtIO-SCSI controller PnP records report driver
`100.100.104.27100`, INF `oem0.inf`, driver date 2025-01-13. This supports the
user's recollection of package 0.1.271; it is not inferred from the mounted ISO.
NTFS delete notifications are enabled (`DisableDeleteNotify=0`). Q: reports
512-byte logical/physical sectors, aligned device/partition, NoSeekPenalty,
TrimSupported, and a thin-provisioned slab size of 4096 bytes.

On the idle, identity-checked non-OS Q: disk, `defrag Q: /L /U /V` printed
`Retrim: skipped` and `Incorrect function. (0x80070001)`. Its process exit code
was zero: **the operation failed; exit zero is not a successful retrim oracle**.
Before/after cache state and raw output are retained in the VM share's
`ManualRuns\trim-volume-20260919`. Afterward dirty/in-flight bytes and errors
were zero and the original Fast/Idle settings remained active. This attached
volume-level result is separate from the earlier attached/unfiltered file-level
Win32 326 comparison; it does not prove unfiltered volume-retrim behavior.

The matching upstream report is
[virtio-win issue 1574](https://github.com/virtio-win/kvm-guest-drivers-windows/issues/1574),
opened 2026-05-24 following May Windows updates. Community reports describe the
same volume-retrim error and successful 16/32/64 KiB discard-granularity
workarounds depending on storage geometry. The statement that 0.1.271 always
avoids this is not an established compatibility guarantee; the thread also
reports persistence after downgrading to 0.1.226. Our installed .27100 still
fails. A granularity mismatch is a credible hypothesis, not a proven local cause.
Actual Proxmox current/pending configuration, live QEMU device mapping and
backing-storage geometry have not been inspected. No Proxmox args, Windows
drivers, system binaries or VM power state were changed for this investigation.

The subsequent maintained file-only probe
`QueueCache-Verify-20260919-101718-615885a4d9da47c3830ec9edfc5f78e9` finished
`COMPLETED_WITH_SKIPS`, again Win32 326 with guards/reuse unexecuted. Its finished
marker, status and result agree; it is not a TRIM correctness pass. A private
local copy of this investigation is retained in `.lab/trim-volume-20260919`.

### Authorized detached comparison, 2026-09-19

**Conclusion:** file-level TRIM error 326 reproduces with QueueCache completely
absent from Q:'s live device stack. Cache-disabled and filter-detached are distinct
conditions; both have now been tested. General Windows TRIM capability does not
guarantee support for this file-level API. Do not treat these SKIPs as TRIM
correctness passes or spend the next performance iteration rewriting this path.
The exact rejecting Windows/storage layer remains unidentified. The original
driver and cache configuration were restored and verified. Resume priority 1
instrumentation and priority 2 admission proof after the fresh write baseline.

Plan 6 adds opt-in `trim-file` within the existing runner/owned-worker pipeline,
without opening a cache device or changing cache settings. It rejects
boot/system/paging disks and changed identity, writes a fresh 3 MiB file, and
attempts middle-1-MiB file-level TRIM with guard/rewrite checks on success.
Unsupported errors are top-level SKIP / COMPLETED_WITH_SKIPS, not a correctness
PASS. Host-safe contracts and CLI publish passed. No driver source changed.

Matched attached baseline
`QueueCache-Verify-20260919-012558-c5df6410a19d447cbcaf1a5861ea3fa5`
completed with Win32 326; guards/reuse were not executed. VM evidence is under
`ManualRuns\trim-detached-20260919` on its existing share. The same CLI remains
staged there. The full installed 0.4.40.1 package backup passed its checksum
manifest. `recovery.json` preserves the original Q: enabled 2 GiB Fast/Idle
configuration, timing off and saved profiles; `c-before.json` records C: disabled
with zero budget. Driver hash remains the hash in the earlier checkpoint below.

The user explicitly authorized this removal/reboot comparison. The installed
`setup\Install-Driver.ps1 -Uninstall` completed with exit 3010 after reporting
both disks clean. Class UpperFilters became only `partmgr`; `EhStorClass` was
preserved. Applications, profiles, service and driver binaries were retained.
`uninstall.log`, class registration, original boot and driver state were saved.
Restart temporarily closed SSH; it recovered without a forced reset. Early
`boot-detached.json` / `driver-detached.json` snapshots still showed the old boot
and do NOT prove detachment. The subsequent `boot-unfiltered.json` confirms a new
boot. `stack-unfiltered.txt` shows Q: as `partmgr -> disk -> vioscsi`, without
QueueCache, and its cache protocol fails. The retained boot-start service remains
Running without attachment, so service state alone would have been misleading.

Unfiltered run `QueueCache-Verify-20260919-013229-67150a4f8fd446439f4e8f632c9a973e`
also returned 326 / COMPLETED_WITH_SKIPS. CLI and entry-assembly hashes matched
the attached baseline. Both runs have complete reports and raw replies; local
private copies are in `.lab/trim-file-20260919/evidence`. This demonstrates that
the file-level rejection reproduces without QueueCache attached. It does not
identify the rejecting Windows/storage layer, prove physical discard propagation,
or validate the driver's TRIM ordering/guard/reuse paths.

The same installed setup script restored `qcachelab,partmgr` registration and
the startup policy task with exit 3010, retaining the same 0.4.40.1 binary.
Restoration completed after the second reboot: live Q: stack again includes
`partmgr -> qcachelab -> disk -> vioscsi`, driver hash matches the original, and
the startup task completed with result 0. `restored-snapshot.json` matches the
original Q: disk identity, enabled 2 GiB Fast/Idle configuration, options, timing
off and saved profiles. Dirty/in-flight bytes and errors are zero. C: remains
disabled with zero budget, pending bytes and errors. No workload/UI processes
remained at final capture. Class UpperFilters/LowerFilters match the originals.
Final evidence was copied into the same private local evidence directory. No
driver changes, OS-disk workloads, forced resets or performance claims resulted.

### File-level TRIM diagnosis, 2026-09-19

On the same hash-verified 0.4.40.1 driver and disposable Q: disk, Windows reports
TRIM supported and delete notifications enabled. The exact file-level rejection is
Win32 326 (`ERROR_DEVICE_DOES_NOT_SUPPORT_TRIM`), not a generic inferred absence of
TRIM. The plan-4 diagnostic run
`QueueCache-Verify-20260919-000150-d3723a5158094f49ad7c021f4089e7cf`
captured that error. The new opt-in plan-5 `trim-diagnostic` suite completed both
cases in `QueueCache-Verify-20260919-000441-66e57d9c9fe34f8b9125b8439e00a2cd`:
cache routing enabled and disabled both returned 326, with no increase in the
driver TRIM counter during either probe. Both retained guard/reuse SKIP outcomes;
top-level PASS records diagnostic completion, not TRIM correctness.

General TRIM requests were observed in lifetime counters between runs, but are not
attributable to these probes. Disabling cache routing does not detach the filter,
so this comparison does not exonerate every filter path or identify the rejecting
filesystem/storage layer. No hardware changes are justified by this evidence yet.
The test now catches unsupported errors only around the TRIM call, not subsequent
guard/rewrite I/O, and preserves native error codes. Host-safe contracts passed.
Restoration returned the original enabled 2 GiB configuration with zero pending
bytes/errors; all three control observers reported ready. No driver code changed.

### Focused sector verification, 2026-09-19

Maintained `qcache developer verify Q: --suite policies`, plan 4, completed run
`QueueCache-Verify-20260918-234805-f0f1986704e6474ea79d604e6375a32b`
(run ID uses the VM's UTC clock). Loaded driver e8b37be / 0.4.40.1 matched the
installed package SHA-256
`1F1E8772A738D005D0A6E9ECBA4FFCD8FEEA98F21DF2B010D2F22E27F859C2BB`;
installation preceded the current boot. The target was the disposable 200 GiB
non-OS physical disk, with 512-byte logical sectors. No competing test/UI processes
were present; delay and fault hooks were explicitly cleared before the run.

All 29 detailed checks passed: 12 sector admission/disk-oracle checks across
parallelism 1/2/4 and retention off/on, plus 17 existing policy checks. Coverage
included every sector position, crossing writes, cold neighbours, sparse drains,
and 128 full/partial overwrites per combination with 25 ms lower-write delay.
Every combination observed nonzero in-flight bytes. Exact live and disabled-cache
disk bytes matched. Deferred admission left observed lower-write/flush counters
unchanged. Runtime restoration succeeded with no dirty/in-flight bytes or errors;
saved profiles were unchanged. Raw evidence remains private in the exact run.

The separate `quick` run
`QueueCache-Verify-20260918-234946-15631f05af364a08b4be6696af25b18f`
also completed and restored cleanly: six checks passed, including 2048 random
overwrites, explicit drain/reopen bytes and copy/rename hashes; four checks were
explicitly skipped. File-level TRIM was unsupported on this target; no TRIM
coverage is claimed. Fault/lifecycle/concurrency/capacity cases were excluded.
Host-safe management/runner contracts passed again on 2026-09-19. Both complete
raw runs are preserved under the private `.lab/sector-verification-20260919-results/`
directory. No performance matrix was run in this verification checkpoint.

This is limited correctness evidence, not full acceptance or a performance claim.
Observing in-flight data does not prove a specific old/new-version interleaving.
The current scenario does not count lower-read attempts or gate all lower I/O;
unchanged completion counters are not proof of zero lower-I/O attempts. Sparse
segment failures/retry, pinned-reader lifetime, allocation failure, cancellation,
partial TRIM/reuse, capacity pressure, 4Kn, and the one-hour soak remain unverified.

### Partial-write implementation handoff

Blocks remain 4 KiB allocations (no eightfold growth in index entries); each has
an eight-bit 512-byte validity mask. Admission of a sector-valid partial write
copies its bytes and publishes coverage without lower I/O. New versions inherit
known bytes of pinned/in-flight predecessors, preserving untouched sectors. Full
blocks retain adjacent-write batching; sparse blocks drain contiguous valid runs
only. All runs must succeed before retiring the version; failure retains it for
ordered retry. Reads requiring missing sectors use the lower read then overlay
only known RAM sectors. Known-byte counters are separate from slot-based admission
quotas. Partial clean blocks do not count unknown bytes as readable RAM.

Before deployment acceptance, add/run maintained correctness scenarios on a
disposable non-OS volume with known 512-byte sectors (4Kn rejects sub-sector I/O):
all eight sector offsets; disjoint masks; cross-4KiB writes; partial -> full ->
partial overwrites; cold neighbours; cached partial reads and mixed missing reads;
old in-flight full/partial versions replaced at drain parallelism 1/2/4; retention
off with pinned readers; failed second sparse segment then retry; TRIM/reuse;
admission budget exhaustion; no forced lower flush during fitting Fast writes.
Compare exact RAM bytes AND disabled-cache disk bytes, especially unchanged
neighbours. Verify counter/slot accounting returns to zero after drain/release.
Do not describe compile-time coverage tests as those VM correctness tests.

Residual limits: oversized/zero-write-quota fallbacks still use ordered lower I/O;
TRIM retains its existing 4KiB-alignment restrictions and broad fallback; sparse
blocks keep partial coverage after read misses rather than populating missing
sectors. These are explicit later work, not hidden performance guarantees.

Baseline driver de76288 / 0.4.37.1; CLI focused suite added by 36969d1. Baseline
20260916-213221 stopped at case 32 observer readiness; 31 measured cases only.
Restoration succeeded, dirty/error zero. Healthy timing-off random Q1 samples were
~80–83 MB/s; several other cells stalled, with active write/drain phase and no
capacity throttles. Exact broad-barrier trigger is not yet captured. These facts
justify investigation, not a complete before/after performance claim.

### Drive disconnection handoff, 2026-09-29

Owner reopened orderly eject and unexpected removal for implementation planning.
[Detailed implementation handoff](DRIVE_DISCONNECT_IMPLEMENTATION_PLAN.md) defines
driver lifecycle/ownership work, CLI and desktop eject, reconnect identity, and a
maintained removal verification suite. Fast-mode unflushed data survival after an
unexpected unplug is explicitly not promised.

- Implementation: source checkpoint on `volume-filter` adds an ordered QUERY_REMOVE
  drain/disable boundary, CANCEL_REMOVE restoration, a one-shot surprise-removal
  Windows error-log packet, shared whole-disk eject preview/operation, CLI command,
  desktop disk action and unavailable saved-profile display. Eject rejects protected,
  ambiguous or unlettered layouts and revalidates PnP/volume identity. The eject
  operation explicitly disables/drains each affected cache before asking Windows
  for removal; a Windows veto attempts identity-checked rollback.
- Verification: host management and desktop fixtures pass. A disposable 8 GiB
  `drive-scsi2` NTFS disk was attached to the test VM and formatted as W: after exact
  identity checks. No orderly eject or physical hot-unplug has yet passed. Native
  CI build and loaded-driver verification are separate gates; do not infer either
  from host success. External Windows-eject routing, error-log collection, veto,
  reconnect, two-volume, fault, FAT32/ReFS and surprise-removal cases remain open.
  The first W: preview on 0.4.233.1 correctly found the new disk but refused it:
  the initial partition count included the hidden 16 MiB GPT Microsoft Reserved
  partition. Inventory now excludes only that metadata partition from the
  lettered-volume count; a new CI build and live preview must confirm the fix.
- Next: qualify the loaded build on W:, implement the maintained removal suite and
  remaining lifecycle/ownership work, then independent review. Virtual-drive
  feature stays postponed.

### Drive disconnection source and VM checkpoint, 2026-09-30

Implementation: plan 71 adds the opted-in `disk-removal` orderly-eject/reconnect
case to the foreground runner. It requires exact disposable physical disk
identity, one initially unconfigured volume, a separate evidence disk, pending
Fast bytes and observed Windows removal. Reconnect uses a run/case/disk/volume
acknowledgement; a stale/missing acknowledgement or unknown eject outcome defers restoration. The oracle
requires a fresh empty cache lifetime and byte match. This first case does not
cover surprise removal or the full handoff matrix. Disk extent checks and rollback
identity checks were tightened. Configuration mutexes are now per physical disk;
CLI low-level mutations use the same transaction gate. Surprise-event pending
bytes use the synchronized published snapshot. No ordinary admission path changed.

Verification: host managed build and protocol/desktop fixtures passed; Windows
host-safe coordinator tests passed for success, missing observed removal, stale
acknowledgement, cancellation, failed eject worker and failed presence query.
The final Windows run also verified that same-disk transactions serialize while
another disk remains configurable. Native CI confirmation remains required.

Loaded VM build 0.4.239.1 (`fe54353`) under Verifier `0x209bb` corrected the GPT
reserved-partition preview. W: held 8 MiB pending in a runtime-only 256 MiB
Fast/Deferred cache; `disk eject W:` drained it and Windows accepted removal, but
the device remained online. The CLI returned nonzero because disappearance was
not observed. This is INCOMPLETE orderly-eject qualification. Proxmox detach of
only `vm-109-disk-5` reported a controller hot-unplug error; guest event 1000 showed
a veto (type 13), followed by event 1010 confirming disk/volume surprise removal.
Two `qcachelab` System event 157 packets survived with zero pending-byte snapshots.
Their message resources are absent; raw XML/binary is the collected evidence.

The backing volume remained intact. Live reattachment did not add it to QEMU;
after preserving evidence and an explicitly authorized orderly VM shutdown/start,
the same volume GUID reappeared on spare SATA1. All 8 MiB matched SHA-256
`A4E1A7A878555AEED5B189E1A5E93586FD147CBE595B18F9E47EA0DDACFC6D6F`.
W: had no cache, Q:'s saved 2 GiB Fast task restored healthy. This proves the
drained file survived that sequence; it does not prove dirty surprise containment,
live reconnect, cancelled-query restoration or lower-operation races.

Evidence retained on the VM in `C:\QueueCache-Results\Disconnect-20260929`:
`DeviceManagement.evtx`, `System.evtx`, `target-pnp.json`, `qcache-surprise.xml`,
`oracle.json`, `reconnect-oracle.json`; hypervisor task/config evidence remains
private. Earlier W: plan-70 `quick` run
`QueueCache-Verify-20260929-170517-fd6cbcc40537477bbdabe6a9ad32db09` completed 1/1
with FINISHED/status/results/log/raw checks and clean restoration; TRIM checks
were SKIP on this bus. The new plan-71 removal case still needs an exact-build VM
run. Read-only preview on the SATA1 connection reports Windows does not identify
the disposable disk as removable/ejectable; its disk number is now 1. This bus
cannot qualify orderly eject. Two-volume, fault, dirty surprise, FAT32/ReFS, changed-letter identity and
repeated-cycle qualification remain open.

### Direct removal admission and ownership audit, 2026-09-30

Implementation: `QcCacheDisconnect` now publishes Gone while holding the RAM
admission Mutex for both surprise and direct final removal, publishes a coherent
pending snapshot and wakes cache/drainer/paging waiters. Dispatch rejects later
data/control traffic before bypass lanes, continues PnP/power/close/cleanup
handling, and forwards surprise notification promptly. Duplicate query preserves
the first pre-query Enabled setting; failed lower cancel does not reopen it.
Existing lower IRPs retain ownership until actual completion; no timeout permits
freeing them. The lock/lifetime map is in `DRIVE_DISCONNECT_OWNERSHIP_AUDIT.md`.

Verification: source audit only for this follow-up. Native CI and targeted VM
qualification remain required, especially direct remove without query, query
cancel/veto, copied/pinned requests at the cutoff and lower callbacks in flight.
The prior host tests do not verify these native transitions. Explicit preparation
protocol, versioned lifecycle counters and the remaining removal matrix are open.

### Desktop removal refresh checkpoint, 2026-09-30

Implementation: inventory signatures include disk number and physical/volume size;
retired-card sampling failures are ignored like retired successes. The Windows
sampling service checks the volume GUID before and after reading live telemetry.
Flush/Clear read cache use the per-disk management gate and bind the expected
volume GUID, including unformatted cached volumes.

Verification: managed solution builds without warnings. Desktop fixtures pass,
including a re-enumerated volume with an unchanged GUID/new disk number and a
late sample from the retired card that cannot update its replacement. The native
Windows GUID checks still require Windows service integration verification;
settings dialogs/other mutations across reconnect remain a review item.

Eject confirmation follow-up: the desktop passes its confirmed preview to the
shared operation. Preview now records physical size and each volume GUID;
execution validates those identities before preparation and again before native
eject. The removal worker binds the recorded target through the same check.
Host tests reject replaced GUIDs, missing identity data, changed disk number,
PnP identity, physical size and volume membership; case differences are accepted.
This prevents a replacement disk at the same letter from inheriting an earlier
eject confirmation. Managed build and protocol/desktop tests pass; Windows VM
execution of the new preview binding remains unverified.

### CI and cached NTFS checkpoint, 2026-09-30

Implementation commits: `925dc48` (maintained removal suite and per-disk gates),
`9103f2b` (direct/surprise admission cutoff and ownership audit), `b9c6dbb`
(desktop identity/refresh), `5bd2073` (confirmed eject identity binding). CI native
Debug/Release and Windows host contracts passed through `5bd2073`; this is build
verification, not removal-race qualification.

VM build 0.4.243.1 (`9103f2b`) was loaded under Verifier `0x209bb`, confirmed by
module load count 1/unload count 0. Registered/loaded module filename matches
SHA-256 `CB07097679FAF288E806D84B95ECFA02D9D0A106FC42137373AAA815D820F79E`.
The volume registration is topmost on all volumes, absent on disks, with no
registration problems. Q:'s saved task restored successfully.

Plan-71 removal run
`QueueCache-Verify-20260930-140253-4b9d5ad434674cee98e2b923a68e1203`
finished INCOMPLETE 0/1 at preflight because SATA1 is not Windows-ejectable. No
cache preparation or physical removal occurred. FINISHED/status/summary/results/
log and exact worker error were inspected.

A temporary runtime-only 256 MiB Fast/Idle cache on W: was then verified by
`QueueCache-Verify-20260930-140353-7775a062bc8c4fc9909ac720e7484c2f`:
COMPLETED 1/1, live unbuffered read/overwrite, random overwrite, explicit drain/
reopen, copy/rename and settings/health PASS. File-level TRIM is SKIP (Win32 326),
as are the suite's declared exclusions. Raw evidence shows RAM accepted writes,
not merely an uncached file run. Restoration readiness is true; six exact control
trace samples show zero errors, lower-attempt counters and a clean disabled
boundary. Quick has no interval measurement files. Restored runtime policy and
budget matched, then the temporary cache was removed. Q: remained healthy;
C:/T:/W: have no cache. FINISHED/status/summary/results/log/raw and restoration
snapshots were inspected. This is a normal cached-file regression, not proof of
new removal/cancel/lower-callback paths.

### Removal topology / veto checkpoint, 2026-09-30

Implementation: structured Windows veto results preserve native codes, disk
presence and rollback failures. The maintained runner restores a confirmed-present
veto, while uncertain outcomes still defer restoration. Eject preview now resolves
at most the immediate dedicated storage adapter and verifies all descendants and
removal relations. Related normal/hidden volumes are owned only after native
single-disk extent checks; instance names are never parsed to infer ownership.
Preview exposes relation results and extent evidence. Shared/unknown scopes refuse.

Verification: on CI build 0.4.247.1 (`5bd2073`) under Verifier 0x209bb, the VirtIO
disk-node request in
`QueueCache-Verify-20260930-141853-0973fc409d77408d9c01b3697441012a`
was vetoed (Configuration Manager 23, veto 8) after 8,396,800 dirty bytes were
prepared. No removal occurred; eject rollback drained/resumed the original cache.
The older runner correctly deferred its uncertain worker outcome; supported
verify-recover restored the original unconfigured W: state. This is veto evidence,
not successful removal. Raw original outcome and recovery remain preserved.
A subsequent read-only locally published preview resolved both volume extents
through normal/hidden interfaces and accepted only this disk's dedicated adapter.
This preview is diagnostic evidence, not CI driver removal qualification.
Linux protocol checks, desktop fixtures and Windows host runner/protocol
contracts pass for the new resolver and structured veto handling. Verification
plan 72 records the topology/veto contract change. Matching CI VM removal
qualification remains pending. Exact recovery evidence shows readiness true,
three telemetry samples with zero dirty/inflight/error counters, successful owned
worker exits and an unconfigured clean final runtime (budget zero). Recovery uses
its recovery-result marker; it does not create a normal suite FINISHED/report set.

### Orderly evidence refinement, 2026-09-30

Implementation: plan 73 captures volume state and lower-attempt counters around
cache disable plus filesystem flush success. Removal acceptance requires matching
identity/lifetime, pending writes, clean disabled state, advancing lower write and
flush attempts and the reconnect hash oracle. Missing counters remain unsupported.
A verified reconnect allows restoration even when durability evidence is missing,
but cannot turn that incomplete case into PASS. Pure rejection and maintained
runner contracts cover absent, unchanged/regressed counters and invalid states.

VM verification on build 0.4.249.1 (`1752b13`), Verifier 0x209bb, loaded module
`QueueCache-0.4.249.1-A31F6D68C608.sys`, load 1/unload 0, SHA-256
`A31F6D68C608A94B09B0A45AD8F1BD08D1494783E4E2895AFBF3595401755F91`:
`QueueCache-Verify-20260930-152732-c06e5ce0a2de454d88733777e86e8be7`
prepared 8,396,800 dirty bytes with zero drained bytes, then Windows accepted eject
(native result 0) and disk absence was observed (presence 13). The exact owned
workers exited successfully. Live reattachment is currently blocked by a Proxmox
orphan throttle object: the API token cannot execute the root-only cleanup.
The backing disk is preserved; no guest reset/reboot occurred during this case.
The reconnect deadline expired at 15:43:03 UTC: FINISHED/status/summary/results/log
and all 34 raw files were inspected. The run finished RESTORATION_FAILED because
reconnect was unresolved; no recovery writes or VM reset were attempted. The owner
subsequently detached the disk and instructed that it stay detached. Do not retry
reattachment or recovery without a new owner instruction. This is successful native
eject evidence but not a successful reconnect case. Plan-73 durability refinement
has not run on the VM. Future reconnect timeouts now report the specific missing
phase instead of only an opaque cancellation error.

Desktop implementation follow-up: Save/settings, pause/resume, cache removal,
flush and drop-clean now pass the selected VolumeDescription through shared
operations. Native discovery must match GUID, PnP identity, disk number, physical
and volume sizes before mutation; the recorded target is checked again under the
per-disk gate. Host contracts reject replaced GUIDs, devices, letters, disk numbers
and lengths. This closes stale-dialog binding beyond flush/drop-clean. Real Windows
UI transactions across reconnect remain a VM verification gap.

Final host verification for plan 73 and the desktop binding follow-up: Linux
protocol tests and desktop fixtures passed; the Windows self-contained management
protocol/runner contracts passed, including missing preparation evidence and
selection-identity rejection. These tests perform no native driver workloads.
The delivery/review packet is docs/DRIVE_DISCONNECT_DELIVERY_SUMMARY.md.

### Native Windows eject and default CDM checkpoint, 2026-09-30

Implementation: plan 74 adds the maintained opt-in `disk-removal-windows` suite.
It records enabled/dirty cache state, closes observation handles and requests the
native eject API without product preparation. Missing precondition proof fails;
reconnect still requires the exact disk/volume and fresh-instance/hash oracle.
Final topology revalidation and unique cryptographic oracle bytes are implemented.

Verification: Linux and Windows management/runner contracts passed; managed
Release build has zero warnings/errors. On loaded CI 0.4.249.1 under Verifier
0x209bb, native eject accepted W: with 8,437,760 dirty bytes, enabled cache and
zero drained bytes. Removal was observed; same-slot Proxmox reattachment failed
on orphan throttle-drive-virtio2. The exact run
`QueueCache-Verify-20260930-174817-7361fc07ecb34be9aa87d1f462c4557d`
finished RESTORATION_FAILED at 18:03:35 UTC; all 35 nonempty raw files and final
reports were read/preserved. Data survival and native query/drain routing remain
unverified. Final scope/unique-oracle refinements were added after VM publish and
are host-covered only. The disposable disk is detached with backing preserved;
no reset/reboot/recovery writes occurred.

CrystalDiskMark 9.0.3 Default completed all eight scores on Q:, five 1 GiB runs,
2 GiB Fast/Idle cache, Verifier enabled. Full scores, IOPS, latency, provenance
and limitations are in WINDOWS_EJECT_AND_CDM_20260930.md. This is measured output,
not a controlled performance acceptance verdict.

### Performance investigation follow-up, 2026-09-30

Implementation: no driver, runtime-default or workload-contract changes.
Verification: historical 0.4.219.1 CDM-shaped results used Verifier off and
prewarmed files; the latest GUI Default run used Verifier 0x209bb. A maintained
plan-74 selected write-performance run on loaded 0.4.249.1, same CDM DiskSpd
hash and 2048 MiB budget, completed 3/3 with clean restoration:
`QueueCache-Verify-20260930-184356-d3b405509bc34e79aca0c0a643827b0b`.
Random Q1 Idle timing-off median was 94.529 MB/s; working caller path, zero new
capacity waits/errors in the recorded intervals, readiness true and coverage
complete. All final/raw evidence inspected. This reproduces the low speed;
Verifier overhead is a hypothesis, not an isolated cause or accepted regression
verdict. Matched Verifier-off measurement requires a planned restart and remains
pending. See PERFORMANCE_INVESTIGATION_20260930.md for comparisons and limits.

### Approved Verifier-off follow-up, 2026-09-30

Implementation: no source/default changes. Owner approved the benchmark restart
and restoring Verifier afterwards. Loaded 0.4.249.1 kernel-module path/hash, same
CDM DiskSpd hash and original Q: identity/profile were confirmed after reboot.
Runtime Verifier reported no verified drivers during measurement.

Verification: actual CDM Default, 5 × 1 GiB, completed all eight scores. Random
Q1 returned to 1327/1074 MB/s read/write; Q32 reached 1791/1403 MB/s. Identical
maintained random Q1 Idle/timing-off run
`QueueCache-Verify-20260930-200812-c19de70a9d634c3f9fe237964eb0d2d7`
completed 3/3 with clean restoration; median 973.255 MB/s versus Verifier-on
94.529 MB/s (10.30×). The focused sequential Q8 write run
`QueueCache-Verify-20260930-201147-2e87df2dd0534faaa041ab030fcd2a07`
also completed 3/3 with clean restoration, median 15449.857 MB/s. Caller service
and large-write copy offloads respectively advance; neither collection has new
capacity waits/errors. All raw/final/ready/interval/control evidence read.

Conclusion: Verifier explains the main random slowdown; queued sequential GUI
and focused write scores remain below older custom CLI measurements. Historical
preparation/scoring/observer and VM/host conditions are uncontrolled. No matched
older-driver bisect was performed; the residual sequential gap remains open.
Full throughput/IOPS/latency and commands: PERFORMANCE_INVESTIGATION_20260930.md.

Post-benchmark restoration: second approved planned restart confirmed runtime
Verifier 0x209bb, resetonbootfail, intended module load 1/unload 0. Saved Q:
profile was preserved and reapplied through `policy restore`; final 2048 MiB
Fast/Idle cache is active, clean and error-free. No CDM/verification workloads
remain; disposable disk still detached with backing preserved. This planned
restart does not qualify data survival in the earlier incomplete removal case.


### Latest-build warmed sequential reproduction, 2026-09-30

Implementation: plans 75–77 add the opt-in maintained `sequential-resident`
suite, requiring a fixed fitting 1 GiB file and 2048 MiB budget. A complete cold
read pass and a second stable/retained zero-miss RAM-hit pass must succeed before
scoring. Readiness and interval coverage remain strict. Plan 77 distinguishes
per-I/O (`-Zr`) and precomputed (`-Z1M`) random write buffers with immutable
case IDs; three repeats define nine cases, excluded from full. Existing 72-case
write-performance remains unchanged. Host contracts cover insufficient warmup,
misses/hits/retention/identity, in-flight double counting, case selection and
buffer arguments. No native performance code or defaults changed.

Verification: Linux and Windows host-safe contracts passed; managed Release
build had zero warnings/errors. Latest-at-start signed CI 0.4.259.1 loaded
module path/hash matched its artifact after approved installation/restart;
Verifier was off. Plan 75 run
`QueueCache-Verify-20260930-203416-e78ae9299b8b41539f22a68970a7047b`
failed its cold residency preparation, was cleanly restored and preserved.
Plan 76 run `QueueCache-Verify-20260930-204030-f95dbd5c55834e9a9f2a42d62267cd00`
completed 6/6; read median 36,185 MB/s, fresh-random write median 16,521 MB/s.
Plan 77 `precomputed` subset
`QueueCache-Verify-20260930-205241-8657fa00c7c342e99769827220a2145d`
completed 3/3, write median **21,406 MB/s**. Both exact collections have full
raw/final/ready/interval/ownership/control/recovery/restored evidence inspected,
zero new score-observation capacity waits/errors and clean restoration. Neither
combines incomplete repetitions or asserts the full plan-77 matrix ran.
Requested 36k/21k peaks are reproduced. CrystalDiskMark uses precomputed random
write buffers, while the established write-performance suite generates fresh
random data per I/O; comparisons must preserve this distinction. A new actual
GUI benchmark and matched old-driver bisect were not performed.
See SEQUENTIAL_PEAK_REPRODUCTION_20260930.md for all repetitions and limitations.

Post-benchmark restoration: approved planned restart restored Verifier 0x209bb,
resetonbootfail, current CI driver load 1/unload 0 and matching signed-artifact
hash. Q: saved 2048 MiB Fast/Idle profile is Active, clean and error-free. No
owned benchmark process remains. Read-only Proxmox check confirms unused eject
backing vm-109-disk-5 is preserved and detached. This restart is a planned
benchmark-state restoration, not automatic recovery or a removal-path proof.


### Managed disks planning, 2026-10-03

Owner scope: three UI modes — pure RAM disk, VHDX-backed disk with independent
RAM cache, and complete VHDX image loaded into RAM. Pure RAM with startup enabled
must create/partition/format a new empty disk at each new Windows startup;
image-backed and full-image modes preserve their existing filesystem/data.

Planning: [RAM_DISK_UI_PLAN.md](RAM_DISK_UI_PLAN.md) revision 3 contains the
three-mode UI contract and S01–S64 scenario matrix.
[RAM_DISK_IMPLEMENTATION_PLAN.md](RAM_DISK_IMPLEMENTATION_PLAN.md) defines the
native provider, shared kernel RAM budget, broker/ownership, complete logical
image transfer, consistent versioned image saves, startup/recovery, installer
changes and maintained verification handoff for Luna or Sol. Full-image saves
preserve the imported source and publish verified checkpoint generations; an
ordinary file flush in RAM does not save an image. CLI product design remains
later; shared operations and necessary internal service/runner plumbing are
part of the UI implementation.

| Work package | Implementation | Verification |
|---|---|---|
| RD01 — contracts, resource schema and UI flow | Partial: shared definitions/capabilities/runtime, cache configuration composition, creation flow, typed VHDX primitives, checksummed durable catalog/journal with predecessor and removal tombstones | Management/desktop foundation contracts passed; catalog recovery contracts added; native VHDX primitives not VM-tested |
| RD02 — native provider / isolated image-transfer prototype | In progress: Storport provider, private allocations, bounded service ABI, SCSI data plane, native identity/layout/volume/format wrappers, signed package/root installation and maintained `managed-provider` proof suite | Native Debug/Release and signed package CI 37112939166 passed; new native staging/filesystem tests not yet run on VM |
| RD03 — RAM provider and one shared memory budget | In progress: cache shared helpers, kernel-only owned reserve/release endpoint, provider references, redundant-cache refusal and strict managed ABI | Native Debug/Release CI passed; managed ABI contracts passed; cross-driver VM proof pending |
| RD04 — mounted VHDX with existing cache | Source implemented on feature branch: native mount/create, owned GPT/NTFS binding, independent shared cache transaction, explicit flush/drain/detach and veto restoration | Host build passed; product fixed/dynamic Strict/Fast VM case added, not run |
| RD05 — pure RAM create/format/stop | Source implemented: full reservation, owned publication/format, explicit generation-bound discard, fresh recipe restart | Host definitions/UI/identity contracts passed; product VM case added, not run |
| RD06 — whole-image import and RAM operation | Source implemented: read-only letterless import, complete copy plus RAM digest verification, private GPT/NTFS/CRC validation, source detach before publication | Host transfer and corrupt/encrypted-layout tests passed; source-unavailable runtime/reload VM case added, not run |
| RD07 — consistent checkpoint save/export/recovery | Source implemented: volume lock/native freeze, full-sector copy/flush/detach/read-back, durable journal/pointer, predecessor retention, export and save-stop | Host fault tests cover acquire/copy/flush/verify/journal/pointer/cleanup and cancellation; native crash/full-host/save-stop paths not VM-run |
| RD08 — broker/startup/power/package lifecycle | Source implemented: LocalSystem SCM host in qcache, authenticated bounded local IPC, exact-generation adoption, native cold/hybrid startup epoch, opted-in preshutdown save, installer preflight and one startup coordinator | Host framing/identity contracts passed; SCM, power, upgrade/uninstall and native classifier require Windows qualification |
| RD09 — completed three-mode UI and product CLI | Source implemented: creation and managed cards, all planned lifecycle/checkpoint/cache/startup/removal/recovery actions, shared cache editor, explicit erase/discard with fresh identity; physical discovery retained separately | Desktop creation/action/dashboard contracts passed; Windows CLI parser checks expanded; native UI walkthrough and CLI execution remain unrun |
| RD10 — maintained verification integration | In progress: plan 81 adds broker-restart and durable managed lifecycle prepare/verify/cleanup phases; plan-80 blank-image fixtures/image-I/O attempts and plan-78 provider proof remain maintained | Host suite/epoch/identity/oracle contracts added; no new cases have run on VM. Actual cross-boot/crash/power and injected-native-failure qualification remains outstanding |
| RD11 — correctness/performance qualification | Partial: existing maintained cache suites reused without workload changes; native managed-disk qualification outstanding | Foundation: 9/9 cases, 74 raw PASS, 4 SKIP. Old 0.4.264.1 write-performance baseline `20261003-092326-b5e1ac5fb38b4cd4b25014258222610e` INCOMPLETE 4/72: pre-case Flush timed out at 180s; restoration succeeded, evidence preserved. No matrix/performance acceptance claimed. |

This entry records an initial implementation milestone, not completion or VM
qualification of the new managed-disk modes. The initial foundation refused all
creation; the subsequent feature-branch source supplies the provider, broker and
product operations. Activation requires the matching running broker and native
ABI. Simultaneous cross-driver memory enforcement and platform lifecycle contracts
remain unqualified until their Windows evidence is recorded.
Existing historical cache/driver verification does not qualify
the new RAM provider, source import, checkpoint commit or power-state contracts.

Source milestone `e64f163e1338e85a7ff443fd34db57b8b9a4b516` on
`feature/managed-disks`: local managed Release solution build completed with
zero warnings/errors, and both existing test executables passed with the new
contracts. [CI run 37103995071](https://github.com/devedse/QueueCache/actions/runs/37103995071)
passed native Debug/Release builds, Windows host-safe management/desktop tests,
CLI contracts, package checks and lab installer creation. Exact CI logs were
inspected; the four existing warnings in untouched cache I/O code remain, with
no warnings in the extracted shared headers. No driver was installed, test VM
restarted, or real RAM disk created for this initial milestone. The subsequent
installation below covers existing-cache correctness; performance and new native
modes still require qualification.

### Managed disks shared-cache VM verification, 2026-10-03

Implementation: no additional source/default/workload changes. Owner approved
installing the completed build, rebooting and testing. Signed Release x64
0.4.264.1 from commit `8e29bb159db10660ffcaa73ab07c54059993f66e`
([CI 37104360105](https://github.com/devedse/QueueCache/actions/runs/37104360105))
was installed using the existing installer. One planned reboot loaded the intended
immutable kernel-module path with signed-artifact SHA-256 verified; Driver Verifier
remained active at `0x209bb`, resetonbootfail, new module load 1 / unload 0.

Verification: plan-77 `quick`, `policies`, `pressure` on disposable SATA T: and
`volumes`, `trim-cache` on the existing developer lab VHDX completed **9/9 cases,
74 raw PASS, 4 SKIP, 0 FAIL** with clean independent restoration. Policies included
both injected allocation failure rollbacks, zero lower-I/O-attempt fitting-write
intervals and exact concurrent/persisted bytes. Pressure preserved capacity
backpressure; volumes proved independent sibling caches, harmless physical-disk
discovery, resize recovery and VSS contents. All three TRIM cases passed, including
in-flight drain guards. The SATA file-level TRIM case was unsupported and remains
skipped; the quick run itself preserved zero-cache pass-through state.

All exact final/raw/ownership/recovery/control/restoration evidence was inspected
and retained privately. Original Q: 2 GiB Fast/Idle profile/settings are unchanged,
active, clean and error-free; global reservations returned to exactly 2 GiB after
every suite. Temporary caches are released, lab VHDX returned to detached state,
and no test workload remains. No performance benchmark/comparison, new FAT32/ReFS
run, production VHDX wrapper test, native RAM creation or boot/image-save tests were
performed. Fully controlled old/new completion ordering remains open. Full commands,
build hashes, exact run IDs, skip reasons and limits:
[MANAGED_DISKS_FOUNDATION_VERIFICATION_20261003.md](MANAGED_DISKS_FOUNDATION_VERIFICATION_20261003.md).


### Managed-disk broker, product actions and checkpoint source milestone, 2026-10-03

Implementation: on `feature/managed-disks`, all planned product CLI action bindings
and corresponding UI actions now call the shared service API. The SCM broker owns
Windows/native lifetime, authenticates local elevated callers, uses bounded framed
IPC with streamed inventory and correlated cancellation, and reconciles exact
resource/boot/creation identities. Catalogs and host directories are protected;
managed host volumes cannot become Fast or depend on managed disks. Volume
association removes duplicate generic startup profiles. Save/export freezes RAM,
verifies complete logical sectors in a detached candidate, commits the durable
image pointer and retains source/previous files. Private decoded GPT headers and
arrays are checked before image publication, including NTFS/encryption rejection.
Update preflight runs before Inno file replacement; the existing binary hosts the
service and the existing installer registers it. No new installer/test executable.

Verification: managed Release build and Linux management/desktop contracts pass.
New contracts cover checkpoint failures/commit cancellation, framed/truncated IPC,
selected erase generation, GPT checksums/bounds and encrypted-source refusal, plus
UI discard/format/export/recovery and duplicate-card behavior. Windows CLI help
and pre-broker validation contracts are added to CI. Native startup-context changes
require the next native CI build and Windows proof. Driver/power/SCM/installer
success is not inferred from these host checks.

Follow-up source review: the broker now starts automatic recipes and restores
ordinary saved profiles only at a proven new Windows startup. Restarting the
broker preserves an intentionally stopped disk and runtime-only cache settings.
A checksummed coordinator marker records completion without using uptime or a
service PID. Host contracts pass. Windows CI 37128721293 passed both native
builds and management/desktop tests, but exposed a malformed-GUID CLI validator
returning runtime exit code 1 instead of syntax exit code 2; the validator is
fixed in source. These fixes still require the next Windows CI/VM checks.

Windows blocker: signed 0.4.269.1 from commit `980e5c83`/CI 37114226898 was
staged and installer PID 7452 started. SSH disconnected during provider installation
and TCP 22 subsequently timed out. The owner's screenshot and a direct Proxmox
console capture confirm the test VM is in Windows Recovery with automatic repair
failed; it reports `D:\WINDOWS\System32\Logfiles\Srt\SrtTrail.txt`.
Installation success, loaded provider identity and the underlying crash/boot
failure cause remain unproven. Console access using the previously supplied API
token is restored; owned installer/evidence paths and console captures stay private.
No automatic reset/reboot was used as recovery. This blocks native qualification,
not ongoing source implementation. Cross-boot/crash/power/native-fault coverage,
exact loaded-provider evidence and the complete same-binary performance comparison
remain required work. The old 72-case baseline is incomplete, not an acceptance.

### Managed disk scenario completion follow-up, 2026-10-03

Implementation: S16 now has a shared explicit blank-image initialization contract,
UI checkbox and `disk create --load ... --initialize-raw`. Complete logical zero
validation refuses nonempty/corrupt images; source identity is rechecked before
allocation and formatting. Image-in-RAM preserves the blank source and creates a
separate initial checkpoint. Consent is one-shot, with no repeated startup erase.
Stopped recipe editing is exposed in the UI and `disk configure`; only pure RAM
capacity may change. Managed dashboard cards report actual native reserved RAM
and counters, and refresh ordinary inventory when managed ownership changes.
Image-sector read/write/flush attempts include failed attempts and separate
completion bytes, with a new observation epoch on broker restart. Plan 80's
maintained image cases cover explicit blank initialization and require unchanged
attempts during ordinary whole-image RAM I/O with its source unavailable.

Verification: managed Release compilation has zero warnings/errors; management
and desktop contracts pass, including complete blank-sector/tail/error checks,
identity-bound initialization, stopped recipe restrictions and failed I/O attempt
accounting. The preceding startup/CLI fixes passed Windows Debug/Release native,
management, desktop, CLI and signed-package CI 37130121135 (`ebecff3`). The new
scenario additions still require their matching Windows CI build. None has run on the inaccessible VM;
the outstanding installer/console blocker and native qualification gaps remain.

Follow-up recovery implementation: save/catalog reporting failures preserve the
original error while independent thaw still runs; initial checkpoints restore
only an existing letter, preventing premature publication. Creation records the
actual owned GPT/volume before formatting or letter assignment. Read-only RAM is
armed before Windows publication. Interrupted creation/formatting cannot be
relabelled Ready without its durable completion boundary. Ordinary cache
reconfiguration/control/removal refuses managed volume ownership; the broker
uses the same shared cache transaction with its exact resource owner. Read-only
IPC observations are bounded independently from mutation cancellation/commit.
Elevated developer directory creation uses an eligible Administrators owner;
the SYSTEM broker uses SYSTEM, with no implicit restore privilege activation
([Windows owner rules](https://learn.microsoft.com/en-us/windows/win32/api/aclapi/nf-aclapi-setnamedsecurityinfoa)).

Verification: recovery/catalog-reporting and interrupted-format adoption host
contracts pass; managed build and desktop tests pass. The S16/settings/I/O-proof
changes passed Windows Debug/Release and signed-package CI 37131210508 (`b164db6`).
The subsequent hardening still needs matching CI and VM qualification.

Lifecycle verification implementation: plan 81 adds an owned SCM broker-restart
case and independent prepare/verify/cleanup phases with five fixtures, persistent
file hashes, startup/native identities, pointer checks and exact shared-budget
cleanup. The preparation marker explicitly does not qualify a transition. The
operator selects/performs the actual transition; no automatic reboot, sleep,
hibernate, reset or process kill is implemented. Failure retains resources and
raw evidence. Host acceptance contracts reject changed epochs, incomplete/duplicate
manifests, missing hashes, stale creations, changed pointers and recovery states.
Native disappearance is reported as possible volatile loss, never inferred save;
stopped image cards describe the retained checkpoint rather than live Saved RAM.

Verification: local Release compilation passed with zero warnings/errors;
management and desktop contracts passed, including the new lifecycle acceptance
contracts. Matching Windows CI is still required. Cross-boot/power/SCM/native-crash
evidence has not been collected while Windows remains in Recovery;
prototype isolation, read-only/4Kn, failures,
installer and complete performance qualification still need actual Windows runs.

### Test VM recovery investigation and boot identity correction, 2026-10-04

Verification evidence: direct Proxmox API console access was recovered from the
owner's previously supplied private credentials. Windows Recovery permits reading
the OS volume as D:. No current failure minidump or `MEMORY.DMP` is present.
The October 3 installer transcript ends after the volume-class filter registration;
SetupAPI records importing the RAM-provider package and creating its service.
A copied offline SYSTEM hive still selects the new 0.4.269.1 qcache ImagePath,
but has no RAM-provider service entry; this disagreement does not prove the
underlying crash or repair action. The old 0.4.264.1 driver file is present.
The current SYSTEM hive was copied to the owned installer evidence directory
before any proposed recovery edits. Original registration and logs are retained.
The owner approved the proposed scoped rollback and one normal recovery boot.
The committed SYSTEM hive was saved before changing only qcachelab's ImagePath
to the retained 0.4.264.1 immutable file. Windows booted successfully and SSH
returned. No hypervisor reset was issued. The failed package, original hive and
installer/repair logs remain available. This narrows the investigation but does
not establish the crash cause.

Implementation: review identified an independent boot hazard: the boot-start
volume filter returned failure when `ExUuidCreate` could not generate an epoch.
Microsoft documents `STATUS_RETRY` while UUID generation is unavailable
([ExUuidCreate](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddk/nf-ntddk-exuuidcreate)).
Epoch seeding is deferred to an actual later service query; an unavailable UUID
fails that query without fabricating identity or failing the filter's boot load.
Hybrid transitions preceding the first query are preserved under the same spin
lock, and concurrent successful queries retain one chosen epoch. Compile-time
contracts cover deferred seeding and those transition/race invariants.

Verification: hardening/lifecycle commit `5590d19` passed native Debug/Release,
management/desktop contracts, CLI checks and signed-package CI 37158510520.
The boot identity correction `9c16e99` passed both native builds and the managed,
CLI and signed-package checks in CI 37159070887; an actual loaded-driver boot is
still required. It is a plausible boot-risk fix, not a confirmed explanation of
the install failure. Windows/native/performance acceptance remains outstanding.

Incomplete-creation recovery implementation: explicit Stop now reuses the creation
cleanup path when a published RAM disk or owned VHDX lacks its completed GPT/volume
binding. It validates the actual native creation or image identity, geometry and
physical attachment first, locks every enumerated volume and drains any backing
cache before removal/detach. It does not format or infer Ready from a partial
creation. Reconciliation retains those attachments for this explicit action;
RAM requires fresh discard intent. Successful abort also clears the publication
flag before a later creation. A foreign managed cache owner blocks cleanup.

Verification: managed Release compilation passed with zero warnings/errors;
management and desktop contracts passed. Contracts distinguish incomplete error
states from fully bound resources and stopped recipes. Actual interrupted native
creation/Windows veto qualification remains pending the recovered VM and matching
CI; host tests do not prove physical detach ordering.

### Interrupted provider installation repair and loaded-module observation

Implementation: update preflight distinguishes an unbound inactive/disconnected
owned adapter from a bound but unavailable provider. Repair requires a missing
service binding, explicit devnode status and successful module enumeration proving
qcramdisk is absent. Unknown status, missing privilege, started nodes, loaded
provider modules and bound unavailable providers still block. The installer
removes/recreates only that proven unbound root node and includes disconnected
nodes when checking for duplicates. Live/private allocations and catalog recovery
states retain their existing update/uninstall vetoes. Inno preflight uses its
bundled CLI from temporary storage before replacing installed recovery tools,
so an older CLI's inability to recognize the interrupted state cannot prevent
safe repair.

The maintained `qcache developer driver loaded` diagnostic and runner provenance
now capture loaded paths through PSAPI, with temporary process SeDebugPrivilege
restored afterwards. Windows 11 24H2's successful-but-null address result is an
unavailable observation, never proof of absence. File hashes describe the current
on-disk files; qualification must match the filter's immutable loaded filename
and the signed artifact hash, rather than treating SCM registration as loaded
identity. This adds provenance without changing workload/score contracts.

Verification: the recovered VM reproduces the old preflight failure and has an
unbound/disconnected adapter, no qcramdisk service and no current dump at either
the OS or configured Q: dump location. Host repair-policy contracts reject unsafe
and incomplete observations. Matching CI, the actual installer repair, subsequent
loaded-driver boot and full Windows qualification remain outstanding.

Native adapter initialization correction: review against Microsoft's
[HW_INITIALIZATION_DATA contract](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/storport/ns-storport-_hw_initialization_data-r1)
found that the provider omitted STOR_FEATURE_VIRTUAL_MINIPORT while supplying
virtual service callbacks and the seven-argument virtual FindAdapter. DriverEntry
now explicitly declares virtual miniport, legacy SCSI_REQUEST_BLOCK and BTL8
addressing support before StorPortInitialize. Its physical-page transfer limit
is bounded by the maximum transfer size plus an unaligned edge page, rather than
MAXULONG. ConfigInfo.VirtualDevice remains set during FindAdapter; that later
configuration is not a substitute for the initialization type declaration.

Verification: d81424b's repair/observation code passed native Debug/Release,
management/desktop, CLI and signed-installer CI 37175177698. Its maintained
loaded-module diagnostic and unbound-node repair preflight both passed on the
recovered VM with the 0.4.264.1 filter; no provider was installed during those
observations. The adapter declaration correction still needs matching native
CI and real installation/lifecycle proof. It is a concrete initialization defect,
not a confirmed explanation of the original crash. The fresh, separate 72-case
baseline runs on T: with the same DiskSpd hash, 2048 MiB budget and a recorded
3600-second preparation flush deadline; it is not accepted until complete.

Managed geometry verification implementation: plan 82 makes provider and product
fixtures cover both 512-byte and 4096-byte logical sectors through one shared
immutable geometry contract. Provider raw evidence is separate per geometry with
an explicit enclosing completion index. Additional checks require native rejection
of three invalid transfer ranges without generation changes, refusal of redundant
RAM caching without an extra reservation, and an actual protected physical-sector
write with unchanged bytes/generation. Score workloads and the complete 72-case
performance contract are unchanged; the running plan-81 baseline is preserved.

Verification: adapter declaration commit 3bcbcb5 passed native Debug/Release,
host management/desktop contracts, CLI and signed-installer CI 37175645554.
The additional plan-82 scenarios still require host build/contracts and matching
Windows execution. Their source existence does not qualify either geometry or
native write-protection behavior.

Verification update: plan-82 commit 2c53b6a passed native Debug/Release,
management/desktop contracts, CLI and signed-installer CI 37176587183. Signed
0.4.278.1 is downloaded for qualification; it has not been installed. The fresh
T: baseline remains in progress on the recovered 0.4.264.1 filter, with its exact
run evidence retained. No complete comparison is claimed.

Managed TRIM verification implementation: plan 83 adds file-relative TRIM of
the middle 1 MiB of a newly owned 3 MiB RAM file in both sector geometries.
Acceptance requires an advancing native TRIM count/write generation, unchanged
errors, zero discarded bytes, intact adjacent guards and exact flushed rewrite.
Missing/unsupported native behavior fails rather than becoming SKIP. Primary
test and separate teardown failures are both retained in provider/product raw
evidence and combined if both fail, preserving the original diagnostic.

Verification: host evidence-oracle contracts cover missing progress, errors,
changed boot/creation, corrupt guards/nonzero discarded bytes and incomplete
read-back. Host management contracts passed on Linux without compiler warnings;
Windows runner contracts, matching CI and actual signed VM execution are still
required. Source implementation does not qualify native TRIM.

Product CLI verification implementation: plan 84 adds explicit `managed-cli`
through the maintained runner and existing qcache binary. All three modes and
both geometries execute product create/list/status/startup/recover/flush,
mode-specific save/export/inspect/delete-image/cache, Windows open-file and stale
erase vetoes, format, stop/configure/start and remove. Unique recipe/ID guards
exclude preexisting resources even during independent broker fallback cleanup.
Each nested product child has immutable command, PID/start, exit, stdout/stderr
evidence in the enclosing run, with primary and cleanup failures retained.
Original images survive definition removal; no power transition or ordinary
physical-disk format occurs. Existing 72-case score contracts are unchanged.

Verification: new ownership contracts reject wrong letter/label/path/geometry/
capacity and preexisting IDs before targeting cleanup. Host management contracts
and managed CLI compilation passed without warnings/errors. Plan-83 commit
f36a64b also passed native Debug/Release, Windows management/desktop contracts,
CLI and signed-installer CI 37177476249. Matching plan-84 CI and real Windows
CLI execution remain required; this source addition is not native qualification.

Allocation failure/lifetime implementation: plan 85 extends `managed-provider`
with unique request-local new-creation failures after 1/8/16 allocated 4 MiB slabs
in each geometry. Native proof changes only after that allocation/zeroing boundary
is reached, identifies the exact resource and increments once. Acceptance rejects
ordinary early OOM, changed epoch, wrong resource/boundary, lost objects and any
reservation difference. No hook remains armed; normal product creation is
unchanged. Subsequent normal storage must still be zeroed and writable.

Review also found that reservation release previously called the allocating
IoBuildDeviceIoControlRequest during low-memory teardown. It now uses an owned
[IoAllocateIrp](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdm/nf-wdm-ioallocateirp)
request prepared before reserving memory, with a completion/event and explicit
ownership; it is not attached to the creating thread's IRP queue.
Its actual IRP size is included in the reservation. Failed and successful creation
cleanup can return accounting without allocating another request. Physical pages
still disappear before the reservation is released, and the provider retains
the budget device/file reference until release completes.

Verification: host management contracts passed without warnings/errors. ABI and
acceptance contracts cover bounded request-local failure,
missing/native boundary proof, wrong identity and leaked reservations. Native
Debug/Release CI, real rollback/teardown under Verifier and complete before/after
performance comparison remain required. This source fix does not establish VM
allocation/lifetime qualification.

Fast Startup backed-image reconciliation correction: a permanent VHDX attachment
can survive a hybrid startup. Reconciliation now validates the retained image
file/virtual identity, exact physical number/geometry, GPT and volume identity
and completed Ready journal before draining/stopping that owned attachment.
The authoritative native hybrid counter/epoch must prove the immediately
preceding startup of the same kernel session; cold/foreign/skipped/unknown
startup identities cannot authorize retained-attachment retirement.
It then remembers the new authoritative startup epoch; automatic recipes reopen
through normal creation, while startup-off recipes remain stopped. Same-session
broker restarts retain/adopt the live attachment. A missing/mismatched binding,
incomplete journal or Windows lock veto still blocks; no foreign attachment is
detached. Reconciliation also preserves the Stopped journal boundary after
retiring a prior RAM/image creation instead of overwriting it with Ready.

Verification: host management contracts and CLI Release compilation passed without
warnings/errors. Ownership contracts reject changed file/virtual identity,
physical number, capacity/sector geometry and missing binding, and distinguish
new-startup automatic reopen from same-session/manual recipes. Allocation/lifetime
commit 8a58b30 passed native Debug/Release, Windows management/desktop contracts,
CLI and signed-installer CI 37178446155. Matching reconciliation CI and actual
hybrid-startup/lifetime qualification remain required;
normal restart evidence alone cannot qualify this path.

Managed checkpoint cancellation and native failure coverage, 2026-10-04:
precommit cancellation previously became a generic IOException after successful
cleanup, so broker/UI/CLI could not report the real cancelled outcome. It now
retains the request token and reports cancellation only before commit and when
independent cleanup/catalog recording succeeded. A cleanup/reporting failure
still reports failure/recovery; cancellation after pointer commit remains the
actual committed result. Plan 86 extends the maintained `image-in-ram` suite with
open-file Save veto and existing-destination Export refusal at both geometries.
Required proof includes unchanged checkpoint pointers/destination container hash,
the exact live dirty RAM/volume/GPT binding, and successful writes after failure.

Verification: source and host contracts cover cancellation during copy and
before commit, failed thaw after cancellation, late cancellation, and rejection
of missing/frozen/read-only/changed native or checkpoint evidence. Matching CI and
actual Windows failure runs remain required. Reconciliation commit be7a32f passed
native Debug/Release, Windows management/desktop contracts, CLI and signed setup
CI 37179484245. Its signed package 0.4.282.1 is downloaded but not installed.
The exact fresh old-driver baseline on T: remains running; it is separate from
the earlier incomplete Q: run and no performance acceptance is claimed.

Shared locked-page metadata correction, 2026-10-04:
the cache and provider reserved page payload and their own slab tables, but
omitted the allocated MDL header/PFN arrays. A common locked-page sizing helper
now charges those arrays in both paths. Cache slab admission includes that cost
inside the selected hard budget; published reserved bytes include the actual
descriptor count and remain bounded by the budget. Provider reservation includes
all per-slab MDLs before allocation. The managed headroom estimate also includes
PFNs and conservatively sized MDL/slab headers, without replacing authoritative
kernel accounting. There are no changes to ordinary read/write hot-path I/O.

Verification: bounded managed estimate contracts cover 16/17/64 MiB and maximum
capacity, including PFN scaling and invalid/overflowing capacities. Native
Debug/Release CI, actual shared allocation/rollback/removal and the complete
72-case before/after comparison remain required. This is a source accounting
fix, not a native allocation/performance acceptance verdict.

Maintained plan 87 additionally rejects an underreported native reservation even
when matching global/provider counters agree. The independent lower bound includes
x64 MDL/PFN/slab descriptors and the transfer workspace. Host evidence contracts
reject the old omitted-metadata reservation and any global-accounting discrepancy.

RAM adapter start robustness, 2026-10-04: `FindAdapter` refused to start the
adapter when `ExUuidCreate` returned STATUS_RETRY, which Windows documents
before its UUID seed is ready; a root-enumerated adapter can start that early
at boot. The adapter epoch now falls back to a time/counter-seeded random
version-4 value; it only distinguishes adapter instances. Review note on the
unresolved 0.4.269.1 install failure: that build lacked
`STOR_FEATURE_VIRTUAL_MINIPORT`, so Storport would have called the
seven-argument virtual `FindAdapter` with the six-argument physical convention,
making the code write configuration through the wrong pointer when setup
started the root adapter. That is consistent with the setup log ending right
after filter registration, but it remains a hypothesis, not a confirmed
diagnosis. Fixed by 3bcbcb5; not yet VM-verified.

Provider install crash diagnosed, 2026-10-04 (0.4.292.1 install on the test VM):
setup bugchecked with PAGE_FAULT_IN_NONPAGED_AREA in storport.sys while starting
the root adapter, then the next boot failed with INACCESSIBLE_BOOT_DEVICE. The
SetupAPI device log shows the cause: `Cannot overwrite Trusted Installer protected
file 'C:\WINDOWS\System32\drivers\qcramdisk.sys'`. The interrupted 0.4.269.1 install
had left that path as a hardlink into its own driver-store package, so the 0.4.292.1
package was staged but Windows restarted the adapter with the old 0.4.269.1 binary,
which lacks `STOR_FEATURE_VIRTUAL_MINIPORT` (seven-argument FindAdapter called with
the physical convention). The new provider code never ran. The boot failure was a
consequence: the crash discarded unflushed file data, leaving the newly staged
boot-start filter and the Program Files copies entirely zero-filled (their SHA-256
equals that of all-zero buffers of the same size). No dump was saved.

Fixes: the INF uses DIRID 13, so the service runs from its own driver-store package
and a stale fixed-path copy can no longer shadow a newer package. Provider setup
removes leftover QueueCache provider packages whenever no adapter node is bound,
so PnP cannot auto-select an older package for the new node. Install-Driver.ps1
flushes the SYSTEM hive and the system volume before starting the provider, so a
crash there cannot zero the staged boot filter. Recovery used the documented
offline rollback to the 0.4.264.1 filter path (hive backed up first), then deleted
the two stale provider packages (oem3/oem5) that pointed at the old binary.

Broker pipe instance fix, 2026-10-04 (found on installed 0.4.294.1): the broker
started, reconciled and restored the saved Q: profile, then terminated with
service-specific error 1 as soon as the first client connected. Creating the next
pipe server instance needs FILE_CREATE_PIPE_INSTANCE under the existing instance's
DACL, which granted only read/write. LocalSystem now also gets CreateNewInstance;
Administrators keep read/write only. Verified only by the VM run that follows.

managed-provider first VM run, 2026-10-04 (installed 0.4.294.1, run
`QueueCache-Verify-20261004-091256-40896b9416f24fc58239a8491ded2fdb`): at 512-byte
sectors allocation-failure rollback (1/8/16 slabs), private zeroed storage and
shared accounting, native range rejection, isolated VHDX logical transfer,
redundant-cache refusal, file-relative TRIM zero/guards/rewrite, open-file lock
veto, filesystem freeze/thaw and exact owned removal all PASSED. The read-only
check failed: a raw write returned ERROR_IO_DEVICE instead of ERROR_WRITE_PROTECT.
A direct probe showed the provider's sense reaches Windows intact (CHECK CONDITION,
fixed sense 7/27h/00h), IOCTL_DISK_IS_WRITABLE reports ERROR_WRITE_PROTECT, and the
write counted exactly one provider error with no generation change. Microsoft's
classpnp maps DATA PROTECT with ASC 27h to STATUS_IO_DEVICE_ERROR (only other ASCs
become STATUS_MEDIA_WRITE_PROTECTED), so the provider is correct SCSI and the test
expectation was wrong. Plan 88 checks the class write-protect state instead. The
4096-byte geometry did not run because the suite stops at the first failure.

Broker local-client fix, 2026-10-04 (found on installed 0.4.298.1): with the pipe
instance fix the broker stayed running, but every `qcache disk` call failed with
"Pipe is broken". The broker's local-client check required
GetNamedPipeClientComputerName to return this machine's name; a VM probe showed it
fails with ERROR_PIPE_LOCAL (229) for local clients, so every local client was
rejected and the pipe closed before the client finished writing. ERROR_PIPE_LOCAL is
now accepted as local (a returned name must still match). The broker also reads the
bounded request before impersonating, as Windows documents for pipe impersonation.
No product `qcache disk` operation had ever completed before this fix.

Volume-arrival deadlock in the filter, 2026-10-04 (found on installed 0.4.298.1 with
local kernel debugging): `managed-provider` intermittently hung the VM. New processes
(SSH logons, Task Manager) could not start; the worker could not be terminated. A
live `kd -kl` session showed a mount-manager online-notification worker sending
IOCTL_VOLUME_ONLINE to a new volume; QueueCache forwarded it directly (counted in
DirectCount), volsnap's VspOnline called IoVolumeDeviceToDosName and waited for the
mount manager. The same volume's request worker had dequeued an ordered request and
was waiting for DirectIdle at its first wait. The mount manager sends IOCTL_MOUNTDEV_*
queries to volumes with its own lock held, and ordered requests queue behind
DirectIdle, so this cycle never resolves, and with the mount manager stalled so does
process creation. The filter now treats every IOCTL_MOUNTDEV_* control and the
volume online/state queries as observations: forwarded at dispatch, never queued and
never counted in DirectCount. IOCTL_VOLUME_OFFLINE and attribute changes stay ordered.
None of these touch volume data, so cache ordering and invalidation are unchanged.
Not yet VM-verified; the provider suite is repeated in a loop to check.

Broker startup with foreign RAM objects, 2026-10-04: during a boot where the
developer provider suite's own native RAM fixture existed, broker initialization
threw "Uncataloged owned RAM objects are retained for recovery" and the service
exited, so the ordinary Q: saved profile was not restored on that boot. Unknown
native objects are still retained untouched and logged, but the broker now keeps
serving cataloged disks and restoring saved profiles.

Deadlock fix check, 2026-10-04 (installed 0.4.302.1): the provider suite, which hung
on its first iteration twice before the fix, completed six consecutive iterations
(both geometries, all checks) with no hang. The seventh failed once with
ERROR_ACCESS_DENIED from FSCTL_LOCK_VOLUME on the freshly written RAM volume, the
usual sign of a background scanner briefly holding a handle. Volume locking now
retries for up to five seconds on access denied; a handle that stays open is still
reported as the Windows veto.

Image-host validation and failed-create cleanup, 2026-10-04 (installed 0.4.302.1):
`ram-disk` PASSED at both geometries (open-file Stop veto, fresh format/write/flush,
explicit discard and recreate). `vhdx-backed` was refused by the image-host check:
T:\ was owned by NETWORK SERVICE (volumes formatted through the Windows storage
service get that owner). An owner implicitly has WRITE_DAC, so the refusal is
correct; the test volume root was changed to Administrators. The check then refused
Windows' default root ACL (Authenticated Users Modify on the root itself). A volume
root cannot be deleted or renamed, so only Delete on the root is now ignored;
FILE_DELETE_CHILD, permission and ownership rights still fail. Both messages now
say how to fix the directory. The refused creates exposed a product bug: a create
that failed before obtaining a runtime left a catalog definition that could not be
stopped or removed (RemoveDefinition required an identity; the engine dereferenced
a missing runtime). Create now retires such a definition before rethrowing, and
RemoveDefinition accepts an identityless definition. Fixture cleanups no longer
assume a runtime.

Backed-image identity fix, 2026-10-04 (installed 0.4.308.1): `vhdx-backed` created,
formatted and Stop-vetoed correctly, then Flush failed with "The owned VHDX attachment
identity changed." A VM probe showed GET_VIRTUAL_DISK_INFO IDENTIFIER (version 2)
returned the image's VirtualDiskId at creation but a different GUID once the VHDX had
been opened for writing; VIRTUAL_DISK_ID (version 14) stayed equal to the recorded
value. Every used backed image therefore failed Stop/Flush/startup identity checks,
and checkpoint identities had the same exposure. Image inspection now records the
VirtualDiskId. The suite's earlier "open-file veto" PASS was false: it accepted this
identity error as the veto. The product and CLI veto checks now require the broker's
recorded "Windows vetoed stopping" outcome.

Backed Stop/Format cache release fix, 2026-10-04 (installed 0.4.312.1): with the
identity fix, `vhdx-backed` passed the real Windows Stop veto (now checked against the
broker's recorded veto) and Flush, then Stop (drain then detach) failed with "Cannot
open \\?\Volume{...}: The device is not ready." Stop and Format took FSCTL_LOCK_VOLUME
with dismount and then opened the volume again to release the cache; only the locking
handle can reach a locked volume. Both now lock without dismounting, release the cache
through the locking handle, and then dismount, so exclusivity is still proven before
the cache changes.

Checkpoint candidate flush fix, 2026-10-04 (installed 0.4.312.1): every image-in-RAM
creation failed at its initial checkpoint with "Save failed. The live RAM disk and
preceding committed image are retained." The journal's recorded failure showed a
sharing violation: after detaching the candidate, the save reopened the VHDX to flush
it to disk while its own virtual-disk handle (with write access) was still open. The
handle is now closed first. The broker also returned only the outer message, which
hid the cause; error replies now include inner causes and failures are logged with
their full exception in the broker log.

Product suites on installed 0.4.318.1, 2026-10-04: `vhdx-backed` PASSED (real Windows
Stop veto, Strict and explicit-Fast flush/detach/reopen byte oracles). `image-in-ram`
passed save veto, export collision and whole-image save/export/runtime/reload at 512
bytes, then failed creating from an explicitly initialized blank image: publication
re-read the private layout through the provider's service path after formatting had
already published the disk, and the provider correctly refused that read with
STATUS_DEVICE_BUSY. The private-layout check now runs only before first publication;
the published layout is still checked. Provider errors now include the Windows reason.

Checkpoint verification delay fixed, 2026-10-04 (installed 0.4.322.1): every image-in-RAM
save and creation took about 185 s; per-phase timing showed verification spent
inspect=7 ms, open=80 ms, read=169 ms and close=180022 ms. A standalone replay reproduced
it: changing the disk attributes (taking it offline, and even restoring it online) on a
read-only VHDX attachment made its later DetachVirtualDisk wait out a 180 s timeout,
while writable attachments detached at once. Read-only import/verification views are
no longer taken offline; read-only access and no drive letter already isolate them. A
replay without the attribute change detached immediately and left the live volume's
letter, volume GUID and NTFS mount intact. Plan 89 matches the provider read-back check.

Product suites on installed 0.4.324.1, 2026-10-04: `image-in-ram` PASSED (83 s, was
over 12 minutes), `ram-disk`, `vhdx-backed` and `managed-provider` PASSED. `managed-cli`
ran 63 commands, then its image-in-RAM stop veto check failed: Stop with save is refused
by Windows while a file is open (correct), but the outcome was recorded as "Checkpoint
did not complete ... Access is denied." A refused exclusive volume lock (after the
bounded retry) is now a WindowsVetoException that says Windows vetoed exclusive access;
Stop, Save and Format report it that way, Stop records "Windows vetoed stopping" only
for that exception, and the product/CLI veto checks accept the veto from either path.

Read-only RAM disk stop fix, 2026-10-04 (installed 0.4.326.1): `image-in-ram`,
`ram-disk`, `vhdx-backed` and `managed-provider` PASSED again. `managed-cli` reached
command 79, where stopping a read-only image-in-RAM disk failed with "The media is write
protected": volume locking flushed the locked volume, and a read-only volume answers
that flush with ERROR_WRITE_PROTECT. That error is now treated as nothing to flush.

VM qualification on signed 0.4.328.1 (`7b3f3c5`), 2026-10-04, Driver Verifier 0x209bb on
the filter and RAM provider, T: on a non-OS SATA disk:
- `managed-provider`, `ram-disk`, `vhdx-backed`, `image-in-ram` and `managed-cli` PASSED
  at 512- and 4096-byte sectors (runs `QueueCache-Verify-20261004-144501-...`,
  `-144541-...`, `-144621-...`, `-144821-...`, `-144112-...`); the provider suite also
  passed six consecutive iterations after the volume-arrival deadlock fix.
- `managed-broker-restart` PASSED (`C:\QueueCache-Trusted\QueueCache-Verify-20261004-144411-...`).
- Restart lifecycle: `managed-lifecycle-prepare` (`-145012-...`), a real `shutdown /r`
  (boot 14:38:18Z to 14:52:35Z), `managed-lifecycle-verify --managed-transition Restart`
  (`-145548-...`) and `managed-lifecycle-cleanup` (`-145617-...`) all PASSED: automatic RAM
  recreated empty, manual RAM reported lost, stopped automatic recipe started empty,
  backed VHDX reopened, image-in-RAM reloaded its committed checkpoint.
- Setup refused to upgrade while a managed disk was live (observed twice).
- Matched 72-case `write-performance` (budget 2048 MiB, 3 repetitions, same DiskSpd SHA-256
  DD4E57E1..., Verifier on the filter, kernel debug off) against the 0.4.264.1 baseline:
  `QueueCache-Verify-20261004-145704-ef3b348e0f174ccd94ded385a5e1669f` COMPLETED 72/72 with
  clean restoration. Median IOPS ratio new/old across the 16 cached (Eager/Idle)
  configurations 1.004 (range 0.983..1.040); uncached Off rows vary in both directions with
  the slow virtual SATA backend. A few cached Q1 write p99 values rose slightly (for
  example 0.278 to 0.428 ms) without a throughput change. No regression from the shared
  page-metadata accounting or the filter's observation change was measured.
Not covered: sleep, hibernate and Fast Startup (postponed; no VM sleep states), crash during
checkpoint commit, physical hardware.

Desktop UI walkthrough, 2026-10-04 (installed 0.4.328.1, VM console): the app opened
elevated, showed the Managed disks section, created a 1024 MiB pure RAM disk on R:
through Create disk (Ready, 1027 MiB actual reservation), refused Stop without the
discard acknowledgement ("Acknowledge the selected erase/discard before continuing"),
stopped with it, and restarted the stopped disk; action availability followed each
state. Polish from the walkthrough: the header counted only ordinary cache
reservations and now shows the driver's shared total plus the RAM disk count; a stopped
pure RAM card said "full capacity reserved in RAM" and now says it reserves it while
running; every new RAM disk showed "errors 4" from Windows probing optional SCSI
opcodes, VPD and mode pages, which the provider no longer counts as errors (range,
write-protect and invalid UNMAP failures still count).

Verified on 0.4.332.1 (fdc6b1b): managed-provider and ram-disk PASS at 512/4096 sectors; a
new RAM disk reports errors 0 after Windows' probes and a file write.

Failed creates retire their definition (plan 90). Implementation: CreateAsync retired a
definition only when the refusal came before any runtime was recorded; StartCoreAsync
always records a Stopped runtime when it fails, so a create that failed or was cancelled
after allocation (for example a disconnected CLI client, seen on the VM) left a dead
"Stopped" definition with LastError "The operation was canceled." It now retires
whenever rollback left no provider object or attached image and the record holds no
image identity; image modes that already recorded an image stay remembered.
Verification: `ram-disk` gains `failed-create-retired` (occupied letter, refused at
publication after allocation/format; no definition, exact reservation). VM result below.
VM result on 0.4.334.1 (8e326fd): ram-disk PASS including failed-create-retired-512/4096
(occupied D: refused at publication, no definition, exact reservation); managed-provider
PASS. A CLI create killed after 2.5 s of a 4 GiB RAM create left no definition or volume.

RAM disk throughput, 2026-10-04 (0.4.336.1, Verifier off, 1 GiB CDM-style rows, best of 3):
the pure RAM disk reached only 11.7 GB/s SEQ1M at both Q1 and Q8 and 33-35k IOPS RND4K at
both Q1 and Q32, against 36.9 GB/s / 427k IOPS for cache hits on Q:. Equal Q1/Q8/Q32 results
showed requests were serialized: StartIo copied every transfer while holding the disk's
IoLock. Implementation: admission, bounds, read-only/freeze checks and counters stay under
IoLock; the memory copy runs after releasing it. Freeze and set-read-only wait for admitted
writes (ActiveWrites) before returning, so checkpoints and the write-protect guarantee keep
their meaning; removal already waits for request references. The adapter now reports 256
I/Os per LUN (initial queue depth 256) instead of Storport defaults. Verification on
0.4.338.1 (ecc0f1e, Verifier off, target Q: because the lab T: disk is gone): ram-disk and
managed-provider PASS at 512/4096 sectors. Throughput did not change (R: 11.6 GB/s SEQ1M Q1
and Q8, 34k IOPS RND4K Q1 and Q32), so IoLock was not the limiting serialization. Diagnosis
on the same build: one DiskSpd thread is CPU-bound on one core (25% of 4) with identical Q1
and Q32 results; 4 threads reach 320k IOPS RND4K through NTFS and 580k raw (#disk), and
keeping idle cores busy does not help. StartIo copies and completes each request inside
the submitting call, so a single submitter never has more than one request in flight.
Next: complete transfers asynchronously off the submitting thread (worker/DPC per CPU)
and profile the remaining single-thread per-request cost. Same run: cached VHDX (S:, 2 GiB
Fast cache, image on C:) 35.3 GB/s SEQ1M Q8 / 391k IOPS RND4K Q32, image-in-RAM identical
to the pure RAM disk, Q: 37.2 GB/s / 410k IOPS.

RAM disk transfer experiments (temporary tuning switches): a WPR CPU profile of one DiskSpd
thread at RND4K Q32 on 0.4.338.1 showed the thread CPU-bound (it never slept), with the
provider's memcpy about 1% of busy time; Storport's completion DPC insertion (~12%), the
completion DPC chain (~15%) and scatter-gather setup around StartIo (~17%) dominated. Cache
hits on Q: avoid this path and use worker threads. Implementation: the adapter reads
<service>\Parameters PerfFlags (StorPortInitializePerfOpts, masked to what Storport reports),
Workers (0 = inline; N = per-processor worker threads that copy and complete transfers,
never the submitting processor when another exists) and SpinMicroseconds (poll before
sleeping). Queued transfers keep their disk reference; FreeAdapter stops workers only after
their queues drain. Defaults keep the inline path. Verification: pending (variant benchmark,
then suites; switches are removed once a configuration is chosen).

RAM disk transfer strategy, chosen from the switch experiments (0.4.344.1-0.4.352.1, R: 2 GiB,
quick CDM rows, best of 3, Verifier off; IOPS / GB/s read):
| Variant | SEQ1M Q8 | SEQ1M Q1 | RND4K Q32 | RND4K Q1 |
|---|---|---|---|---|
| inline, no perf options (before) | 11.7 | 11.6 | 34k | 34k |
| inline + DPC redirection + completion during StartIo (0x11) | 15.7 | 15.9 | 223k | 234k |
| 4 workers, no perf options | 21.3 | 9.3 | 320k | 19.6k |
| 4 workers + 0x11 (completions redirected to the submitter) | 22.7 | 10.3 | 37k | 26k |
| 0x11 + adaptive workers for >=128 KiB | 22.6 | 15.7 | 225k | 231k |
| 0x11 + split reads (256 KiB chunks) | 24.8 | 24.8 | 229k | 232k |
Completion during StartIo needs DPC redirection (0x10 alone and 0x30 were rejected) and
redirection makes worker-completed 4 KiB transfers slow, so small transfers stay inline.
Non-temporal (SSE2 streaming) copies gave nothing for workers and slowed split reads;
removed. Split writes ran slower than worker writes at Q8 (16.2 vs 24.7 GB/s) and slightly
slower than inline at Q1 (16.3 vs 17.0), so writes use adaptive workers. Worker count 3/4 and
spin 50/200 us made no difference; 512 KiB chunks did not engage a helper in time.
Implementation: switches removed; fixed strategy: perf options 0x11 when supported, one
worker per processor (max 16), small transfers inline, large writes to workers while others
are in flight (inline after 16 transfers that found none outstanding, probe every 64th),
large reads split into 256 KiB chunks copied with idle workers and completed in StartIo.
Verification on 0.4.354.1 (e47f99e, Verifier off, target Q:): ram-disk and managed-provider
PASS at 512/4096 sectors; image-in-ram was refused by design because Q: has a Fast cache
(image hosts must be Strict/uncached) and the lab T: disk is gone, so that suite is not
re-run on this build (its transfers use the same SCSI path as ram-disk; image save/load uses
the unchanged control path). All-mode benchmark (GB/s or IOPS, read/write):
| Disk | SEQ1M Q8 | SEQ1M Q1 | RND4K Q32 | RND4K Q1 |
|---|---|---|---|---|
| RAM disk R: | 24.5/25.4 | 24.7/16.8 | 227k/193k | 232k/192k |
| image-in-RAM I: | 24.5/24.7 | 24.8/16.9 | 225k/192k | 231k/192k |
| cached VHDX S: (2 GiB Fast) | 35.4/21.6 | 14.9/14.0 | 401k/423k | 316k/272k |
| Q: cache hits | 36.2/21.7 | 14.6/13.7 | 412k/408k | 332k/266k |
A follow-up that split queue-depth-1 writes with a 150 us worker spin (0.4.356.1) left SEQ1M
Q1 writes at 16.3 GB/s (profile: memcpy 38%, unattributed frames 28%, user-buffer probe/lock),
so it was reverted. Remaining 4 KiB gap to cache hits is the Windows volume/partition/class/
Storport stack below the cache filter (about 78% of busy time at RND4K Q1).

Verifier qualification of the transfer strategy (0.4.358.1, Verifier 0x209bb on the filter
and qcramdisk, target Q: switched temporarily to a saved Strict profile so it can host
images): managed-provider, ram-disk, vhdx-backed and managed-broker-restart PASS. managed-cli
and image-in-ram failed when publishing an image loaded into RAM: RefuseOnlineClone opened
every PhysicalDrive and one (the just-detached staging VHDX) returned ERROR_NO_SUCH_DEVICE
(433), which was not among the ignored absent/not-ready codes. Implementation: treat 433 like
the other absent-disk codes. Verification on 0.4.360.1 (057b6ab, Verifier): managed-cli and
image-in-ram PASS; managed-lifecycle-prepare -> real Restart -> -verify Restart -> -cleanup PASS
(Q: saved Strict for the transition); stress (stress-ram, 180 s: 512 B and 4 KiB-sector RAM
disks, DiskSpd 4K/256K/1M mixed load plus four unbuffered integrity threads with 4 KiB-1 MiB
requests) verified 66 GiB and 212 GiB byte-exact with ~3.1M DiskSpd I/Os and no bugcheck;
an adapter restart unloaded/reloaded qcramdisk cleanly under Verifier. Verifier off, Q:
restored to its exact saved Fast profile: R: 24.9/24.8, 24.3/16.8 GB/s, 229k/192k, 233k/192k.

Split-read helper preemption: StartIo spins (DISPATCH_LEVEL) until helpers that joined a
split finish, but helpers ran at PASSIVE_LEVEL and could be preempted after joining.
Implementation: a worker raises to DISPATCH_LEVEL before taking a queue entry and stays there
while copying its help chunks, and no longer reads the stack-resident help entry after
releasing it. Verification on 0.4.362.1 (5cd999c) under Verifier with Q: Strict for the boot:
managed-provider, ram-disk, image-in-ram, managed-cli and vhdx-backed PASS; stress verified
65 GiB and 217 GiB byte-exact with ~3.2M DiskSpd I/Os; adapter restart reloaded qcramdisk
cleanly; no bugcheck. Verifier off, Q: back on its saved Fast profile, all modes (GB/s or
IOPS, read/write): RAM R: 25.0/24.2, 25.0/17.3, 225k/192k, 234k/192k; image-in-RAM I: 24.3/25.0,
25.2/17.0, 225k/191k, 236k/192k; cached VHDX S: 36.7/21.3, 14.3/13.2, 412k/386k, 319k/255k;
Q: 36.9/21.7, 15.1/13.9, 411k/406k, 341k/261k.

Direct access for RAM-backed disks (branch feature/ram-fast-path, plan 91). Implementation:
- Shared store and write gate (`driver/shared/ramstore.h`) used by the provider's SCSI path
  and the volume filter; kernel registration contract (`driver/shared/ramview.h`).
- Provider split into disk/transfer/SCSI/control files; its SCSI path no longer takes a
  per-disk spinlock (interlocked gate and counters instead); Direct creations register the
  store, removal unregisters it before freeing pages.
- Volume filter module `driver/qcache/ramdirect.cpp`: view registry, binding (identity,
  single extent, known volume/disk stack drivers, BitLocker signature, write protection,
  shadow copies), caller-thread copy (large transfers via the provider's split copy),
  rundown for removal, permanent fallbacks with reasons (flush-and-hold ends writes;
  unknown controls end Direct before forwarding; boot-sector change ends it). One
  lock-free check per read/write on other volumes. Device-control boilerplate moved to
  `devicecontrol.h`.
- Product: `RamAccess` (default Direct for new RAM-backed disks), provider protocol
  version 1 extended with Direct/DirectRegistered flags (a version bump made the installer's
  update preflight, which runs the new CLI against the old provider, refuse every update;
  found on the VM, reverted), broker bind after every publication (plus a
  `Win32_ShadowCopy` pre-check), live state in list/status/desktop card, CLI `--access`.
- Verification: ram-disk and image-in-ram run Direct and Standard variants; new checks
  direct-coherence, direct-unrecognized-control, direct-snapshot-writes; lifecycle verify
  requires Direct back after transitions. Host contract tests cover the protocol flags,
  Direct state decoding and the Access validation.
- Build: the cache driver now treats warnings as errors (two warning sources fixed). A
  compiler `/analyze` probe reported only false positives and annotation-style notes, so
  it is not part of the build.
- VM findings fixed: the bind opened the disk stack through `\Device\Harddisk<N>\DR<N>`
  (DR numbers are a global counter; now `Partition0`); harmless Microsoft queries seen on
  the VM ended Direct access (now the device-type/access-bit rule, see
  RAM_DISK_IMPLEMENTATION_PLAN.md); `direct-snapshot-writes` read the live file through
  Windows' file cache, so no read reached the volume (now unbuffered).
- Provider reference race (also on master): `DereferenceDisk` decremented the count and
  then signalled the idle event, so removal could free the disk in between and the signal
  landed in freed memory (a worker finishing a queued write can be preempted there).
  Implementation: an `EX_RUNDOWN_REF` replaces the count and event; its release is the last
  access, and idle-to-busy transitions no longer pay two event operations.
Verification on 0.4.381.1 (dad0505) under Verifier (standard flags plus port/miniport
checks, 0x309bb), Q: Strict for the runs: ram-disk, image-in-ram, managed-provider,
managed-cli, vhdx-backed and managed-broker-restart PASS, including direct-coherence,
direct-unrecognized-control and direct-snapshot-writes at 512 and 4096-byte sectors.
managed-lifecycle-prepare, a real restart and managed-lifecycle-verify (Restart) PASS:
automatic Direct disks came back with Direct access. Stress: 451 GiB verified byte-exact on
two Direct disks with ~17.6M DiskSpd I/Os (writes of 4 KiB..1 MiB, so both paths). Adapter
restart (broker stopped, as before) unloaded and reloaded qcramdisk (2 loads, 1 unload)
and a new Direct disk worked; with the broker running PnP vetoes the stop, as on master.
No bugcheck since the one below. Earlier results:
- 0.4.377.1 (9a7c4ef) under Verifier: one bugcheck 0xA in `nt!KiInsertTimerTable` (a
  timer-table entry with a null link, hit by an unrelated thread) about 19 s into
  `ram-disk`, near the Direct variant's shadow-copy step and Stop. Only a minidump was
  configured. Not reproduced since: 9 more `ram-disk` runs, 4 reboot-then-suite rounds,
  16 shadow-copy/Stop rounds (Direct, with and without a live snapshot) and a 180 s
  stress (two Direct disks, 1.14 TiB verified byte-exact, ~9.5M DiskSpd I/Os), with Verifier
  plus port/miniport checks. The VM now writes kernel dumps for the rest of this
  verification so a recurrence identifies the overwritten memory. The cause is not proven;
  the provider reference race above is the one use-after-free found in review.
- BitLocker (manual, `.lab` script, filter unchanged since 0.4.377.1): Direct access was full
  on a new 512 MiB Direct disk; `Enable-BitLocker` ended it before forwarding its first
  state-changing control (reason Control, 0x0056C04C, nothing declined), encryption
  reached FullyEncrypted, the file written before was intact and a write afterwards read
  back exactly.
- Write-performance regression (filter dispatch change), Verifier off, Q: on its saved Fast
  profile, CDM 9.0.3 DiskSpd (SHA-256 7281BF6D...): master 0.4.173.1 (549d237) and 0.4.380.2
  (c43cc2b), both 72/72 MEASURED. Median IOPS ratio 0.997 over all 24 configurations and
  1.007 over the 16 cached ones (0.948..1.022, repetition ranges overlapping); the Off cases
  write through to the cluster-backed disk and vary 0.83..1.08 with single repetitions as
  low as 50 vs 118 IOPS. No regression. A first master attempt stopped at case 29 on a
  2.6 s telemetry gap (host stall) and was rerun in full, not combined.
- CDM-style rows (Verifier off, best of 3, GB/s or IOPS read/write; `.lab` bench-direct):
  0.4.380.2 Direct R: SEQ1M Q8 26.7/17.4, Q1 26.7/17.5, RND4K Q32 430k/337k, Q1 452k/339k;
  Standard T: 24.4/25.4, 24.2/16.7, 228k/194k, 236k/195k. A Direct write occupied its caller,
  so Q8 writes could not overlap: writes of 512 KiB or more now take the standard path
  (dad0505). 0.4.381.1: Direct R: 26.7/24.4, 27.1/17.2, 434k/330k, 453k/334k; image-in-RAM
  Direct I: 26.5/25.1, 26.9/17.2, 429k/332k, 454k/328k; Standard T: 24.3/24.5, 24.9/17.2,
  227k/193k, 232k/194k; Q: (Fast cache) 36.8/22.0, 14.8/13.9, 404k/411k, 331k/265k.
  Direct leads at queue depth 1; Q: still leads at SEQ1M Q8 read and RND4K Q32 write
  because its worker threads overlap queued requests.
- Why (same depth from 1 vs 4 submitting threads, DiskSpd per-CPU busy %): Direct RND4K write
  336k at both Q1T1 and Q32T1 with one CPU busy (100/1/0/0), 1.09M at Q8T4 (all 100); Q:
  264k at Q1T1, 402k at Q32T1 (98/31/40/32: its worker joins), 191k at Q8T4. SEQ1M read:
  Direct 26.2 GB/s at Q8T1 (split copy), 39.2 GB/s at Q2T4; Q: 36.0 at Q8T1, 31.5 at Q2T4.
  A Direct request is finished by its submitter, so one thread's queue depth adds nothing.
- ImDisk 2.1.2 (virtual-memory RAM disk, 2 GiB NTFS) in the same session, same rows: 16.1/19.2,
  10.5/13.0 GB/s, 264k/131k, 44k/27k IOPS (Direct R: 26.8/24.8, 26.8/16.6, 432k/335k,
  451k/336k; Q: 29.2/21.0, 11.0/11.5, 431k/396k, 338k/268k).
- Queued Direct copies (3fb9d39, e8e31c8): tried and reverted. Handing Direct copies to the
  provider's per-processor workers while the submitter kept others in flight (the provider's
  inline-or-worker policy, shared) lost on this VM: queueing every request gave RND4K write
  263k/297k/393k (Q1T1/Q32T1/Q8T4, inline 335k/336k/1.09M) and SEQ1M read 22.3 GB/s at Q8T1
  and Q2T4 (inline 26.2/39.2); only RND4K Q32T1 read gained (532k vs 432k). Queueing only
  copies of 256 KiB or more, and only to free processors, kept small I/O inline but still
  lost large transfers (SEQ1M read 20.8 at Q8T1, 23.1 at Q2T4; CDM SEQ1M Q8 20.7/22.9 vs
  26.7/24.4). A CPU profile of SEQ1M Q8T1 showed the workers spending 21% of busy samples
  spinning between copies against 28% copying; the handoff and spin cost more than the
  overlap gained. The inline design (dad0505) stays.


### Resident sequential performance investigation, 2026-10-08

Implementation: plan 92 adds the opt-in `cache-layout` comparison to the maintained
verification runner. It brackets sequential/random reuse plus drop-clean with
fresh allocations at Q1/Q8, preserves the file and allocation generation across
reuse, and rejects score intervals with lower I/O or missing evidence. Contract
tests cover ordered cases, generation transitions, timing and lower-I/O rejection.
There are no native driver or production policy changes.

Verification: host-safe management contracts passed. On the unchanged CI driver
0.4.414.1, Verifier off, all 24 VM cases completed with verified residency, zero
lower-I/O attempts during score intervals, timing off and complete telemetry.
Fresh/reused/recreated median GB/s: Q1 15.462/10.754/15.375; Q8
36.850/29.809/37.073. Random reuse was slower still. Allocation generations
changed only as intended. Independent restoration succeeded; the original Fast
profile and timing were restored and the tray app restarted. No driver fix is
claimed; writes, RAM disks and hardware cache/TLB causes were not measured here.
Full [results and limitations](CACHE_LAYOUT_INVESTIGATION_20261008.md); repeatable
[method](DEVELOPER_VERIFICATION.md#cache-allocation-history-comparison-plan-92).

Follow-up design, 2026-10-08: [UI preparation and reset semantics](BENCHMARKING.md#ui-preparation-clearing-contents-versus-recreating-the-allocation)
are documented. [Optimization options](CACHE_LAYOUT_INVESTIGATION_20261008.md#what-was-built-and-measured-plans-95-96)
prioritize isolating free-slot order with unchanged buffers, then improving
ordinary placement before considering idle compaction. Implementation: none of
these optimizations is implemented. Verification: the plan-92 results above are
the baseline only; no same-buffer reset, allocator change, copy coalescing or
compaction benefit has been measured. The existing runner contract is unchanged.

### Same-allocation free-order experiment, 2026-10-08

Implementation: an explicit diagnostic action rebuilds free-slot links only at a
validated empty boundary, retaining all payload allocations and generation.
Normal caching, eviction and UI clearing behavior are unchanged. Plan 93 adds
`cache-layout-reset` with 36 ordered Q1/Q8 cases and occupied/parameter refusal,
allocation/counter preservation and existing resident-score evidence checks.

Verification: host-safe management contracts passed; CI 0.4.423.1 passed. On the
VM (loaded driver hash checked, Verifier and timing off, `quick` passed first)
plan 93 completed 36/36 with zero misses/lower writes/errors/reallocation in
score windows. Medians Q1/Q8 GB/s: fresh 15.44/36.27, sequential reuse
10.75/29.70, reset same allocation 15.24/36.26, random reuse 8.75/23.81, reset
15.15/36.62, recreated 15.41/36.37. Free-slot order alone explains the gap. No
production optimization is implemented yet. Method and evidence
contract: [same-allocation reset](DEVELOPER_VERIFICATION.md#same-allocation-reset-comparison-plan-93).

### Free-slot order patterns and never-cleared churn, 2026-10-08

Implementation: `LabResetFreeOrder` takes an order (ascending, chunks shuffled,
reversed inside chunks, scattered; bijections checked at compile time) and
`LabMeasureLayout` reports block/memory adjacency and whole free chunks in
diagnostics V18. Plan 94 adds `cache-layout-patterns` (30 cases) and
`cache-layout-steady` (18 cases: fresh, 60 s random-read churn without clearing,
ascending reset control). Normal caching, eviction and clearing are unchanged.
Purpose: decide whether a chunk-based allocator suffices and how far a cache in
long use drifts from the fast layout. Verification: CI 0.4.426.1 passed; on the
VM (loaded hash checked, Verifier/timing off, `quick` first) patterns 30/30 and
steady 18/18 completed with clean score windows. Medians Q1/Q8 GB/s: fresh
15.39/36.65, chunks shuffled 14.77/35.28, reversed inside chunks 10.71/29.54,
scattered 8.48/22.64. Never cleared: 60 s random-read churn left 92% contiguous
and 13.86/30.66 while about 1,500 whole chunks stayed free. Only order inside a
256 KiB chunk matters. No production allocator change yet; design proposal in
[the investigation](CACHE_LAYOUT_INVESTIGATION_20261008.md#what-was-built-and-measured-plans-95-96).

### Chunk allocator and copy experiments (plan 96), 2026-10-08

Implementation: per-chunk free bitmaps replace the LIFO free list (open chunk filled
upwards; wholly free chunks first, then the partial chunk that changed state last;
O(1) list maintenance). Lab copy flags (prefetch next block, coalesce memory-neighbour
runs) for A/B within one boot, reported in diagnostics V19. `cache-layout-patterns`
retired. Verification (loaded driver hashes checked, Verifier/timing off, clean score
windows): `quick`, `policies`, `pressure` pass on 0.4.431.1 and with copy flags 3 on
0.4.433.1. cache-layout reuse Q1/Q8 14.61/35.66 and 14.50/35.48 (old allocator
10.75/29.81, 8.56/23.18); steady/full churn unchanged; write-performance versus
0.4.426.1: sequential Q1 +28%, random unchanged, sequential Q8 with write-back
-8% to -14% (open). Copy flags (fresh Q1/Q8): 0 15.13/35.94, 1 18.25/37.44,
2 uncapped 14.88/22.10, 2 capped 20.60/39.51, 3 20.85/39.36; both on by default
since 0.4.434.1. Details: [investigation](CACHE_LAYOUT_INVESTIGATION_20261008.md).

### Follow-up experiments (plan 97), 2026-10-09

Implementation: runner waits for a quiet cache before layout windows (plan 97); NTFS
last-access updates off on the test VM; `qcache developer driver layout-map`. Measured
and removed: RAM disk Direct reads on worker threads (no gain or much slower) and one
copy per run for writes (+1.5% Q1, noise at Q8). Verification: quick, policies and
pressure passed with write runs on; RAM disk round trips identical with offload on;
memory map polling at 8x the app rate only moved p99.99. Open: Q8 sequential write gap
(3-14%), RAM disk Q8 reads (needs a different design). Details:
[investigation](CACHE_LAYOUT_INVESTIGATION_20261008.md#follow-up-experiments-04391-04401).

### Concurrent and sustained cache validation (plan 98), 2026-10-09

Implementation: new opt-in `cache-concurrency`, `cache-sustained` and
`cache-map-cost` suites in the maintained runner. They compare fixed total Q8
across 1/2/4 separate targets, retain strict lower-I/O checks for fitting Deferred
writes/read hits, and distinguish intentional background/mixed disk I/O. The
sustained run adds concurrent deterministic byte oracles, idle boundaries,
sequential rereads and post-drain disk verification. Quiet waits now fail when
all five attempts are exhausted and reject identity/error/flush changes.
Memory views reject late, partial and changed-generation maps, clear maps when
state goes stale or a cache is removed/resized, and explain their metric.

Verification: host contracts and headless desktop tests passed. VM performance,
long-run byte evidence, comparison to the old allocator, and CI are pending;
no speed-up or resolved write regression is claimed at this point.

Plan 99 adds a separate neighboring-sector concurrent byte oracle and a bounded
warm-up for scan-resistant insertion without clearing the cache. The plan-98 smoke
run `QueueCache-Verify-20261009-121724-e294932cf71f46baa28681a5fa0e98ce`
stopped before scoring: a second half-budget file competed with the previously
warmed file and still missed 37,789,696 bytes on its proof pass. No driver errors;
restoration completed. This is INCOMPLETE, not a sustained-use pass. The corrected
plan-99 short smoke run `QueueCache-Verify-20261009-122536-2130df0b40f142e5abc883d8ab1c0508`
completed 6/6 with clean restoration: 1,682 exact write/read checks, all 24 final
oracle files verified from disk. Reread warm-up took 1/4/1/1/1/1 attempts. This is
120 seconds of mixed I/O, not a 30-minute acceptance run. The full run is pending.
The RAM disk physical map
now clears when its state is unavailable; frontend tests cover unavailable,
fresh and stopped snapshots. Sector writers rendezvous before each pair so both
workers are ready before submission; this still does not force kernel overlap.
Implementation also stops cache-map polling on other pages or while the window
is hidden in the notification area. Frontend verification counts no requests in
those states, with fresh-map requests on return; 8/32 GiB grouping fixtures pass.
Added Windows runner contracts for rejecting empty/failing concurrent-sector
checks before workload preparation, retaining a complete immutable plan and
restoring ownership; their CI run is pending.

Plan-99 long attempt `QueueCache-Verify-20261009-123638-526002062ef94655aad8118e88279f01`
is INCOMPLETE, 1/6, clean restoration. After its five-minute mixed episode the
five proof passes still missed 676/533/430/350/279 MB; throughput stayed near
239 MiB/s and the last proof read less than the complete 1 GiB prefix. This is
slow scan-resistant residency recovery, not a reported byte mismatch or driver
fault. It remains an open performance finding.
Plan 100 separates the strict fresh RAM reference from natural post-churn
rereads with hit/miss accounting and their own telemetry coverage, so the full
soak measures recovery rather than forcing it. Map-cost preparation establishes
its hot reference before filling spare space and re-proves it afterward. Strict
RAM-only controls are unchanged. Implementation/host checks complete; new VM
smoke, full soak and remaining matrices pending.
Windows CI at `8c37c13` rejected the new fake concurrency cases because the CI
build launches its test process below normal priority. The production gate was
correct. Fixture tests now explicitly switch to Normal and restore the original
priority, and separately prove below-normal execution is rejected before target
access. Local compilation/host checks pass; corrected Windows CI remains pending.
Oracle failure handling now cancels other streams and propagates a mismatch
before the workload finishes. The coordinator cancels and awaits the owned score
process before restoration. Host contracts exercise both early failure and early
successful oracle completion.
The neighboring-sector oracle now checks NTFS, 512-byte logical sectors and
4K-aligned clusters before creating files, and records the cluster size. This
ensures the two sectors share a cache block. Host target-boundary checks pass;
the final VM check remains pending.

Plan-100 full soak `QueueCache-Verify-20261009-130343-20af98641401425ab5705572c3d07e1c`
completed 6/6 with clean restoration: 30 minutes of mixed I/O, 24,414 exact
write/read checks and all 24 post-drain files verified. Natural rereads remain
disk-bound with substantial misses; this is complete collection and the stated
byte/lifecycle checks, not a residency-speed acceptance verdict.
Windows Debug/Release CI passes through `59859a0`.
The following concurrency attempt
`QueueCache-Verify-20261009-134053-c604aa926e8d4582804bbbeffcd8bdac`
is INCOMPLETE, 1/37: the oracle wrongly rejected Disable's intentional generation
advance before comparing disk bytes. Restoration completed. Plan 101 corrects
that lifecycle check and separates state evidence from byte evidence. Implementation
complete; host regression passes and the corrected VM sector oracle passes its
128 active/post-drain byte pairs. Its focused RAM-read control and new VM matrices
are in progress. Windows CI at `54339c7` exposed an outdated plan-100 fixture
assertion; that assertion is updated to the documented plan-101 contract.
Consolidated results
and remaining decisions: [sustained validation](SUSTAINED_CACHE_VALIDATION_20261009.md).
Plan-101 full concurrency stopped at 10/37 on two lower paging-read attempts in
the two-stream Deferred-write score; zero data-miss bytes, no drain writes/errors,
clean restoration. The failed run is preserved. Plan 102 primes the exact write
targets/shape outside scoring, retains those snapshots/XML and then uses the
existing drain/quiet boundary. Strict zero-lower-attempt score checks and the
72-case write-performance suite are unchanged. Implementation complete; focused
VM regression pending. Windows Debug/Release CI passes at `a25d93c`.
Plan-102 focused regression also failed on the same two paging reads, clean
restoration; its write-prime pass did not help and is removed. The captured PID
identifies Defender, and NTFS extents map the last read to the final 64 KiB of a
1 GiB file beyond the 512 MiB hot prefix. Plan 103 uses distinct whole-file fixtures
per shape, totaling half the budget; manifest/worker output records sizes and
pre-warm checks enforce them. Defender stays enabled, no exclusions are used and
strict score checks are unchanged. Implementation complete; host contracts and
new VM regression pending. Windows Debug/Release CI passes at `7ebd503`.
Plan-103 focused VM regression completed 2/2 with clean restoration: 128 sector
pairs and post-drain guards matched; the two-stream Deferred-write control measured
20,347 MiB/s with exactly zero lower read/write/flush attempts, drain bytes or
data misses. Defender remained enabled. This verifies the focused fixture fix,
not a full matrix or native speed-up. Windows Debug/Release CI passes at `3360135`;
complete concurrency and 2/4 GiB polling matrices are running with that runner.
Plan-103 full concurrency completed 37/37 with clean restoration. Three complete
repetitions retained zero lower attempts for read/Deferred controls and copy flags
3. Q8 read medians: one/two/four streams 34,704/34,154/31,999 MiB/s; write Deferred
20,603/20,332/20,028 and Eager 20,491/20,009/19,909 MiB/s (`-Z1M`, not comparable
with the separate `-Zr` matrix). In-chunk order is 99.6–100%; no placement cause
or per-stream allocator improvement is proved. Polling matrices and the old/current
Q8 write recheck remain in progress.
Plan-103 2 GiB map-cost completed 9/9, clean restoration and zero lower attempts
in every score; complete full-allocation maps (8,024 chunks, 513,536 used slots)
and interval coverage. Median MiB/s Off/2 s/250 ms: 1,389.55/1,383.92/1,399.04,
overlapping ranges; no throughput gain claimed. Normal steady map median 1.622 ms;
recorded polling-window CPU median 46.875 ms over roughly 12 s. This excludes UI
rendering. Existing two-second cadence retained; 4 GiB and old/current Q8 checks
remain in progress.
Plan 104 / read recall. Implementation: `5e17604` (driver 0.4.469.1) replaces
bimodal read-fill insertion with a 4-way history of evicted blocks and their last
use (`readrecall.h`, 4 bytes per slot in the fixed budget). A miss used more
recently than the oldest used block still cached enters as recent; others stay at
the eviction end. `QcLabReadRecall`/`developer driver read-recall` (action 18)
restores bimodal insertion for A/B; diagnostics V20 report mode and decisions;
`drop-clean` and mode changes clear the history. Modelled against the earlier
rule before implementation (report). Compile-time table checks, host contracts,
desktop tests and Windows Debug/Release CI pass. `10dc815` fixes the managed
diagnostics request size (0.4.469.1's controller asked for V19). The runner adds
`cache-recall` and exact whole-file warm passes. The old/current Q8 write recheck
is blocked: 0.4.426.1's installer preflight cannot read the newer RAM provider's
state and refuses to downgrade; the cross-day comparison is confounded by a
255 vs 115 MiB/s disk-only change. Verification: quick, policies and pressure
pass on 0.4.469.1 (recall on). `cache-recall` run
`QueueCache-Verify-20261009-161142-fbaac21da41a4410b05e44dcbf28ef01` completed
12/12 with clean restoration and the CI filter loaded: a fitting file re-read after
stale data is 100% hits from its third pass with zero lower reads (19,052/18,988
MiB/s Q1; 36,943 MiB/s Q8), against 12.1/17.6% and 158 MiB/s Q8 with bimodal
insertion. The hot set stays 100% after a one-off and a repeated 1.5× scan in both
modes; the loop is 48.6% in both. The 30-minute `cache-sustained` soak on the same
driver completed 6/6 with clean restoration, 22,123 oracle checks and 24 post-drain
files verified: the churned Q8 reread reached 15,620–19,877 MiB/s at 99.6–99.8% hits
in every episode (plan 100, bimodal: 130–237 MiB/s, 8.8–17.5%), and the mixed
windows kept a 53.6–55.1% RAM hit rate (43.3–58.2% before). The 4 GiB map-cost
rerun with exact warm passes completed 9/9 (clean restoration, zero lower attempts,
complete 16,050-chunk maps): Off/2 s/250 ms medians 1,353.10/1,358.05/1,304.80
MiB/s; two-second polling retained. Same-build switch off/on/off six-minute soaks
(all 6/6, clean restoration) measured the trade-off: Q8 rereads 1,601–2,527 /
27,554–30,216 / 2,674–26,622 MiB/s, while the 60-second mixed windows that follow
a reread had 50.1 / 46.1 / 52.2% median RAM hits. Splitting the 30-minute soaks'
windows: with read recall the first minute after a reread is ~7 points lower
(42.5% vs 49.4%), minutes 2–5 ~4 points higher (58.6% vs 54.7%). The RAM-path
regression check (`cache-concurrency --case-filter s1-q8`, 10/10) matches the
plan-103 results within 0.6% for writes and is higher for reads. Kept on by default.
Housekeeping on the same branch: `7373135` fixes `qcache disk stop`/`remove`
printing a null-reference message after succeeding (a stopped disk keeps statistics
but has no native state), verified on the VM. `063fbde` makes a completed run
remove its own workload folders from the tested volume (failed runs and system,
managed-lifecycle and disk-removal suites keep them; `--keep-workloads`), verified
on the VM; 63 older folders (174 GiB in total with the day's runs) were removed by
hand. README CrystalDiskMark numbers and screenshots redone on 0.4.476.1 the same
evening (cache column re-shot after that cleanup). Known issues now list the
read-recall trade-off, partly cached requests, the RAM disk's single-reader limit
and the blocked downgrade; the old Q8 write-gap note is closed as not demonstrated.
The 72-case `write-performance` matrix is now complete (final entry below).
Partial-hit counters remain planned after the merge.

2026-10-10 UI additions on PR #8. Implementation: a live cache-map pop-out with
maximized/full-screen modes (F11/Escape), a shared darker/lighter occupancy legend,
and a persisted 0.1-second application refresh option. Visible maps follow the
fast preference; per-volume sample ownership prevents overlap, and minimizing or
closing the pop-out releases its map request. Expanded maps fit the viewport and
show up to 32,768 cells. Driver behavior is unchanged. Verification: desktop
contracts pass, light/dark pop-out screenshots inspected, and README app maps
regenerated. VM UI check pending; 72-case write-performance matrix
now authorized, to follow the UI commit. Merge and follow-up performance branch
are authorized after verification.
Pop-out follow-up: inventory refresh reuses unchanged volume models, so adding an
unrelated volume keeps a live map open. Changed volume identities still close the
pop-out and reject late samples. Desktop regression passes this case. The matrix
is running on signed CI 0.4.476.1, filter SHA-256 `88880997C11D27A7…`, with native
sources identical to the current PR head; same CDM DiskSpd hash, 2 GiB and three
repetitions as the 0.4.426.1 reference. UI stays closed during scoring.

Final write-matrix verification, 2026-10-10: plan 104 on signed CI 0.4.476.1
completed 72/72, run
`QueueCache-Verify-20261009-220533-0439cff369ce45c8b577e23951b77317`.
All raw XML outputs/exits, readiness handshakes, interval coverage, loaded module
hashes and restoration were checked. Original enabled 2 GiB policy/timing restored,
zero dirty/in-flight bytes and no driver error. No driver implementation changed
for this run. Cached random medians are 1–7% higher and sequential Q1 29–32% higher;
sequential Q8 is 5–14% lower than the historical 0.4.426.1 reference, with overlapping
ranges and much slower disk-only results. This is a current baseline, not a
controlled causal comparison. Full table and limits:
[write-performance reference](WRITE_PERFORMANCE_20261010.md).

VM UI verification: the published current-source desktop preview on Windows
opened the 8,017-chunk map through Pop out, rendered full-screen with F11, returned to maximized with Escape, and
persisted `UpdateSeconds: 0.1`. The shared fuller/partly-used legend is visible.
Headless tests cover page changes, minimization, closure, pending samples and
unrelated inventory changes. The preview does not establish a 100 ms performance
acceptance result. Temporary owner settings are restored after this check.

### Post-merge performance follow-up, 2026-10-10

PR #8 merged as `fb34f878581590123a190b048fdfd23c5d4e9b39` after the full
72-case matrix, host/Windows CI and real Windows pop-out/100 ms checks.
The temporary UI preview exited, the owner's original preference state was
restored, and the installed tray application resumed. Branch
`perf/ram-read-followups` starts from the merged master.

| Item | Implementation | Verification |
|---|---|---|
| RAM-disk single-reader Q8 scheduling | Plan-108 reference sampler and plan-109 same-build queue comparison implemented. V22 counters and a default-off bounded shared sleeping queue reuse the three copy executors; adaptive Q1 fallback retains the existing synchronous path. See [design/profile](RAM_READ_SCHEDULING_20261010.md). | Existing 26 versus 40–42 GB/s observation is motivation; Reference plan/accounting/ownership/failure-restoration host contracts pass; Windows CI through plan 107 passes. First plan-106 VM reference stopped on small volume writes. Plan 107 passed two guarded byte-checked windows, then stopped on a 2.084 s telemetry gap in the four-thread window; both attempts restored cleanly. Plan 108 replaces broker polling with dedicated native sampling; Windows CI and all 36 plan-108 VM windows pass (`20261010-031011-5ff0b7da045a4b559abcc5cef68060b1`); raw evidence, 1.137449 s maximum gap and independent restoration inspected. Plan-109 ABI/plan/counter/ownership/failure-restoration host contracts pass; native Windows Debug/Release CI passes; same-build VM A/B and explicit lifecycle/cancellation qualification are pending. Preliminary Q1 CPU profile: 46.4% memcpy, 33.8% helper WorkerMain, about 95% CPU busy; enclosing process interval only. No speed-up verified. |
| Partly cached read diagnostics | Implemented after merge: diagnostics V21 append four counters; plan-105 `partial-read-accounting` uses a held unbuffered handle and patterned partial sectors. Lower-read behavior unchanged. | Host ABI/window/runner contracts pass, including rejection of empty and failed worker checks. Windows Debug/Release CI and plan-105 VM run `20261010-020527-79bbee34e94c47faa71b8b062523627a` pass all ten byte/accounting checks on 0.4.495.1. Original settings restored, no errors/pending bytes. Concurrent ordering remains covered by existing scenarios, not this serialized counter window. |
| ReFS caller-path backoff | V23 appends candidate counts and mutually exclusive first-decline reasons (control, queue, worker, owner, offload, cooldown, probe). Runtime lab action 20 bounds the cooldown at 0..256; default remains 256, and every ownership/control check still applies. Plan 111 captures, restores and verifies the setting. | ABI/delta/restoration host checks pass; native CI and controlled ReFS/NTFS reference, ordering/flush/capacity checks pending. No performance gain established. |
| Background priority limits | Planned CPU/I/O/memory and copy-worker wait attribution. | Independent priority controls pending; no boost justified. |

Plan 112 implements the supported 24-window `caller-backoff` comparison, with
alternating 256/0 cooldowns, 64 KiB mixed Q1/Q8 and 4K/1M read controls, complete
residency proofs and independent concurrent/post-drain byte checks after scoring.
Implementation is complete; host contracts pass; Windows CI and VM verification are pending.
The mixed boundaries also retain the V21 staged-read counters for overlap evidence.

The [follow-up plan](PERFORMANCE_FOLLOWUP_PLAN_20261010.md) defines acceptance and
keeps implementation separate from verification. The initial draft PR contains
this plan; the historical Q8 write gap remains unproved. Closed/accepted items
remain closed, and the 72-case measurement contract is unchanged.


Partial-read evidence: exact run `QueueCache-Verify-20261010-020527-79bbee34e94c47faa71b8b062523627a`
completed in `C:\QueueCache-Results\PartialRead495-20261010`, plan 105,
commit `6e4d192f7ef81e251e0b862c8a7437c1605c09dc`. Loaded filter SHA-256
`B9C1AF5DB37000AF905B4A9DCE68BF2AD94796F062F79A322A6BBDBB25585233`,
provider `804AD65A31B26091841D77D9020A16A8B01B0FC52E66813695675DF331B8697D`.
Both timing modes recorded one 1 MiB staged attempt containing 131,584 already
cached bytes; full misses staged 1 MiB with zero overlap, and crossing/full/
overwritten hits staged nothing. Every byte matched. FINISHED/status/summary/
results/log, all ten raw observation boundaries and independent restoration
were inspected; original enabled 2 GiB Fast/Idle settings restored with timing
off, zero dirty/in-flight bytes and no driver error. This validates accounting,
not a missing-span speed-up. The shaped overlap is 12.55% of the lower traffic;
real random/mixed windows are still needed before changing lower-read behavior.
