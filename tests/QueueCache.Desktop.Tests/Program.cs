using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QueueCache.Desktop;
using QueueCache.Management;
using QueueCache.Operations;

[assembly: SupportedOSPlatform("windows")]

AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
var output = args.Length == 0 ? "artifacts/ui-tests" : args[0];
Directory.CreateDirectory(output);
var fixture = new Fixture();
var window = new MainWindow(fixture);
window.Show();
Check(window.Icon is not null, "application window icon is embedded");
Dispatcher.UIThread.RunJobs();
using (var frame = window.CaptureRenderedFrame() ?? throw new Exception("No rendered dashboard frame.")) frame.Save(Path.Combine(output, "dashboard.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
var labels = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
Check(labels.Contains("Active") && labels.Contains("Available"), "active and available volumes render");
Check(labels.Count(t => t?.StartsWith("Disk ") == true) == 2 && labels.Any(t => t?.Contains("each volume has its own cache") == true), "volumes are grouped under their disk; a shared disk says each volume has its own cache");
Check(window.GetVisualDescendants().OfType<Button>().Count(b => Equals(b.Content, "Safely eject disk 1")) == 1 &&
      !window.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Content, "Safely eject disk 0")),
    "the disk group offers one safe-eject action, never on the system disk");
Check(labels.Contains("Q: Games") && labels.Contains("C:") && labels.Contains("R:"), "one card per lettered volume");
Check(labels.Any(t => t?.Contains("not formatted") == true), "an unformatted volume explains why it cannot be cached");
Check(labels.Any(t => t?.Contains("FAT32 has no journal") == true), "a FAT32 volume can be cached and warns that it has no journal");
Check(!labels.Any(t => t?.Contains("Inspect") == true), "no manual inspect step");
// Colour keys use the same brushes the bar and chart draw with.
var swatches = window.GetVisualDescendants().OfType<Border>().Where(b => b.Width == 11 && b.Background is not null).ToArray();
Check(labels.Contains("READABLE FROM RAM") && swatches.Length >= 7, "legend swatches label every chart colour");
var colours = swatches.Select(b => (b.Background as Avalonia.Media.ISolidColorBrush)?.Color).ToArray();
Check(new[] { "#3489DB", "#087F8C", "#9A66CC", "#E8F1F2" }.All(hex => colours.Contains(Avalonia.Media.Color.Parse(hex))), "legend colours match the occupancy palette");
var buttons = window.GetVisualDescendants().OfType<Button>().ToArray();
var inventoryReads = fixture.InventoryReads;
buttons.Single(b => Equals(b.Content, "Refresh volumes")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
Dispatcher.UIThread.RunJobs();
Check(fixture.InventoryReads == inventoryReads + 1, "manual inventory refresh remains immediate between fallback scans");
Check((DateTimeOffset)typeof(MainWindow).GetField("nextInventory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)! > DateTimeOffset.UtcNow.AddSeconds(100), "inventory fallback is independent of fast telemetry interval");
var pause = buttons.Single(b => Equals(b.Content, "Pause") && b.IsVisible);
pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
Check(fixture.Pauses == 1, "Pause uses shared task service");
var clear = buttons.Single(b => Equals(b.Content, "Clear read cache") && b.IsVisible);
clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
Check(fixture.CleanDrops == 1, "Clear read cache releases clean blocks only");
var frequency = window.GetVisualDescendants().OfType<ComboBox>().Single(c => (c.SelectedItem as string)?.StartsWith("Update every") == true);
frequency.SelectedIndex = 3;
Dispatcher.UIThread.RunJobs();
Check(Equals(frequency.SelectedItem, "Update every 5s"), "one selector sets the live update interval");
var remove = buttons.Single(b => Equals(b.Content, "Remove cache") && b.IsVisible);
remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
Check(fixture.Removes == 1, "Remove uses draining task operation");
fixture.PendingFlush = new();
buttons.Single(b => Equals(b.Content, "Flush now") && b.IsVisible).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
Dispatcher.UIThread.RunJobs();
Check(!pause.IsEnabled, "same-volume mutations disabled during pending Flush");
var addButtons = buttons.Where(b => Equals(b.Content, "Add cache")).ToArray();
Check(addButtons.Length == 3 && addButtons[0].IsEnabled, "another volume remains configurable during pending Flush");
Check(addButtons[1].IsEnabled, "a FAT32 volume can get a cache");
Check(!addButtons[2].IsEnabled, "an unformatted volume cannot get a cache");
var duringFlush = fixture.DataReads;
Invoke("Sample").GetAwaiter().GetResult();
Check(fixture.DataReads > duringFlush, "telemetry continues during pending Flush");
fixture.PendingFlush.SetResult();
fixture.PendingFlush = null;
Dispatcher.UIThread.RunJobs();
Check(pause.IsEnabled, "volume actions re-enabled after Flush completion");
fixture.PendingInventory = new();
var discovery = Invoke("Refresh");
fixture.PendingDisk = new();
var blockedSample = Invoke("Sample");
var reads = fixture.DataReads;
Invoke("Sample").GetAwaiter().GetResult();
Check(fixture.DataReads > reads, "healthy volume keeps sampling while inventory and another volume are stalled");
Check(fixture.BlockedReads == 1, "only one outstanding request per stalled volume");
fixture.PendingDisk.SetResult(fixture.State);
fixture.PendingDisk = null;
fixture.PendingInventory.SetResult(fixture.Volumes);
fixture.PendingInventory = null;
Dispatcher.UIThread.RunJobs();
Check(blockedSample.IsCompleted && discovery.IsCompleted, "pending sampling/discovery finish independently");
// A cache left on an unformatted volume (raw developer tests) can be flushed and removed, not reconfigured.
fixture.RawVolumeHasCache = true;
Invoke("Sample").GetAwaiter().GetResult();
Dispatcher.UIThread.RunJobs();
var rawCard = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "R:").GetVisualAncestors().OfType<Border>()
    .First(b => b.Child is StackPanel);
Button RawButton(string label) => rawCard.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, label));
Check(!RawButton("Cache settings").IsEnabled && !RawButton("Pause").IsEnabled, "a cached unformatted volume cannot be reconfigured or paused");
Check(RawButton("Remove cache").IsEnabled && RawButton("Flush now").IsEnabled, "a cached unformatted volume can be flushed and removed");
Check(rawCard.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("no file system") == true), "the card says why settings are unavailable");
fixture.RawVolumeHasCache = false;
window.Close();
var newState = fixture.State with { Flags = fixture.State.Flags | 4096, BudgetBytes = 0, ReservedBytes = 0, Options = null };
var newSettings = new CacheSettingsWindow(fixture.Volumes[1], newState, false);
newSettings.Show();
Dispatcher.UIThread.RunJobs();
Check(newSettings.GetVisualDescendants().OfType<ComboBox>().Any(c => Equals(c.SelectedItem, "Idle")), "new cache editor selects Idle alpha default");
newSettings.Close();
var settings = new CacheSettingsWindow(fixture.Volumes[1], fixture.State with { Flags = fixture.State.Flags | 4096 }, true);
settings.Show();
Dispatcher.UIThread.RunJobs();
using (var frame = settings.CaptureRenderedFrame() ?? throw new Exception("No settings frame.")) frame.Save(Path.Combine(output, "settings.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
Check(!settings.GetVisualDescendants().OfType<CheckBox>().Any(), "no consent checkboxes");
Check(settings.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.StartsWith("Q: Games  /  NTFS") == true), "settings name the volume and its disk");
var memory = settings.GetVisualDescendants().OfType<ComboBox>().First();
Check(memory.SelectedIndex == 4, "existing 4 GiB memory selected");
memory.SelectedIndex = 5;
Dispatcher.UIThread.RunJobs();
Check(settings.GetVisualDescendants().OfType<NumericUpDown>().Single(n => n.Name == "CustomBudget").IsVisible, "custom memory input appears");
// Only the settings the selected drain algorithm actually uses are shown, in one panel.
var drain = settings.GetVisualDescendants().OfType<ComboBox>().Single(c => Equals(c.SelectedItem, "Eager"));
bool Visible(string label) => settings.GetVisualDescendants().OfType<TextBlock>()
    .Any(t => t.Text?.StartsWith(label) == true && t.IsVisible && t.GetVisualAncestors().All(a => a is not Control c || c.IsVisible));
Check(Visible("Eager:") && !Visible("Start pressure") && !Visible("Maximum dirty age") && !Visible("Write-idle"), "Eager describes itself and hides watermark, age and idle settings");
Check(Visible("Maximum adjacent-write batch") && Visible("Maximum simultaneous disk writes"), "batch and parallelism apply to every algorithm");
drain.SelectedIndex = 1;
Dispatcher.UIThread.RunJobs();
Check(Visible("Balanced:") && Visible("Start pressure") && Visible("Maximum dirty age") && !Visible("Write-idle"), "Balanced adds watermarks and age but not the idle interval");
drain.SelectedIndex = 2;
Dispatcher.UIThread.RunJobs();
Check(Visible("Idle:") && Visible("Write-idle") && Visible("Maximum dirty age"), "Idle adds the write-idle interval");
var marks = settings.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == "?").Select(t => t.Parent as Control).ToArray();
drain.SelectedIndex = 3;
Dispatcher.UIThread.RunJobs();
Check(Visible("Deferred:") && Visible("Maximum dirty age") && !Visible("Start pressure") && !Visible("Write-idle"), "Deferred exposes age only");
Check(settings.GetVisualDescendants().OfType<NumericUpDown>().Any(n => n.Maximum == 3600000), "one-hour age available on capable driver");
Check(marks.Length >= 8 && marks.All(m => ToolTip.GetTip(m!) is TextBlock { Text.Length: > 40 }), "each help badge carries explanatory hover text");
settings.Close();
Console.WriteLine("Desktop fixture checks passed; no real volume or disk operations performed.");
Task Invoke(string method) => (Task)typeof(MainWindow).GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null)!;
static void Check(bool result, string description)
{
    if (!result)
        throw new Exception(description);
    Console.WriteLine("PASS: " + description);
}

