// SPDX-License-Identifier: MIT
// Storport owns the adapter/PDOs; RAM lifetime is independent of user handles.
#include <ntifs.h>
extern "C" {
#include <storport.h>
}
#include <ntddscsi.h>
#include "../shared/lockedpages.h"
#include "../shared/budgetprotocol.h"
#include "../shared/ramdiskprotocol.h"

static constexpr ULONG Tag = 'dRCQ', SlabBytes = 4 << 20;
static const GUID EmptyGuid{};
static PDRIVER_OBJECT Driver;
struct SLAB { PMDL Mdl; PUCHAR Bytes; ULONG Length; };
struct DISK
{
    GUID Resource, Epoch, FreezeOwner;
    ULONGLONG Creation, Capacity, Generation, ReservedBytes;
    ULONG SectorBytes, Flags, SlabCount;
    SLAB* Slabs;
    QC_KERNEL_RESERVATION Reservation;
    PFILE_OBJECT BudgetFile;
    PDEVICE_OBJECT BudgetDevice;
    KSPIN_LOCK IoLock;
    volatile LONG References;
    BOOLEAN Removing;
    KEVENT Idle;
    ULONGLONG ReadBytes, WriteBytes, Flushes, Trims, Errors, Transfers;
};
struct ADAPTER
{
    KSPIN_LOCK TableLock;
    FAST_MUTEX ControlLock;
    DISK* Disks[QcRamMaxDisks];
    GUID Epoch;
    ULONGLONG NextCreation;
    BOOLEAN Stopped;
};
extern "C" DRIVER_INITIALIZE DriverEntry;
VIRTUAL_HW_FIND_ADAPTER FindAdapter;
HW_INITIALIZE Initialize;
HW_STARTIO StartIo;
HW_RESET_BUS ResetBus;
HW_ADAPTER_CONTROL AdapterControl;
HW_FREE_ADAPTER_RESOURCES FreeAdapter;
HW_PROCESS_SERVICE_REQUEST ServiceRequest;
HW_COMPLETE_SERVICE_IRP CompleteService;

