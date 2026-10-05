// SPDX-License-Identifier: MIT
#pragma once
#include <ntddk.h>
#include "ramstore.h"

// Kernel-only contract: the RAM disk provider offers a store to the volume filter for
// Direct access, over the filter's budget device (kernel-mode INTERNAL_DEVICE_CONTROL at
// PASSIVE_LEVEL). After Unregister returns, the filter never touches the store or calls
// LargeCopy again, so the provider may free them.
#define IOCTL_QCACHE_KERNEL_RAM_VIEW CTL_CODE(0x8844UL, 0xD21UL, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS)
enum QC_RAM_VIEW_ACTION : ULONG { QcRamViewRegister = 1, QcRamViewUnregister = 2 };
// Copies a large transfer, possibly with the provider's helper threads. IRQL <= DISPATCH_LEVEL.
typedef void QC_RAM_LARGE_COPY(PVOID context, QC_RAM_STORE* store, ULONGLONG offset, PUCHAR buffer, ULONG bytes, BOOLEAN write);
struct QC_KERNEL_RAM_VIEW
{
    ULONG Size, Version, Action, Reserved;
    PDRIVER_OBJECT Owner;          // The provider; also owns the RAM disk's PDO.
    GUID Resource;
    QC_RAM_STORE* Store;           // Register: valid until Unregister returns.
    QC_RAM_LARGE_COPY* LargeCopy;  // Register: optional.
    PVOID LargeCopyContext;
    PVOID View;                    // Register output; Unregister input.
};
constexpr ULONG QcRamViewVersion = 1;
