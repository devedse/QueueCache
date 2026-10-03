using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using QueueCache.Management;

namespace QueueCache.Operations.ManagedDisks;

public sealed record ManagedPartition(Guid Id, Guid Type, ulong Offset, ulong Bytes, ulong Attributes);
public sealed record ManagedDiskLayout(Guid DiskId, IReadOnlyList<ManagedPartition> Partitions)
{
    public static readonly Guid BasicData = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
    public static readonly Guid Reserved = new("E3C9E316-0B5C-4DB8-817D-F92DF00215AE");
    public void Validate(ulong capacity, uint sector)
    {
        if (DiskId == Guid.Empty || Partitions.Count is < 1 or > 16 ||
            Partitions.Count(p => p.Type == BasicData) != 1 ||
            Partitions.Any(p => p.Id == Guid.Empty || p.Bytes == 0 || p.Offset < (1UL << 20) ||
                p.Offset % sector != 0 || p.Bytes % sector != 0 || p.Offset > capacity || p.Bytes > capacity - p.Offset ||
                (p.Type != BasicData && p.Type != Reserved) || (p.Attributes & ~0x8000000000000000UL) != 0) ||
            Partitions.Select(p => p.Id).Distinct().Count() != Partitions.Count)
            throw new NotSupportedException("Activation requires GPT with one unencrypted NTFS data partition and optional Microsoft reserved partitions.");
        var sorted = Partitions.OrderBy(p => p.Offset).ToArray();
        for (var i = 1; i < sorted.Length; i++)
            if (sorted[i - 1].Offset + sorted[i - 1].Bytes > sorted[i].Offset)
                throw new InvalidDataException("Overlapping image partitions are unsupported.");
    }
}