static DISK* ReferenceDisk(ADAPTER* adapter, ULONG slot, bool published)
{
    if (slot >= QcRamMaxDisks) return nullptr;
    KIRQL irql; KeAcquireSpinLock(&adapter->TableLock, &irql);
    auto disk = adapter->Disks[slot];
    if (!disk || disk->Removing || (published && !(disk->Flags & QcRamPublished))) disk = nullptr;
    if (disk && InterlockedIncrement(&disk->References) == 1) KeClearEvent(&disk->Idle);
    KeReleaseSpinLock(&adapter->TableLock, irql);
    return disk;
}
static void DereferenceDisk(DISK* disk)
{
    if (InterlockedDecrement(&disk->References) == 0) KeSetEvent(&disk->Idle, IO_NO_INCREMENT, FALSE);
}
static void WaitReferences(DISK* disk)
{
    // A previous zero-reference signal may race a new reference before removal.
    // Once Removing is set under TableLock, references can only decrease.
    while (InterlockedCompareExchange(&disk->References, 0, 0))
    {
        KeClearEvent(&disk->Idle);
        if (InterlockedCompareExchange(&disk->References, 0, 0))
            KeWaitForSingleObject(&disk->Idle, Executive, KernelMode, FALSE, nullptr);
    }
}
static NTSTATUS BudgetCall(DISK* disk, ULONG action)
{
    disk->Reservation.Action = action;
    KEVENT done; KeInitializeEvent(&done, NotificationEvent, FALSE);
    IO_STATUS_BLOCK result{};
    auto irp = IoBuildDeviceIoControlRequest(IOCTL_QCACHE_KERNEL_BUDGET, disk->BudgetDevice,
        &disk->Reservation, sizeof(disk->Reservation), &disk->Reservation, sizeof(disk->Reservation), TRUE, &done, &result);
    if (!irp) return STATUS_INSUFFICIENT_RESOURCES;
    auto status = IoCallDriver(disk->BudgetDevice, irp);
    if (status == STATUS_PENDING) KeWaitForSingleObject(&done, Executive, KernelMode, FALSE, nullptr);
    return result.Status;
}
static void FreeDisk(DISK* disk)
{
    if (disk->Slabs)
    {
        for (ULONG i = 0; i < disk->SlabCount; ++i)
            if (disk->Slabs[i].Mdl) QcFreeLockedPages(disk->Slabs[i].Mdl, disk->Slabs[i].Bytes, true);
        ExFreePoolWithTag(disk->Slabs, Tag);
    }
    // Accounting survives until the last reference and last physical page are gone.
    if (disk->Reservation.Bytes && disk->BudgetFile)
    {
        const auto status = BudgetCall(disk, QcBudgetRelease);
        NT_ASSERT(NT_SUCCESS(status)); UNREFERENCED_PARAMETER(status);
    }
    if (disk->BudgetFile) ObDereferenceObject(disk->BudgetFile);
    RtlSecureZeroMemory(disk, sizeof(*disk)); ExFreePoolWithTag(disk, Tag);
}
static NTSTATUS AllocateDisk(ADAPTER* adapter, QC_RAM_REQUEST* request, PIRP irp, DISK** result)
{
    if (request->Capacity < (16ULL << 20) || request->Capacity > (128ULL << 30) || request->Capacity % (1 << 20) ||
        (request->SectorBytes != 512 && request->SectorBytes != 4096) || request->Flags ||
        IsEqualGUID(request->Resource, EmptyGuid)) return STATUS_INVALID_PARAMETER;
    auto disk = static_cast<DISK*>(ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(DISK), Tag));
    if (!disk) return STATUS_INSUFFICIENT_RESOURCES;
    KeInitializeSpinLock(&disk->IoLock); KeInitializeEvent(&disk->Idle, NotificationEvent, TRUE);
    disk->Resource = request->Resource; disk->Epoch = adapter->Epoch;
    disk->Creation = ++adapter->NextCreation; disk->Capacity = request->Capacity; disk->SectorBytes = request->SectorBytes;
    disk->SlabCount = static_cast<ULONG>((disk->Capacity + SlabBytes - 1) / SlabBytes);
    disk->ReservedBytes = disk->Capacity + disk->SlabCount * sizeof(SLAB) + sizeof(DISK) + QcRamTransferBytes + 4096;
    UNICODE_STRING name = RTL_CONSTANT_STRING(L"\\Device\\QueueCacheBudget");
    auto status = IoGetDeviceObjectPointer(&name, FILE_READ_DATA | FILE_WRITE_DATA, &disk->BudgetFile, &disk->BudgetDevice);
    if (!NT_SUCCESS(status)) { FreeDisk(disk); return status; }
    disk->Reservation.Size = sizeof(disk->Reservation); disk->Reservation.Version = 1;
    disk->Reservation.Owner = Driver; disk->Reservation.Resource = disk->Resource;
    disk->Reservation.Bytes = disk->ReservedBytes;
    status = BudgetCall(disk, QcBudgetReserve);
    if (!NT_SUCCESS(status)) { disk->Reservation.Bytes = 0; FreeDisk(disk); return status; }
    disk->Slabs = static_cast<SLAB*>(ExAllocatePool2(POOL_FLAG_NON_PAGED, disk->SlabCount * sizeof(SLAB), Tag));
    if (!disk->Slabs) { FreeDisk(disk); return STATUS_INSUFFICIENT_RESOURCES; }
    for (ULONG i = 0; i < disk->SlabCount; ++i)
    {
        if (irp->Cancel) { FreeDisk(disk); return STATUS_CANCELLED; }
        auto slab = &disk->Slabs[i];
        slab->Length = static_cast<ULONG>(min(static_cast<ULONGLONG>(SlabBytes), disk->Capacity - static_cast<ULONGLONG>(i) * SlabBytes));
        slab->Bytes = QcAllocateLockedPages(slab->Length, &slab->Mdl);
        if (!slab->Bytes) { FreeDisk(disk); return STATUS_INSUFFICIENT_RESOURCES; }
        RtlZeroMemory(slab->Bytes, slab->Length);
    }
    *result = disk; return STATUS_SUCCESS;
}
static bool Bounds(DISK* disk, ULONGLONG offset, ULONGLONG bytes)
{
    return offset <= disk->Capacity && bytes <= disk->Capacity - offset && offset % disk->SectorBytes == 0 && bytes % disk->SectorBytes == 0;
}
static void Copy(DISK* disk, ULONGLONG offset, PUCHAR buffer, ULONG length, bool write, bool zero = false)
{
    while (length)
    {
        const auto index = static_cast<ULONG>(offset / SlabBytes), within = static_cast<ULONG>(offset % SlabBytes);
        const auto bytes = min(length, disk->Slabs[index].Length - within);
        auto storage = disk->Slabs[index].Bytes + within;
        if (zero) RtlZeroMemory(storage, bytes);
        else if (write) RtlCopyMemory(storage, buffer, bytes);
        else RtlCopyMemory(buffer, storage, bytes);
        if (buffer) buffer += bytes;
        offset += bytes; length -= bytes;
    }
}
static void Snapshot(DISK* disk, QC_RAM_REQUEST* reply, ULONG slot)
{
    reply->Resource = disk->Resource; reply->Epoch = disk->Epoch; reply->Creation = disk->Creation;
    reply->Capacity = disk->Capacity; reply->Generation = disk->Generation; reply->ReservedBytes = disk->ReservedBytes;
    reply->SectorBytes = disk->SectorBytes; reply->Flags = disk->Flags; reply->Slot = slot; reply->FreezeOwner = disk->FreezeOwner;
    reply->ReadBytes = disk->ReadBytes; reply->WriteBytes = disk->WriteBytes; reply->Flushes = disk->Flushes;
    reply->Trims = disk->Trims; reply->Errors = disk->Errors; reply->Transfers = disk->Transfers;
}
static bool Authorized(PIRP irp)
{
    if (irp->RequestorMode == KernelMode) return true;
    auto process = IoGetRequestorProcess(irp);
    if (!process) return false;
    auto token = PsReferencePrimaryToken(process);
    const bool admin = SeTokenIsAdmin(token) != FALSE;
    PsDereferencePrimaryToken(token);
    return admin;
}
void ServiceRequest(PVOID extension, PVOID requestIrp)
{
    auto adapter = static_cast<ADAPTER*>(extension); auto irp = static_cast<PIRP>(requestIrp);
    auto stack = IoGetCurrentIrpStackLocation(irp);
    const auto input = stack->Parameters.DeviceIoControl.InputBufferLength, output = stack->Parameters.DeviceIoControl.OutputBufferLength;
    auto reply = static_cast<QC_RAM_REQUEST*>(irp->AssociatedIrp.SystemBuffer);
    NTSTATUS status = STATUS_INVALID_PARAMETER; ULONG_PTR returned = 0;
    if (!Authorized(irp)) status = STATUS_ACCESS_DENIED;
    else if (reply && input >= sizeof(*reply) && output >= sizeof(*reply) &&
        reply->Magic == QcRamMagic && reply->Version == QcRamVersion && reply->Size == sizeof(*reply) &&
        reply->Action >= QcRamCapabilities && reply->Action <= QcRamSetReadOnly && input <= sizeof(*reply) + QcRamTransferBytes && output <= sizeof(*reply) + QcRamTransferBytes)
    {
        const auto command = *reply;
        // Service callbacks run at PASSIVE_LEVEL. Serialize control/transfer/remove,
        // leaving the SCSI hot path independent of this blocking mutex.
        KeEnterCriticalRegion();
        ExAcquireFastMutexUnsafe(&adapter->ControlLock);
        if (command.Action == QcRamCapabilities)
        {
            RtlZeroMemory(reply, sizeof(*reply)); reply->Magic = QcRamMagic; reply->Version = QcRamVersion; reply->Size = sizeof(*reply);
            reply->Epoch = adapter->Epoch; reply->Capacity = 128ULL << 30; reply->Slot = QcRamMaxDisks;
            reply->TransferBytes = QcRamTransferBytes; status = STATUS_SUCCESS;
        }
        else if (command.Action == QcRamCreate)
        {
            ULONG slot = QcRamMaxDisks; bool duplicate = false;
            for (ULONG i = 0; i < QcRamMaxDisks; ++i)
            {
                auto disk = adapter->Disks[i];
                if (!disk && slot == QcRamMaxDisks) slot = i;
                if (disk && IsEqualGUID(disk->Resource, command.Resource)) duplicate = true;
            }
            if (duplicate) status = STATUS_OBJECT_NAME_COLLISION;
            else if (slot == QcRamMaxDisks) status = STATUS_INSUFFICIENT_RESOURCES;
            else
            {
                DISK* disk = nullptr; status = AllocateDisk(adapter, reply, irp, &disk);
                if (NT_SUCCESS(status))
                {
                    KIRQL irql; KeAcquireSpinLock(&adapter->TableLock, &irql); adapter->Disks[slot] = disk; KeReleaseSpinLock(&adapter->TableLock, irql);
                    Snapshot(disk, reply, slot);
                }
            }
        }
        else
        {
            auto disk = ReferenceDisk(adapter, command.Slot, false);
            if (!disk) status = STATUS_NOT_FOUND;
            else if (command.Action != QcRamEnumerate && (!IsEqualGUID(disk->Resource, command.Resource) ||
                !IsEqualGUID(disk->Epoch, command.Epoch) || disk->Creation != command.Creation))
                status = STATUS_REVISION_MISMATCH;
            else if (command.Action == QcRamRemove)
            {
                KIRQL irql; KeAcquireSpinLock(&adapter->TableLock, &irql);
                disk->Removing = TRUE; adapter->Disks[command.Slot] = nullptr;
                KeReleaseSpinLock(&adapter->TableLock, irql);
                DereferenceDisk(disk);
                WaitReferences(disk);
                FreeDisk(disk); disk = nullptr;
                StorPortNotification(BusChangeDetected, adapter, 0); status = STATUS_SUCCESS;
            }
            else
            {
                KIRQL irql; KeAcquireSpinLock(&disk->IoLock, &irql);
                status = STATUS_SUCCESS;
                switch (command.Action)
                {
                case QcRamEnumerate: case QcRamQuery: break;
                case QcRamRead: case QcRamWrite:
                    if (!command.TransferBytes || command.TransferBytes > QcRamTransferBytes || !Bounds(disk, command.Offset, command.TransferBytes) ||
                        (command.Action == QcRamRead && output < sizeof(*reply) + command.TransferBytes) ||
                        (command.Action == QcRamWrite && input != sizeof(*reply) + command.TransferBytes)) status = STATUS_INVALID_PARAMETER;
                    else if (command.Action == QcRamWrite && (disk->Flags & (QcRamReadOnly | QcRamPublished))) status = STATUS_MEDIA_WRITE_PROTECTED;
                    else if ((disk->Flags & QcRamPublished) && !(disk->Flags & QcRamFrozen)) status = STATUS_DEVICE_BUSY;
                    else if ((disk->Flags & QcRamFrozen) && !IsEqualGUID(disk->FreezeOwner, command.FreezeOwner)) status = STATUS_ACCESS_DENIED;
                    else
                    {
                        Copy(disk, command.Offset, reinterpret_cast<PUCHAR>(reply + 1), command.TransferBytes, command.Action == QcRamWrite);
                        ++disk->Transfers;
                        if (command.Action == QcRamWrite) ++disk->Generation;
                        else returned = sizeof(*reply) + command.TransferBytes;
                    }
                    break;
                case QcRamPublish:
                    if (disk->Flags & QcRamPublished) status = STATUS_DEVICE_BUSY;
                    else disk->Flags |= QcRamPublished;
                    break;
                case QcRamFreeze:
                    if (IsEqualGUID(command.FreezeOwner, EmptyGuid)) status = STATUS_INVALID_PARAMETER;
                    else if (disk->Flags & QcRamFrozen) status = STATUS_DEVICE_BUSY;
                    else { disk->FreezeOwner = command.FreezeOwner; disk->Flags |= QcRamFrozen; }
                    break;
                case QcRamThaw:
                    if (!(disk->Flags & QcRamFrozen) || !IsEqualGUID(disk->FreezeOwner, command.FreezeOwner)) status = STATUS_ACCESS_DENIED;
                    else { disk->Flags &= ~QcRamFrozen; disk->FreezeOwner = EmptyGuid; }
                    break;
                case QcRamSetReadOnly:
                    if (command.Flags & ~QcRamReadOnly) status = STATUS_INVALID_PARAMETER;
                    else disk->Flags = (disk->Flags & ~QcRamReadOnly) | command.Flags;
                    break;
                default: status = STATUS_INVALID_DEVICE_REQUEST; break;
                }
                Snapshot(disk, reply, command.Slot);
                KeReleaseSpinLock(&disk->IoLock, irql);
                if (NT_SUCCESS(status) && command.Action == QcRamPublish) StorPortNotification(BusChangeDetected, adapter, 0);
            }
            if (disk) DereferenceDisk(disk);
        }
        ExReleaseFastMutexUnsafe(&adapter->ControlLock); KeLeaveCriticalRegion();
        if (NT_SUCCESS(status) && !returned) returned = sizeof(*reply);
    }
    irp->IoStatus.Status = status; irp->IoStatus.Information = NT_SUCCESS(status) ? returned : 0;
    StorPortCompleteServiceIrp(adapter, irp);
}
void CompleteService(PVOID) { /* Service IRPs are completed synchronously; none survive callbacks. */ }

