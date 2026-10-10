# QueueCache performance findings

This is the consolidated reference for performance findings and decisions from
the RAM-first, RAM-disk and cache-layout investigations through 2026-10-10.
Read it before proposing another experiment. The
[implementation tracker](RAM_FIRST_IMPLEMENTATION_TRACKER.md) remains the
execution/status source of truth; implementation and verification are separate.
The dated reports preserve detailed methods and historical evidence. Their
findings are consolidated here, including negative results and limits.

**Current conclusion:** retain the ordered chunk allocator, bounded RAM-hit
copy optimizations, synchronous RAM-disk split reads and read recall. The newer
shared whole-read queue was slower and has been removed. Shortening caller
backoff benefits mixed Q8 but harms large NTFS reads, so production backoff stays
256. Partial-read counters are implemented; missing-span reads are conditional.
The independent 36-window priority comparison is complete: CPU BelowNormal loses
34.20% resident Q8 throughput. Focused churn accounting is also complete and finds
under 1% aggregate overlap in lower traffic for the tested shapes. CPU/wait
attribution is complete and points at scheduling/utilization. Thirty RAM-disk
affinity/source-lane controls are now complete: they leave most of the single-
versus multi-submitter gap intact. Request concurrency and copy-helper handoff
cost remain the next investigation; cache CPU-priority/affinity interaction is
a separate untested question. All five retained-driver qualification scenarios
completed with their raw checks and restoration inspected.

Plan 114 now implements the maintained single-command campaign and durable
completion event; automatic Manager wakeup remains external. A new focused
RAM-disk comparison holds copying fixed and isolates affinity plus source-stream
overlap, addressing the earlier queue experiment's combined changes. Windows contracts, Debug/Release CI and the six-phase focused VM campaign pass.
The controls do not establish a production speed-up; no driver/default change
is accepted. The tracker records implementation and verification separately.

## Which changes belong together

