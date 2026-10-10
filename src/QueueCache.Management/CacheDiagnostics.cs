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
public sealed record CachePagingProgress(ulong MapFailures, ulong CapacityWaits, ulong ServicedReadMisses,
    ulong ReservedBytes, ulong MaxReadLength, ulong MaxWriteLength);
public sealed record CachePagingRoute(ulong ReadRequests, ulong ReadCompletions, ulong ReadFailures,
    ulong WriteRequests, ulong WriteCompletions, ulong WriteFailures, ulong OverlapWaits);
/// <summary>V8: paging reads executed by the per-disk paging-read thread instead of the request worker.
/// WriteWaits/IdleWaits count requests that waited for overlapping/all offloaded reads.</summary>
public sealed record CachePagingOffload(ulong OffloadedReads, ulong Completions, ulong Failures,
    ulong WriteWaits, ulong IdleWaits, ulong MaxQueued);
/// <summary>V8: lower attempts attributed at their issuing path. Generated writes are QueueCache drains;
/// Forwarded writes are original non-paging requests; PagingForwarded are original paging requests.
/// Lower flushes are always QueueCache barriers or forwarded original flushes (see attribution totals).</summary>
public sealed record CacheLowerSources(ulong GeneratedWrites, ulong ForwardedWrites, ulong PagingForwardedWrites,
    ulong PagingForwardedReads, ulong OtherReads);
/// <summary>V9 lab range gate. Sequence values share one per-disk counter; zero means not observed.
/// State: 0 off, 1 armed, 2 holding a submitted overlapping drain, 3 released.</summary>
/// <summary>
/// V10 (T085): paging-marked writes admitted to RAM versus kept on the ordered direct path, and per-request
/// paging-file recognition counted at dispatch for every paging request. ReferenceMisses counts requests inside a
/// known paging-file extent that recognition would have treated as application traffic; it must stay zero.
/// </summary>
public sealed record CachePagingAdmission(ulong AdmittedWrites, ulong AdmittedBytes, ulong DirectWrites,
    ulong PagingFileRequests, ulong NoFileObject, ulong HighIrql, ulong ReferenceMisses,
    ulong ForceDirectRanges, ulong ReferenceRanges);
