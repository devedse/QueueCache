using System.Diagnostics;

namespace QueueCache.Developer.Verification;

public static class TelemetryCoverage
{
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
