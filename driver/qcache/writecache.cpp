// SPDX-License-Identifier: MIT
// Serialized block cache: dirty FIFO, clean write/read LRUs and a single versioned index.
#include "writecache.h"
#include <ntddstor.h>
#include <ntdddisk.h>
#include <ntddscsi.h>
#include "observation.h"
#include "sectorcoverage.h"
static constexpr ULONG Chunk = 4096, SlabBytes = 262144, SlotsPerSlab = SlabBytes / Chunk, Tag = 'wCCQ';
static constexpr ULONG NoSlot = MAXULONG;
static constexpr ULONG MaxBatchBytes = 1024 * 1024;
static volatile LONG64 GlobalBudget, NextInstance;
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
static ULONGLONG GlobalLimit;
static ULONGLONG NowMs()
{
    return KeQueryInterruptTime() / 10000;
}
#include "cacheblocks.inl"
static void PagingReader(PVOID context);
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
    KeWaitForSingleObject(&c->Mutex, Executive, KernelMode, FALSE, nullptr);
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
    KeReleaseMutex(&c->Mutex, FALSE);
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
    c->ReadWriteSnapshot.GlobalLimitBytes = GlobalLimit;
    c->ReadWriteSnapshot.GlobalReservedBytes = InterlockedCompareExchange64(&GlobalBudget, 0, 0);
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
    output->GlobalReservedBytes = InterlockedCompareExchange64(&GlobalBudget, 0, 0);
    output->GlobalLimitBytes = GlobalLimit;
    KeReleaseSpinLock(&c->SnapshotLock, irql);
}
void QcCacheDiagnostics(QC_CACHE* c, QC_DIAGNOSTICS* output)
{
    KIRQL irql;
    KeAcquireSpinLock(&c->SnapshotLock, &irql);
    *output = c->DiagnosticsSnapshot;
    output->LowerGeneratedWrites = InterlockedCompareExchange64(&c->LowerGeneratedWrites, 0, 0);
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
void QcCacheRecordPagingIo(QC_CACHE* c, PIRP irp)
{
    if (!(irp->Flags & IRP_PAGING_IO))
        return;
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
        return;
    // These last-request fields are diagnostic breadcrumbs, not an atomic tuple.
    InterlockedExchange64(&c->PagingLastMajor, stack->MajorFunction);
    InterlockedExchange64(&c->PagingLastFlags, irp->Flags);
    InterlockedExchange64(&c->PagingLastOffset, offset);
    InterlockedExchange64(&c->PagingLastLength, length);
    InterlockedExchange64(&c->PagingLastProcessId, reinterpret_cast<LONGLONG>(PsGetCurrentProcessId()));
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
    auto irp = IoBuildSynchronousFsdRequest(major,
                                            c->Lower,
                                            slot ? slot->Buffer : nullptr,
                                            slot ? slot->Length : 0,
                                            slot ? &slot->Offset : nullptr,
                                            &completed,
                                            &iosb);
    if (!irp)
        return STATUS_INSUFFICIENT_RESOURCES;
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
static NTSTATUS OriginalIo(QC_CACHE* c, PIRP irp, bool allowReadService = true)
{
    const bool read = IoGetCurrentIrpStackLocation(irp)->MajorFunction == IRP_MJ_READ;
    KEVENT completed;
    KeInitializeEvent(&completed, NotificationEvent, FALSE);
    IoCopyCurrentIrpStackLocationToNext(irp);
    IoSetCompletionRoutine(irp, RetainCompletion, &completed, TRUE, TRUE, TRUE);
    QcCacheRecordLowerAttempt(c, IoGetCurrentIrpStackLocation(irp)->MajorFunction, irp);
    auto status = IoCallDriver(c->Lower, irp);
    if (status == STATUS_PENDING)
    {
        while (!KeReadStateEvent(&completed))
        {
            // This IRP remains owned by the lower stack until RetainCompletion.
            // Cache.Read has pinned its hit versions before reaching this wait.
            // The service lane can complete cached hits or one independent
            // paging-marked miss. A nested lower wait cannot recurse again.
            if (allowReadService && c->ServiceReads && c->RequestAvailable &&
                (read || c->RangeDrain))
            {
                // Null means the active request is a read. Do not inspect an IRP
                // whose current stack location belongs to the pending lower I/O.
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
    return irp->IoStatus.Status;
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
static bool PagingReadsOverlap(QC_CACHE* c, LONGLONG first, LONGLONG end)
{
    bool overlap = false;
    KIRQL irql;
    KeAcquireSpinLock(&c->PagingLock, &irql);
    for (const auto& read : c->PagingReads)
        if (read.Irp && read.Start < end && read.End > first)
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
                               volatile LONG64* counter)
{
    bool counted = false;
    for (;;)
    {
        KeClearEvent(&c->PagingDone);
        if (!PagingReadsOverlap(c, first, end))
            return;
        if (!counted)
        {
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
        if (worker->Number >= c->Options.Parallelism || c->State.DirtyBytes == 0 || c->TrimPaused ||
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
        auto inject = c->InjectFault == 1 || c->InjectFault == 2 || c->InjectFault == 4 || c->InjectFault == 5
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
NTSTATUS QcCacheInitialize(QC_CACHE* c, PDEVICE_OBJECT lower)
{
    RtlZeroMemory(c, sizeof(*c));
    c->Lower = lower;
    c->Head = c->Tail = c->FreeHead = NoSlot;
    c->CleanHead[0] = c->CleanHead[1] = c->CleanTail[0] = c->CleanTail[1] = NoSlot;
    LARGE_INTEGER frequency;
    KeQueryPerformanceCounter(&frequency);
    c->Performance.Frequency = frequency.QuadPart;
    c->Options = QcDefaultOptions();
    c->Instance = InterlockedIncrement64(&NextInstance);
    GlobalLimit = MemoryLimit();
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
    KeInitializeMutex(&c->Mutex, 0);
    KeInitializeSpinLock(&c->SnapshotLock);
    KeInitializeEvent(&c->Wake, NotificationEvent, FALSE);
    KeInitializeEvent(&c->Changed, NotificationEvent, FALSE);
    KeInitializeSpinLock(&c->PagingLock);
    KeInitializeEvent(&c->PagingWork, SynchronizationEvent, FALSE);
    KeInitializeEvent(&c->PagingDone, NotificationEvent, FALSE);
    Publish(c);
    OBJECT_ATTRIBUTES attrs;
    InitializeObjectAttributes(&attrs, nullptr, OBJ_KERNEL_HANDLE, nullptr, nullptr);
    auto pagingStatus =
        PsCreateSystemThread(&c->PagingThread, THREAD_ALL_ACCESS, &attrs, nullptr, nullptr, PagingReader, c);
    if (!NT_SUCCESS(pagingStatus))
    {
        c->PagingThread = nullptr;
        QcCacheDestroy(c);
        return pagingStatus;
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
    if (c->State.BudgetBytes)
        InterlockedAdd64(&GlobalBudget, -static_cast<LONG64>(c->State.BudgetBytes));
    if (c->Slots)
    {
        for (ULONG i = 0; i < c->Capacity; i += SlotsPerSlab)
            if (c->Slots[i].Buffer)
                ExFreePoolWithTag(c->Slots[i].Buffer, Tag);
        ExFreePoolWithTag(c->Slots, Tag);
    }
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
void QcCacheDestroy(QC_CACHE* c)
{
    AcquireCache(c);
    c->Stop = TRUE;
    WakeDrainers(c);
    ReleaseCache(c);
    if (c->PagingThread)
    {
        // Remove-lock drain already completed every offloaded IRP; the thread
        // still finishes any queued entry before it observes PagingStop.
        KIRQL irql;
        KeAcquireSpinLock(&c->PagingLock, &irql);
        c->PagingStop = TRUE;
        KeReleaseSpinLock(&c->PagingLock, irql);
        KeSetEvent(&c->PagingWork, IO_NO_INCREMENT, FALSE);
        ZwWaitForSingleObject(c->PagingThread, FALSE, nullptr);
        ZwClose(c->PagingThread);
        c->PagingThread = nullptr;
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
}
static NTSTATUS Configure(QC_CACHE* c, ULONGLONG budget)
{
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
    for (;;)
    {
        auto total = InterlockedCompareExchange64(&GlobalBudget, 0, 0);
        if (static_cast<ULONGLONG>(total) > GlobalLimit || budget > GlobalLimit - static_cast<ULONGLONG>(total))
        {
            Publish(c);
            ReleaseCache(c);
            return STATUS_INSUFFICIENT_RESOURCES;
        }
        if (InterlockedCompareExchange64(&GlobalBudget, total + budget, total) == total)
            break;
    }
    c->State.BudgetBytes = budget;
    // Include both page-rounded descriptor and hash-index allocations in the hard budget.
    c->DrainCapacity = budget < (16ULL << 20) ? SlabBytes / 4 : MaxBatchBytes;
    auto stagingBytes = c->DrainCapacity * RTL_NUMBER_OF(c->Workers);
    auto n = static_cast<ULONG>((budget - 2 * PAGE_SIZE - stagingBytes) / (Chunk + sizeof(QC_SLOT) + sizeof(ULONG)));
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
                             : i % SlotsPerSlab == 0
                                 ? static_cast<PUCHAR>(ExAllocatePool2(POOL_FLAG_NON_PAGED, SlabBytes, Tag))
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
    c->State.ReservedBytes = stagingBytes + descriptors + indexBytes + static_cast<ULONGLONG>(n) * Chunk;
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
        else if (c->BlockedPlacement)
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
        if (command.Value > 7)
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
static NTSTATUS Write(QC_CACHE* c, PIRP irp)
{
    auto stack = IoGetCurrentIrpStackLocation(irp);
    auto length = stack->Parameters.Write.Length;
    auto offset = stack->Parameters.Write.ByteOffset;
    const bool pagingIo = (irp->Flags & IRP_PAGING_IO) != 0;
    AcquireCache(c);
    if (c->Gone || c->Suspended || irp->Cancel)
    {
        auto error = c->Gone ? STATUS_DEVICE_NOT_CONNECTED : c->Suspended ? STATUS_DEVICE_NOT_READY : STATUS_CANCELLED;
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
    if (!QcShouldCacheDataIo(pagingIo))
    {
        // A paging-marked write can also be ordinary mapped-file data. Drain
        // only its overlapping versions, including writes already issued by a
        // drainer. Keep the range fenced until the direct lower write completes.
        // The foreground worker owns the only range fence; unrelated blocks may
        // continue draining and fitting ordinary writes retain RAM admission.
        const auto first = offset.QuadPart;
        const auto end = first + length;
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
        ReleaseCache(c);
        const bool gateSubmit = gateRecord && RecordLabSequence(c, &c->LabGateDirectSubmitSeq);
        auto status = OriginalIo(c, irp);
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
    auto source = Map(irp);
    if (!source)
    {
        if (pagingIo)
            InterlockedIncrement64(&c->PagingMapFailures);
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
    for (auto block = firstBlock; block < end; block += Chunk)
    {
        auto slot = &c->Slots[admittedSlot != NoSlot ? admittedSlot : FindSlot(c, block)];
        const auto from = max(block, offset.QuadPart);
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
    irp->IoStatus.Information = length;
    return STATUS_SUCCESS;
}
// Foreground admission remains single-owner. Pins protect exact cached versions
// from drainer retirement while lower reads and payload copies run without Mutex.
// pinned: the paging thread's scratch (QcPagingPinBlocks entries, range already
// bounded). An offloaded read runs concurrently with the request worker, which
// may add a clean read-fill or a newer version for these blocks. It therefore
// overlays and unpins exactly the versions it pinned; FindSlot could name a slot
// this read never pinned. Writes overlapping it wait (WaitForPagingReads).
static NTSTATUS Read(QC_CACHE* c, PIRP irp, bool hitOnly = false, bool allowReadService = true,
                     ULONG* pinned = nullptr)
{
    auto stack = IoGetCurrentIrpStackLocation(irp);
    auto length = stack->Parameters.Read.Length;
    auto start = stack->Parameters.Read.ByteOffset.QuadPart;
    auto end = start + length;
    AcquireCache(c);
    if (c->Gone || c->Suspended || irp->Cancel)
    {
        auto error = c->Gone ? STATUS_DEVICE_NOT_CONNECTED : c->Suspended ? STATUS_DEVICE_NOT_READY : STATUS_CANCELLED;
        ReleaseCache(c);
        return error;
    }
    if (length == 0)
    {
        ReleaseCache(c);
        irp->IoStatus.Information = 0;
        return STATUS_SUCCESS;
    }
    const bool pagingIo = (irp->Flags & IRP_PAGING_IO) != 0;
    if (pagingIo && !ResidentRange(c, start, end))
    {
        // No cached version exists to overlay. Avoid RAM retention and mapping.
        // The request worker may service independent reads during this wait;
        // the paging thread and nested service never do (allowReadService).
        ReleaseCache(c);
        return hitOnly ? STATUS_NOT_FOUND : OriginalIo(c, irp, allowReadService);
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
        if (irp->Flags & IRP_PAGING_IO)
            InterlockedIncrement64(&c->PagingMapFailures);
        ReleaseCache(c);
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
    if (!full)
    {
        c->Performance.Phase = QcLowerReadPhase;
        Publish(c);
        ReleaseCache(c);
        status = OriginalIo(c, irp, allowReadService);
        AcquireCache(c);
        c->Performance.Phase = QcRequestPhase;
    }
    else
        irp->IoStatus.Information = length;
    if (NT_SUCCESS(status) && irp->IoStatus.Information != length)
        status = STATUS_DEVICE_DATA_ERROR;
    if (NT_SUCCESS(status))
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
            if (!pagingIo && block >= start && block + Chunk <= end && ReadRoom(c))
            {
                index = AllocateSlot(c, false, true);
                auto fill = &c->Slots[index];
                fill->Offset.QuadPart = block;
                fill->Length = Chunk;
                fill->ValidSectors = 255;
                c->CleanValidBytes[1] += Chunk; // AllocateSlot linked it with an empty mask.
                RtlCopyMemory(fill->Buffer, target + (block - start), Chunk);
                IndexSlot(c, index);
            }
        }
        if (hitOnly)
            ++c->Performance.BypassReads;
    }
    Publish(c);
    ReleaseCache(c);
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
    const bool overlapsActiveWrite = c->RangeDrain &&
        offset < c->RangeEnd && offset + length > c->RangeStart;
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
static bool FullyResident(QC_CACHE* c, LONGLONG start, LONGLONG end)
{
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
    if (stack->MajorFunction != IRP_MJ_READ || !(irp->Flags & IRP_PAGING_IO) || !c->PagingThread ||
        !c->CompleteRequest)
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
    AcquireCache(c);
    const bool refused = c->OffloadBlocked || c->Stop || c->Gone ||
        (c->RangeDrain && start < c->RangeEnd && end > c->RangeStart) ||
        (c->ActiveWrite && start < c->ActiveWriteEnd && end > c->ActiveWriteStart) ||
        FullyResident(c, start, end); // A RAM hit completes faster on the worker.
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
        RecordMaximum(&c->PagingOffloadMaxQueued, ++c->PagingQueued);
    }
    KeReleaseSpinLock(&c->PagingLock, irql);
    if (!entry)
        return false; // Table full: the caller keeps its existing ordered path.
    // The paging thread owns irp from here; do not touch it again.
    InterlockedIncrement64(&c->PagingOffloadedReads);
    InterlockedIncrement64(&c->PagingRoutedReadRequests);
    KeSetEvent(&c->PagingWork, IO_NO_INCREMENT, FALSE);
    return true;
}
// One per disk. Executes offloaded paging reads oldest-first. It depends only on
// Mutex (never held across waits) and lower completion. It never runs the read
// service, never waits for the request worker and never admits cached writes.
static void PagingReader(PVOID context)
{
    auto c = static_cast<QC_CACHE*>(context);
    for (;;)
    {
        QC_PAGING_READ* next = nullptr;
        KIRQL irql;
        KeAcquireSpinLock(&c->PagingLock, &irql);
        for (auto& read : c->PagingReads)
            if (read.Irp && !read.Started && (!next || read.Sequence < next->Sequence))
                next = &read;
        if (next)
            next->Started = TRUE;
        const bool stop = c->PagingStop && !next;
        KeReleaseSpinLock(&c->PagingLock, irql);
        if (stop)
            break;
        if (!next)
        {
            KeWaitForSingleObject(&c->PagingWork, Executive, KernelMode, FALSE, nullptr);
            continue;
        }
        auto irp = next->Irp;
        auto status = Read(c, irp, false, false, c->PagingPins);
        InterlockedIncrement64(NT_SUCCESS(status) ? &c->PagingOffloadCompletions : &c->PagingOffloadFailures);
        InterlockedIncrement64(NT_SUCCESS(status) ? &c->PagingRoutedReadCompletions : &c->PagingRoutedReadFailures);
        c->CompleteRequest(c->ServiceContext, irp, status);
        KeAcquireSpinLock(&c->PagingLock, &irql);
        next->Irp = nullptr;
        --c->PagingQueued;
        KeReleaseSpinLock(&c->PagingLock, irql);
        KeSetEvent(&c->PagingDone, IO_NO_INCREMENT, FALSE);
    }
    PsTerminateSystemThread(STATUS_SUCCESS);
}
// Only optimized, bounded, full-cache-block TRIM ranges are handled here. Any
// unfamiliar flags/parameters/alignment use the existing ordered drain/pass-through.
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
    auto count = input->DataSetRangesLength / sizeof(DEVICE_DATA_SET_RANGE);
    DEVICE_DATA_SET_RANGE ranges[128];
    if (count > RTL_NUMBER_OF(ranges))
        return false;
    RtlCopyMemory(ranges,
                  static_cast<PUCHAR>(irp->AssociatedIrp.SystemBuffer) + input->DataSetRangesOffset,
                  input->DataSetRangesLength);
    for (ULONG i = 0; i < count; ++i)
    {
        auto range = ranges[i];
        if (range.StartingOffset < 0 || range.StartingOffset > deviceBytes || !range.LengthInBytes ||
            range.LengthInBytes > static_cast<ULONGLONG>(deviceBytes - range.StartingOffset) ||
            range.StartingOffset % Chunk || range.LengthInBytes % Chunk)
            return false;
        ULONG j = i;
        while (j && ranges[j - 1].StartingOffset > range.StartingOffset)
        {
            ranges[j] = ranges[j - 1];
            --j;
        }
        ranges[j] = range;
    }
    // Merge overlap/adjacency so the binary-search membership check is unambiguous.
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
            if (!c->Slots[i].Length)
                continue;
            auto offset = c->Slots[i].Offset.QuadPart;
            ULONG low = 0, high = merged;
            while (low < high)
            {
                auto mid = low + (high - low) / 2;
                if (ranges[mid].StartingOffset <= offset)
                    low = mid + 1;
                else
                    high = mid;
            }
            if (low && offset - ranges[low - 1].StartingOffset < static_cast<LONGLONG>(ranges[low - 1].LengthInBytes))
            {
                if (c->Slots[i].Dirty)
                {
                    c->DiscardedBytes += QcValidBytes(c->Slots[i].ValidSectors);
                    c->State.DirtyBytes -= QcValidBytes(c->Slots[i].ValidSectors);
                    --c->DirtySlots;
                }
                RetireSlot(c, i);
            }
        }
    }
    c->TrimPaused = FALSE;
    Publish(c);
    KeSetEvent(&c->Changed, IO_NO_INCREMENT, FALSE);
    WakeDrainers(c);
    ReleaseCache(c);
    *result = status;
    return true;
}
static NTSTATUS Process(QC_CACHE* c, PIRP irp, LONGLONG deviceBytes)
{
    auto stack = IoGetCurrentIrpStackLocation(irp);
    irp->IoStatus.Information = 0;
    AcquireCache(c);
    c->State.DeviceBytes = deviceBytes;
    Publish(c);
    bool suspended = c->Suspended != FALSE;
    ReleaseCache(c);
    if (c->Gone)
    {
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
    if (stack->MajorFunction == IRP_MJ_DEVICE_CONTROL || stack->MajorFunction == IRP_MJ_INTERNAL_DEVICE_CONTROL)
    {
        auto code = stack->Parameters.DeviceIoControl.IoControlCode;
        // Neither-I/O and raw controller pass-through may contain uncaptured user pointers.
        // They cannot safely be forwarded from a system worker in another process context.
        if ((code & 3) == METHOD_NEITHER || DEVICE_TYPE_FROM_CTL_CODE(code) == FILE_DEVICE_CONTROLLER)
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
    if (suspended)
        return STATUS_DEVICE_NOT_READY;
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
    return OriginalIo(c, irp);
}
// Request-worker entry point. Offloaded paging reads run concurrently with the
// worker, so every request class states what it must not overlap:
// - paging read needing lower I/O: handed to the paging thread (never waits here);
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
        if ((irp->Flags & IRP_PAGING_IO) && QcCacheOffloadPagingRead(c, irp, deviceBytes))
        {
            *transferred = true;
            return STATUS_PENDING;
        }
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
    return status;
}
