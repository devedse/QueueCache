using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using QueueCache.Desktop.Formatting;
using QueueCache.Operations;

namespace QueueCache.Desktop.ViewModels;

/// <summary>The volumes on one physical disk. Each volume has its own cache.</summary>
public sealed partial class DiskGroupViewModel
{
    private readonly DashboardMonitor monitor;

    internal DiskGroupViewModel(DashboardMonitor monitor, IReadOnlyList<VolumeViewModel> volumes)
    {
        this.monitor = monitor;
        var first = volumes[0].Volume;
        DiskNumber = first.DiskNumber;
        Header = $"Disk {first.DiskNumber} · {first.DiskName} · {Format.Bytes(first.DiskBytes)}";
        Hint = volumes.Count > 1 ? "Each volume has its own cache" : "";
        // Windows cannot remove the disk it runs from or the one holding the paging file.
        CanEject = !first.IsBoot && !first.IsSystem && !volumes.Any(v => v.Volume.IsPaging);
        EjectText = $"Safely eject disk {first.DiskNumber}…";
        Volumes = new(volumes);
        foreach (var volume in volumes)
            volume.Group = this;
        EjectVolume = first.Volume;
    }

    public int DiskNumber { get; }
    public string Header { get; }
    public string Hint { get; }
    public bool CanEject { get; }
    public string EjectText { get; }
    public ObservableCollection<VolumeViewModel> Volumes { get; }
    internal string EjectVolume { get; }

    [RelayCommand] private Task Eject() => monitor.EjectDiskAsync(this);
}

/// <summary>A volume with saved cache settings that is not connected now. No live values exist.</summary>
public sealed record SavedVolumeViewModel(string Volume, string VolumeId)
{
    public string Text => $"{Volume} · {VolumeId} · Not connected. Its saved cache settings are kept and apply when it returns.";
}
