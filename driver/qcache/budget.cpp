// SPDX-License-Identifier: MIT
#include <ntifs.h>
#include <wdmsec.h>
#include "../shared/budgetprotocol.h"
#include "../shared/memorybudget.h"
#include "../shared/startupepoch.h"

extern QC_MEMORY_BUDGET* QcSharedMemoryBudget();
extern void QcInitializeMemoryBudget();
static PDEVICE_OBJECT ControlDevice;
static FAST_MUTEX ReservationLock;
static LIST_ENTRY Reservations;
static KSPIN_LOCK StartupLock;
static QC_STARTUP_EPOCH Startup;
static bool ResumeObserved;
static constexpr bool FastStartup(ULONG target, ULONG effective)
{ return target == PowerSystemShutdown && effective == PowerSystemHibernate; }
static_assert(FastStartup(PowerSystemShutdown, PowerSystemHibernate));
static_assert(!FastStartup(PowerSystemHibernate, PowerSystemHibernate));
static_assert(!FastStartup(PowerSystemSleeping3, PowerSystemSleeping3));
struct RESERVATION
{
    LIST_ENTRY Link;
    PDRIVER_OBJECT Owner;
    GUID Resource, Token;
    ULONGLONG Bytes;
};
static constexpr ULONG Tag = 'bRCQ';
static const GUID ControlClass = {0x7c67ec5a,0x88df,0x4dd0,{0xb1,0x2e,0x8d,0x25,0xee,0x3b,0x41,0x97}};

