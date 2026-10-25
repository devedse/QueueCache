using System.ComponentModel;
using System.Runtime.Versioning;
using QueueCache.Developer.FileTests;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

/// <summary>Disposable NTFS checks for the experimental Fast ordering path. These are live-driver
/// ordering checks, not a power-loss qualification. Every byte belongs to a new file in the run.</summary>
[SupportedOSPlatform("windows")]
public static class FlushOrderScenario
{
    private const int Block = 4096, MiB = 1 << 20, Offset = 4 * MiB;

    public static void ValidateRamAdmission(CacheDiagnostics before, CacheDiagnostics after)
    {
        var a = before.FlushOrder ?? throw new InvalidDataException("Missing pre-write ordering evidence.");
        var b = after.FlushOrder ?? throw new InvalidDataException("Missing post-write ordering evidence.");
        var lowerA = before.Attribution ?? throw new InvalidDataException("Missing lower-I/O evidence.");
        var lowerB = after.Attribution ?? throw new InvalidDataException("Missing lower-I/O evidence.");
        if (a.Mode != 1 || b.Mode != 1 || b.Seals <= a.Seals || b.VersionCopies <= a.VersionCopies ||
            lowerA.LowerWriteAttempts != lowerB.LowerWriteAttempts || lowerA.LowerReadAttempts != lowerB.LowerReadAttempts ||
            lowerA.LowerFlushAttempts != lowerB.LowerFlushAttempts)
            throw new InvalidDataException("Sealing and overwriting did not retain history with zero lower-I/O attempts.");
    }

    public static void ValidateHeld(CacheDiagnostics held)
    {
        var order = held.FlushOrder ?? throw new InvalidDataException("Missing held ordering evidence.");
        if (order.Mode != 1 || order.DrainingEpoch >= order.CurrentEpoch ||
            order.MaxSubmittedEpoch != order.DrainingEpoch || held.LabGate is not { State: 2, Hits: 1, OldLowerDoneSeq: > 0 })
            throw new InvalidDataException("The older completed-but-held write did not fence all later groups.");
    }

    public static void ValidateFailed(CacheDiagnostics before, CacheDiagnostics after, WriteCacheState state, bool flushFailed)
    {
        var a = before.FlushOrder ?? throw new InvalidDataException("Missing pre-failure ordering evidence.");
        var b = after.FlushOrder ?? throw new InvalidDataException("Missing post-failure ordering evidence.");
        if (!flushFailed || state.LastError == 0 || state.DirtyBytes < Block || b.FlushFailures != a.FlushFailures + 1 ||
            b.DrainingEpoch != a.DrainingEpoch || b.MaxSubmittedEpoch > b.DrainingEpoch)
            throw new InvalidDataException("A failed group flush advanced the frontier, lost the later version or reported success.");
    }

    public static void ValidateCapacity(CacheDiagnostics before, CacheDiagnostics after,
        WriteCacheState stateBefore, WriteCacheState stateAfter)
    {
        var a = before.FlushOrder ?? throw new InvalidDataException("Missing pre-pressure ordering evidence.");
        var b = after.FlushOrder ?? throw new InvalidDataException("Missing post-pressure ordering evidence.");
        if (a.Mode != 1 || b.Mode != 1 || b.VersionCopies <= a.VersionCopies ||
            stateAfter.ThrottleWaits <= stateBefore.ThrottleWaits || stateAfter.LastError != 0)
            throw new InvalidDataException("Retained-version capacity pressure was not exercised or the cache faulted.");
    }

