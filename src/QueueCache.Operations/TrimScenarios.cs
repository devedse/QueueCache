using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using QueueCache.Management;

namespace QueueCache.Operations;

/// <summary>
/// File-level TRIM (FSCTL_FILE_LEVEL_TRIM; NTFS sends the volume a data-set TRIM) while the cache holds data for
/// the trimmed range: pending writes are dropped rather than written, clean copies are dropped, an already
/// issued overlapping write completes first while unrelated in-flight writes are not awaited, and untrimmed
/// neighbours and later rewrites are exact. Needs a disk that accepts
/// TRIM (for example a VHDX); otherwise the case is SKIP. New files only; trimmed bytes are never compared.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TrimScenarios
{
    private const int MiB = 1 << 20;

    public static IReadOnlyList<CheckResult> Run(DiskTarget target, CacheDevice device, string workDirectory)
    {
        var initial = device.GetWriteCacheState();
        if (!initial.SupportsDeferredDrain || !initial.ExtendedCountersAvailable)
            throw new NotSupportedException("TRIM verification requires Deferred draining and the extended TRIM/discard counters.");
        ConfigurationManager.Apply(target, VolumeScenarios.Pending(256), true);
        var directory = VolumeScenarios.NewDirectory(target, workDirectory, "trim-cache");
        // Probe on a file of its own so an unsupported disk leaves no cached data behind.
        var probe = Path.Combine(directory, "probe.bin");
        VolumeScenarios.WriteNew(probe, VolumeScenarios.Pattern(MiB, 0x7121));
        try
        {
            using var file = new AlignedFile(probe, MiB, create: false);
            file.Trim(0, MiB);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 1 or 50 or 326)
        {
            device.Control(WriteCacheAction.Flush);
            return [new("trim-cache", "SKIP", $"The disk under {target.Device} does not accept TRIM: Win32 {ex.NativeErrorCode} ({ex.Message}). " +
                "Run this suite on a lab-disk volume (qcache developer lab-disk create).")];
        }
        var original = device.GetDiagnostics().DrainPriority?.TrimMode
            ?? throw new NotSupportedException("TRIM verification requires V24 diagnostics (QcLabTrimMode).");
        var checks = new List<CheckResult>();
        try
        {
            // Plan 162: every check with the ordered full-scan TRIM, then again with index lookup and
            // asynchronous lower TRIMs, then the two checks only an asynchronous TRIM can fail (plan 163 ordering).
            foreach (var (mode, suffix) in new[] { (0UL, ""), (3UL, "-async") })
            {
                device.Control(WriteCacheAction.LabTrimMode, value: mode);
                checks.Add(Pending(device, directory, suffix));
                checks.Add(Clean(device, directory, suffix));
                checks.Add(InFlight(target, device, directory, suffix));
                checks.Add(EagerTrim(target, device, directory, "eager-unrelated-not-awaited" + suffix, 12 * MiB, 4 * MiB, false));
                checks.Add(EagerTrim(target, device, directory, "eager-overlap-awaited" + suffix, 0, 2 * MiB, true));
            }
            checks.Add(AsyncPathFree(device, directory));
            checks.Add(AsyncEagerOrdering(target, device, directory));
        }
        finally
        {
            device.Control(WriteCacheAction.LabTrimMode, value: original);
            device.Control(WriteCacheAction.LabDelay, value: 0);
            ConfigurationManager.Apply(target, VolumeScenarios.Pending(256), true);
        }
        return checks;
    }

    // Accepted, undrained writes in the trimmed range are dropped, not drained to the disk.
    private static CheckResult Pending(CacheDevice device, string directory, string suffix)
    {
        var path = Path.Combine(directory, "pending" + suffix + ".bin");
        var first = VolumeScenarios.Pattern(4 * MiB, 0x7122);
        VolumeScenarios.WriteNew(path, first);
        var before = device.GetWriteCacheState();
        if (before.DirtyBytes < 4UL * MiB)
            throw new IOException("Could not establish 4 MiB of pending writes before TRIM.");
        Trim(path, MiB, MiB);
        var after = device.GetWriteCacheState();
        var guards = Guards(path, first, MiB, MiB);
        var dropped = after.DiscardedBytes - before.DiscardedBytes;
        var second = VolumeScenarios.Pattern(4 * MiB, 0x7123);
        VolumeScenarios.WriteNew(path, second);
        device.Control(WriteCacheAction.Flush);
        device.Control(WriteCacheAction.DropClean);
        var rewrite = VolumeScenarios.ReadAll(path).AsSpan().SequenceEqual(second);
        var pass = after.TrimRequests > before.TrimRequests && dropped >= (ulong)MiB && after.DrainedBytes == before.DrainedBytes &&
            Healthy(after) && guards && rewrite;
        return new("trim-cache/pending-writes-dropped" + suffix, pass ? "PASS" : "FAIL",
            $"TRIM of the middle 1 MiB of 4 MiB pending: {after.TrimRequests - before.TrimRequests} TRIM request(s), {dropped} pending bytes dropped, " +
            $"{after.DrainedBytes - before.DrainedBytes} bytes drained; untrimmed guards {(guards ? "exact" : "DIFFER")} from RAM; " +
            $"full rewrite after flush and dropping clean data {(rewrite ? "exact" : "DIFFERS")} from the disk.");
    }

    // Drained, retained copies in the trimmed range are released; nothing is written.
    private static CheckResult Clean(CacheDevice device, string directory, string suffix)
    {
        var path = Path.Combine(directory, "clean" + suffix + ".bin");
        var data = VolumeScenarios.Pattern(4 * MiB, 0x7124);
        VolumeScenarios.WriteNew(path, data);
        device.Control(WriteCacheAction.Flush);
        var before = device.GetWriteCacheState();
        Trim(path, MiB, MiB);
        var after = device.GetWriteCacheState();
        var released = before.CleanReadBytes + before.CleanWriteBytes - Math.Min(before.CleanReadBytes + before.CleanWriteBytes,
            after.CleanReadBytes + after.CleanWriteBytes);
        device.Control(WriteCacheAction.DropClean);
        var guards = Guards(path, data, MiB, MiB);
        var pass = after.TrimRequests > before.TrimRequests && released >= (ulong)MiB && after.DrainedBytes == before.DrainedBytes &&
            Healthy(after) && guards;
        return new("trim-cache/clean-copies-released" + suffix, pass ? "PASS" : "FAIL",
            $"TRIM of the middle 1 MiB of 4 MiB drained and retained: {released} clean bytes released, {after.DrainedBytes - before.DrainedBytes} bytes drained; " +
            $"untrimmed guards {(guards ? "exact" : "DIFFER")} from the disk after dropping clean data.");
    }

    // TRIM arriving while a drain of the same file is in progress: it waits for the overlapping issued write only,
    // and the remaining drain and the guards stay exact.
    private static CheckResult InFlight(DiskTarget target, CacheDevice device, string directory, string suffix)
    {
        var path = Path.Combine(directory, "in-flight" + suffix + ".bin");
        var data = VolumeScenarios.Pattern(8 * MiB, 0x7125);
        VolumeScenarios.WriteNew(path, data);
        var start = device.GetWriteCacheState();
        device.Control(WriteCacheAction.LabDelay, value: 300);
        Task flush;
        WriteCacheState before, after;
        try
        {
            flush = Task.Run(() =>
            {
                using var control = new CacheDevice(target.Device, writable: true);
                control.Control(WriteCacheAction.Flush);
            });
            if (!SpinWait.SpinUntil(() => device.GetWriteCacheState().InFlightBytes != 0 || flush.IsCompleted, 5000) || flush.IsCompleted)
                throw new IOException("Could not observe a drain in flight before TRIM.");
            before = device.GetWriteCacheState();
            Trim(path, 3 * MiB, 2 * MiB);
            after = device.GetWriteCacheState();
        }
        finally
        {
            device.Control(WriteCacheAction.LabDelay, value: 0);
        }
        flush.GetAwaiter().GetResult();
        var drained = device.GetWriteCacheState();
        // Every pending byte of the file either reached the disk or was dropped by the TRIM, and the TRIM dropped
        // no more than its own range. New file-system metadata may arrive after the flush (Fast), so the guards
        // are compared after a second flush, from the disk.
        var written = drained.DrainedBytes - start.DrainedBytes;
        var dropped = drained.DiscardedBytes - start.DiscardedBytes;
        device.Control(WriteCacheAction.Flush);
        device.Control(WriteCacheAction.DropClean);
        var guards = Guards(path, data, 3 * MiB, 2 * MiB);
        var pass = after.TrimRequests > before.TrimRequests && Healthy(drained) && written + dropped >= 8UL * MiB &&
            dropped <= 2UL * MiB && guards;
        return new("trim-cache/during-drain" + suffix, pass ? "PASS" : "FAIL",
            $"TRIM of 2 MiB inside 8 MiB pending while {before.InFlightBytes} bytes were in flight (300 ms lab delay per drain batch): " +
            $"{after.TrimRequests - before.TrimRequests} TRIM request(s); the flush wrote {written} bytes and the TRIM dropped {dropped} " +
            $"(at most its 2 MiB); untrimmed guards {(guards ? "exact" : "DIFFER")} from the disk.");
    }

    // Background (Eager) draining, no explicit flush: an explicit flush holds the ordered request path, so a TRIM
    // issued during it waits for the whole flush whatever the driver's TRIM rule. Oldest first, the first batches
    // hold the file's start and each stays in flight for the 2 s lab delay (plan 160).
    private static CheckResult EagerTrim(DiskTarget target, CacheDevice device, string directory, string name,
        int offset, int length, bool overlapsInFlight)
    {
        var path = Path.Combine(directory, name + ".bin");
        var data = VolumeScenarios.Pattern(16 * MiB, 0x7126 + offset / MiB);
        device.Control(WriteCacheAction.Flush);
        // 1024 KiB x 4 keeps the first 4 MiB in flight; the 2048 KiB x 8 default would issue the whole file at once.
        ConfigurationManager.Apply(target, new CacheConfiguration(256, CachePreset.Fast)
        {
            Options = new(Drain: DrainAlgorithm.Eager, BatchKiB: 1024, Parallelism: 4)
        }, true);
        var start = device.GetWriteCacheState();
        WriteCacheState before, after;
        TimeSpan elapsed;
        device.Control(WriteCacheAction.LabDelay, value: 2000);
        try
        {
            VolumeScenarios.WriteNew(path, data);
            if (!SpinWait.SpinUntil(() => device.GetWriteCacheState().InFlightBytes != 0, 5000))
                throw new IOException("Could not observe a background drain in flight before TRIM.");
            before = device.GetWriteCacheState();
            var timer = Stopwatch.StartNew();
            Trim(path, offset, length);
            elapsed = timer.Elapsed;
            after = device.GetWriteCacheState();
        }
        finally
        {
            device.Control(WriteCacheAction.LabDelay, value: 0);
        }
        device.Control(WriteCacheAction.Flush);
        var drained = device.GetWriteCacheState();
        var written = drained.DrainedBytes - start.DrainedBytes;
        var dropped = drained.DiscardedBytes - start.DiscardedBytes;
        device.Control(WriteCacheAction.DropClean);
        var guards = Guards(path, data, offset, length);
        ConfigurationManager.Apply(target, VolumeScenarios.Pending(256), true);
        // Each batch holds 2 s, so no write lands while an unrelated TRIM runs: it returns under 1 s with drained bytes
        // unchanged. An overlapping TRIM must not reach the disk before that range's in-flight write lands.
        var landed = after.DrainedBytes - before.DrainedBytes;
        var timing = overlapsInFlight ? landed > 0 && elapsed >= TimeSpan.FromMilliseconds(250)
            : landed == 0 && elapsed < TimeSpan.FromSeconds(1);
        var pass = after.TrimRequests > before.TrimRequests && timing && Healthy(drained) &&
            written + dropped >= 16UL * MiB && dropped <= (ulong)length && guards;
        return new("trim-cache/" + name, pass ? "PASS" : "FAIL",
            $"Eager drain with a 2 s lab delay per batch, {before.InFlightBytes} bytes in flight: TRIM of {length / MiB} MiB at " +
            $"{offset / MiB} MiB of a new 16 MiB file ({(overlapsInFlight ? "inside" : "outside")} the first batches) returned in " +
            $"{elapsed.TotalMilliseconds:F0} ms with {landed} bytes landing meanwhile (must be {(overlapsInFlight ? "after a write landed, at least 250 ms" : "none, under 1000 ms")}); {after.InFlightBytes} bytes " +
            $"still in flight; drains wrote {written} bytes and the TRIM dropped {dropped} (at most its range); untrimmed guards " +
            $"{(guards ? "exact" : "DIFFER")} from the disk.");
    }

    // While an asynchronous lower TRIM is held for 2 s (QcTrimLabHold), the ordered request path is free:
    // another file's rewrite and read finish meanwhile. The TRIM still drops its pending range (plan 162).
    private static CheckResult AsyncPathFree(CacheDevice device, string directory)
    {
        var path = Path.Combine(directory, "async-held.bin");
        var other = Path.Combine(directory, "async-other.bin");
        var data = VolumeScenarios.Pattern(4 * MiB, 0x7130);
        var next = VolumeScenarios.Pattern(4 * MiB, 0x7131);
        VolumeScenarios.WriteNew(path, data);
        VolumeScenarios.WriteNew(other, VolumeScenarios.Pattern(4 * MiB, 0x7132));
        var before = device.GetWriteCacheState();
        var asyncBefore = device.GetDiagnostics().DrainPriority!.TrimAsync;
        device.Control(WriteCacheAction.LabTrimMode, value: 7);
        TimeSpan trimElapsed, otherElapsed;
        bool overlapped, otherExact;
        try
        {
            var timer = Stopwatch.StartNew();
            var trim = Task.Run(() => Trim(path, MiB, MiB, shared: true));
            Thread.Sleep(200);
            if (trim.IsCompleted) throw new IOException("The held TRIM returned before the concurrent I/O started.");
            var otherTimer = Stopwatch.StartNew();
            using (var file = new AlignedFile(other, MiB, create: false))
                for (var offset = 0; offset < next.Length; offset += MiB)
                    file.Write(offset, next[offset..(offset + MiB)]);
            otherExact = VolumeScenarios.ReadAll(other).AsSpan().SequenceEqual(next);
            otherElapsed = otherTimer.Elapsed;
            overlapped = !trim.IsCompleted;
            trim.GetAwaiter().GetResult();
            trimElapsed = timer.Elapsed;
        }
        finally { device.Control(WriteCacheAction.LabTrimMode, value: 3); }
        var after = device.GetWriteCacheState();
        var asyncAfter = device.GetDiagnostics().DrainPriority!.TrimAsync;
        var guardsRam = Guards(path, data, MiB, MiB);
        device.Control(WriteCacheAction.Flush);
        device.Control(WriteCacheAction.DropClean);
        var guardsDisk = Guards(path, data, MiB, MiB);
        var otherDisk = VolumeScenarios.ReadAll(other).AsSpan().SequenceEqual(next);
        var dropped = after.DiscardedBytes - before.DiscardedBytes;
        var pass = overlapped && otherElapsed < TimeSpan.FromSeconds(1) && trimElapsed >= TimeSpan.FromSeconds(1.5) &&
            asyncAfter > asyncBefore && after.TrimRequests > before.TrimRequests && dropped >= (ulong)MiB &&
            otherExact && otherDisk && guardsRam && guardsDisk && Healthy(after);
        return new("trim-cache/async-request-path-free", pass ? "PASS" : "FAIL",
            $"Asynchronous TRIM of 1 MiB inside 4 MiB pending, held 2 s, returned in {trimElapsed.TotalMilliseconds:F0} ms (at least 1500); " +
            $"meanwhile another file's 4 MiB rewrite and read took {otherElapsed.TotalMilliseconds:F0} ms (under 1000, " +
            $"{(overlapped ? "while" : "NOT while")} the TRIM was outstanding) and read back {(otherExact ? "exact" : "DIFFERENT")}; " +
            $"{asyncAfter - asyncBefore} asynchronous TRIM(s), {dropped} pending bytes dropped; guards {(guardsRam && guardsDisk ? "exact" : "DIFFER")} " +
            $"from RAM and disk; other file {(otherDisk ? "exact" : "DIFFERS")} from disk.");
    }

    // Under Eager, while an asynchronous lower TRIM of one file is held 2 s (QcTrimLabHold): another file's rewrite
    // is admitted and drains meanwhile, so the live fence holds back only its own ranges. A rewrite of the held
    // range is queued too. NTFS keeps a file's writes behind its outstanding file-level TRIM, so that rewrite
    // normally reaches the cache only after the TRIM. If it completes while the TRIM is outstanding, it must stay
    // dirty, because the fence keeps it from draining before the TRIM. Afterwards the disk holds both rewrites (plan 163).
    private static CheckResult AsyncEagerOrdering(DiskTarget target, CacheDevice device, string directory)
    {
        var path = Path.Combine(directory, "async-ordering.bin");
        var other = Path.Combine(directory, "async-ordering-other.bin");
        var data = VolumeScenarios.Pattern(4 * MiB, 0x7133);
        var rewrite = VolumeScenarios.Pattern(MiB, 0x7134);
        var next = VolumeScenarios.Pattern(4 * MiB, 0x7135);
        device.Control(WriteCacheAction.Flush);
        ConfigurationManager.Apply(target, new CacheConfiguration(256, CachePreset.Fast) { Options = new(Drain: DrainAlgorithm.Eager) }, true);
        VolumeScenarios.WriteNew(path, data);
        VolumeScenarios.WriteNew(other, VolumeScenarios.Pattern(4 * MiB, 0x7136));
        device.Control(WriteCacheAction.Flush);
        device.Control(WriteCacheAction.DropClean);
        var before = device.GetWriteCacheState();
        var asyncBefore = device.GetDiagnostics().DrainPriority!.TrimAsync;
        device.Control(WriteCacheAction.LabTrimMode, value: 7);
        TimeSpan otherElapsed;
        bool drained, overlapped = false, admittedDuringTrim = false;
        ulong dirtyDuringHold = 0;
        try
        {
            var trim = Task.Run(() => Trim(path, MiB, MiB, shared: true));
            Thread.Sleep(200);
            if (trim.IsCompleted) throw new IOException("The held TRIM returned before the rewrites started.");
            var same = Task.Run(() =>
            {
                using var file = new AlignedFile(path, MiB, create: false, shareAll: true);
                file.Write(MiB, rewrite);
            });
            var timer = Stopwatch.StartNew();
            using (var file = new AlignedFile(other, MiB, create: false))
                for (var offset = 0; offset < next.Length; offset += MiB)
                    file.Write(offset, next[offset..(offset + MiB)]);
            otherElapsed = timer.Elapsed;
            // Eager drains 4 MiB within milliseconds; only a fenced rewrite may stay dirty.
            drained = SpinWait.SpinUntil(() =>
            {
                var sameDone = same.IsCompleted;
                dirtyDuringHold = device.GetWriteCacheState().DirtyBytes;
                overlapped = !trim.IsCompleted;
                admittedDuringTrim |= sameDone && overlapped; // observed in that order: it finished first
                return dirtyDuringHold <= (sameDone ? (ulong)MiB : 0);
            }, 1200);
            trim.GetAwaiter().GetResult();
            same.GetAwaiter().GetResult();
        }
        finally { device.Control(WriteCacheAction.LabTrimMode, value: 3); }
        var held = !admittedDuringTrim || dirtyDuringHold >= (ulong)MiB;
        device.Control(WriteCacheAction.Flush);
        var after = device.GetWriteCacheState();
        var asyncAfter = device.GetDiagnostics().DrainPriority!.TrimAsync;
        device.Control(WriteCacheAction.DropClean);
        var actual = VolumeScenarios.ReadAll(path);
        var rewritten = actual.Length == data.Length && actual.AsSpan(MiB, MiB).SequenceEqual(rewrite);
        var guards = Guards(path, data, MiB, MiB);
        var otherDisk = VolumeScenarios.ReadAll(other).AsSpan().SequenceEqual(next);
        ConfigurationManager.Apply(target, VolumeScenarios.Pending(256), true);
        var pass = overlapped && drained && held && otherElapsed < TimeSpan.FromSeconds(1) && asyncAfter > asyncBefore &&
            after.TrimRequests > before.TrimRequests && rewritten && guards && otherDisk && Healthy(after);
        return new("trim-cache/async-eager-ordering", pass ? "PASS" : "FAIL",
            $"Eager drain with an asynchronous TRIM held 2 s: another file's 4 MiB rewrite took {otherElapsed.TotalMilliseconds:F0} ms (under 1000) " +
            $"and {(drained ? "drained" : "did NOT drain")} to {dirtyDuringHold} dirty bytes {(overlapped ? "while" : "NOT while")} the TRIM was outstanding; " +
            $"the rewrite of the held range {(admittedDuringTrim ? (held ? "completed during the TRIM and stayed fenced" : "completed during the TRIM and was NOT fenced") : "waited for the TRIM (NTFS ordered it)")}; " +
            $"{asyncAfter - asyncBefore} asynchronous TRIM(s); after a flush the disk holds the rewrite {(rewritten ? "exactly" : "NOT (erased or stale)")}, " +
            $"guards {(guards ? "exact" : "DIFFER")}, other file {(otherDisk ? "exact" : "DIFFERS")}.");
    }

    private static void Trim(string path, long offset, long length, bool shared = false)
    {
        using var file = new AlignedFile(path, MiB, create: false, shareAll: shared);
        file.Trim(offset, length);
    }

    private static bool Healthy(WriteCacheState state) => !state.Faulted && state.LastError == 0 && state.Enabled;

    // Everything outside [offset, offset + length) matches; the trimmed range itself is undefined.
    private static bool Guards(string path, byte[] expected, int offset, int length)
    {
        var actual = VolumeScenarios.ReadAll(path);
        return actual.Length == expected.Length && actual.AsSpan(0, offset).SequenceEqual(expected.AsSpan(0, offset)) &&
            actual.AsSpan(offset + length).SequenceEqual(expected.AsSpan(offset + length));
    }
}
