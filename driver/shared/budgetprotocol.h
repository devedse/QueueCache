// SPDX-License-Identifier: MIT
#pragma once
#include <ntddk.h>

#define IOCTL_QCACHE_KERNEL_BUDGET CTL_CODE(0x8844, 0xD20, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS)
enum QC_BUDGET_ACTION : ULONG { QcBudgetReserve = 1, QcBudgetRelease = 2 };
struct QC_KERNEL_RESERVATION
{
    ULONG Size, Version, Action, Reserved;
    PDRIVER_OBJECT Owner; // Only accepted on kernel-mode INTERNAL_DEVICE_CONTROL.
    GUID Resource, Token;
    ULONGLONG Bytes;
};
NTSTATUS QcBudgetInitialize(PDRIVER_OBJECT driver);
void QcBudgetDestroy();
bool QcBudgetOwnsDevice(PDEVICE_OBJECT device);
NTSTATUS QcBudgetDispatch(PDEVICE_OBJECT device, PIRP irp);
bool QcBudgetIsRamSerial(const char* serial, ULONG length);
