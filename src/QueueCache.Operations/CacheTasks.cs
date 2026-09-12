using QueueCache.Management;
using System.Runtime.Versioning;

namespace QueueCache.Operations;

[SupportedOSPlatform("windows")]
public static class CacheTasks
{
    public static async Task RemoveAsync(string volume, CancellationToken token = default, bool preserveSaved = false)
    {
        var target = await DiskTarget.InspectAsync(volume, token);
        await Task.Run(() =>
        {
            using var gate = ConfigurationGate.Enter();
            using var device = new CacheDevice(target.Device, writable: true);
            if (!device.GetWriteCacheState().SupportsRelease)
                throw new IOException("The loaded driver does not support removing cache tasks. Install the matching driver and restart Windows.");
            device.Control(WriteCacheAction.Release);
            if (!preserveSaved) SavedConfigurations.Remove(target.Instance);
        }, token);
    }
    public static async Task<WriteCacheState> SaveAsync(string volume, CacheConfiguration configuration, bool persistent,
        IProgress<string>? progress = null, CancellationToken token = default, bool preserveSaved = false)
    {
        if (persistent && preserveSaved) throw new ArgumentException("Cannot save a configuration while preserving the saved profile unchanged.");
        var target = await DiskTarget.InspectAsync(volume, token);
        return await Task.Run(() =>
        {
            // One management transaction across CLI/UI processes, including persistence.
            using var gate = ConfigurationGate.Enter();
            token.ThrowIfCancellationRequested();
            var state = ConfigurationManager.Apply(target, configuration, true, progress);
            if (persistent) SavedConfigurations.Save(target, configuration, true);
            else if (!preserveSaved) SavedConfigurations.Remove(target.Instance);
            return state;
        }, token);
    }

    public static async Task SetEnabledAsync(string volume, bool enabled, bool persistent, CancellationToken token = default, bool preserveSaved = false)
    {
        var target = await DiskTarget.InspectAsync(volume, token);
        await Task.Run(() =>
        {
            using var gate = ConfigurationGate.Enter();
            using var device = new CacheDevice(target.Device);
            var configuration = CacheConfiguration.FromState(device.GetWriteCacheState()) with { Enabled = enabled };
            ConfigurationManager.Apply(target, configuration, true);
            if (!preserveSaved) {
                if (persistent) SavedConfigurations.Save(target, configuration, true);
                else SavedConfigurations.Remove(target.Instance);
            }
        }, token);
    }
}
