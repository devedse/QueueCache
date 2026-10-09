// SPDX-License-Identifier: MIT
#pragma once
// Read recall: a short history of evicted blocks and when each was last used. No allocation,
// waits or I/O; the caller holds the cache mutex and owns the table.
//
// A read miss is normally kept at the eviction end ("just read"), so a one-off scan only ever
// evicts its own blocks. A miss whose block is in the history and was last used more recently
// than the oldest used block still cached is kept as recent instead: the order a least-recently
// used cache with a longer memory would choose. So a file that fits settles on its second pass
// (bimodal insertion needed dozens), while data in use is never displaced by a scan or by a loop
// larger than the cache. Modelled before implementation (SUSTAINED_CACHE_VALIDATION_20261009.md).
//
// One 32-bit entry per cache slot, in 4-way sets: tag (16 bits) | last use (16 bits, 256 ms).
// A false tag match only keeps one block as recent; it never affects which data is returned.
constexpr ULONG QcRecallWays = 4;
constexpr ULONG QcRecallUnitShift = 8;     // 256 ms units; 16 bits wrap after about 4.6 hours.
constexpr ULONG QcRecallProbationScan = 8; // Just-read blocks skipped at the eviction end.

constexpr ULONG QcRecallSet(ULONGLONG block, ULONG sets)
{
    const auto h = block * 0x9E3779B97F4A7C15ULL; // Spreads consecutive blocks evenly over the sets.
    return static_cast<ULONG>(((h >> 32) * sets) >> 32);
}
constexpr ULONG QcRecallTag(ULONGLONG block)
{
    const auto tag = static_cast<ULONG>((block * 0xC2B2AE3D27D4EB4FULL) >> 48);
    return tag ? tag : 1; // 0 marks an empty entry.
}
constexpr ULONG QcRecallUnits(ULONGLONG ms)
{
    return static_cast<ULONG>(ms >> QcRecallUnitShift) & 0xFFFF;
}
constexpr ULONG QcRecallAge(ULONG entry, ULONGLONG nowMs)
{
    return (QcRecallUnits(nowMs) - (entry & 0xFFFF)) & 0xFFFF;
}
// Record an evicted block. Its own entry is refreshed, else an empty or the oldest one is
// replaced. A block not used for a whole wrap period is dropped instead of aliasing.
constexpr void QcRecallRecord(ULONG* table, ULONG sets, ULONGLONG block, ULONGLONG lastUseMs, ULONGLONG nowMs)
{
    const auto set = table + static_cast<ULONGLONG>(QcRecallSet(block, sets)) * QcRecallWays;
    const auto tag = QcRecallTag(block);
    const bool stale = lastUseMs > nowMs || ((nowMs - lastUseMs) >> QcRecallUnitShift) >= 0xFFFF;
    ULONG victim = 0;
    for (ULONG way = 0; way < QcRecallWays; ++way)
    {
        if (set[way] >> 16 == tag)
        {
            victim = way;
            break;
        }
        if (set[victim] && (!set[way] || QcRecallAge(set[way], nowMs) > QcRecallAge(set[victim], nowMs)))
            victim = way;
    }
    if (stale)
    {
        if (set[victim] >> 16 == tag)
            set[victim] = 0;
        return;
    }
    set[victim] = (tag << 16) | QcRecallUnits(lastUseMs);
}
// Remove a block's entry. Returns its last-use age in 256 ms units, or -1 if it has none.
constexpr LONG QcRecallTake(ULONG* table, ULONG sets, ULONGLONG block, ULONGLONG nowMs)
{
    const auto set = table + static_cast<ULONGLONG>(QcRecallSet(block, sets)) * QcRecallWays;
    const auto tag = QcRecallTag(block);
    for (ULONG way = 0; way < QcRecallWays; ++way)
        if (set[way] >> 16 == tag)
        {
            const auto age = QcRecallAge(set[way], nowMs);
            set[way] = 0;
            return static_cast<LONG>(age);
        }
    return -1;
}
// Keep the miss as recent when it was used strictly more recently (in 256 ms units) than the
// oldest used block it would displace. victimSinceMs 0: only just-read blocks or free room.
constexpr bool QcRecallKeeps(LONG age, ULONGLONG victimSinceMs, ULONGLONG nowMs)
{
    if (age < 0)
        return false;
    if (!victimSinceMs)
        return true;
    const auto victimAge = victimSinceMs >= nowMs ? 0 : (nowMs - victimSinceMs) >> QcRecallUnitShift;
    return static_cast<ULONGLONG>(age) < victimAge;
}
