# Single-command performance verification and completion delivery

Status: typed campaigns, sequential execution, combined reports and durable
completion events were introduced in plan 114; current contract is plan 117.
Windows/VM qualification is recorded separately in the [tracker](RAM_FIRST_IMPLEMENTATION_TRACKER.md).
Companion Manager job/delivery code is implemented; live deployment/wake verification
remains separate. `full` does not include every specialized
performance suite or the separate write matrix. Findings and priorities are in
[PERFORMANCE_FINDINGS.md](PERFORMANCE_FINDINGS.md).

## Intended operator experience

Run one foreground command with explicit targets and an immutable campaign plan.
For example:

```powershell
qcache developer verify Q: --campaign performance --lab-ntfs W: --lab-refs R: --diskspd C:\Tools\DiskSpd\diskspd.exe --pause-backing-cache --output C:\QueueCache-Results
```

Selecting a campaign is mutually exclusive with selecting a single suite. No `--detach`, new
benchmark executable, installer or private PowerShell scenario loop is needed.
The CLI binds arguments; typed orchestration owns phases, cases and evidence.
Existing single-suite commands remain available for focused regressions.

Current user workflow: use a maintained individual suite and affected correctness
checks while diagnosing or comparing a concrete speedup. The profile named
`focused` also adds 81 retained correctness checks; it is not the minimum unit of
iteration. Reserve the broad `release-performance` campaign for the final
retained candidate, launch it once, and review the combined report after exit.
This changes run selection, not suite contracts/counts or evidence guards. More
priority/affinity diagnosis and a dedicated 100 ms UI study are parked; historical
measurements remain available without rerunning them.

Launch the foreground process through the controlling application's job
mechanism. Keep its process/session identity and stream its normal progress/log.
The controller receives process completion once and then reads the exact campaign
report. The agent need not wake every five minutes to ask whether a file exists.
Long runs retain human-readable progress without requiring an agent turn per
progress line.

```mermaid
flowchart LR
    Command["One foreground command"] --> Phases["Maintained typed suites"]
    Phases --> Evidence["Raw evidence and independent restoration"]
    Evidence --> Event["Durable terminal event"]
    Event --> Watcher["Controller job watcher"]
    Watcher -. "Manager integration required" .-> Agent["Agent resumes analysis"]
```

## Typed profiles and safe targets

| Profile | Phases | Scope |
|---|---|---|
| `smoke` | Two maintained NTFS cases: `quick`, `partial-read-accounting` | Short real-run orchestration check, no DiskSpd needed. |
| `focused` | `--focus-suite` plus partial-read, paging, policy, pressure and final ordering/fault checks | Only the named affected measurement suite; optional ReFS only for caller-backoff. |
| `experiment` | Exactly one `--focus-suite ram-read-coordination` phase | Nine off/on/off RAM diagnostic windows at one repeat, without the 81 retained checks. Candidate suites remain conditional/unimplemented. Explicit NTFS role, preparation, identities and restoration still required. |
| `performance` | Retained correctness; cache layout; RAM reference and scheduling controls; NTFS/optional ReFS caller; priority; recall; 120-second sustained accounting; map cost; ordering/faults last | Thirteen phases / 118 outer cases without ReFS at defaults; fourteen / 142 with ReFS. The short sustained phase is a smoke, not 30-minute acceptance. No retired queue, disk-removal or write matrix. |
| `release-performance` | Performance plus `write-performance`; sustained default 1800 seconds | Fourteen phases / 190 outer cases without ReFS; fifteen / 214 with ReFS. Write suite has 72 cases at three repeats. GUI CrystalDiskMark/README tasks remain separate. |

Campaign budgets default to 2048 MiB; repetitions/duration remain explicit and
are forwarded to maintained factories. `cache-sustained` always has one repetition;
`--soak-seconds` overrides its profile default. Counts describe outer runner cases,
not the separate inner correctness checks or RAM windows. At three repetitions,
RAM reference/scheduling phases contain 36/30 guarded measurement windows.

