# Developer tools

One installer includes the desktop, driver and `qcache`. Integration scenarios are
compiled into `QueueCache.Developer.dll` beside the CLI, not separate executable
applications or duplicate .NET runtimes. The desktop can reuse this library later.
The `tests/` projects run on the build host/CI and are not installed.

| Command | Purpose / effects |
|---|---|
| `qcache test Q:` | Ordinary current-boot file checks. New files retained; no raw writes, faults or reboot. |
| `qcache benchmark Q:` | Ordinary sequential file benchmark. |
| `qcache developer performance Q: [--timing true/false]` | Queue/phase/counter JSON; optional detailed lock and drain timing. No workload is started. |
| `qcache developer test <disk> <exact-bytes> <PnP-instance>` | Read-only pass-through smoke test; use an idle disk. `--detached` expects no filter. |
| `qcache developer file-tests Q <disk> <exact-bytes> <PnP-instance> <mode>` | Advanced NTFS integrity, concurrency, coalescing and flush-policy scenarios. |
| `qcache developer write-tests <disk> <exact-bytes> <PnP-instance> <mode>` | Raw-disk integration tests, including destructive writes in a fixed 64 MiB region at 1 GiB. Requires an empty RAW non-OS disk. |
| `qcache developer driver inspect <PnP-instance>` | Per-device registration diagnostics (not a class-registration report). |
| `qcache developer driver delay <device> <milliseconds>` | Synthetic lower-write delay; 0 clears. |
| `qcache developer driver fault <device> <value>` | Synthetic fault selector; 0 clears. |

Each command has `--help`, including supported mode names. Mode names are positional,
without the historical `--` prefix. Raw prefix scenarios use `--prefix-bytes`; seeded
patterns use `--seed`. File sizes use `--size-mib`; post-reboot verification uses
`verify-files --run-id <GUID-in-N-format>`.

Advanced tests preserve the existing exact-size, PnP identity and non-OS target
checks. They are not run by the ordinary `test` command or by setup. Raw writes
destroy data in their test region. Policy/fault scenarios can change cache state;
dirty-reboot modes intentionally leave dirty data and a delay active, but never
reboot automatically. A failure is not permission to discard dirty data: inspect
state, clear the synthetic hook, retry and drain explicitly. These tests do not
prove crash durability or complete kernel coverage.

## Repository-only scripts and recovery

`scripts/Measure-Performance.ps1` runs the repeatable performance experiments: small-request
cost, foreground/background interference, capacity pressure, controlled slow storage,
random-drain efficiency, drain parallelism and flush-under-load. It interleaves configurations,
repeats them, reports medians with spread, and restores the original policy and clears the lab
delay hook even after a failure. It refuses disk 0 and boot/system volumes and only touches its
own files under `<volume>\QueueCache-Perf`. See [performance baseline and method](../docs/PERFORMANCE.md)
and the [performance and concurrency plan](../docs/PERFORMANCE_PLAN.md).

`scripts/Test-CacheFaults.ps1` retains the historical multi-scenario fault sequence
and now invokes `qcache developer write-tests`, not another executable. It requires
the recorded identity from the earlier per-device test setup. `Test-LabReads.ps1`
is a compatibility wrapper for that same setup. New installations should invoke
the CLI directly with the current identity.

`scripts/Manage-Lab.ps1` is historical per-device bring-up/recovery tooling, **not
the product installer**. Do not use its Install/Reattach/Upgrade actions on a
class-filter installation. Current installation/removal belongs to the single
installer and `packaging/Install-Driver.ps1`; registration backups and logs are
kept under ProgramData/QueueCache. Offline recovery cannot depend on a running
CLI: retain the VM snapshot and registration backup outside the guest before
driver/boot experiments. See the root README's recovery notes.

No `lab`, `tests`, `write-tests` or `file-tests` folder is shipped. During upgrades,
setup removes the old applications/runtime files and known scripts, removing the
directories only if empty; saved logs and test data are preserved. Those old
application directories are product-owned: their EXE/DLL/PDB payload is removed.
