using System.Buffers.Binary;

namespace QueueCache.Management;

/// <summary>How each 256 KiB chunk of a cache's RAM is used (driver layout map V1). Per chunk:
/// used slots, dirty (not yet on disk), clean read cache, and slots whose next slot holds the next
/// disk block. Chunks may be fewer than <see cref="TotalChunks"/> if the cache was reallocated.</summary>
public sealed record CacheLayoutMap(ulong ChunkBytes, ulong Generation, int TotalChunks, byte[] Used, byte[] Dirty, byte[] Read, byte[] Ordered)
{
    public const int HeaderSize = 32;
    public const int SlotsPerChunk = 64;
    public int Chunks => Used.Length;
    public bool IsComplete => Chunks == TotalChunks;
    public int FreeChunks => Used.Count(u => u == 0);
    /// <summary>Share of possible links among used slots within each chunk that join consecutive disk blocks;
    /// null when nothing is cached. This describes block placement, not filesystem fragmentation or physical page adjacency.</summary>
    public double? InOrder
    {
        get
        {
            long ordered = 0, pairs = 0;
            for (var i = 0; i < Used.Length; i++)
            {
                ordered += Ordered[i];
                pairs += Math.Max(0, Used[i] - 1);
            }
            return pairs == 0 ? null : (double)ordered / pairs;
        }
    }

    public static CacheLayoutMap Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 1)
            throw new InvalidDataException("Unsupported cache layout map.");
        var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        var total = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        var returned = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        if (size != bytes.Length || size != HeaderSize + 4L * returned || returned > total || total > int.MaxValue ||
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]) != 256 * 1024)
            throw new InvalidDataException("Invalid cache layout map size.");
        var used = new byte[returned]; var dirty = new byte[returned]; var read = new byte[returned]; var ordered = new byte[returned];
        for (var i = 0; i < returned; i++)
        {
            var entry = bytes.Slice(HeaderSize + 4 * i, 4);
            if (entry[0] > SlotsPerChunk || entry[1] + entry[2] > entry[0] || entry[3] > Math.Max(0, entry[0] - 1))
                throw new InvalidDataException("Inconsistent cache layout map entry.");
            (used[i], dirty[i], read[i], ordered[i]) = (entry[0], entry[1], entry[2], entry[3]);
        }
        return new(BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]),
            (int)total, used, dirty, read, ordered);
    }
}
