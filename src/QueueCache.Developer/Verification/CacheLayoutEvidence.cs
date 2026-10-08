using QueueCache.Management;

namespace QueueCache.Developer.Verification;

public enum CacheLayoutStage { None, Fresh, SequentialReuse, RandomReuse, Recreated }
public sealed record CacheLayoutSnapshot(WriteCacheState State, CachePerformance Performance, CacheDiagnostics Diagnostics);

/// <summary>Allocation history must change only where intended; scored reads must stay in RAM.</summary>
public static class CacheLayoutEvidence
{
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
