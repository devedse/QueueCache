// SPDX-License-Identifier: MIT
#pragma once
#include "memorybudget.h"

// Exercise the exact admission predicate compiled into the authority's CAS loop.
static_assert(QcMemoryReservationFits(0, 1024, 1024), "Exact-fit reservation");
static_assert(QcMemoryReservationFits(768, 1024, 256), "Cache and RAM disk share remaining budget");
static_assert(!QcMemoryReservationFits(768, 1024, 257), "Cannot overcommit the shared budget");
static_assert(!QcMemoryReservationFits(1025, 1024, 1), "Existing overcommit rejects new reservations");
static_assert(!QcMemoryReservationFits(0, 0, 1), "No physical memory means no reservation");
static_assert(!QcMemoryReservationFits(0, 1024, 0), "Zero-byte reservation is invalid");
static_assert(QcMemoryReservationFits(MAXLONGLONG - 1, MAXLONGLONG, 1), "Signed atomic boundary");
static_assert(!QcMemoryReservationFits(MAXLONGLONG - 1, MAXLONGLONG, 2), "Signed atomic sum cannot overflow");
static_assert(!QcMemoryReservationFits(0, MAXULONGLONG, MAXULONGLONG), "Reject unsigned-to-signed overflow");
static_assert(sizeof(QC_MEMORY_BUDGET) == 16 && alignof(QC_MEMORY_BUDGET) >= 8, "x64 atomic alignment");
