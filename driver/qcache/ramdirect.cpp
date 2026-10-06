// SPDX-License-Identifier: MIT
// Direct access to QueueCache RAM disks (see ramdirect.h).
#include "ramdirect.h"
#include "devicecontrol.h"

// Stable public layouts (ntddvol.h, winioctl.h), declared here to keep the include set small.
struct QC_DISK_EXTENT { ULONG DiskNumber; LARGE_INTEGER StartingOffset, ExtentLength; };
struct QC_VOLUME_DISK_EXTENTS { ULONG NumberOfDiskExtents; QC_DISK_EXTENT Extents[1]; };
constexpr ULONG QcGetVolumeDiskExtents = QcVolumeControl(0, FILE_ANY_ACCESS);
constexpr ULONG QcGetGptAttributes = QcVolumeControl(14, FILE_ANY_ACCESS);
constexpr ULONGLONG QcGptReadOnly = 0x1000000000000000ULL; // GPT_BASIC_DATA_ATTRIBUTE_READ_ONLY

// One RAM disk offered by the provider. Freed after its registration and binding are gone.
struct QC_RAM_VIEW
{
    LIST_ENTRY Link;
    volatile LONG References;   // Registration, binding and in-progress binds.
    PDRIVER_OBJECT Owner;       // The provider; also owns the RAM disk's PDO.
    GUID Resource;
    QC_RAM_STORE* Store;
    QC_RAM_LARGE_COPY* LargeCopy;
    QC_RAM_QUEUE_COPY* QueueCopy;
    PVOID CopyContext;
    QC_RAM_BINDING* Binding;    // ViewLock
    BOOLEAN Registered;         // ViewLock; the store may be used only while set.
};
// A Direct request copied on a provider worker (QcRamDirectOutcome::Pending). It holds a
// Rundown reference, the caller's remove lock and, for a write, its admission through the
// store's gate, until CopyCompleted.
struct QC_DIRECT_COPY
{
    QC_RAM_ASYNC_COPY Copy;
    QC_RAM_BINDING* Binding;
    PIRP Irp;
    PIO_REMOVE_LOCK RemoveLock;
};
static FAST_MUTEX ViewLock;
static LIST_ENTRY Views;
static NPAGED_LOOKASIDE_LIST CopyItems;
static ULONG Processors;
static constexpr ULONG Tag = 'vRCQ';
static constexpr ULONG LargeCopyBytes = 512 * 1024; // The provider splits copies from 2 x 256 KiB.
// Smaller copies cost less than handing them to a worker (lab VM, RND4K write: 336k IOPS
// copied inline vs 297k queued at Q32T1, 1.09M vs 393k at Q8T4).
static constexpr ULONG QueueMinBytes = 256 * 1024;
static const UCHAR BitLockerSignature[8] = {'-', 'F', 'V', 'E', '-', 'F', 'S', '-'};

