using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using QueueCache.Desktop.Services;
using QueueCache.Desktop.ViewModels;
using QueueCache.Desktop.Views;
using QueueCache.Operations.ManagedDisks;
using static Test;

/// <summary>Every page and dialog renders in light and dark from fixture data; screenshots go to the output folder for review.</summary>
internal static class ViewTests
{
    public static void Run(string output)
    {
        foreach (var (variant, name) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            RenderShell(output, name);
            RenderDialogs(output, name);
        }
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        Console.WriteLine("View contracts passed; screenshots in " + Path.GetFullPath(output));
    }

    private static void RenderShell(string output, string theme)
    {
        var volumes = new VolumeFixture { FaultStick = theme == "dark" };
        var dialogs = new FakeDialogs();
        var disks = DiskFixture.Sample();
        var now = DateTimeOffset.UtcNow;
        var shell = new ShellViewModel(volumes, disks, dialogs, new MemorySettingsStore(), () => 8UL << 30, () => new HashSet<char>(),
            _ => new VolumeSpace(2UL << 30, 1300UL << 20), () => now);
        var window = new MainWindow { DataContext = shell, Width = 1180, Height = 820 };
        window.Show();
        Settle(shell.Monitor.RefreshAsync());
        // A few samples so the activity chart has a line.
        for (var i = 0; i < 3; i++)
            Settle(shell.Monitor.SampleAsync());
        Check(window.Icon is not null, $"{theme}: the window icon is embedded");

        Show(window, shell, AppPage.Overview);
        Check(Named<Control>(window, "HealthCard").IsEffectivelyVisible && Named<Control>(window, "RamCard").IsEffectivelyVisible, $"{theme}: the overview opens with health and RAM");
        Check(Texts(window).Contains("I: Builds") && Texts(window).Contains("Save now") && Texts(window).Contains("Add cache"), $"{theme}: the overview lists disks and volumes with their main action");
        Save(window, output, $"overview-{theme}");

        Show(window, shell, AppPage.Caches);
        var list = Named<ListBox>(window, "VolumeList");
        var headers = list.GetRealizedContainers().Where(c => c.DataContext is DiskGroupViewModel).ToArray();
        Check(headers.Length == 2 && headers.All(h => !h.IsEnabled && !h.Focusable), $"{theme}: disk headers in the volume list are not selectable");
        shell.Caches.Select(shell.Monitor.Volumes.Single(v => v.Volume.Volume == (theme == "dark" ? "S:" : "Q:")));
        Dispatcher.UIThread.RunJobs();
        Check(Named<Button>(window, "EditButton").IsEffectivelyVisible && Texts(window).Contains("What is in RAM"),
            $"{theme}: the selected volume shows its actions and contents");
        if (theme == "dark")
            Check(Named<FAInfoBar>(window, "ProblemBar").IsOpen, "dark: a failing cache shows its problem with Retry");
        Save(window, output, $"caches-{theme}");

        Show(window, shell, AppPage.VirtualDisks);
        shell.VirtualDisks.Select(shell.Monitor.VirtualDisks.Single(d => d.IsImage));
        Dispatcher.UIThread.RunJobs();
        Check(Named<Button>(window, "SaveButton").IsEffectivelyVisible && !Named<Button>(window, "StartButton").IsEffectivelyVisible &&
              Named<Control>(window, "FactsCard").IsEffectivelyVisible, $"{theme}: a running image offers Save now, not Start, and lists its details");
        Save(window, output, $"virtual-disks-{theme}");

        // A busy RAM disk: activity, totals, Direct share and (in dark) driver timing.
        for (var i = 0; i < 20; i++)
        {
            now += TimeSpan.FromSeconds(1);
            var ram = disks.Records[1];
            var wave = (ulong)(400 + 300 * Math.Sin(i / 3.0));
            disks.Records[1] = ram with
            {
                Native = ram.Native! with { ReadBytes = ram.Native.ReadBytes + (wave << 18), WriteBytes = ram.Native.WriteBytes + (wave << 17),
                    Flags = theme == "dark" ? ram.Native.Flags | QueueCache.Management.RamDiskFlags.Timing : ram.Native.Flags },
                Direct = ram.Direct! with { ReadBytes = ram.Direct.ReadBytes + (wave << 20), WriteBytes = ram.Direct.WriteBytes + (wave << 19) },
                Statistics = ram.Statistics! with { ReadRequests = ram.Statistics.ReadRequests + wave * 40, TimedReads = 120_000, ReadTicks = 380_000,
                    MaxReadTicks = 410, TimedWrites = 61_000, WriteTicks = 290_000, MaxWriteTicks = 880 }
            };
            Settle(shell.Monitor.SampleVirtualDisksAsync());
        }
        shell.VirtualDisks.Select(shell.Monitor.VirtualDisks.Single(d => d.IsRamDisk));
        Dispatcher.UIThread.RunJobs();
        Check(Named<Control>(window, "ActivityCard").IsEffectivelyVisible && Named<Control>(window, "SpaceCard").IsEffectivelyVisible &&
              Named<Control>(window, "TimingCard").IsEffectivelyVisible == (theme == "dark"),
            $"{theme}: a running RAM disk shows its space, activity and (when on) driver timing");
        Save(window, output, $"ram-disk-{theme}");

        Show(window, shell, AppPage.Diagnostics);
        Save(window, output, $"diagnostics-{theme}");
        Show(window, shell, AppPage.Settings);
        Check(Named<ComboBox>(window, "UpdateBox").SelectedIndex == 1, $"{theme}: Settings shows the update interval");
        Save(window, output, $"settings-{theme}");
        Named<FASettingsExpander>(window, "AdvancedSettings").IsExpanded = true;
        Show(window, shell, AppPage.Settings);
        Check(Named<ToggleSwitch>(window, "TimingToggle").IsChecked == false && Named<ToggleSwitch>(window, "CallerPathToggle").IsChecked == true,
            $"{theme}: Advanced settings start with timing off and the caller path on");
        Save(window, output, $"settings-advanced-{theme}");

        // The navigation and the shell agree in both directions.
        var navigation = Named<FANavigationView>(window, "Navigation");
        Check(navigation.SelectedItem is FANavigationViewItem { Tag: "Settings" }, $"{theme}: the selected page is highlighted in the navigation");
        navigation.SelectedItem = navigation.MenuItems.OfType<FANavigationViewItem>().Single(i => Equals(i.Tag, "Caches"));
        Dispatcher.UIThread.RunJobs();
        Check(shell.Page == AppPage.Caches, $"{theme}: choosing a navigation item opens its page");

        // Narrow window: the navigation collapses and the pages still fit.
        window.Width = 760;
        Show(window, shell, AppPage.Overview);
        Save(window, output, $"overview-narrow-{theme}");

        // A destructive confirmation as a content dialog: Cancel is the default, the destructive button is marked.
        var service = new DialogService(() => window);
        var pending = service.ConfirmAsync(new("Stop X: Scratch?", "X: is a RAM disk. Stopping it permanently erases everything on it.",
            [new("discard", "Erase and stop", ChoiceKind.Destructive)]));
        Dispatcher.UIThread.RunJobs();
        var dialog = window.GetVisualDescendants().OfType<FAContentDialog>().Single();
        Check(dialog.DefaultButton == FAContentDialogButton.Close && dialog.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "PrimaryButton" && b.Classes.Contains("danger")),
            $"{theme}: a destructive confirmation defaults to Cancel and marks the destructive button");
        Save(window, output, $"confirm-{theme}");
        dialog.Hide(FAContentDialogResult.Primary);
        Check(Settle(pending) == "discard", $"{theme}: the confirmation returns the chosen action");

