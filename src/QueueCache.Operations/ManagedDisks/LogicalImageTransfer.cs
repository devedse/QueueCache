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

/// <summary>Logical source digest, per-MiB fingerprints and completed payload writes. It does not
/// count container metadata writes or replace complete read-only verification.</summary>
public sealed record ChunkedImageCopy(LogicalImageDigest Digest, byte[] Hashes, ulong WrittenBytes);

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
        int chunkBytes = DefaultChunkBytes, IProgress<ImageTransferProgress>? progress = null)
    {
        Validate(source, chunkBytes);
        return Walk(source, null, progress, token, chunkBytes);
    }

    public static async Task VerifyAsync(ILogicalDisk candidate, LogicalImageDigest expected, CancellationToken token = default,
        IProgress<ImageTransferProgress>? progress = null)
    {
        if (candidate.CapacityBytes != expected.Bytes)
            throw new InvalidDataException("Saved image virtual capacity changed.");
        var actual = await HashAsync(candidate, token, progress: progress);
        if (actual != expected)
            throw new InvalidDataException("Saved image logical sectors do not match the frozen RAM generation.");
    }

    /// <summary>Before any candidate mutation, hash only source chunks that cannot be written
    /// through its existing BAT. An unchanged sparse chunk needs no allocation. A changed one
    /// requires a complete new candidate. The source remains frozen through both passes.</summary>
    public static async Task<bool> CanReuseAsync(ILogicalDisk source, ReadOnlyMemory<byte> previous,
        Func<ulong, int, bool> canWrite, CancellationToken token = default)
    {
        Validate(source, DefaultChunkBytes);
        if (previous.Length != ImageChunkMap.ChunkCount(source.CapacityBytes) * ImageChunkMap.HashBytes)
            throw new InvalidDataException("Checkpoint fingerprints do not match image geometry.");
        var rented = ArrayPool<byte>.Shared.Rent(ImageChunkMap.ChunkBytes);
        try
        {
            for (ulong offset = 0; offset < source.CapacityBytes; offset += ImageChunkMap.ChunkBytes)
            {
                token.ThrowIfCancellationRequested();
                var length = (int)Math.Min((ulong)ImageChunkMap.ChunkBytes, source.CapacityBytes - offset);
                if (canWrite(offset, length)) continue;
                var memory = rented.AsMemory(0, length);
                for (var read = 0; read < length;)
                {
                    var count = await source.ReadAsync(offset + (ulong)read, memory[read..], token);
                    if (count <= 0 || count > length - read || count % source.SectorBytes != 0)
                        throw new InvalidDataException("Invalid frozen logical image read during sparse reuse preflight.");
                    read += count;
                }
                if (!SHA256.HashData(memory.Span).AsSpan().SequenceEqual(previous.Span.Slice(
                    checked((int)(offset / ImageChunkMap.ChunkBytes) * ImageChunkMap.HashBytes), ImageChunkMap.HashBytes)))
                    return false;
            }
            return true;
        }
        finally { ArrayPool<byte>.Shared.Return(rented, clearArray: true); }
    }

    /// <summary>Reads the frozen source completely, writes only changed MiB regions, and flushes
    /// the candidate. A missing map writes everything. The caller still verifies the ENTIRE image.</summary>
    public static async Task<ChunkedImageCopy> CopyChangedAsync(ILogicalDisk source, ILogicalDisk destination,
        ReadOnlyMemory<byte>? previous, IProgress<ImageTransferProgress>? progress = null, CancellationToken token = default)
    {
        Validate(source, DefaultChunkBytes); Validate(destination, DefaultChunkBytes);
        if (source.CapacityBytes != destination.CapacityBytes || source.SectorBytes != destination.SectorBytes)
            throw new InvalidDataException("Logical image capacity and sector sizes must match exactly.");
        var hashes = new byte[ImageChunkMap.ChunkCount(source.CapacityBytes) * ImageChunkMap.HashBytes];
        if (previous is { } old && old.Length != hashes.Length)
            throw new InvalidDataException("Checkpoint fingerprints do not match image geometry.");
        var rented = ArrayPool<byte>.Shared.Rent(DefaultChunkBytes);
        ulong written = 0;
        try
        {
            using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (ulong offset = 0; offset < source.CapacityBytes;)
            {
                token.ThrowIfCancellationRequested();
                var length = (int)Math.Min((ulong)DefaultChunkBytes, source.CapacityBytes - offset);
                var memory = rented.AsMemory(0, length);
                var read = 0;
                while (read < length)
                {
                    var count = await source.ReadAsync(offset + (ulong)read, memory[read..], token);
                    if (count <= 0 || count > length - read || count % source.SectorBytes != 0)
                        throw new InvalidDataException("Invalid or incomplete frozen logical image read.");
                    read += count;
                }
                whole.AppendData(memory.Span);
                var first = checked((int)(offset / ImageChunkMap.ChunkBytes));
                var chunks = (length + ImageChunkMap.ChunkBytes - 1) / ImageChunkMap.ChunkBytes;
                for (var chunk = 0; chunk < chunks; chunk++)
                {
                    var start = chunk * ImageChunkMap.ChunkBytes;
                    SHA256.HashData(memory.Span.Slice(start, Math.Min(ImageChunkMap.ChunkBytes, length - start)),
                        hashes.AsSpan((first + chunk) * ImageChunkMap.HashBytes, ImageChunkMap.HashBytes));
                }
                for (var chunk = 0; chunk < chunks;)
                {
                    if (!Changed(first + chunk)) { chunk++; continue; }
                    var end = chunk + 1;
                    while (end < chunks && Changed(first + end)) end++;
                    var start = chunk * ImageChunkMap.ChunkBytes;
                    var count = Math.Min(end * ImageChunkMap.ChunkBytes, length) - start;
                    await destination.WriteAsync(offset + (ulong)start, memory.Slice(start, count), token);
                    written += (ulong)count; chunk = end;
                }
                offset += (ulong)length; progress?.Report(new(offset, source.CapacityBytes));
            }
            token.ThrowIfCancellationRequested();
            await destination.FlushAsync(token);
            return new(new(source.CapacityBytes, Convert.ToHexString(whole.GetHashAndReset())), hashes, written);
        }
        finally { ArrayPool<byte>.Shared.Return(rented, clearArray: true); }
        bool Changed(int index) => previous is not { } map || !map.Span.Slice(index * ImageChunkMap.HashBytes, ImageChunkMap.HashBytes)
            .SequenceEqual(hashes.AsSpan(index * ImageChunkMap.HashBytes, ImageChunkMap.HashBytes));
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
