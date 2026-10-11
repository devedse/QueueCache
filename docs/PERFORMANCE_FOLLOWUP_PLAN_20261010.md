# Performance follow-up after sustained cache validation

[PR #8](https://github.com/devedse/QueueCache/pull/8) is merged. It establishes read recall, complete sustained/concurrent/map measurements,
README benchmarks and the map pop-out / 100 ms refresh controls. This follow-up
works on remaining measured limits. The tracker separates implementation from
verification; changes are retained only after controlled measurements and byte
checks. A larger aggregate score from four readers is evidence of available
bandwidth, not proof that a single reader can reach it.

The consolidated decisions, including earlier retained improvements and rejected
experiments, are in [PERFORMANCE_FINDINGS.md](PERFORMANCE_FINDINGS.md). The
[campaign/completion plan](PERFORMANCE_CAMPAIGN_PLAN.md) describes the implemented
single-command runner. Smoke and focused campaigns have completed on the VM;
automatic Manager wake-up remains an external integration.

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

## Plan-114 follow-up closure

Single-command smoke/focused/performance/release campaigns and a strict durable
completion consumer are implemented; automatic Manager wake delivery needs an
external controller API. Windows host/CLI contracts and Debug/Release CI pass.
The smoke and six-phase focused campaign both complete with exact restoration;
81 retained checks and thirty RAM scheduling windows are inspected. Default /
unbound Q8 remains 25.84 / 26.30 GB/s with overlapping ranges; separate four-reader
source lanes retain 38–39 GB/s. Benchmark-thread affinity or overlapping source
reads do not explain most of the gap in these controls. Provider helpers still
have fixed CPU assignments; their placement was not independently varied. The next candidate is request
concurrency and split-helper handoff cost, with copying held fixed; exact
attribution and lifecycle qualification remain prerequisites for a production
change. Cache CPU-priority/affinity interaction remains separate and untested.
All matched medians/ranges, raw identities, timing, failure history and decisions
are in [PERFORMANCE_FINDINGS.md](PERFORMANCE_FINDINGS.md).

## Continuation plan after findings review

The current agent-ready execution plan is
[PERFORMANCE_FINALIZATION_HANDOFF.md](PERFORMANCE_FINALIZATION_HANDOFF.md): one
remaining diagnostic, at most one candidate mechanism plus one justified revision,
focused comparisons first and a single final broad campaign only for a winner.
Its stop conditions supersede the historical continuation order below.

Current scope after user review, 2026-10-11: prioritize concrete RAM speedup
experiments and finish automatic completion delivery. The completed cached
priority/affinity comparison explains a benchmark-launch sensitivity; additional
low-priority tracing is parked unless a relevant real workload warrants it.
Dedicated 100 ms UI cost measurement is removed from the active backlog at the
user's request, without claiming that rendering cost was measured. During
iteration, use the smallest maintained suite plus correctness checks affected by
the change. The current `focused` campaign includes 81 retained correctness checks
and is not mandatory for every experiment. Run broad regression/write/sustained
qualification once at the end for the retained candidate using one campaign and
its combined result; repeat affected work only if a failure or later change
requires it. This scope update does not change existing suite contracts/counts.

The [agent implementation handoff](PERFORMANCE_AGENT_HANDOFF.md) provides exact
file/entry-point mappings, ordered changes, proposed workload counts, commands,
tests, dependency handling and acceptance/stop gates for these priorities.

This is a plan, not another completed measurement or an accepted driver change.
Keep the established allocator, cache-copy optimizations, read recall and
synchronous RAM-disk split copies. The latest matched comparison is about
26 GB/s from one submitting thread versus 38–39 GB/s from four. It establishes
available aggregate throughput for those workloads, not a single-thread target
or a regression from historical 42–45 GB/s scores under different conditions.

The removed queue changed both request scheduling and copy granularity. Its
loss rejects that implementation; a future experiment must isolate its change.
The latest thirty RAM windows qualify the diagnostic comparison. The 81
correctness checks qualify the retained behavior exercised by those scenarios;
they do not qualify an asynchronous implementation that has not been built.

| Priority | Work | Concrete completion condition | Decision after the result |
|---|---|---|---|
| 1, orchestration track | Connect the campaign terminal event to DeveAgentManager process completion. | One foreground job produces one resumed-agent result after evidence and restoration, with duplicate/reconnect handling and explicit failure/cancellation outcomes. | Close the remaining automation gap. Requires a supported Manager API; do not hold up the performance diagnosis if it is unavailable. |
| 1, performance track | Attribute RAM-disk request concurrency and split-copy coordination on the current retained code. | A focused comparison explains actual concurrent requests, copy work, helper handoff/withdrawal and completion waits for one versus four submitters. | Choose one demonstrated cost to reduce, or stop native experimentation if no actionable cost is established. |
| 2, conditional | Implement one default-off RAM experiment selected by that attribution, keeping the copy algorithm and 256 KiB split size fixed. | Same-build alternating A/B shows a repeatable practical gain and passes affected byte, latency and lifecycle controls. | Retain only a qualified improvement; otherwise remove the experiment and preserve its findings. |
| 3, completed; further work parked | Isolate CPU priority versus benchmark affinity for resident disk-cache reads. | Plan-116 comparison completed: Q8 low-priority penalty shrinks from 30.21% at default placement to 3.14% unbound, with overlapping unbound ranges. | Keep normal-priority benchmark launches. No normal-priority product problem is established; do not pursue more tracing without a relevant observed problem or new user request. |
| 4, removed from active backlog | Measure real Desktop map cost at 100 ms. | User accepts the current UI behavior and does not want dedicated cost testing. | Keep existing UI functionality. Rendering cost remains unmeasured; reopen only for an observed problem or new request. |
| 5, conditional | Investigate a request-size-aware caller policy on NTFS and ReFS. | Small mixed gains survive transitions into large reads with the large-copy backoff retained, plus ordering/flush/capacity checks. | Lower priority than RAM diagnosis: global backoff 0 gained only 4–5% mixed throughput and lost 21% NTFS large-read throughput. |
| Release gate, last | Qualify the final retained native change with one relevant maintained campaign. | After focused candidate comparisons, required phases and restoration complete; inspect the combined report and matched baselines. | Broad regression/write/sustained testing belongs at the end, not after every diagnostic. Update README benchmarks only after an accepted performance change. |

### RAM diagnosis and experiment gate

Source review confirms that [Direct reads](../driver/qcache/ramdirect.cpp)
finish copying before [request completion](../driver/qcache/driver.cpp).
[Provider CopySplit](../driver/ramdisk/transfer.cpp) posts helper work, lets the
submitter copy chunks, withdraws untaken helper entries, then waits for taken
helpers. `WorkerMain` includes queue handling and a bounded poll as well as copy
dispatch. Workers retain their CPU assignments when DiskSpd uses `-n`.
These are specific mechanisms to measure, not proof that any one causes the gap.

1. Reuse preserved profiles to define the hypothesis; do not rerun the completed
   affinity/source-lane matrix just to reconfirm its result. Extend the maintained
   RAM scenario only where the new attribution needs evidence. Use current
   loaded-module hashes and matching symbols, fixed Normal launch settings,
   payload/copy mode, file size, native timing state and DiskSpd hash.
2. Profile 1 MiB Q1T1, Q8T1 and Q2T4. Establish actual concurrent Direct requests,
   helper participation and time spent copying, posting/withdrawing work,
   waiting for helpers and completing requests. Separate helper polling from
   useful work; a function-level WorkerMain percentage alone cannot do that.
   Prefer sampled/context-switch evidence first. Add bounded default-off
   diagnostics only if necessary and measure their overhead. Diagnostic/traced
   scores remain separate from untraced performance acceptance.
3. If handoff dominates, change one handoff decision while keeping completion
   and copy behavior fixed. If inline request serialization dominates, consider
   one bounded asynchronous experiment retaining CopySplit. Prove the executor
   can make progress without waiting for work queued behind itself, and preserve
   stack/helper lifetime, request ownership, cancellation and withdrawal. Do not
   revive the removed whole-copy queue unchanged.
4. Compare control and candidate in the same build with at least three alternating
   repetitions. Retain Q1, Q8, four readers including separate source lanes, and
   small-read controls. Proposed usefulness gate: at least a 5% target-workload
   gain that exceeds observed run variation; investigate any repeatable 3% or
   greater control loss. These are decision thresholds, not statistical proof.
   Inspect latency and CPU cost as well as throughput; add focused repetitions
   only when uncertainty could change the decision.
5. Byte/accounting and zero-backing-I/O checks must pass before accepting scores.
   Before promotion, exercise affected cancellation, stop/withdrawal, resource
   pressure, ordering and restoration paths. Shared provider changes also need
   affected Standard-path and write controls. A new lifetime/scheduling path
   requires its own qualification; the existing 81 checks are not a substitute.

### Completion integration and run budget

The Manager owns the process/job-to-conversation mapping and delivery retries.
After process exit it validates `completion.json` using `verify-completion`,
records the stable event ID and wakes the associated agent once. A controller
restart must recover a pending event without rerunning the benchmark; duplicate
delivery must not start duplicate analysis. Missing terminal evidence after an
unexpected process exit is an interrupted run requiring inspection, not success.
Test controller success, failure, cancellation, interruption and reconnect with
fixtures; the approximately one-minute campaign smoke is enough for the first
end-to-end VM check. No broad performance matrix is needed for notification work.

Use one focused command per diagnostic or A/B stage, with automatic process-exit
delivery when available. Until then, await the owned foreground job once and
consume its evidence; repeated agent SSH/status polling is not the intended flow.
The completed focused campaign took 17 min 21 s. Budget roughly 20–40 minutes of
VM time for a new focused diagnostic/A/B stage, depending on its frozen scope;
coding, CI, deployment and analysis are additional and are not yet timed.
A release run that includes the existing write matrix and 30-minute soak already
contains about 84 minutes of those phases before its other work. Do not schedule
it to answer a question that a focused stage can resolve.

### Keep deferred unless new evidence changes the decision

- Missing-span reads: measured overlap is 0.788% of mixed staged bytes and
  0.449% during recovery. Reopen only for a representative workload with material
  waste and favorable request-fragmentation costs; the artificial 12.55% fixture
  is not performance acceptance.
- Global zero caller backoff, the removed whole-read queue and unbounded copy
  coalescing: measured regressions outweigh their gains.
- Idle relocation, per-stream chunks and large pages: current evidence does not
  justify their complexity. Existing in-chunk ordering optimizations remain.
- Historical cross-day Q8 write differences: obtain a controlled comparison only
  if a new current observation warrants it; preserve the completed 72-case baseline.

After each stage, append the conclusion and immutable evidence identity to
PERFORMANCE_FINDINGS.md and update implementation and verification separately
in RAM_FIRST_IMPLEMENTATION_TRACKER.md. Stop an unsuccessful branch of the
investigation rather than widening the matrix without a new hypothesis.
