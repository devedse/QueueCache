// SPDX-License-Identifier: MIT
// Current QueueCache storage filter. It remains inactive pass-through until an
// explicit cache configuration enables routing for a supported disk.
#include <ntifs.h>
#include <ntdddisk.h>
#include "qcstats.h"
#include "observation.h"
#include "readselection.h"
#if QCACHE_CACHE_DRIVER
#include "writecache.h"
#endif

struct QC_EXTENSION
{
    PDEVICE_OBJECT Self;
    PDEVICE_OBJECT Pdo;
    PDEVICE_OBJECT Lower;
    IO_REMOVE_LOCK RemoveLock;
    volatile LONG64 ReadBytes;
    volatile LONG64 WrittenBytes;
    LARGE_INTEGER Size;
#if QCACHE_CACHE_DRIVER
    QC_CACHE Cache;
#endif
#if QCACHE_SERIALIZED_IO
    IO_CSQ Csq;
    KSPIN_LOCK QueueLock;
    LIST_ENTRY Pending;
    KEVENT WorkAvailable;
    HANDLE Worker;
    BOOLEAN Closing; // Protected by QueueLock, including the CSQ insertion gate.
    BOOLEAN Routing; // QueueLock: transition gate between direct I/O and cached I/O.
    LONG DirectCount;
    LONG PendingControls;
    KEVENT DirectIdle;
    ULONGLONG NextSequence;
    ULONGLONG ReadServiceEpoch;                // Normal worker admissions, not drain wakeups.
    QcReadSelection<LIST_ENTRY> ReadSelection; // QueueLock; see readselection.h.
    bool ReadSelectionAllowsFlush;             // Reset cursors when the eligibility changes.
    ULONGLONG QueueDepth, QueueWaitTicks, MaxQueueWaitTicks, ActiveMajor, ActiveSince;
#endif
};
static WCHAR ExpectedDriverKey[512];
static UNICODE_STRING AllowedDriverKey;
static BOOLEAN ClassCoverage;
extern "C" DRIVER_INITIALIZE DriverEntry;
DRIVER_ADD_DEVICE QcAddDevice;
DRIVER_DISPATCH QcDispatch;
DRIVER_UNLOAD QcUnload;
IO_COMPLETION_ROUTINE QcCompletion;
IO_COMPLETION_ROUTINE QcStartCompletion;

static NTSTATUS Complete(PIRP irp, NTSTATUS status, ULONG_PTR bytes = 0)
{
    irp->IoStatus.Status = status;
    irp->IoStatus.Information = bytes;
    IoCompleteRequest(irp, IO_NO_INCREMENT);
    return status;
}

NTSTATUS QcCompletion(PDEVICE_OBJECT, PIRP irp, PVOID context)
{
    auto ext = static_cast<QC_EXTENSION*>(context);
    if (irp->PendingReturned)
        IoMarkIrpPending(irp);
    IoReleaseRemoveLock(&ext->RemoveLock, irp);
    return STATUS_CONTINUE_COMPLETION;
}

NTSTATUS QcStartCompletion(PDEVICE_OBJECT, PIRP, PVOID context)
{
    KeSetEvent(static_cast<PKEVENT>(context), IO_NO_INCREMENT, FALSE);
    return STATUS_MORE_PROCESSING_REQUIRED;
}

#if QCACHE_CACHE_DRIVER
static void UsageStateChanged(QC_EXTENSION* ext)
{
    if (QcCachePagingPathCount(&ext->Cache) > 0)
        ext->Self->Flags &= ~DO_POWER_PAGABLE;
    else if (!(ext->Self->Flags & DO_POWER_INRUSH))
        ext->Self->Flags |= DO_POWER_PAGABLE;
    IoInvalidateDeviceState(ext->Pdo);
}

static NTSTATUS UsageInCompletion(PDEVICE_OBJECT, PIRP irp, PVOID context)
{
    auto ext = static_cast<QC_EXTENSION*>(context);
    if (irp->PendingReturned)
        IoMarkIrpPending(irp);
    // In-path is recorded before forwarding so cache activation cannot race a
    // pending usage notification. Roll it back only if the lower stack rejects it.
    auto type = IoGetCurrentIrpStackLocation(irp)->Parameters.UsageNotification.Type;
    QcCacheRecordUsageCompletion(&ext->Cache, type, TRUE, irp->IoStatus.Status);
    if (!NT_SUCCESS(irp->IoStatus.Status))
        QcCacheRecordUsage(&ext->Cache, type, FALSE);
    else
        UsageStateChanged(ext);
    IoReleaseRemoveLock(&ext->RemoveLock, irp);
    return STATUS_CONTINUE_COMPLETION;
}

static NTSTATUS UsageOutCompletion(PDEVICE_OBJECT, PIRP irp, PVOID context)
{
    auto ext = static_cast<QC_EXTENSION*>(context);
    if (irp->PendingReturned)
        IoMarkIrpPending(irp);
    auto type = IoGetCurrentIrpStackLocation(irp)->Parameters.UsageNotification.Type;
    QcCacheRecordUsageCompletion(&ext->Cache, type, FALSE, irp->IoStatus.Status);
    if (NT_SUCCESS(irp->IoStatus.Status))
    {
        QcCacheRecordUsage(&ext->Cache, type, FALSE);
        UsageStateChanged(ext);
    }
    IoReleaseRemoveLock(&ext->RemoveLock, irp);
    return STATUS_CONTINUE_COMPLETION;
}

static NTSTATUS ForwardUsage(QC_EXTENSION* ext, PIRP irp, BOOLEAN inPath)
{
    IoCopyCurrentIrpStackLocationToNext(irp);
    IoSetCompletionRoutine(irp, inPath ? UsageInCompletion : UsageOutCompletion, ext, TRUE, TRUE, TRUE);
    return IoCallDriver(ext->Lower, irp);
}

static NTSTATUS QueryPnpStateCompletion(PDEVICE_OBJECT, PIRP irp, PVOID context)
{
    auto ext = static_cast<QC_EXTENSION*>(context);
    if (irp->PendingReturned)
        IoMarkIrpPending(irp);
    if (NT_SUCCESS(irp->IoStatus.Status) && QcCachePagingPathCount(&ext->Cache) > 0)
        irp->IoStatus.Information |= PNP_DEVICE_NOT_DISABLEABLE;
    IoReleaseRemoveLock(&ext->RemoveLock, irp);
    return STATUS_CONTINUE_COMPLETION;
}

static NTSTATUS ForwardQueryPnpState(QC_EXTENSION* ext, PIRP irp)
{
    IoCopyCurrentIrpStackLocationToNext(irp);
    IoSetCompletionRoutine(irp, QueryPnpStateCompletion, ext, TRUE, TRUE, TRUE);
    return IoCallDriver(ext->Lower, irp);
}
#endif

static NTSTATUS Forward(QC_EXTENSION* ext, PIRP irp)
{
    IoCopyCurrentIrpStackLocationToNext(irp);
    IoSetCompletionRoutine(irp, QcCompletion, ext, TRUE, TRUE, TRUE);
#if QCACHE_CACHE_DRIVER
    QcCacheRecordLowerAttempt(&ext->Cache, IoGetCurrentIrpStackLocation(irp)->MajorFunction);
#endif
    return IoCallDriver(ext->Lower, irp);
}

