using System.Buffers.Binary;

namespace QueueCache.Developer.Verification;

/// <summary>Versioned content for blocks that are written while the cache changes underneath. Every
/// 4 KiB sector carries (seed, block, sector, version) and a body derived from them, so a read shows
/// which whole version each sector holds; a torn, misplaced or stale sector is detected.</summary>
public static class StressPattern
{
    public const int Sector = 4096;

    public static void Fill(Span<byte> buffer, ulong seed, int block, int version)
    {
        if (buffer.Length == 0 || buffer.Length % Sector != 0) throw new ArgumentException("Whole sectors only.");
        for (var index = 0; index * Sector < buffer.Length; index++)
            FillSector(buffer.Slice(index * Sector, Sector), seed, block, index, version);
    }

    /// <summary>The version a sector holds, or -1 when it is not a whole sector of this block.</summary>
    public static int Version(ReadOnlySpan<byte> sector, ulong seed, int block, int index)
    {
        if (sector.Length != Sector || BinaryPrimitives.ReadUInt64LittleEndian(sector) != seed ||
            BinaryPrimitives.ReadInt32LittleEndian(sector[8..]) != block || BinaryPrimitives.ReadInt32LittleEndian(sector[12..]) != index)
            return -1;
        var version = BinaryPrimitives.ReadInt32LittleEndian(sector[16..]);
        if (version < 0 || BinaryPrimitives.ReadInt32LittleEndian(sector[20..]) != ~version) return -1;
        Span<byte> expected = stackalloc byte[Sector];
        FillSector(expected, seed, block, index, version);
        return expected.SequenceEqual(sector) ? version : -1;
    }

    /// <summary>Null when every sector holds a whole version within [low, high]; otherwise what is wrong.</summary>
    public static string? Check(ReadOnlySpan<byte> buffer, ulong seed, int block, int low, int high)
    {
        for (var index = 0; index * Sector < buffer.Length; index++)
        {
            var version = Version(buffer.Slice(index * Sector, Sector), seed, block, index);
            if (version < low || version > high)
                return version < 0
                    ? $"block {block} sector {index} is not a whole written sector"
                    : $"block {block} sector {index} holds version {version}, outside {low}..{high}";
        }
        return null;
    }

    private static void FillSector(Span<byte> sector, ulong seed, int block, int index, int version)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(sector, seed);
        BinaryPrimitives.WriteInt32LittleEndian(sector[8..], block);
        BinaryPrimitives.WriteInt32LittleEndian(sector[12..], index);
        BinaryPrimitives.WriteInt32LittleEndian(sector[16..], version);
        BinaryPrimitives.WriteInt32LittleEndian(sector[20..], ~version);
        BinaryPrimitives.WriteInt32LittleEndian(sector[24..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(sector[28..], 0);
        var state = seed ^ ((ulong)(uint)block << 32 | (uint)index) ^ (ulong)(uint)version * 0x9E3779B97F4A7C15UL;
        for (var offset = 32; offset < Sector; offset += 8)
        {
            state += 0x9E3779B97F4A7C15UL;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            BinaryPrimitives.WriteUInt64LittleEndian(sector[offset..], z ^ (z >> 31));
        }
    }
}

/// <summary>Started/completed versions of each block of one region. A read may return any version from
/// the last one completed before it started to the newest one started before it finished.</summary>
public sealed class StressBlockVersions(int blocks)
{
    private readonly int[] started = new int[blocks], done = new int[blocks];
    public int Blocks => started.Length;
    public void Begin(int block, int version) => Volatile.Write(ref started[block], version);
    public void Complete(int block, int version) => Volatile.Write(ref done[block], version);
    public int Done(int block) => Volatile.Read(ref done[block]);
    public int Started(int block) => Volatile.Read(ref started[block]);
}

/// <summary>One file of equal blocks with a single writer. Block sizes cover the caller-thread path
/// (4 KiB), coalescing (64 KiB), offloaded copies (1 MiB) and requests larger than the 1 MiB pin
/// scratch (4 MiB), which the ordered worker serves.</summary>
public sealed record StressRegion(string Name, int BlockBytes, int Blocks, bool Overlapped = false)
{
    public long Bytes => (long)BlockBytes * Blocks;
    public static readonly StressRegion[] All =
    [
        new("small-4k", 4 << 10, 2048), new("medium-64k", 64 << 10, 256), new("large-1m", 1 << 20, 16),
        new("oversized-4m", 4 << 20, 4), new("cancel-64k", 64 << 10, 384, Overlapped: true)
    ];
}

/// <summary>Chaos actions applied to a cached volume while its regions are in use.</summary>
public enum StressAction
{
    Flush, Resize, DropClean, Teardown, LowerDelay, TransientAllocation, OversizeRefused, OversizeKernel,
    AllocationRollback, Strict
}

public static class StressPlan
{
    /// <summary>Weighted, seeded choice; every action has a non-zero chance in any long run.</summary>
    public static StressAction Next(Random random)
    {
        var roll = random.Next(100);
        return roll switch
        {
            < 18 => StressAction.Flush,
            < 40 => StressAction.Resize,
            < 50 => StressAction.DropClean,
            < 58 => StressAction.Teardown,
            < 68 => StressAction.LowerDelay,
            < 78 => StressAction.TransientAllocation,
            < 83 => StressAction.OversizeRefused,
            < 88 => StressAction.OversizeKernel,
            < 94 => StressAction.AllocationRollback,
            _ => StressAction.Strict
        };
    }

    public static QueueCache.Operations.CacheConfiguration RandomConfiguration(Random random, int[] budgets) =>
        new(budgets[random.Next(budgets.Length)], QueueCache.Operations.CachePreset.Fast)
        {
            Options = new QueueCache.Management.CacheOptions(
                Drain: random.Next(3) switch { 0 => QueueCache.Management.DrainAlgorithm.Eager, 1 => QueueCache.Management.DrainAlgorithm.Idle,
                    _ => QueueCache.Management.DrainAlgorithm.Deferred },
                MaxDirtyAgeMs: random.Next(2) == 0 ? 1000 : 5000, IdleMs: random.Next(2) == 0 ? 100 : 250,
                Parallelism: random.Next(3) switch { 0 => 1, 1 => 2, _ => 4 }, RetainWrites: random.Next(2) == 0)
        };
}
