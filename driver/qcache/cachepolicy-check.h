// SPDX-License-Identifier: MIT
#pragma once
#include "cachepolicy.h"
// Compile-time regression tests exercise the exact scheduling code used by the
// driver, in every Debug/Release build. No duplicate managed scheduling model.
constexpr bool PolicyChecks()
{
    auto o = QcDefaultOptions();
    bool pressure = false;
    if (!QcValidOptions(o) || QcWriteLimit(o, 1000) != 1000)
        return false;
    if (o.Drain != QcIdle || o.MaxAgeMs != 5000 || o.IdleMs != 250 || o.LowPercent != 40 ||
        o.HighPercent != 80 || o.BatchKiB != 256 || o.Parallelism != 1 ||
        QcShouldDrain(o, 1, 100, 0, 0, false, pressure) || pressure ||
        !QcShouldDrain(o, 1, 100, 0, o.IdleMs, false, pressure))
        return false;
    o.Allocation = QcFixed;
    o.WritePercent = 40;
    if (QcWriteLimit(o, 1000) != 400)
        return false;
    o.WritePercent = 0;
    if (QcWriteLimit(o, 1000) != 0)
        return false;
    o.WritePercent = 100;
    if (QcWriteLimit(o, 1000) != 1000)
        return false;
    if (QcAdmissionWriteLimit(1000, false, false) != 1000 ||
        QcAdmissionWriteLimit(1000, true, false) != 1000 ||
        QcAdmissionWriteLimit(1000, true, true) != 1000 ||
        !QcShouldCacheDataIo(false, false) || QcShouldCacheDataIo(true, false) || !QcShouldCacheDataIo(true, true) ||
        !QcResumeAfterPower(true, true, true, false) ||
        QcResumeAfterPower(false, true, true, false) ||
        QcResumeAfterPower(true, false, true, false) ||
        QcResumeAfterPower(true, true, false, false) ||
        QcResumeAfterPower(true, true, true, true))
        return false;
    o.Drain = QcBalanced;
    if (QcShouldDrain(o, 10, 100, 0, 0, false, pressure))
        return false;
    if (!QcShouldDrain(o, 80, 100, 0, 0, false, pressure) || !pressure)
        return false;
    if (!QcShouldDrain(o, 60, 100, 0, 0, false, pressure))
        return false;
    if (QcShouldDrain(o, 40, 100, 0, 0, false, pressure) || pressure)
        return false;
    if (!QcShouldDrain(o, 10, 100, o.MaxAgeMs, 0, false, pressure))
        return false;
    if (!QcShouldDrain(o, 10, 100, 0, 0, true, pressure))
        return false;
    o.Drain = QcIdle;
    pressure = false;
    if (QcShouldDrain(o, 10, 100, 0, 0, false, pressure) || pressure)
        return false;
    if (!QcShouldDrain(o, 80, 100, 0, 0, false, pressure) || !pressure ||
        !QcShouldDrain(o, 60, 100, 0, 0, false, pressure))
        return false;
    if (QcShouldDrain(o, 40, 100, 0, 0, false, pressure) || pressure)
        return false;
    if (!QcShouldDrain(o, 10, 100, o.MaxAgeMs, 0, false, pressure) ||
        !QcShouldDrain(o, 10, 100, 0, 0, true, pressure))
        return false;
    if (!QcShouldDrain(o, 10, 100, 0, o.IdleMs, false, pressure))
        return false;
    if (QcShouldDrain(o, 0, 100, 0, 0, true, pressure))
        return false;
    o.Drain = QcDeferred;
    o.MaxAgeMs = 3600000;
    pressure = true; // switching policy cannot inherit pressure early draining
    if (!QcValidOptions(o) || QcShouldDrain(o, 99, 100, 3599999, 3600000, false, pressure) || pressure)
        return false;
    if (!QcShouldDrain(o, 1, 100, 3600000, 0, false, pressure) ||
        !QcShouldDrain(o, 1, 100, 0, 0, true, pressure) ||
        QcShouldDrain(o, 0, 100, 3600000, 0, true, pressure))
        return false;
    o.MaxAgeMs = 3600001;
    if (QcValidOptions(o))
        return false;
    o.MaxAgeMs = 3600000;
    o.Parallelism = 5;
    if (QcValidOptions(o))
        return false;
    o.Parallelism = 4;
    o.LowPercent = o.HighPercent;
    if (QcValidOptions(o))
        return false;
    return true;
}
static_assert(PolicyChecks(), "Cache scheduling/allocation regression");

constexpr bool WriteWakeChecks()
{
    auto options = QcDefaultOptions();
    for (ULONG policy = QcEager; policy <= QcDeferred; ++policy)
    {
        options.Drain = policy;
        for (ULONG parallelism = 1; parallelism <= 4; ++parallelism)
        {
            options.Parallelism = parallelism;
            bool pressure = false;
            if (QcShouldWakeAfterWrite(options, 0, 100, options.MaxAgeMs, 0, true, pressure) ||
                !QcShouldWakeAfterWrite(options, 10, 100, options.MaxAgeMs, 0, false, pressure) ||
                !QcShouldWakeAfterWrite(options, 10, 100, 0, 0, true, pressure))
                return false;
            if (QcShouldWakeAfterWrite(options, 10, 100, options.MaxAgeMs, 512, false, pressure) != (parallelism > 1) ||
                QcShouldWakeAfterWrite(options, 10, 100, 0, 512, true, pressure) != (parallelism > 1))
                return false;
            if (QcShouldWakeAfterWrite(options, 10, 100, 0, 0, false, pressure) != (policy == QcEager))
                return false;
        }
    }
    options.Drain = QcIdle;
    options.Parallelism = 1;
    bool pressure = false;
    if (QcShouldWakeAfterWrite(options, 80, 100, 0, 512, false, pressure) || !pressure ||
        !QcShouldWakeAfterWrite(options, 60, 100, 0, 0, false, pressure) || !pressure ||
        QcShouldWakeAfterWrite(options, 40, 100, 0, 0, false, pressure) || pressure)
        return false;
    return true;
}
static_assert(WriteWakeChecks(), "Foreground drain wake regression");

static_assert(QcServiceReadsDuringLowerWait(IRP_MJ_READ, false) && QcServiceReadsDuringLowerWait(IRP_MJ_PNP, false) &&
                  QcServiceReadsDuringLowerWait(IRP_MJ_SHUTDOWN, false) && QcServiceReadsDuringLowerWait(IRP_MJ_WRITE, true) &&
                  !QcServiceReadsDuringLowerWait(IRP_MJ_WRITE, false) && !QcServiceReadsDuringLowerWait(IRP_MJ_POWER, false) &&
                  !QcServiceReadsDuringLowerWait(IRP_MJ_FLUSH_BUFFERS, false) &&
                  !QcServiceReadsDuringLowerWait(IRP_MJ_DEVICE_CONTROL, false),
              "Paging reads must progress while the worker forwards a range-less PnP/shutdown request");
static_assert(QcForwardOffWorker(IRP_MJ_PNP) && QcForwardOffWorker(IRP_MJ_SHUTDOWN) &&
                  !QcForwardOffWorker(IRP_MJ_POWER) && !QcForwardOffWorker(IRP_MJ_READ) &&
                  !QcForwardOffWorker(IRP_MJ_WRITE) && !QcForwardOffWorker(IRP_MJ_FLUSH_BUFFERS),
              "Only range-less PnP/shutdown requests are called from the lower-call work item");
