using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using QueueCache.Management;

namespace QueueCache.Operations;

/// <summary>
/// The volume filter's contract on a validated non-OS volume: how it is registered, that commands sent to the
/// physical disk never reach the cache, and that volumes sharing one disk have independent caches and profiles.
/// Every workload writes new files only; the physical disk receives read-only queries.
/// </summary>
[SupportedOSPlatform("windows")]
public static class VolumeScenarios
{
    private const int MiB = 1 << 20;
    // Fast with Deferred draining and a one-hour age: accepted writes stay pending until an explicit flush,
    // so any drain or flush a command causes is visible in the counters.
    internal static CacheConfiguration Pending(int budgetMiB) => new(budgetMiB, CachePreset.Fast)
    {
        Options = new(Drain: DrainAlgorithm.Deferred, MaxDirtyAgeMs: 3600000)
    };

    public static IReadOnlyList<CheckResult> Registration(DiskTarget target, CacheDevice device)
    {
        var checks = new List<CheckResult>();
        var registration = DriverRegistration.Inspect();
        var problems = registration.Problems();
        checks.Add(new("registration/volume-class", problems.Count == 0 ? "PASS" : "FAIL", problems.Count == 0
            ? $"Volume-class upper filters {string.Join(",", registration.VolumeClassUpperFilters)} (QueueCache last, topmost); " +
              $"disk-class {string.Join(",", registration.DiskClassUpperFilters)} (no QueueCache); every volume; no lab diagnostic mode."
            : string.Join(" ", problems)));
        var state = device.GetWriteCacheState();
        checks.Add(new("registration/volume-length", state.DeviceBytes == (ulong)target.Bytes ? "PASS" : "FAIL",
            $"Driver device length {state.DeviceBytes} bytes; {target.Device} volume length {target.Bytes} bytes (disk {target.DiskBytes} bytes)."));
        var answered = SendToDisk(target.Number, CacheDevice.WriteStateIoctl, null, new byte[WriteCacheState.WireSize], out var error);
        checks.Add(new("registration/physical-disk-not-filtered", answered ? "FAIL" : "PASS", answered
            ? $"PhysicalDrive{target.Number} answered a QueueCache request: a disk-level QueueCache filter is still loaded."
            : $"PhysicalDrive{target.Number} rejected a QueueCache request (Win32 {error}): the disk stack has no QueueCache filter."));
        var volumes = VolumeCatalog.ListAsync().GetAwaiter().GetResult();
        var missing = volumes.Where(volume => !DriverRegistration.IsLoaded(volume.Volume)).Select(volume => volume.Volume).ToArray();
        checks.Add(new("registration/every-volume", missing.Length == 0 && volumes.Count != 0 ? "PASS" : "FAIL", missing.Length == 0
            ? $"The filter answers on every lettered fixed volume: {string.Join(", ", volumes.Select(volume => volume.Volume))}."
            : $"No QueueCache filter on {string.Join(", ", missing)}. Restart Windows after installing, then check the registration."));
        return checks;
    }

