using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Desktop;

public sealed record CacheSettingsResult(CacheConfiguration Configuration, bool Persistent);
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class CacheSettingsWindow : Window
{
    public CacheSettingsWindow(DiskDescription disk, WriteCacheState state, bool persistent, int availableMiB = 4096)
    {
        Icon = AppBranding.CreateIcon();
        Title = state.BudgetBytes == 0 ? "Add cache" : "Cache settings";
        Width = 620; Height = 780; MinWidth = 540; MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Brushes.White;
        if (availableMiB is < 1 or > MemoryBudget.MaximumMiB) throw new ArgumentOutOfRangeException(nameof(availableMiB));
        var options = state.Options ?? new();
        var sizes = new[] { 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536 }.Where(n => n <= availableMiB).ToArray();
        var initial = state.BudgetBytes == 0 ? Math.Min(4096, availableMiB) : Math.Min(availableMiB, (int)(state.BudgetBytes >> 20));
        var memory = Choice(sizes.Select(n => $"{n:N0} MiB").Append("Custom").ToArray(), Array.IndexOf(sizes, initial));
        if (memory.SelectedIndex < 0) memory.SelectedIndex = sizes.Length;
        var custom = Number(1, availableMiB, initial); custom.Name = "CustomBudget";
        custom.IsVisible = memory.SelectedIndex == sizes.Length;
        memory.SelectionChanged += (_, _) => custom.IsVisible = memory.SelectedIndex == sizes.Length;
        var allocation = Choice(["Automatic sharing", "Fixed read / write split"], (int)options.Allocation);
        var write = Number(0, 100, options.WritePercent);
        var split = MainWindow.Text("", 13, MainWindow.Muted);
        void DescribeSplit()
        {
            write.IsEnabled = allocation.SelectedIndex == 1;
            split.Text = allocation.SelectedIndex == 0 ? "Reads and writes share one pool. Clean data yields space to incoming writes."
                : $"{100 - (int)(write.Value ?? 50)}% read / {(int)(write.Value ?? 50)}% write. 0% write gives read-only caching; 100% gives write-only caching.";
        }
        allocation.SelectionChanged += (_, _) => DescribeSplit(); write.ValueChanged += (_, _) => DescribeSplit(); DescribeSplit();
        var retain = new ToggleSwitch { Content = "Keep drained writes cached for reads", IsChecked = options.RetainWrites };
        var promote = new ToggleSwitch { Content = "Move retained writes into the read quota when read", IsChecked = options.PromoteOnRead };
        var preset = Choice(["Fast", "Strict"], state.BudgetBytes == 0 || state.UnsafeDefer ? 0 : 1);
        var behaviour = MainWindow.Text("", 13, MainWindow.Muted);
        void DescribePreset() => behaviour.Text = preset.SelectedIndex == 0 ? "Writes and application flushes can finish in RAM. Flush now always drains to disk." : "Application flushes and write-through writes wait for disk.";
        preset.SelectionChanged += (_, _) => DescribePreset(); DescribePreset();
        // Background draining. Which tuning settings actually affect the driver depends on the
        // selected algorithm (QcShouldDrain in driver/qcache/cachepolicy.h):
        //   Eager    — always drains while dirty; watermarks, maximum age and idle interval are unused.
        //   Balanced — watermarks + maximum age.
        //   Idle     — watermarks + maximum age + write-idle interval.
        // Batch size and parallelism describe how a drain is issued and apply to every algorithm.
        var algorithm = Choice(["Eager", "Balanced", "Idle"], (int)options.Drain);
        var low = Number(0, 99, options.LowPercent); var high = Number(1, 100, options.HighPercent);
        var parallel = Number(1, 4, options.Parallelism);
        var age = Number(10, 300000, options.MaxDirtyAgeMs); var idle = Number(10, 60000, options.IdleMs);
        var batch = Choice(["4 KiB", "64 KiB", "256 KiB", "512 KiB", "1024 KiB"], Array.IndexOf(new[] { 4, 64, 256, 512, 1024 }, options.BatchKiB));
        // Preserve CLI batch sizes not present in the suggested list.
        var batchCustom = Number(4, 1024, options.BatchKiB);
        batchCustom.IsVisible = batch.SelectedIndex < 0;
        batch.SelectionChanged += (_, _) => batchCustom.IsVisible = batch.SelectedIndex < 0;
        var startup = new ToggleSwitch { Content = "Start with Windows", IsChecked = persistent };
        var panel = new StackPanel { Margin = new Thickness(28), Spacing = 12 };
        panel.Children.Add(MainWindow.Text(Title!, 25, null, FontWeight.SemiBold));
        panel.Children.Add(MainWindow.Text($"{string.Join(" · ", disk.Volumes)}  /  {disk.Device}  /  {disk.SizeGiB:0.##} GiB", 13, MainWindow.Muted));
        Add(panel, "Total RAM budget", memory, "Total nonpaged RAM reserved for this disk, including block index, descriptors and staging buffers, so usable payload is slightly smaller.");
        panel.Children.Add(custom);
        Add(panel, "Memory allocation", allocation, "Automatic: reads and writes share the whole payload pool and clean blocks yield space to writes. Fixed: the write share below is reserved for pending and retained writes.");
        Add(panel, "Write share (%)", write, "Fixed allocation only: percentage of the payload pool reserved for writes. This is also the pool the draining percentages below are measured against.");
        panel.Children.Add(split);
        panel.Children.Add(retain); panel.Children.Add(promote);
        Add(panel, "Write behaviour", preset, "Fast lets application flushes and write-through writes complete while data is still in volatile RAM. Strict makes them wait for the disk. Independent of the draining algorithm.");
        panel.Children.Add(behaviour);
        var draining = new StackPanel { Spacing = 10 };
        Add(draining, "Algorithm", algorithm, "Decides when pending (dirty) writes are sent to the disk in the background. It never affects cached reads: clean read data is evicted, never drained.");
        var describe = MainWindow.Text("", 13, MainWindow.Muted); draining.Children.Add(describe);
        // Watermarks and maximum age: Balanced and Idle only.
        var watermarks = Field("Start pressure draining at (%)", PressureHint + " Above this fill level, background draining runs until the stop level is reached.", high,
            Field("Stop pressure draining at (%)", PressureHint + " Pressure draining stops here, so writes below this level can keep absorbing overwrites in RAM.", low),
            Field("Maximum dirty age before draining starts (ms)", "Drain once the oldest pending write has waited this long, even when the pool is far below the start level. A scheduling trigger only: slow or failing storage can still take longer.", age));
        // Write-idle interval: Idle only.
        var idleField = Field("Write-idle interval (ms)", "Idle algorithm only: drain after this long with no newly cached write, so bursts stay in RAM and the disk catches up during pauses.", idle);
        // Always relevant: how each drain is issued to the disk.
        draining.Children.Add(watermarks); draining.Children.Add(idleField);
        draining.Children.Add(Field("Maximum adjacent-write batch", "Pending 4 KiB blocks that are adjacent on disk are gathered into one lower write up to this size. Larger batches favour sequential throughput; budgets under 16 MiB cap each staging buffer at 64 KiB.", batch, batchCustom));
        draining.Children.Add(Field("Maximum simultaneous disk writes", "How many gathered writes may be outstanding to the disk at once. Higher values help random draining on fast devices; overlapping versions of a block stay ordered regardless.", parallel));
        draining.Children.Add(MainWindow.Text("Age is a scheduling trigger, not a durability deadline. Every algorithm yields to explicit flushes, shutdown barriers and writers waiting for capacity.", 12, MainWindow.Muted));
        void DescribeDraining()
        {
            describe.Text = algorithm.SelectedIndex switch
            {
                0 => "Eager: each pending write starts draining to disk as soon as it is accepted. Smallest window of volatile data and the most disk traffic; repeated overwrites are still coalesced in RAM, but watermarks, maximum age and the idle interval are ignored.",
                1 => "Balanced: pending writes stay in RAM until the write pool reaches the start watermark or the oldest pending block exceeds its maximum age; draining then continues down to the stop watermark. Absorbs repeated overwrites and write bursts, at the cost of more data waiting in volatile RAM.",
                _ => "Idle: the Balanced triggers, plus draining whenever no new cached write has arrived for the write-idle interval. Keeps the disk quiet during a burst and catches up between bursts.",
            };
            watermarks.IsVisible = algorithm.SelectedIndex != 0;
            idleField.IsVisible = algorithm.SelectedIndex == 2;
        }
        algorithm.SelectionChanged += (_, _) => DescribeDraining(); DescribeDraining();
        panel.Children.Add(MainWindow.Text("Background draining", 13, null, FontWeight.SemiBold));
        panel.Children.Add(new Border { BorderBrush = Brush.Parse("#DDDDDD"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(14), Child = draining });
        panel.Children.Add(startup);
        var error = MainWindow.Text("", 13, Brush.Parse("#B33C36")); panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => Close(null);
        var save = new Button { Content = state.BudgetBytes == 0 ? "Create cache" : "Save changes", Background = MainWindow.Accent, Foreground = Brushes.White };
        save.Click += (_, _) =>
        {
            try
            {
                var config = new CacheConfiguration(memory.SelectedIndex == sizes.Length ? (int)(custom.Value ?? initial) : sizes[memory.SelectedIndex],
                    preset.SelectedIndex == 0 ? CachePreset.Fast : CachePreset.Strict, state.BudgetBytes == 0 || state.Enabled)
                {
                    Options = new((CacheAllocation)allocation.SelectedIndex, (int)(write.Value ?? 50), retain.IsChecked == true,
                        promote.IsChecked == true, (DrainAlgorithm)algorithm.SelectedIndex, (int)(low.Value ?? 40), (int)(high.Value ?? 80),
                        (int)(age.Value ?? 5000), (int)(idle.Value ?? 250),
                        batch.SelectedIndex < 0 ? (int)(batchCustom.Value ?? options.BatchKiB) : new[] { 4, 64, 256, 512, 1024 }[batch.SelectedIndex], (int)(parallel.Value ?? 1))
                };
                config.Validate(true); Close(new CacheSettingsResult(config, startup.IsChecked == true));
            }
            catch (ArgumentException ex) { error.Text = ex.Message; }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        buttons.Margin = new Thickness(28, 14);
        var layout = new DockPanel(); DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        layout.Children.Add(new ScrollViewer { Content = panel }); Content = layout;
    }
    private static ComboBox Choice(string[] values, int selected) => new() { ItemsSource = values, SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Stretch };
    private static NumericUpDown Number(int min, int max, int value) => new() { Minimum = min, Maximum = max, Value = value, Increment = 1 };
    // Shared wording: the draining percentages measure the write pool only, never cached reads.
    private const string PressureHint = "Percentage of the write pool (the whole budget under Automatic allocation, otherwise the fixed write share) held as pending writes. Cached read data is not counted and is never drained.";
    private static void Add(StackPanel panel, string label, Control control, string? hint = null)
    {
        var text = MainWindow.Text(label, 13, null, FontWeight.SemiBold);
        if (hint is null) panel.Children.Add(text);
        else
        {
            var mark = MainWindow.Text("?", 11, Brushes.White, FontWeight.Bold);
            mark.TextAlignment = TextAlignment.Center; mark.Width = 15;
            var badge = new Border { Background = MainWindow.Accent, CornerRadius = new CornerRadius(8), Height = 16, VerticalAlignment = VerticalAlignment.Center, Child = mark, Cursor = new(StandardCursorType.Help) };
            ToolTip.SetTip(badge, new TextBlock { Text = hint, MaxWidth = 380, TextWrapping = TextWrapping.Wrap });
            ToolTip.SetShowDelay(badge, 150);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(text); row.Children.Add(badge); panel.Children.Add(row);
        }
        panel.Children.Add(control);
    }
    /// <summary>One labelled setting with hover help, optionally grouped with related controls so they show and hide together.</summary>
    private static StackPanel Field(string label, string? hint, Control control, params Control[] more)
    {
        var group = new StackPanel { Spacing = 10 };
        Add(group, label, control, hint);
        foreach (var extra in more) group.Children.Add(extra);
        return group;
    }
}
