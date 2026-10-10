# Final performance investigation: bounded agent handoff

Prepared 2026-10-11 against `perf/ram-read-followups`, source checkpoint
`6c52ce4`, verification plan 116. This is the **current execution plan**, replacing
the order and scope of the older [implementation handoff](PERFORMANCE_AGENT_HANDOFF.md).
That file remains technical reference, not a second backlog. Proposed commands
and suites below are explicitly marked; implement them before use.

## Outcome and limits

Finish this investigation with one of these supported conclusions:

1. **Accepted speedup:** a specific retained RAM-read change beats its same-build
   control, passes relevant correctness checks and the final regression campaign.
2. **No acceptable speedup found in this investigation:** no actionable cost was
   established, or the bounded candidate experiment failed its benefit/correctness
   gates. Remove the failed candidate and document the measured reason and limits.

The second outcome does not prove that no future optimization is possible.
An interrupted run, unsafe/unavailable deployment or restoration failure is a
**verification blocker**, not either performance conclusion. Record that blocker
explicitly rather than declaring the product improved or the hypothesis disproved.

Research budget: **one new diagnostic pass, one selected candidate mechanism,
and at most one evidence-driven revision of that same mechanism**. One narrowly
scoped ambiguity repeat is allowed below. Do not turn an inconclusive outcome
into another investigation tree. Existing byte, identity, telemetry, ownership
and restoration checks remain required in every selected suite.

Primary target: normal-priority Direct RAM-disk sequential 1 MiB Q8T1 reads
(one submitting thread, at most eight outstanding requests). Four-reader
throughput is a control, not a promised single-reader target. Keep copying,
256 KiB split size, priorities and benchmark placement fixed.

## What is already known; do not repeat it

Read [PERFORMANCE_FINDINGS.md](PERFORMANCE_FINDINGS.md) first. Raw evidence is
private; use the exact recorded runs, never combine incomplete repetitions.

| Established result | Consequence |
|---|---|
| Ordered chunks, bounded cache-hit copies and read recall have retained benefits. | Keep them. They are not the failed RAM queue. |
| Thirty normal-priority RAM controls: single-reader Q8 25.84/26.30 GB/s default/unbound; four separate readers 37.97–39.04 GB/s. | Do not rerun the affinity/source-lane factorial. Most of the gap remains. |
| Three traced shapes: helper polling is about 32–33% of enclosing CPU samples with one reader, 7% with four. | Polling exists, but changing it is not a demonstrated throughput fix. Do not repeat the existing CPU recording. |
| The removed whole-read queue lost 11.30% Q8 and 49.62% four-reader throughput; it also changed copy granularity. | Do not restore it. Preserve split copying in any new scheduling candidate. |
| Earlier polling-duration, worker-count and non-temporal-copy trials did not establish a gain. | No parameter sweep or revival without different causal evidence. |
| Cached low-priority penalty shrinks with different benchmark CPU placement. | Documented; further priority investigation is parked. Normal-priority product impact is unproved. |
| Missing-span overlap was below 1%; global zero caller backoff lost 21.18% on large NTFS reads. | Missing-span work and selective NTFS/ReFS policy remain deferred. |
| UI pop-out/full-screen/100 ms controls work; full rendering cost is unmeasured. | User removed dedicated UI cost testing from the active backlog. |
| Both latest campaigns passed 81 existing correctness checks; no new native candidate was retained. | Preserve their evidence. Do not attach all 81 checks to every experiment or claim they qualify a future asynchronous lifetime. |

No allocator redesign, idle relocation, large pages, new filesystem policy,
priority boost, UI profiling or extra README benchmark is part of diagnosis.

## 1. Establish the starting point and finish completion delivery

1. Read `AGENTS.md`, this plan, the current findings and the current tracker table.
   Inspect branch/status/log and PR #9 before changing anything. The recorded head
   was `6c52ce4`; later work may exist. Continue on the existing PR branch unless
   it has merged. Preserve unrelated changes and private lab evidence.
2. Record the actual code commit and installed native filter/provider hashes.
   Last validated native baseline was signed 0.4.514.1; managed source `78ecfb2`
   introduced the final plan-116 tooling. These are provenance references, not
   permission to downgrade or reboot automatically. Use intended signed CI builds
   for native edits; GitHub Actions owns product-version increments.
3. Close the remaining Manager wake gap independently of performance research.
   Read `../deveagentmanager/docs/background-jobs.md`. Existing implementation is
   in `DeveAgentManager.Core/Agents/AgentJobCompletions.cs` and
   `DeveAgentManager.Core/Scripts/dam-tools.py`; tests are in
   `DeveAgentManager.Tests/AgentJobCompletionTests.cs` and
   `DeveAgentManager.Tests/Desktop/dam-tools.test.py`.
