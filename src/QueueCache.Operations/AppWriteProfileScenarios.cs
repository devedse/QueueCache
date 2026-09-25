using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using QueueCache.Management;

namespace QueueCache.Operations;

/// <summary>
/// T085 characterization on a validated disposable non-OS volume: how ordinary application writes
/// (buffered close, buffered + explicit flush, memory-mapped) reach the cache. Measures application-visible
/// time and whether bytes arrive as RAM-admitted writes or as forwarded paging writes. Measurement only:
/// no admission threshold is asserted, but final bytes are verified after the cache is released.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AppWriteProfileScenarios
{
    private const int MiB = 1 << 20;
    private const int FileBytes = 256 * MiB;

    internal sealed record Counters(ulong Accepted, ulong PagingWriteBytes, ulong PagingForwardedWrites,
        ulong ForwardedWrites, ulong GeneratedWrites, ulong PagingAdmittedBytes = 0);

    /// <summary>Bytes handed to the disk stack between two samples. An admitted paging write is counted both as
    /// accepted and as a paging write, so it must not count twice.</summary>
    internal static ulong Arrived(Counters before, Counters after) =>
        (after.Accepted - before.Accepted) - (after.PagingAdmittedBytes - before.PagingAdmittedBytes) +
        (after.PagingWriteBytes - before.PagingWriteBytes);

    internal static Counters Read(CacheDevice device)
    {
        var state = device.GetWriteCacheState();
        var diagnostics = device.GetDiagnostics();
        var io = diagnostics.PagingIo ?? throw new NotSupportedException("Requires Diagnostics V5 paging counters.");
        var sources = diagnostics.LowerSources ?? throw new NotSupportedException("Requires Diagnostics V8 source attribution.");
        // Drivers before Diagnostics V10 never admit paging writes, so their admitted paging bytes are truly zero.
        return new(state.AcceptedBytes, io.WriteBytes, sources.PagingForwardedWrites, sources.ForwardedWrites,
            sources.GeneratedWrites, diagnostics.PagingAdmission?.AdmittedBytes ?? 0);
    }

    /// <summary>Plain-language summary of one mode's counter deltas.</summary>
    internal static string Describe(string mode, double appMs, double settleMs, Counters before, Counters after)
    {
        static double Mb(ulong bytes) => bytes / (double)MiB;
        var accepted = after.Accepted - before.Accepted;
        var paging = after.PagingWriteBytes - before.PagingWriteBytes;
        var appMbps = FileBytes / (double)MiB / Math.Max(appMs / 1000.0, 0.001);
        var forwarded = after.PagingForwardedWrites - before.PagingForwardedWrites;
        return FormattableString.Invariant(
            $"{mode}: application saw {appMbps:F0} MiB/s ({appMs:F0} ms for {FileBytes / MiB} MiB); ") +
            FormattableString.Invariant(
                $"QueueCache RAM-admitted {Mb(accepted):F1} MiB; paging-marked writes {Mb(paging):F1} MiB ") +
            FormattableString.Invariant(
                $"({forwarded} forwarded straight to disk); all bytes reached QueueCache within {settleMs:F0} ms of completion.");
    }

    /// <summary>T085 acceptance: every application mode's bytes reach QueueCache RAM (at least 90%; NTFS may
    /// write a little metadata separately). Older drivers without V10 cannot admit and report SKIP.</summary>
    internal static CheckResult VerifyAdmission(bool admissionAvailable, IReadOnlyDictionary<string, ulong> admitted)
    {
        const string id = "app-write-profile/application-writes-admitted";
        var detail = string.Join("; ", admitted.Select(pair =>
            FormattableString.Invariant($"{pair.Key} {pair.Value / (double)MiB:F1} MiB admitted")));
        if (!admissionAvailable)
            return new(id, "SKIP", detail + ". The loaded driver predates application paging admission (V10).");
        var short_ = admitted.Where(pair => pair.Value < (ulong)FileBytes * 9 / 10).Select(pair => pair.Key).ToList();
        if (short_.Count != 0)
            throw new IOException($"{id}: application writes were not admitted to RAM for {string.Join(", ", short_)} ({detail}).");
        return new(id, "PASS", detail + FormattableString.Invariant($" of {FileBytes / MiB} MiB each."));
    }

    /// <summary>Cache payload must not come from the kernel nonpaged pool: a 1 GiB Configure may add only
    /// descriptors, index and staging (well under 10% of the budget).</summary>
    internal static CheckResult VerifyPoolIndependent(bool pageBacked, ulong poolBefore, ulong poolAfter, ulong budget)
    {
        const string id = "app-write-profile/cache-memory-outside-nonpaged-pool";
        var delta = poolAfter > poolBefore ? poolAfter - poolBefore : 0;
        var detail = FormattableString.Invariant(
            $"Kernel nonpaged pool changed by {delta / (double)MiB:F1} MiB while configuring a {budget / MiB} MiB cache.");
        if (!pageBacked)
            return new(id, "SKIP", detail + " The loaded driver predates page-backed cache memory.");
        if (delta > budget / 10)
            throw new IOException($"{id}: {detail} Cache payload appears to use nonpaged pool.");
        return new(id, "PASS", detail);
    }

    private static ulong KernelNonpagedBytes()
    {
        var info = new PerformanceInformation { Size = (uint)Marshal.SizeOf<PerformanceInformation>() };
        if (!GetPerformanceInfo(ref info, info.Size))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return (ulong)info.KernelNonpaged * (ulong)info.PageSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache,
            KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(ref PerformanceInformation information, uint size);

    public static IReadOnlyList<CheckResult> Run(DiskTarget target, CacheDevice device, string workDirectory)
    {
        target.ValidateCurrent();
        if (target.IsBoot || target.IsSystem || target.IsPaging ||
            !string.Equals(Path.GetDirectoryName(Path.GetFullPath(workDirectory)), target.Root,
                StringComparison.OrdinalIgnoreCase) || Directory.Exists(workDirectory))
            throw new IOException("Application write profile requires a fresh owned directory on a non-OS disk.");
        Directory.CreateDirectory(workDirectory);
        // Release the existing cache first so the kernel nonpaged-pool delta of a fresh 1 GiB Configure is measured.
        device.Control(WriteCacheAction.Flush);
        device.Control(WriteCacheAction.Disable);
        device.Control(WriteCacheAction.Release);
        var poolBefore = KernelNonpagedBytes();
        // A cache large enough to hold every mode's bytes, with a policy that does not drain during measurement.
        ConfigurationManager.Apply(target, new CacheConfiguration(1024, CachePreset.Fast)
        {
            Options = new CacheOptions(Drain: DrainAlgorithm.Idle, IdleMs: 60000, MaxDirtyAgeMs: 3600000)
        }, true);
        var poolAfter = KernelNonpagedBytes();
        var admissionAvailable = device.GetDiagnostics().PagingAdmission is not null;
        var admitted = new Dictionary<string, ulong>();
        var block = new byte[4 * MiB];
        new Random(105019).NextBytes(block);
        var results = new List<CheckResult>();
        foreach (var mode in new[] { "buffered-close", "buffered-flush", "mapped-flush" })
        {
            var path = Path.Combine(workDirectory, mode + ".bin");
            var before = Read(device);
            var timer = Stopwatch.StartNew();
            if (mode == "mapped-flush")
            {
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    file.SetLength(FileBytes);
                using var map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite);
                using var view = map.CreateViewAccessor(0, FileBytes, MemoryMappedFileAccess.ReadWrite);
                for (var offset = 0; offset < FileBytes; offset += block.Length)
                {
                    block[0] = (byte)(offset / block.Length);
                    view.WriteArray(offset, block, 0, block.Length);
                }
                view.Flush();
            }
            else
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, MiB,
                    FileOptions.None);
                for (var offset = 0; offset < FileBytes; offset += block.Length)
                {
                    block[0] = (byte)(offset / block.Length);
                    file.Write(block);
                }
                if (mode == "buffered-flush")
                    file.Flush(flushToDisk: true);
            }
            var appMs = timer.Elapsed.TotalMilliseconds;
            // Wait (bounded) until Windows has handed the whole file to the disk stack.
            var settle = Stopwatch.StartNew();
            Counters after;
            do
            {
                after = Read(device);
                if (Arrived(before, after) >= FileBytes)
                    break;
                Thread.Sleep(100);
            } while (settle.Elapsed < TimeSpan.FromSeconds(60));
            var settleMs = settle.Elapsed.TotalMilliseconds;
            admitted[mode] = after.Accepted - before.Accepted;
            results.Add(new($"app-write-profile/{mode}", "PASS", Describe(mode, appMs, settleMs, before, after)));
        }
        results.Add(VerifyAdmission(admissionAvailable, admitted));
        results.Add(VerifyPoolIndependent(admissionAvailable, poolBefore, poolAfter, 1024UL << 20));
        // Integrity: every mode's bytes must be on media after the cache is released.
        device.Control(WriteCacheAction.Flush);
        device.Control(WriteCacheAction.Disable);
        device.Control(WriteCacheAction.Release);
        var actual = new byte[4 * MiB];
        foreach (var mode in new[] { "buffered-close", "buffered-flush", "mapped-flush" })
        {
            using var file = new AlignedFile(Path.Combine(workDirectory, mode + ".bin"), 4 * MiB, create: false);
            for (var offset = 0; offset < FileBytes; offset += block.Length)
            {
                file.Read(offset, actual);
                block[0] = (byte)(offset / block.Length);
                if (!actual.AsSpan().SequenceEqual(block))
                    throw new IOException($"app-write-profile/{mode}: bytes at {offset} differed after release.");
            }
        }
        return results;
    }
}
