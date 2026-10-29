// SPDX-License-Identifier: MIT
// Serialized block cache: dirty FIFO, clean write/read LRUs and a single versioned index.
#include "writecache.h"
#include <ntddstor.h>
#include <ntdddisk.h>
#include <ntddscsi.h>
#include "observation.h"
#include "sectorcoverage.h"
#include "readrecall.h"
#include "cacheallocation.h"
#include "../shared/lockedpages.h"
#include "../shared/memorybudget.h"
static constexpr ULONG Chunk = 4096, SlabBytes = 262144, SlotsPerSlab = SlabBytes / Chunk, Tag = 'wCCQ';
static constexpr ULONG NoSlot = MAXULONG;
static constexpr ULONG MaxBatchBytes = 2 * 1024 * 1024; // Ceiling and default since plan 164.
static QC_MEMORY_BUDGET SharedMemoryBudget{};
static volatile LONG64 NextInstance;
static ULONGLONG MemoryLimit()
{
    auto ranges = MmGetPhysicalMemoryRangesEx2(nullptr, 0);
    if (!ranges)
        return 0;
    ULONGLONG total = 0;
    for (auto r = ranges; r->NumberOfBytes.QuadPart; ++r)
        total += r->NumberOfBytes.QuadPart;
    ExFreePool(ranges);
    return min(128ULL << 30, total / 4 * 3);
}
QC_MEMORY_BUDGET* QcSharedMemoryBudget() { return &SharedMemoryBudget; }
void QcInitializeMemoryBudget()
{
    InterlockedExchange64(&SharedMemoryBudget.LimitBytes, static_cast<LONG64>(MemoryLimit()));
}

