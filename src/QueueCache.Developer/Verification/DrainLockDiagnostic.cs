using QueueCache.Management;

namespace QueueCache.Developer.Verification;

/// <summary>A standalone window saw background writes (file-system metadata, TRIM); it is never scored, only
/// recorded and remeasured within <see cref="DrainLockDiagnostic.StandaloneAttempts"/> (plan 160).</summary>
public sealed class StandaloneNoiseException(ulong lowerWrites, ulong drainedBytes) : IOException(
    $"Clean standalone control is missing: {lowerWrites} background lower write(s), {drainedBytes} bytes drained.")
{
    public ulong LowerWrites { get; } = lowerWrites;
    public ulong DrainedBytes { get; } = drainedBytes;
}

public sealed record DrainLockWindow(PerformanceCase Case, CacheLayoutSnapshot Before, CacheLayoutSnapshot After,
    DateTimeOffset Start, DateTimeOffset End, DiskSpdScore Reader, DiskSpdScore? Writer);

/// <summary>Short same-build attribution screen. Global lock timings include readers, writer, drainers and observers.</summary>
public static class DrainLockDiagnostic
{
    /// <summary>Recorded measurements per standalone case before a background-write window fails the run.</summary>
    public const int StandaloneAttempts = 3;
    public const string Suite = "drain-lock-diagnostic";
    // PromoteOnRead controls promotion of retained write blocks, not miss admission.
    // A Fixed 100% write partition makes ReadLimit zero and keeps the disk-read arm uncached.
    public static CacheOptions Options(PerformanceCase test) => new(
        Allocation: test.Resident ? CacheAllocation.Automatic : CacheAllocation.Fixed,
        WritePercent: test.Resident ? 50 : 100, RetainWrites: false, PromoteOnRead: test.Resident,
        Drain: DrainAlgorithm.Eager, BatchKiB: 2048, Parallelism: test.Parallelism);
    public static void ValidateOptions(VerificationOptions options)
    {
        if (options.BudgetMiB != 2048 || options.Repeats != 3 || options.DurationSeconds != 6 ||
            options.CaseFilter is not null || options.SoakSeconds is not null)
            throw new ArgumentException("drain-lock-diagnostic requires 2048 MiB, three repeats, six-second windows and no case filter/soak.");
    }

    public static IReadOnlyList<PerformanceCase> Cases(VerificationOptions options)
    {
        var cases = new List<PerformanceCase>();
        for (var repeat = 1; repeat <= 3; repeat++)
        foreach (var resident in repeat % 2 == 1 ? new[] { true, false } : [false, true])
        foreach (var parallel in repeat % 2 == 1 ? new[] { 4, 8 } : [8, 4])
        foreach (var loaded in repeat % 2 == 1 ? new[] { false, true } : [true, false])
        foreach (var timing in repeat % 2 == 1 ? new[] { false, true } : [true, false])
            cases.Add(new($"lock-r{repeat}-{(resident ? "cached1M-q8" : "disk4K-q1")}-p{parallel}-{(loaded ? "loaded" : "alone")}-timing{timing}",
                "Automatic", "Eager", 0, resident ? 8 : 1, loaded, repeat,
                Workload: resident ? "sequential-read" : "random-read", Resident: resident, Timing: timing,
                BatchKiB: 2048, Parallelism: parallel));
        return cases;
    }

    public static object Metrics(DrainLockWindow window)
    {
        Validate(window);
        var a = window.Before; var b = window.After;
        var seconds = (window.End - window.Start).TotalSeconds;
        double Ms(ulong first, ulong last) => 1000.0 * checked(last - first) / a.Performance.Frequency;
        double? Timed(ulong first, ulong last) => window.Case.Timing ? Ms(first, last) : null;
        return new
        {
            EnvelopeSeconds = seconds,
            // These sum across all callers; never label them reader-only or divide them into reader latency.
            GlobalLockWaitMilliseconds = Timed(a.Performance.LockWaitTicks, b.Performance.LockWaitTicks),
            GlobalLockHeldMilliseconds = Timed(a.Performance.LockHoldTicks, b.Performance.LockHoldTicks),
            GlobalLockBusyPercent = window.Case.Timing ? Ms(a.Performance.LockHoldTicks, b.Performance.LockHoldTicks) / (seconds * 10) : (double?)null,
            GlobalLockAcquires = window.Case.Timing ? b.Performance.LockAcquires - a.Performance.LockAcquires : (ulong?)null,
            GlobalQueueWaitMilliseconds = Timed(a.Performance.QueueWaitTicks, b.Performance.QueueWaitTicks),
            DrainSelectionMilliseconds = Timed(a.Performance.DrainSelectionTicks, b.Performance.DrainSelectionTicks),
            DrainCopyMilliseconds = Timed(a.Performance.DrainCopyTicks, b.Performance.DrainCopyTicks),
            DrainRetirementMilliseconds = Timed(a.Performance.DrainRetirementTicks, b.Performance.DrainRetirementTicks),
            SummedDrainerLowerWaitMilliseconds = Timed(a.Performance.LowerIoTicks, b.Performance.LowerIoTicks),
            DrainedBytes = b.State.DrainedBytes - a.State.DrainedBytes,
            DrainMiBPerSecond = (b.State.DrainedBytes - a.State.DrainedBytes) / seconds / (1 << 20),
            LowerReads = b.Diagnostics.Attribution!.LowerReadAttempts - a.Diagnostics.Attribution!.LowerReadAttempts,
            LowerWrites = b.Diagnostics.Attribution!.LowerWriteAttempts - a.Diagnostics.Attribution!.LowerWriteAttempts,
            ReadHitBytes = b.State.ReadHitBytes - a.State.ReadHitBytes,
            ReadMissBytes = b.State.ReadMissBytes - a.State.ReadMissBytes,
            CapacityWaits = b.Performance.CapacityWaits - a.Performance.CapacityWaits
        };
    }

