// SPDX-License-Identifier: MIT
#pragma once
#include <ntddstor.h>
#include <ntdddisk.h>
#include <ntddscsi.h>
// Explicit buffer-based identity/health queries only. Access bits are permissions,
// not proof of safety. No pass-through, reset, TRIM, media change or private command.
constexpr bool QcObservationCode(ULONG code) {
    switch (code) {
    case IOCTL_STORAGE_QUERY_PROPERTY:
    case IOCTL_STORAGE_GET_DEVICE_NUMBER:
    case IOCTL_STORAGE_GET_DEVICE_NUMBER_EX:
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
    default: return false;
    }
}
inline bool QcObservationRequest(PIO_STACK_LOCATION stack) {
    return stack->MajorFunction == IRP_MJ_DEVICE_CONTROL &&
        QcObservationCode(stack->Parameters.DeviceIoControl.IoControlCode);
}
static_assert(QcObservationCode(IOCTL_STORAGE_QUERY_PROPERTY));
static_assert(!QcObservationCode(IOCTL_STORAGE_MANAGE_DATA_SET_ATTRIBUTES));
static_assert(!QcObservationCode(IOCTL_SCSI_PASS_THROUGH));
static_assert(!QcObservationCode(IOCTL_STORAGE_RESET_DEVICE));
static_assert(!QcObservationCode(IOCTL_STORAGE_FIRMWARE_DOWNLOAD));