#if QCACHE_SERIALIZED_IO
static NTSTATUS DirectCompletion(PDEVICE_OBJECT, PIRP irp, PVOID context)
{
    auto ext = static_cast<QC_EXTENSION*>(context);
    if (irp->PendingReturned)
        IoMarkIrpPending(irp);
    KIRQL irql;
    KeAcquireSpinLock(&ext->QueueLock, &irql);
    if (--ext->DirectCount == 0)
        KeSetEvent(&ext->DirectIdle, IO_NO_INCREMENT, FALSE);
    KeReleaseSpinLock(&ext->QueueLock, irql);
    IoReleaseRemoveLock(&ext->RemoveLock, irp);
    return STATUS_CONTINUE_COMPLETION;
}
#endif

#if QCACHE_SERIALIZED_IO
// Worker foundation: original requests only, NO early acknowledgements or cached data.
// CSQ owns cancellation while queued; the lower driver owns it after dequeue/forward.
static constexpr ULONG_PTR UsageReservationMarker = 2;

#if QCACHE_CACHE_DRIVER
static bool HasUsageReservation(PIRP irp)
{
    auto stack = IoGetCurrentIrpStackLocation(irp);
    return QcTrackedUsageNotification(stack) && stack->Parameters.UsageNotification.InPath &&
           irp->Tail.Overlay.DriverContext[2] == reinterpret_cast<PVOID>(UsageReservationMarker);
}
#endif

