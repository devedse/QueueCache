using System.Buffers;
using System.Security.Cryptography;

namespace QueueCache.Operations.ManagedDisks;

/// <summary>The image adapter exposes decoded logical sectors, never VHDX container bytes.</summary>
public interface ILogicalDisk
{
    ulong CapacityBytes { get; }
    uint SectorBytes { get; }
    ValueTask<int> ReadAsync(ulong offset, Memory<byte> buffer, CancellationToken token);
    ValueTask WriteAsync(ulong offset, ReadOnlyMemory<byte> buffer, CancellationToken token);
    ValueTask FlushAsync(CancellationToken token);
}

public sealed record LogicalImageDigest(ulong Bytes, string Sha256);
public sealed record ImageTransferProgress(ulong CompletedBytes, ulong TotalBytes);

public static class LogicalImageTransfer
{
    public const int DefaultChunkBytes = 8 << 20;

    public static async Task<LogicalImageDigest> CopyAsync(ILogicalDisk source, ILogicalDisk destination,
        IProgress<ImageTransferProgress>? progress = null, CancellationToken token = default, int chunkBytes = DefaultChunkBytes)
    {
        Validate(source, chunkBytes);
        Validate(destination, chunkBytes);
        if (source.CapacityBytes != destination.CapacityBytes || source.SectorBytes != destination.SectorBytes)
            throw new InvalidDataException("Logical image capacity and sector sizes must match exactly.");
        var result = await Walk(source, destination, progress, token, chunkBytes);
        await destination.FlushAsync(token);
        return result;
    }

    public static Task<LogicalImageDigest> HashAsync(ILogicalDisk source, CancellationToken token = default,
        int chunkBytes = DefaultChunkBytes)
    {
        Validate(source, chunkBytes);
        return Walk(source, null, null, token, chunkBytes);
    }

    public static async Task VerifyAsync(ILogicalDisk candidate, LogicalImageDigest expected, CancellationToken token = default)
    {
        if (candidate.CapacityBytes != expected.Bytes)
            throw new InvalidDataException("Saved image virtual capacity changed.");
        var actual = await HashAsync(candidate, token);
        if (actual != expected)
            throw new InvalidDataException("Saved image logical sectors do not match the frozen RAM generation.");
    }

    private static void Validate(ILogicalDisk disk, int chunkBytes)
    {
        if (disk.SectorBytes is not (512 or 4096) || disk.CapacityBytes == 0 || disk.CapacityBytes > long.MaxValue ||
            disk.CapacityBytes % disk.SectorBytes != 0 || chunkBytes < disk.SectorBytes || chunkBytes > 64 << 20 || chunkBytes % disk.SectorBytes != 0)
            throw new ArgumentException("Invalid logical geometry or bounded aligned transfer chunk.");
    }

    private static async Task<LogicalImageDigest> Walk(ILogicalDisk source, ILogicalDisk? destination,
        IProgress<ImageTransferProgress>? progress, CancellationToken token, int chunkBytes)
    {
        var rented = ArrayPool<byte>.Shared.Rent(chunkBytes);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            ulong offset = 0;
            while (offset < source.CapacityBytes)
            {
                token.ThrowIfCancellationRequested();
                var length = (int)Math.Min((ulong)chunkBytes, source.CapacityBytes - offset);
                var bytes = rented.AsMemory(0, length);
                var read = 0;
                while (read < length)
                {
                    var count = await source.ReadAsync(offset + (ulong)read, bytes[read..], token);
                    if (count <= 0 || count > length - read || count % source.SectorBytes != 0)
                        throw new InvalidDataException("Short, unaligned or invalid logical sector read; a partially loaded image cannot become Ready.");
                    read += count;
                }
                hash.AppendData(bytes.Span);
                if (destination is not null)
                    await destination.WriteAsync(offset, bytes, token);
                offset += (ulong)length;
                progress?.Report(new(offset, source.CapacityBytes));
            }
            token.ThrowIfCancellationRequested();
            return new(source.CapacityBytes, Convert.ToHexString(hash.GetHashAndReset()));
        }
        finally { ArrayPool<byte>.Shared.Return(rented, clearArray: true); }
    }
}