/// <summary>Which driver range set to replace: ForceDirect keeps matching paging traffic on the direct path;
/// Reference is observe-only paging-file extents for the recognition cross-check.</summary>
public enum SpecialRangeKind : uint { ForceDirect = 1, Reference = 2 }
/// <summary>One special-file range in disk byte offsets.</summary>
public sealed record DiskRange(long Start, long Length);
public sealed record CacheLabGate(ulong State, ulong Hits, ulong OldSubmitSeq, ulong OldLowerDoneSeq,
    ulong OldRetireSeq, ulong DirectWaitSeq, ulong DirectSubmitSeq, ulong DirectDoneSeq);

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
    public const int PagingProgressWireSize = 528;
    public const int PagingRouteWireSize = 584;
    public const int PagingOffloadWireSize = 672;
    public const int LabGateWireSize = 736;
    public const int PagingAdmissionWireSize = 808;
    public const int AllocationRetryWireSize = 816;
    public const int PagingBypassWireSize = 824;
    public const int ReadFillWireSize = 840;
    public const int CallerPathWireSize = 864;
    public const int CopyOffloadWireSize = 872;
    public const int WriteOffloadWireSize = 880;
    public const int RepeatedPageWireSize = 896;
    public const int LayoutWireSize = 944;
    public const int CopyFlagsWireSize = 952;
    public const int ReadRecallWireSize = 976;
    public const int StagedReadWireSize = 1008;
    /// <summary>The newest version: the buffer callers offer, so the driver returns every known field.</summary>
    public const int CurrentWireSize = StagedReadWireSize;
    public CacheAttribution? Attribution { get; init; }
    public CacheUsagePaths? UsagePaths { get; init; }
    public CacheUsageActivities? UsageActivity { get; init; }
    public CachePagingIo? PagingIo { get; init; }
    public CachePagingProgress? PagingProgress { get; init; }
    public CachePagingRoute? PagingRoute { get; init; }
    public CachePagingOffload? PagingOffload { get; init; }
    public CacheLowerSources? LowerSources { get; init; }
    public CacheLabGate? LabGate { get; init; }
    public CachePagingAdmission? PagingAdmission { get; init; }
    /// <summary>V11: failed lower IRP builds retried before submission (transient memory pressure).</summary>
    public ulong? LowerAllocationRetries { get; init; }
    /// <summary>V12: recognised paging-file requests forwarded straight to the disk from dispatch.</summary>
    public ulong? PagingFileBypasses { get; init; }
    /// <summary>V13: read misses kept as clean blocks: all reads, and application paging reads.</summary>
    public ulong? ReadFills { get; init; }
    public ulong? PagingReadFills { get; init; }
    /// <summary>V14: reads/writes served on the caller's thread, and attempts handed to the request worker.</summary>
    public CacheCallerPath? CallerPath { get; init; }
    /// <summary>V15: large RAM-hit reads handed to the offloaded-read threads so their copies run in parallel.</summary>
    public ulong? CopyOffloadReads { get; init; }
    /// <summary>V16: large fitting writes whose payload copy ran on those threads.</summary>
    public ulong? CopyOffloadWrites { get; init; }
    /// <summary>V17: paging reads whose buffer repeated a physical page (the memory manager's dummy page).</summary>
    public ulong? PagingReadsRepeatedPages { get; init; }
    /// <summary>V17: ordinary read misses not kept: since plan 62 because no driver-owned copy could be made
    /// (allocation failure or over 16 MiB); before, because their buffer repeated a physical page.</summary>
    public ulong? ReadFillsSkippedRepeatedPages { get; init; }
    /// <summary>V18: the last explicit layout measurement; null on older drivers.</summary>
    public CacheLayout? Layout { get; init; }
    /// <summary>V19: lab copy flags in effect (1 prefetch, 2 coalesced runs); null on older drivers.</summary>
    public ulong? CopyFlags { get; init; }
    /// <summary>V20: read-recall mode and how often a read miss was kept as recent or not; null on older drivers.</summary>
    public CacheReadRecall? ReadRecall { get; init; }
    /// <summary>V21: driver-owned lower-read attempts and bytes already known in RAM before submission;
    /// null on older drivers. Live counters are individually atomic, not a transactional snapshot.</summary>
    public CacheStagedReads? StagedReads { get; init; }
    public static CacheDiagnostics Decode(ReadOnlySpan<byte> bytes)
    {
        var expectedVersion = bytes.Length switch
        {
            WireSize => 1u,
            AttributionWireSize => 2u,
            UsageWireSize => 3u,
            UsageActivityWireSize => 4u,
            PagingIoWireSize => 5u,
            PagingProgressWireSize => 6u,
            PagingRouteWireSize => 7u,
            PagingOffloadWireSize => 8u,
            LabGateWireSize => 9u,
            PagingAdmissionWireSize => 10u,
            AllocationRetryWireSize => 11u,
            PagingBypassWireSize => 12u,
            ReadFillWireSize => 13u,
            CallerPathWireSize => 14u,
            CopyOffloadWireSize => 15u,
            WriteOffloadWireSize => 16u,
            RepeatedPageWireSize => 17u,
            LayoutWireSize => 18u,
            CopyFlagsWireSize => 19u,
            ReadRecallWireSize => 20u,
            StagedReadWireSize => 21u,
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
        if (bytes.Length >= PagingIoWireSize)
        {
            var paging = new ulong[9];
            for (var index = 0; index < paging.Length; index++)
                paging[index] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(UsageActivityWireSize + index * 8)..]);
            pagingIo = new(paging[0], paging[1], paging[2], paging[3], paging[4], paging[5], paging[6], paging[7], paging[8]);
        }
        CachePagingProgress? pagingProgress = null;
        if (bytes.Length >= PagingProgressWireSize)
        {
            var progress = new ulong[6];
            for (var index = 0; index < progress.Length; index++)
                progress[index] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(PagingIoWireSize + index * 8)..]);
            pagingProgress = new(progress[0], progress[1], progress[2], progress[3], progress[4], progress[5]);
        }
        CachePagingRoute? pagingRoute = null;
        if (bytes.Length >= PagingRouteWireSize)
        {
            var route = new ulong[7];
            for (var index = 0; index < route.Length; index++)
                route[index] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(PagingProgressWireSize + index * 8)..]);
            if (route[1] + route[2] > route[0] || route[4] + route[5] > route[3])
                throw new InvalidDataException("Inconsistent routed paging completion counters.");
            pagingRoute = new(route[0], route[1], route[2], route[3], route[4], route[5], route[6]);
        }
        CachePagingOffload? pagingOffload = null;
        CacheLowerSources? lowerSources = null;
        if (bytes.Length >= PagingOffloadWireSize)
        {
            var offload = new ulong[11];
            for (var index = 0; index < offload.Length; index++)
                offload[index] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(PagingRouteWireSize + index * 8)..]);
            if (offload[1] + offload[2] > offload[0] || offload[5] > 64)
                throw new InvalidDataException("Inconsistent offloaded paging-read counters.");
            // The driver reads source counters before totals and increments totals first.
            if (offload[6] + offload[7] + offload[8] > attribution!.LowerWriteAttempts ||
                offload[9] + offload[10] > attribution.LowerReadAttempts)
                throw new InvalidDataException("Attributed lower attempts exceed total lower attempts.");
            pagingOffload = new(offload[0], offload[1], offload[2], offload[3], offload[4], offload[5]);
            lowerSources = new(offload[6], offload[7], offload[8], offload[9], offload[10]);
        }
        CacheLabGate? labGate = null;
        if (bytes.Length >= LabGateWireSize)
        {
            var gate = new ulong[8];
            for (var index = 0; index < gate.Length; index++)
                gate[index] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(PagingOffloadWireSize + index * 8)..]);
            if (gate[0] > 3 || gate[1] > 1)
                throw new InvalidDataException("Invalid lab gate state.");
            labGate = new(gate[0], gate[1], gate[2], gate[3], gate[4], gate[5], gate[6], gate[7]);
        }
        CachePagingAdmission? pagingAdmission = null;
        if (bytes.Length >= PagingAdmissionWireSize)
        {
            var admission = new ulong[9];
            for (var index = 0; index < admission.Length; index++)
                admission[index] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(LabGateWireSize + index * 8)..]);
            if (admission[7] > SpecialRangeMap.MaxRanges || admission[8] > SpecialRangeMap.MaxRanges)
                throw new InvalidDataException("Invalid driver range-set size.");
            pagingAdmission = new(admission[0], admission[1], admission[2], admission[3], admission[4], admission[5],
                admission[6], admission[7], admission[8]);
        }
        ulong? allocationRetries = bytes.Length >= AllocationRetryWireSize
            ? BinaryPrimitives.ReadUInt64LittleEndian(bytes[PagingAdmissionWireSize..])
            : null;
        ulong? pagingBypasses = bytes.Length >= PagingBypassWireSize
            ? BinaryPrimitives.ReadUInt64LittleEndian(bytes[AllocationRetryWireSize..])
            : null;
        ulong? readFills = null, pagingReadFills = null;
        if (bytes.Length >= ReadFillWireSize)
        {
            readFills = BinaryPrimitives.ReadUInt64LittleEndian(bytes[PagingBypassWireSize..]);
            pagingReadFills = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(PagingBypassWireSize + 8)..]);
        }
        CacheCallerPath? callerPath = bytes.Length >= CallerPathWireSize
            ? new(BinaryPrimitives.ReadUInt64LittleEndian(bytes[ReadFillWireSize..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[(ReadFillWireSize + 8)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[(ReadFillWireSize + 16)..]))
            : null;
        ulong? copyOffloadReads = bytes.Length >= CopyOffloadWireSize
            ? BinaryPrimitives.ReadUInt64LittleEndian(bytes[CallerPathWireSize..])
            : null;
        ulong? copyOffloadWrites = bytes.Length >= WriteOffloadWireSize
            ? BinaryPrimitives.ReadUInt64LittleEndian(bytes[CopyOffloadWireSize..])
            : null;
        ulong? pagingRepeated = null, fillsSkippedRepeated = null;
        if (bytes.Length >= RepeatedPageWireSize)
        {
            pagingRepeated = BinaryPrimitives.ReadUInt64LittleEndian(bytes[WriteOffloadWireSize..]);
            fillsSkippedRepeated = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(WriteOffloadWireSize + 8)..]);
        }
        CacheLayout? layout = null;
        if (bytes.Length >= LayoutWireSize)
        {
            var v = new ulong[6];
            for (var i = 0; i < v.Length; i++)
                v[i] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(RepeatedPageWireSize + i * 8)..]);
            layout = new(v[0], v[1], v[2], v[3], v[4], v[5]);
        }
        ulong? copyFlags = bytes.Length >= CopyFlagsWireSize ? BinaryPrimitives.ReadUInt64LittleEndian(bytes[LayoutWireSize..]) : null;
        var readRecall = bytes.Length >= ReadRecallWireSize
            ? new CacheReadRecall(BinaryPrimitives.ReadUInt64LittleEndian(bytes[CopyFlagsWireSize..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[(CopyFlagsWireSize + 8)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[(CopyFlagsWireSize + 16)..]))
            : null;
        var stagedReads = bytes.Length >= StagedReadWireSize
            ? new CacheStagedReads(BinaryPrimitives.ReadUInt64LittleEndian(bytes[ReadRecallWireSize..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[(ReadRecallWireSize + 8)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[(ReadRecallWireSize + 16)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[(ReadRecallWireSize + 24)..]))
            : null;
        return new(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7], values[8])
        {
            Attribution = attribution,
            UsagePaths = usagePaths,
            UsageActivity = usageActivity,
            PagingIo = pagingIo,
            PagingProgress = pagingProgress,
            PagingRoute = pagingRoute,
            PagingOffload = pagingOffload,
            LowerSources = lowerSources,
            LabGate = labGate,
            PagingAdmission = pagingAdmission,
            LowerAllocationRetries = allocationRetries,
            PagingFileBypasses = pagingBypasses,
            ReadFills = readFills,
            PagingReadFills = pagingReadFills,
            CallerPath = callerPath,
            CopyOffloadReads = copyOffloadReads,
            CopyOffloadWrites = copyOffloadWrites,
            PagingReadsRepeatedPages = pagingRepeated,
            ReadFillsSkippedRepeatedPages = fillsSkippedRepeated,
            Layout = layout,
            CopyFlags = copyFlags,
            ReadRecall = readRecall,
            StagedReads = stagedReads
        };
    }
}
/// <summary>Last explicit layout measurement (Measurements 0: never measured). Of the cached blocks,
/// Neighbors also have the next disk block cached; Contiguous/Reversed hold it in the next/previous
/// 4 KiB of memory. FreeChunks: 256 KiB chunks with every slot free.</summary>
public sealed record CacheLayout(ulong Measurements, ulong Blocks, ulong Neighbors, ulong Contiguous, ulong Reversed, ulong FreeChunks)
{
    public double? ContiguousShare => Neighbors == 0 ? null : (double)Contiguous / Neighbors;
}
/// <summary>Read recall (driver readrecall.h). Mode 1: a read miss whose block was used more recently than
/// the oldest used block still cached is kept as recent (Recalled); history matches that were not are Denied.
/// Mode 0: the earlier bimodal insertion (one miss in 16 kept as recent); both counters then stay unchanged.</summary>
public sealed record CacheReadRecall(ulong Mode, ulong Recalled, ulong Denied);
/// <summary>Submitted staged reads, including lower failures. CachedBytes is the valid sector overlap
/// pinned before submission, not bytes subsequently filled, completed traffic, or timing-window data.
/// Allocation failures, original/paging reads and fully cached reads are outside these counters.</summary>
public sealed record CacheStagedReads(ulong Requests, ulong Bytes, ulong MixedRequests, ulong CachedBytes)
{
    /// <summary>Difference between quiescent boundaries. Live snapshots can straddle an update;
    /// only a completed scenario window may enforce relationships between counters.</summary>
    public CacheStagedReads Since(CacheStagedReads before)
    {
        if (Requests < before.Requests || Bytes < before.Bytes || MixedRequests < before.MixedRequests || CachedBytes < before.CachedBytes)
            throw new InvalidDataException("Staged-read counters decreased; the driver/window identity changed.");
        var result = new CacheStagedReads(Requests - before.Requests, Bytes - before.Bytes,
            MixedRequests - before.MixedRequests, CachedBytes - before.CachedBytes);
        if (result.MixedRequests > result.Requests || result.CachedBytes > result.Bytes)
            throw new InvalidDataException("Staged-read boundaries do not describe a quiescent window.");
        return result;
    }
}
/// <summary>Requests served on the dispatching thread instead of the request worker.</summary>
public sealed record CacheCallerPath(ulong Reads, ulong Writes, ulong Declined);
