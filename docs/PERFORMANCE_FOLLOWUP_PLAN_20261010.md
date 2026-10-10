# Performance follow-up after sustained cache validation

[PR #8](https://github.com/devedse/QueueCache/pull/8) is merged. It establishes read recall, complete sustained/concurrent/map measurements,
README benchmarks and the map pop-out / 100 ms refresh controls. This follow-up
works on remaining measured limits. The tracker separates implementation from
verification; changes are retained only after controlled measurements and byte
checks. A larger aggregate score from four readers is evidence of available
bandwidth, not proof that a single reader can reach it.

| Order | Problem and intended change | Implementation | Required evidence |
|---|---|---|---|
| 1 | RAM-disk SEQ1M Q8T1 has a lower score than four readers. Profile copying, synchronous completion and helper coordination before another scheduling change. | Reference and same-build A/B implemented; all 36 reference and 54 comparison windows pass byte/collection checks. The shared sleeping queue loses 11.30% Q8 and 49.62% four-reader throughput, so its engine is removed. Synchronous split copies retained. See [full results and limits](RAM_READ_SCHEDULING_20261010.md). | Earlier and current queue approaches are rejected. Further changes require copy/completion/wake-up attribution and a controlled repeatable gain with Q1/small controls preserved; no 42 GB/s single-reader promise. |
| 2 | A partly cached request is read entirely from disk. Add staged-read counters for mixed requests, disk bytes and bytes that RAM already held. Collect focused random/mixed and patterned partial-residency evidence with read recall on. Change lower reads to missing spans only if the measured benefit warrants it. | Diagnostics V21 counters and plan-105 `partial-read-accounting` implemented; host contracts pass. Windows Debug/Release CI and all ten VM byte/accounting checks pass on 0.4.495.1. Span reads remain conditional. | Appended diagnostics ABI and managed decoder checks; strict accounting in exact scenario windows; known byte patterns with partial sectors, concurrent writes, full hits and full misses. No speculative cached content from paging/application buffers. |
| 3 | ReFS/Dev Drive overlaps push the next 256 requests to the ordered worker. Count decline reasons and compare a bounded shorter backoff for small caller-path overlaps while preserving the one foreground owner. | V23 dispatch attribution and a runtime lab backoff control implemented, default 256 unchanged. Plan 111 captures/restores/verifies the original setting; older drivers report unavailable fields. Both complete 24-window NTFS/ReFS comparisons pass collection/correctness checks: cooldown 0 gains 5.26%/3.79% mixed Q8 but loses 21.18% NTFS large-read Q8. Retain 256; ReFS large-read ranges overlap. [Results](CALLER_BACKOFF_20261010.md). | Same-session controls and attribution complete; default-path ordering/flush/capacity checks remain. A request-size/filesystem policy is not implemented or qualified by these measurements. |
| 4 | Below-normal scheduled workloads read cached data much slower. Isolate CPU, I/O and memory priority effects and submitting/copy-worker waits before trying a scheduling change. | Plan-113 supported `priority-cost` implements independent owned-child CPU/I/O/memory controls, explicit readbacks and pre-score deadlines. VM evidence remains pending; no boost or default change justified. Memory priority is a process default after launch, with no added memory pressure. | Same binary/hash, fully resident data, zero lower attempts, three alternating repetitions of 1M Q1/Q8 and 4K Q1 controls. Record CPU/wait attribution when a difference warrants it; preserve user application and driver priorities. |

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
