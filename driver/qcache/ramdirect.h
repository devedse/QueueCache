// SPDX-License-Identifier: MIT
#pragma once
#include <ntifs.h>
#include "observation.h"
#include "../shared/offloadpolicy.h"
#include "../shared/ramview.h"

// Direct access: the volume filter serves reads and writes for a volume on a QueueCache RAM
// disk straight from the provider's store, above the volume, partition, disk class and
// Storport layers. The data stays in one place (the store); the standard path keeps
// working underneath for everything else. Direct access steps aside whenever a layer below
// could treat I/O differently: BitLocker, a shadow copy (writes), write protection, and
// any control request not known to be harmless. Each step aside is permanent for the
// binding and reported with its reason.

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

// Controls that change nothing a layer below would do with reads and writes. Anything else
// ends Direct access before it is forwarded.
// Binding requires every driver below to be a known Microsoft storage driver. Microsoft
// reserves device types below 0x8000 and declares state-changing controls with
// FILE_WRITE_ACCESS, so a Microsoft control that needs no write access only queries or
// notifies (found on the VM: BitLocker's status query on every new volume,
// IOCTL_DISK_MEDIA_REMOVAL and a FILE_DEVICE_MULTITIER_MEMORY query during lock/dismount).
// Known exceptions that change state without write access stay excluded, and
// FILE_DEVICE_UNKNOWN, which third-party drivers commonly reuse, always ends it.
constexpr ULONG QcFveDeviceType = 0x4556; // BitLocker (fvevol)
constexpr bool QcRamDirectStateChangeWithoutWriteAccess(ULONG code)
{
    return code == QcVolumeControl(13, FILE_ANY_ACCESS); // IOCTL_VOLUME_SET_GPT_ATTRIBUTES (read-only, hidden)
}
constexpr bool QcRamDirectHarmlessControl(ULONG code)
{
    if (DEVICE_TYPE_FROM_CTL_CODE(code) == 0x8844 || QcObservationCode(code) || QcVolumeManagementCode(code))
        return true; // QueueCache's own controls, identity/health queries, mount/volume state.
    if (code == IOCTL_STORAGE_MANAGE_DATA_SET_ATTRIBUTES)
        return true; // TRIM and allocation queries run on the standard path, coherent with Direct data.
    if (DEVICE_TYPE_FROM_CTL_CODE(code) == 0x53)
        return code != QcVolsnapFlushAndHoldWrites; // Snapshot management; flush-and-hold is handled separately.
    if (QcRamDirectStateChangeWithoutWriteAccess(code))
        return false;
    const auto type = DEVICE_TYPE_FROM_CTL_CODE(code);
    return type < 0x8000 && type != FILE_DEVICE_UNKNOWN && !((code >> 14) & FILE_WRITE_ACCESS);
}
static_assert(QcRamDirectHarmlessControl(IOCTL_STORAGE_QUERY_PROPERTY));
static_assert(QcRamDirectHarmlessControl(0x53C004));   // IOCTL_VOLSNAP_RELEASE_WRITES
static_assert(QcRamDirectHarmlessControl(0x00074804)); // IOCTL_DISK_MEDIA_REMOVAL, seen on the VM during lock/dismount
static_assert(QcRamDirectHarmlessControl(0x455610D4)); // BitLocker status query, seen on the VM on every new volume
static_assert(QcRamDirectHarmlessControl(IOCTL_DISK_GET_CACHE_INFORMATION));
static_assert(QcRamDirectHarmlessControl(0x0066001B)); // FILE_DEVICE_MULTITIER_MEMORY query, seen on the VM during lock/dismount
static_assert(!QcRamDirectHarmlessControl(QcVolsnapFlushAndHoldWrites));
static_assert(!QcRamDirectHarmlessControl(0x56C00C)); // IOCTL_VOLUME_OFFLINE
static_assert(!QcRamDirectHarmlessControl(QcVolumeControl(13, FILE_ANY_ACCESS))); // SET_GPT_ATTRIBUTES
static_assert(!QcRamDirectHarmlessControl(IOCTL_DISK_SET_DRIVE_LAYOUT_EX));
static_assert(!QcRamDirectHarmlessControl(IOCTL_DISK_SET_CACHE_INFORMATION));
static_assert(!QcRamDirectHarmlessControl(IOCTL_SCSI_PASS_THROUGH));
static_assert(!QcRamDirectHarmlessControl(CTL_CODE(QcFveDeviceType, 0x435, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS)));
static_assert(!QcRamDirectHarmlessControl(CTL_CODE(0x22, 0xAAA, METHOD_BUFFERED, FILE_ANY_ACCESS))); // FILE_DEVICE_UNKNOWN
static_assert(!QcRamDirectHarmlessControl(CTL_CODE(0x8123UL, 0x1UL, METHOD_BUFFERED, FILE_ANY_ACCESS))); // Third-party device type

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
    QC_RAM_QUEUE_COPY* QueueCopy;
    PVOID CopyContext;
    QC_OFFLOAD_POLICY Offload; // Copy inline or on the provider's workers (racy hints).
    volatile LONG Queued;     // Copies on the provider's workers; each holds Rundown.
    volatile LONG Copying;    // Large copies in their submitters' threads.
    ULONGLONG Offset, Length; // Volume extent on the RAM disk.
    UCHAR Signature[8];       // Boot sector OEM name at bind; BitLocker changes it.
    BOOLEAN SnapshotSeen;     // A flush-and-hold passed this volume (sticky).
    QC_RAM_VIEW* View;        // Registry lock.
    GUID Resource;
    WCHAR Driver[32];
    volatile LONG64 ReadRequests, WriteRequests, ReadBytes, WriteBytes, Declined;
};

void QcRamDirectInitialize();                                // DriverEntry
void QcRamDirectDestroy();                                   // Unload
void QcRamDirectInitialize(QC_RAM_BINDING* binding);        // AddDevice
NTSTATUS QcRamViewControl(PIRP irp);                         // Budget device, kernel registration
// Hot path for reads and writes holding removeLock (acquired with irp as its tag).
enum class QcRamDirectOutcome
{
    Standard,  // Not served: continue on the standard path.
    Completed, // Served: IoStatus is set; the caller releases removeLock and completes the IRP.
    Pending,   // Marked pending and queued: a provider worker completes it and releases removeLock.
};
QcRamDirectOutcome QcRamDirectTransfer(QC_RAM_BINDING* binding, PIRP irp, PIO_REMOVE_LOCK removeLock);
void QcRamDirectObserveControl(QC_RAM_BINDING* binding, ULONG code);
// PASSIVE_LEVEL. lower is the next device in the volume stack.
void QcRamDirectBind(QC_RAM_BINDING* binding, PDEVICE_OBJECT lower, const GUID& resource, ULONGLONG volumeBytes, ULONG flags);
// Records why a volume cannot use Direct access (when it is not bound).
void QcRamDirectRefuse(QC_RAM_BINDING* binding, ULONG reason);
void QcRamDirectState(QC_RAM_BINDING* binding, QC_RAM_DIRECT_STATE* state);
void QcRamDirectEnd(QC_RAM_BINDING* binding, ULONG reason); // PASSIVE_LEVEL, e.g. volume removal.
