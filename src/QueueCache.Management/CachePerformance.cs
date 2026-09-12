using System.Buffers.Binary;

namespace QueueCache.Management;

/// <summary>Independent lifetime performance sample. Durations use QPC Frequency;
/// detailed lock/lower-I/O timings only accumulate while timing is enabled.</summary>
public sealed record CachePerformance(ulong Frequency, ulong TimingEnabled, ulong QueueDepth, ulong OldestQueuedTicks, ulong QueuedRequests, ulong QueueWaitTicks, ulong MaxQueueWaitTicks, ulong ActiveMajor, ulong Phase, ulong ActiveAgeTicks, ulong CapacityWaits, ulong CapacityWaitTicks, ulong BypassReads, ulong BypassMisses, ulong LockAcquires, ulong LockWaitTicks, ulong LockHoldTicks, ulong MaxLockWaitTicks, ulong MaxLockHoldTicks, ulong DrainBatches, ulong DrainBytes, ulong WakeSignals, ulong LowerIoTicks,
    ulong ServiceReadCalls = 0, ulong ServiceReadAttempts = 0, ulong ServiceReadCompletions = 0, ulong ServiceReadNoCandidate = 0,
    ulong SelectionRejectNotRead = 0, ulong SelectionRejectAfterSequence = 0, ulong SelectionRejectMaxSize = 0,
    ulong SelectionRejectActiveOverlap = 0, ulong SelectionRejectOlderWriteOverlap = 0, ulong SelectionRejectFence = 0,
    ulong SelectionScanLimit = 0, ulong ServiceReadBudgetExhausted = 0, ulong ServiceReadMisses = 0,
    ulong WaitChanged = 0, ulong WaitRequestAvailable = 0, ulong WaitTimeout = 0, ulong WaitLowerCompleted = 0,
    ulong LastBlockedMajor = 0, ulong LastBlockedOffset = 0, ulong LastBlockedLength = 0,
    ulong LastSelectionMajor = 0, ulong LastSelectionCode = 0, ulong LastSelectionOffset = 0, ulong LastSelectionLength = 0,
    ulong LastSelectionSequence = 0, ulong LastAfterSequence = 0, ulong LastSelectionScanned = 0)
{
    public const int LegacyWireSize = 192;
    public const int WireSize = 408;
    public double Milliseconds(ulong ticks) => 1000.0 * ticks / Frequency;
    public string PhaseName => Phase switch {
        0 => "Idle", 1 => "Processing request", 2 => "Waiting for write capacity",
        3 => "Draining pending writes", 4 => "Waiting for disk flush", 5 => "Waiting for disk read", _ => "Unknown"
    };
    public static CachePerformance Decode(ReadOnlySpan<byte> data)
    {
        var version = data.Length >= 8 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : 0;
        var size = data.Length >= 8 ? BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) : 0;
        if ((version != 1 || data.Length != LegacyWireSize || size != LegacyWireSize) &&
            (version != 2 || data.Length != WireSize || size != WireSize))
            throw new InvalidDataException("Unsupported performance snapshot.");
        var v = new ulong[(data.Length - 8) / 8];
        for (int i = 0; i < v.Length; ++i) v[i] = BinaryPrimitives.ReadUInt64LittleEndian(data[(8 + i * 8)..]);
        if (v[0] == 0 || v[1] > 1 || v[8] > 5) throw new InvalidDataException("Invalid performance snapshot.");
        Array.Resize(ref v, 50);
        return new(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15], v[16], v[17], v[18], v[19], v[20], v[21], v[22],
            v[23], v[24], v[25], v[26], v[27], v[28], v[29], v[30], v[31], v[32], v[33], v[34], v[35], v[36], v[37], v[38], v[39], v[40], v[41], v[42], v[43], v[44], v[45], v[46], v[47], v[48], v[49]);
    }
}
