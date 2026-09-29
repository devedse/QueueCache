// SPDX-License-Identifier: MIT
#pragma once
#include <ntifs.h>
#include "cachepolicy.h"

#define IOCTL_QCACHE_STATE_V1 CTL_CODE(0x8844UL, 0xD10UL, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_QCACHE_CONTROL_V1 CTL_CODE(0x8844UL, 0xD11UL, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS)
#define IOCTL_QCACHE_DIAGNOSTICS_V1 CTL_CODE(0x8844UL, 0xD12UL, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_QCACHE_STATE_V2 CTL_CODE(0x8844UL, 0xD13UL, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_QCACHE_STATE_V3 CTL_CODE(0x8844UL, 0xD14UL, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_QCACHE_OPTIONS_V1 CTL_CODE(0x8844UL, 0xD15UL, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS)
#define IOCTL_QCACHE_PERFORMANCE_V1 CTL_CODE(0x8844UL, 0xD16UL, METHOD_BUFFERED, FILE_ANY_ACCESS)
// T085 disk byte range sets. Input: QC_SPECIAL_RANGES_HEADER + Count entries.
// Paging-file traffic is recognised per request (FsRtlIsPagingFile on the
// request's file object), not by these ranges. Flags selects exactly one set:
//   QcRangesForceDirect (1): paging-marked requests touching these ranges always
//     take the ordered direct path (verification of that path on test disks).
//   QcRangesReference (2): observe-only extents of known paging files; the driver
//     counts paging requests inside them that per-request recognition missed.
// Count 0 clears the selected set.
#define IOCTL_QCACHE_SPECIAL_RANGES_V1 CTL_CODE(0x8844UL, 0xD17UL, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS)
struct QC_SPECIAL_RANGE
{
    LONGLONG Start, Length;
};
enum : ULONG
{
    QcRangesForceDirect = 1,
    QcRangesReference = 2
};
struct QC_SPECIAL_RANGES_HEADER
{
    ULONG Version, Size, Count, Flags;
    ULONGLONG Generation;
};
static_assert(sizeof(QC_SPECIAL_RANGE) == 16 && sizeof(QC_SPECIAL_RANGES_HEADER) == 24);
static constexpr ULONG QcSpecialRangeMax = 256;
// Durations are QPC ticks, converted using Frequency. Counters are lifetime cumulative.
// V2 appends opt-in cooperative-read diagnostics; V3 appends drain phase timings.
// The first 192 bytes remain V1 and the first 408 bytes remain V2.
struct QC_PERFORMANCE
{
    ULONG Version, Size;
    ULONGLONG Frequency, TimingEnabled, QueueDepth, OldestQueuedTicks, QueuedRequests, QueueWaitTicks,
        MaxQueueWaitTicks;
    ULONGLONG ActiveMajor, Phase, ActiveAgeTicks, CapacityWaits, CapacityWaitTicks, BypassReads, BypassMisses;
    ULONGLONG LockAcquires, LockWaitTicks, LockHoldTicks, MaxLockWaitTicks, MaxLockHoldTicks;
    ULONGLONG DrainBatches, DrainBytes, WakeSignals, LowerIoTicks;
    ULONGLONG ServiceReadCalls, ServiceReadAttempts, ServiceReadCompletions, ServiceReadNoCandidate;
    ULONGLONG SelectionRejectNotRead, SelectionRejectAfterSequence, SelectionRejectMaxSize;
    ULONGLONG SelectionRejectActiveOverlap, SelectionRejectOlderWriteOverlap, SelectionRejectFence;
    ULONGLONG SelectionScanLimit, ServiceReadBudgetExhausted, ServiceReadMisses;
    ULONGLONG WaitChanged, WaitRequestAvailable, WaitTimeout, WaitLowerCompleted;
    ULONGLONG LastBlockedMajor, LastBlockedOffset, LastBlockedLength;
    ULONGLONG LastSelectionMajor, LastSelectionCode, LastSelectionOffset, LastSelectionLength;
    ULONGLONG LastSelectionSequence, LastAfterSequence, LastSelectionScanned;
    ULONGLONG DrainSelectionTicks, DrainCopyTicks, DrainRetirementTicks;
};
static constexpr ULONG QcPerformanceV1Size = 192;
static constexpr ULONG QcPerformanceV2Size = 408;
static_assert(sizeof(QC_PERFORMANCE) == 432);
enum : ULONGLONG
{
    QcIdlePhase,
    QcRequestPhase,
    QcCapacityPhase,
    QcDrainPhase,
    QcLowerFlushPhase,
    QcLowerReadPhase
};
struct QC_DIAGNOSTICS
{
    ULONG Version, Size;
    ULONGLONG ApplicationFlushes, DeferredFlushes, WriteThroughWrites, DeferredWriteThroughWrites;
    ULONGLONG ControlBarriers, OtherBarriers, ShutdownBarriers, PowerBarriers, LastBarrierCode;
    ULONGLONG LowerReadAttempts, LowerWriteAttempts, LowerFlushAttempts;
    ULONGLONG BarrierReasons[9];
    ULONGLONG LastReason, LastMajor, LastCode, LastOffset, LastLength;
    ULONGLONG PagingUsagePaths, HibernationUsagePaths, DumpUsagePaths;
    ULONGLONG UsageInRequests[3], UsageOutRequests[3];
    ULONGLONG UsageInSuccesses[3], UsageOutSuccesses[3];
    ULONGLONG UsageInFailures[3], UsageOutFailures[3];
    ULONGLONG UsageLastProcessId[3];
    ULONGLONG PagingReadRequests, PagingReadBytes, PagingWriteRequests, PagingWriteBytes;
    ULONGLONG PagingLastMajor, PagingLastFlags, PagingLastOffset, PagingLastLength, PagingLastProcessId;
    ULONGLONG PagingMapFailures, PagingCapacityWaits, PagingServicedReadMisses;
    ULONGLONG PagingReservedBytes, PagingMaxReadLength, PagingMaxWriteLength;
    ULONGLONG PagingRoutedReadRequests, PagingRoutedReadCompletions, PagingRoutedReadFailures;
    ULONGLONG PagingRoutedWriteRequests, PagingRoutedWriteCompletions, PagingRoutedWriteFailures;
    ULONGLONG PagingOverlapWaits;
    // V8: paging reads executed by the independent paging-read thread.
    ULONGLONG PagingOffloadedReads, PagingOffloadCompletions, PagingOffloadFailures;
    ULONGLONG PagingOffloadWriteWaits, PagingOffloadIdleWaits, PagingOffloadMaxQueued;
    // V8: lower attempts by source. Generated = drainer writes; Forwarded = an
    // original non-paging request; PagingForwarded = an original paging request.
    ULONGLONG LowerGeneratedWrites, LowerForwardedWrites, LowerPagingForwardedWrites;
    ULONGLONG LowerPagingForwardedReads, LowerOtherReads;
    // V9: one-shot lab range gate (disposable non-paging disks only). Sequence
    // values come from one per-disk counter, so they order these events.
    ULONGLONG LabGateState, LabGateHits, LabGateOldSubmitSeq, LabGateOldLowerDoneSeq;
    ULONGLONG LabGateOldRetireSeq, LabGateDirectWaitSeq, LabGateDirectSubmitSeq, LabGateDirectDoneSeq;
    // V10: T085 application paging admission and paging-file recognition.
    // Direct = paging writes kept on the ordered direct path. The classification
    // counters are recorded at dispatch for every paging request, routed or not.
    ULONGLONG PagingAdmittedWrites, PagingAdmittedBytes, PagingDirectWrites;
    ULONGLONG PagingFileRequests, PagingNoFileObject, PagingHighIrql, PagingReferenceMisses;
    ULONGLONG ForceDirectRanges, ReferenceRanges;
    // V11: lower IRP builds that failed and were retried before submission.
    ULONGLONG LowerAllocationRetries;
    // V12: recognised paging-file requests forwarded from dispatch, bypassing the worker.
    ULONGLONG PagingFileBypasses;
    // V13: read misses kept as clean cache blocks (all reads / application paging reads;
    // the second stays zero since paging read misses are no longer kept, see V17).
    ULONGLONG ReadFills, PagingReadFills;
    // V14: requests served on the caller's thread without the request worker, and
    // caller-thread attempts that handed the request to the worker unchanged.
    ULONGLONG CallerPathReads, CallerPathWrites, CallerPathDeclined;
    // V15: large RAM-hit reads handed to the offloaded-read threads for a parallel copy.
    ULONGLONG CopyOffloadReads;
    // V16: large fitting writes whose payload copy was handed to those threads.
    ULONGLONG CopyOffloadWrites;
    // V17: paging read misses whose buffer repeats a physical page (the memory
    // manager's dummy page; never kept), and ordinary misses not kept because no
    // staging copy could be made (before plan 62: because their buffer repeated a page).
    ULONGLONG PagingReadsRepeatedPages, ReadFillsSkippedRepeatedPages;
};
static constexpr ULONG QcDiagnosticsV1Size = 80;
static constexpr ULONG QcDiagnosticsV2Size = 216;
static constexpr ULONG QcDiagnosticsV3Size = 240;
static constexpr ULONG QcDiagnosticsV4Size = 408;
static constexpr ULONG QcDiagnosticsV5Size = 480;
static constexpr ULONG QcDiagnosticsV6Size = 528;
static constexpr ULONG QcDiagnosticsV7Size = 584;
static constexpr ULONG QcDiagnosticsV8Size = 672;
static constexpr ULONG QcDiagnosticsV9Size = 736;
static constexpr ULONG QcDiagnosticsV10Size = 808;
static constexpr ULONG QcDiagnosticsV11Size = 816;
static constexpr ULONG QcDiagnosticsV12Size = 824;
static constexpr ULONG QcDiagnosticsV13Size = 840;
static constexpr ULONG QcDiagnosticsV14Size = 864;
static constexpr ULONG QcDiagnosticsV15Size = 872;
static constexpr ULONG QcDiagnosticsV16Size = 880;
static_assert(sizeof(QC_DIAGNOSTICS) == 896);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, PagingReadsRepeatedPages) == QcDiagnosticsV16Size);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, CopyOffloadWrites) == QcDiagnosticsV15Size);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, CopyOffloadReads) == QcDiagnosticsV14Size);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, CallerPathReads) == QcDiagnosticsV13Size);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, ReadFills) == QcDiagnosticsV12Size);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, PagingFileBypasses) == QcDiagnosticsV11Size);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, LowerAllocationRetries) == QcDiagnosticsV10Size);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, PagingAdmittedWrites) == QcDiagnosticsV9Size);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, LabGateState) == QcDiagnosticsV8Size);
// LabGateState: 0 disarmed, 1 armed, 2 holding a submitted overlapping drain, 3 released.
enum : ULONG
{
    QcLabGateOff,
    QcLabGateArmed,
    QcLabGateHolding,
    QcLabGateReleased
};
static constexpr ULONG QcLabGateMaxHoldMs = 5000, QcLabGateMaxBytes = 1024 * 1024;
// About 5 s of consecutive failed lower IRP builds before a drain/flush faults the cache.
static constexpr ULONG QcLowerAllocationAttempts = 250, QcLowerAllocationBackoffMs = 20;
// Lab fault 8 simulates this many failed builds on one drain batch; 9 fails every build until cleared.
static constexpr ULONG QcLabAllocationFailures = 3;
static_assert(QcLabAllocationFailures < QcLowerAllocationAttempts);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, PagingOffloadedReads) == QcDiagnosticsV7Size);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, LowerReadAttempts) == QcDiagnosticsV1Size);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, LastReason) == 176);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, PagingUsagePaths) == QcDiagnosticsV2Size);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, UsageInRequests) == QcDiagnosticsV3Size);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, UsageOutRequests) == 264);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, UsageInSuccesses) == 288);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, UsageOutSuccesses) == 312);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, UsageInFailures) == 336);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, UsageOutFailures) == 360);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, UsageLastProcessId) == 384);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, PagingReadRequests) == QcDiagnosticsV4Size);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, PagingMapFailures) == QcDiagnosticsV5Size);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, PagingRoutedReadRequests) == QcDiagnosticsV6Size);
enum QC_BARRIER_REASON : ULONG
{
    QcControlBarrier = 1, QcStrictWriteBarrier, QcDisabledWriteBarrier, QcQuotaWriteBarrier,
    QcApplicationBarrier, QcShutdownBarrier, QcPowerBarrier, QcOrderedBarrier, QcRemoveBarrier
};
struct QC_STATE
{
    ULONG Version, Size, Flags;
    NTSTATUS LastError;
    ULONGLONG DeviceBytes, BudgetBytes, ReservedBytes, DirtyBytes, InFlightBytes, PayloadCapacity;
    ULONGLONG OccupiedSlots, AcceptedBytes, DrainedBytes, ThrottleWaits, CacheReadBytes, Errors, Flushes,
        PeakDirtyBytes;
};
struct QC_COMMAND
{
    ULONG Version, Size, Action, Reserved;
    ULONGLONG BudgetBytes, Value;
};
static_assert(sizeof(QC_STATE) == 128);
struct QC_STATE_V2
{
    QC_STATE Base;
    ULONGLONG DiscardedBytes, LowerWrites, BatchedWrites, TrimRequests;
};
static_assert(sizeof(QC_STATE_V2) == 160);
struct QC_STATE_V3
{
    QC_STATE_V2 Base;
    QC_OPTIONS Options;
    ULONGLONG CleanReadBytes, CleanWriteBytes, ReadHitBytes, ReadMissBytes, Evictions;
    ULONGLONG OldestDirtyMs, Generation, Instance, GlobalLimitBytes, GlobalReservedBytes;
};
static_assert(sizeof(QC_STATE_V3) == 288);
static_assert(sizeof(QC_COMMAND) == 32);
enum : ULONG
{
    QcConfigure = 1,
    QcEnable,
    QcFlush,
    QcDisable,
    QcRetry,
    QcLabDelay,
    QcLabFault,
    QcFlushPolicy,
    QcRelease,
    QcDropClean,
    QcPerformanceTiming,
    QcEnablePaging, // Deprecated compatibility alias for QcEnable.
    // Lab: BudgetBytes = range start; Value = mode << 48 | hold ms << 32 | range bytes.
    // Mode 1/2: the held batch's real lower write reports failure/short transfer
    // (last run of a sparse version) and faults the cache as a real error would.
    // Value 0 disarms. Refused on any disk hosting a paging/hibernation/dump path.
    QcLabGate,
    // Value 0: every read/write goes through the request worker. Value 1: when no
    // other request is queued or active, RAM hits and fitting writes are served on
    // the calling thread (see QcCacheTryCallerPath). Runtime only; not persisted.
    QcCallerPath
}; // Toggle optional detailed timing; never resets counters.
struct QC_SLOT
{
    PUCHAR Buffer;
    LARGE_INTEGER Offset;
    ULONG Length, HashNext, HashPrevious, QueueNext, QueuePrevious, FreeNext;
    BOOLEAN InFlight;
    BOOLEAN Dirty, ReadClass;
    ULONG Pins;
    BOOLEAN Filling, RetireWhenUnpinned;
    ULONG ValidSectors; // sectorcoverage.h: unknown sectors must never be read from this buffer or drained
    ULONGLONG DirtySince;
};
struct QC_CACHE;
// A paging read that needs lower I/O is executed by one dedicated thread per
// disk, never by the sole request worker. The table records ranges only: once
// forwarded, the IRP's Tail/DriverContext belong to the lower stack.
static constexpr ULONG QcPagingReadSlots = 64;
static constexpr ULONG QcPagingPinBlocks = 256; // 1 MiB of 4 KiB cache blocks.
// An offloaded request: a read, or the payload copy of a large write whose
// blocks the request worker already admitted (and marked Filling).
struct QC_PAGING_READ
{
    PIRP Irp; // Null when free.
    LONGLONG Start, End;
    ULONGLONG Sequence;
    BOOLEAN Started;
    BOOLEAN Write, WriteThrough;
    PUCHAR Source; // Write: the mapped caller buffer.
};
struct QC_DRAIN_WORKER
{
    QC_CACHE* Cache;
    ULONG Number;
    HANDLE Thread;
};
// Offloaded-request executors: paging reads that need the disk, and large reads
// served entirely from RAM and large fitting writes (QcCopyOffloadMinBytes), so
// their copies run in parallel instead of one after another on the request worker.
static constexpr ULONG QcReadThreads = 3;
static constexpr ULONG QcCopyOffloadMinBytes = 256 * 1024;
// Largest read miss copied through a driver-owned buffer and kept (StagedRead).
static constexpr ULONG QcStagedReadMaxBytes = 16 * 1024 * 1024;
struct QC_READ_THREAD
{
    QC_CACHE* Cache;
    HANDLE Thread;
    ULONG Pins[QcPagingPinBlocks]; // This thread only.
};
struct QC_CACHE
{
    PDEVICE_OBJECT Lower;
    PIO_WORKITEM LowerCallItem; // Null: forward inline (allocation failed at attach).
    // "Mutex" in comments: exclusive push lock, never taken recursively or held across
    // a wait or I/O. A KMUTEX handed ownership to the next waiter, which convoyed
    // parallel readers behind thread wake-ups (9.7 s of waits for 0.7 s held).
    EX_PUSH_LOCK Mutex;
    KSPIN_LOCK SnapshotLock;
    PKSPIN_LOCK RoutingLock; // QueueLock: makes usage reservation and Enable atomic.
    QC_STATE State, Snapshot;
    QC_STATE_V2 ExtendedSnapshot;
    QC_STATE_V3 ReadWriteSnapshot;
    QC_OPTIONS Options;
    ULONG CleanHead[2], CleanTail[2], CleanCount[2]; // write/read LRU lists
    ULONG DirtySlots;
    ULONGLONG CleanValidBytes[2];
    ULONGLONG ReadHitBytes, ReadMissBytes, Evictions, Generation, Instance, LastWriteTime;
    BOOLEAN Pressure, WriterWaiting;
    ULONGLONG DiscardedBytes, LowerWrites, BatchedWrites, TrimRequests;
    QC_DIAGNOSTICS Diagnostics, DiagnosticsSnapshot;
    volatile LONG64 LowerReadAttempts, LowerWriteAttempts, LowerFlushAttempts;
    QC_PERFORMANCE Performance, PerformanceSnapshot;
    ULONGLONG LockStarted;
    volatile LONG Timing;
    // Only the request worker invokes this callback, outside Mutex, while its
    // current write is capacity-blocked or its lower read is pending. No concurrent
    // foreground writer is introduced, and the callback must never submit lower I/O.
    // True: read completion OR bounded selector progress; recheck the owner
    // condition before calling again. False: wait for work/capacity/completion.
    bool (*ServiceReads)(PVOID, PIRP);
    PVOID ServiceContext;
    PKEVENT RequestAvailable;
    KEVENT Wake, Changed;
    KEVENT Stopping; // Set once by QcCacheDestroy; drainers above Parallelism wait on it, never on Wake.
    QC_DRAIN_WORKER Workers[4];
    QC_SLOT* Slots;
    // One MDL per 256 KiB payload slab: physical pages from the memory manager
    // (MmAllocatePagesForMdlEx), mapped once, never nonpaged pool. Indexed by
    // slot / SlotsPerSlab; the slab's mapping is Slots[slab * SlotsPerSlab].Buffer.
    PMDL* SlabMdls;
    ULONG* Buckets;
    PUCHAR DrainBuffer;
    ULONG Capacity, Head, Tail, FreeHead, Count, SectorBytes, DrainCapacity;
    BOOLEAN Enabled, Barrier, Suspended, Stop, ResumeEnabled;
    // Request worker owns these fields. QUERY_REMOVE drains and disables the
    // cache; CANCEL_REMOVE (or a lower veto) restores only the prior enablement.
    BOOLEAN QueryRemovePending, QueryRemoveWasEnabled;
    BOOLEAN UnsafeDefer;
    BOOLEAN TrimPaused;
    // One foreground paging write owns this range until its lower completion.
    // RangeForward becomes true after older dirty versions have drained.
    BOOLEAN RangeDrain, RangeForward;
    LONGLONG RangeStart, RangeEnd;
    BOOLEAN BlockedPlacement; // partmgr below us would reject generated background writes.
    volatile LONG Gone, PagingPathCount;
    volatile LONG PagingUsageCount, HibernationUsageCount, DumpUsageCount;
    volatile LONG64 UsageInRequests[3], UsageOutRequests[3];
    volatile LONG64 UsageInSuccesses[3], UsageOutSuccesses[3];
    volatile LONG64 UsageInFailures[3], UsageOutFailures[3];
    volatile LONG64 UsageLastProcessId[3];
    volatile LONG64 PagingReadRequests, PagingReadBytes, PagingWriteRequests, PagingWriteBytes;
    volatile LONG64 PagingLastMajor, PagingLastFlags, PagingLastOffset, PagingLastLength, PagingLastProcessId;
    volatile LONG64 PagingMapFailures, PagingCapacityWaits, PagingServicedReadMisses;
    volatile LONG64 PagingMaxReadLength, PagingMaxWriteLength;
    volatile LONG64 PagingRoutedReadRequests, PagingRoutedReadCompletions, PagingRoutedReadFailures;
    volatile LONG64 PagingRoutedWriteRequests, PagingRoutedWriteCompletions, PagingRoutedWriteFailures;
    volatile LONG64 PagingOverlapWaits;
    volatile LONG64 PagingOffloadedReads, PagingOffloadCompletions, PagingOffloadFailures;
    volatile LONG64 PagingOffloadWriteWaits, PagingOffloadIdleWaits, PagingOffloadMaxQueued;
    volatile LONG64 LowerGeneratedWrites, LowerForwardedWrites, LowerPagingForwardedWrites;
    volatile LONG64 LowerAllocationRetries, PagingFileBypasses, ReadFills, PagingReadFills;
    ULONG ReadFillsSinceRecent; // Mutex: bimodal read-fill insertion counter.
    volatile LONG CallerPath; // QcCallerPath mode, read by dispatch.
    volatile LONG64 CallerPathReads, CallerPathWrites, CallerPathDeclined, CopyOffloadReads, CopyOffloadWrites;
    volatile LONG64 PagingReadsRepeatedPages, ReadFillsSkippedRepeatedPages;
    volatile LONG64 LowerPagingForwardedReads, LowerOtherReads;
    // Offloaded paging reads. PagingLock protects the table, PagingQueued and
    // PagingStop. Only the request worker inserts; only ReadThreads execute.
    // The worker never waits for the paging thread while holding Mutex, and the
    // paging thread waits only for Mutex and lower completion, never the worker.
    KSPIN_LOCK PagingLock;
    QC_PAGING_READ PagingReads[QcPagingReadSlots];
    ULONG PagingQueued;
    ULONGLONG PagingSequence;
    BOOLEAN PagingStop;
    KEVENT PagingWork, PagingDone;
    QC_READ_THREAD ReadThreads[QcReadThreads];
    // Mutex. Set by the request worker: OffloadBlocked around requests that may
    // retire pinned slots or change power/media state; ActiveWrite while a write
    // (including its barrier/fence waits) owns [ActiveWriteStart, ActiveWriteEnd).
    BOOLEAN OffloadBlocked, ActiveWrite;
    LONGLONG ActiveWriteStart, ActiveWriteEnd;
    // Completes an offloaded original IRP and releases its remove lock.
    void (*CompleteRequest)(PVOID, PIRP, NTSTATUS);
    // Releases the remove lock of a forwarded control when the lower device completes it.
    void (*ReleaseRequest)(PVOID, PIRP);
    PDEVICE_OBJECT Self;
    // Media-changing controls forwarded without waiting and not yet completed below.
    // While any is in flight, read misses are not kept (they could predate its change).
    volatile LONG ControlsInFlight;
    ULONG DelayMs, InjectFault;
    // Lab range gate. State/range/hold under Mutex; sequences written once each.
    ULONG LabGateState, LabGateHoldMs, LabGateMode; // Mode: 0 success, 1 report failure, 2 short transfer.
    LONGLONG LabGateStart, LabGateEnd;
    volatile LONG64 LabSequence, LabGateHits, LabGateOldSubmitSeq, LabGateOldLowerDoneSeq, LabGateOldRetireSeq;
    volatile LONG64 LabGateDirectWaitSeq, LabGateDirectSubmitSeq, LabGateDirectDoneSeq;
    // T085 range sets. RangeLock (spin lock) because dispatch reads the reference
    // set at up to DISPATCH_LEVEL for classification counters.
    KSPIN_LOCK RangeLock;
    ULONG ForceDirectCount, ReferenceCount;
    QC_SPECIAL_RANGE ForceDirect[QcSpecialRangeMax], Reference[QcSpecialRangeMax];
    volatile LONG64 PagingAdmittedWrites, PagingAdmittedBytes, PagingDirectWrites;
    volatile LONG64 PagingFileRequests, PagingNoFileObject, PagingHighIrql, PagingReferenceMisses;
};
FORCEINLINE bool QcTrackedUsageNotification(PIO_STACK_LOCATION stack)
{
    return stack->MajorFunction == IRP_MJ_PNP && stack->MinorFunction == IRP_MN_DEVICE_USAGE_NOTIFICATION &&
           (stack->Parameters.UsageNotification.Type == DeviceUsageTypePaging ||
            stack->Parameters.UsageNotification.Type == DeviceUsageTypeHibernation ||
            stack->Parameters.UsageNotification.Type == DeviceUsageTypeDumpFile);
}
NTSTATUS QcCacheInitialize(QC_CACHE* cache, PDEVICE_OBJECT self, PDEVICE_OBJECT lower);
void QcCacheDestroy(QC_CACHE* cache);
void QcCacheSnapshot(QC_CACHE* cache, QC_STATE* output);
void QcCacheSnapshotV2(QC_CACHE* cache, QC_STATE_V2* output);
void QcCacheSnapshotV3(QC_CACHE* cache, QC_STATE_V3* output);
void QcCacheDiagnostics(QC_CACHE* cache, QC_DIAGNOSTICS* output);
void QcCacheRecordLowerAttempt(QC_CACHE* cache, ULONG major, PIRP original = nullptr);
void QcCacheRecordUsage(QC_CACHE* cache, DEVICE_USAGE_NOTIFICATION_TYPE type, BOOLEAN inPath);
void QcCacheRecordUsageRequest(QC_CACHE* cache, DEVICE_USAGE_NOTIFICATION_TYPE type, BOOLEAN inPath,
                               ULONGLONG processId);
