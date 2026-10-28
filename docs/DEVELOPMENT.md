# Developing QueueCache

How QueueCache works in detail, its command line, the verification runner and
the build. For what QueueCache is and how to use it, see the [README](../README.md).

## Project status and documents

QueueCache is a private-alpha product tested on recoverable VMs: volume caching
(Fast and Strict), RAM disks, disk images in RAM and VHDX files with a RAM cache.
Confirmed gaps are in [known issues](KNOWN_ISSUES.md); performance results and
keep/reject decisions in [performance](PERFORMANCE.md). The desktop app's
structure is [below](#desktop-app).

| Document | Contents |
|---|---|
| [TESTING.md](TESTING.md) | Host tests, the test VM, one-command verification runs, comparing performance, the README benchmark |
| [PERFORMANCE.md](PERFORMANCE.md) | Performance decisions first, then every result behind them: drain speed and TRIM (#15), read misses (#14), RAM disks, cached reads, scheduling |
| [KNOWN_ISSUES.md](KNOWN_ISSUES.md) | Stability and bugs: every known issue with its status, verification gaps, the correctness verification record and fixed issues. Ships with the installer |
| [COMPARISON.md](COMPARISON.md) | QueueCache against other RAM disks and RAM caches: scores, methods, power-cut evidence and how comparisons are run |
| [CACHE_POLICIES.md](CACHE_POLICIES.md) | How the cache works: the volume filter, Fast/Strict, admission, draining. Ships with the installer |
| [MANAGED_DISKS.md](MANAGED_DISKS.md) | RAM disks, images in RAM and cached VHDX files |
| [LICENSING_REVIEW.md](LICENSING_REVIEW.md) | Licensing and provenance |
| [history/](history/README.md) | Dated investigation reports, the former implementation tracker and design records; not maintained |

Product decisions that still hold: Fast mode is the default and the optimized
mode; Strict is supported for correctness but not tuned for speed. C: is a normal
target with the same activation path as other disks. Fast mode's volatility
(loss of acknowledged writes still in RAM) is accepted and must be explained.
With write ordering off (the default), filesystem damage after a crash remains
a risk. Write ordering keeps NTFS healthy instead: it passed power-cut tests on
NTFS data volumes, which closed [#13](https://github.com/devedse/QueueCache/issues/13)
([scope](PERFORMANCE.md#release-206)); it is not available on the Windows volume. Silent corruption, stale reads and wrong
write ordering during normal operation are defects.

## Product model

QueueCache caches volumes. Installation registers the filter once as the topmost
volume filter (directly below the file system), which covers every volume after a
restart, including volumes created later. It starts inactive: installation does not
allocate RAM or enable caching. A task is explicitly created for one volume (NTFS, ReFS, FAT32 or exFAT; FAT32 and exFAT
have no journal, so a crash with Fast-mode data in RAM can damage them), and
each volume has its own cache, also when several volumes share a disk. Only a
saved profile whose volume (GUID), disk and size still match is restored at
startup. Commands that disk tools send to the physical disk (health/SMART queries,
SCSI/ATA pass-through) never reach the cache. See [volume filtering](CACHE_POLICIES.md).

New tasks default to:

- Fast write behavior, requiring explicit volatility acknowledgement in the CLI;
- Automatic RAM allocation with retained writes and read promotion;
- Eager background draining with 2048 KiB batches and parallelism 8 since plan 164,
  held to two outstanding writes while foreground disk reads wait; the 5,000 ms
  first-dirty trigger, 250 ms idle trigger and 40/80 watermarks apply when another
  algorithm is chosen. Idle with 1024 KiB × 4 was the default from plan 125 to 163
  (256 KiB and 2 before).

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
supported one. The app, the CLI and cache targets read disks and volumes natively (mount
manager, SetupAPI disk interfaces, storage IOCTLs, the SystemPartition setup value and the
kernel page file list), not through the Storage Management provider behind `Get-Disk` and
`Get-Volume`, which reports no disks on some machines, for example an iSCSI-booted Windows. `--save` writes an administrator-only machine profile for that volume
(named by the volume GUID, so it follows the volume, not the letter). Applying without `--save`
removes the saved profile unless `--runtime-only` is used. Pause drains while
preserving the task; Remove drains, releases RAM and removes its saved profile.
Files are never deleted by task removal.

The desktop uses the same management library and shows one card per volume,
grouped under its disk. Selecting Fast in its task editor is
the explicit volatility acknowledgement. Closing the desktop does not stop the
driver or change a task.

## Verification

`qcache developer verify` is the supported runner; one command runs a whole
campaign and restores the machine. How to prepare the test VM, which suite to run,
how to read results and how to judge a performance change are in
[TESTING.md](TESTING.md).

```powershell
qcache developer verify Q: --suite quick --output C:\QueueCache-Results
qcache developer verify Q: --campaign release-performance --lab-ntfs W: --budget-mib 2048 --repeats 3 --diskspd C:\Tools\DiskSpd\DiskSpd64.exe --pause-backing-cache --output C:\QueueCache-Results
```

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

The full build runs host-safe tests, publishes the CLI and desktop, builds the
current native driver, checks package layout and creates a fresh unsigned package.
It never installs, registers, formats or enables anything. GitHub Actions supplies
release version numbers; do not bump them manually.

`build/Sign-Lab.ps1` test-signs an unsigned Release package and
`build/Build-Installer.ps1` creates the installer. Their historical names and the
`labWriteCache` manifest key are compatibility surfaces, not separate driver
editions. The private signing key stays outside packages. Production signing and
trust are out of scope.

Every build signs with a new self-signed test certificate. Setup does not add it to
Windows' trusted certificate lists: in test mode Windows loads the drivers without it, and
Windows asks once to confirm installing the RAM disk driver. An unattended install (the
test VM) cannot answer that question, so it runs setup with `/TRUSTTESTCERTIFICATE`, which
trusts this build's certificate in `LocalMachine\Root` and `TrustedPublisher` first.

The installer stages an immutable version/hash-named driver and retains the
`qcachelab` service identity across upgrades. A reboot loads a changed driver.
Setup does not reboot automatically or silently configure a cache. Recovery and
registration backups live under `%ProgramData%\QueueCache`; keep an external VM
snapshot plus a copy of the selected backup and
`setup\Recover-Registration.ps1` before boot/lifecycle testing. The recovery script
does not require the driver or CLI and can target an offline SYSTEM hive; it never
reboots automatically.

## Desktop app

The desktop app (`src/QueueCache.Desktop`) is an Avalonia 12 app built from AXAML
views and view-models. It talks to the driver and the managed-disk service only
through `ICacheTaskService` and `IManagedDiskService`; closing it never stops a
cache or a disk.

### Structure

| Path | Contents |
|---|---|
| `App.axaml` | FluentAvalonia theme (follows Windows light/dark, teal accent `#087F8C`), notification-area icon |
| `Views/` | `MainWindow` (left navigation), one view per page, the selected item's details, and the dialogs |
| `ViewModels/` | `ShellViewModel` (navigation, timer, tray actions), `DashboardMonitor` (sampling and every action), one view-model per page, volume, virtual disk and dialog |
| `Controls/` | `StatusPill`, `VolatileBadge`, `SegmentBar`, `ActivityChart` |
| `Themes/` | `Colors.axaml` (light and dark values of every QueueCache color), `Icons.axaml` (Fluent UI System Icons, MIT), `Controls.axaml`, `Styles.axaml` |
| `Formatting/` | Sizes, rates and durations shown one way everywhere; value converters |
| `Services/` | Service interfaces, the dialog service and per-user desktop settings |

Views use compiled bindings (`x:DataType`), so a wrong binding fails the build.
View-models contain every state rule (which action applies when, status words,
units) and are tested without a window. Code-behind is limited to view concerns:
file pickers, closing dialogs, navigation selection, layout breakpoints.

Pages: **Overview** (health, RAM used, every item with its one figure that
matters, attention first), **Caches** (volumes grouped by physical disk, the
selected volume in detail), **Virtual disks**, and in the navigation footer
**Diagnostics** and **Settings** (theme, update interval, keep running in the
notification area, and an **Advanced** section with developer settings). Setup registers the `QueueCache-SignIn` scheduled task, which
starts QueueCache elevated and without its window (`--tray`) when an
administrator signs in; Windows' Run key would silently skip an app that needs
administrator rights. Settings → **Start QueueCache when you sign in** enables
or disables the task, an update keeps that choice, and uninstall removes it.
Closing the window keeps QueueCache in the notification area
(unless turned off); starting it again shows the running instance, as there is
one per Windows session. Windows fit the screen's working area, so title bars
and dialog buttons stay reachable on small screens. Desktop settings are stored in
`%LOCALAPPDATA%\QueueCache\desktop.json`; they never affect caches or disks.

Settings → **Windows shutdown** and VHDX creation share the detailed Windows
status-message control. It reads and writes the machine's `VerboseStatus` policy,
reports `DisableStatusMessages` suppression and errors, and never saves a copy in
`desktop.json`. While the control is visible it rereads once per second and on
window activation. This affects Windows' messages across the machine; it does not
provide a live save percentage. See [saving at shutdown](MANAGED_DISKS.md#saving-at-shutdown).

### Live figures

A running RAM disk or image in RAM shows reads and writes per second (both access
paths together), requests per second, totals since it started, the share served
by Direct access, errors, and space used and free on its volume. An image in RAM
also shows its last save (duration, size, speed) and an upper bound of what was
written since. A disk image with a RAM cache shows its cache exactly as the
Caches page does. Counters start again when a disk is started again; the chart
then starts over instead of showing a negative rate.

### Memory views

- **Memory map (Caches).** The monitor reads the driver's layout map
  (`IOCTL_QCACHE_LAYOUT_MAP_V1`) while its details or pop-out are visible. The normal
  cadence is every 2 s; the 0.5 s and 0.1 s preferences also apply to visible maps. One square per 256 KiB
  chunk (grouped to at most 2,048 squares): colour = read cache / on disk, kept /
  not yet on disk / free, strength = how full, diagonal = out of disk order (at least 8
  used slots, under half of the neighbouring pairs in order). The header shows the
  in-disk-order share and free chunks. The driver releases the cache lock every 256
  chunks. Older drivers: no card. The shared legend says **Darker: fuller** and
  **Lighter: partly used**; strength measures all occupied space, rather than only
  the amount still waiting for disk.
- **Pop out.** Opens a separate live cache map, initially maximized, with **Full
  screen** / F11 and **Close** controls. Escape exits full screen. Squares fit the
  window; up to 32,768 squares show more individual chunks than the embedded map.
  The window keeps updating across page changes. Closing or minimizing it stops
  its map requests; volume replacement closes it. It shares samples with the
  details pane, so opening both never doubles polling.
- **Place in physical memory (RAM disk, image in RAM).** The provider's `PhysicalMap`
  action counts the disk's locked pages in 1,024 slices of physical memory (span:
  installed RAM, widened to the highest page). The service asks once per disk creation,
  since locked pages never move, and leaves it out if the provider is older or the
  query fails. The strip shows lowest to highest address; stronger colour = more of
  that slice is the disk's.

### Update interval

Settings → Live values offers 0.1, 0.5, 1, 2, 5 and 10 seconds, saved per user.
The 0.1-second option updates cache and disk values, charts, and visible cache maps
up to ten times per second. Discovery remains on its separate two-minute cadence;
physical RAM-disk placement is fixed while the disk runs and read once per creation.
Slow samples never overlap for one volume. State becomes unavailable after three
intervals, with a minimum of three seconds.

### Advanced settings

- **Detailed driver timing** turns on the timing of the cache driver
  (`PerformanceTiming`) and the RAM disk driver (`disk timing`) for every cache
  and running RAM disk. The drivers start with it off; while QueueCache runs, the
  monitor switches each driver whose state differs from the setting, once per
  cache instance or disk creation. Figures appear on the item and on Diagnostics.
  Measured cost (lab VM, CrystalDiskMark-style rows, best of 3 passes, medians
  of 2–3 rounds; noise about ±2%):

  | Workload | Timing on vs off |
  |---|---|
  | Cache, 4 KiB random writes into RAM, Q1 / Q32 | −7% to −8% / −10% to −12% |
  | Cache, 1 MiB sequential writes, Q1 / Q8 | −1% / −4% to −7% |
  | RAM disk (Direct or Standard), 4 KiB random reads | −5% to −8% |
  | RAM disk, 4 KiB random writes | −2% to −4% |
  | RAM disk, 1 MiB sequential reads and writes | −1.5% to +1% |

  With timing off, the new RAM disk driver measured within noise of the previous
  one (every row within −3.3% to +2.8%; the 4 KiB rows average about −1%).
- **Answer cache hits on the calling thread** (`CallerPath`, default on). The
  driver does not report it, so the monitor assumes the default for a new cache
  instance and sends the setting only when it differs.

The cache editor's **Advanced tuning** also offers **Write to disk in save order**
for an individual Fast cache. Its question-mark button opens
help on hover, keyboard focus or activation, including when ordering is
unavailable. The help explains the save boundaries, volatility, the tested
crash-safety scope, slower flush-heavy writing, volume restrictions and persistence.
This selects ordinary ordering; the forced-write variant from #31 remains a
developer-only experiment. See [cache policies](CACHE_POLICIES.md#fast-write-ordering-13).

Lab delay and fault hooks are deliberately not settings: they stall or fail real
writes and belong to `qcache developer verify`.

### Design rules

- Status is an icon plus a word (Active, Error, Running, Stopped…), never color
  alone. A volume without a fresh driver answer shows **State unavailable**,
  never zero.
- Fast caches, RAM disks and images in RAM carry a **Volatile** badge.
- One main action per item (accent button), the common ones beside it, the rest
  under **More**. Destructive items sit at the bottom of More, in red, and are
  confirmed in a content dialog whose buttons say what happens ("Erase and stop").
  When a choice loses data, Cancel is the default button.
- A confirmation that discards data carries the generation that was displayed, so
  the service refuses it if the disk changed meanwhile.
- Results and errors appear on the item the action ran on, as an info bar.
- Settings dialogs show the few choices most people make; tuning sits in a
  collapsed **Advanced** section. New caches default to Fast.
- Every color is a theme resource with a light and a dark value. The chart and
  occupancy colors (reads blue, not yet on disk orange, on disk green) were
  checked for color-vision deficiency separation; every series also has a
  labelled legend with its value.
- Sizes are binary (KiB, MiB, GiB) with at most three significant digits, live
  numbers use fixed-width digits, and every statistic names its time window.

### Tests

`dotnet run --project tests/QueueCache.Desktop.Tests -c Release [output]` runs
the view-model contracts against fixtures (no volume or disk is opened), then
renders every page and dialog headlessly in light and dark and writes
screenshots to the output folder (default `artifacts/ui-tests`). Review the
screenshots when changing a view.

Memory-map lifecycle (plans 98–99): while the Caches page and window are visible,
the selected volume gets maps at the cadence above. Other pages and hiding
the main window stop its map requests; a visible pop-out keeps its volume requested.
Returning gets a fresh map. A response is published only while the same volume is still requested
and its cache generation remains current. Partial replies (for example a resize
during collection) are hidden. Stale/unavailable state, removal and allocation
changes clear the prior map. The map tooltip describes within-chunk consecutive
block placement, rather than filesystem fragmentation. Headless tests cover late
selection replies, changed generations, partial maps, resize and stale state.
RAM disk physical maps also disappear when the disk's state is unavailable or
stopped, and return with a fresh running snapshot.
Headless map grouping checks cover 8 GiB and 32 GiB fixtures. This verifies the
rendering contract, not the driver's polling cost with a 32 GiB allocation.

Pop-out and 100 ms refresh tests cover page changes, duplicate opens, minimization,
closing during a pending sample, volume replacement, full-screen/F11/Escape controls,
shared shade legends and light/dark rendering. These are host-safe UI checks;
the earlier two-second map-cost measurements do not establish 100 ms polling cost.


## README screenshots

**App screenshots** (`overview`, `cache`, `cache-map`, `ram-disk`, `ram-disk-physical`, `cache-settings`,
`new-disk` in `docs/images`) are rendered headlessly from the real views with an
invented example setup (drives, disks, sizes and a minute of activity), defined
in `tests/QueueCache.Desktop.Tests/ReadmeImages.cs`. After a UI change, run:

```powershell
dotnet run --project tests/QueueCache.Desktop.Tests -c Release -- --readme docs/images
```

It works on any OS with the .NET SDK, in dark mode, at 1280×860 (the RAM disk
page 1280×960). Change the example in that file, not by editing images.

**CrystalDiskMark screenshots** (`cdm-cache.png`, `cdm-ram-disk.png`) are real
runs; the README's performance table copies their numbers. How to redo them is in
[TESTING.md](TESTING.md#7-readme-benchmark-crystaldiskmark).

## Repository layout

| Path | Purpose |
|---|---|
| `driver/qcache` | Current native dispatch, cache, policy and ABI code |
| `src/QueueCache.Management` | Versioned protocol and device access |
| `src/QueueCache.Operations` | Configuration, persistence and file workloads |
| `src/QueueCache.Cli` | Operator and developer command binding |
| `src/QueueCache.Desktop` | Avalonia desktop app: AXAML views, view-models, live telemetry ([structure](DEVELOPMENT.md)) |
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
