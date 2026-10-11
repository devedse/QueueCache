# Performance continuation: implementation handoff

**Superseded execution plan:** use
[PERFORMANCE_FINALIZATION_HANDOFF.md](PERFORMANCE_FINALIZATION_HANDOFF.md).
It bounds the remaining diagnosis/candidate work and defines final acceptance or
closure. This document retains historical design detail; its old stage order,
proposed names and broad intermediate test commands are not the current backlog.

Prepared 2026-10-10 against `perf/ram-read-followups`, planning commit `4fdb73f`,
verification plan 114. This document records the plan at that checkpoint. Implementation has since
started: consult the tracker for actual suite/delivery/verification status;
conditional native optimizations remain evidence-gated. The existing
[follow-up plan](PERFORMANCE_FOLLOWUP_PLAN_20261010.md) explains the priorities.
[PERFORMANCE_FINDINGS.md](PERFORMANCE_FINDINGS.md) owns measured conclusions;
[RAM_FIRST_IMPLEMENTATION_TRACKER.md](RAM_FIRST_IMPLEMENTATION_TRACKER.md) owns
implementation and verification status. Update those sources after each stage.

User scope update, 2026-10-11, supersedes the original execution order below:
stage D's priority/affinity comparison is complete and further low-priority
diagnosis is parked. Stage E's dedicated 100 ms UI cost study is removed from the
active backlog. Prioritize a concrete RAM candidate, using maintained individual
suites and affected correctness checks during iteration; do not automatically
attach all 81 retained checks to every diagnostic. Stage G's broad campaign is
the final qualification of a retained change, launched once and reviewed from its
combined report. Preserve each selected suite's evidence/restoration contract;
this instruction does not alter case counts or skip correctness relevant to a
new request-lifetime/copy path. Completion delivery remains a separate short
integration check. See the current follow-up plan and tracker for status.

## 1. Starting instructions for the implementing agent

1. Read [AGENTS.md](../AGENTS.md), the current conclusions and next steps in
   PERFORMANCE_FINDINGS.md, the current-work table in the tracker, and
   [PERFORMANCE_CAMPAIGN_PLAN.md](PERFORMANCE_CAMPAIGN_PLAN.md). Read this handoff
   before choosing a change. Historical reports are evidence, not a backlog to
   rerun in full.
2. Inspect the current branch, working tree and intervening commits. At this
   checkpoint work belongs to existing draft PR #9 on `perf/ram-read-followups`.
   Preserve unrelated changes. If that PR has since merged, start the next branch
   from the merged result and record the new base. This handoff does not authorize
   merging PR #9 or doing destructive VM recovery.
3. Enter the actual starting commit, verification-plan version, selected stage
   and its expected deliverable in the tracker. Do not assume the version remains
   114. Increment the verification-plan version when changing a measurement
   contract; build/product versions continue to come from GitHub Actions.
4. Execute A and B independently. A's unavailable external API must not prevent B.
   Execute C only if B establishes an actionable bottleneck. D is the next separate
   diagnostic. E is optional UI work; F is a lower-priority conditional experiment.
   G qualifies whatever code is actually retained. Do not launch concurrent VM
   workloads, even when implementation work can proceed independently.
5. Keep commits small: runner/contract changes; diagnostic findings; one candidate;
   accepted result or removal. Every implementation checkpoint records tests run
   and remaining verification separately. Update the existing PR description when
   its implemented scope changes. Never describe a planned feature as shipped.

Suggested first commands, on the repository host:

```bash
git status --short --branch
git log -5 --oneline
rg -n 'Version =|IsRamReadSuite' src/QueueCache.Developer/Verification/VerificationPlan.cs
rg -n 'Current conclusion|What to do next' docs/PERFORMANCE_FINDINGS.md
```

### Established facts to preserve

| Fact | Consequence for this work |
|---|---|
| Ordered 256 KiB chunks, bounded cache-hit copies and read recall have retained gains. | Keep them; do not reopen allocator/defragmentation work without contradictory resident measurements. |
| Latest matched RAM controls: Q8T1 25.84 GB/s default affinity, 26.30 unbound; four separate readers 37.97–39.04 GB/s. | A concurrency/coordination question remains. Historical 42–45 GB/s scores are not the current single-reader target or a matched release comparison. |
| The removed whole-read queue lost 11.30% Q8 and 49.62% four-reader throughput. It also removed split copying. | Do not restore that implementation. Isolate scheduling from copy granularity in any successor. |
| Benchmark `-n` did not change provider helper affinity. | CPU assignment and helper coordination are not fully ruled out. |
| Mixed/recovery staged-byte overlap was 0.788%/0.449%. | Missing-span reads remain deferred for those workloads. |
| Global caller backoff 0 gained 4–5% mixed throughput and lost 21% NTFS large-read throughput. | Preserve default 256; a selective policy needs new evidence. |
| Campaign smoke and focused run passed; the latter took 17 min 21 s, with 30 RAM windows and 81 retained-driver checks. | Reuse the runner. Existing checks do not qualify future asynchronous request lifetime changes. |
| Completion event exists; automatic Manager delivery does not. | Implement delivery outside the driver/benchmark runner. |

