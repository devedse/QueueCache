using System.Buffers.Binary;

namespace QueueCache.Management;

/// <summary>Versioned, coherent write-cache snapshot. Dirty includes in-flight payload, not unique disk blocks.</summary>
public sealed record WriteCacheState(uint Flags, int LastError, ulong DeviceBytes, ulong BudgetBytes,
    ulong ReservedBytes, ulong DirtyBytes, ulong InFlightBytes, ulong PayloadCapacity, ulong OccupiedSlots,
    ulong AcceptedBytes, ulong DrainedBytes, ulong ThrottleWaits, ulong CacheReadBytes, ulong Errors,
    ulong Flushes, ulong PeakDirtyBytes)
{
    public const int WireSize = 128;
    public const int ExtendedWireSize = 160;
    public const int ReadWriteWireSize = 288;
    public CacheOptions? Options { get; init; }
    public ulong CleanReadBytes { get; init; }
    public ulong CleanWriteBytes { get; init; }
    public ulong ReadHitBytes { get; init; }
    public ulong ReadMissBytes { get; init; }
    public ulong Evictions { get; init; }
    public ulong OldestDirtyMs { get; init; }
    public ulong Generation { get; init; }
    public ulong Instance { get; init; }
    public ulong GlobalLimitBytes { get; init; }
    public ulong GlobalReservedBytes { get; init; }
    public bool SupportsReadWrite => (Flags & 256) != 0;
    public bool RoutingConfirmed => SupportsReadWrite && (Flags & 512) != 0;
    public bool Operational => Enabled && !Faulted && LastError == 0 && !Removed && !Suspended && !Draining &&
        BudgetBytes > 0 && ReservedBytes > 0 && PayloadCapacity > 0 && (!SupportsReadWrite || RoutingConfirmed);
    public string RuntimeStatus => Removed ? "Removed" : Faulted || LastError != 0 ? "Faulted" : Suspended ? "Suspended" :
        Draining ? "Draining" : Operational ? (SupportsReadWrite ? "Active" : "Active (legacy driver)") :
        Enabled ? "State mismatch" : BudgetBytes > 0 ? "Paused" : "Available";
    public ulong FreeBytes => PayloadCapacity - Math.Min(PayloadCapacity, DirtyBytes + CleanReadBytes + CleanWriteBytes);
    public double ReadHitPercent => ReadHitBytes + ReadMissBytes == 0 ? 0 : 100.0 * ReadHitBytes / (ReadHitBytes + ReadMissBytes);
    public static WriteCacheState DecodeReadWrite(ReadOnlySpan<byte> data)
    {
        if (data.Length != ReadWriteWireSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != 3 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != ReadWriteWireSize) throw new InvalidDataException("Unsupported read/write cache state.");
        Span<byte> legacy = stackalloc byte[ExtendedWireSize]; data[..ExtendedWireSize].CopyTo(legacy);
        BinaryPrimitives.WriteUInt32LittleEndian(legacy, 2); BinaryPrimitives.WriteUInt32LittleEndian(legacy[4..], ExtendedWireSize);
        var v = new ulong[10];
        for (int i = 0; i < v.Length; i++) v[i] = BinaryPrimitives.ReadUInt64LittleEndian(data[(208 + i * 8)..]);
        var state = DecodeExtended(legacy) with { Options = CacheOptions.Decode(data.Slice(160, 48)), CleanReadBytes = v[0],
            CleanWriteBytes = v[1], ReadHitBytes = v[2], ReadMissBytes = v[3], Evictions = v[4], OldestDirtyMs = v[5],
            Generation = v[6], Instance = v[7], GlobalLimitBytes = v[8], GlobalReservedBytes = v[9] };
        if (!state.SupportsReadWrite || state.Instance == 0 || state.CleanReadBytes > state.PayloadCapacity - state.DirtyBytes ||
            state.CleanWriteBytes > state.PayloadCapacity - state.DirtyBytes - state.CleanReadBytes)
            throw new InvalidDataException("Inconsistent read/write cache snapshot.");
        return state;
    }
    public ulong DiscardedBytes { get; init; }
    public ulong LowerWrites { get; init; }
    public ulong BatchedWrites { get; init; }
    public ulong TrimRequests { get; init; }
    public bool ExtendedCountersAvailable { get; init; }
    public bool Enabled => (Flags & 1) != 0;
    public bool Faulted => (Flags & 2) != 0;
    public bool Suspended => (Flags & 4) != 0;
    public bool Draining => (Flags & 8) != 0;
    public bool Removed => (Flags & 16) != 0;
    public bool UnsafeDefer => (Flags & 32) != 0;
    public bool SupportsRelease => (Flags & 128) != 0;
    public string FlushPolicy => UnsafeDefer ? "UNSAFE-DEFER" : "STRICT";
    /// <summary>Accepted bytes superseded in RAM, not bytes written to the lower disk.</summary>
    public ulong CoalescedBytes => AcceptedBytes >= DrainedBytes && AcceptedBytes - DrainedBytes >= DirtyBytes &&
        AcceptedBytes - DrainedBytes - DirtyBytes >= DiscardedBytes
        ? AcceptedBytes - DrainedBytes - DirtyBytes - DiscardedBytes : 0;
    public static WriteCacheState DecodeExtended(ReadOnlySpan<byte> data)
    {
        if (data.Length != ExtendedWireSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != ExtendedWireSize)
            throw new InvalidDataException("Unsupported extended state version/size.");
        Span<byte> legacy = stackalloc byte[WireSize]; data[..WireSize].CopyTo(legacy);
        BinaryPrimitives.WriteUInt32LittleEndian(legacy, 1); BinaryPrimitives.WriteUInt32LittleEndian(legacy[4..], WireSize);
        return Decode(legacy) with
        {
            DiscardedBytes = BinaryPrimitives.ReadUInt64LittleEndian(data[128..]),
            LowerWrites = BinaryPrimitives.ReadUInt64LittleEndian(data[136..]),
            BatchedWrites = BinaryPrimitives.ReadUInt64LittleEndian(data[144..]),
            TrimRequests = BinaryPrimitives.ReadUInt64LittleEndian(data[152..]), ExtendedCountersAvailable = true
        };
    }
    public static WriteCacheState Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length != WireSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != WireSize)
            throw new InvalidDataException("Unsupported write-cache snapshot version/size.");
        var values = new ulong[14];
        for (var i = 0; i < values.Length; i++) values[i] = BinaryPrimitives.ReadUInt64LittleEndian(data[(16 + i * 8)..]);
        var result = new WriteCacheState(BinaryPrimitives.ReadUInt32LittleEndian(data[8..]), BinaryPrimitives.ReadInt32LittleEndian(data[12..]),
            values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7], values[8], values[9], values[10], values[11], values[12], values[13]);
        if (result.ReservedBytes > result.BudgetBytes || result.DirtyBytes > result.PayloadCapacity || result.InFlightBytes > result.DirtyBytes)
            throw new InvalidDataException("Driver reported inconsistent write-cache bounds.");
        return result;
    }
}
public enum WriteCacheAction : uint { Configure = 1, Enable, Flush, Disable, Retry, LabDelay, LabFault, FlushPolicy, Release }
