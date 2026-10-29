using System.Diagnostics;
using System.Text.Json;

namespace QueueCache.Developer.Verification;

public sealed record HeartbeatSample(DateTimeOffset Utc, TelemetryClock Clock, double? PreviousWaitMilliseconds);

/// <summary>A separate owned process measures scheduling through the final control and cleanup,
/// with filesystem polling and persistence off the sampling thread.</summary>
public static class ProgressHeartbeat
{
    public const int MaximumSeconds = 14400;
    public static void ValidateDuration(int seconds)
    {
        if (seconds is <= 0 or > MaximumSeconds) throw new ArgumentOutOfRangeException(nameof(seconds));
    }
    public static async Task RecordAsync(string reply, string stopFile, string readyFile, int seconds)
    {
        ValidateDuration(seconds);
        var stop = new TelemetryStopSignal(() => File.Exists(stopFile));
        var ready = false;
        await using var writer = new StreamWriter(reply, append: false);
        await TelemetryRecorder.RecordAsync<HeartbeatSample>(delay =>
                new(new(DateTimeOffset.UtcNow, TelemetryClock.Capture(), delay), stop.Requested),
            async (sample, token) =>
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(sample).AsMemory(), token);
                await writer.FlushAsync(token);
                if (!ready) RunStorage.AtomicJson(readyFile, new { Ready = true });
                ready = true;
                stop.Poll();
            }, TimeSpan.FromSeconds(seconds), TimeSpan.FromMilliseconds(100));
    }

    public static double Validate(IReadOnlyList<HeartbeatSample> samples, TelemetryClock begin, TelemetryClock end)
    {
        TelemetryCoverage.Validate(samples.Select(s => (TelemetryClock?)s.Clock).ToArray(), begin, end);
        return samples.Zip(samples.Skip(1), (a, b) =>
            ((double)b.Clock.Timestamp - a.Clock.Timestamp) / a.Clock.Frequency).Max();
    }
}