4. Establish whether the running Manager service contains that implementation.
   Use its supported deployment procedure if authorized/available. The installed
   helper alone does not prove the service is updated. Do not replace another
   agent's unrelated Manager work or invent a new delivery API.
5. Launch **one existing smoke campaign** through `dam-tools run-job`, with JSON
   argv arrays for preparation, workload and read-only completion validation.
   Allocate a unique empty output parent on the VM; validate it with
   `qcache developer verify-completion <parent> --output-parent` after exit.
   Let the agent become idle, the correct conversation selected and input blank,
   so actual automatic delivery can occur. Do not mark the job reviewed before
   testing delivery. Record its job/event identity and actual received notice.
6. Check success/failed-event/reconnect/deduplication behavior with existing host
   fixtures; do not launch a native fault matrix for notification testing. An
   ambiguous terminal delivery must remain explicit, not automatically replayed.
   If service access is unavailable, record that integration gap and continue
   research with one owned foreground wait; do not poll via repeated agent turns.

Existing smoke command on the VM, after validating its actual volume roles:

```powershell
& $qcCli developer verify Q: --campaign smoke --lab-ntfs W: --pause-backing-cache --output $qcJobRoot
```

`$qcCli`, `$qcDiskSpd` and `$qcJobRoot` below are task-specific variables for
actual VM paths. Do not copy private transport or credentials into public docs.

## 2. Provide a small campaign without the 81 unrelated checks

Current `--campaign focused` always adds the retained correctness group. Direct
`--suite` runs already avoid that scope, but the current Manager completion reader
validates campaign terminal events. Make one small composition change so narrow
research retains the existing automatic completion path:

1. Add proposed profile **`experiment`** to `VerificationCampaignPlan`. Require
   `--focus-suite`, initially allowing only the three proposed RAM suites below.
   Compose exactly **one selected phase** using the existing phase/target factory,
   ownership, preparation, restoration, evidence and completion writer. Do not
   change the contents/counts of `focused`, `performance` or `release-performance`.
2. Keep the existing explicit performance/NTFS lab role requirements and clean
   state checks; the unused NTFS role does not imply running its 81 checks. Refuse
   irrelevant ReFS/trace/filter options. Do not add a private shell scenario loop,
   another executable or a second completion protocol.
3. Propagate typed RAM kinds through `CasesFor`, worker dispatch and evidence.
   Freeze unique IDs/counts, repeat ordering, candidate identity, mode readbacks
   and activation expectations. Increment the verification plan when contracts
   change. Do not use `--case-filter`: current RAM suites reject it.
4. Add host contracts proving one phase, no hidden correctness matrix, strict
   counts/options, restoration before completion and preserved legacy profiles.
   Verify Windows CLI bindings/CI. Run the first real experiment as this profile's
   VM qualification; no extra performance smoke matrix is necessary.

| Proposed suite | Frozen initial scope | Measurement windows |
|---|---|---:|
| `ram-read-coordination` | Q1T1, Q8T1 and separate-lane Q2T4; diagnostics off/on/off for each | 9 at `--repeats 1` |
| `ram-read-candidate-screen` | Direct sequential 1 MiB Q8T1 target and Q1T1 control; original/candidate, three alternating repetitions | 12 at `--repeats 3` |
| `ram-read-candidate-controls` | Six additional shapes in section 5; original/candidate, three alternating repetitions | 36 at `--repeats 3` |

Candidate suites do not exist at this checkpoint. Implement them only after the
diagnostic gate selects a candidate. Store candidate revision/activation in the
manifest and each case identity; never silently reuse results after an edit.

Commands below are **proposed and not runnable until implemented**. Run each
through the owned job helper with a different unique output parent. Execute the
second only after the diagnostic gate, and the third only after a winning screen:

```powershell
& $qcCli developer verify Q: --campaign experiment --focus-suite ram-read-coordination --lab-ntfs W: --budget-mib 2048 --repeats 1 --duration-seconds 10 --diskspd $qcDiskSpd --pause-backing-cache --output $qcJobRoot
& $qcCli developer verify Q: --campaign experiment --focus-suite ram-read-candidate-screen --lab-ntfs W: --budget-mib 2048 --repeats 3 --duration-seconds 10 --diskspd $qcDiskSpd --pause-backing-cache --output $qcJobRoot
& $qcCli developer verify Q: --campaign experiment --focus-suite ram-read-candidate-controls --lab-ntfs W: --budget-mib 2048 --repeats 3 --duration-seconds 10 --diskspd $qcDiskSpd --pause-backing-cache --output $qcJobRoot
```

