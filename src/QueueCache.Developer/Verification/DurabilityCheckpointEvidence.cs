namespace QueueCache.Developer.Verification;

/// <summary>Post-checkpoint cuts require all bytes; ordinary volatile cuts retain their classification-only contract.</summary>
public static class DurabilityCheckpointEvidence
{
    public static void Validate(DurabilityOracle oracle, DurabilityResult result)
    {
        var sectors = (long)oracle.SetMiB * (1 << 20) / 4096;
        static bool Exact(SectorCounts counts, long expected) => counts.Expected == expected && counts.New == expected &&
            counts.Old == 0 && counts.Other == 0 && counts.Missing == 0;
        if (oracle.SchemaVersion != 3 || !oracle.DurableCheckpoint || sectors <= 0 || oracle.SmallFiles <= 0 ||
            !Exact(result.Flushed, sectors) || !Exact(result.Unflushed, sectors) ||
            result.SmallFilesExpected != oracle.SmallFiles || result.SmallFilesIntact != oracle.SmallFiles ||
            result.SmallFilesDamaged != 0 || result.SmallFilesMissing != 0 || result.ChkdskExitCode != 0)
            throw new InvalidDataException("Data or filesystem damage followed the explicit durable checkpoint; inspect the retained byte counts and scan.");
    }
}
