// SPDX-License-Identifier: MIT
// RAM disk lifetime: allocation, accounting with the shared budget, references and the
// Direct-access registration with the volume filter.
#include "provider.h"

DISK* ReferenceDisk(ADAPTER* adapter, ULONG slot, bool published)
{
    if (slot >= QcRamMaxDisks) return nullptr;
    KIRQL irql; KeAcquireSpinLock(&adapter->TableLock, &irql);
    auto disk = adapter->Disks[slot];
    if (disk && ((published && !(ReadNoFence(&disk->Store.Flags) & QcRamPublished)) || !ExAcquireRundownProtection(&disk->Users)))
        disk = nullptr;
    KeReleaseSpinLock(&adapter->TableLock, irql);
    return disk;
}
// A plain count plus an idle event let removal free the disk between the last decrement and
// that thread's signal. The rundown release is the last access to the disk.
void DereferenceDisk(DISK* disk) { ExReleaseRundownProtection(&disk->Users); }
void WaitReferences(DISK* disk) { ExWaitForRundownProtectionRelease(&disk->Users); }
// Freeze and read-only close the store's write gate first (QcRamStoreBeginWrite); this
// waits for writes already admitted on either path, so the caller sees a stable view.
void DrainWrites(DISK* disk)
{
    LARGE_INTEGER interval{}; interval.QuadPart = -1000; // 100 us
    while (InterlockedCompareExchange(&disk->Store.ActiveWrites, 0, 0))
        KeDelayExecutionThread(KernelMode, FALSE, &interval);
}

static NTSTATUS SyncCall(DISK* disk, ULONG code, PVOID buffer, ULONG size)
{
    KEVENT done; KeInitializeEvent(&done, NotificationEvent, FALSE);
    IO_STATUS_BLOCK result{};
    auto irp = IoBuildDeviceIoControlRequest(code, disk->BudgetDevice, buffer, size, buffer, size, TRUE, &done, &result);
    if (!irp) return STATUS_INSUFFICIENT_RESOURCES;
    auto status = IoCallDriver(disk->BudgetDevice, irp);
    if (status == STATUS_PENDING) KeWaitForSingleObject(&done, Executive, KernelMode, FALSE, nullptr);
    return result.Status;
}
static NTSTATUS CallCompleted(PDEVICE_OBJECT, PIRP irp, PVOID context)
{
    auto call = static_cast<KERNEL_CALL*>(context);
    call->Status = irp->IoStatus.Status;
    KeSetEvent(&call->Done, IO_NO_INCREMENT, FALSE);
    return STATUS_MORE_PROCESSING_REQUIRED; // The owner frees this IRP.
}
// IoAllocateIrp has no originating thread association: the prepared request may safely
// outlive the creating service callback. IoBuildDeviceIoControlRequest would instead leave
// it on that callback thread's cancellable IRP list.
static NTSTATUS PrepareCall(KERNEL_CALL* call, PDEVICE_OBJECT device, ULONG code, PVOID buffer, ULONG size)
{
    call->Irp = IoAllocateIrp(device->StackSize, FALSE);
    if (!call->Irp) return STATUS_INSUFFICIENT_RESOURCES;
    KeInitializeEvent(&call->Done, NotificationEvent, FALSE);
    call->Irp->RequestorMode = KernelMode;
    call->Irp->AssociatedIrp.SystemBuffer = buffer;
    auto stack = IoGetNextIrpStackLocation(call->Irp);
    stack->MajorFunction = IRP_MJ_INTERNAL_DEVICE_CONTROL;
    stack->Parameters.DeviceIoControl.IoControlCode = code;
    stack->Parameters.DeviceIoControl.InputBufferLength = size;
    stack->Parameters.DeviceIoControl.OutputBufferLength = size;
    IoSetCompletionRoutine(call->Irp, CallCompleted, call, TRUE, TRUE, TRUE);
    return STATUS_SUCCESS;
}
static NTSTATUS SendPrepared(DISK* disk, KERNEL_CALL* call)
{
    NT_ASSERT(call->Irp);
    call->Status = STATUS_PENDING;
    const auto status = IoCallDriver(disk->BudgetDevice, call->Irp);
    if (status == STATUS_PENDING) KeWaitForSingleObject(&call->Done, Executive, KernelMode, FALSE, nullptr);
    return call->Status;
}