static QC_EXTENSION* QueueOwner(PIO_CSQ csq)
{
    return CONTAINING_RECORD(csq, QC_EXTENSION, Csq);
}
static NTSTATUS QueueInsert(PIO_CSQ csq, PIRP irp, PVOID reinsert)
{
    auto ext = QueueOwner(csq);
    if (ext->Closing)
        return STATUS_DELETE_PENDING;
    ext->Routing = TRUE;
    if (reinsert)
    {
        InsertHeadList(&ext->Pending, &irp->Tail.Overlay.ListEntry);
        ext->ReadSelection.Reset(ext->Pending.Flink);
    }
    else
    {
        auto stack = IoGetCurrentIrpStackLocation(irp);
#if QCACHE_CACHE_DRIVER
        if (QcTrackedUsageNotification(stack) && stack->Parameters.UsageNotification.InPath)
        {
            // QcDispatch reserved this before routing selection. The marker lets
            // cancellation undo a notification the worker never sees.
            irp->Tail.Overlay.DriverContext[2] = reinterpret_cast<PVOID>(UsageReservationMarker);
        }
        else
#endif
            irp->Tail.Overlay.DriverContext[2] = nullptr; // Last unsuccessful read-bypass epoch.
        irp->Tail.Overlay.DriverContext[0] = reinterpret_cast<PVOID>(++ext->NextSequence);
        irp->Tail.Overlay.DriverContext[1] = reinterpret_cast<PVOID>(KeQueryPerformanceCounter(nullptr).QuadPart);
        InsertTailList(&ext->Pending, &irp->Tail.Overlay.ListEntry);
        ext->ReadSelection.Appended(&ext->Pending, &irp->Tail.Overlay.ListEntry);
        KeSetEvent(&ext->WorkAvailable, IO_NO_INCREMENT, FALSE);
    }
    ++ext->QueueDepth;
    return STATUS_SUCCESS;
}
static void QueueRemove(PIO_CSQ csq, PIRP irp)
{
    auto ext = QueueOwner(csq);
    --ext->QueueDepth;
    auto now = static_cast<ULONGLONG>(KeQueryPerformanceCounter(nullptr).QuadPart);
    auto elapsed = now - reinterpret_cast<ULONGLONG>(irp->Tail.Overlay.DriverContext[1]);
    ext->QueueWaitTicks += elapsed;
    ext->MaxQueueWaitTicks = max(ext->MaxQueueWaitTicks, elapsed);
    irp->Tail.Overlay.DriverContext[1] = reinterpret_cast<PVOID>(now);
    ext->ReadSelection.Removing(&irp->Tail.Overlay.ListEntry, irp->Tail.Overlay.ListEntry.Flink);
    RemoveEntryList(&irp->Tail.Overlay.ListEntry);
    // Cancellation can remove a fence/dependency without a new insertion.
    KeSetEvent(&ext->WorkAvailable, IO_NO_INCREMENT, FALSE);
}
struct READ_SELECTION
{
    PIRP BlockedRequest;
    ULONGLONG AfterSequence;
    bool More;
    bool AllowApplicationFlush;
};
#if QCACHE_CACHE_DRIVER
static void Increment(ULONGLONG* value)
{
    InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(value));
}
#endif
static void RecordSelection(QC_PERFORMANCE* performance, PIRP request, ULONG scanned)
{
    auto stack = IoGetCurrentIrpStackLocation(request);
    performance->LastSelectionMajor = stack->MajorFunction;
    performance->LastSelectionCode =
        stack->MajorFunction == IRP_MJ_DEVICE_CONTROL || stack->MajorFunction == IRP_MJ_INTERNAL_DEVICE_CONTROL
            ? stack->Parameters.DeviceIoControl.IoControlCode
            : 0;
    performance->LastSelectionSequence = reinterpret_cast<ULONGLONG>(request->Tail.Overlay.DriverContext[0]);
    performance->LastSelectionScanned = scanned;
    if (stack->MajorFunction == IRP_MJ_READ || stack->MajorFunction == IRP_MJ_WRITE)
    {
        performance->LastSelectionOffset = stack->Parameters.Read.ByteOffset.QuadPart;
        performance->LastSelectionLength = stack->Parameters.Read.Length;
    }
    else
        performance->LastSelectionOffset = performance->LastSelectionLength = 0;
}
static bool Overlaps(PIRP read, PIRP write)
{
    auto r = IoGetCurrentIrpStackLocation(read);
    auto w = IoGetCurrentIrpStackLocation(write);
    auto a = r->Parameters.Read.ByteOffset.QuadPart;
    auto b = w->Parameters.Write.ByteOffset.QuadPart;
    if (a < 0 || b < 0)
        return true;
    return a <= b ? static_cast<ULONGLONG>(b - a) < r->Parameters.Read.Length
                  : static_cast<ULONGLONG>(a - b) < w->Parameters.Write.Length;
}
// Adapter keeps WDM classification/diagnostics separate from cursor ownership.
struct ReadQueueView
{
    QC_EXTENSION* Ext;
    READ_SELECTION* Selection;
    ULONG Scanned;
    static PIRP Request(PLIST_ENTRY node)
    {
        return CONTAINING_RECORD(node, IRP, Tail.Overlay.ListEntry);
    }
    PLIST_ENTRY Next(PLIST_ENTRY node)
    {
        return node->Flink;
    }
    void Count(ULONGLONG* value)
    {
#if QCACHE_CACHE_DRIVER
        if (Ext->Cache.Timing)
            Increment(value);
#else
        UNREFERENCED_PARAMETER(value);
#endif
    }
    bool Fence(PLIST_ENTRY node)
    {
        auto request = Request(node);
        auto stack = IoGetCurrentIrpStackLocation(request);
        ++Scanned;
#if QCACHE_CACHE_DRIVER
        if (Ext->Cache.Timing)
            RecordSelection(&Ext->Cache.Performance, request, Scanned);
#endif
        if (stack->MajorFunction == IRP_MJ_READ || stack->MajorFunction == IRP_MJ_WRITE || QcObservationRequest(stack))
            return false;
        // Do not dequeue/acknowledge the flush. A Fast application flush does not
        // alter cached bytes; independent reads may cross it, but validation must
        // still visit every older write on BOTH sides of it. Other fences remain.
        if (stack->MajorFunction == IRP_MJ_FLUSH_BUFFERS && Selection->AllowApplicationFlush)
            return false;
#if QCACHE_CACHE_DRIVER
        Count(&Ext->Cache.Performance.SelectionRejectFence);
#endif
        return true;
    }
    bool Eligible(PLIST_ENTRY node)
    {
        auto request = Request(node);
        auto stack = IoGetCurrentIrpStackLocation(request);
        if (stack->MajorFunction != IRP_MJ_READ)
        {
#if QCACHE_CACHE_DRIVER
            Count(&Ext->Cache.Performance.SelectionRejectNotRead);
#endif
            return false;
        }
        if (reinterpret_cast<ULONGLONG>(request->Tail.Overlay.DriverContext[2]) == Ext->ReadServiceEpoch)
            return false;
        if (stack->Parameters.Read.Length > 1024 * 1024)
        {
#if QCACHE_CACHE_DRIVER
            Count(&Ext->Cache.Performance.SelectionRejectMaxSize);
#endif
            return false;
        }
        if (Selection->BlockedRequest && Overlaps(request, Selection->BlockedRequest))
        {
#if QCACHE_CACHE_DRIVER
            Count(&Ext->Cache.Performance.SelectionRejectActiveOverlap);
#endif
            return false;
        }
        return true;
    }
    bool Conflict(PLIST_ENTRY candidate, PLIST_ENTRY older)
    {
        auto request = Request(older);
        if (IoGetCurrentIrpStackLocation(request)->MajorFunction != IRP_MJ_WRITE ||
            !Overlaps(Request(candidate), request))
            return false;
#if QCACHE_CACHE_DRIVER
        Count(&Ext->Cache.Performance.SelectionRejectOlderWriteOverlap);
#endif
        return true;
    }
};
static PIRP QueuePeek(PIO_CSQ csq, PIRP irp, PVOID context)
{
    auto ext = QueueOwner(csq);
    auto head = &ext->Pending;
    auto next = irp ? irp->Tail.Overlay.ListEntry.Flink : head->Flink;
    if (!context)
        return next == head ? nullptr : CONTAINING_RECORD(next, IRP, Tail.Overlay.ListEntry);
    auto selection = static_cast<READ_SELECTION*>(context);
    ReadQueueView view{ext, selection, 0};
    auto found = ext->ReadSelection.Step(head, view, 64, selection->More);
#if QCACHE_CACHE_DRIVER
    if (selection->More && ext->Cache.Timing)
        Increment(&ext->Cache.Performance.SelectionScanLimit);
#endif
    return found ? ReadQueueView::Request(found) : nullptr;
}
static void QueueAcquire(PIO_CSQ csq, PKIRQL irql)
{
    KeAcquireSpinLock(&QueueOwner(csq)->QueueLock, irql);
}
static void QueueRelease(PIO_CSQ csq, KIRQL irql)
{
    KeReleaseSpinLock(&QueueOwner(csq)->QueueLock, irql);
}
static void QueueCancel(PIO_CSQ csq, PIRP irp)
{
#if QCACHE_CACHE_DRIVER
    auto ext = QueueOwner(csq);
    auto stack = IoGetCurrentIrpStackLocation(irp);
    if (QcTrackedUsageNotification(stack))
        QcCacheRecordUsageCompletion(&ext->Cache, stack->Parameters.UsageNotification.Type,
            stack->Parameters.UsageNotification.InPath, STATUS_CANCELLED);
    if (HasUsageReservation(irp))
    {
        QcCacheRecordUsage(&ext->Cache,
            IoGetCurrentIrpStackLocation(irp)->Parameters.UsageNotification.Type, FALSE);
        irp->Tail.Overlay.DriverContext[2] = nullptr;
    }
#endif
    IoReleaseRemoveLock(&QueueOwner(csq)->RemoveLock, irp);
    Complete(irp, STATUS_CANCELLED);
}
#if QCACHE_CACHE_DRIVER
static bool ServiceCachedReads(PVOID context, PIRP blockedRequest)
{
    auto ext = static_cast<QC_EXTENSION*>(context);
    QC_STATE state;
    QcCacheSnapshot(&ext->Cache, &state);
    const bool allowFlush = QcReadMayPassApplicationFlush(state.Flags);
    KIRQL irql;
    KeAcquireSpinLock(&ext->QueueLock, &irql);
    if (ext->ReadSelectionAllowsFlush != allowFlush)
    {
        ext->ReadSelection.Reset(ext->Pending.Flink);
        ext->ReadSelectionAllowsFlush = allowFlush;
    }
    const bool diagnostics = ext->Cache.Timing != 0;
    if (diagnostics)
    {
        Increment(&ext->Cache.Performance.ServiceReadCalls);
        ext->Cache.Performance.LastAfterSequence = 0;
        if (blockedRequest)
        {
            auto stack = IoGetCurrentIrpStackLocation(blockedRequest);
            ext->Cache.Performance.LastBlockedMajor = stack->MajorFunction;
            ext->Cache.Performance.LastBlockedOffset = stack->Parameters.Write.ByteOffset.QuadPart;
            ext->Cache.Performance.LastBlockedLength = stack->Parameters.Write.Length;
        }
        else
            ext->Cache.Performance.LastBlockedMajor = ext->Cache.Performance.LastBlockedOffset =
                ext->Cache.Performance.LastBlockedLength = 0;
    }
    KeClearEvent(&ext->WorkAvailable);
    const bool closing = ext->Closing != FALSE;
    KeReleaseSpinLock(&ext->QueueLock, irql);
    if (closing)
        return false;
    // Policy mutations are executed by this same foreground worker, never during
    // this callback. Queued policy changes remain fences. SnapshotLock is released
    // before QueueLock; no cache mutex acquisition inside a CSQ callback.
    READ_SELECTION selection{blockedRequest, 0, false, allowFlush};
    bool completed = false;
    ULONG used = 0;
    for (ULONG n = 0; n < 8; ++n)
    {
        auto read = IoCsqRemoveNextIrp(&ext->Csq, &selection);
        if (!read)
        {
            if (diagnostics)
                Increment(&ext->Cache.Performance.ServiceReadNoCandidate);
            break;
        }
        ++used;
        if (diagnostics)
            Increment(&ext->Cache.Performance.ServiceReadAttempts);
        selection.AfterSequence = reinterpret_cast<ULONGLONG>(read->Tail.Overlay.DriverContext[0]);
        if (diagnostics)
            InterlockedExchange64(reinterpret_cast<volatile LONG64*>(&ext->Cache.Performance.LastAfterSequence),
                                  selection.AfterSequence);
        NTSTATUS status;
        if (QcCacheTryReadHit(&ext->Cache, read, InterlockedCompareExchange64(&ext->Size.QuadPart, 0, 0), &status))
        {
            auto bytes = NT_SUCCESS(status) ? read->IoStatus.Information : 0;
            IoReleaseRemoveLock(&ext->RemoveLock, read);
            Complete(read, status, bytes);
            if (diagnostics)
                Increment(&ext->Cache.Performance.ServiceReadCompletions);
            completed = true;
        }
        else
        {
            if (diagnostics)
                Increment(&ext->Cache.Performance.ServiceReadMisses);
            // The sole foreground owner is still blocked. Draining and cache hits
            // cannot populate a missing block; retry after normal admission advances.
            // DriverContext[3] belongs to CSQ and must never be used here.
            read->Tail.Overlay.DriverContext[2] = reinterpret_cast<PVOID>(ext->ReadServiceEpoch);
            // A miss must not block this lane on the lower device. Reinsertion at
            // the head crosses only reads/non-overlapping writes, observations
            // and eligible Fast flushes already checked. No lower read is issued
            // here; the normal worker may later execute this independent miss.
            // CSQ may cancel/free it inline; never touch it after successful insert.
            status = IoCsqInsertIrpEx(&ext->Csq, read, nullptr, reinterpret_cast<PVOID>(1));
            if (!NT_SUCCESS(status))
            {
                IoReleaseRemoveLock(&ext->RemoveLock, read);
                Complete(read, status);
            }
        }
    }
    if (diagnostics && used == 8)
        Increment(&ext->Cache.Performance.ServiceReadBudgetExhausted);
    // A chunk may finish without selecting an IRP. Let the owner recheck its
    // completion/capacity, then continue scanning without a 100 ms sleep. A
    // stable fence, exhausted queue or known miss returns false (no busy retry).
    return completed || selection.More;
}
#endif
static void RequestWorker(PVOID context)
{
    auto ext = static_cast<QC_EXTENSION*>(context);
    for (;;)
    {
        auto irp = IoCsqRemoveNextIrp(&ext->Csq, nullptr);
        if (irp)
        {
            KIRQL activeIrql;
            KeAcquireSpinLock(&ext->QueueLock, &activeIrql);
            ext->ActiveMajor = IoGetCurrentIrpStackLocation(irp)->MajorFunction;
            if (++ext->ReadServiceEpoch == 0)
                ++ext->ReadServiceEpoch;
            ext->ReadSelection.Reset(ext->Pending.Flink);
            ext->ActiveSince = KeQueryPerformanceCounter(nullptr).QuadPart;
            KeReleaseSpinLock(&ext->QueueLock, activeIrql);
            // A control request closes direct admission under QueueLock. Complete
            // older direct I/O before any queued request changes cache state.
            KeWaitForSingleObject(&ext->DirectIdle, Executive, KernelMode, FALSE, nullptr);
#if QCACHE_CACHE_DRIVER
            auto status = QcCacheProcess(&ext->Cache, irp, InterlockedCompareExchange64(&ext->Size.QuadPart, 0, 0));
            auto stack = IoGetCurrentIrpStackLocation(irp);
            if (NT_SUCCESS(status) && QcTrackedUsageNotification(stack))
                UsageStateChanged(ext);
            auto bytes = NT_SUCCESS(status) ? irp->IoStatus.Information : 0;
#else
            KEVENT completed;
            KeInitializeEvent(&completed, NotificationEvent, FALSE);
            IoCopyCurrentIrpStackLocationToNext(irp);
            IoSetCompletionRoutine(irp, QcStartCompletion, &completed, TRUE, TRUE, TRUE);
            auto status = IoCallDriver(ext->Lower, irp);
            if (status == STATUS_PENDING)
                KeWaitForSingleObject(&completed, Executive, KernelMode, FALSE, nullptr);
            // Completion retained this IRP. Preserve lower status and exact byte count.
            status = irp->IoStatus.Status;
            auto bytes = irp->IoStatus.Information;
#endif
            KeAcquireSpinLock(&ext->QueueLock, &activeIrql);
            ext->ActiveSince = 0;
            KeReleaseSpinLock(&ext->QueueLock, activeIrql);
            IoReleaseRemoveLock(&ext->RemoveLock, irp);
            Complete(irp, status, bytes);
            continue;
        }
        // Clear/check under insertion's lock: no lost wakeup if a request races idle.
        KIRQL irql;
        KeAcquireSpinLock(&ext->QueueLock, &irql);
        const bool empty = IsListEmpty(&ext->Pending) != FALSE;
        const bool stop = ext->Closing && empty;
        if (empty)
        {
#if QCACHE_CACHE_DRIVER
            QC_STATE state;
            QcCacheSnapshot(&ext->Cache, &state);
            if (!ext->PendingControls && !(state.Flags & (1UL | 4UL | 16UL)) && !state.DirtyBytes &&
                NT_SUCCESS(state.LastError))
                ext->Routing = FALSE;
#else
            ext->Routing = FALSE;
#endif
        }
        if (empty)
            KeClearEvent(&ext->WorkAvailable);
        KeReleaseSpinLock(&ext->QueueLock, irql);
        if (stop)
            break;
        if (empty)
            KeWaitForSingleObject(&ext->WorkAvailable, Executive, KernelMode, FALSE, nullptr);
    }
#if QCACHE_CACHE_DRIVER
    QcCacheBarrier(&ext->Cache, TRUE, QcRemoveBarrier);
#endif
    PsTerminateSystemThread(STATUS_SUCCESS);
}
static NTSTATUS QueueRequest(QC_EXTENSION* ext, PIRP irp)
{
#if QCACHE_CACHE_DRIVER
    // Save before insertion: cancellation may complete/free the IRP inline.
    auto stack = IoGetCurrentIrpStackLocation(irp);
    const bool control = stack->MajorFunction == IRP_MJ_DEVICE_CONTROL &&
                         (stack->Parameters.DeviceIoControl.IoControlCode == IOCTL_QCACHE_CONTROL_V1 ||
                          stack->Parameters.DeviceIoControl.IoControlCode == IOCTL_QCACHE_OPTIONS_V1);
    const bool usageNotification = QcTrackedUsageNotification(stack);
    const auto usageType = usageNotification ? stack->Parameters.UsageNotification.Type : DeviceUsageTypePaging;
    const BOOLEAN usageInPath = usageNotification ? stack->Parameters.UsageNotification.InPath : FALSE;
    const bool usageReservation = HasUsageReservation(irp);
#endif
    // The CSQ/worker can complete the original IRP before insertion returns.
    // Keep the extension alive for the admission-accounting epilogue.
    UCHAR submissionTag = 0;
    auto admission = IoAcquireRemoveLock(&ext->RemoveLock, &submissionTag);
    if (!NT_SUCCESS(admission))
    {
#if QCACHE_CACHE_DRIVER
        if (usageNotification)
            QcCacheRecordUsageCompletion(&ext->Cache, usageType, usageInPath, admission);
        if (usageReservation)
        {
            QcCacheRecordUsage(&ext->Cache, usageType, FALSE);
            irp->Tail.Overlay.DriverContext[2] = nullptr;
        }
        if (control)
        {
            KIRQL irql;
            KeAcquireSpinLock(&ext->QueueLock, &irql);
            --ext->PendingControls;
            KeSetEvent(&ext->WorkAvailable, IO_NO_INCREMENT, FALSE);
            KeReleaseSpinLock(&ext->QueueLock, irql);
        }
#endif
        IoReleaseRemoveLock(&ext->RemoveLock, irp);
        return Complete(irp, admission);
    }
    IoMarkIrpPending(irp);
    auto status = IoCsqInsertIrpEx(&ext->Csq, irp, nullptr, nullptr);
#if QCACHE_CACHE_DRIVER
    if (control)
    {
        KIRQL irql;
        KeAcquireSpinLock(&ext->QueueLock, &irql);
        --ext->PendingControls;
        // Also wake on cancellation/rejection, when QueueInsert may not run.
        KeSetEvent(&ext->WorkAvailable, IO_NO_INCREMENT, FALSE);
        KeReleaseSpinLock(&ext->QueueLock, irql);
    }
#endif
    if (!NT_SUCCESS(status))
    {
#if QCACHE_CACHE_DRIVER
        if (usageNotification)
            QcCacheRecordUsageCompletion(&ext->Cache, usageType, usageInPath, status);
        if (usageReservation)
            QcCacheRecordUsage(&ext->Cache, usageType, FALSE);
#endif
        IoReleaseRemoveLock(&ext->RemoveLock, irp);
        Complete(irp, status);
    }
    IoReleaseRemoveLock(&ext->RemoveLock, &submissionTag);
    // Pending was marked even if insertion rejected or cancellation completed inline.
    return STATUS_PENDING;
}
#endif

