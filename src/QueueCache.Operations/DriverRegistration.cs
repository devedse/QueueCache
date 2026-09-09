using Microsoft.Win32;
using QueueCache.Management;
using System.Runtime.Versioning;

namespace QueueCache.Operations;

/// <summary>Explicit per-device registration, separate from software installation and cache policy.</summary>
[SupportedOSPlatform("windows")]
public static class DriverRegistration
{
    public static async Task<string> ChangeAsync(string volume, bool attach, CancellationToken token = default)
    {
        var target = await DiskTarget.InspectAsync(volume, token);
        var inventory = await DiskCatalog.ListAsync(token);
        var selected = inventory.Single(d => d.Number == target.Number && d.Instance == target.Instance);
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var service = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\qcachelab", writable: true)
            ?? throw new IOException("Install QueueCache first.");
        var snapshot = DeviceFilters.Inspect(target.Instance);
        if (attach)
        {
            // The current engine deliberately owns one exact disk. Never retarget
            // a still-registered filter or silently attach a whole disk class.
            foreach (var disk in inventory.Where(d => d.Instance != target.Instance))
                if (DeviceFilters.Inspect(disk.Instance).UpperFilters.Contains(DeviceFilters.LabService, StringComparer.OrdinalIgnoreCase))
                    throw new IOException("Detach the currently selected disk and reboot before selecting another. This engine supports one disk at a time.");
            service.SetValue("LabAllowedDriverKey", snapshot.DriverKey, RegistryValueKind.String);
            service.SetValue("Start", selected.IsBoot || selected.IsSystem ? 0 : 3, RegistryValueKind.DWord);
            service.SetValue("Group", "Filter", RegistryValueKind.String);
            service.Flush();
            target.CheckExtents();
            DeviceFilters.Change(target.Instance, snapshot.DriverKey, true);
        }
        else
        {
            // A missing/unloaded filter has no cache to drain. Never swallow a
            // real control failure from a filter which is responding.
            using var device = new CacheDevice(target.Device, writable: true);
            try { device.GetWriteCacheState(); }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 1 or 50) { goto Remove; }
            device.Control(WriteCacheAction.Disable);
        Remove:
            DeviceFilters.Change(target.Instance, snapshot.DriverKey, false);
        }
        return $"{selected.Display}: filter {(attach ? "registered" : "unregistered")}. Reboot required. Caching is not automatically enabled. Boot/paging support remains unvalidated.";
    }
}
