# Developer tools

One installer includes the desktop, driver and `qcache`. Integration scenarios are
compiled into `QueueCache.Developer.dll` beside the CLI, not separate executable
applications or duplicate .NET runtimes. The desktop can reuse this library later.
The `tests/` projects run on the build host/CI and are not installed.

For repeatable batches, start with `qcache developer verify Q: --suite quick`.
Use `--suite full --diskspd C:\Tools\DiskSpd\diskspd.exe` for the longer matrix.
Output defaults to a unique subfolder of the current directory (`--output` overrides
the parent). See [verification suites and recovery](../docs/DEVELOPER_VERIFICATION.md)
before running; use the shared runner instead of copying private harness scripts.

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

## Retired-script replacement map

The old PowerShell workload wrappers were removed after their modes were compared
with the maintained executable. Historical results remain in Git history and the
secondary documentation; do not recreate those wrappers as another runner.

| Retired source/mode | Maintained replacement or disposition |
|---|---|
| `Measure-Performance`: small-request reads/writes | `verify --suite performance` scaling cells plus the separate `write-performance` matrix |
| interference and controlled slow storage | `verify --suite performance`, with hot-reader alone/loaded, delay 0/25 ms, QD8/32/128 and Automatic/Fixed allocation |
| capacity pressure | Performance writer cells record capacity waits; plan-11 `policies` separately proves fitting traffic has zero waits. A dedicated forced-exhaustion acceptance case remains deferred in the tracker. |
| random drain | Random-write performance cells plus recorded explicit preparation/restoration drains |
| drain-parallelism throughput comparison | Deferred until residual evidence justifies multi-worker tuning. Correctness at parallelism 1/2/4 remains in `policies`. |
| flush under load | `verify --suite flush-interference` |
| `Test-LabReads` attached/detached | `qcache developer test <disk> <bytes> <instance> [--detached]` |
| `Test-CacheFaults` lower completion error/retry | `file-tests ... test-coalescing` uses fault 4, retains dirty data, retries and verifies an independent file hash |
| `Test-CacheFaults` lower flush error/retry | `file-tests ... test-flush-policy` uses fault 3, requires visible failure, retries and verifies the file hash |
| raw pre-submission/short-completion faults 1/2/5 | Deferred raw-only variants. They are not needed to claim the A06 minimum fault paths and remain an explicit gap. |
| allocation faults 6/7 | Deferred until allocation-path work is touched; the gap remains in the tracker. |
| `Manage-Lab` install/reattach/upgrade/uninstall | The single installer and `packaging/Install-Driver.ps1`; no per-device legacy installation path remains. |

Registration backups and logs are stored under `%ProgramData%\QueueCache` before
setup changes registration. `packaging/Recover-Registration.ps1` restores an exact
version-1 backup without the driver or CLI, either in Safe Mode or against an
offline SYSTEM hive. Copy that script and the selected backup outside the guest
before boot experiments. It requires `-ConfirmRestore`, supports `-WhatIf`, refuses
to rewrite registration under a running QueueCache driver, and never reboots.

No `lab`, `tests`, `write-tests` or `file-tests` folder is shipped. During upgrades,
setup removes the old applications/runtime files and known scripts, removing the
directories only if empty; saved logs and test data are preserved. Those old
application directories are product-owned: their EXE/DLL/PDB payload is removed.
