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
notification area). Desktop settings are stored in
`%LOCALAPPDATA%\QueueCache\desktop.json`; they never affect caches or disks.

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
