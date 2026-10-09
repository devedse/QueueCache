using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QueueCache.Desktop.Services;

namespace QueueCache.Desktop.ViewModels;

/// <summary>The navigation destinations.</summary>
public enum AppPage { Overview, Caches, VirtualDisks, Diagnostics, Settings }

/// <summary>Health at a glance, RAM in use, and every item with its one figure that matters.
/// Items that need attention come first.</summary>
public sealed partial class OverviewViewModel : ObservableObject
{
    private readonly ShellViewModel shell;

    internal OverviewViewModel(ShellViewModel shell)
    {
        this.shell = shell;
        Monitor = shell.Monitor;
        Monitor.Updated += (_, _) => Reorder();
        Monitor.Rebuilt += (_, _) => Reorder();
    }

    public DashboardMonitor Monitor { get; }
    /// <summary><see cref="VirtualDiskViewModel"/> and <see cref="VolumeViewModel"/> items, in display order.</summary>
    public ObservableCollection<object> Items { get; } = [];

    [RelayCommand] private void AddCache(VolumeViewModel volume) => shell.OpenVolume(volume, addCache: true);
    [RelayCommand] private void OpenVolume(VolumeViewModel volume) => shell.OpenVolume(volume);
    [RelayCommand] private void OpenDisk(VirtualDiskViewModel disk) => shell.OpenDisk(disk);

    private void Reorder()
    {
        var ordered = Monitor.VirtualDisks.Select(d => ((object)d, Rank(d.Health, d.HasUnsavedChanges), 0, d.Title))
            .Concat(Monitor.Volumes.Select(v => ((object)v, Rank(v.Health, false) + (v.HasCache ? 0 : 10), 1, v.Volume.Volume)))
            .OrderBy(i => i.Item2).ThenBy(i => i.Item3).ThenBy(i => i.Item4, StringComparer.OrdinalIgnoreCase)
            .Select(i => i.Item1).ToArray();
        if (ordered.SequenceEqual(Items))
            return;
        Items.Clear();
        foreach (var item in ordered)
            Items.Add(item);
    }

    private static int Rank(Health health, bool attention) => health switch
    {
        Health.Error => 0,
        Health.Unknown => 1,
        _ when attention || health == Health.Attention => 2,
        Health.Ok => 3,
        _ => 4
    };
}

/// <summary>Volumes grouped by physical disk; the selected volume's cache in detail.</summary>
public sealed partial class CachesViewModel : ObservableObject
{
    private string? selectedId;
    private bool active = true;

    internal CachesViewModel(DashboardMonitor monitor)
    {
        Monitor = monitor;
        monitor.Rebuilt += (_, _) => Rebuild();
    }

    public DashboardMonitor Monitor { get; }
    /// <summary>The list: each <see cref="DiskGroupViewModel"/> header followed by its <see cref="VolumeViewModel"/> rows.</summary>
    public ObservableCollection<object> Rows { get; } = [];

    [ObservableProperty] private VolumeViewModel? selected;

    partial void OnSelectedChanged(VolumeViewModel? oldValue, VolumeViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.MapRequested = false;
            if (!oldValue.MapRequested)
                oldValue.LayoutMap = null;
        }
        if (newValue is not null)
        {
            selectedId = newValue.Volume.VolumeId;
            newValue.MapRequested = active;
            newValue.MapSampled = default; // Read its map at the next sample.
        }
    }

    [RelayCommand] private Task Refresh() => Monitor.RefreshAsync();
    [RelayCommand] private void DismissMessage() => Monitor.Message = null;

    public void Select(VolumeViewModel volume) => Selected = volume;

    internal void SetActive(bool value)
    {
        if (active == value)
            return;
        active = value;
        if (Selected is { } volume)
        {
            volume.MapRequested = value;
            if (!volume.MapRequested)
                volume.LayoutMap = null;
            volume.MapSampled = default;
        }
    }

    private void Rebuild()
    {
        var rows = Monitor.DiskGroups.SelectMany(g => g.Volumes.Prepend<object>(g)).ToArray();
        if (!rows.SequenceEqual(Rows))
        {
            Rows.Clear();
            foreach (var row in rows)
                Rows.Add(row);
        }
        Reselect();
    }

    // A rebuilt list keeps the same volume selected; otherwise the first that is not the Windows volume.
    private void Reselect()
    {
        Selected = Monitor.Volumes.FirstOrDefault(v => v.Volume.VolumeId.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
            ?? Monitor.Volumes.FirstOrDefault(v => !v.Volume.IsBoot && !v.Volume.IsSystem) ?? Monitor.Volumes.FirstOrDefault();
    }
}

public sealed partial class VirtualDisksViewModel : ObservableObject
{
    private Guid? selectedId;

    internal VirtualDisksViewModel(DashboardMonitor monitor)
    {
        Monitor = monitor;
        monitor.Rebuilt += (_, _) => Reselect();
    }

    public DashboardMonitor Monitor { get; }

    [ObservableProperty] private VirtualDiskViewModel? selected;

    partial void OnSelectedChanged(VirtualDiskViewModel? value)
    {
        if (value is not null)
            selectedId = value.ResourceId;
    }

    [RelayCommand]
    private async Task NewDisk()
    {
        var created = await Monitor.CreateDiskAsync();
        if (created is { } id)
        {
            selectedId = id;
            Reselect();
        }
    }

    public void Select(VirtualDiskViewModel disk) => Selected = disk;

    private void Reselect() =>
        Selected = Monitor.VirtualDisks.FirstOrDefault(d => d.ResourceId == selectedId) ?? Monitor.VirtualDisks.FirstOrDefault();
}

