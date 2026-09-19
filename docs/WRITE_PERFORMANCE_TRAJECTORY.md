# Write performance: implementation and verification sequence

Current execution/status source: [RAM_FIRST_IMPLEMENTATION_TRACKER.md](RAM_FIRST_IMPLEMENTATION_TRACKER.md).
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
