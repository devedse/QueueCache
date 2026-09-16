# QueueCache agent instructions

## Keep verification maintainable

- RAM-first design principle and ordered performance/test plan:
  `docs/RAM_FIRST_IMPLEMENTATION_TRACKER.md` is the execution/status source of truth;
  update implementation and verification separately for each item changed.
  `docs/RAM_FIRST_PERFORMANCE_PLAN.md`. In explicit volatile Fast mode, fitting
  supported writes must not depend on lower I/O merely for admission. Preserve
  Strict/explicit durability, capacity backpressure and lifecycle/error semantics.
  No incidental whole-cache drains for partial writes or harmless observations.
  Document exceptions and prove the contract with lower-I/O-attempt counters.

- For small-write regressions use `--suite write-performance --budget-mib 2048`
  with the same DiskSpd binary before/after changes. This separate 72-case suite
  compares fitting 1 GiB files, random Q1/32 and sequential Q1/8, Off/Eager/Idle,
  and detailed timing off/on. It is not included in `full`; keep its contracts and
  documentation up to date. Do not confuse process-wide counters with score windows.

- The supported current-boot runner is `qcache developer verify`. Maintain it when
  changing driver behavior, policies, management APIs or performance diagnostics.
  Add/regress scenarios in `src/QueueCache.Developer/Verification` and contract tests
  in `tests/QueueCache.Management.Tests/VerificationRunnerTests.cs`. Do not create
  another private orchestration script to repeat a supported scenario.
- Keep orchestration, typed scenarios, process ownership, parsing, and reporting
  separate. The CLI only binds arguments. There is no `--detach`; launch the normal
  foreground command externally if necessary. Do not add another test executable
  or installer. Worker mode uses the same qcache binary.
- Update the plan version and documentation when a workload/measurement contract
  changes. Preserve old raw output. Every repetition needs a unique immutable case
  ID. Never silently turn missing fields into zero, infer loaded-driver identity
  from a version string, or declare an incomplete matrix passed.
- Before a run: use an elevated terminal on the test VM, stop competing tests/UI
  workloads, confirm the intended CI driver is loaded, and use a clean non-OS
  physical disk. No existing delay/fault hooks may be armed. The current driver
  cannot report their original settings; the runner restores its delay to zero.
- Examples (paths are on the machine running qcache, not the controlling machine):

  ```powershell
  qcache developer verify Q: --suite quick --output C:\QueueCache-Results
  qcache developer verify Q: --suite flush-interference --repeats 2 --diskspd C:\Tools\DiskSpd\diskspd.exe --output C:\QueueCache-Results
  qcache developer verify Q: --suite full --diskspd C:\Tools\DiskSpd\diskspd.exe --output C:\QueueCache-Results
  qcache developer verify-status C:\QueueCache-Results\QueueCache-Verify-<run-id>
  ```

- Both **Microsoft DiskSpd and CrystalDiskMark's bundled DiskSpd** are supported in
  XML mode (tested Microsoft 2.3 and CDM 9.0.3's DiskSpd 2.2). On x64 select
  Microsoft's `amd64\diskspd.exe` or CDM's `CdmResource\DiskSpd\DiskSpd64.exe`.
  CDM's recognized two-line score trailer may follow valid XML; do not loosen
  parsing to swallow arbitrary errors or accept text-mode score exit codes.
  Keep the same binary/hash when comparing releases. Details and suite scope:
  `docs/DEVELOPER_VERIFICATION.md`. Do not run a broad matrix when only the focused
  regression was requested. Results default to a unique subfolder of the current
  directory; never search unrelated historical folders to assemble a result.
- Wait for `FINISHED.txt`, then read `status.json`, `SUMMARY.md`, `results.json`
  and `run.log` (the timestamped console progress/error log). Keep immediate failure
  reporting and synchronous CLI progress covered by host-safe runner tests; do not
  replace it with fire-and-forget `Progress<T>` callbacks in the console frontend.
  Also read raw evidence in that exact run. `MEASURED` is not a performance acceptance
  verdict. Zero-completion samples have N/A latency. A missing completion marker
  means interrupted/running, not success. Do not combine incomplete repetitions.
- Restoration has a separate deadline. If it fails, stop and inspect the recorded
  evidence. Runs have no overall deadline by default (`--deadline-minutes 0`);
  preserve per-operation timeouts and `Test N of M` console/file progress. Inspect
  the recorded
  telemetry readiness/coverage and `*-interval.json` as well. Never bypass the
  ready handshake or relax missing samples to get a passing result. Recovery may
  wait for transient draining, but must never retry faults as if they were drains.
  Inspect the recorded
  PIDs, control traces and recovery snapshot. Do not kill arbitrary PowerShell
  processes, format, reboot, or change driver defaults as automatic recovery.
  After confirming owned processes stopped, use `qcache developer verify-recover
  <run-directory>`; it refuses an identity mismatch or live owned process.
- Run host-safe contract tests with `dotnet run --project
  tests/QueueCache.Management.Tests -c Release`. For an optional real DiskSpd XML
  parser smoke check, set `QCACHE_TEST_DISKSPD` to either supported executable first:
  this creates/deletes only a unique 16 MiB temp workload, never accesses a driver.
  Run `tests/QueueCache.Desktop.Tests` for frontend changes. Local success is not VM
  driver verification. Record gaps honestly, including unexercised ordering paths.

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
