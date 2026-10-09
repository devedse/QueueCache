# Desktop app

The desktop app (`src/QueueCache.Desktop`) is an Avalonia 12 app built from AXAML
views and view-models. It talks to the driver and the managed-disk service only
through `ICacheTaskService` and `IManagedDiskService`; closing it never stops a
cache or a disk.

## Structure

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

## Live figures

A running RAM disk or image in RAM shows reads and writes per second (both access
paths together), requests per second, totals since it started, the share served
by Direct access, errors, and space used and free on its volume. An image in RAM
also shows its last save (duration, size, speed) and an upper bound of what was
written since. A disk image with a RAM cache shows its cache exactly as the
Caches page does. Counters start again when a disk is started again; the chart
then starts over instead of showing a negative rate.

## Memory views

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

## Update interval

Settings → Live values offers 0.1, 0.5, 1, 2, 5 and 10 seconds, saved per user.
The 0.1-second option updates cache and disk values, charts, and visible cache maps
up to ten times per second. Discovery remains on its separate two-minute cadence;
physical RAM-disk placement is fixed while the disk runs and read once per creation.
Slow samples never overlap for one volume. State becomes unavailable after three
intervals, with a minimum of three seconds.

## Advanced settings

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

Lab delay and fault hooks are deliberately not settings: they stall or fail real
writes and belong to `qcache developer verify`.

## Design rules

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

## Tests

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