/// <summary>Tools for checking a volume (file tests, benchmark) and driver details for support.</summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private CancellationTokenSource? workload;

    internal DiagnosticsViewModel(DashboardMonitor monitor) => Monitor = monitor;

    public DashboardMonitor Monitor { get; }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RunFileTestsCommand), nameof(RunBenchmarkCommand))] private VolumeViewModel? target;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RunFileTestsCommand), nameof(RunBenchmarkCommand), nameof(CancelCommand))] private bool isRunning;
    [ObservableProperty] private string output = "Choose a volume, then run the file tests or the benchmark. Both write a few test files to that volume and remove them afterwards.";

    private bool CanRun => Target is not null && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))] private Task RunFileTests() => RunAsync(benchmark: false);
    [RelayCommand(CanExecute = nameof(CanRun))] private Task RunBenchmark() => RunAsync(benchmark: true);
    [RelayCommand(CanExecute = nameof(IsRunning))] private void Cancel() => workload?.Cancel();

    private async Task RunAsync(bool benchmark)
    {
        var volume = Target!;
        workload = new();
        IsRunning = true;
        Output = benchmark ? $"Benchmarking {volume.Volume.Volume}…" : $"Running file tests on {volume.Volume.Volume}…";
        try
        {
            var progress = new Progress<string>(text => Output = text);
            var report = await Monitor.Caches.TestAsync(volume.Volume.Volume, benchmark, progress, workload.Token);
            Output = string.Join("\n", report.Checks.Select(c => $"{c.Result} · {c.Name}: {c.Detail}")) + $"\nFiles: {report.Directory}";
        }
        catch (OperationCanceledException)
        {
            Output = "Cancelled. The cache keeps running.";
        }
        catch (Exception ex)
        {
            Output = ex.Message;
        }
        finally
        {
            workload.Dispose();
            workload = null;
            IsRunning = false;
        }
    }
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IDesktopSettingsStore store;
    private bool loading;

    private readonly ISignInTask signIn;

    internal SettingsViewModel(IDesktopSettingsStore store, ISignInTask? signIn = null)
    {
        this.store = store;
        this.signIn = signIn ?? new MemorySignInTask(null);
        loading = true;
        try
        {
            var state = this.signIn.IsEnabled();
            startAtSignIn = state == true;
            SignInAvailable = state is not null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Xml.XmlException or System.ComponentModel.Win32Exception)
        {
            SignInAvailable = false;
        }
        Current = store.Load();
        theme = (int)Current.Theme;
        updateChoice = Math.Max(0, Array.IndexOf(DesktopSettings.UpdateChoices, Current.UpdateSeconds));
        keepRunningInTray = Current.KeepRunningInTray;
        driverTiming = Current.DriverTiming;
        callerPath = Current.CallerPath;
        loading = false;
        Version = typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    }

    public DesktopSettings Current { get; private set; }
    public IReadOnlyList<string> ThemeChoices { get; } = ["Use the Windows setting", "Light", "Dark"];
    public IReadOnlyList<string> UpdateChoices { get; } = DesktopSettings.UpdateChoices.Select(s => s switch
    {
        0.1 => "Every 0.1 seconds",
        0.5 => "Every half second",
        1 => "Every second",
        _ => $"Every {s:0} seconds"
    }).ToArray();
    public string Version { get; }

    public event EventHandler<DesktopSettings>? Changed;

    [ObservableProperty] private int theme;
    [ObservableProperty] private int updateChoice;
    [ObservableProperty] private bool keepRunningInTray;
    [ObservableProperty] private bool startAtSignIn;
    [ObservableProperty] private string? signInError;
    /// <summary>False when setup did not register the task (for example a copied build).</summary>
    public bool SignInAvailable { get; }
    public string SignInDescription => SignInAvailable
        ? "QueueCache starts in the notification area when you sign in, so its status and quick actions are there from the start."
        : "Unavailable: setup did not register the sign-in task. Reinstall QueueCache to add it.";

    partial void OnStartAtSignInChanged(bool value)
    {
        if (loading)
            return;
        try
        {
            signIn.SetEnabled(value);
            SignInError = null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SignInError = ex.Message;
        }
    }
    [ObservableProperty] private bool driverTiming;
    [ObservableProperty] private bool callerPath;

    /// <summary>What timing costs, measured on the lab VM (docs/DESKTOP_UI.md).</summary>
    public string DriverTimingDescription { get; } = "Measures how long every read and write takes inside the cache and RAM disk drivers. " +
        "The figures appear on each cache and virtual disk, and on the Diagnostics page. Applies while QueueCache runs. " +
        "Measured cost while on: small random writes into a cache's RAM up to 12% slower, small random reads from a RAM disk up to 8% slower, " +
        "large transfers barely affected (under 2% on RAM disks). Off, it costs nothing measurable.";
    public string CallerPathDescription { get; } = "On (recommended): reads already in a cache's RAM and writes that fit are answered at once, on the " +
        "program's own thread. Off: every request goes through the cache's worker thread, which is slower. Only for comparing performance.";

    partial void OnDriverTimingChanged(bool value) => Save();
    partial void OnCallerPathChanged(bool value) => Save();
    partial void OnThemeChanged(int value) => Save();
    partial void OnUpdateChoiceChanged(int value) => Save();
    partial void OnKeepRunningInTrayChanged(bool value) => Save();

    private void Save()
    {
        if (loading)
            return;
        Current = new(DesktopSettings.UpdateChoices[Math.Clamp(UpdateChoice, 0, DesktopSettings.UpdateChoices.Length - 1)], (AppTheme)Theme, KeepRunningInTray, DriverTiming, CallerPath);
        try
        {
            store.Save(Current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferences still apply for this session.
        }
        Changed?.Invoke(this, Current);
    }
}
