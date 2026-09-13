# Progressive read selection: implementation and verification

## Scope

The measured QD128 case repeatedly stopped at 64 queued writes, hiding resident
reads farther down the queue. Selection now preserves intrusive cursors between
short CSQ lock acquisitions. This is not a multi-worker driver redesign and does
not promise improvements across real flush fences, cold reads or write throughput.

Each selection step examines at most 64 entries, counting both candidate search
and older-request validation. A candidate is not removed until **every** older
queued request has been checked. Active overlapping writes, older overlapping
writes, unknown controls, flushes and TRIM retain their existing restrictions.
The maximum read size (1 MiB) and eight-read service budget are unchanged.

The cursor state is protected by QueueLock. Removal advances matching cursors and
clears a cancelled candidate before unlinking it. Tail insertion extends an
exhausted search; head reinsertion and normal worker admission reset it. No IRP
pointer is dereferenced outside the lock by the selector. Payload ownership and
cache-slot pinning are unchanged. Known misses remain suppressed for the current
foreground-owner epoch.

A partial scan reports progress so the caller rechecks its own completion or
capacity and continues instead of sleeping 100 ms. Exhaustion and a stable fence
report no progress. Cancellation signals WorkAvailable because it can remove a
fence without any subsequent insertion. The existing performance ABI is unchanged:
SelectionScanLimit now counts bounded continuation yields, not an absolute queue
depth rejection. ServiceReadNoCandidate includes incomplete scan chunks; do not
interpret it alone as a cache miss or fence rejection.

## Local checks

`readselection-check.h` exercises the actual generic selector at compile time in
every native build. It covers deep reads, per-step bounds, older-write conflicts,
fence cancellation, candidate cancellation, head reinsertion, cursor removal and
tail insertion. These are not a substitute for live CSQ cancellation tests.

## VM verification handoff

### Assignment and boundaries

Verify the CI release containing the commit that adds this document. Wait for a
successful CI build, install that artifact and reboot before the batch. Record the
release's source commit and SHA256 of its driver and the loaded driver file; they
must match. An old driver with a similarly named version is not acceptable.
Do not modify driver code, defaults, saved profiles, security settings or C: cache
configuration. Test harness changes are allowed only in the private `.lab/`
directory. Do not commit raw results, machine paths or credentials.

This document is an assignment, **not a ready-to-run comprehensive test script**.
Review/extend the private harness first. Locate `.lab/Trace-TwoQueues.ps1` and its
sibling `.lab/Run-ObserverProbe.ps1` (the native sampler dependency); baseline raw
evidence is `.lab/TwoQueues-Review-20260913/`. These are deliberately not shipped
in Git. If working from another checkout, obtain these exact files from the owner
before running; do not substitute the previously invalid focused matrix. The old
`developer/scripts/Test-Concurrency.ps1` path is absent in this checkout. Locate
and review the previous VM correctness harness, or the local
`.lab/Run-ConcurrencyProbe.ps1`, rather than assuming that old path exists.

Resolve paths on the machine where each command executes: a local checkout path
is not a VM path; a UNC share is not its VM-local desktop path. Verify the CLI,
DiskSpd, sampler dependency and workload files on the VM before starting. Copy
both probe scripts into the same new VM run directory. Never overwrite baseline
results. The private probe contains machine-specific paths/preconditions: check
each, and stop on a mismatch rather than silently selecting another disk.

### Mandatory harness preflight

- Use PowerShell 5.1-compatible types and syntax, strict mode, and `$arguments`
  (never `$args`) for workload parameters. Parse and smoke-test the runner first.
- Assert the actual DiskSpd `Command Line:` contains every expected workload
  token. Parse a real completed output and exercise CSV/JSON row accumulation.
  The CDM-bundled DiskSpd can return its score as exit code; validate that specific
  convention, not a blanket nonzero-exit exemption. Unexpected stderr is a failure.
- Query state with `qcache policy status Q: --json`; query performance separately
  with `qcache developer performance Q:`. Enable/read back timing using
  `qcache developer performance Q: --timing true`. Require fields to exist and
  performance counters to advance during a small workload. Missing values are
  collection errors, never zero. Reject instance changes or counter resets.
- Use an owned detached SYSTEM task because SSH children may die on disconnect.
  Set finite per-process and overall deadlines before starting; never put an
  unbounded wait before the timeout check. Preserve exact commands and raw output.
  Cleanup must target only this run's task/processes, not all PowerShell processes.
- Capture and prove restoration of the runtime options and saved profiles before
  the long run. If the captured budget cannot be restored, report failure and the
  exact recovery command; do not silently substitute a smaller cache.

### Execution order

1. Record loaded driver path/hash, CLI identity and Q: physical disk identity.
   Use the exact new artifact, not version text alone (local versions are reused).
   Capture runtime options and saved profiles separately. Do not change C:.