## 3. Answer only the remaining RAM coordination question

The question is: **is one submitter limited by supplying requests, or is useful
time being lost posting, taking, withdrawing or waiting for helper work?**
Do not equate a worker's polling percentage to recoverable throughput.

1. Review `QcRamDirectTransfer` and completion in `driver/qcache/ramdirect.cpp`
   and `driver.cpp`; review `LargeCopy`, `CopySplit`, `CopyChunks`, `WorkerMain`
   and `Post` in `driver/ramdisk/transfer.cpp`. Write predicted observations for
   the two explanations before editing native code.
2. Add only missing, default-off diagnostics scoped to the owned RAM resource
   and Direct binding. Existing traces already answer where CPU samples land.

   | Missing fact | Required measurement |
   |---|---|
   | Request overlap | Eligible Direct requests started/completed; active/peak-active reads at clearly defined entry/exit points |
   | Helper participation | Entries posted/taken/withdrawn; useful bytes/chunks copied by submitter versus helpers |
   | Handoff delay | Bounded sampled post-to-take, posting/withdrawal and final helper-wait durations, each with count, units and sample denominator |
   | Scope | Resource/binding generation, mode and worker/CPU identity where needed; unavailable fields stay unavailable |

3. Preserve IRQL and object lifetime. No hot-path allocation, blocking or request
   logs. Sample expensive timestamps; do not timestamp every polling iteration.
   Avoid a global contended counter that could manufacture the observed limit.
   Record overlapping intervals separately rather than summing them as latency.
4. Map transport before changing it: `driver/qcache/ramdirect.h`,
   `driver/shared/ramview.h`, `ramstore.h`, `ramdiskprotocol.h`, provider/control
   handlers and managed `RamDirectState.cs`/`RamDiskProtocol.cs`. Preserve existing
   ABI prefixes and offsets; version/size and explicit field availability must
   agree in both drivers, parsing and tests. Do not reuse retired queue controls.
5. Capture/restore the diagnostic setting and generation. At quiescence check
   started=completed, active=0, posted=taken+withdrawn and eligible byte totals.
   Do not demand transactional snapshots from independent live counters.
6. Run the nine-window diagnostic with unchanged copying. Use owned 2 GiB RAM,
   fitting 1 GiB file, Direct reads, default affinity, CPU Normal, memory 5, I/O 3,
   detailed timing off, three-second warmup and ten-second score. Four readers use
   separate lanes (`-s4M -T1M`, no `-si`). Keep the established DiskSpd binary/hash.
   Preserve zero lower-image attempts, byte checks and maximum two-second native
   telemetry gap. Counters enclose process lifetime; do not relabel as exact scores.
7. Compare off/on/off. Flag a diagnostic throughput shift greater than 2% outside
   both bracketing off results, or unstable off results, as possible perturbation.
   This small diagnostic is not statistical acceptance. Allow at most one scoped
   sampling/collection correction and repeat; never subtract estimated overhead
   from scores or accept unreliable timing as causal evidence.

**Exit:** write a short cause table: observation, supported mechanism, competing
explanation and proposed change. Proceed only if one feasible change has a
specific cost it could remove. If the result only reconfirms high polling or
serialization without a practical safe improvement, close with **no justified
candidate / no acceptable speedup found**. Do not restart the CPU profiler or
expand to priority/UI/filesystem work.

## 4. Implement one candidate and screen it

Select one mechanism from the diagnostic. Examples, not preselected optimizations:

- Excessive late/unused helper posts with measurable coordination cost: change
  one bounded helper-admission or handoff decision.
- Insufficient simultaneous Direct requests with evidence that overlap can help:
  consider bounded asynchronous admission **only if** the lifetime/progress design
  is concrete and testable. Retain existing split copying. A large unqualified
  scheduling rewrite is not required to finish this investigation.

Before coding, state the predicted gain and failure mode. Preserve the existing
mode as a same-build control; make the candidate runtime-only, default-off and
owned-resource scoped. Keep copy code/chunk size, data layout, CPU assignments,
priorities and workload settings unchanged. No bundled second optimization.

For helper changes, preserve stack-backed `SPLIT`/`HELP` lifetime, withdrawal
locking and helper completion at the required IRQL. For asynchronous changes,
prove references/buffer ownership through completion, exactly-once completion,
bounded saturation/fallback, cancellation and stop/withdrawal. No worker may wait
for helper work queued behind itself or behind a set of equally waiting workers.
Preserve fallback, boot-signature checks, ordering and existing large-write routing.

