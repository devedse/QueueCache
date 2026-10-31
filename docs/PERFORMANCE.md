# QueueCache performance

How fast QueueCache is, what was decided and why, and the measurements behind each
decision. Start with the decisions table; each row links to its evidence. Results come
from one recoverable 4-vCPU Windows 11 VM on Ceph-backed storage unless stated. Its
backend and the host's load change speeds by up to threefold within hours, so compare
builds only within one session. How tests are run is in [testing](TESTING.md), other
products in [comparison](COMPARISON.md), and bugs, stability and correctness checks in
[known issues](KNOWN_ISSUES.md). Dated reports with full methods are under
[history](history/README.md).

Contents: [Decisions](#current-decisions-and-when-to-repeat-tests) ·
[Reading and comparing results](#reading-and-comparing-results) ·
[Results, newest first](#results-newest-first) ·
[Drain speed and TRIM (#15)](#drain-speed-and-trim-15) ·
[Fix branch verification](#fix-branch-verification-14--15) ·
[Read misses and the first fix branch (#14)](#read-misses-and-the-first-fix-branch-14) ·
[RAM disks, cached reads and scheduling](#ram-disks-cached-reads-and-scheduling)

## Current decisions and when to repeat tests

| Topic | Decision | Reopen when |
|---|---|---|
| Fast write ordering (#13) | **Keep ordering default off. The ordinary mode is the former lab mode 3; a separate forced-write lab mode is measured below.** Mode 2 had made applications slower (plan 194: flushed small files 2.5×, write-through pairs 43×) because every write and bookmark woke all drain threads, which then searched the whole backlog while holding the cache lock (plan 195). The current mode searches only the group it may write now and wakes the threads only for that group. Plan 196 on release 0.4.865.1: writing into the cache took the same time with ordering on and off for a 1.5 GiB file that fits the cache (about 0.2–0.3 s), 25,600 flushed files (about 9 s) and 25,600 write-through pairs (about 0.25 s). Reaching the disk is still slow with ordering on, because each bookmark needs its own disk flush: about **7×** as long as off for files and **54×** for pairs. That only matters for nonstop flush-heavy writing (plan 197): flushing every small file for five minutes filled the 2 GiB cache after about 1.5 minutes, after which the application ran at about 230 files/s instead of about 2,000; `tar` and `robocopy` on a 16,000-file tree showed no difference. [Sustained result](#fast-ordering-13-sustained), [single-mode result](#fast-ordering-13-single), [fix](#fast-ordering-13-fix), [minute-scale result](#fast-ordering-13-minute). The plan-203 forced-write lab variant on a temporary virtual SCSI disk shortened flush-heavy durable completion by **8.8% for files and 7.2% for pairs**, with every pair improved and separated ranges; measured group-flush calls were only about 5–6% of the ordinary mode's total time. [B result](#fast-ordering-31-fua). Its complete sustained comparison still filled and throttled the cache, with five-minute application throughput −0.33%, +3.62%, +2.59% against ordinary ordering and no tree application benefit. [Sustained B result](#fast-ordering-31-fua-sustained). The current ordinary mode passed 14/14 targeted checks under Verifier and six bounded NTFS data-volume VM-reset scenarios plus a middle-drain repeat on 0.4.882.1; the ordering-off early-churn control damaged NTFS. Broader power-loss and system-volume scope remains unqualified. The [final plan-206 release campaign](#release-206) completed/restored all 11 phases, 106 cases and 94 retained checks on the unchanged 0.4.882.1 native pair. Earlier [preparation/clock failures](#release-203-read-preparation) remain separate. #13 was closed for the tested NTFS data-volume VM-reset scope on 2026-10-28 and the "experimental" label removed; the default stays off and broader limits are unchanged. | A default-on decision, a flush-heavy application that matters to users, fewer or cheaper disk flushes per bookmark, remaining bypass/crash qualification, a new storage contract, or group retention/flush scheduling changes. No global or system-volume safety claim. |
| Windows-drive ordering (#32) | **Qualification tests done; default off; the owner decides the scope.** Bounded live correctness passed on 0.4.897.1 to 0.4.909.1. Earlier cost attempts were incomplete: on 0.4.906.1 the on arm lost 262 s after its flush-heavy step because a TRIM waited behind about 190,000 save points and held NTFS (plan 216 fixed it, validated on 0.4.907.1). On 0.4.907.1 its measured steps were not slower than off, but the arm's own explicit cache flush then held every C: request for 386 s behind the same backlog; plan 217 waits for it off the request worker. On 0.4.909.1 the fresh window R10 passed the cost gate: normal use +1.3%, pressure no slower, longest pause 0.32 s, flush-heavy 485 against 1,289/1,469 files/s (report only), and that flush no longer stalled C: (0.21 s). With ordering on, six of seven C: power cuts passed (0.4.910.1/0.4.912.1); the page-file growth cut was not made in five attempts. The same early-churn cut with ordering off damaged NTFS on C:. The release regression on 0.4.912.1 completed (measurements only). [Cost attempts](#c-ordering-cost-observer-32). The unbounded pressure writer under a 100 ms lab delay stalled Windows when a new paging range waited for about 2,400 save points. The bounded fixture does not qualify saturated-backlog responsiveness. [Finding](#c-paging-under-an-artificial-drain-delay-32-october-29). | The owner decides the C: scope with the growth cut not made. Do not rerun R10 or the cuts unless the driver, workload or measurement contract changes. Revisit full-backlog paging latency and page-file growth separately; do not treat a bounded-fixture pass as resolving them. |
| Changed-region image checkpoints (#28) | Keep eligible older-candidate reuse with full read-back and safe full-save fallback. A small change to half-filled 1 GiB images saved **21–22% elapsed time** in the short plan-182 observation. **Mostly empty images have no consistent time gain** in plans 180/181. Protect current/original images and explicit exports. [Evidence](#checkpoint-reuse-28). | Changes to payload mapping, source hashing, verification, candidate ownership or host storage, or a materially different image size/occupancy. These are single observations; logical-byte reduction alone does not establish physical I/O or general speed gains. |
| Asynchronous split RAM-disk reads | **Accepted, default adaptive** (1M Q8T1 +79.84%; Q1 and four readers unchanged). | Changes to provider workers, the Direct read path or chunking; a `ram-read-async` or `ram-read-async-lifecycle` regression. |
| Cached read-hit bookkeeping (copy flag 4) | **Accepted, default on** (1M Q8T1 +7.92%, Q1T1 +4.52%, four readers +3.46%). | Changes to the read-hit path, pinning, recency lists or the slot index; a `cache-read-bookkeeping` regression. |
| Large RAM-disk writes | **No speedup available on this VM**: four writers ≤ +5% over one; queued writes already exceed user-mode copy rates into cold memory. Split writes (September) and streaming stores (plan 120) both rejected. | Different hardware with a measured gap between `ram-write-reference` disk writes and its memory-copy rows. |
| Inline split RAM-disk reads | Retained for queue depth 1, several submitters, paging and synchronous callers. | As above. |
| Shared whole-read queue (and the two earlier whole-read offloads) | Removed: each gave a whole 1 MiB read to one worker. | Not as such. |
| Caller backoff | 256. A selective NTFS/ReFS policy is closed without an experiment: best measured gain +5.26% (NTFS mixed Q8), +3.79% (ReFS), at the 5% bar, while touching ordering/flush paths. | New workload evidence of a material gain. |
| Missing-span reads | Not built: overlap 0.788% / 0.449% of lower traffic in churn. | Representative evidence of material waste. |
| Low-priority cache slowdown | Benchmark-setup effect; launch benchmarks at normal priority (Task Scheduler priority 4). | An observed normal-priority problem. |
| 100 ms map refresh cost | Not measured further at the owner's request; native map reads take milliseconds. | Owner request. |
| Read misses one at a time | **Qualified for throughput, p99 and equal-work tails** (plans 131/132/141, issue #14): arrival order retained, ordinary misses offloaded; latest October 15 same-build repeat: cold random 9.95×, sequential 3.13×, all pairs up and ranges separated, every cold p99 improved, no resident pair loses 3%. The 568.7 ms fixed-window maximum is not reproduced with equal work: the complete 24-case fixed-operation run `20261014-230805-92a9f1ac4191423cbd05b571db4595b2` improved every cold-random pair (+541% to +617% IOPS, p99.9 −92 to −148 ms, no operation above 500 ms) and showed no flag-driven control loss (resident differences follow run order). Both arms use the new pool and queue order; the pool size was not varied. | Changes to read pinning, miss admission, queue order, offload workers or the measurement contract; a backing store with a different latency profile; evidence that the 16-thread pool starves another workload. |
| 40,000-file create/copy/read/hash slowdown | **Environmental: Windows Defender real-time scans, not the driver.** The complete plan-134 same-build matrix (`20261014-175457-b7459c972da24d5a809c98512e31245b`) measured 160.07–170.45 s read+hash in every arm including cache-off, with no QueueCache read hits or writes. A diagnostic case (`20261015-000302-165f7519bbf14c7fac6b28b0e648ef16`, not decision-grade) found `MsMpEng.exe` using 156.5 CPU-seconds of the 164.2-second phase and 40,024 `OnOpen` scans (151.6 s) in the tree worker, with zero Q: disk I/O. Defender stayed enabled for that diagnosis; no exclusion or driver change. The owner later disabled Defender and Search on the dedicated VM (2026-10-15); one subsequent observation completed read/hash in 6.86 s with every hash verified. The cache path also changed, so this is not an isolated driver gain or a complete matrix. Why the 0.4.604.1 session took 4.9 s is unresolved (Defender state was not recorded). `tree-build-decision` was removed unrun. | A recorded Defender-off or excluded baseline that shows a driver-dependent cost, a tree phase that stays slow with Defender scan time near zero, or a new driver path in file reads. |
| Random-read latency tails | **Accepted** (plan 141): 24/24 fixed-operation cases (`20261014-230805-92a9f1ac4191423cbd05b571db4595b2`) show flags 15 cold-random p99.9 60.7 versus 191.7 ms, maximum 123.5 versus 271.6 ms, >100 ms counts down in every pair, none above 500 ms, with equal lower-read counts. Resident random/sequential and fitting-write controls had zero misses and no flag-driven loss; run order explained their pair differences. | A repeated order-balanced control loss of 3% or more, a different backing store, or an observed starvation tail in another workload. |
| Drain batch size and parallelism | **Eager 2048 KiB × 8 is the default since plan 164** (with read-aware limit 2 and TRIM mode 3; [results](#final-164)). Same-window A–B–A: 292.4 / 90.7 MB/s against PrimoCache 261.0 / 52.5 on the two #15 workloads at matched anchors, a concurrent disk reader 757 against 170 IOPS. Under it, Idle 1024 KiB × 4 was the default from plan 125 (plan 132 and 142 explicit-drain gains; [history](history/README.md)). Saved profiles keep their settings. Strict caches also inherit the new default; Strict boundaries under Eager were not measured separately (#16). | Storage with a different latency profile (hard disks and SATA SSDs were not measured), a repeated control loss under the release campaign, or Strict measurements. |
| Next drain improvement | **Done for #15 (plans 159–167).** Eager 2 MiB × 8 with read-aware 2 and asynchronous TRIM is the default and beats a same-window PrimoCache on both workloads ([results](#final-164)). Rejected on the way: aligned gather (plans 155/156), Eager 2 MiB × 4 and read-aware limit 1 (plans 161/162). The uncached Q1 miss path, alone 20–35% slower than Primo's, is a separate improvement. | A workload where Primo leads at matched backend latency, or the Q1 miss-path issue. |
| TRIM while draining | **Fixed; default since plan 164.** A TRIM waits only for overlapping in-flight drain writes (native 0.4.766.1, plans 159/160); TRIM mode 3 adds an index lookup and an asynchronous lower TRIM with a range fence (plans 162/163). Native 0.4.776.1 also keeps the caller path while a lower TRIM is outstanding (`87de299`). Under Verifier `trim-cache` passed 12/12. The fence's holding path is unexercised, because NTFS orders a file's writes after its own TRIM. 0.4.687.1 and earlier wait for every in-flight write. [Known issue](KNOWN_ISSUES.md#trim-no-longer-waits-for-unrelated-drain-writes). | A file system or tool that writes trimmed ranges before the TRIM completes. |
| Strict durability barriers | **Backlog limited to about two seconds of write-out since 0.4.825.1** (#16, [results](#strict-16)). Write-through writes go direct (0.4.810.1); a write that adds dirty data waits while Strict holds as many separate pieces of unwritten data as the drain completed in about 2 s (at least 256; 1,024 before a measurement). The stall after random bursts is gone (random 4K Q1 0 → 7.9 MB/s, flush 1.4 s) at a measured cost: random writes run at the disk's speed (Q32 304 → 7 MB/s), sequential −20% (Q8) / −57% (Q1), mixed −69%; 40,000 files create/copy 15.4/14.3 → 6.6/8.0 s. Accepted: Strict is not the focus. **Rejected earlier:** a byte cap of two seconds of the measured drain rate (sequential 5,510 → 285 MB/s; random still stalled). | A Strict workload that needs more, or the unexplained run 17 minutes after a restart that held writes for whole 10 s windows. |
| RAM-path rates on the test VM | **Host-bound on 2026-10-22, not a branch regression.** The release run's cached reads, RAM-admitted writes and RAM-disk reference were 35–73% below the October 14 run, while disk-bound cases were faster. A same-window `cache-map-cost` pair measured 107k–156k IOPS on the final build and 114k–163k on 0.4.687.1 (host load 28–29 of 32 threads, VM ballooned to 8–12.6 GiB). ([Results](#final-164).) | Before comparing RAM-path rates across days: check host load and the VM's balloon, and run both builds in the same window. |
| Page-ins of file data (#19) | **Kept since plan 173**, from a driver-owned copy of the disk data ([results](#reads-19)). A 1 GiB file read twice through Windows' file cache, with Windows' standby memory purged before each read: 275 then 2,254 MiB/s (871 MiB from RAM). | Changes to the staged read path, page-in routing or the read-fill admission. |
| Coordination/attribution counters | Retained, default off; one flag read when off. | When the diagnosis is needed. |

For a missing-span experiment, first record how many missing runs each partly
cached request contains. Start with a runtime default-off implementation using
the existing owned staging buffer: copy known pinned sectors, merge short gaps,
submit bounded missing ranges and fall back to the current whole-range path when
fragmentation exceeds the bound. Choose the run/merge limits from measured costs,
not the overlap percentage alone. Preserve allocation-failure fallback, short
read/error completion, cancellation, concurrent dirty overlays and pin lifetime.
Version the counter/workload contracts explicitly if a logical staged read can
submit multiple lower requests. Compare identical whole-range and missing-span
modes on partial-residency/churn shapes plus full-hit/full-miss controls, then
run partial-sector, neighboring-write, paging, ordering, drain and capacity checks.
Promote only for a repeated gain without correctness or control regressions.

For a caller-policy experiment, preserve 256 for large copies and investigate a
shorter window only for the measured small mixed shape. First separate the lab
maximum from the production-default constant, retain every owner/queue/control
gate and append attribution for the new choice. Alternate modes on both NTFS and
ReFS, including transitions between small mixed and large reads; a gain restricted
to one shape is insufficient if it damages the next workload. The current global
zero-backoff experiment is not a qualified request-size policy.

Do not repeat an established experiment merely because its result was forgotten.
Repeat a relevant regression when native/runner behavior, hardware, power/CPU
placement, benchmark binary, allocation/copy settings, workload or measurement
contract changes, or when a new observation contradicts the finding. Use the
smallest maintained discriminating suite first; request a broad matrix only
when its scope is needed. Keep failures and raw evidence immutable. Update this
file with conclusions, units, identities, limits and decisions.

The step-by-step records of the plan 115-117 diagnostics (attribution, cached
priority/affinity, coordination counters) that led to the accepted change are in
[history/RAM_READ_DIAGNOSTICS_20261010.md](history/RAM_READ_DIAGNOSTICS_20261010.md).

## Reading and comparing results

### Which changes belong together

| Stage | Changes retained | Evidence and limits |
|---|---|---|
| Already on master before PR #8 | Direct RAM-disk access; large RAM-disk writes use the Standard provider path; provider split-copy helpers; 256 KiB chunk allocator; prefetch and bounded in-chunk RAM-cache hit copies | These earlier optimizations were measured separately. They were not introduced by PR #8 or rejected by PR #9. |
| Merged [PR #8](https://github.com/devedse/QueueCache/pull/8), merge `fb34f878581590123a190b048fdfd23c5d4e9b39` | Read recall; maintained concurrency, sustained-use and map-cost scenarios; neighboring-sector checks; map lifecycle/grouping; map pop-out/full-screen; 100 ms refresh preference and occupancy legend; runner cleanup; regenerated README benchmarks/screenshots | Read recall has controlled positive results and a documented temporary hit-rate trade-off. Full write matrix completed 72/72. Real Windows UI behavior was checked; 100 ms rendering cost was not benchmarked. |
| [PR #9](https://github.com/devedse/QueueCache/pull/9), `perf/ram-read-followups` | **Asynchronous split RAM-disk reads (default adaptive)**; partial-read diagnostics/scenario; caller dispatch attribution and bounded runtime lab control; RAM-reference verification and robust sampling; independent owned-child priority comparison; single-command campaigns; restoration, ownership and parser contracts; consolidated findings | 1M Q8T1 RAM-disk reads +79.84% in the accepted same-build comparison; lifecycle checks pass with and without Driver Verifier. Earlier PR #9 measurements: 10 partial-read checks, 36 RAM-reference windows, 54 RAM-queue comparison windows, 24 windows per NTFS/ReFS caller comparison and 36 priority windows. |
| PR #9 experiment removed | Shared sleeping queue for whole large Direct RAM-disk reads | Same-build comparison loses 3.62% Q1, 11.30% Q8 and 49.62% four-reader throughput. The native engine is removed; wire reservations remain for compatibility. |
| Plan 120, `perf/writes-cache-edge-cases` | **Lighter read-hit bookkeeping (copy flags 7)**; `ram-write-reference`, `cache-read-reference`, `cache-read-bookkeeping` and `stress-races` suites | Cached 1M Q8T1 +7.92% in the accepted same-build comparison. RAM-disk writes measured at the machine's memory write bandwidth. |
| Plan 120 experiment removed | Streaming (non-temporal) stores for large RAM-disk write copies | Same-build comparison: Q1T1 −0.59%, Q8T1 −0.09%, four writers −2.17%. Mode, provider actions 0x105/0x106 (reserved) and comparison suite removed. |

### Measurement rules

- GB/s below means decimal billions of bytes/s; MiB/s means 1,048,576 bytes/s.
  Q8T1 means one submitter with queue depth eight; Q2T4 means four submitters
  with depth two each. Equal total depth does not imply equal execution cost.
- Use the maintained foreground `qcache developer verify` runner, immutable
  run/case IDs and actual loaded-module hashes. A version string does not prove
  which driver is loaded. `MEASURED` proves collection, not speed acceptance.
- Compare the same DiskSpd binary/hash, settings, workload and machine. The
  investigations below use CDM 9.0.3's DiskSpd 2.2 x64, SHA-256
  `7281BF6DA6C03797016EDDF2E8AAEC4C644AE893D403D57A030B7E2E14B61079`.
- Resident controls require zero lower read/write/flush attempts, complete
  telemetry readiness/coverage, stable identity and byte accounting. Missing
  samples or lower-I/O contamination invalidate a window. Preserve incomplete
  runs separately; never join their repetitions into a completed matrix.
- XML supplies scored bytes/seconds. Driver counter and CPU-profile intervals
  enclose process startup, warmup and closure; do not label them exact score
  windows. Traced runs are diagnostic and excluded from untraced acceptance.
- Scopes differ: oracle files prove their own bytes; they do not verify
  DiskSpd's randomized write payload or force every kernel interleaving.

**File-tree environment.** Tracing attributed the 160–170-second read/hash
slowdown to Defender. The full disabled-baseline generic refresh took
2.84 s Fast, 2.90 s paused and 2.87 s PrimoCache, with every hash verified
(one repetition each). The earlier 6.86-second diagnostic and historical
4.9-second session retain their own scope; the latter did not record Defender
state. PrimoCache also has a post-run Ceph near-full warning; its onset and
effect are unknown; the warnings cleared after restoration. Controls and exact runs are in the decisions table and the sections below.

## Results, newest first

<a id="c-ordering-cost-observer-32"></a>
### C: ordering cost observer (#32, October 29)

The first 0.4.897.1 same-build off/on/off window completed but is **invalid**:
the app-launch fixture left browser windows/background processes running, and
memory pressure interrupted the repeatedly launched CLI sampler. Heartbeat
maxima were 10.783 / 4.877 / 25.582 s; every arm lost required telemetry coverage.
Ordinary-work observations were 131 / 124 / 135 s and flushed-file rates
955 / 443 / 909 files/s. These are diagnostic observations, not an isolated
ordering cost. The on arm's final write-out/cleanup took about 20 minutes;
disk flushes progressed without reported I/O failures, but a 1,158 s telemetry
gap prevents a coverage or responsiveness claim for that tail.

The corrected fixture contains each app launch and descendants in an owned
Windows job, verifies closure, uses the existing resident qcache observer and
measures the same 100 ms heartbeat in a resident loop. Its first off arm
completed with a 0.804 s heartbeat maximum and the required locked-memory/4 GiB
pager allocation. Telemetry nevertheless had 3.005 s and 2.639 s gaps, so the
repeat was stopped while the on arm was still idle; no complete comparison is
credited. One gap spent 2.442 s checking the stop file; the other combined
observer delays. Both were ordering-off observations, not evidence of an
ordering-specific slowdown. The original settings/apps were restored; exports
of 850 and 957 files were independently hash-verified. No C: reset occurred.

Plan 211 moves filesystem stop polling from the sampling thread to persistence,
with actual poll timing retained. The sampler observes the published stop flag
and still takes a trailing sample. Cadence, buffer capacity, ready handshake,
missing-field failures and the two-second coverage limit are unchanged. A host
regression blocks a synchronous stop poll and requires sampling to continue,
all collected samples to persist, and a trailing observation after stop. A fresh
complete cost comparison is required; the earlier windows remain invalid.

On 0.4.898.1 (plan 211), 22/22 ordering checks passed again, but the fresh
bounded C: pressure case failed telemetry coverage (8.031 / 3.883 s gaps), with
maximum heartbeat 0.274 s and successful restoration. No final-byte pass is
credited because coverage validation failed first. The fix removed synchronous
stop-file polling from sampling; it did not resolve every observer pause.

One WPR diagnostic repetition also failed coverage (2.646 s; heartbeat 0.228 s).
CLR events show a 1.573 s runtime suspension during the gap, mostly waiting for
threads rather than collecting. Scheduler records show a 2.358 s observer-thread
wait with negligible ready-to-run delay. Resolved wakeup stacks include a hard
page fault caused by WPR's own stack unwinding, so this trace cannot establish
a queue-insertion defect or explain the earlier untraced pause. It is excluded
from qualification. Original trace and failed-run evidence are retained.

Plan 212 gives only this suite's observer a 64 MiB resident working set minimum,
with strict application/restoration evidence before readiness and after completion.
The pressure processes, two-second coverage/heartbeat limits and byte oracle are
unchanged. This is a bounded instrumentation candidate, not a proven resolution
of the earlier pauses or the native growth/backlog limitation. The corrected
request leaves room above its minimum; equal minimum/maximum values were
refused by Windows CI before packaging.

On clean 0.4.900.1 under Verifier, the fresh untraced plan-212 run completed
and restored: 10 GiB locked RAM, 4 GiB pager, 240 acknowledged writes and all
128 latest-generation files exact after release. 651 actual samples covered the
full interval, with maximum gap 1.097 s; heartbeat maximum 0.209 s. It observed
1,340 page-file bypasses and 117 learning boundaries, with zero unidentified
barriers, safe overlaps, table exhaustion or cache errors. Original observer
limits were exactly restored. All 22 ordering checks and ordered recognition
also passed on this package. This qualifies the bounded fixture only; the
earlier failed runs remain separate, and complete same-build cost, C: reset
and final release qualification are still pending.

The next cost attempt on the same 0.4.900.1 package was refused before the first
workload because Edge had started with Windows. It collected no measurements.
After Edge was closed normally, fresh off/on attempts ran with verified app
closure and observer-limit restoration. The off arm completed; the on arm
finished all four workloads and cleanup, but failed the unchanged two-second
coverage guard during its final write-out. The final off control was not
started. **This is another incomplete comparison, not a cost pass.**

| Diagnostic observation, latest attempt | First off control | Ordering on |
|---|---:|---:|
| Copy/archive/extract/app launches/deletion | 115.202 s | 113.758 s |
| Memory-pressure completion | 312.156 s | 311.661 s |
| Longest heartbeat gap during pressure | 0.243 s | 0.262 s |
| Fresh 4 KiB flushed files, five minutes | 1,609/s | 532/s |
| Final write-out, deletion and identity collection | 65.225 s | 347.999 s |
| Largest observer gap, including final write-out | 0.532 s | 234.211 s |

The on arm's gap was 14:19:15.925–14:23:10.137 UTC, after the timed workloads.
Its completed state/performance/diagnostic calls took less than a millisecond;
both previous persistence and sampler wait timings spanned about 234 s.
These timings do not establish the cause or prove a desktop freeze. The
pressure heartbeat had already ended, so it does not cover this tail. Across
the timed workloads, the largest on-arm sample gap was 0.332 s; it recorded
3,185 safe paging bypasses and zero unidentified barriers, overlaps or cache
errors. At the flush-heavy workload's end, 2.078 GB remained pending. Retained
observations surrounding the gap and independent host counters show disk
write/flush progress without reported read/write/flush failures, but cannot
replace missing guest observations. Original C: settings and saved profiles,
Q: uncached, Verifier off, apps/service and detached lab image were checked
after restoration. All 1,680 exported evidence files were independently
hash-verified. No C: reset or final release run followed this failed guard.

Plan 213 adds the focused maintained `system-ordering-drain` diagnostic. Its
independent heartbeat and telemetry run through five minutes of flushed files,
the final cache flush and deletion, with detailed queue timing enabled and the
same two-second coverage limit. It retains both observer results even if one
fails. This is preparation for diagnosis, not a fix or accepted repetition;
the existing bounded paging contract and prior failed evidence are unchanged.

The flush-only plan-213 diagnostic on 0.4.902.1 completed/restored, with 2,376
telemetry samples (maximum gap 0.242 s) and 4,418 independent heartbeat samples
(maximum gap 0.127 s). Its 123,676 flushed files took 300.011 s; final cache
flush took 164.954 s and deletion 16.761 s. The run had 2.080 GB pending before
the explicit flush. Detailed queue timing recorded the storage dataset IOCTL as a read
selection fence and a maximum queue wait of 164.869 s, but it did not reproduce
the lost observer coverage. That queued control alone does not establish why
the earlier observer paused. The 1,782 exported records were independently
hash-verified. The trace is diagnostic only and remains retained.

Plan 214 adds a separate pressure-conditioned diagnostic: five minutes of
locked-memory/4 GiB pager activity before the same flushed-file/write-out work,
with both observers active throughout. Monitor residency is recorded/restored;
the pressure workers retain ordinary paging. The original cost window had this
preceding memory pressure, which the flush-only diagnostic omitted. The new
case tests that condition without changing the existing bounded paging fixture,
weakening coverage or claiming the original pause fixed. Timing diagnostics now
record a queued control's input length and, for the buffered dataset IOCTL, its
action. That interface carries TRIM and other actions; its IOCTL number alone
does not identify the operation. I/O handling is unchanged.

The fresh 0.4.905.1 cost window also stopped before the final off control.
The first off arm completed with maximum telemetry/heartbeat gaps 0.696 / 0.662 s.
The on arm's telemetry stayed complete (0.294 s maximum), but its independent
heartbeat missed 261.738 s during final write-out. The earlier pressure-only
heartbeat had already finished. This is an invalid comparison; completed timed
workloads do not qualify responsiveness through cleanup. Original policies and
profiles were restored, with no reset; all 2,161 exported records were verified.

A separate traced repetition of that existing on arm reproduced a 173.249 s
heartbeat gap and 81.253 s telemetry gap, with zero lost trace events. The heartbeat
thread waited 143.895 s for a page of the installed runtime, followed by another
29.181 s page fault. The telemetry runtime's 81.252 s suspension included a page
fault on its GC thread. Recorded lower-disk reads in that interval completed in
under one second; the evidence establishes blocked page-ins, not slow GC work.
Detailed timing had switched off when the workload launched the desktop app,
so unchanged selection counters and its old 1.7 MiB rejection cannot identify
the later queue fence. That proposed size-limit explanation was discarded.
The trace is diagnostic only, restoration succeeded, and its 2,226 exported
records were verified. The focused plan-215 held-flush regression on unchanged
0.4.905.1 reproduced a blocked 64 KiB page-in behind a queued file flush. Both
flushes remained pending at the two-second failure, and final published timing
recorded 49 fence rejections with an application flush last selected. The run
failed its check and restored. The candidate changes only the healthy enabled
Fast drain's application-flush exception; it does not acknowledge either flush,
bypass older overlapping writes, or cross control/TRIM fences. A native regression
and full new untraced cost window are still required. This controlled reproduction
does not identify every fence in the original trace. No C: cut or cost pass is credited.

On 0.4.906.1 (that read fix; Verifier off) the next window first stopped in its off
arm: the app-launch fixture misread a job's process list while Edge exited. The
fixture now retries that transient state for up to two seconds; the arm's payload
was removed and the original settings restored. The fresh repeat's first off arm
passed: telemetry / heartbeat maximum gaps 0.384 / 0.388 s, M2 97.3 s, pressure
heartbeat 1.492 s, 1,619 flushed files/s. The ordering-on arm completed its timed
steps (M2 113.4 s, pressure heartbeat 0.313 s, 495 files/s, telemetry maximum
0.360 s), then its heartbeat lost 262.529 s, almost exactly the 0.4.905.1 gap.

| Arm (0.4.906.1) | Telemetry max gap | Heartbeat max gap | M2 | M3 heartbeat | M4 files/s |
|---|---|---|---|---|---|
| Off (first) | 0.384 s | 0.388 s | 97.3 s | 1.492 s | 1,619 |
| On | 0.360 s | 262.529 s | 113.4 s | 0.313 s | 495 |

The flush-heavy step ended with 1,958 MiB in RAM in about 190,000 sealed groups.
One TRIM was pending from at least 40 s before the step ended until 5 min after it.
With ordering on it completed only when every earlier group and its lower flush
were on the disk, and the drain wrote about 640 groups/s, one lower flush each. No
write reached the cache for those five minutes although free space rose from 13
to 1,387 MiB; every paging read that reached the driver completed and other reads
stopped arriving, so NTFS was waiting above the driver. The arm's own
`qcache policy flush` reached the driver only after the TRIM completed. The
comparison is incomplete (the final off arm did not run) and is not a cost verdict.
Original settings were restored; 2,809 exported records were verified.

Plan 216 therefore completes an ordered TRIM when it is queued, like a write. The
drain sends its lower TRIM after every earlier group and flush, so crash ordering
is unchanged, and the flush-order regression now requires a TRIM to return within
one second while an earlier group's lower write is held
([TRIM](KNOWN_ISSUES.md#trim-no-longer-waits-for-unrelated-drain-writes)). It still
needs native validation and a fresh complete window; neither the read fix nor this
change is credited for cost yet.

On 0.4.907.1 (plan 216, validated under Verifier) the fresh window R9 again stopped
after its ordering-on arm. Its measured steps were not slower than the off arm, but
after the flush-heavy step its heartbeat lost 385.904 s.

| Arm (0.4.907.1) | Telemetry max gap | Heartbeat max gap | M2 | M3 heartbeat | M4 files/s |
|---|---|---|---|---|---|
| Off (first) | 1.054 s | 0.530 s | 120.3 s | 1.364 s | 1,489 |
| On | 0.261 s | 385.904 s | 114.6 s | 1.443 s | 471 |

The step again left about 190,000 sealed groups (1,967 MiB) in RAM. No TRIM waited
(152 queued, all sent; 309 completed without a lower TRIM while all 16 slots were
in use). Instead the arm's `qcache policy flush C:` reached the driver at once and
held its single request worker for 428 s, a barrier waiting for the whole backlog:
up to 79 requests queued behind it until the drain had written every group, about
290 per second at first and 700 at the end, one lower flush each. Plan 217 lets an
ordered explicit flush wait on an offload thread for the groups sealed before it, so
later requests keep moving; it still takes as long as the backlog. Diagnostics V30 show whether a flush took that path.
`flush-order/flush-leaves-requests-moving` requires a later write and read to
complete within one second while the flush waits for a held group. Original
settings were restored; 3,232 exported records were verified. The comparison is
incomplete and not a cost verdict. Other full barriers (a page-file write outside
the safe ranges, media changes, removal, shutdown) still wait for the whole backlog
while holding requests ([limit](KNOWN_ISSUES.md#fast-mode-without-write-ordering-can-damage-the-file-system-in-a-crash)).

On 0.4.909.1 (plans 217/218, validated under Verifier) the fresh window R10 ran all
three arms (off, on, off) in one hour after the 15-minute settle, Verifier off,
and passed every cost limit.

| Arm (0.4.909.1) | Telemetry max gap | Heartbeat max gap (tail) | M2 | M3 | M3 heartbeat | M4 files/s |
|---|---|---|---|---|---|---|
| Off (first) | 1.096 s | 0.383 s | 124.7 s | 313.0 s | 1.697 s | 1,289 |
| On | 0.303 s | 0.206 s | 120.9 s | 312.0 s | 0.315 s | 485 |
| Off (second) | 0.283 s | 0.231 s | 114.2 s | 311.9 s | 0.501 s | 1,469 |

Normal use was 1.3% slower than the off mean (limit 10%), memory pressure 0.2%
faster (limit 25%), 3,321 page-file writes skipped the queue and none was
unidentified. The flush-heavy step remains the known ordering cost (485 against
1,289 / 1,469 files/s). After it, about 190,000 groups (2,058 MiB) were in RAM; the
arm's explicit flush took the offloaded path (no fallback) and waited 8 min 22 s
for them at about 380 groups/s while the heartbeat's longest pause was 0.206 s and
accepted writes kept rising. R9's 385.9 s stall is gone; the flush still takes as
long as the backlog. Original settings were restored; 3,794 exported records were
verified.

After the C: power cuts, `release-performance` on 0.4.912.1 (Verifier off, Q:, the
same CrystalDiskMark DiskSpd, `--pause-backing-cache`) completed and restored all
11 phases and 106 cases, with all 94 retained checks passing. Its speed rows are
measurements only, not a cross-day verdict. A first attempt wrote its own results
to Q:, so the cache never went quiet and the recall phase stopped INCOMPLETE; it is
kept separate and not combined.

On 0.4.903.1 the pressure-conditioned diagnostic collected 4,287 telemetry and
8,060 heartbeat samples with full coverage; maximum gaps were 0.533 / 0.522 s.
It wrote 112,333 flushed files in 300.002 s after the recorded five-minute
pressure phase, without reproducing the original 234 s gap. The overall run is
**RESTORATION_FAILED**, not an accepted repetition: ordering selection returned
ERROR_BUSY after draining/disabling, and a separate recovery attempt chose the
wrong baseline by losing the recorded suite. All 1,902 exported records were
hash-verified. Scoped runner fixes and live recovery validation precede any new
cost window; no cost or crash-safety verdict changes. Separate official recovery
with the verified 0.4.904.1 controller restored the original 0.4.903.1 native
installation's cache/profile state without rebooting. The failed run's original
completion and case records are unchanged; 1,937 exported records were hash-verified.

### C: paging under an artificial drain delay (#32, October 29)

On native/controller 0.4.896.1 under Driver Verifier, the first guarded two-minute
paging/ordering run was **INCOMPLETE**, with original C: settings restored. Its
unbounded flushed-file writer built about 2,400 save points while a 100 ms lab
delay slowed each drain. The last samples held about 25 MiB; a new paging range
entered its required full ordering boundary. Windows stopped responding for
several minutes, workers exceeded their deadlines, and telemetry coverage was
incomplete. The runner cleared the delay and restored the original cache without
a reboot. This is not a performance verdict: Verifier and an artificial delay
were active. It is evidence of a lifecycle/latency limit: new page-file space
cannot skip earlier pending saves, even if the byte backlog fits in RAM.

Plan 210 bounds the pressure writer at two flushed writes/s, retaining the same
locked-memory/pager pressure, two-minute duration, ≤2 s heartbeat, complete
monotonic coverage and latest acknowledged bytes after release. The new workload
has a distinct case ID. The original run remains failed evidence; bounding the
fixture does not fix or qualify responsiveness with a saturated flush backlog.
Ordering remains off by default and C: qualification remains in progress.

The bounded fixture completed/restored on 0.4.897.1 under Verifier: 10.125 GiB
locked RAM, a 4 GiB pager, 240 acknowledged writes in two minutes and all 128
latest-byte files exact after release. It observed 886 page-file bypasses and
79 learning boundaries, no unidentified barriers, overlaps or cache errors,
and a 0.484 s maximum heartbeat gap with complete monotonic coverage. This is
bounded correctness evidence, not a normal-use cost or saturated-backlog
latency result. The 22 targeted ordering checks and ordered recognition also
completed/restored; C: reset qualification remains pending.

### C: ordering baseline (#32, October 29)

The 0.4.882.1 native pair, with a saved 2 GiB Fast C: cache, ordering off, Verifier
off and the dedicated Defender/Search baseline, passed the pre-implementation
screen. Ten minutes idle and the 91.3 s copy/archive/extract/app-launch workload
had busiest observed one-minute flush-plus-write-through rates of **2.53/s** and
**10.04/s**, below the 100/s gate. Both and the corrected five-minute memory-pressure
step had **zero unidentified direct paging writes, mapping failures or cache errors**.
The guarded recognition suite reported zero reference misses and byte-exact
program-file reads. Recognition counters on this driver mix reads and writes;
DirectWrites is write-only, with no force-direct ranges armed.

The pressure fixture locked 5.625 GiB and the pager allocated 4 GiB; its heartbeat's
longest interval was **0.343 s**. Its first attempt is invalid: an Int32 overload in
the private PowerShell RAM-lock fixture failed, while the pager continued. The
corrected repeat, separate completion records and allocation checks supply the
pressure result; the invalid attempt is not counted as a passing pressure test.
Five minutes creating and flushing new 4 KiB files completed 480,832 files,
**1,603 files/s**. This flush-heavy row is a baseline, not an ordering-on result.

These measurements permit implementation of the C: ordering experiment. They do
not qualify ordering on C:, change its default, or establish an on/off cost. The
existing saved C: profile was preserved and the baseline payloads removed. All 81
exported evidence files were independently hash-checked. Further work is tracked
in [#32](https://github.com/devedse/QueueCache/issues/32).

<a id="release-206"></a>**Final release qualification and #13 scope, plan 206 (2026-10-28).**
The current ordering mode meets the planned NTFS data-volume VM-reset screen:
six ordering-on scenarios plus a middle-drain repeat passed on native 0.4.882.1;
the ordering-off early-churn control damaged NTFS. All completed cuts preserved
the independent durable guard's bytes and lengths. The failed control's image
was preserved before an explicitly approved snapshot rollback. The image is
post-scan: the official scan repaired part of the bitmap online, then still
required offline directory-index repair. No offline repair or format was run.
The two later ordering-off controls were not run after that damage stop.

The final `release-performance` campaign
`20261028-152119-c71f832ab68c4eca94fdc350401519bb` ran 15:21:19–17:02:47 UTC:
**COMPLETED, 11/11 phases, 106/106 cases, RESTORED**. `verify-completion`
accepted its terminal event. Controller 0.4.885.1, clean source `033127a`, used
the unchanged loaded native 0.4.882.1 pair and the same recorded DiskSpd binary.
Both Windows CI configurations and Linux host-safe contracts passed. Verifier
was off; normal priority, the 15-minute boot wait, strict registration,
closed background apps and the disabled Defender/Search baseline were checked.
The ordering-on crash/correctness checks had used Verifier `0x209bb`.

| Final campaign component | Completed |
|---|---:|
| Retained correctness subchecks | 94/94: 22 partial reads, 9 paging, 42 policies, 13 pressure, 8 ordering/faults |
| RAM reference / asynchronous lifecycle | 36 windows / 1 check |
| Recall / sustained / map cost | 12 / 6 / 9 cases |
| Full write matrix | 72/72 cases |
| Sustained file oracles after cache release | 6/6 |

All 186 raw DiskSpd XML scores agree with their saved bytes, operations, elapsed
time and latency fields; none had zero completions. All 65 paired score
intervals had complete telemetry (largest gap 1.030869 s). The 29 intervals
using plan-206 monotonic clocks had a largest gap of 0.2244281 s, and all six
map-poll intervals passed their cadence allowance. Fresh Q1/Q8 reads had zero
lower read/write/flush attempts, drained bytes, misses and errors. Preparation's
first ten-second quiet observation caught two writes / 8 KiB and was refused;
the next was quiet. No scored guard was relaxed. The earlier incomplete
attempts below remain separate.

This completes the planned collection/correctness regression, not a speed
comparison. UTC-aligned backend samples recorded at least **235.89 GB** serving
pool headroom, no observation errors, no new health warnings, and no overlapping
backup/restore/snapshot tasks. Standing unavailable pools 11/15 were separate
from serving pool 8. Global deep-scrub samples at 15:45:58–15:47:23 UTC (pool
unknown) bracket recall cases `0009-r3-reread-recall0`,
`0010-r3-reread-recall1` and `0011-r3-scan-recall0`; their timings may be
disturbed. Other guests wrote/read 8.63/5.67 GB; three guests lacked endpoint
counters and remain unknown. Up NVMe OSD apply/commit latency reached 13 ms.
No cross-day speed verdict is drawn from this shared-backend run.

The final export contains 45,596 independently hash-verified files, 69,411,849
bytes; SHA-256 `4381A99EB5C2BDE033153206782808DE9A62ACB2F0CE83F7695BC2D9250188D8`.
Loaded filter/provider SHA-256:
`50BCAF7D0F9DDAEC0F16A49207D0B657D8E07B73EDD7305FB2CCFBA27D69BA41` /
`06EDCA2D66158D0FC17EC0083A7DD550CA1DC462E8D0F7C24543BBCDDCF6AD54`.
The original Q: profile, stopped managed-disk definition and normal apps/services
were restored; the healthy lab image detached normally. Verifier stays off,
Defender/Search retain the dedicated baseline. The recovery snapshot and
failed-image copy were deleted after review on 2026-10-28.

#13 was closed for this tested scope on 2026-10-28 and the app, CLI and docs no
longer call ordering experimental; it stays off by default. The evidence covers these
bounded resets on one NTFS non-system volume/storage setup, not physical power
loss, every drain configuration, other file systems or system/special-file
paths. The ordinary Fast warning still applies when ordering is off.

<a id="release-203-read-preparation"></a>**Release preparation, plans 203–206 (2026-10-28).**
Two `release-performance` attempts on unchanged native 0.4.882.1 stopped at the
first sustained fresh Q1 resident-read guard. Campaigns
`20261028-095958-b6d5944ae53141e797c212b5afa66cf9` and
`20261028-104214-41dcd9ce2de94b0cbb060c544354deeb` each finished **INCOMPLETE,
7/11 phases, 19/106 recorded cases, RESTORED**. The enclosing snapshots recorded
two lower writes / 8 KiB in the first attempt and four / 16 KiB in the second;
lower reads/flushes, misses and driver errors did not advance. In the first,
the writes preceded the first telemetry sample; in the second they arrived
during the read. Neither rejected window is a RAM-only score or a complete
release result.

The unchanged short standalone sustained probe completed 6/6 cases, with zero
lower read/write/flush attempts in both fresh-read windows and all six persisted
byte oracles passing. A selected final recall case followed by a traced short
sustained probe also completed without reproducing the guard failure. These
separate runs do not complete either campaign or identify the failed writes'
source. Driver Verifier was off; loaded native hashes, the same DiskSpd,
15-minute boot wait, normal priority and the disabled Defender/Search baseline
were checked. Background apps remained closed after the second failure.

Plan 204 submits filesystem metadata after the fresh-read warm-up handles close,
before the existing cache flush and quiet wait. The old preparation could prove
a quiet cache without proving that NTFS had submitted its metadata. This is a
stronger preparation boundary, not an established attribution of the failed
writes or a driver change. The zero-lower-I/O, telemetry, identity and byte checks
remain strict. Sustained cases have new `sustained-v204` IDs; later churned
rereads and the six mixed workloads are unchanged. Linux host-safe tests and both
Windows CI configurations passed on controller 0.4.883.1 (`1053d51`), but its
focused regression still failed: `20261028-120343-3d773484311f488d867c643ba535746c`
and the traced repeat `20261028-121717-86e3978e580a49e3bb8514e2d5424249` each ended
INCOMPLETE, 4/6 phases, 5/11 cases, RESTORED. The filesystem flush was insufficient.

The reproduced read interval was 12:20:32.597300–12:20:42.651706 UTC. File-I/O
records identify System (PID 4) writing two 4 KiB blocks to Q:'s NTFS `$LogFile`
at 12:20:33.120877/33.120973, matching the two lower attempts and 8 KiB admitted
and drained by QueueCache. Preparation had written `$LogFile` and `$Mft` at
12:20:28.843. Both WPR collectors reported zero lost events. The raw v3 payloads
were decoded using Microsoft's [file-key/name correlation](https://learn.microsoft.com/en-us/windows/win32/etw/fileio-readwrite)
and [PerfView field layout](https://github.com/microsoft/perfview/blob/main/src/TraceEvent/Parsers/KernelTraceEventParser.cs#L5886);
the raw FILETIME header aligned the converter's inconsistent
timezone text. This attributes the traced repeat, not the earlier untraced runs.
Raw ETL SHA-256: `5304B8DDBDC33DDC3DE0AAC09160D163BAF8CB6B6E2D4F96813AAA720CAD4746`.

Plan 205 requires a ten-second quiet observation for the fresh resident read,
retaining the five-attempt cap and strict scored checks. Interval evidence records
UTC boundaries and monotonic elapsed time; completed metadata writes still refuse
quiescence even when both snapshots have no pending bytes. Other quiet waits stay
at two seconds. Sustained IDs are `sustained-v205`; no driver code changes.
Controller 0.4.884.1 (`a4395db`) passed both Windows CI configurations and the
focused campaign `20261028-130747-9a70e2ad8db4426bb4746847efdd86a2`:
6/6 phases, 11/11 cases, all 94 retained checks and six persisted byte oracles,
RESTORED. The first ten-second quiet observation caught two writes / 8 KiB and
was refused; the next was quiet. Both fresh scored reads had zero lower I/O.

The full plan-205 release attempt
`20261028-132426-b201fc84d09f438fa9448b9ddd94cd23` stopped INCOMPLETE at
7/11 phases and 22/106 recorded cases, RESTORED. Sustained case 4's churned Q8
reread had a UTC gap of 2.016181 s but a monotonic gap of only 0.2073548 s.
Windows Kernel-General event 1 records a 1.809-second wall-clock advance at
14:16:25.866621 UTC, matching the discrepancy. Capture stages remained below
one millisecond at the boundary. This was a clock adjustment, not an actual
sampling pause; the existing UTC guard correctly prevented acceptance under
that measurement contract. No aggregate is formed from the incomplete run.

Plan 206 records system-wide Stopwatch ticks/frequency in cache-exercise samples
and both coordinator process boundaries. Coverage uses that common monotonic
time base, retaining the same strict two-second limit and all UTC observations.
Map polling uses the same clock with its existing cadence allowance. Missing
clocks never fall back to UTC. Microsoft's [QPC guidance](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps)
documents its common time base across processes on the same Windows boot.
Affected IDs are versioned `sustained-v206` / `exercise-v206`; other suites keep
their coverage contracts. Linux host-safe tests passed, including missing clocks,
incomplete coverage, frequency mismatches, reversed samples and the unchanged
two-second boundary. Both Windows CI configurations passed on controller
0.4.885.1. Focused campaign `20261028-150511-18712248b62849efa81ff59208eb1812`
completed 6/6 phases, 11/11 cases, all 94 retained checks and six persisted
oracles, RESTORED; a selected map250 probe also completed/restored. Independent
raw audits passed before the complete [final release campaign](#release-206).
No speed improvement is claimed.

<a id="fast-ordering-31-fua-sustained"></a>**B under sustained use (#31, plan 203, October 26).**
The forced-write variant does not resolve the sustained flush-heavy bottleneck.
In three five-minute file runs, B changed application throughput by **−0.33%,
+3.62%, +2.59%** against ordinary ordering. Both filled the usable 2 GiB cache
and waited for space; neither approaches ordering off. B's final drain was
shorter in all three repetitions, but the arms completed different file counts,
so that is not an equal-work drain-speed comparison. Keep B as a lab control and
ordering default off. The [short equal-work comparison](#fast-ordering-31-fua)
establishes its limited durable-completion benefit.

This is a complete three-arm, three-repetition comparison on the same native
0.4.875.1 and plan-203 controller as the short run below. Every arm takes each
position once. Values separated by slashes are repetitions 1 / 2 / 3, not samples
combined from different runs. Off still has Fast caching enabled.

| Five-minute flushed 4 KiB file workload | Off | Ordinary ordering | B |
|---|---:|---:|---:|
| Files completed | 1,076,109 / 973,289 / 978,349 | 322,527 / 301,809 / 301,543 | 321,462 / 312,686 / 309,366 |
| Files/s, first 30 s | 3,559 / 3,579 / 3,493 | 3,017 / 2,896 / 2,911 | 3,002 / 2,927 / 2,904 |
| Files/s, last minute | 3,556 / 2,273 / 2,213 | 528 / 445 / 458 | 554 / 489 / 543 |
| 90% of configured cache budget dirty after | never | 86 / 102 / 84 s | 98 / 79 / 79 s |
| Peak dirty data | 7.5 / 7.4 / 10.3 MiB | 1,988.5 MiB each | 1,988.5 MiB each |
| Waits for cache space | 0 each | 189,210 / 156,447 / 169,995 | 127,180 / 136,928 / 141,182 |
| Extra time after application completion until on disk | 1.62 / 1.37 / 1.44 s | 218.66 / 239.05 / 270.94 s | 202.22 / 236.85 / 230.41 s |
| Group flushes | 0 each | 322,708 / 301,982 / 301,710 | 124 / 113 / 110 |

B completed 309,434–321,533 groups by forced writes instead, but retained
631,565–656,545 forced lower writes. Eliminating almost every group flush did
not eliminate the many small ordered writes. Ordinary group-flush durations
summed to 18.08–18.97 s; B's summed to 0.0090–0.0097 s. These request durations
are not disjoint portions of elapsed time. The last-minute application rate was
higher with B in every repetition, but the whole five-minute throughput did not
improve consistently, and B did not consistently delay cache saturation.

The unflushed tree controls also establish no application benefit. Each copies
or extracts the same 16,000-file, 1,328 MiB tree, verifies every file after cache
release and records no waits for space. Times are medians [minimum–maximum].

| Tree workload and timing | Off | Ordinary ordering | B |
|---|---:|---:|---:|
| `tar`, application | 2.83 s [2.68–3.22] | 2.90 s [2.74–3.16] | 2.89 s [2.78–2.89] |
| `tar`, until on disk | 10.49 s [10.47–10.66] | 10.74 s [10.23–12.93] | 10.34 s [10.04–10.50] |
| `robocopy`, application | 4.96 s [4.84–5.29] | 4.98 s [4.86–5.10] | 5.11 s [5.10–5.34] |
| `robocopy`, until on disk | 9.23 s [9.13–10.43] | 9.72 s [9.66–10.45] | 9.72 s [9.59–10.70] |

`tar` durable time improved in all B pairs (1.85–18.82%), with overlapping
ranges. `robocopy` application time increased 0.007%, 9.82% and 2.64%; durable
time changed +2.37%, +0.63% and −1.33%. There is no repeated application loss
of 3% or more, but this is not evidence of a general application speedup.

Run `QueueCache-Verify-20261026-182453-0e6a1dfeebe4442cb582f7430705bb12`
completed all 27 inner cases, its full telemetry interval and restoration,
18:24:53–19:52:48 UTC. Every tree file and the prescribed sample of flushed
files (every 64th plus the final thousand) matched unbuffered after release;
there were no driver errors. The evidence includes the exact `FINISHED.txt`,
status, summary, results, worker replies, samples, checkpoints and run log.
Backend sampling bracketed the whole run with a maximum 5.001 s gap, no sampling
errors, new health checks or overlapping backup/restore tasks; serving-pool
available space stayed above 244.70 GB. Other guests continued I/O and three
guests' counters were unavailable. A deep scrub was observed. The pre-existing
health warning for unrelated offline pools remained; this was shared storage,
not an exclusive or wholly healthy cluster.

All 966 evidence files, including the separate incomplete attempts, were archived
and independently checked against their length/SHA-256 manifest before removal
of T:. Archive SHA-256:
`B77093EE459F4FB2F15112022781CCE5DF446C122212BEA4751C2535266F6468`.
The temporary disk was taken offline, detached and its backing volume deleted;
guest, live block-device, current/pending configuration and storage checks agree.
Original disks, Q:'s settings, stopped managed RAM disk and the closed applications
were restored on the tested driver. The final guest receipt SHA-256 is
`46CADE32B1F6431118BE67806251FDCEC333B1D188D6D1EBC69C2153790634F3`.
The TRIM and power-cut qualification gaps below remain.

<a id="fast-ordering-31-fua"></a>**B: force small groups instead of flushing them (#31, plans 200–203, October 26).**
A modest improvement on the temporary virtual SCSI disk, rather than removal of
ordering's write-out cost. Lab mode 2 writes every part of an eligible sealed
group of at most 1 MiB with forced unit access (`SL_WRITE_THROUGH`) and advances
only after all those writes complete. Groups that already sent an ordinary write
or a TRIM still flush. Selection requires the actual lower device to report
write-through support; W:'s VHDX does not, so this comparison used a temporary
16 GiB Proxmox disk, NTFS T:, with the same pool/options as Q:.

Same loaded driver in all arms, three repetitions with each arm once in each
position. Times are medians, with ranges in brackets. Into the cache ends when
the last application write returns; on disk also includes closing the handles,
flushing the filesystem and the explicit cache checkpoint.

| Workload | Into the cache: off / on / B | On disk, off | On disk, on | On disk, B |
|---|---|---:|---:|---:|
| One 1.5 GiB file, fits the cache | 0.196 / 0.180 / 0.157 s (overlapping ranges) | 8.53 s [8.13–8.60] | 8.21 s [8.14–8.29] | 8.31 s [8.21–8.36] |
| 25,600 new 4 KiB files, each flushed | 7.23 / 7.64 / 7.43 s | 7.28 s [7.22–7.28] | 26.78 s [25.78–28.15] | 24.42 s [24.28–24.50] |
| 25,600 ordinary + write-through 4 KiB pairs | 0.218 / 0.226 / 0.225 s (overlapping ranges) | 1.09 s [0.96–1.12] | 24.64 s [24.23–25.93] | 22.86 s [22.29–23.86] |

Relative to ordinary ordering, B shortened durable time **5.26%, 8.52%, 13.73%**
for files and **14.04%, 3.15%, 5.67%** for pairs (median reductions 8.80% and
7.23%). Every paired repetition improved and the ranges separate. The sequential
control changed +2.07%, −0.96%, +1.75% in elapsed time, with overlapping ranges
and no repeated 3% loss. No application-time speedup is established. B still
took 3.35 times off's durable time for files and 21.07 times for pairs (ratios of
medians).

The new counters answer why the gain is limited. Ordinary ordering used 25,623
group flushes for files and 25,603 for pairs. B used only 12–17 and 0–1, with
about 25,600 groups instead completed by forced writes. But the measured ordinary
group-flush calls summed to only **1.45–1.50 s** for files and **1.24–1.26 s** for
pairs, about 5–6% of their total durable time. Lower-write time remained large:
41.8–45.7 thread-seconds for files and 22.6–24.3 for pairs, versus 41.4–42.0 and
22.1–23.7 with B. These are summed request durations across workers, so they can
exceed wall time and cannot be added as disjoint portions. Eliminating the
separate flush cannot eliminate the time spent waiting for each forced write.
For write-through pairs, ordering off submitted only **211–241 lower writes**,
where ordinary ordering submitted **25,606–25,618** and B **25,605–25,606**.
For flushed files the counts were 23,398–24,823 off and about 52,300 with either
ordering mode. B preserves the groups and their small ordered writes, so it
retains that request-count cost even when nearly all separate flushes disappear.

Setup: native release 0.4.875.1 (`88331c0`), loaded filter
`73281443FE5D00E5A0536D6F55C7CDFE3E34F012F364233D3E357196D049B4E0` and RAM driver
`06723ACB5DC9E2B7BC1F5FD04217951903A3B4068D2038A6F345D417327030F5`, hashes
verified. Controller published from `77c315c` (plan 203), Fast 2 GiB, Eager
1024 KiB × 4, timing on in every arm, normal priority, Verifier off, Windows up
35 minutes, last-access updates off; Everything/service, Steam and the tray
closed, Defender/Search services stopped and disabled, WdFilter absent. The run
`QueueCache-Verify-20261026-181141-62dc3314a87946559d9ed217b1030867`
completed 27/27 inner cases, full telemetry coverage and every unbuffered
post-release byte check, with restoration successful. Six checkpoints required
two attempts; none needed a third. Plan 203 flushes filesystem metadata before
each of the same three permitted cache flushes; the earlier plan-201 run stopped
at its last checkpoint and remains a separate incomplete matrix. Backend
monitoring covered the full rerun: no new health warning or overlapping backup
or restore task, serving-pool available space at least 247.11 GB. Other guests
continued I/O; three guests' counters were unavailable. The completed review
bundle was independently hash-verified (SHA-256
`942975F64D080B604FBAE127A78E63B83AD5D135A9CA4CC1458E3C533B4D96E6`).

Under Verifier, ordinary ordering passed 14/14 checks on W: and B passed its
11 non-TRIM checks on T:. T: refused file-level TRIM; W: supports it but reports
no FUA support. There was no power cut with this variant, and reported FUA
support is not end-to-end persistence qualification. The ordinary setting stays
off by default; B remains an explicit lab control.


<a id="fast-ordering-13-sustained"></a>**Does ordering's slower write-out matter in longer use? (#13, plan 197, October 26).**
Only for nonstop flush-heavy writing. An application that flushes every small
file for five minutes fills the 2 GiB cache after about 1.5 minutes with ordering
on, and is then held to the rate at which bookmarks reach the disk: about 230
files/s, against about 2,000 with ordering off. Windows' own `tar` and `robocopy`
on a 16,000-file, 1,328 MiB tree showed no difference: they do not flush per
file, and NTFS created only 22–189 bookmarks of its own per run. Release 0.4.868.1,
same build in both arms, two repetitions with alternating order.

| Workload | Off | On |
|---|---:|---:|
| Flushed 4 KiB files written in 5 minutes | 605,234 / 837,553 | 228,586 / 230,162 |
| Files/s, first 30 s | 2,260 / 3,262 | 2,413 / 2,700 |
| Files/s, last minute | 1,841 / 2,191 | 218 / 243 |
| Cache 90% full after | never (peak 5 / 15 MiB) | 85 / 77 s |
| Extra time after the 5 minutes until everything is on disk | 1.2 / 1.6 s | 463 / 534 s |
| `tar` extract: into the cache / on disk | 3.23 / 3.69 s; 10.4 / 9.6 s | 3.29 / 3.57 s; 10.6 / 11.1 s |
| `robocopy` copy: into the cache / on disk | 5.43 / 5.76 s; 10.3 / 11.1 s | 5.50 / 5.90 s; 10.9 / 11.2 s |
| Bookmarks: files / `tar` / `robocopy` | 0 (off does not count them) | 228,780 and 230,371 / 189 and 172 / 22 and 27 |

Values are repetition 1 / repetition 2; repetition 1 ran off first, repetition 2
on first. With ordering on, the unwritten backlog grows from the start because the
application flushes faster than bookmarks reach the disk; the first wait for cache
space came at 93 / 86 s, and 147,791 / 158,739 writes waited in total. The off arm
varied between repetitions (605,234 against 837,553 files) without any backlog;
no storage-backend monitor ran. An earlier run of the same suite on 0.4.865.1
measured 801,914 files off and 212,868 on in its first repetition before it stopped
at a driver race (see [known issues](KNOWN_ISSUES.md#correctness-qualification));
its other cases are not used.

Setup: published release 0.4.868.1 (`be1ad24`, the race fix), loaded filter
`94044F378A60F96E5178393CC362C05F001D6542FE9C4EA85585034A36FE9BE7` and RAM driver
`97BD8FFB0E650FBD97B02BCB537F188F5BA96544CBD93E0C9206CD4379BB5D92` (hashes
checked), installed controller from the same release. Disposable NTFS W: (8 GiB
VHDX on Ceph-backed storage), backing Q: paused by the campaign, Fast 2 GiB,
Eager 1024 KiB × 4, lock timing on in both arms, Verifier off, Windows up 20
minutes, normal priority, Everything/service, Steam and the tray closed, Defender
and Windows Search not running. Campaign
`QueueCache-Campaign-20261026-132758-661ec8bb27664852967ccd1929264dd7`
completed 1/1 phases and 1/1 outer cases in 2,739 s; all 12 inner cases passed
their unbuffered byte checks (every tree file; every 64th flushed file and the
last thousand); restoration `RESTORED` and `verify-completion` passed. Q:'s
profile, the applications and the loaded driver were restored; W: is unconfigured
with ordering off. Private evidence: 956 hash-verified files, archive SHA-256
`98D1CE2F7AAF80421BB7D5990E54D4DB24EFED29DF373318DCD57EF239114B9B`.

<a id="fast-ordering-13-single"></a>**One ordering mode, off versus on (#13, plan 196, October 26).**
Plan 196 removed modes 1 and 2; ordering on is now the former lab mode 3. On
release 0.4.865.1 it again matched ordering off for time into the cache, and
reaching the disk still takes one disk flush per bookmark. Both arms ran on the
same loaded build, alternating which ran first. Times are the middle of three
runs (range in brackets); "into the cache" and "on disk" are measured as in
[plan 195](#fast-ordering-13-fix).

| Workload | Into the cache, off | Into the cache, on | On disk, off | On disk, on |
|---|---:|---:|---:|---:|
| One new 1.5 GiB file, 1 MiB writes (fits the cache) | 0.297 s [0.151–0.593] | 0.183 s [0.153–0.222] | 7.8 s [7.7–7.8] | 7.9 s [7.8–8.0] |
| 25,600 new 4 KiB files, each flushed | 8.96 s [8.90–10.82] | 9.60 s [8.31–9.64] | 9.0 s [8.9–10.9] | 61.0 s [57.3–61.4] |
| 25,600 ordinary + write-through 4 KiB pairs | 0.248 s [0.227–0.291] | 0.267 s [0.231–0.623] | 1.0 s [0.9–2.0] | 54.2 s [51.7–118.6] |

- **Into the cache:** every range overlaps; no difference between off and on is
  established. The slowest 1.5 GiB value (0.59 s, off) was again the first
  case after the warm boot.
- **On disk:** ordering on took about 7× as long for files and 54× for pairs
  (25,622–25,623 and 25,603–25,604 group flushes per run). The first pairs run
  with ordering on took 118.6 s against 51.7 and 54.2 s for the others; no
  storage-backend monitor ran, so its cause is unknown.
- **Cache lock:** during the file phase threads waited 3.5 s in total for the
  cache lock with ordering on (0.09 s off), the same level as mode 3 in plan 195
  and a tenth of mode 2's 35 s.

Per run (off / on, seconds into the cache; seconds on disk): sequential
0.593/0.222, 0.151/0.183, 0.297/0.153; 7.8/7.9, 7.8/7.8, 7.7/8.0. Files
10.82/8.31, 8.96/9.64, 8.90/9.60; 10.9/57.3, 9.0/61.4, 8.9/61.0. Pairs
0.248/0.231, 0.227/0.623, 0.291/0.267; 2.0/118.6, 1.0/51.7, 0.9/54.2. Repeat 2
ran on before off.

Setup: published release 0.4.865.1 (`f9ce425`), loaded filter
`BFC1994800A773C703963D178FC5E22D693B0154E2A9D33DDCF5B27B781C8902` and RAM
driver `CD7128DDDF63EA9B0737C3184C1FC7486C2ED9011B01FF0025DF74123B443235`
(hashes checked), installed controller from the same release. Disposable NTFS
W: (8 GiB VHDX on Ceph-backed storage), backing Q: paused by the campaign, Fast
2 GiB, Eager 1024 KiB × 4, lock timing on in both arms, Verifier off, Windows up
19 minutes, normal priority, Everything/service, Steam and the tray closed,
Defender and Windows Search not running, last-access updates off. Campaign
`QueueCache-Campaign-20261026-115758-7c6cb355d6974ca3b39284611db45786`
completed 1/1 phases and 1/1 outer cases in 766 s; all 18 inner cases passed
their unbuffered byte checks; restoration `RESTORED` and `verify-completion`
passed. No storage-backend monitor ran. Q:'s profile, the applications and the
loaded driver were restored; W: is unconfigured with ordering off. Private
evidence: 474 hash-verified files, archive SHA-256
`BF3F92ED66BCC5D808AEA115FC0A1DD75D2B2F34FD74BEBFBDDAECDFB9BC60FE`.

<a id="fast-ordering-13-fix"></a>**Ordering off, mode 2 and lab mode 3 (#13, plan 195, October 26).**
Lab mode 3 removes the application slowdown that ordering caused in plan 194;
the time until everything is on the disk is unchanged. Mode 3 keeps mode 2's
groups, but a drain thread looks only at the group it may write now, and writes
or bookmarks in later groups no longer wake every drain thread. All three arms
ran on the same loaded build; each arm took each position once over three
repetitions. Times are the middle of three runs (range in brackets). "Into the
cache" ends when the application's last write returns; "on disk" also includes
closing the files, the NTFS volume flush and an explicit QueueCache checkpoint.

| Workload | Into the cache, off | Mode 2 | Mode 3 | On disk, off | Mode 2 | Mode 3 |
|---|---:|---:|---:|---:|---:|---:|
| One new 1.5 GiB file, 1 MiB writes (fits the cache) | 0.178 s [0.147–0.699] | 0.204 s [0.148–0.969] | 0.173 s [0.151–0.263] | 7.7 s [7.7–8.3] | 7.8 s [7.8–8.3] | 7.8 s [7.8–7.9] |
| 25,600 new 4 KiB files, each flushed | 7.82 s [7.76–10.78] | 20.08 s [19.21–20.29] | 8.27 s [8.19–8.53] | 7.9 s [7.8–10.8] | 62.1 s [61.8–62.4] | 57.5 s [55.5–59.7] |
| 25,600 ordinary + write-through 4 KiB pairs | 0.233 s [0.220–0.247] | 9.35 s [9.27–9.42] | 0.236 s [0.236–0.282] | 0.9 s [0.9–1.0] | 54.6 s [52.2–56.0] | 57.2 s [49.5–60.7] |

- **1.5 GiB file that fits:** about 8.4 GiB/s into the cache with ordering off,
  7.4 with mode 2 and 8.7 with mode 3 (medians), with no wait for cache space in
  any run. The ranges overlap: ordering does not change it. The two slow values
  (0.70 s and 0.97 s) were the first two cases after the warm boot.
- **Flushed files:** mode 2 took 2.6× as long as off to get into the cache;
  mode 3's runs lie inside off's range, so no difference from off is
  established.
- **Write-through pairs:** mode 2 took 40× as long; mode 3 matched off.
- **On disk:** ordering on still needs one disk flush per bookmark (25,621–25,630
  group flushes per run), so files took about 7× and pairs about 60× as long as
  off. Mode 3 was 7% faster than mode 2 for files (ranges do not overlap); for
  pairs the ranges overlap. The disk was about three times faster than during
  plan 194 (mode-2 files 62 s against 204 s), so compare only within one run.

The cause, measured with lock timing on in every arm (application phase only,
summed over all threads, middle of three runs):

| Workload | Drain-thread wake-ups, off / mode 2 / mode 3 | Waiting for the cache lock (ms) | Drain threads searching the backlog (ms) |
|---|---:|---:|---:|
| Flushed files | 336 / 95,139 / 54,251 | 59 / 35,348 / 2,705 | 17 / 13,241 / 415 |
| Write-through pairs | 1 / 61,058 / 217 | 9 / 18,356 / 13 | 0 / 6,074 / 0 |

During mode 2's 20-second file phase the drain threads spent 13.2 s searching
the backlog while holding the cache lock, and threads waited 35 s in total for
it. That confirms the cause suspected in plan 194. In mode 3 the remaining
wake-ups in the file workload come with group flushes, which make the next group
writable, and with writes into the group being written.

Setup: published release 0.4.862.1 (`b6ec0e1`), loaded native filter
`1A19AFBD7FF2399A8E30504D5E7CE2EEF6EEF8A05A2637C08D7E11282660CAF3` and RAM
driver `9DE0E2ABD088A7B86BF02A5E29FBFC4A8860F2EED88E841B7D62A312F5D821E7`
(hashes checked). Controller published locally from `b410142`, which only fixes
the flush-order check count; the cost scenario is unchanged from `b6ec0e1`.
Disposable NTFS W: (8 GiB VHDX on Ceph-backed storage), backing Q: paused by the
campaign, Fast 2 GiB, Eager 1024 KiB × 4, lock timing on in all arms, Verifier
off, Windows up 19 minutes, normal priority, Everything/service, Steam and the
tray closed, Defender and Windows Search not running, last-access updates off.
Campaign `QueueCache-Campaign-20261026-033002-448e51ed4a8f42ae893fb2b47dc59d94`
completed 1/1 phases and 1/1 outer cases in 1,181 s; all 27 inner cases passed
their unbuffered byte checks; restoration `RESTORED` and `verify-completion`
passed. No storage-backend monitor ran; all arms ran in one 20-minute window in
rotated order. Q:'s profile, the applications and the loaded driver were
restored; W: is unconfigured with ordering off. Private evidence: 1,192
hash-verified files, archive SHA-256
`EF2C2ACC0BAE4E9E2BBCCD0A85B47D7AD808C5BDA1CB4499F2A6DA5627214DC4`.

<a id="fast-ordering-13-minute"></a>**Ordering off/on at minute scale (#13, plan 194, October 25–26).**
Keep ordering off by default. With the same work in both arms, ordering on
did not change sequential writes, but it made the application itself slower
and the disk much later for flush-heavy work. Times are the middle of three
runs (range in brackets). "Into the cache" ends when the application's last
write returns; "on disk" additionally includes closing the files, the NTFS
volume flush and an explicit QueueCache checkpoint, so everything is on the
disk at its end.

| Workload | Into the cache, off | Into the cache, on | On disk, off | On disk, on |
|---|---:|---:|---:|---:|
| 4 GiB file written twice (8 GiB, 1 MiB writes) | 62.2 s [46.8–72.3] | 56.2 s [53.7–62.3] | 85.4 s [63.0–93.6] | 74.9 s [71.7–88.7] |
| 25,600 new 4 KiB files, each flushed | 8.1 s [7.3–10.8] | 20.7 s [20.4–24.6] | 8.2 s [7.4–10.9] | 203.9 s [192.6–255.6] |
| 25,600 ordinary + write-through 4 KiB pairs | 0.21 s [0.19–0.21] | 8.97 s [8.87–9.52] | 0.97 s [0.94–1.12] | 166.0 s [145.3–168.7] |

Per run (off / on, seconds into the cache; seconds on disk): sequential
62.24/53.73, 72.31/62.32, 46.81/56.18; 85.4/74.9, 93.6/88.7, 63.0/71.7. Files
10.81/20.38, 8.12/24.59, 7.30/20.67; 10.9/192.6, 8.2/203.9, 7.4/255.6. Pairs
0.21/8.87, 0.21/9.52, 0.19/8.97; 1.1/168.7, 1.0/166.0, 0.9/145.3. Repeat 2 ran
on before off. Sequential ranges overlap: no difference is established; both
arms filled the cache and wrote at the disk's speed (about 100 MB/s that night),
with 3,853–3,980 waits for cache space each.

The flush-heavy slowdowns are not capacity backpressure: those cases recorded
no waits for cache space, and the unwritten backlog peaked at 312–332 MiB
(files) and 180–191 MiB (pairs) of 2 GiB. Getting to the disk took one group
flush per bookmark (25,627–25,632 and 25,604), at about 100–190 per second. That rate
stayed flat while the backlog shrank from about 320 to 100 MiB, so the write-out
waited on the disk for each bookmark (about 5–10 ms each that night, against
about 2 ms in the afternoon's short screen; sequential throughput was also
roughly half the afternoon's). During the application phase the drain threads
were woken 52,098–84,958 times with ordering on, against 0–258 with it off. A
plausible cause of the application-side slowdown is those threads competing
with the writer for the cache lock; lock timing was off here. Plan 195 measured
it and confirmed it ([result](#fast-ordering-13-fix)).

Setup: plan-194 controller published locally from the committed source, loaded
native 0.4.851.1 (filter
`89964D25937701001AE16CC86102AD9246BA3ED78E7FE4E84088E5CBCE202CF4`, RAM driver
`82501FF4434782636F1E267D1488123430B749453A30D639ED220C5268217FF1`). Disposable
NTFS W: (8 GiB VHDX on Ceph-backed storage), backing Q: paused by the campaign,
Fast 2 GiB, Eager 1024 KiB × 4, Verifier off, Windows up 6.6 hours, normal
priority, Everything/service, Steam and the tray closed. Campaign
`QueueCache-Campaign-20261025-230348-812f5ca387ab4238bc2d418817e973f6`
completed 1/1 phases and 1/1 outer cases in 2,083 s; all 18 inner cases passed
their unbuffered byte checks; restoration `RESTORED` and `verify-completion`
passed. Backend observations covered every case: no backup/restore task, scrub,
recovery or new health warning; serving-pool space stayed above 249.7 GB. Other
guests kept doing I/O and three guest counters were unavailable. Q:'s saved and
runtime profile, the applications and the loaded driver were restored; W: is
unconfigured with ordering off. Private evidence: 476 hash-verified files,
archive SHA-256 `00F3E372CEAC418A9F5D7B5D1BDA441F2D87F64AB61AE5BCCFDC4BA987B6FC28`.

<a id="fast-ordering-13-toggle"></a>**Direct ordering off/on comparison (#13, plan 193, October 25).**
The current toggle has a measured durable-completion cost. On the same loaded
native build, mode 2 took **4.06–4.91×** as long as off for 256 explicit file
flushes and **18.06–29.45×** for 256 ordinary/write-through pairs. Sequential
pairs changed direction and their ranges overlap; no sequential gain or loss is
established. Application-time differences also changed direction. These results
measure the toggle directly; release runs from other days and the earlier
mode-1/mode-2 comparison are not controls for it. Keep both experiments off by
default.

The maintained `flush-order-cost` experiment now compares mode 0 with mode 2,
using new immutable `flush-order-cost-v193` case IDs. Workloads and timing/byte
contracts are unchanged from plan 189. A locally published plan-193 controller
ran the complete three-workload, three-repetition matrix in **79.28 s** on the
unchanged native 0.4.851.1. Loaded hashes were checked: filter
`89964D25937701001AE16CC86102AD9246BA3ED78E7FE4E84088E5CBCE202CF4`, RAM driver
`82501FF4434782636F1E267D1488123430B749453A30D639ED220C5268217FF1`.
Campaign `QueueCache-Campaign-20261025-211920-a9a5ba13fd3e48689b92d54c403e8ba2`
completed 1/1 phases and 1/1 outer cases; all **18/18 inner cases passed byte checks**, with
restoration `RESTORED` and `verify-completion` passed. Host management tests and
the Windows CLI publish passed; no native code changed or broad release rerun
was needed.

Both arms used disposable NTFS W:, 256 MiB Fast/Eager, retained writes/read
promotion and 1024 KiB × 4 drains, with backing Q: paused by the campaign.
Verifier was off, Windows had been up almost five hours, normal priority and
disabled last-access updates were checked, and Everything/service, Steam and the
tray were closed. The dedicated Defender/Search-disabled baseline was verified.
Repeat 1 and 3 ran off/on for each shape; repeat 2 ran on/off. Each arm did the
same fixed work: 1 GiB of 1 MiB Q1 writes; 256 fresh 4 KiB files, each explicitly
flushed; or 256 ordinary/write-through 4 KiB pairs. This is a short equal-work
screen, not a five-minute sustained or peak-throughput test.

Application time ends after the writes; total durable time additionally includes
writer close, filesystem volume flush and the explicit QueueCache checkpoint.
All bytes were checked with unbuffered reads after releasing the cache. Seconds
below are the recorded values from this single completed run.

| Workload | Rep | Off application s | On application s | Off total durable s | On total durable s |
|---|---:|---:|---:|---:|---:|
| Sequential 1 GiB | 1 | 4.2497225 | 4.0096971 | 5.4428456 | 5.2083103 |
| Sequential 1 GiB | 2 | 4.1741032 | 4.1661592 | 5.3397584 | 5.4322743 |
| Sequential 1 GiB | 3 | 4.12529 | 4.1262387 | 5.2651809 | 5.3644406 |
| 256 file flushes | 1 | 0.1353385 | 0.1214631 | 0.146629 | 0.5957926 |
| 256 file flushes | 2 | 0.1145052 | 0.1233104 | 0.1252898 | 0.5701554 |
| 256 file flushes | 3 | 0.115868 | 0.1207615 | 0.1281177 | 0.629289 |
| 256 write-through pairs | 1 | 0.02224 | 0.0141004 | 0.0302839 | 0.5470719 |
| 256 write-through pairs | 2 | 0.0029266 | 0.0144278 | 0.0170345 | 0.5016934 |
| 256 write-through pairs | 3 | 0.0074119 | 0.0077919 | 0.0238465 | 0.6328589 |

The lower-I/O mechanism matches the durable cost. File-flush cases had one lower
flush and no group flushes when off, versus 259 lower flushes and 258 group
flushes when on; lower writes rose from 274–298 to 538–539. Write-through pairs
had one lower flush when off versus 261–263 when on (260 group flushes); lower
writes rose from 16–62 to 265–267. Counts cover the enclosing diagnostic interval,
including metadata and the durable tail, not physical Ceph requests or exactly
the application timing window. Every case ended clean in the requested mode,
with no error-count increase. These small workloads do not establish a universal
application slowdown, sustained admission behavior or filesystem crash safety.

UTC-aligned backend evidence bracketed the full 21:19:21–21:20:40 campaign and
every case within the expected cadence. No backup/restore task, scrub,
recovery/backfill or new health warning overlapped it; serving-pool availability
stayed above 251.51 GB. Other guests continued I/O; three guest counters were
missing/reset and remain unknown. The existing availability warnings concerned
separate intentionally offline pools. Q:'s original active 2 GiB Fast/Idle
1024 KiB × 4 profile and saved configuration were restored; W: is unconfigured,
ordering is off, and Everything/service, Steam and the tray were restored. The VM
remained on the tested driver. Private evidence contains 476 independently
hash-verified files; archive SHA-256
`DF8FE9E72B67601AD05A67DB71DE4678886EE74F3D8193EC5DD4DD2AB75943F5`.

<a id="fast-ordering-13-write-through"></a>**Experimental write-through boundary reduction (#13, October 25).**
Retain mode 2 as a separate, default-off experiment. It removes the extra boundary
before a write-through request, preserving its post-boundary, earlier explicit
flushes, overlapping versions and the successful lower flush before later groups.
The [Windows per-request contract](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdm/ns-wdm-_io_stack_location)
does not require unrelated earlier ordinary writes to become durable first.
This local dependency argument does not prove the whole NTFS safety goal.

Loaded native 0.4.851.1, source `ae71238ba6a674efb24a5be9dbd5ac84e747836d`,
ran both modes. Loaded hashes matched the signed package: filter
`89964D25937701001AE16CC86102AD9246BA3ED78E7FE4E84088E5CBCE202CF4`, RAM driver
`82501FF4434782636F1E267D1488123430B749453A30D639ED220C5268217FF1`.
Plan-189 controller 0.4.852.1, source `58e9ba68f29a55dc33f60d8656645d2049ba7e2a`,
was staged from its published package; native source was unchanged. Host tests and
Windows Debug/Release, CLI, signing and publication checks passed for both commits.

The maintained `flush-order-cost` experiment completed all 18 cases in 76.2 s on
disposable non-OS NTFS W:, with Q:'s backing cache paused. Both arms used 256 MiB
Fast/Eager, retained writes/read promotion, 1024 KiB × 4 drains and normal priority.
Verifier was off, uptime exceeded 15 minutes, Everything/service, Steam and tray
were closed, last-access updates were disabled, and the owner's dedicated
Defender/Search-disabled baseline was checked. Repeat 1 and 3 ran mode 1 then 2;
repeat 2 reversed each pair. No competitor or mode-0 arm was included.

Equal work: 1 GiB unbuffered sequential writes at 1 MiB Q1; 256 fresh 4 KiB files
with `Flush(true)` per file; or 256 ordinary 4 KiB writes paired with a separate
4 KiB write-through write (2 MiB total). Application time ends with the workload.
Total durable time includes closing the writers, flushing the filesystem volume
and explicitly flushing QueueCache. Every case passed independent unbuffered
byte checks after cache release. Counts below cover the enclosing diagnostic
interval, including metadata and the durable tail; they are not physical Ceph
requests or exact scored-window attribution. All rows come from this one completed
matrix; seconds are unrounded recorded values.

| Workload | Rep | Mode | Application s | Total durable s | Lower writes | Lower flushes | Group flushes | Version copies | Capacity waits |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Sequential 1 GiB | 1 | 1 | 3.8264138 | 5.0474101 | 1034 | 5 | 4 | 0 | 519 |
| Sequential 1 GiB | 1 | 2 | 3.9508842 | 5.2184539 | 1031 | 4 | 3 | 0 | 465 |
| 256 file flushes | 1 | 1 | 0.117449 | 0.6744812 | 537 | 259 | 258 | 368 | 0 |
| 256 file flushes | 1 | 2 | 0.1142909 | 0.6569167 | 537 | 259 | 258 | 372 | 0 |
| 256 write-through pairs | 1 | 1 | 0.0190913 | 0.9381924 | 522 | 520 | 517 | 0 | 0 |
| 256 write-through pairs | 1 | 2 | 0.0092876 | 0.5426187 | 264 | 261 | 260 | 1 | 0 |
| Sequential 1 GiB | 2 | 2 | 3.9788728 | 5.2257585 | 1029 | 5 | 4 | 1 | 491 |
| Sequential 1 GiB | 2 | 1 | 4.0568571 | 5.2497957 | 1034 | 5 | 4 | 1 | 457 |
| 256 file flushes | 2 | 2 | 0.1054973 | 0.5568152 | 537 | 259 | 258 | 374 | 0 |
| 256 file flushes | 2 | 1 | 0.094582 | 0.5204281 | 538 | 259 | 258 | 372 | 0 |
| 256 write-through pairs | 2 | 2 | 0.0128253 | 0.4855021 | 264 | 261 | 260 | 1 | 0 |
| 256 write-through pairs | 2 | 1 | 0.0186966 | 1.0642524 | 520 | 517 | 516 | 1 | 0 |
| Sequential 1 GiB | 3 | 1 | 3.9019565 | 5.0156929 | 1028 | 4 | 3 | 0 | 498 |
| Sequential 1 GiB | 3 | 2 | 3.9567751 | 5.1114068 | 1035 | 5 | 4 | 0 | 453 |
| 256 file flushes | 3 | 1 | 0.1026273 | 0.6830741 | 538 | 259 | 258 | 375 | 0 |
| 256 file flushes | 3 | 2 | 0.1080835 | 0.6206641 | 537 | 259 | 258 | 370 | 0 |
| 256 write-through pairs | 3 | 1 | 0.0115733 | 1.1649326 | 520 | 517 | 516 | 0 | 0 |
| 256 write-through pairs | 3 | 2 | 0.0175339 | 0.5814027 | 264 | 261 | 260 | 1 | 0 |

Write-through-pair durable completion improved **42.16%, 54.38%, 50.09%**:
all three pairs exceed 5%, and ranges separate (mode 1 0.9382–1.1649 s,
mode 2 0.4855–0.5814 s). Lower flushes fell from 517–520 to 261; writes from
520–522 to 264. The mechanism changed as intended, with no cache-error increases.
Application times are only milliseconds and changed direction; no application
speedup is accepted from those timings.

The explicit-file-flush shape observed **zero write-through requests in every
case**, 258 seals/group flushes and 259 lower flushes in both modes. Mode 2's changed
branch was not entered. Durable pair differences were −2.60%, +6.99%, −9.14%
(new time relative to old); directions disagree and ranges overlap. This does
**not** remove the earlier 4.9–5.0× mode-0/mode-1 durable-tail cost. Sequential
durable differences were +3.39%, −0.46%, +1.91%, with overlapping ranges and no
repeated ≥3% loss. Neither control establishes a speedup or broad equivalence.

Read-only capability queries found write-through supported on Q: but not its W:
VHDX device; both reported flush support. Therefore no FUA variant was built or
qualified on this path. Retain ordinary lower writes plus explicit flushes.
A supported per-request durable flag on only the last write cannot make earlier
ordinary writes durable, and combining dependent writes is not an atomicity
contract. Other disk types remain unqualified.

UTC-bracketed Ceph client/OSD/pool, VM block and other-guest observations covered
all 18 durable intervals at the recorded 5/15 s cadences. No backup task, scrub,
recovery/backfill, new health warning or unexpected unavailable serving pool was
observed; serving-pool available space stayed above 259.164 GB. Shared guest I/O
continued. Three other guests lacked/reset counters and were not converted to
zero; global client I/O and coarse integer OSD latencies cannot establish exclusive
load or exact per-request backend latency. The intentionally offline unrelated
pools remain outside the serving path. Backend monitoring is evidence of observed
conditions, not a guarantee that shared load had no timing effect.

Correctness: the separate plan-188 campaign passed **22 checks under Driver
Verifier**, eleven per mode: RAM-first admission, held predecessors, failed flushes
and explicit recovery, both write-through successor boundaries, TRIM, capacity,
live selection refusal and lifecycle, with exact lower-device bytes. Campaign
`QueueCache-Campaign-20261025-130038-c192d335a8b44656acc452beb129779f`, child
`20261025-130046-2db054102d97428a8d48c9b48a0495c9`, restored. No new crash tests
were run in that campaign; the earlier three follow-up cuts apply to mode 1.
The subsequent plan-191/192 follow-up completed 26 checks and three bounded
mode-2 cuts, including pending write-through data and an explicit durable
checkpoint, with clean NTFS scans and exact independent guards
([full correctness record](KNOWN_ISSUES.md#correctness-qualification)).
The 26-check follow-up ran with Verifier off; it does not replace the earlier
Verifier evidence or qualify physical-storage power loss and system volumes.

Cost campaign
`QueueCache-Campaign-20261025-133739-72983cdb58ac422f80bc4d244b3ccfa4`, child
`20261025-133743-b24d8f0e85c24b67a43ce23f92b0c8d7`, completed and RESTORED,
with full telemetry coverage and validated durable terminal event. Case IDs are
`flush-order-cost-v189-r<repeat>-<workload>-mode<mode>`.
The separate plan-188 attempt stopped at its eleventh case because new accepted
writes prevented a clean checkpoint within three flushes. It restored and is
retained separately; no incomplete repetitions were reused. Plan 189 closes writer
handles before the filesystem/checkpoint tail while including closure in durable
time. The checkpoint's health/new-write guards were not weakened.

Plan 189's release campaign
`QueueCache-Campaign-20261025-134513-8fabb9dbe61e44cbac42f6b0f6092ffe` did not
complete: it stopped at the mode-zero high-watermark pressure check after 3/11
phases, RESTORED. Its diagnosis is tracked in
[#30](https://github.com/devedse/QueueCache/issues/30) and
[known issues](KNOWN_ISSUES.md#high-watermark-pressure-check-stopped-the-plan-189-release-campaign).

<a id="release-192"></a>**Final release qualification (plan 192): completed.**
Controller 0.4.856.1 (`52ff318`) beside the same loaded native 0.4.851.1 pair ran
`release-performance` in
`QueueCache-Campaign-20261025-163917-513dbdddc39a49798ef125787a8fb197`
(16:39–18:25 UTC). Verifier and experimental ordering were off and Q:'s backing
cache was paused. It used the same CrystalDiskMark-bundled DiskSpd and 2 GiB budget
as plans 132 and 171. **COMPLETED 11/11 phases, 106/106 cases, RESTORED**;
`verify-completion` accepted it. All 94 retained correctness sub-checks passed,
including the high-watermark checkpoint
([details](KNOWN_ISSUES.md#high-watermark-pressure-check-stopped-the-plan-189-release-campaign)).
RAM reference medians, three windows each: Direct 1M Q1T1 21,472 MiB/s, Q8T1
34,048, Q2T4 30,621; Direct 4K Q1T1 1,509.

No speed verdict is drawn. Under [the comparison rule](TESTING.md#6-deciding-whether-a-change-is-faster),
another day's run does not judge a change, and this campaign had no same-window
control. The two previous completed release runs point in opposite directions:

| Same 72-case matrix, medians of three | Plan 132 (Oct 14, 0.4.687.1) | Plan 171 (Oct 22, 0.4.776.1, host-bound) |
|---|---|---|
| Cached (Eager/Idle) write rows, plan 192 against | 14–35% lower; every range separated | 56–163% higher |
| Same campaign's RAM-disk read reference, plan 192 against | 7–24% lower | 36–234% higher |

Host memory state moves both rows. Against plan 132, though, cached writes fell
further than the RAM reference, so a small write-path change since then cannot be
excluded without a same-build or same-window comparison. Uncached (Off) rows follow
the Ceph storage.

UTC-aligned Ceph/guest observations covered the campaign at their cadences. No
backup or restore task overlapped it, no new health warning appeared, and
serving-pool free space stayed above 243.57 GB. Five global scrub samples (pool
unknown) fell in three short episodes, at 17:54:14–19, 18:02:35–40 and
18:14:30 UTC. Brackets to the adjacent clean samples overlap four write-case
process windows: random Q1 Off r1 with timing (0002), random Q1 Eager r1 (0003,
edge only), sequential Q8 Off r1 (0019) and sequential Q8 Off r2 with timing
(0047). Treat those rows as possibly disturbed. Three other guests' counters were
unavailable or reset.

Keep #13 open and both experiments off by default: crash/bypass/special-file scope
remains open.

A follow-up review retained the explicit-flush fallback. Adjacent writes inside
one group are already gathered. Advancing a dependent group still requires the
previous group's successful lower flush. Reusing the final barrier would require
tracking every intervening lower-device effect, including direct writes, TRIM,
media controls and concurrent lifecycle work; dirty/group counters alone do not
prove that coverage. It could remove at most one final command from the measured
258-group-flush file workload, without addressing its dominant cost. No such
driver change or new performance gain is claimed, and the established 18-case
comparison was not rerun for these runner-only additions.

<a id="fast-ordering-13-cost"></a>**Experimental Fast ordering (#13, October 25).**
Keep the runtime experiment off by default. Ordered groups preserved RAM-first
admission in the focused checks, but finishing a flush-heavy workload durably
cost substantially more. This is a short equal-work screen, with two repetitions
and reversed arm order, rather than a release-performance qualification.

Plan 187, source `38464a75664405f3fbc0f5af27155150313f7a10`, installed and loaded
0.4.849.1, used the maintained `flush-order-cost` experiment on disposable NTFS W:
inside the lab VHDX. Q:'s backing cache was paused. Both arms used a 256 MiB
Fast/Eager cache, retained writes/read promotion, 1024 KiB × 4 drains and normal
priority. Verifier was off, Windows had been up for over 15 minutes, and Everything,
Steam and the tray were closed. The owner's dedicated Defender/Search-disabled
baseline and disabled last-access updates were verified. The temporary snapshot
was removed before this comparison.

Equal work was 1 GiB of unbuffered sequential writes at 1 MiB Q1 (larger than the
cache); 256 fresh 4 KiB files, each with `Flush(true)`; and 4 GiB of reads over a
warmed 16 MiB file. Repeat 1 ran off/on for each shape; repeat 2 ran on/off. All
12 unique cases were collected, and lower-device bytes were checked after cache
release. Application time ends after the workload; total durable time also includes
the filesystem volume flush and explicit QueueCache flush, including any bounded
follow-up for newly admitted writes. Every cost case needed one explicit call.

| Workload | Repeat | Off application s | On application s | Off total durable s | On total durable s |
|---|---:|---:|---:|---:|---:|
| Sequential 1 GiB | 1 | 3.9518 | 4.1221 | 5.1924 | 5.3632 |
| Sequential 1 GiB | 2 | 5.3937 | 4.0705 | 7.0863 | 5.3503 |
| 256 file flushes | 1 | 0.0965 | 0.0966 | 0.1148 | 0.5640 |
| 256 file flushes | 2 | 0.0787 | 0.0847 | 0.0927 | 0.4668 |
| Resident reads, timing control unqualified | 1 | 0.4658 | 0.4009 | 0.4714 | 0.4093 |
| Resident reads, timing control unqualified | 2 | 0.3571 | 0.4114 | 0.3625 | 0.4196 |

The file-flush shape repeated a **4.91× / 5.04× total durable-time cost**. Its
application acknowledgments remained around 0.08–0.10 s: Fast still acknowledges
these flushes from RAM. The later cost is real disk work: mode 1 completed 258
group flushes and 259 lower flush attempts per repetition, versus zero group
flushes and one lower flush with mode 0. Lower write attempts rose from 301/289 to
543/542. Preserving earlier versions and their boundaries reduces coalescing and
adds real barriers. This small workload does not establish a universal 5× slowdown
or the cost when a larger backlog starts blocking admission.

Sequential application time was 4.3% worse in the first pair and 24.5% better in
the second. The directions disagree; there is no established sequential gain or
loss from these two pairs. Sequential mode 1 added four group flushes versus zero
with mode 0. No cache error increased in any case.

All four read cases accounted for exactly 4,294,967,296 cache-read bytes and zero
lower-read attempts. However, their enclosing diagnostic interval also contained
6–9 lower metadata writes; mode 1 completed two group flushes. The current record
does not separate that I/O between the timed reads and the durable tail. These
windows therefore **do not qualify as clean resident timing controls**, and the
opposite pair directions cannot support a read-speed acceptance claim. The raw
observations are retained, and no broader performance acceptance is claimed.

The campaign completed in 58.7 s with restoration RESTORED and its durable terminal
event validated:
`QueueCache-Campaign-20261025-054430-eeb5036c208747ac9f8e133a8bb0ff9b`, child
`20261025-054435-c404f803b9574f2e9b536fd6780a29bc`.
Worker telemetry covered 05:44:36.059–05:45:26.579 UTC after its ready handshake,
within the 2 s coverage contract. Loaded hashes matched the signed package:
filter `91250E12106A82F5EE342F3A9367DD69C61FCAAFF9D9FBAA2271B2D4D58807D8`,
RAM provider `00FAD28F4A51D3BED79E92FCEC6FD44747CE440E0250BCA3F3790D6FF1D17BC0`.

UTC-aligned Ceph observations covered the full worker with 5 s traffic and 15 s
pool/task/other-guest observations. No backup, scrub, recovery, overlapping
non-console Proxmox job or new health warning was observed. Backups had finished
by 02:17:49 UTC. The serving pool retained at least 262.1 GB available; reported
OSD commit/apply latency reached 2 ms (coarse integer reports). Ordinary shared
load remained: the available other-guest counters increased by about 56.9 MB in
the bracketing interval. Three intentionally offline guests had unavailable
counters and were not treated as zero. Those deltas and cluster traffic are
bracketing observations, not exact attribution to the scored work. They rule out
an observed overlapping backup, not all environmental variation.

[Known issues](KNOWN_ISSUES.md) records the nine Verifier checks, three cuts,
incomplete preparations, untouched durable guards and remaining safety scope.
Raw evidence, full telemetry and all incomplete attempts were exported and verified
before owned workload cleanup: `QC13Followup-20261025-evidence.zip`, 690 files,
SHA-256 `822A4BFE58B52EA479254F595961B07E73BEC81DCDEDC9C7F4A62B4B3E115B63`.

<a id="checkpoint-reuse-28"></a>**Changed-region checkpoints (#28, October 24–25).**
The save freezes RAM, compares SHA-256 fingerprints per MiB, writes changed runs
into allocated payload of an older owned VHDX without mounting it read-write,
and verifies the entire result through a read-only Windows VHDX view. Sparse
regions must be unchanged before reuse; a change needing allocation, invalid hints
or unsupported layout selects a new complete save. Current/original containers
are protected; explicit generated-checkpoint provenance excludes committed user
exports even when their filenames resemble automatic checkpoints.

Short same-session observations, one fixture per type and size, Direct RAM access:

| Plan / image host | Image | Last warm full save s | Small-change reuse s | Reuse payload / full logical MiB |
|---|---|---:|---:|---:|
| 180, NTFS W: lab VHDX (backing Q: paused) | 128 MiB dynamic / 512-byte sectors | 1.077 | 1.208 | 11 / 128 |
| 180, same | 128 MiB fixed / 4096-byte sectors | 1.139 | 1.050 | 11 / 128 |
| 181, direct NTFS Q:, its cache paused | 1024 MiB dynamic / 512-byte sectors | 5.947 | 7.461 | 9 / 1024 |
| 181, same | 1024 MiB fixed / 4096-byte sectors | 5.820 | 5.866 | 9 / 1024 |
| 182, direct NTFS Q:, its cache paused; 512 MiB existing data | 1024 MiB dynamic / 512-byte sectors | 9.613 | 7.519 | 9 / 1024 |
| 182, same | 1024 MiB fixed / 4096-byte sectors | 9.972 | 7.903 | 9 / 1024 |

Plans 180/181 contained only a 4 MiB payload file and a 1 MiB guard before reuse.
The small change rewrites that payload with one changed byte. Logical writes fell
91.4% / 99.1%, with 8192 header bytes, but full logical read-back remains necessary
and sparse-region preflight adds source reads/hashing. Native full-copy logical
bytes include zero regions that the VHDX provider can leave unallocated; physical
host writes were not measured. A later corrupt-hint full fallback is a separate
correctness check: in the dynamic fixture it follows 80 MiB growth, so its timing
must not be substituted for the earlier warm full-save comparison.

Plan 182 additionally seeded 512 MiB of deterministic nonzero existing data in
each image. Reuse reduced elapsed time by 21.78% (dynamic) and 20.75% (fixed)
against the preceding warm complete save, while still writing only 9 MiB of
logical payload plus 8192 header bytes. Verification read 1,073,776,640 and
1,073,790,976 logical bytes respectively, including the full 1 GiB image. Reloads
matched the populated file's recorded SHA-256 as well as payload and guard bytes.
All nine checks passed, restoration completed and owned workloads were removed;
worker 258.8 s, total run about 261 s.

Keep the optimization for eligible candidates, with a conditional benefit claim.
These are single observations per fixture, not a repeated performance acceptance
matrix or a prediction for large real disks. The two image sizes used different
hosts. All three runs passed their nine correctness checks with restoration and
full-read counters. Mostly empty images showed no consistent time benefit; a live
crash during checkpoint commit remains an unrun qualification case.

Backend 0.4.840.1, filter 0.4.839.1, RAM provider 0.4.840.1; the filter source was
unchanged. Plan 180 used controller 0.4.840.1; plan 181 controller 0.4.841.1 with
unchanged product code; plan 182 used controller 0.4.842.1, also with unchanged
product code. Loaded hashes matched their respective signed packages.
Verifier off, more than 15 minutes since restart, Steam/Everything/tray closed,
Everything service stopped, last-access updates disabled, Defender/Search disabled
and WdFilter absent. Exact runs:
`QueueCache-Campaign-20261024-193633-267d9c1dcbe24a84b402ceb342e73c73`,
`QueueCache-Verify-20261024-194714-d25ab524e7ea40248f0e19e75c264316` and
`QueueCache-Verify-20261025-000011-ed9d80275808461ab30bfab008f8d87b`.
Evidence archive SHA-256:
`E773923E5111AB01974CD067B7F6BFD78C39BF335023594B7466FF89766EB8ED`,
`942EDA082A726BA42DC69C60370F009039948C73CD172FCD67800893180FEBBC` and
`A217DAC0D691D888A2640208FA1B64817B393AA4B549CE1415019F9E6C785D84`.

<a id="reads-19"></a>**Page-ins of file data are kept (#19, plan 173, October 23).** Files read through
Windows' file cache (Explorer copies, most programs) and mapped files reach the volume
as page-ins, which were never kept. On 0.4.809.1 a 1 GiB file read twice through
Windows' file cache on `Q:`, with Windows' standby memory purged before each read (as
after a restart), took 275 MiB/s the first time (13 MiB from RAM) and 2,254 MiB/s the
second (871 MiB from RAM). `paging-coherence`: a mapped read kept 2,048 of 2,048 blocks
and a 32 MiB buffered re-read came 100% from RAM. On a cached `C:`, 216 program files
(82 MiB) matched their disk hashes after 5,000 page-in blocks were kept, 36 of them from
page-ins whose buffer repeated the memory manager's dummy page, and again after a
restart with a saved `C:` profile (3,536 kept, 54 repeated); every started process
exited normally. Evidence: `C:\QCResults\Batch2-20261023` and
`Q:\QCResults\Batch2-20261023` on the test VM.

<a id="strict-16"></a>**Strict (#16, October 23): `compare-cache` in Strict, 2 GiB, one repetition,
same window, same DiskSpd.** Before is 0.4.803.1; "with cap" is 0.4.809.1 (direct
write-through and a dirty cap of two seconds of the measured drain rate); "direct" is
0.4.810.1 (direct write-through only, the merged change).

| Test | Before | With cap | Direct |
|---|---:|---:|---:|
| Random 4K Q32 write, 10 s | 0.0 MB/s | 0.0 MB/s | 153.6 MB/s |
| Random 4K Q1 write, 10 s | 44.1 MB/s | 7.0 MB/s | 0.0 MB/s |
| Sequential 1M Q8 / Q1 write | 5,509.5 / 2,095.2 MB/s | 284.5 / 282.6 MB/s | 5,994.8 / 2,151.1 MB/s |
| Write-through 1M Q8 / 4K Q1 | 103.0 / 2.6 MB/s | 111.8 / 3.6 MB/s | 109.0 / 3.4 MB/s |
| Absorb 1 GiB | 1,511.3 MB/s | 629.7 MB/s | 2,616.0 MB/s |
| Flush after 512 MiB | 1.152 s | 1.139 s | 1.339 s |
| Sustained 8 GiB | 370.4 MB/s | 355.1 MB/s | 406.0 MB/s |
| Mixed 70/30 random 4K Q8 | 55.3 MB/s | 16.0 MB/s | 52.8 MB/s |
| 40,000 files create / copy | 33.95 / 22.85 s | 43.42 / 37.01 s | 22.72 / 35.30 s |

The cap is rejected and removed; direct write-through is kept (write-through +6% /
+31%; other differences are within one repetition's spread). The random-write stall
did not go away: with direct write-through it moved from the Q32 window to the next Q1
window, which shows it comes from a flush (NTFS sends them on its own) draining the
random backlog that the disk writes at a few MB/s, whichever test is running then. The
cap, sized from sequential drain batches, did not bound that and throttled sequential
bursts. Runs: `C:\QCResults\Batch2-20261023\compare\before`, `after` and
`after-direct` on the test VM.

**Backlog limit (0.4.825.1, October 23).** Strict counts separate pieces of unwritten
data (a version that becomes dirty starts one unless the block before it is dirty
too) and holds at most what the drain completed in about two seconds (pieces per
second of drain time, at least 256 pieces, 1,024 before a measurement); a write that
adds dirty data waits beyond that. `compare-cache` in Strict, 2 GiB, one repetition
each; before is 0.4.810.1 (19:54 UTC), after is 0.4.819.1 (same native change)
rerun at 21:07 on a settled VM:

| Test | Before | After |
|---|---:|---:|
| Random 4K Q32 / Q1 write, 10 s | 304.2 / 0.0 MB/s | 7.1 / 7.9 MB/s |
| Sequential 1M Q8 / Q1 write | 9,017 / 4,860 MB/s | 7,172 / 2,080 MB/s |
| Write-through 1M Q8 / 4K Q1 | 116.3 / 4.1 MB/s | 115.9 / 4.5 MB/s |
| Absorb 1 GiB / flush after 512 MiB | 3,228 MB/s / 1.445 s | 2,626 MB/s / 1.363 s |
| Sustained 8 GiB | 449.6 MB/s | 426.9 MB/s |
| Mixed 70/30 random 4K Q8 | 54.7 MB/s | 17.2 MB/s |
| 40,000 files create / copy | 15.40 / 14.26 s | 6.59 / 8.00 s |

Sampled every 2 s, the backlog sat at its limit (1,200–4,800 pieces, 5–19 MiB) during
the random and mixed tests and the drain wrote about 4.7 MB/s; the limit fell to its
256 floor after sequential write-outs, which slows the next scattered writes until it
is measured again. A first run 17 minutes after a restart (not sampled) completed
nothing in the sequential 1M Q8 and Q1 windows and ran the file copy in 44.8 s; an
isolated rerun of that sequential test wrote 7,940 MB/s with 33 pieces held. That run
is unexplained. A short check on the final 0.4.825.1, 4 minutes after a restart (10 s
of random 4K Q32 writes, one program flush, 10 s of sequential 1M Q8 writes): 31.7
MB/s, 0.02 s and 9,568 MB/s, against 52.5 MB/s, 0.02 s and 8,635 MB/s on 0.4.821.1
without the limit (both backlogs had drained within the random test). Runs:
`C:\QCResults\Batch4-20261023\compare\before`, `after` and `after2` (with
`strict-samples.txt`), and `strict\before`/`after` on the test VM.

`release-performance` on 0.4.810.1 (Verifier off) completed 11/11 phases and
106/106 cases with every correctness phase passing and all 2,310 owned process exits
zero. Against 0.4.776.1's run of October 22 the 32 case groups' medians differ by
−27% to +92% (median +0.7%; 17 up, 15 down), with no direction by policy or queue
depth; RAM-path rates stay at that day's host-bound level (see the decisions table).

**The new default (plans 164–167, October 21–22): Eager 2 MiB × 8 with read-aware
limit 2 and asynchronous TRIM is the default for new caches, and it beats a
same-window PrimoCache on both #15 workloads.** With matched backends (paused
anchors 95.9 against 96.3 MB/s, drain latency 53.1 against 53.7 ms), the default
measured 292.4 / 90.7 MB/s (new 1 GiB file / 4,096 × 64 KiB files, each including
the drain) against PrimoCache's 261.0 / 52.5. A concurrent disk reader got 757 IOPS
(p99 11.8 ms) against 170 (50.7 ms). Under Driver Verifier the final native build
passed `trim-cache` 12/12, the focused campaign 98/98 and an integrity run with
12,291 hashes. Qualification found and fixed one driver bug (asynchronous TRIMs
switched the caller path off) and four test-environment faults. `release-performance` on the final build then completed all 11 phases and 106 cases; its RAM-path rates were host-bound that day, which a same-window 0.4.687.1 control confirmed.
[Results](#final-164).

**Read-aware drain limit and asynchronous TRIM (plans 162/163, October 21):
Eager 2 MiB × 8 keeps its #15 speed and reads recover to the default's level.**
With read-aware limit 2, drainers start a batch only while fewer than two are
outstanding, and only while a foreground disk read is in flight or ended within
10 ms. A concurrent 4 KiB Q1 disk reader then got 1,031–1,141 IOPS (p99
10–11 ms), against 340 IOPS (29 ms) without the limit and 1,152 (10 ms) under the
1024 KiB × 4 default. During the reads the drain ran at 245–247 MB/s, the default's rate.
The #15 workloads never engaged the limit and measured the same as
Eager 2 MiB × 8 alone: 300.3 / 122.2 against 299.8 / 113.6 MB/s. Asynchronous
TRIM cut a mass-delete small-file case from 2.5 to 2.0 s. All checks passed under
Driver Verifier; one new TRIM check was redesigned after NTFS showed it could not
reach its target. [Results](#prio-162) and [the updated plan](#finish-15).

**Read during drain (plan 161, October 21): PrimoCache does not protect reads;
Eager 2 MiB × 8 hurts them half as much.** At the same drain and writer rate
(about 360 / 295 MB/s), a concurrent 4 KiB Q1 disk reader got 163 IOPS (p99 64 ms)
under Primo, against 277–331 IOPS (p99 29–30 ms) under QueueCache Eager 2 MiB × 8.
The 1024 KiB × 4 Idle default kept 959–1,153 IOPS (p99 11–12 ms), but drained
only about 250 MB/s. A read-aware limit is therefore not needed for Primo
parity. [Results](#read-161) and [the plan to finish #15](#finish-15).

**Same-window result (plans 159/160, October 20–21): Eager 2 MiB × 8 meets the
#15 medians; defaults unchanged.** TRIM now waits only for overlapping in-flight
drain writes (native 0.4.766.1). Under Driver Verifier, 103/103 subchecks and an
Eager 2 MiB × 8 file-integrity run passed. In an A–B–A bracket with
matched backend latency, PrimoCache measured 285.5 / 70.4 / 315.8 MB/s (1 GiB,
4,096 files, 8 GiB). Eager 2 MiB × 8 measured 304.9 and 298.3 / 142.6 and
132.2 / 294.1 and 288.9 in the two QueueCache blocks; the default 1024 KiB × 4
Idle measured 184.1 and 170.0 / 86.6 and 80.0. Eight drainers still cut a
concurrent disk reader's IOPS about in half (545 against 1,094; plan 151
reproduced). A read-aware drain limit and release qualification come before any
default change. [Results and decision](#trim-160).

**Matched research (plan 158, October 18–20): drain during writes is the #15
candidate.** 47 complete runs and 454,794 bypassed hashes passed. The Ceph-backed
disk changed speed up to threefold within hours, so products were compared by
matched drain latency at the same bytes in flight. Against Primo's quiet window
(120.5 / 59.9 / 126.9 MB/s for the 1 GiB, 4,096-file and 8 GiB workloads), the
default 1024 KiB × 4 Idle measured 96.5 / 59.0 / 92.6, and 2 MiB × 8 Eager
141.3 / 173.3 / 125.5. Drain speed follows bytes in flight. Small files are
limited by writing, then draining, which Eager overlaps; request size is not the
limit. QueueCache absorbs the small files faster than Primo (1.15 versus
1.90 s). Eager has one new blocker: each TRIM waits for every in-flight drain
write, stalling writes for up to 25 s after deletes. Foreground-read
interference is still unmeasured. Defaults unchanged.
[Results, corrections and decision](#matched-158).

**#15 goal.** The read-miss and larger-default changes were merged in
PRs #17/#18. [Issue #15](https://github.com/devedse/QueueCache/issues/15) was
the drain follow-up: match or exceed a contemporaneous PrimoCache control on
both new-file workloads, while preserving correctness, RAM-first admission and
explicit durability. Since plan 164 the default meets it in a same-window
comparison ([results](#final-164)).

**Short completed writes (2026-10-16, plan 148).** Three complete repetitions
per provider, with one identical temporary snapshot present, measured one new
1 GiB file and 4,096 new 64 KiB files (256 MiB). Through filesystem flush, explicit
product drain and zero pending/disk queue, large/small medians were
**48.41 / 1.25 MB/s Direct**, **52.71 / 41.71 QueueCache Fast**,
and **78.96 / 57.62 PrimoCache**. All 18 cases and 36,873 cache-bypassed file
hashes passed, with the same unique payload. QueueCache's small-file rate was
33.2× Direct; its mean observed backing write was
341.9 KiB against
5.0 KiB Direct and
1746.2 KiB PrimoCache, including metadata.
The request sizes are consistent with batching, without establishing the source
of every speed difference. The native driver stayed 0.4.687.1; this is new
measurement evidence, not another driver fix.
QueueCache’s small-file median remains 27.6% below PrimoCache, with a wide
18.36–41.94 MB/s range; its large-file range overlaps Direct’s. The slow
repetition’s cause is untraced. A follow-up should separate backend/OS-barrier
variation from batching limits before choosing another driver change.
One post-score, pre-pause snapshot also had 56 KiB newly dirty, drained by untimed
Disable. Its source is untraced; the byte oracle validates the later paused state,
not the exact scored endpoint.

Defender/Search were disabled; processes had normal priority; Verifier/timing
were off. Direct/Primo had QueueCache absent. Pre/post capacity and near-full
guards passed, while existing Ceph OSD/PG/scrub warnings remain an uncertainty.
The snapshot was removed after verified restoration. These unbuffered new-file,
cache-fitting workloads do not replace the earlier 8 GiB sustained result,
isolate a Defender speedup or qualify Strict/general interference. A slow 1 GiB
small-file pilot and a pre-snapshot QueueCache diagnostic were excluded; every
published provider used the final plan 148 contract. Exact runs, ranges and
per-case scores: [short write plus drain comparison](COMPARISON.md#short-write-plus-drain-comparison-2026-10-16).

**Disabled-baseline cache refresh (2026-10-15/16, plan 146).** The same pinned tools
completed 33 main and 7 empty-cache steps per mode: 120 steps and 57 raw XML scores
reviewed, all tree hashes verified. Fast wrote 8 GiB at 267.5 MB/s at the application,
but 200.1 MB/s through the last busy backing-disk sample, against 257.4 paused (-22.2%).
Its sampled catch-up tail was 10.81 s. **Disk-speed parity is not established; the
residual gap is measured here.** File-tree read/hash took 2.84 s Fast and 2.90 s paused,
against the earlier Fast 154.23 s scanning episode. PrimoCache application /
catch-up-inclusive 8 GiB rates were 425.1 / 279.6 MB/s. Each generic suite has one
repetition. One-second virtual-disk sampling is not physical-media durability proof;
sub-second tails are below its resolution. PrimoCache's retained CDM sequential
read/write changed +5.1%/+8.2%, queued-random read/write −6.3%/+10.0%; the old Defender
state was not recorded, so these cross-date deltas do not isolate disabling it. The
paused control keeps QueueCache loaded and its 2 GiB reservation. Native code is
unchanged. Keep 1024 KiB × 4 on the earlier paired explicit-drain evidence; this refresh
does not qualify foreground interference or Strict latency. Full scores, six exact run
identities and real captures: [current
comparison](COMPARISON.md#current-cache-refresh-2026-10-1516) and its existing product
data.

**Storage qualification.** The storage guard passed before PrimoCache; its post-run
check reported Ceph near-full warnings with 131.3 GB free. The onset and performance
effect were not measured. Treat its storage-dependent scores as observations under that
additional uncertainty. The warnings cleared after restoration and snapshot cleanup.

**Competitor comparison and fix qualification (2026-10-15).** QueueCache leads cached reads and
single-program RAM-disk reads, but the comparison with the bare disk and
PrimoCache found three cache weaknesses: read misses do not overlap (#14), the drain
writes small pieces (#15), and Strict stalls all writes behind whole-cache
drains (#16).
The fix branch has completed its correctness and concurrency checks. Same-build
miss offload improves cold random/sequential throughput 9.95×/3.13× in the latest
controlled repeat, with better p99. The earlier equal-work comparison also
improved cold-random latency tails. Larger drain batches and four
drainers improve the cold-read target and repeated write-drain times. Fixed-window
foreground write IOPS are not qualified as invariant. The final release campaign
completed all 11 phases on October 14; the October 15 environment repeat stopped
incomplete at 8/11 phases on a RAM-only guard. Its unchanged nine-case map-cost
repeat completed separately, without reproducing that failure. The focused October 15/16 cache-paused, Fast and PrimoCache refresh completed both generic suites for each mode; it does not complete the interrupted release campaign.
Real CrystalDiskMark captures are recorded
separately in the README.
Details: [competitor comparison](#competitor-comparison-three-cache-weaknesses-plan-121-2026-10-13).

**RAM-path findings (2026-10-11): two speedups accepted; RAM-disk writes reach memory bandwidth.**
Asynchronous split reads (PR #9) raise single-thread queued 1 MiB RAM-disk reads
(the CrystalDiskMark SEQ1M Q8T1 shape) from a median **24,234 to 43,583 MiB/s
(+79.8%)**; default for new RAM disks from plan 119. Lighter read-hit bookkeeping
(plan 120) raises resident 1 MiB cached reads **+7.9% at Q8T1** (34,951 to 37,718
MiB/s), +4.5% at Q1T1 and +3.5% with four readers; default from plan 120. Large
RAM-disk writes are at this VM's memory write bandwidth: four writers are no
faster than one, and streaming stores were measured and rejected. Details:
[asynchronous split reads](#ram-disk-asynchronous-split-reads-accepted-pr-9),
[cached reads](#cached-reads-lighter-read-hit-bookkeeping-accepted-plan-120),
[RAM-disk writes](#ram-disk-writes-no-speedup-available-on-this-machine-plan-120).
Everything else retained earlier stays: the ordered chunk allocator, bounded
RAM-hit copy optimizations, read recall and caller backoff 256. The shared
whole-read queue stays removed. Partial-read counters are implemented and
missing-span reads stay conditional (under 1% overlap measured). Low-priority
cache slowdowns are a benchmark-setup effect (launch at normal priority); the
selective NTFS/ReFS caller policy was closed without an experiment (best measured
gain +5.26%, at the acceptance bar). Dedicated 100 ms map-refresh cost testing was
dropped by the owner.

The maintained single-command campaign and durable completion event are
implemented (plan 119, including the one-phase `experiment` profile).

## Drain speed and TRIM (#15)

<a id="drain-15-status"></a>

### Drain speed against PrimoCache (#15): met on the test VM, with limits

Since plan 164 new caches drain Eager with 2 MiB batches and eight drainers, with
read-aware limit 2 and asynchronous TRIM. In a same-window A–B–A at matched
backend speed (paused anchors 95.9 / 96.3 MB/s, drain latency 53.1 / 53.7 ms), the
default measured 292.4 / 90.7 MB/s for a new 1 GiB file / 4,096 new 64 KiB files
including the drain. PrimoCache 4.4.1 measured 261.0 / 52.5, and a concurrent disk
reader got 757 against 170 IOPS. The third block ran on a degraded backend (the
small-file anchor fell 25%); its raw small-file median of 49.6 was below Primo's,
while per anchor it stayed ahead. Release qualification on the final build passed (11/11 phases, 106 cases). Limits:
- One Ceph-backed VM; hard disks and SATA SSDs were not measured.
- The 8 GiB sustained workload trailed Primo by 7–9% in plans 159/160 and was not
  repeated.
- Saved profiles keep their old settings: an upgraded machine with a saved
  1 MiB × 4 Idle profile keeps it until it is changed.
- Strict caches also inherit the new default, and their boundaries were not
  measured under it (#16).
[Results](PERFORMANCE.md#final-164).

Earlier steps, each with its own results and limits: the October 15/16 8 GiB
refresh ([comparison](COMPARISON.md#current-cache-refresh-2026-10-1516)), plan 148's
short new-file comparison ([method and ranges](COMPARISON.md#short-write-plus-drain-comparison-2026-10-16)),
the October 16/17 decision screen ([details](COMPARISON.md#drain-decision-screen-2026-10-1617)),
plan 156's rejected aligned gather ([result](PERFORMANCE.md#small-file-gather-156)),
plan 158's matched research ([results](PERFORMANCE.md#matched-158)), the
plan 159/160 same-window comparison ([results](PERFORMANCE.md#trim-160)) and plan
161's reads during a drain ([plan](PERFORMANCE.md#finish-15)). Plan 161 found that
PrimoCache does not protect reads while it drains (a 4 KiB Q1 disk reader got 163
IOPS, p99 64 ms) and that eight drainers alone halved that reader's IOPS; the
read-aware limit of plans 162/163 kept it at the old default's level.

<a id="final-164"></a>

### The new default and its final qualification (plans 164–171, October 21–22)

**The new default and its final qualification (plans 164–167, October 21–22).**
Commit `ce21c5a` makes new caches drain Eager with 2048 KiB batches and eight
drainers. The driver also enables read-aware limit 2 and TRIM mode 3 (index lookup
and asynchronous lower TRIM) at cache creation. Saved profiles keep their settings:
after the upgrade the VM's startup service reapplied Q:'s saved 1024 KiB × 4 Idle
profile about 150 s after boot. The final native build is 0.4.776.1 (`ec10204`;
loaded filter and RAM-disk hashes `9F572C17…E29F` / `B0382266…BCE6`). It includes
the caller-path fix below. Correctness runs 21 and 23 used its own controller;
run 29, the comparison and the release campaign used controller 0.4.780.1
(`38a667e`, plan 167). That controller changes only verification code, and its rebuilt
native binaries were not loaded.

*Correctness, under Driver Verifier 0x209bb on both drivers, defaults as shipped*
(diagnostics confirmed read-aware 2 and TRIM mode 3 on Q: and the lab volume):
- `trim-cache` 12/12.
- `compare-drain` on the default profile: 6/6 cases and 12,291 bypassed hashes.
- The focused `stress-races` campaign: 6/6 phases and 98/98 subchecks. Its busy
  window completed 11 RAM-disk cycles, retrying 8 creations that the RAM headroom
  guard refused.
- The plan-165/166 `parallel-copies` diagnostic: 11 one-minute cases returned only
  whole versions. The one torn read is explained below; three later cases refused
  a request-splitting file layout instead of running.

*What qualification found, in order:*
1. **Driver: asynchronous TRIMs switched the caller path off** (fixed in `87de299`,
   native 0.4.776.1). They were counted in `PagingQueued`, and dispatch treats any
   queued offload as several outstanding requests. While one slow lower TRIM was
   outstanding, every caller-thread candidate was declined and the next 256 backed
   off. In release attempt 11, `foreground-background` served only 10,792 of
   950,056 requests on the caller's thread; the lab volume's counters showed 962,833
   backoff requests after only 6 asynchronous TRIMs. On the lab VHDX each lower TRIM
   goes through Q: to the Ceph-backed disk. TRIM entries no longer count; the worker
   path already excluded them from range waits.
2. **Runner: a lab-image write-back race** (fixed in `ec10204`). Release attempt 10
   stopped at its first performance phase. The lab volume's image lives on Q:, and its
   cleanup was still writing back after Q:'s own preparation had drained, so the
   strict clean capture failed. A performance phase now first prepares the lab
   targets stored on its volume.
3. **Tests: torn reads and caller-path shortfalls at file-extent splits** (fixed in
   `3487f50` and `6ed3a16`). `policies` and `pressure` left their folders on the lab
   volume root, outside the runner's work directory, until the 8 GiB volume had
   1.7 GiB free. The plan-165 diagnostic then reproduced the torn read in six
   minutes, at 1024 KiB batches: chunks 0–199 from pass 434 and 200–255 from pass
   435. That file's extent boundary was at cluster 0x1c8, i.e. 1 MiB + 800 KiB, inside
   the block at exactly chunk 200. NTFS sent that 1 MiB read and write each as two
   volume requests, and nothing makes such a pair atomic, with or without a cache.
   The two `foreground-background` shortfalls used six-extent hot-set files. Both
   suites now write under the run's work directory. `parallel-copies` and
   `foreground-background` recreate a file whose extent boundary would split one of
   their requests, and fail if it stays split. The lab volume was cleaned and its
   free space consolidated.
4. **Tests: RAM-headroom refusals in `stress-races`** (fixed in `38a667e`). Its owned
   2 GiB RAM-disk churn was refused in 3 of 4 runs on the new builds. Sampling every
   2 s showed 8.2–8.5 GiB available at idle on the 16 GiB VM and about 6.2 GiB while
   a churn disk existed. A creation needs 6 GiB, the disk itself plus 4 GiB of
   headroom, which left about 200 MiB of margin. The broker refused correctly; the
   test now retries a refused creation and still fails with fewer than two cycles.
   Why refusals became common on these builds is not established. Its busy-volume
   caches now inherit Eager instead of Idle.

*Same-window A–B–A, Verifier off:*
- **Block A** (21:02–21:13 UTC): QueueCache on the default profile.
- **Block B** (21:35–21:47): PrimoCache 4.4.1 with QueueCache uninstalled; shared
  2 GiB L1, 4 KiB blocks, Native defer 10 s.
- **Block C** (22:16–22:30): QueueCache again, after rollback to the same snapshot
  and redeployment.

The QueueCache arm ran `policy apply Q: --budget-mib 2048 --preset Fast
--accept-volatile-flush --runtime-only` with no drain options and no lab switches.
Diagnostics confirmed Eager 2048 KiB × 8, read-aware 2 and TRIM mode 3. All 12 runs
completed, with 110,619 bypassed file hashes.

| MB/s, median of 6 (range) | Paused anchor, 1 GiB / small | New 1 GiB + drain | ÷ anchor | 4,096 × 64 KiB + drain | ÷ anchor | Drain latency ms |
|---|---:|---:|---:|---:|---:|---:|
| A: QueueCache default | 95.9 / 6.5 | 292.4 (212.6–322.4) | 3.05 | 90.7 (80.7–99.1) | 13.96 | 53.1 |
| B: PrimoCache 4.4.1 | 96.3 / 6.7 | 261.0 (242.6–290.3) | 2.71 | 52.5 (49.7–59.3) | 7.84 | 53.7 |
| C: QueueCache default | 93.9 / 4.9 | 295.0 (128.2–323.0) | 3.14 | 49.6 (34.8–111.2) | 10.05 | 51.9 |

| Read during drain (4 KiB Q1) | Reader alone, IOPS | Reader while draining, IOPS (range) | p99 ms | Disk writes in the window, MB/s |
|---|---:|---:|---:|---:|
| A: QueueCache default | 1,524 | 757 (732–800) | 11.8 | 234 |
| B: PrimoCache 4.4.1 | 1,944 | 170 (131–243) | 50.7 | 421 |
| C: QueueCache default | 456 | 398 (274–401) | 14.7 | 210 |

**A against B, at matched anchors and drain latency: the default beats PrimoCache
on both #15 workloads.** It is 12% faster for the 1 GiB file and 73% faster for small
files. A concurrent disk reader got 4.5 times Primo's IOPS at a quarter of its p99,
while Primo drained faster during the reads (421 against 234 MB/s). Block C ran on a
degraded backend. The reader alone fell to 456 IOPS and the small-file anchor to
4.9 MB/s, and the default's small-file application phase doubled to 5.3 s. Raw, its
small-file median (49.6) is below block B's Primo (52.5); per anchor it stays ahead
(10.05 against 7.84). Other Ceph clients read 4.8 MB/s on average then, against
0.9–1.5 during A and B; the cause is not established.
The same-deployment repeat of block C on the morning of 2026-10-22 ran at matched
anchors (91.4 / 6.3 MB/s, drain latency 52.4 ms). The default measured 301.1
(282.9–313.3) / 74.9 (47.1–105.9) MB/s, 3.30 and 11.86 times its anchors, and the
4 KiB reader kept 554 of 1,226 IOPS (p99 12.0 ms) while it drained. That confirms block
A. Both medians are above block B's PrimoCache, but the small-file range overlaps it
and the repeat was not in Primo's window. Runs 54 and 58–61: 49,164 bypass-verified
file hashes, nothing excluded.

*Release qualification:* **completed.** Attempt 18 (`QueueCache-Campaign-20261022-142222-19bc4dd86a9b4f03b74d9c9b0f099a92`,
14:22–16:29 UTC, controller 0.4.788.1, plan 171, beside native 0.4.776.1) finished
11/11 phases and 106/106 outer cases with every phase restored, and `verify-completion`
accepted it. Attempts 13–17 stopped on test-runner faults, none in the driver:
- 13 and 14 (`ram-read-reference`): the second owned 2 GiB RAM disk, created right
  after the first was stopped, was refused by the headroom guard while that memory was
  still returning to Windows. Plan 168 waits up to 60 s for headroom first.
- 15 and 17 (campaign preflight, then phase 5's capture): Q: still held a few
  milliseconds of writes from the lab image stored on it. The native disk listing
  (`ecbe709`) made the inspection under a second instead of 3–9 s, which removed the
  slack that had hidden this. Plans 169 and 171 wait, bounded, for those writes to
  drain on their own; nothing is drained for them.
- 16 (`policies`, on the previous controller because a staging check refused a
  mislabelled bundle): `foreground-background.bin` split at 4 MiB inside a gap left
  by the run's own small files, on all three attempts. Plan 170 reserves such files'
  whole allocation first.

*The release run's rates against the 0.4.687.1 release run (October 14).* Disk-bound
cases were 1.2–3.2 times faster (for example cache-off random 4 KiB Q1 writes 255 →
751 IOPS, the sustained mixed phase 4,027 → 8,704 IOPS), but RAM-only paths were
50–70% lower: cached 4 KiB Q8 reads about 395k → 133k–180k IOPS, RAM-admitted random
writes 238k → 118k, recall re-reads 36.9k → 10.3k. The RAM-disk reference, whose
driver is unchanged on this branch, was 35–73% lower too. The Proxmox host was busy
throughout: load average 22–35 on its 32 threads, 9.9 GiB merged by KSM, and the VM
auto-ballooned to 8.0–12.6 of its 16 GiB. A same-window control settled it. The
unchanged `cache-map-cost` suite measured 107k–156k IOPS on the final build (run A,
`QueueCache-Verify-20261022-170234-2df61acaa3fa4a27bbb8e90b6ef423be`, mean host load
29.2) and 114k–163k on 0.4.687.1 from the restore snapshot, run by its own
controller (run B, `QueueCache-Verify-20261022-172901-a5b6b01a96064e09b750ba4255300911`,
load 28.2). The branch shows no RAM-path regression. Those RAM-path rates are host-bound
and not comparable across days; the ballooning is also the likely reason RAM-headroom
refusals became common in these sessions.

*Installer check on the final build (0.4.788.1, VM).* Upgrade from 0.4.776.1, a
setup re-run, uninstall, a forced rollback and a reinstall, with a restart after each
step that changes the registration. Results:
- Setup registered the boot filter last. The re-run rewrote no desktop file: checksums
  and timestamps were unchanged.
- Uninstall removed the filter and kept the service at demand start for recovery.
- The rollback check ran the installed `Install-Driver.ps1` with one injected failure
  after activation. Setup exited 1 and restored the previous registration exactly:
  filter list, start type, driver path and group.
- After the reinstall and a restart the 0.4.788.1 filter loaded at boot, and the
  installed CLI listed C: and Q:. Q:'s saved profile came back by itself.
- The rollback leaves the steps before activation in place (RAM provider, broker,
  sign-in task, PATH) by design. Run from a package folder, as in this check, they
  then point at that folder.

Evidence: exports `20DE474A…61F0` (10,133 files), `08B586A2…FE50` (675 files),
`D31602C1…F1B3` (31,441 files, release attempts 12–18, the block-C repeat and the
installer check), `214D84A7…15C7` (33,868 files, adds run A) and `4547C5BB…4587`
(2,460 files, run B on the restore snapshot), each checked file by file against guest
SHA-256 manifests. Verifier runs
`QueueCache-Verify-20261021-184820-9e4b93dcd4654e1badf709e762fa2750` (`trim-cache`),
`20261021-185202-c0307530a60c494e8fe0fc65674abf3a` (integrity) and
`QueueCache-Campaign-20261021-203107-a44bd25af1bb47f38c8b88260b872897` (focused).
Comparison runs `20261021-210214-…` (block A) to `20261021-222831-3281edf2e47f426f99719ded27979ba3`
(block C). Excluded: release attempts 10 and 11 (finding 1 and 2), the focused
campaigns that stopped on findings 3 and 4, and run 30, whose app-closing helper
collided with an orphaned run 29 left by an agent restart. Run 29 completed on its
own as the block-B paused anchor.

<a id="prio-162"></a>

### Read-aware drain limit and asynchronous TRIM (plans 162/163)

**Read-aware drain limit and asynchronous TRIM (plans 162/163, October 21).**
Native 0.4.771.1 from source `0406d48`, with loaded filter and RAM-disk hashes
`66769C20…7535` / `3F415741…042F`. Both features were then default-off lab switches
(`qcache developer driver read-aware` / `trim-mode`); they are defaults since
[plan 164](#final-164):
- *Read-aware limit N.* While a foreground lower read is in flight or ended
  within the last 10 ms, a drainer starts a batch only while fewer than N are
  outstanding. Barriers and paging-write fences keep full concurrency.
- *TRIM mode 3.* A TRIM of at most a quarter of the cache visits only its blocks
  through the index instead of scanning every slot under the lock. The lower
  TRIM is sent from an offload thread, so the ordered request path continues.
  The cache side applies at once. A fence keeps any newer version of the trimmed
  ranges from draining before the lower TRIM completes, and forwarded writes to
  those ranges wait for it.
The controller was 0.4.771.1 (plan 162), except for the plan-163 `trim-cache`
run, which used controller 0.4.772.1 from `4e9d0c0`; its rebuilt native binaries
were not loaded.

*Correctness, under Driver Verifier 0x209bb on both drivers:*
- The focused `stress-races` campaign passed 6/6 phases and 98/98 subchecks
  twice: once with both switches off, and once with read-aware 2 and TRIM mode 3
  on Q: and the lab volume. The second run's diagnostics show the switches held
  in every phase; the read-aware gate made drainers wait 903 times, and 7
  asynchronous TRIMs ran, none falling back.
- An Eager 2 MiB × 8 `compare-drain` run with both switches passed 6/6 cases and
  12,291 bypassed file hashes.
- `trim-cache` (plan 163) passed 12/12. Every earlier TRIM check passed in both
  TRIM modes: with mode 3, an unrelated TRIM returned in 13 ms with 3 MiB in
  flight and an overlapping one after 2,022 ms. While a TRIM was held 2 s,
  another file's 4 MiB rewrite and read took 14 ms. In `async-eager-ordering`,
  another file's rewrite drained to zero dirty bytes while that TRIM was held. A
  rewrite of the held range waited until the TRIM completed, and the disk then
  held it exactly.
- The plan-162 `async-fence` check failed (11/12, run excluded). It expected a
  rewrite of the held range to reach the cache at once. It took 1,810 ms
  instead and arrived after the TRIM: NTFS keeps a file's writes behind its
  outstanding file-level TRIM. A file rewrite therefore cannot reach the
  fence, and the verifier performs no raw disk writes. The fence's holding path
  and the forwarded-write wait stay unexercised (see [known issues](KNOWN_ISSUES.md#trim-no-longer-waits-for-unrelated-drain-writes)).

*Performance, Verifier off.* The nine runs took 16:10–16:37 UTC on one boot;
another Ceph client's reads averaged 2.0 MB/s (maximum 12.6). Read during drain
(`compare-drain-read`, three alone and three loaded windows per run; medians, ranges across the loaded
windows):

| Setting | Reader alone, IOPS | Reader while draining, IOPS (range) | p99 ms | Disk writes in the window, MB/s | Writer, MB/s | Gate waits |
|---|---:|---:|---:|---:|---:|---:|
| Eager 2 MiB × 8 | 1,839 | 340 (336–355) | 29.4 | 413 (351–431) | 325 | 0 |
| Eager 2 MiB × 8, read-aware 2, runs 11 / 18 | 2,170 / 2,263 | 1,141 (1,034–1,191) / 1,031 (992–1,127) | 10.2 / 11.3 | 247 / 245 | 285 / 269 | 13,813 / 13,748 |
| Eager 2 MiB × 8, read-aware 1 | 2,067 | 1,232 (1,075–1,560) | 7.8 | 170 (163–175) | 251 | 16,091 |
| 1024 KiB × 4 Idle (default) | 2,190 | 1,152 (1,110–1,162) | 10.4 | 249 (248–251) | 202 | 0 |

The #15 workloads (`compare-drain`) ran four times, alternating control and candidate,
with 12,291 hashes each:

| MB/s, median of 6 (range) | New 1 GiB + drain | 4,096 × 64 KiB + drain |
|---|---:|---:|
| Eager 2 MiB × 8, runs 14 / 17 | 299.8 (293.0–320.0) | 113.6 (88.9–131.6) |
| Eager 2 MiB × 8, read-aware 2, TRIM mode 3, runs 15 / 16 | 300.3 (265.2–314.2) | 122.2 (117.0–132.4) |

**Read-aware 2 restores the default's read responsiveness only while disk reads
happen.** With 2 MiB batches it allows 4 MiB in flight, the same as the default's
4 × 1 MiB. Reader IOPS, p99 and the drain rate during reads all matched the
default. In the #15 runs the gate never engaged (0 waits), and the large- and
small-file medians matched the control, so the limit costs nothing when no disk
read competes. Read-aware 1 protected reads a little more (p99 7.8 ms) but
drained at 170 MB/s, which does not justify it.

**Asynchronous TRIM removed the TRIM stall in mass-delete cases.** All 793
TRIMs in the candidate runs went asynchronous, with no fallbacks. In the
small-file case that received the TRIMs from the previous case's deletes, the
control took 2.52 and 2.58 s of application time (227–290 TRIMs, 3.2–3.9 s of
request queue wait). The candidate took 2.01 s (257 TRIMs, 1.1 s). That matches
the 0.5–1 s that plans 159/160 attributed to synchronous lower TRIMs. Small-file
cases without TRIMs took 1.96–2.98 s in both arms. No PrimoCache ran in this
session, so parity rests on the final same-window check. For orientation only:
the plan-159/160 window measured Eager 2 MiB × 8 at 298.3–304.9 / 132.2–142.6
and Primo at 285.5 / 70.4 MB/s.

Evidence: export `E5A0C5A5…60FC` (3,427 files), checked file by file against the
guest SHA-256 manifest. Verifier campaigns
`QueueCache-Campaign-20261021-153116-9b85e935b38347928044dfc430de543c` (switches
off) and `20261021-154027-54212b91ad2849308758b596f540979c` (on); Eager integrity
`QueueCache-Verify-20261021-154946-3083b8ac44ea496dbe31e9137a5b3c12`; `trim-cache`
plan 163 `20261021-155304-decee315cf2746aca7e2f098b50b9b1e`; excluded plan-162
attempt `20261021-152542-6b3f97ca9801418ba41773860aadeabb`. Performance runs
`20261021-161044-…` to `20261021-163325-48b6b5a9a2cb4c569c0b8a68a6485489`. The VM
was then restored and checked against the recorded original state: 0.4.687.1
hashes, the Fast 2 GiB 1024 KiB × 4 Idle profile, flags 15, timing and Verifier
off, original apps and Everything service, no owned files. The snapshot is
deleted (none remain), and the guard passed.

<a id="read-161"></a>

### Read during drain against PrimoCache (plan 161)

**Read during drain against PrimoCache (plan 161, October 21).** The question
was whether PrimoCache protects foreground reads while it drains, which would
make a read-aware limit necessary before Eager 2 MiB × 8 could be the default.
The new `compare-drain-read` suite runs the same DiskSpd windows for every
product. A 4 KiB random Q1 reader works on an uncached 8 GiB file, alone, and
12 s after a sequential 1 MiB Q8 writer started keeping the product's 2 GiB
cache full. The product therefore drains at its own full rate throughout.
Loaded windows must show pending data at both reader boundaries and at least
64 MiB written to the disk; alone windows at most 16 MiB.

Native 0.4.766.1 (the plan-159/160 TRIM-fix build; loaded hashes matched
`D68C25AD…0C11` / `489EE1D3…6CC8`) ran with controller 0.4.769.1 (`4441f09`,
plan 161). The session was A–B–A in one afternoon: QueueCache, PrimoCache 4.4.1
(same task settings as before), then QueueCache again after rollback and
redeploy. Foreign Ceph reads averaged 1.8 MB/s. All 10 runs completed; each has
three alone and three loaded windows. Reader figures are medians; ranges are
across the three loaded windows.

| Product and setting | Reader alone, IOPS | Reader while draining, IOPS (range) | p99 ms | Disk writes in the window, MB/s | Writer, MB/s |
|---|---:|---:|---:|---:|---:|
| PrimoCache, 2 runs | 2,598 | 163 (107–366) | 64.0 | 356 | 294 |
| QueueCache Eager 2 MiB × 8, A / C | 1,936 / 1,852 | 331 / 277 (250–377) | 29.4 / 30.0 | 363 / 420 | 296 / 320 |
| QueueCache Eager 2 MiB × 4, A / C | 2,012 / 1,673 | 497 / 414 (400–769) | 23.3 / 21.1 | 244 / 282 | 213 / 246 |
| QueueCache 1024 KiB × 4 Idle (default), A / C | 2,107 / 1,747 | 1,153 / 959 (948–1,216) | 10.7 / 11.6 | 251 / 248 | 203 / 201 |

**PrimoCache does not protect reads while it drains.** At the same drain and
writer rate, Eager 2 MiB × 8 left the reader about twice Primo's IOPS and half
its p99. So making Eager 2 MiB × 8 the default costs nothing relative to Primo,
and a read-aware limit is not needed for parity. The cost is relative to today's
default: during heavy draining a disk reader gets about 300 instead of about
1,000 IOPS, with p99 around 30 instead of 11 ms. In exchange, sustained drains
are 45–70% faster (363–420 against 248–251 MB/s), and the #15 workloads were 65–75% faster
(plans 159/160).
Reader slowdown follows the bytes in flight (4 → 8 → 16 MiB), as the plan-151
and plan-160 diagnostics also showed.

One side observation: alone, QueueCache's uncached 4 KiB Q1 reads ran at
1,673–2,107 IOPS against Primo's 2,598. That is a 20–35% slower single-request
miss path, separate from draining and not investigated here.

Eager 2 MiB × 4 is not a useful middle ground. It drained no faster than the
default under sustained writes, slowed the reader about twice as much, and
measured 238.0 / 235.2 MB/s for the new 1 GiB file (small files 111.3 / 124.5).
That is short of Primo's 285.5 in the plan-159/160 window.

Evidence: exports `BC349AE7…ACC2`, `F2EFA896…8568` and `08BF8AAE…A39C`, checked file
by file against guest SHA-256 manifests. One arm stopped before measuring,
because Steam's first post-boot shutdown exceeded the helper's 45 s wait; it was
rerun from scratch. The VM was then restored and checked: original 0.4.687.1
hashes, the Fast 2 GiB 1024 KiB × 4 Idle profile, flags 15, timing and Verifier
off, original apps and Everything service. Primo is absent, no owned files
remain, the snapshot is deleted (none remain), and the guard passed.

<a id="trim-160"></a>

### TRIM fix and same-window comparison (plans 159/160)

**TRIM fix and same-window comparison (plans 159/160, October 20–21).** Native
0.4.766.1 from source `402cb79`; the controller is 0.4.767.1 from `115fbf6`
(plan 160), a managed-only change that corrected the new TRIM checks. The loaded
filter and RAM-disk SHA-256 hashes were `D68C25AD…0C11` and `489EE1D3…6CC8`,
matching the 0.4.766.1 artifact; the 0.4.767.1 native binaries were not
substituted. Each drainer now records the disk range of its one outstanding
batch. A TRIM waits only while one of those ranges overlaps a trimmed range;
`TrimPaused` still starts no new batch, and the ordered request path still holds
later writes during the TRIM itself.

*Correctness, under Driver Verifier 0x209bb on both drivers:* the focused
`stress-races` campaign completed 6/6 phases with 98/98 subchecks (22 partial
reads, 8 paging, 42 policies, 13 pressure, 5 stress-races, 8 ordering/faults),
and `trim-cache` passed 5/5. A TRIM outside the in-flight batches returned in
2 ms with no write landing, while 3 MiB of 2 s lab-delayed batches stayed in
flight. A TRIM inside them returned after 2,008 ms, once those writes had landed.
Both left the untrimmed data exact. An Eager 2 MiB × 8 `compare-drain` run under
Verifier passed all 6 cases and 12,291 bypassed file hashes. The first plan-159
`trim-cache` attempt is excluded: its new check started the drain with an explicit
flush, which holds the ordered path, so its TRIM waited 10 s for the whole flush
whatever the TRIM rule. The existing `during-drain` check has the same shape and
therefore never exercised a concurrent TRIM. Plan 160 checks TRIM during
background Eager drains instead.

*Same-window A–B–A comparison, Verifier off:* block A (QueueCache, 22:14–23:01
UTC), block B (PrimoCache, 23:32–23:48), then block C (QueueCache again after
rolling back to the same snapshot and redeploying the same build, 00:17–00:34).
Another Ceph client's reads were near zero. The paused anchors were
72.6 / 2.9 MB/s rising to 114.4 / 7.4 during A, 111.9 / 8.1 and 108.7 / 6.9
around B, and 114.5 / 8.5 and 109.7 / 8.2 around C. Drain latency at 2 MiB × 8
was 52.6 ms (A Eager), 51.9 ms (Primo) and 50.3 ms (C Eager). All **22 complete
`compare-drain` runs and 233,538 bypassed file hashes** passed their proofs.

| MB/s, median (range) | New 1 GiB + drain | 4,096 × 64 KiB + drain | New 8 GiB + drain |
|---|---:|---:|---:|
| PrimoCache 4.4.1, block B | 285.5 (248.7–300.8) | 70.4 (55.6–87.0) | 315.8 (246.0–324.1) |
| QueueCache Eager 2 MiB × 8, block A | 304.9 (116.6–324.6), +6.8% | 142.6 (87.8–193.1), +102% | 294.1 (259.4–304.9), −6.9% |
| QueueCache Eager 2 MiB × 8, block C | 298.3 (281.7–328.1), +4.5% | 132.2 (120.9–163.1), +88% | 288.9 (284.0–314.6), −8.5% |
| QueueCache 2 MiB × 8 Idle, A / C | 284.3 / 214.1 | 91.0 / 69.3 | — |
| QueueCache 1024 KiB × 4 Idle (default), A / C | 184.1 / 170.0 | 86.6 / 80.0 | — |

**Eager 2 MiB × 8 meets both #15 medians in both QueueCache blocks bracketing
Primo.** The large-file ranges overlap: parity, with a 4.5–6.8% median lead.
Small files are about twice as fast, with block C's range wholly above Primo's.
Sustained 8 GiB writes trail Primo by 7–9%, with overlapping ranges. The default
trails Primo by 36–41% for the 1 GiB file and leads by 14–23% for small files.
Primo again took longer to absorb the 4,096 files (2.55 s; QueueCache Idle 1.34–2.06 s,
Eager 1.64–1.97 s). Two first-of-run Eager large cases (runs 11 and 51) took 3.1 and
3.6 s of application time without TRIMs or queue waits; their cause is untraced.

*TRIM in the file workloads.* No large-file case in plans 159/160 received the
100+ TRIMs that caused plan 158's 6.7 / 25.1 s stalls. Eager large cases with 5–32
TRIMs kept 0.20–0.22 s application time. Eager small-file cases with about 200
TRIMs took 2.02–2.34 s instead of 1.3–1.9 s; in plan 158 comparable cases took
2.24–4.21 s. The remainder is the TRIM itself: it is still sent synchronously on
the ordered request path, roughly 10–15 ms each by the queue-wait counter. An asynchronous lower
TRIM with a range fence is the next TRIM candidate.

*Foreground read interference (step 3), complete.* The plan-160
`drain-lock-diagnostic` completed 48/48 windows. One standalone window saw 5
background lower writes (20 KiB) and was remeasured as designed. Read scores are
timing off; drain rates are from the same windows.

| Reader | Drainers | Alone | With the Eager 2 MiB writer | Drain MiB/s |
|---|---:|---:|---:|---:|
| Uncached random 4 KiB Q1 | 4 | 2,824 IOPS, p99 0.698 ms | 1,094 IOPS, p99 15.413 ms | 238.2 |
| Uncached random 4 KiB Q1 | 8 | 3,352 IOPS, p99 0.577 ms | 545 IOPS, p99 28.457 ms | 274.7 |
| Cached 1 MiB Q8 | 4 | 28,109 MiB/s, p99 0.651 ms | 21,498 MiB/s, p99 0.988 ms | 215.5 |
| Cached 1 MiB Q8 | 8 | 25,549 MiB/s, p99 0.726 ms | 25,100 MiB/s, p99 1.007 ms | 282.5 |

Eight drainers gave +15% drain speed but halved concurrent disk-read IOPS (545
against 1,094) and nearly doubled p99 (28.5 against 15.4 ms). That reproduces plan
151 (465 against 1,057 IOPS, 24.5 against 13.7 ms), so plan 151's magnitude
stands. RAM-hit reads ran at about 97% VM CPU, and their alone controls varied;
no cached-read conclusion is drawn.

**Decision.** The #15 throughput goal is met by a configuration, not by the
defaults. Eager 2 MiB × 8 matched or beat a same-window Primo on both new-file
workloads, with byte, Verifier and TRIM checks passing. Defaults stay 1024 KiB × 4
Idle, for three reasons:
- At eight drainers a concurrent disk reader loses about half its IOPS.
- Eager drains compete with foreground I/O by design.
- Release-performance and Strict qualification have not run.

Next, in order:
1. A read-aware outstanding-write limit: full concurrency when no foreground disk
   read waits, fewer drain writes while one does.
2. An asynchronous lower TRIM.
3. A same-window re-check, then `release-performance`, before any default change.

Evidence: performance runs `QueueCache-Verify-20261020-222008-…` to
`20261021-003127-…`. Lock diagnostic `20261020-223909-4aeb1aeec2f34e72b09bf7aef631b305`.
Verifier campaign `QueueCache-Campaign-20261020-215026-659353b48a934421842e098abfa64839`,
`trim-cache` `20261020-220011-38d76dd275064013b8f06f4f573726fc`, Eager integrity
`20261020-220055-7b2ff598d72b4304a409ff60e62a9632`. Excluded check-design attempt:
`20261020-214645-b1b5ba0f19b1496fa8fd0689609afdf1`. All three exports were checked file
by file against guest SHA-256 manifests (archives `68986122…9057`, `635A60A4…80A7`,
`AE3A976F…890C`). The VM was then restored and checked: original 0.4.687.1
hashes, the Fast 2 GiB 1024 KiB × 4 Idle profile, flags 15, timing and Verifier
off, original apps and Everything service. Primo is absent, no owned files
remain, the temporary snapshot is deleted (none remain), and the final guard
passed. The VM work finished before the cluster's 03:00 backups.

<a id="matched-158"></a>

### Matched in-session research (plan 158)

**Matched in-session research (plan 158, October 18–20).** Branch
`experiment/drain-batch-concurrency`, source `d741d05`, CI build 0.4.764.1.
Loaded filter and RAM-disk SHA-256 hashes matched that artifact
(`71BB49F0…2F27`, `F4C23485…2B19`). Plan 158 adds the 8 GiB
`compare-drain-sustained` suite and the `QueueCachePaused` / `PrimoCachePaused`
disk anchors. Settings: Fast 2 GiB, flags 15, timing/Verifier off, normal
priority, Defender/Search disabled, background apps closed, one temporary
snapshot present. Primo 4.4.1 used shared 2 GiB L1, 4 KB blocks, Native defer
10 s, no L2 or prefetch.

| Session (UTC) | Runs |
|---|---|
| S1 QueueCache, October 18 05:52–06:48 | Twenty interleaved `compare-drain` settings arms (1024/2048 KiB × 4/8 Idle, 2048 × 8 Eager), four sustained arms, anchors |
| S3 PrimoCache, October 18 12:28–15:21 | Five `compare-drain` and two sustained arms between three anchors |
| S4a QueueCache, October 18 20:00 | One arm under another client's 50–80 MB/s Ceph reads; the remaining arms were held |
| S4 QueueCache, October 20 16:52–17:49 | After that load ended: same build redeployed from the same snapshot; one 1024 × 4 Idle run, two runs each of 2048 × 8 Idle and Eager, two sustained arms, anchors at both ends |

All **47 complete runs and 454,794 cache-bypassed file hashes** passed their
byte, bypass and restoration proofs. The incomplete runs are excluded and never
combined. Three anchors stopped at 2.0–2.9 s telemetry gaps during backend
stalls. One sustained Eager run stopped before its third repetition, on the
strict busy-disk check after an 8 GiB delete, and was rerun complete. Three
read/drain lock diagnostics did not complete. The Primo post-oracle pause proof
now has real provider runs.

1. **The backend changed more than the products did.** At queue depth 1 the
   anchors measured 104–113 MB/s for the new 1 GiB file at 06:21, then 30–43 at
   11:43 (QueueCache paused), 37–59 at 14:22 and 32–46 at 15:12 (Primo paused).
   `ceph health` reported the same warnings throughout. From 17:30 another Ceph
   client read 50–80 MB/s at 800–1,200 operations/s. During that load,
   QueueCache 1024 KiB × 4 fell from 174.2 / 92.1 MB/s (S1) to 23.0 / 8.9 (S4a).
   Primo measured 120.5 / 59.9 MB/s at 12:28–12:40, when the other client read
   about 6 MB/s, and 81.2 / 37.6 at 14:34–15:12, when foreign reads reached
   18–70 MB/s for part of the window.
   Per-case drain latency (mean queue depth ÷ write IOPS) at 2 MiB × 8 was
   51–58 ms for QueueCache in S1, 111–151 ms in Primo's quiet window and up to
   357 ms later. Cross-session product gaps on this disk are therefore not
   product differences; see the corrections below.
2. **Drain speed follows the bytes in flight, with diminishing returns.** In
   S1's first round, every arm ran within eight minutes. The new 1 GiB file
   drained at 170.7 MB/s with 4 MiB in flight (1024 × 4), 220.9 / 220.2 with
   8 MiB (2048 × 4 / 1024 × 8), 274.5 with 16 MiB (2048 × 8) and 305.2 with
   Eager 2048 × 8. Equal bytes in flight from different shapes performed the
   same. Drain latency rose 23 → 35–36 → 51–58 ms: about 10–13 ms fixed plus
   2.7 ms per MiB in flight, which limits that backend state to about
   350–400 MB/s. Twelve-case session medians were 174.2 / 215.0 / 210.0 / 213.9 /
   293.8 MB/s. A backend dip at 06:11–06:18 slowed whichever arms ran then
   (runs 16–19); the wide ranges of the 2048 KiB arms come from that dip, not
   from the settings. 1024 × 4 happened not to run inside it.
3. **Small files are limited by sequencing, not request size.** Under Idle a
   small-file case is about 1.15 s of application writes, then 1.2–2.3 s of
   drain. Its drain phase reached 182–265 MB/s with 387–1041 KiB writes in S1's
   first round, close to the large file's drain rate at the same bytes in
   flight. Eager drains during the writes: the drain left after the application
   ended fell to 0.07 s, and the first-round median rose 114.5 → 183.9 MB/s at
   2048 × 8 (twelve-case 76.2 → 177.5). Larger gathers have little left to gain
   for this shape. Primo took a median 1.90 s to absorb the same 4,096 files,
   against QueueCache's 1.15 s, in both of its windows. That application-phase
   gap does not depend on the backend; the 1 GiB file took both products about
   0.15 s.
4. **Sustained 8 GiB writes run at the drain rate once the cache fills.** S1:
   1024 × 4 156.1 MB/s (139.0–165.3), 2048 × 8 183.4 (122.4–218.7), only +17%.
   Primo: 126.9 (98.0–134.2) in its quiet window, 82.4 (78.3–122.7) later.
5. **TRIM stalls Eager writes.** NTFS's TRIMs for the previous case's 4,096
   deleted files landed in 32 of 132 S1 cases. Under Eager, 144 / 272 TRIM
   requests stretched the 1 GiB file's application time from 0.16 s to 6.7 s /
   25.1 s, and three small-file cases from about 1.3 s to 2.2–4.2 s. The driver's
   queue wait (6.6 s / 29.5 s) matches. Each TRIM pauses the drainers and waits
   for every in-flight lower write, overlapping or not, while the ordered
   request path holds later writes. Under Idle, TRIMs mostly land when little is
   in flight. The large file's application time stayed 0.15 s; five small-file
   cases with TRIMs took a median 1.74 s instead of 1.15 s. This also shows that
   the VM disk now accepts TRIM ([known issue](KNOWN_ISSUES.md#trim-no-longer-waits-for-unrelated-drain-writes)).
6. **Foreground read interference (step 3) is still unmeasured on this build.**
   Three `drain-lock-diagnostic` attempts stopped at 5/48 and 13/48 (a standalone
   control saw background writes) and 26/48 (a 2.4 s telemetry gap during a
   backend stall). Partial windows point the same way as plan 151, but their
   disk-read-alone controls swung 740 → 137 IOPS; no magnitude is claimed. Why
   standalone windows now see background writes (possibly the newly accepted
   TRIMs) is untraced.
7. **QueueCache versus Primo, matched by backend latency.** S4 ran when the
   drain latency at 2 MiB × 8 was 102–142 ms for QueueCache Idle. Primo's quiet
   window measured 111–151 ms at the same shape: about 16 MiB in flight in
   ~2 MiB writes. Against that window:

   | Mode | New 1 GiB + drain | New 4,096 × 64 KiB + drain | New 8 GiB + drain |
   |---|---:|---:|---:|
   | PrimoCache, S3 quiet window | 120.5 (105.0–141.0) | 59.9 (40.5–67.1) | 126.9 (98.0–134.2) |
   | QueueCache 1024 × 4 Idle (default), S4 | 96.5 (93.8–101.0), −20% | 59.0 (46.4–64.8), −2% | 92.6 (74.0–103.2), −27% |
   | QueueCache 2048 × 8 Idle, S4 | 126.5 (106.0–156.0), +5% | 50.6 (34.9–95.3), −16% | — |
   | QueueCache 2048 × 8 Eager, S4 | 141.3 (79.9–314.3), +17% | 173.3 (38.2–204.1), 2.9× | 125.5 (124.8–144.6), −1% |

   Default settings trail Primo for the large and sustained files. With the same
   bytes in flight, 2048 × 8 Idle matches Primo for the large file. Eager
   2048 × 8 beats Primo's medians for both #15 workloads and matches it
   sustained. The large-file ranges overlap. The match uses a measured
   covariate, not simultaneous runs, so this is evidence for the direction, not
   a parity qualification. S4's own anchors moved from 81.9 / 2.4 MB/s to
   113.3 / 7.2 during the session. S4 Idle small-file writes averaged
   185–272 KiB, against 511–751 KiB in S1, for an unknown reason; Eager's
   small-file result was insensitive to it.

**Decision.** Defaults stay 1024 KiB × 4 Idle. For #15 the candidate is
draining during writes (Eager, or an earlier Idle start) at 2 MiB × 8; it is the
only measured setting that meets both #15 medians here. Before promotion it
needs three things. First, TRIM must wait only for overlapping in-flight writes.
Second, a completed foreground-read measurement, adding a read-aware limit if
eight drainers still hurt readers. Third, a qualification campaign that compares
against Primo within one window. Batching and gap-filling work is deprioritized.

**Corrections to earlier sections.** Plans 153/154 put 2 MiB × 8 23.2% / 33.7%
behind Primo, but the QueueCache arms ran at 22:05–22:20 and Primo at 00:02.
Given the swings above, that gap measures the backend as much as the products.
The plan-153 batch attribution (94–96% of small-file batches end at an uncached
gap) is accurate but does not identify the bottleneck; finding 3 does. The
plan-155/156 aligned gather stays rejected because it showed no gain. Its −16.3%
small-file loss is within the variation one arm showed within an hour here
(2048 × 4 small: 57.6–116.4 MB/s). Plan 151's direction (eight drainers hurt disk
readers more than four) remains plausible. Its −56% magnitude came with
disk-read-alone controls ranging 1,869–3,449 IOPS.

Evidence: S1 runs `QueueCache-Verify-20261018-055219-…` to `-064348-…`, S3
`20261018-122803-…` to `-151158-…`, S4 `20261020-165208-…` to `-174540-…`. Full
identities, per-case phases, contention samples and TRIM counters stay in the
private evidence. All three exports were checked file by file against guest
SHA-256 manifests: archive hashes `F594F10A…22EE`, `8FD1A099…1D75` and
`B2AC6FC3…EB44`. Restoration was verified afterwards. The VM is back on the
original 0.4.687.1 hashes with the Fast 2 GiB 1024 KiB × 4 Idle profile, copy
flags 15, timing and Verifier off. Its original apps and the Everything service
are running, Primo is absent, and no owned files remain. The temporary snapshot
is deleted (none remain), and the final capacity guard passed with 298.4 GB free.

<a id="finish-15"></a>

### Plan to finish #15

**Plan to finish #15 (status after plans 164–167: steps 1 and 2 done; step 3 awaits the owner).**
This replaces the step table below, which is kept as history. The research questions are answered:

- *Write speed.* Drain speed follows the bytes in flight, and small files need
  draining during writes. Eager 2 MiB × 8 meets both #15 medians in a same-window
  comparison (+5–7% for the 1 GiB file, about 2× for small files) and is 7–9%
  behind on 8 GiB sustained writes ([plans 159/160](#trim-160)).
- *Reads during drain.* PrimoCache does not protect them ([plan 161](#read-161)).
  Eager 2 MiB × 8 alone cut a concurrent disk reader from about 1,100 to about
  300 IOPS. With read-aware limit 2, the reader keeps the default's level
  (1,031–1,141 IOPS, p99 10–11 ms). The #15 workloads keep their full speed,
  because the limit acts only while disk reads happen ([plans 162/163](#prio-162)).
- *TRIM.* An asynchronous lower TRIM with an index lookup removed the
  remaining 0.5 s that mass-delete TRIMs added to a small-file case.
- *What will not help:* more batching or gap work, lock rewrites, larger batches,
  more drainers, Eager 2 MiB × 4, or read-aware limit 1.

| Step | Work | Done when | Estimate |
|---|---|---|---|
| 1. Done: make the candidate the default | New caches default to Eager, 2048 KiB × 8; the driver enables read-aware 2 and TRIM mode 3 at cache creation; the lab switches stay for A/B tests. Saved profiles keep their drain settings. (`ce21c5a`; driver fix `87de299`.) | Done: host tests and CI pass; under Driver Verifier `trim-cache` 12/12, the focused campaign 98/98 and a 12,291-hash integrity run on native 0.4.776.1 ([results](#final-164)) | Took about 4 h of VM time, mostly the four findings in the results |
| 2. Done: final qualification | One same-window Primo check (`compare-drain` and `compare-drain-read`, A–B–A), then `release-performance` | A against B: the default at 292.4 / 90.7 MB/s against Primo's 261.0 / 52.5, and the reader at 757 against 170 IOPS. `release-performance`: 11/11 phases, 106/106 cases, restored (attempt 18, plan 171). Its RAM-path rates were host-bound that day; a same-window 0.4.687.1 control matched them. | About 4 h VM |
| 3. Close #15 | Publish the final numbers in the comparison, findings and issue | Owner agrees | — |

After #15, as separate issues, none blocking:
- *Uncached Q1 read miss path.* Alone, it ran 20–35% slower than Primo's.
- *#13 power-loss safety* and *#16 Strict stalls.*

All evidence so far comes from one Ceph-backed VM. Hard disks and SATA SSDs were
not measured; on them eight 2 MiB drain writes may cost reads more.

<a id="drain-decision-153"></a>

### Plan to match or exceed Primo (October 17)

**Plan to match or exceed Primo (updated after the October 17 batching test).**
The aligned-gather experiment is complete and rejected. The current gather already
produced much larger small-file backing writes in this session; explain that
change and refresh the provider controls before choosing another kernel policy.
Parity means matching a contemporaneous Primo run, not beating its older score.

*Superseded by the [plan to finish #15](#finish-15).* *Status after plans 159/160:* the TRIM fix and step 3's foreground-read
measurement are complete, and a same-window comparison shows Eager 2 MiB × 8
meeting both #15 medians ([results](#trim-160)). Next comes a read-aware
outstanding-write limit and an asynchronous lower TRIM. Step 5 (qualification)
follows on the final candidate.

*Status after plan 158:* step 2 is complete ([results](#matched-158)). The next
work is a TRIM path that waits only for overlapping in-flight writes. Then step
3's foreground-read measurement, which failed its strict gates three times in
plan 158. Step 5 then qualifies Eager 2 MiB × 8 rather than a new batching
policy. Step 4 is not justified by the plan-158 evidence.

| Step / status | Work and reason | Completion criterion | Expected test time, excluding engineering, builds, provider swaps and boot settling |
|---|---|---|---|
| 1. Complete: test small-file batching | Same native build, current versus aligned 2 MiB gather, Fast 2 GiB Idle, 2 MiB × 4, flags 15; three alternating pairs per shape. Added gap/backward attribution. | All 12 performance cases and 24,582 bypassed hashes passed. Aligned medians lost 3.5% large / 16.3% small and failed the gain/control gates. Policy removed; diagnostics retained. Separate Verifier run also passed all 12 cases / 24,582 hashes. | Actual worker time: about 2 min performance, 2.5 min correctness; setup took longer |
| 2. Next: establish current controls and placement | Run only the two existing shapes on Direct, QueueCache and Primo, each alone with three repetitions. Use the same recoverable VM layout and disabled services. Capture file extents and dirty-run/gap-distance distributions outside scoring if needed to explain the old ~181 KiB versus current ~1642 KiB small-file requests. If considering defaults, screen 1 MiB × 4 against 2 MiB × 4 on the same build. | Complete provider matrices, stable/bracketed QueueCache controls, all hashes and bypass proof; report every range. State whether parity already holds under these conditions. Do not attribute a cross-session gain to code or to disabled services. | About 20–30 min runner work; optional settings screen 5–15 min; no full competitor refresh |
| 3. Conditional: improve concurrency while protecting reads | If a throughput gap remains despite large backing requests, test one default-off read-aware outstanding-write limit: allow higher concurrency when quiet, reduce it while foreground disk reads wait. Plan 151 supports this responsiveness experiment. | Lower loaded-reader latency, no repeatable 3% loss in declared throughput controls, preserved fairness/error semantics. A code-gain claim needs at least 5%, every relevant pair faster and separated aggregate ranges; otherwise reject or report inconclusive. | About 20–30 min short mixed-read and large/small controls |
| 4. Conditional: one further batching candidate | Only if measured dirty runs still limit representative small files, use gap lengths/extents/available adjacent dirty data to choose one bounded picker or background merge experiment. Clean cached gap filling has low support: 202/202 current forward gaps were absent here. Never write unknown bytes or make fitting Fast admission depend on lower I/O; retain pins, oldest-version ordering and range fences. | Stated hypothesis, default-off candidate, same-build alternating pairs, all byte/telemetry proofs and the same gain/control gates. Reject failed ideas. Lock rewrites, 12/16 threads and 4–16 MiB gathers require new evidence. | 10–30 min per justified candidate; stop after the first discriminating screen |
| 5. Pending: qualify one final candidate | Targeted Verifier, faults, partial sectors, paging, ordering/lifecycle and RAM-first lower-attempt counters; check sustained larger-than-cache writes, default Idle foreground interference and Strict boundaries. Then the maintained focused checks and one final release-performance campaign. | Complete final-build checks, no correctness/read regression or hidden control loss, successful restoration. Record every unexercised path; the short fitting test cannot settle them. | Focused 10–30 min; final release-performance about 2 h |
| 6. Pending: final parity and publication | Repeat the narrow provider comparison if candidate/environment changes require it; publish build identities, all medians/ranges and remaining gaps in the existing comparison, findings and issue #15. Restore VM/apps and remove owned data/snapshot after verified export. | QueueCache median ≥ contemporaneous Primo on both shapes, stable controls, complete hashes/telemetry, and step 5 passed before promotion. Only then consider defaults/closing #15. Filesystem power-loss safety (#13) remains separate. | Narrow provider test about 20–30 min if needed; documentation/cleanup otherwise |

These are short decision cycles followed by one final qualification, not a fixed
multi-day test campaign. Engineering effort and the number of useful candidates
remain uncertain; this experiment provides no basis to promise a parity date.

<a id="small-file-gather-156"></a>

### Aligned-gather experiment (plans 155/156)

**Completed aligned-gather experiment (plans 155/156, October 17).** Current
selection versus a runtime default-off policy that constrained gathering to the
oldest anchor's aligned 2 MiB region. It did not bridge gaps, add lower reads,
change ownership/fences or change admission. Both arms used Fast 2 GiB Idle,
2048 KiB × 4, flags 15 and timing off, with three paired repetitions per shape
and reversed order in the second repetition. Existing unique SplitMix payload,
new-file workloads and write-plus-explicit-drain endpoint are unchanged.

| Shape | Current gather MB/s, median (range) | Aligned gather MB/s, median (range) | Median change | Mean backing write KiB, current / aligned |
|---|---:|---:|---:|---:|
| One new 1 GiB file | 227.95 (227.65–261.66) | 219.98 (214.26–257.45) | −3.5% | 1964.4 / 1942.7 |
| 4,096 new 64 KiB files | 111.85 (110.17–113.50) | 93.58 (92.16–110.98) | −16.3% | 1641.5 / 990.0 |

These are decimal payload MB/s including creation, writes, metadata flush,
explicit product drain and sampled zero pending/disk queue, not isolated drain
bandwidth. Backing sizes are median per-case byte/operation ratios including
metadata, not physical-media writes. Large-file paired changes were −1.61%,
−3.37%, −6.00%; small-file changes were −16.33%, −18.80%, +0.74%. The candidate
fails the predefined gain gate and repeatedly loses more than 3% in the large
control. **Reject it and retain the original gather.** Plan 158 later saw one
arm range 57.6–116.4 MB/s for small files within an hour; the rejection rests on
the absent gain, not on the size of the loss. Ranges overlap and
application times vary too; the median loss alone does not isolate every cause.
The retired suite is no longer selectable from plan 157; its evidence models
remain readable for historical reports/recovery. No default was changed.

The small-file current/aligned totals were 629/934 batches. Forward gaps were
202/484: all 202 current gaps were absent from cache, and aligned had 483 absent
plus one clean-full gap. Current backward stops were 263 gaps / 366 in-flight;
aligned had 483 gaps / 451 aligned boundaries. No partial/filling/fence backward
stops advanced. Each case reconciles forward reason, backward reason and size
bucket to its batch count, and gap classifications to forward gaps. Aggregate
software batches averaged 1448.8/976.0 KiB (distinct from the table's median
backing-request statistic). Classification does not measure gap distance,
physical allocation, or how much useful adjacent dirty data remains.

The unchanged current gather's ~1642 KiB small-file backing requests are much
larger than the earlier ~181 KiB session, at the same 2 MiB × 4 setting. Current
227.95/111.85 MB/s must **not** be compared with older Primo 109.68/54.47 to claim
parity or a driver gain. No fresh Direct/Primo ran here. Backend/NTFS placement,
request formation and session variation remain unresolved. This changes the
next decision: establish fresh controls before a riskier batching implementation.

**Verification and evidence.** Native 0.4.761.1, source `a2249b1`, came from
[the passing native CI](https://github.com/devedse/QueueCache/actions/runs/39057350363).
Actual loaded filter SHA-256 was
`3037482DB14CE0CAEA5E5222BF8179048E7CF7C283631DBC4D70AF21C1541C3F`;
RAM disk was `6330959F9BC5A36122F7477963BB0833EC84A9EEFC808B64869529EB6229D1C2`.
The complete Verifier run used plan 155 / controller 761; performance retained
those exact native binaries, with the managed-only plan-156 controller from
`e2aa5c8`, [runner CI](https://github.com/devedse/QueueCache/actions/runs/39059367859),
package 0.4.762.1. That package's native binaries were not substituted.
Controller SHA-256 was
`FB66CE0F96C62F40BBD1B55E0A867D62C3102550B207AD58E654F82D7112C859`.

| Evidence | Complete run ID, prefixed QueueCache-Verify- | Outcome |
|---|---|---|
| Correctness, actual Verifier 0x209bb on both drivers | `20261017-183856-b25c1cd7215449b99ba081fc2cb6e3f0` | 12 cases / 24,582 bypassed hashes, about 153 s worker time; not performance scores |
| Performance, Verifier off, 15.6 min boot settling | `20261017-190028-238f1f85f3bd449294f714ed3be8e210` | 12 cases / 24,582 hashes and post-oracle bypass proofs; about 120 s worker time |

FINISHED/status/summary/results/run log and the exact raw scores, counters,
policy states, telemetry coverage and oracles were audited. Every performance
hash oracle also proved unchanged hit/miss/accepted-byte counters across the
same paused identity/profile; policy restoration completed. Five performance
cases acquired 8–72 KiB dirty after the scored endpoint before untimed pause;
its source remains unattributed. Disable drained it. Hashes validate the later
paused state, not persistence at the exact scored instant or power-loss safety.
Defender/Search stayed disabled, indexers/launchers were closed and priority was
normal. Capacity/near-full guards passed; existing Ceph availability/scrub
warnings remain a storage uncertainty.

**Mistakes and fixes.** The first Windows CI failed a stale Windows-only plan
version assertion; fixed before deployment. An early correctness attempt
`20261017-182743-ce6bd0b60a30461cb0c12e0fdd29cfec` began before delayed broker
startup finished. Saved-profile restoration replaced the temporary 2 MiB profile
with 1 MiB and re-enabled the cache during the fourth oracle; strict restoration
correctly refused the changed settings. The entire matrix is excluded. Raw
state/service logs were preserved, owned workers confirmed stopped, original
test settings explicitly restored, and maintained verify-recover completed in
`20261017-183508-51b723075a9644a68730b741b334ccfd`. A fresh complete correctness
matrix followed. The failed run remains failed. No earlier settled performance
session is thereby invalidated.

The maintained runner now refuses an installed startup broker that is not
Running, and requires post-oracle bypass proof for QueueCache and Primo. Plan
157 additionally rejects omitted observed snapshot counters instead of accepting
DTO zero defaults, and retires the failed policy/suite. A private PowerShell
wrapper also miscounted the successful correctness JSON array; raw maintained
results showed all 12 passed, the wrapper was fixed and no successful matrix was
rerun. Host contracts cover missing evidence, startup rejection, false bypass
success and independent failure restoration. The new Primo post-oracle path
still needs a real provider run. The final retirement/parser edits require CI;
their driver behavior was not separately VM-deployed. Broader Strict, sustained,
Idle foreground, faults and power-loss qualification remain open.

All 785 final evidence files were exported and guest SHA-256 manifests checked
before rollback; archive SHA-256
`33A2D5F26D77A6228B5979EA9C6137894EA92CF9B7AB7996E57C242573ADEF90`.
Raw evidence stays private. Final restoration passed: original 0.4.687.1 loaded
module hashes, saved profiles, Fast 2 GiB Idle 1024 KiB × 4 runtime, flags 15,
timing/Verifier off, original applications and Everything service. The broker
was confirmed Running before restoring the profile. Defender/Search remain
disabled, Primo is absent. Restoration records were pulled and SHA-256 checked;
all ten remaining owned helper/evidence files were preserved and hash-verified
before scoped deletion. Only system folders and the reusable QueueCache-Lab
remain at Q:'s root. The temporary snapshot is deleted, zero snapshots remain,
and the final capacity/near-full guard passed with 313.6 GB free. Existing Ceph
availability/scrub warnings remain; no unrelated files or lab disks were removed.

### Drain decision screen (plans 152–154)

**Completed drain decision screen (plans 152–154, October 16/17).** Native
0.4.756.1 (`d7895b5`), QueueCache runner `d58b2e1` / plan 153, Direct and Primo
runner `48f8966` / plan 154. Fast 2 GiB Idle, flags 15, timing/Verifier off,
normal priority, Defender/Search disabled. Four complete QueueCache six-case
runs used bracket controls; Direct and Primo each completed a fresh six-case
run after snapshot rollback. All used the same unique payload, workload and
scored endpoint, three repetitions per shape and the same temporary snapshot.
All **36 cases and 73,746 unbuffered file hashes passed**. Plan 154 changes only
Direct's unscored preparation wait; QueueCache/Primo preparation and every
scored workload are unchanged from 153. This is a completed decision screen,
not a default qualification; grouped arms and control drift limit attribution.
The six successful comparison workers took **about 23 minutes combined**, including
preparation, file checks and case restoration. CI/build/deployment, excluded
attempts, provider swaps and required post-boot settling are additional time.

| Runtime arm | New 1 GiB + drain MB/s, median (range) | New small files + drain MB/s, median (range) | Mean backing write KiB, large / small |
|---|---:|---:|---:|
| Starting control, 1024 KiB × 4 | 57.81 (49.43–61.94) | 20.05 (18.59–24.66) | 1009.6 / 182.0 |
| 2048 KiB × 4 | 74.66 (65.18–101.26) | 27.07 (25.42–65.67) | 2002.3 / 180.9 |
| 2048 KiB × 8 | 84.24 (73.98–127.51) | 36.14 (34.15–49.95) | 2009.4 / 190.2 |
| Ending control, 1024 KiB × 4 | 66.54 (59.86–73.39) | 42.54 (29.28–51.28) | 1015.4 / 207.0 |
| Direct disk, QueueCache absent | 47.71 (43.60–54.87) | 1.28 (1.25–1.48) | 986.9 / 5.0 |
| PrimoCache 4.4.1, shared 2 GiB, Native defer 10 s | 109.68 (100.31–117.36) | 54.47 (45.71–67.54) | 1939.0 / 1339.3 |

The ending control improved 15.1% for large files and 112.2% for small files.
The combined arm's large-file range narrowly exceeds both control ranges; the
small-file ranges overlap and do not establish a stable improvement. These
results preserve the earlier plan-148 session as historical evidence and do not
complete the default-qualification gates. The 2 MiB × 8 median still trails
fresh Primo by **23.2% large / 33.7% small**; reaching its medians would require
about **30.2% / 50.7%** further throughput. Its small-file median is also 15.0%
below the ending control, with overlapping ranges. Picking its fastest
127.51 MB/s large-file repetition would not establish parity.
*Correction (plan 158):* those QueueCache arms and the Primo run were about two
hours apart on a disk whose speed changed up to threefold within hours, so this
gap does not isolate the products ([plan 158](#matched-158)).

Primo's earlier 78.96 / 57.62 MB/s becomes 109.68 / 54.47 in this session
(+38.9% / −5.5%); Direct changes 48.41 / 1.25 to 47.71 / 1.28 (−1.4% / +1.8%,
using unrounded scores). Defender/Search were already disabled in both sessions.
These differences do not isolate a product improvement or the effect of disabling
services. Existing Ceph availability/scrub warnings remain; capacity and near-full
guards passed. These are virtual-disk, completed-write measurements, not a
physical-media speed limit or a power-loss test.

**Batch attribution:** V4 snapshots count one forward-gather termination reason and size bucket
per batch; their totals reconcile with the batch count in every case. At 2 MiB,
94.3% / 96.1% of small-file batches (four/eight drainers) ended with no adjacent
dirty version; mean software batch size was 209.6 / 217.5 KiB. Partial sectors,
in-flight overlaps, filling and range fences did not advance the other forward
termination counters; backward-walk stops were not attributed. Large-file batches averaged about 2 MiB and mostly reached their size limit.
This points toward investigating gaps before raising the size ceiling again.
*Plan 158:* small-file drain phases later ran close to the large file's rate at
the same bytes in flight; the small-file total is limited by writing, then
draining, in sequence ([finding 3](#matched-158)).
`FindOldestSlot` excludes clean entries: a gap does not establish file fragmentation
or prove that the neighboring bytes are absent from RAM. A bounded first candidate
is gathering within aligned 2 MiB address regions, preserving the oldest anchor,
to avoid leaving short dirty tails; this is a source-derived hypothesis, not a
measured gain, and cannot bridge true gaps. Classify clean cached versus uncached
gaps before considering gap filling; never write unknown sectors or bypass
version/fence ownership.

For example, the four-drainer small-file case at 65.67 MB/s issued 402 backing
writes averaging 755.8 KiB; its 25.42 MB/s case issued 2,269 averaging 134.1 KiB.
Both admitted application writes in about 1.2 s. Request shape therefore varies
substantially even at fixed settings; storage latency alone is not established
as the cause. In all twelve small-file cases, samples at/before application end
recorded no backing-write increase. The 100 ms sampling cannot resolve the last
fraction of that interval; longer admission delay is not the first candidate.

Nineteen of the 24 QueueCache cases recorded 8–68 KiB newly dirty between the
scored zero-pending observation and untimed Disable. Its source remains
unattributed; Disable drained it, then every paused-state observation was clean
and every file hash passed. The oracle validates that later paused state,
without proving file persistence at the exact earlier scored instant.

**Decision at the end of plans 153/154:** the small backing requests justified
classifying gaps and trying one bounded aligned gather. That follow-up is now
[complete and rejected](#small-file-gather-156). Use the updated plan above;
current controls and request sizes changed enough to require a fresh provider
comparison before another batching policy. Defaults remain 1024 KiB × 4;
issues #13/#15/#16 remain open.

**Allocation and preparation:** staging is now bounded at small budgets, and
remaining payload space is checked before unsigned subtraction. Normal 2 GiB
staging and selection are unchanged. All fourteen 1/15/16/17/63/64/65 MiB Fast/Strict
byte-oracle checks and the maximum-settings quick file-integrity smoke passed
under actual Driver Verifier. This resolves the source-reviewed allocation blocker;
broader Strict, sustained and lifecycle qualification is still pending.

The initial plan-152 combined run stopped before its third workload after a
new 4 KiB pending write appeared during preparation (no in-flight bytes, errors
or fault; source unattributed). Restoration succeeded; its two completed samples
are excluded. Plan 153 records bounded unscored preparation drainage, retaining
immediate rejection of faults/missing evidence and the scored ready handshake.
All four arms were collected afresh; no 152/153 matrix was assembled. The new
complete screen needed one preparation attempt per case. The earlier two complete
152 arms remain historical evidence, outside this comparison.

The first Direct plan-153 run stopped before its sixth workload because its
single preparation observation found the backing disk busy after cleanup;
restoration succeeded. All five samples are excluded. Plan 154 adds a bounded,
recorded idle wait only to Direct preparation, preserving the zero-queue start
condition, payload, scored endpoint and complete-matrix checks. Its invalid-counter
and timeout paths are host tested. The fresh complete Direct 154 run recorded one
busy observation followed by idle, exercising the new preparation wait. Direct
and Primo 154 both passed; no incomplete repetitions are combined. QueueCache
153 measurements are unaffected. Both setup failures and their excluded samples
remain in the private evidence; neither changed scored timing or converted
missing/invalid evidence into success.

Exact complete runs (each prefixed `QueueCache-Verify-`):

| Mode | Run suffix |
|---|---|
| Starting control | `20261016-220507-3ebc4a18ad3441deab333c85154695c4` |
| 2 MiB × 4 | `20261016-221007-e5e08b8a0d7c4e23a03f4f1d69dbbf65` |
| 2 MiB × 8 | `20261016-221358-6797e3fb17954ed884ccb80edb8f7f73` |
| Ending control | `20261016-221756-a7c32e274d27444a8674ec0b0b951ab3` |
| Direct | `20261016-231227-cce2dba4f55f495cbadfded7bdcd1c4e` |
| Primo | `20261017-000251-c6fcca8b303b4436ab65d2eca47626f3` |

Loaded native SHA-256 hashes matched the [0.4.756.1 CI artifact](https://github.com/devedse/QueueCache/actions/runs/38968658858):
filter `5EBB7F8D3AED873DC592B5C370A0FCC943B77F1F1222F6BC651F3449A68E5DA7`,
RAM disk `B3F50B4883BA3CE9C298DB95DB8957860F25A28F81F0A977BEA3D54A1D9F17C6`.
The [final runner CI](https://github.com/devedse/QueueCache/actions/runs/38979534587)
passed. Host Release management contracts and Developer builds passed; Linux
management tests emitted platform analyzer warnings. The three exported archives
containing the complete measurements have 1,767 files, all checked against guest
SHA-256 manifests before restoration. The excluded Direct archive was checked
separately. Raw evidence stays private.

Restoration is verified: original 0.4.687.1 loaded module hashes, saved profiles,
Fast 2 GiB Idle 1024 KiB × 4 runtime, flags 15, timing/Verifier off, original
apps and Everything service. Defender/Search remain disabled; Primo is absent.
The first post-boot profile application was refused by the available-memory
guard. After inspecting the clean driver and startup memory, the same captured
profile applied successfully; the guard was not bypassed. The temporary snapshot
and owned C:/Q: test/evidence files are removed; reusable lab fixtures remain.
Zero snapshots remain, C:/Q: have 62.4/199.8 GB free, and the final capacity/
near-full guard passed with 313.9 GB in the backing pool. Existing availability/
scrub warnings remain. All benchmark processes have finished.

### Lock and drain measurement (plan 151)

**Short lock/drain measurement (plan 151, 2026-10-16).** The complete
`drain-lock-diagnostic` run measured 48 six-second windows: cached sequential
1 MiB Q8 and uncached random 4 KiB Q1 reads, alone/with a continuous sequential
writer, 2 MiB batches at four/eight drainers, timing off/on and three repetitions
with reversed order. The runner took **18m56s**, including preparation,
inter-case flushing/warming and restoration; scored reads total **4m48s**.
Native build **0.4.749.1** is unchanged from the earlier experiments; the tested
managed runner source is `0f44814`. Actual loaded hashes matched that CI artifact.
Verifier was off, post-boot settling exceeded 15 minutes, processes had normal
priority, Defender/Search were disabled and background apps/service were stopped.

The writer uses a separate 4 GiB file, 1 MiB Q8 writes and Eager draining with
write retention off. Cached reads use a warmed 512 MiB file with Automatic
allocation; uncached reads use a separate 1 GiB file with Fixed 100% writes
(zero read-cache admission). Pure hit/miss counters and at least 75% pending
observations during each loaded reader envelope were required. These are two
separate read workloads, not a controlled comparison of cache-hit versus miss
cost or a qualification of the Idle product defaults.

Medians of three windows per cell. Read scores/p99 are **timing off**; lock
occupancy is from separate **timing-on** diagnostics. Arrows mean alone → with
the writer. GiB/s is binary throughput; IOPS is completed reads per second.

| Reader | Drainers | Read throughput, alone → loaded | Read p99 ms, alone → loaded | Global lock held %, alone → loaded |
|---|---:|---:|---:|---:|
| Cached 1 MiB Q8 | 4 | 35.47 → 30.84 GiB/s | 0.436 → 0.665 | 30.27 → 34.80 |
| Cached 1 MiB Q8 | 8 | 34.79 → 30.04 GiB/s | 0.456 → 0.756 | 30.14 → 33.59 |
| Uncached random 4 KiB Q1 | 4 | 2,275 → 1,057 IOPS | 0.803 → 13.684 | 0.64 → 1.12 |
| Uncached random 4 KiB Q1 | 8 | 3,437 → 465 IOPS | 0.598 → 24.473 | 0.78 → 0.88 |

**Decision:** prioritize storage scheduling/concurrency over a reader–writer
lock rewrite for the drain/read problem. During uncached reads, eight drainers
raised median drainage **240.5 → 322.0 MiB/s (+33.9%)**, but reduced reader IOPS
**56.0%** and raised p99 **78.8%** against four. Every loaded eight-drainer read
IOPS/p99 range is worse than four's. *Plan 158:* the disk-read-alone controls
here ranged 1,869–3,449 IOPS, so the magnitude is uncertain; two plan-158
repeats did not complete. The lock was held only about 1% of the
diagnostic envelope and mean global acquisition waits were 1.09 / 3.69 µs;
these global means are not reader-specific latency. Low VM CPU use (about
5–6% in timing-off loaded disk-read windows) and the lower-path timings support
storage competition as the main explanation, without identifying a physical
device or Ceph cause.

For RAM hits, concurrent draining reduced throughput **13.1% / 13.7%** versus
the matching alone controls. Eight drainers drained **251.0 → 332.2 MiB/s**
but served RAM reads 2.6% slower than four; ranges were separated. VM CPU use
was approximately **97%** even for reads alone. Lock occupancy around 30–35%
and global waits of 6.5 µs alone / 11–13 µs loaded show scope for less metadata
work, but do not isolate lock blocking from CPU scheduling or memory-copy costs.
Do not claim a reader–writer lock's gain from this screen.

Across loaded timing-on windows, lower I/O accounted for approximately
**98.9–99.9% of the summed measured drainer selection/copy/retirement/lower
phases**. Concurrent drainer waits sum across threads and exceed wall time;
they are not an application-latency breakdown or an exhaustive account of
thread time. A candidate follow-up is to reduce outstanding drain writes while
foreground disk reads are waiting, retaining higher concurrency when they are
absent. Shorter read-hit bookkeeping is a secondary candidate; sharding/lock-free
lookup is not justified as the first change. No locking or default change was made.

Storage controls varied: four-drainer disk-read-alone IOPS ranged
**1,869–3,449**, and eight-drainer loaded timing-on drainage ranged
**69.8–331.8 MiB/s**. Ceph retained availability and BlueStore slow-operation
warnings; capacity/near-full guards passed. The timing-on disk-read results
cannot isolate instrumentation overhead from that variation. Keep this screen
separate from the matched competitor comparison; no Primo run, Strict test,
sustained qualification or power-cut test was added.

The first plan-150 attempt stopped at case 9 because the fixture incorrectly
treated disabled promotion as disabled read-miss admission. The strict purity
check rejected its cached hits; restoration succeeded with no driver errors.
It remains incomplete and is excluded entirely. Plan 151 fixes the cold arm
with a zero read partition and adds a host contract for that distinction.

Exact completed run: `QueueCache-Verify-20261016-201557-410f1de47e934ba48036c8e4d2a51593`.
Excluded setup attempt: `QueueCache-Verify-20261016-200734-551c990292b04121a9a4a2753ea41294`.
All 11,749 exported evidence and runner files were verified against guest-side
SHA-256 manifests before rollback.
The original 0.4.687.1 native hashes, Fast 2 GiB 1024 KiB × 4 runtime/saved
profile and apps/service were restored and checked. Defender/Search remain
disabled. The temporary snapshot and new VM test/evidence files were removed;
zero snapshots remain, C:/Q: have 62.8/199.8 GB free, and the final capacity/
near-full guard passed with 315.4 GB available in the backing pool.


### First investigation: drain limits and crash ordering (October 16)

**First investigation: drain limits and crash ordering (2026-10-16).** A short
source review and reanalysis of the existing complete plan-148 timelines found
two concrete performance candidates; no new VM run or driver change was made.
`Drainer` gathers adjacent blocks, submits one lower write and waits for it.
`MaxBatchBytes`, the worker array and policy validation cap this at **1 MiB × 4**.
During the three large-file cases, the sampled physical-disk queue was exactly
4 for approximately 98%, 99% and 98% of the scored interval. Primo's queue was
mostly 8–9, with mean backing writes about 1.9 MiB versus QueueCache's 1.0 MiB.
These are total guest-disk counter observations, including metadata, not proof
of Primo's internal thread count or a physical-media queue.

| Existing QueueCache small-file case | RAM/application interval | Completed interval | Backing write operations | Mean backing write | Time sampled queue was 4 |
|---|---:|---:|---:|---:|---:|
| `drain-small-r2` | 1.542 s | 6.400 s | 889 | 341.9 KiB | 74.5% |
| `drain-small-r3` | 1.111 s | 14.619 s | 918 | 331.2 KiB | 92.2% |

The slow case admitted its writes faster, transferred essentially the same
296.9 MiB including metadata, and performed only 3.3% more backing writes.
Its busy queue shifts investigation toward lower-path latency/concurrency;
it does not identify a Ceph cause or exclude driver effects. Queue percentages
weight each observed depth until the next sample and clip to the scored UTC
interval; the 100 ms sampling cannot resolve short gaps. Source runs are the
same published Direct/QueueCache/Primo runs above; no incomplete samples were used.

**Candidate order for #15:** compare 4 versus 8 outstanding drain writes and
1 versus 2 MiB gathers as separate factors, retaining the present defaults until
qualified. Both require a scoped driver/management/verification extension;
they are not currently accepted CLI settings. Check lower-device transfer,
scatter/gather and alignment limits before issuing larger requests
([Microsoft adapter descriptor](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddstor/ns-ntddstor-_storage_adapter_descriptor)).
If small writes remain fragmented, inspect which gaps, partial-sector versions,
in-flight overlaps or oldest-block selection terminate batches; a bounded
address-aware picker is a later candidate. Do not fill gaps by writing unknown
sectors. Broad lock/copy rewrites are lower priority while the lower queue stays
occupied; existing timing counters can distinguish selection/copy/retirement
from lower waits in a separate diagnostic, with scored runs timing off.

**Candidate direction for #13:** Fast's `IRP_MJ_FLUSH_BUFFERS` branch returns
success without a durable barrier or an ordering marker, and `QC_SLOT` has no
flush-group identity. Pending writes to an unpinned, non-in-flight block can be
coalesced across those acknowledgments. A candidate is to retain flush and
write-through boundaries as dependency groups, preserve required older block
versions, and make a predecessor group durable before writing dependent blocks.
This could preserve RAM-first acknowledgment while delaying destination writes;
it is an unproven design direction, not a crash-safety guarantee. Merely sorting
by address, retaining arrival order, or tagging the latest block version is not
enough to establish the required journal dependencies. Persistent redo/recovery
logging is a fallback with extra I/O and boot-recovery work, not the first small
optimization (rejected on 2026-10-25; see
[KNOWN_ISSUES](KNOWN_ISSUES.md#fast-mode-without-write-ordering-can-damage-the-file-system-in-a-crash)). Passing every Fast flush through as Strict changes the volatile
contract and is not an incidental fix.

Keep the next step narrow: a short lower-I/O diagnostic, then a maintained,
versioned 2 × 2 batch/concurrency experiment with three repetitions of both
existing file shapes (24 cases), hashes and restoration. A fresh full competitor
refresh is unnecessary for screening. For crash safety, first audit one
journal/dependent-write ordering sequence and its overwritten versions; only
then design a targeted partial-drain cut. These are proposals, not scheduled or
completed runs; neither issue is resolved by this inspection.

### Individual VM experiments (plan 149)

**Individual VM experiments (plan 149, 2026-10-16).** Branch
`experiment/drain-batch-concurrency`, tested source `b417470`, CI build
**0.4.749.1**. Raise the experimental ceilings to 2 MiB and eight drainers;
keep the 1024 KiB × 4 defaults. Native and managed bounds, compile-time policy
checks and the existing `compare-drain` profile validation were extended together.
The file workload, payload and timing endpoint are unchanged from plan 148.
The lower adapter reports 2,097,152 maximum transfer bytes, 513 physical pages
and alignment mask 3. Every arm uses this same build, including its larger fixed
staging reservation (16 MiB), and the same temporary snapshot.

| Runtime arm | New 1 GiB + drain MB/s, median (range) | New 4,096 × 64 KiB + drain MB/s, median (range) | Mean backing write KiB, large / small |
|---|---:|---:|---:|
| Starting control, 1024 KiB × 4 | 179.57 (153.10–185.13) | 98.82 (80.13–102.23) | 1004.8 / 737.9 |
| More concurrency only, 1024 KiB × 8 | 213.58 (211.81–222.09) | 106.54 (94.70–110.86) | 1007.7 / 333.2 |
| Larger batches only, 2048 KiB × 4 | 225.02 (218.36–232.23) | 103.00 (98.49–107.79) | 1986.7 / 298.3 |
| Ending control, 1024 KiB × 4 | 170.68 (160.01–179.52) | 91.45 (87.04–93.34) | 1012.5 / 188.1 |

All four full six-case runs completed with restoration: **24 workload cases and
49,164 unbuffered file hashes passed**. Each row has three repetitions per shape.
The four comparison workers took about **4 minutes 17 seconds** combined; CI,
deployment, correctness checks, reboots and the required 15-minute post-boot
settle were additional setup time. The maximum-settings 2048 KiB × 8 Eager
file-integrity smoke also passed under Driver Verifier, including repeated
overwrites, live reads, flush/reopen and copy hashes. File TRIM is unsupported
and broader race/fault/lifecycle paths are outside that smoke. Actual loaded
hashes matched the CI artifact; performance runs used Verifier and timing off,
Fast 2 GiB Idle, flags 15, normal priority, Defender/Search disabled and the
background apps/service stopped. Saved profiles were unchanged.

After exporting and verifying all evidence, the original 0.4.687.1 module hashes,
Fast 2 GiB 1024 KiB × 4 runtime/saved profile and original apps/service were
restored and checked. Defender/Search remain disabled. The temporary snapshot
was removed; no snapshots remain, and the final capacity/near-full guard passed
with 148.3 GB free.

Nine cases recorded 8–64 KiB newly dirty between the scored zero-pending endpoint
and untimed Disable; their source is unattributed. Disable drained it and every
case's final paused observation was clean. One closing-control pause also
exercised the transient-Draining observation wait and then confirmed clean,
without replaying Disable. As with plan 148, the hashes validate the later paused
state, not the exact earlier scored instant or power-loss durability.

**Screen decision:** both individual changes merit follow-up for large files.
Eight drainers improved the median **18.9% / 25.1%** versus starting/ending
controls; 2 MiB batches improved it **25.3% / 31.8%**. Every candidate large-file
sample exceeds both control ranges. The controls drifted **−4.9% large / −7.5%
small**, and arms were grouped, not alternated pairs. Small-file median gains
were only **7.8%** (eight drainers) and **4.2%** (2 MiB) against the starting
control, with overlapping ranges and substantially varying backing-write sizes.
Do not qualify either small-file gain as stable or describe this as a causal
percentage against the released driver. The 2 MiB arm doubled mean large-file
write size but did not increase mean small-file write size.

The first candidate selected from this screen was **2 MiB × 4**. The later
[decision screen](#drain-decision-153) completes 2 MiB × 4/8 and fresh provider
controls; broader Strict/sustained/default qualification remains unrun.
Ceph retained hosts/OSDs-down and inactive/stale-PG warnings. Capacity
and near-full guards passed, but shared-storage behavior remains an uncertainty.
Even today's controls are much faster than the earlier plan-148 QueueCache
52.71 / 41.71 MB/s, so these observations cannot replace the matched three-product
comparison or establish Primo parity. No crash-safety change or power cut ran;
[#13](https://github.com/devedse/QueueCache/issues/13) and
[#15](https://github.com/devedse/QueueCache/issues/15) remain open. No production
default change or full release qualification is justified by this screen alone.

**Allocation blocker found after this screen, now fixed:** the larger fixed staging
reservation was 16 MiB even at the default runtime settings. `Configure` then
switched to it at a 16 MiB cache budget, allowing its unsigned slot-count
subtraction to underflow. Native 0.4.756.1 fixes it, with fourteen small-budget
Fast/Strict checks under Verifier described below. The earlier 2 GiB measurements
did not exercise this allocation edge. Further ceiling
increases also need bounded staging and selection storage: the current design
would reserve 256 MiB for sixteen 16 MiB workers, and its local `selected` array
would grow from 2 KiB to 16 KiB on the kernel stack. These are source-derived
costs, not measured failures. On this adapter, 4–16 MiB gathers also exceed the
reported 2 MiB single-transfer limit and require a deliberate split strategy.
The later read/drain diagnostic found a foreground-read penalty at eight drainers;
the decision screen attributes most small-file batch endings to missing dirty
neighbors. Further ceiling increases are therefore deferred in favor of gap
classification and read-aware concurrency. Mixed foreground latency under Idle,
broader Strict/flush correctness and sustained checks must precede a new default.

Exact completed run suffixes (each prefixed `QueueCache-Verify-`): starting
control `20261016-132412-6209369546514c2f974d041502ca6979`; eight drainers
`20261016-132619-c2e501747076483999a489b541daa22d`; 2 MiB batches
`20261016-132900-e8107338c2094977b5890ac873ffd9a8`; ending control
`20261016-133018-7b62779ae82c43aea3c25c11d75550cf`. Verifier smoke:
`20261016-130550-0a059f72bc3e431cba1e18f3d290bcb3`.

## Fix branch verification (#14 / #15)

The branch overlaps ordinary read misses, preserves each read's original pin
identities, and defaults new tasks to Eager draining with 2048 KiB batches, eight
drainers, read-aware limit 2 and TRIM mode 3 (1024 KiB × 4 Idle from 0.4.687.1 until plan 164). Saved
profiles retain their settings. Misses spanning more than 256 cache blocks
(1 MiB when aligned), or arriving when the offload table is full, use the ordered
worker.

| Area | Qualified evidence | Limit |
|---|---|---|
| Read misses (#14) | Latest complete 24-case same-build screen: cold random 9.95×, sequential 3.13×, every cold p99 improves; no resident pair loses 3%, and resident misses/lower reads are zero | Both arms use the new pool and queue order; this isolates miss offload, not every old-driver difference |
| Equal-work read tails | Complete 24-case, 100,000-operation comparison: cold-random p99.9 191.7 → 60.7 ms; median case maximum 271.6 → 123.5 ms; every cold pair improves | Fixed-duration maxima sample unequal amounts of work; backing-store profile and pool size were not varied |
| Drain settings (#15) | Complete timing-off 512 MiB comparison: 12.7 → 8.0 s, every pair improves; separate cold-read/write-drain comparisons also improve | Qualifies explicit drain time, not disk-speed parity, general foreground throughput or Strict impact |
| Short completed writes | Plan 148: all 18 provider cases, 36,873 file hashes, zero pending/disk queue and restored state | New files fitting in cache; no new native code, sustained/Strict qualification or power-loss test |
| Experimental drain ceilings / gather | Plans 153/154 retain the earlier provider screen. Later plans 155/156 complete paired current/aligned selection: 227.95/219.98 MB/s large, 111.85/93.58 small, 12 performance cases / 24,582 hashes and bypass proofs; separate Verifier matrix passed. Aligned policy rejected/removed, diagnostics retained. | Defaults unchanged. Fresh same-session Primo is needed; current request sizes changed substantially. Strict / default Idle foreground / release qualification unrun. The Primo post-oracle path ran on Primo in plan 158; the retired-policy native build was not separately VM-exercised. |
| Matched provider research | Plan 158: latency-matched, default 96.5 / 59.0 / 92.6 and Eager 2 MiB × 8 141.3 / 173.3 / 125.5 against Primo 120.5 / 59.9 / 126.9 MB/s. Plans 159/160 same-window A–B–A: Primo 285.5 / 70.4 / 315.8, Eager 2 MiB × 8 304.9 and 298.3 / 142.6 and 132.2 / 294.1 and 288.9, default 184.1 and 170.0 / 86.6 and 80.0; 22 runs / 233,538 hashes | Configuration, not defaults; large-file ranges overlap; sustained 7–9% behind; release/Strict qualification unrun |
| Read during drain, provider-neutral (plan 161) | A–B–A against PrimoCache: concurrent disk reader 163 IOPS under Primo, 277–331 under Eager 2 MiB × 8 and 959–1,153 under the default, at drain rates of about 360 / 360–420 / 250 MB/s; 10 complete runs | One Ceph-backed VM; uncached Q1 reads alone were 20–35% slower than Primo's |
| New default, same-window PrimoCache (plans 164–167) | Native 0.4.776.1, Eager 2 MiB × 8 with read-aware 2 and TRIM mode 3 as shipped. A–B–A at matched anchors: 292.4 / 90.7 MB/s against Primo 261.0 / 52.5; disk reader 757 against 170 IOPS; 12 runs, 110,619 hashes. Under Verifier: `trim-cache` 12/12, focused campaign 98/98, integrity 12,291 hashes. `release-performance` on the final build: 11/11 phases, 106/106 cases, restored (plan 171); its RAM-path rates were host-bound that day and matched by a same-window 0.4.687.1 control. Block C repeat: 301.1 / 74.9 MB/s. | One Ceph-backed VM; block C on a degraded backend; 8 GiB sustained not repeated; Strict boundaries under the new default unmeasured |
| Read-aware limit and asynchronous TRIM (plans 162/163) | Lab switches on native 0.4.771.1. Read-aware 2 keeps a concurrent disk reader at 1,031–1,141 IOPS (p99 10–11 ms) during Eager 2 MiB × 8 drains, against 340 without it, while #15 medians stay 300.3 / 122.2 MB/s with 0 gate waits; Verifier campaign 98/98 and `trim-cache` 12/12 with both on | One Ceph-backed VM, one session; default-off; the TRIM fence's holding path is unexercised through NTFS |
| Read/drain lock diagnostic (plan 160 repeat) | Complete 48/48 on native 0.4.766.1, one noisy standalone window remeasured: eight versus four drainers +15% drainage, disk reader 1,094 → 545 IOPS, p99 15.4 → 28.5 ms | Same Eager screen; reproduces plan 151's magnitude; read-aware limit unbuilt |
| Read/drain lock diagnostic | Plan 151: 48 windows; at 2 MiB, eight versus four drainers raised drainage about 34% during disk reads but reduced reader IOPS 56%, p99 13.684 → 24.473 ms; global lock held about 1% | Short Eager screen with write retention off and separate pure RAM/disk paths; no Idle-default, Strict or sustained qualification. Storage scheduling is the first follow-up; a locking speedup is unproven. Exact methods, variability and excluded setup failure are in [findings](PERFORMANCE.md). |
| Correctness | Qualification includes 98 Driver Verifier checks, the 37-case concurrency matrix with 24 zero-lower-I/O RAM-only windows, and the October 14 release campaign's 11 phases / 106 cases / 93 retained correctness checks | Exact builds, loaded hashes, byte oracles, telemetry and run identities are in [performance findings](PERFORMANCE.md) |

The experimental branch now bounds drain staging at small cache budgets and
checks the remaining allocation before unsigned subtraction. This fixes the
source-reviewed 16 MiB underflow introduced by the larger experimental staging
ceiling. Plan 152's fourteen small-budget Fast/Strict byte-oracle checks passed on native
0.4.756.1 under Driver Verifier, as did the maximum-settings quick file-integrity
smoke. Broader promotion qualification remains pending.

The earlier same-build Defender/Search-off drain-settings repeat has no complete matrix: extra small writes
invalidated seed/end snapshots. A failed file-handle experiment was removed in
plan 146; its failed case never reached the byte oracle. The write source is
untraced. Earlier accepted drain evidence remains valid within its recorded scope.

The October 15 release repeat stopped at 8/11 phases: map-cost case 7 recorded
six lower reads / 4,608 miss bytes in a RAM-only window, without lower writes,
flushes, draining or driver errors. Hit-byte growth exceeded scored bytes, but
the counters cannot identify the read source. Restoration succeeded. Separate
unchanged map 9/9, write 72/72 and ordering/fault 8/8 follow-ups completed and
restored; they do not turn the original campaign into a completed one.

After sustained churn, the first Q1 reread missed entirely; the following Q8
reread reached 99.750–99.988% hits but still made lower reads. These are sequential
recovery stages, not independent queue-depth controls. Natural idle left
768–897 MiB dirty with 56–70-second oldest ages: the 5-second age starts scheduling
and cannot guarantee a drain deadline. Timing diagnostics cost about 6–10% in
cached random writes and must match between comparisons. Product comparisons and
CrystalDiskMark keep their own sessions; see [comparison](COMPARISON.md).

## Read misses and the first fix branch (#14)

### Competitor comparison: three cache weaknesses (plan 121, 2026-10-13)

**Question.** How does QueueCache compare with other RAM disks and RAM caches on the
same VM, and what does the comparison show about QueueCache itself? Full results:
[COMPARISON.md](COMPARISON.md) (generated from the `compare-*` suites, plan 121;
products installed one at a time on restored snapshots, measured by tools that use
no QueueCache feature).

**RAM disk.** QueueCache leads single-program large reads (SEQ1M Q8T1 45,245 MB/s;
next SoftPerfect hard-disk emulation 35,386) and ties at Q1T1 with SoftPerfect RAM
File System (about 25,100). Small requests sit in the leading block-device group
(RND4K Q1T1 1,611 / 1,234; OSFMount logical 1,744 / 1,390). SoftPerfect RAM File
System is a file system, not a disk, and wins small requests and the file tree by
a wide margin. No RAM-disk decision changes.

**Cache (2 GiB on Q:, against the bare disk, PrimoCache 4.4.1 L1 2 GiB).** Cached
data is fastest with QueueCache (hot 1M Q8 39,487 / 21,214 MB/s Fast; PrimoCache
17,172 / 19,036). Three measured weaknesses:

| Finding | Evidence | Cause | Candidate |
|---|---|---|---|
| Read misses do not overlap | 1 GiB first read 217 MB/s Fast / 187 Strict, bare disk 560, PrimoCache 512 / 409; cold RND4K Q32 11 MB/s against 65 bare and 72-80 PrimoCache. QueueCache equals the bare disk at Q1 (230 MB/s, 2,949 IOPS). | The request worker reads each miss synchronously (`StagedRead`/`OriginalIo` on the worker), so one miss is in flight per volume. | Several misses in flight (asynchronous lower reads with ordered completion), keeping the driver-owned staging buffer and the pins. |
| Drain uses small writes | After a 1 GiB burst: 10 s at 105 MB/s (PrimoCache 7 s at 149). 8 GiB sustained write 178 MB/s against 242 bare and 486 PrimoCache. During it the disk received 256 KiB writes at 129 MB/s; PrimoCache's averaged about 1.4 MiB at 366 MB/s, DiskSpd's 1 MiB Q8 215 MB/s. | Default 256 KiB batches with drain parallelism 2. | Measure `--batch-kib 1024` and `--drain-parallelism 4` (existing settings) with `compare-cache`; change the defaults only for a repeated gain. |
| Strict stalls all writes behind whole-cache drains | Rerun: 6 of 12 hot write windows completed nothing in 10 s; creating 40,000 files 213 s (bare 32), 8 GiB write 53 MB/s with a 0 MB/s slowest second (bare 242), write-through 1M Q8 52 MB/s (bare 251), flush after 512 MiB 12.6 s. Probe on the same VM (cache flushed first): RND4K Q32 then Q1 writes completed 0 operations in their windows and needed 106 s and 168 s to finish; each window received one write-through write and two flushes from NTFS (`StrictWriteBarriers` +1, `ApplicationBarriers` +2). Random dirty data drains at about 10 MB/s (bare RND4K Q32 write 10.7 MB/s). | Strict admits writes to RAM at RAM speed (about 1 GB during a 2 s warm-up), then honours each write-through write or flush with `QcCacheBarrier`: wait for `DirtyBytes == 0`, lower flush, while the ordered worker holds every other write. | Bound Strict's dirty data by what the disk drains in a short time, so a flush finishes in bounded time, and complete a write-through write after older overlapping versions without draining unrelated dirty data. |

**Investigation (2026-10-13, issues #14 and #15).**

- *Misses (#14):* the caller path serves hits only (`Read(..., hitOnly)`); misses queue to the ordered worker, which
  waits for each lower read (`StagedRead`/`OriginalIo` -> `CallLowerAndWait`). During that wait the service lane takes
  hits and one paging miss, and moves up to 8 queued misses back to the queue head; each worker step rescans from the
  head. In every comparison window with 32 queued misses the slowest read waited 41-59 s of 60 (bare disk and PrimoCache
  under 0.5 s; median 0.7 ms, average 7-19 ms). Proposed: stop the head reinsertion, then several misses in flight
  (offload threads, at most 3, or asynchronous misses with Filling ranges).
- *Drain (#15):* each drainer copies one batch (at most 1 MiB, `MaxBatchBytes`) and waits for its lower write, so
  writes in flight equal the parallelism. `compare-cache` with only batch/parallelism changed (one repetition each,
  default first and last as a drift check): the storage became about 2.3x faster between runs 2 and 3, so only
  same-period runs compare. Same period: 1024 KiB x 4 against 256 KiB x 2 drained a 1 GiB burst at 126 against
  87 MB/s and sustained 8 GiB at 267 against 182 MB/s (slowest second 202 against 131); hot, mixed and tree results
  unchanged. Slow period: 1024 x 2 against 256 x 2 drained the burst equally (37/39 MB/s), sustained 83 against
  57 MB/s. Not yet attributable to batch size or parallelism (256 x 4 not measured; T050 found 256 x 4 no faster).
  Next: an alternated, repeated comparison (drain-decision with a batch option) before any default change.
- *Drain decision (plan 125, `drain-decision`, 42 cases, run
  `QueueCache-Verify-20261013-131647-d5f42aaebc094cf38529b3a18514968a`, installed 0.4.604.1, Verifier off; medians
  of three, batch and parallelism order reversed every other repetition).* Seconds to drain a 256 MiB backlog during
  cold random reads: 256 KiB x 1/2/4 = 3.88/2.67/3.07, 1024 KiB x 1/2/4 = 3.08/3.68/1.86. During fitting random
  writes (the flush also drains writes admitted meanwhile): 256 x 1/2/4 = 32.6/18.2/17.7 s at 14,202/14,413/13,202
  foreground IOPS; 1024 x 1/2/4 = 18.8/15.3/8.4 s at 37,342/26,412/50,859 IOPS. 1024 x 4 beat 256 x 2 in every
  write pairing (11.4/8.4/6.3 against 18.2/19.3/8.8 s) and in two of three read pairings (third equal); median p99
  was unchanged. Foreground write IOPS improved in two pairings but fell 3.7% in the first, and the flush-time
  ranges overlapped. This exploratory decision does not meet the separated-range acceptance bar by itself;
  final-build qualification remains required. 256 x 4 alone was not faster, as in T050.
  **Branch defaults changed to 1024 KiB x 4** (plan 125).

**Fix branch qualification history (plans 126–132).** The initial concurrent-miss
candidate built successfully but needed a correctness fix before acceptance. Review found that using the
old worker path without pin scratch allowed one overlapping miss to unpin a slot
filled by another read. Every read now saves its original pin identities, and the
maintained partial-read suite checks simultaneous cold/partial readers and clean
entry eviction. The first cache-concurrency attempt on 0.4.673.1 failed its
RAM-only guard (36 lower reads, 27 KiB of misses); restoration succeeded. The tray
was left open, so the failure needs a clean repeat and cannot be counted as a
passing correctness or performance result. The completed same-build comparisons
and concurrency matrix follow. Final release qualification on 0.4.687.1 is
complete; the file-tree and latency-tail questions are resolved below, and the
foreground-interference and Strict-impact questions remain unmeasured.

**Read-miss speed screen (plan 127).** Signed 0.4.677.1 completed all 24 windows
of `cache-read-misses`, three alternated repetitions of flags 7/15, timing off,
20-second scores, 2 GiB cache, a 4 GiB cold file and 1 GiB resident controls
(`QueueCache-Verify-20261013-182938-2fbb3355818244b18228ff848afe88c2`). Both modes
use the branch's 16-thread pool and corrected queue order; flag 8 isolates ordinary
miss offload. This is a same-build comparison, not a comparison against the entire
older driver. The screen returned ACCEPT_SCREEN and restoration succeeded.

| Read shape | Serial misses, median MiB/s (range) | Offloaded misses, median MiB/s (range) | Median change | Median p99, serial / offloaded (ms) |
|---|---:|---:|---:|---:|
| Cold random 4K Q32 | 5.55 (4.44–8.53) | 33.27 (28.22–103.18) | +499.9% | 100.664 / 34.456 |
| Cold sequential 1M Q8 | 177.85 (167.28–337.55) | 612.10 (597.15–1009.70) | +244.2% | 160.428 / 121.793 |
| Resident random 4K Q32 | 1653.06 (1646.02–1661.38) | 1636.53 (1621.46–1642.49) | −1.0% | 0.104 / 0.106 |
| Resident sequential 1M Q8 | 37053.70 (36948.05–37118.54) | 36604.50 (35928.65–37096.05) | −1.2% | 0.430 / 0.442 |

Every cold pair improved, and the candidate ranges were above the serial ranges.
Every cold p99 improved. The largest cold-read samples were higher in some
candidate windows: 376.696 ms versus 300.564 ms for random reads, 731.899 ms
versus 602.090 ms for sequential reads. This does not qualify worst-read latency
as improved. All resident controls recorded zero misses and zero lower reads;
one sequential control pair lost 3.04%, while the other two lost 0.93% and 0.06%,
so there was no repeated 3% control loss. The loaded filter hash was
`B7AA752B6F21E00FF8A2027B9DBA679E14DFF6D5A6186CE9BD81820BAB130F73`, provider
`0FDD5279B96D8B20D416FD5C509C7C90D66A3D5299A1F98EFF5AC88E2DCA651C`, DiskSpd
`7281BF6DA6C03797016EDDF2E8AAEC4C644AE893D403D57A030B7E2E14B61079`.
The final release regression remains required.

**Concurrency repeat, plan 128 (incomplete).** On signed 0.4.679.1, all four
first-repetition resident-read shapes passed, including four readers at Q2 that
failed in the initial 0.4.673.1 attempt. The first RAM-only write window then
recorded three lower reads / 24 KiB of misses. All 141,410,959,360 scored write
bytes were admitted; lower writes, lower flushes and drained bytes remained zero,
and identity/health stayed stable. This is consistent with writable-file
metadata activity, rather than lower I/O for payload admission, but the strict
window correctly failed. Plan 129 primes the writable DiskSpd profile before
the quiet wait and measurement boundary. The zero-lower-I/O guard is unchanged.
The incomplete 6/37 run (`20261013-230517-9cd6a418dc51416c9ba8611511f3ba91`)
restored successfully and supplies no complete matrix or performance aggregate.
The focused plan-129 repeat completed 2/2 selected cases and restored
(`20261013-233136-2fe8cbd5ec9244a09b0c8cf685caf66d`, managed runner `d0372d3`
against the same signed 0.4.679.1 native driver). The write score completed
144,997,089,280 bytes, all admitted; lower read/write/flush attempts, misses and
drained-byte deltas were all zero. This qualifies that fixture correction for the
affected window, not the complete 37-case concurrency matrix. The completed full
repeat on the CI package is recorded below.

The full 0.4.681.1 repeat completed the four read shapes and first RAM-only write
window, whose lower read/write/flush attempts, misses and drained-byte deltas
were zero. The next background-drain write score stopped on a 9.84-second
telemetry gap; the two-second coverage guard remained intact. The 7/37 run
(`20261013-235750-a2aa7d221f8740948a568a844368d11f`) restored the original
256 KiB × 2 profile cleanly, with zero errors. Its gap prompted investigation;
no aggregate or complete concurrency qualification is inferred from it.
The unchanged focused background-drain repeat completed 2/2 selected cases
(`20261014-001223-b6ada1ceaea74be0ba911244aa7b1392`), restored cleanly, and
recorded 51 score-window samples with a maximum gap of 0.313 seconds. The
9.84-second gap was not reproduced; its individual cause remains unproven.
That repeat completed the first 12 read/write windows, then stopped during the
second repetition on a 3.04-second telemetry gap in a resident-read window
(`20261014-001931-d4c4bb8d868c4d6187ba49c264af77f1`, 15/37, restored). In both
failed runs the UTC and monotonic gaps agreed, excluding a clock adjustment.
Plan 130 records observer-stage timings to locate the actual delay; coverage
requirements remain unchanged. Neither incomplete matrix is aggregated.
The instrumented plan-130 run reached 20/37 cases, then stopped on a
3.367-second gap (`20261014-005352-afae0a6dbf324a669460071287c75c4a`, signed
0.4.681.1 native driver with the standard CLI built from clean `f9f4c89`). The
next sample recorded 3147.796 ms spent serializing/persisting the previous sample
and 214.456 ms awaiting its delay; driver queries took at most 2.163 ms. The gap
began before the scored process, across the readiness boundary. Restoration was
healthy and clean. Plan 131 separates the actual sampling thread from
serialization/persistence through a bounded 256-sample buffer. It still rejects
real coverage gaps and buffer exhaustion, publishes readiness after the first
persisted sample, and waits for all observations to be written. Host contracts
cover blocked persistence, order, overflow and failure ownership; VM validation
of this correction is complete for the concurrency and read-miss matrices and
the drain comparison.

**Final-build concurrency (plan 131).** Signed 0.4.685.1 completed all 37 cases
(`20261014-041653-4cf5d812f35744c1acf35af6647fa7ed`). All 12 resident-read and
12 fitting-write windows required zero lower read/write/flush attempts, misses
and drained bytes and accounted for every scored byte; all passed. The other
12 windows exercised writes with background draining. Every actual telemetry
interval covered its process window; the largest UTC/monotonic gap was
0.215076/0.215075 seconds. Writer persistence reached 247.289 ms while capture
stages took at most 3.892 ms. The original 2 GiB Fast/Idle 256 KiB × 2 profile
was restored with zero errors, dirty or in-flight bytes. Full raw evidence and
XML byte totals were reviewed; no samples from incomplete runs were aggregated.
The final cold-read screen, complete drain comparison and release campaign below
are finished. The refreshed product comparison is recorded below.

**Final-build read-miss screen (plan 131).** Signed 0.4.685.1 completed 24/24
windows with the same budget, file sizes, alternated three repetitions and
20-second scores as the plan-127 screen
(`20261014-044032-7009cf8578ec4b178648b13f18e38a2e`). The result was
ACCEPT_SCREEN. Both arms still use the 16-thread pool and corrected queue order.

| Read shape | Serial misses, median MiB/s (range) | Offloaded misses, median MiB/s (range) | Median change | Median p99, serial / offloaded (ms) |
|---|---:|---:|---:|---:|
| Cold random 4K Q32 | 10.44 (9.72–11.69) | 98.05 (55.03–98.98) | +839.7% | 19.080 / 7.374 |
| Cold sequential 1M Q8 | 340.50 (338.83–342.80) | 1140.58 (1140.08–1141.20) | +235.0% | 49.879 / 28.576 |
| Resident random 4K Q32 | 1621.66 (1587.31–1635.31) | 1618.97 (1610.94–1629.27) | −0.17% | 0.106 / 0.107 |
| Resident sequential 1M Q8 | 36623.09 (36562.55–36952.67) | 36770.55 (36739.15–36800.40) | +0.40% | 0.451 / 0.446 |

Every cold pair improved, candidate throughput ranges were above the serial
ranges, and every cold p99 improved. No resident pair lost 3%; all resident
windows had zero misses and zero lower reads. The largest random-read sample
increased from 46.219 to 568.738 ms; sequential maximum decreased from 60.706 to
41.321 ms. This accepts throughput and p99, not a blanket worst-latency gain.
The largest telemetry gap was 0.258 seconds. Original settings and copy flags
15 were restored cleanly. All raw XML byte totals and telemetry intervals were
reviewed. Loaded filter:
`894509CAB30A6CE4856B4837905A62AD2FC835C808B43199C663897429658891`;
provider: `7ABCE03E9CBB7ABEB0BF816BA427865150D604EDEDCD43484EA40FE1599EB579`;
DiskSpd: `7281BF6DA6C03797016EDDF2E8AAEC4C644AE893D403D57A030B7E2E14B61079`.

**Fixed-operation read-tail comparison (plan 141, 2026-10-14/15).** The earlier
tails (maximum 568.738 ms versus 46.219 ms) came from fixed 20-second windows in
which the faster arm completed 9× more requests, so the two arms did not sample
equal work. `read-tail-decision` runs the same seeded 100,000-operation address
sequence in each arm. The complete 24/24 run
`20261014-230805-92a9f1ac4191423cbd05b571db4595b2` (controller 0.4.716.1 at
commit `f29c784`, loaded 0.4.687.1 filter `FE00891A…`, provider `0B13C2F5…`,
timing off, 2 GiB Fast, 256 KiB × 2, Idle draining) completed with every start
snapshot clean, all 24 latency summaries recomputed independently from the
100,000 raw samples, six durable write oracles verified, and the original copy
flags 15 read back after the run. Medians (range) over three order-alternated
pairs:

| Workload | Flags 7 | Flags 15 |
|---|---:|---:|
| Cold random 4 KiB Q32, IOPS | 1,547 (1,518–1,568) | 10,675 (9,732–11,097) |
| Cold random p99 / p99.9 / max, ms | 62.0 / 191.7 / 271.6 | 24.4 / 60.7 / 123.5 |
| Cold random >50 / >100 / >250 / >500 ms (per repetition) | 2100·1552·1367 / 457·387·297 / 15·0·37 / 0 | 210·161·149 / 1·2·25 / 0 / 0 |
| Resident random, IOPS | 189,025 | 179,393 |
| Resident sequential 1 MiB Q8, IOPS | 31,902 | 32,756 |
| Fitting foreground write, IOPS | 191,012 | 185,158 |

Every cold pair improved throughput (+541%, +617%, +581%), p99.9 (−92 to −148 ms),
maximum (−119, −148 and −230 ms) and every threshold count; no operation in any
arm exceeded 500 ms, and both arms issued the same number of lower reads (95,336
to 95,339). The 568.7 ms maximum was therefore a fixed-window artifact, not a
property of equal work. The resident and write controls had zero misses, zero
lower reads (writes: no throttle waits) and tails of at most 6.7 ms. Their
throughput differences followed run order, not flags: in all three
resident-random pairs the arm that ran first was faster (−7.2%, +5.2%, −8.5% for
flags 15), and the resident-sequential (+2.9%, +3.3%, −3.7%) and write
(+0.8%, −6.5%, +2.5%) pairs have no consistent direction. Resident reads never
reach the miss path that flag 8 controls, and the plan-131 DiskSpd screen showed
no resident loss (−0.17%). The pairs were not rerun in an order-balanced design.

Limits: two earlier incomplete runs on commit `c64b954` collected 15 and 13 cases
before stopping (a stray 32 KiB metadata write in the start snapshot while
Windows Search ran; a 16 KiB write still dirty in the post-flush snapshot); they
are excluded from the decision. Their cold-random samples are directional only:
flags 15 finished 100,000 reads in 2.7–3.9 s (p99.9 3–5 ms) and flags 7 in
40–42 s (p99.9 38–145 ms). The final run was slower in absolute terms (flags 15
9–10 s; flags 7 about 64 s) because the backing disk was slower that hour
(draining 100,000 scattered 4 KiB writes took 251–258 s against 74–137 s
before), so latencies are not comparable across sessions, only within a pair.
The first attempt on `f29c784` stopped INCOMPLETE at 7/24 when the post-write
Flush exceeded the default 180-second preparation deadline; the complete run
used `--preparation-flush-seconds 900`. Plan 141 adds a bounded two-second quiet
wait before each start snapshot; it was never needed in the final run (every
case's first check passed) while the same wait before the durable oracle needed
a second attempt in five write cases. The pool size (16 threads) was not varied:
no result implicates it, so no executor-limit experiment was run.

**Drain completion budget (plan 132).** The first final-build 42-case drain
repeat stopped at 27/42 (`20261014-045632-97825e31882149d89fbb3ecd56f3cbb2`).
The generic ten-second-score plus 120-second process deadline expired while
the parallelism-1 administrative flush was still draining small, scattered
writes. Telemetry showed 589.8 MiB drained in 129.9 seconds, 27,365 lower-write
attempts and zero driver errors, with 8.6 MiB still dirty when its old observer
duration expired. Restoration succeeded. No aggregates from that incomplete
run are used. Plan 132 gives this diagnostic an explicit 300-second completion
allowance and an observer lasting beyond process completion and readiness.
The scored duration, workload, complete 42-case matrix, coverage limit and
persisted-byte checks are unchanged. Host contracts reject the old truncated
interval and real sample gaps. The corrected complete run is recorded below.

**Final-build drain comparison (plan 132).** All 42 cases completed
(`20261014-053536-741edee09d044ee7b751ef2e8162c487`, standard CLI from clean
`4b0e3c6`, signed 0.4.685.1 native drivers with the hashes above). This is one
complete run: three alternated repetitions, a 1 GiB cache, deterministic 256 MiB
backlogs and ten-second foreground scores. Every one of the 36 seeded backlogs
was verified from disk. All raw XML byte totals and actual telemetry intervals
were checked; the largest sample gap was 0.320 seconds. The original 2 GiB
Fast/Idle 256 KiB × 2 profile was restored with zero errors or pending bytes.

| Measurement | 256 KiB × 2, median (range) | 1024 KiB × 4, median (range) |
|---|---:|---:|
| Flush during cold reads, seconds | 6.517 (6.298–7.613) | 3.863 (3.846–3.868) |
| Foreground cold reads, IOPS | 514.57 (476.00–596.81) | 1157.04 (988.31–1268.10) |
| Cold-read p99, ms | 4.810 (4.058–7.053) | 3.808 (3.533–4.860) |
| Flush during fitting writes, seconds | 101.153 (40.294–114.397) | 88.727 (29.099–90.784) |
| Foreground fitting writes, IOPS | 15797.40 (14109.60–38916.48) | 14537.66 (14163.00–37758.40) |

Cold-read flush time improved 40.7%, with every pair improving and separated
ranges. Every foreground cold-read pair and p99 improved too. Fitting-write
flush times improved 12.3%, 27.8% and 20.6% in their pairs, but their ranges
overlap. Foreground write changes were +3.0%, −2.98% and −10.35%; this does not
establish a fitting-write throughput gain. These flushes include additional
writes admitted while draining (593–742 MiB total, not a fixed 256 MiB alone).
Normalized drain throughput improved in every write pair, with no capacity waits.
The write-only investigation follows; no selected cases are mixed into this
42-case result.

**Write-drain repeat (plan 132).** The maintained `fitting-write-b` selection
completed all 18 selected cases, three alternated repetitions of every batch /
parallelism setting (`20261014-071942-0428cc322079410ca84c019dd44983eb`), on
the same CLI and native drivers. All 18 seed oracles matched from disk; raw XML
totals and complete telemetry intervals were checked (largest gap 0.303 seconds).
Original settings were restored cleanly.

For 256 KiB × 2 versus 1024 KiB × 4, median flush time was 127.608 versus
92.157 seconds, with ranges 111.768–133.770 versus 89.589–94.871. Every pair
improved and ranges separated. Actual drained throughput was 4.692 versus
6.439 MiB/s, again with every pair improving and separated ranges
(4.420–5.379 versus 6.250–6.546). The completed write process improved in every
pair too: scored bytes divided by its full recorded process interval, including
the flush stall, gave 4.436 versus 5.918 MiB/s median, with separated ranges.
These complete observations qualify the drain-time target; they are not combined
with the earlier run's scores.

Fixed ten-second write IOPS changed −6.30%, −4.22% and +0.99% in their pairs;
the ratio of medians was −2.93%. In both complete runs all scored bytes had
already been admitted by the first observed drain progress. The interval before
that progress varied substantially because the requested flush launches an
owned control worker and observer before entering the administrative barrier.
The barrier then holds the outstanding write beyond the ten-second score.
Consequently these IOPS values do not qualify general foreground throughput or
an invariant write control; the raw drops remain recorded. No capacity waits
occurred. Retain 1024 KiB × 4 for the repeated drain-time improvement, with this
measurement limit and the unchanged explicit-flush blocking behavior. Final
product-default release checks and the historical generic comparison refresh are recorded below. A new competitor refresh is not required for these focused checks.

**Explicit-drain comparison, timing off (plan 142, 2026-10-15).** The complete
6/6 `drain-flush-decision` run `20261015-002709-cad2f47e9da14a0ba9a386a4240c607f`
(controller 0.4.718.1 at commit `587b07f`, loaded 0.4.687.1 filter `FE00891A…` and
provider `0B13C2F5…`, timing off, 2 GiB Fast, age-only Deferred) flushed a
deterministic 512 MiB dirty seed. An owned worker was ready before release and
the duration is a monotonic timer around the driver's Flush call, so process
launch is excluded. Every case drained the exact 512 MiB, left no pending bytes,
and its seed matched through unbuffered reads after the runner disabled the cache.
The byte oracle follows that additional control; it does not independently prove
the contents immediately before the disable operation.

| Setting | Flush ms per pair (1, 2, 3) | Median | Drain MiB/s | Lower writes |
|---|---|---:|---:|---:|
| 256 KiB × 2 | 12,846 · 12,713 · 11,536 | 12,713 | 40.3 | 2,050 each |
| 1024 KiB × 4 | 8,012 · 6,629 · 9,394 | 8,012 | 63.9 | 514–517 |

Every pair improved (−37.6%, −47.9%, −18.6%), the median improved 37.0%, and the
ranges separated (6.6–9.4 s against 11.5–12.8 s): the 5% target for explicit-drain
time is met on this sequential seed, consistent with the earlier cold-read flush
result (6.52 → 3.86 s). Only explicit drain time is qualified by this run.
This does not establish parity with the bare disk: its 63.9 MiB/s timed 512 MiB
Flush and the historical bare-disk 230 MB/s post-burst observation have different
workloads, timing boundaries and sessions. A matched baseline and actual backing
I/O are needed; application write completion alone can include RAM admission.

**Disabled-baseline drain repeat: incomplete (plans 144–146).** Runs
`20261015-152403-4df1148acefc487cb8f8585f98016874` and
`20261015-152550-f94d0a83a1094d84a17d653575ea537b` stopped at cases 1 and 5:
12 KiB was admitted between seed and Flush snapshots, or another 8 KiB was
accepted/dirty in the post-Flush snapshot. Plan 145 held the seed handle through
Flush/end snapshots, but `20261015-154542-30466d022cae4ed4bd7a351f60a3d451`
also failed with another 4 KiB dirty. Its drained-byte counter matched the seed
amount, but the failed case never reached its byte oracle. All three restored
without driver errors; source/precise arrival time remain untraced. Plan 146
removed the ineffective held-handle trial, retaining the original measurement
and all guards. No complete fresh drain aggregate exists.

**Read-miss repeat with Defender/Search disabled (2026-10-15).** The complete
24/24 `cache-read-misses` run
`20261015-152826-599322e9accd4e45a7aef7ae4467908e` returned ACCEPT_SCREEN and
restored the original 2 GiB / 256 KiB × 2 profile and copy flags 15. Controller
0.4.726.1 (`22463de`) used the unchanged loaded 0.4.687.1 native hashes recorded
above, the same pinned DiskSpd, timing off, 2 GiB Fast/Idle and 1024 KiB × 4
settings in both arms. Defender services/drivers and Search were disabled;
Everything, Steam and the tray were closed. Three alternated pairs gave:

| Read shape | Serial median MiB/s (range) | Offload median MiB/s (range) | Ratio | Median p99, serial / offload (ms) |
|---|---:|---:|---:|---:|
| Cold random 4 KiB Q32 | 8.802 (7.825–10.204) | 87.568 (78.166–139.892) | 9.95× | 35.199 / 8.920 |
| Cold sequential 1 MiB Q8 | 310.095 (153.073–316.542) | 970.115 (771.050–973.513) | 3.13× | 79.557 / 44.472 |
| Resident random 4 KiB Q32 | 1658.638 | 1654.824 | −0.23% | 0.105 / 0.104 |
| Resident sequential 1 MiB Q8 | 36850.000 | 36635.282 | −0.58% | 0.440 / 0.443 |

Every cold pair improved throughput and p99, and throughput ranges separated.
No resident pair lost 3% (largest loss 2.12%); resident controls had zero misses
and lower reads. All 24 raw XML byte/operation totals and actual telemetry
coverage were independently checked; the maximum gap was 0.215 seconds.
Fixed-window random maxima were 111.543 / 175.209 ms, sequential maxima
385.328 / 268.295 ms. These sample unequal amounts of work; the retained
100,000-operation comparison above addresses equal-work tails. Absolute scores
across sessions cannot isolate an effect from disabling Defender/Search or a
new driver speedup. This repeat confirms the existing #14 improvement.

**New-environment release campaign (2026-10-15, incomplete).** Controller
0.4.730.1 (`d1cf8d2`, plan 146), unchanged native 0.4.687.1 and pinned DiskSpd
ran `release-performance` in
`20261015-160320-a9813078abb44a13acb482133b8f53b4`. The first eight phases
completed, including all 12 recall cases and six five-minute sustained cases.
Map-cost case `0007-r3-random-read-s1-q8-drainFalse-map0` then failed the RAM-only
guard: six lower reads / 4,608 miss bytes, zero lower writes/flushes, zero drained
bytes and zero driver errors. Hit bytes increased by 15,433,596,928 against
15,433,564,160 scored bytes. The first extra read was observed about five seconds
into the score. This is consistent with metadata activity, but its source was
not traced; it is not a passing RAM-only window. The campaign stopped at 8/11
phases / 31 recorded cases and restored successfully. No complete campaign
performance verdict or aggregate is produced. A focused unchanged map-cost
repeat completed all nine cases in
`20261015-173103-968c4d0558e14a24ad3a223508cfacf0` and restored, without changing
the guard or reproducing the lower reads. This does not identify their source.
The separate `write-performance` run
`20261015-202536-cb295eda46644df2bef04a4b7020c5ba` completed all 72 cases in
31 min 45 s and restored. All 72 raw XML byte/operation totals and full process
telemetry intervals were independently checked (maximum gap 0.215 s; no
zero-completion scores). These are collected write scores, not a new accepted
speedup comparison. The separate `ordering-faults` run
`20261015-205732-73bca262a5374343af046e87a9f85525` completed all eight checks and
restored in 30 seconds. Its backing Q: cache was paused for the lab VHDX test
and resumed with its original settings afterwards; Q: stayed clean with zero
errors. Lab V:'s three deliberate lifetime-error increments were accounted for,
with no remaining active fault. These independent suite results cannot turn
the original campaign into a completed one.

**Foreground interference and Strict impact: unmeasured (plans 142/143).**
Both proposed suites failed at their first seed twice and were removed in
`206c5e8`. They supply no performance result:

| Fixture | Failed runs | Why it cannot answer the question |
|---|---|---|
| `drain-overlap-decision` | `20261015-003236-c69bd0f31004430dabe56f4cc0a4c494`; `20261015-004735-c183d0de9ee34efdb9e9fe051c23679f` | A quiet wait removed an initial 20 KiB dirty start. The second 75% seed took about 7 s, exceeding the default 5 s dirty age: 238 lower writes drained about 61 MB before measurement. A no-drain seed contradicts those options. |
| `strict-impact-decision` | `20261015-005332-2141cd24785c42e9bca1ebaf931cb810`; `20261015-011121-03fa9bfa90cb4f6c94b5270a8a2cd861` | In the second attempt, 2 application flushes and 1 write-through write (not issued by the seed worker) caused 2 application barriers, 1 Strict barrier, 3 lower flushes and 1,072 drain batches. Of 536,890,368 accepted bytes, 280,502,272 drained and 256,388,096 remained dirty. Sources are unattributed. |

The driver refuses option changes while enabled or dirty
(`IOCTL_QCACHE_OPTIONS_V1`, `driver/qcache/writecache.cpp`), so seeding under
held-off options and then switching to Idle is unsupported. Overlap needs a
smaller/faster seed or a native capability change. Strict's result exposes its
known whole-cache barrier cost (#16), not a regression of the new defaults;
there is no measured Strict boundary latency.

**Final release campaign (plan 132).** Signed 0.4.687.1 from functional commit
`4b0e3c6` completed `release-performance` in 7918.2 seconds (2 h 12 min), all
11 phases / 106 outer cases
(`20261014-081924-fd5ed11e2abd459aab30498cf5ca8918`). The maintained completion
reader accepted its terminal event and manifest hash. All 18,171 evidence files
were copied with SHA-256 verification. The 93 retained correctness subchecks,
36 RAM read windows, RAM lifecycle, 12 recall cases, six 300-second mixed
episodes, nine map-polling windows and 72 fitting-write windows completed.
Every one of 186 raw XML scores matched its byte total and completed operations;
all 137 declared process intervals had actual complete telemetry. Largest gap:
1.048 seconds in RAM reference, 0.333 seconds elsewhere. Final Q: was restored
to its original 2 GiB Fast/Idle 256 KiB × 2 settings with zero errors or pending
bytes, timing off and unchanged saved profiles. Lab V: was uncached and clean;
its lifetime error count includes the three intentional ordering faults, with
no active fault. Loaded filter:
`FE00891A0D6ADA54CD4FCB587C704DAFFDA97C6C0DD47F6BCF9F444C56A16632`;
provider: `0B13C2F5DBDF45BBCFBEB3B07FFCE5BB915B2A2CA50193D6B2CF8EDA5F34D8FC`.
DiskSpd is the same binary recorded above. Later `43eb208` changes documentation
only; it does not change the qualified workload or native source.

Direct RAM 1M Q8T1 median was 44,489 MiB/s (44,363–44,809), with p99
0.261–0.278 ms and zero image-I/O attempts. Lifecycle verified adaptive reads,
the 16-request cap, inline overflow and ending Direct access under load.
Controlled recall-on final scores reached 100% hits with zero lower attempts;
the hot scan control stayed resident in both recall modes. Map polling at
2 seconds / 250 ms had no repeatable throughput loss of 3%; this does not test
GUI rendering or 100 ms refresh.

The mixed episodes measured 219–258 MiB/s without driver-error deltas and with
concurrent/persisted byte oracles. Post-churn Q1 rereads still missed entirely
(152–202 MiB/s); following Q8 rereads reached 99.750–99.988% hits and
20,992–28,193 MiB/s, with 37–526 lower reads per window. Q8 follows Q1 recovery,
so these are not independent queue-depth comparisons or RAM-only controls.
Natural idle left 768–897 MiB dirty with oldest ages of 56–70 seconds before
explicit drainage. Maximum age schedules work; slow storage is not bounded by
that age. This does not establish the cause of the remaining recovery cost.

All cached write-matrix windows had zero capacity waits, error deltas and
durability barriers; background writes occurred. Two boundaries had small lower
reads, so this matrix is not the zero-I/O admission proof supplied by the separate
37-case concurrency matrix. Timing-on reduced cached random-write throughput
in every pair: about 6–7% at Q1 and 9–10% at Q32. Keep timing off for product
scores and match it between comparisons. Sequential and cache-Off results varied
substantially. These measurements do not infer a causal old-build gain or accept
every latency tail; all windows had completions. The original settings were
restored; the refreshed generic product comparison follows.

**Generic comparison refresh (2026-10-14, 0.4.687.1).** `compare-cache` completed
with three repetitions under its maintained contract (larger-file and tree
steps intentionally use two, the five-minute mix one), run
`20261014-103546-64b8c94e36eb475084cf48d92c58a5a4`. `compare-cache-cold`
completed once after checked flush and drop-clean, run
`20261014-113824-70b3db176e494efea4f7823e9199e453`. Both used the same signed
drivers and DiskSpd as the release campaign, normal priority, timing off,
Fast 2 GiB with 1024 KiB × 4, closed indexers and no competing workloads.
All 475 evidence files were copied with SHA-256 verification; all 49 raw scored
XML files matched their byte totals with completions. The owned hypervisor
sampler recorded 3,938 actual per-disk samples, covered every step, had no missing
read/write counters and a maximum 1.036-second gap; it stopped after the cold run.

Hot DiskSpd 1M Q8 median read/write was 34,775 / 20,655 MB/s. Larger 8 GiB file
reads were 80.90 MB/s random 4K Q32 and 621.88 MB/s sequential 1M Q8. The emptied
cache's first/second 1 GiB reads were 570.54 / 19,637.80 MB/s. Sustained 8 GiB
writes averaged 259.62 MB/s, with a 177.73 MB/s median slowest second. The file
tree's median create/copy/read-and-hash/delete times were
50.23 / 13.68 / 154.23 / 15.90 seconds. These do not establish an improvement in
every workload: cached large reads and tree reads remain slower in this session
than some historical comparison rows. Shared storage and different dates prevent
attributing those changes to code from these rows alone. The repeated same-build
screens above support the causal miss-offload and drain-time decisions.
Fast application flushes still return in volatile RAM; the near-zero measured
flush is not a durable-save claim. Original 0.4.604.1 and competitor rows,
including their power-cut results, retain their evidence and dates. No power-cut
qualification was repeated on this build. Full rows and ranges:
[comparison](COMPARISON.md).

The same seeded 40,000-file tree contains 1,290,078,507 bytes in both sessions.
Its read-and-hash median was 4.914 seconds on 0.4.604.1 and 154.226 seconds on
0.4.687.1 (31.4× slower); the two 0.4.687.1 repetitions were 154.160 and 154.292
seconds. Creation rose from 12.091 to 50.230 seconds, copy from 8.602 to 13.679,
and deletion changed from 16.898 to 15.898. The read/hash timer excludes the
settling wait. Windows changed from build 28000.2956 to 28000.3231 and disk use
from about 19% to 43%. At run start, Windows reported 3.31 GiB versus 2.06 GiB
in `SystemCacheBytes`, while available physical memory was similar (10.75 versus
10.36 GiB). Shared storage and file-cache residency also differed. Hypervisor
read counters across each complete tree step were about 193/0 MiB on 604 and
195/0 MiB on 687 (first/second repetition); those intervals include creation,
settling, copy, read/hash and deletion, so they do not isolate read-phase traffic.
These cross-date numbers identify a large repeated observation in the new
session, not its cause.

**Maintained tree diagnosis (2026-10-14, plan 134).** The complete 15-case run
`20261014-175457-b7459c972da24d5a809c98512e31245b` passed every file-count,
byte-count and SHA-256 check and restored the original 256 KiB × 2 configuration
with zero dirty or in-flight bytes. It used CLI 0.4.702.1 on Windows build
28000.0 and the loaded 0.4.687.1 filter hash recorded in the run manifest;
diagnostic timing was off. Across three repetitions, median create/copy/read+hash/
delete seconds were:

| Arm | Create | Copy | Read + hash | Delete |
| --- | ---: | ---: | ---: | ---: |
| Cache disabled | 51.68 | 30.67 | 161.68 | 19.12 |
| Flags 7, 256 KiB × 2 | 50.98 | 10.61 | 169.30 | 17.68 |
| Flags 15, 256 KiB × 2 | 52.03 | 10.44 | 160.88 | 15.00 |
| Flags 7, 1024 KiB × 4 | 52.44 | 10.55 | 161.01 | 17.36 |
| Flags 15, 1024 KiB × 4 | 50.77 | 10.51 | 160.94 | 15.80 |

Read+hash took 160.07–170.45 s across all arms. The paired 1024 KiB × 4 arms
were effectively identical; the smaller-batch flags-7 arm was noisier, but its
three paired differences did not repeat. During read+hash, driver lower-write and
flush attempts and cache read-hit bytes were zero in every case; lower-read
attempts were 0–33. Windows reported 5.49–6.32 GiB of system cache around this
phase. This points away from QueueCache's read-miss scheduling and drain settings
as the cause of the measured slow phase. The run does not distinguish per-file
open/NTFS work, hashing, security filters, or actual Windows cache residency, and
it cannot explain the cross-date 604/687 difference. No driver change is justified
by this matrix; the historical tree regression remains open at the Windows/file-
tree attribution level. Do not treat the near-zero QueueCache read-hit counter as
proof that all file data was resident in physical memory.

**Tree diagnosis disposition (plan 141 runner, 2026-10-15).** One filtered,
diagnostic (not decision-grade) tree case, `tree-r01-p05-flags15-b1024-p4` in run
`20261015-000302-165f7519bbf14c7fac6b28b0e648ef16`, was repeated with Windows
Search stopped and disabled, controller 0.4.716.1 and an owned observer: a
Microsoft Defender performance recording over the read phase
(`New-MpPerformanceRecording`, `Get-MpPerformanceReport`) plus per-process CPU
time and system counters at about one sample per second. It passed the
40,000-file byte/SHA-256 oracle and reproduced the slow phase: create 57.2 s, copy
17.4 s, read+hash 164.2 s, delete 41.7 s. During the 164.2-second read+hash
window `MsMpEng.exe` used 156.5 CPU-seconds (136.2 user, 20.4 kernel), the tree
worker only 9.2 s and System 2.0 s; Q: disk throughput was zero and the driver
reported 144 lower read attempts and no read-hit bytes. Defender's report for the
240-second recording counted 40,338 real-time scans (153.1 s in total), 40,024 of
them in the tree worker (151.6 s, median 3.47 ms, maximum 595 ms) — 92% of the
phase; each of the ten slowest was an `OnOpen` scan of a tree file. The slow phase
is therefore Windows Defender's real-time scan of each freshly written file at
its first read-open (about 3.8 ms of antimalware CPU per file), above the volume
filter; the cache-off arm of the earlier 15-case matrix had the same time.
**Disposition: environmental, not a QueueCache regression.** Defender was left
enabled with no exclusions for that diagnosis, and no driver change is supported.
The observer and recording added a small CPU load, so this case is not used as a
timing result. What remains unproven is why the 0.4.604.1 session on 2026-10-12
took only 4.9 s for the same step: the comparison's environment record does not
capture Defender state, platform/signature versions or scan caching, and Windows
had moved from revision 2956 to 3231, so the older session's Defender behaviour
cannot be reconstructed. The planned cross-build driver swap (`tree-build-decision`,
four reboots) was therefore removed unrun: the cost does not depend on the filter,
and the measurement could not separate the missing Defender state.

**Later environment change (2026-10-15).** At the owner's request, the dedicated
test VM now has Defender Antivirus and Windows Search indexing disabled as its
persistent baseline; the effective-state checks are in [TESTING.md](TESTING.md).
No performance workload was run during that administration change. A subsequent
single current-settings `tree-decision` observation completed on controller
0.4.728.1 (`cfd0713`) and unchanged native 0.4.687.1:
`20261015-155033-d0d1032a62954ca7be893e4f6ce05e66`, case
`tree-r01-p05-flags15-b1024-p4`. All 40,000 files / 1,290,078,507 payload bytes
passed the hash oracle and the original runtime settings/flags were restored.

| Phase | Defender-active diagnostic, seconds | Disabled-baseline observation, seconds |
|---|---:|---:|
| Create | 57.2 | 8.691 |
| Copy | 17.4 | 9.346 |
| Read/hash | 164.2 | 6.857 |
| Delete | 41.7 | 40.971 |

The current after-create/copy settles lasted 161.3 / 170.4 seconds and observed
disk idle. Read/hash added 1,300,009,984 cache-hit bytes with zero lower
read/write/flush attempts, whereas the earlier diagnostic stayed in the Windows
file cache. This is one observation in a changed environment/cache path, not a
complete paired matrix, an isolated Defender percentage, a new driver speedup or
a replacement for historical product/CrystalDiskMark scores. It is consistent
with removal of the previously observed scanning cost. The 164.2-second figure
remains a diagnostic with observer overhead.

Historical power loss on QueueCache 0.4.604.1 (VM reset 0.03 s after a write test):
the two checked large files survived in each mode; this does not establish that
Fast-mode application flushes are durable. New unflushed small files: bare disk 698 missing and
1,066 incomplete of 10,000; Strict 478 / 950; PrimoCache without Defer-Write 0 /
105; PrimoCache Defer-Write all 10,000 missing with a clean file system; QueueCache
Fast 5,668 missing and `chkdsk` exit 13. Later partial-drain churn also damaged
NTFS with PrimoCache. The current safety goal in
[#13](https://github.com/devedse/QueueCache/issues/13) allows loss of pending
writes while requiring a healthy filesystem and intact untouched durable data.
That supersedes the earlier acceptance of filesystem damage; no safety fix is
claimed. On 0.4.810.1 (October 23, default drain) the same burst left NTFS clean in
three cuts (1,729–2,979 of the 10,000 small files missing, 3,595–8,265 damaged), and
three churn cuts were clean too.
[Full evidence](COMPARISON.md#power-cut-while-the-cache-is-writing).

## RAM disks, cached reads and scheduling

### RAM-disk asynchronous split reads: accepted (PR #9)

**Why reads were slow.** A Direct RAM-disk read of 1 MiB is split into four
256 KiB chunks copied by four processors, but the submitting thread waited until
all were copied before it could submit its next read, so queue depth 8 behaved
like depth 1. Plan 117 measured one active Direct read at Q8T1; of each ~41 µs
read, ~23 µs was the copy and ~18 µs submission/completion while the helpers
idled. The disk cache already completes queued reads from its own threads and
reached 39,492 MB/s in the README's CrystalDiskMark run versus the RAM disk's
25,353 MB/s.

**Change.** A large, asynchronous, non-paging Direct read posts all of its chunks
to the other processors' existing helpers (same chunk size, copy code and posting)
and returns; the helper that copies the last chunk completes the request. Helpers
receive work on every read, so they stay awake (post-to-take ~1.2 µs). The three
earlier asynchronous designs (`3fb9d39`/`e8e31c8`, `c5d32a8`, `9262493`) instead
gave each whole 1 MiB read to one worker: a 4× slower copy, and round-robin
workers that went to sleep between reads (21% spin versus 28% copy); they lost.
Admission reuses the provider's write policy (asynchronous while another read is
in flight; inline after 16 that found none, probing every 64th), stays inline
while more than one thread submitted a large read within 1 ms, and caps 16 in
flight. The adapter now withdraws Direct views before stopping its workers.
Modes per RAM disk: off, adaptive (default), always (lab correctness checks).

**Same-build comparisons** (owned 2 GiB RAM disk, 1 GiB file, DiskSpd 2.2 from
CDM 9.0.3, 10 s windows with 3 s warmup, normal priority, three alternating pairs,
diagnostics off; MiB/s medians and ranges):

| Run | Shape | Original | Candidate | Change |
|---|---|---:|---:|---:|
| Plan 118, build 575 | 1M Q8T1 target | 24,268 (23,986–24,591) | 44,766 (42,485–44,950) | +84.47% |
| | 1M Q1T1 | 24,441 (24,389–24,978) | 24,249 (24,218–24,536) | −0.79% |
| | 1M Q2T4 | 36,327 (36,202–36,886) | 34,223 (34,144–35,307) | **−5.79%, every pair lower** |
| Plan 119 revision, build 579 | 1M Q8T1 target | 24,234 (23,927–24,548) | **43,583 (43,295–43,696)** | **+79.84%** |
| | 1M Q1T1 | 24,975 (24,881–25,109) | 24,844 (24,600–24,953) | −0.52% |
| | 1M Q2T4 | 36,412 (36,168–37,887) | 36,578 (36,474–36,693) | +0.46% |

The first run lost four-reader throughput because every thread handed off; the
pre-declared revision (inline while several threads submit) kept ~473k of their
reads inline and removed the loss. Candidate target windows completed 99.7–99.9%
of Direct reads asynchronously (the runner fails a run below 50%). Q8T1 p99 read
latency rises from ~0.07 to ~0.3 ms because queued reads now really overlap; the
old figure was each read completing synchronously before the next was submitted.
Both runs: all 18 whole-file hashes identical, 33 owned exits zero, zero image
I/O, maximum telemetry gap 1.157/1.112 s, mode restored after every window.
Runs `QueueCache-Campaign-20261011-080905-c70ad40ec31c42548c7a91415a73afd6` and
`QueueCache-Campaign-20261011-084408-58e5284403004b94a02d67dac82dee06` (PR9-AsyncCompare1/3, private evidence).

**Correctness.** Driver Verifier (special pool, IRQL, I/O verification, deadlock
detection, DDI compliance) first caught a bugcheck 0x3B: the eligibility check
used `IoIsOperationSynchronous`, which dereferences a file object that an NTFS
request to the volume does not carry; it was replaced before any measurement.
`ram-read-async-lifecycle` then passed under Verifier and without it: a
single-thread overlapped reader checks every 32 KiB of each 1 MiB block (poisoned
before each read). Without Verifier: adaptive Q8 59,469 MiB verified (99.8%
asynchronous); forced Q32 60,952 MiB with the in-flight cap reached at exactly 16;
forced Q8 while an unrecognized control ended Direct access: 31,367 asynchronous
reads then the standard path, 58,361 MiB verified, rebound. Under Verifier the
same checks verified 14,452 + 18,690 + 17,558 MiB with no fault. Under Verifier
submission is slower than a copy, so the adaptive policy correctly stays inline.

**README benchmark.** Real CrystalDiskMark 9.0.3 GUI on a new 4 GiB RAM disk T:
(0.4.583.1, default adaptive mode, normal priority, Verifier off, Default theme,
150% zoom, two complete runs, the faster kept): SEQ1M Q8T1 read **47,583 MB/s**
(README before: 25,353), write 24,434 (23,266); SEQ1M Q1T1 26,649 / 16,463;
RND4K Q32T1 1,467 / 1,194; RND4K Q1T1 1,657 / 1,310 (1,641 / 1,317). The other
run: 47,663 / 23,393, 26,206 / 16,830, 1,463 / 1,206, 1,649 / 1,312. Only the RAM
disk column was re-measured; the Q: columns are unaffected and stay from 0.4.476.1.

**Final regression.** Signed 0.4.583.1 (final source; drivers loaded and
Verifier off), lean `release-performance`, one command: **COMPLETED 11/11 phases,
106/106 cases, 1 h 56 min 23 s, restored**, run
`QueueCache-Campaign-20261011-100402-d4f2fc304e714c708305406f06b93426`. All 118
retained correctness sub-checks pass; 2,266 owned process exits are zero. The
RAM reference under the new default measures Direct 1M Q8T1 at a median
43,590 MiB/s (Q1T1 24,821, Q2T4 36,097; 4K reads never reach the new path). The
lifecycle checks, recall, 30-minute sustained phase and map cost pass their
guards. The 72-case write matrix against PR #8's run (0.4.476.1, two days
earlier, same DiskSpd): cached sequential writes are within noise (Q1 +0.6%, Q8
−5% to +1% with overlapping ranges), cached RND4K Q32 ±2%, cached RND4K Q1
1.5–2.7% lower with separated ranges. That last difference is under the 3%
review threshold and within cross-day variation seen before; PR #9's default-off
diagnostics add a few counter updates to the cache path and may contribute.
Uncached results follow the Ceph storage (higher today).

### RAM-disk writes: no speedup available on this machine (plan 120)

**Question.** CrystalDiskMark wrote SEQ1M Q8T1 at 24,434 MB/s to the RAM disk
against 47,583 MB/s read. Queued large writes already return at once and are
copied by the provider's per-processor workers, so the read fix (overlapping one
submitter's requests) was already in place for writes.

**Measurement** (`ram-write-reference`, signed 0.4.596.1, owned 2 GiB RAM disk,
1 GiB file, DiskSpd 2.2 with a fixed 1 MiB source buffer `-Z1M`, 10 s windows with
3 s warmup, three alternating repetitions; medians, MiB/s). The memory rows copy
1 MiB blocks between a hot buffer and a cold 1 GiB region in user mode, with no
driver involved:

| Shape | One thread / submitter | Four threads / submitters |
|---|---:|---:|
| Copy into cold memory, runtime copy | 19,271 | 22,110 |
| Copy into cold memory, explicit SSE2 stores | 18,030 | 22,114 |
| Copy into cold memory, non-temporal (streaming) stores | 23,621 | 24,161 |
| Copy out of cold memory (read), runtime copy | 40,091 | 47,099 |
| RAM-disk 1M writes, Q1 / four submitters at Q2 | 15,791 | 23,101 |
| RAM-disk 1M writes, Q8 / four submitters at Q8 | 23,190 | 24,293 |

Four submitters write 0 to 5% faster than one, and queued writes already exceed
the machine's four-thread ordinary-store copy rate. Writing into memory the
processor has not cached costs about twice a read on this VM (22 against 47 GiB/s),
so the RAM disk's write rate is the machine's, not the driver's. Splitting writes
across processors (as reads now are) cannot help; the September tuning had also
measured split writes slower than worker writes (16.2 against 24.7 GB/s).

**Candidate rejected: streaming stores.** The only lever left was non-temporal
stores, which skip reading each destination line (one user-mode thread: 23.6
against 18.0 GiB/s). A default-off per-disk mode streamed every large write copy;
the same-build comparison (`ram-write-streaming`, 18 windows, 100% of candidate
bytes streamed) found 1M Q1T1 **−0.59%** (target), Q8T1 −0.09%, four submitters
−2.17%: verdict REJECT. The mode, its provider actions (0x105/0x106, reserved)
and the comparison suite were removed; `ram-write-reference` stays. The Q1 write
time is not dominated by the copy's memory traffic.

Runs: `QueueCache-Campaign-20261011-180850-8b826f60a1b041e6a900421e6f25e7f8`
(reference, COMPLETED 36/36, all three disks rewritten and read back) and
`QueueCache-Campaign-20261011-181541-df9636018eab4518b202118baa8f8954`
(comparison, COMPLETED 18/18). An earlier reference run on 0.4.595.1 stopped after
its first repetition on a runner bug (rewriting the oracle over the existing file)
and is preserved but not used. The VM's virtual processor hides some copy
features (ERMS is not reported), so a physical PC may reach a different limit.

### Cached reads: lighter read-hit bookkeeping accepted (plan 120)

**Where the time went** (`cache-read-reference`, signed 0.4.595.1, 2 GiB Fast
cache on Q:, resident 1 GiB file, 10 s windows, three repetitions, timing-on
windows for the lock counters; medians):

| Resident 1M read | MiB/s | Served by | Lock acquisitions / wait / held per read | Worker queue per read |
|---|---:|---|---:|---:|
| Q1T1 | 18,625 | caller thread (99%) | 2.0 / 0.4 µs / 7.5 µs | 0.1 µs |
| Q8T1 | 36,138 | copy threads (100%) | 3.0 / 8.9 µs / 11.5 µs | 20.8 µs |
| Q2T4 (four readers) | 32,072 | copy threads (100%) | 3.0 / 8.8 µs / 12.2 µs | 15.9 µs |

At Q8 the cache lock was busy about 40% of the time, and four readers were slower
than one: the limit was bookkeeping under the lock, not the copy. Each 1 MiB hit
walked its 256 blocks five times under the lock: the request worker's residency
check, the copy thread's residency check, a second lookup to pin, the unpin and a
recency move per block.

**Change.** Copy flag 4: the copy thread records each block's slot during its
residency check and pins from that record (one lookup per block, not two), and
blocks that are already neighbours in their clean recency list move to its end in
one step instead of one relink each. The resulting recency order is identical to
moving them one at a time; dirty blocks, promotions and non-neighbours keep the
per-block path. Same-build comparison (`cache-read-bookkeeping`, signed 0.4.597.1,
18 cases, three alternating repetitions, timing off; MiB/s):

| Shape | Flags 3 (before) median / range | Flags 7 median / range | Change |
|---|---:|---:|---:|
| 1M Q8T1 target | 34,951 (34,159–36,190) | **37,718 (37,217–37,738)** | **+7.92%**, every pair up, ranges separated |
| 1M Q1T1 | 18,782 (18,702–18,847) | 19,631 (19,570–19,860) | +4.52%, ranges separated |
| 1M Q2T4 | 32,689 (32,007–33,352) | 33,818 (33,017–34,080) | +3.46%, every pair up |

Accepted and on by default from plan 120 (copy flags 7). Runs:
`QueueCache-Campaign-20261011-173857-a865de3e67804982a12c91fc40b04568`
(attribution, COMPLETED 18/18) and
`QueueCache-Campaign-20261011-182600-f8dea4c1adaf491ab3996c92fba644e1`
(comparison, COMPLETED 18/18). The request worker's own residency check and the
per-block lookups that confirm a version is still current remain; removing them
would change which reads are offloaded or which versions are touched.

**CrystalDiskMark** (0.4.604.1, the README procedure, one session, MB/s). Copy
flags 7 in three runs: SEQ1M Q8T1 read 38,945 / 39,702 / 39,707, SEQ1M Q1T1
19,825 / 19,564 / 19,887. One run with the old bookkeeping (`lab-copy-flags Q: 3`)
between them: 38,645 and 18,962. CrystalDiskMark therefore shows about +3% at
Q8T1 and +4.5% at Q1T1; its five-second passes include file opening and the
first touches, which the 10-second DiskSpd windows above exclude. The README
cache column (39,702 read, run 2) matches the 0.4.476.1 run two days earlier
(39,492): that earlier evening was simply faster, so cross-day CrystalDiskMark
numbers say nothing about a change.

**Final regression.** Signed 0.4.604.1 (final source; drivers loaded and
Verifier off), lean `release-performance`, one command: **COMPLETED 11/11 phases,
106/106 cases, 1 h 47 min 34 s, restored**, run
`QueueCache-Campaign-20261011-223323-2aa3fb80bfdd4040ad56e7a601f3d09a`. All 118
retained correctness sub-checks pass. Of 2,265 owned process exits one is
non-zero: the first final inspection found Q: still writing back, and the
plan-120 retry found it clean on the next attempt. Against PR #9's final run
(0.4.583.1, the same day, same DiskSpd, same analysis): the RAM reference is
unchanged within run-to-run variation (Direct 1M Q8T1 median 43,845 against
44,215 MiB/s, Q1T1 24,434 against 24,668, Q2T4 36,067 against 37,818; the RAM-disk
driver did not change). Cached 4K random reads at Q8 in the map-cost phase have a
median of 381,055 against 369,516 IOPS (+3.1%); cache recall hits are within 1%.
The write matrix's cached cells are 0.8–4.2% lower at Q1 and RND4K Q32 and between
−0.6% and +2.2% at SEQ1M Q8. Plan 120 changed only the driver's read path, so
these write differences are run-to-run variation (see the
[measurement rules](#measurement-rules) on cross-run comparisons). The 30-minute
sustained phase ran at 2,709–2,990 IOPS against PR #9's 1,415–2,837, following
the Ceph storage below Q:.

### Priority and background interference

| Finding | Evidence | Decision |
|---|---|---|
| Task Scheduler's default below-normal launch can substantially depress scores | Historical combined CPU/I/O/memory change: cached SEQ1M Q8 reads 16.4–17.7 versus 28.9–30.6 GB/s; RAM-disk Q8 writes 16.3 versus 24.7 GB/s; uncached reads/writes 262/116 versus 640/270 MB/s | Launch normal-priority benchmarks; Task Scheduler priority **4**. This historical comparison did not isolate the three settings. |
| Driver Verifier can dominate benchmark overhead | Same 0.4.249.1, matched maintained random-write Q1 diagnostic: Verifier on median 94.529 MB/s, off 973.255 MB/s (10.30×). GUI random Q32 read/write 77.030/69.959 → 1,790.783/1,402.940 MB/s after the approved restart | Use Verifier for correctness runs and Verifier off for performance. Restart/cache history also changed; do not attribute every remaining sequential difference to Verifier. |
| Normal CPU class alone is insufficient to reproduce the intended launch settings | Scheduler priorities 5/6 also use normal CPU but lower memory defaults | Use priority 4 and record actual child settings. Do not silently boost user applications or driver threads. |
| CPU class alone reproduces a large resident Q8 loss | Complete plan-113 comparison, 36/36: normal 36,699.00 versus CPU BelowNormal 24,147.65 MiB/s, **−34.20%**, separated ranges and zero lower I/O. Q8 low I/O/memory 36,648.95/36,915.58, overlapping normal ranges | Keep explicit normal benchmark launches. Completed CPU/wait analysis supports a priority/affinity follow-up; no driver/application priority change is accepted. Low memory is a process default after launch, without pressure injection or locked-page changes. |
| First isolated-priority attempt is incomplete | Run `20261010-071617-21a3d11a0fc748f4b1d68be4c7c286cc`: 26 measurements then case 27 rejected one ordinary 8 KiB lower read. No paging-read, eviction, drain or driver-error increment; clean restoration | Preserve separately. Counters do not identify the read's file/process. Accepted retry on W: uses new immutable IDs and none of the incomplete run's samples. |
| NTFS last-access updates contaminate strict controls | 12–16 KiB metadata writes caused five rejected layout runs in a day | Disable last-access updates on this benchmark VM and record the setting. Do not relax lower-I/O guards. |
| Defender can read an unscored cold file tail | Earlier concurrency failure identified `MsMpEng.exe`; retained extents mapped the recorded offset to the last 64 KiB of a 1 GiB file, outside its 512 MiB scored prefix | Workload preparation/size contracts were corrected and versioned. This was not proof of write-admission fallback. |

Methods and preserved failure details: [README benchmark method](TESTING.md#7-readme-benchmark-crystaldiskmark),
[priority comparison](history/PRIORITY_COST_20261010.md),
[sustained validation](history/SUSTAINED_CACHE_VALIDATION_20261009.md),
[Verifier comparison](history/PERFORMANCE_INVESTIGATION_20260930.md).

The accepted priority retry is
`20261010-073414-2f6dd18225094fc98abc3560ddaca39e`, 07:34:14–07:53:17 UTC,
signed native 0.4.514.1 / managed preview `1788970`, same native hashes as the
caller comparison below. All 940 owned process exits succeeded; maximum telemetry
gap was 0.689031 seconds and priority application 0.01089 seconds. All windows
had zero staged/lower I/O and clean independent restoration. Its raw archive
SHA-256 is `5A97128327D6C155EDE6DB82252182C14F310D8D339E2581C7296826A054A8DF`.

| Shape, median MiB/s | Normal | CPU BelowNormal | I/O low | Memory default low |
|---|---:|---:|---:|---:|
| 1M Q1T1 | 18,689.61 | 18,511.79 | 18,576.52 | 18,580.02 |
| 1M Q8T1 | 36,699.00 | 24,147.65 | 36,648.95 | 36,915.58 |
| 4K random Q1T1 | 1,245.19 | 1,241.80 | 1,271.56 | 1,253.55 |

CPU Q1/random median changes are −0.95%/−0.27%; low I/O random Q1 is +2.12%
with separated three-repeat ranges. Small control differences do not establish
general improvements. These resident cached-read results do not measure low-I/O
miss performance or isolate every historical RAM-disk/disk-only difference.
Full ranges are in the priority report.

The separate WPR Q8 subset completed 4/4, run
`20261010-075450-78a3c72b51f74c20839c855175eb9f1c`, with clean restoration,
zero lower/staged I/O and maximum telemetry gap 0.281858 seconds. The trace is
preserved and stopped. Traced scores
are excluded from the comparison, so recording overhead cannot be mistaken for
a release regression. Offline analysis has now completed with lost events
rejected and matching native PDBs verified:

| Q8 trace variant | Non-idle CPU | Cache memcpy, busy samples | Cache RequestWorker, busy samples | All DiskSpd threads' ready time |
|---|---:|---:|---:|---:|
| Normal | 95.9% | 44.2% | 5.5% | 1.454 s |
| CPU BelowNormal | 65.0% | 37.2% | 8.3% | 6.358 s |
| I/O low | 96.6% | 42.2% | 5.5% | 1.536 s |
| Memory default low | 94.8% | 42.6% | 5.1% | 1.517 s |

BelowNormal has CPU 0 99.3% busy while the other three are 44.0–49.1% idle;
Normal has all four only 2.4–4.8% idle. XML records affinity enabled in every
variant. This supports scheduler-ready delay and reduced parallel utilization;
it does not establish a specific helper-polling cause or justify a priority boost.
The next diagnostic should isolate priority **and affinity** before selecting a
driver change. Function shares are over enclosing 13.1-second intervals;
RequestWorker includes request work and polling. Ready time sums all benchmark
threads; long control/termination waits are not I/O latency. Many BelowNormal
readying stacks/processes are unavailable, so a specific waker cannot explain
every delay. These cache-worker samples are distinct from the RAM-provider
WorkerMain samples below. Traced scores remain excluded from acceptance.
ETL SHA-256 `26C2F946D8F06DAC5D3321E443D8ED04FBA294C6AA6ACBFBC92465CD5B9C598A`;
analysis archive `A22B6B563DFBA206CE71B61B7012B068C786CC6C98DD629AAD6FC827D992A751`.

### Earlier retained request-path improvements

The copy-offload strategy that works for a RAM cache on a disk volume predates
both recent PRs. These historical before/after figures explain what was reused
as motivation for the RAM-disk experiments; they are not fresh PR #9 scores.

| Change, 0.4.153.1–0.4.162.1 | Measured finding | Retained behavior |
|---|---|---|
| Serve an idle eligible request on its submitter | Random Q1 about 24,500 → 238,000 reads/s and 22,000 → 190,000 writes/s; avoided about 11 μs worker wake for about 1.4 μs cache work | Caller path for eligible idle requests; preserve control/ordering ownership. |
| Stop waking unused drainers | Previously about 180,000 extra wake-ups in five seconds; random Q32 writes could collapse near 35,000/s during write-back | Only configured drainers wake. |
| Keep deep queues on the ordered worker | Q32 worker 333,000–356,000 versus caller 217,000–236,000 reads/s | Periodic probes and 256-request post-overlap backoff, now attributed by PR #9 counters. |
| Parallel copies with fewer lock acquisitions and a push lock | Initial copy offload alone stayed near 15.2 GB/s; one lock release for pinned range reached 21.9–23.8; push lock reached 34.5–36.2 GB/s | Three offloaded-request threads for suitable resident reads/large fitting write copies; metadata/pin ownership remains serialized where required. |
| Brief idle-worker polling | Restored random Q32 writes near 310,000/s | Bounded 30 μs poll before sleeping. |

With the same historical warmed 1 GiB CDM-style contract, 0.4.151.1 → 0.4.162.1
SEQ1M Q8 read ranges changed 14,337–14,437 → 35,949–36,510 MB/s and writes
12,635–13,954 → 20,508–21,483 MB/s. Earlier builds' single-worker ceiling was
confirmed by bisect; more submitting threads alone did not lift it.

Additional reusable findings: detailed timing reduced historical random Q32
write medians about 6.9%/6.5% in separate Eager/Idle comparisons; keep timing modes
separate. On 0.4.259.1, precomputed write data (`-Z1M`) measured 21,406 MB/s versus
16,521 MB/s for fresh randomized data per I/O (`-Zr`), alongside resident reads at
36,185 MB/s. Data generation is part of the workload cost, not a driver speed-up.
Large repeated cached writes measure volatile acknowledgement/coalescing, not
durable storage throughput; capacity pressure and real Flush remain disk-bound.
Historical one-sample baselines must not be treated as current scaling limits.
Methods: [write trajectory](history/WRITE_PERFORMANCE_TRAJECTORY.md),
[sequential reproduction](history/SEQUENTIAL_PEAK_REPRODUCTION_20260930.md).

### Cache allocation and copy optimizations

All figures here are resident sequential 1 MiB read medians in GB/s, with Q1/Q8
shown together. Lower-I/O guards distinguish RAM placement cost from disk misses.

| Controlled comparison | Q1 / Q8 | Finding |
|---|---:|---|
| Old allocator, fresh | 15.46 / 36.85 | Initial ascending free slots are fast. |
| Old allocator, sequential use then clear | 10.75 / 29.81 | Reuse reverses free-slot order and reduces resident throughput. |
| Old allocator, random use then clear | 8.56 / 23.18 | Scattered reuse is slower still, despite zero disk reads. |
| Same allocation, reset free-slot order after sequential reuse | 15.24 / 36.26 | Changing only free-slot order restores fresh speed; changing physical allocation is unnecessary. |
| Shuffled 256 KiB chunks, ascending slots inside | 14.77 / 35.28 | Within 4% of fresh. Ordering within a chunk matters more than ordering all chunks globally. |
| Chunks ordered, slots descending inside | 10.71 / 29.54 | Direction inside each chunk reproduces the reuse loss. |
| Chunk allocator, sequential reuse then clear | 14.61 / 35.66 | Retained from 0.4.431.1; uses per-chunk free bitmaps, ascending allocation and whole-free chunks first. |
| Chunk allocator, random reuse then clear | 14.50 / 35.48 | Reuse penalty largely removed. |

The allocator does not relocate survivors after partial eviction. Before the copy
optimizations, short/full-cache churn rereads changed little between allocators
(13.86/30.66 versus 13.79/30.05; 14.15/33.17 versus 14.28/32.56 GB/s).
Physical TLB/prefetch costs were not isolated by hardware-counter measurements.

| RAM-hit copy mode | Fresh Q1 / Q8 | Sequential reuse Q1 / Q8 | Random reuse Q1 / Q8 | Decision |
|---|---:|---:|---:|---|
| One copy per 4 KiB block | 15.13 / 35.94 | 14.61 / 35.66 | 14.50 / 35.48 | Diagnostic flag 0 control. |
| Prefetch next block | 18.25 / 37.44 | 17.36 / 35.29 | 17.40 / 36.19 | Helps Q1, including about 19% after churn. |
| Unbounded contiguous copy runs | 14.88 / 22.10 | 19.33 / 37.91 | 19.37 / 37.02 | Reject the unbounded form: fresh Q8 falls sharply when a run becomes 1 MiB. |
| Runs bounded to one 256 KiB chunk | 20.60 / 39.51 | 19.22 / 37.79 | 19.41 / 34.55 | Removes that copy-size regression. |
| Prefetch plus bounded runs | 20.85 / 39.36 | 19.34 / 37.71 | 19.32 / 37.17 | Retained as default flags 3 from 0.4.434.1; integrity/policy/pressure checks pass. Plan 120 adds flag 4 (default 7, [cached reads](#cached-reads-lighter-read-hit-bookkeeping-accepted-plan-120)). |

These optimizations affect **RAM-backed cache hits on a disk volume**. A dedicated
RAM disk uses a different payload layout: locked pages and the provider's
existing `CopySplit`/`LargeCopy` helpers. Direct RAM-disk access already reuses
that provider copy path; there is no separate newly accepted port of the cache's
prefetch/coalescing loop into PR #9. Large copies are split into 256 KiB pieces
and can enlist provider helpers. A new whole-request queue changes submission
and completion scheduling, which must be tested independently of the copy loop.

Detailed evidence: [allocation/copy investigation](history/CACHE_LAYOUT_INVESTIGATION_20261008.md).

### Read recall: the retained PR #8 speed-up

Disk rereads are necessary when data was evicted. The old admission rule also
made repeated reads of a fitting file keep evicting their own freshly fetched
blocks. Read recall fixes that second problem: a bounded history of evicted
blocks compares their last-use time with the oldest cached data. It costs four
bytes per cache slot, about 0.1%, within the fixed budget. New one-off scans still
enter near the eviction end; `drop-clean` clears history.

| Workload | Earlier rule | Read recall | Interpretation |
|---|---|---|---|
| Fitting reread, pass 3 | 12.1% hits, 178 MiB/s | 100% hits, 19,052 MiB/s | Subsequent passes become resident; first disk fetches are not eliminated. |
| Fitting reread, Q8 after full passes | 22.8% hits, 158 MiB/s | 100% hits, 36,943 MiB/s | Controlled same-build gain; recalled resident passes make zero disk reads. |
| One-off/repeated scan then hot-set read | Hot set stays 100% resident | Hot set stays 100% resident | Scan resistance retained. Larger-than-cache repeated scan has 48.6% hits in both modes. |
| 30-minute mixed soak, churned Q8 reread | 130–237 MiB/s; 8.8–17.5% hits | 15,620–19,877 MiB/s; 99.6–99.8% hits | Recovery improves substantially with exact concurrent/post-drain byte checks. Q8 follows Q1 recovery, so it is not an independent queue-depth comparison. |
| Random workload resuming after a reread | More of its older working set survives | About 7 percentage points fewer hits initially, catches up in about one minute, then about 4 points more | Measured, accepted recency trade-off; read recall is not faster for every transition. |

The controlled `cache-recall` run completed 12/12 on 0.4.469.1,
`20261009-161142-fbaac21da41a4410b05e44dcbf28ef01`.
The earlier 30-minute soak completed six episodes, 24,414 independent exact
write/read checks and all 24 post-drain oracle files. A short 120-second soak is
not a substitute for that sustained-use result.
Methods/tables: [sustained validation](history/SUSTAINED_CACHE_VALIDATION_20261009.md).

### Dedicated RAM-disk access and rejected scheduling experiments

Earlier Direct access removes volume/provider dispatch overhead for suitable
RAM-disk reads and small writes. Large writes, at least 512 KiB, use the Standard
path because the provider can overlap queued writes. Earlier measurements were
17.4 versus 25.4 GB/s for Direct versus Standard Q8 large writes; after selecting
Standard for them, Direct-mode disks reached about 24.4 GB/s. Small Direct I/O
retains its advantage. These choices predate PR #8.

Earlier attempts to reuse asynchronous copy offload for RAM-disk reads also
predate PR #9 and were removed: per-CPU queued Direct copies gave 20.8–22.3
GB/s versus about 26.2 GB/s inline, and system-worker offload gave no gain or
13–17 GB/s. A historical sample profile had 21% busy samples spinning between
copies versus 28% copying. Moving the same copy to another thread does not
automatically improve throughput.

Earlier provider tuning on signed 0.4.354.1 also found no throughput difference
between three/four workers or 50/200 microsecond polling duration. Streaming
non-temporal copies gave no worker gain and slowed split reads; 512 KiB split
chunks did not engage a helper in time. These negative findings belong here as
well as in the historical tracker. They are not a matched experiment on 514,
but a new poll-duration or copy-size experiment needs a specific new reason.

The new PR #9 experiment instead used a bounded shared queue and three sleeping
whole-read executors. These executors copied each request without the retained
provider split helper. Scheduling **and copy granularity** changed together;
the result rejects this combined implementation, not every possible scheduling
improvement or the earlier cache-copy optimizations. Its 54-window same-build comparison completed on
0.4.503.1, run `20261010-034755-884b991306b245e7b91b3aea82fa9351`:

| Direct large-read shape | Retained synchronous median MiB/s | Experimental queue median MiB/s | Change |
|---|---:|---:|---:|
| 1M Q1T1 | 24,729.50 | 23,834.47 | −3.62% |
| 1M Q8T1 | 23,977.50 | 21,268.10 | −11.30% |
| 1M Q2T4 | 42,934.37 | 21,628.17 | −49.62% |

All large-read ranges fall below their synchronous controls. Small reads bypass
the experiment and remain within variation. Q8/four-reader synchronous medians
are **25.14/45.02 decimal GB/s**, versus 22.30/22.68 with the queue. Exact byte
guards, whole-file hashes, telemetry and restoration passed. The hidden installed
tray remained resident during this comparison, a recorded preflight limitation;
it was closed before later runs. No trace ran during this A/B.

**Decision:** remove the new queue engine and retain synchronous split reads.
Later superseded: asynchronous split reads keep the split and are accepted (above).
The larger four-reader result demonstrates aggregate available bandwidth; the
readers use independent cursors and may overlap source data. It does not prove a
single sequential reader can reach that score. Preliminary enclosing CPU
profiles show about 35% helper `WorkerMain` versus 47% memcpy at Q8T1, and about
4% versus 70% with four readers. These function shares motivate coordination
profiling but do not prove every helper sample is spin. A new design needs a
repeatable gain, preserved Q1/small/multi-reader controls and lifecycle checks.

Complete reference: 36/36, run
`20261010-031011-5ff0b7da045a4b559abcc5cef68060b1`, with ETW enabled; traced scores
are diagnostic, not the acceptance baseline. Full results and compatibility
reservations: [RAM scheduling](history/RAM_READ_SCHEDULING_20261010.md).

### RAM-disk affinity and source-lane controls (before asynchronous reads)

These controls showed the single-reader gap was not caused by affinity or source
lanes; asynchronous split reads later closed it.

The plan-114 focused campaign on unchanged signed 0.4.514.1 completed **6/6
phases, 81 retained correctness checks and 30 RAM measurement windows**, run
`QueueCache-Campaign-20261010-124559-697930c96ddb4e57a97924a301d5a7e3`,
12:45:59–13:03:20 UTC (**17 min 21 s**). Managed preview code matches `4307679`;
Windows Release host contracts and Debug/Release CI pass. Loaded filter/provider
SHA-256 are `C8856CCD2DABAC66B9648FBFE6CF0A3697A2F2EB4CA8D936806E683E6348B764` /
`8BC6DF3260FA77B447E7DF737800370706A10D324F345FC2A4C6A5AE88EA9A8C`.
The DiskSpd hash is the unchanged bundled 2.2 hash in the measurement rules above.
Copy/native code stays fixed; CPU Normal, memory default 5 and XML I/O hint 3,
three-second warmup, ten-second score, no ETW or Desktop workload. Every entry
is the median and full range of three alternating repetitions. GB/s is decimal.

| Read shape | Default affinity GB/s (range) | Unbound GB/s (range) | Median change |
|---|---:|---:|---:|
| 1M Q1T1 | 26.48 (26.10–26.87) | 26.36 (26.04–26.48) | -0.45% |
| 1M Q8T1 | 25.84 (25.59–26.12) | 26.30 (25.89–26.53) | +1.77% |
| 1M Q2T4, overlapping | 38.10 (37.61–38.60) | 39.48 (38.01–40.13) | +3.63% |
| 1M Q2T4, separate lanes | 37.97 (37.00–38.53) | 39.04 (38.31–39.54) | +2.83% |
| 4K random Q1T1 | 1.755 (1.742–1.761) | 1.741 (1.733–1.747) | -0.76% |

**Decision:** no production change. Removing affinity adds only 1.77% to the
single-submitter Q8 median, with overlapping ranges. It does not turn 26 GB/s
into the four-submitter score. Removing shared source blocks changes four-reader
medians by −0.34% with default affinity and −1.11% unbound, also overlapping ranges.
That is evidence against shared source locality explaining most of this gap for
this workload. Four separate readers still reach 37.97–39.04 GB/s. These are
matched controls; the older 45 GB/s reference had different driver/launch/warmup
conditions and is not a release comparison with this run.

The retained Direct path performs its large copy before completing the request
([transfer](../driver/qcache/ramdirect.cpp), [completion](../driver/qcache/driver.cpp));
configured Q8 does not guarantee eight copies overlap from one submitter.
Together with earlier function profiles, this makes request-level concurrency
and split-helper handoff/coordination the next candidates. That is a source-based
inference, not a completed causal attribution. No faster cache-copy loop was
ported into the RAM disk here. The rejected queue changed both scheduling and
whole-copy granularity; a future experiment should keep `CopySplit` fixed first.
Cache BelowNormal × affinity interaction remains a separate open diagnostic.
The completed `-n` control changes DiskSpd thread affinity. Provider copy helpers
still use fixed CPU assignments in `driver/ramdisk/transfer.cpp`; their placement
and handoff cost were not independently varied. The next attribution must also
distinguish useful helper work from polling/coordination before selecting a change.

All thirty raw XML/settings, scored-byte calculations, owned priority readbacks,
1 GiB before/after hashes, native identities/counters, readiness and enclosing
intervals were inspected. Every window has zero image/backing read/write attempts;
maximum RAM sample gap is **1.021481 s**, below the unchanged two-second contract.
Priority application completes within **0.0227392 s**. All 93 owned process exits
are zero; 22 control readiness handshakes have maximum gap **0.246594 s**. All three
RAM fixture groups return the original resource IDs and 2 GiB reservation. Final
fault tests record exactly three accepted injected errors and restore a healthy
lab; no later benchmark follows them.

The original Q: runtime, timing/recall/backoff and saved profiles restore exactly,
with zero pending bytes or error. The owned NTFS lab is detached and the installed
tray resumes in the signed-in session. Private immutable raw archive SHA-256:
`7AF11BA9579E1C73A52F737D3B149308A117916502B8E3C5D614223DFE6BC4F3`.
The earlier dirty-preflight and 4/6 interrupted campaign attempts remain separate;
none of their checks/scores are used to complete this run. The explicit bounded
phase preparation fix preserves strict clean capture and refuses faults before
controls; [orchestration evidence and failure history](TESTING.md#3-running-tests-one-command).

### Caller-path backoff: retain the production default

An overlap can send the next 256 eligible candidates to the ordered worker.
PR #9 appends candidate/first-decline counters and a runtime lab-only 0..256
control. Mandatory control, queue, active-owner, worker and offload checks still
apply. Default remains 256; no persistent product setting was added.

Two complete 24-window comparisons used the same signed 0.4.514.1 native driver,
managed preview `1788970`, normal priority, timing off, 2 GiB Fast/Deferred cache
and fitting 1 GiB files. Three alternating repetitions per mode, with independent
concurrent and persisted byte oracles after scoring:

| Filesystem / shape | Default 256 MiB/s | Lab 0 MiB/s | Change | Interpretation |
|---|---:|---:|---:|---|
| NTFS mixed 64K Q1, 70% reads | 8,897.40 | 8,811.69 | −0.96% | Ranges overlap. |
| NTFS mixed 64K Q8 | 7,685.96 | 8,090.55 | +5.26% | Ranges separate; more caller work. |
| NTFS random 4K read Q1 | 1,242.31 | 1,240.75 | −0.13% | Ranges overlap. |
| NTFS sequential 1M read Q8 | 35,992.91 | 28,370.53 | **−21.18%** | Ranges separate; shorter backoff loses throughput. |
| ReFS mixed 64K Q1 | 9,224.79 | 9,264.40 | +0.43% | Ranges overlap. |
| ReFS mixed 64K Q8 | 7,783.93 | 8,078.71 | +3.79% | Ranges separate; more caller work. |
| ReFS random 4K read Q1 | 1,335.16 | 1,346.03 | +0.81% | Ranges overlap. |
| ReFS sequential 1M read Q8 | 33,832.97 | 35,027.47 | +3.53% | Ranges overlap; no established gain. |

All 48 fitting windows staged zero lower reads. No capacity waits were observed.
More requests served on the caller is not itself a speed criterion. These are
within-filesystem mode comparisons, not a controlled NTFS-versus-ReFS ranking.
Retain 256. A size/filesystem-specific policy is a future hypothesis and requires
a new same-build A/B plus ordering/flush/capacity qualification; it is not already
implemented or proven by this table.

Exact runs: NTFS `20261010-043439-25f6f295740048c3a933ecc726d9f4f0`;
ReFS `20261010-065536-e440846e99264ddb86145783b78c8615`.
Both independently restored disabled/zero-budget lab volumes and backoff 256.
Loaded filter SHA-256
`C8856CCD2DABAC66B9648FBFE6CF0A3697A2F2EB4CA8D936806E683E6348B764`;
provider `8BC6DF3260FA77B447E7DF737800370706A10D324F345FC2A4C6A5AE88EA9A8C`.
Full ranges, routing and evidence: [caller comparison](history/CALLER_BACKOFF_20261010.md).

### Partly cached requests: accounting first

Today a staged ordinary read with one missing block reads the whole request,
up to 16 MiB, from the lower device. RAM overlap can therefore waste disk bytes.
PR #9 adds request/byte/mixed-request/already-cached-byte counters before lower
submission, including failed lower attempts. It does not change read behavior.

The plan-105 patterned NTFS scenario completed ten byte/accounting checks on
0.4.495.1, timing off/on: a 1 MiB partly cached request held 131,584 cached bytes
(12.55% of its lower traffic); full misses held zero; cross-sector/full hits and
overwritten hits staged nothing. Every byte matched and restoration completed.
Exact run `20261010-020527-79bbee34e94c47faa71b8b062523627a`.
This shaped percentage is not a measured speed-up. All fitting caller-comparison
windows staged zero bytes, so they do not establish real churn waste.

The focused plan-113 `cache-sustained` follow-up completed 6/6 on signed
0.4.514.1, run `20261010-075924-c08eff16b204482fb371f9d09265f56e`,
07:59:24–08:09:02 UTC. It uses a 512 MiB Fast/Idle cache, a 1 GiB random 64 KiB
70/30 mixed file at Q8, six 20-second mixed episodes, twenty seconds natural idle
per episode, then Q1/Q8 1 MiB rereads of a fitting 256 MiB file. Read recall 1,
copy flags 3 and timing off; no clears/reallocations between episodes. Q8 follows
Q1 recovery rather than independently starting from the same churn state.

| Episode | Mixed throughput MiB/s | Mixed lower-byte overlap | Q1 recovery MiB/s | Q1 lower-byte overlap | Q8 recovery MiB/s |
|---|---:|---:|---:|---:|---:|
| 1 | 20.43 | 0.000% | 9,803.30 | N/A: no staged reads | 34,511.99 |
| 2 | 39.20 | 0.000% | 8,842.36 | 0.797% | 35,422.08 |
| 3 | 54.84 | 0.378% | 8,206.19 | 1.186% | 33,317.88 |
| 4 | 61.80 | 0.762% | 7,224.88 | 0.562% | 32,282.22 |
| 5 | 78.92 | 1.162% | 6,924.28 | 0.283% | 31,600.60 |
| 6 | 85.91 | 1.185% | 6,438.26 | 0.151% | 30,531.77 |

Weighted across all six mixed boundaries: 51,854 staged attempts / 3,398,303,744
lower bytes, 686 partly cached requests and 26,779,648 already-cached bytes:
**0.788% of staged lower traffic**. Across Q1 recovery: 1,398 attempts /
1,459,617,792 bytes, ten partly cached requests and 6,553,600 cached bytes:
**0.449%**. All Q8 recovery windows staged zero bytes. Percentages use sums of
bytes, not averages of per-episode percentages. Boundaries enclose process/oracle
activity, not just DiskSpd's score or a file-attributed trace.

All 18 mixed/reread XML outputs, counter boundaries, readiness/coverage and native
identity were inspected. Maximum telemetry gap: 0.752202 seconds. All 215 owned
process exits are zero; 1,539 independent concurrent 1 MiB write/read checks and
all 24 disabled-cache persisted oracle files passed. Original zero-budget lab
state and backoff 256 restored with zero errors/dirty/in-flight bytes. Raw archive
SHA-256: `6C0C5D819CD52F5A16C1FEC035C738F49E03A7A0A70F0C2840E8535F86104BAC`.
This has 120 seconds of mixed activity and is an accounting smoke, not another
30-minute sustained-use acceptance or a mode-comparison performance A/B.

**Decision:** do not implement missing-span reads for these measured shapes.
Even eliminating every overlapping byte would remove under 1% of aggregate
staged traffic, before extra lower-request/synchronization costs. That byte
fraction is not an exact throughput bound: storage latency, request count and
coalescing also matter. Other shapes, especially smaller scattered updates followed
by larger reads, could differ; reopen only when representative evidence shows
material overlap. The patterned 12.55% scenario proves counter correctness and
does not override the natural-churn result.

Missing-span reads should be considered only if real overlap is material.
Measure run counts and merge slack as well as saved bytes: several fragmented
small lower requests can cost more than one larger request. Preserve pins,
write ordering, partial-sector validity, error and cancellation semantics.
Full-hit/full-miss controls must not regress. Paging reads remain deliberately
outside ordinary cache admission; Windows' file cache has its own coherence
boundary. Historical unsafe paging admission is not reopened by these counters.

### Writes, stream placement, maps and ideas not pursued

| Item | Finding | Decision / limit |
|---|---|---|
| Complete PR #8 write reference | 72/72 on 0.4.476.1; cached random medians +1–7%, sequential Q1 +29–32%, Q8 −5–14% versus the historical 0.4.426.1 run | Q8 ranges overlap; disk-only Q8 also falls 243.46 → 84.92 MiB/s. Cross-day difference does not establish allocator/PR causality. Retain the complete current reference. |
| Contemporary RAM-only writes | Deferred/Eager Q8 20,603/20,491 MiB/s within 0.5%; PR #8 same-build comparison within 0.6% of its predecessor | Different payload contract (`-Z1M`) from the 72-case matrix (`-Zr`); do not substitute one for the other. |
| Coalesced write copies | About +1.2–1.8% Q1; −2.7% to +8.4% Q8 noise; did not close the historical gap | Removed. No demonstrated benefit warranting changed placement/copy logic. |
| Multiple separate streams | 37/37 matrix: four streams at total Q8 about 2.8% below one for writes, 7.8% for reads with overlapping ranges; 99.6–100% in-chunk order before scoring | Per-stream open chunks not justified. These measurements do not attribute the difference to allocation. |
| Idle relocation/defragmentation | Earlier post-churn upper bound about 5–7% Q1/10–15% Q8; later slow reread was missing data, despite good resident order | Not built. Read recall addresses the measured admission problem without moving pinned/draining data. Reopen only for a measured resident-placement bottleneck. |
| Native full-map polling, 2 GiB | Off/2 s/250 ms medians 1,389.55/1,383.92/1,399.04 MiB/s; steady map about 1.4–1.6 ms | Two-second polling has no measurable throughput cost; ranges overlap. |
| Native full-map polling, 4 GiB | Off/2 s/250 ms medians 1,353.10/1,358.05/1,304.80 MiB/s; steady map about 2.4 ms | Two-second polling again has no measurable cost; fast cadence about 3.6% lower with nearly overlapping ranges. |
| 100 ms UI refresh/full-screen map | Frontend contracts and actual Windows controls pass; shared sampling prevents overlapping map requests, hidden/minimized views release demand | Rendering cost at 100 ms is unmeasured. Default remains one-second app refresh/two-second map refresh. 32 GiB fixtures validate display/grouping, not a real 32 GiB allocation on the 16 GiB VM. |
| Map colors/order percentage | Darker means fuller total occupancy, lighter means partly used. Orange means some dirty data; order measures neighboring cached disk blocks inside a chunk | Color is not temperature or age; order is not whole-file fragmentation or proof of adjacent physical pages. Grouped squares can represent multiple chunks. |
| Large pages | Shuffled ascending chunks were within 4% of fresh; no controlled large-page A/B or TLB profile | Deferred, not disproven. No current evidence justifies adding allocation complexity. |
| Old-build downgrade | Old installer cannot interpret newer provider state and refuses safely | Do not force reinstall to obtain a historical comparison. Use same-build diagnostic switches for future causal A/B. |

The complete 24-configuration write table and exact raw identity are preserved in
[write reference](history/WRITE_PERFORMANCE_20261010.md), run
`20261009-220533-0439cff369ce45c8b577e23951b77317`, 22:05:33–22:59:38 UTC.
Scoring is ten seconds per case; preparation/drains, including 45–53 second
flushes, account for much of the roughly 54-minute runtime.
Map/concurrency methods and full ranges are in
[sustained validation](history/SUSTAINED_CACHE_VALIDATION_20261009.md).

The refreshed README uses actual CDM 9.0.3 GUI runs on 0.4.476.1: Default
profile, five 1 GiB passes, better of two runs per column, normal priority,
Verifier off and `drop-clean` before each complete run. All columns were measured
the same evening without a restart. These are decimal MB/s, not medians from
the verification matrices:

| GUI row, MB/s | Q: cache off | Q: 2 GiB Fast cache | 4 GiB RAM disk |
|---|---:|---:|---:|
| SEQ1M Q8T1 read | 594 | 39,492 | 25,353 |
| SEQ1M Q8T1 write | 112 | 21,504 | 23,266 |
| RND4K Q1T1 read | 9.0 | 1,292 | 1,641 |
| RND4K Q1T1 write | 1.2 | 1,031 | 1,317 |

On this Ceph-backed virtual disk, uncached sequential writes changed from
245 MB/s that morning to 112 MB/s that evening while cached/RAM-disk results
stayed within 6%. Historical comparisons need a contemporary disk-only control.
Leftover cached test data previously reduced reads to 5–20 GB/s by causing
misses; clearing clean data prevents this preparation confound. Windows startup
work and competing tests/UI must finish before scoring. The screenshots are
actual captures; frontend README images are generated by the desktop tests.