void QcRamDirectInitialize()
{
    ExInitializeFastMutex(&ViewLock);
    InitializeListHead(&Views);
    ExInitializeNPagedLookasideList(&CopyItems, nullptr, nullptr, POOL_NX_ALLOCATION, sizeof(QC_DIRECT_COPY), Tag, 0);
    Processors = KeQueryActiveProcessorCountEx(ALL_PROCESSOR_GROUPS);
}
void QcRamDirectDestroy()
{
    ExDeleteNPagedLookasideList(&CopyItems);
}
void QcRamDirectInitialize(QC_RAM_BINDING* binding)
{
    ExInitializeRundownProtection(&binding->Rundown);
    ExWaitForRundownProtectionRelease(&binding->Rundown); // Not bound: every acquire fails.
}
static void Release(QC_RAM_VIEW* view)
{
    if (InterlockedDecrement(&view->References) == 0)
    {
        ObDereferenceObject(view->Owner);
        ExFreePoolWithTag(view, Tag);
    }
}
static void Note(QC_RAM_BINDING* binding, ULONG reason, ULONG detail = 0)
{
    if (InterlockedCompareExchange(&binding->Reason, static_cast<LONG>(reason), 0) == 0)
        binding->Detail = detail;
}
// Writes only; reads stay Direct. Waits for Direct writes already past their check.
static void EndWrites(QC_RAM_BINDING* binding, ULONG reason)
{
    if (!(InterlockedAnd(&binding->Access, ~static_cast<LONG>(QcRamDirectWrites)) & QcRamDirectWrites))
        return;
    Note(binding, reason);
    LARGE_INTEGER interval = {};
    interval.QuadPart = -10; // 1 us
    while (ReadNoFence(&binding->Writing))
    {
        if (KeGetCurrentIrql() <= APC_LEVEL)
            KeDelayExecutionThread(KernelMode, FALSE, &interval);
        else
            YieldProcessor();
    }
}
// Caller holds ViewLock. Returns once no Direct request uses the store; the binding never
// touches it again.
static void EndLocked(QC_RAM_BINDING* binding, ULONG reason, ULONG detail = 0)
{
    InterlockedExchange(&binding->Access, 0);
    Note(binding, reason, detail);
    auto view = binding->View;
    if (!view)
        return;
    ExWaitForRundownProtectionRelease(&binding->Rundown);
    view->Binding = nullptr;
    binding->View = nullptr;
    binding->Store = nullptr;
    binding->LargeCopy = nullptr;
    binding->QueueCopy = nullptr;
    binding->CopyContext = nullptr;
    Release(view);
}
void QcRamDirectEnd(QC_RAM_BINDING* binding, ULONG reason)
{
    ExAcquireFastMutex(&ViewLock);
    EndLocked(binding, reason);
    ExReleaseFastMutex(&ViewLock);
}

