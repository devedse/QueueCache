using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace QueueCache.Developer.Verification;

/// <summary>One user-mode copy window. No driver is involved.</summary>
public sealed record MemoryCopyResult(string Method, bool Write, int Threads, long Bytes, double Seconds, double MiBPerSecond);

/// <summary>Copies 1 MiB blocks between a cold 1 GiB region and a hot per-thread buffer, as a RAM-disk
/// write (application buffer into storage) or read (the reverse) does, but without the driver. It
/// separates what this machine can copy from what the driver's request handling allows.
/// "temporal" and "streaming" are explicit SSE2 loops (the instructions a kernel copy may use without
/// saving extended state); "streaming" uses non-temporal stores, which skip reading the destination.</summary>
public static unsafe class MemoryCopyProbe
{
    public const long RegionBytes = 1L << 30;
    public const int BlockBytes = 1 << 20, Seconds = 5;
    public static readonly string[] Methods = ["runtime", "temporal", "streaming"];

    public static MemoryCopyResult Run(string method, bool write, int threads, int seconds = Seconds)
    {
        var code = Array.IndexOf(Methods, method);
        if (code < 0 || threads is < 1 or > 16 || seconds is < 1 or > 60 || (!write && method == "streaming"))
            throw new ArgumentException("Unsupported memory copy probe.");
        if (!Sse2.IsSupported) throw new PlatformNotSupportedException("The memory copy probe requires SSE2.");
        var region = (nint)NativeMemory.AlignedAlloc((nuint)RegionBytes, 4096);
        var buffers = new nint[threads];
        try
        {
            // Touch every page first, so the window measures copies rather than page faults.
            NativeMemory.Fill((void*)region, (nuint)RegionBytes, 0x5A);
            var slice = RegionBytes / threads / BlockBytes * BlockBytes;
            for (var i = 0; i < threads; i++)
            {
                buffers[i] = (nint)NativeMemory.AlignedAlloc(BlockBytes, 64);
                new Random(7 + i).NextBytes(new Span<byte>((void*)buffers[i], BlockBytes));
            }
            var totals = new long[threads];
            var measuring = 0; var stop = 0;
            using var start = new Barrier(threads + 1);
            var workers = Enumerable.Range(0, threads).Select(index => new Thread(() =>
            {
                var hot = (byte*)buffers[index]; var cold = (byte*)region + index * slice;
                long offset = 0, bytes = 0;
                start.SignalAndWait();
                while (Volatile.Read(ref stop) == 0)
                {
                    if (write) Copy(code, hot, cold + offset); else Copy(code, cold + offset, hot);
                    offset = (offset + BlockBytes) % slice;
                    if (Volatile.Read(ref measuring) != 0) bytes += BlockBytes;
                }
                totals[index] = bytes;
            }) { IsBackground = true }).ToArray();
            foreach (var worker in workers) worker.Start();
            start.SignalAndWait();
            Thread.Sleep(1000); // warmup, not counted
            var timer = Stopwatch.StartNew();
            Volatile.Write(ref measuring, 1);
            Thread.Sleep(seconds * 1000);
            Volatile.Write(ref measuring, 0);
            var elapsed = timer.Elapsed.TotalSeconds;
            Volatile.Write(ref stop, 1);
            foreach (var worker in workers) worker.Join();
            var total = totals.Sum();
            var result = new MemoryCopyResult(method, write, threads, total, elapsed, total / elapsed / (1 << 20));
            Validate(result, seconds);
            return result;
        }
        finally
        {
            foreach (var buffer in buffers) if (buffer != 0) NativeMemory.AlignedFree((void*)buffer);
            NativeMemory.AlignedFree((void*)region);
        }
    }

    public static void Validate(MemoryCopyResult result, int seconds = Seconds)
    {
        if (result.Bytes <= 0 || result.Bytes % BlockBytes != 0 || result.Seconds < seconds * 0.95 || result.Seconds > seconds * 2 ||
            !(result.MiBPerSecond > 0) || !Methods.Contains(result.Method) || result.Threads is < 1 or > 16)
            throw new InvalidDataException("Memory copy probe has missing or implausible bytes/time.");
    }

    private static void Copy(int code, byte* source, byte* destination)
    {
        if (code == 0) { Buffer.MemoryCopy(source, destination, BlockBytes, BlockBytes); return; }
        for (var i = 0; i < BlockBytes; i += 64)
        {
            var a = Sse2.LoadAlignedVector128(source + i); var b = Sse2.LoadAlignedVector128(source + i + 16);
            var c = Sse2.LoadAlignedVector128(source + i + 32); var d = Sse2.LoadAlignedVector128(source + i + 48);
            if (code == 1)
            {
                Sse2.StoreAligned(destination + i, a); Sse2.StoreAligned(destination + i + 16, b);
                Sse2.StoreAligned(destination + i + 32, c); Sse2.StoreAligned(destination + i + 48, d);
            }
            else
            {
                Sse2.StoreAlignedNonTemporal(destination + i, a); Sse2.StoreAlignedNonTemporal(destination + i + 16, b);
                Sse2.StoreAlignedNonTemporal(destination + i + 32, c); Sse2.StoreAlignedNonTemporal(destination + i + 48, d);
            }
        }
        if (code == 2) Sse.StoreFence();
    }
}
