// SPDX-License-Identifier: MIT
#pragma once
#include <ntifs.h>
#include "observation.h"
#include "../shared/ramview.h"

// Direct access: the volume filter serves reads and writes for a volume on a QueueCache RAM
// disk straight from the provider's store, above the volume, partition, disk class and
// Storport layers. The data stays in one place (the store); the standard path keeps
// working underneath for everything else. Direct access steps aside whenever a layer below
// could treat I/O differently: BitLocker, a shadow copy (writes), write protection, and
// any control request not known to be harmless (QcRamDirectHarmlessControl, observation.h).
// Each step aside is permanent for the binding and reported with its reason.

// Volume-handle controls (user mode).
#define IOCTL_QCACHE_RAM_DIRECT_STATE_V1 CTL_CODE(0x8844UL, 0xD18UL, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_QCACHE_RAM_DIRECT_BIND_V1 CTL_CODE(0x8844UL, 0xD19UL, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS)
enum QC_RAM_DIRECT_ACCESS : ULONG { QcRamDirectReads = 1, QcRamDirectWrites = 2 };
enum QC_RAM_DIRECT_REASON : ULONG
{
    QcRamDirectNoReason = 0,
    QcRamDirectNotRamDisk = 1,      // The volume is not on a QueueCache RAM disk.
    QcRamDirectNotOffered = 2,      // The provider did not register this disk for Direct access.
    QcRamDirectCacheActive = 3,     // A QueueCache cache task owns the volume.
    QcRamDirectLayout = 4,          // Not exactly one extent inside the RAM disk.
    QcRamDirectUnknownDriver = 5,   // An unrecognized driver in the volume or disk stack (Driver).
    QcRamDirectBitLocker = 6,
    QcRamDirectSnapshot = 7,        // Writes: a shadow copy exists or was taken.
    QcRamDirectWriteProtected = 8,  // Writes: the volume or disk is write-protected.
    QcRamDirectControl = 9,         // An unrecognized control request (Detail is its code).
    QcRamDirectRemoved = 10,        // The RAM disk or the volume went away.
    QcRamDirectQueryFailed = 11,    // A required identity query failed (Detail is the NTSTATUS).
    QcRamDirectInUse = 12,          // Another volume already uses this RAM disk directly.
};
constexpr ULONG QcRamDirectBindReadsOnly = 1; // The caller found an existing shadow copy.
struct QC_RAM_DIRECT_BIND { ULONG Size, Version, Flags, Reserved; };
struct QC_RAM_DIRECT_STATE
{
    ULONG Size, Version;
    ULONG Access;   // QC_RAM_DIRECT_ACCESS
    ULONG Reason;   // QC_RAM_DIRECT_REASON for whatever is not Direct
    ULONG Detail;
    ULONG Reserved;
    GUID Resource;
    ULONGLONG Offset, Length; // The volume's extent on the RAM disk.
    ULONGLONG ReadRequests, WriteRequests, ReadBytes, WriteBytes, Declined;
    WCHAR Driver[32];
};
static_assert(sizeof(QC_RAM_DIRECT_BIND) == 16, "Managed/native Direct bind ABI size");
static_assert(sizeof(QC_RAM_DIRECT_STATE) == 160, "Managed/native Direct state ABI size");

struct QC_RAM_VIEW;
// Per volume (in the filter's device extension). Dispatch reads Access and the hot-path
// fields without a lock; transitions are serialized by the view registry.
struct QC_RAM_BINDING
{
    EX_RUNDOWN_REF Rundown;   // Direct requests in flight; run down whenever not bound.
    volatile LONG Access;     // QC_RAM_DIRECT_ACCESS
    volatile LONG Writing;    // Direct writes in flight (writes can end on their own).
    volatile LONG Reason;     // First reason something is not Direct.
    ULONG Detail;
    QC_RAM_STORE* Store;      // Valid while Rundown is held.
    QC_RAM_LARGE_COPY* LargeCopy;
    PVOID LargeCopyContext;
    ULONGLONG Offset, Length; // Volume extent on the RAM disk.
    UCHAR Signature[8];       // Boot sector OEM name at bind; BitLocker changes it.
    BOOLEAN SnapshotSeen;     // A flush-and-hold passed this volume (sticky).
    QC_RAM_VIEW* View;        // Registry lock.
    GUID Resource;
    WCHAR Driver[32];
    volatile LONG64 ReadRequests, WriteRequests, ReadBytes, WriteBytes, Declined;
};

// An admitted asynchronous read owns binding rundown until copied, cancelled or
// declined. Its caller's MDL stays locked under the original pending IRP.
struct QC_RAM_READ
{
    QC_RAM_BINDING* Binding;
    QC_RAM_STORE* Store;
    PUCHAR Buffer;
    ULONGLONG Offset;
    LONG64 Started;
    ULONG Length;
};
bool QcRamDirectPrepareRead(QC_RAM_BINDING* binding, PIRP irp, QC_RAM_READ* read);
void QcRamDirectReleaseRead(QC_RAM_READ* read);
// STATUS_NOT_FOUND means the signature changed: rundown is already released,
// and the owner must use the standard lower path. No store pointer remains live.
NTSTATUS QcRamDirectFinishRead(QC_RAM_READ* read, PIRP irp);

void QcRamDirectInitialize();                                // DriverEntry
void QcRamDirectInitialize(QC_RAM_BINDING* binding);        // AddDevice
NTSTATUS QcRamViewControl(PIRP irp);                         // Budget device, kernel registration
// Hot path. True when served: IoStatus is set and the caller completes the IRP.
bool QcRamDirectTransfer(QC_RAM_BINDING* binding, PIRP irp);
void QcRamDirectObserveControl(QC_RAM_BINDING* binding, ULONG code);
// PASSIVE_LEVEL. lower is the next device in the volume stack.
void QcRamDirectBind(QC_RAM_BINDING* binding, PDEVICE_OBJECT lower, const GUID& resource, ULONGLONG volumeBytes, ULONG flags);
// Records why a volume cannot use Direct access (when it is not bound).
void QcRamDirectRefuse(QC_RAM_BINDING* binding, ULONG reason);
void QcRamDirectState(QC_RAM_BINDING* binding, QC_RAM_DIRECT_STATE* state);
void QcRamDirectEnd(QC_RAM_BINDING* binding, ULONG reason); // PASSIVE_LEVEL, e.g. volume removal.
