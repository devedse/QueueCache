using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Desktop;

[SupportedOSPlatform("windows")]
public sealed class MainWindow : Window
{
    internal static readonly IBrush Ink = Brush.Parse("#172B42"), Muted = Brush.Parse("#66788A"), Accent = Brush.Parse("#087F8C");
    // Chart palette lives on CacheOccupancy so the bar, the chart and the legends agree.
    internal static readonly IBrush ReadFill = CacheOccupancy.ReadFill, RetainedFill = CacheOccupancy.RetainedFill,
        PendingFill = CacheOccupancy.PendingFill, FreeFill = CacheOccupancy.FreeFill;
    private readonly StackPanel cards = new() { Spacing = 16 };
    private readonly StackPanel disconnected = new() { Spacing = 8 };
    private readonly TextBlock message = Text("Discovering your volumes…", 14, Muted), summary = Text("Your storage, accelerated.", 16, Muted);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<Card> views = [];
    private bool busy, discovering, closed;
    private DateTimeOffset nextInventory = DateTimeOffset.MinValue;
    private readonly ICacheTaskService service;
    public MainWindow() : this(new WindowsCacheTaskService()) { }
    public MainWindow(ICacheTaskService service)
    {
        this.service = service;
        Icon = AppBranding.CreateIcon();
        Title = "QueueCache";
        Width = 1080;
        Height = 790;
        MinWidth = 820;
        MinHeight = 620;
        Background = Brush.Parse("#F3F6FA");
        FontFamily = new FontFamily("Segoe UI");
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 24) };
        var brand = new StackPanel { Spacing = 7 };
        brand.Children.Add(Text("QueueCache", 30, Ink, FontWeight.SemiBold));
        brand.Children.Add(summary);
        var brandRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        var brandImage = AppBranding.CreateHeaderImage();
        var logo = new Image { Source = brandImage, Width = 64, Height = 64, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapInterpolationMode(logo, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        brandRow.Children.Add(logo);
        brandRow.Children.Add(brand);
        heading.Children.Add(brandRow);
        Closed += (_, _) => brandImage.Dispose();
        // One selector drives every live value: metrics, residency line and the history chart,
        // which stores one point per sample, so the visible window is 60 x this interval.
        var rates = new[] { 0.5, 1, 2, 5, 10 };
        var frequency = new ComboBox { ItemsSource = rates.Select(s => $"Update every {s:0.#}s").ToArray(), SelectedIndex = 1, VerticalAlignment = VerticalAlignment.Center };
        frequency.SelectionChanged += (_, _) => timer.Interval = TimeSpan.FromSeconds(rates[Math.Max(0, frequency.SelectedIndex)]);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(frequency);
        controls.Children.Add(Action("Refresh volumes", Refresh));
        Grid.SetColumn(controls, 1);
        heading.Children.Add(controls);
        var body = new StackPanel { Margin = new Thickness(36), Spacing = 12 };
        body.Children.Add(heading);
        body.Children.Add(Text("VOLUMES & CACHES", 12, Muted, FontWeight.SemiBold));
        body.Children.Add(cards);
        body.Children.Add(disconnected);
        body.Children.Add(message);
        Content = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Opened += async (_, _) => { timer.Start(); await Refresh(); };
        timer.Tick += async (_, _) =>
        {
            // Driver freshness follows telemetry frequency; identity discovery does not.
            var stale = TimeSpan.FromSeconds(Math.Max(3, timer.Interval.TotalSeconds * 3));
            foreach (var card in views.Where(v => v.State is not null && DateTimeOffset.UtcNow - v.Sampled > stale))
            {
                card.Badge.Text = "State unavailable";
                card.Activity.IsVisible = false;
                card.Description.Text = "Waiting for a fresh driver response. Last known state is not being shown as live.";
                card.Settings.IsEnabled = card.Pause.IsEnabled = card.Flush.IsEnabled = card.Remove.IsEnabled = card.DropClean.IsEnabled = false;
            }
            UpdateSummary();
            if (closed)
                return;
            // Discovery never holds up telemetry; each volume has at most one outstanding sample.
            if (DateTimeOffset.UtcNow >= nextInventory && !busy)
                _ = Refresh();
            await Sample();
        };
        Closing += (_, e) => { if (busy || views.Any(v => v.Busy)) { e.Cancel = true; message.Text = "Finishing the current operation…"; } else { closed = true; timer.Stop(); } };
    }
    private async Task Refresh()
    {
        if (discovering || views.Any(v => v.Busy))
            return;
        discovering = true;
        // Cached identity plus manual refresh and a conservative fallback. Native
        // device-change notification is a future refinement, not implemented here.
        nextInventory = DateTimeOffset.UtcNow.AddMinutes(2);
        try
        {
            var volumes = await service.ListAsync();
            var saved = await service.ListSavedAsync();
            if (closed)
                return;
            if (!views.Any(v => v.Busy) && Signature(volumes) != Signature(views.Select(v => v.Volume)))
            {
                cards.Children.Clear();
                views.Clear();
                // Each volume is its own cache; volumes are grouped under the disk that holds them.
                foreach (var disk in volumes.GroupBy(v => (v.DiskNumber, v.Instance)).OrderBy(g => g.Key.DiskNumber))
                {
                    var first = disk.First();
                    var header = Text($"Disk {first.DiskNumber}  ·  {first.DiskName}  ·  {first.DiskBytes / 1073741824.0:0.##} GiB" +
                        (disk.Count() > 1 ? "  ·  each volume has its own cache" : ""), 13, Muted, FontWeight.SemiBold);
                    header.Margin = new Thickness(0, cards.Children.Count == 0 ? 0 : 12, 0, 0);
                    cards.Children.Add(header);
                    if (!first.IsBoot && !first.IsSystem && !disk.Any(v => v.IsPaging))
                    {
                        var diskVolume = first.Volume;
                        cards.Children.Add(Action($"Safely eject disk {first.DiskNumber}", () => EjectDisk(diskVolume)));
                    }
                    foreach (var volume in disk)
                    {
                        var card = new Card(volume);
                        views.Add(card);
                        cards.Children.Add(BuildCard(card));
                    }
                }
            }
            disconnected.Children.Clear();
            var present = volumes.Select(v => v.VolumeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in saved.Where(p => !present.Contains(p.VolumeId)))
            {
                if (disconnected.Children.Count == 0)
                    disconnected.Children.Add(Text("SAVED VOLUMES NOT CONNECTED", 12, Muted, FontWeight.SemiBold));
                disconnected.Children.Add(Text($"{profile.Volume} · {profile.VolumeId} · Unavailable. Its saved settings remain; no live cache state is shown.", 13, Muted));
            }
            await Sample();
            message.Text = volumes.Count == 0 ? "No volumes found." : "";
        }
        catch (Exception ex) { if (!closed) message.Text = $"Volume discovery: {ex.Message}"; }
        finally { discovering = false; }
    }
    private Control BuildCard(Card card)
    {
        Button VolumeAction(string label, Func<Task> action)
        {
            var button = Action(label, action, card);
            card.Actions.Add(button);
            return button;
        }
        var content = new StackPanel { Spacing = 18 };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var name = new StackPanel { Spacing = 5 };
        name.Children.Add(Text(card.Volume.Name, 23, Ink, FontWeight.SemiBold));
        name.Children.Add(Text(Describe(card.Volume), 13, Muted));
        heading.Children.Add(name);
        Grid.SetColumn(card.Badge, 1);
        heading.Children.Add(card.Badge);
        content.Children.Add(heading);
        content.Children.Add(card.Description);
        card.Activity.Children.Add(card.Bucket);
        card.Activity.Children.Add(Legend((ReadFill, "Read cache (blocks read from disk)"), (RetainedFill, "Retained writes (already on disk, kept for reads)"),
            (PendingFill, "Pending writes (still only in RAM)"), (FreeFill, "Free RAM budget")));
        var metrics = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*") };
        metrics.Children.Add(Metric("READABLE FROM RAM", card.Cached));
        var dirty = Metric("PENDING WRITES", card.Dirty);
        Grid.SetColumn(dirty, 1);
        metrics.Children.Add(dirty);
        var incoming = Metric("INCOMING", card.Incoming);
        Grid.SetColumn(incoming, 2);
        metrics.Children.Add(incoming);
        var draining = Metric("WRITING TO DISK", card.Draining);
        Grid.SetColumn(draining, 3);
        metrics.Children.Add(draining);
        card.Activity.Children.Add(metrics);
        content.Children.Add(card.Activity);
        card.Activity.Children.Add(card.Residency);
        card.Activity.Children.Add(card.History);
        card.Activity.Children.Add(Legend((ReadFill, "Reads"), (RetainedFill, "Incoming writes"), (PendingFill, "Writing to disk")));
        card.Activity.Children.Add(Text("Last 60 samples · auto-scaled", 11, Muted));
        var actions = new WrapPanel();
        card.Settings = VolumeAction("Add cache", () => Edit(card));
        actions.Children.Add(card.Settings);
        card.Settings.Background = Accent;
        card.Settings.Foreground = Brushes.White;
        card.Pause = VolumeAction("Pause", () => service.SetEnabledAsync(Letter(card), !(card.State?.Enabled ?? false), Persistent(card)));
        actions.Children.Add(card.Pause);
        card.Flush = VolumeAction("Flush now", () => service.FlushAsync(card.Volume));
        actions.Children.Add(card.Flush);
        card.DropClean = VolumeAction("Clear read cache", async () => { await service.DropCleanAsync(card.Volume); message.Text = "Cached clean blocks released. Pending writes were not touched."; });
        actions.Children.Add(card.DropClean);
        card.Remove = VolumeAction("Remove cache", async () => { message.Text = "Draining and removing cache…"; await service.RemoveAsync(Letter(card)); });
        actions.Children.Add(card.Remove);
        content.Children.Add(actions);
        content.Children.Add(card.Operation);
        var details = new Expander { Header = "Diagnostics" };
        var diagnostics = new StackPanel { Spacing = 10 };
        var tools = new WrapPanel();
        tools.Children.Add(VolumeAction("File tests", () => Test(card, false)));
        tools.Children.Add(VolumeAction("Benchmark", () => Test(card, true)));
        var cancel = new Button { Content = "Cancel test" };
        cancel.Click += (_, _) => card.Workload?.Cancel();
        tools.Children.Add(cancel);
        diagnostics.Children.Add(tools);
        diagnostics.Children.Add(card.Details);
        details.Content = diagnostics;
        content.Children.Add(details);
        return new Border { Background = Brushes.White, CornerRadius = new CornerRadius(14), BorderBrush = Brush.Parse("#E1E8F0"), BorderThickness = new Thickness(1), Padding = new Thickness(24), Child = content };
    }
    private async Task EjectDisk(string volume)
    {
        var preview = await service.PreviewEjectAsync(volume);
        if (!preview.Ejectable)
        {
            message.Text = preview.UnsupportedReason ?? "Windows cannot eject this disk.";
            return;
        }
        var dialog = new Window
        {
            Title = "Safely eject disk", Width = 480, Height = 235,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(24), Spacing = 18,
                Children =
                {
                    Text($"Eject disk {preview.DiskNumber} ({preview.Name})?", 19, Ink, FontWeight.SemiBold),
                    Text($"Windows will remove the whole disk: {string.Join(", ", preview.Volumes)}. Pending cache data must be written first. Open files may make Windows refuse removal.", 14),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 12,
                        Children =
                        {
                            new Button { Content = "Cancel", Padding = new Thickness(14, 8) },
                            new Button { Content = "Eject disk", Padding = new Thickness(14, 8) }
                        }
                    }
                }
            }
        };
        var choices = ((StackPanel)dialog.Content!).Children.OfType<StackPanel>().Last().Children.OfType<Button>().ToArray();
        choices[0].Click += (_, _) => dialog.Close(false);
        choices[1].Click += (_, _) => dialog.Close(true);
        if (!await dialog.ShowDialog<bool>(this))
            return;
        var progress = new Progress<string>(value => Dispatcher.UIThread.Post(() => message.Text = value));
        var result = await service.EjectAsync(volume, progress);
        message.Text = result.RemovalObserved
            ? $"Windows removed disk {result.Disk.DiskNumber}."
            : $"Windows accepted eject of disk {result.Disk.DiskNumber}, but removal is not yet observed. Check device state before disconnecting it; affected caches remain disabled.";
        nextInventory = DateTimeOffset.MinValue;
    }

    private async Task Sample()
    {
        await Task.WhenAll(views.ToArray().Select(SampleCard));
    }
    private async Task SampleCard(Card card)
    {
        if (card.Sampling || closed)
            return;
        card.Sampling = true;
        try
        {
            var state = await service.ReadAsync(card.Volume);
            if (closed || !views.Contains(card))
                return;
            var now = DateTimeOffset.UtcNow;
            var rates = card.State is null ? null : CacheTelemetry.Between(card.State, state, now > card.Sampled ? now - card.Sampled : TimeSpan.FromTicks(1));
            card.State = state;
            card.Sampled = now;
            var performance = state.Performance;
            card.Operation.Text = (card.Busy ? $"{card.OperationName}… " : "") +
                (performance is null ? "" : $"Driver: {performance.PhaseName} · queued {performance.QueueDepth} · current request {performance.Milliseconds(performance.ActiveAgeTicks):0} ms · bypassed RAM reads {performance.BypassReads:N0}");
            bool exists = state.BudgetBytes > 0;
            card.Activity.IsVisible = exists;
            card.Badge.Text = state.RuntimeStatus;
            card.Description.Text = exists ? $"{state.BudgetBytes / 1048576:0} MiB RAM cache · {(state.UnsafeDefer ? "Fast" : "Strict")} · {state.Options?.Allocation.ToString() ?? "Legacy"} · {state.Options?.Drain.ToString() ?? "Eager"}"
                : card.Volume.HasFileSystem ? "Ready for a cache. Choose a memory budget to get started."
                : "This volume is not formatted. Format it to add a cache.";
            if (exists && !card.Volume.HasFileSystem)
                card.Description.Text += " · no file system: only Flush, Clear read cache and Remove are available";
            if (card.Volume.HasFileSystem && !card.Volume.Journaled)
                card.Description.Text += $" · {card.Volume.FileSystem} {FileSystems.NoJournalWarning}";
            // Amounts are live; hits and evictions are driver totals since Windows started.
            card.Residency.Text = $"In RAM now: read cache {state.CleanReadBytes / 1048576.0:0.0} MiB · retained writes {state.CleanWriteBytes / 1048576.0:0.0} MiB · free {state.FreeBytes / 1048576.0:0.0} MiB · oldest unwritten data {state.OldestDirtyMs / 1000.0:0.0}s · reading {rates?.ReadMiBPerSecond ?? 0:0.0} MiB/s\n" +
                $"Since Windows started: read hits {state.ReadHitPercent:0.0}% ({state.ReadHitBytes / 1073741824.0:0.00} GiB from RAM) · evicted blocks {state.Evictions:N0}";
            card.History.Add(rates?.AcceptedMiBPerSecond ?? 0, rates?.DrainedMiBPerSecond ?? 0, rates?.ReadMiBPerSecond ?? 0, rates?.CountersReset ?? true);
            card.Bucket.Update(state.CleanReadBytes, state.CleanWriteBytes, state.DirtyBytes, state.PayloadCapacity);
            // Cached reads: clean read-fill blocks plus retained drained writes, both readable from RAM.
            card.Cached.Text = $"{(state.CleanReadBytes + state.CleanWriteBytes) / 1048576.0:0.0} MB";
            card.Dirty.Text = $"{state.DirtyBytes / 1048576.0:0.0} MB";
            card.Incoming.Text = $"{rates?.AcceptedMiBPerSecond ?? 0:0.0} MB/s";
            card.Draining.Text = $"{rates?.DrainedMiBPerSecond ?? 0:0.0} MB/s";
            card.Settings.Content = exists ? "Cache settings" : "Add cache";
            card.Settings.IsEnabled = !card.Busy && state.SupportsReadWrite && card.Volume.HasFileSystem;
            card.Pause.Content = state.Enabled ? "Pause" : "Resume";
            card.Pause.IsVisible = card.Flush.IsVisible = card.Remove.IsVisible = exists;
            card.Pause.IsEnabled = card.Flush.IsEnabled = card.Remove.IsEnabled = !card.Busy;
            // Settings and Pause re-apply a configuration, which needs a file system; Flush and Remove work on any volume.
            card.Pause.IsEnabled &= card.Volume.HasFileSystem;
            card.DropClean.IsVisible = exists && state.SupportsDropClean;
            card.DropClean.IsEnabled = !card.Busy && state.CleanReadBytes + state.CleanWriteBytes > 0;
            card.Remove.IsEnabled = !card.Busy && state.SupportsRelease;
            if (state.Faulted)
                card.Description.Text = $"Disk I/O failed (0x{state.LastError:X8}). Pending writes for this volume are retained; check the disk before retrying.";
        }
        catch (Exception ex)
        {
            card.State = null;
            card.Badge.Text = "Not connected";
            card.Description.Text = "This volume is unavailable or its QueueCache filter is not responding. Refresh after reconnecting it.";
            card.Activity.IsVisible = false;
            card.Settings.IsEnabled = false;
            card.Pause.IsVisible = card.Flush.IsVisible = card.Remove.IsVisible = card.DropClean.IsVisible = false;
            card.Details.Text = ex.Message;
        }
        finally { card.Sampling = false; }
        UpdateSummary();
    }
    private void UpdateSummary()
    {
        var fresh = views.Where(v => v.State is not null && DateTimeOffset.UtcNow - v.Sampled <= TimeSpan.FromSeconds(Math.Max(3, timer.Interval.TotalSeconds * 3))).ToArray();
        var active = fresh.Count(v => v.State!.Operational);
        var memory = fresh.Aggregate(0UL, (total, v) => total + v.State!.ReservedBytes);
        summary.Text = $"{active} active {(active == 1 ? "cache" : "caches")}  ·  {memory / 1048576:0} MB reserved  ·  {views.Count} {(views.Count == 1 ? "volume" : "volumes")}";
    }
    private async Task Edit(Card card)
    {
        var current = card.State ?? throw new IOException("Driver unavailable.");
        var globalFree = current.GlobalLimitBytes - Math.Min(current.GlobalLimitBytes, current.GlobalReservedBytes);
        var available = (long)(Math.Min(globalFree, MemoryBudget.AvailableForCache()) >> 20) + (long)(current.BudgetBytes >> 20);
        available = Math.Min(MemoryBudget.MaximumMiB, available);
        if (available < 1)
            throw new IOException("No RAM is currently available for a cache. Reduce another cache or free memory first.");
        var result = await new CacheSettingsWindow(card.Volume, card.State ?? throw new IOException("Driver unavailable."), Persistent(card), (int)available).ShowDialog<CacheSettingsResult?>(this);
        if (result is null)
            return;
        message.Text = "Applying cache settings…";
        await service.SaveAsync(Letter(card), result.Configuration, result.Persistent, new Progress<string>(text => message.Text = text));
        message.Text = "Cache settings saved.";
    }
    private async Task Test(Card card, bool benchmark)
    {
        card.Workload = new();
        try
        {
            var progress = new Progress<string>(text => card.Details.Text = text);
            var report = await service.TestAsync(Letter(card), benchmark, progress, card.Workload.Token);
            card.Details.Text = string.Join("\n", report.Checks.Select(c => $"{c.Result} · {c.Name}: {c.Detail}")) + $"\nFiles: {report.Directory}";
        }
        finally { card.Workload.Dispose(); card.Workload = null; }
    }
    private static string Letter(Card card) => card.Volume.Volume;
    private bool Persistent(Card card) => card.State?.BudgetBytes is null or 0 || service.IsPersistent(card.Volume);
    // Identity, not size or label: a changed GUID, letter or disk rebuilds the cards.
    private static string Signature(IEnumerable<VolumeDescription> volumes) =>
        string.Join("|", volumes.Select(v => $"{v.Volume}/{v.VolumePath}/{v.Instance}/{v.FileSystem}"));
    internal static string Describe(VolumeDescription volume) =>
        $"{(string.IsNullOrEmpty(volume.FileSystem) ? "Unformatted" : volume.FileSystem)}  /  {volume.SizeGiB:0.##} GiB  /  disk {volume.DiskNumber} ({volume.DiskName})" +
        (volume.IsBoot || volume.IsSystem ? "  /  Windows" : "") + (volume.IsPaging ? "  /  paging file" : "");
    private Button Action(string label, Func<Task> action, Card? card = null)
    {
        var button = new Button { Content = label, Padding = new Thickness(16, 9), Margin = new Thickness(0, 0, 8, 0), CornerRadius = new CornerRadius(7) };
        button.Click += async (_, _) => await Run(action, card, label);
        return button;
    }
    private async Task Run(Func<Task> action, Card? card, string label)
    {
        if (card is null ? busy : card.Busy)
            return;
        if (card is null)
            busy = true;
        else
        {
            card.Busy = true;
            card.OperationName = label;
            card.Operation.Text = label + "…";
            foreach (var button in card.Actions)
                button.IsEnabled = false;
        }
        try
        {
            await action();
        }
        catch (OperationCanceledException) { message.Text = "Test cancelled. Cache remains running."; }
        catch (Exception ex) { message.Text = ex.Message; }
        finally
        {
            if (card is null)
                busy = false;
            else
            {
                card.Busy = false;
                card.OperationName = "";
                foreach (var button in card.Actions)
                    button.IsEnabled = true;
                card.Operation.Text = "";
            }
            if (!closed)
            {
                if (card is null)
                    await Sample();
                else
                    await SampleCard(card);
            }
        }
    }
    private static Control Metric(string label, TextBlock value)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Text(label, 11, Muted, FontWeight.SemiBold));
        panel.Children.Add(value);
        return panel;
    }
    /// <summary>Colour key using the same brushes the charts draw with.</summary>
    private static Control Legend(params (IBrush Fill, string Label)[] items)
    {
        var panel = new WrapPanel();
        foreach (var (fill, label) in items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 0, 14, 0) };
            row.Children.Add(new Border { Background = fill, Width = 11, Height = 11, CornerRadius = new CornerRadius(3), BorderBrush = Brush.Parse("#C9D6E2"), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(Text(label, 11, Muted));
            panel.Children.Add(row);
        }
        return panel;
    }
    internal static TextBlock Text(string text, double size, IBrush? brush = null, FontWeight? weight = null) => new() { Text = text, FontSize = size, Foreground = brush ?? Ink, FontWeight = weight ?? FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
    private sealed class Card(VolumeDescription volume)
    {
        public bool Sampling, Busy;
        public string OperationName = "";
        public TextBlock Operation = Text("", 13, Muted);
        public List<Button> Actions = [];
        public CancellationTokenSource? Workload;
        public VolumeDescription Volume = volume; public WriteCacheState? State; public DateTimeOffset Sampled;
        public TextBlock Badge = Text("Connecting", 13, Accent, FontWeight.SemiBold), Description = Text("", 14, Muted), Cached = Text("—", 24), Dirty = Text("—", 24), Incoming = Text("—", 24), Draining = Text("—", 24);
        public CacheOccupancy Bucket = new();
        public TextBlock Residency = Text("", 13, Muted);
        public RateHistory History = new();
        public StackPanel Activity = new() { Spacing = 18 };
        public TextBox Details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 190 };
        public Button Settings = null!, Pause = null!, Flush = null!, DropClean = null!, Remove = null!;
    }
}
