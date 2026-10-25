// SPDX-License-Identifier: MIT
#pragma once
// Wire policy and deterministic scheduling decisions. No allocation or I/O here.
enum : ULONG
{
    QcAutomatic = 0,
    QcFixed = 1
};
// Drain triggers, and the settings each one reads in QcShouldDrain below:
//   QcEager    - drain while any dirty block exists; watermarks, MaxAgeMs and IdleMs unused.
//   QcBalanced - drain from HighPercent down to LowPercent, or when the oldest dirty block reaches MaxAgeMs.
//   QcIdle     - QcBalanced triggers, plus IdleMs without a newly cached write.
//   QcDeferred - age only; ignores watermarks/idle, but not explicit barriers/capacity.
// BatchKiB and Parallelism shape each drain and apply to all three.
enum : ULONG
{
    QcEager = 0,
    QcBalanced = 1,
    QcIdle = 2,
    QcDeferred = 3 // age only; no watermark/idle early drain
};
enum : ULONG
{
    QcRetainWrites = 1,
    QcPromoteReads = 2
};
struct QC_OPTIONS
{
    ULONG Version, Size, Allocation, WritePercent;
    ULONG Retention, Drain, LowPercent, HighPercent;
    ULONG MaxAgeMs, IdleMs, BatchKiB, Parallelism;
};
static_assert(sizeof(QC_OPTIONS) == 48);
constexpr QC_OPTIONS QcDefaultOptions()
{
    return {
        // Eager draining with 2048 KiB batches and 8 lower writes in flight since plan 164 (issue #15):
        // the read-aware limit (QcDefaultReadAware) keeps foreground disk reads responsive. Idle,
        // 1024 KiB and 4 from plan 125; 256 KiB and 2 before.
        1, sizeof(QC_OPTIONS), QcAutomatic, 50, QcRetainWrites | QcPromoteReads, QcEager, 40, 80, 5000, 250, 2048, 8};
}
// QcCallerPath default: serve RAM hits and fitting writes on the calling thread
// when the device has no other request queued or active.
constexpr LONG QcDefaultCallerPath = 1;
// Every Nth caller-path candidate is queued instead. With several requests
// outstanding (queue depth > 1) later arrivals then find it queued and follow it,
// so the worker again overlaps its work with the caller's submissions; with one
// outstanding request it completes and the caller path resumes (about 1% cost).
constexpr ULONG QcCallerPathProbeInterval = 1024;
// A candidate that finds another request queued or in progress means several are
// outstanding. The worker then overlaps with the submitting threads, which is
// faster than doing the work on them, and it can briefly empty its queue while
// they reap completions. So keep using the worker for this many further
// candidates, restarting the count each time one finds it busy again.
constexpr ULONG QcCallerPathWorkerWindow = 256;
// An idle request worker polls this long for the next request before sleeping.
// A wake-up costs about 11 us on the test VM, several times a 4 KiB request's
// work, so a worker slightly faster than its submitters otherwise slept and woke
// once per request. Only spent right after processing a request.
constexpr ULONG QcWorkerSpinMicroseconds = 30;
constexpr bool QcValidOptions(const QC_OPTIONS& o)
{
    return o.Version == 1 && o.Size == sizeof(o) && o.Allocation <= QcFixed && o.WritePercent <= 100 &&
           !(o.Retention & ~3UL) && o.Drain <= QcDeferred && o.LowPercent < o.HighPercent && o.HighPercent <= 100 &&
           o.MaxAgeMs >= 10 && o.MaxAgeMs <= 3600000 && o.IdleMs >= 10 && o.IdleMs <= 60000 && o.BatchKiB >= 4 &&
           o.BatchKiB <= 2048 && o.BatchKiB % 4 == 0 && o.Parallelism >= 1 && o.Parallelism <= 8;
}
constexpr ULONG QcWriteLimit(const QC_OPTIONS& o, ULONG capacity)
{
    return o.Allocation == QcAutomatic ? capacity
                                       : static_cast<ULONG>(static_cast<ULONGLONG>(capacity) * o.WritePercent / 100);
}
constexpr ULONG QcAdmissionWriteLimit(ULONG writeLimit, bool, bool)
{
    // Paging-marked writes use an ordered lower path. Ordinary fitting writes
    // retain the full quota; no unused paging reserve is held in nonpaged RAM.
    return writeLimit;
}
// Strict (#16): a flush writes out every dirty block before it completes, and the drain needs at least
// one disk write for each separate piece of unwritten data. So Strict holds at most the pieces its
// drain completes in QcStrictFlushTargetMs at the measured rate (0: not measured yet, then
// QcStrictInitialPieces), never fewer than QcStrictMinPieces. A write that adds dirty data beyond it
// waits for the drain: scattered small writes slow down to the disk's speed instead of a later flush
// stalling for minutes. A long contiguous write is one piece, so it is not slowed (the byte cap tried
// first throttled those, see docs/PERFORMANCE.md "Strict (#16)").
constexpr ULONGLONG QcStrictFlushTargetMs = 2000;
constexpr ULONG QcStrictMinPieces = 256, QcStrictInitialPieces = 1024;
constexpr ULONG QcStrictPieceLimit(ULONGLONG piecesPerSecond)
{
    const auto pieces = piecesPerSecond ? piecesPerSecond * QcStrictFlushTargetMs / 1000 : QcStrictInitialPieces;
    return pieces < QcStrictMinPieces ? QcStrictMinPieces : pieces > 0xFFFFFFFFULL ? 0xFFFFFFFFUL : static_cast<ULONG>(pieces);
}
// IRP_PAGING_IO also marks filesystem cache and mapped-file traffic, not only
// pagefile contents. T085: cache a paging-marked request only when a current
// special-file map proves it is outside pagefile/swapfile/hiberfil/dump ranges.
// Otherwise it takes the ordered direct path, where the caller
// must reconcile every overlapping cached/in-flight version before forwarding.
constexpr bool QcShouldCacheDataIo(bool pagingIo, bool applicationPaging)
{
    return !pagingIo || applicationPaging;
}
// #13: keep later versions in RAM until the earlier group's lower flush completed.
// The lab mode remains off until its real-driver qualification is complete.
static constexpr LONG QcDefaultFlushOrder = 0;
// Experimental ordering excludes paging/hibernation/dump paths for its entire
// lifetime, including registrations arriving after mode selection. Both checks
// run under RoutingLock so a reservation and mode selection cannot pass each other.
constexpr bool QcMaySelectFlushOrder(ULONGLONG mode, LONG usagePaths)
{
    return mode == 0 || ((mode == 1 || mode == 2) && usagePaths == 0);
}
// Mode 1 is the original conservative prototype. Mode 2 preserves the
// post-write-through boundary without ordering unrelated predecessors before it.
constexpr bool QcSealBeforeWriteThrough(LONG mode)
{
    return mode == 1;
}
constexpr bool QcMayRegisterUsage(LONG flushOrderMode)
{
    return flushOrderMode == 0;
}
constexpr bool QcEpochMayDrain(bool ordered, ULONGLONG version, ULONGLONG frontier)
{
    return !ordered || version == frontier;
}
constexpr bool QcEpochNeedsVersion(bool ordered, bool dirty, ULONGLONG version, ULONGLONG writing)
{
    return ordered && dirty && version != writing;
}
constexpr bool QcResumeAfterPower(bool wasEnabled, bool hasCapacity, bool healthy, bool gone)
{
    return wasEnabled && hasCapacity && healthy && !gone;
}
// Lower drivers may page-fault while handling PnP, shutdown or disk-control requests
// (ACPI's usage-notification handler faulted on the C: pagefile at shutdown), so those
// are called off the worker thread while it services paging reads. Not power: a paging
// disk holds I/O until D0. Data requests stay inline; their ordering is range-based.
constexpr bool QcForwardOffWorker(UCHAR major)
{
    return major == IRP_MJ_PNP || major == IRP_MJ_SHUTDOWN || major == IRP_MJ_DEVICE_CONTROL ||
           major == IRP_MJ_INTERNAL_DEVICE_CONTROL;
}
constexpr bool QcServiceReadsDuringLowerWait(UCHAR major, bool rangeDrain)
{
    return major == IRP_MJ_READ || QcForwardOffWorker(major) || rangeDrain;
}
// Protect resident read demand up to half the payload, borrowing unused space.
// A large atomic request may reduce protection so it can eventually be admitted.
constexpr ULONG QcProtectedReadSlots(ULONG capacity, ULONG residentReads, ULONG requestSlots)
{
    if (requestSlots >= capacity)
        return 0;
    const auto ceiling = capacity / 2 < capacity - requestSlots ? capacity / 2 : capacity - requestSlots;
    return residentReads < ceiling ? residentReads : ceiling;
}
static_assert(QcProtectedReadSlots(100, 30, 1) == 30);
static_assert(QcProtectedReadSlots(100, 80, 1) == 50);
static_assert(QcProtectedReadSlots(100, 80, 75) == 25);
static_assert(QcProtectedReadSlots(100, 80, 100) == 0);
// Background policies never override barriers or writers waiting for capacity.
constexpr bool QcShouldDrain(const QC_OPTIONS& o,
                             ULONGLONG dirty,
                             ULONGLONG limit,
                             ULONGLONG ageMs,
                             ULONGLONG idleMs,
                             bool forced,
                             bool& pressure)
{
    if (!dirty)
    {
        pressure = false;
        return false;
    }
    if (o.Drain == QcDeferred)
    {
        pressure = false;
        return forced || ageMs >= o.MaxAgeMs;
    }
    if (dirty * 100 >= limit * o.HighPercent)
        pressure = true;
    if (dirty * 100 <= limit * o.LowPercent)
        pressure = false;
    return forced || pressure || o.Drain == QcEager || ageMs >= o.MaxAgeMs || (o.Drain == QcIdle && idleMs >= o.IdleMs);
}
constexpr bool QcShouldWakeAfterWrite(const QC_OPTIONS& options,
                                      ULONGLONG dirty,
                                      ULONGLONG limit,
                                      ULONGLONG ageMs,
                                      ULONGLONG inFlightBytes,
                                      bool forced,
                                      bool& pressure)
{
    return QcShouldDrain(options, dirty, limit, ageMs, 0, forced, pressure) &&
           (options.Parallelism > 1 || inFlightBytes == 0);
}