    /// <summary>
    /// Commands a disk tool sends to the physical disk while this volume holds pending writes. With the
    /// volume filter they never reach the cache: no drain, no flush, no clean-data loss, no error.
    /// </summary>
    public static IReadOnlyList<CheckResult> RawDiskCommands(DiskTarget target, CacheDevice device, string workDirectory)
    {
        ConfigurationManager.Apply(target, Pending(256), true);
        var directory = NewDirectory(target, workDirectory, "raw-disk-commands");
        // Clean data (a drained, retained write) and pending data (accepted, not yet drained).
        var cleanPath = Path.Combine(directory, "clean.bin");
        var clean = Pattern(8 * MiB, 0x5EED0002);
        WriteNew(cleanPath, clean);
        device.Control(WriteCacheAction.Flush);
        var path = Path.Combine(directory, "pending.bin");
        var expected = Pattern(16 * MiB, 0x5EED0001);
        WriteNew(path, expected);
        var before = device.GetWriteCacheState();
        if (before.DirtyBytes < 16UL * MiB || before.CleanWriteBytes + before.CleanReadBytes == 0)
            throw new IOException($"Could not establish pending and clean data (pending {before.DirtyBytes}, clean {before.CleanReadBytes + before.CleanWriteBytes}).");
        var commands = new List<string>();
        var descriptor = new byte[1024];
        var query = new byte[12]; // STORAGE_PROPERTY_QUERY: StorageDeviceProperty, PropertyStandardQuery.
        if (!SendToDisk(target.Number, 0x2D1400, query, descriptor, out var error))
            throw new Win32Exception(error, "IOCTL_STORAGE_QUERY_PROPERTY on the physical disk failed.");
        commands.Add($"device descriptor ({Descriptor(descriptor)})");
        var geometry = new byte[256];
        if (!SendToDisk(target.Number, 0x700A0, null, geometry, out error))
            throw new Win32Exception(error, "IOCTL_DISK_GET_DRIVE_GEOMETRY_EX on the physical disk failed.");
        commands.Add($"geometry ({BinaryPrimitives.ReadInt64LittleEndian(geometry.AsSpan(24))} bytes)");
        var inquiry = Inquiry(target.Number, out error);
        commands.Add(inquiry is null ? $"SCSI INQUIRY not supported by this storage driver (Win32 {error})" : $"SCSI INQUIRY ({inquiry})");
        var after = device.GetWriteCacheState();
        var unchanged = after.DrainedBytes == before.DrainedBytes && after.Flushes == before.Flushes &&
            after.DirtyBytes >= before.DirtyBytes && after.CleanReadBytes + after.CleanWriteBytes >= before.CleanReadBytes + before.CleanWriteBytes &&
            !after.Faulted && after.LastError == 0 && after.Errors == before.Errors && after.Enabled;
        var detail = $"Sent to PhysicalDrive{target.Number} with {before.DirtyBytes / (double)MiB:0.0} MiB pending and " +
            $"{(before.CleanReadBytes + before.CleanWriteBytes) / (double)MiB:0.0} MiB clean: {string.Join("; ", commands)}. " +
            $"Drained {after.DrainedBytes - before.DrainedBytes} bytes, flushes {after.Flushes - before.Flushes}, " +
            $"pending {after.DirtyBytes / (double)MiB:0.0} MiB, clean {(after.CleanReadBytes + after.CleanWriteBytes) / (double)MiB:0.0} MiB afterwards.";
        var checks = new List<CheckResult> { new("raw-disk-commands/cache-untouched", unchanged ? "PASS" : "FAIL", detail) };
        device.Control(WriteCacheAction.Flush);
        device.Control(WriteCacheAction.DropClean);
        var bytesMatch = ReadAll(path).AsSpan().SequenceEqual(expected) && ReadAll(cleanPath).AsSpan().SequenceEqual(clean);
        checks.Add(new("raw-disk-commands/bytes", bytesMatch ? "PASS" : "FAIL",
            "After an explicit flush and dropping clean data, both files read back unbuffered from the disk match their oracles."));
        return checks;
    }

