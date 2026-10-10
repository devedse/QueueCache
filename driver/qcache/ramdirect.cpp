// SPDX-License-Identifier: MIT
// Direct access to QueueCache RAM disks (see ramdirect.h).
#include "ramdirect.h"
#include "devicecontrol.h"

// Stable public layouts (ntddvol.h, winioctl.h), declared here to keep the include set small.
struct QC_DISK_EXTENT { ULONG DiskNumber; LARGE_INTEGER StartingOffset, ExtentLength; };
struct QC_VOLUME_DISK_EXTENTS { ULONG NumberOfDiskExtents; QC_DISK_EXTENT Extents[1]; };
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
    PVOID LargeCopyContext;
    QC_RAM_BINDING* Binding;    // ViewLock
    BOOLEAN Registered;         // ViewLock; the store may be used only while set.
};
static FAST_MUTEX ViewLock;
static LIST_ENTRY Views;
static constexpr ULONG Tag = 'vRCQ';
static constexpr ULONG LargeCopyBytes = 512 * 1024; // The provider splits copies from 2 x 256 KiB.
// Writes this large take the standard path by design: the provider's workers overlap queued
// writes across processors, while Direct copies each in its caller's thread, one at a time
// (lab VM, 1 MiB writes: 25.4 GB/s standard vs 17.4 GB/s Direct at Q8, 16.7 vs 17.5 at Q1).
static constexpr ULONG LargeWriteBytes = 512 * 1024;
static const UCHAR BitLockerSignature[8] = {'-', 'F', 'V', 'E', '-', 'F', 'S', '-'};

void QcRamDirectInitialize()
{
    ExInitializeFastMutex(&ViewLock);
    InitializeListHead(&Views);
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
    binding->LargeCopyContext = nullptr;
    Release(view);
}
void QcRamDirectEnd(QC_RAM_BINDING* binding, ULONG reason)
{
    ExAcquireFastMutex(&ViewLock);
    EndLocked(binding, reason);
    ExReleaseFastMutex(&ViewLock);
}

