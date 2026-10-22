using System.Runtime.Versioning;

namespace QueueCache.Operations;

/// <summary>A lettered fixed volume: the unit QueueCache caches. The disk fields identify the physical
/// disk that holds it (several volumes can share one disk; each has its own cache).</summary>
public sealed record VolumeDescription(string Volume, string Label, string FileSystem, long Bytes, string VolumePath,
    int DiskNumber, string DiskName, string Instance, long DiskBytes, bool IsBoot, bool IsSystem, bool IsPaging)
{
    public string VolumeId => VolumeIds.Parse(VolumePath);
    public double SizeGiB => Bytes / 1073741824.0;
    /// <summary>A file system Windows mounted (NTFS, ReFS, FAT32, exFAT, ...): a cache task can be created.</summary>
    public bool HasFileSystem => FileSystems.IsMounted(FileSystem);
    /// <summary>False for FAT32/exFAT: no journal, so losing Fast-mode data in a crash can corrupt the file system.</summary>
    public bool Journaled => FileSystems.IsJournaled(FileSystem);
    public string Name => string.IsNullOrWhiteSpace(Label) ? $"{Volume}" : $"{Volume} {Label}";
    public string Display => $"{Name} | {(string.IsNullOrEmpty(FileSystem) ? "RAW" : FileSystem)} | {SizeGiB:0.##} GiB | " +
        $"disk {DiskNumber} ({DiskName})" + (IsBoot || IsSystem ? " [Windows]" : "") + (IsPaging ? " [paging file]" : "");
}

/// <summary>What the cache needs from a volume's file system. The driver works below every file system; the
/// difference is what a crash that loses Fast-mode data does: NTFS and ReFS recover from their journal, FAT32 and
/// exFAT have none.</summary>
public static class FileSystems
{
    public static bool IsMounted(string? name) => !string.IsNullOrWhiteSpace(name) && !string.Equals(name, "RAW", StringComparison.OrdinalIgnoreCase);
    public static bool IsJournaled(string? name) => name is not null &&
        (name.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || name.Equals("ReFS", StringComparison.OrdinalIgnoreCase));
    public const string NoJournalWarning = "has no journal (FAT32/exFAT): if Windows crashes or loses power while Fast-mode data is still in RAM, " +
        "the file system itself can be damaged and need chkdsk, not only the newest files. Prefer Strict here, or NTFS/ReFS.";
}

/// <summary>Volume GUID names, the stable identity of a volume across drive-letter changes.</summary>
public static class VolumeIds
{
    /// <summary>\\?\Volume{guid}\ to the lowercase {guid}.</summary>
    public static string Parse(string volumeName)
    {
        const string prefix = @"\\?\Volume";
        if (volumeName is null || !volumeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !volumeName.EndsWith('\\'))
            throw new IOException("Unexpected volume name.");
        var id = volumeName[prefix.Length..^1];
        if (!Guid.TryParseExact(id, "B", out _))
            throw new IOException("Unexpected volume GUID.");
        return id.ToLowerInvariant();
    }
}

public static class VolumeCatalog
{
    /// <summary>One entry per lettered fixed volume on a single disk range (the only layout the tools target).</summary>
    [SupportedOSPlatform("windows")]
    public static async Task<IReadOnlyList<VolumeDescription>> ListAsync(CancellationToken token = default) =>
        Validate((await Task.Run(() => StorageInventory.Read(token), token).WaitAsync(TimeSpan.FromSeconds(30), token)).LetteredVolumes());

    internal static IReadOnlyList<VolumeDescription> Validate(IReadOnlyList<VolumeDescription> volumes)
    {
        foreach (var volume in volumes)
        {
            if (volume.Volume is not { Length: 2 } || !char.IsAsciiLetter(volume.Volume[0]) || volume.Volume[1] != ':' ||
                volume.Bytes <= 0 || volume.DiskBytes < volume.Bytes || volume.DiskNumber < 0 || string.IsNullOrWhiteSpace(volume.Instance))
                throw new IOException("Invalid volume inventory entry.");
            _ = volume.VolumeId; // Throws for a malformed volume path.
        }
        return volumes;
    }
}