NTSTATUS StartupSession(GUID* epoch, ULONGLONG* transitions)
{
    UNICODE_STRING name = RTL_CONSTANT_STRING(L"\\Device\\QueueCacheBudget");
    DISK request{};
    auto status = IoGetDeviceObjectPointer(&name, FILE_READ_DATA | FILE_WRITE_DATA, &request.BudgetFile, &request.BudgetDevice);
    if (!NT_SUCCESS(status)) return status;
    request.Reservation.Size = sizeof(request.Reservation); request.Reservation.Version = 1; request.Reservation.Owner = Driver;
    request.Reservation.Action = QcBudgetStartupSession;
    status = SyncCall(&request, IOCTL_QCACHE_KERNEL_BUDGET, &request.Reservation, sizeof(request.Reservation));
    if (NT_SUCCESS(status)) { *epoch = request.Reservation.Token; *transitions = request.Reservation.Bytes; }
    ObDereferenceObject(request.BudgetFile); return status;
}

// Offers the store to the volume filter. Failure (for example an older filter until the
// next restart) leaves the disk on the standard SCSI path; the reply flag reports it.
void RegisterView(ADAPTER* adapter, DISK* disk)
{
    auto& view = disk->View;
    view.Size = sizeof(view); view.Version = QcRamViewVersion; view.Owner = Driver; view.Resource = disk->Resource;
    view.Store = &disk->Store; view.LargeCopy = LargeCopy; view.LargeCopyContext = adapter;
    view.Action = QcRamViewRegister;
    if (!NT_SUCCESS(PrepareCall(&disk->Unregister, disk->BudgetDevice, IOCTL_QCACHE_KERNEL_RAM_VIEW, &view, sizeof(view)))) return;
    if (NT_SUCCESS(SyncCall(disk, IOCTL_QCACHE_KERNEL_RAM_VIEW, &view, sizeof(view))) && view.View)
        disk->ViewRegistered = TRUE;
    view.Action = QcRamViewUnregister;
}

void FreeDisk(DISK* disk)
{
    // Withdraw Direct access first: the filter returns only after its in-flight requests
    // left the store, and never touches it again.
    if (disk->ViewRegistered)
    {
        const auto status = SendPrepared(disk, &disk->Unregister);
        NT_ASSERT(NT_SUCCESS(status)); UNREFERENCED_PARAMETER(status);
    }
    auto& store = disk->Store;
    if (store.Slabs)
    {
        for (ULONG i = 0; i < store.SlabCount; ++i)
            if (store.Slabs[i].Mdl) QcFreeLockedPages(store.Slabs[i].Mdl, store.Slabs[i].Bytes, true);
        ExFreePoolWithTag(store.Slabs, Tag);
    }
    // Accounting survives until the last reference and last physical page are gone.
    if (disk->Reservation.Bytes && disk->BudgetFile)
    {
        disk->Reservation.Action = QcBudgetRelease;
        const auto status = SendPrepared(disk, &disk->Release);
        NT_ASSERT(NT_SUCCESS(status)); UNREFERENCED_PARAMETER(status);
    }
    if (disk->Release.Irp) IoFreeIrp(disk->Release.Irp);
    if (disk->Unregister.Irp) IoFreeIrp(disk->Unregister.Irp);
    if (disk->BudgetFile) ObDereferenceObject(disk->BudgetFile);
    RtlSecureZeroMemory(disk, sizeof(*disk)); ExFreePoolWithTag(disk, Tag);
}

