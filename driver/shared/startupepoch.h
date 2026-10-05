// SPDX-License-Identifier: MIT
#pragma once
#include <ntddk.h>

// Caller serializes access. An unseeded epoch is unavailable, never a guessed
// startup identity. Hybrid transitions may arrive before UUID generation is ready.
struct QC_STARTUP_EPOCH
{
    GUID Base{}, Epoch{};
    ULONGLONG Transitions{};
    bool Initialized{};
    constexpr void Update()
    {
        Epoch = Base;
        for (ULONG i = 0; i < 8; ++i) Epoch.Data4[i] ^= static_cast<UCHAR>(Transitions >> (i * 8));
    }
    constexpr void Seed(GUID candidate)
    {
        if (!Initialized) { Base = candidate; Update(); Initialized = true; }
    }
    constexpr void AdvanceHybrid()
    {
        ++Transitions;
        if (Initialized) Update();
    }
};