// ---- Hot path ----
static QcRamDirectOutcome Decline(QC_RAM_BINDING* binding)
{
    InterlockedIncrement64(&binding->Declined);
    return QcRamDirectOutcome::Standard;
}
// In the submitter's thread: split a large copy across the provider's workers unless they are
// busy with queued copies.
static void Copy(QC_RAM_BINDING* binding, QC_RAM_STORE* store, ULONGLONG at, PUCHAR buffer, ULONG length, bool write)
{
    const bool large = length >= QueueMinBytes;
    if (large)
        InterlockedIncrement(&binding->Copying);
    if (length >= LargeCopyBytes && binding->LargeCopy && !ReadNoFence(&binding->Queued))
        binding->LargeCopy(binding->CopyContext, store, at, buffer, length, write);
    else
        QcRamStoreCopy(store, at, buffer, length, write);
    if (large)
        InterlockedDecrement(&binding->Copying);
}
// A copied request: close its write admission, count it and set its status.
static void Served(QC_RAM_BINDING* binding, QC_RAM_STORE* store, PIRP irp, ULONG length, bool write)
{
    if (write)
    {
        QcRamStoreEndWrite(store);
        InterlockedDecrement(&binding->Writing);
        InterlockedIncrement64(&binding->WriteRequests);
        InterlockedAdd64(&binding->WriteBytes, length);
    }
    else
    {
        InterlockedIncrement64(&binding->ReadRequests);
        InterlockedAdd64(&binding->ReadBytes, length);
    }
    irp->IoStatus.Status = STATUS_SUCCESS;
    irp->IoStatus.Information = length;
}
// Provider worker, PASSIVE_LEVEL.
static void CopyCompleted(QC_RAM_ASYNC_COPY* copy)
{
    auto item = CONTAINING_RECORD(copy, QC_DIRECT_COPY, Copy);
    const auto binding = item->Binding;
    const auto irp = item->Irp;
    const auto removeLock = item->RemoveLock;
    Served(binding, copy->Store, irp, copy->Bytes, copy->Write != FALSE);
    ExFreeToNPagedLookasideList(&CopyItems, item);
    InterlockedDecrement(&binding->Queued);
    ExReleaseRundownProtection(&binding->Rundown); // The store may be withdrawn from here on.
    IoCompleteRequest(irp, IO_NO_INCREMENT);
    IoReleaseRemoveLock(removeLock, irp); // Last: the volume may be removed afterwards.
}
// Hands a large copy to a provider worker while the submitter keeps others in flight
// (QcOffload) and a processor is free: the submitter, other submitters copying inline and
// queued copies each keep one busy. The submitter then issues its next request at once.
// False: copy inline.
static bool Queue(QC_RAM_BINDING* binding, QC_RAM_STORE* store, PIRP irp, PIO_REMOVE_LOCK removeLock,
    ULONGLONG at, PUCHAR buffer, ULONG length, bool write)
{
    if (!binding->QueueCopy || length < QueueMinBytes)
        return false;
    const auto queued = ReadNoFence(&binding->Queued);
    if (1 + static_cast<ULONG>(ReadNoFence(&binding->Copying) + queued) >= Processors ||
        !QcOffload(&binding->Offload, queued))
        return false;
    auto item = static_cast<QC_DIRECT_COPY*>(ExAllocateFromNPagedLookasideList(&CopyItems));
    if (!item)
        return false;
    item->Copy.Store = store;
    item->Copy.Offset = at;
    item->Copy.Buffer = buffer;
    item->Copy.Bytes = length;
    item->Copy.Write = write;
    item->Copy.Completed = CopyCompleted;
    item->Binding = binding;
    item->Irp = irp;
    item->RemoveLock = removeLock;
    InterlockedIncrement(&binding->Queued);
    IoMarkIrpPending(irp); // Before queueing: the worker may complete it at once.
    binding->QueueCopy(binding->CopyContext, &item->Copy);
    return true;
}
// BitLocker replaces the volume's boot sector name on disk (below fvevol) when it starts.
static bool SignatureIntact(const QC_RAM_BINDING* binding, const QC_RAM_STORE* store)
{
    UCHAR now[8];
    QcRamStoreCopy(store, binding->Offset + 3, now, sizeof(now), false);
    return RtlEqualMemory(now, binding->Signature, sizeof(now));
}
QcRamDirectOutcome QcRamDirectTransfer(QC_RAM_BINDING* binding, PIRP irp, PIO_REMOVE_LOCK removeLock)
{
    const auto access = ReadNoFence(&binding->Access);
    if (!access)
        return QcRamDirectOutcome::Standard;
    const auto stack = IoGetCurrentIrpStackLocation(irp);
    const bool write = stack->MajorFunction == IRP_MJ_WRITE;
    const auto offset = stack->Parameters.Read.ByteOffset.QuadPart;
    const ULONG length = stack->Parameters.Read.Length;
    const auto mdl = irp->MdlAddress;
    if ((write && !(access & QcRamDirectWrites)) || !length || !mdl || mdl->Next || MmGetMdlByteCount(mdl) < length ||
        offset < 0 || static_cast<ULONGLONG>(offset) > binding->Length || length > binding->Length - static_cast<ULONGLONG>(offset))
        return Decline(binding);
    if (!ExAcquireRundownProtection(&binding->Rundown))
        return Decline(binding);
    auto store = binding->Store;
    const auto at = binding->Offset + static_cast<ULONGLONG>(offset);
    PUCHAR buffer = nullptr;
    bool admitted = false;
    if (!SignatureIntact(binding, store))
    {
        InterlockedExchange(&binding->Access, 0); // Ended for good; the next bind or removal finishes it.
        Note(binding, QcRamDirectBitLocker);
    }
    else if (QcRamStoreBounds(store, at, length))
    {
        const auto priority = static_cast<ULONG>((irp->Flags & IRP_PAGING_IO) ? HighPagePriority : NormalPagePriority) | MdlMappingNoExecute;
        buffer = static_cast<PUCHAR>(MmGetSystemAddressForMdlSafe(mdl, priority));
        admitted = buffer && !write;
        if (buffer && write)
        {
            InterlockedIncrement(&binding->Writing); // Before re-checking Access: see EndWrites.
            admitted = (InterlockedOr(&binding->Access, 0) & QcRamDirectWrites) && QcRamStoreBeginWrite(store) == QcRamAdmission::Admitted;
            if (admitted)
                QcRamStoreChanged(store);
            else
                InterlockedDecrement(&binding->Writing);
        }
    }
    if (!admitted)
    {
        ExReleaseRundownProtection(&binding->Rundown);
        return Decline(binding); // Read-only, frozen or unmappable: the standard path answers.
    }
    if (Queue(binding, store, irp, removeLock, at, buffer, length, write))
        return QcRamDirectOutcome::Pending; // CopyCompleted finishes it.
    Copy(binding, store, at, buffer, length, write);
    Served(binding, store, irp, length, write);
    ExReleaseRundownProtection(&binding->Rundown);
    return QcRamDirectOutcome::Completed;
}
void QcRamDirectObserveControl(QC_RAM_BINDING* binding, ULONG code)
{
    if (code == QcVolsnapFlushAndHoldWrites)
    {
        // A shadow copy is being taken: its copy-on-write lives below this filter.
        binding->SnapshotSeen = TRUE;
        EndWrites(binding, QcRamDirectSnapshot);
        return;
    }
    if (!ReadNoFence(&binding->Access) || QcRamDirectHarmlessControl(code))
        return;
    if (KeGetCurrentIrql() > APC_LEVEL)
    {
        InterlockedExchange(&binding->Access, 0);
        Note(binding, QcRamDirectControl, code);
        return;
    }
    ExAcquireFastMutex(&ViewLock);
    EndLocked(binding, QcRamDirectControl, code);
    ExReleaseFastMutex(&ViewLock);
}

