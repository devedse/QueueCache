// SPDX-License-Identifier: MIT
#pragma once
#include <ntddk.h>

// Per-store mode (QcRamAsyncMode; adaptive at creation). A large asynchronous Direct read
// hands every split chunk to the provider's helpers and returns; the helper that copies the
// last chunk completes the request, so one thread's queue depth overlaps copying with
// submission (lab VM, 1 MiB Q8T1: 24.3 -> 44.8 GiB/s). Off: one flag read per large read. Admission reuses the provider's write
// policy (asynchronous while another is in flight; inline after a run that found none,
// probing periodically). It stays inline while more than one thread submits: several
// submitters already keep every processor copying (measured: four readers lost 7% when
// all of them handed off).
#define QC_RAM_ASYNC_COUNTERS(X) \
    X(Started) X(Completed) X(Declined) X(MaxInFlight) X(Probes) \
    X(InlineStreak) X(InlineBusy) X(InlineCap)
struct QC_RAM_ASYNC_COUNTS
{
#define QC_ASYNC_FIELD(name) volatile LONG64 name;
    QC_RAM_ASYNC_COUNTERS(QC_ASYNC_FIELD)
#undef QC_ASYNC_FIELD
};
// Off; adaptive (the policy above); always (lab correctness checks: skips only the
// queue-depth policy, never the submitter guard or the cap).
enum QC_RAM_ASYNC_MODE : LONG { QcRamAsyncOff = 0, QcRamAsyncAdaptive = 1, QcRamAsyncAlways = 2 };
struct QC_RAM_ASYNC
{
    volatile LONG Enabled;      // QC_RAM_ASYNC_MODE
    volatile LONG InFlight;     // Admitted, not yet completed or declined. Never reset.
    volatile LONG InlineActive; // Large inline Direct reads copying while Enabled.
    LONG Streak, Probe;         // Racy hints, like the provider's write policy.
    PVOID Submitter;            // Racy hints: the last submitting thread, and when
    LONG64 OtherSubmitter;      // another thread last submitted (QPC ticks).
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
constexpr LONG64 QcRamAsyncSubmitterWindowUs = 1000; // Several submitters within this window: inline.

// Provider ControlLock serializes settings. Turning it on (either mode) starts a new
// generation and requires nothing in flight, so counters never include an earlier request.
inline bool QcRamAsyncConfigure(QC_RAM_ASYNC* state, LONG mode)
{
    if (mode == QcRamAsyncOff) { InterlockedExchange(&state->Enabled, 0); return true; } // keep counters for readback
    InterlockedExchange(&state->Enabled, 0);
    if (ReadNoFence(&state->InFlight)) return false;
    RtlZeroMemory(&state->Counts, sizeof(state->Counts));
    state->Streak = state->Probe = 0;
    state->Submitter = nullptr; state->OtherSubmitter = 0;
    InterlockedIncrement64(&state->Generation);
    InterlockedExchange(&state->Enabled, mode);
    return true;
}
// A large asynchronous non-paging read while Enabled. Each admission ends in exactly one of
// QcRamAsyncCompleted or QcRamAsyncDeclined.
inline bool QcRamAsyncAdmit(QC_RAM_ASYNC* state, LONG mode)
{
    auto& counts = state->Counts;
    LARGE_INTEGER frequency;
    const auto now = KeQueryPerformanceCounter(&frequency).QuadPart;
    const PVOID thread = KeGetCurrentThread();
    if (const auto last = state->Submitter; last != thread)
    {
        if (last) state->OtherSubmitter = now; // The first submitter after enabling is not "another".
        state->Submitter = thread;
    }
    if (ReadNoFence(&state->InlineActive) ||
        now - state->OtherSubmitter < frequency.QuadPart * QcRamAsyncSubmitterWindowUs / 1000000)
    {
        InterlockedIncrement64(&counts.InlineBusy);
        return false;
    }
    const auto inFlight = ReadNoFence(&state->InFlight);
    if (inFlight >= QcRamAsyncCap) { InterlockedIncrement64(&counts.InlineCap); return false; }
    if (mode == QcRamAsyncAlways) InterlockedIncrement64(&counts.Probes); // counted as forced admissions
    else if (inFlight) state->Streak = 0;
    else if (state->Streak < QcRamAsyncInlineAfter) ++state->Streak;
    else if (++state->Probe % QcRamAsyncProbeEvery) { InterlockedIncrement64(&counts.InlineStreak); return false; }
    else InterlockedIncrement64(&counts.Probes);
    const LONG64 admitted = InterlockedIncrement(&state->InFlight);
    InterlockedIncrement64(&counts.Started);
    for (auto seen = ReadNoFence64(&counts.MaxInFlight); admitted > seen;)
    {
        const auto prior = InterlockedCompareExchange64(&counts.MaxInFlight, admitted, seen);
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
    data->Enabled = static_cast<ULONGLONG>(ReadNoFence(&state->Enabled));
    data->InFlight = static_cast<ULONGLONG>(ReadNoFence(&state->InFlight));
    data->InlineActive = static_cast<ULONGLONG>(ReadNoFence(&state->InlineActive));
#define QC_ASYNC_COPY(name) data->name = static_cast<ULONGLONG>(ReadNoFence64(&state->Counts.name));
    QC_RAM_ASYNC_COUNTERS(QC_ASYNC_COPY)
#undef QC_ASYNC_COPY
}
