// SPDX-License-Identifier: MIT
#pragma once
#include <ntddstor.h>
#include <ntdddisk.h>
#include <ntddscsi.h>
// Explicit buffer-based identity/health queries only. Access bits are permissions,
// not proof of safety. No pass-through, reset, TRIM, media change or private command.
constexpr bool QcObservationCode(ULONG code)
{
    switch (code)
    {
    case IOCTL_STORAGE_QUERY_PROPERTY:
    case IOCTL_STORAGE_GET_DEVICE_NUMBER:
    case IOCTL_STORAGE_GET_DEVICE_NUMBER_EX:
    case IOCTL_STORAGE_GET_HOTPLUG_INFO:
    case IOCTL_STORAGE_PREDICT_FAILURE:
    case IOCTL_STORAGE_FIRMWARE_GET_INFO:
    case IOCTL_DISK_GET_DRIVE_GEOMETRY:
    case IOCTL_DISK_GET_DRIVE_GEOMETRY_EX:
    case IOCTL_DISK_GET_LENGTH_INFO:
    case IOCTL_DISK_GET_DRIVE_LAYOUT:
    case IOCTL_DISK_GET_DRIVE_LAYOUT_EX:
    case IOCTL_DISK_GET_PARTITION_INFO:
    case IOCTL_DISK_GET_PARTITION_INFO_EX:
    case IOCTL_DISK_IS_WRITABLE:
    case IOCTL_SCSI_GET_ADDRESS:
    case SMART_GET_VERSION:
        return true;
    default:
        return false;
    }
}
// Mount manager and volume-state traffic touches no volume data. It must never queue
// behind the ordered worker or hold DirectIdle: the mount manager sends these with its
// own lock held, and volsnap completes IOCTL_VOLUME_ONLINE only after querying the mount
// manager, so ordering either one deadlocked a new volume's arrival (found on the VM).
// Offline and attribute changes stay ordered behind pending writes.
constexpr ULONG QcMountDeviceType = 0x4D; // MOUNTDEVCONTROLTYPE: every IOCTL_MOUNTDEV_*
constexpr ULONG QcVolumeControl(ULONG function, ULONG access) { return CTL_CODE(0x56, function, METHOD_BUFFERED, access); }
constexpr bool QcVolumeManagementCode(ULONG code)
{
    if (DEVICE_TYPE_FROM_CTL_CODE(code) == QcMountDeviceType)
        return true;
    return code == QcVolumeControl(0, FILE_ANY_ACCESS) ||                       // GET_VOLUME_DISK_EXTENTS
           code == QcVolumeControl(2, FILE_READ_ACCESS | FILE_WRITE_ACCESS) ||  // ONLINE
           code == QcVolumeControl(4, FILE_ANY_ACCESS) ||                       // IS_OFFLINE
           code == QcVolumeControl(5, FILE_ANY_ACCESS) ||                       // IS_IO_CAPABLE
           code == QcVolumeControl(7, FILE_ANY_ACCESS) ||                       // QUERY_VOLUME_NUMBER
           code == QcVolumeControl(10, FILE_ANY_ACCESS) ||                      // IS_PARTITION
           code == QcVolumeControl(12, FILE_ANY_ACCESS) ||                      // IS_CLUSTERED
           code == QcVolumeControl(14, FILE_ANY_ACCESS) ||                      // GET_GPT_ATTRIBUTES
           code == QcVolumeControl(25, FILE_READ_ACCESS | FILE_WRITE_ACCESS);   // POST_ONLINE
}
inline bool QcObservationRequest(PIO_STACK_LOCATION stack)
{
    return stack->MajorFunction == IRP_MJ_DEVICE_CONTROL &&
           (QcObservationCode(stack->Parameters.DeviceIoControl.IoControlCode) ||
            QcVolumeManagementCode(stack->Parameters.DeviceIoControl.IoControlCode));
}
static_assert(QcVolumeManagementCode(CTL_CODE(QcMountDeviceType, 0, METHOD_BUFFERED, FILE_ANY_ACCESS))); // QUERY_UNIQUE_ID
static_assert(QcVolumeManagementCode(CTL_CODE(QcMountDeviceType, 1, METHOD_BUFFERED, FILE_ANY_ACCESS))); // UNIQUE_ID_CHANGE_NOTIFY
static_assert(QcVolumeManagementCode(0x56C008)); // IOCTL_VOLUME_ONLINE
static_assert(QcVolumeManagementCode(0x560000)); // IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS
static_assert(!QcVolumeManagementCode(0x56C00C)); // IOCTL_VOLUME_OFFLINE stays ordered
static_assert(!QcVolumeManagementCode(QcVolumeControl(13, FILE_ANY_ACCESS))); // SET_GPT_ATTRIBUTES stays ordered
static_assert(!QcVolumeManagementCode(IOCTL_DISK_SET_DRIVE_LAYOUT_EX));
static_assert(!QcVolumeManagementCode(0x6D0008)); // mount manager's own (MOUNTMGRCONTROLTYPE) controls
static_assert(QcObservationCode(IOCTL_STORAGE_QUERY_PROPERTY));
static_assert(QcObservationCode(IOCTL_STORAGE_GET_HOTPLUG_INFO));
static_assert(!QcObservationCode(IOCTL_STORAGE_SET_HOTPLUG_INFO));
static_assert(!QcObservationCode(IOCTL_STORAGE_MANAGE_DATA_SET_ATTRIBUTES));
static_assert(!QcObservationCode(IOCTL_SCSI_PASS_THROUGH));
static_assert(!QcObservationCode(IOCTL_STORAGE_RESET_DEVICE));
static_assert(!QcObservationCode(IOCTL_STORAGE_FIRMWARE_DOWNLOAD));

// volsnap (device type 0x53). Only flush-and-hold must be ordered behind the cache's
// pending writes (it drains them, so a shadow copy contains data that was pending in
// RAM). Its other controls (commit, release-writes, ...) arrive while volsnap holds
// every write and need no ordering against cached data: they are forwarded at
// dispatch, never queued behind a request that waits for a held write (found on the
// VM: the hold timed out and the snapshot failed).
constexpr ULONG QcVolsnapFlushAndHoldWrites = 0x53C000; // IOCTL_VOLSNAP_FLUSH_AND_HOLD_WRITES
constexpr bool QcSnapshotControlWithoutDrain(ULONG code)
{
    return DEVICE_TYPE_FROM_CTL_CODE(code) == 0x53 && code != QcVolsnapFlushAndHoldWrites;
}
static_assert(!QcSnapshotControlWithoutDrain(QcVolsnapFlushAndHoldWrites));
static_assert(QcSnapshotControlWithoutDrain(0x53C004)); // IOCTL_VOLSNAP_RELEASE_WRITES, seen on the VM
static_assert(QcSnapshotControlWithoutDrain(0x53C038)); // seen on the VM during a snapshot