NTSTATUS QcBudgetInitialize(PDRIVER_OBJECT driver)
{
    ExInitializeFastMutex(&ReservationLock);
    InitializeListHead(&Reservations);
    QcInitializeMemoryBudget();
    KeInitializeSpinLock(&StartupLock);
    // The volume filter is boot-start. ExUuidCreate may return STATUS_RETRY
    // before UUID generation is ready; optional managed-disk identity must not
    // make the OS volume filter fail DriverEntry. Seed only on a later service
    // query, and return the real failure to that caller without inventing an epoch.
    UNICODE_STRING name = RTL_CONSTANT_STRING(L"\\Device\\QueueCacheBudget");
    UNICODE_STRING security = RTL_CONSTANT_STRING(L"D:P(A;;GA;;;SY)(A;;GA;;;BA)");
    auto status = IoCreateDeviceSecure(driver, 0, &name, FILE_DEVICE_UNKNOWN,
        FILE_DEVICE_SECURE_OPEN, FALSE, &security, &ControlClass, &ControlDevice);
    if (NT_SUCCESS(status)) ControlDevice->Flags &= ~DO_DEVICE_INITIALIZING;
    return status;
}
bool QcBudgetOwnsDevice(PDEVICE_OBJECT device) { return device == ControlDevice; }
static NTSTATUS EnsureStartupEpoch()
{
    KIRQL irql; KeAcquireSpinLock(&StartupLock, &irql);
    const bool ready = Startup.Initialized;
    KeReleaseSpinLock(&StartupLock, irql);
    if (ready) return STATUS_SUCCESS;
    GUID candidate{};
    auto status = ExUuidCreate(&candidate); // PASSIVE_LEVEL, outside all spin/fast mutexes.
    if (!NT_SUCCESS(status)) return status;
    KeAcquireSpinLock(&StartupLock, &irql);
    Startup.Seed(candidate);
    KeReleaseSpinLock(&StartupLock, irql);
    return STATUS_SUCCESS;
}
void QcBudgetObserveSystemPower(SYSTEM_POWER_STATE state, SYSTEM_POWER_STATE_CONTEXT context)
{
    KIRQL irql; KeAcquireSpinLock(&StartupLock, &irql);
    if (state != PowerSystemWorking) ResumeObserved = false;
    else if (!ResumeObserved && context.TargetSystemState >= PowerSystemSleeping1 &&
        context.TargetSystemState <= PowerSystemShutdown && context.EffectiveSystemState >= PowerSystemSleeping1 &&
        context.EffectiveSystemState <= PowerSystemShutdown)
    {
        ResumeObserved = true;
        if (FastStartup(context.TargetSystemState, context.EffectiveSystemState))
        {
            Startup.AdvanceHybrid();
        }
    }
    KeReleaseSpinLock(&StartupLock, irql);
}
void QcBudgetDestroy()
{
    // Provider holds an open file reference until all of its pages are freed.
    NT_ASSERT(IsListEmpty(&Reservations));
    if (ControlDevice) { IoDeleteDevice(ControlDevice); ControlDevice = nullptr; }
}
NTSTATUS QcBudgetDispatch(PDEVICE_OBJECT, PIRP irp)
{
    auto stack = IoGetCurrentIrpStackLocation(irp);
    auto status = STATUS_INVALID_DEVICE_REQUEST;
    ULONG_PTR returned = 0;
    if (stack->MajorFunction == IRP_MJ_CREATE || stack->MajorFunction == IRP_MJ_CLOSE || stack->MajorFunction == IRP_MJ_CLEANUP)
        status = STATUS_SUCCESS;
    else if (stack->MajorFunction == IRP_MJ_INTERNAL_DEVICE_CONTROL && irp->RequestorMode == KernelMode &&
        stack->Parameters.DeviceIoControl.IoControlCode == IOCTL_QCACHE_KERNEL_BUDGET &&
        stack->Parameters.DeviceIoControl.InputBufferLength == sizeof(QC_KERNEL_RESERVATION) &&
        stack->Parameters.DeviceIoControl.OutputBufferLength >= sizeof(QC_KERNEL_RESERVATION) && KeGetCurrentIrql() == PASSIVE_LEVEL)
    {
        auto request = static_cast<QC_KERNEL_RESERVATION*>(irp->AssociatedIrp.SystemBuffer);
        status = STATUS_INVALID_PARAMETER;
        if (request && request->Size == sizeof(*request) && request->Version == 1 && !request->Reserved && request->Owner)
        {
            if (request->Action == QcBudgetStartupSession)
            {
                status = EnsureStartupEpoch();
                if (!NT_SUCCESS(status)) goto Complete;
                KIRQL irql; KeAcquireSpinLock(&StartupLock, &irql);
                request->Token = Startup.Epoch; request->Bytes = Startup.Transitions;
                KeReleaseSpinLock(&StartupLock, irql);
                status = STATUS_SUCCESS; returned = sizeof(*request); goto Complete;
            }
            GUID newToken{};
            if (request->Action == QcBudgetReserve)
            {
                status = ExUuidCreate(&newToken); // PASSIVE_LEVEL, before FAST_MUTEX raises to APC_LEVEL.
                if (!NT_SUCCESS(status)) goto Complete;
            }
            ExAcquireFastMutex(&ReservationLock);
            if (request->Action == QcBudgetReserve && request->Bytes && request->Bytes <= MAXLONGLONG)
            {
                bool duplicate = false;
                for (auto link = Reservations.Flink; link != &Reservations; link = link->Flink)
                    duplicate |= IsEqualGUID(CONTAINING_RECORD(link, RESERVATION, Link)->Resource, request->Resource) != FALSE;
                auto entry = duplicate ? nullptr : static_cast<RESERVATION*>(ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(RESERVATION), Tag));
                if (!entry) status = duplicate ? STATUS_OBJECT_NAME_COLLISION : STATUS_INSUFFICIENT_RESOURCES;
                else if (!QcReserveMemory(QcSharedMemoryBudget(), request->Bytes))
                {
                    ExFreePoolWithTag(entry, Tag);
                    status = STATUS_INSUFFICIENT_RESOURCES;
                }
                else
                {
                    entry->Token = newToken;
                    entry->Owner = request->Owner; entry->Resource = request->Resource; entry->Bytes = request->Bytes;
                    ObReferenceObject(entry->Owner);
                    InsertTailList(&Reservations, &entry->Link);
                    request->Token = entry->Token;
                    status = STATUS_SUCCESS; returned = sizeof(*request);
                }
            }
            else if (request->Action == QcBudgetRelease)
            {
                status = STATUS_NOT_FOUND;
                for (auto link = Reservations.Flink; link != &Reservations; link = link->Flink)
                {
                    auto entry = CONTAINING_RECORD(link, RESERVATION, Link);
                    if (entry->Owner == request->Owner && IsEqualGUID(entry->Token, request->Token) &&
                        IsEqualGUID(entry->Resource, request->Resource) && entry->Bytes == request->Bytes)
                    {
                        RemoveEntryList(link);
                        QcReleaseMemory(QcSharedMemoryBudget(), entry->Bytes);
                        ObDereferenceObject(entry->Owner);
                        ExFreePoolWithTag(entry, Tag);
                        status = STATUS_SUCCESS; returned = sizeof(*request); break;
                    }
                }
            }
            ExReleaseFastMutex(&ReservationLock);
        }
    }
Complete:
    irp->IoStatus.Status = status; irp->IoStatus.Information = returned;
    IoCompleteRequest(irp, IO_NO_INCREMENT);
    return status;
}
bool QcBudgetIsRamSerial(const char* serial, ULONG length)
{
    if (!serial || length != 34 || serial[0] != 'Q' || serial[1] != 'C') return false;
    GUID resource{};
    auto raw = reinterpret_cast<PUCHAR>(&resource);
    for (ULONG i = 0; i < 16; ++i)
    {
        ULONG value = 0;
        for (ULONG half = 0; half < 2; ++half)
        {
            const auto c = serial[2 + i * 2 + half];
            if (c >= '0' && c <= '9') value = value * 16 + c - '0';
            else if (c >= 'A' && c <= 'F') value = value * 16 + c - 'A' + 10;
            else return false;
        }
        raw[i] = static_cast<UCHAR>(value);
    }
    bool found = false;
    ExAcquireFastMutex(&ReservationLock);
    for (auto link = Reservations.Flink; link != &Reservations; link = link->Flink)
        found |= IsEqualGUID(CONTAINING_RECORD(link, RESERVATION, Link)->Resource, resource) != FALSE;
    ExReleaseFastMutex(&ReservationLock);
    return found;
}
