# Write performance: implementation and verification sequence

Baseline: installed CI driver de76288 / 0.4.37.1. Do not replace the driver while
the baseline is running. The focused runner can be published side-by-side without
changing the installed driver. Record CLI and driver provenance separately.

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
