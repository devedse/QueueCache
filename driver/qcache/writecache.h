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
};
static constexpr ULONG QcDiagnosticsV1Size = 80;
static constexpr ULONG QcDiagnosticsV2Size = 216;
static constexpr ULONG QcDiagnosticsV3Size = 240;
static constexpr ULONG QcDiagnosticsV4Size = 408;
static constexpr ULONG QcDiagnosticsV5Size = 480;
static constexpr ULONG QcDiagnosticsV6Size = 528;
static constexpr ULONG QcDiagnosticsV7Size = 584;
static constexpr ULONG QcDiagnosticsV8Size = 672;
static_assert(sizeof(QC_DIAGNOSTICS) == 736);
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
    // Lab: BudgetBytes = range start; Value = hold ms << 32 | range bytes.
    // Value 0 disarms. Refused on any disk hosting a paging/hibernation/dump path.
    QcLabGate
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
struct QC_PAGING_READ
{
    PIRP Irp; // Null when free.
    LONGLONG Start, End;
    ULONGLONG Sequence;
    BOOLEAN Started;
};
struct QC_DRAIN_WORKER
{
    QC_CACHE* Cache;
    ULONG Number;
    HANDLE Thread;
};
struct QC_CACHE
{
    PDEVICE_OBJECT Lower;
    KMUTEX Mutex;
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
    QC_DRAIN_WORKER Workers[4];
    QC_SLOT* Slots;
    ULONG* Buckets;
    PUCHAR DrainBuffer;
    ULONG Capacity, Head, Tail, FreeHead, Count, SectorBytes, DrainCapacity;
    BOOLEAN Enabled, Barrier, Suspended, Stop, ResumeEnabled;
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
    volatile LONG64 LowerPagingForwardedReads, LowerOtherReads;
    // Offloaded paging reads. PagingLock protects the table, PagingQueued and
    // PagingStop. Only the request worker inserts; only PagingThread executes.
    // The worker never waits for the paging thread while holding Mutex, and the
    // paging thread waits only for Mutex and lower completion, never the worker.
    KSPIN_LOCK PagingLock;
    QC_PAGING_READ PagingReads[QcPagingReadSlots];
    ULONG PagingQueued;
    ULONGLONG PagingSequence;
    BOOLEAN PagingStop;
    KEVENT PagingWork, PagingDone;
    HANDLE PagingThread;
    ULONG PagingPins[QcPagingPinBlocks]; // Paging thread only.
    // Mutex. Set by the request worker: OffloadBlocked around requests that may
    // retire pinned slots or change power/media state; ActiveWrite while a write
    // (including its barrier/fence waits) owns [ActiveWriteStart, ActiveWriteEnd).
    BOOLEAN OffloadBlocked, ActiveWrite;
    LONGLONG ActiveWriteStart, ActiveWriteEnd;
    // Completes an offloaded original IRP and releases its remove lock.
    void (*CompleteRequest)(PVOID, PIRP, NTSTATUS);
    ULONG DelayMs, InjectFault;
    // Lab range gate. State/range/hold under Mutex; sequences written once each.
    ULONG LabGateState, LabGateHoldMs;
    LONGLONG LabGateStart, LabGateEnd;
    volatile LONG64 LabSequence, LabGateHits, LabGateOldSubmitSeq, LabGateOldLowerDoneSeq, LabGateOldRetireSeq;
    volatile LONG64 LabGateDirectWaitSeq, LabGateDirectSubmitSeq, LabGateDirectDoneSeq;
};
FORCEINLINE bool QcTrackedUsageNotification(PIO_STACK_LOCATION stack)
{
    return stack->MajorFunction == IRP_MJ_PNP && stack->MinorFunction == IRP_MN_DEVICE_USAGE_NOTIFICATION &&
           (stack->Parameters.UsageNotification.Type == DeviceUsageTypePaging ||
            stack->Parameters.UsageNotification.Type == DeviceUsageTypeHibernation ||
            stack->Parameters.UsageNotification.Type == DeviceUsageTypeDumpFile);
}
NTSTATUS QcCacheInitialize(QC_CACHE* cache, PDEVICE_OBJECT lower);
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
void QcCacheRecordPagingIo(QC_CACHE* cache, PIRP irp);
LONG QcCachePagingPathCount(QC_CACHE* cache);
void QcCachePerformance(QC_CACHE* cache, QC_PERFORMANCE* output);
bool QcCacheTryReadHit(QC_CACHE* cache, PIRP irp, LONGLONG deviceBytes, NTSTATUS* status);
bool QcCacheTryPagingReadProgress(QC_CACHE* cache, PIRP irp, LONGLONG deviceBytes, NTSTATUS* status);
// Request worker only. True: the paging thread now owns and will complete irp.
bool QcCacheOffloadPagingRead(QC_CACHE* cache, PIRP irp, LONGLONG deviceBytes);
bool QcCachePagingReadsOutstanding(QC_CACHE* cache);
void QcCacheWaitPagingReads(QC_CACHE* cache);
// *transferred: the paging thread owns irp; the caller must not complete it.
NTSTATUS QcCacheProcess(QC_CACHE* cache, PIRP irp, LONGLONG deviceBytes, bool* transferred);
NTSTATUS QcCacheBarrier(QC_CACHE* cache, BOOLEAN disable, QC_BARRIER_REASON reason,
                       PIRP request = nullptr, ULONG code = 0);
