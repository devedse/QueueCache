using QueueCache.Management;
using QueueCache.Operations;
using System.Runtime.Versioning;

namespace QueueCache.Desktop;

// The view depends on task operations, never on a CLI process. This also lets
// headless UI tests exercise real screens without opening physical disks.
public interface ICacheTaskService
{
    Task<IReadOnlyList<DiskDescription>> ListAsync();
    Task<WriteCacheState> ReadAsync(DiskDescription disk);
    bool IsPersistent(DiskDescription disk);
    Task SaveAsync(string volume, CacheConfiguration configuration, bool persistent, IProgress<string> progress);
    Task SetEnabledAsync(string volume, bool enabled, bool persistent);
    Task FlushAsync(DiskDescription disk);
    Task RemoveAsync(string volume);
    Task<WorkloadReport> TestAsync(string volume, bool benchmark, IProgress<string> progress, CancellationToken token);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsCacheTaskService : ICacheTaskService
{
    public async Task<IReadOnlyList<DiskDescription>> ListAsync() => await DiskCatalog.ListAsync();
    public Task<WriteCacheState> ReadAsync(DiskDescription disk) => Task.Run(() =>
    {
        using var device = new CacheDevice(disk.Device);
        return device.GetWriteCacheState();
    });
    public bool IsPersistent(DiskDescription disk) => SavedConfigurations.List().Any(p => p.Instance.Equals(disk.Instance, StringComparison.OrdinalIgnoreCase));
    public async Task SaveAsync(string volume, CacheConfiguration configuration, bool persistent, IProgress<string> progress) =>
        await CacheTasks.SaveAsync(volume, configuration, persistent, progress);
    public Task SetEnabledAsync(string volume, bool enabled, bool persistent) => CacheTasks.SetEnabledAsync(volume, enabled, persistent);
    public Task FlushAsync(DiskDescription disk) => Task.Run(() =>
    {
        using var device = new CacheDevice(disk.Device, true);
        device.Control(WriteCacheAction.Flush);
    });
    public Task RemoveAsync(string volume) => CacheTasks.RemoveAsync(volume);
    public async Task<WorkloadReport> TestAsync(string volume, bool benchmark, IProgress<string> progress, CancellationToken token)
    {
        var target = await DiskTarget.InspectAsync(volume, token);
        return benchmark ? await DiskWorkloads.BenchmarkAsync(target, progress: progress, cancellationToken: token)
            : await DiskWorkloads.TestAsync(target, progress, token);
    }
}
