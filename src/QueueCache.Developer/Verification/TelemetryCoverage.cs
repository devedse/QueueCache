using System.Diagnostics;

namespace QueueCache.Developer.Verification;

/// <summary>Stopwatch/QPC has one time base across processes on this Windows boot.
/// Retain the frequency with every observation; UTC remains separate reporting evidence.</summary>
public sealed record TelemetryClock(long Timestamp, long Frequency)
{
    public static TelemetryClock Capture() => new(Stopwatch.GetTimestamp(), Stopwatch.Frequency);
}

public static class TelemetryCoverage
{
    /// <summary>Prove actual sampling and process coverage without treating a wall-clock
    /// adjustment as a missing sample. Missing clocks never fall back to UTC or zero.</summary>
    public static void Validate(IReadOnlyList<TelemetryClock?> samples, TelemetryClock start,
        TelemetryClock end, double maximumGapSeconds = 2)
    {
        if (!double.IsFinite(maximumGapSeconds) || maximumGapSeconds <= 0 || start.Timestamp <= 0 ||
            start.Frequency <= 0 || end.Frequency != start.Frequency || end.Timestamp < start.Timestamp ||
            samples.Count < 2 || samples.Any(s => s is null || s.Timestamp <= 0 || s.Frequency != start.Frequency))
            throw new InvalidDataException("Missing or inconsistent monotonic telemetry clocks.");
        if (samples[0]!.Timestamp > start.Timestamp || samples[^1]!.Timestamp < end.Timestamp)
            throw new InvalidDataException("Telemetry does not cover the complete monotonic workload interval.");
        for (var i = 1; i < samples.Count; i++)
        {
            var gap = ((decimal)samples[i]!.Timestamp - samples[i - 1]!.Timestamp) / start.Frequency;
            if (gap <= 0 || gap > (decimal)maximumGapSeconds)
                throw new InvalidDataException($"Monotonic telemetry timestamps are out of order or have a gap over {maximumGapSeconds} seconds ({gap:F3}s).");
        }
    }

    public static async Task WaitReadyAsync(string readyPath, Task observer, TimeSpan timeout, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(readyPath))
        {
            token.ThrowIfCancellationRequested();
            if (observer.IsCompleted)
            {
                await observer;
                throw new IOException("Telemetry exited before becoming ready: " + readyPath);
            }
            if (timer.Elapsed >= timeout)
                throw new TimeoutException("Telemetry did not become ready before workloads: " + readyPath);
            await Task.Delay(50, token);
        }
        if (observer.IsCompleted)
        {
            await observer;
            throw new IOException("Telemetry exited before workloads started.");
        }
    }

    /// <summary>
    /// Proves that ordered telemetry samples cover the complete workload process
    /// interval without a gap large enough to hide a transient failure.
    /// </summary>
    // The enclosing interval deliberately includes DiskSpd startup and teardown.
    public static void Validate(IReadOnlyList<DateTimeOffset> samples, DateTimeOffset start, DateTimeOffset end)
    {
        if (end < start || samples.Count < 2 || samples[0] > start || samples[^1] < end)
            throw new InvalidDataException("Telemetry does not cover the complete workload interval.");
        for (var i = 1; i < samples.Count; i++)
        {
            var gap = samples[i] - samples[i - 1];
            if (gap <= TimeSpan.Zero || gap > TimeSpan.FromSeconds(2))
                throw new InvalidDataException($"Telemetry timestamps are out of order or have a gap over 2 seconds ({gap.TotalSeconds:F3}s).");
        }
    }
}
