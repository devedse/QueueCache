using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace QueueCache.Developer.Verification;

/// <summary>Portable file-only helpers. Windows opens use NO_BUFFERING; Linux host contracts exercise
/// the same scheduling, content and hashes with ordinary handles. No product controls or timing of verification.</summary>
public sealed unsafe class ComparisonDrainFiles : IDisposable
{
    private byte* payload;
    private readonly int bytes;
    private readonly int[] order;
    public ComparisonFileHash[] SmallHashes { get; }
    public string LargeHash { get; }
    public static string SmallName(int index) => $"f{index:D5}.bin";

    public ComparisonDrainFiles(int bytes = ComparisonDrain.PayloadBytes)
    {
        if (bytes <= 0 || bytes % ComparisonDrain.LargeBlockBytes != 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        this.bytes = bytes;
        payload = (byte*)NativeMemory.AlignedAlloc((nuint)bytes, 4096);
        if (payload == null) throw new OutOfMemoryException();
        try
        {
            Fill(new Span<byte>(payload, bytes));
            LargeHash = Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>(payload, bytes)));
            var files = Math.Min(bytes, ComparisonDrain.SmallBytes) / ComparisonDrain.SmallFileBytes;
            SmallHashes = Enumerable.Range(0, files).Select(i => new ComparisonFileHash(SmallName(i), ComparisonDrain.SmallFileBytes,
                Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>(payload + i * ComparisonDrain.SmallFileBytes, ComparisonDrain.SmallFileBytes))))).ToArray();
            order = ShuffledOrder(files);
        }
        catch { Dispose(); throw; }
    }

    public static void Fill(Span<byte> destination)
    {
        if (destination.Length % 8 != 0) throw new ArgumentException("Whole 64-bit words required.");
        // SplitMix64: fixed algorithm and seed, one distinct word per offset across the entire GiB.
        // No per-file/block seed reset, repeated template, compression or timed data generation.
        ulong state = ComparisonDrain.Seed;
        for (var offset = 0; offset < destination.Length; offset += 8)
        {
            state = unchecked(state + 0x9e3779b97f4a7c15UL);
            var word = state;
            word = unchecked((word ^ (word >> 30)) * 0xbf58476d1ce4e5b9UL);
            word = unchecked((word ^ (word >> 27)) * 0x94d049bb133111ebUL);
            BinaryPrimitives.WriteUInt64LittleEndian(destination[offset..], word ^ (word >> 31));
        }
    }

    public static int[] ShuffledOrder(int files)
    {
        var result = Enumerable.Range(0, files).ToArray();
        var random = new Random(ComparisonDrain.Seed);
        for (var i = files - 1; i > 0; i--) { var j = random.Next(i + 1); (result[i], result[j]) = (result[j], result[i]); }
        return result;
    }

    private static FileOptions Options => (OperatingSystem.IsWindows() ? (FileOptions)0x20000000 : FileOptions.None) | FileOptions.Asynchronous;

    /// <summary>Four ready threads share an overlapped CreateNew handle (large Q4), or each creates
    /// one small file at a time in a fixed shuffled lane. New directory and all file metadata are timed.
    /// onStart runs immediately before releasing writers, outside their transfer loops.</summary>
    public (DateTimeOffset Started, DateTimeOffset Ended, double Seconds) Write(string directory, bool small, Action<DateTimeOffset> onStart)
    {
        if (Directory.Exists(directory)) throw new IOException("Scored directory already exists: " + directory);
        using var ready = new CountdownEvent(ComparisonDrain.Workers);
        using var start = new ManualResetEventSlim();
        var errors = new ConcurrentQueue<Exception>();
        SafeFileHandle? large = null;
        long next = -ComparisonDrain.LargeBlockBytes;
        var workers = Enumerable.Range(0, ComparisonDrain.Workers).Select(lane => new Thread(() =>
        {
            ready.Signal(); start.Wait();
            try
            {
                if (small)
                {
                    for (var position = lane; position < order.Length; position += ComparisonDrain.Workers)
                    {
                        var index = order[position];
                        using var file = File.OpenHandle(Path.Combine(directory, SmallName(index)), FileMode.CreateNew, FileAccess.Write, FileShare.Read, Options);
                        for (var offset = 0; offset < ComparisonDrain.SmallFileBytes; offset += ComparisonDrain.SmallBlockBytes)
                            RandomAccess.Write(file, new ReadOnlySpan<byte>(payload + index * ComparisonDrain.SmallFileBytes + offset, ComparisonDrain.SmallBlockBytes), offset);
                    }
                }
                else
                {
                    while (true)
                    {
                        var offset = Interlocked.Add(ref next, ComparisonDrain.LargeBlockBytes);
                        if (offset >= bytes) break;
                        RandomAccess.Write(large!, new ReadOnlySpan<byte>(payload + offset, ComparisonDrain.LargeBlockBytes), offset);
                    }
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        }) { IsBackground = true }).ToArray();
        foreach (var thread in workers) thread.Start();
        ready.Wait();
        var began = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        try
        {
            onStart(began);
            Directory.CreateDirectory(directory);
            if (!small) large = File.OpenHandle(Path.Combine(directory, "large.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, Options);
        }
        catch (Exception ex) { errors.Enqueue(ex); }
        finally
        {
            // Always release/join owned threads, including on a failed create; never free a live buffer.
            start.Set();
            foreach (var thread in workers) thread.Join();
            large?.Dispose();
        }
        var seconds = timer.Elapsed.TotalSeconds;
        var ended = DateTimeOffset.UtcNow;
        if (!errors.IsEmpty) throw new AggregateException("compare-drain file writes failed.", errors);
        return (began, ended, seconds);
    }

    public ComparisonFileHash[] Verify(string directory, bool small)
    {
        ComparisonFileHash[] expected = small ? SmallHashes : [new("large.bin", bytes, LargeHash)];
        var actualNames = Directory.EnumerateFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        if (!expected.Select(e => e.File).Order(StringComparer.Ordinal).SequenceEqual(actualNames, StringComparer.Ordinal))
            throw new InvalidDataException("Missing or unexpected compare-drain files.");
        var buffer = (byte*)NativeMemory.AlignedAlloc(ComparisonDrain.LargeBlockBytes, 4096);
        if (buffer == null) throw new OutOfMemoryException();
        try
        {
            foreach (var item in expected)
            {
                var path = Path.Combine(directory, item.File);
                using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, Options);
                if (RandomAccess.GetLength(file) != item.Bytes) throw new InvalidDataException("Wrong file length: " + path);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                for (long offset = 0; offset < item.Bytes;)
                {
                    var count = (int)Math.Min(ComparisonDrain.LargeBlockBytes, item.Bytes - offset);
                    var read = RandomAccess.Read(file, new Span<byte>(buffer, count), offset);
                    if (read != count) throw new IOException("Short unbuffered read: " + path);
                    hash.AppendData(new ReadOnlySpan<byte>(buffer, read)); offset += read;
                }
                if (Convert.ToHexString(hash.GetHashAndReset()) != item.Sha256) throw new InvalidDataException("Hash mismatch: " + path);
            }
            return expected;
        }
        finally { NativeMemory.AlignedFree(buffer); }
    }

    public void Dispose() { NativeMemory.AlignedFree(payload); payload = null; }
}
