using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace QueueCache.Operations;

/// <summary>A disk as its class driver reports it. <see cref="PartitionCount"/> leaves out unused MBR slots and the
/// GPT Microsoft Reserved partition, which has no file system to flush.</summary>
public sealed record StorageDisk(int Number, string Name, long Bytes, string Instance, int PartitionCount, bool IsReadOnly, bool IsOffline);

/// <summary>One disk range of a volume.</summary>
public sealed record StorageExtent(int Disk, long Start, long Length);

/// <summary>A mounted volume: its mount-manager name (\\?\Volume{guid}\), its drive letter (Q:) if it has one and the
/// disk ranges it occupies (none when the volume does not report them, for example a CD).</summary>
public sealed record StorageVolume(string Path, string? Letter, string Label, string FileSystem, bool IsFixed, IReadOnlyList<StorageExtent> Extents);

/// <summary>Disks and volumes from native Windows sources only: the mount manager, the disk interfaces SetupAPI
/// lists, storage IOCTLs that need no access rights, the SystemPartition setup value and the kernel's page file list.
/// The Storage Management provider behind Get-Disk, Get-Partition and Get-Volume is not used: on some machines (an
/// iSCSI-booted Windows, for example) it reports no disks at all.</summary>
/// <param name="WindowsVolume">The volume holding the Windows directory (the Storage provider's boot partition).</param>
/// <param name="SystemVolume">The partition Windows started from: the EFI system partition, or the MBR active one.</param>
/// <param name="PagingVolumes">Every volume holding an active page file.</param>
public sealed record StorageInventory(IReadOnlyList<StorageDisk> Disks, IReadOnlyList<StorageVolume> Volumes,
    string? WindowsVolume, string? SystemVolume, IReadOnlyList<string> PagingVolumes)
{
    /// <summary>Lettered fixed volumes on a single disk range of a listed disk, by letter. The role flags are the
    /// volume's own: it holds Windows, it is the system partition, it holds a page file.</summary>
    public IReadOnlyList<VolumeDescription> LetteredVolumes()
    {
        var disks = DisksByNumber();
        return Volumes.Where(v => v.Letter is not null && v.IsFixed && v.Extents.Count == 1 && disks.ContainsKey(v.Extents[0].Disk))
            .OrderBy(v => v.Letter, StringComparer.OrdinalIgnoreCase)
            .Select(v => (Volume: v, Disk: disks[v.Extents[0].Disk]))
            .Select(x => new VolumeDescription(x.Volume.Letter!, x.Volume.Label, x.Volume.FileSystem, x.Volume.Extents[0].Length, x.Volume.Path,
                x.Disk.Number, x.Disk.Name, x.Disk.Instance, x.Disk.Bytes, Same(WindowsVolume, x.Volume.Path), Same(SystemVolume, x.Volume.Path),
                PagingVolumes.Any(p => Same(p, x.Volume.Path))))
            .ToArray();
    }

    /// <summary>Every disk by number with its lettered single-range volumes in disk order. The role flags are the
    /// disk's: it holds the Windows volume, the system partition or a page file.</summary>
    public IReadOnlyList<DiskDescription> DiskList()
    {
        return DisksByNumber().Values.OrderBy(d => d.Number).Select(d => new DiskDescription(d.Number, d.Name, d.Bytes, d.Instance,
            Volumes.Where(v => v.Letter is not null && v.Extents.Count == 1 && v.Extents[0].Disk == d.Number)
                .OrderBy(v => v.Extents[0].Start).Select(v => v.Letter!).ToArray(),
            OnDisk(WindowsVolume, d.Number), OnDisk(SystemVolume, d.Number), PagingVolumes.Any(p => OnDisk(p, d.Number)),
            d.PartitionCount, d.IsReadOnly, d.IsOffline)).ToArray();
    }

    /// <summary>The disk holding a lettered volume, with the disk's role flags (as in <see cref="DiskList"/>).</summary>
    public (StorageDisk Disk, bool IsBoot, bool IsSystem, bool IsPaging) DiskOf(char letter)
    {
        var name = $"{char.ToUpperInvariant(letter)}:";
        var volume = Volumes.SingleOrDefault(v => string.Equals(v.Letter, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException($"{name} is not a mounted volume.");
        if (volume.Extents.Count != 1)
            throw new IOException("The selected volume must have exactly one disk extent.");
        var disk = DisksByNumber().GetValueOrDefault(volume.Extents[0].Disk)
            ?? throw new IOException($"Windows lists no disk {volume.Extents[0].Disk} for {name}.");
        return (disk, OnDisk(WindowsVolume, disk.Number), OnDisk(SystemVolume, disk.Number), PagingVolumes.Any(p => OnDisk(p, disk.Number)));
    }

    /// <summary>A page file name from the kernel (\??\C:\pagefile.sys) as a Win32 path (C:\pagefile.sys).</summary>
    public static string PageFilePath(string kernelName) =>
        kernelName.StartsWith(@"\??\", StringComparison.Ordinal) ? kernelName[4..] : kernelName;

    /// <summary>Volume{guid} for QueryDosDevice, from \\?\Volume{guid}\.</summary>
    public static string DosName(string volumePath)
    {
        _ = VolumeIds.Parse(volumePath);
        return volumePath[4..^1];
    }

    private Dictionary<int, StorageDisk> DisksByNumber()
    {
        var byNumber = new Dictionary<int, StorageDisk>();
        foreach (var disk in Disks)
            if (!byNumber.TryAdd(disk.Number, disk))
                throw new IOException($"Windows lists disk {disk.Number} twice.");
        return byNumber;
    }

    private bool OnDisk(string? path, int disk) => path is not null && Volumes.Any(v => Same(path, v.Path) && v.Extents.Any(e => e.Disk == disk));

    private static bool Same(string? a, string b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static readonly Guid DiskInterface = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");
    // GPT type of the Microsoft Reserved partition.
    private static readonly Guid Reserved = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");

    [SupportedOSPlatform("windows")]
    public static StorageInventory Read(CancellationToken token = default)
    {
        var disks = ReadDisks(token);
        var volumes = new List<StorageVolume>();
        var devices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in VolumePaths())
        {
            token.ThrowIfCancellationRequested();
            volumes.Add(ReadVolume(path));
            if (QueryDosDevice(DosName(path)) is { } device)
                devices[device] = path;
        }
        // Set by Windows at every start, for example \Device\HarddiskVolume1.
        var system = Registry.LocalMachine.OpenSubKey(@"SYSTEM\Setup")?.GetValue("SystemPartition") as string;
        return new(disks, volumes, VolumeOf(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
            system is not null && devices.TryGetValue(system, out var systemPath) ? systemPath : null,
            PageFiles().Select(name => VolumeOf(PageFilePath(name))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [SupportedOSPlatform("windows")]
    private static List<StorageDisk> ReadDisks(CancellationToken token)
    {
        var disks = new List<StorageDisk>();
        foreach (var (instance, path) in DiskTarget.InterfacePaths(DiskInterface, token))
        {
            using var handle = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            // Removed since SetupAPI listed it, or a disk without media: neither can hold a volume to cache or eject.
            if (handle.IsInvalid || Control(handle, 0x2d1080, [], 12) is not { Length: 12 } number ||
                BinaryPrimitives.ReadInt32LittleEndian(number) != 7 || Control(handle, 0x700a0, [], 256) is not { Length: >= 32 } geometry)
                continue;
            var bytes = BinaryPrimitives.ReadInt64LittleEndian(geometry.AsSpan(24));
            var attributes = Control(handle, 0x700f0, [], 16) is { Length: 16 } read ? BinaryPrimitives.ReadUInt64LittleEndian(read.AsSpan(8)) : 0;
            var writeProtected = !DeviceIoControl(handle, 0x70024, null, 0, null, 0, out _, IntPtr.Zero) && Marshal.GetLastWin32Error() == 19;
            var index = BinaryPrimitives.ReadInt32LittleEndian(number.AsSpan(4));
            disks.Add(new(index, DeviceName(handle) is { Length: > 0 } name ? name : $"Disk {index}", bytes, instance,
                PartitionCount(Control(handle, 0x70050, [], 48 + 144 * 128)), (attributes & 2) != 0 || writeProtected, (attributes & 1) != 0));
        }
        return disks;
    }

    /// <summary>IOCTL_STORAGE_QUERY_PROPERTY's vendor and product, as the Storage provider names a disk ("Msft Virtual Disk").</summary>
    private static string DeviceName(SafeFileHandle handle)
    {
        var descriptor = Control(handle, 0x2d1400, new byte[12], 1024) ?? [];
        string Text(int field)
        {
            var offset = descriptor.Length >= field + 4 ? BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(field)) : 0;
            if (offset <= 0 || offset >= descriptor.Length)
                return "";
            var end = Array.IndexOf(descriptor, (byte)0, offset);
            return Encoding.ASCII.GetString(descriptor, offset, (end < 0 ? descriptor.Length : end) - offset).Trim();
        }
        return string.Join(' ', new[] { Text(12), Text(16) }.Where(s => s.Length != 0));
    }

    /// <summary>Partitions in an IOCTL_DISK_GET_DRIVE_LAYOUT_EX reply, without unused MBR slots and the GPT Microsoft
    /// Reserved partition; 0 when the layout is unreadable.</summary>
    public static int PartitionCount(byte[]? layout)
    {
        if (layout is not { Length: >= 48 })
            return 0;
        var style = BinaryPrimitives.ReadUInt32LittleEndian(layout);
        var count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(layout.AsSpan(4)), (uint)((layout.Length - 48) / 144));
        var used = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = layout.AsSpan(48 + 144 * i);
            if (style == 0 ? entry[32] != 0 : style == 1 && new Guid(entry.Slice(32, 16)) is var type && type != Guid.Empty && type != Reserved)
                used++;
        }
        return used;
    }

    [SupportedOSPlatform("windows")]
    private static StorageVolume ReadVolume(string path)
    {
        var label = new StringBuilder(261);
        var format = new StringBuilder(261);
        // Unformatted and locked volumes have no readable file system.
        if (!GetVolumeInformationW(path, label, (uint)label.Capacity, out _, out _, out _, format, (uint)format.Capacity))
        {
            label.Clear();
            format.Clear();
        }
        byte[]? reply;
        using (var handle = CreateFileW(path.TrimEnd('\\'), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            reply = handle.IsInvalid ? null : Control(handle, 0x560000, [], 8 + 24 * 64);
        return new(path, MountLetter(path), label.ToString(), format.ToString(), GetDriveTypeW(path) == 3, Extents(reply));
    }

    /// <summary>The ranges in an IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS reply; none when it failed.</summary>
    public static IReadOnlyList<StorageExtent> Extents(byte[]? reply)
    {
        if (reply is not { Length: >= 8 })
            return [];
        var count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(reply), (uint)(reply.Length - 8) / 24);
        var extents = new StorageExtent[count];
        for (var i = 0; i < count; i++)
        {
            var extent = reply.AsSpan(8 + 24 * i);
            extents[i] = new(BinaryPrimitives.ReadInt32LittleEndian(extent), BinaryPrimitives.ReadInt64LittleEndian(extent[8..]),
                BinaryPrimitives.ReadInt64LittleEndian(extent[16..]));
        }
        return extents;
    }

    [SupportedOSPlatform("windows")]
    private static List<string> VolumePaths()
    {
        var paths = new List<string>();
        var name = new StringBuilder(1024);
        var search = FindFirstVolumeW(name, (uint)name.Capacity);
        if (search == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            do paths.Add(name.ToString());
            while (FindNextVolumeW(search, name, (uint)name.Capacity));
            if (Marshal.GetLastWin32Error() != 18)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { FindVolumeClose(search); }
        return paths;
    }

    [SupportedOSPlatform("windows")]
    private static string? MountLetter(string path)
    {
        var names = new char[1024];
        if (!GetVolumePathNamesForVolumeNameW(path, names, (uint)names.Length, out var used))
        {
            if (Marshal.GetLastWin32Error() != 234)
                return null;
            names = new char[used];
            if (!GetVolumePathNamesForVolumeNameW(path, names, (uint)names.Length, out used))
                return null;
        }
        return new string(names, 0, (int)Math.Min(used, (uint)names.Length)).Split('\0')
            .FirstOrDefault(n => n.Length == 3 && char.IsAsciiLetter(n[0]) && n[1] == ':' && n[2] == '\\') is { } root
            ? $"{char.ToUpperInvariant(root[0])}:" : null;
    }

    /// <summary>The mount-manager name of the volume holding a file or folder.</summary>
    [SupportedOSPlatform("windows")]
    private static string VolumeOf(string path)
    {
        var root = new StringBuilder(1024);
        var name = new StringBuilder(64);
        if (!GetVolumePathNameW(path, root, (uint)root.Capacity) || !GetVolumeNameForVolumeMountPointW(root.ToString(), name, (uint)name.Capacity))
            throw new IOException($"Cannot find the volume holding {path}.", new Win32Exception(Marshal.GetLastWin32Error()));
        return name.ToString();
    }

    [SupportedOSPlatform("windows")]
    private static string? QueryDosDevice(string name)
    {
        var target = new char[1024];
        var used = QueryDosDeviceW(name, target, (uint)target.Length);
        return used == 0 ? null : new string(target, 0, (int)used).Split('\0')[0];
    }

    /// <summary>Active page files from the kernel (SystemPageFileInformation), as \??\C:\pagefile.sys.</summary>
    [SupportedOSPlatform("windows")]
    private static List<string> PageFiles()
    {
        for (var length = 4096; ; )
        {
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                var status = NtQuerySystemInformation(18, buffer, length, out var needed);
                if (status == unchecked((int)0xC0000004))
                {
                    length = Math.Max(length * 2, needed);
                    continue;
                }
                if (status != 0)
                    throw new IOException($"Windows did not list its page files (NTSTATUS 0x{status:X8}).");
                var names = new List<string>();
                // Four ULONGs, then the name's UNICODE_STRING, whose buffer pointer is pointer-aligned.
                var pointer = IntPtr.Size == 8 ? 24 : 20;
                for (var offset = 0; needed >= offset + pointer + IntPtr.Size; )
                {
                    var entry = IntPtr.Add(buffer, offset);
                    names.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry, pointer), (ushort)Marshal.ReadInt16(entry, 16) / 2));
                    var next = Marshal.ReadInt32(entry);
                    if (next <= 0)
                        break;
                    offset += next;
                }
                return names;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    private static byte[]? Control(SafeFileHandle handle, uint code, byte[] input, int output)
    {
        var result = new byte[output];
        return DeviceIoControl(handle, code, input, (uint)input.Length, result, (uint)result.Length, out var used, IntPtr.Zero) && used <= output
            ? result[..(int)used] : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, uint inputBytes,
        [Out] byte[]? output, uint outputBytes, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr FindFirstVolumeW(StringBuilder name, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextVolumeW(IntPtr find, StringBuilder name, uint size);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindVolumeClose(IntPtr find);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNamesForVolumeNameW(string volume, [Out] char[] names, uint length, out uint used);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string root, StringBuilder label, uint labelSize, out uint serial,
        out uint componentLength, out uint flags, StringBuilder fileSystem, uint fileSystemSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetDriveTypeW(string root);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string path, StringBuilder root, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string name, [Out] char[] target, uint length);
    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, IntPtr information, int length, out int returned);
}