        shell.Stop();
        window.Close();
    }

    private static void RenderDialogs(string output, string theme)
    {
        var volumes = new VolumeFixture();
        var settings = new CacheSettingsWindow { DataContext = new CacheSettingsViewModel(volumes.Volumes[1], volumes.State, true, 8192) };
        settings.Show();
        Dispatcher.UIThread.RunJobs();
        Check(Named<RadioButton>(settings, "FastOption").IsChecked == true && !Named<Control>(settings, "CustomBudget").IsEffectivelyVisible,
            $"{theme}: cache settings show the RAM size and Fast or Strict first");
        Save(settings, output, $"cache-settings-{theme}");
        Named<Expander>(settings, "AdvancedExpander").IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        Check(Named<Control>(settings, "IdleInterval").IsEffectivelyVisible && !Named<Control>(settings, "WriteShare").IsEffectivelyVisible,
            $"{theme}: advanced tuning shows only what the chosen drain uses");
        Save(settings, output, $"cache-settings-advanced-{theme}");
        settings.Close();

        var create = new CreateDiskWindow { DataContext = new CreateDiskViewModel(new DiskFixture(), new HashSet<char> { 'C' }) };
        create.Show();
        Settle(((CreateDiskViewModel)create.DataContext!).LoadAsync());
        Check(!Named<Control>(create, "DiskImagePath").IsEffectivelyVisible && Named<Button>(create, "CreateManagedDisk").IsEnabled,
            $"{theme}: a RAM disk asks only for size, letter and name");
        Save(create, output, $"new-disk-{theme}");
        Named<RadioButton>(create, "ImageOption").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Check(Named<Control>(create, "DiskImagePath").IsEffectivelyVisible && Named<Control>(create, "DiskCheckpointDirectory").IsEffectivelyVisible,
            $"{theme}: an image in RAM asks for its file and the folder for saved copies");
        Save(create, output, $"new-disk-image-{theme}");
        create.Close();

        var disks = DiskFixture.Sample();
        var export = new DiskActionWindow { DataContext = new DiskActionViewModel(disks, disks.Records[0], ManagedDiskAction.Export) };
        export.Show();
        Dispatcher.UIThread.RunJobs();
        Check(Named<Control>(export, "ActionPath").IsEffectivelyVisible && Named<CheckBox>(export, "ActionCommit").IsChecked == false,
            $"{theme}: Export asks for a path and keeps the startup image by default");
        Save(export, output, $"export-{theme}");
        export.Close();
    }

    private static void Show(Window window, ShellViewModel shell, AppPage page)
    {
        shell.Page = page;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) ?? throw new Exception($"FAIL: no {typeof(T).Name} named {name}");

    private static HashSet<string> Texts(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Text is not null).Select(t => t.Text!).ToHashSet();

    private static void Save(Window window, string output, string name)
    {
        // Let transitions (navigation indicator, expanders) finish before the capture.
        for (var i = 0; i < 60; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        using var frame = window.CaptureRenderedFrame() ?? throw new Exception("No rendered frame for " + name);
        frame.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
    }
}
