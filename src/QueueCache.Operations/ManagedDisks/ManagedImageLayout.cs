using System.Buffers.Binary;
using System.Text;

namespace QueueCache.Operations.ManagedDisks;

/// <summary>Validate decoded sectors while an import remains private/offline, before Windows publication.</summary>
public static class ManagedImageLayout
{
    private sealed record Header(Guid DiskId, ulong Current, ulong Alternate, ulong First, ulong Last,
        ulong EntriesLba, uint Count, uint EntryBytes, uint EntriesCrc);
    public static async Task<ManagedDiskLayout> InspectAsync(ILogicalDisk disk, CancellationToken token = default)
    {
        if (disk.SectorBytes is not (512 or 4096) || disk.CapacityBytes < 16UL << 20 || disk.CapacityBytes % disk.SectorBytes != 0)
            throw new InvalidDataException("Unsupported image sector geometry.");
        var sectors = disk.CapacityBytes / disk.SectorBytes;
        var mbr = await ReadAsync(disk, 0, checked((int)disk.SectorBytes), token);
        if (mbr[510] != 0x55 || mbr[511] != 0xAA || mbr[450] != 0xEE ||
            BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(454)) != 1 ||
            new[] { 466, 482, 498 }.Any(i => mbr[i] != 0))
            throw new NotSupportedException("Only a protective GPT MBR is supported; hybrid/legacy layouts are refused.");
        var primary = ParseHeader(await ReadAsync(disk, disk.SectorBytes, checked((int)disk.SectorBytes), token), sectors, 1);
        var backup = ParseHeader(await ReadAsync(disk, (sectors - 1) * disk.SectorBytes, checked((int)disk.SectorBytes), token), sectors, sectors - 1);
        if (primary.DiskId != backup.DiskId || primary.First != backup.First || primary.Last != backup.Last ||
            primary.Count != backup.Count || primary.EntryBytes != backup.EntryBytes || primary.EntriesCrc != backup.EntriesCrc ||
            primary.Alternate != backup.Current || backup.Alternate != primary.Current)
            throw new InvalidDataException("Primary and backup GPT headers disagree.");
        var length = checked((int)(primary.Count * primary.EntryBytes));
        var padded = checked((length + (int)disk.SectorBytes - 1) / (int)disk.SectorBytes * (int)disk.SectorBytes);
        var arraySectors = (ulong)padded / disk.SectorBytes;
        if (primary.EntriesLba < 2 || primary.EntriesLba + arraySectors > primary.First ||
            backup.EntriesLba <= backup.Last || backup.EntriesLba + arraySectors > backup.Current)
            throw new InvalidDataException("GPT metadata overlaps usable sectors.");
        var entries = await ReadAsync(disk, primary.EntriesLba * disk.SectorBytes, padded, token);
        var copy = await ReadAsync(disk, backup.EntriesLba * disk.SectorBytes, padded, token);
        if (Crc32(entries.AsSpan(0, length)) != primary.EntriesCrc || !entries.AsSpan(0, length).SequenceEqual(copy.AsSpan(0, length)))
            throw new InvalidDataException("GPT partition arrays are corrupt or disagree.");
        var parts = new List<ManagedPartition>();
        for (var i = 0; i < primary.Count; i++)
        {
            var offset = checked((int)(i * primary.EntryBytes)); var item = entries.AsSpan(offset, (int)primary.EntryBytes);
            var type = new Guid(item[..16]); if (type == Guid.Empty) continue;
            var first = BinaryPrimitives.ReadUInt64LittleEndian(item[32..]); var last = BinaryPrimitives.ReadUInt64LittleEndian(item[40..]);
            if (first < primary.First || last < first || last > primary.Last) throw new InvalidDataException("Image partition exceeds GPT usable sectors.");
            parts.Add(new(new Guid(item.Slice(16, 16)), type, first * disk.SectorBytes, (last - first + 1) * disk.SectorBytes,
                BinaryPrimitives.ReadUInt64LittleEndian(item[48..])));
        }
        var result = new ManagedDiskLayout(primary.DiskId, parts); result.Validate(disk.CapacityBytes, disk.SectorBytes);
        var data = parts.Single(p => p.Type == ManagedDiskLayout.BasicData);
        var boot = await ReadAsync(disk, data.Offset, checked((int)disk.SectorBytes), token);
        if (!boot.AsSpan(3, 8).SequenceEqual("NTFS    "u8) || BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11)) != disk.SectorBytes ||
            boot[13] == 0 || (boot[13] & (boot[13] - 1)) != 0 || boot[510] != 0x55 || boot[511] != 0xAA ||
            BinaryPrimitives.ReadUInt64LittleEndian(boot.AsSpan(40)) > data.Bytes / disk.SectorBytes)
            throw new NotSupportedException("Activation requires unencrypted NTFS sectors. BitLocker, other filesystems and malformed boot records are refused.");
        return result;
    }
    private static Header ParseHeader(byte[] sector, ulong sectors, ulong expectedLba)
    {
        if (!sector.AsSpan(0, 8).SequenceEqual("EFI PART"u8) || BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(8)) != 0x10000)
            throw new InvalidDataException("Missing supported GPT header.");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(12)); var crc = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(16));
        if (length < 92 || length > sector.Length || BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(20)) != 0)
            throw new InvalidDataException("Invalid GPT header length/reserved field.");
        var header = sector.AsSpan(0, (int)length).ToArray(); header.AsSpan(16, 4).Clear();
        if (Crc32(header) != crc) throw new InvalidDataException("GPT header checksum mismatch.");
        var result = new Header(new Guid(sector.AsSpan(56, 16)), BinaryPrimitives.ReadUInt64LittleEndian(sector.AsSpan(24)),
            BinaryPrimitives.ReadUInt64LittleEndian(sector.AsSpan(32)), BinaryPrimitives.ReadUInt64LittleEndian(sector.AsSpan(40)),
            BinaryPrimitives.ReadUInt64LittleEndian(sector.AsSpan(48)), BinaryPrimitives.ReadUInt64LittleEndian(sector.AsSpan(72)),
            BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(80)), BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(84)),
            BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(88)));
        if (result.DiskId == Guid.Empty || result.Current != expectedLba || result.Alternate != (expectedLba == 1 ? sectors - 1 : 1) ||
            result.First < 2 || result.Last < result.First || result.Last >= sectors - 1 || result.Count is 0 or > 1024 ||
            result.EntryBytes < 128 || result.EntryBytes % 128 != 0 || (ulong)result.Count * result.EntryBytes > (1 << 20) || result.EntriesLba >= sectors)
            throw new InvalidDataException("Invalid or unsupported GPT geometry.");
        return result;
    }
    private static async Task<byte[]> ReadAsync(ILogicalDisk disk, ulong offset, int length, CancellationToken token)
    {
        if (offset > disk.CapacityBytes || (ulong)length > disk.CapacityBytes - offset) throw new InvalidDataException("Image metadata exceeds disk capacity.");
        var bytes = new byte[length]; var read = 0;
        while (read < length)
        {
            var count = await disk.ReadAsync(offset + (ulong)read, bytes.AsMemory(read), token);
            if (count <= 0 || count > length - read || count % disk.SectorBytes != 0) throw new InvalidDataException("Incomplete image metadata sector read.");
            read += count;
        }
        return bytes;
    }
    public static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320U : 0U); }
        return ~crc;
    }
}