// ---- Binding (PASSIVE_LEVEL) ----
static bool BaseNameIs(const UNICODE_STRING& driverName, const wchar_t* expected)
{
    auto start = static_cast<USHORT>(driverName.Length / sizeof(WCHAR));
    while (start && driverName.Buffer[start - 1] != L'\\')
        --start;
    UNICODE_STRING base = {static_cast<USHORT>(driverName.Length - start * sizeof(WCHAR)), 0, driverName.Buffer + start};
    base.MaximumLength = base.Length;
    UNICODE_STRING wanted;
    RtlInitUnicodeString(&wanted, expected);
    return RtlEqualUnicodeString(&base, &wanted, TRUE) != FALSE;
}
// Every driver from top down must be one known to pass reads and writes unchanged; the
// bottom device may instead be required to belong to bottomOwner. Records the first other.
static bool KnownStack(QC_RAM_BINDING* binding, PDEVICE_OBJECT top, const wchar_t* const* allowed, ULONG count, PDRIVER_OBJECT bottomOwner)
{
    ObReferenceObject(top);
    for (auto device = top; device;)
    {
        auto lower = IoGetLowerDeviceObject(device);
        bool known = false;
        if (!lower && bottomOwner)
            known = device->DriverObject == bottomOwner;
        else
            for (ULONG i = 0; i < count && !known; ++i)
                known = BaseNameIs(device->DriverObject->DriverName, allowed[i]);
        if (!known)
        {
            const auto& name = device->DriverObject->DriverName;
            RtlCopyMemory(binding->Driver, name.Buffer, min(static_cast<ULONG>(name.Length), static_cast<ULONG>(sizeof(binding->Driver) - sizeof(WCHAR))));
            ObDereferenceObject(device);
            if (lower)
                ObDereferenceObject(lower);
            return false;
        }
        ObDereferenceObject(device);
        device = lower;
    }
    return true;
}
static const wchar_t* const VolumeDrivers[] = {L"volsnap", L"volume", L"fvevol", L"iorate", L"rdyboost", L"volmgr"};
static const wchar_t* const DiskDrivers[] = {L"partmgr", L"disk"};
static NTSTATUS DiskStackKnown(QC_RAM_BINDING* binding, ULONG diskNumber, PDRIVER_OBJECT provider, bool* known)
{
    WCHAR path[64] = L"\\Device\\Harddisk";
    UNICODE_STRING name = {static_cast<USHORT>(wcslen(path) * sizeof(WCHAR)), sizeof(path), path};
    WCHAR digits[12];
    UNICODE_STRING number = {0, sizeof(digits), digits};
    auto status = RtlIntegerToUnicodeString(diskNumber, 10, &number);
    // Partition0 links to the disk's DR device; the DRn index is a global counter, not the disk number.
    if (NT_SUCCESS(status)) status = RtlAppendUnicodeStringToString(&name, &number);
    if (NT_SUCCESS(status)) status = RtlAppendUnicodeToString(&name, L"\\Partition0");
    if (!NT_SUCCESS(status))
        return status;
    PFILE_OBJECT file = nullptr;
    PDEVICE_OBJECT top = nullptr;
    status = IoGetDeviceObjectPointer(&name, FILE_READ_ATTRIBUTES, &file, &top);
    if (!NT_SUCCESS(status))
        return status;
    *known = KnownStack(binding, top, DiskDrivers, RTL_NUMBER_OF(DiskDrivers), provider);
    ObDereferenceObject(file);
    return STATUS_SUCCESS;
}

