// SPDX-License-Identifier: MIT
#pragma once
#include "cachepolicy.h"
// Compile-time regression tests exercise the exact scheduling code used by the
// driver, in every Debug/Release build. No duplicate managed scheduling model.
constexpr bool PolicyChecks() {
    auto o = QcDefaultOptions(); bool pressure = false;
    if (!QcValidOptions(o) || QcWriteLimit(o, 1000) != 1000) return false;
    if (!QcShouldDrain(o, 1, 100, 0, 0, false, pressure)) return false;
    o.Allocation = QcFixed; o.WritePercent = 40;
    if (QcWriteLimit(o, 1000) != 400) return false;
    o.WritePercent = 0; if (QcWriteLimit(o, 1000) != 0) return false;
    o.WritePercent = 100; if (QcWriteLimit(o, 1000) != 1000) return false;
    o.Drain = QcBalanced;
    if (QcShouldDrain(o, 10, 100, 0, 0, false, pressure)) return false;
    if (!QcShouldDrain(o, 80, 100, 0, 0, false, pressure) || !pressure) return false;
    if (!QcShouldDrain(o, 60, 100, 0, 0, false, pressure)) return false;
    if (QcShouldDrain(o, 40, 100, 0, 0, false, pressure) || pressure) return false;
    if (!QcShouldDrain(o, 10, 100, o.MaxAgeMs, 0, false, pressure)) return false;
    if (!QcShouldDrain(o, 10, 100, 0, 0, true, pressure)) return false;
    o.Drain = QcIdle;
    if (!QcShouldDrain(o, 10, 100, 0, o.IdleMs, false, pressure)) return false;
    if (QcShouldDrain(o, 0, 100, 0, 0, true, pressure)) return false;
    o.Parallelism = 5; if (QcValidOptions(o)) return false;
    o.Parallelism = 4; o.LowPercent = o.HighPercent; if (QcValidOptions(o)) return false;
    return true;
}
static_assert(PolicyChecks(), "Cache scheduling/allocation regression");
