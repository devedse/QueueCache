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

    public static void ValidateRamAdmission(CacheDiagnostics before, CacheDiagnostics after, bool requireVersion = true)
    {
        var a = before.FlushOrder ?? throw new InvalidDataException("Missing pre-write ordering evidence.");
        var b = after.FlushOrder ?? throw new InvalidDataException("Missing post-write ordering evidence.");
        var lowerA = before.Attribution ?? throw new InvalidDataException("Missing lower-I/O evidence.");
        var lowerB = after.Attribution ?? throw new InvalidDataException("Missing lower-I/O evidence.");
        if (a.Mode is not (1 or 2 or 3) || b.Mode != a.Mode || b.Seals <= a.Seals ||
            (requireVersion ? b.VersionCopies <= a.VersionCopies : b.VersionCopies != a.VersionCopies) ||
            lowerA.LowerWriteAttempts != lowerB.LowerWriteAttempts || lowerA.LowerReadAttempts != lowerB.LowerReadAttempts ||
            lowerA.LowerFlushAttempts != lowerB.LowerFlushAttempts)
            throw new InvalidDataException("Sealing and overwriting did not retain history with zero lower-I/O attempts.");
    }

    public static void ValidateWriteThrough(CacheDiagnostics before, CacheDiagnostics after, bool sealedPredecessor)
    {
        var a = before.FlushOrder ?? throw new InvalidDataException("Missing pre-write-through ordering evidence.");
        var b = after.FlushOrder ?? throw new InvalidDataException("Missing post-write-through ordering evidence.");
        ValidateRamAdmission(before, after, requireVersion: sealedPredecessor || a.Mode == 1);
        var seals = a.Mode == 1 && !sealedPredecessor ? 2UL : 1UL;
        if (after.WriteThroughWrites != before.WriteThroughWrites + 1 || b.Seals != a.Seals + seals ||
            b.CurrentEpoch != a.CurrentEpoch + seals)
            throw new InvalidDataException("The isolated write-through did not preserve its required boundary and selected predecessor policy.");
    }

    public static void ValidateHeld(CacheDiagnostics held)
    {
        var order = held.FlushOrder ?? throw new InvalidDataException("Missing held ordering evidence.");
        if (order.Mode is not (1 or 2 or 3) || order.DrainingEpoch >= order.CurrentEpoch ||
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
        if (a.Mode is not (1 or 2 or 3) || b.Mode != a.Mode || b.VersionCopies <= a.VersionCopies ||
            stateAfter.ThrottleWaits <= stateBefore.ThrottleWaits || stateAfter.LastError != 0)
            throw new InvalidDataException("Retained-version capacity pressure was not exercised or the cache faulted.");
    }

    public static void ValidateOversizedFallback(CacheDiagnostics before, CacheDiagnostics after, WriteCacheState state)
    {
        var a = before.Attribution ?? throw new InvalidDataException("Missing direct-fallback attribution.");
        var b = after.Attribution ?? throw new InvalidDataException("Missing direct-fallback attribution.");
        if (before.FlushOrder?.Mode is not (1 or 2 or 3) || after.FlushOrder?.Mode != before.FlushOrder?.Mode ||
            b.QuotaWriteBarriers <= a.QuotaWriteBarriers || b.LowerWriteAttempts <= a.LowerWriteAttempts ||
            b.LowerFlushAttempts < a.LowerFlushAttempts + 2 || state.LastError != 0)
            throw new InvalidDataException("The oversized write did not exercise its ordered direct fallback and lower durability boundaries.");
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
            foreach (var testMode in new ulong[] { 1, 2, 3 })
            {
                var firstCheck = checks.Count;
                var modeEvidence = evidence + $".mode{testMode}";
                Reset(device);
                var path = OrderingFaultScenarios.CreateOwned(workDirectory, $"mode{testMode}-epochs.bin");
                // Keep this handle open so last-close metadata is outside the measured admission window.
                using (var file = new AlignedFile(path, Block, create: false, shareAll: true))
                {
                    Apply(target, device, testMode);
                    var before = device.GetDiagnostics();
                    file.Write(Offset, Pattern(1));
                    file.Flush(); // Filesystem flush, deliberately acknowledged from volatile RAM.
                    file.Write(Offset, Pattern(2));
                    file.Write(Offset + Block, Pattern(3));
                    var admitted = device.GetDiagnostics();
                    RunStorage.AtomicJson(modeEvidence + ".admission-before.json", before);
                    RunStorage.AtomicJson(modeEvidence + ".admission-after.json", admitted);
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
                        RunStorage.AtomicJson(modeEvidence + ".held.json", held);
                        ValidateHeld(held);
                    }
                    finally
                    {
                        await flush;
                        RunStorage.AtomicJson(modeEvidence + ".held-final.json", device.GetDiagnostics());
                    }
                    device.Control(WriteCacheAction.LabGate, value: 0);
                    var done = device.GetDiagnostics();
                    RunStorage.AtomicJson(modeEvidence + ".drained.json", done);
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
                    Apply(target, device, testMode);
                    file.Write(Offset, Pattern(4));
                    file.Flush();
                    file.Write(Offset, Pattern(5));
                    var before = device.GetDiagnostics();
                    device.Control(WriteCacheAction.LabFault, value: 3);
                    var failed = false;
                    try { device.Control(WriteCacheAction.Flush); }
                    catch (Exception ex) when (ex is IOException or Win32Exception) { failed = true; }
                    var after = device.GetDiagnostics();
                    RunStorage.AtomicJson(modeEvidence + ".fault-before.json", before);
                    RunStorage.AtomicJson(modeEvidence + ".fault-after.json", new { Diagnostics = after, State = device.GetWriteCacheState(), FlushFailed = failed });
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
                using (var writeThrough = new AlignedFile(path, Block, create: false, shareAll: true, writeThrough: true))
                {
                    Apply(target, device, testMode);
                    file.Write(Offset, Pattern(8));
                    var before = device.GetDiagnostics();
                    writeThrough.Write(Offset, Pattern(9));
                    var after = device.GetDiagnostics();
                    RunStorage.AtomicJson(modeEvidence + ".write-through-before.json", before);
                    RunStorage.AtomicJson(modeEvidence + ".write-through-after.json", after);
                    ValidateWriteThrough(before, after, sealedPredecessor: false);
                    AssertRead(file, Offset, Pattern(9));
                    device.Control(WriteCacheAction.Flush);
                }
                Reset(device);
                using (var file = new AlignedFile(path, Block, create: false, shareAll: true)) AssertRead(file, Offset, Pattern(9));
                checks.Add(new("flush-order/write-through-boundaries", "PASS",
                    "Write-through retained its required post-boundary with zero lower-I/O admission attempts; the original mode also sealed its predecessor. Latest bytes matched after release."));

                await CheckWriteThroughDependency(target, device, path, modeEvidence, testMode, fail: false);
                checks.Add(new("flush-order/write-through-held-successor", "PASS",
                    "A write-through overwrite retained an explicitly sealed predecessor; while its lower completion was held, no dependent later group submitted. Bytes matched after release."));
                await CheckWriteThroughDependency(target, device, path, modeEvidence, testMode, fail: true);
                checks.Add(new("flush-order/write-through-failed-boundary", "PASS",
                    "A failed post-write-through flush retained its later overwrite and stopped advancement; explicit Retry recovered the deliberate fault and bytes matched after release."));

                Apply(target, device, testMode);
                device.Control(WriteCacheAction.LabTrimMode, value: 3);
                using (var file = new AlignedFile(path, Block, create: false, shareAll: true))
                {
                    file.Write(Offset, Pattern(6));
                    file.Flush();
                    var before = device.GetDiagnostics();
                    file.Trim(Offset, Block);
                    var after = device.GetDiagnostics();
                    RunStorage.AtomicJson(modeEvidence + ".trim-before.json", before);
                    RunStorage.AtomicJson(modeEvidence + ".trim-after.json", after);
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
                Apply(target, device, testMode);
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
                var pressurePath = OrderingFaultScenarios.CreateOwned(workDirectory, $"mode{testMode}-capacity.bin");
                using (var file = new AlignedFile(pressurePath, 64 * 1024, create: false, shareAll: true))
                {
                    Apply(target, device, testMode, budgetMiB: 8);
                    var before = device.GetDiagnostics();
                    var stateBefore = device.GetWriteCacheState();
                    var first = ConcurrentCacheOracle.Pattern(214, 1, 64 * 1024);
                    var latest = ConcurrentCacheOracle.Pattern(214, 2, 64 * 1024);
                    for (var offset = 0; offset < 4 * MiB; offset += first.Length) file.Write(offset, first);
                    file.Flush();
                    for (var offset = 0; offset < 4 * MiB; offset += latest.Length) file.Write(offset, latest);
                    var after = device.GetDiagnostics();
                    var stateAfter = device.GetWriteCacheState();
                    RunStorage.AtomicJson(modeEvidence + ".capacity.json", new { Before = before, After = after, StateBefore = stateBefore, StateAfter = stateAfter });
                    ValidateCapacity(before, after, stateBefore, stateAfter);
                    for (var offset = 0; offset < 4 * MiB; offset += latest.Length) AssertRead(file, offset, latest);

                    var rejected = false;
                    try { device.Control(WriteCacheAction.LabFlushOrder, value: 0); }
                    catch (Win32Exception ex) when (ex.NativeErrorCode == 170 /* ERROR_BUSY */) { rejected = true; }
                    if (!rejected || device.GetDiagnostics().FlushOrder?.Mode != testMode || !device.GetWriteCacheState().Enabled)
                        throw new IOException("A live ordering-mode change was accepted or disturbed the cache.");
                    checks.Add(new("flush-order/live-mode-change-refused", "PASS",
                        "A mode change while enabled was refused with ERROR_BUSY; the ordering mode and enabled state stayed intact."));
                    Reset(device); // Disable drains and flushes sealed versions before Release frees the budget.
                    var released = device.GetWriteCacheState();
                    if (released.Enabled || released.BudgetBytes != 0 || released.DirtyBytes != 0 || released.LastError != 0 ||
                        device.GetDiagnostics().FlushOrder?.Mode != testMode)
                        throw new IOException("Disable/Release lost the ordering state or left pending resources.");
                    for (var offset = 0; offset < 4 * MiB; offset += latest.Length) AssertRead(file, offset, latest);
                    AssertRead(file, 4 * MiB, new byte[64 * 1024]);
                    checks.Add(new("flush-order/retained-version-capacity", "PASS",
                        "An 8 MiB budget held two 4 MiB generations until pressure forced progress; capacity waits and retained versions increased. Latest bytes and untouched neighbor matched after release."));

                    Apply(target, device, testMode, budgetMiB: 8);
                    file.Write(0, first);
                    file.Flush();
                    file.Write(0, latest);
                    Reset(device);
                    AssertRead(file, 0, latest);
                    checks.Add(new("flush-order/disable-release-reapply", "PASS",
                        "Disable drained sealed work, Release returned the allocation, and a new allocation preserved ordering and the newest on-disk version."));
                }
                checks.Add(CheckOversizedFallback(target, device, workDirectory, modeEvidence, testMode));
                checks.Add(CheckMapFallback(target, device, workDirectory, modeEvidence, testMode));
                for (var i = firstCheck; i < checks.Count; i++)
                    checks[i] = checks[i] with { Name = $"flush-order/mode{testMode}/" + checks[i].Name["flush-order/".Length..] };
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
        if (checks.Count != 26 || checks.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != 26 ||
            checks.Any(c => c.Result != "PASS"))
            throw new InvalidDataException("The two-mode ordering dependency checks are incomplete or duplicated.");
        return checks;
    }

    private static async Task CheckWriteThroughDependency(DiskTarget target, CacheDevice device,
        string path, string evidence, ulong mode, bool fail)
    {
        Reset(device);
        using var file = new AlignedFile(path, Block, create: false, shareAll: true);
        using var writeThrough = new AlignedFile(path, Block, create: false, shareAll: true, writeThrough: true);
        Apply(target, device, mode);
        file.Write(Offset, Pattern(12));
        file.Flush();
        var before = device.GetDiagnostics();
        writeThrough.Write(Offset, Pattern(13));
        var after = device.GetDiagnostics();
        var suffix = fail ? ".write-through-failure" : ".write-through-held";
        RunStorage.AtomicJson(evidence + suffix + ".sealed.json", new { Before = before, After = after });
        ValidateWriteThrough(before, after, sealedPredecessor: true);
        Reset(device);
        AssertRead(file, Offset, Pattern(13));

        // A fresh allocation isolates the post-write-through dependency: the
        // held write is W itself, rather than an older explicitly sealed version.
        Apply(target, device, mode);
        writeThrough.Write(Offset, Pattern(14));
        file.Write(Offset, Pattern(15));
        if (fail)
        {
            before = device.GetDiagnostics();
            device.Control(WriteCacheAction.LabFault, value: 3);
            var failed = false;
            try { device.Control(WriteCacheAction.Flush); }
            catch (Exception ex) when (ex is IOException or Win32Exception) { failed = true; }
            after = device.GetDiagnostics();
            RunStorage.AtomicJson(evidence + suffix + ".fault.json", new { Before = before, After = after, State = device.GetWriteCacheState(), Failed = failed });
            ValidateFailed(before, after, device.GetWriteCacheState(), failed);
            device.Control(WriteCacheAction.LabFault, value: 0);
            device.Control(WriteCacheAction.Retry); // Only the deliberate, observed fault is retried.
            device.Control(WriteCacheAction.Flush);
        }
        else
        {
            var diskOffset = PagingCoherenceScenarios.DiskOffsetOf(path, Offset, target.Root);
            device.Control(WriteCacheAction.LabGate, (ulong)diskOffset, 4000UL << 32 | Block);
            var flush = Task.Run(() =>
            {
                using var control = new CacheDevice(target.Device, writable: true);
                control.Control(WriteCacheAction.Flush);
            });
            try
            {
                var held = await WaitHeld(device, flush);
                RunStorage.AtomicJson(evidence + suffix + ".held.json", held);
                ValidateHeld(held);
            }
            finally { await flush; }
            device.Control(WriteCacheAction.LabGate, value: 0);
        }
        Reset(device);
        AssertRead(file, Offset, Pattern(15));
    }

    private static CheckResult CheckOversizedFallback(DiskTarget target, CacheDevice device,
        string work, string evidence, ulong mode)
    {
        Reset(device);
        var path = OrderingFaultScenarios.CreateOwned(work, $"mode{mode}-oversized.bin");
        using var file = new AlignedFile(path, 2 * MiB, create: false, shareAll: true);
        Apply(target, device, mode, budgetMiB: 1);
        if (device.GetWriteCacheState().PayloadCapacity >= 2 * MiB)
            throw new IOException("Oversized fallback requires a request larger than the cache payload.");
        file.Write(Offset, Pattern(31)); file.Flush();
        var before = device.GetDiagnostics();
        var data = new byte[2 * MiB]; new Random(217 + (int)mode).NextBytes(data);
        file.Write(Offset, data);
        var after = device.GetDiagnostics(); var state = device.GetWriteCacheState();
        RunStorage.AtomicJson(evidence + ".oversized.json", new { Before = before, After = after, State = state });
        ValidateOversizedFallback(before, after, state);
        Reset(device);
        AssertRead(file, Offset, data); AssertRead(file, Offset + data.Length, new byte[Block]);
        return new("flush-order/oversized-direct-write", "PASS",
            "A 2 MiB write over a sealed predecessor exceeded a 1 MiB budget. Quota/direct and lower flush counters advanced; newest bytes and untouched neighbor matched after release.");
    }

    private static CheckResult CheckMapFallback(DiskTarget target, CacheDevice device,
        string work, string evidence, ulong mode)
    {
        Reset(device);
        var path = OrderingFaultScenarios.CreateOwned(work, $"mode{mode}-map-fallback.bin");
        using var file = new AlignedFile(path, Block, create: false, shareAll: true);
        Apply(target, device, mode);
        file.Write(Offset, Pattern(41)); file.Flush();
        var before = device.GetDiagnostics(); var originalErrors = device.GetWriteCacheState().Errors;
        device.Control(WriteCacheAction.LabFault, value: 11);
        try { OrderingFaultScenarios.MappedSave(path, Pattern(42), shareAll: true); }
        finally { device.Control(WriteCacheAction.LabFault, value: 0); }
        var after = device.GetDiagnostics(); var state = device.GetWriteCacheState();
        RunStorage.AtomicJson(evidence + ".map-fallback.json", new { Before = before, After = after, State = state });
        var detail = OrderingFaultScenarios.VerifyPagingMapFallback(true, state.LastError,
            before.PagingProgress?.MapFailures, after.PagingProgress?.MapFailures,
            before.LowerSources?.PagingForwardedWrites, after.LowerSources?.PagingForwardedWrites);
        if (after.FlushOrder?.Mode != mode || after.FlushOrder!.Flushes <= before.FlushOrder!.Flushes ||
            state.Errors != originalErrors)
            throw new IOException("Map fallback lost its ordering mode, skipped the predecessor flush or faulted.");
        Reset(device); // Unexpected faults are preserved; this path never retries them.
        AssertRead(file, Offset, Pattern(42)); AssertRead(file, Offset + Block, new byte[Block]);
        return new("flush-order/unmappable-direct-write", "PASS", detail +
            " The sealed predecessor flushed; latest bytes and untouched neighbor matched after release.");
    }

    private static void Reset(CacheDevice device)
    {
        device.Control(WriteCacheAction.Disable);
        device.Control(WriteCacheAction.Release);
    }
    private static void Apply(DiskTarget target, CacheDevice device, ulong mode, int budgetMiB = 64)
    {
        device.Control(WriteCacheAction.LabFlushOrder, value: mode);
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
