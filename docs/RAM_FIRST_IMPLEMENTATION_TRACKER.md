# RAM-first cache: contract, implementation tracker and verification

Last updated: 2026-09-19. This is the authoritative execution tracker. Detailed
audit/rationale: [RAM_FIRST_PERFORMANCE_PLAN.md](RAM_FIRST_PERFORMANCE_PLAN.md).
Statuses distinguish source implementation from VM verification. No performance
gain is claimed until measured. Keep each row current in the implementing commit.

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
| 3 | Cache sector-valid partial writes without reading disk or draining whole cache | 0–260% affected Q1 recovery envelope (~22 to ~80 MB/s); healthy path may gain 0% | Sector ownership deployed as e8b37be / 0.4.40.1; plan-8 maintained scenario adds partial/full admission attempt checks and positive drain/disk-read counter checks | Plan-8 VM zero-attempt admission, positive drain/read counter checks and byte oracles passed at parallelism 1/2/4, retention off/on. Bounded lower-I/O gate and deterministic fault/lifetime tests remain open |
| 4 | Zero-length/oversized/quota fallback handling | 0–20% affected cases; ordinary fitting 4 KiB often 0% | Partial: valid zero-length writes return without draining; oversized/quota work pending | Native compile; VM no-I/O and request/quota/failure/cancel tests pending |
| 5 | Proven-safe observation/query fences | Isolated writes ~0%; affected hot-reader traffic 0–100%+ | Hotplug GET allowlist implemented in 4dacd5e | Native compile and VM policy retention checks across metadata/discovery passed; focused performance comparison pending; SET remains fenced |
| 6 | Independent drain versions, bounded copy/metadata locking, transient reserves | 0–30% writes during draining | Partial: existing pins and unlocked copies; further work pending | VM full/partial overwrite byte oracles passed with 25 ms delay, parallelism 1/2/4, retention off/on and in-flight observations; deterministic race, allocation failure and lifetime checks remain open |
| 7 | Independent ready-request service around capacity waits/fences | 0–50%+ mixed throughput; unstalled Q1 little gain | Partial: cooperative read lane exists; general admission work pending | Deep queues, ordering, cancel/reinsert, starvation |
| 8 | Admission budget clarity and Automatic clean-space borrowing | 0–20% under pressure; fitting cases ~0% | Pending | Fixed 0/50/100%, Automatic, transient versions, multi-disk budget |
| 9 | Per-4KiB lookup/publication/synchronization overhead | Hypothesis 5–25% CPU-limited; 0% if waits dominate | b63e14b / 0.4.42.1: reuse the reserved single-block write slot, removing two repeated hash lookups; wake coalescing already exists | Native/Debug/Release CI and focused VM correctness passed. Matched three-repeat Idle timing-off medians: Q1 -3.61%, Q32 +1.62%; Q1 has a slow outlier. No demonstrated speedup or performance acceptance; request-time attribution is next |
| 10 | Range-aware TRIM instead of broad drain/in-flight waits | Isolated writes ~0%; concurrent delete workloads 0–50%+ | Range-aware implementation pending; maintained plan-6 driver-independent file probe added after plan-5 routing comparison | Matched attached/unfiltered VM probes both return Win32 326; Q: live stack without QueueCache verified. Same driver/configuration restored and verified after reboot. Partial ranges, reuse, overlapping old writes, malformed/failed requests remain unverified |
| 11 | Cutoff flush, safe live policy changes, transactional resize | Isolated writes 0%; concurrent workloads 0–50%+ | Pending | Exact durable cutoff, concurrent writes, Strict flush, failure/cancel/resize |
| 12 | Foreground cold-read versus drain scheduling | 0–500% mixed recovery envelope; no RAM-only promise | Pending | Mixed/cold + slow disk, sustained capacity pressure, bounded drain progress |
| 13 | Indexed ready selection / independent-range workers if still justified | 0–100%+ high QD; Q1 usually 0% | Deferred until remaining profiles justify redesign | Range ordering, barriers, cancellation, faults, cross-thread lifetime |
| 14 | Power/shutdown/PnP/removal boundaries | 0% throughput; reliability | Pending audit | Dedicated disposable VM lifecycle tests; no automatic destructive recovery |
| 15 | Windowed UI statistics, trigger/wait visibility and faithful evidence windows | 0% driver gain; trustworthy analysis | Partial: repeatable write suite/report exists; UI work pending | UI/CLI parity, sample staleness, interval/lifetime labels |

## Next execution order (2026-09-19)

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
single-slot lookup experiment below did not establish a speedup. Next distinguish
existing request queue/service time from copy/publication time using the maintained
focused selection and existing timing diagnostics before further item-9 edits.
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
