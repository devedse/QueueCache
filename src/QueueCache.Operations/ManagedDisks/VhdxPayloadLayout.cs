using System.Buffers.Binary;

namespace QueueCache.Operations.ManagedDisks;

/// <summary>Strict, bounded reader for an already allocated checkpoint. It never repairs an image,
/// replays its log, allocates payload or changes metadata. Unsupported layouts use a full new save.
/// MS-VHDX: https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-vhdx/83e061f8-f6e2-4de1-91bd-5d518a43d477.</summary>
internal sealed record VhdxPayloadLayout(ulong CapacityBytes, uint SectorBytes, uint BlockBytes,
    long[] PayloadOffsets, byte[] Header, long CurrentHeaderOffset, ulong Sequence, Guid DiskId)
{
    private const int MiB = 1 << 20, KiB = 1024;
    private static readonly Guid BatId = new("2dc27766-f623-4200-9d64-115e9bfd4a08"),
        MetadataId = new("8b7ca206-4790-4b9a-b8fe-575f050f886e"),
        ParametersId = new("caa16737-fa36-4d43-b3b6-33f0aa44e76b"),
        CapacityId = new("2fa54224-cd1b-4876-b211-5dbed83bf4b8"),
        Page83Id = new("beca12ab-b2e6-4523-93ef-c309e000c746"),
        LogicalSectorId = new("8141bf1d-a96f-4709-ba47-f233a8faab5f"),
        PhysicalSectorId = new("cda348c7-445d-4471-9cc9-e9885251c556");
    private static readonly HashSet<Guid> KnownItems = [ParametersId, CapacityId, Page83Id, LogicalSectorId, PhysicalSectorId];
    private static readonly uint[] CrcTable = BuildCrcTable();
    private readonly record struct Region(long Offset, long Length);

    public static VhdxPayloadLayout Read(Stream source, ulong expectedCapacity, uint expectedSector)
    {
        if (!source.CanRead || !source.CanSeek || source.Length < MiB ||
            !ReadAt(source, 0, 8).AsSpan().SequenceEqual("vhdxfile"u8))
            throw new InvalidDataException("Invalid VHDX file identifier or length.");
        var first = ReadAt(source, 64 * KiB, 4096);
        var second = ReadAt(source, 128 * KiB, 4096);
        // Conservative eligibility: Windows can recover some damaged headers, but a reuse
        // writer must not repair them or guess which valid equal-sequence header is current.
        if (!ValidHeader(first) || !ValidHeader(second) || U64(first, 8) == U64(second, 8))
            throw new NotSupportedException("Checkpoint reuse requires two valid, unambiguous VHDX headers.");
        var firstCurrent = U64(first, 8) > U64(second, 8);
        var header = firstCurrent ? first : second;
        var sequence = U64(header, 8);
        if (U16(header, 64) != 0 || U16(header, 66) != 1 || new Guid(header.AsSpan(48, 16)) != Guid.Empty ||
            sequence > ulong.MaxValue - 2)
            throw new NotSupportedException("Checkpoint reuse requires VHDX version 1 with no pending log.");
        var log = BoundedRegion(U64(header, 72), U32(header, 68), source.Length);
        var a = ReadAt(source, 192 * KiB, 64 * KiB);
        var b = ReadAt(source, 256 * KiB, 64 * KiB);
        if (!a.AsSpan(0, 4).SequenceEqual("regi"u8) || U32(a, 4) != Crc32C(a, 4) || !a.AsSpan().SequenceEqual(b) ||
            U32(a, 8) is 0 or > 2047 || U32(a, 12) != 0)
            throw new NotSupportedException("Checkpoint reuse requires matching valid VHDX region tables.");
        var regions = new Dictionary<Guid, Region>();
        var intervals = new List<Region> { new(0, MiB), log };
        for (var i = 0; i < U32(a, 8); i++)
        {
            var entry = 16 + checked((int)i * 32);
            var id = new Guid(a.AsSpan(entry, 16));
            var required = U32(a, entry + 28);
            if (required > 1 || required == 1 && id != BatId && id != MetadataId)
                throw new NotSupportedException("Unknown required VHDX region.");
            var region = BoundedRegion(U64(a, entry + 16), U32(a, entry + 24), source.Length);
            if (!regions.TryAdd(id, region)) throw new InvalidDataException("Duplicate VHDX region.");
            intervals.Add(region);
        }
        Disjoint(intervals);
        if (!regions.TryGetValue(MetadataId, out var metadata) || !regions.TryGetValue(BatId, out var bat))
            throw new InvalidDataException("Missing VHDX metadata or allocation table.");
        var table = ReadAt(source, metadata.Offset, 64 * KiB);
        if (!table.AsSpan(0, 8).SequenceEqual("metadata"u8) || U16(table, 10) is 0 or > 2047)
            throw new InvalidDataException("Invalid VHDX metadata table.");
        var items = new Dictionary<Guid, byte[]>();
        var itemIds = new HashSet<Guid>();
        var itemIntervals = new List<Region> { new(0, 64 * KiB) };
        for (var i = 0; i < U16(table, 10); i++)
        {
            var entry = 32 + i * 32;
            var id = new Guid(table.AsSpan(entry, 16));
            var offset = U32(table, entry + 16);
            var length = U32(table, entry + 20);
            var flags = U32(table, entry + 24);
            if ((flags & ~7u) != 0 || U32(table, entry + 28) != 0 ||
                (flags & 4) != 0 && !KnownItems.Contains(id))
                throw new NotSupportedException("Unknown required VHDX metadata.");
            if (offset < 64 * KiB || length == 0 || length > MiB || offset > metadata.Length || length > metadata.Length - offset ||
                !itemIds.Add(id))
                throw new InvalidDataException("Invalid, duplicate or out-of-range VHDX metadata item.");
            itemIntervals.Add(new(offset, length));
            if (KnownItems.Contains(id))
            {
                if ((flags & 1) != 0) throw new InvalidDataException("System VHDX metadata marked as user data.");
                items.Add(id, ReadAt(source, metadata.Offset + offset, checked((int)length)));
            }
        }
        Disjoint(itemIntervals);
        byte[] Item(Guid id, int length) => items.TryGetValue(id, out var bytes) && bytes.Length == length
            ? bytes : throw new InvalidDataException("Missing or invalid VHDX geometry metadata.");
        var parameters = Item(ParametersId, 8);
        var blockBytes = U32(parameters, 0);
        var parameterFlags = U32(parameters, 4);
        if (blockBytes < MiB || blockBytes > 256 * MiB || (blockBytes & (blockBytes - 1)) != 0 || (parameterFlags & ~1u) != 0)
            throw new NotSupportedException("Unsupported VHDX block size or differencing image.");
        var capacity = U64(Item(CapacityId, 8), 0);
        var sector = U32(Item(LogicalSectorId, 4), 0);
        var physical = U32(Item(PhysicalSectorId, 4), 0);
        var diskId = new Guid(Item(Page83Id, 16));
        if (capacity == 0 || capacity > long.MaxValue || sector is not (512 or 4096) || physical is not (512 or 4096) ||
            capacity % sector != 0 || capacity != expectedCapacity || sector != expectedSector || diskId == Guid.Empty)
            throw new InvalidDataException("VHDX geometry differs from the recorded checkpoint.");
        var blocks = (capacity - 1) / blockBytes + 1;
        var ratio = (1UL << 23) * sector / blockBytes;
        if (blocks > 1_048_576 || ratio == 0)
            throw new NotSupportedException("Checkpoint allocation map exceeds the bounded reuse reader.");
        var entries = blocks + (blocks - 1) / ratio;
        var bytesNeeded = checked((int)(entries * 8));
        if (bytesNeeded > bat.Length) throw new InvalidDataException("VHDX allocation table is too short.");
        var allocation = ReadAt(source, bat.Offset, bytesNeeded);
        var offsets = new long[checked((int)blocks)];
        for (ulong block = 0; block < blocks; block++)
        {
            var entry = U64(allocation, checked((int)((block + block / ratio) * 8)));
            // Sparse blocks may remain unchanged. The coordinator checks their source
            // fingerprints before consuming the candidate; they are never writable here.
            if (entry is 0 or 2)
            {
                offsets[checked((int)block)] = -1;
                continue;
            }
            // No BAT writes or allocation. Other non-present/unmapped/partial states fall back.
            if ((entry & 0xfffff) != 6)
                throw new NotSupportedException("Checkpoint payload is not fully allocated.");
            var offset = entry & 0xfffffffffff00000UL;
            if (offset < MiB || offset > (ulong)source.Length || blockBytes > (ulong)source.Length - offset)
                throw new InvalidDataException("VHDX payload points outside the container.");
            offsets[checked((int)block)] = checked((long)offset);
            intervals.Add(new(checked((long)offset), blockBytes));
        }
        for (var bitmap = ratio; bitmap < entries; bitmap += ratio + 1)
            if (U64(allocation, checked((int)(bitmap * 8))) != 0)
                throw new NotSupportedException("Unexpected sector bitmap in a non-differencing checkpoint.");
        Disjoint(intervals);
        return new(capacity, sector, blockBytes, offsets, header, firstCurrent ? 64 * KiB : 128 * KiB, sequence, diskId);
    }

    private static Region BoundedRegion(ulong offset, ulong length, long fileBytes)
    {
        if (offset < MiB || offset % MiB != 0 || length == 0 || length % MiB != 0 ||
            offset > (ulong)fileBytes || length > (ulong)fileBytes - offset)
            throw new InvalidDataException("VHDX region is misaligned or outside the container.");
        return new(checked((long)offset), checked((long)length));
    }
    private static void Disjoint(List<Region> regions)
    {
        regions.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        for (var i = 1; i < regions.Count; i++)
            if (regions[i].Offset < checked(regions[i - 1].Offset + regions[i - 1].Length))
                throw new InvalidDataException("Overlapping VHDX structures or payload blocks.");
    }
    private static bool ValidHeader(byte[] header) => header.AsSpan(0, 4).SequenceEqual("head"u8) && U32(header, 4) == Crc32C(header, 4);
    private static byte[] ReadAt(Stream source, long offset, int bytes)
    {
        source.Position = offset;
        var result = new byte[bytes];
        source.ReadExactly(result);
        return result;
    }
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
    internal static uint Crc32C(ReadOnlySpan<byte> bytes, int checksumOffset = -1)
    {
        var crc = uint.MaxValue;
        for (var i = 0; i < bytes.Length; i++)
        {
            var value = checksumOffset >= 0 && i >= checksumOffset && i < checksumOffset + 4 ? (byte)0 : bytes[i];
            crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        }
        return ~crc;
    }
    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0x82f63b78u : 0);
            table[i] = crc;
        }
        return table;
    }
}
