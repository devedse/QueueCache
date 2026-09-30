using System.Runtime.Versioning;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Desktop;

// The view depends on task operations, never on a CLI process. This also lets
// headless UI tests exercise real screens without opening physical disks.
public interface ICacheTaskService
{
    // One cache per volume; several volumes can share a disk.
    Task<IReadOnlyList<VolumeDescription>> ListAsync();
    Task<IReadOnlyList<SavedConfiguration>> ListSavedAsync() => Task.FromResult<IReadOnlyList<SavedConfiguration>>([]);
    Task<WriteCacheState> ReadAsync(VolumeDescription volume);
    bool IsPersistent(VolumeDescription volume);
    Task SaveAsync(string volume, CacheConfiguration configuration, bool persistent, IProgress<string> progress);
    Task SetEnabledAsync(string volume, bool enabled, bool persistent);
    Task FlushAsync(VolumeDescription volume);
    Task DropCleanAsync(VolumeDescription volume);
    Task RemoveAsync(string volume);
    Task<DiskEjectPreview> PreviewEjectAsync(string volume) => throw new NotSupportedException("Disk eject is unavailable in this service.");
    Task<DiskEjectResult> EjectAsync(string volume, IProgress<string> progress) => throw new NotSupportedException("Disk eject is unavailable in this service.");
    Task<WorkloadReport> TestAsync(string volume, bool benchmark, IProgress<string> progress, CancellationToken token);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsCacheTaskService : ICacheTaskService
{
    public async Task<IReadOnlyList<VolumeDescription>> ListAsync() => await VolumeCatalog.ListAsync();
    public Task<IReadOnlyList<SavedConfiguration>> ListSavedAsync() => Task.Run<IReadOnlyList<SavedConfiguration>>(SavedConfigurations.List);
    public Task<WriteCacheState> ReadAsync(VolumeDescription volume) => Task.Run(() =>
    {
        ValidateVolume(volume);
        using var device = new CacheDevice(volume.Volume);
        var state = device.GetWriteCacheState();
        var sampled = state.SupportsPerformance ? state with
        {
            Performance = device.GetPerformance()
        } : state;
        ValidateVolume(volume);
        return sampled;
    });
    private static void ValidateVolume(VolumeDescription volume)
    {
        if (!DiskTarget.ReadVolumeId(volume.Volume[0]).Equals(volume.VolumeId, StringComparison.OrdinalIgnoreCase))
            throw new IOException("This drive letter now identifies a different volume. Refresh the volume list.");
    }
    public bool IsPersistent(VolumeDescription volume) => SavedConfigurations.IsSaved(volume.VolumeId);
    public async Task SaveAsync(string volume, CacheConfiguration configuration, bool persistent, IProgress<string> progress) =>
        // Choosing Fast in the editor is the desktop's explicit volatility acknowledgement.
        await CacheTasks.SaveAsync(volume, configuration, persistent, configuration.Preset == CachePreset.Fast, progress);
    public Task SetEnabledAsync(string volume, bool enabled, bool persistent) => CacheTasks.SetEnabledAsync(volume, enabled, persistent);
    public async Task FlushAsync(VolumeDescription volume) =>
        await CacheTasks.ControlAsync(volume.Volume, WriteCacheAction.Flush, expectedVolumeId: volume.VolumeId);
    // Clean blocks only: no drain, no effect on pending writes.
    public async Task DropCleanAsync(VolumeDescription volume) =>
        await CacheTasks.ControlAsync(volume.Volume, WriteCacheAction.DropClean, expectedVolumeId: volume.VolumeId);
    public Task RemoveAsync(string volume) => CacheTasks.RemoveAsync(volume);
    public Task<DiskEjectPreview> PreviewEjectAsync(string volume) => DiskEjection.PreviewAsync(volume);
    public Task<DiskEjectResult> EjectAsync(string volume, IProgress<string> progress) => DiskEjection.EjectAsync(volume, progress);
    public async Task<WorkloadReport> TestAsync(string volume, bool benchmark, IProgress<string> progress, CancellationToken token)
    {
        var target = await DiskTarget.InspectAsync(volume, token);
        return benchmark ? await DiskWorkloads.BenchmarkAsync(target, progress: progress, cancellationToken: token)
            : await DiskWorkloads.TestAsync(target, progress, token);
    }
}