    public static void Validate(DrainLockWindow window)
    {
        var a = window.Before; var b = window.After; var c = window.Case;
        foreach (var s in new[] { a, b })
            if (!s.State.Operational || !s.State.Enabled || !s.State.UnsafeDefer || s.State.LastError != 0 ||
                s.State.Suspended || s.State.Removed || s.State.Faulted || s.State.BudgetBytes != 2UL << 30 ||
                s.State.Options != Options(c) ||
                s.Performance.Frequency == 0 || s.Performance.TimingEnabled != (c.Timing ? 1UL : 0UL) ||
                s.Diagnostics.CopyFlags != 15 || s.Diagnostics.Attribution is null)
                throw new InvalidDataException("Lock diagnostic cache/profile/timing/attribution evidence differs.");
        if (a.State.Instance != b.State.Instance || a.State.Generation != b.State.Generation || a.State.Errors != b.State.Errors ||
            a.Performance.Frequency != b.Performance.Frequency || window.End <= window.Start ||
            window.Reader.Operations <= 0 || window.Reader.ReadP99Milliseconds is null ||
            window.Reader.Seconds is < 5.5 or > 6.5 || window.Reader.Bytes <= 0 ||
            window.Reader.Bytes != window.Reader.Operations * (c.Resident ? 1L << 20 : 4096))
            throw new InvalidDataException("Invalid lock diagnostic identity or read score.");
        if (c.Timing && b.Performance.LockAcquires <= a.Performance.LockAcquires)
            throw new InvalidDataException("Timing-on diagnostic has no lock acquisitions.");
        static ulong Delta(ulong before, ulong after) => after >= before ? after - before :
            throw new InvalidDataException("Diagnostic lifetime counter moved backwards.");
        var hits = Delta(a.State.ReadHitBytes, b.State.ReadHitBytes);
        var misses = Delta(a.State.ReadMissBytes, b.State.ReadMissBytes);
        var reads = Delta(a.Diagnostics.Attribution!.LowerReadAttempts, b.Diagnostics.Attribution!.LowerReadAttempts);
        var writes = Delta(a.Diagnostics.Attribution.LowerWriteAttempts, b.Diagnostics.Attribution.LowerWriteAttempts);
        var drained = Delta(a.State.DrainedBytes, b.State.DrainedBytes);
        if (c.Resident ? misses != 0 || reads != 0 || hits < (ulong)window.Reader.Bytes :
            hits != 0 || misses < (ulong)window.Reader.Bytes || reads == 0)
            throw new InvalidDataException("Reader did not stay in its declared RAM-hit/disk-miss path.");
        if (c.Writer ? window.Writer is null || window.Writer.Operations <= 0 || writes == 0 || drained < 64UL << 20 :
            window.Writer is not null)
            throw new InvalidDataException("Expected concurrent drain activity or clean standalone control is missing.");
        if (!c.Writer && (writes != 0 || drained != 0))
            throw new StandaloneNoiseException(writes, drained);
        foreach (var (first, last) in new[]
        {
            (a.Performance.LockAcquires, b.Performance.LockAcquires), (a.Performance.LockWaitTicks, b.Performance.LockWaitTicks),
            (a.Performance.LockHoldTicks, b.Performance.LockHoldTicks), (a.Performance.QueueWaitTicks, b.Performance.QueueWaitTicks),
            (a.Performance.DrainSelectionTicks, b.Performance.DrainSelectionTicks), (a.Performance.DrainCopyTicks, b.Performance.DrainCopyTicks),
            (a.Performance.DrainRetirementTicks, b.Performance.DrainRetirementTicks), (a.Performance.LowerIoTicks, b.Performance.LowerIoTicks),
            (a.Performance.CapacityWaits, b.Performance.CapacityWaits)
        }) Delta(first, last);
    }

    public static void RequireComplete(IReadOnlyList<PerformanceCase> cases, IReadOnlyList<DrainLockWindow> windows)
    {
        if (cases.Count != 48 || windows.Count != cases.Count || windows.Select(w => w.Case.Id).Distinct().Count() != windows.Count ||
            cases.Any(c => windows.Count(w => w.Case == c) != 1))
            throw new InvalidDataException("Incomplete or inconsistent lock diagnostic matrix; no complete report.");
        foreach (var window in windows)
        {
            try { Validate(window); }
            catch (StandaloneNoiseException ex) { throw new InvalidDataException("A scored window is not a clean standalone control.", ex); }
        }
    }
}
