# RAM-first cache: contract, implementation tracker and verification

Last updated: 2026-09-24. This is the authoritative execution tracker. Detailed
audit/rationale: [RAM_FIRST_PERFORMANCE_PLAN.md](RAM_FIRST_PERFORMANCE_PLAN.md).
Statuses distinguish source implementation from VM verification. No performance
gain is claimed until measured. Keep each row current in the implementing commit.

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
| A06a / T049-T054, T069 | PARTIAL | T049 ledger, plan-14 pressure proof and plan-16 T050 `drain-decision` contract implemented; T051/T069 are complete. Plan-17 `policies` adds an observed in-flight replacement regression. Recovery now validates all recorded disk keys before any restore action. | Installed 0.4.64.1 pressure, 0.4.67.1 drain comparison and 0.4.69.1 observed-overlap policy run passed. Copied-hive recovery dry run passed, but T050 tuning, controlled T052-T053, and actual T054 offline/Safe Mode recovery remain. |
| A07 / T023-T027, T067-T068, T070-T077, T082/T085 | PARTIAL | Installed paging coherence repair and normal C: activation exist. **Review update:** remaining synchronous progress waits, range-drain interference and application RAM-admission gap assigned to T082/T085. | Focused 0.4.92.1 Q:/C: bytes pass. Actual submitted-order/fault/cancel/dependency and lifecycle proof remain. Historical BSOD cause unknown. |
| A08 / T028-T031, T078-T079, T083-T084 | PARTIAL | Per-case/final image oracles and stable identity are installed and exercised. **Review update:** selected-write overlap and aggregate paging-count matches do not prove submitted completion order or attributable zero-lower admission. | CI-built plan-37 policy/byte passes retained. T083/T084 strengthen the exact claims; role-transition proof remains. |
| A09 / T032-T037, T073-T074, T080-T081, T086 | PARTIAL | Normal activation and active-created restart preparation exist. **Review update:** owner successfully saved and opened the same BMP uncached and with C: Fast active. | T086 capture and post-drain app comparison, T081 active-created restart/pressure and broader lifecycle remain. The observed app success is not image-range pending proof. |
| A10 / T038-T041 | PARTIAL | Setup/recovery foundations and documentation cleanup exist. The recovery script now prevalidates every recorded disk key and labels `-WhatIf` honestly. | Installed 0.4.70.1 script successfully changed a disposable SYSTEM-hive copy, not the live registry. Real offline/Safe Mode boot recovery, full servicing/failure matrix and final product docs remain. |
| A11 / T042-T045 | PARTIAL | Measurement tools and historical evidence exist. | Final-candidate matched/full matrix and bounded endurance pending. |
| A12 / T046-T048 | FALSE | Private-alpha freeze and reporting handoff pending. | Participant release approval not recorded. |
| A13 / T055-T057 | FALSE | Production support contract and safety gap closure planned. | Support scope can be designed during A07; qualification pending. |
| A14 / T058-T060 | FALSE | Security, distribution/signing and production servicing planned. | Actual trust/privilege/servicing evidence pending. |
| A15 / T061-T063 | FALSE | Environment/endurance/final performance qualification planned. | Frozen-candidate support matrix and long-run evidence pending. |
| A16 / T064-T066 | FALSE | Production release/support process planned. | Support collection, staged rollout, recovery and owner release decision pending. |

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
| T080 / A09/T067 | PARTIAL: owner completed uncached and cached edits of the same baseline BMP and opened Photos | Successful app observation plus save timestamp/screenshot; no exact open interval, target-range pending proof or post-drain comparison. Dump capture disabled; Q: benchmark/cache also active | T086 finishes capture/post-drain comparison; preserve this non-reproduction without calling the BSOD fixed. |
| T081 / A09 | PARTIAL: plan-34 active-write preparation source exists; bounded pressure work pending | No installed active-created restart; earlier uncached-created oracle and short startup smoke only | Check cached work survives normal restart and Windows stays usable under paging pressure. |

### Revision 5 follow-up implementation status

| Task | Implementation | Verification / next concrete result |
|---|---|---|
| T082 / A07 | INSTALLED (0.4.99.1 offload; 0.4.101.1 current): top-level and service-lane paging reads needing lower I/O run on a per-disk paging-read thread with exact-version pins; writes wait only for overlapping offloads; destructive requests wait for all; the fence forces only its overlap and unrelated batches keep policy/size. Remaining synchronous fallback: reads over 1 MiB, a full 64-entry table, and service during destructive/control requests. | FORCED DEPENDENCY PASSED on 0.4.101.1 (run `QueueCache-Verify-20260924-214451-6058809968374c149677a21997018845`): with a 32 MiB writer capacity-blocked behind a 16 MiB cache, 4 uncached page faults returned correct bytes (slowest 0.8 ms), with 3/3 offloaded paging reads completing while the writer was still blocked. Remaining: slow-lower-read page-in, memory-pressure and C: paging-role evidence (A09). |
| T083 / A06a/A07-A08 | INSTALLED (0.4.101.1, verifier `1d6dbe3`): one-shot lab range gate at the owned block's disk offset holds a drain after its real lower completion; Diagnostics V9 sequences; later cached C. | PASSED on 0.4.101.1 (same run): gate sequence old submit 1 < lower completion 2 < direct paging write waiting 3 < old retirement 4 < direct submit 5 < direct completion 6; the mapped flush took 2,180 ms against a 2,000 ms hold; newest bytes B, then later cached C active and after release. The first plan-39 run (`...-213420-797a9cbe...`) stopped cleanly because the gate had been armed with a file offset; `1d6dbe3` resolves the disk offset. `policies` also completed on 0.4.101.1 (`...-214519-eb322f68...`). Remaining: failed/short lower completion, cancellation and later sparse-segment failure orders. |
| T084 / A08 | SOURCE CANDIDATE (plan 38): driver records each lower attempt's issuing path (V8); byte PASS and zero-lower-I/O verdicts are separate checks; generated writes/flushes fail, forwarded non-paging I/O is SKIP (unproven, makes the run incomplete), forwarded paging I/O is attributed to other activity. Drain-trigger timing uses generated writes. Without V8 any lower attempt is SKIP. | VERIFIED on installed 0.4.99.1: all 13 zero-lower-I/O checks in `policies`/`pressure` PASSED with zero generated/non-paging attempts; each window's single extra write was a forwarded paging write. Per-request identity is still not recorded; a non-paging request from another process yields SKIP, never PASS. Runtime counter wording now says device-wide. |
| T085 / A07/A11 | OPEN: all paging-marked writes bypass new RAM admission and misses are not retained | Characterize and implement supported ordinary buffered/mapped application caching after progress repair. |
| T086 / A09 | PARTIAL: successful owner Paint/Photos observation recorded; no capture or missing post-drain/restart/pressure results | Restore capture readiness and complete those separate checks with exact file/environment evidence. |
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
