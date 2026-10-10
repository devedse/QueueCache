# Single-command performance verification and completion delivery

Status: implementation plan, not an available command. The existing supported
command is `qcache developer verify`; `full` does not include every specialized
performance suite or the separate 72-case write matrix. Findings and priorities
are in [PERFORMANCE_FINDINGS.md](PERFORMANCE_FINDINGS.md).

## Intended operator experience

Run one foreground command with explicit targets and an immutable campaign plan.
For example, a **proposed** interface is:

```powershell
qcache developer verify Q: --campaign performance --lab-ntfs W: --lab-refs R: --diskspd C:\Tools\DiskSpd\diskspd.exe --output C:\QueueCache-Results
```

The final argument names remain an implementation decision. Selecting a campaign
must be mutually exclusive with selecting a single suite. No `--detach`, new
benchmark executable, installer or private PowerShell scenario loop is needed.
The CLI binds arguments; typed orchestration owns phases, cases and evidence.
Existing single-suite commands remain available for focused regressions.

Launch the foreground process through the controlling application's job
mechanism. Keep its process/session identity and stream its normal progress/log.
The controller receives process completion once and then reads the exact campaign
report. The agent need not wake every five minutes to ask whether a file exists.
Long runs retain human-readable progress without requiring an agent turn per
progress line.

## Typed profiles and safe targets

| Profile | Phases | Scope |
|---|---|---|
| Focused regression | A named affected suite plus its required correctness checks | Small default for an isolated change; preserve existing measurement contracts. |
| Performance | Retained-driver correctness checks; resident read/copy/layout baseline; RAM-disk reference; NTFS and optional ReFS caller comparison; independent priority comparison; sustained mixed/read-recall recovery; native map cost | Explicit workloads and budgets per phase. Exclude historical retired queue experiments, invasive system/disk-removal suites and unrequested broad writes. |
| Release performance | Performance profile plus explicitly selected 72-case `write-performance` and a 30-minute sustained phase | Full validation cost is visible before launch. CrystalDiskMark GUI/README screenshots remain a separate explicit operator task with their documented GUI contract. |

Every phase declares a target role, filesystem, budget, repetitions, score and
warmup durations, expected cases, failure policy, driver feature requirements and
restoration deadline. Resolve roles to exact volume IDs and owned lab registrations
before running. Refuse OS volumes or wrong targets for lab-only scenarios. Do not
create/format a disk silently. ReFS caller tests require the supported owned lab;
fault injection stays on the NTFS lab. Cache pausing on a lab's backing volume must
be explicit, captured and restored, with saved profiles unchanged.

Freeze the resolved plan and options in a campaign manifest before the first
measurement. Changes to workload/measurement contracts increment the verification
plan version. A campaign has one immutable namespace; each phase retains its own
normal run artifacts and independently unique case IDs. A summary indexes those
exact children rather than searching historical folders. Phase counts and results
must agree with the frozen manifest before overall completion.

## Runner changes

1. Add typed campaign/profile/phase records beside `VerificationPlan`,
   `CacheExercisePlan` and `RamReadReferencePlan`. Reuse their scenario factories;
   do not copy scenario logic into CLI handlers or shell scripts.
2. Add a campaign coordinator around the existing `VerificationRunner` execution
   seam. Keep one ownership boundary, no concurrent benchmark phases, and retain
   each phase's provenance, expected-case list, progress, worker journal,
   readiness/coverage and raw results. Prevent unrelated test/UI work at preflight.
3. Capture original runtime state once and independently restore after each phase.
   Validate restoration before advancing. Faulted restoration stops the campaign;
   transient draining and a driver fault remain different states. Run injected
   fault scenarios last if selected. Never reboot/format or kill unowned processes
   as recovery. Preserve `verify-recover` identity/live-process refusal behavior.
4. Write atomic campaign status after each case/phase. Include time spent scoring,
   preparing, draining, restoring and waiting for owned children so future status
   reports explain elapsed time. Report exact counts; do not invent percent
   estimates for unknown-duration operations.
5. Produce campaign `SUMMARY.md`, machine-readable results, failed-phase location
   and restoration outcome. Performance verdicts use predetermined comparisons
   and controls, separate from collection status. An incomplete phase cannot pass
   by combining another run's repetitions or treating missing counters as zero.
6. Finalize reports and owned-process cleanup/restoration, then atomically write
   the completion marker and a versioned completion event. A failed measurement
   still produces a terminal event with failure and restoration outcomes;
   interrupted runs with no marker remain interrupted/running.

No change to strict zero-lower-I/O controls, two-second maximum telemetry gaps,
native identity validation, priority readbacks or per-operation timeouts is part
of this plan. There is no overall deadline by default; restoration retains its
own deadline.

## Completion-driven agent notification

The runner can guarantee a durable local event containing campaign/run ID,
manifest digest, exact output path, terminal status, expected/completed counts,
failure and restoration outcome. It should not hold manager credentials or send
arbitrary external messages. A controller-owned job watcher can wait on the
foreground process exit and consume that event; reconnecting controllers can
recover unsent events from a durable outbox. Deduplicate by campaign ID and
terminal-event ID. Deliver only after terminal evidence is committed.

The current exposed `dam-tools` command only supports sharing images; no
completion notification or agent wake API is available here. Automatic awakening
therefore needs an explicit DeveAgentManager job/completion integration, or an
existing controller process-completion feature verified by its owner. Do not
claim this integration already exists. Until it does, stream/await one owned
foreground process and inspect its completion once; use manual status only for
operator requests or diagnosing stalls. A watcher should be event-driven, not an
agent repeatedly opening SSH sessions.

Distinguish run completion from notification delivery. Delivery failure does not
change a measurement verdict or rerun benchmarks. Record delivery separately and
retry only the controller event with bounded backoff/deduplication. Never include
credentials, private VM connection data or raw secrets in the event.

## Verification and rollout

| Step | Required checks | Deliverable |
|---|---|---|
| Typed campaign and coordinator | Host contracts for deterministic counts/IDs, suite and target selection, sequential phases, forwarded options, stop-on-failure, no next phase after restoration failure, exact child evidence indexing | One supported command with honest campaign status and summaries. |
| Durable terminal event | Host contracts for finalize-before-event ordering, failure events, interrupted runs, duplicate delivery and reconnect recovery; preserve synchronous console errors/progress | Process completion can be consumed once without polling by the agent. |
| Manager integration | Verify a supported job callback/wake contract; controller owns authentication and persistent mapping to the conversation/job | Agent receives one terminal result and resumes analysis. This dependency is external to the repo runner. |
| VM smoke | Two short maintained phases on owned NTFS lab; actual native identity, immutable evidence, process ownership and exact restoration | Validate orchestration, not a broad performance acceptance matrix. |
| First full campaign | Freeze profile/options, run once on the normal-priority quiet VM, inspect every phase and final restoration | Establish campaign reference and document elapsed phase times and result-driven next steps. |

Implement coordinator/event work first, then integrate the manager, then run the
full selected campaign. Do not spend another broad matrix merely to validate
reporting or notification plumbing. Estimated engineering work: roughly one to
two working days for the maintained coordinator/events/contracts, plus manager
integration time determined by its available API; the first release profile's
benchmark time includes about 54 minutes for the current write matrix and at
least 30 minutes of sustained mixed I/O, before other phases/preparation/drains.
