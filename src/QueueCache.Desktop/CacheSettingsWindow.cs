using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Desktop;

public sealed record CacheSettingsResult(int BudgetMiB, CachePreset Preset, bool Persistent);
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class CacheSettingsWindow : Window
{
    public CacheSettingsWindow(DiskDescription disk, WriteCacheState state, bool persistent, int availableMiB = 4096)
    {
        Title = state.BudgetBytes == 0 ? "Add cache" : "Cache settings"; Width = 500; Height = 560; CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Brushes.White;
        if (availableMiB is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(availableMiB));
        var sizes = new[] { 256, 512, 1024, 2048, 4096 }.Where(n => n <= availableMiB).ToArray();
        var initial = state.BudgetBytes == 0 ? availableMiB : Math.Min(availableMiB, (int)(state.BudgetBytes >> 20));
        var memory = new ComboBox { ItemsSource = sizes.Select(n => $"{n:N0} MiB").Append("Custom").ToArray(), SelectedIndex = Array.IndexOf(sizes, initial), HorizontalAlignment = HorizontalAlignment.Stretch };
        if (memory.SelectedIndex < 0) memory.SelectedIndex = sizes.Length;
        var custom = new NumericUpDown { Minimum = 1, Maximum = availableMiB, Value = initial, IsVisible = memory.SelectedIndex == sizes.Length };
        memory.SelectionChanged += (_, _) => custom.IsVisible = memory.SelectedIndex == sizes.Length;
        var preset = new ComboBox { ItemsSource = new[] { "Fast", "Strict" }, SelectedIndex = state.BudgetBytes == 0 || state.UnsafeDefer ? 0 : 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        var description = MainWindow.Text("", 13, MainWindow.Muted);
        void Describe() => description.Text = preset.SelectedIndex == 0 ? "Writes finish in RAM and drain to disk in the background. Application flushes may also finish in RAM." : "Application flushes wait for pending writes to reach the disk.";
        preset.SelectionChanged += (_, _) => Describe(); Describe();
        var startup = new ToggleSwitch { Content = "Start with Windows", IsChecked = persistent };
        var panel = new StackPanel { Margin = new Thickness(30), Spacing = 14 };
        panel.Children.Add(MainWindow.Text(Title!, 25, null, FontWeight.SemiBold)); panel.Children.Add(MainWindow.Text($"{string.Join(" · ", disk.Volumes)}  /  {disk.Device}  /  {disk.SizeGiB:0.##} GiB", 13, MainWindow.Muted));
        panel.Children.Add(MainWindow.Text("Write cache memory", 14, null, FontWeight.SemiBold)); panel.Children.Add(memory); panel.Children.Add(custom);
        panel.Children.Add(MainWindow.Text("Write behaviour", 14, null, FontWeight.SemiBold)); panel.Children.Add(preset); panel.Children.Add(description); panel.Children.Add(startup);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Margin = new Thickness(0, 10, 0, 0) };
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => Close(null);
        var save = new Button { Content = state.BudgetBytes == 0 ? "Create cache" : "Save changes", Background = MainWindow.Accent, Foreground = Brushes.White };
        save.Click += (_, _) => Close(new CacheSettingsResult(memory.SelectedIndex == sizes.Length ? (int)(custom.Value ?? initial) : sizes[memory.SelectedIndex], preset.SelectedIndex == 0 ? CachePreset.Fast : CachePreset.Strict, startup.IsChecked == true));
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons); Content = panel;
    }
}