static ULONGLONG NowMs()
{
    return KeQueryInterruptTime() / 10000;
}
void QcCacheForegroundReadDone(QC_CACHE* c)
{
    InterlockedExchange64(&c->LastForegroundReadMs, static_cast<LONG64>(NowMs()));
    InterlockedDecrement(&c->ForegroundReads);
}
#include "cacheblocks.inl"
static void PagingReader(PVOID context);
struct QC_PAGING_READ;
static NTSTATUS RunAsyncTrim(QC_CACHE* c, const QC_PAGING_READ* entry);
static bool TrimsOverlap(QC_CACHE* c, LONGLONG first, LONGLONG end);
static bool RangesOverlap(const QC_SPECIAL_RANGE* ranges, ULONG count, LONGLONG first, LONGLONG end);
// Lab gate evidence: store the next per-disk sequence once. Returns true if set.
static bool RecordLabSequence(QC_CACHE* c, volatile LONG64* field)
{
    return InterlockedCompareExchange64(field, InterlockedIncrement64(&c->LabSequence), 0) == 0;
}
static bool LabGateOverlaps(QC_CACHE* c, LONGLONG first, LONGLONG end)
{
    return c->LabGateState != QcLabGateOff && first < c->LabGateEnd && end > c->LabGateStart;
}
static ULONGLONG Tick()
{
    return KeQueryPerformanceCounter(nullptr).QuadPart;
}
static void AcquireCache(QC_CACHE* c)
{
    const auto start = c->Timing ? Tick() : 0;
    KeEnterCriticalRegion();
    ExAcquirePushLockExclusiveEx(&c->Mutex, EX_DEFAULT_PUSH_LOCK_FLAGS);
    c->LockStarted = c->Timing ? Tick() : 0;
    if (start && c->LockStarted)
    {
        const auto wait = c->LockStarted - start;
        ++c->Performance.LockAcquires;
        c->Performance.LockWaitTicks += wait;
        c->Performance.MaxLockWaitTicks = max(c->Performance.MaxLockWaitTicks, wait);
    }
}
static void ReleaseCache(QC_CACHE* c)
{
    if (c->LockStarted)
    {
        auto held = Tick() - c->LockStarted;
        c->Performance.LockHoldTicks += held;
        c->Performance.MaxLockHoldTicks = max(c->Performance.MaxLockHoldTicks, held);
    }
    ExReleasePushLockExclusiveEx(&c->Mutex, EX_DEFAULT_PUSH_LOCK_FLAGS);
    KeLeaveCriticalRegion();
}
// Mutex held on every safe-set access. Clearing does not allocate or wait.
static void AdvanceSafeMutation(QC_CACHE* c)
{
    // Saturation refuses future seeds rather than reusing an old token.
    if (c->Diagnostics.SafeMutationSequence != MAXULONGLONG) ++c->Diagnostics.SafeMutationSequence;
}
static void ClearSafePaging(QC_CACHE* c, QC_SAFE_CLEAR_REASON reason)
{
    AdvanceSafeMutation(c);
    if (c->SafePaging.Count)
    {
        ++c->Diagnostics.SafeRangeClears;
        c->Diagnostics.SafeRangeLastClearReason = reason;
        c->SafePaging.Count = 0;
        c->SafePaging.Low = c->SafePaging.High = 0;
    }
}
static void InvalidateSafePaging(QC_CACHE* c, LONGLONG first, LONGLONG end, QC_SAFE_CLEAR_REASON reason)
{
    if (!c->FlushOrderMode && !c->SafePaging.Count) return;
    AdvanceSafeMutation(c);
    if (QcSafePagingOverlaps(c->SafePaging, first, end)) ClearSafePaging(c, reason);
}
static void WakeDrainers(QC_CACHE* c)
{
    // NotificationEvent is level-triggered. Re-signalling an already-set event
    // adds scheduler work without waking any additional waiter.
    if (!KeReadStateEvent(&c->Wake))
    {
        ++c->Performance.WakeSignals;
        KeSetEvent(&c->Wake, IO_NO_INCREMENT, FALSE);
    }
}
// Caller holds Mutex. Dispatch can read the small coherent snapshot at DISPATCH_LEVEL.
static void Publish(QC_CACHE* c)
{
    c->State.Version = 1;
    c->State.Size = sizeof(QC_STATE);
    c->State.Flags = (c->Enabled ? 1UL : 0UL) | (!NT_SUCCESS(c->State.LastError) ? 2UL : 0UL) |
                     (c->Suspended ? 4UL : 0UL) | (c->Barrier ? 8UL : 0UL) | (c->Gone ? 16UL : 0UL) |
                     (c->UnsafeDefer ? 32UL : 0UL) | 64UL | 128UL | 256UL | 1024UL |
                     2048UL | 4096UL; // 4096: Deferred policy and one-hour age support.
    c->State.OccupiedSlots = c->Count;
    KIRQL irql;
    KeAcquireSpinLock(&c->SnapshotLock, &irql);
    c->Snapshot = c->State;
    c->ExtendedSnapshot.Base = c->State;
    c->ExtendedSnapshot.Base.Version = 2;
    c->ExtendedSnapshot.Base.Size = sizeof(QC_STATE_V2);
    c->ExtendedSnapshot.DiscardedBytes = c->DiscardedBytes;
    c->ExtendedSnapshot.LowerWrites = c->LowerWrites;
    c->ExtendedSnapshot.BatchedWrites = c->BatchedWrites;
    c->ExtendedSnapshot.TrimRequests = c->TrimRequests;
    c->ReadWriteSnapshot.Base = c->ExtendedSnapshot;
    c->ReadWriteSnapshot.Base.Base.Version = 3;
    c->ReadWriteSnapshot.Base.Base.Size = sizeof(QC_STATE_V3);
    c->ReadWriteSnapshot.Options = c->Options;
    c->ReadWriteSnapshot.CleanReadBytes = c->CleanValidBytes[1];
    c->ReadWriteSnapshot.CleanWriteBytes = c->CleanValidBytes[0];
    c->ReadWriteSnapshot.ReadHitBytes = c->ReadHitBytes;
    c->ReadWriteSnapshot.ReadMissBytes = c->ReadMissBytes;
    c->ReadWriteSnapshot.Evictions = c->Evictions;
    c->ReadWriteSnapshot.OldestDirtyMs = c->Head == NoSlot ? 0 : NowMs() - c->Slots[c->Head].DirtySince;
    c->ReadWriteSnapshot.Generation = c->Generation;
    c->ReadWriteSnapshot.Instance = c->Instance;
    c->ReadWriteSnapshot.GlobalLimitBytes = QcMemoryLimit(&SharedMemoryBudget);
    c->ReadWriteSnapshot.GlobalReservedBytes = QcReservedMemory(&SharedMemoryBudget);
    c->Diagnostics.Version = 6;
    c->Diagnostics.Size = sizeof(QC_DIAGNOSTICS);
    c->Diagnostics.StrictPieces = c->StrictPieces;
    c->Diagnostics.StrictPieceLimit = c->UnsafeDefer ? 0 : QcStrictPieceLimit(c->PiecesPerSecond);
    c->Diagnostics.FlushOrderMode = c->FlushOrderMode;
    c->Diagnostics.WriteEpoch = c->WriteEpoch;
    c->Diagnostics.DrainEpoch = c->DrainEpoch;
    c->Diagnostics.SafeRangeCount = c->SafePaging.Count;
    c->DiagnosticsSnapshot = c->Diagnostics;
    c->Performance.Version = 5;
    c->Performance.Size = sizeof(QC_PERFORMANCE);
    c->Performance.TimingEnabled = c->Timing != 0;
    c->PerformanceSnapshot = c->Performance;
    KeReleaseSpinLock(&c->SnapshotLock, irql);
}
void QcCacheSnapshot(QC_CACHE* c, QC_STATE* output)
{
    KIRQL irql;
    KeAcquireSpinLock(&c->SnapshotLock, &irql);
    *output = c->Snapshot;
    KeReleaseSpinLock(&c->SnapshotLock, irql);
}
void QcCacheSnapshotV2(QC_CACHE* c, QC_STATE_V2* output)
{
    KIRQL irql;
    KeAcquireSpinLock(&c->SnapshotLock, &irql);
    *output = c->ExtendedSnapshot;
    KeReleaseSpinLock(&c->SnapshotLock, irql);
}
void QcCacheSnapshotV3(QC_CACHE* c, QC_STATE_V3* output)
{
    KIRQL irql;
    KeAcquireSpinLock(&c->SnapshotLock, &irql);
    *output = c->ReadWriteSnapshot;
    output->GlobalReservedBytes = QcReservedMemory(&SharedMemoryBudget);
    output->GlobalLimitBytes = QcMemoryLimit(&SharedMemoryBudget);
    KeReleaseSpinLock(&c->SnapshotLock, irql);
}
void QcCacheDiagnostics(QC_CACHE* c, QC_DIAGNOSTICS* output)
{
    KIRQL irql;
    KeAcquireSpinLock(&c->SnapshotLock, &irql);
    *output = c->DiagnosticsSnapshot;
    output->LowerGeneratedWrites = InterlockedCompareExchange64(&c->LowerGeneratedWrites, 0, 0);
    output->LowerAllocationRetries = InterlockedCompareExchange64(&c->LowerAllocationRetries, 0, 0);
    output->PagingFileBypasses = InterlockedCompareExchange64(&c->PagingFileBypasses, 0, 0);
    output->PagingFileWriteRequests = InterlockedCompareExchange64(&c->PagingFileWriteRequests, 0, 0);
    output->PagingWriteNoFileObject = InterlockedCompareExchange64(&c->PagingWriteNoFileObject, 0, 0);
    output->PagingWriteHighIrql = InterlockedCompareExchange64(&c->PagingWriteHighIrql, 0, 0);
    output->ReadFills = InterlockedCompareExchange64(&c->ReadFills, 0, 0);
    output->PagingReadFills = InterlockedCompareExchange64(&c->PagingReadFills, 0, 0);
    output->CallerPathReads = InterlockedCompareExchange64(&c->CallerPathReads, 0, 0);
    output->CallerPathWrites = InterlockedCompareExchange64(&c->CallerPathWrites, 0, 0);
    output->CallerPathDeclined = InterlockedCompareExchange64(&c->CallerPathDeclined, 0, 0);
    output->CopyOffloadReads = InterlockedCompareExchange64(&c->CopyOffloadReads, 0, 0);
    output->CopyOffloadWrites = InterlockedCompareExchange64(&c->CopyOffloadWrites, 0, 0);
    output->PagingReadsRepeatedPages = InterlockedCompareExchange64(&c->PagingReadsRepeatedPages, 0, 0);
    output->ReadFillsSkippedRepeatedPages = InterlockedCompareExchange64(&c->ReadFillsSkippedRepeatedPages, 0, 0);
    output->LayoutMeasurements = c->LayoutMeasurements;
    output->LayoutBlocks = c->LayoutBlocks;
    output->LayoutNeighbors = c->LayoutNeighbors;
    output->LayoutContiguous = c->LayoutContiguous;
    output->LayoutReversed = c->LayoutReversed;
    output->LayoutFreeChunks = c->LayoutFreeChunks;
    output->CopyFlags = static_cast<ULONG>(InterlockedCompareExchange(&c->CopyFlags, 0, 0));
    output->ReadRecall = static_cast<ULONG>(InterlockedCompareExchange(&c->ReadRecall, 0, 0));
    output->RecalledFills = InterlockedCompareExchange64(&c->RecalledFills, 0, 0);
    output->RecallDenied = InterlockedCompareExchange64(&c->RecallDenied, 0, 0);
    output->StagedReadRequests = InterlockedCompareExchange64(&c->StagedReadRequests, 0, 0);
    output->StagedReadBytes = InterlockedCompareExchange64(&c->StagedReadBytes, 0, 0);
    output->MixedStagedReads = InterlockedCompareExchange64(&c->MixedStagedReads, 0, 0);
    output->StagedReadCachedBytes = InterlockedCompareExchange64(&c->StagedReadCachedBytes, 0, 0);
    // V22 prefix is retained for ABI compatibility; the rejected queue has no engine.
    output->RamReadQueueMode = output->RamReadQueued = output->RamReadCompleted = output->RamReadCancelled = 0;
    output->RamReadInline = output->RamReadQueueFull = output->RamReadFallback = 0;
    output->CallerBackoff = static_cast<ULONG>(InterlockedCompareExchange(&c->CallerBackoff, 0, 0));
    output->CallerCandidates = InterlockedCompareExchange64(&c->CallerCandidates, 0, 0);
    output->CallerControls = InterlockedCompareExchange64(&c->CallerControls, 0, 0);
    output->CallerQueued = InterlockedCompareExchange64(&c->CallerQueued, 0, 0);
    output->CallerWorkerActive = InterlockedCompareExchange64(&c->CallerWorkerActive, 0, 0);
    output->CallerOwnerActive = InterlockedCompareExchange64(&c->CallerOwnerActive, 0, 0);
    output->CallerOffloaded = InterlockedCompareExchange64(&c->CallerOffloaded, 0, 0);
    output->ReadAware = static_cast<ULONG>(InterlockedCompareExchange(&c->ReadAware, 0, 0));
    output->ReadAwareWaits = InterlockedCompareExchange64(&c->ReadAwareWaits, 0, 0);
    output->TrimMode = static_cast<ULONG>(InterlockedCompareExchange(&c->TrimMode, 0, 0));
    output->TrimLookups = InterlockedCompareExchange64(&c->TrimLookups, 0, 0);
    output->TrimAsync = InterlockedCompareExchange64(&c->TrimAsync, 0, 0);
    output->TrimAsyncFallbacks = InterlockedCompareExchange64(&c->TrimAsyncFallbacks, 0, 0);
    output->CallerBackoffRequests = InterlockedCompareExchange64(&c->CallerBackoffRequests, 0, 0);
    output->CallerProbes = InterlockedCompareExchange64(&c->CallerProbes, 0, 0);
    output->LowerForwardedWrites = InterlockedCompareExchange64(&c->LowerForwardedWrites, 0, 0);
    output->LowerPagingForwardedWrites = InterlockedCompareExchange64(&c->LowerPagingForwardedWrites, 0, 0);
    output->LowerPagingForwardedReads = InterlockedCompareExchange64(&c->LowerPagingForwardedReads, 0, 0);
    output->LowerOtherReads = InterlockedCompareExchange64(&c->LowerOtherReads, 0, 0);
    output->LowerReadAttempts = InterlockedCompareExchange64(&c->LowerReadAttempts, 0, 0);
    output->LowerWriteAttempts = InterlockedCompareExchange64(&c->LowerWriteAttempts, 0, 0);
    output->LowerFlushAttempts = InterlockedCompareExchange64(&c->LowerFlushAttempts, 0, 0);
    output->PagingUsagePaths = InterlockedCompareExchange(&c->PagingUsageCount, 0, 0);
    output->HibernationUsagePaths = InterlockedCompareExchange(&c->HibernationUsageCount, 0, 0);
    output->DumpUsagePaths = InterlockedCompareExchange(&c->DumpUsageCount, 0, 0);
    for (ULONG index = 0; index < 3; ++index)
    {
        output->UsageInRequests[index] = InterlockedCompareExchange64(&c->UsageInRequests[index], 0, 0);
        output->UsageOutRequests[index] = InterlockedCompareExchange64(&c->UsageOutRequests[index], 0, 0);
        output->UsageInSuccesses[index] = InterlockedCompareExchange64(&c->UsageInSuccesses[index], 0, 0);
        output->UsageOutSuccesses[index] = InterlockedCompareExchange64(&c->UsageOutSuccesses[index], 0, 0);
        output->UsageInFailures[index] = InterlockedCompareExchange64(&c->UsageInFailures[index], 0, 0);
        output->UsageOutFailures[index] = InterlockedCompareExchange64(&c->UsageOutFailures[index], 0, 0);
        output->UsageLastProcessId[index] = InterlockedCompareExchange64(&c->UsageLastProcessId[index], 0, 0);
    }
    output->PagingReadRequests = InterlockedCompareExchange64(&c->PagingReadRequests, 0, 0);
    output->PagingReadBytes = InterlockedCompareExchange64(&c->PagingReadBytes, 0, 0);
    output->PagingWriteRequests = InterlockedCompareExchange64(&c->PagingWriteRequests, 0, 0);
    output->PagingWriteBytes = InterlockedCompareExchange64(&c->PagingWriteBytes, 0, 0);
    output->PagingLastMajor = InterlockedCompareExchange64(&c->PagingLastMajor, 0, 0);
    output->PagingLastFlags = InterlockedCompareExchange64(&c->PagingLastFlags, 0, 0);
    output->PagingLastOffset = InterlockedCompareExchange64(&c->PagingLastOffset, 0, 0);
    output->PagingLastLength = InterlockedCompareExchange64(&c->PagingLastLength, 0, 0);
    output->PagingLastProcessId = InterlockedCompareExchange64(&c->PagingLastProcessId, 0, 0);
    output->PagingMapFailures = InterlockedCompareExchange64(&c->PagingMapFailures, 0, 0);
    output->PagingCapacityWaits = InterlockedCompareExchange64(&c->PagingCapacityWaits, 0, 0);
    output->PagingServicedReadMisses = InterlockedCompareExchange64(&c->PagingServicedReadMisses, 0, 0);
    output->PagingReservedBytes = 0; // Paging data is ordered but never admitted to RAM.
    output->PagingMaxReadLength = InterlockedCompareExchange64(&c->PagingMaxReadLength, 0, 0);
    output->PagingMaxWriteLength = InterlockedCompareExchange64(&c->PagingMaxWriteLength, 0, 0);
    // Outcomes are read before requests: each request is counted before its
    // outcome, so a concurrently completing paging read cannot make a snapshot
    // report more outcomes than requests.
    output->PagingRoutedReadCompletions = InterlockedCompareExchange64(&c->PagingRoutedReadCompletions, 0, 0);
    output->PagingRoutedReadFailures = InterlockedCompareExchange64(&c->PagingRoutedReadFailures, 0, 0);
    output->PagingRoutedReadRequests = InterlockedCompareExchange64(&c->PagingRoutedReadRequests, 0, 0);
    output->PagingRoutedWriteCompletions = InterlockedCompareExchange64(&c->PagingRoutedWriteCompletions, 0, 0);
    output->PagingRoutedWriteFailures = InterlockedCompareExchange64(&c->PagingRoutedWriteFailures, 0, 0);
    output->PagingRoutedWriteRequests = InterlockedCompareExchange64(&c->PagingRoutedWriteRequests, 0, 0);
    output->PagingOverlapWaits = InterlockedCompareExchange64(&c->PagingOverlapWaits, 0, 0);
    output->PagingOffloadCompletions = InterlockedCompareExchange64(&c->PagingOffloadCompletions, 0, 0);
    output->PagingOffloadFailures = InterlockedCompareExchange64(&c->PagingOffloadFailures, 0, 0);
    output->PagingOffloadedReads = InterlockedCompareExchange64(&c->PagingOffloadedReads, 0, 0);
    output->PagingOffloadWriteWaits = InterlockedCompareExchange64(&c->PagingOffloadWriteWaits, 0, 0);
    output->PagingOffloadIdleWaits = InterlockedCompareExchange64(&c->PagingOffloadIdleWaits, 0, 0);
    output->PagingOffloadMaxQueued = InterlockedCompareExchange64(&c->PagingOffloadMaxQueued, 0, 0);
    output->LabGateState = c->LabGateState;
    output->LabGateHits = InterlockedCompareExchange64(&c->LabGateHits, 0, 0);
    output->LabGateOldSubmitSeq = InterlockedCompareExchange64(&c->LabGateOldSubmitSeq, 0, 0);
    output->LabGateOldLowerDoneSeq = InterlockedCompareExchange64(&c->LabGateOldLowerDoneSeq, 0, 0);
    output->LabGateOldRetireSeq = InterlockedCompareExchange64(&c->LabGateOldRetireSeq, 0, 0);
    output->LabGateDirectWaitSeq = InterlockedCompareExchange64(&c->LabGateDirectWaitSeq, 0, 0);
    output->LabGateDirectSubmitSeq = InterlockedCompareExchange64(&c->LabGateDirectSubmitSeq, 0, 0);
    output->LabGateDirectDoneSeq = InterlockedCompareExchange64(&c->LabGateDirectDoneSeq, 0, 0);
    output->PagingAdmittedWrites = InterlockedCompareExchange64(&c->PagingAdmittedWrites, 0, 0);
    output->PagingAdmittedBytes = InterlockedCompareExchange64(&c->PagingAdmittedBytes, 0, 0);
    output->PagingDirectWrites = InterlockedCompareExchange64(&c->PagingDirectWrites, 0, 0);
    output->PagingFileRequests = InterlockedCompareExchange64(&c->PagingFileRequests, 0, 0);
    output->PagingNoFileObject = InterlockedCompareExchange64(&c->PagingNoFileObject, 0, 0);
    output->PagingHighIrql = InterlockedCompareExchange64(&c->PagingHighIrql, 0, 0);
    output->PagingReferenceMisses = InterlockedCompareExchange64(&c->PagingReferenceMisses, 0, 0);
    output->ForceDirectRanges = c->ForceDirectCount;
    output->ReferenceRanges = c->ReferenceCount;
    KeReleaseSpinLock(&c->SnapshotLock, irql);
}
static void RecordMaximum(volatile LONG64* target, ULONG value)
{
    auto current = InterlockedCompareExchange64(target, 0, 0);
    while (static_cast<ULONGLONG>(current) < value)
    {
        auto observed = InterlockedCompareExchange64(target, value, current);
        if (observed == current)
            return;
        current = observed;
    }
}
// T085. The file object a paging request belongs to. Below the volume the
// current stack location carries none: the file system passes its own paging IRP
// down (OriginalFileObject is the file the memory or cache manager wrote) or
// splits it into associated IRPs whose master still holds the file system's
// stack location. Each source stays referenced while this request is outstanding.
// Anything that is not a file object is unknown, which keeps the direct path.
static PFILE_OBJECT RequestFileObject(PIRP irp)
{
    auto fileObject = IoGetCurrentIrpStackLocation(irp)->FileObject;
    if (!fileObject)
        fileObject = irp->Tail.Overlay.OriginalFileObject;
    if (!fileObject && (irp->Flags & IRP_ASSOCIATED_IRP) && irp->AssociatedIrp.MasterIrp)
    {
        const auto master = irp->AssociatedIrp.MasterIrp;
        fileObject = master->Tail.Overlay.OriginalFileObject;
        if (!fileObject && master->CurrentLocation <= master->StackCount)
            fileObject = IoGetCurrentIrpStackLocation(master)->FileObject;
    }
    return fileObject && fileObject->Type == IO_TYPE_FILE ? fileObject : nullptr;
}
// Returns true for a request recognised as paging-file I/O.
bool QcCacheRecordPagingIo(QC_CACHE* c, PIRP irp)
{
    if (!(irp->Flags & IRP_PAGING_IO))
        return false;
    auto stack = IoGetCurrentIrpStackLocation(irp);
    ULONG length;
    LONGLONG offset;
    if (stack->MajorFunction == IRP_MJ_READ)
    {
        length = stack->Parameters.Read.Length;
        offset = stack->Parameters.Read.ByteOffset.QuadPart;
        InterlockedIncrement64(&c->PagingReadRequests);
        InterlockedAdd64(&c->PagingReadBytes, length);
        RecordMaximum(&c->PagingMaxReadLength, length);
    }
    else if (stack->MajorFunction == IRP_MJ_WRITE)
    {
        length = stack->Parameters.Write.Length;
        offset = stack->Parameters.Write.ByteOffset.QuadPart;
        InterlockedIncrement64(&c->PagingWriteRequests);
        InterlockedAdd64(&c->PagingWriteBytes, length);
        RecordMaximum(&c->PagingMaxWriteLength, length);
    }
    else
        return false;
    // T085 recognition evidence, counted for every paging request (cache active or
    // not). A reference miss is a request inside a known paging-file extent that
    // per-request recognition would have treated as application traffic.
    auto fileObject = RequestFileObject(irp);
    bool pagingFile = false;
    if (!fileObject)
    {
        InterlockedIncrement64(&c->PagingNoFileObject);
        if (stack->MajorFunction == IRP_MJ_WRITE) InterlockedIncrement64(&c->PagingWriteNoFileObject);
    }
    else if (KeGetCurrentIrql() > APC_LEVEL)
    {
        InterlockedIncrement64(&c->PagingHighIrql);
        if (stack->MajorFunction == IRP_MJ_WRITE) InterlockedIncrement64(&c->PagingWriteHighIrql);
    }
    else if (FsRtlIsPagingFile(fileObject))
    {
        InterlockedIncrement64(&c->PagingFileRequests);
        if (stack->MajorFunction == IRP_MJ_WRITE) InterlockedIncrement64(&c->PagingFileWriteRequests);
        pagingFile = true;
    }
    else
    {
        KIRQL irql;
        KeAcquireSpinLock(&c->RangeLock, &irql);
        const bool miss = RangesOverlap(c->Reference, c->ReferenceCount, offset, offset + length);
        KeReleaseSpinLock(&c->RangeLock, irql);
        if (miss)
            InterlockedIncrement64(&c->PagingReferenceMisses);
    }
    // These last-request fields are diagnostic breadcrumbs, not an atomic tuple.
    InterlockedExchange64(&c->PagingLastMajor, stack->MajorFunction);
    InterlockedExchange64(&c->PagingLastFlags, irp->Flags);
    InterlockedExchange64(&c->PagingLastOffset, offset);
    InterlockedExchange64(&c->PagingLastLength, length);
    InterlockedExchange64(&c->PagingLastProcessId, reinterpret_cast<LONGLONG>(PsGetCurrentProcessId()));
    return pagingFile;
}
void QcCacheLayoutMap(QC_CACHE* c, QC_LAYOUT_MAP* header, QC_LAYOUT_CHUNK* chunks, ULONG maxChunks)
{
    AcquireCache(c);
    const auto generation = c->Generation;
    const auto total = c->Slots && c->Chunks ? c->Capacity / SlotsPerSlab : 0;
    header->Version = 1;
    header->Chunks = total;
    header->ChunkBytes = SlabBytes;
    header->Generation = generation;
    ULONG j = 0;
    for (; j < min(total, maxChunks); ++j)
    {
        // Short lock holds: one batch of 256 chunks (16,384 slots) at a time.
        if (j && j % 256 == 0)
        {
            ReleaseCache(c);
            AcquireCache(c);
            if (c->Generation != generation || !c->Slots || !c->Chunks)
                break;
        }
        ULONG used = 0, dirty = 0, read = 0, ordered = 0;
        const auto free = c->Chunks[j].Free;
        for (ULONG k = 0; k < SlotsPerSlab; ++k)
        {
            if (free & (1ULL << k))
                continue;
            const auto slot = &c->Slots[j * SlotsPerSlab + k];
            ++used;
            dirty += slot->Dirty ? 1 : 0;
            read += !slot->Dirty && slot->ReadClass ? 1 : 0;
            ordered += k + 1 < SlotsPerSlab && !(free & (1ULL << (k + 1))) &&
                slot[1].Offset.QuadPart == slot->Offset.QuadPart + Chunk ? 1 : 0;
        }
        chunks[j] = { static_cast<UCHAR>(used), static_cast<UCHAR>(dirty), static_cast<UCHAR>(read), static_cast<UCHAR>(ordered) };
    }
    ReleaseCache(c);
    header->Returned = j;
    header->Size = static_cast<ULONG>(sizeof(*header) + j * sizeof(QC_LAYOUT_CHUNK));
}
void QcCachePerformance(QC_CACHE* c, QC_PERFORMANCE* output)
{
    KIRQL irql;
    KeAcquireSpinLock(&c->SnapshotLock, &irql);
    *output = c->PerformanceSnapshot;
    KeReleaseSpinLock(&c->SnapshotLock, irql);
}
static void Fault(QC_CACHE* c, NTSTATUS status)
{
    c->State.LastError = status;
    ++c->State.Errors;
    Publish(c);
    KeSetEvent(&c->Changed, IO_NO_INCREMENT, FALSE);
}
static NTSTATUS InjectLowerCompletion(PDEVICE_OBJECT, PIRP irp, PVOID context)
{
    // Lab-only: the real lower request has completed, but alter the result before
    // the I/O manager copies it to our IOSB/signals the event. Never mask a real error.
    if (NT_SUCCESS(irp->IoStatus.Status))
    {
        if (reinterpret_cast<ULONG_PTR>(context) == 4)
        {
            irp->IoStatus.Status = STATUS_IO_DEVICE_ERROR;
            irp->IoStatus.Information = 0;
        }
        else
            irp->IoStatus.Information /= 2;
    }
    // This driver created the IRP; do not propagate PendingReturned to a nonexistent upper stack location.
    return STATUS_CONTINUE_COMPLETION;
}
// original: the forwarded original IRP, or null for QueueCache-generated I/O
// (drainer writes and barrier flushes). Totals are incremented before the source
// counters, and diagnostics read sources first, so a snapshot never shows more
// attributed attempts than total attempts.
void QcCacheRecordLowerAttempt(QC_CACHE* cache, ULONG major, PIRP original)
{
    const bool paging = original && (original->Flags & IRP_PAGING_IO);
    if (major == IRP_MJ_READ)
    {
        InterlockedIncrement64(&cache->LowerReadAttempts);
        InterlockedIncrement64(paging ? &cache->LowerPagingForwardedReads : &cache->LowerOtherReads);
    }
    else if (major == IRP_MJ_WRITE)
    {
        InterlockedIncrement64(&cache->LowerWriteAttempts);
        InterlockedIncrement64(!original ? &cache->LowerGeneratedWrites :
            paging ? &cache->LowerPagingForwardedWrites : &cache->LowerForwardedWrites);
    }
    else if (major == IRP_MJ_FLUSH_BUFFERS)
        InterlockedIncrement64(&cache->LowerFlushAttempts);
}
static volatile LONG* QcUsageCounter(QC_CACHE* cache, DEVICE_USAGE_NOTIFICATION_TYPE type)
{
    switch (type)
    {
    case DeviceUsageTypePaging: return &cache->PagingUsageCount;
    case DeviceUsageTypeHibernation: return &cache->HibernationUsageCount;
    case DeviceUsageTypeDumpFile: return &cache->DumpUsageCount;
    default: return nullptr;
    }
}
static ULONG QcUsageIndex(DEVICE_USAGE_NOTIFICATION_TYPE type)
{
    switch (type)
    {
    case DeviceUsageTypePaging: return 0;
    case DeviceUsageTypeHibernation: return 1;
    case DeviceUsageTypeDumpFile: return 2;
    default: return MAXULONG;
    }
}
void QcCacheRecordUsageRequest(QC_CACHE* cache, DEVICE_USAGE_NOTIFICATION_TYPE type, BOOLEAN inPath,
                               ULONGLONG processId)
{
    auto index = QcUsageIndex(type);
    if (index == MAXULONG)
        return;
    InterlockedIncrement64(inPath ? &cache->UsageInRequests[index] : &cache->UsageOutRequests[index]);
    InterlockedExchange64(&cache->UsageLastProcessId[index], processId);
}
void QcCacheRecordUsageCompletion(QC_CACHE* cache, DEVICE_USAGE_NOTIFICATION_TYPE type, BOOLEAN inPath,
                                  NTSTATUS status)
{
    auto index = QcUsageIndex(type);
    if (index == MAXULONG)
        return;
    auto successes = inPath ? &cache->UsageInSuccesses[index] : &cache->UsageOutSuccesses[index];
    auto failures = inPath ? &cache->UsageInFailures[index] : &cache->UsageOutFailures[index];
    InterlockedIncrement64(NT_SUCCESS(status) ? successes : failures);
}
static void QcAdjustUsageCounter(volatile LONG* counter, BOOLEAN inPath)
{
    if (!counter)
        return;
    if (inPath)
    {
        InterlockedIncrement(counter);
        return;
    }
    auto current = InterlockedCompareExchange(counter, 0, 0);
    while (current > 0)
    {
        auto observed = InterlockedCompareExchange(counter, current - 1, current);
        if (observed == current)
            return;
        current = observed;
    }
}
void QcCacheRecordUsage(QC_CACHE* cache, DEVICE_USAGE_NOTIFICATION_TYPE type, BOOLEAN inPath)
{
    QcAdjustUsageCounter(&cache->PagingPathCount, inPath);
    QcAdjustUsageCounter(QcUsageCounter(cache, type), inPath);
}
LONG QcCachePagingPathCount(QC_CACHE* cache)
{
    return InterlockedCompareExchange(&cache->PagingPathCount, 0, 0);
}
static NTSTATUS LowerIo(QC_CACHE* c, ULONG major, QC_SLOT* slot = nullptr, ULONG inject = 0, bool writeThrough = false)
{
    KEVENT completed;
    KeInitializeEvent(&completed, NotificationEvent, FALSE);
    IO_STATUS_BLOCK iosb = {}; // Lives until the entire lower completion is observed.
    // A failed IRP build is transient memory pressure and nothing reached the disk,
    // so retry briefly instead of faulting the cache; a lasting shortage still faults.
    PIRP irp = nullptr;
    for (ULONG attempt = 0;; ++attempt)
    {
        const bool simulated = (inject == 8 && attempt < QcLabAllocationFailures) || c->InjectFault == 9;
        if (!simulated)
            irp = IoBuildSynchronousFsdRequest(major,
                                               c->Lower,
                                               slot ? slot->Buffer : nullptr,
                                               slot ? slot->Length : 0,
                                               slot ? &slot->Offset : nullptr,
                                               &completed,
                                               &iosb);
        if (irp)
            break;
        if (attempt + 1 >= QcLowerAllocationAttempts)
            return STATUS_INSUFFICIENT_RESOURCES;
        InterlockedIncrement64(&c->LowerAllocationRetries);
        LARGE_INTEGER interval;
        interval.QuadPart = -10000LL * QcLowerAllocationBackoffMs;
        KeDelayExecutionThread(KernelMode, FALSE, &interval);
    }
    if (major == IRP_MJ_WRITE)
        irp->Flags |= IRP_WRITE_OPERATION | IRP_NOCACHE;
    if (major == IRP_MJ_WRITE && writeThrough)
        IoGetNextIrpStackLocation(irp)->Flags |= SL_WRITE_THROUGH; // Forced unit access (lab mode 2, #31).
    if (inject == 4 || inject == 5)
        IoSetCompletionRoutine(
            irp, InjectLowerCompletion, reinterpret_cast<PVOID>(static_cast<ULONG_PTR>(inject)), TRUE, TRUE, TRUE);
    QcCacheRecordLowerAttempt(c, major);
    auto status = IoCallDriver(c->Lower, irp);
    if (status == STATUS_PENDING)
        KeWaitForSingleObject(&completed, Executive, KernelMode, FALSE, nullptr);
    if (!NT_SUCCESS(iosb.Status))
        return iosb.Status;
    if (slot && iosb.Information != slot->Length)
        return STATUS_DEVICE_DATA_ERROR;
    return STATUS_SUCCESS;
}
// Read-only queries versus controls that can change stored data. The access bits of a
// control code state whether its caller may modify the device; every media-modifying
// storage/disk control declares FILE_WRITE_ACCESS, except the media-swap, bus/device
// reset and data-set-attribute (TRIM) codes listed explicitly below.
constexpr bool QcMayChangeMedia(ULONG code)
{
    if (((code >> 14) & 3) & FILE_WRITE_ACCESS)
        return true;
    switch (code)
    {
    case IOCTL_STORAGE_MANAGE_DATA_SET_ATTRIBUTES: // TRIM shapes TryTrim did not optimize.
    case IOCTL_STORAGE_EJECT_MEDIA:
    case IOCTL_STORAGE_LOAD_MEDIA:
    case IOCTL_STORAGE_LOAD_MEDIA2:
    case IOCTL_STORAGE_RESET_BUS:
    case IOCTL_STORAGE_RESET_DEVICE:
    case IOCTL_DISK_EJECT_MEDIA:
    case IOCTL_DISK_LOAD_MEDIA:
    case IOCTL_DISK_REASSIGN_BLOCKS:
        return true;
    default:
        return false;
    }
}
// Compile-time contract: the polled queries that previously wiped the cache stay read-only,
// and destructive controls keep draining and invalidating.
static_assert(!QcMayChangeMedia(IOCTL_STORAGE_FIRMWARE_GET_INFO));
static_assert(!QcMayChangeMedia(IOCTL_STORAGE_QUERY_PROPERTY));
static_assert(!QcMayChangeMedia(IOCTL_STORAGE_PREDICT_FAILURE));
static_assert(!QcMayChangeMedia(IOCTL_STORAGE_CHECK_VERIFY));
static_assert(!QcMayChangeMedia(IOCTL_STORAGE_GET_DEVICE_NUMBER));
static_assert(!QcMayChangeMedia(IOCTL_DISK_GET_DRIVE_GEOMETRY_EX));
static_assert(!QcMayChangeMedia(IOCTL_DISK_GET_DRIVE_LAYOUT_EX));
// These lock/unlock the eject mechanism; they do not eject or modify media.
// Keep ordered forwarding, but do not invalidate clean cache during discovery.
// Unlocking first writes pending data out (QcReleasesMedia).
static_assert(!QcMayChangeMedia(IOCTL_DISK_MEDIA_REMOVAL));
static_assert(!QcMayChangeMedia(IOCTL_STORAGE_MEDIA_REMOVAL));
static_assert(!QcMayChangeMedia(IOCTL_STORAGE_EJECTION_CONTROL));
static_assert(QcMayChangeMedia(IOCTL_DISK_EJECT_MEDIA));
static_assert(QcMayChangeMedia(IOCTL_DISK_LOAD_MEDIA));
static_assert(QcMayChangeMedia(IOCTL_STORAGE_LOAD_MEDIA));
static_assert(QcMayChangeMedia(IOCTL_STORAGE_RESET_DEVICE));
static_assert(QcMayChangeMedia(IOCTL_STORAGE_MANAGE_DATA_SET_ATTRIBUTES));
static_assert(QcMayChangeMedia(IOCTL_DISK_SET_DRIVE_LAYOUT_EX));
static_assert(QcMayChangeMedia(IOCTL_DISK_SET_DISK_ATTRIBUTES));
static_assert(QcMayChangeMedia(IOCTL_DISK_FORMAT_TRACKS));
static_assert(QcMayChangeMedia(IOCTL_SCSI_PASS_THROUGH));
static_assert(QcMayChangeMedia(IOCTL_STORAGE_EJECT_MEDIA));
static_assert(QcMayChangeMedia(IOCTL_STORAGE_FIRMWARE_DOWNLOAD));
// A file system letting go of the volume: NTFS allows media removal when it dismounts. Detaching a
// virtual disk dismounts and then removes the volume without a query-remove (traced on the VM, #21:
// IOCTL_DISK_MEDIA_REMOVAL, a storage query, device relations, then surprise removal), so this is the
// last request before the disk below is gone. Preventing removal (at mount) is not a release.
static bool QcReleasesMedia(PIRP irp, PIO_STACK_LOCATION stack)
{
    if (stack->MajorFunction != IRP_MJ_DEVICE_CONTROL && stack->MajorFunction != IRP_MJ_INTERNAL_DEVICE_CONTROL)
        return false;
    const auto code = stack->Parameters.DeviceIoControl.IoControlCode;
    if (code != IOCTL_DISK_MEDIA_REMOVAL && code != IOCTL_STORAGE_MEDIA_REMOVAL)
        return false;
    const auto request = static_cast<PPREVENT_MEDIA_REMOVAL>(irp->AssociatedIrp.SystemBuffer);
    return request && stack->Parameters.DeviceIoControl.InputBufferLength >= sizeof(PREVENT_MEDIA_REMOVAL) &&
           !request->PreventMediaRemoval;
}
static_assert(IOCTL_DISK_MEDIA_REMOVAL == 0x74804); // As recorded in the removal event's dump data on the VM.
static NTSTATUS RetainCompletion(PDEVICE_OBJECT, PIRP, PVOID event)
{
    KeSetEvent(static_cast<PKEVENT>(event), IO_NO_INCREMENT, FALSE);
    return STATUS_MORE_PROCESSING_REQUIRED;
}
struct QC_LOWER_CALL
{
    PDEVICE_OBJECT Lower;
    PIRP Irp;
    KEVENT Returned;
};
static IO_WORKITEM_ROUTINE CallLower;
static void CallLower(PDEVICE_OBJECT, PVOID context)
{
    auto call = static_cast<QC_LOWER_CALL*>(context);
    IoCallDriver(call->Lower, call->Irp);
    KeSetEvent(&call->Returned, IO_NO_INCREMENT, FALSE);
}
// Sends irp (next stack location prepared, RetainCompletion signalling completed)
// and waits for its completion, servicing independent reads meanwhile.
static void CallLowerAndWait(QC_CACHE* c, PIRP irp, UCHAR major, KEVENT* completedEvent, bool allowReadService)
{
    auto& completed = *completedEvent;
    // Lower drivers can page-fault synchronously inside IoCallDriver on this thread;
    // the page-in then queues behind this worker. Call from a work item and keep
    // servicing paging reads below until the lower completion.
    QC_LOWER_CALL call{c->Lower, irp};
    const bool offWorker = allowReadService && c->LowerCallItem && QcForwardOffWorker(major);
    // Every foreground read the filter sends down passes here (OriginalIo, StagedRead);
    // QcLabReadAware holds drain batches while one is in flight.
    if (major == IRP_MJ_READ)
        InterlockedIncrement(&c->ForegroundReads);
    NTSTATUS status;
    if (offWorker)
    {
        KeInitializeEvent(&call.Returned, NotificationEvent, FALSE);
        IoQueueWorkItem(c->LowerCallItem, CallLower, DelayedWorkQueue, &call);
        status = STATUS_PENDING;
    }
    else
        status = IoCallDriver(c->Lower, irp);
    if (status == STATUS_PENDING)
    {
        while (!KeReadStateEvent(&completed))
        {
            // This IRP remains owned by the lower stack until RetainCompletion.
            // Cache.Read has pinned its hit versions before reaching this wait.
            // The service lane can complete cached hits or one independent
            // paging-marked miss. A nested lower wait cannot recurse again.
            if (allowReadService && c->ServiceReads && c->RequestAvailable &&
                QcServiceReadsDuringLowerWait(major, c->RangeDrain != FALSE))
            {
                // Null: the active request is a read or has no data range. Do not inspect
                // an IRP whose current stack location belongs to the pending lower I/O.
                if (c->ServiceReads(c->ServiceContext, nullptr))
                    continue;
                PVOID objects[] = {&completed, c->RequestAvailable};
                auto waited =
                    KeWaitForMultipleObjects(2, objects, WaitAny, Executive, KernelMode, FALSE, nullptr, nullptr);
                if (c->Timing)
                    InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(
                        waited == STATUS_WAIT_0 ? &c->Performance.WaitLowerCompleted
                                                : &c->Performance.WaitRequestAvailable));
            }
            else
                KeWaitForSingleObject(&completed, Executive, KernelMode, FALSE, nullptr);
        }
    }
    // The work item (and the stack context it uses) must be idle before reuse.
    if (offWorker)
        KeWaitForSingleObject(&call.Returned, Executive, KernelMode, FALSE, nullptr);
    if (major == IRP_MJ_READ)
        QcCacheForegroundReadDone(c);
}
// A control that can change the device is forwarded after the barrier has drained
// every earlier write, but the request worker does not wait for it. The lower device
// may need this volume to finish it: preparing a shadow copy, volsnap creates its
// diff-area file on the same volume, and NTFS's log write for that file queued behind
// a worker waiting for the control (found on the VM: every process start then hung).
static NTSTATUS ForwardedControlCompletion(PDEVICE_OBJECT, PIRP irp, PVOID context)
{
    auto c = static_cast<QC_CACHE*>(context);
    if (irp->PendingReturned)
        IoMarkIrpPending(irp);
    InterlockedDecrement(&c->ControlsInFlight);
    c->ReleaseRequest(c->ServiceContext, irp);
    return STATUS_CONTINUE_COMPLETION;
}
struct QC_FORWARD
{
    PIO_WORKITEM Item;
    PDEVICE_OBJECT Lower;
    PIRP Irp;
};
static IO_WORKITEM_ROUTINE ForwardControlItem;
static void ForwardControlItem(PDEVICE_OBJECT, PVOID context)
{
    auto forward = static_cast<QC_FORWARD*>(context);
    auto item = forward->Item;
    // Lower drivers can page-fault inside IoCallDriver; never on the request worker.
    IoCallDriver(forward->Lower, forward->Irp);
    ExFreePoolWithTag(forward, Tag);
    IoFreeWorkItem(item);
}
static constexpr NTSTATUS QcControlForwarded = STATUS_PENDING;
static NTSTATUS OriginalIo(QC_CACHE* c, PIRP irp, bool allowReadService = true);
static NTSTATUS ForwardControl(QC_CACHE* c, PIRP irp)
{
    if (!c->ReleaseRequest || !c->Self)
        return OriginalIo(c, irp);
    auto forward = static_cast<QC_FORWARD*>(ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(QC_FORWARD), Tag));
    auto item = forward ? IoAllocateWorkItem(c->Self) : nullptr;
    if (!item)
    {
        if (forward)
            ExFreePoolWithTag(forward, Tag);
        return OriginalIo(c, irp); // Allocation failed: the previous, waiting path.
    }
    const auto major = IoGetCurrentIrpStackLocation(irp)->MajorFunction;
    IoCopyCurrentIrpStackLocationToNext(irp);
    IoSetCompletionRoutine(irp, ForwardedControlCompletion, c, TRUE, TRUE, TRUE);
    InterlockedIncrement(&c->ControlsInFlight);
    QcCacheRecordLowerAttempt(c, major, irp);
    forward->Item = item;
    forward->Lower = c->Lower;
    forward->Irp = irp;
    IoQueueWorkItem(item, ForwardControlItem, DelayedWorkQueue, forward);
    return QcControlForwarded; // The lower device completes irp; do not touch it again.
}
static NTSTATUS OriginalIo(QC_CACHE* c, PIRP irp, bool allowReadService)
{
    const auto major = IoGetCurrentIrpStackLocation(irp)->MajorFunction;
    if (major == IRP_MJ_WRITE)
    {
        // A write sent straight to the disk must not run ahead of an outstanding
        // asynchronous lower TRIM of the same sectors, which could then erase it.
        // That TRIM completes on an offload thread that needs only Mutex.
        const auto& write = IoGetCurrentIrpStackLocation(irp)->Parameters.Write;
        const auto first = write.ByteOffset.QuadPart;
        const auto end = first + static_cast<LONGLONG>(write.Length);
        while (TrimsOverlap(c, first, end))
        {
            KeClearEvent(&c->PagingDone);
            if (!TrimsOverlap(c, first, end))
                break;
            LARGE_INTEGER interval;
            interval.QuadPart = -1000000;
            KeWaitForSingleObject(&c->PagingDone, Executive, KernelMode, FALSE, &interval);
        }
    }
    KEVENT completed;
    KeInitializeEvent(&completed, NotificationEvent, FALSE);
    IoCopyCurrentIrpStackLocationToNext(irp);
    IoSetCompletionRoutine(irp, RetainCompletion, &completed, TRUE, TRUE, TRUE);
    QcCacheRecordLowerAttempt(c, major, irp);
    CallLowerAndWait(c, irp, major, &completed, allowReadService);
    return irp->IoStatus.Status;
}
// Reads [offset, offset + length) from the disk into a new driver-owned buffer, the
// only source for keeping a read miss: an application's buffer can change while
// its read runs, or map one page twice. Returns null (read nothing) when the
// buffer or request cannot be allocated; the caller then forwards the original
// request and keeps nothing. *status: the lower read's result.
static PUCHAR StagedRead(QC_CACHE* c, LONGLONG offset, ULONG length, ULONGLONG cachedBytes, bool allowReadService, NTSTATUS* status)
{
    if (length > QcStagedReadMaxBytes)
        return nullptr;
    auto staging = static_cast<PUCHAR>(ExAllocatePool2(POOL_FLAG_NON_PAGED, length, Tag));
    if (!staging)
        return nullptr;
    LARGE_INTEGER position;
    position.QuadPart = offset;
    auto irp = IoBuildAsynchronousFsdRequest(IRP_MJ_READ, c->Lower, staging, length, &position, nullptr);
    if (!irp)
    {
        ExFreePoolWithTag(staging, Tag);
        return nullptr;
    }
    KEVENT completed;
    KeInitializeEvent(&completed, NotificationEvent, FALSE);
    IoSetCompletionRoutine(irp, RetainCompletion, &completed, TRUE, TRUE, TRUE);
    // Record attempted traffic only after both allocations succeeded. These live
    // lifetime counters are independent of opt-in timing and lower completion.
    InterlockedIncrement64(&c->StagedReadRequests);
    InterlockedAdd64(&c->StagedReadBytes, length);
    if (cachedBytes)
    {
        InterlockedIncrement64(&c->MixedStagedReads);
        InterlockedAdd64(&c->StagedReadCachedBytes, cachedBytes);
    }
    QcCacheRecordLowerAttempt(c, IRP_MJ_READ);
    CallLowerAndWait(c, irp, IRP_MJ_READ, &completed, allowReadService);
    *status = irp->IoStatus.Status;
    if (NT_SUCCESS(*status) && irp->IoStatus.Information != length)
        *status = STATUS_DEVICE_DATA_ERROR;
    if (irp->MdlAddress)
    {
        MmUnlockPages(irp->MdlAddress);
        IoFreeMdl(irp->MdlAddress);
    }
    IoFreeIrp(irp);
    return staging;
}
static bool RangeOverlaps(LONGLONG first, LONGLONG end, LONGLONG block)
{
    return block < end && block + Chunk > first;
}
// Inspect every version, including an older version already owned by a drainer.
// The caller holds Mutex. This is used only for paging-marked direct I/O.
static bool PendingRange(QC_CACHE* c, LONGLONG first, LONGLONG end)
{
    if (!c->Capacity)
        return false;
    for (auto block = first - first % Chunk; block < end; block += Chunk)
        for (auto i = c->Buckets[Bucket(c, block)]; i != NoSlot; i = c->Slots[i].HashNext)
            if (c->Slots[i].Offset.QuadPart == block &&
                (c->Slots[i].Dirty || c->Slots[i].InFlight || c->Slots[i].Filling))
                return true;
    return false;
}
static bool ResidentRange(QC_CACHE* c, LONGLONG first, LONGLONG end)
{
    if (!c->Capacity)
        return false;
    for (auto block = first - first % Chunk; block < end; block += Chunk)
        if (FindSlot(c, block) != NoSlot)
            return true;
    return false;
}
static bool RangesOverlap(const QC_SPECIAL_RANGE* ranges, ULONG count, LONGLONG first, LONGLONG end)
{
    for (ULONG i = 0; i < count; ++i)
        if (ranges[i].Start < end && ranges[i].Start + ranges[i].Length > first)
            return true;
    return false;
}
// T085. True when a paging-marked request is ordinary application traffic
// (file-cache write-back or a mapped file) and may use RAM admission. The
// request's originating file object (RequestFileObject) must be present and
// must not be a paging file; the
// check runs at PASSIVE/APC_LEVEL only (FsRtlIsPagingFile's contract). Growth
// of a paging file keeps its file object, so no layout map is needed. Unknown
// origin, high IRQL, a paging file or a forced-direct range: ordered direct path.
static bool ApplicationPaging(QC_CACHE* c, PIRP irp, LONGLONG first, LONGLONG end)
{
    auto fileObject = RequestFileObject(irp);
    if (!fileObject || KeGetCurrentIrql() > APC_LEVEL || FsRtlIsPagingFile(fileObject))
        return false;
    KIRQL irql;
    KeAcquireSpinLock(&c->RangeLock, &irql);
    const bool forced = RangesOverlap(c->ForceDirect, c->ForceDirectCount, first, end);
    KeReleaseSpinLock(&c->RangeLock, irql);
    return !forced;
}
static NTSTATUS SetSpecialRanges(QC_CACHE* c, PIRP irp)
{
    auto stack = IoGetCurrentIrpStackLocation(irp);
    const auto inputBytes = stack->Parameters.DeviceIoControl.InputBufferLength;
    if (inputBytes < sizeof(QC_SPECIAL_RANGES_HEADER) || !irp->AssociatedIrp.SystemBuffer)
        return STATUS_INVALID_PARAMETER;
    const auto header = *static_cast<QC_SPECIAL_RANGES_HEADER*>(irp->AssociatedIrp.SystemBuffer);
    if (header.Version != 1 || header.Size != sizeof(header) || header.Count > QcSpecialRangeMax ||
        (header.Flags != QcRangesForceDirect && header.Flags != QcRangesReference &&
         header.Flags != QcRangesSafePaging && header.Flags != QcRangesLabPaging) ||
        inputBytes != sizeof(header) + header.Count * sizeof(QC_SPECIAL_RANGE))
        return STATUS_INVALID_PARAMETER;
    auto ranges = reinterpret_cast<const QC_SPECIAL_RANGE*>(
        static_cast<PUCHAR>(irp->AssociatedIrp.SystemBuffer) + sizeof(header));
    for (ULONG i = 0; i < header.Count; ++i)
        if (ranges[i].Start < 0 || ranges[i].Length <= 0 || ranges[i].Start > MAXLONGLONG - ranges[i].Length)
            return STATUS_INVALID_PARAMETER;
    if (header.Flags == QcRangesSafePaging)
    {
        AcquireCache(c);
        const bool ordered = c->UnsafeDefer && c->FlushOrderMode != 0;
        const auto size = c->State.DeviceBytes;
        const auto sequence = c->Diagnostics.SafeMutationSequence;
        ReleaseCache(c);
        if (!ordered) return STATUS_INVALID_DEVICE_STATE;
        if (sequence == MAXULONGLONG || header.Generation != sequence) return STATUS_RETRY;
        for (ULONG i = 0; i < header.Count; ++i)
            if (static_cast<ULONGLONG>(ranges[i].Start + ranges[i].Length) > size)
                return STATUS_INVALID_PARAMETER;
        // The foreground owner holds admission behind this control. Older copies
        // and every sealed group must be durable before these extents become safe.
        const auto status = QcCacheBarrier(c, FALSE, QcOrderedBarrier, irp);
        if (!NT_SUCCESS(status)) return status;
        AcquireCache(c);
        if (c->Diagnostics.SafeMutationSequence != sequence)
        {
            ReleaseCache(c);
            return STATUS_RETRY;
        }
        ClearSafePaging(c, QcSafeSeed);
        for (ULONG i = 0; i < header.Count; ++i)
            if (!QcSafePagingAdd(c->SafePaging, ranges[i].Start, ranges[i].Start + ranges[i].Length))
                ++c->Diagnostics.SafeRangeTableFull;
        Publish(c);
        ReleaseCache(c);
        return STATUS_SUCCESS;
    }
    // A newly forced range may cover cached versions; the direct path's overlap
    // fence reconciles them before any lower write, so no drain is needed here.
    KIRQL irql;
    KeAcquireSpinLock(&c->RangeLock, &irql);
    if (header.Flags == QcRangesForceDirect || header.Flags == QcRangesLabPaging)
    {
        c->ForceDirectPaging = header.Flags == QcRangesLabPaging;
        RtlCopyMemory(c->ForceDirect, ranges, header.Count * sizeof(QC_SPECIAL_RANGE));
        c->ForceDirectCount = header.Count;
    }
    else
    {
        RtlCopyMemory(c->Reference, ranges, header.Count * sizeof(QC_SPECIAL_RANGE));
        c->ReferenceCount = header.Count;
    }
    KeReleaseSpinLock(&c->RangeLock, irql);
    return STATUS_SUCCESS;
}
static bool FlushOrdering(QC_CACHE* c)
{
    return c->UnsafeDefer && c->FlushOrderMode != 0;
}
// Foreground owner, Mutex held. Repeated empty barriers share the same boundary.
// An outstanding copy still owns its dirty versions, so sealing never waits for I/O.
static bool SealEpoch(QC_CACHE* c)
{
    if (!FlushOrdering(c) || !c->EpochHasWrites)
        return true;
    if (c->WriteEpoch == MAXULONGLONG)
    {
        Fault(c, STATUS_INTEGER_OVERFLOW);
        return false;
    }
    ++c->WriteEpoch;
    c->EpochHasWrites = FALSE;
    ++c->Diagnostics.EpochSeals;
    if (QcSealWakes(c->DrainEpoch, c->WriteEpoch, c->State.InFlightBytes))
        WakeDrainers(c);
    return true;
}
// Mutex held. Remaining bytes of the frontier group, counted up to just past limit. Its
// versions lead the dirty FIFO, so this walks at most limit / Chunk + 1 slots.
static ULONGLONG FrontierGroupBytes(QC_CACHE* c, ULONGLONG limit)
{
    ULONGLONG bytes = 0;
    for (auto index = c->Head; index != NoSlot && c->Slots[index].Epoch == c->DrainEpoch && bytes <= limit;
         index = c->Slots[index].QueueNext)
        bytes += QcValidBytes(c->Slots[index].ValidSectors);
    return bytes;
}
// Mutex held, frontier group complete: move to the next group that has work. Empty groups
// have no lower effects left to order; a pending TRIM is an effect even when the dirty FIFO is empty.
static void AdvanceFrontier(QC_CACHE* c)
{
    auto next = c->Head == NoSlot ? c->WriteEpoch : c->Slots[c->Head].Epoch;
    for (const auto& fence : c->TrimFences)
        if (fence.Ranges) next = min(next, fence.Epoch);
    c->DrainEpoch = QcNextFrontier(c->DrainEpoch, next, c->WriteEpoch);
    c->EpochNeedsFlush = FALSE;
}
// Mutex held. True when block lies inside a TRIM whose lower request is still
// outstanding (QcTrimAsync): written first, that TRIM could then erase the newer data.
static bool TrimFenced(QC_CACHE* c, LONGLONG block, ULONGLONG epoch = MAXULONGLONG)
{
    if (!c->TrimFenceCount)
        return false;
    for (const auto& fence : c->TrimFences)
    {
        if (!fence.Ranges || (FlushOrdering(c) && epoch < fence.Epoch) || block >= fence.High || block + Chunk <= fence.Low)
            continue;
        // First range ending after block (sorted, merged, disjoint).
        ULONG low = 0, high = fence.Count;
        while (low < high)
        {
            const auto mid = low + (high - low) / 2;
            if (fence.Ranges[mid].StartingOffset + static_cast<LONGLONG>(fence.Ranges[mid].LengthInBytes) <= block)
                low = mid + 1;
            else
                high = mid;
        }
        if (low < fence.Count && fence.Ranges[low].StartingOffset < block + Chunk)
            return true;
    }
    return false;
}
// Mutex held. Lower writes the drainers have outstanding right now.
static ULONG OutstandingBatches(QC_CACHE* c)
{
    ULONG count = 0;
    for (const auto& worker : c->Workers)
        count += worker.InFlightStart != worker.InFlightEnd;
    return count;
}
static bool ReadPressure(QC_CACHE* c, ULONGLONG now)
{
    return InterlockedCompareExchange(&c->ForegroundReads, 0, 0) > 0 ||
        now - static_cast<ULONGLONG>(InterlockedCompareExchange64(&c->LastForegroundReadMs, 0, 0)) < QcReadAwareHoldMs;
}
static bool DrainCandidate(QC_CACHE* c, ULONG index)
{
    const auto slot = &c->Slots[index];
    if (slot->InFlight || slot->Filling || !QcEpochMayDrain(FlushOrdering(c), slot->Epoch, c->DrainEpoch) ||
        FindOldestSlot(c, slot->Offset.QuadPart) != index || TrimFenced(c, slot->Offset.QuadPart, slot->Epoch))
        return false;
    return !c->RangeDrain || !c->RangeForward ||
        !RangeOverlaps(c->RangeStart, c->RangeEnd, slot->Offset.QuadPart);
}
// A batch never mixes fenced and unrelated blocks, and never includes a fenced
// block once its direct write has been forwarded. Unrelated batches keep normal
// size. Caller holds Mutex.
static bool FenceSplitsBatch(QC_CACHE* c, LONGLONG block, bool anchorOverlaps)
{
    if (!c->RangeDrain)
        return false;
    const bool overlaps = RangeOverlaps(c->RangeStart, c->RangeEnd, block);
    return overlaps != anchorOverlaps || (c->RangeForward && overlaps);
}
// A paging-write fence forces only its own overlapping versions. Unrelated dirty
// data drains only when its own policy (or a barrier/capacity waiter) says so;
// the fence must not make Deferred/Idle data drain early. Caller holds Mutex.
static ULONG SelectDrainCandidate(QC_CACHE* c, bool policyDrain)
{
    // Ordered versions join the dirty FIFO in group order, so none after the
    // first later-group version may drain before the next lower flush.
    const auto last = FlushOrdering(c) ? c->DrainEpoch : MAXULONGLONG;
    // Before forwarding the fenced write, choose its oldest eligible overlap
    // first, but keep unrelated policy-eligible work moving when that overlap is
    // already owned by another lower request. After forwarding, exclude the fence.
    if (c->RangeDrain && !c->RangeForward)
        for (auto index = c->Head; index != NoSlot && c->Slots[index].Epoch <= last; index = c->Slots[index].QueueNext)
            if (DrainCandidate(c, index) &&
                RangeOverlaps(c->RangeStart, c->RangeEnd, c->Slots[index].Offset.QuadPart))
                return index;
    if (!policyDrain)
        return NoSlot;
    for (auto index = c->Head; index != NoSlot && c->Slots[index].Epoch <= last; index = c->Slots[index].QueueNext)
        if (DrainCandidate(c, index))
            return index;
    return NoSlot;
}
// writesOnly: only offloaded write copies (whose blocks are still Filling).
// Asynchronous lower TRIMs count only for trims: their cache effect is already
// applied and their fence keeps drains out; a forwarded write waits in OriginalIo.
static bool PagingReadsOverlap(QC_CACHE* c, LONGLONG first, LONGLONG end, bool writesOnly = false, bool trims = false)
{
    bool overlap = false;
    KIRQL irql;
    KeAcquireSpinLock(&c->PagingLock, &irql);
    for (const auto& read : c->PagingReads)
        if (read.Irp && (!writesOnly || read.Write) && (trims || !read.Trim) && read.Start < end && read.End > first)
        {
            overlap = true;
            break;
        }
    KeReleaseSpinLock(&c->PagingLock, irql);
    return overlap;
}
// Called by the request worker with Mutex released. Offloaded reads depend only
// on Mutex and lower completion, so this wait cannot form a cycle with the worker.
// A blocked write may service/offload further independent reads; those exclude
// the blocked range, so the overlapping set only shrinks. A full wait (no range)
// never services, so it cannot be extended indefinitely by new offloads.
static void WaitForPagingReads(QC_CACHE* c, LONGLONG first, LONGLONG end, PIRP blockedWrite,
                               volatile LONG64* counter, bool writesOnly = false, bool trims = false)
{
    bool counted = false;
    for (;;)
    {
        KeClearEvent(&c->PagingDone);
        if (!PagingReadsOverlap(c, first, end, writesOnly, trims))
            return;
        if (!counted)
        {
            if (counter)
                InterlockedIncrement64(counter);
            counted = true;
        }
        if (blockedWrite && c->ServiceReads && c->ServiceReads(c->ServiceContext, blockedWrite))
            continue;
        LARGE_INTEGER interval;
        interval.QuadPart = -1000000;
        if (blockedWrite && c->RequestAvailable)
        {
            PVOID objects[] = {&c->PagingDone, c->RequestAvailable};
            KeWaitForMultipleObjects(2, objects, WaitAny, Executive, KernelMode, FALSE, &interval, nullptr);
        }
        else
            KeWaitForSingleObject(&c->PagingDone, Executive, KernelMode, FALSE, &interval);
    }
}
bool QcCachePagingReadsOutstanding(QC_CACHE* c)
{
    return PagingReadsOverlap(c, MINLONGLONG, MAXLONGLONG, false, true);
}
// Every offloaded request, including asynchronous lower TRIMs (teardown, media changes).
void QcCacheWaitPagingReads(QC_CACHE* c)
{
    WaitForPagingReads(c, MINLONGLONG, MAXLONGLONG, nullptr, &c->PagingOffloadIdleWaits, false, true);
}
// Called with Mutex released. Read service may perform one independent page-in;
// waking on RequestAvailable is essential when a drainer needs that page-in.
static void WaitForCacheProgress(QC_CACHE* c, PIRP blockedRequest)
{
    if (c->ServiceReads && c->ServiceReads(c->ServiceContext, blockedRequest))
        return;
    LARGE_INTEGER interval;
    interval.QuadPart = -1000000;
    if (c->RequestAvailable)
    {
        PVOID objects[] = {&c->Changed, c->RequestAvailable};
        KeWaitForMultipleObjects(2, objects, WaitAny, Executive, KernelMode, FALSE, &interval, nullptr);
    }
    else
        KeWaitForSingleObject(&c->Changed, Executive, KernelMode, FALSE, &interval);
}
// Strict (#16): pieces of unwritten data the drain completes per second of time with drain writes in
// flight (idle gaps excluded), over windows of at least half a second; a window that completed fewer
// than 8 pieces (a long contiguous run) keeps the previous rate. Mutex held, after a completed batch.
static void MeasurePieceRate(QC_CACHE* c, ULONG pieces)
{
    const auto now = KeQueryInterruptTime();
    c->PieceRateDone += pieces;
    c->PieceRateTime += now - c->DrainBusySince;
    c->DrainBusySince = now;
    if (c->PieceRateTime < 5000000) // 100 ns units
        return;
    if (c->PieceRateDone >= 8)
    {
        const auto rate = c->PieceRateDone * 10000000 / c->PieceRateTime;
        c->PiecesPerSecond = c->PiecesPerSecond ? (c->PiecesPerSecond + rate) / 2 : rate;
    }
    c->PieceRateDone = c->PieceRateTime = 0;
}
static void Drainer(PVOID context)
{
    auto worker = static_cast<QC_DRAIN_WORKER*>(context);
    auto c = worker->Cache;
    for (;;)
    {
        AcquireCache(c);
        if (c->Stop)
        {
            ReleaseCache(c);
            break;
        }
        if (worker->Number >= c->Options.Parallelism)
        {
            // Unused drainer. It must not clear Wake: that re-armed the shared event
            // for every cached write, whose signal then woke this thread to contend
            // for Mutex (about one wake-up per write while the others drained).
            ReleaseCache(c);
            LARGE_INTEGER interval;
            interval.QuadPart = -1000000; // Parallelism changes only at a disabled boundary.
            KeWaitForSingleObject(&c->Stopping, Executive, KernelMode, FALSE, &interval);
            continue;
        }
        // Only drainer zero advances a sealed group. No later group can submit
        // writes until this lower flush completes successfully. The foreground
        // owner may meanwhile admit later versions entirely in RAM.
        bool epochOperation = false;
        for (const auto& fence : c->TrimFences)
            epochOperation |= fence.Ranges && fence.Epoch <= c->DrainEpoch;
        if (worker->Number == 0 && FlushOrdering(c) && !c->TrimPaused &&
            !c->EpochFlushInFlight && c->DrainEpoch < c->WriteEpoch &&
            !c->State.InFlightBytes && !epochOperation &&
            (c->Head == NoSlot || c->Slots[c->Head].Epoch > c->DrainEpoch) &&
            NT_SUCCESS(c->State.LastError) && !c->Gone)
        {
            if (c->FlushOrderMode == 2 && !c->EpochNeedsFlush)
            {
                // Every write of this group was forced to stable media and has completed.
                ++c->Diagnostics.EpochWriteThroughGroups;
                AdvanceFrontier(c);
                Publish(c);
                KeSetEvent(&c->Changed, IO_NO_INCREMENT, FALSE);
                WakeDrainers(c);
                ReleaseCache(c);
                continue;
            }
            c->EpochFlushInFlight = TRUE;
            const bool inject = c->InjectFault == 3;
            if (inject) c->InjectFault = 0;
            Publish(c);
            ReleaseCache(c);
            const auto flushStart = c->Timing ? Tick() : 0;
            const auto status = inject ? STATUS_IO_DEVICE_ERROR : LowerIo(c, IRP_MJ_FLUSH_BUFFERS);
            const auto flushTicks = flushStart ? Tick() - flushStart : 0;
            AcquireCache(c);
            c->EpochFlushInFlight = FALSE;
            c->Diagnostics.EpochFlushTicks += flushTicks;
            if (NT_SUCCESS(status))
            {
                ++c->Diagnostics.EpochFlushes;
                AdvanceFrontier(c);
            }
            else
            {
                ++c->Diagnostics.EpochFlushFailures;
                Fault(c, status); // Keep the boundary for explicit Retry, like a failed drain.
            }
            Publish(c);
            KeSetEvent(&c->Changed, IO_NO_INCREMENT, FALSE);
            WakeDrainers(c);
            ReleaseCache(c);
            continue;
        }
        if (c->State.DirtyBytes == 0 || c->TrimPaused ||
            !NT_SUCCESS(c->State.LastError) || c->Gone)
        {
            KeClearEvent(&c->Wake);
            ReleaseCache(c);
            LARGE_INTEGER interval;
            interval.QuadPart = -1000000;
            KeWaitForSingleObject(&c->Wake, Executive, KernelMode, FALSE, &interval);
            continue;
        }
        bool pressure = c->Pressure != FALSE;
        auto now = NowMs();
        const bool drain = QcShouldDrain(c->Options,
                                         c->State.DirtyBytes,
                                         static_cast<ULONGLONG>(WriteLimit(c)) * Chunk,
                                         now - c->Slots[c->Head].DirtySince,
                                         now - c->LastWriteTime,
                                         c->Barrier || c->WriterWaiting || c->EpochWaiters,
                                         pressure);
        c->Pressure = pressure;
        const bool fenceDrain = c->RangeDrain && !c->RangeForward;
        if (!drain && !fenceDrain)
        {
            Publish(c);
            KeClearEvent(&c->Wake);
            ReleaseCache(c);
            LARGE_INTEGER interval;
            interval.QuadPart = -1000000; // age/idle deadlines checked every 100 ms
            KeWaitForSingleObject(&c->Wake, Executive, KernelMode, FALSE, &interval);
            continue;
        }
        // QcLabReadAware: while foreground reads wait on the disk, keep at most that many
        // drain batches outstanding. Each outstanding byte delays a waiting read (plan 161).
        // Explicit barriers and paging-write fences keep full speed.
        const auto readAware = static_cast<ULONG>(InterlockedCompareExchange(&c->ReadAware, 0, 0));
        if (readAware && !c->Barrier && !fenceDrain && ReadPressure(c, now) && OutstandingBatches(c) >= readAware)
        {
            InterlockedIncrement64(&c->ReadAwareWaits);
            ReleaseCache(c);
            LARGE_INTEGER interval;
            interval.QuadPart = -10000; // 1 ms; the timer resolution may round it up
            KeWaitForSingleObject(&c->Stopping, Executive, KernelMode, FALSE, &interval);
            continue;
        }
        // Gather disk-adjacent blocks into a preallocated staging buffer. Their RAM
        // addresses need not be adjacent. Always choose the oldest version of each
        // address so a later completion can never overwrite newer data on disk.
        // The oldest eligible block remains the fairness anchor. Gather forward
        // by address below; never scan the whole cache under the mutex.
        const auto selectionStart = c->Timing ? Tick() : 0;
        // Distinct disk ranges can drain concurrently. Never issue a newer version
        // while an older write to that address is still outstanding. A range fence
        // prioritizes its overlap without freezing unrelated eligible work.
        auto index = SelectDrainCandidate(c, drain);
        if (index == NoSlot)
        {
            if (selectionStart)
                c->Performance.DrainSelectionTicks += Tick() - selectionStart;
            KeClearEvent(&c->Wake);
            ReleaseCache(c);
            LARGE_INTEGER interval;
            interval.QuadPart = -1000000;
            KeWaitForSingleObject(&c->Wake, Executive, KernelMode, FALSE, &interval);
            continue;
        }
        // Arrival order can be random even when neighboring disk blocks are dirty.
        // Walk backwards by at most one batch before gathering forwards, keeping
        // the oldest eligible anchor in the batch and preserving version order.
        auto batchBlocks = c->Slots[index].ValidSectors == 255
                               ? min(c->Options.BatchKiB * 1024, c->DrainCapacity) / Chunk : 1UL;
        const bool batchOverlapsFence = c->RangeDrain &&
            RangeOverlaps(c->RangeStart, c->RangeEnd, c->Slots[index].Offset.QuadPart);
        auto backReason = &c->Performance.DrainBackLimit;
        if (c->Slots[index].ValidSectors != 255) backReason = &c->Performance.DrainBackPartial;
        for (ULONG back = 1; back < batchBlocks && c->Slots[index].Offset.QuadPart >= Chunk; ++back)
        {
            auto previous = FindOldestSlot(c, c->Slots[index].Offset.QuadPart - Chunk);
            if (previous == NoSlot) { backReason = &c->Performance.DrainBackGap; break; }
            if (c->Slots[previous].InFlight) { backReason = &c->Performance.DrainBackInFlight; break; }
            if (c->Slots[previous].Filling) { backReason = &c->Performance.DrainBackFilling; break; }
            if (c->Slots[previous].ValidSectors != 255) { backReason = &c->Performance.DrainBackPartial; break; }
            if (!QcEpochMayDrain(FlushOrdering(c), c->Slots[previous].Epoch, c->DrainEpoch))
            { backReason = &c->Performance.DrainBackFence; break; }
            if (FenceSplitsBatch(c, c->Slots[previous].Offset.QuadPart, batchOverlapsFence) ||
                TrimFenced(c, c->Slots[previous].Offset.QuadPart, c->Slots[previous].Epoch))
            { backReason = &c->Performance.DrainBackFence; break; }
            index = previous;
        }
        if (batchBlocks > 1 && c->Slots[index].Offset.QuadPart < Chunk)
            backReason = &c->Performance.DrainBackBoundary;
        ++*backReason;
        auto first = &c->Slots[index];
        QC_SLOT io = *first;
        ULONG selected[MaxBatchBytes / Chunk];
        ULONG merged = 0;
        io.Buffer = c->DrainBuffer + worker->Number * c->DrainCapacity;
        io.Length = 0;
        // Exactly one terminal reason per submitted batch; counters do not change selection.
        auto endReason = &c->Performance.DrainEndLimit;
        while (index != NoSlot && merged < batchBlocks)
        {
            auto slot = &c->Slots[index];
            if (slot->InFlight) { endReason = &c->Performance.DrainEndInFlight; break; }
            if (slot->Filling) { endReason = &c->Performance.DrainEndFilling; break; }
            if (!QcEpochMayDrain(FlushOrdering(c), slot->Epoch, c->DrainEpoch))
            { endReason = &c->Performance.DrainEndFence; break; }
            if (merged && slot->ValidSectors != 255)
            { endReason = &c->Performance.DrainEndPartial; break; }
            if (FenceSplitsBatch(c, slot->Offset.QuadPart, batchOverlapsFence) || TrimFenced(c, slot->Offset.QuadPart, slot->Epoch))
            { endReason = &c->Performance.DrainEndFence; break; }
            selected[merged++] = index;
            slot->InFlight = TRUE;
            ++slot->Pins;
            io.Length += Chunk;
            index = FindOldestSlot(c, io.Offset.QuadPart + io.Length);
        }
        if (io.ValidSectors != 255) endReason = &c->Performance.DrainPartialAnchor;
        else if (merged < batchBlocks && index == NoSlot)
        {
            endReason = &c->Performance.DrainEndGap;
            const auto neighbor = FindSlot(c, io.Offset.QuadPart + io.Length);
            if (neighbor == NoSlot) ++c->Performance.DrainGapAbsent;
            else if (c->Slots[neighbor].Filling) ++c->Performance.DrainGapFilling;
            else if (c->Slots[neighbor].ValidSectors == 255) ++c->Performance.DrainGapCleanFull;
            else ++c->Performance.DrainGapCleanPartial;
        }
        ++*endReason;
        const auto transferBytes = io.ValidSectors == 255 ? io.Length : QcValidBytes(io.ValidSectors);
        if (transferBytes <= 65536) ++c->Performance.DrainSizeSmall;
        else if (transferBytes <= 1048576) ++c->Performance.DrainSizeMedium;
        else ++c->Performance.DrainSizeLarge;
        if (!c->State.InFlightBytes)
            c->DrainBusySince = KeQueryInterruptTime(); // The disk starts writing (MeasurePieceRate).
        bool writeThrough = false;
        if (FlushOrdering(c))
        {
            c->Diagnostics.EpochMaxSubmitted = max(c->Diagnostics.EpochMaxSubmitted, c->Slots[selected[0]].Epoch);
            const bool sealed = c->DrainEpoch < c->WriteEpoch;
            writeThrough = QcWriteThroughGroup(c->FlushOrderMode, c->LowerWriteThrough != FALSE, sealed, c->EpochNeedsFlush != FALSE,
                                               sealed ? FrontierGroupBytes(c, QcWriteThroughGroupBytes) : MAXULONGLONG);
            if (writeThrough)
                ++c->Diagnostics.EpochWriteThroughWrites;
            else
                c->EpochNeedsFlush = TRUE;
        }
        c->State.InFlightBytes += transferBytes;
        worker->InFlightStart = io.Offset.QuadPart;
        worker->InFlightEnd = io.Offset.QuadPart + io.Length;
        // Lab gate (one shot): keep this overlapping batch in flight, after its
        // lower write completed, for a bounded hold. Newer overlapping direct
        // writes must wait for its retirement, not merely for its submission.
        ULONG gateHoldMs = 0, gateInject = 0;
        if (c->LabGateState == QcLabGateArmed &&
            LabGateOverlaps(c, io.Offset.QuadPart, io.Offset.QuadPart + io.Length))
        {
            c->LabGateState = QcLabGateHolding;
            gateHoldMs = c->LabGateHoldMs;
            // Report failure (4) or a short transfer (5) for the real lower write:
            // for a sparse version, on its LAST run, after earlier runs landed.
            gateInject = c->LabGateMode == 1 ? 4UL : c->LabGateMode == 2 ? 5UL : 0UL;
            InterlockedIncrement64(&c->LabGateHits);
        }
        auto delay = c->DelayMs;
        auto inject = c->InjectFault == 1 || c->InjectFault == 2 || c->InjectFault == 4 || c->InjectFault == 5 ||
                              c->InjectFault == 8
                          ? c->InjectFault
                          : 0;
        if (inject)
            c->InjectFault = 0;
        ++c->Performance.DrainBatches;
        c->Performance.DrainBytes += transferBytes;
        if (selectionStart)
            c->Performance.DrainSelectionTicks += Tick() - selectionStart;
        Publish(c);
        ReleaseCache(c);
        // InFlight + Pins keep these exact payload versions immutable and alive.
        const auto copyStart = c->Timing ? Tick() : 0;
        for (ULONG i = 0; i < merged; ++i)
            RtlCopyMemory(io.Buffer + i * Chunk, c->Slots[selected[i]].Buffer, Chunk);
        const auto copyTicks = copyStart ? Tick() - copyStart : 0;
        const auto lowerStart = c->Timing ? Tick() : 0;
        if (delay)
        {
            LARGE_INTEGER interval;
            interval.QuadPart = -10000LL * delay;
            KeDelayExecutionThread(KernelMode, FALSE, &interval);
        }
        // Lab-only synthetic failure/short-completion path, deliberately identified in controls.
        auto status = inject == 1   ? STATUS_IO_DEVICE_ERROR
                      : inject == 2 ? STATUS_DEVICE_DATA_ERROR
                                    : STATUS_SUCCESS;
        if (gateHoldMs)
            RecordLabSequence(c, &c->LabGateOldSubmitSeq);
        if (NT_SUCCESS(status))
        {
            if (io.ValidSectors == 255)
                status = LowerIo(c, IRP_MJ_WRITE, &io, inject ? inject : gateInject, writeThrough);
            else
            {
                // Sparse writes own only these sectors. Issue contiguous valid runs;
                // never read-modify-write unknown neighbours. The version stays pinned
                // and InFlight until EVERY run succeeds; failure retains the whole
                // version for ordered retry (already written runs are idempotent).
                NT_ASSERT(merged == 1 && io.ValidSectors != 0);
                for (ULONG sector = 0; sector < 8 && NT_SUCCESS(status);)
                {
                    if (!(io.ValidSectors & (1UL << sector))) { ++sector; continue; }
                    const auto firstSector = sector;
                    while (sector < 8 && (io.ValidSectors & (1UL << sector))) ++sector;
                    auto part = io;
                    part.Offset.QuadPart += firstSector * 512;
                    part.Buffer += firstSector * 512;
                    part.Length = (sector - firstSector) * 512;
                    const bool lastRun = sector == 8 || !(io.ValidSectors >> sector);
                    status = LowerIo(c, IRP_MJ_WRITE, &part, inject ? inject : lastRun ? gateInject : 0, writeThrough);
                    inject = 0;
                }
            }
        }
        const auto lowerTicks = lowerStart ? Tick() - lowerStart : 0;
        if (gateHoldMs)
        {
            RecordLabSequence(c, &c->LabGateOldLowerDoneSeq);
            LARGE_INTEGER interval;
            interval.QuadPart = -10000LL * gateHoldMs;
            KeDelayExecutionThread(KernelMode, FALSE, &interval);
        }
        const auto retirementStart = c->Timing ? Tick() : 0;
        AcquireCache(c);
        c->Performance.LowerIoTicks += lowerTicks;
        c->Performance.DrainCopyTicks += copyTicks;
        c->State.InFlightBytes -= transferBytes;
        worker->InFlightStart = worker->InFlightEnd = 0;
        for (ULONG i = 0; i < merged; ++i)
        {
            c->Slots[selected[i]].InFlight = FALSE;
            --c->Slots[selected[i]].Pins;
        }
        if (NT_SUCCESS(status))
        {
            ++c->LowerWrites;
            if (merged > 1)
                ++c->BatchedWrites;
            c->State.DirtyBytes -= transferBytes;
            c->DirtySlots -= merged;
            c->State.DrainedBytes += transferBytes;
            ULONG pieces = 0;
            for (ULONG i = 0; i < merged; ++i)
            {
                auto completedIndex = selected[i];
                auto slot = &c->Slots[completedIndex];
                if (slot->PieceStart)
                {
                    slot->PieceStart = FALSE;
                    --c->StrictPieces;
                    ++pieces;
                }
                const bool retain = c->Enabled && (c->Options.Retention & QcRetainWrites) &&
                                    FindSlot(c, slot->Offset.QuadPart) == completedIndex;
                if (retain || slot->Pins)
                {
                    slot->RetireWhenUnpinned = !retain;
                    Unlink(c, completedIndex);
                    slot->Dirty = FALSE;
                    slot->ReadClass = FALSE;
                    Link(c, completedIndex);
                }
                else
                    RetireSlot(c, completedIndex);
            }
            MeasurePieceRate(c, pieces);
            if (retirementStart)
                c->Performance.DrainRetirementTicks += Tick() - retirementStart;
            Publish(c);
            KeSetEvent(&c->Changed, IO_NO_INCREMENT, FALSE);
        }
        else
        {
            if (retirementStart)
                c->Performance.DrainRetirementTicks += Tick() - retirementStart;
            Fault(c, status); // Keep the dirty slot and stop retries until explicit recovery.
        }
        if (gateHoldMs)
        {
            RecordLabSequence(c, &c->LabGateOldRetireSeq);
            if (c->LabGateState == QcLabGateHolding)
                c->LabGateState = QcLabGateReleased;
            KeSetEvent(&c->Changed, IO_NO_INCREMENT, FALSE);
        }
        WakeDrainers(c);
        ReleaseCache(c);
    }
    PsTerminateSystemThread(STATUS_SUCCESS);
}
NTSTATUS QcCacheInitialize(QC_CACHE* c, PDEVICE_OBJECT self, PDEVICE_OBJECT lower)
{
    RtlZeroMemory(c, sizeof(*c));
    c->Lower = lower;
    c->Self = self;
    // Failing attach on the boot disk would stop Windows; forward inline instead.
    c->LowerCallItem = IoAllocateWorkItem(self);
    c->Head = c->Tail = c->OpenChunk = NoSlot;
    c->ChunkHead[0] = c->ChunkHead[1] = NoSlot;
    c->CleanHead[0] = c->CleanHead[1] = c->CleanTail[0] = c->CleanTail[1] = NoSlot;
    // Measured (plan 96, 1 MiB sequential RAM hits): prefetch +19-21% Q1; coalescing within a chunk
    // +33% Q1 and up to +11% Q8; together no loss. Bookkeeping (plan 120): 1M Q8T1 +7.9%, Q1T1 +4.5%,
    // four readers +3.5% in a same-build comparison. QcLabCopyFlags can still turn them off for A/B.
    c->CopyFlags = QcCopyPrefetch | QcCopyCoalesce | QcCopyBookkeeping | QcCopyOffloadMisses;
    // Read recall (readrecall.h) replaces bimodal insertion; QcLabReadRecall 0 restores it for A/B.
    c->ReadRecall = 1;
    c->ReadAware = QcDefaultReadAware;
    c->TrimMode = QcDefaultTrimMode;
    c->FlushOrderMode = QcDefaultFlushOrder;
    c->WriteEpoch = c->DrainEpoch = 1;
    LARGE_INTEGER frequency;
    KeQueryPerformanceCounter(&frequency);
    c->Performance.Frequency = frequency.QuadPart;
    c->Options = QcDefaultOptions();
    c->CallerPath = QcDefaultCallerPath;
    c->CallerBackoff = QcCallerPathWorkerWindow;
    c->Instance = InterlockedIncrement64(&NextInstance);
    InterlockedExchange64(&SharedMemoryBudget.LimitBytes, static_cast<LONG64>(MemoryLimit()));
    // A disk-class upper filter can accidentally be installed ABOVE partmgr.
    // Its generated background writes have no filesystem FileObject, so partmgr
    // rejects writes into mounted partitions. Stay usable as pass-through, but
    // refuse write-cache enablement before acknowledging any volatile data.
    UNICODE_STRING partitionManager = RTL_CONSTANT_STRING(L"\\Driver\\partmgr");
    auto current = lower;
    ObReferenceObject(current);
    while (current)
    {
        if (RtlEqualUnicodeString(&current->DriverObject->DriverName, &partitionManager, TRUE))
            c->BlockedPlacement = TRUE;
        auto next = IoGetLowerDeviceObject(current);
        ObDereferenceObject(current);
        current = next;
    }
    ExInitializePushLock(&c->Mutex);
    KeInitializeSpinLock(&c->SnapshotLock);
    KeInitializeEvent(&c->Wake, NotificationEvent, FALSE);
    KeInitializeEvent(&c->Changed, NotificationEvent, FALSE);
    KeInitializeEvent(&c->Stopping, NotificationEvent, FALSE);
    KeInitializeSpinLock(&c->PagingLock);
    KeInitializeSpinLock(&c->RangeLock);
    KeInitializeEvent(&c->PagingWork, SynchronizationEvent, FALSE);
    KeInitializeEvent(&c->PagingDone, NotificationEvent, FALSE);
    Publish(c);
    OBJECT_ATTRIBUTES attrs;
    InitializeObjectAttributes(&attrs, nullptr, OBJ_KERNEL_HANDLE, nullptr, nullptr);
    for (auto& reader : c->ReadThreads)
    {
        reader.Cache = c;
        auto readerStatus =
            PsCreateSystemThread(&reader.Thread, THREAD_ALL_ACCESS, &attrs, nullptr, nullptr, PagingReader, &reader);
        if (!NT_SUCCESS(readerStatus))
        {
            reader.Thread = nullptr;
            QcCacheDestroy(c);
            return readerStatus;
        }
    }
    for (ULONG i = 0; i < RTL_NUMBER_OF(c->Workers); ++i)
    {
        auto worker = &c->Workers[i];
        worker->Cache = c;
        worker->Number = i;
        auto status =
            PsCreateSystemThread(&worker->Thread, THREAD_ALL_ACCESS, &attrs, nullptr, nullptr, Drainer, worker);
        if (!NT_SUCCESS(status))
        {
            QcCacheDestroy(c);
            return status;
        }
    }
    return STATUS_SUCCESS;
}
// Shared across disk instances, not a separate 4 GiB reservation per disk.
static void FreeSlots(QC_CACHE* c)
{
    ClearSafePaging(c, QcSafeAllocation);
    const auto reservation = c->State.BudgetBytes;
    if (c->Slots)
    {
        for (ULONG i = 0; i < c->Capacity; i += SlotsPerSlab)
            if (c->SlabMdls && c->SlabMdls[i / SlotsPerSlab] && c->Slots[i].Buffer)
                QcFreeLockedPages(c->SlabMdls[i / SlotsPerSlab], c->Slots[i].Buffer);
        ExFreePoolWithTag(c->Slots, Tag);
    }
    if (c->SlabMdls)
        ExFreePoolWithTag(c->SlabMdls, Tag);
    c->SlabMdls = nullptr;
    c->Count = c->CleanCount[0] = c->CleanCount[1] = 0;
    c->DirtySlots = c->StrictPieces = 0;
    c->WriteEpoch = c->DrainEpoch = 1;
    c->Diagnostics.EpochMaxSubmitted = 0;
    c->EpochHasWrites = c->EpochFlushInFlight = c->EpochNeedsFlush = FALSE;
    c->EpochWaiters = 0;
    c->CleanValidBytes[0] = c->CleanValidBytes[1] = 0;
    c->CleanHead[0] = c->CleanHead[1] = c->CleanTail[0] = c->CleanTail[1] = NoSlot;
    c->Slots = nullptr;
    c->Capacity = 0;
    if (c->Chunks)
        ExFreePoolWithTag(c->Chunks, Tag);
    c->Chunks = nullptr;
    c->Head = c->Tail = c->OpenChunk = NoSlot;
    c->ChunkHead[0] = c->ChunkHead[1] = NoSlot;
    if (c->Buckets)
        ExFreePoolWithTag(c->Buckets, Tag);
    c->Buckets = nullptr;
    if (c->Recall)
        ExFreePoolWithTag(c->Recall, Tag);
    c->Recall = nullptr;
    c->RecallSets = 0;
    if (c->DrainBuffer)
        ExFreePoolWithTag(c->DrainBuffer, Tag);
    c->DrainBuffer = nullptr;
    c->State.ReservedBytes = c->State.PayloadCapacity = c->State.BudgetBytes = 0;
    QcReleaseMemory(&SharedMemoryBudget, reservation); // Account pages until they are actually freed.
}
NTSTATUS QcCacheBarrier(QC_CACHE* c, BOOLEAN disable, QC_BARRIER_REASON reason, PIRP request, ULONG code)
{
    AcquireCache(c);
    ++c->Diagnostics.BarrierReasons[static_cast<ULONG>(reason) - 1];
    c->Diagnostics.LastReason = reason;
    c->Diagnostics.LastMajor = 0;
    c->Diagnostics.LastCode = code;
    c->Diagnostics.LastOffset = 0;
    c->Diagnostics.LastLength = 0;
    if (request)
    {
        auto stack = IoGetCurrentIrpStackLocation(request);
        c->Diagnostics.LastMajor = stack->MajorFunction;
        if (stack->MajorFunction == IRP_MJ_READ || stack->MajorFunction == IRP_MJ_WRITE)
        {
            c->Diagnostics.LastOffset = stack->Parameters.Read.ByteOffset.QuadPart;
            c->Diagnostics.LastLength = stack->Parameters.Read.Length;
        }
        else if (stack->MajorFunction == IRP_MJ_DEVICE_CONTROL || stack->MajorFunction == IRP_MJ_INTERNAL_DEVICE_CONTROL)
            c->Diagnostics.LastCode = code ? code : stack->Parameters.DeviceIoControl.IoControlCode;
    }
    if (disable)
    {
        ClearSafePaging(c, QcSafeDisable);
        c->Enabled = FALSE;
    }
    c->Barrier = TRUE;
    c->Performance.Phase = QcDrainPhase;
    Publish(c);
    while (QcBarrierHasPending(FlushOrdering(c), c->State.DirtyBytes != 0, c->EpochFlushInFlight != FALSE,
                              c->Head != NoSlot, c->TrimFenceCount != 0, c->DrainEpoch, c->WriteEpoch) &&
           NT_SUCCESS(c->State.LastError) && !c->Gone)
    {
        KeClearEvent(&c->Changed);
        WakeDrainers(c);
        ReleaseCache(c);
        WaitForCacheProgress(c, nullptr);
        AcquireCache(c);
    }
    auto status = c->Gone ? STATUS_DEVICE_NOT_CONNECTED : c->State.LastError;
    c->Performance.Phase = QcLowerFlushPhase;
    Publish(c);
    bool inject = c->InjectFault == 3;
    if (inject)
        c->InjectFault = 0;
    ReleaseCache(c);
    if (NT_SUCCESS(status))
        status = inject ? STATUS_IO_DEVICE_ERROR : LowerIo(c, IRP_MJ_FLUSH_BUFFERS);
    AcquireCache(c);
    if (NT_SUCCESS(status))
    {
        ++c->State.Flushes;
        if (FlushOrdering(c))
        {
            if (c->WriteEpoch == MAXULONGLONG)
            {
                status = STATUS_INTEGER_OVERFLOW;
                Fault(c, status);
            }
            else
            {
                c->DrainEpoch = ++c->WriteEpoch;
                c->EpochHasWrites = FALSE;
                c->EpochNeedsFlush = FALSE;
            }
        }
    }
    else if (NT_SUCCESS(c->State.LastError))
        Fault(c, status);
    if (disable && NT_SUCCESS(status))
    {
        ClearClean(c);
        ++c->Generation;
    }
    c->Barrier = FALSE;
    c->Performance.Phase = QcRequestPhase;
    Publish(c);
    ReleaseCache(c);
    return status;
}
bool QcCacheDisconnect(QC_CACHE* c, QC_STATE* snapshot)
{
    // Read/Write admission checks Gone while holding Mutex. Taking the same
    // lock makes this the cutoff: previously pinned/copied requests retain
    // their ownership, but later requests cannot start from RAM.
    AcquireCache(c);
    ClearSafePaging(c, QcSafeRemoval);
    const bool first = !InterlockedExchange(&c->Gone, TRUE);
    Publish(c);
    if (snapshot)
        *snapshot = c->State;
    KeSetEvent(&c->Changed, IO_NO_INCREMENT, FALSE);
    WakeDrainers(c);
    ReleaseCache(c);
    KeSetEvent(&c->PagingWork, IO_NO_INCREMENT, FALSE);
    return first;
}
void QcCacheDestroy(QC_CACHE* c)
{
    AcquireCache(c);
    c->Stop = TRUE;
    WakeDrainers(c);
    KeSetEvent(&c->Stopping, IO_NO_INCREMENT, FALSE);
    ReleaseCache(c);
    if (c->ReadThreads[0].Thread)
    {
        // Remove-lock drain already completed every offloaded IRP; the thread
        // still finishes any queued entry before it observes PagingStop.
        KIRQL irql;
        KeAcquireSpinLock(&c->PagingLock, &irql);
        c->PagingStop = TRUE;
        KeReleaseSpinLock(&c->PagingLock, irql);
        // PagingWork wakes one thread; each stopping thread passes it on.
        KeSetEvent(&c->PagingWork, IO_NO_INCREMENT, FALSE);
        for (auto& reader : c->ReadThreads)
            if (reader.Thread)
            {
                ZwWaitForSingleObject(reader.Thread, FALSE, nullptr);
                ZwClose(reader.Thread);
                reader.Thread = nullptr;
            }
    }
    for (auto& worker : c->Workers)
        if (worker.Thread)
        {
            ZwWaitForSingleObject(worker.Thread, FALSE, nullptr);
            ZwClose(worker.Thread);
            worker.Thread = nullptr;
        }
    if (c->State.DirtyBytes)
        DbgPrintEx(DPFLTR_IHVDRIVER_ID,
                   DPFLTR_ERROR_LEVEL,
                   "QueueCache removed with %llu dirty bytes; status 0x%08X\n",
                   c->State.DirtyBytes,
                   c->State.LastError);
    // Offload threads finished every asynchronous TRIM before exiting; release defensively.
    for (auto& fence : c->TrimFences)
        if (fence.Ranges)
        {
            ExFreePoolWithTag(fence.Ranges, Tag);
            fence.Ranges = nullptr;
        }
    c->TrimFenceCount = 0;
    FreeSlots(c); // Surprise removal cannot promise volatile data survival.
    if (c->LowerCallItem)
    {
        IoFreeWorkItem(c->LowerCallItem);
        c->LowerCallItem = nullptr;
    }
}
static NTSTATUS Configure(QC_CACHE* c, ULONGLONG budget)
{
    if (c->OwnedRamDevice) return STATUS_NOT_SUPPORTED;
    if (budget < (1ULL << 20) || budget > (128ULL << 30))
        return STATUS_INVALID_PARAMETER;
    AcquireCache(c);
    if (c->Enabled || c->State.DirtyBytes || c->State.InFlightBytes || !NT_SUCCESS(c->State.LastError))
    {
        ReleaseCache(c);
        return STATUS_DEVICE_BUSY;
    }
    const auto allocationFault = c->InjectFault == 6 || c->InjectFault == 7 ? c->InjectFault : 0;
    if (allocationFault)
        c->InjectFault = 0;
    FreeSlots(c);
    if (!QcReserveMemory(&SharedMemoryBudget, budget))
    {
        Publish(c);
        ReleaseCache(c);
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    c->State.BudgetBytes = budget;
    // Include both page-rounded descriptor and hash-index allocations in the hard budget.
    c->DrainCapacity = QcDrainCapacityForBudget(budget, RTL_NUMBER_OF(c->Workers), MaxBatchBytes);
    auto stagingBytes = c->DrainCapacity * RTL_NUMBER_OF(c->Workers);
    // Reserve the page-rounded slab-handle table first (one PMDL per slab).
    const auto slabTableReserve = ((budget / SlabBytes + 1) * sizeof(PMDL) + PAGE_SIZE - 1) & ~(static_cast<ULONGLONG>(PAGE_SIZE) - 1);
    // Per slot: descriptor, hash bucket and read-recall entry (readrecall.h).
    const auto slabCost = SlabBytes + SlotsPerSlab * (sizeof(QC_SLOT) + 2 * sizeof(ULONG)) + sizeof(QC_CHUNK) +
        QcLockedPageMetadataBytes(SlabBytes, 1);
    // Four page-rounded tables (descriptors, index, chunks, recall) each round up by less than a page.
    const auto slabs = QcCacheSlabCount(budget, stagingBytes, 4 * PAGE_SIZE + slabTableReserve, slabCost);
    if (!slabs || slabs > MAXULONG / SlotsPerSlab)
    {
        FreeSlots(c);
        Publish(c);
        ReleaseCache(c);
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    auto n = static_cast<ULONG>(slabs) * SlotsPerSlab;
    auto descriptors =
        (static_cast<SIZE_T>(n) * sizeof(QC_SLOT) + PAGE_SIZE - 1) & ~(static_cast<SIZE_T>(PAGE_SIZE) - 1);
    c->Slots =
        allocationFault == 6 ? nullptr : static_cast<QC_SLOT*>(ExAllocatePool2(POOL_FLAG_NON_PAGED, descriptors, Tag));
    if (!c->Slots)
    {
        FreeSlots(c);
        Publish(c);
        ReleaseCache(c);
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    c->Capacity = n;
    const auto slabTableBytes =
        (static_cast<SIZE_T>(n / SlotsPerSlab) * sizeof(PMDL) + PAGE_SIZE - 1) & ~(static_cast<SIZE_T>(PAGE_SIZE) - 1);
    c->SlabMdls = static_cast<PMDL*>(ExAllocatePool2(POOL_FLAG_NON_PAGED, slabTableBytes, Tag));
    if (!c->SlabMdls)
    {
        FreeSlots(c);
        Publish(c);
        ReleaseCache(c);
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    auto indexBytes = (static_cast<SIZE_T>(n) * sizeof(ULONG) + PAGE_SIZE - 1) & ~(static_cast<SIZE_T>(PAGE_SIZE) - 1);
    c->Buckets = static_cast<ULONG*>(ExAllocatePool2(POOL_FLAG_NON_PAGED, indexBytes, Tag));
    if (!c->Buckets)
    {
        FreeSlots(c);
        Publish(c);
        ReleaseCache(c);
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    RtlFillMemory(c->Buckets, indexBytes, 0xFF);
    const auto chunkBytes =
        (static_cast<SIZE_T>(n / SlotsPerSlab) * sizeof(QC_CHUNK) + PAGE_SIZE - 1) & ~(static_cast<SIZE_T>(PAGE_SIZE) - 1);
    c->Chunks = static_cast<QC_CHUNK*>(ExAllocatePool2(POOL_FLAG_NON_PAGED, chunkBytes, Tag));
    if (!c->Chunks)
    {
        FreeSlots(c);
        Publish(c);
        ReleaseCache(c);
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    // Capacity is whole 64-slot chunks, so the history divides into 4-way sets exactly.
    const auto recallBytes = (static_cast<SIZE_T>(n) * sizeof(ULONG) + PAGE_SIZE - 1) & ~(static_cast<SIZE_T>(PAGE_SIZE) - 1);
    c->Recall = static_cast<ULONG*>(ExAllocatePool2(POOL_FLAG_NON_PAGED, recallBytes, Tag)); // Zeroed: empty.
    if (!c->Recall)
    {
        FreeSlots(c);
        Publish(c);
        ReleaseCache(c);
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    c->RecallSets = n / QcRecallWays;
    c->DrainBuffer = static_cast<PUCHAR>(ExAllocatePool2(POOL_FLAG_NON_PAGED, stagingBytes, Tag));
    if (!c->DrainBuffer)
    {
        FreeSlots(c);
        Publish(c);
        ReleaseCache(c);
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    for (ULONG i = 0; i < n; ++i)
    {
        c->Slots[i].Buffer = allocationFault == 7 && i == 2 * SlotsPerSlab ? nullptr
                             : i % SlotsPerSlab == 0 ? QcAllocateLockedPages(SlabBytes, &c->SlabMdls[i / SlotsPerSlab])
                                                     : c->Slots[i - 1].Buffer + Chunk;
        if (!c->Slots[i].Buffer)
        {
            FreeSlots(c);
            Publish(c);
            ReleaseCache(c);
            return STATUS_INSUFFICIENT_RESOURCES;
        }
    }
    ResetChunks(c);
    c->State.BudgetBytes = budget;
    c->State.ReservedBytes = stagingBytes + descriptors + slabTableBytes + indexBytes + chunkBytes + recallBytes +
        static_cast<ULONGLONG>(n) * Chunk +
        QcLockedPageMetadataBytes(static_cast<ULONGLONG>(n) * Chunk, n / SlotsPerSlab);
    NT_ASSERT(c->State.ReservedBytes <= budget);
    c->State.PayloadCapacity = static_cast<ULONGLONG>(n) * Chunk;
    ++c->Generation;
    Publish(c);
    ReleaseCache(c);
    return STATUS_SUCCESS;
}
// PASSIVE_LEVEL, cache lock not held. Lab mode 2 (#31): whether the lower device honours
// forced unit access, from its write-cache property.
static bool LowerSupportsWriteThrough(QC_CACHE* c)
{
    STORAGE_PROPERTY_QUERY query = {};
    query.PropertyId = StorageDeviceWriteCacheProperty;
    query.QueryType = PropertyStandardQuery;
    STORAGE_WRITE_CACHE_PROPERTY property = {};
    KEVENT completed;
    KeInitializeEvent(&completed, NotificationEvent, FALSE);
    IO_STATUS_BLOCK iosb = {};
    auto irp = IoBuildDeviceIoControlRequest(IOCTL_STORAGE_QUERY_PROPERTY, c->Lower, &query, sizeof(query),
                                             &property, sizeof(property), FALSE, &completed, &iosb);
    if (!irp)
        return false;
    auto status = IoCallDriver(c->Lower, irp);
    if (status == STATUS_PENDING)
    {
        KeWaitForSingleObject(&completed, Executive, KernelMode, FALSE, nullptr);
        status = iosb.Status;
    }
    return NT_SUCCESS(status) &&
           iosb.Information >= RTL_SIZEOF_THROUGH_FIELD(STORAGE_WRITE_CACHE_PROPERTY, WriteThroughSupported) &&
           property.WriteThroughSupported == WriteThroughSupported;
}
static NTSTATUS Control(QC_CACHE* c, PIRP irp, LONGLONG size)
{
    auto stack = IoGetCurrentIrpStackLocation(irp);
    if (stack->Parameters.DeviceIoControl.InputBufferLength != sizeof(QC_COMMAND))
        return STATUS_INVALID_PARAMETER;
    const auto command = *static_cast<QC_COMMAND*>(irp->AssociatedIrp.SystemBuffer);
    if (command.Version != 1 || command.Size != sizeof(command) || command.Reserved || size <= 0)
        return STATUS_INVALID_PARAMETER;
    // A successful QUERY_REMOVE holds admission closed until removal or cancel.
    // An operator command queued behind that query must not reopen the cache.
    AcquireCache(c);
    const BOOLEAN removePending = c->QueryRemovePending;
    ReleaseCache(c);
    if (removePending)
        return STATUS_DEVICE_BUSY;
    if (command.Action == QcConfigure)
        return Configure(c, command.BudgetBytes);
    if (command.Action == QcRelease)
    {
        auto status = QcCacheBarrier(c, TRUE, QcControlBarrier, irp, command.Action);
        if (!NT_SUCCESS(status))
            return status;
        AcquireCache(c);
        // Freeing resets the group counters. A TRIM can seal a group while the cache is
        // disabled, so drainer zero may still be flushing it; let that finish first (plan 197).
        while (c->EpochFlushInFlight && !c->Gone)
        {
            KeClearEvent(&c->Changed);
            ReleaseCache(c);
            KeWaitForSingleObject(&c->Changed, Executive, KernelMode, FALSE, nullptr);
            AcquireCache(c);
        }
        FreeSlots(c);
        Publish(c);
        ReleaseCache(c);
        return STATUS_SUCCESS;
    }
    if (command.Action == QcFlush || command.Action == QcDisable)
    {
        AcquireCache(c);
        ++c->Diagnostics.ControlBarriers;
        Publish(c);
        ReleaseCache(c);
        return QcCacheBarrier(c, command.Action == QcDisable, QcControlBarrier, irp, command.Action);
    }
    // Lab mode 2 needs the lower device's write-through support: asked without the cache lock,
    // and only on a volume without paging/hibernation/dump paths (mode 2 refuses those anyway).
    const bool lowerWriteThrough = command.Action == QcLabFlushOrder && command.Value == 2 &&
                                   QcCachePagingPathCount(c) == 0 && LowerSupportsWriteThrough(c);
    AcquireCache(c);
    NTSTATUS status = STATUS_SUCCESS;
    switch (command.Action)
    {
    case QcLabFlushOrder:
        if (command.Value > 2 || command.BudgetBytes)
            status = STATUS_INVALID_PARAMETER;
        else if (c->Enabled || c->Count || c->TrimFenceCount || c->EpochFlushInFlight ||
                 !NT_SUCCESS(c->State.LastError))
            status = STATUS_DEVICE_BUSY;
        else if (command.Value == 2 && !lowerWriteThrough)
            status = STATUS_NOT_SUPPORTED;
        else
        {
            KIRQL irql;
            KeAcquireSpinLock(c->RoutingLock, &irql);
            if (!QcMaySelectFlushOrder(command.Value, QcCachePagingPathCount(c)))
                status = STATUS_NOT_SUPPORTED;
            else
            {
                ClearSafePaging(c, QcSafeMode);
                c->FlushOrderMode = static_cast<LONG>(command.Value);
                c->WriteEpoch = c->DrainEpoch = 1;
                c->Diagnostics.EpochMaxSubmitted = 0;
                c->EpochHasWrites = c->EpochNeedsFlush = FALSE;
                c->LowerWriteThrough = lowerWriteThrough;
                c->Diagnostics.EpochWriteThroughAvailable = lowerWriteThrough;
                ++c->Generation;
            }
            KeReleaseSpinLock(c->RoutingLock, irql);
        }
        break;
    case QcFlushPolicy:
        // Explicit operator choice, only at a clean disabled boundary. Never persists across boot.
        if (command.Value > 1 || command.BudgetBytes)
            status = STATUS_INVALID_PARAMETER;
        else if (c->Enabled || c->Count || !NT_SUCCESS(c->State.LastError))
            status = STATUS_DEVICE_BUSY;
        else
        {
            c->UnsafeDefer = command.Value == 1;
            ++c->Generation;
        }
        break;
    case QcEnable:
    case QcEnablePaging:
    {
        if (command.Value || command.BudgetBytes)
            status = STATUS_INVALID_PARAMETER;
        else if (c->BlockedPlacement || c->OwnedRamDevice)
            status = STATUS_INVALID_DEVICE_STATE;
        else if (!c->Capacity || c->Suspended || c->Gone || (c->SectorBytes != 512 && c->SectorBytes != 4096))
            status = STATUS_DEVICE_NOT_READY;
        else if (!NT_SUCCESS(c->State.LastError))
            status = c->State.LastError;
        else
        {
            // Dispatch holds this same lock while it reserves an incoming
            // paging/hibernation/dump path. Keep it through Enabled transition.
            KIRQL irql;
            KeAcquireSpinLock(c->RoutingLock, &irql);
            // Keep activation and prior usage reservations under the same lock.
            if (!QcMaySelectFlushOrder(static_cast<ULONGLONG>(c->FlushOrderMode), QcCachePagingPathCount(c)))
                status = STATUS_NOT_SUPPORTED;
            else
            {
                ClearSafePaging(c, QcSafeEnable);
                c->Enabled = TRUE;
                ++c->Generation;
            }
            KeReleaseSpinLock(c->RoutingLock, irql);
        }
        break;
    }
    case QcRetry:
        if (c->Gone || c->Suspended)
        {
            status = STATUS_DEVICE_NOT_READY;
            break;
        }
        ++c->Diagnostics.ControlBarriers;
        c->State.LastError = STATUS_SUCCESS;
        WakeDrainers(c);
        break;
    case QcPerformanceTiming:
        if (command.Value > 1 || command.BudgetBytes)
            status = STATUS_INVALID_PARAMETER;
        else
            c->Timing = static_cast<LONG>(command.Value);
        break;
    case QcCallerPath:
        if (command.Value > 1 || command.BudgetBytes)
            status = STATUS_INVALID_PARAMETER;
        else
            InterlockedExchange(&c->CallerPath, static_cast<LONG>(command.Value));
        break;
    case QcDropClean:
        // Release clean read/retained-write blocks on request. Dirty and in-flight payload
        // is untouched: this is not a flush and never discards data the disk has not taken.
        if (command.Value || command.BudgetBytes)
            status = STATUS_INVALID_PARAMETER;
        else
            ClearClean(c);
        break;
    case QcLabResetFreeOrder:
        status = command.Value || command.BudgetBytes ? STATUS_INVALID_PARAMETER : ResetFreeOrder(c);
        break;
    case QcLabMeasureLayout:
        status = command.Value || command.BudgetBytes ? STATUS_INVALID_PARAMETER : MeasureLayout(c);
        break;
    case QcLabCopyFlags:
        if (command.Value > (QcCopyPrefetch | QcCopyCoalesce | QcCopyBookkeeping | QcCopyOffloadMisses) || command.BudgetBytes)
            status = STATUS_INVALID_PARAMETER;
        else
            InterlockedExchange(&c->CopyFlags, static_cast<LONG>(command.Value));
        break;
    case QcLabReadRecall:
        if (command.Value > 1 || command.BudgetBytes)
            status = STATUS_INVALID_PARAMETER;
        else
        {
            // Under the cache mutex, like every history access: each mode starts with no history.
            InterlockedExchange(&c->ReadRecall, static_cast<LONG>(command.Value));
            ClearRecall(c);
        }
        break;
    case QcLabRamReadQueue:
        // ABI reservation for the rejected 0.4.503.1 scheduling experiment.
        // Mode 0 remains a harmless reset; activating either removed strategy is unsupported.
        if (command.BudgetBytes || command.Value > 2) status = STATUS_INVALID_PARAMETER;
        else if (command.Value) status = STATUS_NOT_SUPPORTED;
        break;
    case QcLabCallerBackoff:
        if (command.Value > QcCallerPathWorkerWindow || command.BudgetBytes)
            status = STATUS_INVALID_PARAMETER;
        else
            InterlockedExchange(&c->CallerBackoff, static_cast<LONG>(command.Value));
        break;
    case QcLabDrainGather:
        // ABI reservation for the rejected aligned-gather experiment. Reset is
        // harmless; current selection is the retained pre-experiment algorithm.
        if (command.Value > 1 || command.BudgetBytes)
            status = STATUS_INVALID_PARAMETER;
        else if (command.Value)
            status = STATUS_NOT_SUPPORTED;
        break;
    case QcLabReadAware:
        if (command.Value > RTL_NUMBER_OF(c->Workers) || command.BudgetBytes)
            status = STATUS_INVALID_PARAMETER;
        else
        {
            InterlockedExchange(&c->ReadAware, static_cast<LONG>(command.Value));
            WakeDrainers(c);
        }
        break;
    case QcLabTrimMode:
        if (command.Value > (QcTrimLookup | QcTrimAsync | QcTrimLabHold) || command.BudgetBytes)
            status = STATUS_INVALID_PARAMETER;
        else
            InterlockedExchange(&c->TrimMode, static_cast<LONG>(command.Value));
        break;
    case QcLabDelay:
        if (command.Value > 2000)
            status = STATUS_INVALID_PARAMETER;
        else
            c->DelayMs = static_cast<ULONG>(command.Value);
        break;
    case QcLabFault:
        if (command.Value > 11)
            status = STATUS_INVALID_PARAMETER;
        else
            c->InjectFault = static_cast<ULONG>(command.Value);
        break;
    case QcLabGate:
    {
        const auto bytes = static_cast<ULONG>(command.Value);
        const auto holdMs = static_cast<ULONG>((command.Value >> 32) & 0xFFFF);
        const auto mode = static_cast<ULONG>(command.Value >> 48);
        if (!command.Value)
        {
            // Disarm; recorded sequences remain readable as evidence.
            c->LabGateState = QcLabGateOff;
            break;
        }
        // Never on a disk that hosts paging/hibernation/dump backing, never
        // while a previous hold is active, and only for a bounded range/time.
        if (QcCachePagingPathCount(c) > 0 || c->LabGateState == QcLabGateHolding)
            status = STATUS_INVALID_DEVICE_STATE;
        else if (!bytes || bytes > QcLabGateMaxBytes || !holdMs || holdMs > QcLabGateMaxHoldMs || mode > 2 ||
                 command.BudgetBytes > static_cast<ULONGLONG>(MAXLONGLONG - bytes))
            status = STATUS_INVALID_PARAMETER;
        else
        {
            c->LabGateStart = static_cast<LONGLONG>(command.BudgetBytes);
            c->LabGateEnd = c->LabGateStart + bytes;
            c->LabGateHoldMs = holdMs;
            c->LabGateMode = mode;
            volatile LONG64* evidence[] = {&c->LabGateHits, &c->LabGateOldSubmitSeq, &c->LabGateOldLowerDoneSeq,
                                           &c->LabGateOldRetireSeq, &c->LabGateDirectWaitSeq,
                                           &c->LabGateDirectSubmitSeq, &c->LabGateDirectDoneSeq};
            for (auto field : evidence)
                InterlockedExchange64(field, 0);
            c->LabGateState = QcLabGateArmed;
        }
        break;
    }
    default:
        status = STATUS_INVALID_DEVICE_REQUEST;
    }
    Publish(c);
    ReleaseCache(c);
    if (NT_SUCCESS(status) && command.Action == QcRetry)
        status = QcCacheBarrier(c, FALSE, QcControlBarrier, irp, command.Action);
    return status;
}
static NTSTATUS PrepareUsagePath(QC_CACHE* c, PIRP irp)
{
    AcquireCache(c);
    ClearSafePaging(c, QcSafeUsage);
    const bool active = c->Enabled != FALSE;
    auto status = irp->Cancel ? STATUS_CANCELLED : c->Gone ? STATUS_DEVICE_NOT_CONNECTED : c->State.LastError;
    ReleaseCache(c);
    if (NT_SUCCESS(status) && active)
    {
        // Windows may turn a normal file into paging/hibernation/dump backing at
        // runtime. Establish a lower-media boundary once at registration, without
        // disabling routing. The subsequent data IRPs bypass RAM caching: caching
        // swapped-out memory in nonpaged RAM is circular and can starve the very
        // memory needed to complete a page-in. Clean raw blocks are also discarded
        // so no pre-registration view can satisfy a system-file read.
        status = QcCacheBarrier(c, FALSE, QcOrderedBarrier, irp);
        if (NT_SUCCESS(status))
        {
            AcquireCache(c);
            ClearClean(c);
            Publish(c);
            ReleaseCache(c);
        }
    }
    return status;
}
static PUCHAR Map(PIRP irp)
{
    if (irp->MdlAddress)
        return static_cast<PUCHAR>(
            MmGetSystemAddressForMdlSafe(irp->MdlAddress,
                ((irp->Flags & IRP_PAGING_IO) ? HighPagePriority : NormalPagePriority) | MdlMappingNoExecute));
    return static_cast<PUCHAR>(irp->AssociatedIrp.SystemBuffer); // Neither-I/O is not supported for cached data.
}
// Ordered direct path for paging-marked writes that are not admitted to RAM
// (special-file ranges, no current special map, or an unmappable buffer).
// Caller holds Mutex; it is released on return.
// Writes one request straight to the disk: a paging write that is not cached, or a Strict
// write-through write (#16).
static NTSTATUS DirectWrite(QC_CACHE* c, PIRP irp, LONGLONG first, ULONG length)
{
    const bool ordered = FlushOrdering(c);
    const auto end = first + length;
    bool pagingFile = false;
    if (ordered && KeGetCurrentIrql() <= APC_LEVEL)
    {
        auto fileObject = RequestFileObject(irp);
        pagingFile = fileObject && FsRtlIsPagingFile(fileObject);
        // Verification on W: uses real forced-direct paging writes to an owned
        // file. Only this explicit lab override substitutes for a page-file FO.
        KIRQL irql;
        KeAcquireSpinLock(&c->RangeLock, &irql);
        if (!pagingFile && c->ForceDirectPaging)
            for (ULONG i = 0; i < c->ForceDirectCount; ++i)
                if (c->ForceDirect[i].Start <= first &&
                    c->ForceDirect[i].Start + c->ForceDirect[i].Length >= end) pagingFile = true;
        KeReleaseSpinLock(&c->RangeLock, irql);
        pagingFile = pagingFile && Map(irp) != nullptr;
    }
    bool safe = ordered && pagingFile && QcSafePagingContains(c->SafePaging, first, end);
    if (safe && PendingRange(c, first, end))
    {
        // Never silently wait on an impossible safe overlap. Fail closed and
        // retain the witness even in Release builds.
        ++c->Diagnostics.SafeRangeOverlapFound;
        ClearSafePaging(c, QcSafeDirectWrite);
        safe = false;
    }
    if (!pagingFile) InvalidateSafePaging(c, first, end, QcSafeDirectWrite);
    if (ordered && !safe)
    {
        if (pagingFile) ++c->Diagnostics.PagingOrderLearnBarriers;
        else ++c->Diagnostics.UnidentifiedDirectBarriers;
        ReleaseCache(c);
        const auto status = QcCacheBarrier(c, FALSE, QcOrderedBarrier, irp);
        if (!NT_SUCCESS(status)) return status;
        AcquireCache(c);
    }
    if (safe)
    {
        ++c->Diagnostics.PagingOrderBypasses;
        c->Diagnostics.PagingOrderBypassBytes += length;
    }
    // A paging-marked write can also be ordinary mapped-file data. Drain
    // only its overlapping versions, including writes already issued by a
    // drainer. Keep the range fenced until the direct lower write completes.
    // The foreground worker owns the only range fence; unrelated blocks may
    // continue draining and fitting ordinary writes retain RAM admission.
    c->RangeStart = first;
    c->RangeEnd = end;
    c->RangeDrain = TRUE;
    c->RangeForward = FALSE;
    WakeDrainers(c);
    const bool gateRecord = LabGateOverlaps(c, first, end);
    if (PendingRange(c, first, end))
    {
        if (irp->Flags & IRP_PAGING_IO)
            InterlockedIncrement64(&c->PagingOverlapWaits);
        if (gateRecord)
            RecordLabSequence(c, &c->LabGateDirectWaitSeq);
    }
    while (PendingRange(c, first, end) && NT_SUCCESS(c->State.LastError) &&
           !c->Gone && !irp->Cancel)
    {
        KeClearEvent(&c->Changed);
        ReleaseCache(c);
        WaitForCacheProgress(c, irp);
        AcquireCache(c);
    }
    if (PendingRange(c, first, end) || c->Gone || irp->Cancel)
    {
        auto error = c->Gone ? STATUS_DEVICE_NOT_CONNECTED :
            irp->Cancel ? STATUS_CANCELLED : c->State.LastError;
        c->RangeDrain = c->RangeForward = FALSE;
        WakeDrainers(c);
        ReleaseCache(c);
        return error;
    }
    // Do not use stale clean data while the direct request owns this range.
    InvalidateCleanRange(c, first, length);
    c->RangeForward = TRUE;
    // Lab fault 10 applies only to a force-direct range, which only verification sets.
    bool injectFailure = false;
    if (c->InjectFault == 10)
    {
        KIRQL irql;
        KeAcquireSpinLock(&c->RangeLock, &irql);
        injectFailure = RangesOverlap(c->ForceDirect, c->ForceDirectCount, first, end);
        KeReleaseSpinLock(&c->RangeLock, irql);
        if (injectFailure)
            c->InjectFault = 0;
    }
    ReleaseCache(c);
    const bool gateSubmit = gateRecord && RecordLabSequence(c, &c->LabGateDirectSubmitSeq);
    auto status = OriginalIo(c, irp);
    // Lab-only: the direct write reached the disk but is reported as failed.
    if (injectFailure && NT_SUCCESS(status))
    {
        status = STATUS_IO_DEVICE_ERROR;
        irp->IoStatus.Information = 0;
    }
    if (gateSubmit)
        RecordLabSequence(c, &c->LabGateDirectDoneSeq);
    if (NT_SUCCESS(status) && irp->IoStatus.Information != length)
        status = STATUS_DEVICE_DATA_ERROR;
    NTSTATUS orderedFlush = STATUS_SUCCESS;
    if (ordered && !safe)
    {
        orderedFlush = LowerIo(c, IRP_MJ_FLUSH_BUFFERS);
        if (NT_SUCCESS(status)) status = orderedFlush;
    }
    AcquireCache(c);
    if (!NT_SUCCESS(orderedFlush) && NT_SUCCESS(c->State.LastError)) Fault(c, orderedFlush);
    if (ordered && pagingFile && !safe && NT_SUCCESS(status))
        if (!QcSafePagingAdd(c->SafePaging, first, end)) ++c->Diagnostics.SafeRangeTableFull;
    // A failed or short lower write may still have changed some sectors.
    // The request reports that failure; no old clean view may survive it.
    InvalidateCleanRange(c, first, length);
    c->RangeDrain = c->RangeForward = FALSE;
    Publish(c);
    KeSetEvent(&c->Changed, IO_NO_INCREMENT, FALSE);
    WakeDrainers(c);
    ReleaseCache(c);
    return status;
}
// Returned by a caller-thread attempt before it changes anything.
static constexpr NTSTATUS QcCallerPathDeclined = STATUS_RETRY;
// Returned by Write when an offloaded-request thread now owns the IRP (payload copy).
static constexpr NTSTATUS QcWriteTransferred = STATUS_PENDING;
static NTSTATUS FinishWrite(QC_CACHE* c, PIRP irp, LONGLONG offset, ULONG length, const ULONG* slots,
                            bool writeThrough, bool pagingIo);
// callerPath: QcCacheTryCallerPath. Anything that would wait, forward to the
// disk or take a barrier returns QcCallerPathDeclined before any change.
// Strict (#16): a version that just became dirty begins a separate piece of unwritten data, unless the
// block before it is dirty and waiting too (the drain then writes both with one disk write). Mutex held.
static void MarkPiece(QC_CACHE* c, ULONG index)
{
    auto slot = &c->Slots[index];
    if (c->UnsafeDefer || slot->PieceStart)
        return;
    const auto block = slot->Offset.QuadPart;
    const auto previous = block >= Chunk ? FindSlot(c, block - Chunk) : NoSlot;
    if (previous != NoSlot && c->Slots[previous].Dirty && !c->Slots[previous].InFlight)
        return;
    slot->PieceStart = TRUE;
    ++c->StrictPieces;
}
static NTSTATUS Write(QC_CACHE* c, PIRP irp, bool callerPath = false)
{
    auto stack = IoGetCurrentIrpStackLocation(irp);
    auto length = stack->Parameters.Write.Length;
    auto offset = stack->Parameters.Write.ByteOffset;
    const bool pagingIo = (irp->Flags & IRP_PAGING_IO) != 0;
    AcquireCache(c);
    if (c->Gone || irp->Cancel)
    {
        auto error = c->Gone ? STATUS_DEVICE_NOT_CONNECTED : STATUS_CANCELLED;
        ReleaseCache(c);
        return error;
    }
    // A valid zero-byte write owns no data and imposes no durability boundary.
    // Do not flush unrelated pending writes merely to complete this no-op.
    if (length == 0)
    {
        ReleaseCache(c);
        irp->IoStatus.Information = 0;
        return STATUS_SUCCESS;
    }
    if (!QcShouldCacheDataIo(pagingIo, pagingIo && ApplicationPaging(c, irp, offset.QuadPart, offset.QuadPart + length)))
    {
        InterlockedIncrement64(&c->PagingDirectWrites);
        return DirectWrite(c, irp, offset.QuadPart, length);
    }
    if (!NT_SUCCESS(c->State.LastError))
    {
        auto error = c->State.LastError;
        ReleaseCache(c);
        return error;
    }
    const auto firstBlock = offset.QuadPart / Chunk * Chunk;
    const auto end = offset.QuadPart + length;
    const auto blocks = static_cast<ULONG>((end - firstBlock + Chunk - 1) / Chunk);
    auto needed = blocks;
    const bool writeThrough = (stack->Flags & SL_WRITE_THROUGH) != 0;
    // No offload can start while a caller-thread request runs (only the worker
    // offloads), so an in-flight offloaded read is the only overlap to exclude.
    if (callerPath && (!c->Enabled || (writeThrough && !c->UnsafeDefer) || needed > WriteLimit(c) ||
                       PagingReadsOverlap(c, offset.QuadPart, end)))
    {
        ReleaseCache(c);
        return QcCallerPathDeclined;
    }
    if (writeThrough)
    {
        ++c->Diagnostics.WriteThroughWrites;
        Publish(c);
    }
    if (!c->Enabled && !c->State.DirtyBytes)
    {
        InvalidateSafePaging(c, offset.QuadPart, end, QcSafeDirectWrite);
        if (c->FlushOrderMode) Publish(c);
        ReleaseCache(c);
        return OriginalIo(c, irp);
    }
    // Strict write-through (NTFS's journal writes it this way): it must be on the disk when it completes,
    // nothing more. It goes straight to the disk after only the older versions of its own blocks, not
    // after all dirty data (#16: that barrier stopped writes for minutes after a burst). A flush still
    // writes out everything before it. NTFS writes its log record before the metadata it covers.
    if (c->Enabled && writeThrough && !c->UnsafeDefer && needed <= WriteLimit(c))
        return DirectWrite(c, irp, offset.QuadPart, length);
    // Sector-valid partial writes use the same RAM admission path as full blocks.
    // Only explicit durability/disabled/over-budget fallbacks remain here.
    if (!c->Enabled || (writeThrough && !c->UnsafeDefer) || needed > WriteLimit(c))
    {
        const bool ordered = FlushOrdering(c);
        InvalidateSafePaging(c, offset.QuadPart, end, QcSafeDirectWrite);
        auto reason = !c->Enabled ? QcDisabledWriteBarrier :
            (writeThrough && !c->UnsafeDefer) ? QcStrictWriteBarrier : QcQuotaWriteBarrier;
        ReleaseCache(c);
        auto status = QcCacheBarrier(c, FALSE, reason, irp);
        AcquireCache(c);
        InvalidateCleanRange(c, offset.QuadPart, length);
        Publish(c);
        ReleaseCache(c);
        if (!NT_SUCCESS(status)) return status;
        status = OriginalIo(c, irp);
        if (ordered)
        {
            const auto flushed = LowerIo(c, IRP_MJ_FLUSH_BUFFERS);
            if (!NT_SUCCESS(flushed))
            {
                AcquireCache(c);
                if (NT_SUCCESS(c->State.LastError)) Fault(c, flushed);
                ReleaseCache(c);
                if (NT_SUCCESS(status)) status = flushed;
            }
        }
        return status;
    }
    // Lab fault 11: the next paging write's buffer cannot be mapped (fallback proof only).
    const bool mapFault = pagingIo && c->InjectFault == 11;
    if (mapFault)
        c->InjectFault = 0;
    auto source = mapFault ? nullptr : Map(irp);
    if (!source)
    {
        if (callerPath)
        {
            ReleaseCache(c);
            return QcCallerPathDeclined;
        }
        if (pagingIo)
        {
            // Under memory pressure an application paging write must still make
            // progress: use the ordered direct path, which needs no mapping.
            InterlockedIncrement64(&c->PagingMapFailures);
            return DirectWrite(c, irp, offset.QuadPart, length);
        }
        ReleaseCache(c);
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    ULONGLONG writeEpoch = c->WriteEpoch;
    for (;;)
    {
        // Preflight the WHOLE request before modifying any payload. Recompute after
        // every wait because the drainer may have removed or pinned indexed slots.
        writeEpoch = c->WriteEpoch;
        if (FlushOrdering(c))
        {
            if (writeEpoch >= MAXULONGLONG - 2)
            {
                Fault(c, STATUS_INTEGER_OVERFLOW);
                ReleaseCache(c);
                return STATUS_INTEGER_OVERFLOW;
            }
        }
        needed = 0;
        ULONG newDirty = 0, newWriteOccupancy = 0;
        for (auto block = firstBlock; block < end; block += Chunk)
        {
            auto index = FindSlot(c, block);
            const bool immutable = index != NoSlot && QcEpochNeedsVersion(FlushOrdering(c),
                c->Slots[index].Dirty, c->Slots[index].Epoch, writeEpoch);
            if (index == NoSlot || c->Slots[index].InFlight || c->Slots[index].Pins || immutable)
                ++needed;
            if (index == NoSlot || !c->Slots[index].Dirty || c->Slots[index].InFlight || c->Slots[index].Pins || immutable)
                ++newDirty;
            if (index == NoSlot || c->Slots[index].InFlight || c->Slots[index].Pins || immutable ||
                (!c->Slots[index].Dirty && c->Slots[index].ReadClass))
                ++newWriteOccupancy;
        }
        // Evictions may remove clean blocks referenced by this request; recalculate
        // the preflight after every eviction before touching any payload.
        auto writeUsed = c->DirtySlots + c->CleanCount[0];
        const auto admissionLimit = QcAdmissionWriteLimit(
            WriteLimit(c), QcCachePagingPathCount(c) > 0, pagingIo);
        if (needed > c->Capacity - c->Count && c->Options.Allocation == QcAutomatic && EvictForWrite(c, blocks))
            continue;
        if (c->Options.Allocation == QcFixed &&
            (needed > c->Capacity - c->Count || writeUsed + newWriteOccupancy > admissionLimit) && Evict(c, 0))
            continue;
        // Strict (#16): a write that adds dirty data waits while the backlog holds as many separate pieces as
        // the drain completes in about two seconds, so a later flush finishes in seconds. Overwrites of dirty
        // blocks and a write into an empty backlog never wait.
        const bool piecesFit = c->UnsafeDefer || !newDirty || !c->StrictPieces ||
                               c->StrictPieces < QcStrictPieceLimit(c->PiecesPerSecond);
        const bool roomFits = needed <= c->Capacity - c->Count && c->DirtySlots + newDirty <= admissionLimit;
        if ((roomFits && piecesFit) || !NT_SUCCESS(c->State.LastError) || c->Gone || irp->Cancel)
            break;
        if (callerPath)
        {
            // Only clean blocks were evicted; the worker waits for capacity.
            ReleaseCache(c);
            return QcCallerPathDeclined;
        }
        c->WriterWaiting = TRUE;
        ++c->State.ThrottleWaits;
        if (roomFits)
            ++c->Diagnostics.StrictPieceWaits;
        else
        {
            ++c->Performance.CapacityWaits;
            if (pagingIo)
                InterlockedIncrement64(&c->PagingCapacityWaits);
        }
        c->Performance.Phase = QcCapacityPhase;
        Publish(c);
        KeClearEvent(&c->Changed);
        WakeDrainers(c);
        LARGE_INTEGER interval;
        interval.QuadPart = -1000000; // Check cancellation at least every 100 ms.
        const auto waitStart = c->Timing ? Tick() : 0;
        ReleaseCache(c);
        // Preserve ownership/order of the blocked write, but service bounded,
        // independent RAM reads rather than occupying the worker with a sleep.
        if (!c->ServiceReads || !c->ServiceReads(c->ServiceContext, irp))
        {
            if (c->RequestAvailable)
            {
                PVOID objects[] = {&c->Changed, c->RequestAvailable};
                auto waited =
                    KeWaitForMultipleObjects(2, objects, WaitAny, Executive, KernelMode, FALSE, &interval, nullptr);
                if (c->Timing)
                    InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(
                        waited == STATUS_WAIT_0       ? &c->Performance.WaitChanged
                        : waited == STATUS_WAIT_0 + 1 ? &c->Performance.WaitRequestAvailable
                                                      : &c->Performance.WaitTimeout));
            }
            else
            {
                auto waited = KeWaitForSingleObject(&c->Changed, Executive, KernelMode, FALSE, &interval);
                if (c->Timing)
                    InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(
                        waited == STATUS_WAIT_0 ? &c->Performance.WaitChanged : &c->Performance.WaitTimeout));
            }
        }
        AcquireCache(c);
        if (waitStart)
            c->Performance.CapacityWaitTicks += Tick() - waitStart;
    }
    c->WriterWaiting = FALSE;
    c->Performance.Phase = QcRequestPhase;
    if (irp->Cancel)
    {
        ReleaseCache(c);
        return STATUS_CANCELLED;
    }
    if (!NT_SUCCESS(c->State.LastError) || c->Gone)
    {
        auto error = c->Gone ? STATUS_DEVICE_NOT_CONNECTED : c->State.LastError;
        ReleaseCache(c);
        return error;
    }
    // A write-through joins the open group with its unrelated predecessors: the seal
    // below makes the entire group durable before any later group submits.
    NT_ASSERT(!FlushOrdering(c) || writeEpoch == c->WriteEpoch);
    InvalidateSafePaging(c, firstBlock, firstBlock + static_cast<LONGLONG>(blocks) * Chunk, QcSafeCachedWrite);
    ULONG admittedSlot = NoSlot;
    for (auto block = firstBlock; block < end; block += Chunk)
    {
        auto index = FindSlot(c, block);
        if (index == NoSlot || c->Slots[index].InFlight || c->Slots[index].Pins ||
            QcEpochNeedsVersion(FlushOrdering(c), c->Slots[index].Dirty, c->Slots[index].Epoch, writeEpoch))
        {
            const auto previous = index;
            index = AllocateSlot(c);
            auto slot = &c->Slots[index];
            slot->Length = Chunk;
            slot->Offset.QuadPart = block;
            slot->InFlight = FALSE;
            // A newest version must contain every still-valid sector of the older
            // version, since the index returns newest first. Copy only known bytes.
            // Mutex keeps the previous slot alive; its pins/in-flight payload is immutable.
            if (previous != NoSlot)
            {
                auto old = &c->Slots[previous];
                if (QcEpochNeedsVersion(FlushOrdering(c), old->Dirty, old->Epoch, writeEpoch))
                    ++c->Diagnostics.EpochVersionCopies;
                slot->ValidSectors = old->ValidSectors;
                for (ULONG sector = 0; sector < 8; ++sector)
                    if (old->ValidSectors & (1UL << sector))
                        RtlCopyMemory(slot->Buffer + sector * 512, old->Buffer + sector * 512, 512);
                if (old->Dirty) slot->DirtySince = old->DirtySince;
                else old->RetireWhenUnpinned = TRUE;
            }
            IndexSlot(c, index);
            c->State.DirtyBytes += QcValidBytes(slot->ValidSectors);
            ++c->DirtySlots;
            MarkPiece(c, index);
        }
        auto slot = &c->Slots[index];
        if (!slot->Dirty)
        {
            Unlink(c, index);
            slot->Dirty = TRUE;
            slot->ReadClass = FALSE;
            slot->DirtySince = NowMs();
            slot->Epoch = writeEpoch;
            Link(c, index);
            c->State.DirtyBytes += QcValidBytes(slot->ValidSectors);
            ++c->DirtySlots;
            MarkPiece(c, index);
        }
        slot->Filling = TRUE;
        if (blocks == 1)
            admittedSlot = index;
    }
    if (FlushOrdering(c))
    {
        c->EpochHasWrites = TRUE;
        // Seal at admission, before an offloaded copy lets the foreground owner
        // accept another non-overlapping write. Filling keeps this version alive.
        if (writeThrough) SealEpoch(c); // Overflow was excluded before reservation.
    }
    // A large write's copy can run on an offloaded-request thread, in parallel with
    // other copies. Its entry keeps the range owned until FinishWrite: overlapping
    // writes and reads wait for it (their Filling blocks are not readable) and
    // controls wait for every entry, exactly as for offloaded reads.
    if (!callerPath && !pagingIo && length >= QcCopyOffloadMinBytes && blocks <= QcPagingPinBlocks &&
        c->ReadThreads[0].Thread && c->CompleteRequest)
    {
        KIRQL irql;
        KeAcquireSpinLock(&c->PagingLock, &irql);
        QC_PAGING_READ* entry = nullptr;
        if (!c->PagingStop)
            for (auto& candidate : c->PagingReads)
                if (!candidate.Irp)
                {
                    entry = &candidate;
                    break;
                }
        if (entry)
        {
            entry->Irp = irp;
            entry->Start = offset.QuadPart;
            entry->End = end;
            entry->Sequence = ++c->PagingSequence;
            entry->Started = FALSE;
            entry->Write = TRUE;
            entry->WriteThrough = writeThrough;
            entry->Trim = FALSE;
            entry->Source = source;
            RecordMaximum(&c->PagingOffloadMaxQueued, ++c->PagingQueued);
        }
        KeReleaseSpinLock(&c->PagingLock, irql);
        if (entry)
        {
            ReleaseCache(c);
            InterlockedIncrement64(&c->CopyOffloadWrites);
            KeSetEvent(&c->PagingWork, IO_NO_INCREMENT, FALSE);
            return QcWriteTransferred; // The thread owns irp; do not touch it again.
        }
    }
    // No other foreground request runs during publication. Filling prevents
    // drain selection; bounded pointer batches allow payload copies unlocked.
    for (auto block = firstBlock; block < end;)
    {
        PUCHAR buffers[64];
        ULONG count = static_cast<ULONG>(min(64LL, (end - block + Chunk - 1) / Chunk));
        for (ULONG i = 0; i < count; ++i)
            buffers[i] = c->Slots[admittedSlot != NoSlot ? admittedSlot : FindSlot(c, block + i * Chunk)].Buffer;
        ReleaseCache(c);
        for (ULONG i = 0; i < count; ++i)
        {
            const auto current = block + i * Chunk;
            const auto from = max(current, offset.QuadPart);
            const auto to = min(current + Chunk, end);
            RtlCopyMemory(buffers[i] + (from - current), source + (from - offset.QuadPart),
                          static_cast<SIZE_T>(to - from));
        }
        AcquireCache(c);
        block += count * Chunk;
    }
    return FinishWrite(c, irp, offset.QuadPart, length, admittedSlot != NoSlot ? &admittedSlot : nullptr,
                       writeThrough, pagingIo);
}
// Publishes a copied write: its sectors become valid and its blocks stop Filling.
// slots: one index per block, or null to look up the (newest, Filling) versions.
// Caller holds Mutex; it is released on return.
static NTSTATUS FinishWrite(QC_CACHE* c, PIRP irp, LONGLONG offset, ULONG length, const ULONG* slots,
                            bool writeThrough, bool pagingIo)
{
    const auto firstBlock = offset / Chunk * Chunk;
    const auto end = offset + length;
    ULONGLONG epoch = 0; // Admission gave every version of this request the same group.
    for (auto block = firstBlock; block < end; block += Chunk)
    {
        auto slot = &c->Slots[slots ? slots[(block - firstBlock) / Chunk] : FindSlot(c, block)];
        const auto from = max(block, offset);
        const auto to = min(block + Chunk, end);
        const auto added = QcSectorMask(static_cast<ULONG>(from - block), static_cast<ULONG>(to - from));
        c->State.DirtyBytes += QcValidBytes(added & ~slot->ValidSectors);
        slot->ValidSectors |= added;
        slot->Filling = FALSE;
        epoch = slot->Epoch;
    }
    c->State.AcceptedBytes += length;
    c->LastWriteTime = NowMs();
    if (writeThrough)
        ++c->Diagnostics.DeferredWriteThroughWrites;
    c->State.PeakDirtyBytes = max(c->State.PeakDirtyBytes, c->State.DirtyBytes);
    Publish(c);
    bool pressure = c->Pressure != FALSE;
    const auto ordinaryLimit = QcAdmissionWriteLimit(
        WriteLimit(c), QcCachePagingPathCount(c) > 0, false);
    const bool pagingReservePressure = pagingIo && c->DirtySlots > ordinaryLimit;
    const bool forced = c->Barrier || c->WriterWaiting || c->EpochWaiters || pagingReservePressure;
    if (QcShouldWakeAfterWrite(c->Options, c->State.DirtyBytes, static_cast<ULONGLONG>(WriteLimit(c)) * Chunk,
                              c->LastWriteTime - c->Slots[c->Head].DirtySince, c->State.InFlightBytes,
                              forced, pressure) &&
        QcWriteWakes(FlushOrdering(c), forced, epoch, c->DrainEpoch))
        WakeDrainers(c);
    c->Pressure = pressure;
    ReleaseCache(c);
    if (pagingIo)
    {
        InterlockedIncrement64(&c->PagingAdmittedWrites);
        InterlockedAdd64(&c->PagingAdmittedBytes, length);
    }
    irp->IoStatus.Information = length;
    return STATUS_SUCCESS;
}
// Foreground admission remains single-owner. Pins protect exact cached versions
// from drainer retirement while lower reads and payload copies run without Mutex.
// pinned: an offloaded-read thread's scratch (QcPagingPinBlocks entries, range already
// bounded). An offloaded read runs concurrently with the request worker, which
// may add a clean read-fill or a newer version for these blocks. It therefore
// overlays and unpins exactly the versions it pinned; FindSlot could name a slot
// this read never pinned. Writes overlapping it wait (WaitForPagingReads).
// True when no physical page occurs twice in the request's buffer. Only then is
// every block of a completed read a copy of that block's disk data. A page can
// occur twice when a user buffer maps it at two addresses, and in the memory
// manager's clustered page-ins, which point every already-resident page of the
// cluster at one shared dummy page that concurrent reads overwrite. Copying such
// a position into the cache kept another block's data (0.4.148.1-0.4.162.1:
// corrupted executable pages on C:). Called for misses only (after a disk read):
// a direct scan for small buffers, a sorted pool copy for larger ones.
static bool DistinctPages(PIRP irp)
{
    auto mdl = irp->MdlAddress;
    if (!mdl || mdl->Next)
        return false;
    const auto pages = MmGetMdlPfnArray(mdl);
    // One MDL describes at most a ULONG byte count, so its page span fits a ULONG.
    const auto count = static_cast<ULONG>(ADDRESS_AND_SIZE_TO_SPAN_PAGES(MmGetMdlVirtualAddress(mdl), MmGetMdlByteCount(mdl)));
    if (count <= 64)
    {
        for (ULONG i = 1; i < count; ++i)
            for (ULONG j = 0; j < i; ++j)
                if (pages[i] == pages[j])
                    return false;
        return true;
    }
    auto sorted = static_cast<PFN_NUMBER*>(ExAllocatePool2(POOL_FLAG_NON_PAGED, count * sizeof(PFN_NUMBER), Tag));
    if (!sorted)
        return false; // Not provable: do not keep this miss.
    RtlCopyMemory(sorted, pages, count * sizeof(PFN_NUMBER));
    // Heap sort: bounded work, no recursion.
    auto sift = [sorted](ULONG root, ULONG size) {
        for (;;)
        {
            auto child = root * 2 + 1;
            if (child >= size)
                return;
            if (child + 1 < size && sorted[child + 1] > sorted[child])
                ++child;
            if (sorted[root] >= sorted[child])
                return;
            const auto swap = sorted[root];
            sorted[root] = sorted[child];
            sorted[child] = swap;
            root = child;
        }
    };
    for (ULONG i = count / 2; i-- > 0;)
        sift(i, count);
    for (ULONG end = count; end-- > 1;)
    {
        const auto swap = sorted[0];
        sorted[0] = sorted[end];
        sorted[end] = swap;
        sift(0, end);
    }
    bool distinct = true;
    for (ULONG i = 1; i < count && distinct; ++i)
        distinct = sorted[i] != sorted[i - 1];
    ExFreePoolWithTag(sorted, Tag);
    return distinct;
}
// callerPath (with hitOnly): QcCacheTryCallerPath; the read-service counters
// stay the worker's, and a mapping failure declines instead of failing.
// Returned by Read with offloadMiss for an ordinary miss, before any pin or disk I/O:
// QcCacheProcess hands it to an offloaded-read thread (QcCopyOffloadMisses).
static constexpr NTSTATUS QcReadMissOffload = static_cast<NTSTATUS>(0x6000A002L);
static NTSTATUS Read(QC_CACHE* c, PIRP irp, bool hitOnly = false, bool allowReadService = true,
                     ULONG* pinned = nullptr, bool callerPath = false, bool offloadMiss = false)
{
    auto stack = IoGetCurrentIrpStackLocation(irp);
    auto length = stack->Parameters.Read.Length;
    auto start = stack->Parameters.Read.ByteOffset.QuadPart;
    auto end = start + length;
    const bool pagingIo = (irp->Flags & IRP_PAGING_IO) != 0;
    AcquireCache(c);
    if (c->Gone || irp->Cancel)
    {
        auto error = c->Gone ? STATUS_DEVICE_NOT_CONNECTED : STATUS_CANCELLED;
        ReleaseCache(c);
        return error;
    }
    if (length == 0)
    {
        ReleaseCache(c);
        irp->IoStatus.Information = 0;
        return STATUS_SUCCESS;
    }
    const bool resident = !pagingIo || ResidentRange(c, start, end);
    // A page-in of ordinary file data (Windows' file cache or a mapped file) is kept like
    // any other miss (#19), always from a driver-owned copy of the disk data (StagedRead):
    // a clustered page-in's own buffer can repeat the memory manager's shared dummy page
    // (DistinctPages), so it is never the source. Paging-file reads, forced-direct ranges
    // and requests of unknown origin are not kept (ApplicationPaging).
    const bool keepPaging = pagingIo && !hitOnly && length <= QcStagedReadMaxBytes && c->Enabled &&
        ApplicationPaging(c, irp, start, end);
    if (pagingIo && !resident && !keepPaging)
    {
        // No cached version exists to overlay, and this page-in is not kept. The request
        // worker may service independent reads during this wait; offloaded-read threads
        // and nested service never do.
        ReleaseCache(c);
        if (hitOnly)
            return STATUS_NOT_FOUND;
        if (!DistinctPages(irp))
            InterlockedIncrement64(&c->PagingReadsRepeatedPages); // Evidence only.
        return OriginalIo(c, irp, allowReadService);
    }
    if (!NT_SUCCESS(c->State.LastError))
    {
        auto error = c->State.LastError;
        ReleaseCache(c);
        return error;
    }
    if (!c->Enabled && !c->State.DirtyBytes)
    {
        ReleaseCache(c);
        return hitOnly ? STATUS_NOT_FOUND : OriginalIo(c, irp, allowReadService);
    }
    auto target = Map(irp);
    if (!target)
    {
        if (callerPath)
        {
            ReleaseCache(c);
            return STATUS_NOT_FOUND;
        }
        if (irp->Flags & IRP_PAGING_IO)
            InterlockedIncrement64(&c->PagingMapFailures);
        ReleaseCache(c);
        // Nothing cached to overlay: the uncached read is correct, just not retained.
        if (pagingIo && !resident)
            return OriginalIo(c, irp, allowReadService);
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    bool full = true;
    const auto copyFlags = InterlockedCompareExchange(&c->CopyFlags, 0, 0);
    // QcCopyBookkeeping: record every block's slot while checking, so pinning needs no second lookup.
    const bool batched = pinned && (copyFlags & QcCopyBookkeeping);
    for (auto block = start / Chunk * Chunk; block < end; block += Chunk)
    {
        auto index = FindSlot(c, block);
        if (batched)
            pinned[(block - start / Chunk * Chunk) / Chunk] = index;
        const auto from = max(start, block);
        const auto to = min(end, block + Chunk);
        if (full && (index == NoSlot || c->Slots[index].Filling ||
            !QcCovers(c->Slots[index].ValidSectors, static_cast<ULONG>(from - block), static_cast<ULONG>(to - from))))
        {
            full = false;
            if (!batched || hitOnly)
                break; // Unbatched pinning looks the blocks up again; a hit-only read declines.
        }
    }
    if (hitOnly && !full)
    {
        if (!callerPath)
            ++c->Performance.BypassMisses;
        Publish(c);
        ReleaseCache(c);
        return STATUS_NOT_FOUND;
    }
    // The worker would wait here for the disk, one miss at a time (issue #14).
    if (offloadMiss && !full && !pagingIo && length <= QcPagingPinBlocks * Chunk &&
        (copyFlags & QcCopyOffloadMisses))
    {
        ReleaseCache(c);
        return QcReadMissOffload;
    }
    const auto firstBlock = start / Chunk * Chunk;
    // Every reader keeps the identities it pinned, including missing entries.
    // Another miss can fill a previously absent block while lower I/O is pending;
    // looking that block up again would overlay/unpin a version we never pinned.
    // Keep the common <=1 MiB path on bounded stack/thread scratch. Larger worker
    // reads use pool scratch and fail before pinning if it cannot be allocated.
    ULONG localPins[QcPagingPinBlocks];
    ULONG* allocatedPins = nullptr;
    if (!pinned)
    {
        const auto blocks = static_cast<SIZE_T>((end - firstBlock + Chunk - 1) / Chunk);
        if (blocks <= RTL_NUMBER_OF(localPins))
            pinned = localPins;
        else
        {
            allocatedPins = static_cast<ULONG*>(ExAllocatePool2(POOL_FLAG_NON_PAGED, blocks * sizeof(ULONG), Tag));
            if (!allocatedPins)
            {
                ReleaseCache(c);
                return STATUS_INSUFFICIENT_RESOURCES;
            }
            pinned = allocatedPins;
        }
    }
    for (auto block = firstBlock; block < end; block += Chunk)
    {
        const auto k = static_cast<ULONG>((block - firstBlock) / Chunk);
        auto index = batched ? pinned[k] : FindSlot(c, block);
        pinned[k] = index;
        if (index != NoSlot)
            ++c->Slots[index].Pins;
    }
    NTSTATUS status = STATUS_SUCCESS;
    // A miss is kept only from a driver-owned copy of the disk data (StagedRead).
    // Both worker and offloaded reads retain staged misses, including page-ins of
    // ordinary file data (keepPaging). Pin scratch controls version ownership, not admission.
    PUCHAR staging = nullptr;
    const bool keepMiss = !full && (!pagingIo || keepPaging) && !hitOnly && c->Enabled && ReadLimit(c) &&
        !InterlockedCompareExchange(&c->ControlsInFlight, 0, 0);
    ULONGLONG stagedCachedBytes = 0;
    if (keepMiss && length <= QcStagedReadMaxBytes)
    {
        // Only staging candidates scan for accounting. Fully resident reads and
        // writes keep their existing hot paths. Mutex and the pins above protect
        // this pre-submission snapshot; Filling bytes are not known yet.
        for (auto block = firstBlock; block < end; block += Chunk)
        {
            const auto index = FindSlot(c, block);
            if (index == NoSlot || c->Slots[index].Filling) continue;
            const auto from = max(start, block), to = min(end, block + Chunk);
            stagedCachedBytes += QcValidBytes(c->Slots[index].ValidSectors &
                QcSectorMask(static_cast<ULONG>(from - block), static_cast<ULONG>(to - from)));
        }
    }
    if (!full)
    {
        c->Performance.Phase = QcLowerReadPhase;
        Publish(c);
        ReleaseCache(c);
        if (keepMiss)
            staging = StagedRead(c, start, length, stagedCachedBytes, allowReadService, &status);
        if (staging)
        {
            if (NT_SUCCESS(status))
            {
                RtlCopyMemory(target, staging, length);
                irp->IoStatus.Information = length;
            }
        }
        else
        {
            if (keepMiss)
                InterlockedIncrement64(&c->ReadFillsSkippedRepeatedPages); // V17: now "not staged".
            status = OriginalIo(c, irp, allowReadService);
        }
        AcquireCache(c);
        c->Performance.Phase = QcRequestPhase;
    }
    else
        irp->IoStatus.Information = length;
    if (NT_SUCCESS(status) && irp->IoStatus.Information != length)
        status = STATUS_DEVICE_DATA_ERROR;
    if (NT_SUCCESS(status))
    {
        // A pinned version is immutable and stays allocated (writers create a new
        // version; eviction, retirement and trim skip it), so its buffer and valid
        // mask are stable: copy the whole range with one Mutex release instead of
        // one per 64 blocks, which convoyed parallel offloaded reads on Mutex.
        ULONGLONG hitBytes = 0, missBytes = 0;
        ReleaseCache(c);
        for (auto block = firstBlock; block < end; block += Chunk)
        {
            const auto index = pinned[(block - firstBlock) / Chunk];
            const auto from = max(start, block);
            const auto to = min(end, block + Chunk);
            const auto bytes = static_cast<ULONG>(to - from);
            if (index == NoSlot)
            {
                missBytes += bytes;
                continue;
            }
            const auto buffer = c->Slots[index].Buffer;
            const auto valid = c->Slots[index].ValidSectors;
            const auto hits = valid == 255 ? bytes :
                QcValidBytes(valid & QcSectorMask(static_cast<ULONG>(from - block), bytes));
            hitBytes += hits;
            missBytes += bytes - hits;
            if (valid == 255)
            {
                const auto source = buffer + (from - block);
                SIZE_T run = bytes;
                // QcCopyCoalesce: extend over following whole, valid blocks in the next slot of the same
                // 256 KiB chunk (always the next 4 KiB of memory). Runs never cross a chunk: uncapped
                // runs measured 22 instead of 36 GB/s at Q8 on a fresh cache (plan 96).
                // Pinned versions are immutable, so their slots and masks are stable here.
                for (auto current = index; (copyFlags & QcCopyCoalesce) && from + static_cast<LONGLONG>(run) == block + Chunk &&
                                           block + Chunk < end;)
                {
                    const auto next = pinned[(block + Chunk - firstBlock) / Chunk];
                    if (next != current + 1 || next % SlotsPerSlab == 0 || c->Slots[next].ValidSectors != 255)
                        break;
                    current = next;
                    block += Chunk;
                    const auto more = static_cast<ULONG>(min(end, block + Chunk) - block);
                    run += more;
                    hitBytes += more;
                }
                // QcCopyPrefetch: start loading the next block while this one is copied.
                if ((copyFlags & QcCopyPrefetch) && block + Chunk < end)
                    if (const auto next = pinned[(block + Chunk - firstBlock) / Chunk]; next != NoSlot)
                        for (ULONG line = 0; line < Chunk; line += 64)
                            PreFetchCacheLine(PF_TEMPORAL_LEVEL_1, c->Slots[next].Buffer + line);
                RtlCopyMemory(target + (from - start), source, run);
            }
            else
                for (auto sector = from; sector < to; sector += 512)
                    if (valid & (1UL << ((sector - block) / 512)))
                        RtlCopyMemory(target + (sector - start), buffer + (sector - block), 512);
        }
        AcquireCache(c);
        c->ReadHitBytes += hitBytes;
        c->State.CacheReadBytes += hitBytes;
        c->ReadMissBytes += missBytes;
    }
    // Release every pin before admission can evict anything. Deferred retirement
    // allows a read to finish even if retention is disabled and draining completed.
    for (auto block = firstBlock; block < end; block += Chunk)
    {
        auto index = pinned[(block - firstBlock) / Chunk];
        if (index != NoSlot)
            UnpinSlot(c, index);
    }
    if (NT_SUCCESS(status))
    {
        const bool fillable = staging != nullptr;
        if (!full && pagingIo && !DistinctPages(irp))
            InterlockedIncrement64(&c->PagingReadsRepeatedPages); // Evidence only.
        ULONG runFirst = NoSlot, runLast = NoSlot; // QcCopyBookkeeping: neighbours in one clean list.
        for (auto block = firstBlock; block < end; block += Chunk)
        {
            auto index = FindSlot(c, block);
            // An unpinned version may have retired; touch only the version this
            // read used when it is still the current one.
            const auto original = pinned[(block - firstBlock) / Chunk];
            if (original != NoSlot && index != original)
                continue; // Its cached bytes may have been newer than the lower read.
            if (index != NoSlot && batched && TouchCleanMovesOnly(c, index))
            {
                if (runLast != NoSlot && c->Slots[runLast].QueueNext == index)
                {
                    runLast = index;
                    continue;
                }
                if (runFirst != NoSlot)
                    TouchCleanRun(c, runFirst, runLast);
                runFirst = runLast = index;
                continue;
            }
            if (runFirst != NoSlot)
            {
                TouchCleanRun(c, runFirst, runLast);
                runFirst = runLast = NoSlot;
            }
            if (index != NoSlot)
            {
                TouchClean(c, index);
                continue;
            }
            if (!fillable || block < start || block + Chunk > end)
                continue;
            const bool recalled = RecallReadFill(c, block); // Before ReadRoom evicts its victim.
            if (ReadRoom(c))
            {
                index = AllocateSlot(c, false, true);
                auto fill = &c->Slots[index];
                fill->Offset.QuadPart = block;
                fill->Length = Chunk;
                fill->ValidSectors = 255;
                c->CleanValidBytes[1] += Chunk; // AllocateSlot linked it with an empty mask.
                RtlCopyMemory(fill->Buffer, staging + (block - start), Chunk);
                IndexSlot(c, index);
                PlaceReadFill(c, index, recalled);
                InterlockedIncrement64(&c->ReadFills);
                if (pagingIo)
                    InterlockedIncrement64(&c->PagingReadFills);
            }
        }
        if (runFirst != NoSlot)
            TouchCleanRun(c, runFirst, runLast);
        if (hitOnly && !callerPath)
            ++c->Performance.BypassReads;
    }
    Publish(c);
    ReleaseCache(c);
    if (staging)
        ExFreePoolWithTag(staging, Tag);
    if (allocatedPins)
        ExFreePoolWithTag(allocatedPins, Tag);
    return status;
}
bool QcCacheTryReadHit(QC_CACHE* c, PIRP irp, LONGLONG deviceBytes, NTSTATUS* status)
{
    auto stack = IoGetCurrentIrpStackLocation(irp);
    if (stack->MajorFunction != IRP_MJ_READ)
        return false;
    auto offset = stack->Parameters.Read.ByteOffset.QuadPart;
    auto length = stack->Parameters.Read.Length;
    if (offset < 0 || offset > deviceBytes || !length || length > static_cast<ULONGLONG>(deviceBytes - offset))
        return false;
    if (c->SectorBytes && (offset % c->SectorBytes || length % c->SectorBytes))
        return false;
    *status = Read(c, irp, true);
    if (*status != STATUS_NOT_FOUND && (irp->Flags & IRP_PAGING_IO))
    {
        InterlockedIncrement64(&c->PagingRoutedReadRequests);
        InterlockedIncrement64(NT_SUCCESS(*status)
            ? &c->PagingRoutedReadCompletions : &c->PagingRoutedReadFailures);
    }
    return *status != STATUS_NOT_FOUND;
}
// Serves a RAM read hit or a write that fits on the dispatching thread, saving
// the queue hand-off, the worker wake-up and the cross-thread completion. Only
// dispatch calls it, and only when nothing else is queued or active: the worker
// waits for DirectIdle, so this is the sole foreground owner, exactly like the
// worker (drainers and in-flight offloaded paging reads continue as usual).
// Paging I/O, write-through in Strict mode, misses, mapping failures and capacity
// waits decline unchanged so the worker applies its normal ordered path. A
// faulted, removed or cancelled cache returns the status the worker would.
bool QcCacheTryCallerPath(QC_CACHE* c, PIRP irp, LONGLONG deviceBytes, NTSTATUS* status)
{
    auto stack = IoGetCurrentIrpStackLocation(irp);
    const bool read = stack->MajorFunction == IRP_MJ_READ;
    const auto offset = stack->Parameters.Read.ByteOffset.QuadPart;
    const auto length = stack->Parameters.Read.Length;
    if ((!read && stack->MajorFunction != IRP_MJ_WRITE) || (irp->Flags & IRP_PAGING_IO) || !c->Enabled ||
        !length || deviceBytes <= 0 || offset < 0 || offset > deviceBytes ||
        length > static_cast<ULONGLONG>(deviceBytes - offset) ||
        !c->SectorBytes || offset % c->SectorBytes || length % c->SectorBytes)
        return false;
    irp->IoStatus.Information = 0;
    // Pin scratch lets a read hit copy without retaking Mutex (dispatch checked the stack).
    ULONG pins[QcPagingPinBlocks];
    const bool pinScratch = (offset + length - offset / Chunk * Chunk + Chunk - 1) / Chunk <= QcPagingPinBlocks;
    *status = read ? Read(c, irp, true, false, pinScratch ? pins : nullptr, true) : Write(c, irp, true);
    if (*status == (read ? STATUS_NOT_FOUND : QcCallerPathDeclined))
    {
        InterlockedIncrement64(&c->CallerPathDeclined);
        return false;
    }
    InterlockedIncrement64(read ? &c->CallerPathReads : &c->CallerPathWrites);
    return true;
}
bool QcCacheTryPagingReadProgress(QC_CACHE* c, PIRP irp, LONGLONG deviceBytes, NTSTATUS* status)
{
    auto stack = IoGetCurrentIrpStackLocation(irp);
    if (stack->MajorFunction != IRP_MJ_READ || !(irp->Flags & IRP_PAGING_IO))
        return false;
    auto offset = stack->Parameters.Read.ByteOffset.QuadPart;
    auto length = stack->Parameters.Read.Length;
    if (offset < 0 || offset > deviceBytes || !length ||
        length > static_cast<ULONGLONG>(deviceBytes - offset) ||
        (c->SectorBytes && (offset % c->SectorBytes || length % c->SectorBytes)))
        return false;
    AcquireCache(c);
    const bool overlapsActiveWrite = (c->RangeDrain &&
        offset < c->RangeEnd && offset + length > c->RangeStart) ||
        PagingReadsOverlap(c, offset, offset + length, true); // Filling blocks of a write copy.
    ReleaseCache(c);
    if (overlapsActiveWrite)
        return false;
    // One bounded, independent page-in can unblock the lower operation on the
    // foreground worker. No recursive read service is allowed from its lower wait.
    InterlockedIncrement64(&c->PagingRoutedReadRequests);
    *status = Read(c, irp, false, false);
    InterlockedIncrement64(NT_SUCCESS(*status) ? &c->PagingRoutedReadCompletions : &c->PagingRoutedReadFailures);
    if (NT_SUCCESS(*status))
        InterlockedIncrement64(&c->PagingServicedReadMisses);
    return true;
}
// Caller holds Mutex. Like ResidentRange/PendingRange, a released cache has no
// index: Bucket() divides by Capacity, so it must never be reached with zero.
// (0.4.99.1-0.4.104.1 lacked this check: a paging read arriving just after a
// Release bugchecked with 0x7E/divide-by-zero in FindSlot.)
static bool FullyResident(QC_CACHE* c, LONGLONG start, LONGLONG end)
{
    if (!c->Capacity)
        return false;
    for (auto block = start / Chunk * Chunk; block < end; block += Chunk)
    {
        auto index = FindSlot(c, block);
        const auto from = max(start, block);
        const auto to = min(end, block + Chunk);
        if (index == NoSlot || c->Slots[index].Filling ||
            !QcCovers(c->Slots[index].ValidSectors, static_cast<ULONG>(from - block), static_cast<ULONG>(to - from)))
            return false;
    }
    return true;
}
bool QcCacheOffloadPagingRead(QC_CACHE* c, PIRP irp, LONGLONG deviceBytes, bool miss)
{
    auto stack = IoGetCurrentIrpStackLocation(irp);
    const bool pagingIo = (irp->Flags & IRP_PAGING_IO) != 0;
    if (miss && (pagingIo || !(InterlockedCompareExchange(&c->CopyFlags, 0, 0) & QcCopyOffloadMisses)))
        return false;
    if (stack->MajorFunction != IRP_MJ_READ || !c->ReadThreads[0].Thread || !c->CompleteRequest ||
        (!pagingIo && !miss && stack->Parameters.Read.Length < QcCopyOffloadMinBytes))
        return false;
    const auto start = stack->Parameters.Read.ByteOffset.QuadPart;
    const auto length = stack->Parameters.Read.Length;
    if (start < 0 || start > deviceBytes || !length ||
        length > static_cast<ULONGLONG>(deviceBytes - start) ||
        (c->SectorBytes && (start % c->SectorBytes || length % c->SectorBytes)))
        return false;
    const auto end = start + length;
    // Larger reads keep the ordered worker path (bounded pin scratch).
    if ((end - start / Chunk * Chunk + Chunk - 1) / Chunk > QcPagingPinBlocks)
        return false;
    // Only the request worker sets these fields and offloads, so the decision
    // cannot race a new fence, active write or blocking request.
    // A paging read is offloaded for its disk wait: a RAM hit completes faster on
    // the worker. A large ordinary read is offloaded as a RAM hit, so several copies
    // run at once. An ordinary miss (miss: Read found it on the worker, with
    // QcCopyOffloadMisses) is offloaded for its disk wait and keeps its read fill:
    // the thread runs the worker's Read, and writes wait for overlapping offloaded
    // reads, so its blocks cannot change meanwhile. If the range filled up in the
    // meantime, that Read simply serves it from RAM.
    AcquireCache(c);
    const bool refused = c->OffloadBlocked || c->Stop || c->Gone || !c->Enabled ||
        (c->RangeDrain && start < c->RangeEnd && end > c->RangeStart) ||
        (c->ActiveWrite && start < c->ActiveWriteEnd && end > c->ActiveWriteStart) ||
        (!miss && FullyResident(c, start, end) == pagingIo) || PagingReadsOverlap(c, start, end, true);
    ReleaseCache(c);
    if (refused)
        return false;
    KIRQL irql;
    KeAcquireSpinLock(&c->PagingLock, &irql);
    QC_PAGING_READ* entry = nullptr;
    if (!c->PagingStop)
        for (auto& read : c->PagingReads)
            if (!read.Irp)
            {
                entry = &read;
                break;
            }
    if (entry)
    {
        entry->Irp = irp;
        entry->Start = start;
        entry->End = end;
        entry->Sequence = ++c->PagingSequence;
        entry->Started = FALSE;
        entry->Write = entry->WriteThrough = FALSE;
        entry->Trim = FALSE;
        entry->Source = nullptr;
        RecordMaximum(&c->PagingOffloadMaxQueued, ++c->PagingQueued);
    }
    KeReleaseSpinLock(&c->PagingLock, irql);
    if (!entry)
        return false; // Table full: the caller keeps its existing ordered path.
    // An offloaded-read thread owns irp from here; do not touch it again.
    if (pagingIo)
    {
        InterlockedIncrement64(&c->PagingOffloadedReads);
        InterlockedIncrement64(&c->PagingRoutedReadRequests);
    }
    else
        InterlockedIncrement64(&c->CopyOffloadReads);
    KeSetEvent(&c->PagingWork, IO_NO_INCREMENT, FALSE);
    return true;
}
// Payload copy of a write admitted by the request worker (Write). Its blocks are
// the newest, Filling versions: nothing can replace them while the entry exists.
static NTSTATUS CopyOffloadedWrite(QC_CACHE* c, const QC_PAGING_READ* entry, ULONG* slots)
{
    const auto offset = entry->Start;
    const auto end = entry->End;
    const auto firstBlock = offset / Chunk * Chunk;
    AcquireCache(c);
    for (auto block = firstBlock; block < end; block += Chunk)
        slots[(block - firstBlock) / Chunk] = FindSlot(c, block);
    ReleaseCache(c);
    for (auto block = firstBlock; block < end; block += Chunk)
    {
        const auto from = max(block, offset);
        const auto to = min(block + Chunk, end);
        RtlCopyMemory(c->Slots[slots[(block - firstBlock) / Chunk]].Buffer + (from - block),
                      entry->Source + (from - offset), static_cast<SIZE_T>(to - from));
    }
    AcquireCache(c);
    auto status = FinishWrite(c, entry->Irp, offset, static_cast<ULONG>(end - offset), slots,
                              entry->WriteThrough != FALSE, false);
    // Barriers and direct paging writes wait on Changed for Filling blocks.
    KeSetEvent(&c->Changed, IO_NO_INCREMENT, FALSE);
    return status;
}
// QcReadThreads per disk. Each executes the oldest waiting offloaded read; reads
// never order against each other, only against the writes and controls that wait
// for them. It depends only on Mutex (never held across waits) and lower
// completion. It never runs the read service, never waits for the request worker
// and never admits cached writes.
static void PagingReader(PVOID context)
{
    auto reader = static_cast<QC_READ_THREAD*>(context);
    auto c = reader->Cache;
    for (;;)
    {
        QC_PAGING_READ* next = nullptr;
        ULONG waiting = 0;
        KIRQL irql;
        KeAcquireSpinLock(&c->PagingLock, &irql);
        for (auto& read : c->PagingReads)
            if (read.Irp && !read.Started)
            {
                ++waiting;
                if (!next || read.Sequence < next->Sequence)
                    next = &read;
            }
        if (next)
            next->Started = TRUE;
        const bool stop = c->PagingStop && !next;
        KeReleaseSpinLock(&c->PagingLock, irql);
        // PagingWork wakes one thread (and repeated signals coalesce): pass it on
        // while other reads wait, and when stopping so every thread exits.
        if (stop || waiting > 1)
            KeSetEvent(&c->PagingWork, IO_NO_INCREMENT, FALSE);
        if (stop)
            break;
        if (!next)
        {
            KeWaitForSingleObject(&c->PagingWork, Executive, KernelMode, FALSE, nullptr);
            continue;
        }
        auto irp = next->Irp;
        const bool pagingIo = (irp->Flags & IRP_PAGING_IO) != 0;
        // Each read owns a stable pin snapshot, even when other misses fill the
        // same blocks during its lower wait. Ordinary staged misses are retained.
        auto status = next->Trim ? RunAsyncTrim(c, next)
                      : next->Write ? CopyOffloadedWrite(c, next, reader->Pins)
                                    : Read(c, irp, false, false, reader->Pins);
        if (pagingIo)
        {
            InterlockedIncrement64(NT_SUCCESS(status) ? &c->PagingOffloadCompletions : &c->PagingOffloadFailures);
            InterlockedIncrement64(NT_SUCCESS(status) ? &c->PagingRoutedReadCompletions : &c->PagingRoutedReadFailures);
        }
        c->CompleteRequest(c->ServiceContext, irp, status);
        KeAcquireSpinLock(&c->PagingLock, &irql);
        if (!next->Trim)
            --c->PagingQueued;
        next->Irp = nullptr;
        KeReleaseSpinLock(&c->PagingLock, irql);
        KeSetEvent(&c->PagingDone, IO_NO_INCREMENT, FALSE);
    }
    PsTerminateSystemThread(STATUS_SUCCESS);
}
static void SortTrimRanges(DEVICE_DATA_SET_RANGE* ranges, ULONG count)
{
    // Heap sort by start offset: bounded work for any range count, no allocation.
    auto sift = [ranges](ULONG root, ULONG size) {
        for (;;)
        {
            auto child = root * 2 + 1;
            if (child >= size)
                return;
            if (child + 1 < size && ranges[child + 1].StartingOffset > ranges[child].StartingOffset)
                ++child;
            if (ranges[root].StartingOffset >= ranges[child].StartingOffset)
                return;
            auto swap = ranges[root];
            ranges[root] = ranges[child];
            ranges[child] = swap;
            root = child;
        }
    };
    for (auto i = count / 2; i-- > 0;)
        sift(i, count);
    for (auto end = count; end-- > 1;)
    {
        auto swap = ranges[0];
        ranges[0] = ranges[end];
        ranges[end] = swap;
        sift(0, end);
    }
}
// Mutex held. One cached version whose block overlaps a TRIM: a clean copy is
// released; a dirty version loses the trimmed sectors and is retired when none remain.
static void TrimSlot(QC_CACHE* c, ULONG i, unsigned mask, bool cleanOnly = false)
{
    auto slot = &c->Slots[i];
    NT_ASSERT(!slot->InFlight); // overlapping writes were awaited and no batch started since
    if (!slot->Dirty)
    {
        RetireSlot(c, i);
        return;
    }
    // A later discard cannot erase a journal predecessor retained by an earlier
    // volatile flush. It drains before the lower TRIM; newer versions stay fenced.
    if (cleanOnly || (FlushOrdering(c) && slot->Epoch < c->WriteEpoch))
        return;
    const auto trimmed = slot->ValidSectors & mask;
    if (!trimmed)
        return;
    c->DiscardedBytes += QcValidBytes(trimmed);
    c->State.DirtyBytes -= QcValidBytes(trimmed);
    slot->ValidSectors &= ~mask;
    if (!slot->ValidSectors)
    {
        --c->DirtySlots;
        if (slot->PieceStart)
            --c->StrictPieces;
        RetireSlot(c, i);
    }
}
// Mutex held. Drops cached data inside the sorted, merged TRIM ranges. With lookup, a
// TRIM of at most a quarter of the slots visits only its blocks through the index:
// scanning every slot under Mutex cost milliseconds per small TRIM (plan 159).
static void TrimCached(QC_CACHE* c, const DEVICE_DATA_SET_RANGE* ranges, ULONG merged, bool lookup, bool cleanOnly = false)
{
    ULONGLONG blocks = 0;
    for (ULONG j = 0; j < merged && blocks <= c->Capacity / 4; ++j)
    {
        const auto first = ranges[j].StartingOffset / Chunk * Chunk;
        const auto end = ranges[j].StartingOffset + static_cast<LONGLONG>(ranges[j].LengthInBytes);
        blocks += static_cast<ULONGLONG>((end - first + Chunk - 1) / Chunk);
    }
    if (lookup && blocks <= c->Capacity / 4)
    {
        InterlockedIncrement64(&c->TrimLookups);
        // Visiting a block once per covering range equals one visit with their combined mask.
        for (ULONG j = 0; j < merged; ++j)
        {
            const auto start = ranges[j].StartingOffset;
            const auto length = ranges[j].LengthInBytes;
            for (auto block = start / Chunk * Chunk; block < start + static_cast<LONGLONG>(length); block += Chunk)
            {
                const auto mask = QcTrimMask(block, start, length);
                if (!mask)
                    continue;
                for (auto i = c->Buckets[Bucket(c, block)]; i != NoSlot;)
                {
                    const auto next = c->Slots[i].HashNext;
                    if (c->Slots[i].Offset.QuadPart == block)
                        TrimSlot(c, i, mask, cleanOnly);
                    i = next;
                }
            }
        }
        return;
    }
    for (ULONG i = 0; i < c->Capacity; ++i)
    {
        auto slot = &c->Slots[i];
        if (!slot->Length)
            continue;
        const auto block = slot->Offset.QuadPart;
        // First range starting at or beyond this block's end; earlier ones may cover it.
        ULONG low = 0, high = merged;
        while (low < high)
        {
            auto mid = low + (high - low) / 2;
            if (ranges[mid].StartingOffset < block + Chunk)
                low = mid + 1;
            else
                high = mid;
        }
        unsigned mask = 0;
        for (auto j = low; j-- > 0;)
        {
            if (ranges[j].StartingOffset + static_cast<LONGLONG>(ranges[j].LengthInBytes) <= block)
                break;
            mask |= QcTrimMask(block, ranges[j].StartingOffset, ranges[j].LengthInBytes);
        }
        if (mask)
            TrimSlot(c, i, mask, cleanOnly);
    }
}
// Hands an asynchronous lower TRIM to an offload thread. The caller (inside this
// TRIM's own ordered processing, with offloads blocked) holds no lock.
static bool OffloadTrim(QC_CACHE* c, PIRP irp, ULONG fence, LONGLONG low, LONGLONG high)
{
    if (!c->ReadThreads[0].Thread || !c->CompleteRequest)
        return false;
    KIRQL irql;
    KeAcquireSpinLock(&c->PagingLock, &irql);
    QC_PAGING_READ* entry = nullptr;
    if (!c->PagingStop)
        for (auto& read : c->PagingReads)
            if (!read.Irp)
            {
                entry = &read;
                break;
            }
    if (entry)
    {
        entry->Irp = irp;
        entry->Start = low;
        entry->End = high;
        entry->Sequence = ++c->PagingSequence;
        entry->Started = FALSE;
        entry->Write = entry->WriteThrough = FALSE;
        entry->Trim = TRUE;
        entry->Fence = fence;
        entry->Source = nullptr;
        // Not counted in PagingQueued: the caller path must not back off while a slow lower TRIM
        // is outstanding (plan 164). Its cache side is applied and its ranges are fenced.
    }
    KeReleaseSpinLock(&c->PagingLock, irql);
    if (!entry)
        return false;
    KeSetEvent(&c->PagingWork, IO_NO_INCREMENT, FALSE);
    return true;
}
// The lower TRIM of TrimFences[fence] returned: count it and lift its fence.
static void FinishAsyncTrim(QC_CACHE* c, ULONG fence, NTSTATUS status)
{
    AcquireCache(c);
    if (NT_SUCCESS(status))
        ++c->TrimRequests;
    auto& entry = c->TrimFences[fence];
    auto ranges = entry.Ranges;
    if (FlushOrdering(c))
    {
        TrimCached(c, ranges, entry.Count, true, true); // Older dirty versions may now be retained clean.
        c->EpochNeedsFlush = TRUE; // Only a lower flush makes the TRIM durable before later groups.
    }
    entry.Ranges = nullptr;
    entry.Count = 0;
    --c->TrimFenceCount;
    Publish(c);
    KeSetEvent(&c->Changed, IO_NO_INCREMENT, FALSE);
    WakeDrainers(c);
    ReleaseCache(c);
    ExFreePoolWithTag(ranges, Tag);
}
// Offload thread. QcTrimLabHold holds the lower TRIM first, so trim-cache can act
// while it is outstanding; drain batches are unaffected.
static NTSTATUS WaitTrimEpoch(QC_CACHE* c, ULONG fence)
{
    AcquireCache(c);
    if (!FlushOrdering(c))
    {
        ReleaseCache(c);
        return STATUS_SUCCESS;
    }
    const auto epoch = c->TrimFences[fence].Epoch;
    ++c->EpochWaiters;
    if (c->DrainEpoch < epoch) ++c->Diagnostics.EpochTrimWaits;
    WakeDrainers(c);
    while (c->DrainEpoch < epoch && NT_SUCCESS(c->State.LastError) && !c->Gone && !c->Stop)
    {
        KeClearEvent(&c->Changed);
        ReleaseCache(c);
        LARGE_INTEGER interval;
        interval.QuadPart = -1000000;
        KeWaitForSingleObject(&c->Changed, Executive, KernelMode, FALSE, &interval);
        AcquireCache(c);
    }
    --c->EpochWaiters;
    const auto status = c->Gone ? STATUS_DEVICE_NOT_CONNECTED : c->Stop ? STATUS_DEVICE_NOT_READY : c->State.LastError;
    Publish(c);
    ReleaseCache(c);
    return status;
}
static NTSTATUS RunAsyncTrim(QC_CACHE* c, const QC_PAGING_READ* entry)
{
    auto status = WaitTrimEpoch(c, entry->Fence);
    if (!NT_SUCCESS(status))
    {
        FinishAsyncTrim(c, entry->Fence, status);
        return status;
    }
    const auto delay = (InterlockedCompareExchange(&c->TrimMode, 0, 0) & QcTrimLabHold) ? QcTrimLabHoldMs : 0UL;
    if (delay)
    {
        LARGE_INTEGER interval;
        interval.QuadPart = -10000LL * delay;
        KeDelayExecutionThread(KernelMode, FALSE, &interval);
    }
    status = OriginalIo(c, entry->Irp, false);
    FinishAsyncTrim(c, entry->Fence, status);
    return status;
}
// True while an asynchronous lower TRIM overlapping [first, end) is outstanding.
static bool TrimsOverlap(QC_CACHE* c, LONGLONG first, LONGLONG end)
{
    bool overlap = false;
    KIRQL irql;
    KeAcquireSpinLock(&c->PagingLock, &irql);
    for (const auto& read : c->PagingReads)
        if (read.Irp && read.Trim && read.Start < end && read.End > first)
        {
            overlap = true;
            break;
        }
    KeReleaseSpinLock(&c->PagingLock, irql);
    return overlap;
}
// Mutex held. True while a drainer's outstanding lower write overlaps one of these
// sorted, merged TRIM ranges: the TRIM must not reach the disk first, or that write
// would land afterwards on sectors the file system has just freed.
static bool TrimOverlapsInFlight(QC_CACHE* c, const DEVICE_DATA_SET_RANGE* ranges, ULONG count)
{
    for (const auto& worker : c->Workers)
    {
        if (worker.InFlightStart == worker.InFlightEnd)
            continue;
        for (ULONG i = 0; i < count && ranges[i].StartingOffset < worker.InFlightEnd; ++i)
            if (ranges[i].StartingOffset + static_cast<LONGLONG>(ranges[i].LengthInBytes) > worker.InFlightStart)
                return true;
    }
    return false;
}
// TRIM: the file system declares these sectors free. Cached writes for them are
// dropped instead of written (they would overwrite what the disk now treats as
// unallocated), clean copies are dropped, and only already-issued writes that
// overlap a trimmed range are awaited. Any number of sector-aligned ranges is
// handled; only unknown flags or malformed input take the conservative ordered path.
static bool TryTrim(QC_CACHE* c, PIRP irp, LONGLONG deviceBytes, NTSTATUS* result)
{
    auto stack = IoGetCurrentIrpStackLocation(irp);
    auto length = stack->Parameters.DeviceIoControl.InputBufferLength;
    if (length < sizeof(DEVICE_MANAGE_DATA_SET_ATTRIBUTES) || !irp->AssociatedIrp.SystemBuffer)
        return false;
    auto input = static_cast<DEVICE_MANAGE_DATA_SET_ATTRIBUTES*>(irp->AssociatedIrp.SystemBuffer);
    if (input->Size != sizeof(*input) || input->Action != DeviceDsmAction_Trim ||
        (input->Flags & ~(DEVICE_DSM_FLAG_TRIM_NOT_FS_ALLOCATED | DEVICE_DSM_FLAG_TRIM_BYPASS_RZAT)) ||
        input->ParameterBlockLength || input->ParameterBlockOffset || input->DataSetRangesOffset < sizeof(*input) ||
        input->DataSetRangesOffset % alignof(DEVICE_DATA_SET_RANGE) || input->DataSetRangesOffset > length ||
        input->DataSetRangesLength > length - input->DataSetRangesOffset || !input->DataSetRangesLength ||
        input->DataSetRangesLength % sizeof(DEVICE_DATA_SET_RANGE))
        return false;
    const ULONG count = input->DataSetRangesLength / sizeof(DEVICE_DATA_SET_RANGE);
    DEVICE_DATA_SET_RANGE local[128];
    auto ranges = local;
    if (count > RTL_NUMBER_OF(local))
    {
        ranges = static_cast<DEVICE_DATA_SET_RANGE*>(
            ExAllocatePool2(POOL_FLAG_NON_PAGED, static_cast<SIZE_T>(count) * sizeof(DEVICE_DATA_SET_RANGE), Tag));
        if (!ranges)
            return false;
    }
    RtlCopyMemory(ranges,
                  static_cast<PUCHAR>(irp->AssociatedIrp.SystemBuffer) + input->DataSetRangesOffset,
                  input->DataSetRangesLength);
    const LONGLONG sector = c->SectorBytes ? c->SectorBytes : 512;
    for (ULONG i = 0; i < count; ++i)
    {
        const auto& range = ranges[i];
        if (range.StartingOffset < 0 || range.StartingOffset > deviceBytes || !range.LengthInBytes ||
            range.LengthInBytes > static_cast<ULONGLONG>(deviceBytes - range.StartingOffset) ||
            range.StartingOffset % sector || range.LengthInBytes % sector)
        {
            if (ranges != local)
                ExFreePoolWithTag(ranges, Tag);
            return false;
        }
    }
    SortTrimRanges(ranges, count);
    // Merge overlap/adjacency so each block's covering ranges are contiguous in the array.
    ULONG merged = 0;
    for (ULONG i = 0; i < count; ++i)
    {
        if (merged && ranges[i].StartingOffset <=
                          ranges[merged - 1].StartingOffset + static_cast<LONGLONG>(ranges[merged - 1].LengthInBytes))
        {
            auto end = max(ranges[i].StartingOffset + static_cast<LONGLONG>(ranges[i].LengthInBytes),
                           ranges[merged - 1].StartingOffset + static_cast<LONGLONG>(ranges[merged - 1].LengthInBytes));
            ranges[merged - 1].LengthInBytes = end - ranges[merged - 1].StartingOffset;
        }
        else
            ranges[merged++] = ranges[i];
    }
    AcquireCache(c);
    for (ULONG i = 0; i < merged; ++i)
        InvalidateSafePaging(c, ranges[i].StartingOffset,
            ranges[i].StartingOffset + static_cast<LONGLONG>(ranges[i].LengthInBytes), QcSafeTrim);
    c->TrimPaused = TRUE;
    // The serialized request worker admits no subsequent writes while this request
    // runs, and TrimPaused starts no new drain batch. Wait only for already-issued
    // lower writes that overlap a trimmed range, NOT all queued payload: waiting for
    // every in-flight batch stalled Eager writes behind each TRIM (plan 158).
    while (TrimOverlapsInFlight(c, ranges, merged) && !c->Gone)
    {
        KeClearEvent(&c->Changed);
        ReleaseCache(c);
        KeWaitForSingleObject(&c->Changed, Executive, KernelMode, FALSE, nullptr);
        AcquireCache(c);
    }
    auto status = c->Gone ? STATUS_DEVICE_NOT_CONNECTED : c->State.LastError;
    const auto mode = static_cast<ULONG>(InterlockedCompareExchange(&c->TrimMode, 0, 0));
    if (FlushOrdering(c) && c->WriteEpoch >= MAXULONGLONG - 2)
    {
        status = STATUS_INTEGER_OVERFLOW;
        Fault(c, status);
    }
    if (NT_SUCCESS(status) && (mode & QcTrimAsync))
    {
        // Asynchronous lower TRIM: drop the cached data now, fence the ranges so no
        // newer version drains before the lower TRIM completes, and let an offload
        // thread send it while this worker continues with later requests.
        ULONG fence = QcTrimFenceSlots;
        for (ULONG i = 0; i < QcTrimFenceSlots && fence == QcTrimFenceSlots; ++i)
            if (!c->TrimFences[i].Ranges)
                fence = i;
        auto owned = ranges != local ? ranges : nullptr;
        if (fence != QcTrimFenceSlots && !owned)
        {
            owned = static_cast<DEVICE_DATA_SET_RANGE*>(
                ExAllocatePool2(POOL_FLAG_NON_PAGED, static_cast<SIZE_T>(merged) * sizeof(DEVICE_DATA_SET_RANGE), Tag));
            if (owned)
                RtlCopyMemory(owned, ranges, static_cast<SIZE_T>(merged) * sizeof(DEVICE_DATA_SET_RANGE));
        }
        if (fence != QcTrimFenceSlots && owned)
        {
            TrimCached(c, owned, merged, (mode & QcTrimLookup) != 0);
            auto& entry = c->TrimFences[fence];
            if (FlushOrdering(c))
            {
                SealEpoch(c);
                entry.Epoch = c->WriteEpoch;
                c->EpochHasWrites = TRUE;
                SealEpoch(c); // A lower TRIM is its own ordered effect, including an empty range cache.
            }
            else entry.Epoch = 0;
            entry.Ranges = owned;
            entry.Count = merged;
            entry.Low = owned[0].StartingOffset;
            entry.High = owned[merged - 1].StartingOffset + static_cast<LONGLONG>(owned[merged - 1].LengthInBytes);
            ++c->TrimFenceCount;
            const auto low = entry.Low, high = entry.High;
            c->TrimPaused = FALSE;
            Publish(c);
            KeSetEvent(&c->Changed, IO_NO_INCREMENT, FALSE);
            WakeDrainers(c);
            ReleaseCache(c);
            // The fence owns the ranges now, on every path below.
            if (OffloadTrim(c, irp, fence, low, high))
            {
                InterlockedIncrement64(&c->TrimAsync);
                *result = STATUS_PENDING; // the offload thread completes irp
                return true;
            }
            InterlockedIncrement64(&c->TrimAsyncFallbacks); // table full: send it here
            status = WaitTrimEpoch(c, fence);
            if (NT_SUCCESS(status)) status = OriginalIo(c, irp);
            FinishAsyncTrim(c, fence, status);
            *result = status;
            return true;
        }
        if (owned && owned != ranges)
            ExFreePoolWithTag(owned, Tag);
        InterlockedIncrement64(&c->TrimAsyncFallbacks); // no fence slot: ordered path
    }
    const bool ordered = FlushOrdering(c);
    if (ordered && NT_SUCCESS(status))
    {
        // No fence/table memory: reconcile the closed history synchronously.
        // Only this unsupported/fallback TRIM path holds the foreground owner.
        TrimCached(c, ranges, merged, (mode & QcTrimLookup) != 0);
        c->TrimPaused = FALSE;
        ReleaseCache(c);
        status = QcCacheBarrier(c, FALSE, QcOrderedBarrier, irp);
    }
    else ReleaseCache(c);
    NTSTATUS orderedFlush = STATUS_SUCCESS;
    if (NT_SUCCESS(status))
    {
        status = OriginalIo(c, irp);
        if (ordered)
        {
            orderedFlush = LowerIo(c, IRP_MJ_FLUSH_BUFFERS);
            if (NT_SUCCESS(status)) status = orderedFlush;
        }
    }
    AcquireCache(c);
    if (!NT_SUCCESS(orderedFlush) && NT_SUCCESS(c->State.LastError)) Fault(c, orderedFlush);
    if (NT_SUCCESS(status))
    {
        ++c->TrimRequests;
        TrimCached(c, ranges, merged, (mode & QcTrimLookup) != 0, ordered);
    }
    c->TrimPaused = FALSE;
    Publish(c);
    KeSetEvent(&c->Changed, IO_NO_INCREMENT, FALSE);
    WakeDrainers(c);
    ReleaseCache(c);
    if (ranges != local)
        ExFreePoolWithTag(ranges, Tag);
    *result = status;
    return true;
}
static NTSTATUS Process(QC_CACHE* c, PIRP irp, LONGLONG deviceBytes)
{
    auto stack = IoGetCurrentIrpStackLocation(irp);
    irp->IoStatus.Information = 0;
    if (c->State.DeviceBytes != static_cast<ULONGLONG>(deviceBytes))
    {
        // Unchanged for nearly every request: skip the mutex and snapshot copy then.
        AcquireCache(c);
        c->State.DeviceBytes = deviceBytes;
        Publish(c);
        ReleaseCache(c);
    }
    if (c->Gone)
    {
        if ((stack->MajorFunction == IRP_MJ_PNP && !QcTrackedUsageNotification(stack)) ||
            stack->MajorFunction == IRP_MJ_POWER || stack->MajorFunction == IRP_MJ_CLEANUP ||
            stack->MajorFunction == IRP_MJ_CLOSE)
            return OriginalIo(c, irp);
        const auto status = STATUS_DEVICE_NOT_CONNECTED;
        if (QcTrackedUsageNotification(stack))
        {
            if (stack->Parameters.UsageNotification.InPath)
                QcCacheRecordUsage(c, stack->Parameters.UsageNotification.Type, FALSE);
            QcCacheRecordUsageCompletion(c, stack->Parameters.UsageNotification.Type,
                stack->Parameters.UsageNotification.InPath, status);
        }
        return status;
    }
    if (stack->MajorFunction == IRP_MJ_DEVICE_CONTROL &&
        stack->Parameters.DeviceIoControl.IoControlCode == IOCTL_QCACHE_OPTIONS_V1)
    {
        if (stack->Parameters.DeviceIoControl.InputBufferLength != sizeof(QC_OPTIONS))
            return STATUS_INVALID_PARAMETER;
        const auto options = *static_cast<QC_OPTIONS*>(irp->AssociatedIrp.SystemBuffer);
        if (!QcValidOptions(options))
            return STATUS_INVALID_PARAMETER;
        AcquireCache(c);
        if (c->Enabled || c->State.DirtyBytes || !NT_SUCCESS(c->State.LastError))
        {
            ReleaseCache(c);
            return STATUS_DEVICE_BUSY;
        }
        ClearClean(c);
        c->Options = options;
        ++c->Generation;
        Publish(c);
        ReleaseCache(c);
        return STATUS_SUCCESS;
    }
    if (stack->MajorFunction == IRP_MJ_DEVICE_CONTROL &&
        stack->Parameters.DeviceIoControl.IoControlCode == IOCTL_QCACHE_CONTROL_V1)
        return Control(c, irp, deviceBytes);
    if (stack->MajorFunction == IRP_MJ_DEVICE_CONTROL &&
        stack->Parameters.DeviceIoControl.IoControlCode == IOCTL_QCACHE_SPECIAL_RANGES_V1)
        return SetSpecialRanges(c, irp);
    if (stack->MajorFunction == IRP_MJ_DEVICE_CONTROL || stack->MajorFunction == IRP_MJ_INTERNAL_DEVICE_CONTROL)
    {
        auto code = stack->Parameters.DeviceIoControl.IoControlCode;
        // Neither-I/O and raw controller pass-through from an application may contain
        // uncaptured user pointers, which are not valid on this system worker. Kernel
        // components on a volume stack (snapshots, encryption) send kernel pointers.
        if (irp->RequestorMode != KernelMode &&
            ((code & 3) == METHOD_NEITHER || DEVICE_TYPE_FROM_CTL_CODE(code) == FILE_DEVICE_CONTROLLER))
            return STATUS_NOT_SUPPORTED;
    }
    if (stack->MajorFunction == IRP_MJ_SHUTDOWN)
    {
        AcquireCache(c);
        ++c->Diagnostics.ShutdownBarriers;
        Publish(c);
        ReleaseCache(c);
        auto status = QcCacheBarrier(c, TRUE, QcShutdownBarrier, irp);
        AcquireCache(c);
        c->Suspended = TRUE;
        Publish(c);
        ReleaseCache(c);
        return NT_SUCCESS(status) ? OriginalIo(c, irp) : status;
    }
    if (stack->MajorFunction == IRP_MJ_POWER)
    {
        if (stack->Parameters.Power.State.DeviceState != PowerDeviceD0)
        {
            AcquireCache(c);
            const bool resumeEnabled = c->Enabled != FALSE;
            ++c->Diagnostics.PowerBarriers;
            Publish(c);
            ReleaseCache(c);
            auto status = QcCacheBarrier(c, TRUE, QcPowerBarrier, irp);
            if (!NT_SUCCESS(status))
                return status;
            AcquireCache(c);
            c->ResumeEnabled = resumeEnabled;
            c->Suspended = TRUE;
            Publish(c);
            ReleaseCache(c);
        }
        auto status = OriginalIo(c, irp);
        if (NT_SUCCESS(status) && stack->Parameters.Power.State.DeviceState == PowerDeviceD0)
        {
            AcquireCache(c);
            c->Suspended = FALSE;
            if (QcResumeAfterPower(c->ResumeEnabled != FALSE, c->Capacity != 0,
                                   NT_SUCCESS(c->State.LastError), c->Gone != FALSE))
                c->Enabled = TRUE;
            c->ResumeEnabled = FALSE;
            ++c->Generation;
            Publish(c);
            ReleaseCache(c);
        }
        return status;
    }
    if (QcTrackedUsageNotification(stack))
    {

        // In-path was reserved under QueueLock before this request entered the
        // worker. Preserve active routing. If ordinary dirty data predates the
        // first registration, drain only enough to establish its paging reserve.
        auto status = stack->Parameters.UsageNotification.InPath
            ? PrepareUsagePath(c, irp) : STATUS_SUCCESS;
        if (NT_SUCCESS(status))
            status = OriginalIo(c, irp);
        if (stack->Parameters.UsageNotification.InPath)
        {
            if (!NT_SUCCESS(status))
                QcCacheRecordUsage(c, stack->Parameters.UsageNotification.Type, FALSE);
        }
        else if (NT_SUCCESS(status))
            QcCacheRecordUsage(c, stack->Parameters.UsageNotification.Type, FALSE);
        QcCacheRecordUsageCompletion(c, stack->Parameters.UsageNotification.Type,
            stack->Parameters.UsageNotification.InPath, status);
        return status;
    }
    if (stack->MajorFunction == IRP_MJ_PNP && stack->MinorFunction == IRP_MN_QUERY_REMOVE_DEVICE)
    {
        // This IRP is ordered behind all earlier cached writes by the request
        // worker. Keep admission disabled through the PnP transaction, including
        // the interval between our successful query and final removal.
        AcquireCache(c);
        const BOOLEAN wasPending = c->QueryRemovePending;
        const BOOLEAN wasEnabled = c->Enabled;
        ReleaseCache(c);
        // Preserve the original pre-query setting on duplicate queries.
        if (wasPending)
            return OriginalIo(c, irp);
        auto status = QcCacheBarrier(c, TRUE, QcOrderedBarrier, irp);
        if (NT_SUCCESS(status))
            status = OriginalIo(c, irp);
        AcquireCache(c);
        if (NT_SUCCESS(status))
        {
            c->QueryRemovePending = TRUE;
            c->QueryRemoveWasEnabled = wasEnabled;
        }
        else if (!c->Gone && NT_SUCCESS(c->State.LastError))
            c->Enabled = wasEnabled;
        Publish(c);
        ReleaseCache(c);
        return status;
    }
    if (stack->MajorFunction == IRP_MJ_PNP && stack->MinorFunction == IRP_MN_CANCEL_REMOVE_DEVICE)
    {
        const auto status = OriginalIo(c, irp);
        AcquireCache(c);
        if (NT_SUCCESS(status) && c->QueryRemovePending)
        {
            if (!c->Gone && NT_SUCCESS(c->State.LastError))
                c->Enabled = c->QueryRemoveWasEnabled;
            c->QueryRemovePending = FALSE;
            Publish(c);
        }
        ReleaseCache(c);
        return status;
    }
    // Suspended (after shutdown or leaving D0) only blocks re-enabling the cache.
    // Windows still pages after IRP_MJ_SHUTDOWN (exiting processes fault in
    // kernel code), so requests continue: a successful barrier left the cache
    // disabled and empty, which forwards them; a failed one keeps its dirty data
    // authoritative. Failing them here caused bugcheck 0x7A during restart.
    if (stack->MajorFunction == IRP_MJ_DEVICE_CONTROL &&
        stack->Parameters.DeviceIoControl.IoControlCode == IOCTL_STORAGE_MANAGE_DATA_SET_ATTRIBUTES)
    {
        NTSTATUS trimStatus;
        if (TryTrim(c, irp, deviceBytes, &trimStatus))
            return trimStatus;
    }
    if (stack->MajorFunction == IRP_MJ_READ || stack->MajorFunction == IRP_MJ_WRITE)
    {
        auto length = stack->Parameters.Read.Length;
        auto offset = stack->Parameters.Read.ByteOffset.QuadPart;
        if (deviceBytes <= 0 || offset < 0 || offset > deviceBytes ||
            length > static_cast<ULONGLONG>(deviceBytes - offset))
            return STATUS_INVALID_PARAMETER;
        if (c->SectorBytes && (offset % c->SectorBytes || length % c->SectorBytes))
            return STATUS_INVALID_PARAMETER;
        const bool pagingIo = (irp->Flags & IRP_PAGING_IO) != 0;
        const bool write = stack->MajorFunction == IRP_MJ_WRITE;
        if (pagingIo)
            InterlockedIncrement64(write ? &c->PagingRoutedWriteRequests : &c->PagingRoutedReadRequests);
        auto status = write ? Write(c, irp) : Read(c, irp, false, true, nullptr, false, !pagingIo);
        if (pagingIo)
            InterlockedIncrement64(write
                ? NT_SUCCESS(status) ? &c->PagingRoutedWriteCompletions : &c->PagingRoutedWriteFailures
                : NT_SUCCESS(status) ? &c->PagingRoutedReadCompletions : &c->PagingRoutedReadFailures);
        return status;
    }
    if (stack->MajorFunction == IRP_MJ_FLUSH_BUFFERS)
    {
        AcquireCache(c);
        ++c->Diagnostics.ApplicationFlushes;
        const bool defer = c->Enabled && c->UnsafeDefer;
        const auto error = c->State.LastError;
        if (defer && NT_SUCCESS(error))
        {
            ++c->Diagnostics.DeferredFlushes;
            SealEpoch(c);
        }
        const auto deferredStatus = c->State.LastError;
        Publish(c);
        ReleaseCache(c);
        // Unsafe policy changes only application/OS flush semantics. Never hide an existing I/O error.
        if (defer)
            return deferredStatus;
        return QcCacheBarrier(c, FALSE, QcApplicationBarrier, irp);
    }
    // Before the file system lets go of the volume, put its pending writes on the disk (#21): a detach
    // that follows removes the volume without asking. Clean data stays cached; nothing changed on disk.
    if (QcReleasesMedia(irp, stack))
    {
        AcquireCache(c);
        const bool pending = c->State.DirtyBytes != 0;
        if (pending)
        {
            ++c->Diagnostics.OtherBarriers;
            c->Diagnostics.LastBarrierCode = stack->Parameters.DeviceIoControl.IoControlCode;
            Publish(c);
        }
        ReleaseCache(c);
        if (pending)
        {
            auto status = QcCacheBarrier(c, FALSE, QcOrderedBarrier, irp);
            if (!NT_SUCCESS(status))
                return status;
        }
        return OriginalIo(c, irp);
    }
    // A query cannot modify media, so it must neither drain nor invalidate cached data.
    // Windows' storage service, NTFS and monitoring tools poll read-only controls (disk
    // geometry/layout, media presence, SMART, IOCTL_STORAGE_FIRMWARE_GET_INFO) every few
    // seconds; treating each one as "unknown, therefore possibly destructive" emptied the
    // whole clean cache within seconds on an otherwise idle disk. Only newly admitted
    // data, real modifications and explicit pause/remove/reconfigure may displace it.
    if (stack->MajorFunction == IRP_MJ_DEVICE_CONTROL || stack->MajorFunction == IRP_MJ_INTERNAL_DEVICE_CONTROL)
    {
        // Only explicit observations bypass admission. Other controls retain the
        // existing ordered-worker media-invalidation policy; do not turn every
        // vendor inventory query into a clean-cache wipe as a side effect here.
        if (QcObservationRequest(stack) || !QcMayChangeMedia(stack->Parameters.DeviceIoControl.IoControlCode))
            return OriginalIo(c, irp);
    }
    // Modifying and unclassified controls (including unsupported TRIM) must not overtake accepted dirty writes.
    AcquireCache(c);

    bool dirty = c->State.DirtyBytes != 0;
    bool resident = c->CleanCount[0] != 0 || c->CleanCount[1] != 0;
    auto error = c->State.LastError;
    ReleaseCache(c);
    if (!NT_SUCCESS(error))
        return error;
    if (dirty || resident || stack->MajorFunction == IRP_MJ_PNP)
    {
        AcquireCache(c);
        ++c->Diagnostics.OtherBarriers;
        c->Diagnostics.LastBarrierCode =
            stack->MajorFunction == IRP_MJ_DEVICE_CONTROL || stack->MajorFunction == IRP_MJ_INTERNAL_DEVICE_CONTROL
                ? stack->Parameters.DeviceIoControl.IoControlCode
                : stack->MajorFunction;
        Publish(c);
        ReleaseCache(c);
    }
    // Shadow copies: volsnap's flush-and-hold drains the cache (the snapshot then contains
    // data that was pending in RAM); its other controls, notably release-writes, arrive
    // while volsnap holds every write. Draining then would wait for writes that only that
    // release lets through (found on the VM: the hold timed out and the snapshot failed).
    const bool snapshotControl = (stack->MajorFunction == IRP_MJ_DEVICE_CONTROL ||
                                  stack->MajorFunction == IRP_MJ_INTERNAL_DEVICE_CONTROL) &&
                                 QcSnapshotControlWithoutDrain(stack->Parameters.DeviceIoControl.IoControlCode);
    if (snapshotControl)
        dirty = false;
    auto status = dirty || stack->MajorFunction == IRP_MJ_PNP ? QcCacheBarrier(c, stack->MajorFunction == IRP_MJ_PNP, QcOrderedBarrier, irp)
                                                              : STATUS_SUCCESS;
    // Unknown commands can modify media (including unsupported TRIM shapes).
    // Invalidate AFTER the drain, which may itself have retained clean blocks.
    AcquireCache(c);
    ClearClean(c);
    Publish(c);
    ReleaseCache(c);
    if (!NT_SUCCESS(status))
        return status;
    if (stack->MajorFunction == IRP_MJ_DEVICE_CONTROL || stack->MajorFunction == IRP_MJ_INTERNAL_DEVICE_CONTROL)
        return ForwardControl(c, irp);
    return OriginalIo(c, irp);
}
// Request-worker entry point. Offloaded reads run concurrently with the worker,
// so every request class states what it must not overlap:
// - paging read needing lower I/O, or a large read fully in RAM: handed to the
//   offloaded-read threads (never waits here);
// - write: waits only for offloaded reads overlapping its range, servicing
//   independent reads meanwhile; ActiveWrite keeps new offloads out of its range;
// - flush and read-only/observation controls: no slot retirement, no wait;
// - everything else (QueueCache controls, TRIM, power, shutdown, PnP, usage,
//   unknown controls) may retire pinned slots or change device state. It waits
//   for all offloaded reads and blocks new offloads (the synchronous service
//   lane remains) until it completes.
NTSTATUS QcCacheProcess(QC_CACHE* c, PIRP irp, LONGLONG deviceBytes, bool* transferred)
{
    *transferred = false;
    auto stack = IoGetCurrentIrpStackLocation(irp);
    const auto major = stack->MajorFunction;
    if (major == IRP_MJ_READ)
    {
        if (QcCacheOffloadPagingRead(c, irp, deviceBytes))
        {
            *transferred = true;
            return STATUS_PENDING;
        }
        // An older write whose copy is still offloaded owns Filling blocks in this
        // range; this read must return its data, so wait until it is published.
        const auto first = stack->Parameters.Read.ByteOffset.QuadPart;
        const auto length = stack->Parameters.Read.Length;
        if (first >= 0 && length)
            WaitForPagingReads(c, first, first > MAXLONGLONG - static_cast<LONGLONG>(length) ? MAXLONGLONG : first + length,
                               nullptr, nullptr, true);
        auto status = Process(c, irp, deviceBytes);
        if (status != QcReadMissOffload)
            return status;
        if (QcCacheOffloadPagingRead(c, irp, deviceBytes, true))
        {
            *transferred = true;
            return STATUS_PENDING;
        }
        return Read(c, irp); // Refused (table full, overlapping write or fence): the worker reads it.
    }
    if (major == IRP_MJ_WRITE)
    {
        const auto first = stack->Parameters.Write.ByteOffset.QuadPart;
        const auto length = stack->Parameters.Write.Length;
        if (first < 0 || !length)
            return Process(c, irp, deviceBytes); // Rejected or a no-op; owns no range.
        const auto end = first > MAXLONGLONG - static_cast<LONGLONG>(length) ? MAXLONGLONG : first + length;
        AcquireCache(c);
        c->ActiveWrite = TRUE;
        c->ActiveWriteStart = first;
        c->ActiveWriteEnd = end;
        ReleaseCache(c);
        WaitForPagingReads(c, first, end, irp, &c->PagingOffloadWriteWaits);
        auto status = Process(c, irp, deviceBytes);
        AcquireCache(c);
        c->ActiveWrite = FALSE;
        ReleaseCache(c);
        // Write offloaded its payload copy: the thread completes irp.
        *transferred = status == QcWriteTransferred;
        return status;
    }
    const bool control = major == IRP_MJ_DEVICE_CONTROL || major == IRP_MJ_INTERNAL_DEVICE_CONTROL;
    if (major == IRP_MJ_FLUSH_BUFFERS ||
        (control && (QcObservationRequest(stack) || !QcMayChangeMedia(stack->Parameters.DeviceIoControl.IoControlCode))))
        return Process(c, irp, deviceBytes);
    AcquireCache(c);
    c->OffloadBlocked = TRUE;
    ReleaseCache(c);
    // TRIMs commute, so an asynchronous TRIM waits for other offloads (their pins and
    // copies) but not for earlier outstanding lower TRIMs.
    if (control && stack->Parameters.DeviceIoControl.IoControlCode == IOCTL_STORAGE_MANAGE_DATA_SET_ATTRIBUTES &&
        (InterlockedCompareExchange(&c->TrimMode, 0, 0) & QcTrimAsync))
        WaitForPagingReads(c, MINLONGLONG, MAXLONGLONG, nullptr, &c->PagingOffloadIdleWaits);
    else
        QcCacheWaitPagingReads(c);
    auto status = Process(c, irp, deviceBytes);
    AcquireCache(c);
    c->OffloadBlocked = FALSE;
    ReleaseCache(c);
    // A forwarded media-changing control: the lower device completes it.
    *transferred = control && status == QcControlForwarded;
    return status;
}
