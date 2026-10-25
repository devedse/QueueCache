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

[SupportedOSPlatform("windows")]
public static class DurableCheckpoint
{
    public static DurableCheckpointResult Complete(char letter, CacheDevice cache, string failureEvidence)
    {
        var start = DateTimeOffset.UtcNow;
        var attempts = new List<DurableCheckpointAttempt>();
        try
        {
            ComparisonScenarios.FlushVolume(letter);
            for (var attempt = 0; attempt < DurabilityCheckpointEvidence.MaximumFlushAttempts; attempt++)
            {
                var before = cache.GetWriteCacheState(); var diagBefore = cache.GetDiagnostics();
                var callStart = DateTimeOffset.UtcNow;
                cache.Control(WriteCacheAction.Flush);
                var after = cache.GetWriteCacheState(); var diagAfter = cache.GetDiagnostics();
                attempts.Add(new(callStart, DateTimeOffset.UtcNow, before, after, diagBefore, diagAfter));
                if (diagAfter.ControlBarriers <= diagBefore.ControlBarriers)
                    throw new InvalidDataException("No completed control barrier was recorded.");
                if (!DurabilityCheckpointEvidence.NeedsAnotherFlush(before, after))
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
