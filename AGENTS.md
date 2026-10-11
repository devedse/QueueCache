# QueueCache agent instructions

## Documents

- `docs/DEVELOPMENT.md`: architecture, CLI, build and the documentation map.
- `docs/TESTING.md`: how to test (host tests, the VM, one-command campaigns,
  suites, reading results, judging a performance change, the README benchmark).
  Follow it for every verification run.
- `docs/PERFORMANCE_FINDINGS.md`: read it before any performance investigation and
  update it when a result changes a decision. Do not rerun an established
  experiment merely because its finding was forgotten (its decisions table says
  when to reopen); repeat relevant regressions when code, environment or
  measurement contracts change.
- `docs/KNOWN_ISSUES.md`: current limitations and verification gaps; it ships with
  the installer. Update it when you fix or find an issue.
- `docs/history/`: dated reports and the former tracker, not maintained. Add a
  report there only when it records evidence worth keeping; delete completed plans
  and handovers instead (Git keeps them). Update existing documents rather than
  adding new dated plan files.

## Cache contract (RAM-first)

In explicit volatile Fast mode, fitting supported writes must not depend on lower
I/O merely for admission. Preserve Strict and explicit durability, capacity
backpressure and lifecycle/error semantics. No incidental whole-cache drains for
partial writes or harmless observations. Document exceptions and prove the contract
with lower-I/O-attempt counters.

## Verification rules

- `qcache developer verify` is the only runner. Maintain it when changing driver
  behavior, policies, management APIs or performance diagnostics. Scenarios live in
  `src/QueueCache.Developer/Verification`; contract tests in
  `tests/QueueCache.Management.Tests` (`VerificationRunnerTests.cs`, and
  `DiagnosticContracts.cs` for checks that must also run on Linux). Do not create
  private orchestration scripts for a supported scenario, another test executable
  or installer, or a `--detach` mode. Keep orchestration, typed scenarios, process
  ownership, parsing and reporting separate; the CLI only binds arguments; worker
  mode uses the same `qcache` binary.
- Contracts: bump `VerificationPlan.Version` and the docs when a workload or
  measurement contract changes; every repetition has a unique immutable case ID.
  Never turn missing fields into zero, infer the loaded driver from a version string
  (check `qcache developer driver loaded` hashes), declare an incomplete matrix
  passed, bypass the ready handshake or telemetry coverage, or loosen DiskSpd XML
  parsing to swallow errors (CrystalDiskMark's recognized two-line trailer is the
  only accepted extra).
- Before a run, use the checklist in `docs/TESTING.md` section 2: elevated
  terminal, intended driver loaded, Driver Verifier off for performance, Everything
  (and its service) and Steam closed and restored afterwards, no competing
  workloads, a clean non-OS disk, the lab VHDX for `volumes`/`trim-cache`/
  `write-tests`, no armed delay/fault hooks, strict registration check.
- Scope: compare a concrete change with the smallest suite or
  `--campaign experiment` on the same build; `focused` adds 81 retained correctness
  checks; run `release-performance` once on the final build. Do not run a broad
  matrix when only a focused regression was asked for. Keep the same DiskSpd
  binary when comparing; never assemble a result from unrelated run folders.
- Results: wait for `FINISHED.txt`, then read `status.json`, `SUMMARY.md`,
  `results.json`, `run.log` and the raw evidence of that exact run. `MEASURED` is
  not a performance verdict; a missing marker means interrupted or running;
  zero-completion samples have N/A latency; never combine incomplete repetitions.
  Keep immediate failure reporting and synchronous CLI progress covered by
  host-safe tests (no fire-and-forget `Progress<T>` in the console frontend).
- Restoration has its own deadline; runs have no overall deadline by default.
  If restoration fails, stop and inspect the recorded PIDs, control traces and
  recovery snapshot. Recovery may wait for draining but never retries faults as if
  they were drains. Do not kill arbitrary processes, format, reboot or change
  driver defaults as automatic recovery; use `qcache developer verify-recover
  <run>` after confirming owned processes stopped.
- Host tests: `dotnet run --project tests/QueueCache.Management.Tests -c Release`
  (and `tests/QueueCache.Desktop.Tests` for frontend changes). Local success is
  not VM driver verification; record gaps honestly, including unexercised paths.
- Long runs from the agent runner: launch with `dam-tools run-job` (it owns the
  run and validates its completion), then start a background command that waits
  for that job's record to finish; Claude Code wakes the agent when it exits. The
  Manager's own wake message does not reach Claude Code yet (DeveAgentManager
  issue #5), so never rely on it.

## README benchmarks and screenshots

- CrystalDiskMark numbers and screenshots: `docs/TESTING.md` section 7 (normal
  priority, `qcache policy drop-clean` before each run, last-access updates off,
  background indexers closed, Verifier off).
- App screenshots are generated, never edited: `dotnet run --project
  tests/QueueCache.Desktop.Tests -c Release -- --readme docs/images`.

## Repository hygiene

- For failures reported in this ongoing verification work, the user authorizes
  scoped fixes, proportionate tests, and commits/pushes to their master branch
  without another confirmation. This does not authorize destructive VM recovery
  or unrelated changes; report blockers and preserve evidence.
- GitHub Actions owns build-version increments. Do not manually bump versions.
- Personal research notes, credentials, VM keys and one-off evidence stay private.
  Preserve unrelated uncommitted research documents. `.lab/`, downloaded tools,
  build outputs and `QueueCache-Verify-*` run directories must not be committed.
- Commit only relevant source/tests/public documentation. Follow the user's
  requested remote/branch; do not introduce PRs when direct master pushes were asked.
