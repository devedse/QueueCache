using System.Diagnostics;
using System.Runtime.Versioning;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer;

/// <summary>
/// The volume-filter lab disk: an expandable VHDX with two NTFS volumes and one unformatted (RAW) volume on one
/// virtual disk. It gives the volumes suite a second volume on the same disk, write-tests a RAW volume, and the
/// trim-cache suite a disk that accepts TRIM. Only a disk created from, or attached from, the named VHDX file is
/// ever changed; nothing else is partitioned or formatted.
/// </summary>
[SupportedOSPlatform("windows")]
public static class LabDisk
{
    public const int DefaultSizeGiB = 24;
    public const int VolumeGiB = 8;

    public sealed record Layout(char First, char Second, char Raw);

    public static Layout ParseLetters(string letters)
    {
        var parts = letters.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || parts.Any(p => p.Length != 1 || !char.IsAsciiLetter(p[0])) ||
            parts.Select(p => char.ToUpperInvariant(p[0])).Distinct().Count() != 3 ||
            parts.Any(p => char.ToUpperInvariant(p[0]) is 'A' or 'B' or 'C'))
            throw new ArgumentException("Give three different drive letters (not A, B or C), e.g. V,W,X: two NTFS volumes, then the unformatted volume.");
        return new(char.ToUpperInvariant(parts[0][0]), char.ToUpperInvariant(parts[1][0]), char.ToUpperInvariant(parts[2][0]));
    }

    public static string ValidatePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) || full.StartsWith(@"\\", StringComparison.Ordinal) ||
            full.Contains('"') || full.Contains('\'') || full.Contains('`') || full.Contains('$'))
            throw new ArgumentException("Use a local .vhdx path without quotes or '$', e.g. C:\\QueueCache-Lab\\VolumeLab.vhdx.");
        return full;
    }

    public static async Task<int> CreateAsync(string path, Layout letters, int sizeGiB, CancellationToken token)
    {
        path = ValidatePath(path);
        if (sizeGiB < 2 * VolumeGiB + 5 || sizeGiB > 256)
            throw new ArgumentException($"The lab disk needs {2 * VolumeGiB + 5}..256 GiB (two {VolumeGiB} GiB NTFS volumes and a RAW volume of at least 4 GiB).");
        if (File.Exists(path))
            throw new IOException($"{path} already exists. Attach it instead, or choose a new path.");
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var used = DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0])).ToHashSet();
        foreach (var letter in new[] { letters.First, letters.Second, letters.Raw })
            if (used.Contains(letter))
                throw new IOException($"{letter}: is already in use.");
        await DiskPart($"create vdisk file=\"{path}\" maximum={sizeGiB * 1024} type=expandable\r\nattach vdisk\r\n", token);
        // The new VHDX is the only disk this touches: found by its image path and required to be empty (RAW).
        await PowerShell($"$ErrorActionPreference='Stop'; $d=@(Get-Disk | Where-Object Location -eq '{path}'); if ($d.Count -ne 1) {{ throw 'The new VHDX is not attached.' }}; $d=$d[0]; " +
            "if ($d.PartitionStyle -ne 'RAW' -or $d.NumberOfPartitions -ne 0 -or $d.IsBoot -or $d.IsSystem) { throw 'The attached VHDX is not an empty disk.' }; " +
            "Initialize-Disk -Number $d.Number -PartitionStyle GPT; " +
            $"New-Partition -DiskNumber $d.Number -Size {VolumeGiB}GB -DriveLetter {letters.First} | Format-Volume -FileSystem NTFS -NewFileSystemLabel QC-Lab-1 -Confirm:$false | Out-Null; " +
            $"New-Partition -DiskNumber $d.Number -Size {VolumeGiB}GB -DriveLetter {letters.Second} | Format-Volume -FileSystem NTFS -NewFileSystemLabel QC-Lab-2 -Confirm:$false | Out-Null; " +
            $"New-Partition -DiskNumber $d.Number -UseMaximumSize -DriveLetter {letters.Raw} | Out-Null", token);
        return await ShowAsync(path, token);
    }

    public static async Task<int> AttachAsync(string path, CancellationToken token)
    {
        path = ValidatePath(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("No lab disk at this path; create it first.", path);
        // diskpart: after an unclean restart Mount-DiskImage answered "Access is denied" on the VM.
        var attached = await PowerShell($"@(Get-Disk | Where-Object Location -eq '{path}').Count", token);
        if (attached.Trim() == "0")
            await DiskPart($"select vdisk file=\"{path}\"\r\nattach vdisk\r\n", token);
        return await ShowAsync(path, token);
    }

    /// <summary>Refuses while any of its volumes has a cache task: detaching would lose pending writes.</summary>
    public static async Task<int> DetachAsync(string path, CancellationToken token)
    {
        path = ValidatePath(path);
        foreach (var volume in await VolumesAsync(path, token))
        {
            using var device = new CacheDevice(volume);
            if (device.GetWriteCacheState().BudgetBytes != 0)
                throw new IOException($"{volume} has a cache task. Remove it first: qcache policy remove {volume}");
        }
        await DiskPart($"select vdisk file=\"{path}\"\r\ndetach vdisk\r\n", token);
        Console.WriteLine($"Detached {path}.");
        return 0;
    }

    private static async Task<int> ShowAsync(string path, CancellationToken token)
    {
        var letters = await VolumesAsync(path, token);
        var volumes = (await VolumeCatalog.ListAsync(token)).Where(v => letters.Contains(v.Volume)).ToArray();
        Console.WriteLine($"Lab disk {path}:");
        foreach (var volume in volumes)
            Console.WriteLine($"  {volume.Display} · {volume.VolumeId} · {volume.Bytes} bytes");
        var ntfs = volumes.Where(v => v.IsNtfs).ToArray();
        var raw = volumes.FirstOrDefault(v => !v.IsNtfs);
        if (ntfs.Length != 0)
            Console.WriteLine($"Volume suite:  qcache developer verify {ntfs[0].Volume} --suite volumes\n" +
                $"TRIM suite:    qcache developer verify {ntfs[0].Volume} --suite trim-cache");
        if (raw is not null)
            Console.WriteLine($"Raw tests:     qcache developer write-tests {raw.Volume} {raw.Bytes} {raw.VolumeId} write-and-read-disposable-region");
        return 0;
    }

    private static async Task<string[]> VolumesAsync(string path, CancellationToken token)
    {
        var output = await PowerShell($"$ErrorActionPreference='Stop'; $d=@(Get-Disk | Where-Object Location -eq '{path}'); if ($d.Count -ne 1) {{ throw 'The lab disk is not attached.' }}; " +
            "Get-Partition -DiskNumber $d[0].Number | Where-Object DriveLetter | ForEach-Object { \"$($_.DriveLetter):\" }", token);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static async Task DiskPart(string commands, CancellationToken token)
    {
        var script = Path.Combine(Path.GetTempPath(), $"QueueCache-LabDisk-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(script, commands, token);
        try
        {
            await Run("diskpart.exe", ["/s", script], token);
        }
        finally { File.Delete(script); }
    }

    private static Task<string> PowerShell(string command, CancellationToken token) =>
        Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoProfile", "-Command", command], token);

    private static async Task<string> Run(string file, string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException($"Cannot start {file}.");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw; }
        if (process.ExitCode != 0)
            throw new IOException($"{Path.GetFileName(file)} failed ({process.ExitCode}): {(await error).Trim()} {(await output).Trim()}");
        return await output;
    }
}
