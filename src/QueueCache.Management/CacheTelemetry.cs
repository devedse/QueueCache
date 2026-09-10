namespace QueueCache.Management;

/// <summary>UI-independent derived counters. Drained means lower-device completion, not a durability guarantee.</summary>
public sealed record CacheTelemetry(double FillFraction, double AcceptedMiBPerSecond, double DrainedMiBPerSecond,
    ulong NewThrottleWaits, bool CountersReset)
{
    public double ReadMiBPerSecond { get; init; }
    public static CacheTelemetry Between(WriteCacheState previous, WriteCacheState current, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        var reset = current.Instance != previous.Instance || current.Generation != previous.Generation || current.AcceptedBytes < previous.AcceptedBytes || current.DrainedBytes < previous.DrainedBytes ||
            current.ThrottleWaits < previous.ThrottleWaits || current.ReadHitBytes < previous.ReadHitBytes || current.ReadMissBytes < previous.ReadMissBytes;
        var scale = elapsed.TotalSeconds * 1048576;
        return new(current.PayloadCapacity == 0 ? 0 : Math.Clamp((double)current.DirtyBytes / current.PayloadCapacity, 0, 1),
            reset ? 0 : (current.AcceptedBytes - previous.AcceptedBytes) / scale,
            reset ? 0 : (current.DrainedBytes - previous.DrainedBytes) / scale,
            reset ? 0 : current.ThrottleWaits - previous.ThrottleWaits, reset)
        { ReadMiBPerSecond = reset ? 0 : ((double)(current.ReadHitBytes - previous.ReadHitBytes) + (current.ReadMissBytes - previous.ReadMissBytes)) / scale };
    }
}
