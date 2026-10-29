// SPDX-License-Identifier: MIT
#pragma once
#include <ntifs.h>
#include <ntddstor.h> // DEVICE_DATA_SET_RANGE (asynchronous TRIM fences)
#include "cachepolicy.h"

#define IOCTL_QCACHE_STATE_V1 CTL_CODE(0x8844UL, 0xD10UL, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_QCACHE_CONTROL_V1 CTL_CODE(0x8844UL, 0xD11UL, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS)
#define IOCTL_QCACHE_DIAGNOSTICS_V1 CTL_CODE(0x8844UL, 0xD12UL, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_QCACHE_STATE_V2 CTL_CODE(0x8844UL, 0xD13UL, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_QCACHE_STATE_V3 CTL_CODE(0x8844UL, 0xD14UL, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_QCACHE_OPTIONS_V1 CTL_CODE(0x8844UL, 0xD15UL, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS)
#define IOCTL_QCACHE_PERFORMANCE_V1 CTL_CODE(0x8844UL, 0xD16UL, METHOD_BUFFERED, FILE_ANY_ACCESS)
// Read-only usage map: QC_LAYOUT_MAP followed by one QC_LAYOUT_CHUNK per 256 KiB chunk, as many as fit.
#define IOCTL_QCACHE_LAYOUT_MAP_V1 CTL_CODE(0x8844UL, 0xD1AUL, METHOD_BUFFERED, FILE_ANY_ACCESS)
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
    QcRangesReference = 2,
    QcRangesSafePaging = 3,
    QcRangesLabPaging = 4 // Verification only: forced-direct writes are synthetic page-file writes.
};
struct QC_SPECIAL_RANGES_HEADER
{
    ULONG Version, Size, Count, Flags;
    ULONGLONG Generation;
};
static_assert(sizeof(QC_SPECIAL_RANGE) == 16 && sizeof(QC_SPECIAL_RANGES_HEADER) == 24);
static constexpr ULONG QcSpecialRangeMax = 256;
#include "pagingorder.h"
enum QC_SAFE_CLEAR_REASON : ULONG
{
    QcSafeEnable = 1, QcSafeDisable, QcSafeAllocation, QcSafeMode, QcSafeUsage,
    QcSafeRemoval, QcSafeCachedWrite, QcSafeDirectWrite, QcSafeTrim, QcSafeSeed
};
// Durations are QPC ticks, converted using Frequency. Counters are lifetime cumulative.
// V2 appends opt-in cooperative-read diagnostics; V3 appends drain phase timings.
// V4 appends batch termination reasons and size buckets (one reason/bucket per batch).
// V5 appends runtime gather policy, gap contents and backward-stop attribution.
// The first 192/408/432/512 bytes remain V1/V2/V3/V4.
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
    ULONGLONG DrainEndLimit, DrainEndGap, DrainEndInFlight, DrainEndFilling, DrainEndPartial, DrainEndFence;
    ULONGLONG DrainPartialAnchor, DrainSizeSmall, DrainSizeMedium, DrainSizeLarge;
    // V5: runtime-only lab policy, gap contents and one backward stop per batch.
    ULONGLONG DrainGatherPolicy, DrainGapCleanFull, DrainGapCleanPartial, DrainGapAbsent;
    ULONGLONG DrainBackLimit, DrainBackGap, DrainBackInFlight, DrainBackFilling;
    ULONGLONG DrainBackPartial, DrainBackFence, DrainBackBoundary, DrainBackAligned;
    ULONGLONG DrainGapFilling;
};
static constexpr ULONG QcPerformanceV1Size = 192;
static constexpr ULONG QcPerformanceV2Size = 408;
static constexpr ULONG QcPerformanceV3Size = 432;
static constexpr ULONG QcPerformanceV4Size = 512;
static_assert(sizeof(QC_PERFORMANCE) == 616);
static_assert(FIELD_OFFSET(QC_PERFORMANCE, DrainGatherPolicy) == QcPerformanceV4Size);
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
    // V13: read misses kept as clean cache blocks (all reads / application paging reads).
    // Paging read misses were not kept from plan 60 until #19, which keeps them from a
    // driver-owned copy (StagedRead).
    ULONGLONG ReadFills, PagingReadFills;
    // V14: requests served on the caller's thread without the request worker, and
    // caller-thread attempts that handed the request to the worker unchanged.
    ULONGLONG CallerPathReads, CallerPathWrites, CallerPathDeclined;
    // V15: large RAM-hit reads handed to the offloaded-read threads for a parallel copy.
    ULONGLONG CopyOffloadReads;
    // V16: large fitting writes whose payload copy was handed to those threads.
    ULONGLONG CopyOffloadWrites;
    // V17: paging read misses whose buffer repeats a physical page (the memory
    // manager's dummy page; such a buffer is never the source of kept data), and
    // misses not kept because no staging copy could be made (before plan 62: because
    // their buffer repeated a page).
    ULONGLONG PagingReadsRepeatedPages, ReadFillsSkippedRepeatedPages;
    // V18: last QcLabMeasureLayout result (Measurements = 0: never measured). Of the
    // indexed blocks, Neighbors also have the next disk block cached; Contiguous/Reversed
    // hold it in the next/previous 4 KiB of memory. FreeChunks: 256 KiB slabs with every slot free.
    ULONGLONG LayoutMeasurements, LayoutBlocks, LayoutNeighbors, LayoutContiguous, LayoutReversed, LayoutFreeChunks;
    // V19: QcLabCopyFlags in effect (QcCopyPrefetch | QcCopyCoalesce), so evidence records the copy mode.
    ULONGLONG CopyFlags;
    // V20: QcLabReadRecall in effect (readrecall.h), read misses kept as recent because their
    // block was used more recently than the oldest used block, and history matches not kept.
    ULONGLONG ReadRecall, RecalledFills, RecallDenied;
    // V21: submitted driver-owned whole-range reads. CachedBytes measures known
    // sectors pinned before submission, including dirty data later overlaid.
    // Attempts include lower failures; allocation failures submit nothing.
    ULONGLONG StagedReadRequests, StagedReadBytes, MixedStagedReads, StagedReadCachedBytes;
    // V22: reserved prefix for the rejected RAM queue experiment, always zero.
    ULONGLONG RamReadQueueMode, RamReadQueued, RamReadCompleted, RamReadCancelled;
    ULONGLONG RamReadInline, RamReadQueueFull, RamReadFallback;
    // V23: caller-routing candidates and mutually exclusive first-decline reasons.
    ULONGLONG CallerBackoff, CallerCandidates, CallerControls, CallerQueued;
    ULONGLONG CallerWorkerActive, CallerOwnerActive, CallerOffloaded, CallerBackoffRequests, CallerProbes;
    // V24: QcLabReadAware limit and drainer waits it caused; QcLabTrimMode, TRIMs that
    // dropped cached data by range lookup, lower TRIMs sent asynchronously, and
    // asynchronous TRIMs that fell back to the ordered path (fence or table full).
    ULONGLONG ReadAware, ReadAwareWaits, TrimMode, TrimLookups, TrimAsync, TrimAsyncFallbacks;
    // V25: Strict's backlog (#16): separate pieces of unwritten data now held, the limit in effect
    // (QcStrictPieceLimit; 0 in Fast mode) and the waits of writes held because the backlog was at it
    // (counted per wait, like CapacityWaits).
    ULONGLONG StrictPieces, StrictPieceLimit, StrictPieceWaits;
    // V26: experimental volatile write ordering (#13), sealed flush groups and lower flushes.
    ULONGLONG FlushOrderMode, WriteEpoch, DrainEpoch, EpochSeals, EpochFlushes;
    ULONGLONG EpochVersionCopies, EpochTrimWaits, EpochFlushFailures, EpochMaxSubmitted;
    // V27 (#31): lab mode 2's groups finished by forced-unit-access writes without a lower flush,
    // those writes, whether the lower device reported write-through support when mode 2 was
    // selected (0 for other modes), and performance-counter ticks in group flushes (timing on only).
    ULONGLONG EpochWriteThroughGroups, EpochWriteThroughWrites, EpochWriteThroughAvailable, EpochFlushTicks;
    // V28: paging-file ordering. Classification write counters are dispatch-time, write-only.
    ULONGLONG PagingOrderBypasses, PagingOrderBypassBytes, PagingOrderLearnBarriers, UnidentifiedDirectBarriers;
    ULONGLONG SafeRangeOverlapFound, SafeRangeTableFull, SafeRangeClears, SafeRangeCount, SafeRangeLastClearReason;
    ULONGLONG PagingFileWriteRequests, PagingWriteNoFileObject, PagingWriteHighIrql;
    ULONGLONG SafeMutationSequence; // Seed token: rejects maps captured across other writes.
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
static constexpr ULONG QcDiagnosticsV17Size = 896;
static constexpr ULONG QcDiagnosticsV18Size = 944;
static constexpr ULONG QcDiagnosticsV19Size = 952;
static constexpr ULONG QcDiagnosticsV20Size = 976;
static constexpr ULONG QcDiagnosticsV21Size = 1008;
static constexpr ULONG QcDiagnosticsV22Size = 1064;
static constexpr ULONG QcDiagnosticsV23Size = 1136;
static constexpr ULONG QcDiagnosticsV24Size = 1184;
static constexpr ULONG QcDiagnosticsV25Size = 1208;
static constexpr ULONG QcDiagnosticsV26Size = 1280;
static constexpr ULONG QcDiagnosticsV27Size = 1312;
static_assert(sizeof(QC_DIAGNOSTICS) == 1416);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, PagingOrderBypasses) == QcDiagnosticsV27Size);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, EpochWriteThroughGroups) == QcDiagnosticsV26Size);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, FlushOrderMode) == QcDiagnosticsV25Size);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, StrictPieces) == QcDiagnosticsV24Size);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, ReadAware) == QcDiagnosticsV23Size);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, CallerBackoff) == QcDiagnosticsV22Size);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, RamReadQueueMode) == QcDiagnosticsV21Size);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, StagedReadRequests) == QcDiagnosticsV20Size);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, ReadRecall) == QcDiagnosticsV19Size);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, CopyFlags) == QcDiagnosticsV18Size);
static_assert(FIELD_OFFSET(QC_DIAGNOSTICS, LayoutMeasurements) == QcDiagnosticsV17Size);
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
    QcCallerPath,
    // Diagnostic only: on an empty cache, mark every chunk wholly free and close the open
    // chunk, keeping buffers and generation unchanged. No parameters; limited to 2 GiB,
    // no usage paths.
    QcLabResetFreeOrder,
    // Diagnostic only: count block/memory adjacency into the V18 diagnostics. No
    // parameters; holds the cache lock for one pass over the slots.
    QcLabMeasureLayout,
    // Lab: Value = QcCopyPrefetch | QcCopyCoalesce for RAM-hit copies (both on by default).
    // Changes only how hits are copied, never what is cached; read live, runtime only.
    QcLabCopyFlags,
    // Lab: Value 1 (default) = read recall (readrecall.h), 0 = the earlier bimodal insertion.
    // Changes only where a read miss enters the clean list; clears the history. Runtime only.
    QcLabReadRecall,
    // Reserved action for the rejected RAM queue: 0 no-op; 1/2 unsupported.
    QcLabRamReadQueue,
    // Lab: post-overlap caller backoff, 0..256 (default 256). Busy/queued/active
    // ownership checks still apply to every request. Runtime only.
    QcLabCallerBackoff,
    // Reserved rejected gather experiment: 0 harmless reset; 1 unsupported.
    QcLabDrainGather,
    // Lab: Value 0 = off; 1..8 (default QcDefaultReadAware) = while foreground lower reads are in flight
    // or completed within QcReadAwareHoldMs, start a drain batch only while fewer than
    // Value batches are outstanding. Barriers and paging-write fences are never held.
    QcLabReadAware,
    // Lab: Value = QcTrimLookup | QcTrimAsync | QcTrimLabHold (default QcDefaultTrimMode;
    // 0: full slot scan, ordered lower TRIM).
    QcLabTrimMode,
    // Experimental #13 ordering, 0/1 at a clean disabled boundary; never persisted.
    QcLabFlushOrder
};
enum : ULONG
{
    // Drop cached data of small TRIMs by index lookup per trimmed block instead of a full slot scan.
    QcTrimLookup = 1,
    // Apply the TRIM to cached data, fence its ranges from draining, and send the lower
    // TRIM from an offload thread, so the ordered request path continues meanwhile.
    QcTrimAsync = 2,
    // Lab (trim-cache): hold each asynchronous lower TRIM for QcTrimLabHoldMs first.
    QcTrimLabHold = 4
};
// Defaults since plan 164 (issue #15, plans 162/163): with Eager 2048 KiB x 8 drains, a
// concurrent disk reader keeps the 1024 KiB x 4 default's IOPS, and mass-delete TRIMs no longer
// stall the ordered request path. The lab switches still set any value for A/B.
static constexpr LONG QcDefaultReadAware = 2;
static constexpr LONG QcDefaultTrimMode = QcTrimLookup | QcTrimAsync;
static constexpr ULONG QcTrimLabHoldMs = 2000;
static constexpr ULONG QcReadAwareHoldMs = 10;
static constexpr ULONG QcTrimFenceSlots = 16;
struct QC_TRIM_FENCE
{
    DEVICE_DATA_SET_RANGE* Ranges; // Sorted, merged, owned (pool); null when the slot is free.
    ULONG Count;
    LONGLONG Low, High;
    ULONGLONG Epoch; // Ordered Fast: this lower TRIM belongs between sealed write groups.
}; // Toggle optional detailed timing; never resets counters.
enum : ULONG
{
    QcCopyPrefetch = 1, // Prefetch the next block's buffer while copying the current one.
    QcCopyCoalesce = 2, // Copy whole valid blocks that are memory neighbours with one copy.
    // Pinned read hits look each block up once (not once to check and again to pin) and move runs
    // of blocks that are already neighbours in their recency list in one step, leaving the same
    // order. Cached 1 MiB reads held the cache lock about 11 us each (plan 120; on by default).
    QcCopyBookkeeping = 4,
    // An ordinary read that misses (up to 1 MiB) runs on an offloaded-read thread instead of the request
    // worker, which waited for each disk read so misses reached the disk one at a time (issue #14). It is
    // kept in the cache as on the worker: writes wait for overlapping offloaded reads. On by default (plan 125).
    QcCopyOffloadMisses = 8
};
struct QC_SLOT
{
    PUCHAR Buffer;
    LARGE_INTEGER Offset;
    ULONG Length, HashNext, HashPrevious, QueueNext, QueuePrevious;
    BOOLEAN InFlight;
    BOOLEAN Dirty, ReadClass;
    ULONG Pins;
    BOOLEAN Filling, RetireWhenUnpinned;
    BOOLEAN PieceStart; // Strict (#16): this dirty version began a separate piece of unwritten data (StrictPieces)
    ULONG ValidSectors; // sectorcoverage.h: unknown sectors must never be read from this buffer or drained
    ULONGLONG DirtySince;
    ULONGLONG Epoch; // Dirty versions from a sealed group are immutable even before submission.
};
struct QC_CACHE;
// Free slots of one 256 KiB slab (64 slots, bit set = free). A chunk with any free slot
// is in one of two lists (partially / wholly free), linked by chunk index.
struct QC_CHUNK
{
    ULONGLONG Free;
    ULONG Next, Previous;
};
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
    BOOLEAN Trim; // Asynchronous lower TRIM of TrimFences[Fence]; ignored by range waits.
    ULONG Fence;
    PUCHAR Source; // Write: the mapped caller buffer.
};
struct QC_DRAIN_WORKER
{
    QC_CACHE* Cache;
    ULONG Number;
    HANDLE Thread;
    // Mutex: disk range of this drainer's one outstanding lower write; empty when equal.
    LONGLONG InFlightStart, InFlightEnd;
};
// Offloaded-request executors: reads that need the disk (paging reads, and ordinary
// misses with QcCopyOffloadMisses), large reads served entirely from RAM and large
// fitting writes (QcCopyOffloadMinBytes), so their disk waits and copies overlap
// instead of running one after another on the request worker. Each thread waits for
// one disk read at a time, so this is the number of misses at the disk at once (3
// before plan 125).
static constexpr ULONG QcReadThreads = 16;
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
    // Strict (#16), Mutex: dirty versions marked PieceStart, and pieces the drain completed per second of
    // time with drain writes in flight (0: not measured yet), for QcStrictPieceLimit. DrainBusySince is
    // interrupt time; PieceRateDone/PieceRateTime accumulate the current measurement window.
    ULONG StrictPieces;
    ULONGLONG DrainBusySince, PieceRateDone, PieceRateTime, PiecesPerSecond;
    LONG FlushOrderMode; // Writes hold Mutex and RoutingLock; dispatch reads under RoutingLock.
    ULONGLONG WriteEpoch, DrainEpoch;
    BOOLEAN EpochHasWrites, EpochFlushInFlight;
    // Mutex. Lab mode 2 (#31): the frontier group sent a write without FUA, or a TRIM, so it
    // needs a lower flush; and whether the lower device reported write-through support.
    BOOLEAN EpochNeedsFlush, LowerWriteThrough;
    ULONG EpochWaiters;
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
    QC_DRAIN_WORKER Workers[8]; // Ceiling; eight active drainers by default since plan 164.
    QC_SLOT* Slots;
    // One MDL per 256 KiB payload slab: physical pages from the memory manager
    // (MmAllocatePagesForMdlEx), mapped once, never nonpaged pool. Indexed by
    // slot / SlotsPerSlab; the slab's mapping is Slots[slab * SlotsPerSlab].Buffer.
    PMDL* SlabMdls;
    ULONG* Buckets;
    PUCHAR DrainBuffer;
    ULONG Capacity, Head, Tail, Count, SectorBytes, DrainCapacity;
    // Mutex: chunk allocator (cacheblocks.inl). ChunkHead[0] partially free, [1] wholly free.
    QC_CHUNK* Chunks;
    ULONG ChunkHead[2], OpenChunk, OpenNext;
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
    BOOLEAN OwnedRamDevice;   // Correlated against an active kernel-owned budget reservation.
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
    ULONG ReadFillsSinceRecent; // Mutex: bimodal read-fill insertion counter (QcLabReadRecall 0).
    // Mutex: read-recall history, one entry per slot in RecallSets 4-way sets (readrecall.h).
    ULONG* Recall;
    ULONG RecallSets;
    volatile LONG ReadRecall; // QcLabReadRecall mode.
    volatile LONG64 RecalledFills, RecallDenied;
    volatile LONG CallerPath; // QcCallerPath mode, read by dispatch.
    volatile LONG CopyFlags;  // QcLabCopyFlags, read by RAM-hit copies.
    volatile LONG ReadAware;  // QcLabReadAware limit (0 off, QcDefaultReadAware), read by drainers.
    // Foreground lower reads in flight and the last completion (NowMs), from every
    // read the filter sends down for a request (worker, offload threads, direct path).
    volatile LONG ForegroundReads;
    volatile LONG64 LastForegroundReadMs, ReadAwareWaits;
    volatile LONG TrimMode; // QcLabTrimMode (QcDefaultTrimMode).
    // Mutex: ranges of asynchronous lower TRIMs not yet completed. Drainers never
    // select a version inside one, and forwarded writes wait for an overlapping one.
    QC_TRIM_FENCE TrimFences[QcTrimFenceSlots];
    ULONG TrimFenceCount;
    volatile LONG64 TrimLookups, TrimAsync, TrimAsyncFallbacks;
    volatile LONG64 CallerPathReads, CallerPathWrites, CallerPathDeclined, CopyOffloadReads, CopyOffloadWrites;
    volatile LONG64 PagingReadsRepeatedPages, ReadFillsSkippedRepeatedPages;
    volatile LONG64 StagedReadRequests, StagedReadBytes, MixedStagedReads, StagedReadCachedBytes;
    volatile LONG64 LowerPagingForwardedReads, LowerOtherReads;
    // Mutex (written by QcLabMeasureLayout; copied without the lock, values may be one measurement apart).
    ULONGLONG LayoutMeasurements, LayoutBlocks, LayoutNeighbors, LayoutContiguous, LayoutReversed, LayoutFreeChunks;
    // Offloaded paging reads. PagingLock protects the table, PagingQueued and
    // PagingStop. PagingQueued counts read and write-copy entries, not asynchronous
    // TRIMs. Only the request worker inserts; only ReadThreads execute.
    // The worker never waits for the paging thread while holding Mutex, and the
    // paging thread waits only for Mutex and lower completion, never the worker.
    KSPIN_LOCK PagingLock;
    QC_PAGING_READ PagingReads[QcPagingReadSlots];
    ULONG PagingQueued;
    volatile LONG CallerBackoff;
    volatile LONG64 CallerCandidates, CallerControls, CallerQueued, CallerWorkerActive;
    volatile LONG64 CallerOwnerActive, CallerOffloaded, CallerBackoffRequests, CallerProbes;
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
    BOOLEAN ForceDirectPaging; // RangeLock, lab-only classification override.
    QC_SAFE_PAGING SafePaging; // Mutex; never read from dispatch.
    volatile LONG64 PagingFileWriteRequests, PagingWriteNoFileObject, PagingWriteHighIrql;
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
// PASSIVE_LEVEL PnP transition, serialized with RAM admission. Never waits for lower I/O.
bool QcCacheDisconnect(QC_CACHE* cache, QC_STATE* snapshot);
void QcCacheSnapshot(QC_CACHE* cache, QC_STATE* output);
void QcCacheSnapshotV2(QC_CACHE* cache, QC_STATE_V2* output);
void QcCacheSnapshotV3(QC_CACHE* cache, QC_STATE_V3* output);
void QcCacheDiagnostics(QC_CACHE* cache, QC_DIAGNOSTICS* output);
void QcCacheRecordLowerAttempt(QC_CACHE* cache, ULONG major, PIRP original = nullptr);
// A foreground lower read completed: pairs with an increment of ForegroundReads.
void QcCacheForegroundReadDone(QC_CACHE* cache);
void QcCacheRecordUsage(QC_CACHE* cache, DEVICE_USAGE_NOTIFICATION_TYPE type, BOOLEAN inPath);
void QcCacheRecordUsageRequest(QC_CACHE* cache, DEVICE_USAGE_NOTIFICATION_TYPE type, BOOLEAN inPath,
                               ULONGLONG processId);
