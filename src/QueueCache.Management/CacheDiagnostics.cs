using System.Buffers.Binary;

namespace QueueCache.Management;

public sealed record CacheAttribution(ulong LowerReadAttempts, ulong LowerWriteAttempts, ulong LowerFlushAttempts,
    ulong ControlBarriers, ulong StrictWriteBarriers, ulong DisabledWriteBarriers, ulong QuotaWriteBarriers,
    ulong ApplicationBarriers, ulong ShutdownBarriers, ulong PowerBarriers, ulong OrderedBarriers, ulong RemoveBarriers,
    ulong LastReason, ulong LastMajor, ulong LastCode, ulong LastOffset, ulong LastLength);

/// <summary>Lifetime request counters; deferred flushes are not durable flush completions.</summary>
public sealed record CacheDiagnostics(ulong ApplicationFlushes, ulong DeferredFlushes, ulong WriteThroughWrites,
    ulong DeferredWriteThroughWrites, ulong ControlBarriers, ulong OtherBarriers, ulong ShutdownBarriers,
    ulong PowerBarriers, ulong LastBarrierCode)
{
    public const int WireSize = 80;
    public const int AttributionWireSize = 216;
    public CacheAttribution? Attribution { get; init; }
    public static CacheDiagnostics Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is not (WireSize or AttributionWireSize) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes) != (bytes.Length == WireSize ? 1u : 2u) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != bytes.Length)
            throw new InvalidDataException("Unsupported diagnostics version/size.");
        var values = new ulong[9];
        for (var i = 0; i < values.Length; i++)
            values[i] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(8 + i * 8)..]);
        if (values[1] > values[0] || values[3] > values[2])
            throw new InvalidDataException("Inconsistent deferred request counters.");
        CacheAttribution? attribution = null;
        if (bytes.Length == AttributionWireSize)
        {
            var extra = new ulong[17];
            for (var index = 0; index < extra.Length; index++)
                extra[index] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(WireSize + index * 8)..]);
            if (extra[12] > 9)
                throw new InvalidDataException("Unknown barrier reason.");
            attribution = new(extra[0], extra[1], extra[2], extra[3], extra[4], extra[5], extra[6],
                extra[7], extra[8], extra[9], extra[10], extra[11], extra[12], extra[13], extra[14], extra[15], extra[16]);
        }
        return new(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7], values[8])
        {
            Attribution = attribution
        };
    }
}
