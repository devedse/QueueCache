// SPDX-License-Identifier: MIT
#pragma once
#include <ntddk.h>
#include "ramdiskprotocol.h"

// One RAM disk's storage. The provider (qcramdisk) owns it; with Direct access the
// volume filter reads and writes the same pages, so the data exists once. Both paths
// admit writes through the same gate, which Freeze and SetReadOnly close.
struct QC_RAM_SLAB { PMDL Mdl; PUCHAR Bytes; ULONG Length; };
struct QC_RAM_STORE
{
    ULONG Size, Version;        // Checked by the volume filter at registration.
    ULONGLONG Capacity;
    ULONG SectorBytes, SlabCount;
    QC_RAM_SLAB* Slabs;         // QcRamSlabBytes each; only the last may be shorter.
    volatile LONG Flags;        // QC_RAM_FLAGS. Changed only with interlocked operations.
    volatile LONG ActiveWrites; // Writes admitted and still copying.
    volatile LONG64 Generation; // Advances with every admitted change (write or TRIM).
};
constexpr ULONG QcRamStoreVersion = 1;

inline bool QcRamStoreBounds(const QC_RAM_STORE* store, ULONGLONG offset, ULONGLONG bytes)
{
    return offset <= store->Capacity && bytes <= store->Capacity - offset &&
           offset % store->SectorBytes == 0 && bytes % store->SectorBytes == 0;
}

// Copies between a mapped buffer and the store (or zeroes, for TRIM). Bounds are checked
// by the caller. Callable at IRQL <= DISPATCH_LEVEL: every slab is locked and mapped.
inline void QcRamStoreCopy(const QC_RAM_STORE* store, ULONGLONG offset, PUCHAR buffer, ULONG length, bool write, bool zero = false)
{
    while (length)
    {
        const auto index = static_cast<ULONG>(offset / QcRamSlabBytes), within = static_cast<ULONG>(offset % QcRamSlabBytes);
        const auto& slab = store->Slabs[index];
        const auto bytes = min(length, slab.Length - within);
        auto storage = slab.Bytes + within;
        if (zero) RtlZeroMemory(storage, bytes);
        else if (write) RtlCopyMemory(storage, buffer, bytes);
        else RtlCopyMemory(buffer, storage, bytes);
        if (buffer) buffer += bytes;
        offset += bytes; length -= bytes;
    }
}

enum class QcRamAdmission { Admitted, ReadOnly, Frozen };
// Count the write first, then read the flags. Freeze and SetReadOnly set their flag first,
// then wait for ActiveWrites to drain, so neither can miss a write that passed this check.
// Interlocked operations are full barriers, which this ordering relies on.
inline QcRamAdmission QcRamStoreBeginWrite(QC_RAM_STORE* store)
{
    InterlockedIncrement(&store->ActiveWrites);
    const auto flags = InterlockedOr(&store->Flags, 0);
    if (flags & (QcRamReadOnly | QcRamFrozen))
    {
        InterlockedDecrement(&store->ActiveWrites);
        return (flags & QcRamReadOnly) ? QcRamAdmission::ReadOnly : QcRamAdmission::Frozen;
    }
    return QcRamAdmission::Admitted;
}
// Call between BeginWrite and EndWrite, once the change is certain.
inline void QcRamStoreChanged(QC_RAM_STORE* store) { InterlockedIncrement64(&store->Generation); }
inline void QcRamStoreEndWrite(QC_RAM_STORE* store) { InterlockedDecrement(&store->ActiveWrites); }
