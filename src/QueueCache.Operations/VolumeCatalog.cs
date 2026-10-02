using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

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
    [SupportedOSPlatform("windows")]
    public static async Task<IReadOnlyList<VolumeDescription>> ListAsync(CancellationToken token = default)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        // One entry per lettered fixed volume on a single partition (the only layout the tools target).
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; " +
            "$paging=@(Get-CimInstance Win32_PageFileUsage | ForEach-Object { $_.Name.Substring(0,1).ToUpperInvariant() }); " +
            "$items=@(Get-Volume | Where-Object { $_.DriveLetter -and $_.DriveType -eq 'Fixed' } | Sort-Object DriveLetter | ForEach-Object { " +
            "$v=$_; $p=@(Get-Partition -DriveLetter $v.DriveLetter -ErrorAction SilentlyContinue); if ($p.Count -ne 1) { return }; " +
            "$d=Get-Disk -Number $p[0].DiskNumber; $c=Get-CimInstance Win32_DiskDrive -Filter ('Index='+$d.Number); " +
            "[pscustomobject]@{Volume=\"$($v.DriveLetter):\";Label=[string]$v.FileSystemLabel;FileSystem=[string]$v.FileSystem;Bytes=[long]$p[0].Size;" +
            "VolumePath=[string]$v.Path;DiskNumber=[int]$d.Number;DiskName=[string]$d.FriendlyName;Instance=[string]$c.PNPDeviceID;DiskBytes=[long]$d.Size;" +
            "IsBoot=[bool]$p[0].IsBoot;IsSystem=[bool]$p[0].IsSystem;IsPaging=[bool]($paging -contains ([string]$v.DriveLetter).ToUpperInvariant())} }); " +
            "ConvertTo-Json -InputObject $items -Compress -Depth 3");
        using var process = Process.Start(start) ?? throw new IOException("Cannot enumerate volumes.");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw; }
        if (process.ExitCode != 0)
            throw new IOException(await error);
        return Parse(await output);
    }

    internal static IReadOnlyList<VolumeDescription> Parse(string json)
    {
        var volumes = JsonSerializer.Deserialize<VolumeDescription[]>(json) ?? throw new IOException("Invalid volume inventory.");
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
