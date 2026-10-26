using System.Runtime.Versioning;
using QueueCache.Management;

namespace QueueCache.Developer.Verification;

public sealed record DurableCheckpointAttempt(DateTimeOffset Start, DateTimeOffset End,
    WriteCacheState Before, WriteCacheState After, CacheDiagnostics DiagnosticsBefore, CacheDiagnostics DiagnosticsAfter);
public sealed record DurableCheckpointResult(DateTimeOffset Start, DateTimeOffset End,
    IReadOnlyList<DurableCheckpointAttempt> Attempts)
{
    public DurableCheckpointAttempt Final => Attempts[^1];
}

public static class DurableCheckpoint
{
    [SupportedOSPlatform("windows")]
    public static DurableCheckpointResult Complete(char letter, CacheDevice cache, string failureEvidence)
        => Complete(() => ComparisonScenarios.FlushVolume(letter), () =>
        {
            var before = cache.GetWriteCacheState(); var diagBefore = cache.GetDiagnostics();
            var callStart = DateTimeOffset.UtcNow;
            cache.Control(WriteCacheAction.Flush);
            var after = cache.GetWriteCacheState(); var diagAfter = cache.GetDiagnostics();
            return new(callStart, DateTimeOffset.UtcNow, before, after, diagBefore, diagAfter);
        }, failureEvidence);

    // The same bounded protocol is host-testable without issuing volume or driver controls.
    public static DurableCheckpointResult Complete(Action flushFilesystem,
        Func<DurableCheckpointAttempt> flushCache, string failureEvidence)
    {
        var start = DateTimeOffset.UtcNow;
        var attempts = new List<DurableCheckpointAttempt>();
        try
        {
            for (var attempt = 0; attempt < DurabilityCheckpointEvidence.MaximumFlushAttempts; attempt++)
            {
                // NTFS can dirty more metadata during a long cache drain. Each retry must
                // push that filesystem work down before asking the cache for its boundary.
                flushFilesystem();
                var observed = flushCache();
                attempts.Add(observed);
                if (observed.DiagnosticsAfter.ControlBarriers <= observed.DiagnosticsBefore.ControlBarriers)
                    throw new InvalidDataException("No completed control barrier was recorded.");
                if (!DurabilityCheckpointEvidence.NeedsAnotherFlush(observed.Before, observed.After))
                    return new(start, DateTimeOffset.UtcNow, attempts);
            }
            throw new InvalidDataException("New writes kept arriving across three flushes; the VM is not cut-ready.");
        }
        catch (Exception ex)
        {
            RunStorage.AtomicJson(failureEvidence, new { Start = start, End = DateTimeOffset.UtcNow, Attempts = attempts, Failure = ex.Message });
            throw;
        }
    }
}
