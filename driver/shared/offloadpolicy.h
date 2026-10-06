// SPDX-License-Identifier: MIT
#pragma once
#include <ntddk.h>

// Finish a request in its submitter's thread, or hand it to a worker thread on another
// processor? A submitter that keeps other requests in flight gains from returning at once:
// it issues the next while a worker copies. One that waits for each request (queue depth 1)
// gains from the inline copy. A run of requests that each found nothing in flight means
// queue depth 1, so copy inline and probe a worker now and then. Used by the RAM disk
// provider's SCSI path and by Direct access in the volume filter. Racy hints only: no lock.
struct QC_OFFLOAD_POLICY
{
    LONG InlineStreak;
    ULONG Probe;
};
constexpr LONG QcOffloadInlineAfter = 16;
constexpr ULONG QcOffloadProbeEvery = 64;
inline bool QcOffload(QC_OFFLOAD_POLICY* policy, LONG inFlight)
{
    if (inFlight > 0)
    {
        policy->InlineStreak = 0;
        return true;
    }
    if (policy->InlineStreak < QcOffloadInlineAfter)
    {
        ++policy->InlineStreak;
        return true;
    }
    return ++policy->Probe % QcOffloadProbeEvery == 0;
}