For an affected RAM-read regression:

```powershell
qcache developer verify Q: --campaign focused --focus-suite ram-read-scheduling --lab-ntfs W: --diskspd C:\Tools\DiskSpd\diskspd.exe --pause-backing-cache --output C:\QueueCache-Results
```

Targets must already be attached and prepared. Lab roles require the supported
developer VHDX layout, matching filesystem/labels, native identity, clean state
and observed loaded-driver filenames/hashes. Campaign preflight rejects running
benchmark/Desktop processes and below-normal launches. If a lab image is on the
active performance volume, `--pause-backing-cache` explicitly permits a runtime
pause and independent restoration. An active different backing cache is refused.
The campaign owns all target disk leases between phases. It never creates or
formats a lab disk automatically.

Performance-target phases use an explicit bounded preparation worker before
their strict clean-cache capture: verify the original configuration and errors,
flush the filesystem volume, drain the cache once, and wait for clean accounting.
Owned control telemetry records this preparation. Fault/configuration mismatch
stops before controls; this does not retry a fault or change benchmark settings.
It handles transient writes after restoring an active backing cache.

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

## Implemented runner behavior

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
4. Write atomic campaign status after case progress and each phase. Child
   `timing.json` records preflight, common preparation, cases, restoration and
   finalization; case time includes its warmup/draining and is not scored time.
   Campaign phase elapsed includes backing maintenance. Unknown evidence counts
   stay null; no invented progress percentages.
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

The runner writes a durable local event containing campaign/run ID,
manifest digest, exact output path, terminal status, expected/completed counts,
failure and restoration outcome. It should not hold manager credentials or send
arbitrary external messages. A controller-owned job watcher can wait on the
foreground process exit and consume that event; reconnecting controllers can
recover unsent events from a durable outbox. Deduplicate by campaign ID and
terminal-event ID. Deliver only after terminal evidence is committed.

After `FINISHED.txt`, finalized status/reports and owned cleanup, `completion.json`
is published last. It also covers failures/cancellation, with restoration outcome.
`completion-delivery.json` starts as `PENDING_CONTROLLER`; this is not a claim
that a notification was delivered. `verify-status` reads campaign progress.

```powershell
qcache developer verify-completion C:\QueueCache-Results\QueueCache-Campaign-<run-id>
```

This read-only command validates the manifest digest, terminal counts, required
reports and marker before returning the event as JSON. The event identity is
stable across reads, so the controller can deduplicate retries. It has no Manager
credentials or arbitrary callback command. For failed runs, inspect the indexed
child/maintenance directory; `verify-recover` still applies to a recorded child
run, not to restarting or recombining a campaign.

The installed Runner helper now exposes `dam-tools run-job`, `job-status`,
`job-completion`, `job-reviewed` and `job-cancel`, alongside image sharing. It
owns the foreground command and completion reader. The companion Manager service
implements guarded notification delivery, but its live deployment and automatic
agent wake remain unverified here. Until that service is qualified, await the
owned controller process and inspect completion once; use manual status only for
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

The coordinator/event implementation is in this PR. Companion Manager delivery
and reconnect handling are implemented separately (see the plan-115 section);
deployment and real wake verification remain outstanding.
Use the short smoke and focused checks to qualify orchestration; a broad matrix
is not needed merely to validate reporting/notification plumbing. A first release profile's
benchmark time includes about 54 minutes for the current write matrix and at
least 30 minutes of sustained mixed I/O, before other phases/preparation/drains.

### Qualification on 2026-10-10

