// SPDX-License-Identifier: MIT
// SCSI command handling for the RAM disks (Storport StartIo).
#include "provider.h"

static ULONGLONG Big(const UCHAR* bytes, ULONG count)
{
    ULONGLONG value = 0; for (ULONG i = 0; i < count; ++i) value = (value << 8) | bytes[i]; return value;
}
static void PutBig(PUCHAR bytes, ULONGLONG value, ULONG count)
{
    for (ULONG i = 0; i < count; ++i) { bytes[count - i - 1] = static_cast<UCHAR>(value); value >>= 8; }
}
static void Serial(const DISK* disk, char* serial)
{
    static const char digits[] = "0123456789ABCDEF";
    serial[0] = 'Q'; serial[1] = 'C'; auto bytes = reinterpret_cast<const UCHAR*>(&disk->Resource);
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
static void Busy(PSCSI_REQUEST_BLOCK srb) { srb->SrbStatus = SRB_STATUS_BUSY; srb->DataTransferLength = 0; }

enum class Outcome { Completed, Unsupported, Queued };

// READ/WRITE(10/16). Writes pass the store's gate shared with Direct access.
static Outcome Transfer(ADAPTER* adapter, DISK* disk, PSCSI_REQUEST_BLOCK srb, PUCHAR data, bool write, ULONGLONG lba, ULONGLONG blocks)
{
    auto& store = disk->Store;
    if (lba > MAXULONGLONG / store.SectorBytes || blocks > MAXULONG / store.SectorBytes) { Sense(srb, 5, 0x21); return Outcome::Completed; }
    const auto offset = lba * store.SectorBytes; const auto bytes = static_cast<ULONG>(blocks * store.SectorBytes);
    if (srb->DataTransferLength < bytes || !QcRamStoreBounds(&store, offset, bytes)) { Sense(srb, 5, 0x21); return Outcome::Completed; }
    const auto started = QcRamTimingStart(&store);
    if (write)
    {
        const auto admission = QcRamStoreBeginWrite(&store);
        if (admission == QcRamAdmission::ReadOnly) { Sense(srb, 7, 0x27); return Outcome::Completed; }
        if (admission == QcRamAdmission::Frozen) { Busy(srb); return Outcome::Completed; }
        if (bytes) QcRamStoreChanged(&store);
        InterlockedAdd64(&disk->WriteBytes, bytes); InterlockedIncrement64(&disk->WriteRequests);
    }
    else { InterlockedAdd64(&disk->ReadBytes, bytes); InterlockedIncrement64(&disk->ReadRequests); }
    srb->DataTransferLength = bytes;
    if (bytes && UseWorker(adapter, disk, bytes, write))
    {
        // Return to the submitter at once; a worker on another processor copies and
        // completes. The disk reference and the admitted write move with the request.
        auto request = static_cast<REQUEST*>(srb->SrbExtension);
        request->Srb = srb; request->Disk = disk; request->Buffer = data; request->Offset = offset;
        request->Bytes = bytes; request->Write = write; request->Started = started;
        QueueTransfer(adapter, request);
        return Outcome::Queued;
    }
    if (bytes && !write && adapter->WorkerCount > 1 && bytes >= 2 * SplitChunk) CopySplit(adapter, &store, offset, data, bytes, false);
    else if (bytes) QcRamStoreCopy(&store, offset, data, bytes, write);
    if (write) QcRamStoreEndWrite(&store);
    QcRamTimingEnd(&store, write, started);
    return Outcome::Completed;
}

// UNMAP: validate every descriptor before changing any bytes.
static void Unmap(DISK* disk, PSCSI_REQUEST_BLOCK srb, PUCHAR data, const UCHAR* cdb)
{
    auto& store = disk->Store; const auto available = srb->DataTransferLength;
    const auto admission = QcRamStoreBeginWrite(&store);
    if (admission == QcRamAdmission::ReadOnly) { Sense(srb, 7, 0x27); return; }
    if (admission == QcRamAdmission::Frozen) { Busy(srb); return; }
    const auto length = static_cast<ULONG>(Big(cdb + 7, 2));
    if (length < 8 || available < length || Big(data + 2, 2) % 16 || Big(data + 2, 2) > length - 8 || Big(data + 2, 2) > 16) Sense(srb, 5, 0x24);
    else
    {
        const auto descriptors = static_cast<ULONG>(Big(data + 2, 2)); bool valid = true;
        const auto sectors = store.Capacity / store.SectorBytes;
        for (ULONG i = 8; i < 8 + descriptors; i += 16)
        {
            const auto lba = Big(data + i, 8), blocks = Big(data + i + 8, 4);
            if (lba > sectors || blocks > sectors - lba || blocks > QcRamTransferBytes / store.SectorBytes) valid = false;
        }
        if (!valid) Sense(srb, 5, 0x21);
        else
        {
            for (ULONG i = 8; i < 8 + descriptors; i += 16)
            {
                auto offset = Big(data + i, 8) * store.SectorBytes, bytes = Big(data + i + 8, 4) * store.SectorBytes;
                while (bytes) { auto part = static_cast<ULONG>(min(bytes, static_cast<ULONGLONG>(QcRamTransferBytes))); QcRamStoreCopy(&store, offset, nullptr, part, true, true); offset += part; bytes -= part; }
            }
            InterlockedIncrement64(&disk->Trims); QcRamStoreChanged(&store); srb->DataTransferLength = length;
        }
    }
    QcRamStoreEndWrite(&store);
}

static Outcome Execute(ADAPTER* adapter, DISK* disk, PSCSI_REQUEST_BLOCK srb, PUCHAR data)
{
    const auto& store = disk->Store; auto cdb = srb->Cdb; const auto available = srb->DataTransferLength;
    const auto opcode = cdb[0];
    if (opcode == 0x00 || opcode == 0x1B || opcode == 0x1E) srb->DataTransferLength = 0; // ready/start/prevent
    else if (opcode == 0x35 || opcode == 0x91) { InterlockedIncrement64(&disk->Flushes); srb->DataTransferLength = 0; }
    else if (opcode == 0x28 || opcode == 0x2A || opcode == 0x88 || opcode == 0x8A)
    {
        const bool write = opcode == 0x2A || opcode == 0x8A, large = opcode == 0x88 || opcode == 0x8A;
        return Transfer(adapter, disk, srb, data, write, Big(cdb + 2, large ? 8 : 4), Big(cdb + (large ? 10 : 7), large ? 4 : 2));
    }
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
            PutBig(response + 8, QcRamTransferBytes / store.SectorBytes, 4);
            PutBig(response + 20, QcRamTransferBytes / store.SectorBytes, 4);
            PutBig(response + 24, 1, 4); PutBig(response + 28, 1, 4); length = 64;
        }
        else if (cdb[2] == 0xB1) { response[1] = 0xB1; response[3] = 60; response[5] = 1; length = 64; }
        else if (cdb[2] == 0xB2) { response[1] = 0xB2; response[3] = 4; response[5] = 0x84; response[6] = 2; length = 8; }
        else { Sense(srb, 5, 0x24); return Outcome::Unsupported; }
        length = min(length, min(available, static_cast<ULONG>(cdb[4]))); if (length) RtlCopyMemory(data, response, length); srb->DataTransferLength = length;
    }
    else if (opcode == 0x25 || (opcode == 0x9E && (cdb[1] & 31) == 0x10))
    {
        const ULONG length = opcode == 0x25 ? 8 : 32;
        if (available < length) Sense(srb, 5, 0x24);
        else
        {
            RtlZeroMemory(data, length); const auto last = store.Capacity / store.SectorBytes - 1;
            if (length == 8) { PutBig(data, min(last, static_cast<ULONGLONG>(MAXULONG)), 4); PutBig(data + 4, store.SectorBytes, 4); }
            else { PutBig(data, last, 8); PutBig(data + 8, store.SectorBytes, 4); data[14] = 0xC0; }
            srb->DataTransferLength = length;
        }
    }
    else if (opcode == 0x1A || opcode == 0x5A)
    {
        UCHAR response[64]{}; const bool ten = opcode == 0x5A; const auto header = ten ? 8U : 4U;
        const auto page = cdb[2] & 0x3F;
        if (page != 8 && page != 0x3F) { Sense(srb, 5, 0x24); return Outcome::Unsupported; }
        response[header] = 8; response[header + 1] = 18; response[header + 2] = 4; // volatile write cache enabled
        const auto length = header + 20; const UCHAR protect = (ReadNoFence(&store.Flags) & QcRamReadOnly) ? 0x80 : 0;
        if (ten) { PutBig(response, length - 2, 2); response[3] = protect; }
        else { response[0] = static_cast<UCHAR>(length - 1); response[2] = protect; }
        const auto returned = min(length, min(available, static_cast<ULONG>(ten ? Big(cdb + 7, 2) : cdb[4])));
        if (returned) RtlCopyMemory(data, response, returned); srb->DataTransferLength = returned;
    }
    else if (opcode == 0x42) Unmap(disk, srb, data, cdb);
    else if (opcode == 0xA0) // REPORT LUNS; only explicitly published devices.
    {
        UCHAR response[8 + QcRamMaxDisks * 8]{}; ULONG count = 0;
        KIRQL irql; KeAcquireSpinLock(&adapter->TableLock, &irql);
        for (ULONG i = 0; i < QcRamMaxDisks; ++i)
            if (adapter->Disks[i] && (ReadNoFence(&adapter->Disks[i]->Store.Flags) & QcRamPublished)) response[8 + count++ * 8 + 1] = static_cast<UCHAR>(i);
        KeReleaseSpinLock(&adapter->TableLock, irql);
        PutBig(response, count * 8, 4); auto length = min(8 + count * 8, min(available, static_cast<ULONG>(Big(cdb + 6, 4))));
        if (length) RtlCopyMemory(data, response, length); srb->DataTransferLength = length;
    }
    else { Sense(srb, 5, 0x20); return Outcome::Unsupported; }
    return Outcome::Completed;
}

