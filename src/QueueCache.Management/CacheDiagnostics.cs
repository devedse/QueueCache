using System.Buffers.Binary;

namespace QueueCache.Management;

public sealed record CacheAttribution(ulong LowerReadAttempts, ulong LowerWriteAttempts, ulong LowerFlushAttempts,
    ulong ControlBarriers, ulong StrictWriteBarriers, ulong DisabledWriteBarriers, ulong QuotaWriteBarriers,
    ulong ApplicationBarriers, ulong ShutdownBarriers, ulong PowerBarriers, ulong OrderedBarriers, ulong RemoveBarriers,
    ulong LastReason, ulong LastMajor, ulong LastCode, ulong LastOffset, ulong LastLength);
public sealed record CacheUsagePaths(ulong Paging, ulong Hibernation, ulong Dump);
public sealed record CacheUsageActivity(ulong InRequests, ulong OutRequests, ulong InSuccesses,
    ulong OutSuccesses, ulong InFailures, ulong OutFailures, ulong LastProcessId);
public sealed record CacheUsageActivities(CacheUsageActivity Paging, CacheUsageActivity Hibernation,
    CacheUsageActivity Dump);
public sealed record CachePagingIo(ulong ReadRequests, ulong ReadBytes, ulong WriteRequests, ulong WriteBytes,
    ulong LastMajor, ulong LastFlags, ulong LastOffset, ulong LastLength, ulong LastProcessId);

/// <summary>Lifetime request counters; deferred flushes are not durable flush completions.</summary>
public sealed record CacheDiagnostics(ulong ApplicationFlushes, ulong DeferredFlushes, ulong WriteThroughWrites,
    ulong DeferredWriteThroughWrites, ulong ControlBarriers, ulong OtherBarriers, ulong ShutdownBarriers,
    ulong PowerBarriers, ulong LastBarrierCode)
{
    public const int WireSize = 80;
    public const int AttributionWireSize = 216;
    public const int UsageWireSize = 240;
    public const int UsageActivityWireSize = 408;
    public const int PagingIoWireSize = 480;
    public CacheAttribution? Attribution { get; init; }
    public CacheUsagePaths? UsagePaths { get; init; }
    public CacheUsageActivities? UsageActivity { get; init; }
    public CachePagingIo? PagingIo { get; init; }
    public static CacheDiagnostics Decode(ReadOnlySpan<byte> bytes)
    {
        var expectedVersion = bytes.Length switch
        {
            WireSize => 1u,
            AttributionWireSize => 2u,
            UsageWireSize => 3u,
            UsageActivityWireSize => 4u,
            PagingIoWireSize => 5u,
            _ => 0u
        };
        if (expectedVersion == 0 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != expectedVersion ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != bytes.Length)
            throw new InvalidDataException("Unsupported diagnostics version/size.");
        var values = new ulong[9];
        for (var i = 0; i < values.Length; i++)
            values[i] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(8 + i * 8)..]);
        if (values[1] > values[0] || values[3] > values[2])
            throw new InvalidDataException("Inconsistent deferred request counters.");
        CacheAttribution? attribution = null;
        if (bytes.Length >= AttributionWireSize)
        {
            var extra = new ulong[17];
            for (var index = 0; index < extra.Length; index++)
                extra[index] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(WireSize + index * 8)..]);
            if (extra[12] > 9)
                throw new InvalidDataException("Unknown barrier reason.");
            attribution = new(extra[0], extra[1], extra[2], extra[3], extra[4], extra[5], extra[6],
                extra[7], extra[8], extra[9], extra[10], extra[11], extra[12], extra[13], extra[14], extra[15], extra[16]);
        }
        CacheUsagePaths? usagePaths = null;
        if (bytes.Length >= UsageWireSize)
            usagePaths = new(BinaryPrimitives.ReadUInt64LittleEndian(bytes[216..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[224..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[232..]));
        CacheUsageActivities? usageActivity = null;
        if (bytes.Length >= UsageActivityWireSize)
        {
            static CacheUsageActivity Activity(ReadOnlySpan<byte> source, int index) => new(
                BinaryPrimitives.ReadUInt64LittleEndian(source[(240 + index * 8)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(source[(264 + index * 8)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(source[(288 + index * 8)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(source[(312 + index * 8)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(source[(336 + index * 8)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(source[(360 + index * 8)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(source[(384 + index * 8)..]));
            usageActivity = new(Activity(bytes, 0), Activity(bytes, 1), Activity(bytes, 2));
        }
        CachePagingIo? pagingIo = null;
        if (bytes.Length == PagingIoWireSize)
        {
            var paging = new ulong[9];
            for (var index = 0; index < paging.Length; index++)
                paging[index] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(UsageActivityWireSize + index * 8)..]);
            pagingIo = new(paging[0], paging[1], paging[2], paging[3], paging[4], paging[5], paging[6], paging[7], paging[8]);
        }
        return new(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7], values[8])
        {
            Attribution = attribution,
            UsagePaths = usagePaths,
            UsageActivity = usageActivity,
            PagingIo = pagingIo
        };
    }
}
