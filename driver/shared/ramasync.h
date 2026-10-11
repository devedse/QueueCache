// SPDX-License-Identifier: MIT
#pragma once
#include <ntddk.h>

// Default-off, per-store experiment. A large asynchronous Direct read hands every split
// chunk to the provider's helpers and returns; the helper that copies the last chunk
// completes the request, so one thread's queue depth overlaps copying with submission.
// Off: one flag read per large Direct read. Admission reuses the provider's write
// policy (asynchronous while another is in flight; inline after a run that found none,
// probing periodically) and stays inline while another thread copies inline.
#define QC_RAM_ASYNC_COUNTERS(X) \
    X(Started) X(Completed) X(Declined) X(MaxInFlight) X(Probes) \
    X(InlineStreak) X(InlineBusy) X(InlineCap)
struct QC_RAM_ASYNC_COUNTS
{
#define QC_ASYNC_FIELD(name) volatile LONG64 name;
    QC_RAM_ASYNC_COUNTERS(QC_ASYNC_FIELD)
#undef QC_ASYNC_FIELD
};
struct QC_RAM_ASYNC
{
    volatile LONG Enabled;
    volatile LONG InFlight;     // Admitted, not yet completed or declined. Never reset.
    volatile LONG InlineActive; // Large inline Direct reads copying while Enabled.
    LONG Streak, Probe;         // Racy hints, like the provider's write policy.
    volatile LONG64 Generation;
    QC_RAM_ASYNC_COUNTS Counts;
};
struct QC_RAM_ASYNC_DATA
{
    ULONG Size, Version;
    ULONGLONG Generation, Enabled, InFlight, InlineActive;
#define QC_ASYNC_FIELD(name) ULONGLONG name;
    QC_RAM_ASYNC_COUNTERS(QC_ASYNC_FIELD)
#undef QC_ASYNC_FIELD
};
static_assert(sizeof(QC_RAM_ASYNC_DATA) == 104, "RAM asynchronous read wire size");
constexpr LONG QcRamAsyncInlineAfter = 16, QcRamAsyncProbeEvery = 64, QcRamAsyncCap = 16;

// Provider ControlLock serializes settings. Enabling starts a new generation and requires
// nothing in flight, so a window's counters never include an earlier request.
inline bool QcRamAsyncConfigure(QC_RAM_ASYNC* state, bool enabled)
{
    if (!enabled) { InterlockedExchange(&state->Enabled, 0); return true; } // keep counters for readback
    if (ReadNoFence(&state->Enabled) || ReadNoFence(&state->InFlight)) return false;
    RtlZeroMemory(&state->Counts, sizeof(state->Counts));
    state->Streak = state->Probe = 0;
    InterlockedIncrement64(&state->Generation);
    InterlockedExchange(&state->Enabled, 1);
    return true;
}
// A large asynchronous non-paging read while Enabled. Each admission ends in exactly one of
// QcRamAsyncCompleted or QcRamAsyncDeclined.
inline bool QcRamAsyncAdmit(QC_RAM_ASYNC* state)
{
    auto& counts = state->Counts;
    if (ReadNoFence(&state->InlineActive)) { InterlockedIncrement64(&counts.InlineBusy); return false; }
    const auto inFlight = ReadNoFence(&state->InFlight);
    if (inFlight >= QcRamAsyncCap) { InterlockedIncrement64(&counts.InlineCap); return false; }
    if (inFlight) state->Streak = 0;
    else if (state->Streak < QcRamAsyncInlineAfter) ++state->Streak;
    else if (++state->Probe % QcRamAsyncProbeEvery) { InterlockedIncrement64(&counts.InlineStreak); return false; }
    else InterlockedIncrement64(&counts.Probes);
    const LONG64 now = InterlockedIncrement(&state->InFlight);
    InterlockedIncrement64(&counts.Started);
    for (auto seen = ReadNoFence64(&counts.MaxInFlight); now > seen;)
    {
        const auto prior = InterlockedCompareExchange64(&counts.MaxInFlight, now, seen);
        if (prior == seen) break;
        seen = prior;
    }
    return true;
}
inline void QcRamAsyncCompleted(QC_RAM_ASYNC* state)
{
    InterlockedIncrement64(&state->Counts.Completed); // before InFlight: a quiescent snapshot balances
    InterlockedDecrement(&state->InFlight);
}
inline void QcRamAsyncDeclined(QC_RAM_ASYNC* state)
{
    InterlockedIncrement64(&state->Counts.Declined);
    InterlockedDecrement(&state->InFlight);
}
inline void QcRamAsyncSnapshot(const QC_RAM_ASYNC* state, QC_RAM_ASYNC_DATA* data)
{
    data->Size = sizeof(*data); data->Version = 1;
    data->Generation = static_cast<ULONGLONG>(ReadNoFence64(&state->Generation));
    data->Enabled = ReadNoFence(&state->Enabled) ? 1 : 0;
    data->InFlight = static_cast<ULONGLONG>(ReadNoFence(&state->InFlight));
    data->InlineActive = static_cast<ULONGLONG>(ReadNoFence(&state->InlineActive));
#define QC_ASYNC_COPY(name) data->name = static_cast<ULONGLONG>(ReadNoFence64(&state->Counts.name));
    QC_RAM_ASYNC_COUNTERS(QC_ASYNC_COPY)
#undef QC_ASYNC_COPY
}
