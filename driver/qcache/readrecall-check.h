// SPDX-License-Identifier: MIT
#pragma once
#include "readrecall.h"
// Compile-time regression tests of the exact read-recall table code the driver runs.
constexpr bool RecallChecks()
{
    constexpr ULONG sets = 16;
    ULONG table[sets * QcRecallWays] = {};
    constexpr ULONGLONG now = 14'080ULL << QcRecallUnitShift; // About an hour after boot, unit-aligned.
    // 48 consecutive blocks (a sequential file) fit 16 four-way sets; each is found once.
    for (ULONGLONG block = 1000; block < 1048; ++block)
        QcRecallRecord(table, sets, block, now - 1024, now);
    for (ULONGLONG block = 1000; block < 1048; ++block)
        if (QcRecallTake(table, sets, block, now) != static_cast<LONG>(1024 >> QcRecallUnitShift) ||
            QcRecallTake(table, sets, block, now) != -1)
            return false;
    for (auto entry : table)
        if (entry)
            return false;
    // Recording a block again refreshes its own entry instead of using a second way.
    QcRecallRecord(table, sets, 7, now - 61'440, now);
    QcRecallRecord(table, sets, 7, now, now);
    if (QcRecallTake(table, sets, 7, now) != 0 || QcRecallTake(table, sets, 7, now) != -1)
        return false;
    // A full set replaces its oldest entry. Blocks 0, 13, 34, 47 and 68 share set 0 of 16.
    const ULONGLONG same[] = {0, 13, 34, 47, 68};
    for (auto block : same)
        if (QcRecallSet(block, sets) != 0)
            return false;
    QcRecallRecord(table, sets, same[0], now - 4096, now);
    QcRecallRecord(table, sets, same[1], now - 9216, now); // Oldest.
    QcRecallRecord(table, sets, same[2], now - 2048, now);
    QcRecallRecord(table, sets, same[3], now - 3072, now);
    QcRecallRecord(table, sets, same[4], now - 1024, now);
    if (QcRecallTake(table, sets, same[1], now) != -1 || QcRecallTake(table, sets, same[4], now) < 0 ||
        QcRecallTake(table, sets, same[0], now) < 0 || QcRecallTake(table, sets, same[2], now) < 0 ||
        QcRecallTake(table, sets, same[3], now) < 0)
        return false;
    // A block last used a whole wrap period ago is dropped, not aliased to a recent age.
    QcRecallRecord(table, sets, 9, now - 1024, now);
    QcRecallRecord(table, sets, 9, 0, now + (0x10000ULL << QcRecallUnitShift));
    if (QcRecallTake(table, sets, 9, now) != -1)
        return false;
    // Ages survive the 16-bit wrap of the stored time.
    constexpr ULONGLONG wrap = 0x10000ULL << QcRecallUnitShift;
    QcRecallRecord(table, sets, 11, wrap - 512, wrap + 512);
    if (QcRecallTake(table, sets, 11, wrap + 512) != static_cast<LONG>(1024 >> QcRecallUnitShift))
        return false;
    // Keep as recent only when strictly newer than the oldest used block it would displace.
    return QcRecallKeeps(0, now - 10'240, now) && !QcRecallKeeps(-1, now - 10'240, now) &&
           QcRecallKeeps(-1, 0, now) == false && QcRecallKeeps(5, 0, now) &&
           !QcRecallKeeps(static_cast<LONG>(10'240 >> QcRecallUnitShift), now - 10'240, now) &&
           QcRecallKeeps(static_cast<LONG>(10'240 >> QcRecallUnitShift) - 1, now - 10'240, now) &&
           !QcRecallKeeps(0, now, now) && QcRecallTag(0) != 0 && QcRecallSet(~0ULL, sets) < sets;
}
static_assert(RecallChecks(), "Read recall table regression");
