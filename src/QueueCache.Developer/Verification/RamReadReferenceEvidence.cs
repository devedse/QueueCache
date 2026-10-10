using QueueCache.Management;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Developer.Verification;

public sealed record RamReadReferenceBoundary(DateTimeOffset Utc, Guid ResourceId, Guid BootEpoch,
    ulong CreationGeneration, ulong WriteGeneration, ManagedDiskState State, bool CacheEnabled,
    bool Timing, RamDirectState Direct, ulong ProviderReadRequests, ulong ImageReadAttempts, ulong ImageWriteAttempts,
    string? Error);

/// <summary>These checks establish collection, residency and lifecycle, never speed acceptance.</summary>
public static class RamReadReferenceEvidence
{
    public static void ValidateOwnedDefinition(ManagedDiskDefinition expected, ManagedDiskDefinition current,
        IReadOnlyList<Guid> originalResources)
    {
        if (expected.ResourceId == Guid.Empty || originalResources.Contains(expected.ResourceId) || expected != current ||
            expected.Mode != ManagedDiskMode.EphemeralRam || expected.CapacityBytes != RamReadReferencePlan.DiskMiB * (1UL << 20) ||
            expected.StartAtBoot || expected.ImagePath is not null || expected.Cache is not null || !expected.Label.StartsWith("QC-ReadRef-", StringComparison.Ordinal))
            throw new InvalidDataException("RAM reference cleanup refuses an ownership/definition mismatch.");
    }
    public static void ValidateChecks(IReadOnlyList<RamReadReferenceCase> expected, IReadOnlyList<QueueCache.Operations.CheckResult> checks)
    {
        if (checks.Count != expected.Count || checks.Select(c => c.Name).Distinct().Count() != checks.Count ||
            expected.Any(e => !checks.Any(c => c.Name == e.Id && c.Result == "PASS")))
            throw new InvalidDataException("RAM reference results do not cover every expected measurement window.");
    }
    public static void Validate(RamReadReferenceCase scenario, IReadOnlyList<RamReadReferenceBoundary> samples,
        RamReadReferenceBoundary before, RamReadReferenceBoundary after, DiskSpdScore score,
        DateTimeOffset start, DateTimeOffset end)
    {
        TelemetryCoverage.Validate(samples.Select(s => s.Utc).ToArray(), start, end);
        foreach (var sample in samples.Append(before).Append(after))
        {
            if (sample.ResourceId == Guid.Empty || sample.BootEpoch == Guid.Empty || sample.CreationGeneration == 0 ||
                sample.ResourceId != before.ResourceId || sample.BootEpoch != before.BootEpoch ||
                sample.CreationGeneration != before.CreationGeneration || sample.WriteGeneration != before.WriteGeneration ||
                sample.State != ManagedDiskState.Ready || sample.CacheEnabled || sample.Timing || sample.Error is not null ||
                sample.ImageReadAttempts != 0 || sample.ImageWriteAttempts != 0 ||
                (scenario.Access == RamAccess.Direct ? !sample.Direct.Full || sample.Direct.ResourceId != sample.ResourceId : sample.Direct.Access != RamDirectAccess.None))
                throw new InvalidDataException("RAM reference identity, access, residency or health changed during the workload.");
        }
        if (score.Bytes <= 0 || score.Operations <= 0 || score.Bytes != checked(score.Operations * scenario.BlockKiB * 1024L))
            throw new InvalidDataException("RAM reference has missing completions or wrong transfer size.");
        if (after.Direct.ReadBytes < before.Direct.ReadBytes || after.ProviderReadRequests < before.ProviderReadRequests ||
            (scenario.Access == RamAccess.Direct ? after.Direct.ReadBytes - before.Direct.ReadBytes < (ulong)score.Bytes :
                after.ProviderReadRequests - before.ProviderReadRequests < (ulong)score.Operations))
            throw new InvalidDataException("RAM reference traffic was not accounted by the selected access path.");
    }
}