void QcCacheRecordUsageCompletion(QC_CACHE* cache, DEVICE_USAGE_NOTIFICATION_TYPE type, BOOLEAN inPath,
                                  NTSTATUS status);
bool QcCacheRecordPagingIo(QC_CACHE* cache, PIRP irp);
LONG QcCachePagingPathCount(QC_CACHE* cache);
void QcCachePerformance(QC_CACHE* cache, QC_PERFORMANCE* output);
bool QcCacheTryReadHit(QC_CACHE* cache, PIRP irp, LONGLONG deviceBytes, NTSTATUS* status);
// Dispatch only, at PASSIVE_LEVEL, while no other foreground request can run (the
// request worker waits for DirectIdle before processing). True: *status is final
// and the caller completes irp. False: nothing changed; queue irp for the worker.
bool QcCacheTryCallerPath(QC_CACHE* cache, PIRP irp, LONGLONG deviceBytes, NTSTATUS* status);
bool QcCacheTryPagingReadProgress(QC_CACHE* cache, PIRP irp, LONGLONG deviceBytes, NTSTATUS* status);
// Request worker only. True: an offloaded-read thread now owns and will complete irp.
bool QcCacheOffloadPagingRead(QC_CACHE* cache, PIRP irp, LONGLONG deviceBytes);
bool QcCachePagingReadsOutstanding(QC_CACHE* cache);
void QcCacheWaitPagingReads(QC_CACHE* cache);
// *transferred: the paging thread owns irp; the caller must not complete it.
NTSTATUS QcCacheProcess(QC_CACHE* cache, PIRP irp, LONGLONG deviceBytes, bool* transferred);
NTSTATUS QcCacheBarrier(QC_CACHE* cache, BOOLEAN disable, QC_BARRIER_REASON reason,
                       PIRP request = nullptr, ULONG code = 0);
