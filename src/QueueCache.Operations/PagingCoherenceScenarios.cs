using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using QueueCache.Management;

namespace QueueCache.Operations;

/// <summary>Bounded file-only mixed access on a validated disposable non-OS volume.</summary>
[SupportedOSPlatform("windows")]
public static class PagingCoherenceScenarios
{
    private const int MiB = 1 << 20;
    private const int FileBytes = 8 * MiB;
    private const int Offset = 4 * MiB;
    private const int SparseBytes = 3 * 512;

    internal static string VerifyObservedOverlap(CachePagingRoute before, CachePagingRoute after,
        ulong observedInFlight)
    {
        if (observedInFlight != SparseBytes || after.WriteRequests <= before.WriteRequests ||
            after.WriteCompletions < before.WriteCompletions ||
            after.WriteCompletions - before.WriteCompletions != after.WriteRequests - before.WriteRequests ||
            after.WriteFailures != before.WriteFailures || after.OverlapWaits <= before.OverlapWaits)
            throw new IOException("The mapped overwrite did not produce a successful observed paging/older-drain overlap.");
        return $"Observed an isolated {SparseBytes}-byte older write in flight, " +
            $"then {after.WriteRequests - before.WriteRequests} successful routed paging write(s) " +
            $"and {after.OverlapWaits - before.OverlapWaits} overlap wait(s). " +
            "Newest active and released-cache bytes, including untouched guards, matched. " +
            "Routed counters are process-wide; a range-targeted kernel gate remains the stronger proof.";
    }

