// SPDX-License-Identifier: MIT
#pragma once
#include "slotorder.h"

namespace QcSlotOrderChecks
{
constexpr unsigned long Max = 453; // 7 whole 64-slot chunks plus a partial one
constexpr unsigned long At(unsigned long k, unsigned long count, unsigned long order)
{
    return QcSlotOrder(k, count, 64, order, QcScatterStep(count / 64), QcScatterStep(count));
}
constexpr bool Bijection(unsigned long count, unsigned long order)
{
    bool seen[Max]{};
    for (unsigned long k = 0; k < count; ++k)
    {
        const auto i = At(k, count, order);
        if (i >= count || seen[i])
            return false;
        seen[i] = true;
    }
    return true;
}
constexpr bool AllBijections(unsigned long count)
{
    return Bijection(count, 0) && Bijection(count, 1) && Bijection(count, 2) && Bijection(count, 3);
}
static_assert(AllBijections(453) && AllBijections(448) && AllBijections(64) && AllBijections(63) && AllBijections(1),
              "every free-slot order must hand out each slot exactly once");
static_assert(At(1, 453, 1) == At(0, 453, 1) + 1 && At(64, 453, 1) / 64 != At(63, 453, 1) / 64 + 1,
              "order 1 keeps slots ascending inside a chunk and scatters the chunks");
static_assert(At(0, 453, 2) == 63 && At(1, 453, 2) == 62 && At(64, 453, 2) == 127, "order 2 reverses inside each chunk");
static_assert(At(0, 453, 3) / 64 != At(1, 453, 3) / 64 && At(1, 453, 3) / 64 != At(2, 453, 3) / 64,
              "order 3 puts consecutive positions in different chunks");
} // namespace QcSlotOrderChecks