void QcRamDirectBind(QC_RAM_BINDING* binding, PDEVICE_OBJECT lower, const GUID& resource, ULONGLONG volumeBytes, ULONG flags)
{
    ExAcquireFastMutex(&ViewLock);
    QC_RAM_VIEW* view = nullptr;
    const bool bound = binding->View != nullptr;
    if (!bound)
    {
        // A new attempt reports its own outcome.
        InterlockedExchange(&binding->Reason, 0);
        binding->Detail = 0;
        RtlZeroMemory(binding->Driver, sizeof(binding->Driver));
        binding->Resource = resource;
        for (auto link = Views.Flink; link != &Views && !view; link = link->Flink)
        {
            auto candidate = CONTAINING_RECORD(link, QC_RAM_VIEW, Link);
            if (candidate->Registered && IsEqualGUID(candidate->Resource, resource))
                view = candidate;
        }
        if (view)
            InterlockedIncrement(&view->References);
        else
            Note(binding, QcRamDirectNotOffered);
    }
    ExReleaseFastMutex(&ViewLock);
    if (bound || !view)
        return;

    // Identity and stack checks send requests down the stacks: no lock may be held.
    bool writes = !(flags & QcRamDirectBindReadsOnly) && !binding->SnapshotSeen;
    ULONG reason = writes ? QcRamDirectNoReason : QcRamDirectSnapshot, detail = 0;
    UCHAR extentBuffer[sizeof(QC_VOLUME_DISK_EXTENTS) + sizeof(QC_DISK_EXTENT)] = {};
    auto extents = reinterpret_cast<QC_VOLUME_DISK_EXTENTS*>(extentBuffer);
    ULONG_PTR returned = 0;
    auto status = QcSendControl(lower, QcGetVolumeDiskExtents, nullptr, 0, extents, sizeof(extentBuffer), &returned);
    bool usable = true;
    if (!NT_SUCCESS(status) || returned < sizeof(QC_VOLUME_DISK_EXTENTS) || extents->NumberOfDiskExtents != 1)
    {
        Note(binding, NT_SUCCESS(status) || status == STATUS_BUFFER_OVERFLOW ? QcRamDirectLayout : QcRamDirectQueryFailed, static_cast<ULONG>(status));
        usable = false;
    }
    const auto& extent = extents->Extents[0];
    if (usable && !KnownStack(binding, lower, VolumeDrivers, RTL_NUMBER_OF(VolumeDrivers), nullptr))
    {
        Note(binding, QcRamDirectUnknownDriver);
        usable = false;
    }
    if (usable)
    {
        bool known = false;
        status = DiskStackKnown(binding, extent.DiskNumber, view->Owner, &known);
        if (!NT_SUCCESS(status) || !known)
        {
            Note(binding, NT_SUCCESS(status) ? QcRamDirectUnknownDriver : QcRamDirectQueryFailed, static_cast<ULONG>(status));
            usable = false;
        }
    }
    if (usable)
    {
        status = QcSendControl(lower, IOCTL_DISK_IS_WRITABLE, nullptr, 0, nullptr, 0);
        if (!NT_SUCCESS(status) && writes)
        {
            writes = false;
            reason = status == STATUS_MEDIA_WRITE_PROTECTED ? QcRamDirectWriteProtected : QcRamDirectQueryFailed;
            detail = static_cast<ULONG>(status);
        }
        ULONGLONG attributes = 0;
        if (writes && NT_SUCCESS(QcSendControl(lower, QcGetGptAttributes, nullptr, 0, &attributes, sizeof(attributes), &returned)) &&
            returned >= sizeof(attributes) && (attributes & QcGptReadOnly))
        {
            writes = false;
            reason = QcRamDirectWriteProtected;
        }
    }

    ExAcquireFastMutex(&ViewLock);
    const auto start = static_cast<ULONGLONG>(extent.StartingOffset.QuadPart);
    const auto length = min(static_cast<ULONGLONG>(extent.ExtentLength.QuadPart), volumeBytes);
    if (usable && !view->Registered)
    {
        Note(binding, QcRamDirectRemoved);
        usable = false;
    }
    else if (usable && view->Binding)
    {
        Note(binding, QcRamDirectInUse);
        usable = false;
    }
    else if (usable && (extent.StartingOffset.QuadPart < 0 || !length || !QcRamStoreBounds(view->Store, start, length - length % view->Store->SectorBytes)))
    {
        Note(binding, QcRamDirectLayout);
        usable = false;
    }
    if (usable)
    {
        // Read under ViewLock: the store cannot be withdrawn meanwhile.
        QcRamStoreCopy(view->Store, start + 3, binding->Signature, sizeof(binding->Signature), false);
        if (RtlEqualMemory(binding->Signature, BitLockerSignature, sizeof(BitLockerSignature)))
        {
            Note(binding, QcRamDirectBitLocker);
            usable = false;
        }
    }
    if (usable)
    {
        binding->Store = view->Store;
        binding->LargeCopy = view->LargeCopy;
        binding->QueueCopy = view->QueueCopy;
        binding->CopyContext = view->CopyContext;
        binding->Offset = start;
        binding->Length = length - length % view->Store->SectorBytes;
        binding->View = view;
        view->Binding = binding;
        InterlockedIncrement(&view->References);
        ExReInitializeRundownProtection(&binding->Rundown);
        if (!writes)
            Note(binding, reason, detail);
        InterlockedExchange(&binding->Access, static_cast<LONG>(QcRamDirectReads | (writes ? QcRamDirectWrites : 0)));
    }
    ExReleaseFastMutex(&ViewLock);
    Release(view);
}

