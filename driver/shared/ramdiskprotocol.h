// SPDX-License-Identifier: MIT
#pragma once
#include <ntddk.h>
// Buffered, bounded Storport service IRPs. No user addresses in this ABI.
constexpr ULONG QcRamMagic = 0x52444351, QcRamVersion = 1, QcRamMaxDisks = 32, QcRamTransferBytes = 1 << 20;
enum QC_RAM_ACTION : ULONG
{
    QcRamCapabilities = 1, QcRamEnumerate, QcRamCreate, QcRamQuery, QcRamRead,
    QcRamWrite, QcRamPublish, QcRamFreeze, QcRamThaw, QcRamRemove, QcRamSetReadOnly, QcRamStartupSession
};
enum QC_RAM_FLAGS : ULONG { QcRamPublished = 1, QcRamReadOnly = 2, QcRamFrozen = 4 };
struct QC_RAM_REQUEST
{
    ULONG Magic, Version, Size, Action;
    GUID Resource, Epoch;
    ULONGLONG Creation, Capacity, Offset, Generation, ReservedBytes;
    GUID FreezeOwner;
    ULONG SectorBytes, Flags, TransferBytes, Slot;
    ULONGLONG ReadBytes, WriteBytes, Flushes, Trims, Errors, Transfers;
};
static_assert(sizeof(QC_RAM_REQUEST) == 168, "Managed/native RAM ABI size");
