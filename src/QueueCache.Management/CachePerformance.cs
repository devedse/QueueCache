using System.Buffers.Binary;

namespace QueueCache.Management;

/// <summary>Independent lifetime performance sample. Durations use QPC Frequency;
/// detailed lock/lower-I/O timings only accumulate while timing is enabled.</summary>
public sealed record CachePerformance(ulong Frequency, ulong TimingEnabled, ulong QueueDepth, ulong OldestQueuedTicks, ulong QueuedRequests, ulong QueueWaitTicks, ulong MaxQueueWaitTicks, ulong ActiveMajor, ulong Phase, ulong ActiveAgeTicks, ulong CapacityWaits, ulong CapacityWaitTicks, ulong BypassReads, ulong BypassMisses, ulong LockAcquires, ulong LockWaitTicks, ulong LockHoldTicks, ulong MaxLockWaitTicks, ulong MaxLockHoldTicks, ulong DrainBatches, ulong DrainBytes, ulong WakeSignals, ulong LowerIoTicks)
{
    public const int WireSize = 192;
    public double Milliseconds(ulong ticks) => 1000.0 * ticks / Frequency;
    public string PhaseName => Phase switch {
        0 => "Idle", 1 => "Processing request", 2 => "Waiting for write capacity",
        3 => "Draining pending writes", 4 => "Waiting for disk flush", 5 => "Waiting for disk read", _ => "Unknown"
    };
    public static CachePerformance Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length != WireSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != WireSize)
            throw new InvalidDataException("Unsupported performance snapshot.");
        var v = new ulong[23];
        for (int i = 0; i < v.Length; ++i) v[i] = BinaryPrimitives.ReadUInt64LittleEndian(data[(8 + i * 8)..]);
        if (v[0] == 0 || v[1] > 1 || v[8] > 5) throw new InvalidDataException("Invalid performance snapshot.");
        return new(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15], v[16], v[17], v[18], v[19], v[20], v[21], v[22]);
    }
}
