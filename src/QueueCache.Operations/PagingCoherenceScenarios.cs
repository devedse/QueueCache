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

    /// <summary>The owned sparse payload is in flight. Since T085 NTFS metadata write-back is admitted too and
    /// may share the interval, but it moves whole 4 KiB pages; only the owned write leaves a 1,536-byte remainder.
    /// </summary>
    internal static bool SparseInFlight(ulong inFlight) => inFlight % 4096 == SparseBytes;

    internal static string VerifyObservedOverlap(CachePagingRoute before, CachePagingRoute after,
        ulong observedInFlight)
    {
        if (!SparseInFlight(observedInFlight) || after.WriteRequests <= before.WriteRequests ||
            after.WriteCompletions < before.WriteCompletions ||
            after.WriteCompletions - before.WriteCompletions != after.WriteRequests - before.WriteRequests ||
            after.WriteFailures != before.WriteFailures || after.OverlapWaits <= before.OverlapWaits)
            throw new IOException("The mapped overwrite did not produce a successful observed paging/older-drain overlap.");
        return $"Observed an isolated {SparseBytes}-byte older write in flight ({observedInFlight} bytes in flight in total), " +
            $"then {after.WriteRequests - before.WriteRequests} successful routed paging write(s) " +
            $"and {after.OverlapWaits - before.OverlapWaits} overlap wait(s). " +
            "Newest active and released-cache bytes, including untouched guards, matched. " +
            "Routed counters are device-wide across all processes; a range-targeted kernel gate remains the stronger proof.";
    }

    /// <summary>
    /// T083 gate order: the old overlapping drain was submitted and completed at the lower device, the newer
    /// direct paging write started waiting while that drain was still held in flight, and the direct write was
    /// submitted only after the old drain retired. Any missing or out-of-order event fails.
    /// </summary>
    internal static string VerifyGateOrder(CacheLabGate? gate)
    {
        if (gate is null)
            throw new NotSupportedException("Gated ordering requires Diagnostics V9.");
        if (gate.Hits != 1)
            throw new IOException($"The range gate held {gate.Hits} drain batches; exactly one was required.");
        ulong[] order = [gate.OldSubmitSeq, gate.OldLowerDoneSeq, gate.DirectWaitSeq, gate.OldRetireSeq,
            gate.DirectSubmitSeq, gate.DirectDoneSeq];
        if (order.Any(value => value == 0))
            throw new IOException("Gate evidence is incomplete (submit/lower-done/direct-wait/retire/direct-submit/direct-done " +
                string.Join('/', order) + ").");
        for (var index = 1; index < order.Length; index++)
            if (order[index] <= order[index - 1])
                throw new IOException("Gate events are out of order (submit/lower-done/direct-wait/retire/direct-submit/direct-done " +
                    string.Join('/', order) + ").");
        return $"Gate sequence submit {order[0]} < old lower completion {order[1]} < direct paging write waiting {order[2]} " +
            $"< old retirement {order[3]} < direct submission {order[4]} < direct completion {order[5]}.";
    }

    /// <summary>T082: an offloaded page-in completed while a cached write was still capacity-blocked.</summary>
    internal static CheckResult VerifyBlockedPageIn(CachePagingOffload? before, CachePagingOffload? after,
        bool writerStillBlocked, int pagesMatched, double slowestPageMs)
    {
        if (before is null || after is null)
            throw new NotSupportedException("Blocked page-in check requires Diagnostics V8 offload counters.");
        var offloaded = after.OffloadedReads - before.OffloadedReads;
        var completed = after.Completions - before.Completions;
        var failed = after.Failures - before.Failures;
        if (after.OffloadedReads < before.OffloadedReads || after.Completions < before.Completions || failed != 0)
            throw new IOException($"Offloaded page-in counters regressed or failed (failures {failed}).");
        var detail = $"{pagesMatched} mapped page(s) matched; slowest fault {slowestPageMs:F1} ms; " +
            $"offloaded/completed paging reads {offloaded}/{completed}; writer still capacity-blocked: {writerStillBlocked}.";
        if (offloaded == 0 || completed < offloaded)
            return new("paging-coherence/capacity-blocked-page-in", "SKIP",
                detail + " No page-in was observed completing on the paging-read thread; the dependency is unproven.");
        if (!writerStillBlocked)
            return new("paging-coherence/capacity-blocked-page-in", "SKIP",
                detail + " The blocked write finished before the page-ins; the dependency is unproven.");
        return new("paging-coherence/capacity-blocked-page-in", "PASS",
            detail + " Page-ins completed on the paging-read thread while the request worker was blocked.");
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
        var gatedChecks = RunGatedSubmittedOverlap(target, device, workDirectory);
        var blockedPageIn = RunCapacityBlockedPageIn(target, device, workDirectory);
        return
        [
            new("paging-coherence/mapped-after-cache", "PASS", "Mapped read matched the accepted unbuffered write."),
            new("paging-coherence/cache-after-mapped", "PASS", "Unbuffered read and released-cache disk bytes matched the mapped overwrite."),
            new("paging-coherence/observed-routing",
                last.ReadRequests > first.ReadRequests && last.WriteRequests > first.WriteRequests ? "PASS" : "SKIP",
                $"Routed paging-marked requests: reads {last.ReadRequests - first.ReadRequests}, " +
                $"writes {last.WriteRequests - first.WriteRequests}, overlap waits {last.OverlapWaits - first.OverlapWaits}. " +
                "These counters are device-wide across all processes; zero overlap does not prove the forced drainer order."),
            overlapCheck,
            .. gatedChecks,
            blockedPageIn
        ];
    }

    // T083: hold an old overlapping drain after its real lower write completed; a newer mapped (paging)
    // write must wait for its retirement. Then a later cached write C must remain newest.
    private static IReadOnlyList<CheckResult> RunGatedSubmittedOverlap(DiskTarget target, CacheDevice device,
        string workDirectory)
    {
        const int blockBytes = 4096;
        const ulong holdMs = 2000;
        if (device.GetDiagnostics().LabGate is null)
            throw new NotSupportedException("Gated ordering requires Diagnostics V9 (plan-39 driver).");
        var path = Path.Combine(workDirectory, "gated-submitted-overlap.bin");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            file.SetLength(FileBytes);
        ConfigurationManager.Apply(target, new CacheConfiguration(64, CachePreset.Fast)
        {
            Options = new CacheOptions(Drain: DrainAlgorithm.Eager, Parallelism: 1, RetainWrites: false)
        }, true);
        var a = new byte[blockBytes];
        var b = new byte[blockBytes];
        var c = new byte[blockBytes];
        new Random(104789).NextBytes(a);
        new Random(104801).NextBytes(b);
        new Random(104827).NextBytes(c);
        var routeBefore = device.GetDiagnostics().PagingRoute!;
        CacheLabGate? gate;
        double directMilliseconds;
        // The file was extended (clusters allocated) above; resolve the owned block's disk location.
        var diskOffset = DiskOffsetOf(path, Offset, target.Root);
        if (diskOffset % blockBytes != 0)
            throw new NotSupportedException("The owned block is not 4 KiB aligned on disk; the gated case needs aligned clusters.");
        // Since T085 ordinary mapped writes use RAM admission; this stage verifies the direct paging path.
        device.SetSpecialRanges([new DiskRange(diskOffset, blockBytes)], SpecialRangeKind.ForceDirect);
        device.Control(WriteCacheAction.LabGate, (ulong)diskOffset, (holdMs << 32) | blockBytes);
        try
        {
            using (var file = new AlignedFile(path, blockBytes, create: false))
                file.Write(Offset, a);
            // Proceed only once A's real lower write has completed and the gate is holding it in flight.
            CacheLabGate? held = null;
            if (!SpinWait.SpinUntil(() =>
                {
                    held = device.GetDiagnostics().LabGate;
                    return held is { State: 2, OldLowerDoneSeq: > 0 };
                }, 8000))
                throw new IOException($"The owned write was not held after lower completion (gate state {held?.State}).");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            using (var map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0,
                       MemoryMappedFileAccess.ReadWrite))
            using (var view = map.CreateViewAccessor(0, FileBytes, MemoryMappedFileAccess.ReadWrite))
            {
                view.WriteArray(Offset, b, 0, b.Length);
                view.Flush();
            }
            directMilliseconds = timer.Elapsed.TotalMilliseconds;
            gate = device.GetDiagnostics().LabGate;
        }
        finally
        {
            device.Control(WriteCacheAction.LabGate, value: 0);
            device.SetSpecialRanges([], SpecialRangeKind.ForceDirect);
        }
        var order = VerifyGateOrder(gate);
        var routeAfter = device.GetDiagnostics().PagingRoute!;
        if (routeAfter.WriteFailures != routeBefore.WriteFailures || routeAfter.OverlapWaits <= routeBefore.OverlapWaits)
            throw new IOException("The direct paging write failed or recorded no overlap wait.");
        var actual = new byte[blockBytes];
        using (var file = new AlignedFile(path, blockBytes, create: false))
            file.Read(Offset, actual);
        if (!actual.AsSpan().SequenceEqual(b))
            throw new IOException("After the held old drain retired, the newest mapped bytes were not visible.");
        // Later cached C after direct B: RAM admission of C must become the newest view and final media.
        using (var file = new AlignedFile(path, blockBytes, create: false))
        {
            file.Write(Offset, c);
            file.Read(Offset, actual);
        }
        if (!actual.AsSpan().SequenceEqual(c))
            throw new IOException("Later cached write C was not the newest active view.");
        device.Control(WriteCacheAction.Flush);
        device.Control(WriteCacheAction.Disable);
        device.Control(WriteCacheAction.Release);
        Array.Clear(actual);
        using (var file = new AlignedFile(path, blockBytes, create: false))
            file.Read(Offset, actual);
        if (!actual.AsSpan().SequenceEqual(c))
            throw new IOException("Released-cache media did not hold later cached write C.");
        return
        [
            new("paging-coherence/gated-submitted-old-before-direct", "PASS",
                order + $" The mapped flush took {directMilliseconds:F0} ms against a {holdMs} ms hold; newest bytes B matched."),
            new("paging-coherence/later-cached-after-direct", "PASS",
                "Cached write C after direct paging write B was the newest active view and the released-cache media.")
        ];
    }

    // T082: a required page-in must complete while the request worker is blocked by a capacity wait.
    private static CheckResult RunCapacityBlockedPageIn(DiskTarget target, CacheDevice device, string workDirectory)
    {
        const int pageInBytes = 8 * MiB;
        const int writerBytes = 32 * MiB;
        var pageInPath = Path.Combine(workDirectory, "blocked-page-in.bin");
        var writerPath = Path.Combine(workDirectory, "blocked-writer.bin");
        var pattern = new byte[pageInBytes];
        new Random(104831).NextBytes(pattern);
        // Written while caching is released: neither QueueCache nor the system cache holds these pages.
        using (var file = new AlignedFile(pageInPath, MiB, create: true))
            for (var offset = 0; offset < pageInBytes; offset += MiB)
                file.Write(offset, pattern.AsSpan(offset, MiB).ToArray());
        using (var file = new FileStream(writerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            file.SetLength(writerBytes);
        ConfigurationManager.Apply(target, new CacheConfiguration(16, CachePreset.Fast)
        {
            Options = new CacheOptions(Drain: DrainAlgorithm.Eager, Parallelism: 1, RetainWrites: false, BatchKiB: 256)
        }, true);
        var writerBlock = new byte[MiB];
        new Random(104849).NextBytes(writerBlock);
        var throttleBefore = device.GetWriteCacheState().ThrottleWaits;
        CheckResult result;
        device.Control(WriteCacheAction.LabDelay, value: 200);
        Task writer = Task.CompletedTask;
        try
        {
            writer = Task.Run(() =>
            {
                using var file = new AlignedFile(writerPath, MiB, create: false);
                for (var offset = 0; offset < writerBytes; offset += MiB)
                    file.Write(offset, writerBlock);
            });
            if (!SpinWait.SpinUntil(() => writer.IsCompleted ||
                    device.GetWriteCacheState().ThrottleWaits > throttleBefore, 15000) || writer.IsCompleted)
                throw new IOException("The writer never became capacity-blocked; the dependency could not be set up.");
            var before = device.GetDiagnostics().PagingOffload;
            var matched = 0;
            var slowest = 0.0;
            using (var map = MemoryMappedFile.CreateFromFile(pageInPath, FileMode.Open, null, 0,
                       MemoryMappedFileAccess.Read))
            using (var view = map.CreateViewAccessor(0, pageInBytes, MemoryMappedFileAccess.Read))
            {
                var page = new byte[4096];
                foreach (var offset in new[] { 0, 2 * MiB + 8192, 4 * MiB + 20480, 6 * MiB + 40960 })
                {
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    view.ReadArray(offset, page, 0, page.Length);
                    slowest = Math.Max(slowest, timer.Elapsed.TotalMilliseconds);
                    if (!page.AsSpan().SequenceEqual(pattern.AsSpan(offset, page.Length)))
                        throw new IOException($"Page-in at {offset} returned wrong bytes while the writer was blocked.");
                    matched++;
                }
            }
            var stillBlocked = !writer.IsCompleted;
            result = VerifyBlockedPageIn(before, device.GetDiagnostics().PagingOffload, stillBlocked, matched, slowest);
        }
        finally
        {
            device.Control(WriteCacheAction.LabDelay, value: 0);
            try { writer.Wait(TimeSpan.FromMinutes(2)); } catch (AggregateException) { }
        }
        if (!writer.IsCompletedSuccessfully)
            throw new IOException("The capacity-blocked writer failed or did not finish after the delay was cleared.");
        device.Control(WriteCacheAction.Flush);
        device.Control(WriteCacheAction.Disable);
        device.Control(WriteCacheAction.Release);
        var actual = new byte[MiB];
        using (var file = new AlignedFile(writerPath, MiB, create: false))
            for (var offset = 0; offset < writerBytes; offset += MiB)
            {
                file.Read(offset, actual);
                if (!actual.AsSpan().SequenceEqual(writerBlock))
                    throw new IOException($"Capacity-blocked writer bytes differed at {offset} after release.");
            }
        return result;
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
        // Keep the owned block's mapped overwrite on the direct paging path (not T085 RAM admission).
        device.SetSpecialRanges([new DiskRange(DiskOffsetOf(path, Offset, target.Root), 4096)], SpecialRangeKind.ForceDirect);
        device.Control(WriteCacheAction.LabDelay, value: 2000);
        CachePagingRoute? before = null;
        CachePagingRoute after;
        ulong observedInFlight = 0;
        try
        {
            using (var file = new AlignedFile(path, 4096, create: false, alignment: 512))
            {
                file.Write(patchOffset, a);
                // A 1,536-byte in-flight remainder isolates this sparse payload
                // from ordinary 4 KiB NTFS metadata writes. A timeout is an
                // incomplete observation, never an inferred overlap pass.
                if (!SpinWait.SpinUntil(() =>
                    {
                        observedInFlight = device.GetWriteCacheState().InFlightBytes;
                        return SparseInFlight(observedInFlight);
                    }, 8000))
                    throw new IOException(
                        $"The owned sparse write did not enter an isolated in-flight interval (last in flight: {observedInFlight} bytes).");
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
            device.SetSpecialRanges([], SpecialRangeKind.ForceDirect);
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

    /// <summary>
    /// Physical disk byte offset of one file byte: the driver gate works in disk offsets, not file offsets.
    /// Refuses sparse/unallocated clusters and volumes that are not a single disk extent.
    /// </summary>
    internal static long DiskOffsetOf(string path, long fileOffset, string volumeRoot)
    {
        if (!GetDiskFreeSpaceW(volumeRoot, out var sectorsPerCluster, out var bytesPerSector, out _, out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var clusterBytes = (long)sectorsPerCluster * bytesPerSector;
        var vcn = fileOffset / clusterBytes;
        var output = new byte[64 * 1024];
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var input = BitConverter.GetBytes(vcn);
            if (!DeviceIoControl(file.SafeFileHandle, 0x00090073 /* FSCTL_GET_RETRIEVAL_POINTERS */, input, input.Length,
                    output, output.Length, out _, IntPtr.Zero) && Marshal.GetLastWin32Error() != 234 /* ERROR_MORE_DATA */)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        var extents = BitConverter.ToUInt32(output, 0);
        var currentVcn = BitConverter.ToInt64(output, 8);
        long? lcn = null;
        for (var index = 0; index < extents && 16 + index * 16 + 16 <= output.Length; index++)
        {
            var nextVcn = BitConverter.ToInt64(output, 16 + index * 16);
            var extentLcn = BitConverter.ToInt64(output, 24 + index * 16);
            if (vcn >= currentVcn && vcn < nextVcn)
            {
                if (extentLcn < 0)
                    throw new IOException("The owned file range is sparse/unallocated; no physical offset exists.");
                lcn = extentLcn + (vcn - currentVcn);
                break;
            }
            currentVcn = nextVcn;
        }
        if (lcn is null)
            throw new IOException("The owned file range has no retrieval pointer.");
        long partitionStart;
        using (var volume = new FileStream(@"\\.\" + volumeRoot.TrimEnd('\\'), FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite))
        {
            var disk = new byte[256];
            if (!DeviceIoControl(volume.SafeFileHandle, 0x00560000 /* IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS */, null, 0,
                    disk, disk.Length, out _, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if (BitConverter.ToUInt32(disk, 0) != 1)
                throw new NotSupportedException("The gated case requires a volume on exactly one disk extent.");
            partitionStart = BitConverter.ToInt64(disk, 16);
        }
        return partitionStart + lcn.Value * clusterBytes + fileOffset % clusterBytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code,
        byte[]? input, int inputLength, byte[] output, int outputLength, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceW(string root, out uint clustersPerUnit, out uint bytesPerSector,
        out uint freeClusters, out uint totalClusters);
}
