# QueueCache

QueueCache is an experimental Windows RAM cache for development on disposable
test systems. The current repository has one driver implementation, one managed
CLI/desktop stack and one installer. It is test-signed and is not suitable for
production or valuable data.

> Fast mode can acknowledge writes and application flushes while data exists only
> in volatile RAM. A crash, power loss, device failure or driver defect can lose
> data or corrupt a filesystem. Development testing uses recoverable VMs.
> C: is available through normal cache configuration; its focused application
> and lifecycle checks follow the implementation plan. Synthetic delay/fault
> experiments use the separate disposable non-OS disk.

Current execution status lives in the
[private-alpha handover](docs/PRIVATE_ALPHA_IMPLEMENTATION_HANDOVER.md) and
[RAM-first tracker](docs/RAM_FIRST_IMPLEMENTATION_TRACKER.md). Confirmed gaps are
listed in [known issues](docs/KNOWN_ISSUES.md).
Remaining work is ordered in the [next phase plan](docs/NEXT_PHASE_PLAN.md).

## Product model

The filter is registered with the disk class but starts inactive. Installation
does not allocate RAM or enable caching. A new task is explicitly created for a
volume; only a saved, identity-matched profile is restored at startup.

New tasks default to:

- Fast write behavior, requiring explicit volatility acknowledgement in the CLI;
- Automatic RAM allocation with retained writes and read promotion;
- Idle background draining with a 5,000 ms first-dirty trigger, 250 ms idle
  trigger, 40/80 watermarks, 256 KiB batches and parallelism 1.

Strict remains available. Existing profiles retain their saved Fast/Strict and
drain settings. Background age starts scheduling and is not a durability deadline.
Explicit QueueCache flush, Pause, Remove and lifecycle boundaries still drain.
Capacity exhaustion still applies backpressure.

See [cache policies](docs/CACHE_POLICIES.md) for the complete contract.

## Operator commands

Run these from an elevated terminal on the test machine:

```powershell
qcache disk list
qcache policy apply Q: --budget-mib 4096 --accept-volatile-flush --save
qcache policy apply Q: --budget-mib 256 --preset Strict --save
qcache policy status Q: --json
qcache policy watch Q:
qcache policy flush Q:
qcache policy pause Q:
qcache policy resume Q:
qcache policy remove Q:
```

`--save` writes an administrator-only machine profile. Applying without `--save`
removes the saved profile unless `--runtime-only` is used. Pause drains while
preserving the task; Remove drains, releases RAM and removes its saved profile.
Files are never deleted by task removal.

The desktop uses the same management library. Selecting Fast in its task editor is
the explicit volatility acknowledgement. Closing the desktop does not stop the
driver or change a task.

## Verification

The supported current-boot runner is `qcache developer verify`. Use an elevated
terminal, stop competing storage workloads and select a clean non-OS physical
disk. Do not substitute private scripts for maintained scenarios.

```powershell
qcache developer verify Q: --suite quick --output C:\QueueCache-Results
qcache developer verify Q: --suite policies --output C:\QueueCache-Results
qcache developer verify Q: --suite full --diskspd C:\Tools\DiskSpd\diskspd.exe --output C:\QueueCache-Results
qcache developer verify-status C:\QueueCache-Results\QueueCache-Verify-<run-id>
```

Wait for `FINISHED.txt`, then inspect `status.json`, `SUMMARY.md`, `results.json`,
`run.log`, interval telemetry and raw case evidence. A missing completion marker is
not success. If restoration fails, stop and inspect the recorded identity, owned
PIDs, control trace and recovery snapshot before using the guarded
`verify-recover` command.

The separate 72-case small-write matrix is:

```powershell
qcache developer verify Q: --suite write-performance --budget-mib 2048 --diskspd C:\Tools\DiskSpd\diskspd.exe --output C:\QueueCache-Results
```

Microsoft DiskSpd and CrystalDiskMark's bundled `DiskSpd64.exe` are supported in
XML mode. Keep the same executable and hash for comparisons. Full suite scope and
safety rules are in [developer verification](docs/DEVELOPER_VERIFICATION.md).

## Build

Prerequisites are Windows x64, PowerShell 7, the .NET SDK selected by
`global.json`, Visual Studio 2026 Desktop development with C++, and the Windows
Driver Kit component. The driver project and build script have one current native
path; `qcachelab.sys` remains the binary/service name only for installed-upgrade
compatibility.

```powershell
./build/Build.ps1 -Configuration Release -Version 0.1.0.0
./build/Build.ps1 -ManagedOnly
dotnet build QueueCache.Managed.slnx -c Release
dotnet run --project tests/QueueCache.Management.Tests -c Release
dotnet run --project tests/QueueCache.Desktop.Tests -c Release
```

The full build runs host-safe tests, publishes the CLI and desktop, builds the
current native driver, checks package layout and creates a fresh unsigned package.
It never installs, registers, formats or enables anything. GitHub Actions supplies
release version numbers; do not bump them manually.

`build/Sign-Lab.ps1` test-signs an unsigned Release package and
`build/Build-Installer.ps1` creates the installer. Their historical names and the
`labWriteCache` manifest key are compatibility surfaces, not separate driver
editions. The private signing key stays outside packages. Production signing and
trust are out of scope.

The installer stages an immutable version/hash-named driver and retains the
`qcachelab` service identity across upgrades. A reboot loads a changed driver.
Setup does not reboot automatically or silently configure a cache. Recovery and
registration backups live under `%ProgramData%\QueueCache`; keep an external VM
snapshot plus a copy of the selected backup and
`setup\Recover-Registration.ps1` before boot/lifecycle testing. The recovery script
does not require the driver or CLI and can target an offline SYSTEM hive; it never
reboots automatically.

## Repository layout

| Path | Purpose |
|---|---|
| `driver/qcache` | Current native dispatch, cache, policy and ABI code |
| `src/QueueCache.Management` | Versioned protocol and device access |
| `src/QueueCache.Operations` | Configuration, persistence and file workloads |
| `src/QueueCache.Cli` | Operator and developer command binding |
| `src/QueueCache.Desktop` | Avalonia task UI and telemetry |
| `src/QueueCache.Developer` | Maintained verification workers and scenarios |
| `tests` | Host-safe protocol, orchestration and UI contracts |
| `build`, `packaging` | Reproducible packaging, signing and installer workflow |
| `developer` | Distinct repository-only setup/recovery helpers |

Open `QueueCache.Managed.slnx` for managed development or `QueueCache.sln` for the
current native x64 driver. Protocol changes require matching native/C# size,
version and bounds checks. A successful build or driver load is not a storage
correctness result.

## Licensing

Current source is distributed under the [MIT License](LICENSE). The repository
history contains removed Microsoft-sample-derived files that were distributed
under MS-LPL in those revisions; see [third-party notices](THIRD_PARTY_NOTICES.md)
and the [licensing review](docs/LICENSING_REVIEW.md).
