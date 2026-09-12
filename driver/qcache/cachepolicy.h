// SPDX-License-Identifier: MIT
#pragma once
// Wire policy and deterministic scheduling decisions. No allocation or I/O here.
enum : ULONG { QcAutomatic = 0, QcFixed = 1 };
// Drain triggers, and the settings each one reads in QcShouldDrain below:
//   QcEager    - drain while any dirty block exists; watermarks, MaxAgeMs and IdleMs unused.
//   QcBalanced - drain from HighPercent down to LowPercent, or when the oldest dirty block reaches MaxAgeMs.
//   QcIdle     - QcBalanced triggers, plus IdleMs without a newly cached write.
// BatchKiB and Parallelism shape each drain and apply to all three.
enum : ULONG { QcEager = 0, QcBalanced = 1, QcIdle = 2 };
enum : ULONG { QcRetainWrites = 1, QcPromoteReads = 2 };
struct QC_OPTIONS {
    ULONG Version, Size, Allocation, WritePercent;
    ULONG Retention, Drain, LowPercent, HighPercent;
    ULONG MaxAgeMs, IdleMs, BatchKiB, Parallelism;
};
static_assert(sizeof(QC_OPTIONS) == 48);
constexpr QC_OPTIONS QcDefaultOptions() { return { 1, sizeof(QC_OPTIONS), QcAutomatic, 50,
    QcRetainWrites | QcPromoteReads, QcEager, 40, 80, 5000, 250, 256, 1 }; }
constexpr bool QcValidOptions(const QC_OPTIONS& o) {
    return o.Version == 1 && o.Size == sizeof(o) && o.Allocation <= QcFixed && o.WritePercent <= 100 &&
        !(o.Retention & ~3UL) && o.Drain <= QcIdle && o.LowPercent < o.HighPercent && o.HighPercent <= 100 &&
        o.MaxAgeMs >= 10 && o.MaxAgeMs <= 300000 && o.IdleMs >= 10 && o.IdleMs <= 60000 &&
        o.BatchKiB >= 4 && o.BatchKiB <= 1024 && o.BatchKiB % 4 == 0 && o.Parallelism >= 1 && o.Parallelism <= 4;
}
constexpr ULONG QcWriteLimit(const QC_OPTIONS& o, ULONG capacity) {
    return o.Allocation == QcAutomatic ? capacity : static_cast<ULONG>(static_cast<ULONGLONG>(capacity) * o.WritePercent / 100);
}
// Protect resident read demand up to half the payload, borrowing unused space.
// A large atomic request may reduce protection so it can eventually be admitted.
constexpr ULONG QcProtectedReadSlots(ULONG capacity, ULONG residentReads, ULONG requestSlots) {
    if (requestSlots >= capacity) return 0;
    const auto ceiling = capacity / 2 < capacity - requestSlots ? capacity / 2 : capacity - requestSlots;
    return residentReads < ceiling ? residentReads : ceiling;
}
static_assert(QcProtectedReadSlots(100, 30, 1) == 30);
static_assert(QcProtectedReadSlots(100, 80, 1) == 50);
static_assert(QcProtectedReadSlots(100, 80, 75) == 25);
static_assert(QcProtectedReadSlots(100, 80, 100) == 0);
// Background policies never override barriers or writers waiting for capacity.
constexpr bool QcShouldDrain(const QC_OPTIONS& o, ULONGLONG dirty, ULONGLONG limit,
    ULONGLONG ageMs, ULONGLONG idleMs, bool forced, bool& pressure) {
    if (!dirty) { pressure = false; return false; }
    if (dirty * 100 >= limit * o.HighPercent) pressure = true;
    if (dirty * 100 <= limit * o.LowPercent) pressure = false;
    return forced || pressure || o.Drain == QcEager || ageMs >= o.MaxAgeMs ||
        (o.Drain == QcIdle && idleMs >= o.IdleMs);
}
