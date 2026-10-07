// SPDX-License-Identifier: MIT
// Copies and per-processor worker threads (see the strategy note in provider.h).
#include "provider.h"

struct SPLIT;
struct HELP { WORK Work; SPLIT* Split; BOOLEAN Taken; };
// One large transfer copied in chunks by its submitter and any workers that join; lives on
// the submitter's stack.
struct SPLIT
{
    QC_RAM_STORE* Store; PUCHAR Buffer; ULONGLONG Offset; ULONG Bytes, Chunk, Chunks; BOOLEAN Write;
    volatile LONG Next, Helpers;
    HELP Help[MaxWorkers];
};

// Small transfers are cheapest inline (completed during StartIo). A large write goes to a
// worker when the submitter keeps others in flight; a run of writes that each found nothing
// outstanding means queue depth 1, so copy inline and probe a worker periodically.
static constexpr LONG InlineAfter = 16, ProbeEvery = 64;
bool UseWorker(ADAPTER* adapter, DISK* disk, ULONG bytes, bool write)
{
    // Reads copy fastest split (CopySplit); queued writes gain more from returning at once.
    if (!adapter->WorkerCount || !write || bytes < WorkerMinBytes) return false;
    if (ReadNoFence(&disk->Outstanding) > 0) { disk->InlineStreak = 0; return true; }
    if (disk->InlineStreak < InlineAfter) { ++disk->InlineStreak; return true; }
    return ++disk->Probe % ProbeEvery == 0;
}
static void Post(WORKER* worker, WORK* work)
{
    KIRQL irql; KeAcquireSpinLock(&worker->Lock, &irql);
    InsertTailList(&worker->Queue, &work->Link);
    const bool wake = worker->Sleeping; worker->Sleeping = FALSE;
    KeReleaseSpinLock(&worker->Lock, irql);
    if (wake) KeSetEvent(&worker->Work, IO_NO_INCREMENT, FALSE);
}
void QueueTransfer(ADAPTER* adapter, REQUEST* request)
{
    const auto count = adapter->WorkerCount;
    auto index = static_cast<ULONG>(InterlockedIncrement(&adapter->NextWorker)) % count;
    if (count > 1 && adapter->Workers[index].Processor == KeGetCurrentProcessorNumberEx(nullptr)) index = (index + 1) % count;
    InterlockedIncrement(&request->Disk->Outstanding);
    request->Work.Kind = WorkRequest;
    Post(&adapter->Workers[index], &request->Work);
}
static void Finish(ADAPTER* adapter, REQUEST* request)
{
    auto disk = request->Disk;
    QcRamStoreCopy(&disk->Store, request->Offset, request->Buffer, request->Bytes, request->Write);
    if (request->Write) QcRamStoreEndWrite(&disk->Store);
    QcRamTimingEnd(&disk->Store, request->Write, request->Started);
    InterlockedDecrement(&disk->Outstanding);
    DereferenceDisk(disk);
    KIRQL irql; KeRaiseIrql(DISPATCH_LEVEL, &irql);
    StorPortNotification(RequestComplete, adapter, request->Srb);
    KeLowerIrql(irql);
}
static void CopyChunks(SPLIT* split)
{
    for (;;)
    {
        const auto index = static_cast<ULONG>(InterlockedIncrement(&split->Next) - 1);
        if (index >= split->Chunks) break;
        const auto start = index * split->Chunk, bytes = min(split->Chunk, split->Bytes - start);
        QcRamStoreCopy(split->Store, split->Offset + start, split->Buffer + start, bytes, split->Write);
    }
}
static void WorkerMain(PVOID context)
{
    auto worker = static_cast<WORKER*>(context); auto adapter = worker->Adapter;
    KeSetSystemAffinityThreadEx(static_cast<KAFFINITY>(1) << worker->Processor);
    bool spin = false;
    for (;;)
    {
        WORK* work = nullptr;
        // Stay at DISPATCH_LEVEL from taking a help request until its chunks are copied:
        // the submitter spins for this worker, so it must not be preempted in between.
        KIRQL irql; KeRaiseIrql(DISPATCH_LEVEL, &irql);
        KeAcquireSpinLockAtDpcLevel(&worker->Lock);
        if (!IsListEmpty(&worker->Queue))
        {
            work = CONTAINING_RECORD(RemoveHeadList(&worker->Queue), WORK, Link);
            // Taken and Helpers change under the queue lock that CopySplit's withdrawal holds.
            if (work->Kind == WorkHelp) { auto help = CONTAINING_RECORD(work, HELP, Work); help->Taken = TRUE; InterlockedIncrement(&help->Split->Helpers); }
        }
        KeReleaseSpinLockFromDpcLevel(&worker->Lock);
        const bool found = work != nullptr;
        if (found && work->Kind == WorkHelp)
        {
            // The help request lives on the submitter's stack; it may be gone after the decrement.
            auto split = CONTAINING_RECORD(work, HELP, Work)->Split;
            CopyChunks(split); InterlockedDecrement(&split->Helpers); work = nullptr;
        }
        KeLowerIrql(irql);
        if (found)
        {
            if (work) Finish(adapter, CONTAINING_RECORD(work, REQUEST, Work));
            spin = true; continue;
        }
        if (adapter->Closing) break;
        if (spin)
        {
            // Just finished a request: poll briefly before paying for a sleep and wake-up.
            spin = false;
            LARGE_INTEGER frequency; const auto start = KeQueryPerformanceCounter(&frequency).QuadPart;
            const auto limit = frequency.QuadPart * WorkerSpinMicroseconds / 1000000;
            bool arrived = false;
            while (!(arrived = ReadPointerNoFence(reinterpret_cast<PVOID volatile*>(&worker->Queue.Flink)) != &worker->Queue) &&
                   !adapter->Closing && KeQueryPerformanceCounter(nullptr).QuadPart - start < limit)
                for (ULONG i = 0; i < 64; ++i) YieldProcessor();
            if (arrived) continue;
        }
        bool sleep = false;
        KeAcquireSpinLock(&worker->Lock, &irql);
        if (IsListEmpty(&worker->Queue) && !adapter->Closing) { worker->Sleeping = TRUE; KeClearEvent(&worker->Work); sleep = true; }
        KeReleaseSpinLock(&worker->Lock, irql);
        if (sleep) KeWaitForSingleObject(&worker->Work, Executive, KernelMode, FALSE, nullptr);
    }
    PsTerminateSystemThread(STATUS_SUCCESS);
}
void StopWorkers(ADAPTER* adapter)
{
    adapter->Closing = TRUE;
    for (ULONG i = 0; i < adapter->WorkerCount; ++i) KeSetEvent(&adapter->Workers[i].Work, IO_NO_INCREMENT, FALSE);
    for (ULONG i = 0; i < adapter->WorkerCount; ++i)
    {
        KeWaitForSingleObject(adapter->Workers[i].Thread, Executive, KernelMode, FALSE, nullptr);
        ObDereferenceObject(adapter->Workers[i].Thread);
    }
    adapter->WorkerCount = 0;
}
void StartWorkers(ADAPTER* adapter)
{
    const auto processors = KeQueryActiveProcessorCountEx(0);
    const auto wanted = min(processors, MaxWorkers);
    for (ULONG i = 0; i < wanted; ++i)
    {
        auto worker = &adapter->Workers[i];
        KeInitializeSpinLock(&worker->Lock); InitializeListHead(&worker->Queue);
        KeInitializeEvent(&worker->Work, SynchronizationEvent, FALSE);
        worker->Processor = i; worker->Adapter = adapter;
        OBJECT_ATTRIBUTES attributes; InitializeObjectAttributes(&attributes, nullptr, OBJ_KERNEL_HANDLE, nullptr, nullptr);
        HANDLE handle;
        if (!NT_SUCCESS(PsCreateSystemThread(&handle, THREAD_ALL_ACCESS, &attributes, nullptr, nullptr, WorkerMain, worker))) break;
        const auto status = ObReferenceObjectByHandle(handle, SYNCHRONIZE, *PsThreadType, KernelMode, reinterpret_cast<PVOID*>(&worker->Thread), nullptr);
        ZwClose(handle);
        if (!NT_SUCCESS(status)) break;
        adapter->WorkerCount = i + 1;
    }
}
// Copy a large transfer in chunks; workers that are awake join in. Unclaimed help requests
// are withdrawn, so the submitter never waits for a sleeping worker to wake.
void CopySplit(ADAPTER* adapter, QC_RAM_STORE* store, ULONGLONG offset, PUCHAR buffer, ULONG bytes, bool write)
{
    SPLIT split{}; split.Store = store; split.Buffer = buffer; split.Offset = offset; split.Bytes = bytes; split.Write = write;
    split.Chunk = SplitChunk; split.Chunks = (bytes + split.Chunk - 1) / split.Chunk;
    const auto current = KeGetCurrentProcessorNumberEx(nullptr);
    WORKER* targets[MaxWorkers]{}; ULONG helpers = 0;
    for (ULONG i = 0; i < adapter->WorkerCount && helpers + 1 < split.Chunks; ++i)
    {
        auto worker = &adapter->Workers[i];
        if (worker->Processor == current) continue;
        auto help = &split.Help[helpers]; help->Work.Kind = WorkHelp; help->Split = &split;
        Post(worker, &help->Work);
        targets[helpers++] = worker;
    }
    CopyChunks(&split);
    for (ULONG i = 0; i < helpers; ++i)
    {
        KIRQL irql; KeAcquireSpinLock(&targets[i]->Lock, &irql);
        if (!split.Help[i].Taken) RemoveEntryList(&split.Help[i].Work.Link);
        KeReleaseSpinLock(&targets[i]->Lock, irql);
    }
    while (ReadNoFence(&split.Helpers)) YieldProcessor();
}
// Direct access (volume filter): split a large copy across idle workers. Raised to
// DISPATCH_LEVEL as in StartIo, so the request is copied without being preempted.
void LargeCopy(PVOID context, QC_RAM_STORE* store, ULONGLONG offset, PUCHAR buffer, ULONG bytes, BOOLEAN write)
{
    auto adapter = static_cast<ADAPTER*>(context);
    KIRQL irql; KeRaiseIrql(DISPATCH_LEVEL, &irql);
    if (adapter->WorkerCount > 1 && bytes >= 2 * SplitChunk) CopySplit(adapter, store, offset, buffer, bytes, write != FALSE);
    else QcRamStoreCopy(store, offset, buffer, bytes, write != FALSE);
    KeLowerIrql(irql);
}