After relevant host tests and signed CI deployment, run the **12-window screen**
with diagnostics/tracing off. Alternate original/candidate order each repetition.
Validate actual activation, byte counts, settings and lower-I/O guards before
reading speed scores. Compare the current same-build control, not historical 26
or 42 GB/s as a pass threshold.

- Target Q8 median must improve by **at least 5%**, exceed observed variation and
  have consistent paired direction across the three repetitions.
- Investigate any repeatable **3% or greater** Q1 throughput loss or latency
  regression outside variation. Report throughput, CPU and latency separately.
- Report every repetition and full ranges. Three repeats do not establish formal
  statistical significance. An overlapping or drifting result is uncertain.
- An ambiguous result may receive **one additional three-pair batch for only the
  target and affected controls**. Use an explicit maintained plan/count, not an
  unsupported filter, and publish both batches without selecting favorable samples.
- A clear failure may receive at most **one revision of the same mechanism**, only
  if evidence identifies a specific correctable issue. A different scheduling
  design or parameter sweep is outside this finalization effort.

**Exit:** a clear losing/unsafe candidate, unresolved ambiguity after the allowed
repeat, or a failed revision closes the candidate. Remove it; keep findings and
useful maintained diagnostics. Do not run the broad release matrix to reject it.

## 5. Check a winning candidate's affected paths

Only a candidate passing the screen proceeds. Run the **36 additional windows**:

| Access | Additional shape |
|---|---|
| Direct | Sequential 1 MiB Q2T4, overlapping streams |
| Direct | Sequential 1 MiB Q2T4, separate lanes |
| Direct | Random 4 KiB Q1T1 |
| Direct | Random 4 KiB Q32T1 |
| Direct | Random 4 KiB Q8T4 |
| Standard | Sequential 1 MiB Q8T1 |

Together with the screen this is 48 windows, not 48 plus a repeated screen.
All must use the same candidate revision/build/settings; a later edit invalidates
affected comparisons. Keep the 3% regression review threshold, byte oracles and
latency checks. If Standard/helper write behavior changes, add only the directly
affected small write control now; defer the 72-case write matrix until final review.

Add/run precise lifecycle checks for the changed ownership path before promotion:
helper take-versus-withdraw races, resource stop/release under load, partial/error
completion as applicable, and cancellation/saturation if admission is asynchronous.
Reuse existing `RamDirectChecks` and owned managed-disk scenarios where they
actually exercise the new path; add a typed maintained scenario for missing
interleavings. Require candidate activation and targeted path evidence. Existing
81 policy checks do not prove new RAM request lifetimes. Avoid new external reboot
or removal requirements unless essential; report an unexercised necessary path
as a qualification gap, not a pass.

Failing a material control/correctness requirement rejects the candidate unless
the single permitted revision remains and directly addresses it. No new research
branch follows a failed candidate.

## 6. Run final qualification once, or close the negative result

### If a candidate survives

1. Select the retained behavior and final source commit. Keep the original-mode
   comparison available through the final decision; diagnostics remain off.
2. Extend the existing release campaign only for **affected missing** candidate
   lifecycle/activation coverage; reuse its existing RAM/performance/write phases.
   Declare required phases and exact counts before the run. Do not assume the
   current broad campaign already tests a newly added native path. Add host
   contract/CLI tests for changed composition and version the contract.
3. Run final host tests/Windows build checks, deploy the signed intended build and
   verify loaded identities. Then launch **one** broad campaign through the owned
   job helper. No parallel VM test/UI workloads and no periodic agent polling.

   ```powershell
   & $qcCli developer verify Q: --campaign release-performance --lab-ntfs W: --budget-mib 2048 --repeats 3 --duration-seconds 10 --diskspd $qcDiskSpd --pause-backing-cache --output $qcJobRoot
   ```

   This is an existing command. Verify the candidate-specific composition before
   executing it. It includes the 72-case writes and 30-minute sustained phase;
   it is broader than `--suite full`. Keep ReFS detached/omitted for RAM-only
   changes; include `--lab-refs R:` only if affected scope warrants it and the lab
   is validated. Existing native map/priority baseline phases in this campaign
   do not reopen the parked UI-rendering or low-priority research projects.
4. Wait for process exit and validated completion, then read `FINISHED.txt`,
   `status.json`, `SUMMARY.md`, `results.json`, `run.log`, restoration and affected
   raw evidence from that exact run. `MEASURED` is collection, not acceptance.
   Check candidate activation, byte integrity and actual comparable regressions.
