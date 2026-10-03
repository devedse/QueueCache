// SPDX-License-Identifier: MIT
#pragma once
#include "startupepoch.h"

constexpr bool QcDeferredEpochContract()
{
    QC_STARTUP_EPOCH state{};
    // Two resumes before a successful UUID request must not publish a fake epoch.
    state.AdvanceHybrid(); state.AdvanceHybrid();
    if (state.Initialized || state.Epoch.Data1 || state.Epoch.Data4[0]) return false;
    const GUID base = {0x12345678, 0x1234, 0x5678, {0x80, 0, 0, 0, 0, 0, 0, 0}};
    state.Seed(base);
    if (!state.Initialized || state.Epoch.Data1 != base.Data1 || state.Epoch.Data4[0] != 0x82 || state.Transitions != 2) return false;
    // Concurrent/repeated successful queries cannot replace the chosen base.
    const GUID other = {0x87654321, 0, 0, {0}};
    state.Seed(other);
    if (state.Epoch.Data1 != base.Data1) return false;
    state.AdvanceHybrid();
    return state.Epoch.Data4[0] == 0x83 && state.Transitions == 3;
}
static_assert(QcDeferredEpochContract(), "Deferred UUID seed preserves prior hybrid transitions and one chosen epoch");
