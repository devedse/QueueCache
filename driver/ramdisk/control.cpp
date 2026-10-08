// SPDX-License-Identifier: MIT
// Management requests (Storport service IRPs from the managed-disk broker).
#include "provider.h"

static void Snapshot(const DISK* disk, QC_RAM_REQUEST* reply, ULONG slot)
{
    const auto& store = disk->Store;
    reply->Resource = disk->Resource; reply->Epoch = disk->Epoch; reply->Creation = disk->Creation;
    reply->Capacity = store.Capacity; reply->Generation = static_cast<ULONGLONG>(ReadNoFence64(&store.Generation));
    reply->ReservedBytes = disk->ReservedBytes; reply->SectorBytes = store.SectorBytes;
    reply->Flags = static_cast<ULONG>(ReadNoFence(&store.Flags)) | (disk->ViewRegistered ? QcRamDirectRegistered : 0);
    reply->Slot = slot; reply->FreezeOwner = disk->FreezeOwner;
    reply->ReadBytes = ReadNoFence64(&disk->ReadBytes); reply->WriteBytes = ReadNoFence64(&disk->WriteBytes);
    reply->Flushes = ReadNoFence64(&disk->Flushes); reply->Trims = ReadNoFence64(&disk->Trims);
    reply->Errors = ReadNoFence64(&disk->Errors); reply->Transfers = ReadNoFence64(&disk->Transfers);
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

// Control requests on an existing disk. Serialized by ControlLock; the SCSI and Direct
// paths only see the store's interlocked flags and write gate.
static NTSTATUS Change(ADAPTER* adapter, DISK* disk, const QC_RAM_REQUEST& command, QC_RAM_REQUEST* reply,
    ULONG input, ULONG output, ULONG_PTR* returned)
{
    auto& store = disk->Store;
    const auto flags = static_cast<ULONG>(ReadNoFence(&store.Flags));
    switch (command.Action)
    {
    case QcRamEnumerate: case QcRamQuery: return STATUS_SUCCESS;
    case QcRamRead: case QcRamWrite:
        if (!command.TransferBytes || command.TransferBytes > QcRamTransferBytes || !QcRamStoreBounds(&store, command.Offset, command.TransferBytes) ||
            (command.Action == QcRamRead && output < sizeof(*reply) + command.TransferBytes) ||
            (command.Action == QcRamWrite && input != sizeof(*reply) + command.TransferBytes)) return STATUS_INVALID_PARAMETER;
        if (command.Action == QcRamWrite && (flags & (QcRamReadOnly | QcRamPublished))) return STATUS_MEDIA_WRITE_PROTECTED;
        if ((flags & QcRamPublished) && !(flags & QcRamFrozen)) return STATUS_DEVICE_BUSY;
        if ((flags & QcRamFrozen) && !IsEqualGUID(disk->FreezeOwner, command.FreezeOwner)) return STATUS_ACCESS_DENIED;
        // Unpublished, or frozen by this caller: no SCSI or Direct write can run concurrently.
        QcRamStoreCopy(&store, command.Offset, reinterpret_cast<PUCHAR>(reply + 1), command.TransferBytes, command.Action == QcRamWrite);
        InterlockedIncrement64(&disk->Transfers);
        if (command.Action == QcRamWrite) QcRamStoreChanged(&store);
        else *returned = sizeof(*reply) + command.TransferBytes;
        return STATUS_SUCCESS;
    case QcRamPublish:
        if (flags & QcRamPublished) return STATUS_DEVICE_BUSY;
        InterlockedOr(&store.Flags, QcRamPublished);
        StorPortNotification(BusChangeDetected, adapter, 0);
        return STATUS_SUCCESS;
    case QcRamFreeze:
        if (IsEqualGUID(command.FreezeOwner, EmptyGuid)) return STATUS_INVALID_PARAMETER;
        if (flags & QcRamFrozen) return STATUS_DEVICE_BUSY;
        disk->FreezeOwner = command.FreezeOwner;
        InterlockedOr(&store.Flags, QcRamFrozen);
        DrainWrites(disk);
        return STATUS_SUCCESS;
    case QcRamThaw:
        if (!(flags & QcRamFrozen) || !IsEqualGUID(disk->FreezeOwner, command.FreezeOwner)) return STATUS_ACCESS_DENIED;
        InterlockedAnd(&store.Flags, ~static_cast<LONG>(QcRamFrozen));
        disk->FreezeOwner = EmptyGuid;
        return STATUS_SUCCESS;
    case QcRamStatistics:
    {
        if (output < sizeof(*reply) + sizeof(QC_RAM_STATISTICS) || input != sizeof(*reply)) return STATUS_INVALID_PARAMETER;
        auto stats = reinterpret_cast<QC_RAM_STATISTICS*>(reply + 1);
        LARGE_INTEGER frequency; KeQueryPerformanceCounter(&frequency);
        stats->ReadRequests = ReadNoFence64(&disk->ReadRequests); stats->WriteRequests = ReadNoFence64(&disk->WriteRequests);
        stats->Frequency = static_cast<ULONGLONG>(frequency.QuadPart);
        stats->TimedReads = ReadNoFence64(&store.TimedReads); stats->TimedWrites = ReadNoFence64(&store.TimedWrites);
        stats->ReadTicks = ReadNoFence64(&store.ReadTicks); stats->WriteTicks = ReadNoFence64(&store.WriteTicks);
        stats->MaxReadTicks = ReadNoFence64(&store.MaxReadTicks); stats->MaxWriteTicks = ReadNoFence64(&store.MaxWriteTicks);
        *returned = sizeof(*reply) + sizeof(QC_RAM_STATISTICS);
        return STATUS_SUCCESS;
    }
    case QcRamPhysicalMap:
    {
        if (output < sizeof(*reply) + sizeof(QC_RAM_PHYSICAL_MAP) || input != sizeof(*reply)) return STATUS_INVALID_PARAMETER;
        auto map = reinterpret_cast<QC_RAM_PHYSICAL_MAP*>(reply + 1);
        RtlZeroMemory(map, sizeof(*map));
        // Two passes over the locked pages' frame numbers: the highest page first, so the span covers them all.
        ULONGLONG highest = 0;
        for (ULONG s = 0; s < store.SlabCount; ++s)
        {
            const auto pfns = MmGetMdlPfnArray(store.Slabs[s].Mdl);
            for (ULONG p = 0; p < BYTES_TO_PAGES(MmGetMdlByteCount(store.Slabs[s].Mdl)); ++p)
                highest = max(highest, static_cast<ULONGLONG>(pfns[p]));
        }
        map->SpanPages = max(command.Offset, highest + 1);
        map->Bins = QcRamPhysicalBins;
        ULONGLONG previous = 0;
        for (ULONG s = 0; s < store.SlabCount; ++s)
        {
            const auto pfns = MmGetMdlPfnArray(store.Slabs[s].Mdl);
            for (ULONG p = 0; p < BYTES_TO_PAGES(MmGetMdlByteCount(store.Slabs[s].Mdl)); ++p)
            {
                const auto pfn = static_cast<ULONGLONG>(pfns[p]);
                ++map->Counts[pfn * QcRamPhysicalBins / map->SpanPages];
                map->Runs += map->Pages++ == 0 || pfn != previous + 1 ? 1 : 0;
                previous = pfn;
            }
        }
        *returned = sizeof(*reply) + sizeof(QC_RAM_PHYSICAL_MAP);
        return STATUS_SUCCESS;
    }
    case QcRamSetTiming:
        if (command.Flags & ~QcRamTiming) return STATUS_INVALID_PARAMETER;
        if ((command.Flags & QcRamTiming) && !(flags & QcRamTiming))
        {
            InterlockedExchange64(&store.TimedReads, 0); InterlockedExchange64(&store.TimedWrites, 0);
            InterlockedExchange64(&store.ReadTicks, 0); InterlockedExchange64(&store.WriteTicks, 0);
            InterlockedExchange64(&store.MaxReadTicks, 0); InterlockedExchange64(&store.MaxWriteTicks, 0);
            InterlockedOr(&store.Flags, QcRamTiming);
        }
        else if (!(command.Flags & QcRamTiming)) InterlockedAnd(&store.Flags, ~static_cast<LONG>(QcRamTiming));
        return STATUS_SUCCESS;
    case QcRamSetReadOnly:
        if (command.Flags & ~QcRamReadOnly) return STATUS_INVALID_PARAMETER;
        if (command.Flags & QcRamReadOnly) { InterlockedOr(&store.Flags, QcRamReadOnly); DrainWrites(disk); }
        else InterlockedAnd(&store.Flags, ~static_cast<LONG>(QcRamReadOnly));
        return STATUS_SUCCESS;
    default: return STATUS_INVALID_DEVICE_REQUEST;
    }
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
        ((reply->Action >= QcRamCapabilities && reply->Action <= QcRamPhysicalMap) || reply->Action == QcRamDeveloperCreateAllocationFailure) &&
        input <= sizeof(*reply) + QcRamTransferBytes && output <= sizeof(*reply) + QcRamTransferBytes)
    {
        const auto command = *reply;
        // Service callbacks run at PASSIVE_LEVEL. Serialize control/transfer/remove,
        // leaving the SCSI hot path independent of this blocking mutex.
        KeEnterCriticalRegion();
        ExAcquireFastMutexUnsafe(&adapter->ControlLock);
        if (command.Action == QcRamCapabilities || command.Action == QcRamStartupSession)
        {
            RtlZeroMemory(reply, sizeof(*reply)); reply->Magic = QcRamMagic; reply->Version = QcRamVersion; reply->Size = sizeof(*reply);
            reply->Epoch = adapter->Epoch; reply->Capacity = 128ULL << 30; reply->Slot = QcRamMaxDisks;
            reply->TransferBytes = QcRamTransferBytes; status = STATUS_SUCCESS;
            if (command.Action == QcRamCapabilities)
            {
                reply->Resource = adapter->LastAllocationFailureResource;
                reply->Generation = adapter->InjectedAllocationFailures; reply->Transfers = adapter->LastAllocationFailureSlabs;
                reply->Flags = QcRamStatisticsSupported | QcRamPhysicalMapSupported;
            }
            if (command.Action == QcRamStartupSession) status = StartupSession(&reply->Epoch, &reply->Generation);
        }
        else if (command.Action == QcRamCreate || command.Action == QcRamDeveloperCreateAllocationFailure)
        {
            ULONG slot = QcRamMaxDisks; bool duplicate = false;
            for (ULONG i = 0; i < QcRamMaxDisks; ++i)
            {
                auto disk = adapter->Disks[i];
                if (!disk && slot == QcRamMaxDisks) slot = i;
                if (disk && IsEqualGUID(disk->Resource, command.Resource)) duplicate = true;
            }
            if (command.Action == QcRamDeveloperCreateAllocationFailure && (!command.Offset || command.Offset > MAXULONG ||
                input != sizeof(*reply) || output != sizeof(*reply))) status = STATUS_INVALID_PARAMETER;
            else if (duplicate) status = STATUS_OBJECT_NAME_COLLISION;
            else if (slot == QcRamMaxDisks) status = STATUS_INSUFFICIENT_RESOURCES;
            else
            {
                DISK* disk = nullptr; status = AllocateDisk(adapter, &command, irp, &disk,
                    command.Action == QcRamDeveloperCreateAllocationFailure ? static_cast<ULONG>(command.Offset) : 0);
                if (NT_SUCCESS(status))
                {
                    // Registered before publication: no volume, so no Direct request, exists yet.
                    if (command.Flags & QcRamDirect) RegisterView(adapter, disk);
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
                adapter->Disks[command.Slot] = nullptr;
                KeReleaseSpinLock(&adapter->TableLock, irql);
                DereferenceDisk(disk);
                WaitReferences(disk);
                FreeDisk(disk); disk = nullptr;
                StorPortNotification(BusChangeDetected, adapter, 0); status = STATUS_SUCCESS;
            }
            else
            {
                status = Change(adapter, disk, command, reply, input, output, &returned);
                Snapshot(disk, reply, command.Slot);
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