5. If final qualification fails, report the exact failure. Fix/revert within the
   candidate budget and repeat affected checks; repeat the broad run only if the
   change invalidates its wider evidence. Never combine unfinished repetitions
   or call the feature qualified because its speed test passed.
6. After acceptance, update README measurements only where affected, following
   the normal-priority real CrystalDiskMark GUI procedure and generated images.
   Do not replace GUI screenshots with runner scores. This is presentation after
   acceptance, not another optimization search.

### If no candidate survives

Remove the candidate and restore original defaults; retain useful low-cost,
default-off diagnostics only if their compatibility/off-path behavior is qualified.
Run checks affected by the removal. A wholly removed native experiment does not
need the final 72-write/sustained campaign. Do not leave an unsafe/inconclusive
mode enabled or label an unverified retained diagnostic change harmless.

Close the research with the exact attempted hypothesis, results, rejection reason
and remaining uncertainty. Keep low-priority/UI/caller work parked rather than
substituting it as the next automatic task.

## Files and validation responsibilities

| Area | Entry points |
|---|---|
| Direct admission/completion | `driver/qcache/ramdirect.cpp`, `ramdirect.h`, `driver.cpp` |
| Provider coordination/copy | `driver/ramdisk/transfer.cpp`, `provider.h` |
| Native/managed diagnostic contract | `driver/shared/ramview.h`, `ramstore.h`, `ramdiskprotocol.h`; actual control handler found during transport review; `src/QueueCache.Management/RamDirectState.cs`, `RamDiskProtocol.cs` |
| Typed cases/fixture/evidence | `src/QueueCache.Developer/Verification/RamReadReferencePlan.cs`, `RamReadReferenceScenarios.cs`, `RamReadReferenceEvidence.cs` |
| Suite dispatch/cleanup | `VerificationPlan.cs`, `VerificationRunner.cs`, `VerificationWorker.cs`, `RunStorage.cs` in the same directory |
| Campaign composition/completion | `VerificationCampaignPlan.cs`, `VerificationCampaignRunner.cs`, `VerificationCampaignHost.cs`, `VerificationCampaignEvidence.cs` |
| CLI and host checks | `src/QueueCache.Cli/VerificationCommands.cs`, `tests/QueueCache.Management.Tests/VerificationRunnerTests.cs`, `build/Test-Cli.ps1` |
| Actual Direct lifecycle coverage | `src/QueueCache.Developer/Verification/RamDirectChecks.cs`, existing owned managed-disk scenarios |

Host command: `dotnet run --project tests/QueueCache.Management.Tests -c Release`.
Run meaningful changed-path contracts on Windows too, plus the existing Windows
Debug/Release CI and CLI checks. No Desktop code is planned, so no new Desktop
performance campaign is needed. Native edits need their affected native/runtime
checks; Linux host success alone is not driver verification.

## Run budget and final handback

These are planning allowances, not measured future durations:

| Stage | Scope/time expectation |
|---|---|
| Manager delivery | One existing smoke, historically about 1 minute; deployment troubleshooting is separate. |
| Missing-fact diagnosis | 9 windows; budget roughly 5–15 minutes of VM time including fixture/setup. At most one collection correction/retry. |
| Candidate screen | 12 windows; roughly 5–15 minutes. Stop on clear failure. |
| Additional controls | 36 windows only for a winner; roughly 10–25 minutes, plus specifically affected lifecycle checks. |
| Final broad campaign | Once for a retained candidate; write/sustained phases alone previously total about 84 minutes, with other phases additional. |

Coding, CI, signed deployment and analysis are additional; no reliable engineering
duration is established. Record real phase times and failures. The case/revision
limits prevent an open-ended research loop; an infrastructure failure warrants
repairing that specific prerequisite, not silently increasing the research scope.

Before ending, update [PERFORMANCE_FINDINGS.md](PERFORMANCE_FINDINGS.md) with one
explicit final outcome, same-build medians/ranges and limits. Update
[RAM_FIRST_IMPLEMENTATION_TRACKER.md](RAM_FIRST_IMPLEMENTATION_TRACKER.md) with
implementation and verification separately. Commit/push scoped work to the
current PR and update its description; do not merge without authorization for
that PR. Preserve private evidence and restore lab/UI/settings independently.

The user handback must state:

- **Speedup accepted, no acceptable speedup found, or verification blocked**,
  with the concrete reason; do not disguise a blocker as a negative result.
- What changed or was removed; measured benefit and any control losses.
- Which checks ran, actual time, restoration status and necessary coverage gaps.
- Whether automatic agent wake was actually observed, not just implemented.
- PR/commit status and a clearly closed research scope. No proposed next round
  of priority/UI/filesystem experiments unless the user asks for it.
