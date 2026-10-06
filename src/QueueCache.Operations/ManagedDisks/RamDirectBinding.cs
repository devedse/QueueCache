using System.ComponentModel;
using System.Management;
using System.Runtime.Versioning;
using QueueCache.Management;

namespace QueueCache.Operations.ManagedDisks;

/// <summary>Direct access for RAM-backed managed disks (<see cref="RamAccess.Direct"/>). The volume filter
/// enforces every safety rule itself; this only asks it to start and reports what it decided.</summary>
[SupportedOSPlatform("windows")]
internal static class RamDirectBinding
{
    public static bool Requested(ManagedDiskDefinition definition) =>
        definition.Access == RamAccess.Direct && definition.Mode != ManagedDiskMode.CachedVhdx;

    /// <summary>Starts Direct access for a published volume. Never fails the caller's operation: on any
    /// problem the disk simply stays on the standard path, and the returned state (or null) says so.</summary>
    public static RamDirectState? Enable(ManagedDiskDefinition definition, string volume)
    {
        if (!Requested(definition)) return null;
        try
        {
            // Shadow copies made before this Windows session (for example inside a loaded image) are not
            // seen by the filter's flush-and-hold rule; writes then stay on the standard path.
            var readsOnly = ShadowCopiesExist(volume);
            using var device = CacheDevice.OpenVolumeName(volume, writable: true);
            return device.BindRamDirect(readsOnly);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidDataException or ManagementException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Current state without changing anything; null when unavailable.</summary>
    public static RamDirectState? Query(ManagedDiskDefinition definition, string? volume)
    {
        if (!Requested(definition) || volume is null) return null;
        try
        {
            using var device = CacheDevice.OpenVolumeName(volume);
            return device.GetRamDirectState();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool ShadowCopiesExist(string volume)
    {
        // Win32_ShadowCopy.VolumeName is the volume GUID path with a trailing backslash.
        var escaped = volume.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
        using var searcher = new ManagementObjectSearcher(@"root\cimv2", $"SELECT ID FROM Win32_ShadowCopy WHERE VolumeName = '{escaped}'");
        using var results = searcher.Get();
        return results.Count > 0;
    }
}