Latest completed focused evidence ID:
`QueueCache-Campaign-20261010-124559-697930c96ddb4e57a97924a301d5a7e3`.
Its native filter/provider hashes and archive identity are in
[the findings](PERFORMANCE_FINDINGS.md#ram-disk-affinity-and-source-lane-controls-gap-remains).
Private raw evidence remains private; locate that exact run through the existing
lab access, not by combining historical directories. Missing raw evidence does
not justify inventing results or automatically repeating completed experiments.

## 2. File and component map

Paths in this table are existing files. Names explicitly marked **new** below are
proposed additions, not APIs that can be invoked today.

| Responsibility | Files and entry points |
|---|---|
| CLI binding/help | [VerificationCommands.cs](../src/QueueCache.Cli/VerificationCommands.cs), `Create`, `CreateCompletion`; [DeveloperCommands.cs](../src/QueueCache.Cli/DeveloperCommands.cs) for lab-disk commands |
| Suite contract and options | [VerificationPlan.cs](../src/QueueCache.Developer/Verification/VerificationPlan.cs), `Suites`, `IsRamReadSuite`, `Integrity`, `Validate`, `Version`; [RunStorage.cs](../src/QueueCache.Developer/Verification/RunStorage.cs), `VerificationOptions` |
| Campaign composition | [VerificationCampaignPlan.cs](../src/QueueCache.Developer/Verification/VerificationCampaignPlan.cs), `FocusSuites`, `Create`, `ExpectedCases` |
| Campaign execution/evidence | [VerificationCampaignRunner.cs](../src/QueueCache.Developer/Verification/VerificationCampaignRunner.cs), `RunAsync`; [VerificationCampaignHost.cs](../src/QueueCache.Developer/Verification/VerificationCampaignHost.cs); [VerificationCampaignEvidence.cs](../src/QueueCache.Developer/Verification/VerificationCampaignEvidence.cs), `CampaignCompletionEvent`, `ReadCompletion` |
| RAM case definitions/arguments | [RamReadReferencePlan.cs](../src/QueueCache.Developer/Verification/RamReadReferencePlan.cs), `CasesFor`, `SchedulingCases`, `Arguments`, `ValidateProfile` |
| RAM fixture and telemetry ownership | [RamReadReferenceScenarios.cs](../src/QueueCache.Developer/Verification/RamReadReferenceScenarios.cs), `RunAsync`, `CleanupAsync`; [RamReadReferenceEvidence.cs](../src/QueueCache.Developer/Verification/RamReadReferenceEvidence.cs) |
| Coordinator/worker boundary | [VerificationRunner.cs](../src/QueueCache.Developer/Verification/VerificationRunner.cs), RAM manifest/case/cleanup paths; [VerificationWorker.cs](../src/QueueCache.Developer/Verification/VerificationWorker.cs), `WorkerJob`, `ram-read-reference`, `ram-read-cleanup` |
| Child ownership and priorities | [OwnedProcess.cs](../src/QueueCache.Developer/Verification/OwnedProcess.cs), [ProcessScheduling.cs](../src/QueueCache.Developer/Verification/ProcessScheduling.cs), [TelemetryCoverage.cs](../src/QueueCache.Developer/Verification/TelemetryCoverage.cs) |
| Cache priority/caller/map workloads | [CacheExercisePlan.cs](../src/QueueCache.Developer/Verification/CacheExercisePlan.cs), `CacheExerciseCase`, `Cases`; [CacheExerciseRunner.cs](../src/QueueCache.Developer/Verification/CacheExerciseRunner.cs), `MeasureExercise`, `DiskTargets`; [CacheExerciseEvidence.cs](../src/QueueCache.Developer/Verification/CacheExerciseEvidence.cs) |
| Direct transfer/completion | [ramdirect.cpp](../driver/qcache/ramdirect.cpp), `QcRamDirectTransfer`, `Copy`; [driver.cpp](../driver/qcache/driver.cpp), Direct completion and caller routing |
| Provider split copy/helpers | [transfer.cpp](../driver/ramdisk/transfer.cpp), `CopySplit`, `CopyChunks`, `WorkerMain`, `LargeCopy`; [provider.h](../driver/ramdisk/provider.h), worker structures and constants |
| Native/managed diagnostic compatibility | [writecache.h](../driver/qcache/writecache.h), [writecache.cpp](../driver/qcache/writecache.cpp), [ramview.h](../driver/shared/ramview.h), [ramstore.h](../driver/shared/ramstore.h), [ramdiskprotocol.h](../driver/shared/ramdiskprotocol.h), [CacheDiagnostics.cs](../src/QueueCache.Management/CacheDiagnostics.cs), [RamDiskProtocol.cs](../src/QueueCache.Management/RamDiskProtocol.cs), [WriteCacheState.cs](../src/QueueCache.Management/WriteCacheState.cs) |
| Desktop sampling and display | [DashboardMonitor.cs](../src/QueueCache.Desktop/ViewModels/DashboardMonitor.cs), `MapInterval`; [ShellViewModel.cs](../src/QueueCache.Desktop/ViewModels/ShellViewModel.cs); [DesktopSettings.cs](../src/QueueCache.Desktop/Services/DesktopSettings.cs); [CacheMap.cs](../src/QueueCache.Desktop/Controls/CacheMap.cs); [CacheMapWindow.axaml.cs](../src/QueueCache.Desktop/Views/CacheMapWindow.axaml.cs) |
| Host/CLI/frontend contracts | [VerificationRunnerTests.cs](../tests/QueueCache.Management.Tests/VerificationRunnerTests.cs), [CacheExerciseTests.cs](../tests/QueueCache.Management.Tests/CacheExerciseTests.cs), [Program.cs](../tests/QueueCache.Management.Tests/Program.cs), [Test-Cli.ps1](../build/Test-Cli.ps1), [desktop tests](../tests/QueueCache.Desktop.Tests) |

## 3. Common verification procedure

Apply this procedure to each implementation stage; do not repeat VM runs for
documentation-only changes.

1. Run host contracts after changing plans, serialization, process ownership or
   restoration. On a suitable .NET host:

   ```text
   dotnet run --project tests/QueueCache.Management.Tests -c Release
   ```

   Windows process/driver-adjacent host tests must also pass on Windows. A Linux
   build or skipped Windows tests are not equivalent. For frontend changes:

   ```text
   dotnet run --project tests/QueueCache.Desktop.Tests -c Release
   ```

2. Use the existing Windows Debug/Release workflow
   [.github/workflows/githubactionsbuilds.yml](../.github/workflows/githubactionsbuilds.yml).
   It invokes `build/Build.ps1`, packaging checks and `build/Test-Cli.ps1`. Record
   the exact successful code commit. Do not manufacture a product version or
   substitute a locally unsigned driver for the intended CI artifact.
3. Managed-only work can use a managed preview with the retained native driver.
   Native changes require deployment through the established signed lab procedure
   and confirmation of actual loaded module filenames/hashes. Obtain matching
   PDBs for profiling. If loading the artifact requires a restart or recovery
   outside existing authorization, record that specific dependency; do not reboot
   as an automatic fallback or force a historical downgrade.
4. On the Windows VM, use the established authenticated transport and an elevated,
   Normal-priority session. Do not print credential-bearing helper files. Record
   CPU count/placement, Verifier state, last-access setting, native hashes, managed
   commit, DiskSpd hash, cache budget/copy flags and timing. Performance runs need
   Verifier off; correctness with Verifier is separate evidence.
5. Capture original runtime settings, saved profiles, owned UI process identity,
   attached lab identities and any active tracing. Stop only competing processes
   known to belong to this work. No delay/fault hooks may be armed. Use the clean
   non-OS physical data volume for the performance role and the maintained lab
   VHDX for NTFS/ReFS correctness roles. Reconfirm drive letters after attachment.
   Let explicit preparation finish setup writes before strict clean capture.
6. Choose an existing lab image through the lab inventory; do not create or format
   an unrelated disk. Existing supported attachment command:

   ```powershell
   & $qcCli developer lab-disk attach $qcNtfsLabImage
   ```

   `$qcNtfsLabImage` is the validated existing image path. Q:/W:/R: below are role
   examples, not assumed identities. Record actual replacements. `--pause-backing-cache`
   is appropriate only when the lab's active backing cache is the declared Q: role.
7. Select one unique output parent per launched job. Keep the same DiskSpd binary
   across comparisons. Historical controls used CDM 9.0.3's x64 DiskSpd 2.2:
   `7281BF6DA6C03797016EDDF2E8AAEC4C644AE893D403D57A030B7E2E14B61079`.
   If selecting a different supported binary, create a fresh matched baseline;
   do not compare its scores directly to that historical reference.

   ```powershell
   # Set these to the selected managed preview and existing benchmark binary.
   $qcCli = 'C:\Tools\QueueCache-Preview\qcache.exe'
   $qcDiskSpd = 'C:\Tools\DiskSpd\DiskSpd64.exe'
   if (!(Test-Path $qcCli) -or !(Test-Path $qcDiskSpd)) { throw 'Set the actual verified executable paths.' }
   Get-FileHash $qcDiskSpd -Algorithm SHA256
   $qcJobRoot = Join-Path 'C:\QueueCache-Results' ('Handoff-' + [guid]::NewGuid().ToString('N'))
   New-Item -ItemType Directory -Path $qcJobRoot | Out-Null
   & $qcCli developer driver loaded
   ```

8. Run the stage's foreground campaign once. Capture its process exit through the
   controller; keep synchronous progress in `run.log`. Do not use `--detach`, a
   private replacement orchestration script, or periodic agent SSH polling.
9. After the known process exits, inspect only its output parent. Expect exactly
   one campaign subfolder; zero means startup failure and multiple means broken
   job ownership, not permission to pick the newest. Run `verify-completion` on
   that exact directory. Then inspect `FINISHED.txt`, `status.json`, `SUMMARY.md`,
   `results.json`, `run.log`, `restoration.json` and every indexed child required
   for the decision. Check raw XML, process exits, priority readbacks, before/after
   bytes, native identity, ready handshakes and interval coverage. A completed
   report is collection success; it is not automatically performance acceptance.
10. If restoration fails, stop work on that VM and inspect ownership/recovery
    evidence. Only after proving owned processes have exited, use existing
    `qcache developer verify-recover <exact-child-run-directory>` when applicable.
    Do not retry faults as drains, reset defaults, kill arbitrary PowerShell
    processes or combine failed repetitions with a retry.
11. Restore original runtime/settings/profiles, release owned traces/processes,
    detach only lab images attached for the task and resume the previously stopped
    UI. Record actual restoration, including failures, separately from performance.

## 4. Stage A: deliver completion to the agent

**Scope:** Manager/controller integration. QueueCache already supplies the event
and strict read-only validator. `dam-tools --help` was checked during planning;
it exposes `share-image` only. No wake API or Manager source path is known here.
Do not invent an endpoint, executable command or source file for that repository.

1. Discover the Manager's supported foreground-job/process-exit callback and
   conversation-resume API from its source or owner-provided contract. Record
   repository, entry points, authentication owner and cancellation semantics.
   If unavailable, mark A `blocked: Manager API/source unavailable` in the tracker
   and continue B. Keep the implemented runner/completion reader usable.
2. In the Manager's existing job subsystem, persist a launch record before spawn:
   job ID, conversation ID, machine/transport identity, explicit output parent,
   executable/arguments, process identity, launch time and eventual campaign path.
   Use structured arguments. Remote transport disconnection alone does not prove
   the foreground job has exited; reconnect to the same job rather than relaunching.
3. Allocate an empty per-job output parent. Subscribe to the owned process exit,
   not a recurring agent task. Bind the campaign path only inside that parent;
   validate its campaign ID/manifest correlation before accepting an event.
4. On exit, execute `verify-completion` against the exact remote run directory
   using that run's CLI. Read its JSON only if validation succeeds. Do not copy
   the directory and then assume an absolute-path identity check still matches.
5. Consume the existing schema-1 fields without renaming or defaulting missing
   values: `SchemaVersion`, `EventId`, `CampaignId`, `ManifestSha256`, `Directory`,
   `Status`, `ExpectedPhases`, `CompletedPhases`, `ExpectedCases`, nullable
   `CollectedCases`, `Restoration`, `Finished`. The runner uses terminal statuses
   `COMPLETED`, `INCOMPLETE`, `RESTORATION_FAILED`, `CANCELLED`.
6. Persist a controller outbox entry keyed by stable `EventId` and the job binding.
   Use a transactional claim plus an idempotent resume key at the Manager delivery
   boundary. Commit delivery acknowledgement only after the Manager accepts it.
   A crash between delivery and acknowledgement must not create a second analysis
   turn. If the API cannot provide deduplication, record that limitation rather
   than claiming exactly-once delivery. Retry notification only, with bounded
   backoff; never rerun benchmarks to deliver a result.
7. Resume the associated conversation with terminal status, restoration outcome,
   elapsed time/counts, exact report path and errors needed to start analysis.
   Do not put credentials or raw private connection data in the event. Keep
   delivery state in the controller; leave immutable campaign evidence unchanged.
8. Handle missing/invalid terminal evidence as a separate job outcome requiring
   inspection. `INCOMPLETE` and restoration failure still need delivery. Do not
   synthesize `completion.json`, imply a passed test from exit zero, or stop an
   unowned process/trace to make recovery succeed.
9. Add Manager tests for normal completion; nonzero exit with a valid failure
   event; cancellation; process death before event publication; invalid digest;
   cross-job path/event mismatch; duplicate callback; delivery-before-ack crash;
   restart with pending outbox; temporary transport loss while the job still runs;
   and simultaneous callbacks claiming the same event.
10. Run one existing smoke campaign through the new controller:

    ```powershell
    & $qcCli developer verify Q: --campaign smoke --lab-ntfs W: --pause-backing-cache --output $qcJobRoot
    ```

    Prove one automatic resume after terminal evidence and cleanup. Historical
    smoke runtime is about one minute. Preserve failed-job integration tests with
    fixtures; do not inject native faults just to test notification plumbing.

**Done:** one validated result reaches the correct agent automatically, including
failures; retry/reconnect does not run the workload or analysis twice. A completion
file alone does not close A. Update AGENTS and campaign documentation only to the
level actually delivered.

## 5. Stage B: attribute the RAM-disk bottleneck

### B1. Define a small maintained diagnostic

1. Inspect `QcRamDirectTransfer` through Direct completion, then `LargeCopy`,
   `CopySplit`, `CopyChunks`, `WorkerMain`. Write a hypothesis in the findings:
   single-submitter throughput may be limited by synchronous request admission,
   helper handoff/withdrawal or waiting. State what observation would distinguish
   those causes. Reuse preserved CPU profiles for this first analysis.
2. Add proposed suite **`ram-read-attribution`** to the existing runner. This is
   new work: it must not be invoked until implemented. Use the existing 2 GiB
   owned RAM fixture, fitting 1 GiB file, Direct access and unchanged split-copy
   path. Do not add a separate executable or private orchestration script.
3. Define exactly these shapes in the typed RAM plan:

   | Shape | Block | Queue/thread configuration | Source traversal |
   |---|---|---|---|
   | A1 | 1 MiB | Q1T1 | Ordinary sequential |
   | A2 | 1 MiB | Q8T1 | Ordinary sequential |
   | A3 | 1 MiB | Q2T4 | Separate lanes: `-s4M -T1M`, no `-si` |

   Use default benchmark affinity, CPU Normal, memory priority 5, I/O hint 3,
   three-second warmup and ten-second measured workload. Require matching raw XML
   settings, normal priority readbacks and the same recognized stderr rule as
   existing RAM controls. Repeats produce `3 * repeats` diagnostic windows with
   unique IDs and alternating shape order. One repetition is sufficient for the
   first attribution pass; it is explicitly not speed acceptance.
4. Add a typed RAM run kind rather than another pair of ambiguous queue/scheduling
   booleans. Preserve legacy plan meanings and archived queue compatibility. Make
   `RamReadReferencePlan.CasesFor` the single generator for coordinator manifest,
   worker execution, count validation and campaign window counts. Carry the kind
   through `WorkerJob`; update the worker's currently duplicated plan selection.
5. Register the suite in `VerificationPlan.Suites`, `IsRamReadSuite`, `Integrity`
   and validation; add it to `VerificationCampaignPlan.FocusSuites`. Reuse the
   existing `ram-read-reference` worker ownership/cleanup path. Update CLI help,
   RAM dispatch in `VerificationRunner`/`VerificationWorker`, host contracts and
   developer docs. Keep it out of broad performance/release profiles during
   diagnosis. Current RAM suites and campaigns reject `--case-filter`; do not
   document a nonexistent filter to select A1–A3.

### B2. Collect and interpret attribution

1. Prefer sampled CPU/context-switch evidence before adding native counters.
   Introduce an owned trace adapter, suggested **new**
   `src/QueueCache.Developer/Verification/VerificationTraceSession.cs`, separate
   from plan generation and score parsing. The attribution suite explicitly
   records that it is traced/diagnostic; acceptance suites remain untraced.
2. Resolve an installed Windows CPU/context-switch profile and analysis tool from
   the actual VM toolchain. Record their paths/versions/profile definition in
   evidence. Validate required providers/symbol coverage before the workload.
   Do not assume an arbitrary WPR profile captures every required event or invent
   a trace command without checking the installed tool's help/profile inventory.
3. Check existing tracing first. Do not stop another session. Journal ownership
   before starting the trace; stop/finalize only the owned session under an
   independent cleanup deadline on success, failure and cancellation. Use bounded
   process invocations through the existing ownership abstraction. Preserve ETL
   output in the exact run directory, with hash and start/stop/exit evidence.
   The parent RAM cleanup/recovery path must read that ownership journal and
   finalize the owned trace after a worker exits or is terminated before `finally`.
   Validate session identity before stopping it; a worker-local `finally` alone
   is not sufficient. Include an abrupt-worker-exit cleanup contract.
4. Cover A1–A3 with one owned trace session if that profile permits reliable
   attribution. Record owned benchmark PIDs and explicit enclosing process
   intervals for every case. Startup/warmup/oracle activity is not the exact
   DiskSpd score interval. Reject lost events or unusable/mismatched symbols for
   attribution; an ETL file merely existing is insufficient.
5. Produce a table per shape: CPU utilization by CPU/thread, bytes copied, useful
   copy samples, helper queue/poll/withdrawal samples, submitter waits/ready time,
   and observed request overlap if the trace supports it. Map stacks/instruction
   ranges to source; `WorkerMain` contains both work and polling. Never label its
   entire function share wasted CPU. If exact concurrency or time decomposition
   is unavailable, label it unknown and proceed to B3 only for that missing fact.
6. Write typed machine-readable attribution output plus a short explanation of
   the selected cause, competing explanation and uncertainty. This belongs in
   maintained verification/reporting code; raw ETLs and one-off analysis outputs
   stay private. Do not treat traced throughput as a release comparison.
7. Add meaningful host contracts: three shapes/counts, immutable IDs, strict
   settings, correct typed-kind forwarding and cleanup dispatch; trace refusal
   when already owned elsewhere; stop on startup/workload/cancellation errors;
   missing ETL, lost events, wrong symbols and unavailable attribution; rejection
   of cross-run trace/PID evidence. Keep existing readiness/two-second RAM coverage
   and native/whole-file evidence unchanged.
8. After host checks and Windows CI, execute the new diagnostic once:

   ```powershell
   # NEW suite: runnable only after B1/B2 are implemented.
   & $qcCli developer verify Q: --campaign focused --focus-suite ram-read-attribution --lab-ntfs W: --budget-mib 2048 --repeats 1 --duration-seconds 10 --diskspd $qcDiskSpd --pause-backing-cache --output $qcJobRoot
   ```

   With current campaign composition this is six phases: four retained correctness
   phases, one RAM diagnostic phase, and ordering/faults last. It has three RAM
   diagnostic windows plus the existing 81 inner correctness checks. Generate and
   validate outer counts from the plan; do not substitute the inner count for it.

### B3. Add native diagnostics only if B2 cannot answer the question

1. Write the missing question first. Add only fields that can answer it, scoped
   to the owned RAM resource/Direct binding; do not infer its behavior from global
   traffic on other disks. Suggested field groups are:

   | Question | Candidate fields |
   |---|---|
   | Are requests serialized? | Eligible Direct requests started/completed, active and peak-active count |
   | Do helpers perform useful work? | Helper entries posted/taken/withdrawn; caller/helper chunks and bytes |
   | Is coordination expensive? | Bounded samples/counts of posting, withdrawal and helper-wait duration; measured sample count |
   | Is work queued elsewhere? | Provider worker identity/CPU and owned-resource participation, with unavailable attribution explicit |

2. Keep collection default-off. No allocation, blocking calls or per-request log
   emission on the hot path. Respect IRQL, concurrency and lifetime requirements.
   Use per-resource or per-CPU aggregation where needed. Specify counter scope,
   units, reset rules and identity/generation before coding. Do not add independent
   counters' overlapping durations and call that total request latency.
3. Trace the existing native/managed transport end to end before choosing an ABI
   extension. Preserve old prefixes, sizes and offsets; append a new version with
   explicit availability. Update native static assertions, managed parsing and
   compatibility tests together. Older drivers return unavailable, not zero.
   Do not recycle retired `LabRamReadQueue` modes/actions or assume the next ID
   is free. New lab controls must be captured, restored and read back.
4. Validate quiescent invariants, where applicable: started equals completed and
   active equals zero; posted equals taken plus withdrawn; caller/helper eligible
   bytes reconcile to the instrumented scope. Do not require individually atomic
   snapshots to be transactional while work is in flight. Reset only under the
   documented quiescent boundary and record the new measurement generation.
5. Compare diagnostics off/on/off with copying and scheduling unchanged to
   quantify perturbation. Extend the typed attribution plan/counts explicitly if
   needed. Keep collection off for final speed acceptance. Material perturbation
   means reduce collection cost or use sampling; it does not justify subtracting
   an estimated overhead from scores.

**B exit gate:** document one actionable bottleneck and the evidence that supports
it, or record `inconclusive/no justified optimization`. If inconclusive, stop C
and proceed to D. Do not widen the matrix simply because the result is negative.

## 6. Stage C: one conditional native optimization

### C1. Select and implement one mechanism

1. In the findings, name the exact cost B identified and the one intended change.
   If posting/waiting is dominant, modify one helper admission/handoff decision.
   If inline request serialization is dominant and cost analysis supports it,
   try bounded asynchronous admission while retaining `CopySplit`. If the evidence
   points elsewhere, update this decision before coding; do not silently change
   multiple mechanisms to obtain a better score.
2. Keep the current mode as the same-build control. Add a default-off, bounded,
   runtime-only experiment scoped to the owned fixture. Preserve the actual copy
   algorithm, 256 KiB split size, file layout, CPU/application priorities and
   mode-independent preparation. Capture/restore the lab setting and reject
   unsupported drivers before creating a workload. Do not make it a saved user
   preference or change production defaults during experimentation.
3. For helper-only changes, preserve `HELP` withdrawal locking and the lifetime of
   stack-backed `SPLIT`. The existing worker raises IRQL while taking/copying helper
   work; review scheduling constraints before changing that behavior.
4. For asynchronous admission, document state transitions before implementation:
   admit/publish, cancel-before-copy, copying, completing and released. Acquire
   required binding/store/remove ownership and map the buffer before publication;
   mark pending before an executor can observe it. Complete once, then release
   ownership exactly once. Stop/withdrawal must close admission before draining.
   Bounded saturation needs a tested safe fallback. Prove no executor waits on
   split-helper work queued behind itself or behind workers all waiting on it.
   Preserve existing standard fallback, control ordering and boot-signature checks.
5. Keep diagnostic counters separate from the experiment switch. Add activation
   evidence so a candidate case cannot silently run the control path. Tests of
   inactive small/Standard paths must expect non-activation where appropriate.

### C2. Add the matched A/B suite

Add proposed suite **`ram-read-candidate`** using the same typed RAM machinery,
strict XML/settings and ownership. This name denotes the current hypothesis in
the manifest; include its identifier in every case ID so future candidates cannot
overwrite or be mistaken for this experiment.

| Access/control | Shape |
|---|---|
| Direct | 1 MiB Q1T1 |
| Direct | 1 MiB Q8T1 |
| Direct | 1 MiB Q2T4, overlapping streams |
| Direct | 1 MiB Q2T4, separate lanes |
| Direct | Random 4 KiB Q1T1 |
| Direct | Random 4 KiB Q32T1 |
| Direct | Random 4 KiB Q8T4 |
| Standard | 1 MiB Q8T1 |

1. Cross eight shapes with control/candidate and three alternating repetitions:
   **48 untraced windows**. Fix benchmark affinity at the selected B baseline;
   do not repeat the entire affinity factorial. Keep CPU Normal, memory 5, I/O 3,
   W3, ten-second scores, 2 GiB RAM and a 1 GiB file. Reverse variant order on
   alternating repetitions. Freeze IDs, activation expectations and counts.
2. Add focused-campaign registration, plan/worker/evidence propagation and tests
   exactly as in B1. Preserve historical suites. Native diagnostics stay off for
   acceptance except inexpensive required mode/identity/correctness observations.
3. Before scoring, test new-path byte correctness and basic bounded progress on
   the owned fixture. Then run:

   ```powershell
   # NEW suite: runnable only after the selected candidate and its contracts exist.
   & $qcCli developer verify Q: --campaign focused --focus-suite ram-read-candidate --lab-ntfs W: --budget-mib 2048 --repeats 3 --duration-seconds 10 --diskspd $qcDiskSpd --pause-backing-cache --output $qcJobRoot
   ```

4. Compare matched medians, complete per-repeat values/ranges, latency and CPU
   cost. Report decimal GB/s consistently; preserve raw bytes/seconds and MiB/s.
   Do not average percentile latencies and call the result a combined percentile.
   Missing latency/CPU fields remain unavailable. Diagnostic process intervals
   must not be labeled exact score intervals.
5. Acceptance proposal: at least **5%** gain on the declared target shape, larger
   than observed run variation. Any reproducible control loss of **3% or more**
   requires investigation and blocks automatic promotion; smaller consistent
   regressions still need assessment. These are practical gates, not significance
   tests. Add at most one focused confirmation batch initially if uncertainty can
   change the decision; predeclare its scope and preserve both batches.

### C3. Qualify lifetime and decide

1. Extend maintained owned-fixture scenarios for the newly exercised lifetime
   paths. Required cases for asynchronous work: cancellation before admission,
   while queued and while copying; stop/withdrawal with active work; allocation
   failure/queue saturation; mode changes with work outstanding; concurrent
   request/control ordering; no completion-after-release or double completion;
   resource/budget restoration. Add host ownership contracts and real VM cases;
   host tests alone cannot establish kernel race safety.
   Register proposed suite **`ram-read-lifecycle`** with typed named subcases in
   the same runner and owned managed-disk cleanup path. Keep its checks separate
   from the read-score case generator and expose their expected count in the
   manifest. Add it to focused campaign selection and run it after the speed gate;
   do not claim existing `ordering-faults` exercises a new RAM request queue.
2. Keep fault/cancellation tests separate from speed windows. They must actually
   exercise their branch through counters or deterministic bounded hooks, not
   merely call cancellation after the work finished. End fault scenarios last;
   use owned managed disks, not arbitrary physical-disk removal.
3. Shared provider/helper changes require affected Standard reads and writes in
   addition to Direct controls. Select the maintained write/reference checks
   appropriate to the changed path; run the full 72-case write matrix when shared
   write behavior is affected or as part of release qualification. Its payload,
   drain and timing contracts must stay unchanged.
   That disk-cache write matrix does not establish RAM-provider write performance.
   If shared RAM worker/write behavior changes, add proposed **`ram-write-control`**
   through maintained typed plans and scenarios (suggested new
   `RamWriteControlPlan.cs` / `RamWriteControlScenarios.cs` in the existing
   Verification directory), reusing owned fixture/cleanup infrastructure:

   - Direct and Standard access; 1 MiB sequential Q1T1/Q8T1 and 4 KiB random
     Q1T1/Q32T1; control/candidate; three alternating repetitions: **48 write
     windows**. Freeze the 2 GiB RAM/1 GiB file, W3/ten-second score, Normal launch
     and precomputed `-Z1M` payload. This deliberately differs from the separate
     disk-cache matrix's `-Zr` contract.
   - Use a write-specific evidence validator. Do not reuse the RAM read payload
     guard or reject legitimate provider write-byte increments. Require stable
     native identity, zero backing/image attempts, valid priority/XML/coverage,
     and accounting scoped to the enclosing interval.
   - Run independent deterministic write/read and neighboring-byte checks on
     owned files before/after measurement. They prove their own data; they do not
     prove DiskSpd's randomized overwrite payload. A changed workload-file hash
     is expected for this suite and is not its correctness oracle.
   - Wire suite/worker/manifest/count/restoration contracts and register a focused
     campaign. Do not change `IsRamReadSuite` to classify write cases as read-only.
     If existing maintained scenarios already implement the exact required write
     comparison when this task starts, reuse them and document the mapping instead.
4. If the gain or correctness gate fails, remove the candidate engine, preserve
   necessary ABI reservations and record the rejected design/results. If it passes,
   retain it with its supported regression tests and proceed to G. A performance
   pass with lifetime verification pending is still an experiment, not a shipped
   default. No single-reader 42 GB/s promise is part of this gate.

## 7. Stage D: isolate priority and benchmark affinity

This is a disk-volume RAM-cache experiment, distinct from the dedicated RAM-disk
helper investigation. Existing `priority-cost` remains a valid 36-window record.

1. Add proposed suite **`priority-affinity`**. Extend `CacheExerciseCase` with an
   explicit affinity choice defaulting to legacy behavior. In `CacheExercisePlan`,
   generate three shapes (1 MiB Q1T1, 1 MiB Q8T1, random 4 KiB Q1T1), two CPU classes
   (Normal/BelowNormal), two benchmark affinity modes (default/`-n`) and three
   alternating repetitions: **36 windows**. This is a different factorial from
   the existing priority suite; do not overwrite its IDs or reinterpret its data.
2. Add the suite to `CacheExercisePlan.Contains`, `VerificationPlan` validation
   and the CLI. In `VerificationCampaignPlan`, route it to the **NTFS lab**, as
   `priority-cost` is routed today. Merely adding it to `FocusSuites` would route
   it to the performance volume by default; update target-role selection too.
   Update `VerificationRunner`'s `CacheExerciseTargets` manifest selection and
   `prepare` worker's `Value = 3` specialization so it prepares the same single
   fitting stream as `priority-cost`. Check all existing `priority-cost` branches;
   do not quietly prepare extra competing files through the generic fallback.
3. In `MeasureExercise`, use `ProcessScheduling` for actual owned-child CPU class
   and memory priority 5. Use I/O hint 3 and W3 in all four variants, adding only
   `-n` for unbound cases. In `DiskTargets`/a typed profile validator, check XML
   affinity, depth, threads, block/random mode, read-only ratio, duration, warmup,
   cache mode and I/O priority. Keep application/driver priorities unchanged.
4. Preserve fitting-file warmup, exact bytes, zero lower/staged attempts,
   readiness/coverage, owned exits and independent restoration. Add contracts for
   all 36 unique cases, balanced alternation, unchanged I/O/memory settings, XML
   mismatches and priority-application timing/failure. Update
   `CacheExerciseTests.cs`, `VerificationRunnerTests.cs`, CLI tests and docs.
5. Run the new focused campaign once with 2 GiB budget, three repeats and ten-second
   scores using the command pattern above and `--focus-suite priority-affinity`.
   Compare BelowNormal versus Normal separately for default and unbound affinity.
   If the loss materially shrinks unbound, profile the implicated interaction;
   otherwise retain the lower-priority finding and investigate only a newly
   demonstrated cost. Any follow-up trace is separate from the untraced scores.
6. Document all four cells per shape and restore exact original settings. A result
   that unbound benchmarks are faster does not authorize changing user-process
   affinity or boosting system threads as a product feature.

## 8. Stage E: optional real UI cost at 100 ms

1. Start only when UI cost is the selected task. Use existing native map-cost
   findings as context; 250 ms native polling does not measure 100 ms rendering.
2. Add proposed maintained scenario **`cache-ui-cost`**, reusing the resident
   random 4 KiB Q8 workload from `cache-map-cost`. Compare four states: UI closed;
   visible map at default cadence; visible embedded map at 100 ms; full-screen
   pop-out at 100 ms. Three alternating repetitions give **12 windows per cache
   size**. Start at 2 GiB. Add a 4 GiB run only if the result warrants it; do not
   allocate a 32 GiB fixture on the 16 GiB VM.
   Reuse `cache-map-cost`'s pre-fill/warm preparation explicitly: today it is gated
   on that exact suite name in `MeasureExercise`. Keep workload, occupied map size
   and displayed cell count comparable across the four UI states.
3. Launch a verified Desktop preview as an owned process in the interactive Windows
   session. Add a developer-only automation adapter if necessary, separate from
   ordinary UI flows, using the existing application and test projects. Record
   actual window visibility, target identity, preference/cadence, map generation
   and size. Headless rendering or native polling alone cannot close this stage.
4. Preserve the original user's settings. Prefer an isolated settings instance;
   otherwise journal and restore exact bytes. The phase may permit only its
   explicitly owned Desktop PID; retain quiet-host checks for every other process
   and every normal benchmark phase. Start/stop UI within the phase, so the next
   phase gets a quiet restored host. Refuse absent interactive-session capability.
5. Record workload throughput, driver-map request duration/frequency, Desktop CPU
   and memory/allocation activity, dispatcher responsiveness and late/dropped
   updates. Use a shared time base and label enclosing intervals accurately. Keep
   map demand released on minimize/close and prevent overlapping per-volume samples.
6. Only after observing material cost, change the responsible sampling/rendering
   code. Preserve the user-selected fast option and correct legends/grouping.
   Verify page switching, pop-out close/minimize/F11/Escape, unrelated inventory
   changes, target removal and late results in desktop tests plus the real session.
7. Update findings even if no optimization is justified. Regenerate README app
   images only for a relevant visible UI change, using the existing desktop test
   command in AGENTS; do not edit screenshot pixels.

## 9. Stage F: conditional selective caller backoff

1. Begin only after choosing this lower-priority hypothesis explicitly. Freeze a
   default-off candidate: small eligible ordinary requests up to 64 KiB use a
   shorter window; larger requests retain 256. Use 0 as the initial lab small
   window to test the existing measured endpoint, not as a new production default.
   The candidate is size-based on both filesystems; filesystem-specific behavior
   would be a separate experiment.
2. Separate the current constant's production-default role from the maximum lab
   range. Preserve all control, pending-queue, active worker/owner, offloaded-copy,
   paging, stack and IRQL eligibility gates in `driver.cpp`.
3. Do not simply replace `backoff` with a size-dependent value: current routing
   clamps the shared `WorkerWindow`, so small traffic could erase protection needed
   by subsequent large reads. Specify separate state or an explicit conservative
   class-transition rule before coding. Record selected class/window and first
   decline reason with compatible diagnostics; capture/restore the lab mode.
4. Add proposed **`caller-policy`** to the maintained cache exercise machinery and
   route it to NTFS plus optional ReFS lab, including the `--lab-refs` validation
   allowlist. Retain `caller-backoff` and its completed evidence unchanged.
   Update the campaign ReFS phase-addition condition, evidence preflight requiring
   capture/restoration of the new lab setting, and `VerificationRunner`'s fitting
   target manifest/preparation specialization. Older drivers must fail capability
   preflight, rather than run a candidate they cannot activate or restore.
5. Compare six cases per filesystem: mixed 64 KiB Q1; mixed 64 KiB Q8; random 4 KiB
   read Q1; sequential 1 MiB read Q8; small-mixed-to-large-read transition; reverse
   transition. Two modes and three alternating repetitions give **36 cases per
   filesystem**. Each transition retains both phase measurements under unique
   sub-IDs; reset state only between cases, not between the two transition phases.
   Declare those inner measurements separately from case counts in the manifest.
6. Use fitting files, independent concurrent/post-drain byte oracles and identical
   settings within each filesystem. Run a focused campaign with both validated lab
   roles. Check actual routing counters, not just throughput. Qualify flush/order,
   pressure and transitions before promotion. Retain the current default if the
   mixed gain is marginal or large/transition behavior regresses.

## 10. Stage G: final qualification and handback

1. Inspect the final diff and map each retained native/UI/runner change to its
   affected checks. Removed experiments do not need shipping lifecycle acceptance;
   retained new paths do. Keep tests for protocol compatibility/reserved fields.
2. Run the smallest relevant campaign while developing. Add accepted new regression
   scenarios to the appropriate final profile, updating frozen counts/tests/docs.
   Do not silently add every diagnostic to broad profiles. Full/release profiles
   and optional ReFS orchestration were not all rerun during plan-114 qualification;
   report actual coverage rather than inferring it from the smoke.
3. For a release/native change whose scope requires the complete write/sustained
   qualification, the existing command is:

   ```powershell
   & $qcCli developer verify Q: --campaign release-performance --lab-ntfs W: --lab-refs R: --budget-mib 2048 --repeats 3 --duration-seconds 10 --diskspd $qcDiskSpd --pause-backing-cache --output $qcJobRoot
   ```

   Supply R: only when the validated ReFS lab is attached. Without ReFS, omit
   `--lab-refs R:` and record that gap. Release includes the 72-case write suite
   and 1800-second sustained workload by default. An isolated write regression
   can instead use the existing focused campaign with `--focus-suite write-performance`.
   Keep the same DiskSpd hash and distinguish randomized payload from `-Z1M` controls.
4. Inspect the exact final evidence and restoration using section 3. Update
   PERFORMANCE_FINDINGS.md with units, raw identities, comparison conditions,
   per-repeat results/ranges, conclusions and limits, including rejected ideas.
   Update tracker implementation and verification separately. Add exact case IDs
   for any unexercised lifetime/ordering branch; do not call it covered by a general
   policy or pressure pass.
5. Update README benchmark numbers only for an accepted relevant change, following
   [BENCHMARKING.md](BENCHMARKING.md): actual CDM GUI, Normal priority, same contract,
   cleared clean cache before each complete run. A runner XML table does not
   replace the requested GUI screenshot. Follow AGENTS for generated app images.
6. Commit/push scoped source, tests and public documentation to the active work
   branch; preserve private raw output and credentials outside git. Update PR scope
   and state merge readiness. Do not merge until the session authorizes that PR.
7. Hand back a compact table with: implemented change; measured benefit/regression;
   correctness coverage; raw run ID; restoration outcome; remaining uncertainty;
   and next action. State explicitly whether automatic wake-up and a new native
   speedup were each delivered. Include actual test time separately from coding,
   CI, analysis and waiting.

## 11. Checkpoints and stop conditions

| Checkpoint | Small reviewable deliverable | Stop/defer condition |
|---|---|---|
| A | Controller integration, tests, one smoke notification | No supported Manager API: document dependency, continue B |
| B1/B2 | Maintained three-shape diagnostic, trace ownership/evidence, cause report | No reliable attribution: add only missing B3 diagnostics or record inconclusive |
| B3, if needed | Default-off compatible counters and overhead evidence | Perturbed/ambiguous data: no candidate acceptance from those scores |
| C | One candidate, matched 48-window A/B, affected lifetime/write qualification | Gain not practical, control regression or correctness failure: remove candidate |
| D | New 36-window priority/affinity comparison and conclusion | Negative result closes this hypothesis; no automatic priority boost |
| E, optional | Real visible-UI measurements and any justified fix | No interactive session or unmeasured rendering: remain unverified |
| F, conditional | Selective-policy A/B on both filesystems with transitions | Large-read/transition regression: retain backoff 256 |
| G | Final relevant campaign, restored lab, honest PR/report | Missing native identity, incomplete evidence or failed restoration blocks acceptance |

Budget approximately 20–40 minutes of VM time for a new focused comparison after
the runner is ready; freeze the actual case count first. The larger candidate
matrix, trace processing and lifecycle checks may take longer. Engineering/CI
duration is not established. The historical write suite alone took 54 min 05 s;
a release profile also includes at least 30 minutes of sustained activity and
other phases. Avoid rerunning those broad tests for notification or documentation.

Ready-to-use delegation prompt:

> Implement the next ready stages in `docs/PERFORMANCE_AGENT_HANDOFF.md` on the
> active QueueCache performance branch. Read AGENTS and the findings first.
> Start with Manager completion integration where its supported API is available
> and the maintained RAM attribution diagnostic. Do not choose a native speedup
> until its evidence gate is met. Preserve existing contracts and private raw
> evidence, use one foreground campaign per stage, and update findings/tracker
> after every decision. Report actual gains, rejected ideas and verification gaps;
> keep optional stages and external dependencies explicit.
