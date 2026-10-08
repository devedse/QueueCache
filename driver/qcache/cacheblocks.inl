// SPDX-License-Identifier: MIT
// Internal block-store implementation, included once by writecache.cpp.
// Caller holds the cache mutex. These helpers never allocate, wait or perform I/O.
// Mutex protects the fixed-size index and every pending payload. Bucket chains are
// newest-first: an immutable in-flight version can coexist with one newer pending version.
static ULONG Bucket(QC_CACHE* c, LONGLONG offset)
{
    return static_cast<ULONG>((static_cast<ULONGLONG>(offset) / Chunk) % c->Capacity);
}
static ULONG FindSlot(QC_CACHE* c, LONGLONG offset)
{
    for (auto i = c->Buckets[Bucket(c, offset)]; i != NoSlot; i = c->Slots[i].HashNext)
        if (c->Slots[i].Offset.QuadPart == offset)
            return i;
    return NoSlot;
}
static void IndexSlot(QC_CACHE* c, ULONG i)
{
    auto slot = &c->Slots[i];
    auto bucket = Bucket(c, slot->Offset.QuadPart);
    slot->HashPrevious = NoSlot;
    slot->HashNext = c->Buckets[bucket];
    if (slot->HashNext != NoSlot)
        c->Slots[slot->HashNext].HashPrevious = i;
    c->Buckets[bucket] = i;
}
static void UnindexSlot(QC_CACHE* c, ULONG i)
{
    auto slot = &c->Slots[i];
    if (slot->HashPrevious == NoSlot)
        c->Buckets[Bucket(c, slot->Offset.QuadPart)] = slot->HashNext;
    else
        c->Slots[slot->HashPrevious].HashNext = slot->HashNext;
    if (slot->HashNext != NoSlot)
        c->Slots[slot->HashNext].HashPrevious = slot->HashPrevious;
}
// A slot belongs to exactly one list: dirty FIFO or one clean LRU.
// The index may briefly contain an in-flight old version and a newer dirty one.
static void Unlink(QC_CACHE* c, ULONG i)
{
    auto s = &c->Slots[i];
    auto head = s->Dirty ? &c->Head : &c->CleanHead[s->ReadClass ? 1 : 0];
    auto tail = s->Dirty ? &c->Tail : &c->CleanTail[s->ReadClass ? 1 : 0];
    if (s->QueuePrevious != NoSlot)
        c->Slots[s->QueuePrevious].QueueNext = s->QueueNext;
    else
        *head = s->QueueNext;
    if (s->QueueNext != NoSlot)
        c->Slots[s->QueueNext].QueuePrevious = s->QueuePrevious;
    else
        *tail = s->QueuePrevious;
    if (!s->Dirty)
    {
        --c->CleanCount[s->ReadClass ? 1 : 0];
        c->CleanValidBytes[s->ReadClass ? 1 : 0] -= QcValidBytes(s->ValidSectors);
    }
}
static void Link(QC_CACHE* c, ULONG i)
{
    auto s = &c->Slots[i];
    auto head = s->Dirty ? &c->Head : &c->CleanHead[s->ReadClass ? 1 : 0];
    auto tail = s->Dirty ? &c->Tail : &c->CleanTail[s->ReadClass ? 1 : 0];
    s->QueuePrevious = *tail;
    s->QueueNext = NoSlot;
    if (*tail != NoSlot)
        c->Slots[*tail].QueueNext = i;
    else
        *head = i;
    *tail = i;
    if (!s->Dirty)
    {
        ++c->CleanCount[s->ReadClass ? 1 : 0];
        c->CleanValidBytes[s->ReadClass ? 1 : 0] += QcValidBytes(s->ValidSectors);
        s->DirtySince = NowMs();
    }
}
static ULONG AllocateSlot(QC_CACHE* c, bool dirty = true, bool read = false)
{
    auto i = c->FreeHead;
    auto s = &c->Slots[i];
    c->FreeHead = s->FreeNext;
    s->Dirty = dirty;
    s->ReadClass = read;
    s->InFlight = FALSE;
    s->DirtySince = dirty ? NowMs() : 0;
    s->Pins = 0;
    s->ValidSectors = 0;
    s->Filling = s->RetireWhenUnpinned = FALSE;
    Link(c, i);
    ++c->Count;
    return i;
}
static void RetireSlot(QC_CACHE* c, ULONG i)
{
    NT_ASSERT(c->Slots[i].Pins == 0 && !c->Slots[i].InFlight && !c->Slots[i].Filling);
    auto s = &c->Slots[i];
    UnindexSlot(c, i);
    Unlink(c, i);
    s->Length = 0;
    s->InFlight = FALSE;
    s->FreeNext = c->FreeHead;
    c->FreeHead = i;
    --c->Count;
}
static bool Evict(QC_CACHE* c, ULONG pool)
{
    auto i = c->CleanHead[pool];
    while (i != NoSlot && c->Slots[i].Pins)
        i = c->Slots[i].QueueNext;
    if (i == NoSlot)
        return false;
    RetireSlot(c, i);
    ++c->Evictions;
    return true;
}
static void UnpinSlot(QC_CACHE* c, ULONG i)
{
    auto slot = &c->Slots[i];
    NT_ASSERT(slot->Pins != 0);
    --slot->Pins;
    if (!slot->Pins && slot->RetireWhenUnpinned)
        RetireSlot(c, i);
}
static void ClearClean(QC_CACHE* c)
{
    for (ULONG pool = 0; pool < 2; ++pool)
        while (Evict(c, pool))
        {
        }
}
// Explicit diagnostic control only. Caller holds the cache mutex. Validate the
// whole empty boundary before changing any links; never move/free/zero payload,
// drain data, change eviction policy, or reset counters/generation.
static NTSTATUS ResetFreeOrder(QC_CACHE* c)
{
    if (!c->Capacity || !c->Slots || c->Gone || c->Suspended || c->OwnedRamDevice ||
        c->State.BudgetBytes > (2ULL << 30) || QcCachePagingPathCount(c) > 0)
        return STATUS_INVALID_DEVICE_STATE;
    if (!NT_SUCCESS(c->State.LastError))
        return c->State.LastError;
    if (c->Count || c->State.DirtyBytes || c->State.InFlightBytes)
        return STATUS_DEVICE_BUSY;
    for (ULONG i = 0; i < c->Capacity; ++i)
        if (c->Slots[i].Length || c->Slots[i].Pins || c->Slots[i].InFlight || c->Slots[i].Filling)
            return STATUS_DEVICE_BUSY;
    for (ULONG i = 0; i < c->Capacity; ++i)
        c->Slots[i].FreeNext = i + 1 < c->Capacity ? i + 1 : NoSlot;
    c->FreeHead = 0;
    return STATUS_SUCCESS;
}
// Called after overlapping dirty/in-flight versions have drained (or when none
// existed), before forwarding an uncached write. Caller owns range ordering.
// Only intersecting blocks become stale; unrelated clean payload must survive.
static void InvalidateCleanRange(QC_CACHE* c, LONGLONG offset, ULONG length)
{
    if (!length || !c->Capacity)
        return;
    const auto end = offset + length; // Caller validated the range against device length.
    for (auto block = offset - offset % Chunk; block < end; block += Chunk)
    {
        auto i = FindSlot(c, block);
        if (i != NoSlot && !c->Slots[i].Dirty)
            RetireSlot(c, i);
    }
}
static bool EvictOldest(QC_CACHE* c)
{
    if (c->CleanHead[0] == NoSlot)
        return Evict(c, 1);
    if (c->CleanHead[1] == NoSlot)
        return Evict(c, 0);
    auto first = c->Slots[c->CleanHead[0]].DirtySince <= c->Slots[c->CleanHead[1]].DirtySince ? 0UL : 1UL;
    return Evict(c, first) || Evict(c, 1 - first);
}
static bool EvictForWrite(QC_CACHE* c, ULONG requestSlots)
{
    // Retained writes are expendable first. Below the read-demand floor, wait
    // for dirty draining instead of wiping the hot set. Never evict dirty slots.
    if (Evict(c, 0))
        return true;
    const auto protectedReads = QcProtectedReadSlots(c->Capacity, c->CleanCount[1], requestSlots);
    return c->CleanCount[1] > protectedReads && Evict(c, 1);
}
static ULONG WriteLimit(QC_CACHE* c)
{
    return QcWriteLimit(c->Options, c->Capacity);
}
static ULONG ReadLimit(QC_CACHE* c)
{
    return c->Options.Allocation == QcAutomatic ? c->Capacity : c->Capacity - WriteLimit(c);
}
static bool ReadRoom(QC_CACHE* c)
{
    auto limit = ReadLimit(c);
    if (!limit)
        return false;
    if (c->Options.Allocation == QcFixed && c->CleanCount[1] >= limit && !Evict(c, 1))
        return false;
    if (c->Count == c->Capacity && !(c->Options.Allocation == QcAutomatic ? EvictOldest(c) : Evict(c, 1)))
        return false;
    return true;
}
// Scan resistance (bimodal insertion): a block read once goes to the eviction end of
// its clean list, so a one-off large read evicts itself first; a later hit (TouchClean)
// makes it recent. One fill in 16 stays recent so a large working set still settles.
static void DemoteReadFill(QC_CACHE* c, ULONG i)
{
    if (++c->ReadFillsSinceRecent % 16 == 0)
        return;
    auto s = &c->Slots[i];
    const ULONG pool = s->ReadClass ? 1 : 0;
    Unlink(c, i);
    s->QueuePrevious = NoSlot;
    s->QueueNext = c->CleanHead[pool];
    if (s->QueueNext != NoSlot)
        c->Slots[s->QueueNext].QueuePrevious = i;
    else
        c->CleanTail[pool] = i;
    c->CleanHead[pool] = i;
    ++c->CleanCount[pool];
    c->CleanValidBytes[pool] += QcValidBytes(s->ValidSectors);
    s->DirtySince = 0; // Oldest, so the cross-pool eviction choice prefers it too.
}
static void TouchClean(QC_CACHE* c, ULONG i)
{
    auto slot = &c->Slots[i];
    if (slot->Dirty)
        return;
    if (!slot->ReadClass && (c->Options.Retention & QcPromoteReads) && ReadLimit(c))
    {
        if (c->Options.Allocation == QcFixed && c->CleanCount[1] >= ReadLimit(c) && !Evict(c, 1))
            return;
        Unlink(c, i);
        slot->ReadClass = TRUE;
        Link(c, i);
    }
    else
    {
        Unlink(c, i);
        Link(c, i);
    }
}
static ULONG FindOldestSlot(QC_CACHE* c, LONGLONG offset)
{
    auto oldest = NoSlot;
    for (auto i = c->Buckets[Bucket(c, offset)]; i != NoSlot; i = c->Slots[i].HashNext)
        if (c->Slots[i].Offset.QuadPart == offset)
            if (c->Slots[i].Dirty)
                oldest = i;
    return oldest;
}