    public static async Task<IReadOnlyList<CheckResult>> RunAsync(DiskTarget target, CacheDevice device,
        string workDirectory, string evidence)
    {
        target.ValidateCurrent();
        if (target.IsBoot || target.IsSystem || target.IsPaging || device.GetStatistics().PagingPathCount != 0 ||
            !new DriveInfo(target.Root).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(Path.GetFullPath(workDirectory)), target.Root, StringComparison.OrdinalIgnoreCase) ||
            Directory.Exists(workDirectory))
            throw new IOException("Flush ordering requires a fresh owned directory on a disposable non-OS NTFS volume.");
        var original = device.GetDiagnostics();
        var mode = original.FlushOrder?.Mode ?? throw new NotSupportedException("Flush ordering requires Diagnostics V26.");
        var trimMode = original.DrainPriority?.TrimMode ?? throw new NotSupportedException("Missing TRIM mode.");
        RunStorage.AtomicJson(evidence + ".original-hooks.json", new { FlushOrderMode = mode, TrimMode = trimMode });
        Directory.CreateDirectory(workDirectory);
        var checks = new List<CheckResult>();
        try
        {
            Reset(device);
            var path = OrderingFaultScenarios.CreateOwned(workDirectory, "epochs.bin");
            // Keep this handle open so last-close metadata is outside the measured admission window.
            using (var file = new AlignedFile(path, Block, create: false, shareAll: true))
            {
                Apply(target, device);
                var before = device.GetDiagnostics();
                file.Write(Offset, Pattern(1));
                file.Flush(); // Filesystem flush, deliberately acknowledged from volatile RAM.
                file.Write(Offset, Pattern(2));
                file.Write(Offset + Block, Pattern(3));
                var admitted = device.GetDiagnostics();
                RunStorage.AtomicJson(evidence + ".admission-before.json", before);
                RunStorage.AtomicJson(evidence + ".admission-after.json", admitted);
                ValidateRamAdmission(before, admitted);
                AssertRead(file, Offset, Pattern(2));
                AssertRead(file, Offset + Block, Pattern(3));
                checks.Add(new("flush-order/ram-admission-and-versions", "PASS",
                    "Filesystem flush sealed the old version; its overwrite and adjacent write completed with zero lower read/write/flush attempts. Newest reads matched."));
                var diskOffset = PagingCoherenceScenarios.DiskOffsetOf(path, Offset, target.Root);
                device.Control(WriteCacheAction.LabGate, (ulong)diskOffset, 4000UL << 32 | Block);
                // A synchronous Windows file handle serializes its IOCTLs. Keep the
                // blocking control on another handle so diagnostics can observe the hold.
                var flush = Task.Run(() =>
                {
                    using var control = new CacheDevice(target.Device, writable: true);
                    control.Control(WriteCacheAction.Flush);
                });
                try
                {
                    var held = await WaitHeld(device, flush);
                    RunStorage.AtomicJson(evidence + ".held.json", held);
                    ValidateHeld(held);
                }
                finally
                {
                    await flush;
                    RunStorage.AtomicJson(evidence + ".held-final.json", device.GetDiagnostics());
                }
                device.Control(WriteCacheAction.LabGate, value: 0);
                var done = device.GetDiagnostics();
                RunStorage.AtomicJson(evidence + ".drained.json", done);
                if (done.FlushOrder!.Flushes <= admitted.FlushOrder!.Flushes)
                    throw new IOException("No real lower ordering flush completed.");
                checks.Add(new("flush-order/held-predecessor", "PASS",
                    "With two drainers, the highest submitted group stayed at the older held group; a real lower flush then allowed the later versions to drain."));
            }
            Reset(device);
            using (var file = new AlignedFile(path, Block, create: false, shareAll: true))
            {
                AssertRead(file, Offset, Pattern(2));
                AssertRead(file, Offset + Block, Pattern(3));
                Apply(target, device);
                file.Write(Offset, Pattern(4));
                file.Flush();
                file.Write(Offset, Pattern(5));
                var before = device.GetDiagnostics();
                device.Control(WriteCacheAction.LabFault, value: 3);
                var failed = false;
                try { device.Control(WriteCacheAction.Flush); }
                catch (Exception ex) when (ex is IOException or Win32Exception) { failed = true; }
                var after = device.GetDiagnostics();
                RunStorage.AtomicJson(evidence + ".fault-before.json", before);
                RunStorage.AtomicJson(evidence + ".fault-after.json", new { Diagnostics = after, State = device.GetWriteCacheState(), FlushFailed = failed });
                ValidateFailed(before, after, device.GetWriteCacheState(), failed);
                // This is recovery of the one deliberate fault, never an automatic retry of an unknown error.
                device.Control(WriteCacheAction.LabFault, value: 0);
                device.Control(WriteCacheAction.Retry);
                device.Control(WriteCacheAction.Flush);
            }
            Reset(device);
            using (var file = new AlignedFile(path, Block, create: false, shareAll: true)) AssertRead(file, Offset, Pattern(5));
            checks.Add(new("flush-order/failed-flush-and-retry", "PASS",
                "The deliberate lower flush failure faulted the cache, retained the newer version and stopped the frontier. Explicit Retry drained it; bytes matched after release."));

            using (var file = new AlignedFile(path, Block, create: false, shareAll: true))
            {
                Apply(target, device);
                file.Write(Offset, Pattern(8));
                var before = device.GetDiagnostics();
                UnbufferedFileWrite.WriteRange(path, Offset, Pattern(9), writeThrough: true);
                var after = device.GetDiagnostics();
                RunStorage.AtomicJson(evidence + ".write-through-before.json", before);
                RunStorage.AtomicJson(evidence + ".write-through-after.json", after);
                ValidateRamAdmission(before, after);
                if (after.FlushOrder!.Seals < before.FlushOrder!.Seals + 2)
                    throw new IOException("Write-through did not seal both its predecessor and its own write.");
                AssertRead(file, Offset, Pattern(9));
                device.Control(WriteCacheAction.Flush);
            }
            Reset(device);
            using (var file = new AlignedFile(path, Block, create: false, shareAll: true)) AssertRead(file, Offset, Pattern(9));
            checks.Add(new("flush-order/write-through-boundaries", "PASS",
                "Fast write-through sealed both sides of its write with zero lower-I/O admission attempts and a retained old version; latest bytes matched after release."));

            Apply(target, device);
            device.Control(WriteCacheAction.LabTrimMode, value: 3);
            using (var file = new AlignedFile(path, Block, create: false, shareAll: true))
            {
                file.Write(Offset, Pattern(6));
                file.Flush();
                var before = device.GetDiagnostics();
                file.Trim(Offset, Block);
                var after = device.GetDiagnostics();
                RunStorage.AtomicJson(evidence + ".trim-before.json", before);
                RunStorage.AtomicJson(evidence + ".trim-after.json", after);
                if (after.FlushOrder!.TrimWaits <= before.FlushOrder!.TrimWaits ||
                    after.FlushOrder.Flushes <= before.FlushOrder.Flushes)
                    throw new IOException("TRIM did not exercise waiting for a sealed predecessor and its lower flush.");
                file.Write(Offset, Pattern(7));
                device.Control(WriteCacheAction.Flush);
            }
            Reset(device);
            using (var file = new AlignedFile(path, Block, create: false, shareAll: true))
            {
                AssertRead(file, Offset, Pattern(7));
                AssertRead(file, Offset + Block, Pattern(3));
            }
            checks.Add(new("flush-order/trim-predecessor-and-rewrite", "PASS",
                "TRIM waited for the sealed predecessor and its lower flush; a later rewrite and its untouched neighbor matched after release. Trimmed bytes were not assumed to be zero."));
            Apply(target, device);
            device.Control(WriteCacheAction.LabTrimMode, value: 0);
            using (var file = new AlignedFile(path, Block, create: false, shareAll: true))
            {
                file.Write(Offset, Pattern(10));
                file.Flush();
                file.Trim(Offset, Block);
                file.Write(Offset, Pattern(11));
                device.Control(WriteCacheAction.Flush);
            }
            Reset(device);
            using (var file = new AlignedFile(path, Block, create: false, shareAll: true))
            {
                AssertRead(file, Offset, Pattern(11));
                AssertRead(file, Offset + Block, Pattern(3));
            }
            checks.Add(new("flush-order/synchronous-trim", "PASS",
                "The conservative synchronous TRIM path reconciled the sealed predecessor; a later rewrite and untouched neighbor matched after release."));
            Reset(device);
            var pressurePath = OrderingFaultScenarios.CreateOwned(workDirectory, "capacity.bin");
            using (var file = new AlignedFile(pressurePath, 64 * 1024, create: false, shareAll: true))
            {
                Apply(target, device, budgetMiB: 8);
                var before = device.GetDiagnostics();
                var stateBefore = device.GetWriteCacheState();
                var first = ConcurrentCacheOracle.Pattern(214, 1, 64 * 1024);
                var latest = ConcurrentCacheOracle.Pattern(214, 2, 64 * 1024);
                for (var offset = 0; offset < 4 * MiB; offset += first.Length) file.Write(offset, first);
                file.Flush();
                for (var offset = 0; offset < 4 * MiB; offset += latest.Length) file.Write(offset, latest);
                var after = device.GetDiagnostics();
                var stateAfter = device.GetWriteCacheState();
                RunStorage.AtomicJson(evidence + ".capacity.json", new { Before = before, After = after, StateBefore = stateBefore, StateAfter = stateAfter });
                ValidateCapacity(before, after, stateBefore, stateAfter);
                for (var offset = 0; offset < 4 * MiB; offset += latest.Length) AssertRead(file, offset, latest);

                var rejected = false;
                try { device.Control(WriteCacheAction.LabFlushOrder, value: 0); }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 170 /* ERROR_BUSY */) { rejected = true; }
                if (!rejected || device.GetDiagnostics().FlushOrder?.Mode != 1 || !device.GetWriteCacheState().Enabled)
                    throw new IOException("A live ordering-mode change was accepted or disturbed the cache.");
                checks.Add(new("flush-order/live-mode-change-refused", "PASS",
                    "A mode change while enabled was refused with ERROR_BUSY; the ordering mode and enabled state stayed intact."));
                Reset(device); // Disable drains and flushes sealed versions before Release frees the budget.
                var released = device.GetWriteCacheState();
                if (released.Enabled || released.BudgetBytes != 0 || released.DirtyBytes != 0 || released.LastError != 0 ||
                    device.GetDiagnostics().FlushOrder?.Mode != 1)
                    throw new IOException("Disable/Release lost the ordering state or left pending resources.");
                for (var offset = 0; offset < 4 * MiB; offset += latest.Length) AssertRead(file, offset, latest);
                AssertRead(file, 4 * MiB, new byte[64 * 1024]);
                checks.Add(new("flush-order/retained-version-capacity", "PASS",
                    "An 8 MiB budget held two 4 MiB generations until pressure forced progress; capacity waits and retained versions increased. Latest bytes and untouched neighbor matched after release."));

                Apply(target, device, budgetMiB: 8);
                file.Write(0, first);
                file.Flush();
                file.Write(0, latest);
                Reset(device);
                AssertRead(file, 0, latest);
                checks.Add(new("flush-order/disable-release-reapply", "PASS",
                    "Disable drained sealed work, Release returned the allocation, and a new allocation preserved ordering and the newest on-disk version."));
            }
        }
        finally
        {
            device.Control(WriteCacheAction.LabGate, value: 0);
            device.Control(WriteCacheAction.LabFault, value: 0);
            device.Control(WriteCacheAction.LabTrimMode, value: trimMode);
            Reset(device); // A real unexpected fault is preserved and fails restoration.
            device.Control(WriteCacheAction.LabFlushOrder, value: mode);
            if (device.GetDiagnostics().FlushOrder?.Mode != mode)
                throw new IOException("Original flush-order mode was not restored.");
        }
        return checks;
    }

    private static void Reset(CacheDevice device)
    {
        device.Control(WriteCacheAction.Disable);
        device.Control(WriteCacheAction.Release);
    }
    private static void Apply(DiskTarget target, CacheDevice device, int budgetMiB = 64)
    {
        device.Control(WriteCacheAction.LabFlushOrder, value: 1);
        ConfigurationManager.Apply(target, new CacheConfiguration(budgetMiB, CachePreset.Fast)
        {
            Options = new CacheOptions(Drain: DrainAlgorithm.Deferred, MaxDirtyAgeMs: 3600000,
                BatchKiB: 4, Parallelism: 2, RetainWrites: false)
        }, true);
    }
    private static async Task<CacheDiagnostics> WaitHeld(CacheDevice device, Task flush)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            var observed = device.GetDiagnostics();
            if (observed.LabGate is { State: 2, OldLowerDoneSeq: > 0 }) return observed;
            if (flush.IsCompleted) { await flush; break; }
            await Task.Delay(10);
        }
        // Always join our owned operation before returning failure to restoration.
        await flush;
        throw new IOException("The owned predecessor did not reach the lower-completion hold.");
    }
    private static byte[] Pattern(int epoch) => ConcurrentCacheOracle.Pattern(213, epoch, Block);
    private static void AssertRead(AlignedFile file, long offset, byte[] expected)
    {
        var actual = new byte[expected.Length];
        file.Read(offset, actual);
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new IOException($"Flush ordering byte oracle mismatch at owned offset {offset}.");
    }
}