static ULONGLONG Big(const UCHAR* bytes, ULONG count)
{
    ULONGLONG value = 0; for (ULONG i = 0; i < count; ++i) value = (value << 8) | bytes[i]; return value;
}
static void PutBig(PUCHAR bytes, ULONGLONG value, ULONG count)
{
    for (ULONG i = 0; i < count; ++i) { bytes[count - i - 1] = static_cast<UCHAR>(value); value >>= 8; }
}
static void Serial(DISK* disk, char* serial)
{
    static const char digits[] = "0123456789ABCDEF";
    serial[0] = 'Q'; serial[1] = 'C'; auto bytes = reinterpret_cast<PUCHAR>(&disk->Resource);
    for (ULONG i = 0; i < 16; ++i) { serial[2+i*2] = digits[bytes[i] >> 4]; serial[3+i*2] = digits[bytes[i] & 15]; }
}
static void Sense(PSCSI_REQUEST_BLOCK srb, UCHAR key, UCHAR asc)
{
    srb->SrbStatus = SRB_STATUS_ERROR; srb->ScsiStatus = SCSISTAT_CHECK_CONDITION; srb->DataTransferLength = 0;
    if (srb->SenseInfoBuffer && srb->SenseInfoBufferLength >= 18)
    {
        auto sense = static_cast<PUCHAR>(srb->SenseInfoBuffer); RtlZeroMemory(sense, 18);
        sense[0] = 0x70; sense[2] = key; sense[7] = 10; sense[12] = asc; srb->SrbStatus |= SRB_STATUS_AUTOSENSE_VALID;
    }
}
BOOLEAN StartIo(PVOID extension, PSCSI_REQUEST_BLOCK srb)
{
    auto adapter = static_cast<ADAPTER*>(extension);
    auto disk = srb->PathId == 0 && srb->TargetId == 0 ? ReferenceDisk(adapter, srb->Lun, true) : nullptr;
    srb->ScsiStatus = SCSISTAT_GOOD; srb->SrbStatus = SRB_STATUS_SUCCESS;
    if (!disk) { srb->SrbStatus = SRB_STATUS_NO_DEVICE; srb->DataTransferLength = 0; }
    else if (srb->Function == SRB_FUNCTION_FLUSH || srb->Function == SRB_FUNCTION_SHUTDOWN)
    {
        KIRQL irql; KeAcquireSpinLock(&disk->IoLock, &irql); ++disk->Flushes; KeReleaseSpinLock(&disk->IoLock, irql); srb->DataTransferLength = 0;
    }
    else if (srb->Function != SRB_FUNCTION_EXECUTE_SCSI) { srb->SrbStatus = SRB_STATUS_INVALID_REQUEST; srb->DataTransferLength = 0; }
    else
    {
        PVOID mapped = nullptr;
        if (srb->DataTransferLength && StorPortGetSystemAddress(adapter, srb, &mapped) != STOR_STATUS_SUCCESS) Sense(srb, 4, 0x44);
        else
        {
            KIRQL irql; KeAcquireSpinLock(&disk->IoLock, &irql);
            auto data = static_cast<PUCHAR>(mapped); auto cdb = srb->Cdb; const auto available = srb->DataTransferLength;
            const auto opcode = cdb[0];
            if (opcode == 0x00 || opcode == 0x1B || opcode == 0x1E) srb->DataTransferLength = 0; // ready/start/prevent
            else if (opcode == 0x35 || opcode == 0x91) { ++disk->Flushes; srb->DataTransferLength = 0; }
            else if (opcode == 0x12)
            {
                UCHAR response[128]{}; ULONG length = 0;
                if (!(cdb[1] & 1))
                {
                    response[2] = 6; response[3] = 2; response[4] = 31; response[7] = 2;
                    RtlCopyMemory(response + 8, "QCACHE  ", 8); RtlCopyMemory(response + 16, "RAM Disk        ", 16); RtlCopyMemory(response + 32, "0001", 4); length = 36;
                }
                else if (cdb[2] == 0) { response[3] = 6; response[4] = 0; response[5] = 0x80; response[6] = 0x83; response[7] = 0xB0; response[8] = 0xB1; response[9] = 0xB2; length = 10; }
                else if (cdb[2] == 0x80) { response[1] = 0x80; response[3] = 34; Serial(disk, reinterpret_cast<char*>(response + 4)); length = 38; }
                else if (cdb[2] == 0x83) { response[1] = 0x83; response[3] = 38; response[4] = 2; response[5] = 1; response[7] = 34; Serial(disk, reinterpret_cast<char*>(response + 8)); length = 42; }
                else if (cdb[2] == 0xB0)
                {
                    response[1] = 0xB0; response[3] = 60;
                    PutBig(response + 8, QcRamTransferBytes / disk->SectorBytes, 4);
                    PutBig(response + 20, QcRamTransferBytes / disk->SectorBytes, 4);
                    PutBig(response + 24, 1, 4); PutBig(response + 28, 1, 4); length = 64;
                }
                else if (cdb[2] == 0xB1) { response[1] = 0xB1; response[3] = 60; response[5] = 1; length = 64; }
                else if (cdb[2] == 0xB2) { response[1] = 0xB2; response[3] = 4; response[5] = 0x84; response[6] = 2; length = 8; }
                else Sense(srb, 5, 0x24);
                if (length) { length = min(length, min(available, static_cast<ULONG>(cdb[4]))); if (length) RtlCopyMemory(data, response, length); srb->DataTransferLength = length; }
            }
            else if (opcode == 0x25 || (opcode == 0x9E && (cdb[1] & 31) == 0x10))
            {
                const ULONG length = opcode == 0x25 ? 8 : 32;
                if (available < length) Sense(srb, 5, 0x24);
                else
                {
                    RtlZeroMemory(data, length); const auto last = disk->Capacity / disk->SectorBytes - 1;
                    if (length == 8) { PutBig(data, min(last, static_cast<ULONGLONG>(MAXULONG)), 4); PutBig(data + 4, disk->SectorBytes, 4); }
                    else { PutBig(data, last, 8); PutBig(data + 8, disk->SectorBytes, 4); data[14] = 0xC0; }
                    srb->DataTransferLength = length;
                }
            }
            else if (opcode == 0x28 || opcode == 0x2A || opcode == 0x88 || opcode == 0x8A)
            {
                const bool write = opcode == 0x2A || opcode == 0x8A, large = opcode == 0x88 || opcode == 0x8A;
                const auto lba = Big(cdb + 2, large ? 8 : 4), blocks = Big(cdb + (large ? 10 : 7), large ? 4 : 2);
                if (lba > MAXULONGLONG / disk->SectorBytes || blocks > MAXULONG / disk->SectorBytes) Sense(srb, 5, 0x21);
                else
                {
                    const auto offset = lba * disk->SectorBytes; const auto bytes = static_cast<ULONG>(blocks * disk->SectorBytes);
                    if (available < bytes || !Bounds(disk, offset, bytes)) Sense(srb, 5, 0x21);
                    else if (write && (disk->Flags & QcRamReadOnly)) Sense(srb, 7, 0x27);
                    else if (write && (disk->Flags & QcRamFrozen)) { srb->SrbStatus = SRB_STATUS_BUSY; srb->DataTransferLength = 0; }
                    else
                    {
                        Copy(disk, offset, data, bytes, write); srb->DataTransferLength = bytes;
                        if (write) { disk->WriteBytes += bytes; if (bytes) ++disk->Generation; }
                        else disk->ReadBytes += bytes;
                    }
                }
            }
            else if (opcode == 0x1A || opcode == 0x5A)
            {
                UCHAR response[64]{}; const bool ten = opcode == 0x5A; const auto header = ten ? 8U : 4U;
                const auto page = cdb[2] & 0x3F;
                if (page != 8 && page != 0x3F) Sense(srb, 5, 0x24);
                else
                {
                    response[header] = 8; response[header + 1] = 18; response[header + 2] = 4; // volatile write cache enabled
                    const auto length = header + 20;
                    if (ten) { PutBig(response, length - 2, 2); response[3] = (disk->Flags & QcRamReadOnly) ? 0x80 : 0; }
                    else { response[0] = static_cast<UCHAR>(length - 1); response[2] = (disk->Flags & QcRamReadOnly) ? 0x80 : 0; }
                    const auto returned = min(length, min(available, static_cast<ULONG>(ten ? Big(cdb + 7, 2) : cdb[4])));
                    if (returned) RtlCopyMemory(data, response, returned); srb->DataTransferLength = returned;
                }
            }
            else if (opcode == 0x42) // UNMAP: validate every descriptor before changing any bytes.
            {
                const auto length = static_cast<ULONG>(Big(cdb + 7, 2));
                if (disk->Flags & QcRamReadOnly) Sense(srb, 7, 0x27);
                else if (disk->Flags & QcRamFrozen) { srb->SrbStatus = SRB_STATUS_BUSY; srb->DataTransferLength = 0; }
                else if (length < 8 || available < length || Big(data + 2, 2) % 16 || Big(data + 2, 2) > length - 8 || Big(data + 2, 2) > 16) Sense(srb, 5, 0x24);
                else
                {
                    const auto descriptors = static_cast<ULONG>(Big(data + 2, 2)); bool valid = true;
                    for (ULONG i = 8; i < 8 + descriptors; i += 16)
                    {
                        const auto lba = Big(data + i, 8), blocks = Big(data + i + 8, 4);
                        if (lba > disk->Capacity / disk->SectorBytes || blocks > disk->Capacity / disk->SectorBytes - lba || blocks > QcRamTransferBytes / disk->SectorBytes) valid = false;
                    }
                    if (!valid) Sense(srb, 5, 0x21);
                    else
                    {
                        for (ULONG i = 8; i < 8 + descriptors; i += 16)
                        {
                            auto offset = Big(data + i, 8) * disk->SectorBytes, bytes = Big(data + i + 8, 4) * disk->SectorBytes;
                            while (bytes) { auto part = static_cast<ULONG>(min(bytes, static_cast<ULONGLONG>(QcRamTransferBytes))); Copy(disk, offset, nullptr, part, true, true); offset += part; bytes -= part; }
                        }
                        ++disk->Trims; ++disk->Generation; srb->DataTransferLength = length;
                    }
                }
            }
            else if (opcode == 0xA0) // REPORT LUNS; only explicitly published devices.
            {
                UCHAR response[8 + QcRamMaxDisks * 8]{}; ULONG count = 0;
                KIRQL tableIrql; KeAcquireSpinLock(&adapter->TableLock, &tableIrql);
                for (ULONG i = 0; i < QcRamMaxDisks; ++i)
                    if (adapter->Disks[i] && (adapter->Disks[i]->Flags & QcRamPublished)) response[8 + count++ * 8 + 1] = static_cast<UCHAR>(i);
                KeReleaseSpinLock(&adapter->TableLock, tableIrql);
                PutBig(response, count * 8, 4); auto length = min(8 + count * 8, min(available, static_cast<ULONG>(Big(cdb + 6, 4))));
                if (length) RtlCopyMemory(data, response, length); srb->DataTransferLength = length;
            }
            else Sense(srb, 5, 0x20);
            if (srb->SrbStatus & SRB_STATUS_ERROR) ++disk->Errors;
            KeReleaseSpinLock(&disk->IoLock, irql);
        }
    }
    if (disk) DereferenceDisk(disk);
    StorPortNotification(RequestComplete, adapter, srb);
    return TRUE;
}
ULONG FindAdapter(PVOID extension, PVOID, PVOID, PVOID, PCHAR, PPORT_CONFIGURATION_INFORMATION configuration, PBOOLEAN again)
{
    auto adapter = static_cast<ADAPTER*>(extension);
    KeInitializeSpinLock(&adapter->TableLock); ExInitializeFastMutex(&adapter->ControlLock);
    if (!NT_SUCCESS(ExUuidCreate(&adapter->Epoch))) return SP_RETURN_ERROR;
    configuration->VirtualDevice = TRUE; configuration->NumberOfBuses = 1;
    configuration->MaximumNumberOfTargets = 1; configuration->MaximumNumberOfLogicalUnits = QcRamMaxDisks;
    configuration->MaximumTransferLength = QcRamTransferBytes; configuration->NumberOfPhysicalBreaks = MAXULONG;
    configuration->ScatterGather = TRUE; configuration->Master = TRUE;
    configuration->CachesData = TRUE; configuration->AlignmentMask = 0;
    configuration->WmiDataProvider = FALSE; *again = FALSE;
    return SP_RETURN_FOUND;
}
BOOLEAN Initialize(PVOID) { return TRUE; }
BOOLEAN ResetBus(PVOID, ULONG) { return TRUE; }
SCSI_ADAPTER_CONTROL_STATUS AdapterControl(PVOID extension, SCSI_ADAPTER_CONTROL_TYPE type, PVOID parameters)
{
    auto adapter = static_cast<ADAPTER*>(extension);
    if (type == ScsiQuerySupportedControlTypes)
    {
        auto list = static_cast<PSCSI_SUPPORTED_CONTROL_TYPE_LIST>(parameters);
        if (list->MaxControlType > ScsiStopAdapter) list->SupportedTypeList[ScsiStopAdapter] = TRUE;
        if (list->MaxControlType > ScsiRestartAdapter) list->SupportedTypeList[ScsiRestartAdapter] = TRUE;
        return ScsiAdapterControlSuccess;
    }
    if (type == ScsiStopAdapter) { adapter->Stopped = TRUE; return ScsiAdapterControlSuccess; }
    if (type == ScsiRestartAdapter) { adapter->Stopped = FALSE; return ScsiAdapterControlSuccess; }
    return ScsiAdapterControlUnsuccessful;
}
void FreeAdapter(PVOID extension)
{
    auto adapter = static_cast<ADAPTER*>(extension);
    for (ULONG i = 0; i < QcRamMaxDisks; ++i)
    {
        auto disk = adapter->Disks[i]; if (!disk) continue;
        disk->Removing = TRUE; adapter->Disks[i] = nullptr;
        WaitReferences(disk);
        FreeDisk(disk);
    }
}
extern "C" NTSTATUS DriverEntry(PDRIVER_OBJECT driver, PUNICODE_STRING registry)
{
    Driver = driver;
    HW_INITIALIZATION_DATA initialization{};
    initialization.HwInitializationDataSize = sizeof(initialization); initialization.AdapterInterfaceType = Internal;
    initialization.HwFindAdapter = reinterpret_cast<PVOID>(FindAdapter); initialization.HwInitialize = Initialize; initialization.HwStartIo = StartIo;
    initialization.HwResetBus = ResetBus; initialization.HwAdapterControl = AdapterControl;
    initialization.HwFreeAdapterResources = FreeAdapter; initialization.HwProcessServiceRequest = ServiceRequest;
    initialization.HwCompleteServiceIrp = CompleteService; initialization.DeviceExtensionSize = sizeof(ADAPTER);
    initialization.MapBuffers = STOR_MAP_ALL_BUFFERS_INCLUDING_READ_WRITE; initialization.TaggedQueuing = TRUE;
    initialization.AutoRequestSense = TRUE; initialization.MultipleRequestPerLu = TRUE;
    return StorPortInitialize(driver, registry, &initialization, nullptr);
}
