using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using QueueCache.Management;
using QueueCache.Operations;
using System.Runtime.Versioning;

namespace QueueCache.Desktop;

[SupportedOSPlatform("windows")]
public sealed class MainWindow : Window
{
    internal static readonly IBrush Ink = Brush.Parse("#172B42"), Muted = Brush.Parse("#66788A"), Accent = Brush.Parse("#087F8C");
    private readonly StackPanel cards = new() { Spacing = 16 };
    private readonly TextBlock message = Text("Discovering your disks…", 14, Muted), summary = Text("Your storage, accelerated.", 16, Muted);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<Card> views = [];
    private bool busy, sampling, closed;
    private int ticks;
    private CancellationTokenSource? workload;
    private readonly ICacheTaskService service;
    public MainWindow() : this(new WindowsCacheTaskService()) { }
    public MainWindow(ICacheTaskService service)
    {
        this.service = service;
        Title = "QueueCache"; Width = 1080; Height = 790; MinWidth = 820; MinHeight = 620;
        Background = Brush.Parse("#F3F6FA"); FontFamily = new FontFamily("Segoe UI");
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 24) };
        var brand = new StackPanel { Spacing = 7 }; brand.Children.Add(Text("QueueCache", 30, Ink, FontWeight.SemiBold)); brand.Children.Add(summary); heading.Children.Add(brand);
        var refresh = Action("Refresh disks", Refresh); Grid.SetColumn(refresh, 1); heading.Children.Add(refresh);
        var body = new StackPanel { Margin = new Thickness(36), Spacing = 12 };
        body.Children.Add(heading); body.Children.Add(Text("DISKS & CACHES", 12, Muted, FontWeight.SemiBold)); body.Children.Add(cards); body.Children.Add(message);
        Content = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Opened += async (_, _) => { await Run(Refresh); timer.Start(); };
        timer.Tick += async (_, _) =>
        {
            if (sampling || closed) return; sampling = true;
            try { if (++ticks % 10 == 0 && !busy) await Refresh(); else await Sample(); }
            catch (Exception ex) { message.Text = ex.Message; }
            finally { sampling = false; }
        };
        Closing += (_, e) => { if (busy) { e.Cancel = true; message.Text = "Finishing the current operation…"; } else { closed = true; timer.Stop(); } };
    }
    private async Task Refresh()
    {
        var disks = await service.ListAsync(); if (closed) return;
        var signature = string.Join("|", disks.Select(d => d.Instance + string.Join(",", d.Volumes)));
        if (signature != string.Join("|", views.Select(v => v.Disk.Instance + string.Join(",", v.Disk.Volumes))))
        {
            cards.Children.Clear(); views.Clear();
            foreach (var disk in disks) { var card = new Card(disk); views.Add(card); cards.Children.Add(BuildCard(card)); }
        }
        await Sample(); message.Text = disks.Count == 0 ? "No disks found." : "";
    }
    private Control BuildCard(Card card)
    {
        var content = new StackPanel { Spacing = 18 };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var name = new StackPanel { Spacing = 5 };
        name.Children.Add(Text(card.Disk.Volumes.Length == 0 ? card.Disk.Name : string.Join(" · ", card.Disk.Volumes), 23, Ink, FontWeight.SemiBold));
        name.Children.Add(Text($"{card.Disk.Name}  /  {card.Disk.Device}  /  {card.Disk.SizeGiB:0.##} GiB", 13, Muted));
        heading.Children.Add(name); Grid.SetColumn(card.Badge, 1); heading.Children.Add(card.Badge); content.Children.Add(heading);
        content.Children.Add(card.Description); card.Activity.Children.Add(card.Bucket);
        var metrics = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        metrics.Children.Add(Metric("PENDING WRITES", card.Dirty)); var incoming = Metric("INCOMING", card.Incoming); Grid.SetColumn(incoming, 1); metrics.Children.Add(incoming);
        var draining = Metric("WRITING TO DISK", card.Draining); Grid.SetColumn(draining, 2); metrics.Children.Add(draining); card.Activity.Children.Add(metrics); content.Children.Add(card.Activity);
        var actions = new WrapPanel();
        card.Settings = Action("Add cache", () => Edit(card)); actions.Children.Add(card.Settings);
        card.Settings.Background = Accent; card.Settings.Foreground = Brushes.White;
        card.Pause = Action("Pause", () => service.SetEnabledAsync(Volume(card), !(card.State?.Enabled ?? false), Persistent(card))); actions.Children.Add(card.Pause);
        card.Flush = Action("Flush now", () => service.FlushAsync(card.Disk)); actions.Children.Add(card.Flush);
        card.Remove = Action("Remove cache", async () => { message.Text = "Draining and removing cache…"; await service.RemoveAsync(Volume(card)); }); actions.Children.Add(card.Remove); content.Children.Add(actions);
        var details = new Expander { Header = "Diagnostics" }; var diagnostics = new StackPanel { Spacing = 10 }; var tools = new WrapPanel();
        tools.Children.Add(Action("File tests", () => Test(card, false))); tools.Children.Add(Action("Benchmark", () => Test(card, true)));
        var cancel = new Button { Content = "Cancel test" }; cancel.Click += (_, _) => workload?.Cancel(); tools.Children.Add(cancel);
        diagnostics.Children.Add(tools); diagnostics.Children.Add(card.Details); details.Content = diagnostics; content.Children.Add(details);
        return new Border { Background = Brushes.White, CornerRadius = new CornerRadius(14), BorderBrush = Brush.Parse("#E1E8F0"), BorderThickness = new Thickness(1), Padding = new Thickness(24), Child = content };
    }
    private async Task Sample()
    {
        int active = 0; ulong memory = 0;
        foreach (var card in views.ToArray())
        {
            try
            {
                var state = await service.ReadAsync(card.Disk); if (closed) return;
                var now = DateTimeOffset.UtcNow; var rates = card.State is null ? null : CacheTelemetry.Between(card.State, state, now - card.Sampled);
                card.State = state; card.Sampled = now; memory += state.ReservedBytes; if (state.Enabled) active++;
                bool exists = state.BudgetBytes > 0;
                card.Activity.IsVisible = exists;
                card.Badge.Text = state.Faulted ? "Needs attention" : state.Draining ? "Draining" : state.Enabled ? "Active" : exists ? "Paused" : "Available";
                card.Description.Text = exists ? $"{state.BudgetBytes / 1048576:0} MB write cache · {(state.UnsafeDefer ? "Fast" : "Strict")} behaviour" : "Ready for a cache. Choose a memory budget to get started.";
                card.Bucket.Value = state.PayloadCapacity == 0 ? 0 : 100.0 * state.DirtyBytes / state.PayloadCapacity;
                card.Dirty.Text = $"{state.DirtyBytes / 1048576.0:0.0} MB"; card.Incoming.Text = $"{rates?.AcceptedMiBPerSecond ?? 0:0.0} MB/s"; card.Draining.Text = $"{rates?.DrainedMiBPerSecond ?? 0:0.0} MB/s";
                card.Settings.Content = exists ? "Cache settings" : "Add cache"; card.Settings.IsEnabled = !busy && card.Disk.Volumes.Length > 0;
                card.Pause.Content = state.Enabled ? "Pause" : "Resume"; card.Pause.IsVisible = card.Flush.IsVisible = card.Remove.IsVisible = exists;
                card.Pause.IsEnabled = card.Flush.IsEnabled = card.Remove.IsEnabled = !busy;
                card.Remove.IsEnabled = !busy && state.SupportsRelease;
                if (state.Faulted) card.Description.Text = $"Disk I/O failed (0x{state.LastError:X8}). Pending writes are retained; check the disk before retrying.";
            }
            catch (Exception ex)
            {
                card.State = null; card.Badge.Text = "Not connected"; card.Description.Text = "Driver unavailable. Finish installation and restart Windows, then refresh.";
                card.Settings.IsEnabled = false; card.Pause.IsVisible = card.Flush.IsVisible = card.Remove.IsVisible = false; card.Details.Text = ex.Message;
            }
        }
        summary.Text = $"{active} active {(active == 1 ? "cache" : "caches")}  ·  {memory / 1048576:0} MB reserved  ·  {views.Count} disks";
    }
    private async Task Edit(Card card)
    {
        var available = 4096 - views.Where(v => v != card).Sum(v => (long)((v.State?.BudgetBytes ?? 0) >> 20));
        if (available < 1) throw new IOException("All 4 GiB of shared cache memory is allocated. Reduce or remove another cache first.");
        var result = await new CacheSettingsWindow(card.Disk, card.State ?? throw new IOException("Driver unavailable."), Persistent(card), (int)available).ShowDialog<CacheSettingsResult?>(this);
        if (result is null) return; message.Text = "Applying cache settings…";
        await service.SaveAsync(Volume(card), new(result.BudgetMiB, result.Preset, card.State!.BudgetBytes == 0 || card.State.Enabled), result.Persistent, new Progress<string>(text => message.Text = text));
        message.Text = "Cache settings saved.";
    }
    private async Task Test(Card card, bool benchmark)
    {
        workload = new();
        try
        {
            var progress = new Progress<string>(text => card.Details.Text = text);
            var report = await service.TestAsync(Volume(card), benchmark, progress, workload.Token);
            card.Details.Text = string.Join("\n", report.Checks.Select(c => $"{c.Result} · {c.Name}: {c.Detail}")) + $"\nFiles: {report.Directory}";
        }
        finally { workload.Dispose(); workload = null; }
    }
    private static string Volume(Card card) => card.Disk.Volumes.FirstOrDefault() ?? throw new IOException("Create an NTFS volume first.");
    private bool Persistent(Card card) => card.State?.BudgetBytes is null or 0 || service.IsPersistent(card.Disk);
    private Button Action(string label, Func<Task> action)
    {
        var button = new Button { Content = label, Padding = new Thickness(16, 9), Margin = new Thickness(0, 0, 8, 0), CornerRadius = new CornerRadius(7) }; button.Click += async (_, _) => await Run(action); return button;
    }
    private async Task Run(Func<Task> action)
    {
        if (busy) return; busy = true;
        try { await action(); }
        catch (OperationCanceledException) { message.Text = "Test cancelled. Cache remains running."; }
        catch (Exception ex) { message.Text = ex.Message; }
        finally { busy = false; if (!closed) await Sample(); }
    }
    private static Control Metric(string label, TextBlock value) { var panel = new StackPanel { Spacing = 6 }; panel.Children.Add(Text(label, 11, Muted, FontWeight.SemiBold)); panel.Children.Add(value); return panel; }
    internal static TextBlock Text(string text, double size, IBrush? brush = null, FontWeight? weight = null) => new() { Text = text, FontSize = size, Foreground = brush ?? Ink, FontWeight = weight ?? FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
    private sealed class Card(DiskDescription disk)
    {
        public DiskDescription Disk = disk; public WriteCacheState? State; public DateTimeOffset Sampled;
        public TextBlock Badge = Text("Connecting", 13, Accent, FontWeight.SemiBold), Description = Text("", 14, Muted), Dirty = Text("—", 24), Incoming = Text("—", 24), Draining = Text("—", 24);
        public ProgressBar Bucket = new() { Minimum = 0, Maximum = 100, Height = 12, Foreground = Accent, Background = Brush.Parse("#E8F1F2") };
        public StackPanel Activity = new() { Spacing = 18 };
        public TextBox Details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 190 };
        public Button Settings = null!, Pause = null!, Flush = null!, Remove = null!;
    }
}
