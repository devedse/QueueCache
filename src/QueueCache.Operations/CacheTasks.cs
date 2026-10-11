using System.Runtime.Versioning;
using QueueCache.Management;

namespace QueueCache.Operations;

[SupportedOSPlatform("windows")]
public static class CacheTasks
{
    internal static void ValidateSelection(DiskTarget target, VolumeDescription? expected)
    {
        if (expected is not null && (!expected.Volume.Equals(target.Device, StringComparison.OrdinalIgnoreCase) ||
            !expected.VolumeId.Equals(target.VolumeId, StringComparison.OrdinalIgnoreCase) ||
            !expected.Instance.Equals(target.Instance, StringComparison.OrdinalIgnoreCase) ||
            expected.DiskNumber != target.Number || expected.Bytes != target.Bytes || expected.DiskBytes != target.DiskBytes))
            throw new IOException("The selected disk or volume changed; refresh before changing its cache.");
    }

    public static async Task<WriteCacheState> ControlAsync(string volume, WriteCacheAction action,
        ulong budgetBytes = 0, ulong value = 0, bool enableAfter = false, CancellationToken token = default,
        string? expectedVolumeId = null, VolumeDescription? expected = null)
    {
        var target = await DiskTarget.InspectAsync(DevicePath.NormalizeVolume(volume)[4..], token, requireFileSystem: false);
        if (expectedVolumeId is not null && !target.VolumeId.Equals(expectedVolumeId, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The selected volume has been replaced; refresh before changing its cache.");
        ValidateSelection(target, expected);
        return await Task.Run(() =>
        {
            using var gate = ConfigurationGate.Enter(target.Instance);
            target.ValidateCurrent(token, requireFileSystem: false);
            // Timing and the caller path only change how requests are measured or served, so they
            // also apply to the cache a managed disk owns.
            if ((action != WriteCacheAction.Flush || enableAfter) && action is not (WriteCacheAction.PerformanceTiming or WriteCacheAction.CallerPath or WriteCacheAction.LabCopyFlags or WriteCacheAction.LabReadRecall or WriteCacheAction.LabRamReadQueue or WriteCacheAction.LabCallerBackoff))
                ManagedDisks.ManagedDiskConfiguration.RequireCacheOwner(ManagedDisks.ManagedDiskHostProtection.OwnerOfVolume(target.VolumeId), null);
            using var device = new CacheDevice(target.Device, writable: true);
            using var hostProtectionGate = ManagedDisks.ManagedDiskHostProtection.EnterPolicyGate();
            if (action is WriteCacheAction.FlushPolicy or WriteCacheAction.Enable || enableAfter)
            {
                var configuration = CacheConfiguration.FromState(device.GetWriteCacheState());
                if (action == WriteCacheAction.FlushPolicy) configuration = configuration with { Preset = value == 1 ? CachePreset.Fast : CachePreset.Strict };
                ManagedDisks.ManagedDiskHostProtection.ValidateCacheChange(target.VolumeId, configuration with { Enabled = true });
            }
            device.Control(action, budgetBytes, value);
            if (enableAfter) device.Control(WriteCacheAction.Enable);
            return device.GetWriteCacheState();
        }, token);
    }
    public static async Task RemoveAsync(string volume, CancellationToken token = default, bool preserveSaved = false, VolumeDescription? expected = null)
    {
        // Draining and freeing a cache is safe on any volume, including one without a file system.
        var target = await DiskTarget.InspectAsync(volume, token, requireFileSystem: false);
        ValidateSelection(target, expected);
        await Task.Run(() =>
        {
            using var gate = ConfigurationGate.Enter(target.Instance);
            target.ValidateCurrent(token, requireFileSystem: false);
            ManagedDisks.ManagedDiskConfiguration.RequireCacheOwner(ManagedDisks.ManagedDiskHostProtection.OwnerOfVolume(target.VolumeId), null);
            using var device = new CacheDevice(target.Device, writable: true);
            if (!device.GetWriteCacheState().SupportsRelease)
                throw new IOException("The loaded driver does not support removing cache tasks. Install the matching driver and restart Windows.");
            device.Control(WriteCacheAction.Release);
            if (!preserveSaved)
                SavedConfigurations.Remove(target);
        }, token);
    }
    public static async Task<WriteCacheState> SaveAsync(string volume, CacheConfiguration configuration, bool persistent,
        bool acceptVolatileFlush, IProgress<string>? progress = null, CancellationToken token = default, bool preserveSaved = false, VolumeDescription? expected = null, Guid? managedOwner = null)
    {
        if (persistent && preserveSaved)
            throw new ArgumentException("Cannot save a configuration while preserving the saved profile unchanged.");
        var target = await DiskTarget.InspectAsync(volume, token);
        ValidateSelection(target, expected);
        return await Task.Run(() =>
        {
            // One management transaction across CLI/UI processes, including persistence.
            using var gate = ConfigurationGate.Enter(target.Instance);
            token.ThrowIfCancellationRequested();
            var state = ConfigurationManager.Apply(target, configuration, acceptVolatileFlush, progress, managedOwner);
            if (persistent)
                SavedConfigurations.Save(target, configuration, acceptVolatileFlush);
            else if (!preserveSaved)
                SavedConfigurations.Remove(target);
            return state;
        }, token);
    }

    public static async Task SetEnabledAsync(string volume, bool enabled, bool persistent, CancellationToken token = default, bool preserveSaved = false, VolumeDescription? expected = null)
    {
        var target = await DiskTarget.InspectAsync(volume, token);
        ValidateSelection(target, expected);
        await Task.Run(() =>
        {
            using var gate = ConfigurationGate.Enter(target.Instance);
            target.ValidateCurrent(token);
            using var device = new CacheDevice(target.Device);
            var configuration = CacheConfiguration.FromState(device.GetWriteCacheState()) with
            {
                Enabled = enabled
            };
            ConfigurationManager.Apply(target, configuration, true);
            if (!preserveSaved)
            {
                if (persistent)
                    SavedConfigurations.Save(target, configuration, true);
                else
                    SavedConfigurations.Remove(target);
            }
        }, token);
    }
}
