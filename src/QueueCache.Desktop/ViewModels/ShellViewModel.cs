using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QueueCache.Desktop.Services;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Desktop.ViewModels;

/// <summary>The window's navigation, the update timer, and the notification-area actions.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly DispatcherTimer timer = new();

    public ShellViewModel(ICacheTaskService caches, IManagedDiskService disks, IDialogService dialogs, IDesktopSettingsStore settings,
        Func<ulong>? availableRam = null, Func<IReadOnlySet<char>>? usedLetters = null)
    {
        Monitor = new DashboardMonitor(caches, disks, dialogs, availableRam: availableRam, usedLetters: usedLetters);
        Settings = new SettingsViewModel(settings);
        Overview = new OverviewViewModel(this);
        Caches = new CachesViewModel(Monitor);
        VirtualDisks = new VirtualDisksViewModel(Monitor);
        Diagnostics = new DiagnosticsViewModel(Monitor);
        Monitor.Interval = timer.Interval = TimeSpan.FromSeconds(Settings.Current.UpdateSeconds);
        Settings.Changed += (_, current) => Monitor.Interval = timer.Interval = TimeSpan.FromSeconds(current.UpdateSeconds);
        timer.Tick += async (_, _) => await Monitor.TickAsync();
        currentPage = Overview;
    }

    public DashboardMonitor Monitor { get; }
    public OverviewViewModel Overview { get; }
    public CachesViewModel Caches { get; }
    public VirtualDisksViewModel VirtualDisks { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty] private AppPage page;
    [ObservableProperty] private object currentPage;

    partial void OnPageChanged(AppPage value) => CurrentPage = value switch
    {
        AppPage.Caches => Caches,
        AppPage.VirtualDisks => VirtualDisks,
        AppPage.Diagnostics => Diagnostics,
        AppPage.Settings => Settings,
        _ => Overview
    };

    /// <summary>Starts live updates and the first discovery (the window opened).</summary>
    public async Task StartAsync()
    {
        timer.Start();
        await Monitor.RefreshAsync();
    }

    public void Stop()
    {
        timer.Stop();
        Monitor.Close();
    }

    /// <summary>True while an operation is running that the window must not be closed during.</summary>
    public bool HasRunningOperation => Monitor.GlobalBusy || Monitor.Volumes.Any(v => v.IsBusy) || Monitor.VirtualDisks.Any(d => d.IsBusy);

    internal void OpenVolume(VolumeViewModel volume, bool addCache = false)
    {
        Caches.Select(volume);
        Page = AppPage.Caches;
        if (addCache && volume.CanEdit)
            volume.EditCacheCommand.Execute(null);
    }

    internal void OpenDisk(VirtualDiskViewModel disk)
    {
        VirtualDisks.Select(disk);
        Page = AppPage.VirtualDisks;
    }

    /// <summary>Writes every cache's pending data to disk now.</summary>
    [RelayCommand]
    private async Task FlushAll()
    {
        foreach (var volume in Monitor.Volumes.Where(v => v.CanFlush && v.PendingBytes > 0).ToArray())
            await volume.FlushCommand.ExecuteAsync(null);
    }

    /// <summary>Saves every image in RAM that has unsaved changes.</summary>
    [RelayCommand]
    private async Task SaveAll()
    {
        foreach (var disk in Monitor.VirtualDisks.Where(d => d.HasUnsavedChanges && d.CanSave).ToArray())
            await disk.SaveCommand.ExecuteAsync(null);
    }
}
