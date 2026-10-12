using System.Buffers.Binary;

namespace QueueCache.Developer.Verification;

/// <summary>Deterministic content for comparisons: the seeded file tree and the 4 KiB durability sectors.</summary>
public static class ComparisonData
{
    /// <summary>Seeded file tree resembling a source checkout: 80% 1-16 KiB, 18% 16-128 KiB, 2% 128 KiB-1 MiB.</summary>
    public static IReadOnlyList<(string Path, int Bytes)> TreeSpec(int files = ComparisonPlan.TreeFiles, int seed = ComparisonPlan.TreeSeed)
    {
        var random = new Random(seed);
        var spec = new (string, int)[files];
        for (var i = 0; i < files; i++)
        {
            var p = random.NextDouble();
            var size = p < 0.8 ? random.Next(1024, 16385) : p < 0.98 ? random.Next(16384, 131073) : random.Next(131072, 1048577);
            spec[i] = ($@"d{i % 20:D2}\s{i / 20 % 20:D2}\f{i:D5}.bin", size);
        }
        return spec;
    }

    public static byte[] TreeContent(int seed = ComparisonPlan.TreeSeed)
    {
        var content = new byte[4 << 20];
        new Random(seed ^ 0x5A5A).NextBytes(content);
        return content;
    }

    public static int TreeOffset(int index, int bytes) => (int)((long)index * 4099 % ((4 << 20) - bytes));

    /// <summary>4 KiB durability sector: header (magic, nonce, generation, set, index) and a seeded body.</summary>
    public static void FillSector(Span<byte> sector, ulong nonce, int generation, int set, long index)
    {
        if (sector.Length != 4096) throw new ArgumentException("Sectors are 4 KiB.");
        BinaryPrimitives.WriteUInt64LittleEndian(sector, 0x3142415255444351UL); // "QCDURAB1"
        BinaryPrimitives.WriteUInt64LittleEndian(sector[8..], nonce);
        BinaryPrimitives.WriteInt32LittleEndian(sector[16..], generation);
        BinaryPrimitives.WriteInt32LittleEndian(sector[20..], set);
        BinaryPrimitives.WriteInt64LittleEndian(sector[24..], index);
        var state = nonce ^ ((ulong)generation << 56) ^ ((ulong)set << 48) ^ (ulong)index * 0x9E3779B97F4A7C15UL | 1;
        for (var offset = 32; offset < 4096; offset += 8)
        {
            state ^= state << 13; state ^= state >> 7; state ^= state << 17;
            BinaryPrimitives.WriteUInt64LittleEndian(sector[offset..], state);
        }
    }

    /// <summary>Which generation a sector holds (1 or 2), or 0 when it holds anything else.</summary>
    public static int Classify(ReadOnlySpan<byte> sector, ulong nonce, int set, long index)
    {
        Span<byte> expected = stackalloc byte[4096];
        foreach (var generation in (ReadOnlySpan<int>)[2, 1])
        {
            FillSector(expected, nonce, generation, set, index);
            if (sector.SequenceEqual(expected)) return generation;
        }
        return 0;
    }

}