void QcCacheRecordUsageCompletion(QC_CACHE* cache, DEVICE_USAGE_NOTIFICATION_TYPE type, BOOLEAN inPath,
                                  NTSTATUS status);
bool QcCacheRecordPagingIo(QC_CACHE* cache, PIRP irp);
LONG QcCachePagingPathCount(QC_CACHE* cache);
void QcCachePerformance(QC_CACHE* cache, QC_PERFORMANCE* output);
// Per-chunk usage (Used/Dirty/Read slots; Ordered: slots whose next slot holds the next disk block).
struct QC_LAYOUT_CHUNK
{
    UCHAR Used, Dirty, Read, Ordered;
};
struct QC_LAYOUT_MAP
{
    ULONG Version, Size, Chunks, Returned;
    ULONGLONG ChunkBytes, Generation;
};
static_assert(sizeof(QC_LAYOUT_CHUNK) == 4 && sizeof(QC_LAYOUT_MAP) == 32);
// Fills up to maxChunks entries; the lock is released between batches, so a reallocation ends the map early.
void QcCacheLayoutMap(QC_CACHE* cache, QC_LAYOUT_MAP* header, QC_LAYOUT_CHUNK* chunks, ULONG maxChunks);
bool QcCacheTryReadHit(QC_CACHE* cache, PIRP irp, LONGLONG deviceBytes, NTSTATUS* status);
// Dispatch only, at PASSIVE_LEVEL, while no other foreground request can run (the
// request worker waits for DirectIdle before processing). True: *status is final
// and the caller completes irp. False: nothing changed; queue irp for the worker.
bool QcCacheTryCallerPath(QC_CACHE* cache, PIRP irp, LONGLONG deviceBytes, NTSTATUS* status);
bool QcCacheTryPagingReadProgress(QC_CACHE* cache, PIRP irp, LONGLONG deviceBytes, NTSTATUS* status);
// Request worker only. True: an offloaded-read thread now owns and will complete irp.
bool QcCacheOffloadPagingRead(QC_CACHE* cache, PIRP irp, LONGLONG deviceBytes, bool miss = false);
bool QcCachePagingReadsOutstanding(QC_CACHE* cache);
void QcCacheWaitPagingReads(QC_CACHE* cache);
// *transferred: the paging thread owns irp; the caller must not complete it.
NTSTATUS QcCacheProcess(QC_CACHE* cache, PIRP irp, LONGLONG deviceBytes, bool* transferred);
NTSTATUS QcCacheBarrier(QC_CACHE* cache, BOOLEAN disable, QC_BARRIER_REASON reason,
                       PIRP request = nullptr, ULONG code = 0);