    /// <summary>A second NTFS volume on the same disk has its own cache, counters, drains and saved profile.</summary>
    public static IReadOnlyList<CheckResult> SharedDisk(DiskTarget target, CacheDevice device, string workDirectory)
    {
        var sibling = VolumeCatalog.ListAsync().GetAwaiter().GetResult().FirstOrDefault(volume =>
            volume.DiskNumber == target.Number && string.Equals(volume.Instance, target.Instance, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(volume.Volume, target.Device, StringComparison.OrdinalIgnoreCase) && volume.IsNtfs &&
            !volume.IsBoot && !volume.IsSystem && !volume.IsPaging);
        if (sibling is null)
            return [new("shared-disk/independent-caches", "SKIP", $"Needs a second non-OS NTFS volume on disk {target.Number}; " +
                "create the lab disk with qcache developer lab-disk create (docs/DEVELOPER_VERIFICATION.md).")];
        var other = DiskTarget.InspectAsync(sibling.Volume).GetAwaiter().GetResult();
        using var otherDevice = new CacheDevice(other.Device, writable: true);
        var otherInitial = otherDevice.GetWriteCacheState();
        if (otherInitial.BudgetBytes != 0 || SavedConfigurations.IsSaved(other.VolumeId))
            return [new("shared-disk/independent-caches", "SKIP", $"{other.Device} already has a cache task or saved profile; the suite does not change it.")];
        var checks = new List<CheckResult>();
        var saved = false;
        try
        {
            var mine = ConfigurationManager.Apply(target, Pending(256), true);
            var theirs = ConfigurationManager.Apply(other, Pending(128), true);
            if (theirs.DeviceBytes != (ulong)other.Bytes || mine.DeviceBytes != (ulong)target.Bytes || theirs.Instance == mine.Instance ||
                theirs.BudgetBytes != 128UL * MiB || mine.BudgetBytes != 256UL * MiB)
                throw new IOException("The two volumes do not report separate caches with their own lengths and budgets.");
            var directory = NewDirectory(target, workDirectory, "shared-disk");
            var otherDirectory = Path.Combine(other.Root, Path.GetFileName(workDirectory) + "-shared-disk");
            if (Directory.Exists(otherDirectory))
                throw new IOException("Sibling workload directory already exists.");
            Directory.CreateDirectory(otherDirectory);
            var mineData = Pattern(32 * MiB, 0x5EED0101);
            var theirData = Pattern(32 * MiB, 0x5EED0102);
            var a0 = device.GetWriteCacheState();
            var b0 = otherDevice.GetWriteCacheState();
            WriteNew(Path.Combine(directory, "mine.bin"), mineData);
            var a1 = device.GetWriteCacheState();
            var b1 = otherDevice.GetWriteCacheState();
            WriteNew(Path.Combine(otherDirectory, "theirs.bin"), theirData);
            var a2 = device.GetWriteCacheState();
            var b2 = otherDevice.GetWriteCacheState();
            var separate = a1.AcceptedBytes - a0.AcceptedBytes >= 32UL * MiB && b1.AcceptedBytes - b0.AcceptedBytes < 32UL * MiB &&
                b2.AcceptedBytes - b1.AcceptedBytes >= 32UL * MiB && a2.AcceptedBytes - a1.AcceptedBytes < 32UL * MiB;
            device.Control(WriteCacheAction.Flush);
            var a3 = device.GetWriteCacheState();
            var b3 = otherDevice.GetWriteCacheState();
            var independentFlush = a3.DirtyBytes == 0 && b3.DirtyBytes >= 32UL * MiB && b3.DrainedBytes == b2.DrainedBytes;
            otherDevice.Control(WriteCacheAction.Flush);
            device.Control(WriteCacheAction.DropClean);
            otherDevice.Control(WriteCacheAction.DropClean);
            var bytes = ReadAll(Path.Combine(directory, "mine.bin")).AsSpan().SequenceEqual(mineData) &&
                ReadAll(Path.Combine(otherDirectory, "theirs.bin")).AsSpan().SequenceEqual(theirData);
            checks.Add(new("shared-disk/independent-caches", separate && independentFlush && bytes ? "PASS" : "FAIL",
                $"{target.Device} (256 MiB) and {other.Device} (128 MiB) on disk {target.Number}: each accepted only its own 32 MiB " +
                $"({a1.AcceptedBytes - a0.AcceptedBytes}/{b1.AcceptedBytes - b0.AcceptedBytes}, then {a2.AcceptedBytes - a1.AcceptedBytes}/{b2.AcceptedBytes - b1.AcceptedBytes} bytes); " +
                $"flushing {target.Device} left {b3.DirtyBytes / (double)MiB:0.0} MiB pending on {other.Device} undrained; bytes {(bytes ? "match" : "DIFFER")} after both flushes. " +
                $"Retained files: {directory}, {otherDirectory}."));
            // Per-volume saved profiles: named by the volume GUID, independent of the other volume's profile.
            var targetSavedBefore = SavedConfigurations.IsSaved(target.VolumeId);
            SavedConfigurations.Save(other, Pending(128), true);
            saved = true;
            var profile = SavedConfigurations.List().SingleOrDefault(p => p.VolumeId == other.VolumeId);
            var targetSavedDuring = SavedConfigurations.IsSaved(target.VolumeId);
            SavedConfigurations.Remove(other);
            saved = false;
            var removed = !SavedConfigurations.IsSaved(other.VolumeId);
            var profiles = profile is { Version: 2 } && profile.Bytes == other.Bytes && profile.Volume == other.Device &&
                targetSavedDuring == targetSavedBefore && removed;
            checks.Add(new("shared-disk/per-volume-profiles", profiles ? "PASS" : "FAIL",
                $"Saved a version-2 profile for {other.Device} ({other.VolumeId}, {other.Bytes} bytes) without changing {target.Device}'s " +
                $"(saved before/during: {targetSavedBefore}/{targetSavedDuring}); removed it again ({removed})."));
        }
        finally
        {
            if (saved)
                SavedConfigurations.Remove(other);
            // Leave the sibling as found: no cache task.
            var state = otherDevice.GetWriteCacheState();
            if (state.BudgetBytes != 0)
            {
                otherDevice.Control(WriteCacheAction.Flush);
                otherDevice.Control(WriteCacheAction.Release);
            }
        }
        return checks;
    }

    /// <summary>
    /// Shrinks the lab disk's second volume by 1 GiB and extends it back while its cache holds pending writes.
    /// Only a volume labelled QC-Lab-2 on the same virtual disk as the target is resized (qcache developer
    /// lab-disk); anything else is SKIP. The extend goes beyond the length the driver re-read after the shrink.
    /// </summary>
    public static IReadOnlyList<CheckResult> Resize(DiskTarget target, string workDirectory)
    {
        var lab = VolumeCatalog.ListAsync().GetAwaiter().GetResult().FirstOrDefault(volume =>
            volume.DiskNumber == target.Number && string.Equals(volume.Instance, target.Instance, StringComparison.OrdinalIgnoreCase) &&
            volume.Label == "QC-Lab-2" && volume.DiskName == "Msft Virtual Disk" && volume.IsNtfs &&
            !volume.IsBoot && !volume.IsSystem && !volume.IsPaging);
        if (lab is null)
            return [new("resize/shrink-extend", "SKIP", "Resizing is only exercised on the lab disk's QC-Lab-2 volume (qcache developer lab-disk create).")];
        var other = DiskTarget.InspectAsync(lab.Volume).GetAwaiter().GetResult();
        using var device = new CacheDevice(other.Device, writable: true);
        if (device.GetWriteCacheState().BudgetBytes != 0 || SavedConfigurations.IsSaved(other.VolumeId))
            return [new("resize/shrink-extend", "SKIP", $"{other.Device} already has a cache task or saved profile; the suite does not change it.")];
        var original = other.Bytes;
        var smaller = original - (1L << 30);
        try
        {
            ConfigurationManager.Apply(other, Pending(128), true);
            var directory = Path.Combine(other.Root, Path.GetFileName(workDirectory) + "-resize");
            if (Directory.Exists(directory))
                throw new IOException("Resize workload directory already exists.");
            Directory.CreateDirectory(directory);
            var first = Pattern(32 * MiB, 0x5EED0201);
            WriteNew(Path.Combine(directory, "before.bin"), first);
            var pending = device.GetWriteCacheState();
            ResizePartition(other.Letter, smaller);
            var shrunk = device.GetWriteCacheState();
            var shrunkBytes = ReadAll(Path.Combine(directory, "before.bin")).AsSpan().SequenceEqual(first);
            ResizePartition(other.Letter, original);
            var extended = device.GetWriteCacheState();
            var fileSystem = new DriveInfo(other.Root).TotalSize;
            var second = Pattern(32 * MiB, 0x5EED0202);
            WriteNew(Path.Combine(directory, "after.bin"), second);
            device.Control(WriteCacheAction.Flush);
            device.Control(WriteCacheAction.DropClean);
            var bytes = shrunkBytes && ReadAll(Path.Combine(directory, "before.bin")).AsSpan().SequenceEqual(first) &&
                ReadAll(Path.Combine(directory, "after.bin")).AsSpan().SequenceEqual(second);
            var final = device.GetWriteCacheState();
            // NTFS keeps a few sectors of the partition for itself; the file system must have grown past the shrunk size.
            var pass = pending.DirtyBytes >= 32UL * MiB && shrunk.DeviceBytes == (ulong)smaller && extended.DeviceBytes == (ulong)original &&
                fileSystem > smaller && bytes && !final.Faulted && final.LastError == 0 && final.Errors == pending.Errors;
            return [new("resize/shrink-extend", pass ? "PASS" : "FAIL",
                $"{other.Device} with {pending.DirtyBytes / (double)MiB:0.0} MiB pending: shrunk to {smaller} bytes (driver length {shrunk.DeviceBytes}, " +
                $"pending afterwards {shrunk.DirtyBytes / (double)MiB:0.0} MiB), extended back to {original} bytes (driver length {extended.DeviceBytes}, " +
                $"file system {fileSystem} bytes); both files {(bytes ? "exact" : "DIFFER")} from the disk; errors {final.Errors - pending.Errors}. Retained: {directory}.")];
        }
        finally
        {
            // Leave the lab volume at its original size and without a cache task.
            try
            {
                if (new DriveInfo(other.Root).TotalSize < smaller + (1L << 29))
                    ResizePartition(other.Letter, original);
            }
            // The case's own result or exception says what happened; the size is visible in qcache volume list.
            catch (IOException) { }
            var state = device.GetWriteCacheState();
            if (state.BudgetBytes != 0)
            {
                device.Control(WriteCacheAction.Flush);
                device.Control(WriteCacheAction.Release);
            }
        }
    }

    /// <summary>
    /// A shadow copy (System Restore, backup tools) is taken by volsnap, below the cache. Windows' flush-and-hold
    /// request passes through the cache first, so data still pending in RAM must be in the snapshot.
    /// </summary>
    public static IReadOnlyList<CheckResult> Snapshot(DiskTarget target, CacheDevice device, string workDirectory)
    {
        ConfigurationManager.Apply(target, Pending(256), true);
        var directory = NewDirectory(target, workDirectory, "snapshot");
        var path = Path.Combine(directory, "pending.bin");
        var data = Pattern(32 * MiB, 0x5EED0301);
        WriteNew(path, data);
        var before = device.GetWriteCacheState();
        if (before.DirtyBytes < 32UL * MiB)
            throw new IOException("Could not establish 32 MiB of pending writes before the snapshot.");
        var created = PowerShell($"$ErrorActionPreference='Stop'; $r=Invoke-CimMethod -ClassName Win32_ShadowCopy -MethodName Create -Arguments @{{Volume='{target.Root}'; Context='ClientAccessible'}}; " +
            "if ($r.ReturnValue -ne 0) { \"FAILED $($r.ReturnValue)\" } else { $s=Get-CimInstance Win32_ShadowCopy | Where-Object ID -eq $r.ShadowID; \"$($r.ShadowID)|$($s.DeviceObject)\" }").Trim();
        if (created.StartsWith("FAILED", StringComparison.Ordinal))
            return [new("snapshot/pending-data-included", "SKIP", $"Windows could not create a shadow copy of {target.Device} (Win32_ShadowCopy.Create {created}).")];
        var parts = created.Split('|');
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var id) || !parts[1].StartsWith(@"\\?\GLOBALROOT\Device\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Unexpected shadow copy identity: " + created);
        try
        {
            var after = device.GetWriteCacheState();
            var snapshotPath = parts[1] + Path.GetFullPath(path)[2..];
            var copy = File.ReadAllBytes(snapshotPath);
            var included = copy.AsSpan().SequenceEqual(data);
            var pass = included && after.DrainedBytes - before.DrainedBytes >= 32UL * MiB && !after.Faulted && after.LastError == 0;
            return [new("snapshot/pending-data-included", pass ? "PASS" : "FAIL",
                $"Shadow copy of {target.Device} taken with {before.DirtyBytes / (double)MiB:0.0} MiB pending: the cache drained " +
                $"{(after.DrainedBytes - before.DrainedBytes) / (double)MiB:0.0} MiB before it; the 32 MiB file in the snapshot is " +
                $"{(included ? "exact" : "DIFFERENT")} ({snapshotPath}).")];
        }
        finally
        {
            PowerShell($"$ErrorActionPreference='Stop'; Get-CimInstance Win32_ShadowCopy | Where-Object ID -eq '{{{id}}}' | Remove-CimInstance");
        }
    }

