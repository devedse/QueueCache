using System.Text.Json;

namespace QueueCache.Operations;

/// <summary>Existing checkpoint observations, retained before the scenario restores its cache.</summary>
public sealed record PressureWatermarkCounters(ulong DirtyBytes, ulong InFlightBytes,
    ulong AcceptedBytes, ulong DrainedBytes, ulong DrainWriteAttempts, ulong LowerWriteAttempts,
    ulong LowerFlushAttempts, ulong ApplicationFlushes, ulong WriteThroughWrites,
    ulong Errors, int LastError, ulong? OrderingMode);

public sealed record PressureWatermarkEvidence(ulong HighBytes, ulong ExpectedFileBytes,
    PressureWatermarkCounters Before, PressureWatermarkCounters Below)
{
    // Missing/reset counters stay unknown rather than wrapping or becoming zero.
    public ulong? AcceptedDelta => Below.AcceptedBytes >= Before.AcceptedBytes
        ? Below.AcceptedBytes - Before.AcceptedBytes : null;
    public ulong? AcceptedBeyondFileBytes => AcceptedDelta is { } accepted && accepted >= ExpectedFileBytes
        ? accepted - ExpectedFileBytes : null;

    public void ValidateBelow()
    {
        var reasons = new List<string>();
        if (Below.DirtyBytes >= HighBytes)
            reasons.Add("actual dirty bytes reached the high watermark");
        if (Below.DrainedBytes != Before.DrainedBytes)
            reasons.Add("completed drain bytes changed");
        if (Below.DrainWriteAttempts != Before.DrainWriteAttempts)
            reasons.Add("lower drain attempts changed");
        if (reasons.Count > 0)
            throw new InvalidDataException("Below-watermark checkpoint failed: " + string.Join("; ", reasons) +
                ". Evidence: " + JsonSerializer.Serialize(this));
    }
}
