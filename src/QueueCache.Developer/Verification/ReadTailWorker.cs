using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace QueueCache.Developer.Verification;

public sealed record ReadTailWarmResult(long Bytes, int CompletedOperations, double Seconds);
public sealed record ReadTailWriteVerification(int CompletedOperations, long Bytes, string OrderedBlockSha256);

/// <summary>Fixed-address, unbuffered worker operations; all work remains under the verification runner's owned child.</summary>
[SupportedOSPlatform("windows")]
internal static class ReadTailWorker
{
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    public static async Task<ReadTailResult> MeasureAsync(ReadTailCase test, string path)
    {
        ReadTailDecision.ValidateCase(test);
        ValidateFile(path, test.FileBytes);
        var offsets = ReadTailDecision.AddressOffsets(test);
        var latencies = new long[test.OperationCount];
        using var handle = File.OpenHandle(path, FileMode.Open, test.Writes ? FileAccess.ReadWrite : FileAccess.Read,
            FileShare.ReadWrite, FileOptions.Asynchronous | NoBuffering);
        var started = Stopwatch.StartNew();
        var lanes = Enumerable.Range(0, test.QueueDepth).Select(Lane).ToArray();
        await Task.WhenAll(lanes);
        return ReadTailDecision.Summarize(test, latencies, started.Elapsed.TotalSeconds,
            ReadTailDecision.AddressSequenceSha256(test));

        async Task Lane(int lane)
        {
            using var memory = new AlignedMemory(test.BlockBytes);
            for (var operation = lane; operation < test.OperationCount; operation += test.QueueDepth)
            {
                var buffer = memory.Memory[..test.BlockBytes];
                if (test.Writes) FillBlock(buffer.Span, test.Seed, operation);
                var requestStarted = Stopwatch.GetTimestamp();
                if (test.Writes)
                    await RandomAccess.WriteAsync(handle, buffer, offsets[operation]).ConfigureAwait(false);
                else
                {
                    var transferred = await RandomAccess.ReadAsync(handle, buffer, offsets[operation]).ConfigureAwait(false);
                    if (transferred != test.BlockBytes)
                        throw new IOException($"Short read-tail request at {offsets[operation]}: {transferred}/{test.BlockBytes} bytes.");
                }
                latencies[operation] = Stopwatch.GetTimestamp() - requestStarted;
            }
        }
    }

    public static async Task<ReadTailWarmResult> WarmResidentAsync(string path, long fileBytes)
    {
        if (fileBytes != (1L << 30)) throw new ArgumentOutOfRangeException(nameof(fileBytes));
        ValidateFile(path, fileBytes);
        const int blockBytes = 1 << 20;
        const int queueDepth = 8;
        const int operations = (1 << 30) / blockBytes;
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            FileOptions.Asynchronous | NoBuffering);
        var started = Stopwatch.StartNew();
        var lanes = Enumerable.Range(0, queueDepth).Select(Lane).ToArray();
        await Task.WhenAll(lanes);
        return new(fileBytes, operations, started.Elapsed.TotalSeconds);

        async Task Lane(int lane)
        {
            using var memory = new AlignedMemory(blockBytes);
            for (var operation = lane; operation < operations; operation += queueDepth)
            {
                var transferred = await RandomAccess.ReadAsync(handle, memory.Memory, (long)operation * blockBytes).ConfigureAwait(false);
                if (transferred != blockBytes)
                    throw new IOException($"Short read-tail resident warm-up at operation {operation}: {transferred}/{blockBytes} bytes.");
            }
        }
    }

    public static async Task<ReadTailWriteVerification> VerifyWritesAsync(ReadTailCase test, string path)
    {
        if (!test.Writes) throw new ArgumentException("The write oracle only accepts foreground-write cases.");
        ValidateFile(path, test.FileBytes);
        var offsets = ReadTailDecision.AddressOffsets(test);
        var blockHashes = new byte[test.OperationCount][];
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            FileOptions.Asynchronous | NoBuffering);
        var lanes = Enumerable.Range(0, test.QueueDepth).Select(Lane).ToArray();
        await Task.WhenAll(lanes);
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var blockHash in blockHashes) aggregate.AppendData(blockHash);
        return new(test.OperationCount, checked((long)test.OperationCount * test.BlockBytes),
            Convert.ToHexString(aggregate.GetHashAndReset()));

        async Task Lane(int lane)
        {
            using var memory = new AlignedMemory(test.BlockBytes);
            using var expected = new AlignedMemory(test.BlockBytes);
            for (var operation = lane; operation < test.OperationCount; operation += test.QueueDepth)
            {
                var transferred = await RandomAccess.ReadAsync(handle, memory.Memory, offsets[operation]).ConfigureAwait(false);
                if (transferred != test.BlockBytes)
                    throw new IOException($"Short read-tail write-oracle read at operation {operation}: {transferred}/{test.BlockBytes} bytes.");
                FillBlock(expected.Memory.Span, test.Seed, operation);
                if (!memory.Memory.Span.SequenceEqual(expected.Memory.Span))
                    throw new InvalidDataException($"Foreground write oracle differs at operation {operation}, offset {offsets[operation]}.");
                blockHashes[operation] = SHA256.HashData(memory.Memory.Span);
            }
        }
    }

    private static void ValidateFile(string path, long minimumBytes)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < minimumBytes)
            throw new IOException($"Read-tail target {path} is missing or shorter than {minimumBytes} bytes.");
    }

    private static void FillBlock(Span<byte> buffer, ulong seed, int operation)
    {
        var state = seed ^ ((ulong)(uint)operation * 0xD6E8FEB86659FD93UL);
        Span<byte> word = stackalloc byte[sizeof(ulong)];
        for (var offset = 0; offset < buffer.Length; offset += sizeof(ulong))
        {
            state += 0x9E3779B97F4A7C15UL;
            var value = state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            value ^= value >> 31;
            BinaryPrimitives.WriteUInt64LittleEndian(word, value);
            word[..Math.Min(sizeof(ulong), buffer.Length - offset)].CopyTo(buffer[offset..]);
        }
    }

    /// <summary>Page-aligned memory required by Windows unbuffered overlapped I/O.</summary>
    private sealed unsafe class AlignedMemory : MemoryManager<byte>
    {
        private readonly byte* pointer;
        private readonly int length;

        public AlignedMemory(int length)
        {
            this.length = length;
            pointer = (byte*)NativeMemory.AlignedAlloc((nuint)length, 4096);
            if (pointer is null) throw new OutOfMemoryException($"Could not allocate {length} bytes of aligned I/O memory.");
        }

        public override Span<byte> GetSpan() => new(pointer, length);
        public override MemoryHandle Pin(int elementIndex = 0) => new(pointer + elementIndex);
        public override void Unpin() { }
        protected override void Dispose(bool disposing) => NativeMemory.AlignedFree(pointer);
    }
}
