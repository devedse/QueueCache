using System.Buffers.Binary;
using System.Security.Cryptography;

namespace QueueCache.Operations.ManagedDisks;

/// <summary>The SHA-256 of every 1 MiB of a saved image (#22). A later save writes into this image only the
/// chunks whose hash differs from the frozen RAM disk. It is kept beside the image as "&lt;image&gt;.chunks" and
/// describes the image only while the image file keeps the length and last-write time it had when the map was
/// written, and the catalog's reference to the image has the same logical digest.</summary>
public sealed record ImageChunkMap(ulong CapacityBytes, string Sha256, byte[] Hashes, long FileBytes, long FileWriteTicks)
{
    public const int ChunkBytes = 1 << 20, HashBytes = 32;
    private static readonly byte[] Magic = "QCCHUNK1"u8.ToArray();
    public static int ChunkCount(ulong capacityBytes) => checked((int)((capacityBytes + ChunkBytes - 1) / ChunkBytes));
    public static string PathFor(string image) => image + ".chunks";
    public byte[] Serialize()
    {
        if (Hashes.Length != ChunkCount(CapacityBytes) * HashBytes || Sha256.Length != 64) throw new InvalidDataException("Inconsistent chunk map.");
        var body = new byte[8 + 8 + 32 + 8 + 8 + Hashes.Length];
        Magic.CopyTo(body, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), CapacityBytes);
        Convert.FromHexString(Sha256).CopyTo(body, 16);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(48), FileBytes);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(56), FileWriteTicks);
        Hashes.CopyTo(body, 64);
        return [.. body, .. SHA256.HashData(body)];
    }
    /// <summary>Null when the bytes are not a complete, intact map.</summary>
    public static ImageChunkMap? Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 64 + HashBytes || !bytes[..8].SequenceEqual(Magic) ||
            !SHA256.HashData(bytes[..^HashBytes]).AsSpan().SequenceEqual(bytes[^HashBytes..]))
            return null;
        var capacity = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        if (capacity == 0 || capacity > long.MaxValue || bytes.Length != 64 + ChunkCount(capacity) * HashBytes + HashBytes)
            return null;
        return new(capacity, Convert.ToHexString(bytes.Slice(16, 32)), bytes[64..^HashBytes].ToArray(),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[48..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[56..]));
    }
}
