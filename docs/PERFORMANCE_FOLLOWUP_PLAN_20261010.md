# Performance follow-up after sustained cache validation

[PR #8](https://github.com/devedse/QueueCache/pull/8) is merged. It establishes read recall, complete sustained/concurrent/map measurements,
README benchmarks and the map pop-out / 100 ms refresh controls. This follow-up
works on remaining measured limits. The tracker separates implementation from
verification; changes are retained only after controlled measurements and byte
checks. A larger aggregate score from four readers is evidence of available
bandwidth, not proof that a single reader can reach it.

| Order | Problem and intended change | Implementation | Required evidence |
|---|---|---|---|
| 1 | RAM-disk SEQ1M Q8T1 is about 25–26 GB/s while four readers aggregate 40–42 GB/s. Profile synchronous completion, copying, worker spin and wake-up costs before selecting another asynchronous whole-request design. | Plan-106 `ram-read-reference` is implemented for controlled Direct/Standard baselines; host plan/accounting/ownership/restoration contracts pass; Windows/VM verification pending. Provider copy helpers already exist. Earlier queued Direct experiments using these dedicated workers were also rejected: large reads reached 20.8–22.3 GB/s, with substantial spin/handoff cost. Do not repeat the same design. Compare the current Direct/split and Standard paths first; test a shared bounded queue with sleeping workers or changed completion batching only when the profile identifies the expected saving. Keep an experimental strategy behind a lab A/B control. | Same-session Direct/Standard Q1/Q8 and 4K Q1/Q32, one and four submitting threads, three alternating repetitions, exact byte round trips and normal priority. Keep a repeatable Q8 gain only if Q1/small reads remain within measured variation. Exercise cancellation, binding withdrawal, stop/removal, freeze/snapshot and overlapping writes. |
| 2 | A partly cached request is read entirely from disk. Add staged-read counters for mixed requests, disk bytes and bytes that RAM already held. Collect focused random/mixed and patterned partial-residency evidence with read recall on. Change lower reads to missing spans only if the measured benefit warrants it. | Diagnostics V21 counters and plan-105 `partial-read-accounting` implemented; host contracts pass. Windows Debug/Release CI and all ten VM byte/accounting checks pass on 0.4.495.1. Span reads remain conditional. | Appended diagnostics ABI and managed decoder checks; strict accounting in exact scenario windows; known byte patterns with partial sectors, concurrent writes, full hits and full misses. No speculative cached content from paging/application buffers. |
| 3 | ReFS/Dev Drive overlaps push the next 256 requests to the ordered worker. Count decline reasons and compare a bounded shorter backoff for small caller-path overlaps while preserving the one foreground owner. | Planned after a controlled ReFS/NTFS reference on an owned lab volume. | Same-session 64 KiB mixed and 4K/1M controls, caller/worker attribution, ordering/flush/capacity tests. Changing backoff must not permit concurrent unprotected writes or bypass queued controls. |
| 4 | Below-normal scheduled workloads read cached data much slower. Isolate CPU, I/O and memory priority effects and submitting/copy-worker waits before trying a scheduling change. | Investigation planned; no priority boost or default change justified yet. | Normal/below-normal settings recorded separately, same executable/hash, fully resident data and zero lower attempts, CPU/wait attribution and Q1/Q8 controls. Preserve the user's configured process priority. |

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
