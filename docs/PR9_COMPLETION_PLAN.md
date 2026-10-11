# PR #9 completion plan

Written 2026-10-11 after a review of all PR #9 work (branch
`perf/ram-read-followups`, head `1e9d125`). **This is the active plan.** It
supersedes the execution order in `PERFORMANCE_FINALIZATION_HANDOFF.md`,
`PERFORMANCE_AGENT_HANDOFF.md` and `PERFORMANCE_FOLLOWUP_PLAN_20261010.md`; those
remain technical reference until step 6 folds them into the findings.

The goal is a mergeable PR with every discussed item closed: either **one
accepted RAM-disk speedup** or **a final "no speedup" decision**, never another
open research state. Every step has a fixed stop rule.

## Why a new plan: review of the previous work

What was done well and stays:

- The campaign runner works: one foreground command runs typed suites, checks
  bytes/identity/telemetry and restores the machine. Every recent run restored
  cleanly.
- Measurements are trustworthy: byte oracles, zero lower I/O, loaded-driver
  hashes, raw evidence preserved, failed attempts kept separately.
- Findings are consolidated in `PERFORMANCE_FINDINGS.md` and AGENTS.md points to it.

Where it went wrong:

1. **It stopped one step short of the answer.** Plan 117 measured exactly what
   was missing, and the numbers point at one specific, never-tried change (below).
   The agent declined to build it because the change needs careful lifetime
   handling. That is engineering work, not missing evidence.
2. **Automatic wake-up was never actually tested.** Every owned job was waited for
   inside the agent's turn and then marked `REVIEWED`, which by design suppresses
   automatic delivery. No job ever reached delivery `SENT`. The feature has not
   failed; it has not been tried.
3. **The final regression profile repeats finished research.** `release-performance`
   still contains concluded mode comparisons (`caller-backoff`, `priority-cost`,
   `ram-read-scheduling`, `cache-layout`). They are experiments, not regression
   tests, and add well over an hour.
4. **Leftover code from rejected experiments**: the `ram-read-queue` suite, the
   `LabRamReadQueue` control that can only return "not supported", and always-zero
   `RamReadQueue*` diagnostic fields. The owner wants no compatibility code.
5. **Documentation sprawl**: a 1,057-line findings file, three superseded plan
   files, a 3,440-line tracker and a dense AGENTS.md paragraph.
6. **No defined merge criteria.** "Research closed" was reported, but the PR never
   had a definition of "ready to merge".

## The one untried candidate, in plain terms

CrystalDiskMark's headline test, SEQ1M Q8T1 read, keeps eight 1 MiB reads queued
from one thread. In the README's own numbers the **disk cache beats the RAM disk**:
Q: with a 2 GiB cache reads **39,492 MB/s**, the 4 GiB RAM disk **25,353 MB/s**.

Why: the RAM disk splits each 1 MiB read into four 256 KiB pieces copied by four
CPUs, which is fast, but the application's thread **waits until all pieces are
copied before it can even hand over the next read**. Queue depth 8 therefore
behaves like queue depth 1 (Q1T1 and Q8T1 both measure about 23–26 GB/s). Plan 117 proves it: only
**one** RAM-disk read is ever active at Q8T1.

How much time that wastes, from plan 117's Q8T1 window (24.4 GB/s ≈ 41 µs per
1 MiB read): the copy takes about **23 µs**; the remaining **≈18 µs (≈43%)** is
the application thread entering and leaving Windows, building and finishing the
request. During those 18 µs the three helper CPUs have nothing to do; that is the
roughly one third of all CPU samples the profiles showed them spending polling.

The candidate: **hand all four pieces to the helpers and let the thread go
immediately; the helper that copies the last piece finishes the request.** The
thread then prepares the next read while the helpers copy the previous one. The
disk cache already works this way (it completes queued reads from its own worker
threads) and reaches 36–39 GB/s with one thread at Q8 on this same VM.

### Why this is not a repeat of the three failed attempts

All three earlier asynchronous designs (`3fb9d39`/`e8e31c8` queued Direct copies,
`c5d32a8` system-worker offload, `9262493` shared sleeping queue) gave each
**whole 1 MiB read to one worker**. Two things followed, both visible in their
evidence:

- One CPU copies 1 MiB in ~75 µs instead of four CPUs in ~19 µs.
- Round-robin whole requests reach each worker only every third or fourth read,
  so its 50 µs keep-awake spin expires and every handoff pays a thread wake-up,
  which is expensive in a VM. Their profile: workers 21% spinning versus 28%
  copying. `e8e31c8` also forced an unsplit inline copy whenever no CPU looked
  free, which made the submitting thread the bottleneck again.