NTSTATUS AllocateDisk(ADAPTER* adapter, const QC_RAM_REQUEST* request, PIRP irp, DISK** result, ULONG failAfterSlabs)
{
    if (request->Capacity < (16ULL << 20) || request->Capacity > (128ULL << 30) || request->Capacity % (1 << 20) ||
        (request->SectorBytes != 512 && request->SectorBytes != 4096) || (request->Flags & ~QcRamDirect) ||
        IsEqualGUID(request->Resource, EmptyGuid)) return STATUS_INVALID_PARAMETER;
    if (failAfterSlabs > (request->Capacity + QcRamSlabBytes - 1) / QcRamSlabBytes) return STATUS_INVALID_PARAMETER;
    auto disk = static_cast<DISK*>(ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(DISK), Tag));
    if (!disk) return STATUS_INSUFFICIENT_RESOURCES;
    ExInitializeRundownProtection(&disk->Users);
    auto& store = disk->Store;
    store.Size = sizeof(store); store.Version = QcRamStoreVersion;
    store.Capacity = request->Capacity; store.SectorBytes = request->SectorBytes;
    store.SlabCount = static_cast<ULONG>((store.Capacity + QcRamSlabBytes - 1) / QcRamSlabBytes);
    disk->Resource = request->Resource; disk->Epoch = adapter->Epoch; disk->Creation = ++adapter->NextCreation;
    disk->ReservedBytes = store.Capacity + QcLockedPageMetadataBytes(store.Capacity, store.SlabCount) +
        store.SlabCount * sizeof(QC_RAM_SLAB) + sizeof(DISK) + QcRamTransferBytes + 4096;
    UNICODE_STRING name = RTL_CONSTANT_STRING(L"\\Device\\QueueCacheBudget");
    auto status = IoGetDeviceObjectPointer(&name, FILE_READ_DATA | FILE_WRITE_DATA, &disk->BudgetFile, &disk->BudgetDevice);
    if (!NT_SUCCESS(status)) { FreeDisk(disk); return status; }
    status = PrepareCall(&disk->Release, disk->BudgetDevice, IOCTL_QCACHE_KERNEL_BUDGET, &disk->Reservation, sizeof(disk->Reservation));
    if (!NT_SUCCESS(status)) { FreeDisk(disk); return status; }
    const auto calls = (request->Flags & QcRamDirect) ? 2UL : 1UL; // Release, and Unregister for Direct access.
    disk->ReservedBytes += calls * IoSizeOfIrp(disk->BudgetDevice->StackSize);
    disk->Reservation.Size = sizeof(disk->Reservation); disk->Reservation.Version = 1;
    disk->Reservation.Owner = Driver; disk->Reservation.Resource = disk->Resource;
    disk->Reservation.Bytes = disk->ReservedBytes; disk->Reservation.Action = QcBudgetReserve;
    status = SyncCall(disk, IOCTL_QCACHE_KERNEL_BUDGET, &disk->Reservation, sizeof(disk->Reservation));
    if (!NT_SUCCESS(status)) { disk->Reservation.Bytes = 0; FreeDisk(disk); return status; }
    store.Slabs = static_cast<QC_RAM_SLAB*>(ExAllocatePool2(POOL_FLAG_NON_PAGED, store.SlabCount * sizeof(QC_RAM_SLAB), Tag));
    if (!store.Slabs) { FreeDisk(disk); return STATUS_INSUFFICIENT_RESOURCES; }
    for (ULONG i = 0; i < store.SlabCount; ++i)
    {
        if (irp->Cancel) { FreeDisk(disk); return STATUS_CANCELLED; }
        auto slab = &store.Slabs[i];
        slab->Length = static_cast<ULONG>(min(static_cast<ULONGLONG>(QcRamSlabBytes), store.Capacity - static_cast<ULONGLONG>(i) * QcRamSlabBytes));
        slab->Bytes = QcAllocateLockedPages(slab->Length, &slab->Mdl);
        if (!slab->Bytes) { FreeDisk(disk); return STATUS_INSUFFICIENT_RESOURCES; }
        RtlZeroMemory(slab->Bytes, slab->Length);
        if (failAfterSlabs && i + 1 == failAfterSlabs)
        {
            // Proof changes only after actual page allocation/zeroing reaches the requested boundary.
            ++adapter->InjectedAllocationFailures; adapter->LastAllocationFailureResource = disk->Resource;
            adapter->LastAllocationFailureSlabs = i + 1;
            FreeDisk(disk); return STATUS_INSUFFICIENT_RESOURCES;
        }
    }
    *result = disk; return STATUS_SUCCESS;
}