BOOLEAN StartIo(PVOID extension, PSCSI_REQUEST_BLOCK srb)
{
    auto adapter = static_cast<ADAPTER*>(extension);
    auto disk = srb->PathId == 0 && srb->TargetId == 0 ? ReferenceDisk(adapter, srb->Lun, true) : nullptr;
    srb->ScsiStatus = SCSISTAT_GOOD; srb->SrbStatus = SRB_STATUS_SUCCESS;
    if (!disk) { srb->SrbStatus = SRB_STATUS_NO_DEVICE; srb->DataTransferLength = 0; }
    else if (srb->Function == SRB_FUNCTION_FLUSH || srb->Function == SRB_FUNCTION_SHUTDOWN)
    {
        InterlockedIncrement64(&disk->Flushes); srb->DataTransferLength = 0;
    }
    else if (srb->Function != SRB_FUNCTION_EXECUTE_SCSI) { srb->SrbStatus = SRB_STATUS_INVALID_REQUEST; srb->DataTransferLength = 0; }
    else
    {
        PVOID mapped = nullptr;
        if (srb->DataTransferLength && StorPortGetSystemAddress(adapter, srb, &mapped) != STOR_STATUS_SUCCESS) Sense(srb, 4, 0x44);
        else
        {
            const auto outcome = Execute(adapter, disk, srb, static_cast<PUCHAR>(mapped));
            if (outcome == Outcome::Queued) return TRUE; // A worker completes it; do not touch the SRB.
            // Windows probes optional opcodes, VPD and mode pages on every new disk; those
            // illegal-request replies are not storage errors.
            if (outcome != Outcome::Unsupported && (srb->SrbStatus & ~(SRB_STATUS_AUTOSENSE_VALID | SRB_STATUS_QUEUE_FROZEN)) == SRB_STATUS_ERROR)
                InterlockedIncrement64(&disk->Errors);
        }
    }
    if (disk) DereferenceDisk(disk);
    StorPortNotification(RequestComplete, adapter, srb);
    return TRUE;
}