/// <summary>Owned physical-disk access. Numbers are resolved from native resource identities, never selected by a letter.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDiskStorage : ILogicalDisk, IDisposable
{
    private readonly SafeFileHandle handle;
    public string Path { get; }
    public int Number { get; }
    public ulong CapacityBytes { get; }
    public uint SectorBytes { get; }
    private WindowsDiskStorage(string path, SafeFileHandle handle)
    {
        Path = path; this.handle = handle;
        Number = checked((int)BitConverter.ToUInt32(Io(0x2D1080, [], 12), 4));
        CapacityBytes = BitConverter.ToUInt64(Io(0x7405C, [], 8), 0);
        var geometry = Io(0x70000, [], 24); SectorBytes = BitConverter.ToUInt32(geometry, 20);
        if (SectorBytes is not (512 or 4096) || CapacityBytes == 0 || CapacityBytes > long.MaxValue)
            throw new InvalidDataException("Unsupported physical disk geometry.");
    }
    public static WindowsDiskStorage Open(string physicalPath, bool writable)
    {
        if (!physicalPath.StartsWith(@"\\.\PhysicalDrive", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(physicalPath[17..], out var number) || number < 0)
            throw new ArgumentException("A resolved native physical disk path is required.");
        var handle = OpenHandle(physicalPath, writable);
        try { return new(physicalPath, handle); } catch { handle.Dispose(); throw; }
    }
    public static async Task<WindowsDiskStorage> ResolveRamAsync(WindowsRamDisk provider, RamDiskSnapshot expected, CancellationToken token)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(30);
        do
        {
            token.ThrowIfCancellationRequested(); provider.Query(expected).RequireSameCreation(expected);
            for (var number = 0; number < 256; number++)
            {
                WindowsDiskStorage? disk = null;
                var retained = false;
                try
                {
                    disk = Open(@"\\.\PhysicalDrive" + number, true);
                    if (disk.Serial() == expected.DeviceSerial)
                    {
                        var address = disk.Io(0x41018, [], 8); // IOCTL_SCSI_GET_ADDRESS
                        if (address[4] != provider.PortNumber || address[7] != expected.Slot ||
                            disk.CapacityBytes != expected.CapacityBytes || disk.SectorBytes != expected.SectorBytes)
                            throw new IOException("RAM disk serial, SCSI address and geometry disagree.");
                        provider.Query(expected).RequireSameCreation(expected); retained = true; return disk;
                    }
                }
                catch (Win32Exception) { }
                finally { if (!retained) disk?.Dispose(); }
            }
            await Task.Delay(100, token);
        } while (DateTimeOffset.UtcNow < until);
        throw new IOException("Windows did not publish the expected RAM disk within 30 seconds.");
    }
    public string Serial()
    {
        var descriptor = Io(0x2D1400, new byte[12], 4096);
        var offset = BitConverter.ToUInt32(descriptor, 24);
        if (offset == 0 || offset >= descriptor.Length) return "";
        var end = Array.IndexOf(descriptor, (byte)0, (int)offset);
        if (end < 0) throw new InvalidDataException("Unterminated disk serial.");
        return Encoding.ASCII.GetString(descriptor, (int)offset, end - (int)offset).Trim();
    }
    public void SetOffline(bool offline)
    {
        var attributes = new byte[40]; BitConverter.GetBytes(40U).CopyTo(attributes, 0);
        BitConverter.GetBytes(offline ? 1UL : 0UL).CopyTo(attributes, 8); BitConverter.GetBytes(1UL).CopyTo(attributes, 16);
        Io(0x7C0F4, attributes, 0);
        var actual = BitConverter.ToUInt64(Io(0x700F0, [], 16), 8);
        if ((actual & 1) != (offline ? 1UL : 0UL)) throw new IOException("Windows did not apply the requested disk isolation state.");
    }
    public ManagedDiskLayout Layout()
    {
        var bytes = Io(0x70050, [], 48 + 144 * 128);
        if (BitConverter.ToUInt32(bytes) != 1) throw new NotSupportedException("Only GPT images can be activated.");
        var count = BitConverter.ToUInt32(bytes, 4);
        if (count is 0 or > 128 || bytes.Length < 48 + count * 144) throw new InvalidDataException("Invalid GPT disk layout reply.");
        var parts = new List<ManagedPartition>();
        for (var i = 0; i < count; i++)
        {
            var offset = 48 + i * 144;
            if (BitConverter.ToUInt64(bytes, offset + 16) == 0) continue;
            if (BitConverter.ToUInt32(bytes, offset) != 1) throw new InvalidDataException("Mixed partition styles.");
            parts.Add(new(new Guid(bytes.AsSpan(offset + 48, 16)), new Guid(bytes.AsSpan(offset + 32, 16)),
                BitConverter.ToUInt64(bytes, offset + 8), BitConverter.ToUInt64(bytes, offset + 16), BitConverter.ToUInt64(bytes, offset + 64)));
        }
        var result = new ManagedDiskLayout(new Guid(bytes.AsSpan(8, 16)), parts); result.Validate(CapacityBytes, SectorBytes); return result;
    }
    public void RefuseOnlineClone(ManagedDiskLayout layout)
        => RefuseOnlineClone(layout, Number);
    public static void RefuseOnlineClone(ManagedDiskLayout layout, int? excludedNumber = null)
    {
        for (var number = 0; number < 256; number++)
        {
            if (number == excludedNumber) continue;
            try
            {
                using var other = Open(@"\\.\PhysicalDrive" + number, false);
                var foreign = other.Io(0x70050, [], 48 + 144 * 128);
                // Foreign layouts may include EFI/recovery partitions. Read identity
                // directly rather than applying our managed-layout policy to another disk.
                if (foreign.Length < 48 || BitConverter.ToUInt32(foreign) != 1) continue;
                if ((BitConverter.ToUInt64(other.Io(0x700F0, [], 16), 8) & 1) == 0 && new Guid(foreign.AsSpan(8, 16)) == layout.DiskId)
                    throw new IOException("Another online disk has this GPT identity. Stop that disk before publishing this image.");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3 or 21 or 55 or 1167) { }
        }
    }
    public void InitializeNewGpt()
    {
        var id = Guid.NewGuid();
        var create = new byte[24]; BitConverter.GetBytes(1U).CopyTo(create, 0); id.TryWriteBytes(create.AsSpan(4)); BitConverter.GetBytes(128U).CopyTo(create, 20);
        Io(0x7C058, create, 0);
        var layout = new byte[48 + 144];
        BitConverter.GetBytes(1U).CopyTo(layout, 0); BitConverter.GetBytes(1U).CopyTo(layout, 4); id.TryWriteBytes(layout.AsSpan(8));
        const ulong start = 1UL << 20;
        var length = (CapacityBytes - 2 * start) / start * start;
        BitConverter.GetBytes(start).CopyTo(layout, 24); BitConverter.GetBytes(length).CopyTo(layout, 32); BitConverter.GetBytes(128U).CopyTo(layout, 40);
        BitConverter.GetBytes(1U).CopyTo(layout, 48); BitConverter.GetBytes(start).CopyTo(layout, 56); BitConverter.GetBytes(length).CopyTo(layout, 64);
        BitConverter.GetBytes(1U).CopyTo(layout, 72); layout[76] = 1;
        ManagedDiskLayout.BasicData.TryWriteBytes(layout.AsSpan(80)); Guid.NewGuid().TryWriteBytes(layout.AsSpan(96));
        BitConverter.GetBytes(0x8000000000000000UL).CopyTo(layout, 112); // No default drive letter before Ready.
        Encoding.Unicode.GetBytes("QueueCache").CopyTo(layout, 120);
        Io(0x7C054, layout, 0); Io(0x70140, [], 0);
        Layout().Validate(CapacityBytes, SectorBytes);
    }
    public async Task<string> WaitVolumeAsync(CancellationToken token)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(30);
        do
        {
            token.ThrowIfCancellationRequested();
            var found = Volumes(Number);
            if (found.Count == 1) return found[0];
            if (found.Count > 1) throw new IOException("The disk exposes more than one data volume.");
            await Task.Delay(100, token);
        } while (DateTimeOffset.UtcNow < until);
        throw new IOException("Windows did not enumerate the owned data volume.");
    }
    public static IReadOnlyList<string> Volumes(int diskNumber)
    {
        var found = new List<string>(); var name = new StringBuilder(1024);
        var search = FindFirstVolumeW(name, (uint)name.Capacity);
        if (search == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            do
            {
                try { if (VolumeDisk(name.ToString()) == diskNumber) found.Add(name.ToString()); }
                catch (Win32Exception) { }
            } while (FindNextVolumeW(search, name, (uint)name.Capacity));
            if (Marshal.GetLastWin32Error() != 18) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { FindVolumeClose(search); }
        return found;
    }
    public static int VolumeDisk(string volume)
    {
        _ = VolumeIds.Parse(volume);
        using var handle = OpenHandle(volume.TrimEnd('\\'), false);
        var bytes = Control(handle, 0x560000, [], 32);
        if (bytes.Length < 32 || BitConverter.ToUInt32(bytes) != 1) throw new IOException("Only single-disk volumes are supported.");
        return checked((int)BitConverter.ToUInt32(bytes, 8));
    }
    public static string FileSystem(string volume)
    {
        var fs = new StringBuilder(64);
        if (!GetVolumeInformationW(volume, null, 0, out _, out _, out _, fs, (uint)fs.Capacity)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return fs.ToString();
    }
    public static Task FormatNtfsAsync(string volume, int expectedDisk, string label, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (VolumeDisk(volume) != expectedDisk) throw new IOException("The format target changed.");
        var escaped = volume.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        using var target = new ManagementObject("Win32_Volume.DeviceID=\"" + escaped + "\"");
        using var parameters = target.GetMethodParameters("Format");
        parameters["FileSystem"] = "NTFS"; parameters["QuickFormat"] = true; parameters["ClusterSize"] = 4096U;
        parameters["Label"] = label; parameters["EnableCompression"] = false;
        // WMI cancellation does not cancel formatting. Once started, wait and report its actual outcome.
        using var result = target.InvokeMethod("Format", parameters, new InvokeMethodOptions { Timeout = TimeSpan.FromMinutes(5) });
        if (Convert.ToUInt32(result["ReturnValue"]) != 0) throw new IOException("Windows refused formatting the owned volume: " + result["ReturnValue"]);
        if (VolumeDisk(volume) != expectedDisk || FileSystem(volume) != "NTFS") throw new IOException("Formatted volume identity or filesystem changed.");
    }, CancellationToken.None);
    public static void AssignLetter(string volume, char letter)
    {
        if (letter is < 'D' or > 'Z') throw new ArgumentException("Invalid drive letter.");
        var root = letter + @":\";
        var existing = new StringBuilder(1024);
        if (GetVolumeNameForVolumeMountPointW(root, existing, (uint)existing.Capacity))
        {
            if (existing.ToString().Equals(volume, StringComparison.OrdinalIgnoreCase)) return;
            throw new IOException($"Drive {letter}: is already in use.");
        }
        if (!SetVolumeMountPointW(root, volume)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not assign the selected letter.");
    }
    public static bool HasLetter(string volume, char letter)
    {
        var current = new StringBuilder(1024);
        return GetVolumeNameForVolumeMountPointW(letter + @":\", current, (uint)current.Capacity) &&
            current.ToString().Equals(volume, StringComparison.OrdinalIgnoreCase);
    }
    public static void RemoveLetter(string volume, char letter)
    {
        var root = letter + @":\"; var current = new StringBuilder(1024);
        if (GetVolumeNameForVolumeMountPointW(root, current, (uint)current.Capacity) && current.ToString().Equals(volume, StringComparison.OrdinalIgnoreCase) &&
            !DeleteVolumeMountPointW(root)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public static IDisposable LockVolume(string volume, int expectedDisk, bool dismount = false)
    {
        if (VolumeDisk(volume) != expectedDisk) throw new IOException("The selected volume no longer belongs to the managed disk.");
        var handle = OpenHandle(volume.TrimEnd('\\'), true);
        try
        {
            Control(handle, 0x90018, [], 0);
            if (!FlushFileBuffers(handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (dismount) Control(handle, 0x90020, [], 0);
            return new VolumeLock(handle);
        }
        catch { handle.Dispose(); throw; }
    }
    private sealed class VolumeLock(SafeFileHandle handle) : IDisposable
    {
        // Windows releases FSCTL_LOCK_VOLUME when the handle closes, including after device removal.
        public void Dispose() => handle.Dispose();
    }
    private void Range(ulong offset, int bytes)
    {
        if (offset % SectorBytes != 0 || bytes <= 0 || bytes % SectorBytes != 0 || offset > CapacityBytes || (ulong)bytes > CapacityBytes - offset)
            throw new ArgumentException("Invalid aligned logical-sector range.");
    }
    public ValueTask<int> ReadAsync(ulong offset, Memory<byte> buffer, CancellationToken token)
    { Range(offset, buffer.Length); token.ThrowIfCancellationRequested(); return ValueTask.FromResult(RandomAccess.Read(handle, buffer.Span, checked((long)offset))); }
    public ValueTask WriteAsync(ulong offset, ReadOnlyMemory<byte> buffer, CancellationToken token)
    { Range(offset, buffer.Length); token.ThrowIfCancellationRequested(); RandomAccess.Write(handle, buffer.Span, checked((long)offset)); return ValueTask.CompletedTask; }
    public ValueTask FlushAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); if (!FlushFileBuffers(handle)) throw new Win32Exception(Marshal.GetLastWin32Error()); return ValueTask.CompletedTask; }
    public void Dispose() => handle.Dispose();
    private byte[] Io(uint code, byte[] input, int output) => Control(handle, code, input, output);
    internal static SafeFileHandle OpenHandle(string path, bool writable)
    {
        var result = CreateFileW(path, writable ? 0xC0000000U : 0x80000000U, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (result.IsInvalid) { result.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open the owned disk or volume."); } return result;
    }
    internal static byte[] Control(SafeFileHandle handle, uint code, byte[] input, int output)
    {
        var result = new byte[output];
        if (!DeviceIoControl(handle, code, input, (uint)input.Length, result, (uint)result.Length, out var used, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (used > output) throw new InvalidDataException("Invalid native disk reply length.");
        return result[..(int)used];
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputBytes, byte[] output, uint outputBytes, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FlushFileBuffers(SafeFileHandle handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr FindFirstVolumeW(StringBuilder name, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindNextVolumeW(IntPtr find, StringBuilder name, uint size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindVolumeClose(IntPtr find);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeInformationW(string root, StringBuilder? name, uint nameSize, out uint serial, out uint componentLength, out uint flags, StringBuilder fs, uint fsSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeNameForVolumeMountPointW(string mount, StringBuilder name, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetVolumeMountPointW(string mount, string volume);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteVolumeMountPointW(string mount);
}
