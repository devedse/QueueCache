using System.ComponentModel;
using System.Runtime.Versioning;
using Microsoft.Win32;
using QueueCache.Management;

namespace QueueCache.Operations;

/// <summary>How the cache filter is registered. The installer registers it once as the topmost
/// Volume-class upper filter, which covers every volume after a restart (and volumes created later).
/// Caching itself is off on every volume until a cache task is applied.</summary>
public sealed record FilterRegistration(bool ServiceInstalled, bool AllVolumes, string[] VolumeClassUpperFilters,
    string[] DiskClassUpperFilters, string? RestrictedDriverKey, int DiagnosticMode)
{
    public const string Service = "qcachelab";

    /// <summary>Empty when the registration is the supported one; otherwise the problems, in plain words.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (!ServiceInstalled)
            problems.Add("QueueCache is not installed.");
        if (VolumeClassUpperFilters.Length == 0 ||
            !string.Equals(VolumeClassUpperFilters[^1], Service, StringComparison.OrdinalIgnoreCase))
            problems.Add("QueueCache is not the last (topmost) Volume-class upper filter. Reinstall QueueCache.");
        if (VolumeClassUpperFilters.Count(f => string.Equals(f, Service, StringComparison.OrdinalIgnoreCase)) > 1)
            problems.Add("QueueCache appears more than once in the Volume-class upper filters. Reinstall QueueCache.");
        if (DiskClassUpperFilters.Contains(Service, StringComparer.OrdinalIgnoreCase))
            problems.Add("An earlier disk-level QueueCache registration is still present. Reinstall QueueCache.");
        if (ServiceInstalled && !AllVolumes)
            problems.Add($"QueueCache is restricted to one volume by a lab setting (ClassCoverage 0, driver key {RestrictedDriverKey ?? "unset"}). Reinstall QueueCache to cover every volume.");
        if (DiagnosticMode != 0)
            problems.Add($"The lab DiagnosticMode value is {DiagnosticMode}; remove it and restart Windows.");
        return problems;
    }
}

[SupportedOSPlatform("windows")]
public static class DriverRegistration
{
    public const string VolumeClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{71a27cdd-812a-11d0-bec7-08002be2092f}";
    public const string DiskClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e967-e325-11ce-bfc1-08002be10318}";
    public const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\qcachelab";

    /// <summary>Reads the registration as Windows will load it at the next restart.</summary>
    public static FilterRegistration Inspect()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var service = machine.OpenSubKey(ServiceKey);
        using var volumes = machine.OpenSubKey(VolumeClassKey);
        using var disks = machine.OpenSubKey(DiskClassKey);
        return new(service is not null,
            service?.GetValue("ClassCoverage") is int coverage && coverage == 1,
            Filters(volumes), Filters(disks),
            service?.GetValue("LabAllowedDriverKey") as string,
            service?.GetValue("DiagnosticMode") is int mode ? mode : 0);

        static string[] Filters(RegistryKey? key) => key?.GetValue("UpperFilters") switch
        {
            null => [],
            string[] values => values.Where(v => v.Length != 0).ToArray(),
            _ => throw new InvalidDataException("UpperFilters is not a multi-string value.")
        };
    }

    /// <summary>True when the cache filter is loaded on this volume (it answers QueueCache requests).</summary>
    public static bool IsLoaded(string volume)
    {
        try
        {
            using var device = new CacheDevice(volume);
            device.GetWriteCacheState();
            return true;
        }
        // Invalid function / not supported: no QueueCache filter on this volume's stack.
        catch (Win32Exception ex) when (ex.NativeErrorCode is 1 or 50) { return false; }
    }

    /// <summary>Per-disk registration belonged to the disk filter. Every volume is covered by the one
    /// Volume-class registration, so these requests only explain that (or what is wrong).</summary>
    public static async Task<string> ChangeAsync(string volume, bool attach, CancellationToken token = default)
    {
        var target = await DiskTarget.InspectAsync(volume, token);
        var registration = Inspect();
        var problems = registration.Problems();
        if (problems.Count != 0)
            throw new IOException($"{target.Device}: " + string.Join(" ", problems));
        if (!attach)
            throw new IOException($"{target.Device}: QueueCache no longer registers per disk; the filter stays on every volume and passes requests through unless a cache task is running. " +
                $"Use 'qcache policy remove {target.Device}' to stop caching, or uninstall QueueCache to remove the filter.");
        return IsLoaded(target.Device)
            ? $"{target.Device}: covered by QueueCache's volume filter. Use 'qcache policy apply {target.Device} ...' to start caching."
            : $"{target.Device}: registered for QueueCache's volume filter; restart Windows to load it.";
    }
}
