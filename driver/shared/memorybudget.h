// SPDX-License-Identifier: MIT
#pragma once
#include <ntddk.h>

// There is one authority instance in qcache, not one independent global in each driver.
// A separate RAM provider must obtain owned reservations through that authority's
// kernel control endpoint; compiling this helper into another binary does not share state.
struct QC_MEMORY_BUDGET
{
    volatile LONG64 ReservedBytes;
    volatile LONG64 LimitBytes;
};
constexpr bool QcMemoryReservationFits(ULONGLONG total, ULONGLONG limit, ULONGLONG bytes)
{
    return bytes && bytes <= MAXLONGLONG && total <= limit && limit <= MAXLONGLONG && bytes <= limit - total;
}
inline ULONGLONG QcReservedMemory(QC_MEMORY_BUDGET* authority)
{
    return static_cast<ULONGLONG>(InterlockedCompareExchange64(&authority->ReservedBytes, 0, 0));
}
inline ULONGLONG QcMemoryLimit(QC_MEMORY_BUDGET* authority)
{
    // Supported x64 builds: an aligned volatile 64-bit load is atomic. Avoid
    // adding a locked read-modify-write to every existing cache publication.
    // https://learn.microsoft.com/windows/win32/sync/interlocked-variable-access
    static_assert(sizeof(void*) == 8 && alignof(QC_MEMORY_BUDGET) >= 8, "64-bit atomic budget load required");
    return static_cast<ULONGLONG>(authority->LimitBytes);
}
inline bool QcReserveMemory(QC_MEMORY_BUDGET* authority, ULONGLONG bytes)
{
    for (;;)
    {
        auto total = InterlockedCompareExchange64(&authority->ReservedBytes, 0, 0);
        const auto limit = QcMemoryLimit(authority);
        if (total < 0 || !QcMemoryReservationFits(static_cast<ULONGLONG>(total), limit, bytes))
            return false;
        if (InterlockedCompareExchange64(&authority->ReservedBytes, total + static_cast<LONG64>(bytes), total) == total)
            return true;
    }
}
inline void QcReleaseMemory(QC_MEMORY_BUDGET* authority, ULONGLONG bytes)
{
    if (!bytes)
        return;
    NT_ASSERT(bytes <= QcReservedMemory(authority));
    const auto remaining = InterlockedAdd64(&authority->ReservedBytes, -static_cast<LONG64>(bytes));
    NT_ASSERT(remaining >= 0);
    UNREFERENCED_PARAMETER(remaining);
}
