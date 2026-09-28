# Write performance: implementation and verification sequence

Current execution/status source: [RAM_FIRST_IMPLEMENTATION_TRACKER.md](RAM_FIRST_IMPLEMENTATION_TRACKER.md).

## CrystalDiskMark rows after the request-path work, 2026-09-27

CrystalDiskMark 9.0.3's DiskSpd (same binary as below) in CrystalDiskMark's four
default shapes on a 1 GiB file created once, 5 s runs, warm cache (the file read
twice first), Q: 2 GiB Fast/Idle, parallelism 2, Driver Verifier off. MB/s,
range of three runs:

| Row | 0.4.151.1 (before) | 0.4.162.1 (`98a7d03`) |
|---|---|---|
| SEQ1M Q8T1 read | 14,337-14,437 | 35,949-36,510 |
| SEQ1M Q8T1 write | 12,635-13,954 | 20,508-21,483 |
| SEQ1M Q1T1 read | 9,025-9,171 | 14,475-14,606 |
| SEQ1M Q1T1 write | 8,701-9,148 | 13,073-13,354 |
| RND4K Q32T1 read | 1,274-1,321 | 1,489-1,523 |
| RND4K Q32T1 write | 146-1,265 | 1,240-1,285 |
| RND4K Q1T1 read | 100 | 974-981 |
| RND4K Q1T1 write | 77-79 | 848-860 |

What changed, in order, each measured on the VM before the next:

1. Caller-thread service (0.4.153.1): a RAM hit or fitting write on an otherwise
   idle disk is served on the submitting thread. A queued 4 KiB request waited
   about 11 us for the worker to wake, for about 1.4 us of cache work, plus a
   cross-thread completion. Q1 random 4 KiB: 24,500 -> 238,000 reads/s,
   22,000 -> 190,000 writes/s.
2. Unused drainers no longer wake on every write (0.4.154.1). Drainers above
   the configured parallelism cleared the shared wake event, so each cached write
   re-signalled it and woke them to contend for the cache lock (180,000 wake-ups
   in 5 s). Random Q32 writes collapsed to about 35,000/s whenever write-back
   was running. This is the "random Q32 gap": 0.4.148.1's `write-performance`
   Q32 medians (34,066-34,400 IOPS) sat exactly at this collapse.
3. Deep queues stay on the worker (0.4.156.1-0.4.158.1): at Q32 the worker
   overlaps with the submitter and is faster than caller-thread service
   (333,000-356,000 vs 217,000-236,000 reads/s). Every 1024th caller-path
   candidate goes to the worker, and one that finds it busy keeps the next 256
   there.
4. Parallel copies. 0.4.158.1: reads of 256 KiB or more fully in RAM run on three
   offloaded-request threads, but SEQ1M Q8 stayed at 15.2 GB/s with the CPUs half
   idle: 9.7 s of cache-lock waits in a 5 s run for 0.7 s held. 0.4.159.1
   (`df4904f`): a pinned read copies its whole range after one lock release
   (3 instead of 6 acquisitions per request; 1.4 s of waits): 21.9-23.8 GB/s.
   0.4.161.1: the cache lock is an exclusive push lock instead of a KMUTEX, whose
   hand-off to a sleeping waiter convoyed the threads (reads 34.5-36.2 GB/s), and
   the payload copy of fitting writes of 256 KiB or more also runs on those
   threads (writes 20.9 GB/s).
5. The idle worker polls for 30 us before sleeping (0.4.162.1), restoring random
   Q32 writes to about 310,000/s.

0.4.166.1 (`233b501`, which stops keeping page-in misses; see KNOWN_ISSUES)
measured the same: SEQ1M Q8 35,158-37,071 / 20,845-21,444, SEQ1M Q1
14,249-14,669 / 13,192-13,529, RND4K Q32 1,531-1,566 / 1,199-1,310, RND4K Q1
944-972 / 840-856 MB/s.

For reference, a plain user-mode copy on this VM moves 28.6 GB/s on one thread
and 33 GB/s on four, so SEQ1M Q8 reads are now at the VM's memory-copy speed. SEQ1M Q1 is one
request at a time on the caller's thread and stays near one copy's speed.

## CrystalDiskMark SEQ1M Q8T1 and the single-worker ceiling, 2026-09-27

Measured with CrystalDiskMark 9.0.3's DiskSpd (SHA-256 `7281BF6D...1079`) using
its SEQ1M Q8T1 shape (1 GiB file created once, unbuffered 1 MiB, 8 in flight,
5 s runs, best of three), Q: 2 GiB Fast/Idle, Driver Verifier off. A bisect over
releases 0.4.51.1-0.4.151.1 found no regression: every build measured about
14.9-15.2 GB/s read and 14.4-14.6 GB/s write (0.4.151.1 with the owner's saved
profile: 14,953 / 14,413 MB/s). Two things produced lower numbers: standard
Driver Verifier left enabled (about 6 GB/s), and a cache filled with stale
blocks from deleted test files, which this TRIM-less disk cannot release (about
10.5 GB/s).

