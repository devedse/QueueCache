using System.Text.RegularExpressions;

namespace QueueCache.Management;

public static partial class DevicePath
{
    // Accept whole volumes/disks only. Never normalize an arbitrary file into a device.
    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var name = value.StartsWith(@"\\.\", StringComparison.Ordinal) || value.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? value[4..] : value;
        if (!AllowedName().IsMatch(name))
            throw new ArgumentException("Use a volume such as D: or a disk such as PhysicalDrive1.", nameof(value));
        return @"\\.\" + name;
    }

    /// <summary>A QueueCache management target: the cache filters volumes, so the target is a volume
    /// such as Q:. A physical disk never answers QueueCache requests; name it with a clear message.</summary>
    public static string NormalizeVolume(string value)
    {
        var path = Normalize(value);
        if (path.Length != 6)
            throw new ArgumentException($"QueueCache caches volumes, not whole disks. Use the volume's letter, such as Q:, instead of {value}.", nameof(value));
        return path;
    }

    [GeneratedRegex(@"\A(?:[A-Za-z]:|PhysicalDrive[0-9]+)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AllowedName();
}
