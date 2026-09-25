using System.ComponentModel;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using QueueCache.Management;

namespace QueueCache.Operations;

/// <summary>
/// T083 failure/cancellation orders on a validated disposable non-OS volume: a failed old drain while a
/// newer paging write waits, a short last sparse segment, and cancellation of a capacity-blocked write.
/// Each stage restores a healthy cache (Retry) itself; faults are driver lab hooks, never raw disk writes.
/// </summary>
[SupportedOSPlatform("windows")]
public static class OrderingFaultScenarios
{
    private const int MiB = 1 << 20;
    private const int FileBytes = 8 * MiB;
    private const int Offset = 4 * MiB;
    private const int BlockBytes = 4096;
    private const ulong GateFail = 1, GateShort = 2;

    /// <summary>A failed old drain must stop the newer overlapping paging write before submission.</summary>
    internal static string VerifyFailedOldDrain(CacheLabGate? gate, bool directFailed, bool faulted, ulong dirtyBytes)
    {
        if (gate is null)
            throw new NotSupportedException("Failure ordering requires Diagnostics V9.");
        if (gate.Hits != 1 || gate.OldSubmitSeq == 0 || gate.OldLowerDoneSeq <= gate.OldSubmitSeq)
            throw new IOException("The owned old drain was not submitted and completed at the lower device under the gate.");
        if (gate.DirectWaitSeq == 0)
            throw new IOException("The newer paging write never waited for the held old drain.");
        if (gate.DirectSubmitSeq != 0)
            throw new IOException($"The newer paging write was submitted (sequence {gate.DirectSubmitSeq}) although the older drain failed.");
        if (!directFailed)
            throw new IOException("The newer paging write reported success although it was never written: false success.");
        if (!faulted || dirtyBytes < BlockBytes)
            throw new IOException($"Failure did not fault the cache or lost the acknowledged dirty version (dirty {dirtyBytes}).");
        return $"Old drain submitted ({gate.OldSubmitSeq}) and failed after lower completion ({gate.OldLowerDoneSeq}); " +
            $"the newer paging write waited ({gate.DirectWaitSeq}), was never submitted and reported failure; " +
            $"the cache faulted with {dirtyBytes} dirty bytes retained.";
    }

    /// <summary>A short last sparse segment must keep the whole version dirty and fault the cache.</summary>
    internal static string VerifyShortSparse(CacheLabGate? gate, bool flushFailed, bool faulted, ulong dirtyBytes,
        ulong sparseBytes)
    {
        if (gate is null)
            throw new NotSupportedException("Sparse failure requires Diagnostics V9.");
        if (gate.Hits != 1 || gate.OldSubmitSeq == 0 || gate.OldLowerDoneSeq <= gate.OldSubmitSeq)
            throw new IOException("The owned sparse version was not drained under the gate.");
        if (!flushFailed || !faulted)
            throw new IOException("A short sparse completion did not fail the flush and fault the cache: false success.");
        // Other dirty data (e.g. NTFS log writes) may coexist; the owned version must not be dropped.
        if (dirtyBytes < sparseBytes)
            throw new IOException($"Expected the whole {sparseBytes}-byte sparse version to remain dirty, found {dirtyBytes}.");
        return $"The last sparse run completed short; the flush failed, the cache faulted and {dirtyBytes} dirty bytes " +
            $"(including the whole {sparseBytes}-byte sparse version) stayed dirty for ordered retry. A short report " +
            "still wrote the data, so this retention count, not the final bytes, is the proof.";
    }

