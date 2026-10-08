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
// Free slots are tracked per 256 KiB chunk (64 slots, one bit each). Allocation fills one
// open chunk upwards and then opens a wholly free chunk, else the partial chunk whose state
// changed last. Measured: only the order inside a chunk matters, and descending or scattered
// order inside one costs up to 45% (CACHE_LAYOUT_INVESTIGATION_20261008.md). The lists change
// only when a chunk changes state, so every operation is O(1).
static ULONG ChunkState(const QC_CHUNK* chunk) // 0 none free, 1 partially, 2 wholly free
{
    return !chunk->Free ? 0 : chunk->Free == ~0ULL ? 2 : 1;
}
static void UnlinkChunk(QC_CACHE* c, ULONG j, ULONG state)
{
    if (!state)
        return;
    const auto chunk = &c->Chunks[j];
    if (chunk->Previous == NoSlot)
        c->ChunkHead[state - 1] = chunk->Next;
    else
        c->Chunks[chunk->Previous].Next = chunk->Next;
    if (chunk->Next != NoSlot)
        c->Chunks[chunk->Next].Previous = chunk->Previous;
}
static void LinkChunk(QC_CACHE* c, ULONG j)
{
    const auto chunk = &c->Chunks[j];
    const auto state = ChunkState(chunk);
    if (!state)
        return;
    chunk->Previous = NoSlot;
    chunk->Next = c->ChunkHead[state - 1];
    if (chunk->Next != NoSlot)
        c->Chunks[chunk->Next].Previous = j;
    c->ChunkHead[state - 1] = j;
}
static void SetSlotFree(QC_CACHE* c, ULONG i, bool free)
{
    const auto j = i / SlotsPerSlab;
    const auto chunk = &c->Chunks[j];
    const auto before = ChunkState(chunk);
    const auto bit = 1ULL << (i % SlotsPerSlab);
    NT_ASSERT(((chunk->Free & bit) != 0) != free);
    chunk->Free = free ? chunk->Free | bit : chunk->Free & ~bit;
    if (ChunkState(chunk) != before)
    {
        UnlinkChunk(c, j, before);
        LinkChunk(c, j);
    }
}
// Every slot free, chunks listed in ascending order, no open chunk. Capacity is whole chunks.
static void ResetChunks(QC_CACHE* c)
{
    c->ChunkHead[0] = c->ChunkHead[1] = NoSlot;
    for (auto j = c->Capacity / SlotsPerSlab; j-- > 0;)
    {
        c->Chunks[j].Free = ~0ULL;
        LinkChunk(c, j);
    }
    c->OpenChunk = NoSlot;
    c->OpenNext = 0;
}
static ULONG TakeSlot(QC_CACHE* c)
{
    for (int attempt = 0; attempt < 2; ++attempt)
    {
        if (c->OpenChunk != NoSlot && c->OpenNext < SlotsPerSlab)
        {
            const auto bits = c->Chunks[c->OpenChunk].Free & (~0ULL << c->OpenNext);
            if (bits)
            {
                ULONG bit;
                _BitScanForward64(&bit, bits);
                c->OpenNext = bit + 1;
                const auto i = c->OpenChunk * SlotsPerSlab + bit;
                SetSlotFree(c, i, false);
                return i;
            }
        }
        c->OpenChunk = c->ChunkHead[1] != NoSlot ? c->ChunkHead[1] : c->ChunkHead[0];
        c->OpenNext = 0;
    }
    NT_ASSERT(FALSE); // Callers make room first (Count < Capacity), as with the old free list.
    return NoSlot;
}
static ULONG AllocateSlot(QC_CACHE* c, bool dirty = true, bool read = false)
{
    const auto i = TakeSlot(c);
    auto s = &c->Slots[i];
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
    SetSlotFree(c, i, true);
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
    ResetChunks(c);
    return STATUS_SUCCESS;
}
// Explicit diagnostic control only. Caller holds the cache mutex. Read-only pass:
// how many cached disk neighbours are also memory neighbours (V18 diagnostics).
static NTSTATUS MeasureLayout(QC_CACHE* c)
{
    if (!c->Capacity || !c->Slots || c->Gone || QcCachePagingPathCount(c) > 0)
        return STATUS_INVALID_DEVICE_STATE;
    ULONGLONG freeChunks = 0, blocks = 0, neighbors = 0, contiguous = 0, reversed = 0;
    for (ULONG j = 0; j < c->Capacity / SlotsPerSlab; ++j)
        freeChunks += c->Chunks[j].Free == ~0ULL;
    for (ULONG i = 0; i < c->Capacity; ++i)
    {
        // Only the newest indexed version of a block counts; free slots keep stale offsets.
        const auto slot = &c->Slots[i];
        if (FindSlot(c, slot->Offset.QuadPart) != i)
            continue;
        ++blocks;
        const auto next = FindSlot(c, slot->Offset.QuadPart + Chunk);
        if (next == NoSlot)
            continue;
        ++neighbors;
        contiguous += c->Slots[next].Buffer == slot->Buffer + Chunk;
        reversed += c->Slots[next].Buffer + Chunk == slot->Buffer;
    }
    c->LayoutBlocks = blocks;
    c->LayoutNeighbors = neighbors;
    c->LayoutContiguous = contiguous;
    c->LayoutReversed = reversed;
    c->LayoutFreeChunks = freeChunks;
    ++c->LayoutMeasurements;
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
