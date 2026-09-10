// SPDX-License-Identifier: MIT
// Serialized block cache: dirty FIFO, clean write/read LRUs and a single versioned index.
#include "writecache.h"
#include <ntddstor.h>
#include <ntdddisk.h>
#include <ntddscsi.h>
static constexpr ULONG Chunk = 4096, SlabBytes = 262144, SlotsPerSlab = SlabBytes / Chunk, Tag = 'wCCQ';
static constexpr ULONG NoSlot = MAXULONG;
static constexpr ULONG MaxBatchBytes = 1024 * 1024;
static volatile LONG64 GlobalBudget, NextInstance;
static ULONGLONG MemoryLimit() {
    auto ranges = MmGetPhysicalMemoryRangesEx2(nullptr, 0);
    if (!ranges) return 0;
    ULONGLONG total = 0;
    for (auto r = ranges; r->NumberOfBytes.QuadPart; ++r) total += r->NumberOfBytes.QuadPart;
    ExFreePool(ranges);
    return min(128ULL << 30, total / 4 * 3);
}
static ULONGLONG GlobalLimit;
static ULONGLONG NowMs() { return KeQueryInterruptTime() / 10000; }
#include "cacheblocks.inl"
static void AcquireCache(QC_CACHE* c) { KeWaitForSingleObject(&c->Mutex, Executive, KernelMode, FALSE, nullptr); }
static void ReleaseCache(QC_CACHE* c) { KeReleaseMutex(&c->Mutex, FALSE); }
// Caller holds Mutex. Dispatch can read the small coherent snapshot at DISPATCH_LEVEL.
static void Publish(QC_CACHE* c) {
    c->State.Version = 1; c->State.Size = sizeof(QC_STATE);
    c->State.Flags = (c->Enabled ? 1UL : 0UL) | (!NT_SUCCESS(c->State.LastError) ? 2UL : 0UL) |
        (c->Suspended ? 4UL : 0UL) | (c->Barrier ? 8UL : 0UL) | (c->Gone ? 16UL : 0UL) | (c->UnsafeDefer ? 32UL : 0UL) | 64UL | 128UL | 256UL | 1024UL; // 128: drain-and-release task support. 1024: QcDropClean support.
    c->State.OccupiedSlots = c->Count;
    KIRQL irql; KeAcquireSpinLock(&c->SnapshotLock, &irql);
    c->Snapshot = c->State;
    c->ExtendedSnapshot.Base = c->State;
    c->ExtendedSnapshot.Base.Version = 2; c->ExtendedSnapshot.Base.Size = sizeof(QC_STATE_V2);
    c->ExtendedSnapshot.DiscardedBytes = c->DiscardedBytes;
    c->ExtendedSnapshot.LowerWrites = c->LowerWrites;
    c->ExtendedSnapshot.BatchedWrites = c->BatchedWrites;
    c->ExtendedSnapshot.TrimRequests = c->TrimRequests;
    c->ReadWriteSnapshot.Base = c->ExtendedSnapshot;
    c->ReadWriteSnapshot.Base.Base.Version = 3; c->ReadWriteSnapshot.Base.Base.Size = sizeof(QC_STATE_V3);
    c->ReadWriteSnapshot.Options = c->Options;
    c->ReadWriteSnapshot.CleanReadBytes = static_cast<ULONGLONG>(c->CleanCount[1]) * Chunk;
    c->ReadWriteSnapshot.CleanWriteBytes = static_cast<ULONGLONG>(c->CleanCount[0]) * Chunk;
    c->ReadWriteSnapshot.ReadHitBytes = c->ReadHitBytes; c->ReadWriteSnapshot.ReadMissBytes = c->ReadMissBytes;
    c->ReadWriteSnapshot.Evictions = c->Evictions;
    c->ReadWriteSnapshot.OldestDirtyMs = c->Head == NoSlot ? 0 : NowMs() - c->Slots[c->Head].DirtySince;
    c->ReadWriteSnapshot.Generation = c->Generation; c->ReadWriteSnapshot.Instance = c->Instance;
    c->ReadWriteSnapshot.GlobalLimitBytes = GlobalLimit;
    c->ReadWriteSnapshot.GlobalReservedBytes = InterlockedCompareExchange64(&GlobalBudget, 0, 0);
    c->Diagnostics.Version = 1; c->Diagnostics.Size = sizeof(QC_DIAGNOSTICS);
    c->DiagnosticsSnapshot = c->Diagnostics;
    KeReleaseSpinLock(&c->SnapshotLock, irql);
}
void QcCacheSnapshot(QC_CACHE* c, QC_STATE* output) {
    KIRQL irql; KeAcquireSpinLock(&c->SnapshotLock, &irql);
    *output = c->Snapshot;
    KeReleaseSpinLock(&c->SnapshotLock, irql);
}
void QcCacheSnapshotV2(QC_CACHE* c, QC_STATE_V2* output) {
    KIRQL irql; KeAcquireSpinLock(&c->SnapshotLock, &irql);
    *output = c->ExtendedSnapshot;
    KeReleaseSpinLock(&c->SnapshotLock, irql);
}
void QcCacheSnapshotV3(QC_CACHE* c, QC_STATE_V3* output) {
    KIRQL irql; KeAcquireSpinLock(&c->SnapshotLock, &irql);
    *output = c->ReadWriteSnapshot;
    output->GlobalReservedBytes = InterlockedCompareExchange64(&GlobalBudget, 0, 0);
    output->GlobalLimitBytes = GlobalLimit;
    KeReleaseSpinLock(&c->SnapshotLock, irql);
}
void QcCacheDiagnostics(QC_CACHE* c, QC_DIAGNOSTICS* output) {
    KIRQL irql; KeAcquireSpinLock(&c->SnapshotLock, &irql);
    *output = c->DiagnosticsSnapshot;
    KeReleaseSpinLock(&c->SnapshotLock, irql);
}
static void Fault(QC_CACHE* c, NTSTATUS status) {
    c->State.LastError = status; ++c->State.Errors;
    Publish(c); KeSetEvent(&c->Changed, IO_NO_INCREMENT, FALSE);
}
static NTSTATUS InjectLowerCompletion(PDEVICE_OBJECT, PIRP irp, PVOID context) {
    // Lab-only: the real lower request has completed, but alter the result before
    // the I/O manager copies it to our IOSB/signals the event. Never mask a real error.
    if (NT_SUCCESS(irp->IoStatus.Status)) {
        if (reinterpret_cast<ULONG_PTR>(context) == 4) {
            irp->IoStatus.Status = STATUS_IO_DEVICE_ERROR;
            irp->IoStatus.Information = 0;
        } else irp->IoStatus.Information /= 2;
    }
    // This driver created the IRP; do not propagate PendingReturned to a nonexistent upper stack location.
    return STATUS_CONTINUE_COMPLETION;
}
static NTSTATUS LowerIo(QC_CACHE* c, ULONG major, QC_SLOT* slot = nullptr, ULONG inject = 0) {
    KEVENT completed; KeInitializeEvent(&completed, NotificationEvent, FALSE);
    IO_STATUS_BLOCK iosb = {}; // Lives until the entire lower completion is observed.
    auto irp = IoBuildSynchronousFsdRequest(major, c->Lower, slot ? slot->Buffer : nullptr,
        slot ? slot->Length : 0, slot ? &slot->Offset : nullptr, &completed, &iosb);
    if (!irp) return STATUS_INSUFFICIENT_RESOURCES;
    if (major == IRP_MJ_WRITE) irp->Flags |= IRP_WRITE_OPERATION | IRP_NOCACHE;
    if (inject == 4 || inject == 5)
        IoSetCompletionRoutine(irp, InjectLowerCompletion, reinterpret_cast<PVOID>(static_cast<ULONG_PTR>(inject)), TRUE, TRUE, TRUE);
    auto status = IoCallDriver(c->Lower, irp);
    if (status == STATUS_PENDING) KeWaitForSingleObject(&completed, Executive, KernelMode, FALSE, nullptr);
    if (!NT_SUCCESS(iosb.Status)) return iosb.Status;
    if (slot && iosb.Information != slot->Length) return STATUS_DEVICE_DATA_ERROR;
    return STATUS_SUCCESS;
}
// Read-only queries versus controls that can change stored data. The access bits of a
// control code state whether its caller may modify the device; every media-modifying
// storage/disk control declares FILE_WRITE_ACCESS, except the media-swap, bus/device
// reset and data-set-attribute (TRIM) codes listed explicitly below.
constexpr bool QcMayChangeMedia(ULONG code) {
    if (((code >> 14) & 3) & FILE_WRITE_ACCESS) return true;
    switch (code) {
    case IOCTL_STORAGE_MANAGE_DATA_SET_ATTRIBUTES: // TRIM shapes TryTrim did not optimize.
    case IOCTL_STORAGE_EJECT_MEDIA:
    case IOCTL_STORAGE_LOAD_MEDIA:
    case IOCTL_STORAGE_LOAD_MEDIA2:
    case IOCTL_STORAGE_MEDIA_REMOVAL:
    case IOCTL_STORAGE_EJECTION_CONTROL:
    case IOCTL_STORAGE_RESET_BUS:
    case IOCTL_STORAGE_RESET_DEVICE:
    case IOCTL_DISK_EJECT_MEDIA:
    case IOCTL_DISK_LOAD_MEDIA:
    case IOCTL_DISK_MEDIA_REMOVAL:
    case IOCTL_DISK_REASSIGN_BLOCKS:
        return true;
    default: return false;
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
static_assert(QcMayChangeMedia(IOCTL_STORAGE_MANAGE_DATA_SET_ATTRIBUTES));
static_assert(QcMayChangeMedia(IOCTL_DISK_SET_DRIVE_LAYOUT_EX));
static_assert(QcMayChangeMedia(IOCTL_DISK_SET_DISK_ATTRIBUTES));
static_assert(QcMayChangeMedia(IOCTL_DISK_FORMAT_TRACKS));
static_assert(QcMayChangeMedia(IOCTL_SCSI_PASS_THROUGH));
static_assert(QcMayChangeMedia(IOCTL_STORAGE_EJECT_MEDIA));
static_assert(QcMayChangeMedia(IOCTL_STORAGE_FIRMWARE_DOWNLOAD));
static NTSTATUS RetainCompletion(PDEVICE_OBJECT, PIRP, PVOID event) {
    KeSetEvent(static_cast<PKEVENT>(event), IO_NO_INCREMENT, FALSE);
    return STATUS_MORE_PROCESSING_REQUIRED;
}
static NTSTATUS OriginalIo(QC_CACHE* c, PIRP irp) {
    KEVENT completed; KeInitializeEvent(&completed, NotificationEvent, FALSE);
    IoCopyCurrentIrpStackLocationToNext(irp);
    IoSetCompletionRoutine(irp, RetainCompletion, &completed, TRUE, TRUE, TRUE);
    auto status = IoCallDriver(c->Lower, irp);
    if (status == STATUS_PENDING) KeWaitForSingleObject(&completed, Executive, KernelMode, FALSE, nullptr);
    return irp->IoStatus.Status;
}
static void Drainer(PVOID context) {
    auto worker = static_cast<QC_DRAIN_WORKER*>(context);
    auto c = worker->Cache;
    for (;;) {
        AcquireCache(c);
        if (c->Stop) { ReleaseCache(c); break; }
        if (worker->Number >= c->Options.Parallelism || c->State.DirtyBytes == 0 || c->TrimPaused || !NT_SUCCESS(c->State.LastError) || c->Gone) {
            KeClearEvent(&c->Wake); ReleaseCache(c);
            LARGE_INTEGER interval; interval.QuadPart = -1000000;
            KeWaitForSingleObject(&c->Wake, Executive, KernelMode, FALSE, &interval);
            continue;
        }
        bool pressure = c->Pressure != FALSE;
        auto now = NowMs();
        bool drain = QcShouldDrain(c->Options, c->State.DirtyBytes,
            static_cast<ULONGLONG>(WriteLimit(c)) * Chunk, now - c->Slots[c->Head].DirtySince,
            now - c->LastWriteTime, c->Barrier || c->WriterWaiting, pressure);
        c->Pressure = pressure;
        if (!drain) {
            Publish(c); KeClearEvent(&c->Wake); ReleaseCache(c);
            LARGE_INTEGER interval; interval.QuadPart = -1000000; // age/idle deadlines checked every 100 ms
            KeWaitForSingleObject(&c->Wake, Executive, KernelMode, FALSE, &interval); continue;
        }
        // Gather disk-adjacent blocks into a preallocated staging buffer. Their RAM
        // addresses need not be adjacent. Always choose the oldest version of each
        // address so a later completion can never overwrite newer data on disk.
        auto index = c->Head;
        // Distinct disk ranges can drain concurrently. Never issue a newer version
        // while an older write to that address is still outstanding.
        while (index != NoSlot && (c->Slots[index].InFlight ||
            FindOldestSlot(c, c->Slots[index].Offset.QuadPart) != index)) index = c->Slots[index].QueueNext;
        if (index == NoSlot) {
            KeClearEvent(&c->Wake); ReleaseCache(c);
            LARGE_INTEGER interval; interval.QuadPart = -1000000;
            KeWaitForSingleObject(&c->Wake, Executive, KernelMode, FALSE, &interval); continue;
        }
        auto first = &c->Slots[index];
        QC_SLOT io = *first;
        ULONG selected[MaxBatchBytes / Chunk];
        ULONG merged = 0;
        io.Buffer = c->DrainBuffer + worker->Number * c->DrainCapacity; io.Length = 0;
        while (index != NoSlot && merged < min(c->Options.BatchKiB * 1024, c->DrainCapacity) / Chunk) {
            auto slot = &c->Slots[index];
            if (slot->InFlight) break;
            selected[merged++] = index; slot->InFlight = TRUE;
            RtlCopyMemory(io.Buffer + io.Length, slot->Buffer, Chunk); io.Length += Chunk;
            index = FindOldestSlot(c, io.Offset.QuadPart + io.Length);
        }
        c->State.InFlightBytes += io.Length;
        auto delay = c->DelayMs;
        auto inject = c->InjectFault == 1 || c->InjectFault == 2 || c->InjectFault == 4 || c->InjectFault == 5 ? c->InjectFault : 0;
        if (inject) c->InjectFault = 0;
        Publish(c); ReleaseCache(c);
        if (delay) { LARGE_INTEGER interval; interval.QuadPart = -10000LL * delay; KeDelayExecutionThread(KernelMode, FALSE, &interval); }
        // Lab-only synthetic failure/short-completion path, deliberately identified in controls.
        auto status = inject == 1 ? STATUS_IO_DEVICE_ERROR : inject == 2 ? STATUS_DEVICE_DATA_ERROR : LowerIo(c, IRP_MJ_WRITE, &io, inject);
        AcquireCache(c);
        c->State.InFlightBytes -= io.Length;
        for (ULONG i = 0; i < merged; ++i) c->Slots[selected[i]].InFlight = FALSE;
        if (NT_SUCCESS(status)) {
            ++c->LowerWrites; if (merged > 1) ++c->BatchedWrites;
            c->State.DirtyBytes -= io.Length;
            c->State.DrainedBytes += io.Length;
            for (ULONG i = 0; i < merged; ++i) {
                auto completedIndex = selected[i]; auto slot = &c->Slots[completedIndex];
                if (c->Enabled && (c->Options.Retention & QcRetainWrites) && FindSlot(c, slot->Offset.QuadPart) == completedIndex) {
                    Unlink(c, completedIndex); slot->Dirty = FALSE; slot->ReadClass = FALSE; Link(c, completedIndex);
                } else RetireSlot(c, completedIndex);
            }
            Publish(c); KeSetEvent(&c->Changed, IO_NO_INCREMENT, FALSE);
        } else Fault(c, status); // Keep the dirty slot and stop retries until explicit recovery.
        KeSetEvent(&c->Wake, IO_NO_INCREMENT, FALSE); ReleaseCache(c);
    }
    PsTerminateSystemThread(STATUS_SUCCESS);
}
NTSTATUS QcCacheInitialize(QC_CACHE* c, PDEVICE_OBJECT lower) {
    RtlZeroMemory(c, sizeof(*c)); c->Lower = lower;
    c->Head = c->Tail = c->FreeHead = NoSlot;
    c->CleanHead[0] = c->CleanHead[1] = c->CleanTail[0] = c->CleanTail[1] = NoSlot;
    c->Options = QcDefaultOptions(); c->Instance = InterlockedIncrement64(&NextInstance);
    GlobalLimit = MemoryLimit();
    // A disk-class upper filter can accidentally be installed ABOVE partmgr.
    // Its generated background writes have no filesystem FileObject, so partmgr
    // rejects writes into mounted partitions. Stay usable as pass-through, but
    // refuse write-cache enablement before acknowledging any volatile data.
    UNICODE_STRING partitionManager = RTL_CONSTANT_STRING(L"\\Driver\\partmgr");
    auto current = lower; ObReferenceObject(current);
    while (current) {
        if (RtlEqualUnicodeString(&current->DriverObject->DriverName, &partitionManager, TRUE))
            c->BlockedPlacement = TRUE;
        auto next = IoGetLowerDeviceObject(current);
        ObDereferenceObject(current); current = next;
    }
    KeInitializeMutex(&c->Mutex, 0); KeInitializeSpinLock(&c->SnapshotLock);
    KeInitializeEvent(&c->Wake, NotificationEvent, FALSE); KeInitializeEvent(&c->Changed, NotificationEvent, FALSE);
    Publish(c);
    OBJECT_ATTRIBUTES attrs; InitializeObjectAttributes(&attrs, nullptr, OBJ_KERNEL_HANDLE, nullptr, nullptr);
    for (ULONG i = 0; i < RTL_NUMBER_OF(c->Workers); ++i) {
        auto worker = &c->Workers[i]; worker->Cache = c; worker->Number = i;
        auto status = PsCreateSystemThread(&worker->Thread, THREAD_ALL_ACCESS, &attrs, nullptr, nullptr, Drainer, worker);
        if (!NT_SUCCESS(status)) { QcCacheDestroy(c); return status; }
    }
    return STATUS_SUCCESS;
}
// Shared across disk instances, not a separate 4 GiB reservation per disk.
static void FreeSlots(QC_CACHE* c) {
    if (c->State.BudgetBytes) InterlockedAdd64(&GlobalBudget, -static_cast<LONG64>(c->State.BudgetBytes));
    if (c->Slots) {
        for (ULONG i = 0; i < c->Capacity; i += SlotsPerSlab) if (c->Slots[i].Buffer) ExFreePoolWithTag(c->Slots[i].Buffer, Tag);
        ExFreePoolWithTag(c->Slots, Tag);
    }
    c->Count = c->CleanCount[0] = c->CleanCount[1] = 0;
    c->CleanHead[0] = c->CleanHead[1] = c->CleanTail[0] = c->CleanTail[1] = NoSlot;
    c->Slots = nullptr; c->Capacity = 0; c->Head = c->Tail = c->FreeHead = NoSlot;
    if (c->Buckets) ExFreePoolWithTag(c->Buckets, Tag);
    c->Buckets = nullptr;
    if (c->DrainBuffer) ExFreePoolWithTag(c->DrainBuffer, Tag);
    c->DrainBuffer = nullptr;
    c->State.ReservedBytes = c->State.PayloadCapacity = c->State.BudgetBytes = 0;
}
NTSTATUS QcCacheBarrier(QC_CACHE* c, BOOLEAN disable) {
    AcquireCache(c); if (disable) c->Enabled = FALSE;
    c->Barrier = TRUE; Publish(c);
    while (c->State.DirtyBytes && NT_SUCCESS(c->State.LastError) && !c->Gone) {
        KeClearEvent(&c->Changed); KeSetEvent(&c->Wake, IO_NO_INCREMENT, FALSE);
        ReleaseCache(c); KeWaitForSingleObject(&c->Changed, Executive, KernelMode, FALSE, nullptr); AcquireCache(c);
    }
    auto status = c->Gone ? STATUS_DEVICE_NOT_CONNECTED : c->State.LastError;
    bool inject = c->InjectFault == 3; if (inject) c->InjectFault = 0;
    ReleaseCache(c);
    if (NT_SUCCESS(status)) status = inject ? STATUS_IO_DEVICE_ERROR : LowerIo(c, IRP_MJ_FLUSH_BUFFERS);
    AcquireCache(c);
    if (NT_SUCCESS(status)) ++c->State.Flushes;
    else if (NT_SUCCESS(c->State.LastError)) Fault(c, status);
    if (disable && NT_SUCCESS(status)) { ClearClean(c); ++c->Generation; }
    c->Barrier = FALSE; Publish(c); ReleaseCache(c);
    return status;
}
void QcCacheDestroy(QC_CACHE* c) {
    AcquireCache(c); c->Stop = TRUE; KeSetEvent(&c->Wake, IO_NO_INCREMENT, FALSE); ReleaseCache(c);
    for (auto& worker : c->Workers) if (worker.Thread) {
        ZwWaitForSingleObject(worker.Thread, FALSE, nullptr); ZwClose(worker.Thread); worker.Thread = nullptr;
    }
    if (c->State.DirtyBytes) DbgPrintEx(DPFLTR_IHVDRIVER_ID, DPFLTR_ERROR_LEVEL, "QueueCache removed with %llu dirty bytes; status 0x%08X\n", c->State.DirtyBytes, c->State.LastError);
    FreeSlots(c); // Surprise removal cannot promise volatile data survival.
}
static NTSTATUS Configure(QC_CACHE* c, ULONGLONG budget) {
    if (budget < (1ULL << 20) || budget > (128ULL << 30)) return STATUS_INVALID_PARAMETER;
    AcquireCache(c);
    if (c->Enabled || c->State.DirtyBytes || c->State.InFlightBytes || !NT_SUCCESS(c->State.LastError)) { ReleaseCache(c); return STATUS_DEVICE_BUSY; }
    const auto allocationFault = c->InjectFault == 6 || c->InjectFault == 7 ? c->InjectFault : 0;
    if (allocationFault) c->InjectFault = 0;
    FreeSlots(c);
    for (;;) {
        auto total = InterlockedCompareExchange64(&GlobalBudget, 0, 0);
        if (static_cast<ULONGLONG>(total) > GlobalLimit || budget > GlobalLimit - static_cast<ULONGLONG>(total)) { Publish(c); ReleaseCache(c); return STATUS_INSUFFICIENT_RESOURCES; }
        if (InterlockedCompareExchange64(&GlobalBudget, total + budget, total) == total) break;
    }
    c->State.BudgetBytes = budget;
    // Include both page-rounded descriptor and hash-index allocations in the hard budget.
    c->DrainCapacity = budget < (16ULL << 20) ? SlabBytes / 4 : MaxBatchBytes;
    auto stagingBytes = c->DrainCapacity * RTL_NUMBER_OF(c->Workers);
    auto n = static_cast<ULONG>((budget - 2 * PAGE_SIZE - stagingBytes) / (Chunk + sizeof(QC_SLOT) + sizeof(ULONG)));
    n = n / SlotsPerSlab * SlotsPerSlab;
    auto descriptors = (static_cast<SIZE_T>(n) * sizeof(QC_SLOT) + PAGE_SIZE - 1) & ~(static_cast<SIZE_T>(PAGE_SIZE) - 1);
    c->Slots = allocationFault == 6 ? nullptr : static_cast<QC_SLOT*>(ExAllocatePool2(POOL_FLAG_NON_PAGED, descriptors, Tag));
    if (!c->Slots) { FreeSlots(c); Publish(c); ReleaseCache(c); return STATUS_INSUFFICIENT_RESOURCES; }
    c->Capacity = n;
    auto indexBytes = (static_cast<SIZE_T>(n) * sizeof(ULONG) + PAGE_SIZE - 1) & ~(static_cast<SIZE_T>(PAGE_SIZE) - 1);
    c->Buckets = static_cast<ULONG*>(ExAllocatePool2(POOL_FLAG_NON_PAGED, indexBytes, Tag));
    if (!c->Buckets) { FreeSlots(c); Publish(c); ReleaseCache(c); return STATUS_INSUFFICIENT_RESOURCES; }
    RtlFillMemory(c->Buckets, indexBytes, 0xFF);
    c->DrainBuffer = static_cast<PUCHAR>(ExAllocatePool2(POOL_FLAG_NON_PAGED, stagingBytes, Tag));
    if (!c->DrainBuffer) { FreeSlots(c); Publish(c); ReleaseCache(c); return STATUS_INSUFFICIENT_RESOURCES; }
    for (ULONG i = 0; i < n; ++i) {
        c->Slots[i].Buffer = allocationFault == 7 && i == 2 * SlotsPerSlab ? nullptr :
            i % SlotsPerSlab == 0 ? static_cast<PUCHAR>(ExAllocatePool2(POOL_FLAG_NON_PAGED, SlabBytes, Tag)) : c->Slots[i - 1].Buffer + Chunk;
        if (!c->Slots[i].Buffer) { FreeSlots(c); Publish(c); ReleaseCache(c); return STATUS_INSUFFICIENT_RESOURCES; }
        c->Slots[i].FreeNext = i + 1 < n ? i + 1 : NoSlot;
    }
    c->FreeHead = 0;
    c->State.BudgetBytes = budget; c->State.ReservedBytes = stagingBytes + descriptors + indexBytes + static_cast<ULONGLONG>(n) * Chunk;
    c->State.PayloadCapacity = static_cast<ULONGLONG>(n) * Chunk;
    ++c->Generation;
    Publish(c); ReleaseCache(c); return STATUS_SUCCESS;
}
static NTSTATUS Control(QC_CACHE* c, PIRP irp, LONGLONG size) {
    auto stack = IoGetCurrentIrpStackLocation(irp);
    if (stack->Parameters.DeviceIoControl.InputBufferLength != sizeof(QC_COMMAND)) return STATUS_INVALID_PARAMETER;
    const auto command = *static_cast<QC_COMMAND*>(irp->AssociatedIrp.SystemBuffer);
    if (command.Version != 1 || command.Size != sizeof(command) || command.Reserved || size <= 0) return STATUS_INVALID_PARAMETER;
    if (command.Action == QcConfigure) return Configure(c, command.BudgetBytes);
    if (command.Action == QcRelease) {
        auto status = QcCacheBarrier(c, TRUE);
        if (!NT_SUCCESS(status)) return status;
        AcquireCache(c); FreeSlots(c); Publish(c); ReleaseCache(c); return STATUS_SUCCESS;
    }
    if (command.Action == QcFlush || command.Action == QcDisable) {
        AcquireCache(c); ++c->Diagnostics.ControlBarriers; Publish(c); ReleaseCache(c);
        return QcCacheBarrier(c, command.Action == QcDisable);
    }
    AcquireCache(c);
    NTSTATUS status = STATUS_SUCCESS;
    switch (command.Action) {
    case QcFlushPolicy:
        // Explicit operator choice, only at a clean disabled boundary. Never persists across boot.
        if (command.Value > 1 || command.BudgetBytes) status = STATUS_INVALID_PARAMETER;
        else if (c->Enabled || c->Count || !NT_SUCCESS(c->State.LastError)) status = STATUS_DEVICE_BUSY;
        else { c->UnsafeDefer = command.Value == 1; ++c->Generation; }
        break;
    case QcEnable:
        if (c->BlockedPlacement) status = STATUS_INVALID_DEVICE_STATE;
        else if (!c->Capacity || c->Suspended || c->Gone || (c->SectorBytes != 512 && c->SectorBytes != 4096)) status = STATUS_DEVICE_NOT_READY;
        else if (!NT_SUCCESS(c->State.LastError)) status = c->State.LastError;
        else { c->Enabled = TRUE; ++c->Generation; }
        break;
    case QcRetry:
        if (c->Gone || c->Suspended) { status = STATUS_DEVICE_NOT_READY; break; }
        ++c->Diagnostics.ControlBarriers;
        c->State.LastError = STATUS_SUCCESS; KeSetEvent(&c->Wake, IO_NO_INCREMENT, FALSE); break;
    case QcDropClean:
        // Release clean read/retained-write blocks on request. Dirty and in-flight payload
        // is untouched: this is not a flush and never discards data the disk has not taken.
        if (command.Value || command.BudgetBytes) status = STATUS_INVALID_PARAMETER;
        else ClearClean(c);
        break;
    case QcLabDelay:
        if (command.Value > 2000) status = STATUS_INVALID_PARAMETER;
        else c->DelayMs = static_cast<ULONG>(command.Value);
        break;
    case QcLabFault:
        if (command.Value > 7) status = STATUS_INVALID_PARAMETER;
        else c->InjectFault = static_cast<ULONG>(command.Value);
        break;
    default: status = STATUS_INVALID_DEVICE_REQUEST;
    }
    Publish(c); ReleaseCache(c);
    if (NT_SUCCESS(status) && command.Action == QcRetry) status = QcCacheBarrier(c, FALSE);
    return status;
}
static PUCHAR Map(PIRP irp) {
    if (irp->MdlAddress) return static_cast<PUCHAR>(MmGetSystemAddressForMdlSafe(irp->MdlAddress, NormalPagePriority | MdlMappingNoExecute));
    return static_cast<PUCHAR>(irp->AssociatedIrp.SystemBuffer); // Neither-I/O is not supported for cached data.
}
static NTSTATUS Write(QC_CACHE* c, PIRP irp) {
    auto stack = IoGetCurrentIrpStackLocation(irp);
    auto length = stack->Parameters.Write.Length; auto offset = stack->Parameters.Write.ByteOffset;
    AcquireCache(c);
    if (!NT_SUCCESS(c->State.LastError)) { auto error = c->State.LastError; ReleaseCache(c); return error; }
    auto needed = (static_cast<ULONGLONG>(length) + Chunk - 1) / Chunk;
    const bool writeThrough = (stack->Flags & SL_WRITE_THROUGH) != 0;
    if (writeThrough) { ++c->Diagnostics.WriteThroughWrites; Publish(c); }
    if (!c->Enabled && !c->State.DirtyBytes) { ReleaseCache(c); return OriginalIo(c, irp); }
    // Partial cache-block writes retain exact lower-device semantics behind a real barrier.
    // Never read/modify/write an entire 4 KiB block from an incomplete sector payload.
    if (!c->Enabled || (writeThrough && !c->UnsafeDefer) || needed > WriteLimit(c) || length == 0 ||
        offset.QuadPart % Chunk || length % Chunk) {
        ReleaseCache(c);
        auto status = QcCacheBarrier(c, FALSE);
        AcquireCache(c); InvalidateCleanRange(c, offset.QuadPart, length); Publish(c); ReleaseCache(c);
        return NT_SUCCESS(status) ? OriginalIo(c, irp) : status;
    }
    auto source = Map(irp);
    if (!source) { ReleaseCache(c); return STATUS_INSUFFICIENT_RESOURCES; }
    for (;;) {
        // Preflight the WHOLE request before modifying any payload. Recompute after
        // every wait because the drainer may have removed or pinned indexed slots.
        needed = 0; ULONG newDirty = 0;
        for (ULONG copied = 0; copied < length; copied += Chunk) {
            auto index = FindSlot(c, offset.QuadPart + copied);
            if (index == NoSlot || c->Slots[index].InFlight) ++needed;
            if (index == NoSlot || !c->Slots[index].Dirty || c->Slots[index].InFlight) ++newDirty;
        }
        // Evictions may remove clean blocks referenced by this request; recalculate
        // the preflight after every eviction before touching any payload.
        auto writeUsed = c->State.DirtyBytes / Chunk + c->CleanCount[0];
        if (needed > c->Capacity - c->Count && c->Options.Allocation == QcAutomatic && EvictOldest(c)) continue;
        if (c->Options.Allocation == QcFixed && (needed > c->Capacity - c->Count ||
             (c->Options.Allocation == QcFixed && writeUsed + newDirty > WriteLimit(c))) && Evict(c, 0)) continue;
        if ((needed <= c->Capacity - c->Count && c->State.DirtyBytes / Chunk + newDirty <= WriteLimit(c)) || !NT_SUCCESS(c->State.LastError) || c->Gone || irp->Cancel) break;
        c->WriterWaiting = TRUE; ++c->State.ThrottleWaits; Publish(c);
        KeClearEvent(&c->Changed); KeSetEvent(&c->Wake, IO_NO_INCREMENT, FALSE);
        LARGE_INTEGER interval; interval.QuadPart = -1000000; // Check cancellation at least every 100 ms.
        ReleaseCache(c); KeWaitForSingleObject(&c->Changed, Executive, KernelMode, FALSE, &interval); AcquireCache(c);
    }
    c->WriterWaiting = FALSE;
    if (irp->Cancel) { ReleaseCache(c); return STATUS_CANCELLED; }
    if (!NT_SUCCESS(c->State.LastError) || c->Gone) { auto error = c->Gone ? STATUS_DEVICE_NOT_CONNECTED : c->State.LastError; ReleaseCache(c); return error; }
    for (ULONG copied = 0; copied < length;) {
        auto index = FindSlot(c, offset.QuadPart + copied);
        if (index == NoSlot || c->Slots[index].InFlight) {
            index = AllocateSlot(c);
            auto slot = &c->Slots[index];
            slot->Length = Chunk; slot->Offset.QuadPart = offset.QuadPart + copied;
            slot->InFlight = FALSE; IndexSlot(c, index);
            c->State.DirtyBytes += Chunk;
        }
        auto slot = &c->Slots[index];
        if (!slot->Dirty) {
            Unlink(c, index); slot->Dirty = TRUE; slot->ReadClass = FALSE; slot->DirtySince = NowMs(); Link(c, index);
            c->State.DirtyBytes += Chunk;
        }
        RtlCopyMemory(slot->Buffer, source + copied, slot->Length);
        copied += slot->Length;
    }
    c->State.AcceptedBytes += length; c->LastWriteTime = NowMs();
    if (writeThrough) ++c->Diagnostics.DeferredWriteThroughWrites;
    c->State.PeakDirtyBytes = max(c->State.PeakDirtyBytes, c->State.DirtyBytes);
    Publish(c); KeSetEvent(&c->Wake, IO_NO_INCREMENT, FALSE); ReleaseCache(c);
    irp->IoStatus.Information = length; return STATUS_SUCCESS;
}
static NTSTATUS Read(QC_CACHE* c, PIRP irp) {
    auto stack = IoGetCurrentIrpStackLocation(irp);
    auto length = stack->Parameters.Read.Length; auto start = stack->Parameters.Read.ByteOffset.QuadPart;
    auto end = start + length;
    AcquireCache(c);
    if (!NT_SUCCESS(c->State.LastError)) { auto error = c->State.LastError; ReleaseCache(c); return error; }
    if (!c->Enabled || length == 0) { ReleaseCache(c); return OriginalIo(c, irp); }
    auto target = Map(irp);
    if (!target) { ReleaseCache(c); return STATUS_INSUFFICIENT_RESOURCES; }
    // Indexed block coverage avoids scanning the entire cache for each read block.
    // The newest entry wins, including when an older version is still in flight.
    auto covered = start;
    while (covered < end) {
        auto block = covered / Chunk * Chunk;
        if (FindSlot(c, block) == NoSlot) break;
        covered = block + Chunk;
    }
    NTSTATUS status = STATUS_SUCCESS;
    if (covered < end) status = OriginalIo(c, irp);
    else { irp->IoStatus.Information = length; }
    if (NT_SUCCESS(status) && irp->IoStatus.Information != length) status = STATUS_DEVICE_DATA_ERROR;
    if (NT_SUCCESS(status)) {
        for (auto block = start / Chunk * Chunk; block < end; block += Chunk) {
            auto index = FindSlot(c, block);
            if (index == NoSlot) {
                auto from = max(start, block); auto to = min(end, block + Chunk);
                c->ReadMissBytes += to - from;
                continue;
            }
            auto slot = &c->Slots[index];
            auto from = max(start, slot->Offset.QuadPart); auto to = min(end, slot->Offset.QuadPart + slot->Length);
            if (from < to) {
                RtlCopyMemory(target + (from - start), slot->Buffer + (from - slot->Offset.QuadPart), static_cast<SIZE_T>(to - from));
                c->ReadHitBytes += to - from; c->State.CacheReadBytes += to - from;
            }
        }
        // Complete the caller's entire buffer BEFORE admission/promotion can evict
        // any clean block which a later portion of this same read still needs.
        for (auto block = start / Chunk * Chunk; block < end; block += Chunk) {
            auto index = FindSlot(c, block);
            if (index != NoSlot) { TouchClean(c, index); continue; }
            if (block >= start && block + Chunk <= end && ReadRoom(c)) {
                index = AllocateSlot(c, false, true); auto fill = &c->Slots[index];
                fill->Offset.QuadPart = block; fill->Length = Chunk;
                RtlCopyMemory(fill->Buffer, target + (block - start), Chunk); IndexSlot(c, index);
            }
        }
    }
    Publish(c); ReleaseCache(c); return status;
}
// Only optimized, bounded, full-cache-block TRIM ranges are handled here. Any
// unfamiliar flags/parameters/alignment use the existing ordered drain/pass-through.
static bool TryTrim(QC_CACHE* c, PIRP irp, LONGLONG deviceBytes, NTSTATUS* result) {
    auto stack = IoGetCurrentIrpStackLocation(irp);
    auto length = stack->Parameters.DeviceIoControl.InputBufferLength;
    if (length < sizeof(DEVICE_MANAGE_DATA_SET_ATTRIBUTES) || !irp->AssociatedIrp.SystemBuffer) return false;
    auto input = static_cast<DEVICE_MANAGE_DATA_SET_ATTRIBUTES*>(irp->AssociatedIrp.SystemBuffer);
    if (input->Size != sizeof(*input) || input->Action != DeviceDsmAction_Trim ||
        (input->Flags & ~(DEVICE_DSM_FLAG_TRIM_NOT_FS_ALLOCATED | DEVICE_DSM_FLAG_TRIM_BYPASS_RZAT)) || input->ParameterBlockLength || input->ParameterBlockOffset ||
        input->DataSetRangesOffset < sizeof(*input) || input->DataSetRangesOffset % alignof(DEVICE_DATA_SET_RANGE) ||
        input->DataSetRangesOffset > length || input->DataSetRangesLength > length - input->DataSetRangesOffset ||
        !input->DataSetRangesLength || input->DataSetRangesLength % sizeof(DEVICE_DATA_SET_RANGE)) return false;
    auto count = input->DataSetRangesLength / sizeof(DEVICE_DATA_SET_RANGE);
    DEVICE_DATA_SET_RANGE ranges[128];
    if (count > RTL_NUMBER_OF(ranges)) return false;
    RtlCopyMemory(ranges, static_cast<PUCHAR>(irp->AssociatedIrp.SystemBuffer) + input->DataSetRangesOffset, input->DataSetRangesLength);
    for (ULONG i = 0; i < count; ++i) {
        auto range = ranges[i];
        if (range.StartingOffset < 0 || range.StartingOffset > deviceBytes || !range.LengthInBytes ||
            range.LengthInBytes > static_cast<ULONGLONG>(deviceBytes - range.StartingOffset) ||
            range.StartingOffset % Chunk || range.LengthInBytes % Chunk) return false;
        ULONG j = i;
        while (j && ranges[j - 1].StartingOffset > range.StartingOffset) { ranges[j] = ranges[j - 1]; --j; }
        ranges[j] = range;
    }
    // Merge overlap/adjacency so the binary-search membership check is unambiguous.
    ULONG merged = 0;
    for (ULONG i = 0; i < count; ++i) {
        if (merged && ranges[i].StartingOffset <= ranges[merged - 1].StartingOffset + static_cast<LONGLONG>(ranges[merged - 1].LengthInBytes)) {
            auto end = max(ranges[i].StartingOffset + static_cast<LONGLONG>(ranges[i].LengthInBytes),
                ranges[merged - 1].StartingOffset + static_cast<LONGLONG>(ranges[merged - 1].LengthInBytes));
            ranges[merged - 1].LengthInBytes = end - ranges[merged - 1].StartingOffset;
        } else ranges[merged++] = ranges[i];
    }
    AcquireCache(c);
    c->TrimPaused = TRUE;
    // The serialized request worker admits no subsequent writes while this request
    // runs. Wait only for the already-issued lower write, NOT all queued payload.
    while (c->State.InFlightBytes && !c->Gone) {
        KeClearEvent(&c->Changed); ReleaseCache(c);
        KeWaitForSingleObject(&c->Changed, Executive, KernelMode, FALSE, nullptr); AcquireCache(c);
    }
    auto status = c->Gone ? STATUS_DEVICE_NOT_CONNECTED : c->State.LastError;
    ReleaseCache(c);
    if (NT_SUCCESS(status)) status = OriginalIo(c, irp);
    AcquireCache(c);
    if (NT_SUCCESS(status)) {
        ++c->TrimRequests;
        for (ULONG i = 0; i < c->Capacity; ++i) {
            if (!c->Slots[i].Length) continue;
            auto offset = c->Slots[i].Offset.QuadPart;
            ULONG low = 0, high = merged;
            while (low < high) { auto mid = low + (high - low) / 2; if (ranges[mid].StartingOffset <= offset) low = mid + 1; else high = mid; }
            if (low && offset - ranges[low - 1].StartingOffset < static_cast<LONGLONG>(ranges[low - 1].LengthInBytes)) {
                if (c->Slots[i].Dirty) { c->DiscardedBytes += c->Slots[i].Length; c->State.DirtyBytes -= c->Slots[i].Length; }
                RetireSlot(c, i);
            }
        }
    }
    c->TrimPaused = FALSE; Publish(c);
    KeSetEvent(&c->Changed, IO_NO_INCREMENT, FALSE); KeSetEvent(&c->Wake, IO_NO_INCREMENT, FALSE);
    ReleaseCache(c); *result = status; return true;
}
NTSTATUS QcCacheProcess(QC_CACHE* c, PIRP irp, LONGLONG deviceBytes) {
    auto stack = IoGetCurrentIrpStackLocation(irp);
    irp->IoStatus.Information = 0;
    AcquireCache(c); c->State.DeviceBytes = deviceBytes; Publish(c); bool suspended = c->Suspended != FALSE; ReleaseCache(c);
    if (c->Gone) return STATUS_DEVICE_NOT_CONNECTED;
    if (stack->MajorFunction == IRP_MJ_DEVICE_CONTROL && stack->Parameters.DeviceIoControl.IoControlCode == IOCTL_QCACHE_OPTIONS_V1) {
        if (stack->Parameters.DeviceIoControl.InputBufferLength != sizeof(QC_OPTIONS)) return STATUS_INVALID_PARAMETER;
        const auto options = *static_cast<QC_OPTIONS*>(irp->AssociatedIrp.SystemBuffer);
        if (!QcValidOptions(options)) return STATUS_INVALID_PARAMETER;
        AcquireCache(c);
        if (c->Enabled || c->State.DirtyBytes || !NT_SUCCESS(c->State.LastError)) { ReleaseCache(c); return STATUS_DEVICE_BUSY; }
        ClearClean(c); c->Options = options; ++c->Generation; Publish(c); ReleaseCache(c); return STATUS_SUCCESS;
    }
    if (stack->MajorFunction == IRP_MJ_DEVICE_CONTROL && stack->Parameters.DeviceIoControl.IoControlCode == IOCTL_QCACHE_CONTROL_V1)
        return Control(c, irp, deviceBytes);
    if (stack->MajorFunction == IRP_MJ_DEVICE_CONTROL || stack->MajorFunction == IRP_MJ_INTERNAL_DEVICE_CONTROL) {
        auto code = stack->Parameters.DeviceIoControl.IoControlCode;
        // Neither-I/O and raw controller pass-through may contain uncaptured user pointers.
        // They cannot safely be forwarded from a system worker in another process context.
        if ((code & 3) == METHOD_NEITHER || DEVICE_TYPE_FROM_CTL_CODE(code) == FILE_DEVICE_CONTROLLER)
            return STATUS_NOT_SUPPORTED;
    }
    if (stack->MajorFunction == IRP_MJ_SHUTDOWN) {
        AcquireCache(c); ++c->Diagnostics.ShutdownBarriers; Publish(c); ReleaseCache(c);
        auto status = QcCacheBarrier(c, TRUE);
        AcquireCache(c); c->Suspended = TRUE; Publish(c); ReleaseCache(c);
        return NT_SUCCESS(status) ? OriginalIo(c, irp) : status;
    }
    if (stack->MajorFunction == IRP_MJ_POWER) {
        if (stack->Parameters.Power.State.DeviceState != PowerDeviceD0) {
            AcquireCache(c); ++c->Diagnostics.PowerBarriers; Publish(c); ReleaseCache(c);
            auto status = QcCacheBarrier(c, TRUE); if (!NT_SUCCESS(status)) return status;
            AcquireCache(c); c->Suspended = TRUE; Publish(c); ReleaseCache(c);
        }
        auto status = OriginalIo(c, irp);
        if (NT_SUCCESS(status) && stack->Parameters.Power.State.DeviceState == PowerDeviceD0) {
            AcquireCache(c); c->Suspended = FALSE; Publish(c); ReleaseCache(c);
        }
        return status;
    }
    if (suspended) return STATUS_DEVICE_NOT_READY;
    if (stack->MajorFunction == IRP_MJ_DEVICE_CONTROL &&
        stack->Parameters.DeviceIoControl.IoControlCode == IOCTL_STORAGE_MANAGE_DATA_SET_ATTRIBUTES) {
        NTSTATUS trimStatus;
        if (TryTrim(c, irp, deviceBytes, &trimStatus)) return trimStatus;
    }
    if (stack->MajorFunction == IRP_MJ_READ || stack->MajorFunction == IRP_MJ_WRITE) {
        auto length = stack->Parameters.Read.Length; auto offset = stack->Parameters.Read.ByteOffset.QuadPart;
        if (deviceBytes <= 0 || offset < 0 || offset > deviceBytes || length > static_cast<ULONGLONG>(deviceBytes - offset))
            return STATUS_INVALID_PARAMETER;
        if (c->SectorBytes && (offset % c->SectorBytes || length % c->SectorBytes)) return STATUS_INVALID_PARAMETER;
        return stack->MajorFunction == IRP_MJ_WRITE ? Write(c, irp) : Read(c, irp);
    }
    if (stack->MajorFunction == IRP_MJ_FLUSH_BUFFERS) {
        AcquireCache(c);
        ++c->Diagnostics.ApplicationFlushes;
        const bool defer = c->Enabled && c->UnsafeDefer;
        const auto error = c->State.LastError;
        if (defer && NT_SUCCESS(error)) ++c->Diagnostics.DeferredFlushes;
        Publish(c); ReleaseCache(c);
        // Unsafe policy changes only application/OS flush semantics. Never hide an existing I/O error.
        if (defer) return error;
        return QcCacheBarrier(c, FALSE);
    }
    // A query cannot modify media, so it must neither drain nor invalidate cached data.
    // Windows' storage service, NTFS and monitoring tools poll read-only controls (disk
    // geometry/layout, media presence, SMART, IOCTL_STORAGE_FIRMWARE_GET_INFO) every few
    // seconds; treating each one as "unknown, therefore possibly destructive" emptied the
    // whole clean cache within seconds on an otherwise idle disk. Only newly admitted
    // data, real modifications and explicit pause/remove/reconfigure may displace it.
    if (stack->MajorFunction == IRP_MJ_DEVICE_CONTROL || stack->MajorFunction == IRP_MJ_INTERNAL_DEVICE_CONTROL) {
        if (!QcMayChangeMedia(stack->Parameters.DeviceIoControl.IoControlCode)) return OriginalIo(c, irp);
    }
    // Modifying and unclassified controls (including unsupported TRIM) must not overtake accepted dirty writes.
    AcquireCache(c); bool dirty = c->State.DirtyBytes != 0;
    bool resident = c->CleanCount[0] != 0 || c->CleanCount[1] != 0;
    auto error = c->State.LastError; ReleaseCache(c);
    if (!NT_SUCCESS(error)) return error;
    if (dirty || resident || stack->MajorFunction == IRP_MJ_PNP) {
        AcquireCache(c); ++c->Diagnostics.OtherBarriers;
        c->Diagnostics.LastBarrierCode = stack->MajorFunction == IRP_MJ_DEVICE_CONTROL || stack->MajorFunction == IRP_MJ_INTERNAL_DEVICE_CONTROL ? stack->Parameters.DeviceIoControl.IoControlCode : stack->MajorFunction;
        Publish(c); ReleaseCache(c);
    }
    auto status = dirty || stack->MajorFunction == IRP_MJ_PNP ? QcCacheBarrier(c, stack->MajorFunction == IRP_MJ_PNP) : STATUS_SUCCESS;
    // Unknown commands can modify media (including unsupported TRIM shapes).
    // Invalidate AFTER the drain, which may itself have retained clean blocks.
    AcquireCache(c); ClearClean(c); Publish(c); ReleaseCache(c);
    return NT_SUCCESS(status) ? OriginalIo(c, irp) : status;
}