The candidate keeps today's split exactly: the same 256 KiB pieces, the same
posting to every other CPU's helper, the same spin. Helpers receive work on every
read, so they stay awake (today's post-to-take latency is 1.2 µs). Only the
waiting moves.

Supporting evidence that overlap pays on this VM: the same RAM disk already sends
large **writes** to its workers because overlapping them measured 25.4 vs 17.4
GB/s at Q8 (`driver/qcache/ramdirect.cpp`, `LargeWriteBytes`).

**Prediction, stated before coding:** SEQ1M Q8T1 read from ~25 GB/s to 30–38 GB/s
(+20–50%). The ceiling is the four-reader total (36–39.5 GB/s), since three
helpers copy at ~14 GB/s each. Q1T1 and four-reader results unchanged, because
those shapes stay on today's path. **Failure mode:** per-request completion or
wake-up cost on the VM cancels the overlap, as it partly did before. This is
uncertain; that is why it gets one bounded comparison and a hard stop.

Not included: no change to writes, chunk size, copy code, data layout, CPU
assignment, priorities or the disk cache.

## Decision flow

```text
Step 1  wake-up test ───────────────────────────────── record result either way
Step 2  build candidate (default off, runtime toggle)
Step 3  one 18-window comparison
          ├─ target ≥ +5%, controls OK ──► Step 4A safety checks, make default
          ├─ target wins, one control loses ─► one pre-declared revision, rerun step 3 once
          └─ target does not win ─────────► Step 4B remove candidate (closed: no speedup)
Step 5  lean final regression, one command, either path
Step 6  cleanup, docs, README numbers (if speedup), PR ready for review
```

## Step 1: Verify automatic wake-up (≈ 5 minutes)

Procedure:

1. Prepare the existing smoke campaign as an owned job, exactly like
   `.lab/finalization117-smoke` (lab VHDX attached, competing UI stopped, settings
   captured): `--campaign smoke --lab-ntfs W: --pause-backing-cache`. It took 78 s.
2. Launch it with `dam-tools run-job` from **this** session.
3. **End the turn immediately.** Do not poll, do not call `job-status`, do not
   call `job-reviewed`. Tell the owner to keep this conversation selected in the
   Manager with no draft text.
4. Pass: an automatic analysis prompt arrives in this conversation after the job
   finishes, and the job record shows delivery `SENT`. Then restore the VM.
5. If nothing arrives within 10 minutes, the owner sends any message; the agent
   records job ID, record state and Manager version as a DeveAgentManager issue.

This result does not block the QueueCache PR. If it passes, every later VM run in
this plan uses it: launch, end the turn, analyse when woken. That also stops
spending agent turns on progress checks.

## Step 2: Build the candidate (one code change, default off)

Plan version 118. Suite `ram-read-async`. Runtime per-store toggle, off by default
and reset when a disk is created, set through the provider control like
`QcRamSetCoordination`.

### When a read takes the new path (all must hold, else today's path, unchanged)

- Toggle on for this store; Direct reads enabled; same validation, BitLocker
  signature check and MDL mapping as today, done before handoff.
- Read only, non-paging (`IRP_PAGING_IO` clear), asynchronous
  (`IoIsOperationSynchronous` false), length ≥ 512 KiB, at least two other-CPU
  helpers, provider not closing.
- **Queue-depth detection**, reusing the provider's existing write rule
  (`UseWorker`: `InlineAfter = 16`, `ProbeEvery = 64`): go asynchronous while
  another asynchronous read from this store is in flight; after 16 reads that
  found none in flight, stay inline and probe every 64th. Q1 therefore stays inline.
- **Busy-CPU guard**: stay inline if another thread is in an inline large copy on
  this store (one interlocked counter, touched only when the toggle is on). This
  keeps four readers on today's path; they already use every CPU.
- **Cap**: at most 16 asynchronous reads in flight per store; beyond that, inline.
- Off path cost: one extra read of the toggle per large Direct read.

### Mechanics

- Filter (`ramdirect.cpp`, `driver.cpp`): `QcRamDirectTransfer` returns one of
  declined / completed / pending. Before any helper can see the work: mark the
  IRP pending, keep the binding rundown and the remove lock. Dispatch returns
  `STATUS_PENDING`.
- Provider (`transfer.cpp`): heap `SPLIT` from a nonpaged lookaside list instead
  of the submitter's stack. Post one `HELP` per other-CPU helper with the existing
  `Post`; the submitter copies nothing and returns. `CopyChunks` is unchanged.
  A completed-piece counter identifies the helper that finishes the last piece;
  it calls the completion. A reference count (posted helps + completion) frees the
  `SPLIT` only after every posted `HELP` has been dequeued, because a late helper
  may still dequeue a `HELP` whose pieces are all taken. No withdrawal on this path.
- Completion callback (filter, runs on a helper at `DISPATCH_LEVEL`): timing and
  read counters, release binding rundown (store never touched afterwards), set
  `IoStatus`, release remove lock, `IoCompleteRequest`. Exactly once.
- If the lookaside allocation fails or the provider declines, copy inline as today
  (the IRP may still be completed by dispatch after being marked pending).
- View contract: add the asynchronous copy entry to `QC_KERNEL_RAM_VIEW`, bump
  `QcRamViewVersion`. Both drivers ship together; no compatibility path.
- **Lifetime fix required:** `FreeAdapter` (`driver/ramdisk/ramdisk.cpp`) stops
  workers *before* `FreeDisk` unregisters Direct views. With asynchronous reads a
  read could be posted to stopped workers and never finish. Unregister every view
  first, then stop workers; the asynchronous path also declines once `Closing` is
  set. (`3fb9d39` made the same reordering.)
- Cancellation: no cancel routine; a copy finishes within tens of microseconds.
- Coordination counters stay off in this suite; their "active copy" meaning is
  inline-only.

### Activation evidence (required to read any score)

Per-store snapshot: asynchronous started/completed, maximum in flight, inline
because of streak/busy CPU/cap/allocation, probes. The runner checks: original
windows show zero asynchronous reads; candidate Q8T1 windows show most large
reads asynchronous; every window ends with started == completed and nothing in
flight; toggle restored off.

### Host checks before the VM

Linux host contracts (`dotnet run --project tests/QueueCache.Management.Tests -c
Release`), plan/suite/toggle contracts, Windows CI Debug/Release with signing.

## Step 3: One comparison run (18 windows, ≈ 10–15 minutes)

```powershell
qcache developer verify Q: --campaign experiment --focus-suite ram-read-async --lab-ntfs W: --budget-mib 2048 --repeats 3 --duration-seconds 10 --diskspd <CDM DiskSpd64.exe> --pause-backing-cache --output <job root>
```

Shapes on an owned 2 GiB RAM disk, 1 GiB file, same build, original then candidate
order alternating per repetition:

| Shape | Role |
|---|---|
| Sequential 1 MiB Q8T1 | Target (CrystalDiskMark SEQ1M Q8T1) |
| Sequential 1 MiB Q1T1 | Control: must stay inline and not lose |
| Sequential 1 MiB Q2T4 | Control: four readers must not lose |

3 shapes × 2 modes × 3 repetitions = 18 windows. Same byte oracles, telemetry,
zero lower I/O and restoration guards as plan 117.

**Accept** when the target median improves **≥ 5%**, all three paired repetitions
improve, the gain exceeds the observed spread, and neither control shows a
repeatable loss of 3% or more.

**One revision, pre-declared** (only if the target wins and a control loses):

- Q1T1 loses → probes rarer: `InlineAfter` 64, `ProbeEvery` 256.
- Q2T4 loses → stronger busy-CPU guard: inline when more than one thread has
  submitted a large read to this store in the last 1 ms.

Then rerun the same 18 windows once. **Stop rules:** target does not win → no
revision, go to 4B. Revision does not fix the control → go to 4B. An ambiguous
result gets one more identical 18-window batch, then a decision; both batches
are reported.

## Step 4A: Speedup found, prove it is safe (≈ 45 minutes)

1. Add one typed lifecycle scenario: RAM disk stop and removal while Q8
   asynchronous reads are in flight, plus a saturation run above the cap. Pass:
   every read completes with correct bytes or a clean error, no hang, nothing in
   flight afterwards, the store is never touched after release.
2. Run that scenario and the candidate's Q8T1 window **once under Driver Verifier**
   (standard flags on `qcachelab` and `qcramdisk`), for correctness only. Then
   `verifier /reset` and reboot; confirm Verifier is off before any score.
3. Make the behavior the product default; keep the toggle as a lab switch for
   future A/B. Rerun only the target window pair to confirm the default build.

## Step 4B: No speedup, remove the candidate

Revert the candidate (keep the `FreeAdapter` reordering only if it is correct on
its own merits). Record hypothesis, numbers and reason in the findings. RAM-disk
read research is then **closed** with this evidence: every way of overlapping
reads that the design allows has been measured.

## Step 5: Lean final regression, one command (≈ 2–2.5 hours, unattended)

Redefine `release-performance` (no compatibility needed) to contain product
regression checks only:

- Correctness: `partial-read-accounting`, `paging-coherence`, `policies`,
  `pressure`, `ordering-faults` (last).
- Product-default performance: `ram-read-reference`, `cache-recall`,
  `cache-sustained` (30 min), `cache-map-cost`, `write-performance` (72 cases).
- Drop the concluded A/B experiments: `cache-layout`, `ram-read-scheduling`,
  `caller-backoff`, `priority-cost`. They stay available as standalone suites.
- If 4A: add the step-4A lifecycle scenario.

Deploy the exact final signed build, verify loaded hashes, launch once as an owned
job, end the turn (step 1), then read the combined report and raw evidence. A
failure is fixed and only the affected phase is rerun, unless the fix invalidates
the wider run.

## Step 6: Close out the PR

1. Remove dead code: `ram-read-queue` suite and `ArchivedQueue` kind,
   `LabRamReadQueue` control and its always-zero diagnostic fields, the archived
   queue's runner branches and tests. Keep the evidence documents.
2. Coordination counters: keep (default off, one load on the off path, plan 117
   showed no measurable target effect) unless step 5 shows otherwise.
3. Documentation: put a one-screen "current decisions" table at the top of
   `PERFORMANCE_FINDINGS.md`; fold the outcome of the three superseded plan files
   and this plan into it, then delete them (git keeps history). Cut the AGENTS.md
   performance paragraph to about ten lines pointing at the findings. Update the
   tracker's implementation and verification rows.
4. If 4A: redo the README RAM-disk column with the real CrystalDiskMark GUI per
   `BENCHMARKING.md` (normal priority, `drop-clean`, Verifier off).
5. Update the PR description (what changed, measured result, checks, known gaps),
   mark it ready for review. Merging stays the owner's decision.

## Final state of every discussed item

| Item | Final disposition | Done in |
|---|---|---|
| Original 1: automate test runs | Runner done. Automatic wake: verified or filed as a Manager issue. | Step 1 |
| Original 2: RAM-disk bottleneck | Cause established (one read at a time at Q8). Fix tried once: kept or rejected. | Steps 2–4 |
| Original 3: findings in one place | One findings file with a decisions table; superseded plans removed. | Step 6 |
| Plan 1: completion notification | As original 1. | Step 1 |
| Plan 2: RAM concurrency diagnosis | Done (plan 117). | — |
| Plan 3: one targeted improvement | The asynchronous split candidate. | Steps 2–4 |
| Plan 4: low-priority slowdown | Closed: benchmark-setup effect; launch at normal priority. Parked by the owner. | Done |
| Plan 5a: 100 ms map refresh cost | Closed: owner satisfied, native map reads take milliseconds. | Done |
| Plan 5b: selective NTFS/ReFS caller policy | **Closed without experiment**: best measured gain +5.26% (NTFS mixed Q8) and +3.79% (ReFS), at or below the 5% bar, while touching ordering/flush paths that need broad qualification. | Step 6 records it |
| Plan 6: qualify accepted changes | One lean final regression on the final build. | Step 5 |
| Partly cached reads | Closed: wasted rereads 0.788% / 0.449%. | Done |
| Historical write differences | Closed: conditions differed; 72-case baseline kept. | Done |
| Dead code from rejected experiments | Removed. | Step 6 |

## Budget

| Step | VM time | Other |
|---|---:|---|
| 1 Wake-up test | ~2 min | owner keeps the conversation selected |
| 2 Candidate | — | ~200 native + ~300 managed/test lines, CI ~7 min, deploy and reboot ~5 min |
| 3 Comparison | 10–15 min (≤ 3 runs worst case) | |
| 4A Safety | ~45 min incl. two reboots | |
| 5 Final regression | ~2–2.5 h unattended | |
| 6 Close-out | ~20 min CDM GUI if 4A | docs and PR text |

No other experiments. A new idea that comes up along the way is written down in
the findings as a future hypothesis, not pursued in this PR.
