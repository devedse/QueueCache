// SPDX-License-Identifier: MIT
#pragma once
#include "cachepolicy.h"
// The real policy predicates, under the same serialized selection/reservation
// order as dispatch. Successful registration preserves the active cache.
constexpr bool QcUsagePathContract()
{
    for (LONG mode = -1; mode <= 3; ++mode)
    {
        for (LONG paths = -1; paths <= 3; ++paths)
        {
            const bool expected = paths >= 0 && (mode == 0 || mode == 1 || (mode == 2 && paths == 0));
            if (QcMaySelectFlushOrder(static_cast<ULONGLONG>(mode), paths) != expected)
                return false;
        }
        if (QcMayRegisterUsage(mode) != (mode == 0 || mode == 1)) return false;
    }
    // Both reservation-first and selection-first support an ordinary ordered
    // cache. A failed notification drops its reservation, not its cache setting.
    LONG paths = 0;
    if (!QcMayRegisterUsage(1)) return false;
    ++paths;
    if (!QcMaySelectFlushOrder(1, paths)) return false;
    --paths;
    if (!QcMaySelectFlushOrder(1, paths)) return false;
    ++paths;
    return QcMaySelectFlushOrder(1, paths);
}
static_assert(QcUsagePathContract(), "Usage-path selection/reservation contract changed");
