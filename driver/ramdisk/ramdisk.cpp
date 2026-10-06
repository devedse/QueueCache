// SPDX-License-Identifier: MIT
// Storport virtual miniport entry points. Storport owns the adapter/PDOs; RAM lifetime is
// independent of user handles. See provider.h for the source layout.
#include "provider.h"

PDRIVER_OBJECT Driver;
extern "C" DRIVER_INITIALIZE DriverEntry;
VIRTUAL_HW_FIND_ADAPTER FindAdapter;
HW_INITIALIZE Initialize;
HW_RESET_BUS ResetBus;
HW_ADAPTER_CONTROL AdapterControl;
HW_FREE_ADAPTER_RESOURCES FreeAdapter;

static void AdapterEpoch(GUID* epoch)
{
    // ExUuidCreate returns STATUS_RETRY until the UUID seed is available, which can
    // be later than root-enumerated adapter start during boot. The epoch only has to
    // distinguish adapter instances, so a time/counter-seeded random value suffices.
    if (NT_SUCCESS(ExUuidCreate(epoch))) return;
    LARGE_INTEGER now; KeQuerySystemTimePrecise(&now);
    const auto counter = KeQueryPerformanceCounter(nullptr).QuadPart;
    ULONG seed = static_cast<ULONG>(now.QuadPart ^ (now.QuadPart >> 32) ^ counter ^ (counter >> 32));
    auto words = reinterpret_cast<ULONG*>(epoch);
    for (ULONG i = 0; i < sizeof(*epoch) / sizeof(ULONG); ++i)
        words[i] = RtlRandomEx(&seed) ^ static_cast<ULONG>(counter >> (i * 8));
    epoch->Data3 = static_cast<USHORT>((epoch->Data3 & 0x0FFF) | 0x4000);
    epoch->Data4[0] = static_cast<UCHAR>((epoch->Data4[0] & 0x3F) | 0x80);
}
ULONG FindAdapter(PVOID extension, PVOID, PVOID, PVOID, PCHAR, PPORT_CONFIGURATION_INFORMATION configuration, PBOOLEAN again)
{
    auto adapter = static_cast<ADAPTER*>(extension);
    KeInitializeSpinLock(&adapter->TableLock); ExInitializeFastMutex(&adapter->ControlLock);
    AdapterEpoch(&adapter->Epoch);
    configuration->VirtualDevice = TRUE; configuration->NumberOfBuses = 1;
    configuration->MaximumNumberOfTargets = 1; configuration->MaximumNumberOfLogicalUnits = QcRamMaxDisks;
    configuration->MaximumTransferLength = QcRamTransferBytes;
    configuration->NumberOfPhysicalBreaks = QcRamTransferBytes / PAGE_SIZE + 1; // Include an unaligned first/last page.
    configuration->ScatterGather = TRUE; configuration->Master = TRUE;
    configuration->CachesData = TRUE; configuration->AlignmentMask = 0;
    configuration->WmiDataProvider = FALSE; *again = FALSE;
    configuration->MaxNumberOfIO = 4096; configuration->MaxIOsPerLun = 256; configuration->InitialLunQueueDepth = 256;
    StartWorkers(adapter);
    return SP_RETURN_FOUND;
}
BOOLEAN Initialize(PVOID extension)
{
    // Completing inside StartIo skips Storport's completion DPC (about 7x the 4 KiB rate on
    // the lab VM); it requires DPC redirection. Unsupported systems keep the DPC path.
    PERF_CONFIGURATION_DATA query{}; query.Version = STOR_PERF_VERSION_5; query.Size = sizeof(query);
    if (StorPortInitializePerfOpts(extension, TRUE, &query) == STOR_STATUS_SUCCESS)
    {
        PERF_CONFIGURATION_DATA apply{}; apply.Version = STOR_PERF_VERSION_5; apply.Size = sizeof(apply);
        apply.Flags = query.Flags & (STOR_PERF_DPC_REDIRECTION | STOR_PERF_OPTIMIZE_FOR_COMPLETION_DURING_STARTIO);
        if (apply.Flags) StorPortInitializePerfOpts(extension, FALSE, &apply);
    }
    return TRUE;
}
BOOLEAN ResetBus(PVOID, ULONG) { return TRUE; }
SCSI_ADAPTER_CONTROL_STATUS AdapterControl(PVOID extension, SCSI_ADAPTER_CONTROL_TYPE type, PVOID parameters)
{
    auto adapter = static_cast<ADAPTER*>(extension);
    if (type == ScsiQuerySupportedControlTypes)
    {
        auto list = static_cast<PSCSI_SUPPORTED_CONTROL_TYPE_LIST>(parameters);
        if (list->MaxControlType > ScsiStopAdapter) list->SupportedTypeList[ScsiStopAdapter] = TRUE;
        if (list->MaxControlType > ScsiRestartAdapter) list->SupportedTypeList[ScsiRestartAdapter] = TRUE;
        return ScsiAdapterControlSuccess;
    }
    if (type == ScsiStopAdapter) { adapter->Stopped = TRUE; return ScsiAdapterControlSuccess; }
    if (type == ScsiRestartAdapter) { adapter->Stopped = FALSE; return ScsiAdapterControlSuccess; }
    return ScsiAdapterControlUnsuccessful;
}
void FreeAdapter(PVOID extension)
{
    auto adapter = static_cast<ADAPTER*>(extension);
    StopWorkers(adapter); // completes every queued transfer first
    for (ULONG i = 0; i < QcRamMaxDisks; ++i)
    {
        auto disk = adapter->Disks[i]; if (!disk) continue;
        KIRQL irql; KeAcquireSpinLock(&adapter->TableLock, &irql); adapter->Disks[i] = nullptr; KeReleaseSpinLock(&adapter->TableLock, irql);
        WaitReferences(disk);
        FreeDisk(disk);
    }
}
extern "C" NTSTATUS DriverEntry(PDRIVER_OBJECT driver, PUNICODE_STRING registry)
{
    Driver = driver;
    HW_INITIALIZATION_DATA initialization{};
    initialization.HwInitializationDataSize = sizeof(initialization); initialization.AdapterInterfaceType = Internal;
    // HW_INITIALIZATION_DATA uses this flag to select virtual callbacks (including
    // the seven-argument FindAdapter). ConfigInfo.VirtualDevice is set later and
    // cannot substitute for declaring the miniport type at StorPortInitialize.
    initialization.FeatureSupport = STOR_FEATURE_VIRTUAL_MINIPORT;
    initialization.SrbTypeFlags = SRB_TYPE_FLAG_SCSI_REQUEST_BLOCK;
    initialization.AddressTypeFlags = ADDRESS_TYPE_FLAG_BTL8;
    initialization.HwFindAdapter = reinterpret_cast<PVOID>(FindAdapter); initialization.HwInitialize = Initialize; initialization.HwStartIo = StartIo;
    initialization.HwResetBus = ResetBus; initialization.HwAdapterControl = AdapterControl;
    initialization.HwFreeAdapterResources = FreeAdapter; initialization.HwProcessServiceRequest = ServiceRequest;
    initialization.HwCompleteServiceIrp = CompleteService; initialization.DeviceExtensionSize = sizeof(ADAPTER);
    initialization.SrbExtensionSize = sizeof(REQUEST);
    initialization.MapBuffers = STOR_MAP_ALL_BUFFERS_INCLUDING_READ_WRITE; initialization.TaggedQueuing = TRUE;
    initialization.AutoRequestSense = TRUE; initialization.MultipleRequestPerLu = TRUE;
    return StorPortInitialize(driver, registry, &initialization, nullptr);
}
