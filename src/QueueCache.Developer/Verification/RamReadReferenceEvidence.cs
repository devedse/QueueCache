using QueueCache.Management;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Developer.Verification;

public sealed record RamReadReferenceBoundary(DateTimeOffset Utc, Guid ResourceId, Guid BootEpoch,
    ulong CreationGeneration, ulong WriteGeneration, ManagedDiskState State, bool CacheEnabled,
    bool Timing, RamDirectState Direct, ulong ProviderReadRequests, ulong ImageReadAttempts, ulong ImageWriteAttempts,
    string? Error, ulong? ProviderWriteRequests = null, ulong? ProviderWriteBytes = null);

/// <summary>Lightweight native observations; broker state and image counters are
/// separately required at both boundaries, never synthesized in a sample.</summary>
public sealed record RamReadReferenceSample(DateTimeOffset Utc, RamDiskSnapshot Native,
    RamDiskStatistics Statistics, bool CacheEnabled, RamDirectState Direct);

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
    public static void Validate(RamReadReferenceCase scenario, IReadOnlyList<RamReadReferenceSample> samples,
        RamReadReferenceBoundary before, RamReadReferenceBoundary after, DiskSpdScore score,
        DateTimeOffset start, DateTimeOffset end)
    {
        TelemetryCoverage.Validate(samples.Select(s => s.Utc).ToArray(), start, end);
        foreach (var sample in new[] { before, after })
        {
            if (sample.ResourceId == Guid.Empty || sample.BootEpoch == Guid.Empty || sample.CreationGeneration == 0 ||
                sample.ResourceId != before.ResourceId || sample.BootEpoch != before.BootEpoch ||
                sample.CreationGeneration != before.CreationGeneration || sample.WriteGeneration < before.WriteGeneration ||
                sample.State != ManagedDiskState.Ready || sample.CacheEnabled || sample.Timing || sample.Error is not null ||
                sample.ProviderWriteRequests is null || sample.ProviderWriteBytes is null ||
                sample.ImageReadAttempts != 0 || sample.ImageWriteAttempts != 0 ||
                (scenario.Access == RamAccess.Direct ? !sample.Direct.Full || sample.Direct.ResourceId != sample.ResourceId : sample.Direct.Access != RamDirectAccess.None))
                throw new InvalidDataException("RAM reference identity, access, residency or health changed during the workload.");
        }
        foreach (var sample in samples)
        {
            var native = sample.Native;
            if (native.ResourceId != before.ResourceId || native.BootEpoch != before.BootEpoch ||
                native.CreationGeneration != before.CreationGeneration || native.WriteGeneration < before.WriteGeneration ||
                native.CapacityBytes != RamReadReferencePlan.DiskMiB * (1UL << 20) ||
                native.Slot != samples[0].Native.Slot || native.SectorBytes != samples[0].Native.SectorBytes ||
                native.ReservedBytes != samples[0].Native.ReservedBytes || native.Errors != 0 || native.FreezeOwner != Guid.Empty ||
                (native.Flags & (RamDiskFlags.Published | RamDiskFlags.ReadOnly | RamDiskFlags.Frozen | RamDiskFlags.Timing)) != RamDiskFlags.Published ||
                sample.CacheEnabled || sample.Statistics.Frequency == 0 ||
                (scenario.Access == RamAccess.Direct ? !sample.Direct.Full || sample.Direct.ResourceId != native.ResourceId ||
                    !native.Flags.HasFlag(RamDiskFlags.DirectRegistered) : sample.Direct.Access != RamDirectAccess.None ||
                    native.Flags.HasFlag(RamDiskFlags.DirectRegistered)))
                throw new InvalidDataException("RAM reference native identity, access, residency or health changed during the workload.");
        }
        // Image I/O and broker lifecycle remain boundary observations. Native
        // samples provide actual native counters, rather than invented image zeros.
        var ordered = samples.Select(s => (s.Native.WriteGeneration, ProviderWriteRequests: s.Statistics.WriteRequests,
            ProviderWriteBytes: s.Native.WriteBytes, DirectWriteBytes: s.Direct.WriteBytes,
            ProviderReadRequests: s.Statistics.ReadRequests, DirectReadBytes: s.Direct.ReadBytes))
            .Prepend((before.WriteGeneration, ProviderWriteRequests: before.ProviderWriteRequests!.Value,
                ProviderWriteBytes: before.ProviderWriteBytes!.Value, DirectWriteBytes: before.Direct.WriteBytes,
                before.ProviderReadRequests, DirectReadBytes: before.Direct.ReadBytes))
            .Append((after.WriteGeneration, ProviderWriteRequests: after.ProviderWriteRequests!.Value,
                ProviderWriteBytes: after.ProviderWriteBytes!.Value, DirectWriteBytes: after.Direct.WriteBytes,
                after.ProviderReadRequests, DirectReadBytes: after.Direct.ReadBytes)).ToArray();
        if (ordered.Zip(ordered.Skip(1)).Any(p => p.Second.WriteGeneration < p.First.WriteGeneration ||
            p.Second.ProviderWriteRequests < p.First.ProviderWriteRequests || p.Second.ProviderWriteBytes < p.First.ProviderWriteBytes ||
            p.Second.DirectWriteBytes < p.First.DirectWriteBytes || p.Second.ProviderReadRequests < p.First.ProviderReadRequests ||
            p.Second.DirectReadBytes < p.First.DirectReadBytes))
            throw new InvalidDataException("RAM reference content counters decreased during the workload.");
        if (score.Bytes <= 0 || score.Operations <= 0 || score.Bytes != checked(score.Operations * scenario.BlockKiB * 1024L))
            throw new InvalidDataException("RAM reference has missing completions or wrong transfer size.");
        if (after.Direct.ReadBytes < before.Direct.ReadBytes || after.ProviderReadRequests < before.ProviderReadRequests ||
            (scenario.Access == RamAccess.Direct ? after.Direct.ReadBytes - before.Direct.ReadBytes < (ulong)score.Bytes :
                after.ProviderReadRequests - before.ProviderReadRequests < (ulong)score.Operations))
            throw new InvalidDataException("RAM reference traffic was not accounted by the selected access path.");
    }
}