    private static string PowerShell(string command)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(command);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Cannot start PowerShell.");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            process.Kill(true);
            throw new TimeoutException("PowerShell did not finish within five minutes.");
        }
        if (process.ExitCode != 0)
            throw new IOException($"PowerShell failed ({process.ExitCode}): {error.GetAwaiter().GetResult().Trim()}");
        return output;
    }

    private static void ResizePartition(char letter, long bytes)
    {
        try
        {
            PowerShell($"$ErrorActionPreference='Stop'; Resize-Partition -DriveLetter {letter} -Size {bytes}");
        }
        catch (IOException ex)
        {
            throw new IOException($"Resizing {letter}: to {bytes} bytes failed: {ex.Message}", ex);
        }
    }

    internal static string NewDirectory(DiskTarget target, string workDirectory, string name)
    {
        var directory = Path.GetFullPath(workDirectory + "-" + name);
        if (!directory.StartsWith(target.Root, StringComparison.OrdinalIgnoreCase) || Directory.Exists(directory))
            throw new IOException("The workload directory must be new and on the selected volume.");
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Reparse test target rejected.");
        return directory;
    }

    internal static byte[] Pattern(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    // Unbuffered: every byte goes through the volume (and the cache) rather than the Windows file cache.
    // FlushFileBuffers completes in RAM under Fast; the cache keeps the data pending.
    internal static void WriteNew(string path, byte[] data)
    {
        using var file = new AlignedFile(path, MiB, create: !File.Exists(path));
        var chunk = new byte[MiB];
        for (var offset = 0; offset < data.Length; offset += MiB)
        {
            data.AsSpan(offset, MiB).CopyTo(chunk);
            file.Write(offset, chunk);
        }
        file.Flush();
    }

    internal static byte[] ReadAll(string path)
    {
        var length = checked((int)new FileInfo(path).Length);
        var data = new byte[length];
        using var file = new AlignedFile(path, MiB, create: false);
        var chunk = new byte[MiB];
        for (var offset = 0; offset < length; offset += MiB)
        {
            file.Read(offset, chunk);
            chunk.AsSpan(0, Math.Min(MiB, length - offset)).CopyTo(data.AsSpan(offset));
        }
        return data;
    }

    private static string Descriptor(byte[] descriptor)
    {
        // STORAGE_DEVICE_DESCRIPTOR: vendor/product offsets at 12/16, bus type at 28.
        string Text(int at)
        {
            var offset = BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(at));
            if (offset <= 0 || offset >= descriptor.Length)
                return "";
            var end = Array.IndexOf(descriptor, (byte)0, offset);
            return Encoding.ASCII.GetString(descriptor, offset, (end < 0 ? descriptor.Length : end) - offset).Trim();
        }
        return $"{Text(12)} {Text(16)}, bus type {BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(28))}".Trim();
    }

    /// <summary>Standard SCSI INQUIRY through IOCTL_SCSI_PASS_THROUGH; null when the storage driver refuses it.</summary>
    private static string? Inquiry(int disk, out int error)
    {
        // SCSI_PASS_THROUGH (x64, 56 bytes), 32-byte sense buffer at 56, 96-byte data buffer at 88.
        var buffer = new byte[184];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, 56);
        buffer[6] = 6;       // CdbLength
        buffer[7] = 32;      // SenseInfoLength
        buffer[8] = 1;       // SCSI_IOCTL_DATA_IN
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), 96);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), 5);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(24), 88);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(32), 56);
        buffer[36] = 0x12;   // INQUIRY
        buffer[40] = 96;     // allocation length
        using var handle = OpenDisk(disk, writable: true);
        if (!DeviceIoControl(handle, 0x4D004, buffer, (uint)buffer.Length, buffer, (uint)buffer.Length, out _, IntPtr.Zero))
        {
            error = Marshal.GetLastWin32Error();
            if (error is 1 or 50 or 87)
                return null;
            throw new Win32Exception(error, "SCSI INQUIRY pass-through failed.");
        }
        error = 0;
        if (buffer[2] != 0)
            throw new IOException($"SCSI INQUIRY returned status 0x{buffer[2]:X2}.");
        return $"{Encoding.ASCII.GetString(buffer, 88 + 8, 8).Trim()} {Encoding.ASCII.GetString(buffer, 88 + 16, 16).Trim()}";
    }

    /// <summary>One request to \\.\PhysicalDriveN. False with the Win32 error when the disk stack refuses it.</summary>
    private static bool SendToDisk(int disk, uint code, byte[]? input, byte[] output, out int error)
    {
        using var handle = OpenDisk(disk, writable: false);
        if (DeviceIoControl(handle, code, input, input is null ? 0 : (uint)input.Length, output, (uint)output.Length, out _, IntPtr.Zero))
        {
            error = 0;
            return true;
        }
        error = Marshal.GetLastWin32Error();
        return false;
    }

    private static SafeFileHandle OpenDisk(int disk, bool writable)
    {
        var handle = CreateFileW($@"\\.\PhysicalDrive{disk}", writable ? 0xC0000000u : 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Cannot open PhysicalDrive{disk}.");
        }
        return handle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, uint inputBytes,
        [Out] byte[] output, uint outputBytes, out uint returned, IntPtr overlapped);
}