// ---- Hot path ----
static bool Decline(QC_RAM_BINDING* binding)
{
    InterlockedIncrement64(&binding->Declined);
    return false;
}
static void Copy(QC_RAM_BINDING* binding, QC_RAM_STORE* store, ULONGLONG at, PUCHAR buffer, ULONG length, bool write)
{
    if (length >= LargeCopyBytes && binding->LargeCopy)
        binding->LargeCopy(binding->LargeCopyContext, store, at, buffer, length, write);
    else
        QcRamStoreCopy(store, at, buffer, length, write);
}
// BitLocker replaces the volume's boot sector name on disk (below fvevol) when it starts.
static bool SignatureIntact(const QC_RAM_BINDING* binding, const QC_RAM_STORE* store)
{
    UCHAR now[8];
    QcRamStoreCopy(store, binding->Offset + 3, now, sizeof(now), false);
    return RtlEqualMemory(now, binding->Signature, sizeof(now));
}
bool QcRamDirectPrepareRead(QC_RAM_BINDING* binding, PIRP irp, QC_RAM_READ* read)
{
    const auto stack = IoGetCurrentIrpStackLocation(irp);
    const auto offset = stack->Parameters.Read.ByteOffset.QuadPart;
    const ULONG length = stack->Parameters.Read.Length;
    const auto mdl = irp->MdlAddress;
    if (stack->MajorFunction != IRP_MJ_READ || !(ReadNoFence(&binding->Access) & QcRamDirectReads) ||
        !length || !mdl || mdl->Next || MmGetMdlByteCount(mdl) < length || offset < 0 ||
        static_cast<ULONGLONG>(offset) > binding->Length || length > binding->Length - static_cast<ULONGLONG>(offset) ||
        !ExAcquireRundownProtection(&binding->Rundown))
        return false;
    auto store = binding->Store;
    const auto at = binding->Offset + static_cast<ULONGLONG>(offset);
    PUCHAR buffer = nullptr;
    if (!SignatureIntact(binding, store))
    {
        InterlockedExchange(&binding->Access, 0);
        Note(binding, QcRamDirectBitLocker);
    }
    else if (QcRamStoreBounds(store, at, length))
        buffer = static_cast<PUCHAR>(MmGetSystemAddressForMdlSafe(mdl, NormalPagePriority | MdlMappingNoExecute));
    if (!buffer)
    {
        ExReleaseRundownProtection(&binding->Rundown);
        return false;
    }
    *read = {binding, store, buffer, at, QcRamTimingStart(store), length};
    return true;
}
void QcRamDirectReleaseRead(QC_RAM_READ* read)
{
    ExReleaseRundownProtection(&read->Binding->Rundown);
    RtlZeroMemory(read, sizeof(*read));
}
NTSTATUS QcRamDirectFinishRead(QC_RAM_READ* read, PIRP irp)
{
    NTSTATUS status = STATUS_SUCCESS;
    irp->IoStatus.Information = 0;
    if (irp->Cancel)
        status = STATUS_CANCELLED; // Cancellation is best effort once copying begins.
    else if (!SignatureIntact(read->Binding, read->Store))
    {
        InterlockedExchange(&read->Binding->Access, 0);
        Note(read->Binding, QcRamDirectBitLocker);
        InterlockedIncrement64(&read->Binding->Declined);
        status = STATUS_NOT_FOUND;
    }
    else
    {
        // One whole read per sleeping executor. Calling the split-copy helper
        // here would recreate the spin/handoff cost this experiment measures.
        QcRamStoreCopy(read->Store, read->Offset, read->Buffer, read->Length, false);
        QcRamTimingEnd(read->Store, false, read->Started);
        InterlockedIncrement64(&read->Binding->ReadRequests);
        InterlockedAdd64(&read->Binding->ReadBytes, read->Length);
        irp->IoStatus.Information = read->Length;
    }
    QcRamDirectReleaseRead(read);
    return status;
}
bool QcRamDirectTransfer(QC_RAM_BINDING* binding, PIRP irp)
{
    const auto access = ReadNoFence(&binding->Access);
    if (!access)
        return false;
    const auto stack = IoGetCurrentIrpStackLocation(irp);
    const bool write = stack->MajorFunction == IRP_MJ_WRITE;
    const auto offset = stack->Parameters.Read.ByteOffset.QuadPart;
    const ULONG length = stack->Parameters.Read.Length;
    if (write && length >= LargeWriteBytes)
        return false; // Not a decline: see LargeWriteBytes.
    const auto mdl = irp->MdlAddress;
    if ((write && !(access & QcRamDirectWrites)) || !length || !mdl || mdl->Next || MmGetMdlByteCount(mdl) < length ||
        offset < 0 || static_cast<ULONGLONG>(offset) > binding->Length || length > binding->Length - static_cast<ULONGLONG>(offset))
        return Decline(binding);
    if (!ExAcquireRundownProtection(&binding->Rundown))
        return Decline(binding);
    auto store = binding->Store;
    const auto at = binding->Offset + static_cast<ULONGLONG>(offset);
    const auto started = QcRamTimingStart(store);
    bool served = false;
    if (!SignatureIntact(binding, store))
    {
        InterlockedExchange(&binding->Access, 0); // Ended for good; the next bind or removal finishes it.
        Note(binding, QcRamDirectBitLocker);
    }
    else if (QcRamStoreBounds(store, at, length))
    {
        const auto priority = static_cast<ULONG>((irp->Flags & IRP_PAGING_IO) ? HighPagePriority : NormalPagePriority) | MdlMappingNoExecute;
        if (auto buffer = static_cast<PUCHAR>(MmGetSystemAddressForMdlSafe(mdl, priority)))
        {
            if (!write)
            {
                Copy(binding, store, at, buffer, length, false);
                served = true;
            }
            else
            {
                InterlockedIncrement(&binding->Writing); // Before re-checking Access: see EndWrites.
                if ((InterlockedOr(&binding->Access, 0) & QcRamDirectWrites) && QcRamStoreBeginWrite(store) == QcRamAdmission::Admitted)
                {
                    QcRamStoreChanged(store);
                    Copy(binding, store, at, buffer, length, true);
                    QcRamStoreEndWrite(store);
                    served = true;
                }
                InterlockedDecrement(&binding->Writing);
            }
        }
    }
    if (served)
        QcRamTimingEnd(store, write, started);
    ExReleaseRundownProtection(&binding->Rundown);
    if (!served)
        return Decline(binding); // Read-only, frozen or unmappable: the standard path answers.
    if (write)
    {
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
    return true;
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
        binding->LargeCopyContext = view->LargeCopyContext;
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
        view->LargeCopyContext = request->LargeCopyContext;
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
