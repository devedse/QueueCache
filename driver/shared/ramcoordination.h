// SPDX-License-Identifier: MIT
#pragma once
#include <ntddk.h>

// Default-off, per-store research counters. QPC durations are sampled once per
// 64 eligible operations; counter intervals enclose requests, not DiskSpd scores.
// Direct active/ticks cover the admitted read-copy interval, excluding validation,
// MDL mapping and final IRP completion. CopySplit counters include provider reads.
#define QC_RAM_COORDINATION_COUNTERS(X) \
    X(Started) X(Completed) X(Active) X(PeakActive) X(DirectBytes) \
    X(DirectSamples) X(DirectTicks) X(Splits) X(SplitBytes) \
    X(CallerBytes) X(HelperBytes) X(Posted) X(Taken) X(Withdrawn) \
    X(SampledSplits) X(PostTicks) X(WithdrawTicks) X(WaitTicks) \
    X(TakeSamples) X(TakeTicks) X(HelperCpuMask)
struct QC_RAM_COORDINATION_COUNTS
{
#define QC_COORD_FIELD(name) volatile LONG64 name;
    QC_RAM_COORDINATION_COUNTERS(QC_COORD_FIELD)
#undef QC_COORD_FIELD
};
struct QC_RAM_COORDINATION
{
    EX_RUNDOWN_REF Users;
    volatile LONG Enabled;
    volatile LONG64 Generation;
    QC_RAM_COORDINATION_COUNTS Counts;
};
struct QC_RAM_COORDINATION_DATA
{
    ULONG Size, Version;
    ULONGLONG Generation, Frequency, Enabled;
#define QC_COORD_FIELD(name) ULONGLONG name;
    QC_RAM_COORDINATION_COUNTERS(QC_COORD_FIELD)
#undef QC_COORD_FIELD
};
static_assert(sizeof(QC_RAM_COORDINATION_DATA) == 200, "RAM coordination wire size");

inline void QcRamCoordinationInitialize(QC_RAM_COORDINATION* state)
{
    ExInitializeRundownProtection(&state->Users);
    ExWaitForRundownProtectionRelease(&state->Users); // closed until explicitly enabled
}
inline bool QcRamCoordinationBegin(QC_RAM_COORDINATION* state)
{
    return ReadNoFence(&state->Enabled) && ExAcquireRundownProtection(&state->Users);
}
inline void QcRamCoordinationEnd(QC_RAM_COORDINATION* state)
{
    ExReleaseRundownProtection(&state->Users);
}
// Provider ControlLock serializes settings. Close and drain collection before
// resetting its generation; new/off requests cannot write the previous counters.
inline void QcRamCoordinationConfigure(QC_RAM_COORDINATION* state, bool enabled)
{
    if (InterlockedExchange(&state->Enabled, 0))
        ExWaitForRundownProtectionRelease(&state->Users);
    if (!enabled) return; // preserve final counters for a quiescent readback
    RtlZeroMemory(&state->Counts, sizeof(state->Counts));
    InterlockedIncrement64(&state->Generation);
    ExReInitializeRundownProtection(&state->Users);
    InterlockedExchange(&state->Enabled, 1);
}
inline void QcRamCoordinationSnapshot(const QC_RAM_COORDINATION* state, QC_RAM_COORDINATION_DATA* data)
{
    data->Size = sizeof(*data); data->Version = 1;
    LARGE_INTEGER frequency; KeQueryPerformanceCounter(&frequency);
    data->Frequency = static_cast<ULONGLONG>(frequency.QuadPart);
    data->Generation = static_cast<ULONGLONG>(ReadNoFence64(&state->Generation));
    data->Enabled = ReadNoFence(&state->Enabled) ? 1 : 0;
#define QC_COORD_COPY(name) data->name = static_cast<ULONGLONG>(ReadNoFence64(&state->Counts.name));
    QC_RAM_COORDINATION_COUNTERS(QC_COORD_COPY)
#undef QC_COORD_COPY
}
