# Performance follow-up after sustained cache validation

[PR #8](https://github.com/devedse/QueueCache/pull/8) is merged. It establishes read recall, complete sustained/concurrent/map measurements,
README benchmarks and the map pop-out / 100 ms refresh controls. This follow-up
works on remaining measured limits. The tracker separates implementation from
verification; changes are retained only after controlled measurements and byte
checks. A larger aggregate score from four readers is evidence of available
bandwidth, not proof that a single reader can reach it.

The consolidated decisions, including earlier retained improvements and rejected
experiments, are in [PERFORMANCE_FINDINGS.md](PERFORMANCE_FINDINGS.md). The
[campaign/completion plan](PERFORMANCE_CAMPAIGN_PLAN.md) describes future
single-command orchestration; it is not an available command yet.

| Order | Problem and intended change | Implementation | Required evidence |
|---|---|---|---|
| 1 | RAM-disk SEQ1M Q8T1 has a lower score than four readers. Profile copying, synchronous completion and helper coordination before another scheduling change. | Reference and same-build A/B implemented; all 36 reference and 54 comparison windows pass byte/collection checks. The shared sleeping queue loses 11.30% Q8 and 49.62% four-reader throughput, so its engine is removed. Synchronous split copies retained. See [full results and limits](RAM_READ_SCHEDULING_20261010.md). | Earlier and current queue approaches are rejected. Further changes require copy/completion/wake-up attribution and a controlled repeatable gain with Q1/small controls preserved; no 42 GB/s single-reader promise. |
| 2 | A partly cached request is read entirely from disk. Count mixed requests, lower bytes and already-cached bytes before considering missing-span reads. | V21 counters and patterned scenario implemented, all ten original checks pass. Signed 0.4.514.1 churn follow-up completed 6/6: weighted overlap only 0.788% mixed / 0.449% recovery and no staged Q8 reads. [Consolidated evidence](PERFORMANCE_FINDINGS.md#partly-cached-requests-accounting-first). No missing-span implementation justified for these shapes. | Raw 18 mixed/reread windows inspected; telemetry, 1,539 concurrent byte checks, 24 persisted files and independent restoration pass. Original patterned 12.55% overlap is a correctness fixture, not a speed gain. Other shapes remain conditional on representative waste/fragmentation measurements. |
| 3 | ReFS/Dev Drive overlaps push the next 256 requests to the ordered worker. Count decline reasons and compare a bounded shorter backoff for small caller-path overlaps while preserving the one foreground owner. | V23 dispatch attribution and a runtime lab backoff control implemented, default 256 unchanged. Plan 111 captures/restores/verifies the original setting; older drivers report unavailable fields. Both complete 24-window NTFS/ReFS comparisons pass collection/correctness checks: cooldown 0 gains 5.26%/3.79% mixed Q8 but loses 21.18% NTFS large-read Q8. Retain 256; ReFS large-read ranges overlap. [Results](CALLER_BACKOFF_20261010.md). | Same-session comparisons and all 81 retained-driver correctness checks at default 256 are complete. A request-size/filesystem policy is not implemented or qualified by these measurements. |
| 4 | Below-normal scheduled workloads read cached data much slower. Isolate CPU, I/O and memory priority effects and submitting/copy-worker waits before trying a scheduling change. | Plan-113 `priority-cost` implemented; complete 36-window comparison inspected and restored. CPU BelowNormal alone loses 34.20% resident Q8; low I/O/memory Q8 ranges overlap normal. [Results](PRIORITY_COST_20261010.md). No boost or default change justified. Memory priority is a process default after launch, with no added pressure. | Fully resident controls have zero lower attempts, exact hashes/readbacks and three alternating repetitions. Focused trace analysis is complete with lost events rejected. Ready delay grows and other CPUs idle under BelowNormal; isolate priority/affinity before a driver experiment. Preserve user application and driver priorities. |

Keep measurements in the supported `qcache developer verify` runner, extending
its typed plans/contract tests when needed. Keep immutable IDs, exact loaded
module hashes, raw XML, readiness/coverage and independent restoration evidence.
Update the plan version if a workload or measurement contract changes. Small
controls precede broad matrices; preserve the existing write-performance contract.

Accepted or closed: read recall's short mixed-workload hit-rate trade-off, the
32 GiB map-cost follow-up, idle defragmentation, per-stream chunks, write-run
copies, large pages and paging-read admission. They are not targets in this PR.
The [complete current write matrix](WRITE_PERFORMANCE_20261010.md) is a baseline; the historical Q8 allocator gap
remains unproved unless controlled evidence identifies it.

Earlier dedicated-worker results and their CPU profile are recorded in the
[implementation tracker](RAM_FIRST_IMPLEMENTATION_TRACKER.md), under “Queued
Direct copies” (`3fb9d39`, `e8e31c8`, reverted).


Preliminary CPU evidence from the incomplete 0.4.497.1 Direct Q1 reference:
46.4% of non-idle samples are in provider `memcpy` and 33.8% in its copy-helper
`WorkerMain`; the four virtual CPUs were about 95% busy. This is function-level
attribution over the enclosing process interval, not an exact score-window
profile or a completed baseline. It supports examining helper-loop overhead
before a shared sleeping whole-read queue experiment; it proves no speed gain.