void QcRamDirectRefuse(QC_RAM_BINDING* binding, ULONG reason)
{
    ExAcquireFastMutex(&ViewLock);
    if (!binding->View)
    {
        InterlockedExchange(&binding->Reason, static_cast<LONG>(reason));
        binding->Detail = 0;
    }
    ExReleaseFastMutex(&ViewLock);
}
void QcRamDirectState(QC_RAM_BINDING* binding, QC_RAM_DIRECT_STATE* state)
{
    RtlZeroMemory(state, sizeof(*state));
    state->Size = sizeof(*state);
    state->Version = 1;
    state->Access = static_cast<ULONG>(ReadNoFence(&binding->Access));
    state->Reason = static_cast<ULONG>(ReadNoFence(&binding->Reason));
    state->Detail = binding->Detail;
    state->Resource = binding->Resource;
    state->Offset = binding->Offset;
    state->Length = binding->Length;
    state->ReadRequests = static_cast<ULONGLONG>(ReadNoFence64(&binding->ReadRequests));
    state->WriteRequests = static_cast<ULONGLONG>(ReadNoFence64(&binding->WriteRequests));
    state->ReadBytes = static_cast<ULONGLONG>(ReadNoFence64(&binding->ReadBytes));
    state->WriteBytes = static_cast<ULONGLONG>(ReadNoFence64(&binding->WriteBytes));
    state->Declined = static_cast<ULONGLONG>(ReadNoFence64(&binding->Declined));
    RtlCopyMemory(state->Driver, binding->Driver, sizeof(state->Driver));
}

