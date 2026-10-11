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
    // Reply is QC_RAM_REQUEST followed by QC_RAM_STATISTICS. Offered when Capabilities has QcRamStatisticsSupported.
    QcRamStatistics,
    // Flags: QcRamTiming on or off. Turning it on starts the timing counters from zero.
    QcRamSetTiming,
    // Reply is QC_RAM_REQUEST followed by QC_RAM_PHYSICAL_MAP. Offset: the caller's estimate of the physical
    // page span (the map covers at least every page of the disk). Offered with QcRamPhysicalMapSupported.
    QcRamPhysicalMap,
    // Request-local test: Offset is the physical slab count after which this NEW creation fails.
    // No hook survives the request; no existing disk is targeted. Same administrator authorization as Create.
    QcRamDeveloperCreateAllocationFailure = 0x100,
    // Versioned per-resource research payload; no existing reply/flag changes.
    QcRamCoordination = 0x101,
    // Offset 0/1 disables/enables diagnostics; enable resets counters after rundown.
    QcRamSetCoordination = 0x102
};
enum QC_RAM_FLAGS : ULONG
{
    QcRamPublished = 1, QcRamReadOnly = 2, QcRamFrozen = 4,
    QcRamDirect = 8,            // Create: offer the store to the volume filter for Direct access.
    QcRamDirectRegistered = 16, // Reply: the volume filter accepted the store.
    QcRamTiming = 32,           // Time every transfer (SCSI and Direct). Off at creation.
    QcRamStatisticsSupported = 64, // Capabilities reply: Statistics and SetTiming are available.
    QcRamPhysicalMapSupported = 128, // Capabilities reply: PhysicalMap is available.
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
// Request counts of the SCSI path (Direct requests are counted by the volume filter), and
// transfer timing of both paths while QcRamTiming is on. Ticks are in Frequency units.
struct QC_RAM_STATISTICS
{
    ULONGLONG ReadRequests, WriteRequests, Frequency;
    ULONGLONG TimedReads, TimedWrites, ReadTicks, WriteTicks, MaxReadTicks, MaxWriteTicks;
};
static_assert(sizeof(QC_RAM_STATISTICS) == 72, "Managed/native RAM statistics ABI size");
// Where the disk's pages sit in physical memory: Counts[i] pages fall in bin i of [0, SpanPages),
// and Runs is the number of physically contiguous runs in disk order. Pages are locked; they never move.
constexpr ULONG QcRamPhysicalBins = 1024;
struct QC_RAM_PHYSICAL_MAP
{
    ULONGLONG SpanPages, Pages, Runs;
    ULONG Bins, Reserved;
    ULONG Counts[QcRamPhysicalBins];
};
static_assert(sizeof(QC_RAM_PHYSICAL_MAP) == 32 + 4 * QcRamPhysicalBins, "Managed/native RAM physical map ABI size");
// Capabilities also reports request-local fault proof: Resource = last injected resource,
// Generation = injection count, Transfers = its allocated slabs. StartupSession retains its own generation semantics.