sealed class Fixture : ICacheTaskService
{
    // C: alone on disk 0; Q: (NTFS) and R: (unformatted) share disk 1.
    public VolumeDescription[] Volumes =
    [
        new("C:", "", "NTFS", 99L << 30, @"\\?\Volume{00000000-0000-0000-0000-000000000001}\", 0, "System SSD", "fixture-system", 100L << 30, true, false, true),
        new("Q:", "Games", "NTFS", 150L << 30, @"\\?\Volume{00000000-0000-0000-0000-000000000002}\", 1, "Game library SSD", "fixture-data", 200L << 30, false, false, false),
        new("S:", "Stick", "FAT32", 16L << 30, @"\\?\Volume{00000000-0000-0000-0000-000000000004}\", 1, "Game library SSD", "fixture-data", 200L << 30, false, false, false),
        new("R:", "", "", 49L << 30, @"\\?\Volume{00000000-0000-0000-0000-000000000003}\", 1, "Game library SSD", "fixture-data", 200L << 30, false, false, false)
    ];
    public WriteCacheState State = new(929 | 1024, 0, 200UL << 30, 4UL << 30, 4UL << 30, 1536UL << 20, 256UL << 10, 4000UL << 20, 1, 2UL << 30, 512UL << 20, 0, 0, 0, 0, 1536UL << 20)
    {
        Options = new(Drain: DrainAlgorithm.Eager),
        CleanReadBytes = 1024UL << 20,
        CleanWriteBytes = 256UL << 20,
        Instance = 1,
        Generation = 2,
        GlobalLimitBytes = 6UL << 30,
        GlobalReservedBytes = 4UL << 30
    };
    public int Pauses, Removes, CleanDrops, DataReads, BlockedReads, InventoryReads;
    public bool RawVolumeHasCache;
    public TaskCompletionSource<IReadOnlyList<VolumeDescription>>? PendingInventory;
    public TaskCompletionSource<WriteCacheState>? PendingDisk;
    public TaskCompletionSource? PendingFlush;
    public Task<IReadOnlyList<VolumeDescription>> ListAsync()
    {
        InventoryReads++;
        return PendingInventory?.Task ?? Task.FromResult<IReadOnlyList<VolumeDescription>>(Volumes);
    }
    public Task<WriteCacheState> ReadAsync(VolumeDescription volume)
    {
        if (volume.Volume == "C:" && PendingDisk is not null)
        {
            BlockedReads++;
            return PendingDisk.Task;
        }
        if (volume.Volume == "Q:")
            DataReads++;
        return Task.FromResult(volume.Volume == "Q:" || volume.Volume == "R:" && RawVolumeHasCache ? State : State with
        {
            Flags = 256,
            BudgetBytes = 0,
            ReservedBytes = 0,
            DirtyBytes = 0,
            PayloadCapacity = 0
        });
    }
    public bool IsPersistent(VolumeDescription volume) => true;
    public Task SetEnabledAsync(string volume, bool enabled, bool persistent)
    {
        if (!enabled)
            Pauses++;
        return Task.CompletedTask;
    }
    public Task RemoveAsync(string volume)
    {
        Removes++;
        return Task.CompletedTask;
    }
    public Task FlushAsync(VolumeDescription volume) => PendingFlush?.Task ?? Task.CompletedTask;
    public Task DropCleanAsync(VolumeDescription volume)
    {
        CleanDrops++;
        return Task.CompletedTask;
    }
    public Task SaveAsync(string volume, CacheConfiguration configuration, bool persistent, IProgress<string> progress) => Task.CompletedTask;
    public Task<WorkloadReport> TestAsync(string volume, bool benchmark, IProgress<string> progress, CancellationToken token) => throw new NotSupportedException("Fixture never opens disks.");
}
