using QueueCache.Management;
using System.Runtime.Versioning;

namespace QueueCache.Operations;

[SupportedOSPlatform("windows")]
public static class CacheTasks
{
    public static async Task RemoveAsync(string volume, CancellationToken token = default)
    {
        var target = await DiskTarget.InspectAsync(volume, token);
        await Task.Run(() =>
        {
            using var device = new CacheDevice(target.Device, writable: true);
            if (!device.GetWriteCacheState().SupportsRelease)
                throw new IOException("The loaded driver does not support removing cache tasks. Install the matching driver and restart Windows.");
            device.Control(WriteCacheAction.Release);
        }, token);
        SavedConfigurations.Remove(target.Instance);
    }
    public static async Task<WriteCacheState> SaveAsync(string volume, CacheConfiguration configuration, bool persistent,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        var target = await DiskTarget.InspectAsync(volume, token);
        var state = await Task.Run(() => ConfigurationManager.Apply(target, configuration, true, progress), token);
        if (persistent) SavedConfigurations.Save(target, configuration, true);
        else SavedConfigurations.Remove(target.Instance);
        return state;
    }

    public static async Task SetEnabledAsync(string volume, bool enabled, bool persistent, CancellationToken token = default)
    {
        var target = await DiskTarget.InspectAsync(volume, token);
        using var device = new CacheDevice(target.Device);
        var state = device.GetWriteCacheState();
        await SaveAsync(volume, new((int)(state.BudgetBytes >> 20), state.UnsafeDefer ? CachePreset.Fast : CachePreset.Strict, enabled), persistent, token: token);
    }
}
