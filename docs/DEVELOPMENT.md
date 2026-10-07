# Developing QueueCache

How QueueCache works in detail, its command line, the verification runner and
the build. For what QueueCache is and how to use it, see the [README](../README.md).

## Project status

Current execution status lives in the
[private-alpha handover](PRIVATE_ALPHA_IMPLEMENTATION_HANDOVER.md) and
[RAM-first tracker](RAM_FIRST_IMPLEMENTATION_TRACKER.md). Confirmed gaps are
listed in [known issues](KNOWN_ISSUES.md). Remaining work is ordered in the
[next phase plan](NEXT_PHASE_PLAN.md). Development testing uses recoverable VMs;
synthetic delay/fault experiments use a separate disposable non-OS disk.

## Product model

QueueCache caches volumes. Installation registers the filter once as the topmost
volume filter (directly below the file system), which covers every volume after a
restart, including volumes created later. It starts inactive: installation does not
allocate RAM or enable caching. A task is explicitly created for one volume (NTFS, ReFS, FAT32 or exFAT; FAT32 and exFAT
have no journal, so a crash with Fast-mode data in RAM can damage them), and
each volume has its own cache, also when several volumes share a disk. Only a
saved profile whose volume (GUID), disk and size still match is restored at
startup. Commands that disk tools send to the physical disk (health/SMART queries,
SCSI/ATA pass-through) never reach the cache. See [volume filtering](VOLUME_FILTER.md).

New tasks default to:

- Fast write behavior, requiring explicit volatility acknowledgement in the CLI;
- Automatic RAM allocation with retained writes and read promotion;
- Idle background draining with a 5,000 ms first-dirty trigger, 250 ms idle
  trigger, 40/80 watermarks, 256 KiB batches and parallelism 2.

Strict remains available. Existing profiles retain their saved Fast/Strict and
drain settings. Background age starts scheduling and is not a durability deadline.
Explicit QueueCache flush, Pause, Remove and lifecycle boundaries still drain.
Capacity exhaustion still applies backpressure.

See [cache policies](CACHE_POLICIES.md) for the complete contract.

## Managed disks

QueueCache can also create and manage disks of its own, through **New disk** on
the desktop app's Virtual disks page or `qcache disk`:

- `ram`: a temporary RAM disk whose contents are discarded when it stops;
- `cached-vhdx`: a VHDX file behind an ordinary QueueCache cache;
- `image-in-ram`: a VHDX loaded completely into RAM and saved back as verified,
  versioned checkpoints.

They share the cache's RAM budget. Image hosts must not have a Fast cache. See
[managed disks](MANAGED_DISKS.md).

```powershell
qcache disk create --mode ram --size-mib 4096 --letter R
qcache disk list
qcache disk stop <id> --discard
```

## Operator commands

Run these from an elevated terminal on the test machine:

```powershell
qcache volume list
qcache policy apply Q: --budget-mib 4096 --accept-volatile-flush --save
qcache policy apply Q: --budget-mib 256 --preset Strict --save
qcache policy status Q: --json
qcache policy watch Q:
qcache policy flush Q:
qcache policy pause Q:
qcache policy resume Q:
qcache policy remove Q:
```

`qcache volume list` shows each lettered volume with its disk, whether the filter is
loaded, and its cache task; it exits 1 if the filter registration is not the
supported one. `--save` writes an administrator-only machine profile for that volume
(named by the volume GUID, so it follows the volume, not the letter). Applying without `--save`
removes the saved profile unless `--runtime-only` is used. Pause drains while
preserving the task; Remove drains, releases RAM and removes its saved profile.
Files are never deleted by task removal.

The desktop uses the same management library and shows one card per volume,
grouped under its disk. Selecting Fast in its task editor is
the explicit volatility acknowledgement. Closing the desktop does not stop the
driver or change a task.

## Verification

