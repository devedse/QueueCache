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
/// <summary>What a save copied (#22): the logical digest, the SHA-256 of every <see cref="ImageChunkMap.ChunkBytes"/>
/// chunk, and the chunks it wrote to the destination (all of them for a new image).</summary>
public sealed record ChunkedCopy(LogicalImageDigest Digest, byte[] Hashes, IReadOnlyList<int> Written, ulong WrittenBytes);

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

    /// <summary>Copies <paramref name="source"/> to <paramref name="destination"/>, writing only the chunks whose
    /// SHA-256 differs from <paramref name="previous"/> (the destination's chunk map; null writes every chunk), and
    /// returns the logical digest and every chunk's hash (#22). Neighbouring changed chunks are written together.</summary>
    public static async Task<ChunkedCopy> CopyChangedAsync(ILogicalDisk source, ILogicalDisk destination, ReadOnlyMemory<byte>? previous,
        IProgress<ImageTransferProgress>? progress = null, CancellationToken token = default)
    {
        const int block = DefaultChunkBytes, chunk = ImageChunkMap.ChunkBytes, hashBytes = ImageChunkMap.HashBytes;
        Validate(source, block);
        Validate(destination, block);
        if (source.CapacityBytes != destination.CapacityBytes || source.SectorBytes != destination.SectorBytes)
            throw new InvalidDataException("Logical image capacity and sector sizes must match exactly.");
        var hashes = new byte[ImageChunkMap.ChunkCount(source.CapacityBytes) * hashBytes];
        if (previous is { } known && known.Length != hashes.Length)
            throw new InvalidDataException("The saved image's chunk map does not match its capacity.");
        var written = new List<int>();
        ulong writtenBytes = 0;
        var rented = ArrayPool<byte>.Shared.Rent(block);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            ulong offset = 0;
            while (offset < source.CapacityBytes)
            {
                token.ThrowIfCancellationRequested();
                var length = (int)Math.Min((ulong)block, source.CapacityBytes - offset);
                await ReadFullyAsync(source, offset, rented.AsMemory(0, length), token);
                var first = (int)(offset / chunk);
                var chunks = (length + chunk - 1) / chunk;
                var hashing = Task.Run(() => Parallel.For(0, chunks, i =>
                    SHA256.HashData(rented.AsSpan(i * chunk, Math.Min(chunk, length - i * chunk)), hashes.AsSpan((first + i) * hashBytes, hashBytes))), token);
                hash.AppendData(rented, 0, length);
                await hashing;
                for (var i = 0; i < chunks;)
                {
                    if (!Changed(first + i)) { i++; continue; }
                    var end = i;
                    while (end < chunks && Changed(first + end)) written.Add(first + end++);
                    var bytes = Math.Min(end * chunk, length) - i * chunk;
                    await destination.WriteAsync(offset + (ulong)(i * chunk), rented.AsMemory(i * chunk, bytes), token);
                    writtenBytes += (ulong)bytes;
                    i = end;
                }
                offset += (ulong)length;
                progress?.Report(new(offset, source.CapacityBytes));
            }
            token.ThrowIfCancellationRequested();
            await destination.FlushAsync(token);
            return new(new(source.CapacityBytes, Convert.ToHexString(hash.GetHashAndReset())), hashes, written, writtenBytes);
        }
        finally { ArrayPool<byte>.Shared.Return(rented, clearArray: true); }
        bool Changed(int index) => previous is not { } map ||
            !map.Span.Slice(index * hashBytes, hashBytes).SequenceEqual(hashes.AsSpan(index * hashBytes, hashBytes));
    }

    /// <summary>Reads back the listed chunks of an image written in place and compares each with its SHA-256 in
    /// <paramref name="hashes"/> (#22). The other chunks were verified when they were last written.</summary>
    public static async Task VerifyChunksAsync(ILogicalDisk candidate, ReadOnlyMemory<byte> hashes, IReadOnlyList<int> chunks,
        CancellationToken token = default, IProgress<ImageTransferProgress>? progress = null)
    {
        const int chunk = ImageChunkMap.ChunkBytes, hashBytes = ImageChunkMap.HashBytes;
        var count = ImageChunkMap.ChunkCount(candidate.CapacityBytes);
        if (hashes.Length != count * hashBytes) throw new InvalidDataException("Saved image virtual capacity changed.");
        ulong Length(int index) => Math.Min((ulong)chunk, candidate.CapacityBytes - (ulong)index * chunk);
        if (chunks.Any(i => i < 0 || i >= count)) throw new ArgumentOutOfRangeException(nameof(chunks));
        var total = chunks.Aggregate(0UL, (sum, i) => sum + Length(i));
        ulong done = 0;
        var rented = ArrayPool<byte>.Shared.Rent(chunk);
        try
        {
            foreach (var index in chunks)
            {
                token.ThrowIfCancellationRequested();
                var length = (int)Length(index);
                await ReadFullyAsync(candidate, (ulong)index * chunk, rented.AsMemory(0, length), token);
                if (!SHA256.HashData(rented.AsSpan(0, length)).AsSpan().SequenceEqual(hashes.Span.Slice(index * hashBytes, hashBytes)))
                    throw new InvalidDataException("A rewritten part of the saved image does not match the frozen RAM generation.");
                done += (ulong)length;
                progress?.Report(new(done, total));
            }
        }
        finally { ArrayPool<byte>.Shared.Return(rented, clearArray: true); }
    }

    private static async Task ReadFullyAsync(ILogicalDisk source, ulong offset, Memory<byte> bytes, CancellationToken token)
    {
        var read = 0;
        while (read < bytes.Length)
        {
            var count = await source.ReadAsync(offset + (ulong)read, bytes[read..], token);
            if (count <= 0 || count > bytes.Length - read || count % source.SectorBytes != 0)
                throw new InvalidDataException("Short, unaligned or invalid logical sector read; a partially loaded image cannot become Ready.");
            read += count;
        }
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