NTSTATUS QcDispatch(PDEVICE_OBJECT device, PIRP irp)
{
    auto ext = static_cast<QC_EXTENSION*>(device->DeviceExtension);
    auto status = IoAcquireRemoveLock(&ext->RemoveLock, irp);
    if (!NT_SUCCESS(status))
        return Complete(irp, status);
    auto stack = IoGetCurrentIrpStackLocation(irp);
#if QCACHE_CACHE_DRIVER
    // Record paging traffic before routing selection, including disabled pass-through.
    // This is observation only and must not alter completion, ordering or admission.
    QcCacheRecordPagingIo(&ext->Cache, irp);
#endif
    if (stack->MajorFunction == IRP_MJ_PNP)
    {
        if (stack->MinorFunction == IRP_MN_REMOVE_DEVICE)
        {
#if QCACHE_SERIALIZED_IO
            KIRQL irql;
            KeAcquireSpinLock(&ext->QueueLock, &irql);
            ext->Closing = TRUE;
            KeSetEvent(&ext->WorkAvailable, IO_NO_INCREMENT, FALSE);
            KeReleaseSpinLock(&ext->QueueLock, irql);
#endif
            // Close admission and wait before allowing the lower stack to disappear.
            IoReleaseRemoveLockAndWait(&ext->RemoveLock, irp);
#if QCACHE_SERIALIZED_IO
            ZwWaitForSingleObject(ext->Worker, FALSE, nullptr);
            ZwClose(ext->Worker);
#endif
#if QCACHE_CACHE_DRIVER
            IoUnregisterShutdownNotification(device);
            QcCacheDestroy(&ext->Cache);
#endif
            auto lower = ext->Lower;
            IoSkipCurrentIrpStackLocation(irp);
            status = IoCallDriver(lower, irp);
            IoDetachDevice(lower);
            IoDeleteDevice(device);
            return status;
        }
#if QCACHE_CACHE_DRIVER
        if (stack->MinorFunction == IRP_MN_SURPRISE_REMOVAL)
        {
            InterlockedExchange(&ext->Cache.Gone, TRUE);
            KeSetEvent(&ext->Cache.Changed, IO_NO_INCREMENT, FALSE);
            KeSetEvent(&ext->Cache.Wake, IO_NO_INCREMENT, FALSE);
        }
        if (stack->MinorFunction == IRP_MN_QUERY_STOP_DEVICE || stack->MinorFunction == IRP_MN_QUERY_REMOVE_DEVICE)
        {
            KIRQL irql;
            KeAcquireSpinLock(&ext->QueueLock, &irql);
            const bool specialFilePath = QcCachePagingPathCount(&ext->Cache) > 0;
            const bool routed = ext->Routing;
            KeReleaseSpinLock(&ext->QueueLock, irql);
            if (specialFilePath)
            {
                IoReleaseRemoveLock(&ext->RemoveLock, irp);
                return Complete(irp, STATUS_UNSUCCESSFUL);
            }
            return routed ? QueueRequest(ext, irp) : Forward(ext, irp);
        }
        if (stack->MinorFunction == IRP_MN_QUERY_PNP_DEVICE_STATE)
            return ForwardQueryPnpState(ext, irp);
#endif
        if (QcTrackedUsageNotification(stack))
        {
            QcCacheRecordUsageRequest(&ext->Cache, stack->Parameters.UsageNotification.Type,
                stack->Parameters.UsageNotification.InPath,
                reinterpret_cast<ULONGLONG>(PsGetCurrentProcessId()));
            KIRQL irql;
            KeAcquireSpinLock(&ext->QueueLock, &irql);
            if (stack->Parameters.UsageNotification.InPath)
                // QcEnable holds this same lock from its count check through the
                // Enabled transition, so either it rejects or this is ordered next.
                QcCacheRecordUsage(&ext->Cache, stack->Parameters.UsageNotification.Type, TRUE);
            const bool routed = ext->Routing != FALSE;
            if (routed && stack->Parameters.UsageNotification.InPath)
                irp->Tail.Overlay.DriverContext[2] = reinterpret_cast<PVOID>(UsageReservationMarker);
            KeReleaseSpinLock(&ext->QueueLock, irql);
            return routed ? QueueRequest(ext, irp)
                          : ForwardUsage(ext, irp, stack->Parameters.UsageNotification.InPath);
        }
        if (stack->MinorFunction == IRP_MN_START_DEVICE)
        {
            KEVENT event;
            KeInitializeEvent(&event, NotificationEvent, FALSE);
            IoCopyCurrentIrpStackLocationToNext(irp);
            IoSetCompletionRoutine(irp, QcStartCompletion, &event, TRUE, TRUE, TRUE);
            status = IoCallDriver(ext->Lower, irp);
            if (status == STATUS_PENDING)
                KeWaitForSingleObject(&event, Executive, KernelMode, FALSE, nullptr);
            status = irp->IoStatus.Status;
            if (NT_SUCCESS(status))
            {
                GET_LENGTH_INFORMATION length = {};
                IO_STATUS_BLOCK iosb = {};
                KeClearEvent(&event);
                auto query = IoBuildDeviceIoControlRequest(
                    IOCTL_DISK_GET_LENGTH_INFO, ext->Lower, nullptr, 0, &length, sizeof(length), FALSE, &event, &iosb);
                if (query)
                {
                    auto queryStatus = IoCallDriver(ext->Lower, query);
                    if (queryStatus == STATUS_PENDING)
                        KeWaitForSingleObject(&event, Executive, KernelMode, FALSE, nullptr);
                    if (NT_SUCCESS(iosb.Status) && iosb.Information >= sizeof(length))
                        InterlockedExchange64(&ext->Size.QuadPart, length.Length.QuadPart);
                }
#if QCACHE_CACHE_DRIVER
                DISK_GEOMETRY geometry = {};
                iosb = {};
                KeClearEvent(&event);
                query = IoBuildDeviceIoControlRequest(IOCTL_DISK_GET_DRIVE_GEOMETRY,
                                                      ext->Lower,
                                                      nullptr,
                                                      0,
                                                      &geometry,
                                                      sizeof(geometry),
                                                      FALSE,
                                                      &event,
                                                      &iosb);
                if (query)
                {
                    auto queryStatus = IoCallDriver(ext->Lower, query);
                    if (queryStatus == STATUS_PENDING)
                        KeWaitForSingleObject(&event, Executive, KernelMode, FALSE, nullptr);
                    if (NT_SUCCESS(iosb.Status) && iosb.Information >= sizeof(geometry))
                        ext->Cache.SectorBytes = geometry.BytesPerSector;
                }
#endif
            }
            IoReleaseRemoveLock(&ext->RemoveLock, irp);
            return Complete(irp, status, irp->IoStatus.Information);
        }
    }
    if (stack->MajorFunction == IRP_MJ_DEVICE_CONTROL)
    {
        auto code = stack->Parameters.DeviceIoControl.IoControlCode;
#if QCACHE_CACHE_DRIVER
        if (code == IOCTL_QCACHE_STATE_V3)
        {
            if (stack->Parameters.DeviceIoControl.OutputBufferLength < sizeof(QC_STATE_V3))
            {
                IoReleaseRemoveLock(&ext->RemoveLock, irp);
                return Complete(irp, STATUS_BUFFER_TOO_SMALL);
            }
            KIRQL irql;
            KeAcquireSpinLock(&ext->QueueLock, &irql);
            QC_STATE_V3 state;
            QcCacheSnapshotV3(&ext->Cache, &state);
            if (ext->Routing && !ext->Closing)
                state.Base.Base.Flags |= 512;
            KeReleaseSpinLock(&ext->QueueLock, irql);
            state.Base.Base.DeviceBytes = InterlockedCompareExchange64(&ext->Size.QuadPart, 0, 0);
            if (ext->Cache.Gone)
                state.Base.Base.Flags |= 16;
            RtlCopyMemory(irp->AssociatedIrp.SystemBuffer, &state, sizeof(state));
            IoReleaseRemoveLock(&ext->RemoveLock, irp);
            return Complete(irp, STATUS_SUCCESS, sizeof(state));
        }
        if (code == IOCTL_QCACHE_STATE_V2)
        {
            if (stack->Parameters.DeviceIoControl.OutputBufferLength < sizeof(QC_STATE_V2))
            {
                IoReleaseRemoveLock(&ext->RemoveLock, irp);
                return Complete(irp, STATUS_BUFFER_TOO_SMALL);
            }
            QC_STATE_V2 state;
            QcCacheSnapshotV2(&ext->Cache, &state);
            state.Base.DeviceBytes = InterlockedCompareExchange64(&ext->Size.QuadPart, 0, 0);
            if (ext->Cache.Gone)
                state.Base.Flags |= 16;
            RtlCopyMemory(irp->AssociatedIrp.SystemBuffer, &state, sizeof(state));
            IoReleaseRemoveLock(&ext->RemoveLock, irp);
            return Complete(irp, STATUS_SUCCESS, sizeof(state));
        }
        if (code == IOCTL_QCACHE_DIAGNOSTICS_V1)
        {
            auto outputLength = stack->Parameters.DeviceIoControl.OutputBufferLength;
            if (outputLength < QcDiagnosticsV1Size)
            {
                IoReleaseRemoveLock(&ext->RemoveLock, irp);
                return Complete(irp, STATUS_BUFFER_TOO_SMALL);
            }
            QC_DIAGNOSTICS diagnostics;
            QcCacheDiagnostics(&ext->Cache, &diagnostics);
            auto returned = outputLength >= sizeof(diagnostics) ? sizeof(diagnostics) :
                outputLength >= QcDiagnosticsV4Size ? QcDiagnosticsV4Size :
                outputLength >= QcDiagnosticsV3Size ? QcDiagnosticsV3Size :
                outputLength >= QcDiagnosticsV2Size ? QcDiagnosticsV2Size : QcDiagnosticsV1Size;
            diagnostics.Version = returned == QcDiagnosticsV1Size ? 1 : returned == QcDiagnosticsV2Size ? 2 :
                returned == QcDiagnosticsV3Size ? 3 : returned == QcDiagnosticsV4Size ? 4 : 5;
            diagnostics.Size = static_cast<ULONG>(returned);
            RtlCopyMemory(irp->AssociatedIrp.SystemBuffer, &diagnostics, returned);
            IoReleaseRemoveLock(&ext->RemoveLock, irp);
            return Complete(irp, STATUS_SUCCESS, returned);
        }
        if (code == IOCTL_QCACHE_PERFORMANCE_V1)
        {
            auto outputLength = stack->Parameters.DeviceIoControl.OutputBufferLength;
            if (outputLength < QcPerformanceV1Size)
            {
                IoReleaseRemoveLock(&ext->RemoveLock, irp);
                return Complete(irp, STATUS_BUFFER_TOO_SMALL);
            }
            QC_PERFORMANCE performance;
            QcCachePerformance(&ext->Cache, &performance);
#if QCACHE_SERIALIZED_IO
            KIRQL irql;
            KeAcquireSpinLock(&ext->QueueLock, &irql);
            auto now = static_cast<ULONGLONG>(KeQueryPerformanceCounter(nullptr).QuadPart);
            performance.QueueDepth = ext->QueueDepth;
            performance.QueuedRequests = ext->NextSequence;
            performance.QueueWaitTicks = ext->QueueWaitTicks;
            performance.MaxQueueWaitTicks = ext->MaxQueueWaitTicks;
            performance.ActiveMajor = ext->ActiveSince ? ext->ActiveMajor : 0;
            performance.ActiveAgeTicks = ext->ActiveSince ? now - ext->ActiveSince : 0;
            if (!ext->ActiveSince)
                performance.Phase = QcIdlePhase;
            else if (!performance.Phase)
                performance.Phase = QcRequestPhase;
            if (!IsListEmpty(&ext->Pending))
            {
                auto first = CONTAINING_RECORD(ext->Pending.Flink, IRP, Tail.Overlay.ListEntry);
                performance.OldestQueuedTicks = now - reinterpret_cast<ULONGLONG>(first->Tail.Overlay.DriverContext[1]);
            }
            KeReleaseSpinLock(&ext->QueueLock, irql);
#endif
            auto bytes = outputLength < QcPerformanceV2Size ? QcPerformanceV1Size
                         : outputLength < sizeof(performance) ? QcPerformanceV2Size
                                                             : sizeof(performance);
            if (bytes == QcPerformanceV1Size)
            {
                performance.Version = 1;
                performance.Size = QcPerformanceV1Size;
            }
            else if (bytes == QcPerformanceV2Size)
            {
                performance.Version = 2;
                performance.Size = QcPerformanceV2Size;
            }
            RtlCopyMemory(irp->AssociatedIrp.SystemBuffer, &performance, bytes);
            IoReleaseRemoveLock(&ext->RemoveLock, irp);
            return Complete(irp, STATUS_SUCCESS, bytes);
        }
        if (code == IOCTL_QCACHE_STATE_V1)
        {
            if (stack->Parameters.DeviceIoControl.OutputBufferLength < sizeof(QC_STATE))
            {
                IoReleaseRemoveLock(&ext->RemoveLock, irp);
                return Complete(irp, STATUS_BUFFER_TOO_SMALL);
            }
            QC_STATE state;
            QcCacheSnapshot(&ext->Cache, &state);
            state.DeviceBytes = InterlockedCompareExchange64(&ext->Size.QuadPart, 0, 0);
            if (ext->Cache.Gone)
                state.Flags |= 16;
            RtlCopyMemory(irp->AssociatedIrp.SystemBuffer, &state, sizeof(state));
            IoReleaseRemoveLock(&ext->RemoveLock, irp);
            return Complete(irp, STATUS_SUCCESS, sizeof(state));
        }
#endif
        if (code == IOCTL_QCACHE_GET_DEVICE_DATA)
        {
            if (stack->Parameters.DeviceIoControl.OutputBufferLength < sizeof(DEVICE_STATISTICS))
            {
                IoReleaseRemoveLock(&ext->RemoveLock, irp);
                return Complete(irp, STATUS_BUFFER_TOO_SMALL);
            }
            DEVICE_STATISTICS stats = {};
            stats.Version = sizeof(stats);
            stats.Size.QuadPart = InterlockedCompareExchange64(&ext->Size.QuadPart, 0, 0);
            stats.ReadBytes = InterlockedCompareExchange64(&ext->ReadBytes, 0, 0);
            stats.WrittenBytes = InterlockedCompareExchange64(&ext->WrittenBytes, 0, 0);
#if QCACHE_CACHE_DRIVER
            QC_STATE state;
            QcCacheSnapshot(&ext->Cache, &state);
            stats.IsCached = (state.Flags & 1) != 0;
            stats.LastErrorCode = state.LastError;
            stats.WriteQueueItems = state.OccupiedSlots;
            stats.WriteQueueSize = state.DirtyBytes;
            stats.WriteQueueSizeTop = state.PeakDirtyBytes;
            stats.MaxQueueSize = state.PayloadCapacity;
            stats.LowMemQueued = state.ThrottleWaits;
            stats.PagingPathCount = QcCachePagingPathCount(&ext->Cache);
#endif
            RtlCopyMemory(irp->AssociatedIrp.SystemBuffer, &stats, sizeof(stats));
            IoReleaseRemoveLock(&ext->RemoveLock, irp);
            return Complete(irp, STATUS_SUCCESS, sizeof(stats));
        }
        if (code == IOCTL_QCACHE_ON || code == IOCTL_QCACHE_OFF || code == IOCTL_QCACHE_FLUSH)
        {
            IoReleaseRemoveLock(&ext->RemoveLock, irp);
            return Complete(irp, STATUS_NOT_SUPPORTED);
        }
    }
    if (stack->MajorFunction == IRP_MJ_READ)
        InterlockedAdd64(&ext->ReadBytes, stack->Parameters.Read.Length);
    if (stack->MajorFunction == IRP_MJ_WRITE)
        InterlockedAdd64(&ext->WrittenBytes, stack->Parameters.Write.Length);
#if QCACHE_SERIALIZED_IO
    if (QcObservationRequest(stack))
    {
        KIRQL irql;
        KeAcquireSpinLock(&ext->QueueLock, &irql);
        const bool closing = ext->Closing != FALSE;
        KeReleaseSpinLock(&ext->QueueLock, irql);
        if (closing)
        {
            IoReleaseRemoveLock(&ext->RemoveLock, irp);
            return Complete(irp, STATUS_DELETE_PENDING);
        }
        // Preserve caller context, real lower response and normal cancellation.
        // RemoveLock spans completion/removal. No DirectCount: an observation
        // must not hold up subsequent cache admission behind DirectIdle.
        return Forward(ext, irp);
    }
    // Inactive devices have true pass-through semantics, including METHOD_NEITHER
    // requests which must retain the original caller context. A control request
    // atomically switches subsequent traffic to the ordered worker.
    if (stack->MajorFunction != IRP_MJ_PNP)
    {
        KIRQL irql;
        KeAcquireSpinLock(&ext->QueueLock, &irql);
#if QCACHE_CACHE_DRIVER
        const bool control = stack->MajorFunction == IRP_MJ_DEVICE_CONTROL &&
                             (stack->Parameters.DeviceIoControl.IoControlCode == IOCTL_QCACHE_CONTROL_V1 ||
                              stack->Parameters.DeviceIoControl.IoControlCode == IOCTL_QCACHE_OPTIONS_V1);
#else
        const bool control = FALSE;
#endif
        if (control)
        {
            ext->Routing = TRUE;
            ++ext->PendingControls;
        }
        const bool direct = !ext->Routing && !ext->Closing;
        if (direct && ++ext->DirectCount == 1)
            KeClearEvent(&ext->DirectIdle);
        KeReleaseSpinLock(&ext->QueueLock, irql);
        if (direct)
        {
            IoCopyCurrentIrpStackLocationToNext(irp);
            IoSetCompletionRoutine(irp, DirectCompletion, ext, TRUE, TRUE, TRUE);
#if QCACHE_CACHE_DRIVER
            QcCacheRecordLowerAttempt(&ext->Cache, stack->MajorFunction);
#endif
            return IoCallDriver(ext->Lower, irp);
        }
    }
#if QCACHE_CACHE_DRIVER
    if (stack->MajorFunction == IRP_MJ_SHUTDOWN ||
        (stack->MajorFunction == IRP_MJ_POWER && stack->MinorFunction == IRP_MN_SET_POWER &&
         stack->Parameters.Power.Type == DevicePowerState))
        return QueueRequest(ext, irp);
#endif
    if (stack->MajorFunction == IRP_MJ_READ || stack->MajorFunction == IRP_MJ_WRITE ||
        stack->MajorFunction == IRP_MJ_FLUSH_BUFFERS || stack->MajorFunction == IRP_MJ_DEVICE_CONTROL ||
        stack->MajorFunction == IRP_MJ_INTERNAL_DEVICE_CONTROL)
        return QueueRequest(ext, irp);
#endif
    // Includes real flush/write-through, power, shutdown, TRIM and all unknown IOCTLs.
    // Never synthesize success for a storage operation.
    return Forward(ext, irp);
}

