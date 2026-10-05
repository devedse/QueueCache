// SPDX-License-Identifier: MIT
#pragma once
#include <ntddk.h>

// Shared physical-page primitive for the cache and RAM-disk provider. No backing I/O.
// Call only from an allowed allocation worker context, never a Storport fast callback.
// Fully page-aligned allocations have one PFN entry per page plus one MDL per slab.
constexpr ULONGLONG QcLockedPageMetadataBytes(ULONGLONG bytes, ULONGLONG slabs)
{
    return bytes / PAGE_SIZE * sizeof(PFN_NUMBER) + slabs * sizeof(MDL);
}
inline PUCHAR QcAllocateLockedPages(SIZE_T bytes, PMDL* mdl)
{
    if (!mdl || !bytes || bytes % PAGE_SIZE || bytes > MAXULONG)
        return nullptr;
    *mdl = nullptr;
    PHYSICAL_ADDRESS low{}, high{}, skip{};
    high.QuadPart = -1;
    auto pages = MmAllocatePagesForMdlEx(low, high, skip, bytes, MmCached, MM_ALLOCATE_FULLY_REQUIRED);
    if (!pages)
        return nullptr;
    if (MmGetMdlByteCount(pages) != bytes)
    {
        MmFreePagesFromMdl(pages);
        ExFreePool(pages);
        return nullptr;
    }
    auto mapping = static_cast<PUCHAR>(MmMapLockedPagesSpecifyCache(
        pages, KernelMode, MmCached, nullptr, FALSE, NormalPagePriority | MdlMappingNoExecute));
    if (!mapping)
    {
        MmFreePagesFromMdl(pages);
        ExFreePool(pages);
        return nullptr;
    }
    *mdl = pages;
    return mapping;
}
inline void QcFreeLockedPages(PMDL mdl, PUCHAR mapping, bool erase = false)
{
    if (erase)
        RtlSecureZeroMemory(mapping, MmGetMdlByteCount(mdl));
    MmUnmapLockedPages(mapping, mdl);
    MmFreePagesFromMdl(mdl);
    ExFreePool(mdl);
}
