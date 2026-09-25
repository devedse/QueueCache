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

    private static IEnumerable<DiskRange> FileDiskRanges(string path, string root, long partitionStart)
    {
        if (!GetDiskFreeSpaceW(root, out var sectorsPerCluster, out var bytesPerSector, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var clusterBytes = (long)sectorsPerCluster * bytesPerSector;
        using var file = CreateFileW(path, 0x80 /* FILE_READ_ATTRIBUTES */, 7, IntPtr.Zero, 3,
            0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS */, IntPtr.Zero);
        if (file.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            // NTFS refuses every open of an in-use paging file, even attribute-only. Read its file record instead.
            if (error != 32 /* ERROR_SHARING_VIOLATION */)
                throw new Win32Exception(error, "Cannot open " + path + " for its layout.");
            return DataRuns(FileRecord(path, root))
                .Select(run => new DiskRange(partitionStart + run.Lcn * clusterBytes, run.Clusters * clusterBytes))
                .ToList();
        }
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

    /// <summary>Allocated cluster runs of a file's unnamed $DATA attribute, read from its NTFS file record. Sparse
    /// runs are skipped. A layout continued in an attribute list is refused rather than reported partially.</summary>
    internal static List<(long Lcn, long Clusters)> DataRuns(byte[] record)
    {
        if (record.Length < 48 || BitConverter.ToUInt32(record, 0) != 0x454C4946 /* "FILE" */)
            throw new IOException("Not an NTFS file record.");
        ApplyFixups(record);
        var offset = (int)BitConverter.ToUInt16(record, 0x14);
        while (offset + 16 <= record.Length)
        {
            var type = BitConverter.ToUInt32(record, offset);
            if (type == 0xFFFFFFFF)
                break;
            var length = BitConverter.ToInt32(record, offset + 4);
            if (length < 16 || offset + length > record.Length)
                throw new IOException("Corrupt NTFS attribute header.");
            if (type == 0x80 && record[offset + 9] == 0)
            {
                if (record[offset + 8] == 0)
                    return [];
                var highest = BitConverter.ToInt64(record, offset + 0x18);
                if (BitConverter.ToInt64(record, offset + 0x10) != 0)
                    throw new NotSupportedException("The file's data attribute starts in an NTFS attribute list.");
                var runs = new List<(long, long)>();
                var position = offset + BitConverter.ToUInt16(record, offset + 0x20);
                long lcn = 0, vcn = 0;
                while (position < offset + length && record[position] != 0)
                {
                    int lengthBytes = record[position] & 0xF, deltaBytes = record[position] >> 4;
                    if (lengthBytes == 0 || lengthBytes > 8 || deltaBytes > 8 ||
                        position + 1 + lengthBytes + deltaBytes > offset + length)
                        throw new IOException("Corrupt NTFS mapping pairs.");
                    var clusters = ReadLittleEndian(record, position + 1, lengthBytes, signed: false);
                    if (deltaBytes != 0)
                    {
                        lcn += ReadLittleEndian(record, position + 1 + lengthBytes, deltaBytes, signed: true);
                        runs.Add((lcn, clusters));
                    }
                    vcn += clusters;
                    position += 1 + lengthBytes + deltaBytes;
                }
                if (vcn != highest + 1)
                    throw new NotSupportedException("The file's layout continues in an NTFS attribute list.");
                return runs;
            }
            offset += length;
        }
        throw new NotSupportedException("The file's data attribute is in an NTFS attribute list.");
    }

    private static long ReadLittleEndian(byte[] data, int start, int count, bool signed)
    {
        long value = 0;
        for (var index = count - 1; index >= 0; index--)
            value = (value << 8) | data[start + index];
        if (signed && count < 8 && (data[start + count - 1] & 0x80) != 0)
            value |= -1L << (count * 8);
        return value;
    }

    // Undo the update sequence protection unless NTFS already returned the record fixed up (then the sector
    // tails no longer all hold the sequence number).
    private static void ApplyFixups(byte[] record)
    {
        int usaOffset = BitConverter.ToUInt16(record, 4), usaCount = BitConverter.ToUInt16(record, 6);
        if (usaCount < 2 || usaOffset + usaCount * 2 > record.Length || (usaCount - 1) * 512 > record.Length)
            throw new IOException("Corrupt NTFS update sequence array.");
        var sequence = BitConverter.ToUInt16(record, usaOffset);
        for (var sector = 1; sector < usaCount; sector++)
            if (BitConverter.ToUInt16(record, sector * 512 - 2) != sequence)
                return;
        for (var sector = 1; sector < usaCount; sector++)
        {
            record[sector * 512 - 2] = record[usaOffset + sector * 2];
            record[sector * 512 - 1] = record[usaOffset + sector * 2 + 1];
        }
    }

    // The file ID comes from its directory listing (the file itself is never opened), the record from the volume.
    private static byte[] FileRecord(string path, string root)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new IOException("No parent directory for " + path);
        var name = Path.GetFileName(path);
        long? fileId = null;
        using (var handle = CreateFileW(directory, 0x1 /* FILE_LIST_DIRECTORY */, 7, IntPtr.Zero, 3,
                   0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS */, IntPtr.Zero))
        {
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot list " + directory);
            var buffer = new byte[64 * 1024];
            var infoClass = 11; // FileIdBothDirectoryRestartInfo first, then FileIdBothDirectoryInfo
            while (fileId is null && GetFileInformationByHandleEx(handle, infoClass, buffer, buffer.Length))
            {
                infoClass = 10;
                for (var entry = 0; ;)
                {
                    var nameBytes = BitConverter.ToInt32(buffer, entry + 60);
                    var entryName = System.Text.Encoding.Unicode.GetString(buffer, entry + 104, nameBytes);
                    if (string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        fileId = BitConverter.ToInt64(buffer, entry + 96);
                        break;
                    }
                    var next = BitConverter.ToInt32(buffer, entry);
                    if (next == 0)
                        break;
                    entry += next;
                }
            }
            if (fileId is null)
                throw new FileNotFoundException("Cannot find the file ID of " + path);
        }
        using var volume = CreateFileW(@"\\.\" + root[0] + ":", 0x80000000 /* GENERIC_READ */, 7, IntPtr.Zero, 3, 0,
            IntPtr.Zero);
        if (volume.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open volume " + root);
        var output = new byte[12 + 64 * 1024];
        if (!DeviceIoControl(volume, 0x00090068 /* FSCTL_GET_NTFS_FILE_RECORD */, BitConverter.GetBytes(fileId.Value), 8,
                output, output.Length, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the NTFS file record of " + path);
        const long recordMask = 0xFFFFFFFFFFFF;
        if ((BitConverter.ToInt64(output, 0) & recordMask) != (fileId.Value & recordMask))
            throw new IOException("NTFS returned a different file record for " + path);
        var recordBytes = BitConverter.ToInt32(output, 8);
        if (recordBytes <= 0 || recordBytes > output.Length - 12)
            throw new IOException("Invalid NTFS file record length for " + path);
        return output.AsSpan(12, recordBytes).ToArray();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, byte[] buffer, int size);

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