More application threads do not raise throughput: T1/T2/T4 at Q8 gave
14.7/13.4/12.3 GB/s read and 14.1/13.5/13.4 GB/s write on the 4-vCPU VM. Every
request's data copy runs on the single request worker, so large transfers are
capped near one core's memory-copy speed. Exceeding it needs data copies that run
in parallel (see NEXT_PHASE_PLAN).

## Current baseline: 0.4.148.1, 2026-09-27

`write-performance` on installed 0.4.148.1 (`97b8f4f`), Driver Verifier off,
Microsoft DiskSpd 2.3 (`amd64\diskspd.exe`, SHA-256 `DD4E57E1...FAEA2`), 2048 MiB
budget, three repeats, Q: (`QueueCache-Verify-20260927-173048-4b4a8a48...`,
72/72 COMPLETED). Medians, detailed timing off:

| Workload | Off | Eager | Idle | Idle vs Off | Write p99 Off -> Idle |
|---|---:|---:|---:|---:|---|
| Random 4 KiB Q1 | 1,081 IOPS | 19,446 IOPS | 19,359 IOPS | ~18x | 2.48 -> 0.12 ms |
| Random 4 KiB Q32 | 1,794 IOPS | 34,066 IOPS | 34,400 IOPS | ~19x | 84.4 -> 1.32 ms |
| Sequential 1 MiB Q1 | 113 MB/s | 4.84 GB/s | 4.84 GB/s | ~43x | 12.8 -> 0.32 ms |
| Sequential 1 MiB Q8 | 246 MB/s | 7.87 GB/s | 7.92 GB/s | ~32x | 67.2 -> 1.67 ms |

Detailed timing changes results by at most a few percent. This uses a different
DiskSpd binary from every earlier row on this page (CrystalDiskMark's DiskSpd
2.2), so it is a new baseline, not a before/after comparison. Earlier builds
measured random Q32 up to about 74,500 IOPS with the CDM binary; whether the gap
is the binary, the VM or a regression needs the old build measured with this
binary.
The original de76288 / 0.4.37.1 baseline is historical. The fresh 2026-09-19
baseline on e8b37be / 0.4.40.1 stopped during case 4's explicit dirty-data drain;
three measured rows are not an accepted 72-case baseline. Restoration succeeded.
The user approved a 600-second preparation-flush deadline, explicitly versioned
in plan 7. That run collected all 72 cases; final restoration failed with late
dirty writes visible, then a separate supported recovery passed. See the tracker
for both unique IDs and the retained caveat. Score windows, matrix, readiness
and independent restoration deadline are unchanged. This is a complete measured
reference, not a restoration-clean PASS or performance acceptance. The runner can be published
side-by-side; record CLI and driver provenance separately.

1. Run `write-performance` with 2048 MiB, the same CDM DiskSpd64 binary, three
   repeats and default duration. See DEVELOPER_VERIFICATION.md for the command.
   Verify completion, restoration, 72 unique measured rows and raw evidence.
2. Compare Off/Eager/Idle and detailed timing off/on separately. Report medians and
   spread, decimal MB/s, IOPS and write p99. Check the exact process/score interval;
   warmup and file-close draining are not measured throughput. No whole-run counter
   subtraction divided by ten seconds. Keep queue depths separate.
3. Attribute slow writes using existing phase/capacity/flush/selection counters.
   If whole-cache drain barriers dominate, add versioned reason counters and
   triggering request length/alignment before redesigning barriers. Partial writes,
   strict writes and management/media fences must not become indistinguishable.
   Do not replace disk-wide ordering with range ordering without overlap, failure,
   cancellation, flush and post-drain byte-oracle regression tests.
4. Harmless hotplug-information queries are now allowlisted as observations; the
   corresponding setter remains fenced. After baseline completion, test a CI build
   with this fix using `flush-interference --repeats 3`, then the same write suite.
   This is a scheduler fix, not a promise of faster isolated 4 KiB writes. Check
   fence counters and IOCTL code as well as reader throughput. Do not relax actual
   flush, TRIM, pass-through or media-change fences.
5. If small writes remain CPU-bound, measure snapshot publication, block lookup,
   lock wait/hold and worker transitions before changing them. If disk waits
   dominate, prioritize their causes instead. Preserve acknowledgement semantics.
6. Address cold-read/background-drain competition with foreground-aware scheduling
   and bounded drainer progress, not unconditional drain starvation. Use the
   existing mixed/cold cases and slow-device hot-reader cases to catch regressions.
   General multi-worker/range-ordering redesign is a later decision, not required
   just to fix a harmless query or repeated broad drain barriers.

Acceptance: unchanged integrity results, no errors or restoration failures,
repeatable gains beyond sample spread, no new starvation, and no reduced durability
semantics hidden behind a higher score. A successful collection is not acceptance.
Never combine partial runs or claim the entire eleven-scenario historical
concurrency suite was exercised by `quick`/`policies`.

## 2026-09-19 measured reference