2. Run seeded byte-comparison concurrency checks before timing: overlapping writes,
   in-flight overwrites at drain parallelism 1/2/4, cache-miss reinsertion, nested
   reads, cancellation and flush/TRIM fences. Verify RAM reads and post-drain,
   post-drop-clean disk reads. Any mismatch, driver error or hang stops the batch.
3. Add deep-queue cases: at least 128 unrelated queued writes before hot reads;
   then an overlapping older write at positions below and above 64; a flush/TRIM
   fence at both positions; cancel reads during search/validation and cancel an
   older queued fence. Confirm cancelled IRPs complete once and later requests
   complete. No read may cross a conflicting write or fence.
4. Repeat the private TwoQueues probe with QD32 and QD128, three interleaved
   repetitions, Fixed 50%, 1024 MiB, Eager, 256 MiB warmed reader, 25 ms lower-write
   delay. Keep baseline workload arguments and files unchanged. Collect state and
   performance separately; require real fields, never coerce missing fields to 0.
5. Report reader IOPS, p99/p99.9/max latency, bypass completions/misses, scan yields,
   fence/overlap rejects, capacity waits, drain progress and CPU. Use actual
   counter-window duration; do not equate whole-process counters with DiskSpd's
   nominal measured interval. Correlate stall samples with fences.
6. Repeat with no injected delay and with Automatic allocation to separate selector
   progress from eviction. Include an unchanged-fence and all-cache-miss workload
   to check CPU does not spin without useful work; include long writes to confirm
   foreground and drain progress are not starved.
7. Finally clear delay/timing, drain, restore captured runtime options and verify
   saved profiles were not changed. Record dirty bytes/errors and remove only owned
   helpers. Bound all process waits and mark failed collection as invalid.

Acceptance: correct bytes/ordering/cancellation are mandatory. QD128 eligible
resident reads must be reached beyond entry 64; scan-yield counts can increase
while latency improves. Report any remaining fence stalls separately. Do not call
the complete performance problem solved or change defaults on these results alone.

### Exact performance matrix and required report

First run the diagnostic matrix with timing enabled and 200 ms state/performance
sampling. Each row below is six cases in order QD32, QD128, QD128, QD32, QD32,
QD128 (three repeats each). Flush and drop clean blocks, then warm the reader
before each case. The cache is 1024 MiB Fast/Eager, batch 256 KiB, one drainer.

| Allocation | Lower-write delay | Purpose |
|---|---:|---|
| Fixed 50% write | 25 ms | Primary deep-queue regression |
| Fixed 50% write | 0 ms | Normal-storage control |
| Automatic | 25 ms | Distinguish eviction from selection |
| Automatic | 0 ms | Normal automatic-allocation control |

Keep the reference workload: separate 256 MiB reader and 2 GiB writer files;
warm with `-b1M -o4 -t1 -w0 -d4 -W0 -S`; writer
`-b1M -o<QD> -t1 -w100 -d50 -W0 -S -L -Zr`; after a six-second lead-in,
reader `-b4K -r4K -o8 -t2 -w0 -d10 -W0 -S -L`. Append the verified file
path to each command. Confirm the writer remains running through the reader's
completion, the hot set is resident before launch and dirty/drain activity occurs.
Record reader misses rather than assuming residency persists under Automatic.
The original TwoQueues script runs only two cases: parameterize a private copy
for this matrix; do not claim running it unchanged completes this assignment.

For precise queue positions/cancellation phases, normal file I/O does not prove
where an IRP landed. Use available trace evidence or mark that scenario
**not exercised**. Do not manufacture new production hooks to claim coverage;
report the missing capability for review. For overlapping concurrent I/O, verify
completed-write ordering without assuming a large write is atomic across blocks.

Produce `RESULTS.md`, machine-readable summary, raw outputs, samples and a
`status.json` in a fresh private run directory. Report PASS/FAIL/NOT EXERCISED per
correctness scenario; medians and ranges for each matrix cell; restoration proof;
and a decision table: deep-queue fix effective, residual fence stalls, eviction,
CPU/throughput regressions and unresolved evidence. Existing last-selection fields
are snapshots, not a full event trace or an absolute queue-position measurement.
Timing-enabled CPU/latency results include instrumentation overhead; confirm an
apparent regression in a small matched timing-disabled run before attributing it
to the selector. Stop after this report; do not start another redesign.

On corruption, driver error, hang, wrong identity or failed collection: stop the
batch, preserve evidence, perform bounded owned-helper cleanup and restoration,
write FAILED status and report. A failed restoration must remain explicit even
when all measured workloads passed. Never format a disk or reboot away an error
as part of automatic recovery. The user can check `status.json` for completion.
