// SPDX-License-Identifier: MIT
#pragma once
#include <ntifs.h>

// Sends a buffered device control to a device and waits for it. PASSIVE_LEVEL only, and
// never while holding a lock that raises IRQL: completion needs this thread's APC.
inline NTSTATUS QcSendControl(PDEVICE_OBJECT device, ULONG code, PVOID input, ULONG inputBytes,
    PVOID output, ULONG outputBytes, ULONG_PTR* returned = nullptr)
{
    KEVENT event;
    KeInitializeEvent(&event, NotificationEvent, FALSE);
    IO_STATUS_BLOCK iosb = {};
    auto irp = IoBuildDeviceIoControlRequest(code, device, input, inputBytes, output, outputBytes, FALSE, &event, &iosb);
    if (!irp)
        return STATUS_INSUFFICIENT_RESOURCES;
    auto status = IoCallDriver(device, irp);
    if (status == STATUS_PENDING)
    {
        KeWaitForSingleObject(&event, Executive, KernelMode, FALSE, nullptr);
        status = iosb.Status;
    }
    if (returned)
        *returned = iosb.Information;
    return status;
}