Run `QueueCache-Verify-20260919-101745-c820e051450f4f3b8f511f42a9aaf595`,
plan 7, e8b37be / 0.4.40.1, 2048 MiB budget, 1 GiB resident file,
10-second measured spans and the same CDM DiskSpd SHA-256
`7281BF6DA6C03797016EDDF2E8AAEC4C644AE893D403D57A030B7E2E14B61079`.
All 72 raw XML scores and telemetry coverage checks passed. Original status:
RESTORATION_FAILED; separate recovery: RESTORED. See the tracker for details.
Do not relabel the original run or equate collection with correctness/performance
acceptance. These values are not a before/after optimization result.

Each cell reports median [minimum, maximum] across three repetitions, calculated
only from DiskSpd score bytes/time and write latency. MB/s is decimal. Process
intervals additionally contain warmup and teardown; no lifetime counter delta
was divided by the 10-second score duration. Off means cache routing disabled,
not the Deferred background-drain algorithm.

| Workload | Policy | Timing | MB/s median [min, max] | Write p99 ms median [min, max] |
|---|---|---|---:|---:|
| Random 4 KiB Q1 | Off | Off | 1.27 [1.22, 1.65] | 10.257 [7.292, 10.814] |
| Random 4 KiB Q1 | Off | On | 1.21 [1.16, 1.26] | 8.907 [8.332, 13.960] |
| Random 4 KiB Q1 | Eager | Off | 87.38 [84.83, 88.19] | 0.076 [0.076, 0.083] |
| Random 4 KiB Q1 | Eager | On | 86.54 [86.22, 87.20] | 0.077 [0.077, 0.079] |
| Random 4 KiB Q1 | Idle | Off | 86.78 [81.06, 88.12] | 0.076 [0.075, 0.084] |
| Random 4 KiB Q1 | Idle | On | 80.72 [79.88, 86.44] | 0.082 [0.078, 0.085] |
| Random 4 KiB Q32 | Off | Off | 3.32 [2.62, 4.11] | 298.056 [236.682, 466.530] |
| Random 4 KiB Q32 | Off | On | 3.67 [2.92, 4.21] | 253.722 [188.003, 352.274] |
| Random 4 KiB Q32 | Eager | Off | 112.92 [112.35, 113.04] | 1.517 [1.506, 1.533] |
| Random 4 KiB Q32 | Eager | On | 105.10 [103.64, 106.49] | 1.621 [1.550, 1.656] |
| Random 4 KiB Q32 | Idle | Off | 113.30 [110.17, 113.38] | 1.507 [1.486, 1.535] |
| Random 4 KiB Q32 | Idle | On | 105.89 [104.32, 105.96] | 1.597 [1.546, 1.602] |
| Sequential 1 MiB Q1 | Off | Off | 53.53 [39.74, 60.19] | 139.001 [128.418, 290.863] |
| Sequential 1 MiB Q1 | Off | On | 54.79 [48.92, 66.20] | 140.693 [71.544, 196.644] |
| Sequential 1 MiB Q1 | Eager | Off | 5390.62 [5122.52, 5398.17] | 0.276 [0.270, 0.323] |
| Sequential 1 MiB Q1 | Eager | On | 5228.11 [5017.86, 5338.20] | 0.293 [0.287, 0.308] |
| Sequential 1 MiB Q1 | Idle | Off | 5293.27 [5218.26, 5431.73] | 0.274 [0.263, 0.303] |
| Sequential 1 MiB Q1 | Idle | On | 5285.35 [5186.00, 5402.94] | 0.294 [0.276, 0.301] |
| Sequential 1 MiB Q8 | Off | Off | 90.49 [80.22, 96.16] | 475.802 [395.276, 535.037] |
| Sequential 1 MiB Q8 | Off | On | 106.95 [80.11, 129.79] | 355.513 [259.105, 621.660] |
| Sequential 1 MiB Q8 | Eager | Off | 8920.45 [8871.83, 9303.28] | 1.270 [1.245, 1.276] |
| Sequential 1 MiB Q8 | Eager | On | 8597.38 [8391.86, 9038.18] | 1.293 [1.256, 1.369] |
| Sequential 1 MiB Q8 | Idle | Off | 8878.75 [8811.81, 9313.45] | 1.220 [1.215, 1.272] |
| Sequential 1 MiB Q8 | Idle | On | 8766.66 [8742.08, 9055.92] | 1.299 [1.292, 1.304] |

Timing-off random medians are 21,333/21,187 IOPS (Q1 Eager/Idle) and
27,569/27,662 IOPS (Q32 Eager/Idle). Q32 detailed timing reduces the observed
median throughput by about 6.9%/6.5%, with non-overlapping throughput ranges in
this run. Keep timing modes separate during optimization; do not treat diagnostic
overhead as a release regression. This does not isolate its CPU or locking cause.
Off-mode storage throughput and tails vary widely. Fast cached sequential numbers
represent volatile RAM acknowledgement/repeated overwrites, not durable disk speed.
