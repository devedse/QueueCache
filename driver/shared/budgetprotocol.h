// SPDX-License-Identifier: MIT
#pragma once
#include <ntddk.h>

#define IOCTL_QCACHE_KERNEL_BUDGET CTL_CODE(0x8844UL, 0xD20UL, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS)
enum QC_BUDGET_ACTION : ULONG { QcBudgetReserve = 1, QcBudgetRelease = 2, QcBudgetStartupSession = 3 };
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
// A RAM disk's serial is "QC" + its resource GUID in hex (34 characters).
bool QcBudgetParseRamSerial(const char* serial, ULONG length, GUID* resource);
bool QcBudgetIsReserved(const GUID& resource);
void QcBudgetObserveSystemPower(SYSTEM_POWER_STATE state, SYSTEM_POWER_STATE_CONTEXT context);