NTSTATUS QcAddDevice(PDRIVER_OBJECT driver, PDEVICE_OBJECT pdo)
{
    // Idempotent migration: class and old per-device registrations may coexist
    // if setup was interrupted. Never attach this driver twice to the same stack.
    auto existing = IoGetAttachedDeviceReference(pdo);
    while (existing)
    {
        if (existing->DriverObject == driver)
        {
            ObDereferenceObject(existing);
            return STATUS_SUCCESS;
        }
        auto next = IoGetLowerDeviceObject(existing);
        ObDereferenceObject(existing);
        existing = next;
    }
    WCHAR key[512] = {};
    ULONG required = 0;
    auto status = IoGetDeviceProperty(pdo, DevicePropertyDriverKeyName, sizeof(key), key, &required);
    if (!NT_SUCCESS(status) || required < sizeof(WCHAR) || required > sizeof(key) ||
        key[required / sizeof(WCHAR) - 1] != 0)
        return STATUS_SUCCESS;
    UNICODE_STRING actual;
    RtlInitUnicodeString(&actual, key);
    if (!ClassCoverage && !RtlEqualUnicodeString(&actual, &AllowedDriverKey, TRUE))
        return STATUS_SUCCESS;
    PDEVICE_OBJECT device = nullptr;
    status = IoCreateDevice(
        driver, sizeof(QC_EXTENSION), nullptr, FILE_DEVICE_DISK, FILE_DEVICE_SECURE_OPEN, FALSE, &device);
    if (!NT_SUCCESS(status))
        return status;
    auto ext = static_cast<QC_EXTENSION*>(device->DeviceExtension);
    RtlZeroMemory(ext, sizeof(*ext));
    ext->Self = device;
    ext->Pdo = pdo;
    IoInitializeRemoveLock(&ext->RemoveLock, 'bLCQ', 0, 0);
    status = IoAttachDeviceToDeviceStackSafe(device, pdo, &ext->Lower);
    if (!NT_SUCCESS(status))
    {
        IoDeleteDevice(device);
        return status;
    }
    device->Flags |= ext->Lower->Flags & (DO_DIRECT_IO | DO_BUFFERED_IO);
    // All harness code/data is nonpageable; do not advertise pageable power dispatch.
    device->Characteristics |= ext->Lower->Characteristics;
#if QCACHE_CACHE_DRIVER
    status = QcCacheInitialize(&ext->Cache, ext->Lower);
    if (NT_SUCCESS(status))
        status = IoRegisterLastChanceShutdownNotification(device);
    if (!NT_SUCCESS(status))
    {
        QcCacheDestroy(&ext->Cache);
        IoDetachDevice(ext->Lower);
        IoDeleteDevice(device);
        return status;
    }
#endif
#if QCACHE_SERIALIZED_IO
    KeInitializeSpinLock(&ext->QueueLock);
#if QCACHE_CACHE_DRIVER
    ext->Cache.RoutingLock = &ext->QueueLock;
#endif
    KeInitializeEvent(&ext->DirectIdle, NotificationEvent, TRUE);
    InitializeListHead(&ext->Pending);
    ext->ReadSelection.Reset(&ext->Pending);
    KeInitializeEvent(&ext->WorkAvailable, NotificationEvent, FALSE);
#if QCACHE_CACHE_DRIVER
    ext->Cache.ServiceReads = ServiceCachedReads;
    ext->Cache.ServiceContext = ext;
    ext->Cache.RequestAvailable = &ext->WorkAvailable;
#endif
    status = IoCsqInitializeEx(&ext->Csq, QueueInsert, QueueRemove, QueuePeek, QueueAcquire, QueueRelease, QueueCancel);
    if (NT_SUCCESS(status))
    {
        OBJECT_ATTRIBUTES attrs;
        InitializeObjectAttributes(&attrs, nullptr, OBJ_KERNEL_HANDLE, nullptr, nullptr);
        status = PsCreateSystemThread(&ext->Worker, THREAD_ALL_ACCESS, &attrs, nullptr, nullptr, RequestWorker, ext);
    }
    if (!NT_SUCCESS(status))
    {
#if QCACHE_CACHE_DRIVER
        IoUnregisterShutdownNotification(device);
        QcCacheDestroy(&ext->Cache);
#endif
        IoDetachDevice(ext->Lower);
        IoDeleteDevice(device);
        return status;
    }
#endif
    device->Flags &= ~DO_DEVICE_INITIALIZING;
    return STATUS_SUCCESS;
}

