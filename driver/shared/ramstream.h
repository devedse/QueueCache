// SPDX-License-Identifier: MIT
#pragma once
#include <ntddk.h>
#include <emmintrin.h>

// Per-store write copy mode (plan 120 experiment; off at creation). Large RAM-disk writes are
// limited by memory bandwidth, not by request handling: on the lab VM four submitters write no
// faster than one (1 MiB: 23.2-23.7 GiB/s), and copying into memory that is not in the processor's
// cache costs about twice a read (user-mode probe, four threads: 22.5 GiB/s into cold memory,
// 47.4 out of it). Ordinary stores first read each destination line; streaming (non-temporal)
// stores write whole lines without reading them (one thread: 23.7 instead of 18.1 GiB/s).
// Small writes keep ordinary stores: their data is likely to be read again soon. Off costs one
// flag read per large write.
enum QC_RAM_STREAM_MODE : LONG { QcRamStreamOff = 0, QcRamStreamLarge = 1 };
constexpr ULONG QcRamStreamMinBytes = 64 * 1024;
struct QC_RAM_STREAM
{
    volatile LONG Mode;                 // QC_RAM_STREAM_MODE
    mutable volatile LONG64 Generation; // Advances when a mode is set; the counters restart.
    mutable volatile LONG64 Writes, Bytes;
};
struct QC_RAM_STREAM_DATA
{
    ULONG Size, Version;
    ULONGLONG Generation, Mode, Writes, Bytes;
};
static_assert(sizeof(QC_RAM_STREAM_DATA) == 40, "RAM write copy mode wire size");

// Provider ControlLock serializes settings. Counters are exact at quiescent boundaries.
inline void QcRamStreamConfigure(QC_RAM_STREAM* state, LONG mode)
{
    InterlockedExchange(&state->Mode, QcRamStreamOff);
    InterlockedExchange64(&state->Writes, 0);
    InterlockedExchange64(&state->Bytes, 0);
    InterlockedIncrement64(&state->Generation);
    InterlockedExchange(&state->Mode, mode);
}
inline void QcRamStreamSnapshot(const QC_RAM_STREAM* state, QC_RAM_STREAM_DATA* data)
{
    data->Size = sizeof(*data); data->Version = 1;
    data->Generation = static_cast<ULONGLONG>(ReadNoFence64(&state->Generation));
    data->Mode = static_cast<ULONGLONG>(ReadNoFence(&state->Mode));
    data->Writes = static_cast<ULONGLONG>(ReadNoFence64(&state->Writes));
    data->Bytes = static_cast<ULONGLONG>(ReadNoFence64(&state->Bytes));
}
// Destination: a page-aligned slab at a sector offset; bytes: whole sectors. The caller fences
// once after the whole transfer, before the request can complete.
inline void QcRamStreamCopy(PUCHAR destination, const UCHAR* source, ULONG bytes)
{
    NT_ASSERT((reinterpret_cast<ULONG_PTR>(destination) & 15) == 0 && bytes % 64 == 0);
    for (ULONG i = 0; i < bytes; i += 64)
    {
        const auto a = _mm_loadu_si128(reinterpret_cast<const __m128i*>(source + i));
        const auto b = _mm_loadu_si128(reinterpret_cast<const __m128i*>(source + i + 16));
        const auto c = _mm_loadu_si128(reinterpret_cast<const __m128i*>(source + i + 32));
        const auto d = _mm_loadu_si128(reinterpret_cast<const __m128i*>(source + i + 48));
        _mm_stream_si128(reinterpret_cast<__m128i*>(destination + i), a);
        _mm_stream_si128(reinterpret_cast<__m128i*>(destination + i + 16), b);
        _mm_stream_si128(reinterpret_cast<__m128i*>(destination + i + 32), c);
        _mm_stream_si128(reinterpret_cast<__m128i*>(destination + i + 48), d);
    }
}
