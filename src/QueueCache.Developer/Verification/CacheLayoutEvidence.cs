using QueueCache.Management;

namespace QueueCache.Developer.Verification;

public enum CacheLayoutStage { None, Fresh, SequentialReuse, RandomReuse, Recreated, ResetAfterSequential, ResetAfterRandom,
    ResetAscending, Churned, ChurnedFull }
public sealed record CacheLayoutSnapshot(WriteCacheState State, CachePerformance Performance, CacheDiagnostics Diagnostics);

/// <summary>Allocation history must change only where intended; scored reads must stay in RAM.</summary>
public static class CacheLayoutEvidence
{
    /// <summary>Stages that reset the allocator (QcLabResetFreeOrder) on the same allocation.</summary>
    public static bool Resets(CacheLayoutStage stage) =>
        stage is CacheLayoutStage.ResetAfterSequential or CacheLayoutStage.ResetAfterRandom or CacheLayoutStage.ResetAscending;

    /// <summary>Nothing pending or written, and no disk reads, between two snapshots.</summary>
    public static bool IsQuiet(CacheLayoutSnapshot first, CacheLayoutSnapshot second) =>
        first.State.Operational && second.State.Operational && first.State.Instance != 0 &&
        first.State.Instance == second.State.Instance && first.State.Generation == second.State.Generation &&
        first.State.PayloadCapacity == second.State.PayloadCapacity && first.State.Errors == second.State.Errors &&
        first.State.LastError == 0 && second.State.LastError == 0 &&
        first.State.DirtyBytes == 0 && second.State.DirtyBytes == 0 &&
        first.State.InFlightBytes == 0 && second.State.InFlightBytes == 0 &&
        first.State.ReadMissBytes == second.State.ReadMissBytes && first.State.DrainedBytes == second.State.DrainedBytes &&
        first.Diagnostics.Attribution is { } a && second.Diagnostics.Attribution is { } b &&
        a.LowerWriteAttempts == b.LowerWriteAttempts && a.LowerReadAttempts == b.LowerReadAttempts && a.LowerFlushAttempts == b.LowerFlushAttempts;

    /// <summary>The measurement taken just before scoring must exist and cover the resident file.</summary>
    public static void ValidateLayout(CacheLayoutSnapshot measured, ulong? previousMeasurements)
    {
        if (measured.Diagnostics.Layout is not { } layout || layout.Measurements == 0 || layout.Measurements == previousMeasurements ||
            layout.Blocks < (1UL << 30) / 4096 || layout.Contiguous + layout.Reversed > layout.Neighbors || layout.Neighbors > layout.Blocks)
            throw new InvalidDataException("Layout measurement is missing, stale or inconsistent; the driver must support QcLabMeasureLayout.");
    }

    public static void ValidateReset(CacheLayoutSnapshot before, CacheLayoutSnapshot after)
    {
        ValidateTransition(before.State.Generation, after, true);
        var a = before.Diagnostics.Attribution;
        var b = after.Diagnostics.Attribution;
        if (!before.State.Operational || before.State.Instance == 0 || before.State.Instance != after.State.Instance ||
            before.State.OccupiedSlots != 0 || after.State.OccupiedSlots != 0 ||
            before.State.DirtyBytes != 0 || before.State.InFlightBytes != 0 ||
            before.State.CleanReadBytes != 0 || after.State.CleanReadBytes != 0 ||
            before.State.CleanWriteBytes != 0 || after.State.CleanWriteBytes != 0 ||
            before.State.BudgetBytes != after.State.BudgetBytes || before.State.ReservedBytes != after.State.ReservedBytes ||
            before.State.PayloadCapacity != after.State.PayloadCapacity || before.State.Options != after.State.Options ||
            before.State.Flags != after.State.Flags || before.State.Errors != after.State.Errors ||
            before.State.LastError != 0 || after.State.LastError != 0 || before.Performance.TimingEnabled != 0 ||
            before.State.AcceptedBytes != after.State.AcceptedBytes || before.State.DrainedBytes != after.State.DrainedBytes ||
            before.State.ReadHitBytes != after.State.ReadHitBytes || before.State.ReadMissBytes != after.State.ReadMissBytes ||
            before.State.Evictions != after.State.Evictions || a is null || b is null ||
            a.LowerReadAttempts != b.LowerReadAttempts || a.LowerWriteAttempts != b.LowerWriteAttempts ||
            a.LowerFlushAttempts != b.LowerFlushAttempts)
            throw new InvalidDataException("Free-order reset changed allocation, contents, counters or lower I/O; comparison is invalid.");
    }

    public static void ValidateTransition(ulong? previousGeneration, CacheLayoutSnapshot current, bool reuse)
    {
        if (!current.State.Operational || current.State.BudgetBytes != 2UL << 30 ||
            current.State.DirtyBytes != 0 || current.State.InFlightBytes != 0 ||
            current.Performance.TimingEnabled != 0 ||
            (reuse ? previousGeneration is null || previousGeneration != current.State.Generation
                   : previousGeneration == current.State.Generation))
            throw new InvalidDataException("Cache allocation transition, timing or clean-state contract was not met.");
    }

    public static void ValidateScore(CacheLayoutSnapshot before, CacheLayoutSnapshot after, long bytes)
    {
        WarmResidentEvidence.Validate(before.State, after.State, bytes, 1UL << 30);
        var a = before.Diagnostics.Attribution;
        var b = after.Diagnostics.Attribution;
        if (a is null || b is null || before.Diagnostics.CallerPath is null || after.Diagnostics.CallerPath is null ||
            before.Diagnostics.CopyOffloadReads is null || after.Diagnostics.CopyOffloadReads is null ||
            before.Performance.TimingEnabled != 0 || after.Performance.TimingEnabled != 0 ||
            before.State.DirtyBytes != 0 || after.State.DirtyBytes != 0 ||
            before.State.InFlightBytes != 0 || after.State.InFlightBytes != 0 ||
            before.State.DrainedBytes != after.State.DrainedBytes ||
            a.LowerReadAttempts != b.LowerReadAttempts || a.LowerWriteAttempts != b.LowerWriteAttempts ||
            a.LowerFlushAttempts != b.LowerFlushAttempts)
            throw new InvalidDataException("Cache layout measurement has missing evidence, timing enabled or lower I/O; do not treat it as a pure RAM-copy comparison.");
    }
}