// ---- Registration from the provider (budget device, kernel mode, PASSIVE_LEVEL) ----
static bool ValidStore(const QC_RAM_STORE* store)
{
    return store && store->Size == sizeof(*store) && store->Version == QcRamStoreVersion &&
           (store->SectorBytes == 512 || store->SectorBytes == 4096) && store->Capacity &&
           store->Capacity % store->SectorBytes == 0 && store->Slabs &&
           store->SlabCount == (store->Capacity + QcRamSlabBytes - 1) / QcRamSlabBytes;
}
NTSTATUS QcRamViewControl(PIRP irp)
{
    auto stack = IoGetCurrentIrpStackLocation(irp);
    auto request = static_cast<QC_KERNEL_RAM_VIEW*>(irp->AssociatedIrp.SystemBuffer);
    irp->IoStatus.Information = 0;
    if (!request || stack->Parameters.DeviceIoControl.InputBufferLength != sizeof(*request) ||
        stack->Parameters.DeviceIoControl.OutputBufferLength < sizeof(*request) || request->Size != sizeof(*request) ||
        request->Version != QcRamViewVersion || request->Reserved || !request->Owner)
        return STATUS_INVALID_PARAMETER;
    if (request->Action == QcRamViewRegister)
    {
        if (!ValidStore(request->Store) || request->View)
            return STATUS_INVALID_PARAMETER;
        auto view = static_cast<QC_RAM_VIEW*>(ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(QC_RAM_VIEW), Tag));
        if (!view)
            return STATUS_INSUFFICIENT_RESOURCES;
        view->References = 1;
        view->Owner = request->Owner;
        view->Resource = request->Resource;
        view->Store = request->Store;
        view->LargeCopy = request->LargeCopy;
        view->QueueCopy = request->QueueCopy;
        view->CopyContext = request->CopyContext;
        view->Registered = TRUE;
        ExAcquireFastMutex(&ViewLock);
        bool duplicate = false;
        for (auto link = Views.Flink; link != &Views; link = link->Flink)
            duplicate |= IsEqualGUID(CONTAINING_RECORD(link, QC_RAM_VIEW, Link)->Resource, request->Resource) != FALSE;
        if (!duplicate)
        {
            ObReferenceObject(view->Owner);
            InsertTailList(&Views, &view->Link);
        }
        ExReleaseFastMutex(&ViewLock);
        if (duplicate)
        {
            ExFreePoolWithTag(view, Tag);
            return STATUS_OBJECT_NAME_COLLISION;
        }
        request->View = view;
        irp->IoStatus.Information = sizeof(*request);
        return STATUS_SUCCESS;
    }
    if (request->Action != QcRamViewUnregister)
        return STATUS_INVALID_PARAMETER;
    QC_RAM_VIEW* found = nullptr;
    ExAcquireFastMutex(&ViewLock);
    for (auto link = Views.Flink; link != &Views && !found; link = link->Flink)
    {
        auto view = CONTAINING_RECORD(link, QC_RAM_VIEW, Link);
        if (view == request->View && view->Owner == request->Owner && IsEqualGUID(view->Resource, request->Resource))
            found = view;
    }
    if (found)
    {
        if (found->Binding)
            EndLocked(found->Binding, QcRamDirectRemoved);
        found->Registered = FALSE;
        RemoveEntryList(&found->Link);
    }
    ExReleaseFastMutex(&ViewLock);
    if (!found)
        return STATUS_NOT_FOUND;
    Release(found);
    irp->IoStatus.Information = sizeof(*request);
    return STATUS_SUCCESS;
}
