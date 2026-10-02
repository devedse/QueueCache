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
inline bool QcObservationRequest(PIO_STACK_LOCATION stack)
{
    return stack->MajorFunction == IRP_MJ_DEVICE_CONTROL &&
           QcObservationCode(stack->Parameters.DeviceIoControl.IoControlCode);
}
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