    public static IReadOnlyList<CheckResult> Run(DiskTarget target, CacheDevice device, string workDirectory)
    {
        target.ValidateCurrent();
        if (target.IsBoot || target.IsSystem || target.IsPaging ||
            !string.Equals(Path.GetDirectoryName(Path.GetFullPath(workDirectory)), target.Root,
                StringComparison.OrdinalIgnoreCase) || Directory.Exists(workDirectory))
            throw new IOException("Mixed paging/file check requires a fresh owned directory on a non-OS disk.");
        if (!GetDiskFreeSpaceW(target.Root, out _, out var sectorBytes, out _, out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (sectorBytes != 512)
            throw new NotSupportedException("The forced sparse-overlap case requires a 512-byte-sector volume.");
        if (device.GetDiagnostics().PagingRoute is null)
            throw new NotSupportedException("Mixed paging/file check requires routed paging Diagnostics V7.");
        Directory.CreateDirectory(workDirectory);
        var path = Path.Combine(workDirectory, "mapped-coherence.bin");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            file.SetLength(FileBytes);
        ConfigurationManager.Apply(target, new CacheConfiguration(64, CachePreset.Fast)
        {
            Options = new CacheOptions(Drain: DrainAlgorithm.Idle)
        }, true);
        var enabled = device.GetWriteCacheState();
        var before = device.GetDiagnostics();
        var a = new byte[MiB];
        var b = new byte[MiB];
        new Random(104729).NextBytes(a);
        new Random(104759).NextBytes(b);
        using (var file = new AlignedFile(path, MiB, create: false))
        {
            file.Write(Offset, a);
            file.Flush();
        }
        var admitted = device.GetWriteCacheState();
        if (admitted.AcceptedBytes < enabled.AcceptedBytes + MiB)
            throw new IOException("Fast cache did not accept the first owned write.");
        var actual = new byte[MiB];
        using (var map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0,
                   MemoryMappedFileAccess.ReadWrite))
        using (var view = map.CreateViewAccessor(0, FileBytes, MemoryMappedFileAccess.ReadWrite))
        {
            view.ReadArray(Offset, actual, 0, actual.Length);
            if (!actual.AsSpan().SequenceEqual(a))
                throw new IOException("Mapped read returned bytes older than the accepted unbuffered write.");
            view.WriteArray(Offset, b, 0, b.Length);
            view.Flush();
        }
        Array.Clear(actual);
        using (var file = new AlignedFile(path, MiB, create: false))
            file.Read(Offset, actual);
        if (!actual.AsSpan().SequenceEqual(b))
            throw new IOException("Unbuffered read did not observe the latest mapped write.");
        var after = device.GetDiagnostics();
        var first = before.PagingRoute!;
        var last = after.PagingRoute ?? throw new IOException("Routed paging diagnostics disappeared.");
        if (last.ReadRequests < first.ReadRequests || last.WriteRequests < first.WriteRequests ||
            last.ReadFailures != first.ReadFailures || last.WriteFailures != first.WriteFailures)
            throw new IOException("Routed paging counters regressed or reported a failure.");
        device.Control(WriteCacheAction.Flush);
        device.Control(WriteCacheAction.Disable);
        device.Control(WriteCacheAction.Release);
        Array.Clear(actual);
        using (var file = new AlignedFile(path, MiB, create: false))
            file.Read(Offset, actual);
        if (!actual.AsSpan().SequenceEqual(b))
            throw new IOException("Released-cache disk bytes did not match the latest mapped write.");
        var overlapCheck = RunObservedPagingOverlap(target, device, workDirectory);
        return
        [
            new("paging-coherence/mapped-after-cache", "PASS", "Mapped read matched the accepted unbuffered write."),
            new("paging-coherence/cache-after-mapped", "PASS", "Unbuffered read and released-cache disk bytes matched the mapped overwrite."),
            new("paging-coherence/observed-routing",
                last.ReadRequests > first.ReadRequests && last.WriteRequests > first.WriteRequests ? "PASS" : "SKIP",
                $"Routed paging-marked requests: reads {last.ReadRequests - first.ReadRequests}, " +
                $"writes {last.WriteRequests - first.WriteRequests}, overlap waits {last.OverlapWaits - first.OverlapWaits}. " +
                "These counters are process-wide; zero overlap does not prove the forced drainer order."),
            overlapCheck
        ];
    }

    private static CheckResult RunObservedPagingOverlap(DiskTarget target, CacheDevice device, string workDirectory)
    {
        const int patchOffset = Offset + 512;
        const int patchBytes = SparseBytes;
        var path = Path.Combine(workDirectory, "inflight-mapped-overlap.bin");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            file.SetLength(FileBytes);
        ConfigurationManager.Apply(target, new CacheConfiguration(64, CachePreset.Fast)
        {
            Options = new CacheOptions(Drain: DrainAlgorithm.Eager, Parallelism: 1, RetainWrites: false)
        }, true);
        var a = new byte[patchBytes];
        var b = new byte[patchBytes];
        new Random(104761).NextBytes(a);
        new Random(104773).NextBytes(b);
        device.Control(WriteCacheAction.LabDelay, value: 2000);
        CachePagingRoute? before = null;
        CachePagingRoute after;
        ulong observedInFlight = 0;
        try
        {
            using (var file = new AlignedFile(path, 4096, create: false, alignment: 512))
            {
                file.Write(patchOffset, a);
                // A 1,536-byte in-flight transfer isolates this sparse payload
                // from ordinary 4 KiB NTFS metadata writes. A timeout is an
                // incomplete observation, never an inferred overlap pass.
                if (!SpinWait.SpinUntil(() =>
                    {
                        observedInFlight = device.GetWriteCacheState().InFlightBytes;
                        return observedInFlight == patchBytes;
                    }, 8000))
                    throw new IOException("The owned sparse write did not enter an isolated in-flight interval.");
            }
            before = device.GetDiagnostics().PagingRoute ??
                throw new NotSupportedException("Forced overlap requires Diagnostics V7.");
            using (var map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0,
                       MemoryMappedFileAccess.ReadWrite))
            using (var view = map.CreateViewAccessor(0, FileBytes, MemoryMappedFileAccess.ReadWrite))
            {
                view.WriteArray(patchOffset, b, 0, b.Length);
                view.Flush();
            }
            after = device.GetDiagnostics().PagingRoute ??
                throw new IOException("Routed paging diagnostics disappeared during overlap.");
        }
        finally
        {
            device.Control(WriteCacheAction.LabDelay, value: 0);
        }
        var first = before ?? throw new IOException("Missing routed paging baseline during overlap.");
        var overlapEvidence = VerifyObservedOverlap(first, after, observedInFlight);
        var expected = new byte[4096];
        b.CopyTo(expected, 512);
        var actual = new byte[expected.Length];
        using (var file = new AlignedFile(path, 4096, create: false, alignment: 512))
            file.Read(Offset, actual);
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new IOException("Newest mapped bytes or untouched sector guards differed while cache was active.");
        device.Control(WriteCacheAction.Flush);
        device.Control(WriteCacheAction.Disable);
        device.Control(WriteCacheAction.Release);
        Array.Clear(actual);
        using (var file = new AlignedFile(path, 4096, create: false, alignment: 512))
            file.Read(Offset, actual);
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new IOException("Newest mapped bytes or untouched sector guards differed after cache release.");
        return new("paging-coherence/observed-inflight-mapped-overlap", "PASS", overlapEvidence);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceW(string root, out uint clustersPerUnit, out uint bytesPerSector,
        out uint freeClusters, out uint totalClusters);
}
