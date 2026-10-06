// SPDX-License-Identifier: MIT
// Internal declarations shared by the RAM disk provider's source files.
#pragma once
#include <ntifs.h>
extern "C" {
#include <storport.h>
}
#include <ntddscsi.h>
#include "../shared/lockedpages.h"
#include "../shared/budgetprotocol.h"
#include "../shared/ramdiskprotocol.h"
#include "../shared/ramstore.h"
#include "../shared/ramview.h"

constexpr ULONG Tag = 'dRCQ';
inline constexpr GUID EmptyGuid{};
extern PDRIVER_OBJECT Driver;

// A request to the volume filter's budget device that must not fail for lack of memory
// (budget release, view unregistration): its IRP is allocated in advance.
struct KERNEL_CALL
{
    PIRP Irp;
    KEVENT Done;
    NTSTATUS Status;
};

struct DISK
{
    QC_RAM_STORE Store;         // Pages, geometry, flags, write gate and generation.
    GUID Resource, Epoch, FreezeOwner;
    ULONGLONG Creation, ReservedBytes;
    QC_KERNEL_RESERVATION Reservation;
    KERNEL_CALL Release;        // Returns Reservation to the shared budget.
    QC_KERNEL_RAM_VIEW View;    // Registration with the volume filter (Direct access).
    KERNEL_CALL Unregister;
    BOOLEAN ViewRegistered;
    PFILE_OBJECT BudgetFile;
    PDEVICE_OBJECT BudgetDevice;
    // SCSI requests, queued transfers and control calls using this disk. Removal takes the
    // disk out of the table, then runs this down: no user touches the disk after its release.
    EX_RUNDOWN_REF Users;
    volatile LONG Outstanding;  // Transfers queued to workers and not yet completed.
    LONG InlineStreak, Probe;   // Adaptive inline/worker choice (racy hints only).
    volatile LONG64 ReadBytes, WriteBytes, Flushes, Trims, Errors, Transfers; // SCSI path only.
};

// Transfer strategy, chosen by measurement on the lab VM (docs/RAM_FIRST_IMPLEMENTATION_TRACKER.md):
// complete small transfers inline during StartIo; hand large writes to per-processor workers
// while the submitter keeps others in flight; split large reads into chunks that idle workers
// help copy before StartIo completes them.
constexpr ULONG MaxWorkers = 16, WorkerSpinMicroseconds = 50, WorkerMinBytes = 128 * 1024, SplitChunk = 256 * 1024;
struct ADAPTER;
enum WORK_KIND : UCHAR { WorkRequest, WorkHelp };
struct WORK { LIST_ENTRY Link; WORK_KIND Kind; };
// A transfer admitted by StartIo and completed by a worker thread (lives in the SRB extension).
struct REQUEST { WORK Work; PSCSI_REQUEST_BLOCK Srb; DISK* Disk; PUCHAR Buffer; ULONGLONG Offset; ULONG Bytes; BOOLEAN Write; };
struct WORKER
{
    KSPIN_LOCK Lock;
    LIST_ENTRY Queue;
    KEVENT Work;
    BOOLEAN Sleeping;
    ULONG Processor;
    PKTHREAD Thread;
    ADAPTER* Adapter;
};
struct ADAPTER
{
    KSPIN_LOCK TableLock;
    FAST_MUTEX ControlLock;
    DISK* Disks[QcRamMaxDisks];
    GUID Epoch;
    ULONGLONG NextCreation;
    GUID LastAllocationFailureResource;
    ULONGLONG InjectedAllocationFailures, LastAllocationFailureSlabs;
    BOOLEAN Stopped;
    WORKER Workers[MaxWorkers];
    ULONG WorkerCount;
    volatile LONG NextWorker;
    volatile BOOLEAN Closing;
};

// Storport callbacks defined outside ramdisk.cpp.
HW_STARTIO StartIo;                          // scsi.cpp
HW_PROCESS_SERVICE_REQUEST ServiceRequest;   // control.cpp
HW_COMPLETE_SERVICE_IRP CompleteService;     // control.cpp

// disk.cpp: lifetime, accounting and registration.
DISK* ReferenceDisk(ADAPTER* adapter, ULONG slot, bool published);
void DereferenceDisk(DISK* disk);
void WaitReferences(DISK* disk); // After removing the disk from the table; PASSIVE_LEVEL.
void DrainWrites(DISK* disk);
NTSTATUS AllocateDisk(ADAPTER* adapter, const QC_RAM_REQUEST* request, PIRP irp, DISK** result, ULONG failAfterSlabs);
void FreeDisk(DISK* disk);
void RegisterView(ADAPTER* adapter, DISK* disk);
NTSTATUS StartupSession(GUID* epoch, ULONGLONG* transitions);

// transfer.cpp: copies and worker threads.
void StartWorkers(ADAPTER* adapter);
void StopWorkers(ADAPTER* adapter);
bool UseWorker(ADAPTER* adapter, DISK* disk, ULONG bytes, bool write);
void QueueTransfer(ADAPTER* adapter, REQUEST* request);
void CopySplit(ADAPTER* adapter, QC_RAM_STORE* store, ULONGLONG offset, PUCHAR buffer, ULONG bytes, bool write);
QC_RAM_LARGE_COPY LargeCopy;
