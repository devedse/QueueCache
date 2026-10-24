using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace QueueCache.Operations.ManagedDisks;

/// <summary>Reuse hint bound to one verified owned file and logical digest. Full candidate verification
/// remains mandatory; a map never certifies unchanged data after an interrupted save.</summary>
internal sealed record ImageChunkMap(ulong CapacityBytes, string Sha256, byte[] Hashes,
    long FileBytes, long FileWriteTicks, string FileKeySha256)
{
    public const int ChunkBytes = 1 << 20, HashBytes = 32, MaximumChunks = 1 << 20;
    public const int MaximumFileBytes = 96 + MaximumChunks * HashBytes + HashBytes;
    private static ReadOnlySpan<byte> Magic => "QCCHUNK2"u8;
    public static string PathFor(string image) => image + ".qcache-chunks";
    public static bool Supports(ulong capacity) => capacity > 0 && (capacity - 1) / ChunkBytes < MaximumChunks;
    public static int ChunkCount(ulong capacity)
    {
        var count = capacity == 0 ? 0 : (capacity - 1) / ChunkBytes + 1;
        if (count is 0 or > MaximumChunks) throw new NotSupportedException("Image exceeds the bounded checkpoint chunk map.");
        return (int)count;
    }
    public static string FileKeyHash(string identity) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(identity[(identity.LastIndexOf('|') + 1)..].ToUpperInvariant())));
    public byte[] Serialize()
    {
        if (Hashes.Length != ChunkCount(CapacityBytes) * HashBytes || Sha256.Length != 64 || FileKeySha256.Length != 64 ||
            FileBytes <= 0 || FileWriteTicks <= 0 || FileWriteTicks > DateTime.MaxValue.Ticks)
            throw new InvalidDataException("Inconsistent checkpoint chunk map.");
        var body = new byte[96 + Hashes.Length];
        Magic.CopyTo(body); BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), CapacityBytes);
        Convert.FromHexString(Sha256).CopyTo(body, 16);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(48), FileBytes);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(56), FileWriteTicks);
        Convert.FromHexString(FileKeySha256).CopyTo(body, 64); Hashes.CopyTo(body, 96);
        return [.. body, .. SHA256.HashData(body)];
    }
    public static ImageChunkMap? Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 128 or > MaximumFileBytes || !bytes[..8].SequenceEqual(Magic) ||
            !SHA256.HashData(bytes[..^HashBytes]).AsSpan().SequenceEqual(bytes[^HashBytes..])) return null;
        var capacity = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        var count = capacity == 0 ? 0 : (capacity - 1) / ChunkBytes + 1;
        if (count is 0 or > MaximumChunks || (ulong)bytes.Length != 128 + count * HashBytes) return null;
        var fileBytes = BinaryPrimitives.ReadInt64LittleEndian(bytes[48..]);
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes[56..]);
        if (fileBytes <= 0 || ticks <= 0 || ticks > DateTime.MaxValue.Ticks) return null;
        return new(capacity, Convert.ToHexString(bytes.Slice(16, 32)), bytes[96..^HashBytes].ToArray(), fileBytes, ticks,
            Convert.ToHexString(bytes.Slice(64, 32)));
    }
}
