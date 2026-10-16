// SPDX-License-Identifier: MIT
#pragma once
#include <ntddk.h>

// Keep staging bounded at small budgets; retain full batches at normal budgets.
constexpr ULONG QcDrainCapacityForBudget(ULONGLONG budget, ULONG workers, ULONG maximum)
{
    const auto share = (budget / (4ULL * workers)) & ~4095ULL;
    const auto bounded = share < 65536 ? 65536 : share;
    return static_cast<ULONG>(bounded < maximum ? bounded : maximum);
}
constexpr ULONGLONG QcCacheSlabCount(ULONGLONG budget, ULONGLONG staging,
                                   ULONGLONG tables, ULONGLONG slabCost)
{
    return staging >= budget || tables >= budget - staging || !slabCost
               ? 0 : (budget - staging - tables) / slabCost;
}
static_assert(QcDrainCapacityForBudget(1ULL << 20, 8, 2 << 20) == 65536);
static_assert(QcDrainCapacityForBudget(16ULL << 20, 8, 2 << 20) == 512 * 1024);
static_assert(QcDrainCapacityForBudget(64ULL << 20, 8, 2 << 20) == 2 * 1024 * 1024);
static_assert(QcDrainCapacityForBudget(2ULL << 30, 8, 2 << 20) == 2 * 1024 * 1024);
static_assert(QcCacheSlabCount(16ULL << 20, 16ULL << 20, 5 * PAGE_SIZE, 262144) == 0,
              "Staging cannot underflow the remaining cache budget");
static_assert(QcCacheSlabCount(1024, 512, 512, 256) == 0);
static_assert(QcCacheSlabCount(1024, 256, 256, 256) == 2);
