using System.Buffers.Binary;

namespace QueueCache.Developer.Verification;

/// <summary>Independent last-write oracle for the bounded C: pressure writer.</summary>
public static class PagingWriteEvidence
{
    public const int Files = 128;
    public const int WritesPerSecond = 2;

    public static long LastGeneration(long writes, int file)
    {
        if (writes < 0 || file < 0 || file >= Files) throw new ArgumentOutOfRangeException();
        return writes <= file ? 0 : (writes - 1 - file) / Files + 1;
    }

    public static byte[] Pattern(int file, long generation)
    {
        if (file < 0 || file >= Files || generation < 0) throw new ArgumentOutOfRangeException();
        var bytes = ConcurrentCacheOracle.Pattern(132, unchecked((int)(generation * Files + file)), 4096);
        BinaryPrimitives.WriteInt64LittleEndian(bytes, generation);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), file);
        return bytes;
    }
}
