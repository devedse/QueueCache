// SPDX-License-Identifier: MIT
#pragma once
#include <ntifs.h>
#include "cachepolicy.h"

#define IOCTL_QCACHE_STATE_V1 CTL_CODE(0x8844UL, 0xD10UL, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_QCACHE_CONTROL_V1 CTL_CODE(0x8844UL, 0xD11UL, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS)
#define IOCTL_QCACHE_DIAGNOSTICS_V1 CTL_CODE(0x8844UL, 0xD12UL, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_QCACHE_STATE_V2 CTL_CODE(0x8844UL, 0xD13UL, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_QCACHE_STATE_V3 CTL_CODE(0x8844UL, 0xD14UL, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_QCACHE_OPTIONS_V1 CTL_CODE(0x8844UL, 0xD15UL, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS)
struct QC_DIAGNOSTICS {
    ULONG Version, Size;
    ULONGLONG ApplicationFlushes, DeferredFlushes, WriteThroughWrites, DeferredWriteThroughWrites;
    ULONGLONG ControlBarriers, OtherBarriers, ShutdownBarriers, PowerBarriers, LastBarrierCode;
};
static_assert(sizeof(QC_DIAGNOSTICS) == 80);
struct QC_STATE {
    ULONG Version, Size, Flags;
    NTSTATUS LastError;
    ULONGLONG DeviceBytes, BudgetBytes, ReservedBytes, DirtyBytes, InFlightBytes, PayloadCapacity;
    ULONGLONG OccupiedSlots, AcceptedBytes, DrainedBytes, ThrottleWaits, CacheReadBytes, Errors, Flushes, PeakDirtyBytes;
};
struct QC_COMMAND { ULONG Version, Size, Action, Reserved; ULONGLONG BudgetBytes, Value; };
static_assert(sizeof(QC_STATE) == 128);
struct QC_STATE_V2 { QC_STATE Base; ULONGLONG DiscardedBytes, LowerWrites, BatchedWrites, TrimRequests; };
static_assert(sizeof(QC_STATE_V2) == 160);
struct QC_STATE_V3 {
    QC_STATE_V2 Base;
    QC_OPTIONS Options;
    ULONGLONG CleanReadBytes, CleanWriteBytes, ReadHitBytes, ReadMissBytes, Evictions;
    ULONGLONG OldestDirtyMs, Generation, Instance, GlobalLimitBytes, GlobalReservedBytes;
};
static_assert(sizeof(QC_STATE_V3) == 288);
static_assert(sizeof(QC_COMMAND) == 32);
enum : ULONG { QcConfigure = 1, QcEnable, QcFlush, QcDisable, QcRetry, QcLabDelay, QcLabFault, QcFlushPolicy, QcRelease,
    QcDropClean }; // QcDropClean releases clean cached blocks only; pending writes keep draining.
struct QC_SLOT {
    PUCHAR Buffer;
    LARGE_INTEGER Offset;
    ULONG Length, HashNext, HashPrevious, QueueNext, QueuePrevious, FreeNext;
    BOOLEAN InFlight;
    BOOLEAN Dirty, ReadClass;
    ULONGLONG DirtySince;
};
struct QC_CACHE;
struct QC_DRAIN_WORKER { QC_CACHE* Cache; ULONG Number; HANDLE Thread; };
struct QC_CACHE {
    PDEVICE_OBJECT Lower;
    KMUTEX Mutex;
    KSPIN_LOCK SnapshotLock;
    QC_STATE State, Snapshot;
    QC_STATE_V2 ExtendedSnapshot;
    QC_STATE_V3 ReadWriteSnapshot;
    QC_OPTIONS Options;
    ULONG CleanHead[2], CleanTail[2], CleanCount[2]; // write/read LRU lists
    ULONGLONG ReadHitBytes, ReadMissBytes, Evictions, Generation, Instance, LastWriteTime;
    BOOLEAN Pressure, WriterWaiting;
    ULONGLONG DiscardedBytes, LowerWrites, BatchedWrites, TrimRequests;
    QC_DIAGNOSTICS Diagnostics, DiagnosticsSnapshot;
    KEVENT Wake, Changed;
    QC_DRAIN_WORKER Workers[4];
    QC_SLOT* Slots;
    ULONG* Buckets;
    PUCHAR DrainBuffer;
    ULONG Capacity, Head, Tail, FreeHead, Count, SectorBytes, DrainCapacity;
    BOOLEAN Enabled, Barrier, Suspended, Stop;
    BOOLEAN UnsafeDefer;
    BOOLEAN TrimPaused;
    BOOLEAN BlockedPlacement; // partmgr below us would reject generated background writes.
    volatile LONG Gone;
    ULONG DelayMs, InjectFault;
};
NTSTATUS QcCacheInitialize(QC_CACHE* cache, PDEVICE_OBJECT lower);
void QcCacheDestroy(QC_CACHE* cache);
void QcCacheSnapshot(QC_CACHE* cache, QC_STATE* output);
void QcCacheSnapshotV2(QC_CACHE* cache, QC_STATE_V2* output);
void QcCacheSnapshotV3(QC_CACHE* cache, QC_STATE_V3* output);
void QcCacheDiagnostics(QC_CACHE* cache, QC_DIAGNOSTICS* output);
NTSTATUS QcCacheProcess(QC_CACHE* cache, PIRP irp, LONGLONG deviceBytes);
NTSTATUS QcCacheBarrier(QC_CACHE* cache, BOOLEAN disable);
