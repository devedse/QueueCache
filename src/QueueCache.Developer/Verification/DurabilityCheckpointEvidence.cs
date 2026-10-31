using QueueCache.Management;

namespace QueueCache.Developer.Verification;

/// <summary>Post-checkpoint cuts require all bytes; ordinary volatile cuts retain their classification-only contract.</summary>
public static class DurabilityCheckpointEvidence
{
    public const int MaximumFlushAttempts = 3;
    /// <summary>With ordering on (plan 217) the explicit flush returns once every group sealed before it is on the
    /// disk, while writes admitted meanwhile stay in RAM. The filesystem flush before the call sealed everything this
    /// checkpoint protects, so the frontier reaching the group that was open at the start completes the boundary
    /// on a volume that keeps writing (C:).</summary>
    public static bool NeedsAnotherFlush(WriteCacheState before, WriteCacheState after,
        CacheFlushOrder? orderBefore = null, CacheFlushOrder? orderAfter = null)
    {
        if (before.LastError != 0 || after.LastError != 0 || after.Errors != before.Errors ||
            after.Instance != before.Instance || after.AcceptedBytes < before.AcceptedBytes)
            throw new InvalidDataException("Checkpoint health or identity changed; no retry is permitted.");
        if (after.DirtyBytes == 0 && after.InFlightBytes == 0) return false;
        if (before.UnsafeDefer && after.UnsafeDefer && orderBefore is { Mode: not 0 } a && orderAfter is { } b &&
            b.Mode == a.Mode && b.DrainingEpoch >= a.CurrentEpoch)
            return false;
        if (after.AcceptedBytes == before.AcceptedBytes)
            throw new InvalidDataException("Flush left pending work without new write admission; the boundary is not complete.");
        return true;
    }

    public static void Validate(DurabilityOracle oracle, DurabilityResult result)
    {
        DurabilityOracleContract.Validate(oracle);
        var sectors = (long)oracle.SetMiB * (1 << 20) / 4096;
        static bool Exact(SectorCounts counts, long expected) => counts.Expected == expected && counts.New == expected &&
            counts.Old == 0 && counts.Other == 0 && counts.Missing == 0;
        if (!oracle.DurableCheckpoint || sectors <= 0 || oracle.SmallFiles <= 0 ||
            !Exact(result.Flushed, sectors) || !Exact(result.Unflushed, sectors) ||
            result.SmallFilesExpected != oracle.SmallFiles || result.SmallFilesIntact != oracle.SmallFiles ||
            result.SmallFilesDamaged != 0 || result.SmallFilesMissing != 0 || result.ChkdskExitCode != 0)
            throw new InvalidDataException("Data or filesystem damage followed the explicit durable checkpoint; inspect the retained byte counts and scan.");
    }
}
