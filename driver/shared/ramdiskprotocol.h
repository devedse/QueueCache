// SPDX-License-Identifier: MIT
#pragma once
#include <ntddk.h>
// Buffered, bounded Storport service IRPs. No user addresses in this ABI.
// Keep Version 1 and extend compatibly: the installer's update preflight runs the NEW CLI
// against the still-installed OLD provider to prove no RAM disk is live; a version change
// makes that check fail and blocks every update (found on the VM).
constexpr ULONG QcRamMagic = 0x52444351, QcRamVersion = 1, QcRamMaxDisks = 32, QcRamTransferBytes = 1 << 20, QcRamSlabBytes = 4 << 20;
enum QC_RAM_ACTION : ULONG
{
    QcRamCapabilities = 1, QcRamEnumerate, QcRamCreate, QcRamQuery, QcRamRead,
    QcRamWrite, QcRamPublish, QcRamFreeze, QcRamThaw, QcRamRemove, QcRamSetReadOnly, QcRamStartupSession,
    // Request-local test: Offset is the physical slab count after which this NEW creation fails.
    // No hook survives the request; no existing disk is targeted. Same administrator authorization as Create.
    QcRamDeveloperCreateAllocationFailure = 0x100
};
enum QC_RAM_FLAGS : ULONG
{
    QcRamPublished = 1, QcRamReadOnly = 2, QcRamFrozen = 4,
    QcRamDirect = 8,            // Create: offer the store to the volume filter for Direct access.
    QcRamDirectRegistered = 16, // Reply: the volume filter accepted the store.
};
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
// Capabilities also reports request-local fault proof: Resource = last injected resource,
// Generation = injection count, Transfers = its allocated slabs. StartupSession retains its own generation semantics.
