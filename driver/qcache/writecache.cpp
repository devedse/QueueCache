// SPDX-License-Identifier: MIT
// Serialized block cache: dirty FIFO, clean write/read LRUs and a single versioned index.
#include "writecache.h"
#include <ntddstor.h>
#include <ntdddisk.h>
#include <ntddscsi.h>
#include "observation.h"
#include "sectorcoverage.h"
#include "../shared/lockedpages.h"
#include "../shared/memorybudget.h"
static constexpr ULONG Chunk = 4096, SlabBytes = 262144, SlotsPerSlab = SlabBytes / Chunk, Tag = 'wCCQ';
static constexpr ULONG NoSlot = MAXULONG;
static constexpr ULONG MaxBatchBytes = 1024 * 1024;
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
#include "cacheblocks.inl"
static void PagingReader(PVOID context);
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
    c->DiagnosticsSnapshot = c->Diagnostics;
    c->Performance.Version = 3;
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
    output->ReadFills = InterlockedCompareExchange64(&c->ReadFills, 0, 0);
    output->PagingReadFills = InterlockedCompareExchange64(&c->PagingReadFills, 0, 0);
    output->CallerPathReads = InterlockedCompareExchange64(&c->CallerPathReads, 0, 0);
    output->CallerPathWrites = InterlockedCompareExchange64(&c->CallerPathWrites, 0, 0);
    output->CallerPathDeclined = InterlockedCompareExchange64(&c->CallerPathDeclined, 0, 0);
    output->CopyOffloadReads = InterlockedCompareExchange64(&c->CopyOffloadReads, 0, 0);
    output->CopyOffloadWrites = InterlockedCompareExchange64(&c->CopyOffloadWrites, 0, 0);
    output->PagingReadsRepeatedPages = InterlockedCompareExchange64(&c->PagingReadsRepeatedPages, 0, 0);
    output->ReadFillsSkippedRepeatedPages = InterlockedCompareExchange64(&c->ReadFillsSkippedRepeatedPages, 0, 0);
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
        InterlockedIncrement64(&c->PagingNoFileObject);
    else if (KeGetCurrentIrql() > APC_LEVEL)
        InterlockedIncrement64(&c->PagingHighIrql);
    else if (FsRtlIsPagingFile(fileObject))
    {
        InterlockedIncrement64(&c->PagingFileRequests);
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
static NTSTATUS LowerIo(QC_CACHE* c, ULONG major, QC_SLOT* slot = nullptr, ULONG inject = 0)
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
static PUCHAR StagedRead(QC_CACHE* c, LONGLONG offset, ULONG length, bool allowReadService, NTSTATUS* status)
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
        (header.Flags != QcRangesForceDirect && header.Flags != QcRangesReference) ||
        inputBytes != sizeof(header) + header.Count * sizeof(QC_SPECIAL_RANGE))
        return STATUS_INVALID_PARAMETER;
    auto ranges = reinterpret_cast<const QC_SPECIAL_RANGE*>(
        static_cast<PUCHAR>(irp->AssociatedIrp.SystemBuffer) + sizeof(header));
    for (ULONG i = 0; i < header.Count; ++i)
        if (ranges[i].Start < 0 || ranges[i].Length <= 0 || ranges[i].Start > MAXLONGLONG - ranges[i].Length)
            return STATUS_INVALID_PARAMETER;
    // A newly forced range may cover cached versions; the direct path's overlap
    // fence reconciles them before any lower write, so no drain is needed here.
    KIRQL irql;
    KeAcquireSpinLock(&c->RangeLock, &irql);
    if (header.Flags == QcRangesForceDirect)
    {
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
static bool DrainCandidate(QC_CACHE* c, ULONG index)
{
    const auto slot = &c->Slots[index];
    if (slot->InFlight || slot->Filling || FindOldestSlot(c, slot->Offset.QuadPart) != index)
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
    // Before forwarding the fenced write, choose its oldest eligible overlap
    // first, but keep unrelated policy-eligible work moving when that overlap is
    // already owned by another lower request. After forwarding, exclude the fence.
    if (c->RangeDrain && !c->RangeForward)
        for (auto index = c->Head; index != NoSlot; index = c->Slots[index].QueueNext)
            if (DrainCandidate(c, index) &&
                RangeOverlaps(c->RangeStart, c->RangeEnd, c->Slots[index].Offset.QuadPart))
                return index;
    if (!policyDrain)
        return NoSlot;
    for (auto index = c->Head; index != NoSlot; index = c->Slots[index].QueueNext)
        if (DrainCandidate(c, index))
            return index;
    return NoSlot;
}
// writesOnly: only offloaded write copies (whose blocks are still Filling).
static bool PagingReadsOverlap(QC_CACHE* c, LONGLONG first, LONGLONG end, bool writesOnly = false)
{
    bool overlap = false;
    KIRQL irql;
    KeAcquireSpinLock(&c->PagingLock, &irql);
    for (const auto& read : c->PagingReads)
        if (read.Irp && (!writesOnly || read.Write) && read.Start < end && read.End > first)
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
                               volatile LONG64* counter, bool writesOnly = false)
{
    bool counted = false;
    for (;;)
    {
        KeClearEvent(&c->PagingDone);
        if (!PagingReadsOverlap(c, first, end, writesOnly))
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
    return PagingReadsOverlap(c, MINLONGLONG, MAXLONGLONG);
}
void QcCacheWaitPagingReads(QC_CACHE* c)
{
    WaitForPagingReads(c, MINLONGLONG, MAXLONGLONG, nullptr, &c->PagingOffloadIdleWaits);
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
                                         c->Barrier || c->WriterWaiting,
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
        for (ULONG back = 1; back < batchBlocks && c->Slots[index].Offset.QuadPart >= Chunk; ++back)
        {
            auto previous = FindOldestSlot(c, c->Slots[index].Offset.QuadPart - Chunk);
            if (previous == NoSlot || c->Slots[previous].InFlight || c->Slots[previous].Filling ||
                c->Slots[previous].ValidSectors != 255)
                break;
            if (FenceSplitsBatch(c, c->Slots[previous].Offset.QuadPart, batchOverlapsFence))
                break;
            index = previous;
        }
        auto first = &c->Slots[index];
        QC_SLOT io = *first;
        ULONG selected[MaxBatchBytes / Chunk];
        ULONG merged = 0;
        io.Buffer = c->DrainBuffer + worker->Number * c->DrainCapacity;
        io.Length = 0;
        while (index != NoSlot && merged < batchBlocks)
        {
            auto slot = &c->Slots[index];
            if (slot->InFlight || slot->Filling)
                break;
            if (merged && slot->ValidSectors != 255)
                break;
            if (FenceSplitsBatch(c, slot->Offset.QuadPart, batchOverlapsFence))
                break;
            selected[merged++] = index;
            slot->InFlight = TRUE;
            ++slot->Pins;
            io.Length += Chunk;
            index = FindOldestSlot(c, io.Offset.QuadPart + io.Length);
        }
        const auto transferBytes = io.ValidSectors == 255 ? io.Length : QcValidBytes(io.ValidSectors);
        c->State.InFlightBytes += transferBytes;
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
                status = LowerIo(c, IRP_MJ_WRITE, &io, inject ? inject : gateInject);
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
                    status = LowerIo(c, IRP_MJ_WRITE, &part, inject ? inject : lastRun ? gateInject : 0);
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
            for (ULONG i = 0; i < merged; ++i)
            {
                auto completedIndex = selected[i];
                auto slot = &c->Slots[completedIndex];
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
    c->Head = c->Tail = c->FreeHead = NoSlot;
    c->CleanHead[0] = c->CleanHead[1] = c->CleanTail[0] = c->CleanTail[1] = NoSlot;
    LARGE_INTEGER frequency;
    KeQueryPerformanceCounter(&frequency);
    c->Performance.Frequency = frequency.QuadPart;
    c->Options = QcDefaultOptions();
    c->CallerPath = QcDefaultCallerPath;
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
    c->DirtySlots = 0;
    c->CleanValidBytes[0] = c->CleanValidBytes[1] = 0;
    c->CleanHead[0] = c->CleanHead[1] = c->CleanTail[0] = c->CleanTail[1] = NoSlot;
    c->Slots = nullptr;
    c->Capacity = 0;
    c->Head = c->Tail = c->FreeHead = NoSlot;
    if (c->Buckets)
        ExFreePoolWithTag(c->Buckets, Tag);
    c->Buckets = nullptr;
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
        c->Enabled = FALSE;
    c->Barrier = TRUE;
    c->Performance.Phase = QcDrainPhase;
    Publish(c);
    while (c->State.DirtyBytes && NT_SUCCESS(c->State.LastError) && !c->Gone)
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
        ++c->State.Flushes;
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
    c->DrainCapacity = budget < (16ULL << 20) ? SlabBytes / 4 : MaxBatchBytes;
    auto stagingBytes = c->DrainCapacity * RTL_NUMBER_OF(c->Workers);
    // Reserve the page-rounded slab-handle table first (one PMDL per slab).
    const auto slabTableReserve = ((budget / SlabBytes + 1) * sizeof(PMDL) + PAGE_SIZE - 1) & ~(static_cast<ULONGLONG>(PAGE_SIZE) - 1);
    auto n = static_cast<ULONG>((budget - 2 * PAGE_SIZE - stagingBytes - slabTableReserve) /
                                (Chunk + sizeof(QC_SLOT) + sizeof(ULONG)));
    n = n / SlotsPerSlab * SlotsPerSlab;
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
        c->Slots[i].FreeNext = i + 1 < n ? i + 1 : NoSlot;
    }
    c->FreeHead = 0;
    c->State.BudgetBytes = budget;
    c->State.ReservedBytes = stagingBytes + descriptors + slabTableBytes + indexBytes + static_cast<ULONGLONG>(n) * Chunk;
    c->State.PayloadCapacity = static_cast<ULONGLONG>(n) * Chunk;
    ++c->Generation;
    Publish(c);
    ReleaseCache(c);
    return STATUS_SUCCESS;
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
    AcquireCache(c);
    NTSTATUS status = STATUS_SUCCESS;
    switch (command.Action)
    {
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
            // System and data disks share one activation policy. Usage-path
            // registration is ordered by this routing lock and admission keeps
            // reserve for paging traffic; it is not a reason to reject Enable.
            c->Enabled = TRUE;
            ++c->Generation;
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
static NTSTATUS DirectPagingWrite(QC_CACHE* c, PIRP irp, LONGLONG first, ULONG length)
{
    const auto end = first + length;
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
    AcquireCache(c);
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
        return DirectPagingWrite(c, irp, offset.QuadPart, length);
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
        ReleaseCache(c);
        return OriginalIo(c, irp);
    }
    // Sector-valid partial writes use the same RAM admission path as full blocks.
    // Only explicit durability/disabled/over-budget fallbacks remain here.
    if (!c->Enabled || (writeThrough && !c->UnsafeDefer) || needed > WriteLimit(c))
    {
        auto reason = !c->Enabled ? QcDisabledWriteBarrier :
            (writeThrough && !c->UnsafeDefer) ? QcStrictWriteBarrier : QcQuotaWriteBarrier;
        ReleaseCache(c);
        auto status = QcCacheBarrier(c, FALSE, reason, irp);
        AcquireCache(c);
        InvalidateCleanRange(c, offset.QuadPart, length);
        Publish(c);
        ReleaseCache(c);
        return NT_SUCCESS(status) ? OriginalIo(c, irp) : status;
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
            return DirectPagingWrite(c, irp, offset.QuadPart, length);
        }
        ReleaseCache(c);
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    for (;;)
    {
        // Preflight the WHOLE request before modifying any payload. Recompute after
        // every wait because the drainer may have removed or pinned indexed slots.
        needed = 0;
        ULONG newDirty = 0, newWriteOccupancy = 0;
        for (auto block = firstBlock; block < end; block += Chunk)
        {
            auto index = FindSlot(c, block);
            if (index == NoSlot || c->Slots[index].InFlight || c->Slots[index].Pins)
                ++needed;
            if (index == NoSlot || !c->Slots[index].Dirty || c->Slots[index].InFlight || c->Slots[index].Pins)
                ++newDirty;
            if (index == NoSlot || c->Slots[index].InFlight || c->Slots[index].Pins ||
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
        if ((needed <= c->Capacity - c->Count && c->DirtySlots + newDirty <= admissionLimit) ||
            !NT_SUCCESS(c->State.LastError) || c->Gone || irp->Cancel)
            break;
        if (callerPath)
        {
            // Only clean blocks were evicted; the worker waits for capacity.
            ReleaseCache(c);
            return QcCallerPathDeclined;
        }
        c->WriterWaiting = TRUE;
        ++c->State.ThrottleWaits;
        ++c->Performance.CapacityWaits;
        if (pagingIo)
            InterlockedIncrement64(&c->PagingCapacityWaits);
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
    ULONG admittedSlot = NoSlot;
    for (auto block = firstBlock; block < end; block += Chunk)
    {
        auto index = FindSlot(c, block);
        if (index == NoSlot || c->Slots[index].InFlight || c->Slots[index].Pins)
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
        }
        auto slot = &c->Slots[index];
        if (!slot->Dirty)
        {
            Unlink(c, index);
            slot->Dirty = TRUE;
            slot->ReadClass = FALSE;
            slot->DirtySince = NowMs();
            Link(c, index);
            c->State.DirtyBytes += QcValidBytes(slot->ValidSectors);
            ++c->DirtySlots;
        }
        slot->Filling = TRUE;
        if (blocks == 1)
            admittedSlot = index;
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
    for (auto block = firstBlock; block < end; block += Chunk)
    {
        auto slot = &c->Slots[slots ? slots[(block - firstBlock) / Chunk] : FindSlot(c, block)];
        const auto from = max(block, offset);
        const auto to = min(block + Chunk, end);
        const auto added = QcSectorMask(static_cast<ULONG>(from - block), static_cast<ULONG>(to - from));
        c->State.DirtyBytes += QcValidBytes(added & ~slot->ValidSectors);
        slot->ValidSectors |= added;
        slot->Filling = FALSE;
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
    if (QcShouldWakeAfterWrite(c->Options, c->State.DirtyBytes, static_cast<ULONGLONG>(WriteLimit(c)) * Chunk,
                              c->LastWriteTime - c->Slots[c->Head].DirtySince, c->State.InFlightBytes,
                              c->Barrier || c->WriterWaiting || pagingReservePressure, pressure))
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
    const auto count = ADDRESS_AND_SIZE_TO_SPAN_PAGES(MmGetMdlVirtualAddress(mdl), MmGetMdlByteCount(mdl));
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
static NTSTATUS Read(QC_CACHE* c, PIRP irp, bool hitOnly = false, bool allowReadService = true,
                     ULONG* pinned = nullptr, bool callerPath = false)
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
    if (pagingIo && !resident)
    {
        // No cached version exists to overlay. Paging read misses are never kept:
        // a clustered page-in's buffer can repeat the shared dummy page (see
        // DistinctPages). The request worker may service independent reads during
        // this wait; offloaded-read threads and nested service never do.
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
    for (auto block = start / Chunk * Chunk; block < end; block += Chunk)
    {
        auto index = FindSlot(c, block);
        const auto from = max(start, block);
        const auto to = min(end, block + Chunk);
        if (index == NoSlot || c->Slots[index].Filling ||
            !QcCovers(c->Slots[index].ValidSectors, static_cast<ULONG>(from - block), static_cast<ULONG>(to - from)))
        {
            full = false;
            break;
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
    const auto firstBlock = start / Chunk * Chunk;
    for (auto block = firstBlock; block < end; block += Chunk)
    {
        auto index = FindSlot(c, block);
        if (pinned)
            pinned[(block - firstBlock) / Chunk] = index;
        if (index != NoSlot)
            ++c->Slots[index].Pins;
    }
    NTSTATUS status = STATUS_SUCCESS;
    // A miss is kept only from a driver-owned copy of the disk data (StagedRead).
    // Only the request worker keeps misses; paging reads never do (DistinctPages).
    PUCHAR staging = nullptr;
    const bool keepMiss = !full && !pagingIo && !pinned && !hitOnly && c->Enabled && ReadLimit(c) &&
        !InterlockedCompareExchange(&c->ControlsInFlight, 0, 0);
    if (!full)
    {
        c->Performance.Phase = QcLowerReadPhase;
        Publish(c);
        ReleaseCache(c);
        if (keepMiss)
            staging = StagedRead(c, start, length, allowReadService, &status);
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
    if (NT_SUCCESS(status) && pinned)
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
                RtlCopyMemory(target + (from - start), buffer + (from - block), static_cast<SIZE_T>(bytes));
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
    else if (NT_SUCCESS(status))
    {
        for (auto block = start / Chunk * Chunk; block < end;)
        {
            PUCHAR buffers[64];
            ULONG valid[64];
            ULONG count = 0;
            auto first = block;
            for (; count < RTL_NUMBER_OF(buffers) && block < end; ++count, block += Chunk)
            {
                auto index = pinned ? pinned[(block - firstBlock) / Chunk] : FindSlot(c, block);
                buffers[count] = index == NoSlot ? nullptr : c->Slots[index].Buffer;
                valid[count] = index == NoSlot ? 0 : c->Slots[index].ValidSectors;
                const auto from = max(start, block);
                const auto bytes = static_cast<ULONG>(min(end, block + Chunk) - from);
                const auto hits = valid[count] == 255 ? bytes :
                    QcValidBytes(valid[count] & QcSectorMask(static_cast<ULONG>(from - block), bytes));
                c->ReadHitBytes += hits;
                c->State.CacheReadBytes += hits;
                c->ReadMissBytes += bytes - hits;
            }
            ReleaseCache(c);
            for (ULONG i = 0; i < count; ++i)
                if (buffers[i])
                {
                    auto current = first + i * Chunk;
                    auto from = max(start, current);
                    auto to = min(end, current + Chunk);
                    if (valid[i] == 255)
                        RtlCopyMemory(target + (from - start), buffers[i] + (from - current), static_cast<SIZE_T>(to - from));
                    else
                        for (auto sector = from; sector < to; sector += 512)
                            if (valid[i] & (1UL << ((sector - current) / 512)))
                                RtlCopyMemory(target + (sector - start), buffers[i] + (sector - current), 512);
                }
            AcquireCache(c);
        }
    }
    // Release every pin before admission can evict anything. Deferred retirement
    // allows a read to finish even if retention is disabled and draining completed.
    for (auto block = firstBlock; block < end; block += Chunk)
    {
        auto index = pinned ? pinned[(block - firstBlock) / Chunk] : FindSlot(c, block);
        if (index != NoSlot)
            UnpinSlot(c, index);
    }
    if (NT_SUCCESS(status))
    {
        const bool fillable = staging != nullptr;
        if (!full && pagingIo && !DistinctPages(irp))
            InterlockedIncrement64(&c->PagingReadsRepeatedPages); // Evidence only.
        for (auto block = firstBlock; block < end; block += Chunk)
        {
            auto index = FindSlot(c, block);
            // An unpinned version may have retired; touch only the version this
            // read used when it is still the current one.
            if (pinned && index != pinned[(block - firstBlock) / Chunk])
                continue;
            if (index != NoSlot)
            {
                TouchClean(c, index);
                continue;
            }
            if (fillable && block >= start && block + Chunk <= end && ReadRoom(c))
            {
                index = AllocateSlot(c, false, true);
                auto fill = &c->Slots[index];
                fill->Offset.QuadPart = block;
                fill->Length = Chunk;
                fill->ValidSectors = 255;
                c->CleanValidBytes[1] += Chunk; // AllocateSlot linked it with an empty mask.
                RtlCopyMemory(fill->Buffer, staging + (block - start), Chunk);
                IndexSlot(c, index);
                DemoteReadFill(c, index);
                InterlockedIncrement64(&c->ReadFills);
            }
        }
        if (hitOnly && !callerPath)
            ++c->Performance.BypassReads;
    }
    Publish(c);
    ReleaseCache(c);
    if (staging)
        ExFreePoolWithTag(staging, Tag);
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
bool QcCacheOffloadPagingRead(QC_CACHE* c, PIRP irp, LONGLONG deviceBytes)
{
    auto stack = IoGetCurrentIrpStackLocation(irp);
    const bool pagingIo = (irp->Flags & IRP_PAGING_IO) != 0;
    if (stack->MajorFunction != IRP_MJ_READ || !c->ReadThreads[0].Thread || !c->CompleteRequest ||
        (!pagingIo && stack->Parameters.Read.Length < QcCopyOffloadMinBytes))
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
    // the worker. A large ordinary read is offloaded only as a RAM hit, so several
    // copies run at once; a miss keeps the worker's ordered path and read fill.
    AcquireCache(c);
    const bool refused = c->OffloadBlocked || c->Stop || c->Gone || !c->Enabled ||
        (c->RangeDrain && start < c->RangeEnd && end > c->RangeStart) ||
        (c->ActiveWrite && start < c->ActiveWriteEnd && end > c->ActiveWriteStart) ||
        FullyResident(c, start, end) == pagingIo || PagingReadsOverlap(c, start, end, true);
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
        auto status = next->Write ? CopyOffloadedWrite(c, next, reader->Pins)
                                  : Read(c, irp, false, false, reader->Pins);
        if (pagingIo)
        {
            InterlockedIncrement64(NT_SUCCESS(status) ? &c->PagingOffloadCompletions : &c->PagingOffloadFailures);
            InterlockedIncrement64(NT_SUCCESS(status) ? &c->PagingRoutedReadCompletions : &c->PagingRoutedReadFailures);
        }
        c->CompleteRequest(c->ServiceContext, irp, status);
        KeAcquireSpinLock(&c->PagingLock, &irql);
        next->Irp = nullptr;
        --c->PagingQueued;
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
// TRIM: the file system declares these sectors free. Cached writes for them are
// dropped instead of written (they would overwrite what the disk now treats as
// unallocated), clean copies are dropped, and only already-issued writes are awaited.
// Any number of sector-aligned ranges is handled; only unknown flags or malformed
// input take the conservative ordered path.
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
    c->TrimPaused = TRUE;
    // The serialized request worker admits no subsequent writes while this request
    // runs. Wait only for the already-issued lower write, NOT all queued payload.
    while (c->State.InFlightBytes && !c->Gone)
    {
        KeClearEvent(&c->Changed);
        ReleaseCache(c);
        KeWaitForSingleObject(&c->Changed, Executive, KernelMode, FALSE, nullptr);
        AcquireCache(c);
    }
    auto status = c->Gone ? STATUS_DEVICE_NOT_CONNECTED : c->State.LastError;
    ReleaseCache(c);
    if (NT_SUCCESS(status))
        status = OriginalIo(c, irp);
    AcquireCache(c);
    if (NT_SUCCESS(status))
    {
        ++c->TrimRequests;
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
            if (!mask)
                continue;
            if (!slot->Dirty)
            {
                RetireSlot(c, i);
                continue;
            }
            const auto trimmed = slot->ValidSectors & mask;
            if (!trimmed)
                continue;
            c->DiscardedBytes += QcValidBytes(trimmed);
            c->State.DirtyBytes -= QcValidBytes(trimmed);
            slot->ValidSectors &= ~mask;
            if (!slot->ValidSectors)
            {
                --c->DirtySlots;
                RetireSlot(c, i);
            }
        }
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
    if (c->State.DeviceBytes != deviceBytes)
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
        auto status = write ? Write(c, irp) : Read(c, irp);
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
            ++c->Diagnostics.DeferredFlushes;
        Publish(c);
        ReleaseCache(c);
        // Unsafe policy changes only application/OS flush semantics. Never hide an existing I/O error.
        if (defer)
            return error;
        return QcCacheBarrier(c, FALSE, QcApplicationBarrier, irp);
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
        return Process(c, irp, deviceBytes);
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
    QcCacheWaitPagingReads(c);
    auto status = Process(c, irp, deviceBytes);
    AcquireCache(c);
    c->OffloadBlocked = FALSE;
    ReleaseCache(c);
    // A forwarded media-changing control: the lower device completes it.
    *transferred = control && status == QcControlForwarded;
    return status;
}
