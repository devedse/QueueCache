using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QueueCache.Desktop;
using QueueCache.Management;
using QueueCache.Operations;
using System.Runtime.Versioning;

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
Check(labels.Contains("Active") && labels.Contains("Available"), "active and available disks render");
Check(!labels.Any(t => t?.Contains("Inspect") == true), "no manual inspect step");
var buttons = window.GetVisualDescendants().OfType<Button>().ToArray();
var pause = buttons.Single(b => Equals(b.Content, "Pause") && b.IsVisible);
pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
Check(fixture.Pauses == 1, "Pause uses shared task service");
var remove = buttons.Single(b => Equals(b.Content, "Remove cache") && b.IsVisible);
remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
Check(fixture.Removes == 1, "Remove uses draining task operation");
fixture.PendingInventory = new();
var discovery = Invoke("Refresh");
fixture.PendingDisk = new();
var blockedSample = Invoke("Sample");
var reads = fixture.DataReads;
Invoke("Sample").GetAwaiter().GetResult();
Check(fixture.DataReads > reads, "healthy disk keeps sampling while inventory and another disk are stalled");
Check(fixture.BlockedReads == 1, "only one outstanding request per stalled disk");
fixture.PendingDisk.SetResult(fixture.State);
fixture.PendingDisk = null;
fixture.PendingInventory.SetResult(fixture.Disks);
fixture.PendingInventory = null;
Dispatcher.UIThread.RunJobs();
Check(blockedSample.IsCompleted && discovery.IsCompleted, "pending sampling/discovery finish independently");
window.Close();
var settings = new CacheSettingsWindow(fixture.Disks[1], fixture.State, true);
settings.Show();
Dispatcher.UIThread.RunJobs();
using (var frame = settings.CaptureRenderedFrame() ?? throw new Exception("No settings frame.")) frame.Save(Path.Combine(output, "settings.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
Check(!settings.GetVisualDescendants().OfType<CheckBox>().Any(), "no consent checkboxes");
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
drain.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
Check(Visible("Balanced:") && Visible("Start pressure") && Visible("Maximum dirty age") && !Visible("Write-idle"), "Balanced adds watermarks and age but not the idle interval");
drain.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
Check(Visible("Idle:") && Visible("Write-idle") && Visible("Maximum dirty age"), "Idle adds the write-idle interval");
var marks = settings.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == "?").Select(t => t.Parent as Control).ToArray();
Check(marks.Length >= 8 && marks.All(m => ToolTip.GetTip(m!) is TextBlock { Text.Length: > 40 }), "each help badge carries explanatory hover text");
settings.Close();
Console.WriteLine("Desktop fixture checks passed; no real disk operations performed.");
Task Invoke(string method) => (Task)typeof(MainWindow).GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null)!;
static void Check(bool result, string description)
{
    if (!result) throw new Exception(description);
    Console.WriteLine("PASS: " + description);
}

sealed class Fixture : ICacheTaskService
{
    public DiskDescription[] Disks = [new(0, "System SSD", 100L << 30, "fixture-system", ["C:"], true, true), new(1, "Game library SSD", 200L << 30, "fixture-data", ["Q:"], false, false)];
    public WriteCacheState State = new(929, 0, 200UL << 30, 4UL << 30, 4UL << 30, 1536UL << 20, 256UL << 10, 4000UL << 20, 1, 2UL << 30, 512UL << 20, 0, 0, 0, 0, 1536UL << 20)
        { Options = new(), CleanReadBytes = 1024UL << 20, CleanWriteBytes = 256UL << 20,
            Instance = 1, Generation = 2, GlobalLimitBytes = 6UL << 30, GlobalReservedBytes = 4UL << 30 };
    public int Pauses, Removes, DataReads, BlockedReads;
    public TaskCompletionSource<IReadOnlyList<DiskDescription>>? PendingInventory;
    public TaskCompletionSource<WriteCacheState>? PendingDisk;
    public Task<IReadOnlyList<DiskDescription>> ListAsync() => PendingInventory?.Task ?? Task.FromResult<IReadOnlyList<DiskDescription>>(Disks);
    public Task<WriteCacheState> ReadAsync(DiskDescription disk)
    {
        if (disk.Number == 0 && PendingDisk is not null) { BlockedReads++; return PendingDisk.Task; }
        if (disk.Number == 1) DataReads++;
        return Task.FromResult(disk.Number == 1 ? State : State with { Flags = 0, BudgetBytes = 0, ReservedBytes = 0, DirtyBytes = 0, PayloadCapacity = 0 });
    }
    public bool IsPersistent(DiskDescription disk) => true;
    public Task SetEnabledAsync(string volume, bool enabled, bool persistent) { if (!enabled) Pauses++; return Task.CompletedTask; }
    public Task RemoveAsync(string volume) { Removes++; return Task.CompletedTask; }
    public Task FlushAsync(DiskDescription disk) => Task.CompletedTask;
    public Task SaveAsync(string volume, CacheConfiguration configuration, bool persistent, IProgress<string> progress) => Task.CompletedTask;
    public Task<WorkloadReport> TestAsync(string volume, bool benchmark, IProgress<string> progress, CancellationToken token) => throw new NotSupportedException("Fixture never opens disks.");
}