    public static IReadOnlyList<CheckResult> Run(DiskTarget target, CacheDevice device, string workDirectory)
    {
        target.ValidateCurrent();
        if (target.IsBoot || target.IsSystem || target.IsPaging ||
            !string.Equals(Path.GetDirectoryName(Path.GetFullPath(workDirectory)), target.Root,
                StringComparison.OrdinalIgnoreCase) || Directory.Exists(workDirectory))
            throw new IOException("Fault ordering requires a fresh owned directory on a non-OS disk.");
        if (device.GetDiagnostics().LabGate is null)
            throw new NotSupportedException("Fault ordering requires Diagnostics V9 (plan-39+ driver).");
        Directory.CreateDirectory(workDirectory);
        return
        [
            RunFailedOldDrain(target, device, workDirectory),
            RunShortSparse(target, device, workDirectory),
            RunCancelledBlockedWrite(target, device, workDirectory)
        ];
    }

    private static void ApplyEager(DiskTarget target, int budgetMiB, DrainAlgorithm drain = DrainAlgorithm.Eager) =>
        ConfigurationManager.Apply(target, new CacheConfiguration(budgetMiB, CachePreset.Fast)
        {
            Options = new CacheOptions(Drain: drain, MaxDirtyAgeMs: 3600000, Parallelism: 1, RetainWrites: false)
        }, true);

    internal static string CreateOwned(string workDirectory, string name)
    {
        // Write the whole file once so its valid data length covers every owned range. Otherwise NTFS
        // zero-fills the gap before a later write with a paging write, and the paging fence then
        // (correctly) drains the owned version early, defeating the arranged order. Since T085 that
        // zero-fill is also admitted and drained ahead of the owned write. Used by paging-coherence too.
        var path = Path.Combine(workDirectory, name);
        var zeros = new byte[MiB];
        using (var file = new AlignedFile(path, MiB, create: true))
        {
            for (var offset = 0; offset < FileBytes; offset += MiB)
                file.Write(offset, zeros);
            file.Flush();
        }
        return path;
    }

    private static long GateOffset(string path, DiskTarget target)
    {
        var disk = PagingCoherenceScenarios.DiskOffsetOf(path, Offset, target.Root);
        return disk % BlockBytes == 0 ? disk :
            throw new NotSupportedException("The owned block is not 4 KiB aligned on disk.");
    }

    // Clear the fault through the maintained Retry barrier and prove the cache is clean again.
    private static void Recover(CacheDevice device, string label)
    {
        device.Control(WriteCacheAction.LabGate, value: 0);
        device.SetSpecialRanges([], SpecialRangeKind.ForceDirect);
        device.Control(WriteCacheAction.Retry);
        var state = device.GetWriteCacheState();
        if (state.LastError != 0 || state.DirtyBytes != 0 || state.InFlightBytes != 0)
            throw new IOException($"{label}: Retry did not restore a clean healthy cache.");
    }

    private static void ReleaseAndRead(CacheDevice device, string path, long offset, byte[] actual)
    {
        device.Control(WriteCacheAction.Flush);
        device.Control(WriteCacheAction.Disable);
        device.Control(WriteCacheAction.Release);
        using var file = new AlignedFile(path, actual.Length, create: false, alignment: 512);
        file.Read(offset, actual);
    }

