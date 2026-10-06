// SPDX-License-Identifier: MIT
#pragma once
#include <ntddk.h>
#include "ramstore.h"

// Kernel-only contract: the RAM disk provider offers a store to the volume filter for
// Direct access, over the filter's budget device (kernel-mode INTERNAL_DEVICE_CONTROL at
// PASSIVE_LEVEL). After Unregister returns, the filter never touches the store or calls
// the copy routines again, so the provider may free them.
#define IOCTL_QCACHE_KERNEL_RAM_VIEW CTL_CODE(0x8844UL, 0xD21UL, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS)
enum QC_RAM_VIEW_ACTION : ULONG { QcRamViewRegister = 1, QcRamViewUnregister = 2 };
// Copies a large transfer, possibly with the provider's helper threads. IRQL <= DISPATCH_LEVEL.
typedef void QC_RAM_LARGE_COPY(PVOID context, QC_RAM_STORE* store, ULONGLONG offset, PUCHAR buffer, ULONG bytes, BOOLEAN write);
// A copy finished on one of the provider's worker threads, so its submitter can return at once.
// The submitter keeps the store valid (and, for a write, admitted through its gate) until
// Completed runs.
struct QC_RAM_ASYNC_COPY
{
    ULONG_PTR Reserved[4];         // The provider's queue entry while queued.
    QC_RAM_STORE* Store;
    ULONGLONG Offset;
    PUCHAR Buffer;
    ULONG Bytes;
    BOOLEAN Write;
    // Runs once on the worker (PASSIVE_LEVEL) after the copy; the provider never touches the
    // copy again.
    void (*Completed)(QC_RAM_ASYNC_COPY* copy);
};
// Queues the copy to a worker on another processor (when there is one). IRQL <= DISPATCH_LEVEL.
typedef void QC_RAM_QUEUE_COPY(PVOID context, QC_RAM_ASYNC_COPY* copy);
struct QC_KERNEL_RAM_VIEW
{
    ULONG Size, Version, Action, Reserved;
    PDRIVER_OBJECT Owner;          // The provider; also owns the RAM disk's PDO.
    GUID Resource;
    QC_RAM_STORE* Store;           // Register: valid until Unregister returns.
    QC_RAM_LARGE_COPY* LargeCopy;  // Register: optional.
    QC_RAM_QUEUE_COPY* QueueCopy;  // Register: optional (only with worker threads).
    PVOID CopyContext;             // For LargeCopy and QueueCopy.
    PVOID View;                    // Register output; Unregister input.
};
constexpr ULONG QcRamViewVersion = 2;