void QcUnload(PDRIVER_OBJECT driver)
{
    NT_ASSERT(driver->DeviceObject == nullptr);
    UNREFERENCED_PARAMETER(driver);
}

extern "C" NTSTATUS DriverEntry(PDRIVER_OBJECT driver, PUNICODE_STRING registryPath)
{
    // No default/all-disk target. Installer records one exact device's driver key.
    OBJECT_ATTRIBUTES attrs;
    InitializeObjectAttributes(&attrs, registryPath, OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, nullptr, nullptr);
    HANDLE key = nullptr;
    auto status = ZwOpenKey(&key, KEY_QUERY_VALUE, &attrs);
    if (!NT_SUCCESS(status))
        return status;
    UNICODE_STRING name = RTL_CONSTANT_STRING(L"LabAllowedDriverKey");
    alignas(KEY_VALUE_PARTIAL_INFORMATION)
        UCHAR buffer[sizeof(KEY_VALUE_PARTIAL_INFORMATION) + sizeof(ExpectedDriverKey)] = {};
    ULONG required;
    status = ZwQueryValueKey(key, &name, KeyValuePartialInformation, buffer, sizeof(buffer), &required);
    // Preserve compatibility with per-device packages while class coverage is
    // validated. An explicit installer DWORD selects the new attachment model.
    UNICODE_STRING className = RTL_CONSTANT_STRING(L"ClassCoverage");
    alignas(KEY_VALUE_PARTIAL_INFORMATION)
        UCHAR classBuffer[sizeof(KEY_VALUE_PARTIAL_INFORMATION) + sizeof(ULONG)] = {};
    ULONG classRequired;
    if (NT_SUCCESS(ZwQueryValueKey(
            key, &className, KeyValuePartialInformation, classBuffer, sizeof(classBuffer), &classRequired)))
    {
        auto classValue = reinterpret_cast<KEY_VALUE_PARTIAL_INFORMATION*>(classBuffer);
        ClassCoverage = classValue->Type == REG_DWORD && classValue->DataLength == sizeof(ULONG) &&
                        *reinterpret_cast<ULONG*>(classValue->Data) == 1;
    }
    ZwClose(key);
    if (!NT_SUCCESS(status))
        return status;
    auto value = reinterpret_cast<KEY_VALUE_PARTIAL_INFORMATION*>(buffer);
    if (value->Type != REG_SZ || value->DataLength < 2 * sizeof(WCHAR) ||
        value->DataLength > sizeof(ExpectedDriverKey) || value->DataLength % sizeof(WCHAR) != 0)
        return STATUS_INVALID_PARAMETER;
    RtlCopyMemory(ExpectedDriverKey, value->Data, value->DataLength);
    if (ExpectedDriverKey[value->DataLength / sizeof(WCHAR) - 1] != 0)
        return STATUS_INVALID_PARAMETER;
    RtlInitUnicodeString(&AllowedDriverKey, ExpectedDriverKey);
    if (AllowedDriverKey.Length + sizeof(WCHAR) != value->DataLength)
        return STATUS_INVALID_PARAMETER;
    for (ULONG i = 0; i <= IRP_MJ_MAXIMUM_FUNCTION; ++i)
        driver->MajorFunction[i] = QcDispatch;
    driver->DriverExtension->AddDevice = QcAddDevice;
    driver->DriverUnload = QcUnload;
    return STATUS_SUCCESS;
}