Windows Release host contracts pass for success, failure, cancellation, unsafe
preflight, missing markers, count/driver/baseline mismatch and failed cleanup.
They use owned fake workers and files, without workload disk or driver access.
The real-driver smoke on unchanged signed 0.4.514.1, managed source `51cb288`,
completed 2/2 phases and cases in **59.15 seconds**, run
`QueueCache-Campaign-20261010-122615-ed7dc35c4c30483dbf6375a92b103211`.
Both indexed child reports, raw checks, eight telemetry readiness handshakes
(maximum control-sample gap 0.245152 seconds), 26 zero-exit owned process records,
backing pause/restoration and final target snapshots were inspected; the
read-only completion command accepted the event and immutable manifest digest.
Raw archive SHA-256:
`CD25CEEC2EB93F3A215E90E464E923B78C3FF6FD33FA8E6E3855C57CE7D36F12`.

The earlier attempt
`QueueCache-Campaign-20261010-122501-a82a7be5cfe241f28676db226461b260`
is preserved separately as INCOMPLETE, zero phases started: attaching the lab
left Q: dirty and preflight refused it. Explicit preparation drained the setup
writes before the new run. Neither evidence nor the clean-cache guard was
relaxed. This smoke qualifies orchestration, not the full performance profile.

The first focused attempt
`QueueCache-Campaign-20261010-123104-34e4bdc4c0c54e6e8bf4eacd52949a91`,
managed source `6a7cae5`, completed four correctness phases, then stopped before
any RAM score because transient writes left the active Q: cache dirty
at the next strict capture. The writer/file was not attributed. Restoration
completed. This exposed the need for the
explicit phase preparation above; the failed run is preserved, not recombined
with a retry. Its private raw archive SHA-256 is
`ED3B90EDAA46BE8DA4CFC14B97B7A03424A69D6ECDD99EBC1E6CB52EC9C0A515`.

The preparation fix is qualified by the complete new focused run
`QueueCache-Campaign-20261010-124559-697930c96ddb4e57a97924a301d5a7e3`,
managed preview code matching `4307679`: 6/6 phases, 111 inner checks including
30 RAM windows, 17 min 21 s, exit zero. The strict read-only completion consumer
accepts the event/digest. All 93 owned exits and 22 control readiness handshakes
pass; maximum control gap 0.246594 s. All phase evidence, explicit preparation,
independent restoration and final target identities/configuration were inspected.
The three injected lab errors are accepted only by the final ordering phase;
no later benchmark follows them. The original Q: configuration/profiles restore
exactly, the owned NTFS lab is detached and the installed tray resumes. Full
performance/release profiles and optional ReFS orchestration were not rerun for
this managed change. Native benchmark findings, timing, raw archive hash and
next decisions are consolidated in [PERFORMANCE_FINDINGS.md](PERFORMANCE_FINDINGS.md).
Automatic Manager delivery remains external.

### Plan 115 controller integration and final restoration

The companion DeveAgentManager implementation provides owned foreground jobs and
a durable terminal-delivery outbox (`dam-tools run-job`); integration details:
[background jobs](https://github.com/devedse/DeveAgentManager/blob/master/docs/background-jobs.md).
The controller waits for process exit, then runs the same CLI's read-only
`verify-completion <unique-parent> --output-parent` on the workload machine. The
reader requires one campaign under that explicit parent and validates its existing
immutable terminal contract. Manager delivery is keyed, prompt/conversation guarded
and duplicate-resistant; an ambiguous terminal crash requires inspection rather
than automatic resubmission. Controller outbox checks consume no agent turns.
`job-reviewed` suppresses a delayed notification already analyzed in the active turn.

Real controller smoke covers both a valid restoration-failure event and a completed
retry with exact restoration. The updated Runner helper is deployed; deployment of
the updated Manager service and live automatic wake remain verification gaps. An
event file alone does not close them. The smoke failure found transient
backing-cache metadata after resume.
Final restoration now explicitly uses bounded filesystem/cache preparation before
the existing strict final capture. Faults are not retried; disabled baseline
observations do not drain. Detailed evidence is in PERFORMANCE_FINDINGS.md.
