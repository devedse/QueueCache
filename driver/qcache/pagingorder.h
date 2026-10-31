// SPDX-License-Identifier: MIT
#pragma once

// Mutex-owned, allocation-free set. Callers validate volume bounds before adding.
// The bounding box makes unrelated ordinary writes a constant-time check.
struct QC_SAFE_PAGING
{
    ULONG Count;
    LONGLONG Low, High;
    QC_SPECIAL_RANGE Ranges[QcSpecialRangeMax];
};
constexpr bool QcSafePagingContains(const QC_SAFE_PAGING& set, LONGLONG first, LONGLONG end)
{
    if (!set.Count || first < set.Low || end > set.High || first >= end)
        return false;
    for (ULONG i = 0; i < set.Count; ++i)
        if (set.Ranges[i].Start <= first && set.Ranges[i].Start + set.Ranges[i].Length >= end)
            return true;
    return false;
}
constexpr bool QcSafePagingOverlaps(const QC_SAFE_PAGING& set, LONGLONG first, LONGLONG end)
{
    if (!set.Count || first >= set.High || end <= set.Low || first >= end)
        return false;
    for (ULONG i = 0; i < set.Count; ++i)
        if (set.Ranges[i].Start < end && set.Ranges[i].Start + set.Ranges[i].Length > first)
            return true;
    return false;
}
constexpr bool QcSafePagingAdd(QC_SAFE_PAGING& set, LONGLONG first, LONGLONG end)
{
    if (first < 0 || end <= first)
        return false;
    ULONG begin = 0;
    while (begin < set.Count && set.Ranges[begin].Start + set.Ranges[begin].Length < first)
        ++begin;
    ULONG after = begin;
    while (after < set.Count && set.Ranges[after].Start <= end)
    {
        if (set.Ranges[after].Start < first) first = set.Ranges[after].Start;
        const auto high = set.Ranges[after].Start + set.Ranges[after].Length;
        if (high > end) end = high;
        ++after;
    }
    if (begin == after && set.Count == QcSpecialRangeMax)
        return false; // Leave the old set intact; this request is not learned.
    const auto removed = after - begin;
    const auto count = set.Count - removed + 1;
    if (!removed)
        for (ULONG i = set.Count; i > begin; --i) set.Ranges[i] = set.Ranges[i - 1];
    else
        for (ULONG i = after; i < set.Count; ++i) set.Ranges[i - removed + 1] = set.Ranges[i];
    set.Ranges[begin] = {first, end - first};
    set.Count = count;
    set.Low = set.Ranges[0].Start;
    set.High = set.Ranges[count - 1].Start + set.Ranges[count - 1].Length;
    return true;
}
// Plan 220: removes [first, end) from the set. Ranges partly inside it are shortened, ranges inside it are dropped, and
// a range containing it is split (the ranges are disjoint, so nothing else overlaps then). False, with the set unchanged,
// only when that split needs a slot a full set lacks; the caller then clears the whole set.
constexpr bool QcSafePagingRemove(QC_SAFE_PAGING& set, LONGLONG first, LONGLONG end)
{
    if (first >= end || !set.Count || first >= set.High || end <= set.Low)
        return true;
    for (ULONG i = 0; i < set.Count; ++i)
    {
        const auto start = set.Ranges[i].Start;
        const auto stop = start + set.Ranges[i].Length;
        if (start < first && stop > end)
        {
            if (set.Count == QcSpecialRangeMax)
                return false;
            for (ULONG j = set.Count; j > i + 1; --j) set.Ranges[j] = set.Ranges[j - 1];
            set.Ranges[i] = {start, first - start};
            set.Ranges[i + 1] = {end, stop - end};
            ++set.Count;
            return true; // Low and High are unchanged.
        }
    }
    ULONG kept = 0;
    for (ULONG i = 0; i < set.Count; ++i)
    {
        auto start = set.Ranges[i].Start;
        auto stop = start + set.Ranges[i].Length;
        if (stop > first && start < end)
        {
            if (start < first) stop = first;
            else if (stop > end) start = end;
            else continue; // Entirely inside: dropped.
        }
        set.Ranges[kept++] = {start, stop - start};
    }
    set.Count = kept;
    set.Low = kept ? set.Ranges[0].Start : 0;
    set.High = kept ? set.Ranges[kept - 1].Start + set.Ranges[kept - 1].Length : 0;
    return true;
}
constexpr bool QcSafePagingRemoveContract()
{
    QC_SAFE_PAGING set{};
    if (!QcSafePagingAdd(set, 0, 16384) || !QcSafePagingAdd(set, 32768, 40960))
        return false;
    // Inside one range: it splits; the other range is unchanged.
    if (!QcSafePagingRemove(set, 4096, 8192) || set.Count != 3 || !QcSafePagingContains(set, 0, 4096) ||
        QcSafePagingOverlaps(set, 4096, 8192) || !QcSafePagingContains(set, 8192, 16384) ||
        !QcSafePagingContains(set, 32768, 40960))
        return false;
    // Across the end of one range and the start of the next: both shortened.
    if (!QcSafePagingRemove(set, 12288, 36864) || set.Count != 3 || !QcSafePagingContains(set, 8192, 12288) ||
        QcSafePagingOverlaps(set, 12288, 36864) || !QcSafePagingContains(set, 36864, 40960) || set.High != 40960)
        return false;
    // Outside the set: unchanged. Covering everything: empty.
    if (!QcSafePagingRemove(set, 50000, 60000) || set.Count != 3 ||
        !QcSafePagingRemove(set, 0, 65536) || set.Count || set.Low || set.High)
        return false;
    // A full set refuses a split and stays unchanged; shortening still works.
    set = {};
    set.Count = QcSpecialRangeMax;
    for (ULONG i = 0; i < QcSpecialRangeMax; ++i) set.Ranges[i] = {i * 16384LL, 8192};
    set.Low = 0;
    set.High = (QcSpecialRangeMax - 1) * 16384LL + 8192;
    if (QcSafePagingRemove(set, 1024, 2048) || set.Count != QcSpecialRangeMax || !QcSafePagingContains(set, 0, 8192))
        return false;
    return QcSafePagingRemove(set, 0, 4096) && set.Count == QcSpecialRangeMax && set.Ranges[0].Start == 4096 &&
           set.Low == 4096;
}
static_assert(QcSafePagingRemoveContract(), "Removing from the safe set must keep everything outside the removed range");
constexpr bool QcSafePagingContract()
{
    QC_SAFE_PAGING set{};
    if (QcSafePagingContains(set, 0, 4096) || !QcSafePagingAdd(set, 4096, 8192) ||
        !QcSafePagingAdd(set, 12288, 16384) || set.Count != 2 ||
        QcSafePagingContains(set, 4096, 16384) || !QcSafePagingOverlaps(set, 8191, 8193) ||
        QcSafePagingOverlaps(set, 8192, 12288) || !QcSafePagingAdd(set, 8192, 12288) ||
        set.Count != 1 || !QcSafePagingContains(set, 4096, 16384))
        return false;
    set = {};
    set.Count = QcSpecialRangeMax;
    for (ULONG i = 0; i < QcSpecialRangeMax; ++i) set.Ranges[i] = {i * 8192LL, 4096};
    set.Low = 0;
    set.High = (QcSpecialRangeMax - 1) * 8192LL + 4096;
    if (QcSafePagingAdd(set, QcSpecialRangeMax * 8192LL, QcSpecialRangeMax * 8192LL + 4096) ||
        set.Count != QcSpecialRangeMax || !QcSafePagingAdd(set, 4096, 8192) ||
        set.Count != QcSpecialRangeMax - 1)
        return false;
    return QcSafePagingContains(set, 0, 12288);
}
static_assert(QcSafePagingContract(), "Safe paging ranges must preserve gaps and fail closed at capacity");