| Stage | Changes retained | Evidence and limits |
|---|---|---|
| Already on master before PR #8 | Direct RAM-disk access; large RAM-disk writes use the Standard provider path; provider split-copy helpers; 256 KiB chunk allocator; prefetch and bounded in-chunk RAM-cache hit copies | These earlier optimizations were measured separately. They were not introduced by PR #8 or rejected by PR #9. |
| Merged [PR #8](https://github.com/devedse/QueueCache/pull/8), merge `fb34f878581590123a190b048fdfd23c5d4e9b39` | Read recall; maintained concurrency, sustained-use and map-cost scenarios; neighboring-sector checks; map lifecycle/grouping; map pop-out/full-screen; 100 ms refresh preference and occupancy legend; runner cleanup; regenerated README benchmarks/screenshots | Read recall has controlled positive results and a documented temporary hit-rate trade-off. Full write matrix completed 72/72. Real Windows UI behavior was checked; 100 ms rendering cost was not benchmarked. |
| Draft [PR #9](https://github.com/devedse/QueueCache/pull/9), `perf/ram-read-followups` | Partial-read diagnostics/scenario; caller dispatch attribution and bounded runtime lab control; RAM-reference verification and robust sampling; independent owned-child priority comparison; restoration, ownership and parser contracts; consolidated findings | 10 partial-read checks, 36 RAM-reference windows, 54 RAM-queue comparison windows, 24 windows per NTFS/ReFS caller comparison and 36 priority windows complete. CPU attribution/churn/correctness remain separate. No new RAM-disk read speed-up has been accepted. |
| PR #9 experiment removed | Shared sleeping queue for whole large Direct RAM-disk reads | Same-build comparison loses 3.62% Q1, 11.30% Q8 and 49.62% four-reader throughput. The native engine is removed; wire reservations remain for compatibility. |

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

Methods and preserved failure details: [benchmarking](BENCHMARKING.md),
[priority comparison](PRIORITY_COST_20261010.md),
[sustained validation](SUSTAINED_CACHE_VALIDATION_20261009.md),
[Verifier comparison](PERFORMANCE_INVESTIGATION_20260930.md).

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
Methods: [write trajectory](WRITE_PERFORMANCE_TRAJECTORY.md),
[sequential reproduction](SEQUENTIAL_PEAK_REPRODUCTION_20260930.md).

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
| Prefetch plus bounded runs | 20.85 / 39.36 | 19.34 / 37.71 | 19.32 / 37.17 | Retained as default flags 3 from 0.4.434.1; integrity/policy/pressure checks pass. |

These optimizations affect **RAM-backed cache hits on a disk volume**. A dedicated
RAM disk uses a different payload layout: locked pages and the provider's
existing `CopySplit`/`LargeCopy` helpers. Direct RAM-disk access already reuses
that provider copy path; there is no separate newly accepted port of the cache's
prefetch/coalescing loop into PR #9. Large copies are split into 256 KiB pieces
and can enlist provider helpers. A new whole-request queue changes submission
and completion scheduling, which must be tested independently of the copy loop.

Detailed evidence: [allocation/copy investigation](CACHE_LAYOUT_INVESTIGATION_20261008.md).

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
Methods/tables: [sustained validation](SUSTAINED_CACHE_VALIDATION_20261009.md).

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
reservations: [RAM scheduling](RAM_READ_SCHEDULING_20261010.md).

## RAM-disk affinity and source-lane controls: gap remains

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
controls; [orchestration evidence and failure history](PERFORMANCE_CAMPAIGN_PLAN.md).

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
Full ranges, routing and evidence: [caller comparison](CALLER_BACKOFF_20261010.md).

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
[write reference](WRITE_PERFORMANCE_20261010.md), run
`20261009-220533-0439cff369ce45c8b577e23951b77317`, 22:05:33–22:59:38 UTC.
Scoring is ten seconds per case; preparation/drains, including 45–53 second
flushes, account for much of the roughly 54-minute runtime.
Map/concurrency methods and full ranges are in
[sustained validation](SUSTAINED_CACHE_VALIDATION_20261009.md).

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

## Current retained-driver correctness qualification

The current signed 0.4.514.1 native driver and managed `1788970` preview completed
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

These selected completed runs total approximately **3 hours 5 minutes**. This
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

The priority run's main scores total about six minutes within its nineteen-minute
run. Preparation, residency proofs, warmup, worker/process ownership and
restoration account for the rest. The write matrix includes explicit 45–53
second drains between some ten-second scores. A single command removes repeated
agent orchestration; it does not remove required disk preparation or durability
checks. The [campaign plan](PERFORMANCE_CAMPAIGN_PLAN.md) records phase times and
delivers terminal events so those costs become visible without periodic agent
polling.

## What to do next and when to repeat tests

| Order | Next step | Result-driven action |
|---|---|---|
| 1 | Investigate request concurrency and split-helper handoffs with copying held fixed | Thirty normal-priority RAM controls complete: affinity/source overlap leave the 26 versus 38–39 GB/s gap. Profile coordination/completion, then consider an isolated asynchronous experiment retaining `CopySplit`; preserve Q1/small/four-reader and lifecycle checks. Cache CPU-priority × affinity remains separate and untested. |
| 2 | Keep missing-span work closed for the tested churn shapes | Accounting completed: weighted overlap is 0.788% mixed / 0.449% recovery, no staged Q8 reads. Reopen for representative evidence of material waste in other shapes, then quantify fragmentation and merge overhead. |
| 3 | Retain completed partial-read, paging, policy, pressure and ordering evidence | All 81 inner checks pass in five independently restored runs. Repeat affected checks for future code changes; no lifecycle qualification is needed for the removed queue engine. |
| 4 | Use the [single-command campaigns](PERFORMANCE_CAMPAIGN_PLAN.md); connect Manager delivery separately | Plan 114 composes maintained typed suites and publishes one durable completion event after evidence/restoration. Windows contracts, the 59-second smoke and the 17 min 21 s focused campaign pass. Automatic Manager wakeup needs its external completion API; no delivery is claimed. Complete that integration alongside performance attribution, using a short smoke rather than a broad matrix. |
| 5 | Gate the next native experiment on RAM attribution | Keep the copy algorithm fixed and change only a demonstrated coordination or request-concurrency cost. Use same-build alternating A/B and qualify new lifecycle paths. Missing-span reads stay deferred; request-size caller policy is a lower-priority conditional candidate. No promised single-reader 42 GB/s. |

The reviewed [continuation plan](PERFORMANCE_FOLLOWUP_PLAN_20261010.md#continuation-plan-after-findings-review)
sets the implementation order, proposed acceptance thresholds, completion-integration
checks and run budget. Cache priority/affinity and optional 100 ms Desktop cost
remain separate questions. This review adds no new measurements or production changes.

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

For RAM-disk coordination, use sampled/context-switch attribution to select one
specific handoff/wake/poll change. Keep existing synchronous split reads as the
same-build control; the retired queue is evidence to learn from, not a candidate
to re-enable without a different measured mechanism. Small/Q1/four-reader controls
and withdrawal/cancellation/stop checks precede acceptance. Priority changes to
user programs or unconditional driver boosts are not part of these plans.

Do not repeat an established experiment merely because its result was forgotten.
Repeat a relevant regression when native/runner behavior, hardware, power/CPU
placement, benchmark binary, allocation/copy settings, workload or measurement
contract changes, or when a new observation contradicts the finding. Use the
smallest maintained discriminating suite first; request a broad matrix only
when its scope is needed. Keep failures and raw evidence immutable. Update this
file and the tracker with conclusions, units, identities, limits and decisions.

## Plan 115 attribution hypothesis (implementation in progress)

The unchanged Direct path completes synchronously; provider split copying uses
256 KiB caller/helper work and helper withdrawal/waits. One submitter may be
limited by admission or coordination while several submitters keep more useful
copy work in flight. The maintained three-shape attribution suite samples CPU
and scheduler behavior with matching symbols, preserving the native copy path.
A higher WorkerMain share alone does not prove polling overhead: it contains both
copy and polling. Process intervals include startup/warmup; measured read bytes
remain separate. Exact request overlap and coordination timing remain unknown
without instruction mapping or scoped native counters. No new gain is claimed.

### Attribution bootstrap failures (no performance result)

The first launch rejected phase-only `--trace-symbols` during generic campaign
validation before allocating a campaign. Validation now scopes it to the selected
RAM phase, and all registered RAM suites share the same integrity dispatch.
These fixes pass Windows contracts and Debug/Release CI at `8997447`.

The next attempt, `QueueCache-Campaign-20261010-180641-82bbd17c82f2409c81c198801840e0a6`,
completed four retained correctness phases, then stopped while exporting the WPR
profile: `0x80070006` (invalid handle). No trace or score began. The same installed
WPR exported the same profile successfully to a short path; the nested campaign
filename exceeded its native path handling. The run remained INCOMPLETE (4/6),
took 212.45 s and restored cleanly. Its raw evidence remains separate.

WPR now exports/records/stops in a short unique journal-owned temporary directory;
managed I/O copies the profile and finalized ETL into the original run. Publishing
the ETL uses a same-directory rename after copying, retains the staging source
until publication, and resumes after a worker exits between stop and publication.
Only empty staging directories are removed; unexpected evidence is preserved.
Host contracts cover deep paths, cross-directory refusal and interrupted finalization.
This is a verification-tool compatibility fix, not a driver speedup.

Installed-tool smoke then proved collection/finalization at deep paths, but the
bundled native analysis reader rejected that deep path (`E_UNEXPECTED`). A short,
hash-verified clone makes the reader open the same ETL successfully. The next
smoke was correctly rejected for **45,918 lost events**; its original ETL remains
immutable. No attribution or performance conclusion comes from either smoke.
Plan 116 derives the installed CPU file profile with 128 × 1 MiB buffers per
collector (maximum two collectors/256 MiB configured pool capacity). It records
original/derived definitions and hashes plus actual collector status. Missing
samples remain failures; `AllowLostEvents` stays false. The enlarged diagnostic
pool is a recording-contract change, not a production/default speedup.
The enlarged-pool installed-tool smoke passes on Windows: 350 samples and 299
scheduler activities for its owned test PID, both collectors read back 128 ×
1 MiB buffers with zero lost events, and strict final ETL processing succeeds.
All Windows host contracts pass in that preview. Follow-up ownership hardening
gives each collector a unique name and enforces its startup buffer/loss readbacks.
The final smoke (`Wpr-Smoke-7bd5858fdd7e404ab7666acce19ce941`) passes with
351 owned samples and 409 scheduler activities, zero lost events and all Windows
host contracts passing. Windows Debug/Release build and CLI CI #552 pass at
`78ecfb2`. This qualifies collection/analysis tooling, not driver performance;
the actual three-shape diagnostic is tracked separately.

Schema-2 analysis retains native sampled/return-context addresses and available
source lines, with unknown process samples separate. `verify-attribution` reuses
a finalized owned run in a unique analysis folder: it checks the worker/plan,
original ETL digest and original PDB signature/hash without workloads or driver
access. Reanalysis preserves the original collection verdict. Machine-wide
samples and observed predecessor scheduler waits are not exact score-window CPU
or complete blocked/ready totals; resource-tagged request overlap remains unknown.

The separate priority/affinity campaign
`QueueCache-Campaign-20261010-181219-edcb92e1d3bd4a49877854b8675108c0`
stopped before benchmarking: the first sector policy case did not observe a clean
admission boundary after preparing a new NTFS file. It completed 2/6 phases in
47.44 s and restored cleanly. The writer/file was not attributed. Source review
found that the test drained only the cache after the setup write, allowing delayed
filesystem setup writes to arrive afterwards; this is a plausible preparation race.
Preparation now explicitly flushes the owned file before the existing cache
flush/drop-clean boundary; the unchanged guard also reports dirty/in-flight/error
values on failure. The same setup boundary is used for the observed replacement
case. This is a scoped test-preparation fix; the failed campaign contributes no
performance samples and the guard is not relaxed.

### Final campaign restoration gap found by controller smoke

The first plan-115 controller smoke completed both test phases in about 50 s,
but final restoration verification failed: the resumed Q: backing cache had
transient dirty filesystem metadata. The controller validated the nonzero exit's
`RESTORATION_FAILED` event; it did not label it successful or relaunch work.
Exact failed campaign: `QueueCache-Campaign-20261010-173916-42bc00c7467348efa904638243fd9ed3`.
Recorded backing restoration had completed, the native cache remained healthy,
and subsequent inspection found zero dirty/in-flight bytes. Finalization now
uses the existing explicit bounded filesystem/cache preparation on an enabled
performance baseline before the unchanged strict clean capture. Faults still
stop restoration; disabled observations do not drain. Regression smoke `QueueCache-Campaign-20261010-174542-93f8175618624be6b93d7d6d73f58100`
completed 2/2 phases in 67.19 s with exact clean restoration, 28 zero-exit owned
process records and nine readiness files. The real controller validated success
and failure events without rerunning work for delivery. Live Manager wake remains
unverified until the new service/helper is deployed.
