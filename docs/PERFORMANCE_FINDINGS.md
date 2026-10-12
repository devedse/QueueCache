# QueueCache performance findings

This is the consolidated reference for performance findings and decisions from
the RAM-first, RAM-disk and cache-layout investigations through 2026-10-11.
Read it before proposing another experiment. Detailed methods and raw-evidence
identities are in the dated reports under [history](history/README.md); their
findings are consolidated here, including negative results and limits. How tests
are run is in [testing](TESTING.md); current gaps in [known issues](KNOWN_ISSUES.md).

**Current conclusion (2026-10-11): two speedups accepted, writes at the machine limit.**
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

## RAM-disk asynchronous split reads: accepted (PR #9)

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

## RAM-disk writes: no speedup available on this machine (plan 120)

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

## Cached reads: lighter read-hit bookkeeping accepted (plan 120)

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

## Which changes belong together

| Stage | Changes retained | Evidence and limits |
|---|---|---|
| Already on master before PR #8 | Direct RAM-disk access; large RAM-disk writes use the Standard provider path; provider split-copy helpers; 256 KiB chunk allocator; prefetch and bounded in-chunk RAM-cache hit copies | These earlier optimizations were measured separately. They were not introduced by PR #8 or rejected by PR #9. |
| Merged [PR #8](https://github.com/devedse/QueueCache/pull/8), merge `fb34f878581590123a190b048fdfd23c5d4e9b39` | Read recall; maintained concurrency, sustained-use and map-cost scenarios; neighboring-sector checks; map lifecycle/grouping; map pop-out/full-screen; 100 ms refresh preference and occupancy legend; runner cleanup; regenerated README benchmarks/screenshots | Read recall has controlled positive results and a documented temporary hit-rate trade-off. Full write matrix completed 72/72. Real Windows UI behavior was checked; 100 ms rendering cost was not benchmarked. |
| [PR #9](https://github.com/devedse/QueueCache/pull/9), `perf/ram-read-followups` | **Asynchronous split RAM-disk reads (default adaptive)**; partial-read diagnostics/scenario; caller dispatch attribution and bounded runtime lab control; RAM-reference verification and robust sampling; independent owned-child priority comparison; single-command campaigns; restoration, ownership and parser contracts; consolidated findings | 1M Q8T1 RAM-disk reads +79.84% in the accepted same-build comparison; lifecycle checks pass with and without Driver Verifier. Earlier PR #9 measurements: 10 partial-read checks, 36 RAM-reference windows, 54 RAM-queue comparison windows, 24 windows per NTFS/ReFS caller comparison and 36 priority windows. |
| PR #9 experiment removed | Shared sleeping queue for whole large Direct RAM-disk reads | Same-build comparison loses 3.62% Q1, 11.30% Q8 and 49.62% four-reader throughput. The native engine is removed; wire reservations remain for compatibility. |
| Plan 120, `perf/writes-cache-edge-cases` | **Lighter read-hit bookkeeping (copy flags 7)**; `ram-write-reference`, `cache-read-reference`, `cache-read-bookkeeping` and `stress-races` suites | Cached 1M Q8T1 +7.92% in the accepted same-build comparison. RAM-disk writes measured at the machine's memory write bandwidth. |
| Plan 120 experiment removed | Streaming (non-temporal) stores for large RAM-disk write copies | Same-build comparison: Q1T1 −0.59%, Q8T1 −0.09%, four writers −2.17%. Mode, provider actions 0x105/0x106 (reserved) and comparison suite removed. |

## Measurement rules

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

## Priority and background interference

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

## Earlier retained request-path improvements

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

## Cache allocation and copy optimizations

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

## Read recall: the retained PR #8 speed-up

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

## Dedicated RAM-disk access and rejected scheduling experiments

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

## RAM-disk affinity and source-lane controls (before asynchronous reads)

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

## Caller-path backoff: retain the production default

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

## Partly cached requests: accounting first

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

## Writes, stream placement, maps and ideas not pursued

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

## Correctness qualification

**Plan 120 stress suite.** `stress-races` runs both lab NTFS volumes at once:
writers of 4 KiB, 64 KiB, 1 MiB and 4 MiB blocks and an overlapped writer/reader
whose requests are cancelled at random, every read checked per 4 KiB sector
against the versions written, while a chaos actor per volume flushes, resizes,
tears down, drops clean data, slows the disk, injects transient allocation
failures, applies refused and rolled-back settings and switches to Strict, and
owned 2 GiB RAM disks are created and removed against the same reservation. It
then faults each drain parallelism (1, 2, 4) with dirty data, requires reads to
fail rather than return data, retries, and reads every byte back from disk.

Its first long runs found one real driver bug and three of its own: (1) **stale
cached data after large partly cached reads** (fixed in plan 120, see
[known issues](KNOWN_ISSUES.md#fixed-issues)): with retained writes off, a read
over 1 MiB fetched the range from disk while the dirty version of some blocks
finished draining, and kept those blocks from the older disk bytes. Under Driver
Verifier this gave 38 stale reads in 90 s, always whole 256 KiB drain batches of
an older version in 4 MiB blocks, with no unwritten (poisoned) sectors and the
disk verified correct; it reproduced with copy flags 3, so it predates the
bookkeeping change. After the fix, two 300 s busy windows under Verifier checked
about 390,000 reads with no stale data. (2) A RAM churn disk was stopped with a
generation read before its oracle was written. (3) The "refused budget" request
was sized from the free reservation, which a concurrent RAM disk removal could
enlarge, so the driver correctly accepted it. (4) Weighted random actions left
rare ones out of slow Verifier windows; actions now run in shuffled rounds. Wiring
it into campaigns also exposed three runner gaps, now fixed: restoration accepted
deliberately raised lab-fault error counts only for `ordering-faults` (and
`verify-recover` never did); later phases compared against the preflight baseline
instead of the one updated after such errors; and the final capture of the backing
volume could catch the lab image's late writes right after its drain.

**Final Driver Verifier run:** the focused campaign with `stress-races`
(`QueueCache-Campaign-20261011-214930-07f2ed10a5cf42978fbf1cc002c0db35`, signed
0.4.601.1 drivers with the stale-read fix, runner from the final source) completed
**6/6 phases, restored**: partial reads, paging coherence, policies, pressure,
`stress-races` and ordering faults all PASS. Its busy window ran 312 s with
254,894 (W:) and 182,739 (V:) verified reads, 268,875 and 198,640 cancelled
overlapped requests, every chaos action at least three times per volume and 11
RAM disks created and removed; fault and Retry passed at drain parallelism 1, 2
and 4, and the shared reservation returned exactly to its start. Later builds
change only managed code. Without Verifier on the final signed 0.4.604.1
(`QueueCache-Campaign-20261011-221957-8b19fe05c36b4caabb02c12e5149f237`, COMPLETED,
restored) the 306 s window checked 431,437 (W:) and 342,337 (V:) reads, cancelled
362,277 and 308,174 overlapped requests, ran every chaos action at least seven
times per volume and churned 23 RAM disks, with every check passing.

Current: the final `release-performance` regression on 0.4.583.1 passed all 118
retained correctness sub-checks (see the accepted change above). Earlier, the
signed 0.4.514.1 native driver and managed `1788970` preview completed
these maintained plan-113 scenarios on the owned NTFS lab V:. Each is an
independent run with one outer scenario, not a combined performance matrix.
Every inner check is PASS; no check was skipped. Raw reports, worker replies,
native hashes, ownership/exits, control/restoration traces and exact original
settings were inspected. Each has four successful owned-process exit records.

| Suite | Inner checks | Exact run ID suffix | Runtime UTC |
|---|---:|---|---|
| `partial-read-accounting` | 10 | `20261010-081109-aad11964b4d8470e9573e64f19b7787a` | 08:11:09–08:11:15 |
| `paging-coherence` | 8 | `20261010-081454-87a8b7c777be4ea19ccdc2c653758b83` | 08:14:54–08:15:07 |
| `policies` | 42 | `20261010-081712-d0b3498f7f144372871f769fc7c0ec91` | 08:17:12–08:18:52 |
| `pressure` | 13 | `20261010-082056-811b8c23b4c4417088273d84fdf83686` | 08:20:56–08:21:42 |
| `ordering-faults` | 8 | `20261010-082430-0afa9979205a4ea6b2988c9ad7458685` | 08:24:30–08:24:54 |

Coverage includes partial sectors, mapped/cached coherence, observed and gated
old-write ordering, parallel pinned copies, zero-lower-I/O fitting admission,
retention/policy variants, capacity backpressure, failed/short drains, cancellation,
allocation retry/exhaustion, release under load and paging-write failures. The
fault suite deliberately increments the lab error counter by three; final state
is non-faulted, with last error zero. Other suites have zero errors. All restore
disabled/zero-budget state, original options, no pending bytes and backoff 256.
This qualifies retained behavior at default 256; it does not qualify a new
request-size policy or the removed queue's lifecycle paths.
Immutable five-run raw archive SHA-256:
`3E475F93750E588D0A172DF09F4F5C991384B77027D763AB9CA642E789900DFD`.

Final cleanup detached both owned lab VHDX files and resumed Q: runtime-only on
its original active 2 GiB Fast/Idle configuration, with no errors or pending bytes.
Saved profile text matches the before capture exactly; the installed tray is
running again. Private evidence and lab files remain preserved.

## Recorded verification time

These selected completed runs total approximately **8 hours 38 minutes**. This
is measured run time, not a reconstruction of all engineering work or every
earlier investigation. Installation/restarts, CI, coding, analysis, incomplete
attempts and inactive conversation gaps add time. The NTFS/ReFS gap from 04:54
to 06:55 was not two hours spent executing that benchmark.

| Completed run | Wall time |
|---|---:|
| PR #8 write matrix, 72 windows | 54 min 05 s |
| Original partial-read accounting | 7 s |
| RAM reference, 36 windows | 15 min 29 s |
| RAM queue comparison, 54 windows | 22 min 37 s |
| NTFS caller comparison, 24 windows | 19 min 31 s |
| ReFS caller comparison, 24 windows | 20 min 08 s |
| Complete priority retry, 36 windows | 19 min 03 s |
| Traced priority subset, four windows | 2 min 38 s |
| Focused churn, six episodes | 9 min 38 s |
| Five retained-driver scenarios | 3 min 09 s |
| Plan-114 campaign smoke, two phases | 59 s |
| Plan-114 focused campaign, 81 correctness checks + 30 RAM windows | 17 min 21 s |
| Controller/restoration retry smoke, two phases | 1 min 07 s |
| Plan-116 RAM attribution, 81 correctness checks + 3 traced windows | 7 min 38 s |
| Plan-116 cached priority/affinity, 81 correctness checks + 36 untraced windows | 25 min 02 s |
| Plan-117 coordination diagnostic, nine windows | 4 min 33 s |
| Plan-117 controller smoke, two phases | 1 min 18 s |
| Wake-up smoke, two phases | 1 min 03 s |
| Plan-118 asynchronous-read comparison, 18 windows | 8 min 50 s |
| Plan-119 revision comparison, 18 windows | 8 min 52 s |
| Plan-119 lifecycle checks, with / without Driver Verifier | 1 min 10 s / 55 s |
| Final `release-performance` regression, 11 phases / 106 cases | 1 h 56 min 23 s |
| Plan-120 cached-read attribution, 18 windows | 9 min 45 s |
| Plan-120 RAM write reference, 36 windows | 6 min 43 s |
| Plan-120 streaming-store comparison, 18 windows | 5 min 30 s |
| Plan-120 read-bookkeeping comparison, 18 windows | 9 min 57 s |
| Plan-120 Driver Verifier campaign with `stress-races`, 6 phases | 10 min 56 s |
| Plan-120 `stress-races` without Verifier | 5 min 55 s |
| Plan-120 final `release-performance` regression, 11 phases / 106 cases | 1 h 47 min 34 s |

The original priority run's main scores total about six minutes within its nineteen-minute
run. Preparation, residency proofs, warmup, worker/process ownership and
restoration account for the rest. The write matrix includes explicit 45–53
second drains between some ten-second scores. A single command removes repeated
agent orchestration; it does not remove required disk preparation or durability
checks. The [campaign plan](TESTING.md#3-running-tests-one-command) records phase times and
delivers terminal events so those costs become visible without periodic agent
polling.

The latest two plan-116 campaigns together take **32 min 40 s**. The cache
priority/affinity phase accounts for 20 min 29 s of its 25 min 02 s campaign;
the RAM trace phase takes 2 min 40 s of its 7 min 38 s campaign. The surrounding
policy/pressure checks, preparation and restoration explain much of the remaining
time. Tool/bootstrap failures and engineering/CI time remain separate from these
completed measurements.

Not counted above (preserved separately; none contributes a result): the Driver
Verifier run that bugchecked (0x3B, fixed before measurement), a Verifier run and
a lifecycle run stopped by over-strict activation checks, one comparison refused
at start because Q: still had unwritten data, and the first final regression,
which stopped after 33 minutes in the sustained phase: a 197 GB resident reread
saw six background metadata reads (4.5 KiB) on Q:, most likely the Everything
file indexer that had restarted at sign-in. Everything and Steam were closed and
the complete regression rerun passed. Plan 120 adds the first RAM write reference run
(stopped by its oracle rewrite bug), 13 stress, Verifier and stale-data diagnosis
runs that found and fixed the issues described under
[correctness qualification](#correctness-qualification), and their recoveries.

## Current decisions and when to repeat tests

| Topic | Decision | Reopen when |
|---|---|---|
| Asynchronous split RAM-disk reads | **Accepted, default adaptive** (1M Q8T1 +79.84%; Q1 and four readers unchanged). | Changes to provider workers, the Direct read path or chunking; a `ram-read-async` or `ram-read-async-lifecycle` regression. |
| Cached read-hit bookkeeping (copy flag 4) | **Accepted, default on** (1M Q8T1 +7.92%, Q1T1 +4.52%, four readers +3.46%). | Changes to the read-hit path, pinning, recency lists or the slot index; a `cache-read-bookkeeping` regression. |
| Large RAM-disk writes | **No speedup available on this VM**: four writers ≤ +5% over one; queued writes already exceed user-mode copy rates into cold memory. Split writes (September) and streaming stores (plan 120) both rejected. | Different hardware with a measured gap between `ram-write-reference` disk writes and its memory-copy rows. |
| Inline split RAM-disk reads | Retained for queue depth 1, several submitters, paging and synchronous callers. | As above. |
| Shared whole-read queue (and the two earlier whole-read offloads) | Removed: each gave a whole 1 MiB read to one worker. | Not as such. |
| Caller backoff | 256. A selective NTFS/ReFS policy is closed without an experiment: best measured gain +5.26% (NTFS mixed Q8), +3.79% (ReFS), at the 5% bar, while touching ordering/flush paths. | New workload evidence of a material gain. |
| Missing-span reads | Not built: overlap 0.788% / 0.449% of lower traffic in churn. | Representative evidence of material waste. |
| Low-priority cache slowdown | Benchmark-setup effect; launch benchmarks at normal priority (Task Scheduler priority 4). | An observed normal-priority problem. |
| 100 ms map refresh cost | Not measured further at the owner's request; native map reads take milliseconds. | Owner request. |
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