The supported current-boot runner is `qcache developer verify`. Use an elevated
terminal, stop competing storage workloads and select a volume on a clean non-OS
physical disk. Do not substitute private scripts for maintained scenarios. The
volume-filter suites (`volumes`, `trim-cache`) and the raw `write-tests` use the lab
VHDX from `qcache developer lab-disk create` (see developer verification).

```powershell
qcache developer verify Q: --suite quick --output C:\QueueCache-Results
qcache developer verify Q: --suite policies --output C:\QueueCache-Results
qcache developer verify Q: --suite full --diskspd C:\Tools\DiskSpd\diskspd.exe --output C:\QueueCache-Results
qcache developer lab-disk create C:\QueueCache-Lab\VolumeLab.vhdx
qcache developer verify V: --suite volumes --output C:\QueueCache-Results
qcache developer verify V: --suite trim-cache --output C:\QueueCache-Results
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
safety rules are in [developer verification](DEVELOPER_VERIFICATION.md).

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
dotnet run --project tests/QueueCache.Desktop.Tests -c Release -- --readme docs/images
```

The last command regenerates the README's app screenshots; see below.

## README screenshots

**App screenshots** (`overview`, `cache`, `ram-disk`, `cache-settings`,
`new-disk` in `docs/images`) are rendered headlessly from the real views with an
invented example setup (drives, disks, sizes and a minute of activity), defined
in `tests/QueueCache.Desktop.Tests/ReadmeImages.cs`. After a UI change, run:

```powershell
dotnet run --project tests/QueueCache.Desktop.Tests -c Release -- --readme docs/images
```

It works on any OS with the .NET SDK, in dark mode, at 1280×860 (the RAM disk
page 1280×960). Change the example in that file, not by editing images.

**CrystalDiskMark screenshots** (`cdm-cache.png`, `cdm-ram-disk.png`) are real
runs and the README's performance table copies their numbers. To redo them on
the test machine with the installed release:

1. Cache: the drive with a 2 GiB Fast cache (`qcache policy apply Q: --budget-mib 2048 --accept-volatile-flush`).
   RAM disk: `qcache disk create --mode ram --size-mib 4096 --letter T`.
2. CrystalDiskMark 9.0.3 with its defaults: 5 passes, 1 GiB, MB/s.
   **Theme → Dark** and **Theme → Zoom → 150%**.
3. Type a one-line comment in the box at the bottom, e.g.
   `QueueCache RAM cache on Q: (Fast mode, 2 GiB)` or `QueueCache RAM disk T: (4 GiB)`.
4. Close other benchmarks and stop other heavy work, then run **All** twice. Keep the faster run.
5. Capture just the CrystalDiskMark window (Alt+Print Screen, or Snipping Tool in
   window mode) and save it over the image in `docs/images`.
6. For the "without cache" column, pause the cache (`qcache policy pause Q:`),
   run once, then `qcache policy resume Q:`.
7. Update the README's test-machine table and numbers, then remove the RAM disk
   (`qcache disk stop <id> --discard`, `qcache disk remove <id>`).

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
| `src/QueueCache.Desktop` | Avalonia desktop app: AXAML views, view-models, live telemetry ([structure](DESKTOP_UI.md)) |
| `src/QueueCache.Developer` | Maintained verification workers and scenarios |
| `tests` | Host-safe protocol, orchestration and UI contracts |
| `build`, `packaging` | Reproducible packaging, signing and installer workflow |
| `developer` | Distinct repository-only setup/recovery helpers |

Open `QueueCache.Managed.slnx` for managed development or `QueueCache.sln` for the
current native x64 driver. Protocol changes require matching native/C# size,
version and bounds checks. A successful build or driver load is not a storage
correctness result.

## Licensing

Current source is distributed under the [MIT License](../LICENSE). The repository
history contains removed Microsoft-sample-derived files that were distributed
under MS-LPL in those revisions; see [third-party notices](../THIRD_PARTY_NOTICES.md)
and the [licensing review](LICENSING_REVIEW.md).
