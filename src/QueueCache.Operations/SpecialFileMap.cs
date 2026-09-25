using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using QueueCache.Management;

namespace QueueCache.Operations;

/// <summary>
/// T085 verification aid: the current disk ranges of every paging file (pagefile.sys, swapfile.sys and configured
/// paging files) on one physical disk. The driver recognises paging-file requests by file object, not by these
/// ranges. They are sent as an observe-only reference set so recognition misses can be counted.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SpecialFileMap
{
    public sealed record Result(IReadOnlyList<string> Files, IReadOnlyList<DiskRange> Ranges);

    public static Result PagingFiles(DiskTarget target)
    {
        var ranges = new List<DiskRange>();
        var files = new List<string>();
        var entries = PagingFileEntries();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                continue;
            var letter = char.ToUpperInvariant(drive.Name[0]);
            var extents = VolumeExtents(letter);
            if (extents.All(extent => extent.Disk != target.Number))
                continue;
            if (extents.Count != 1)
                throw new NotSupportedException($"Volume {letter}: spans several disk extents.");
            var root = $"{letter}:\\";
            var candidates = new List<string> { root + "pagefile.sys", root + "swapfile.sys" };
            foreach (var entry in entries)
            {
                SpecialRangeMap.IsFixedPagingFileEntry(entry, letter, out var onVolume);
                var path = entry.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                if (onVolume && !path.StartsWith('?'))
                    candidates.Add(path);
            }
            foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(path))
                    continue;
                files.Add(path);
                ranges.AddRange(FileDiskRanges(path, root, extents[0].Start));
            }
        }
        return new(files, SpecialRangeMap.Normalize(ranges));
    }

    private static string[] PagingFileEntries()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
        return key?.GetValue("PagingFiles") as string[] ?? [];
    }

    private readonly record struct Extent(int Disk, long Start);

    private static List<Extent> VolumeExtents(char letter)
    {
        using var volume = CreateFileW($@"\\.\{letter}:", 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (volume.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var output = new byte[16 + 24 * 32];
        if (!DeviceIoControl(volume, 0x00560000 /* IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS */, null, 0, output, output.Length,
                out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var count = BitConverter.ToInt32(output, 0);
        var extents = new List<Extent>();
        for (var index = 0; index < count; index++)
            extents.Add(new(BitConverter.ToInt32(output, 8 + index * 24), BitConverter.ToInt64(output, 16 + index * 24)));
        return extents;
    }

    // Attribute-only handles do not conflict with the exclusive share mode of in-use paging files.
    private static IEnumerable<DiskRange> FileDiskRanges(string path, string root, long partitionStart)
    {
        if (!GetDiskFreeSpaceW(root, out var sectorsPerCluster, out var bytesPerSector, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var clusterBytes = (long)sectorsPerCluster * bytesPerSector;
        using var file = CreateFileW(path, 0x80 /* FILE_READ_ATTRIBUTES */, 7, IntPtr.Zero, 3,
            0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS */, IntPtr.Zero);
        if (file.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open " + path + " for its layout.");
        var ranges = new List<DiskRange>();
        var output = new byte[64 * 1024];
        long vcn = 0;
        while (true)
        {
            var ok = DeviceIoControl(file, 0x00090073 /* FSCTL_GET_RETRIEVAL_POINTERS */, BitConverter.GetBytes(vcn), 8,
                output, output.Length, out _, IntPtr.Zero);
            var error = ok ? 0 : Marshal.GetLastWin32Error();
            if (error == 38 /* ERROR_HANDLE_EOF */)
                break;
            if (!ok && error != 234 /* ERROR_MORE_DATA */)
                throw new Win32Exception(error, "Cannot read the layout of " + path + ".");
            var count = BitConverter.ToInt32(output, 0);
            var current = BitConverter.ToInt64(output, 8);
            for (var index = 0; index < count; index++)
            {
                var next = BitConverter.ToInt64(output, 16 + index * 16);
                var lcn = BitConverter.ToInt64(output, 24 + index * 16);
                if (lcn >= 0)
                    ranges.Add(new(partitionStart + lcn * clusterBytes, (next - current) * clusterBytes));
                current = next;
            }
            if (ok || count == 0)
                break;
            vcn = current;
        }
        return ranges;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation,
        uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputLength,
        byte[] output, int outputLength, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster, out uint bytesPerSector,
        out uint freeClusters, out uint totalClusters);
}