    private static CheckResult RunFailedOldDrain(DiskTarget target, CacheDevice device, string workDirectory)
    {
        const string label = "ordering-faults/failed-old-drain-blocks-direct";
        var path = CreateOwned(workDirectory, "failed-old-drain.bin");
        ApplyEager(target, 64);
        var a = new byte[BlockBytes];
        var b = new byte[BlockBytes];
        new Random(104851).NextBytes(a);
        new Random(104857).NextBytes(b);
        var gateOffset = GateOffset(path, target);
        var directFailed = false;
        string evidence;
        // The waiting newer write must be a direct paging write (not T085 RAM admission).
        device.SetSpecialRanges([new DiskRange(gateOffset, BlockBytes)], SpecialRangeKind.ForceDirect);
        device.Control(WriteCacheAction.LabGate, (ulong)gateOffset, GateFail << 48 | 500UL << 32 | BlockBytes);
        try
        {
            using (var file = new AlignedFile(path, BlockBytes, create: false))
                file.Write(Offset, a);
            CacheLabGate? held = null;
            if (!SpinWait.SpinUntil(() =>
                {
                    held = device.GetDiagnostics().LabGate;
                    return held is { State: 2, OldLowerDoneSeq: > 0 };
                }, 8000))
                throw new IOException($"{label}: the old drain was not held after lower completion (state {held?.State}).");
            try
            {
                using var map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite);
                using var view = map.CreateViewAccessor(0, FileBytes, MemoryMappedFileAccess.ReadWrite);
                view.WriteArray(Offset, b, 0, b.Length);
                view.Flush();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
            {
                directFailed = true;
            }
            var state = device.GetWriteCacheState();
            evidence = VerifyFailedOldDrain(device.GetDiagnostics().LabGate, directFailed, state.LastError != 0,
                state.DirtyBytes);
        }
        finally
        {
            Recover(device, label);
        }
        // After recovery the retained old version was drained; nothing but A or B may be on media.
        var actual = new byte[BlockBytes];
        using (var file = new AlignedFile(path, BlockBytes, create: false))
            file.Read(Offset, actual);
        if (!actual.AsSpan().SequenceEqual(a) && !actual.AsSpan().SequenceEqual(b))
            throw new IOException($"{label}: after Retry the block held neither the old nor the newer version.");
        // A new explicit mapped save now succeeds and is final.
        using (var map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite))
        using (var view = map.CreateViewAccessor(0, FileBytes, MemoryMappedFileAccess.ReadWrite))
        {
            view.WriteArray(Offset, b, 0, b.Length);
            view.Flush();
        }
        ReleaseAndRead(device, path, Offset, actual);
        if (!actual.AsSpan().SequenceEqual(b))
            throw new IOException($"{label}: the repeated mapped save was not the released-cache media.");
        return new(label, "PASS", evidence + " Retry drained the retained version; a repeated mapped save succeeded " +
            "and matched after release.");
    }

    private static CheckResult RunShortSparse(DiskTarget target, CacheDevice device, string workDirectory)
    {
        const string label = "ordering-faults/sparse-last-segment-short-retains-version";
        var path = CreateOwned(workDirectory, "short-sparse.bin");
        // Deferred keeps both sparse runs in one version until the explicit flush drains it.
        ApplyEager(target, 64, DrainAlgorithm.Deferred);
        var first = new byte[1024];
        var second = new byte[1024];
        new Random(104869).NextBytes(first);
        new Random(104879).NextBytes(second);
        var gateOffset = GateOffset(path, target);
        var flushFailed = false;
        string evidence;
        using (var file = new AlignedFile(path, 1024 * 4, create: false, alignment: 512))
        {
            file.Write(Offset + 512, first);   // sectors 1-2
            file.Write(Offset + 2560, second); // sectors 5-6: a separate, last run
        }
        var admitted = device.GetWriteCacheState();
        if (admitted.DirtyBytes < 2048)
            throw new IOException($"{label}: the 2048-byte sparse version was not admitted ({admitted.DirtyBytes} dirty bytes).");
        device.Control(WriteCacheAction.LabGate, (ulong)gateOffset, GateShort << 48 | 50UL << 32 | BlockBytes);
        try
        {
            try
            {
                device.Control(WriteCacheAction.Flush);
            }
            catch (Win32Exception)
            {
                flushFailed = true;
            }
            var state = device.GetWriteCacheState();
            evidence = VerifyShortSparse(device.GetDiagnostics().LabGate, flushFailed, state.LastError != 0,
                state.DirtyBytes, 2048);
        }
        finally
        {
            Recover(device, label);
        }
        var expected = new byte[BlockBytes];
        first.CopyTo(expected, 512);
        second.CopyTo(expected, 2560);
        var actual = new byte[BlockBytes];
        ReleaseAndRead(device, path, Offset, actual);
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new IOException($"{label}: after Retry the sparse runs or untouched guard sectors were wrong.");
        return new(label, "PASS", evidence + " Retry rewrote the whole version; both runs and all four untouched " +
            "guard sectors matched after release.");
    }

    private static CheckResult RunCancelledBlockedWrite(DiskTarget target, CacheDevice device, string workDirectory)
    {
        const string label = "ordering-faults/cancel-capacity-blocked-write";
        const int writerBytes = 32 * MiB;
        var path = Path.Combine(workDirectory, "cancelled-writer.bin");
        var baseline = new byte[MiB];
        var update = new byte[MiB];
        new Random(104891).NextBytes(baseline);
        new Random(104903).NextBytes(update);
        // Baseline written while caching is released: valid data length covers the whole file on media.
        using (var file = new AlignedFile(path, MiB, create: true))
            for (var offset = 0; offset < writerBytes; offset += MiB)
                file.Write(offset, baseline);
        ApplyEager(target, 16);
        var throttleBefore = device.GetWriteCacheState().ThrottleWaits;
        var completedBlocks = 0;
        Exception? writerError = null;
        device.Control(WriteCacheAction.LabDelay, value: 200);
        var cancelled = false;
        try
        {
            using var writerFile = new AlignedFile(path, MiB, create: false);
            var writer = Task.Run(() =>
            {
                try
                {
                    for (var offset = 0; offset < writerBytes; offset += MiB)
                    {
                        writerFile.Write(offset, update);
                        Interlocked.Increment(ref completedBlocks);
                    }
                }
                catch (Exception exception)
                {
                    writerError = exception;
                }
            });
            if (!SpinWait.SpinUntil(() => writer.IsCompleted ||
                    device.GetWriteCacheState().ThrottleWaits > throttleBefore, 15000) || writer.IsCompleted)
                throw new IOException($"{label}: the writer never became capacity-blocked.");
            Thread.Sleep(300); // Let the blocked request settle in the capacity wait.
            cancelled = writerFile.CancelPending();
            if (!writer.Wait(TimeSpan.FromSeconds(10)))
                throw new IOException($"{label}: the cancelled capacity-blocked write did not complete within 10 s.");
        }
        finally
        {
            device.Control(WriteCacheAction.LabDelay, value: 0);
        }
        var state = device.GetWriteCacheState();
        if (state.LastError != 0)
            throw new IOException($"{label}: cancellation faulted the cache (0x{state.LastError:X8}).");
        var cancelledBlock = Volatile.Read(ref completedBlocks);
        var aborted = writerError is Win32Exception { NativeErrorCode: 995 };
        if (writerError is not null && !aborted)
            throw new IOException($"{label}: the writer failed with an unexpected error.", writerError);
        var actual = new byte[MiB];
        ReleaseAndRead(device, path, 0, actual);
        using (var file = new AlignedFile(path, MiB, create: false))
            for (var block = 0; block < writerBytes / MiB; block++)
            {
                file.Read((long)block * MiB, actual);
                var expected = block < cancelledBlock ? update : baseline;
                if (!actual.AsSpan().SequenceEqual(expected))
                    throw new IOException($"{label}: block {block} held the wrong version after release " +
                        $"(completed {cancelledBlock}, aborted {aborted}).");
            }
        var detail = $"{cancelledBlock} acknowledged 1 MiB write(s) persisted; ";
        if (!cancelled || !aborted)
            return new(label, "SKIP", detail + "the blocked write completed before cancellation reached it " +
                $"(cancel issued {cancelled}); cancellation of a capacity-blocked request is unproven.");
        return new(label, "PASS", detail + $"the capacity-blocked write {cancelledBlock} was cancelled " +
            "(ERROR_OPERATION_ABORTED), was not admitted (its range and all later blocks kept the baseline after release), " +
            "and the cache stayed healthy.");
    }
}
