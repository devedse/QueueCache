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

    /// <summary>Churn power-cut test (compare-durability-churn): sets of the base files written and settled first, and of
    /// the files created during the churn.</summary>
    public const int ChurnBaseSet = 5, ChurnSet = 6, ChurnBaseFiles = 2_000, ChurnSeconds = 40, ChurnFilesPerSecond = 200;

    /// <summary>Size of a churn-test file in 4 KiB sectors (1..8), fixed by its set and index.</summary>
    public static int ChurnSectors(int set, long index) => 1 + (int)(((ulong)index * 2654435761UL + (ulong)set) % 8);

    /// <summary>Contents of a churn-test file: durability sectors numbered index * 8 + sector.</summary>
    public static byte[] ChurnFile(ulong nonce, int set, long index)
    {
        var bytes = new byte[ChurnSectors(set, index) * 4096];
        for (var s = 0; s < bytes.Length / 4096; s++)
            FillSector(bytes.AsSpan(s * 4096, 4096), nonce, 1, set, index * 8 + s);
        return bytes;
    }

    public static bool ChurnIntact(ReadOnlySpan<byte> bytes, ulong nonce, int set, long index) => bytes.SequenceEqual(ChurnFile(nonce, set, index));

    /// <summary>Deletes after creating churn file <paramref name="index"/>, each every eighth step: one base file (in order,
    /// so a full 40 s run deletes the first half and keeps the rest; their clusters and file records are reused), and from
    /// step 1,000 on the churn file created 1,000 steps earlier.</summary>
    public static (int? Base, int? Churn) ChurnDeletes(int index, int baseFiles = ChurnBaseFiles) =>
        (index % 8 == 3 ? index / 8 % baseFiles : null, index % 8 == 7 && index >= 1000 ? index - 1000 : null);

    /// <summary>Groups for the churn check: base files kept or deleted, churn files by age when the churn ended, deleted churn files.</summary>
    public static string ChurnGroup(ChurnOracle oracle, bool isBase, int index)
    {
        if (isBase) return oracle.BaseDeletedMs[index] >= 0 ? "base files deleted during the churn" : "base files kept";
        if (oracle.ChurnDeletedMs[index] >= 0) return "churn files deleted again";
        var age = (oracle.CompletedUtc - oracle.ChurnStartUtc).TotalMilliseconds - oracle.CreatedMs[index];
        return age >= 30_000 ? "churn files 30 s or older" : age >= 20_000 ? "churn files 20-30 s old" : age >= 10_000 ? "churn files 10-20 s old" : "churn files under 10 s old";
    }
}
