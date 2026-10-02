# Drive disconnect ownership checkpoint

Source checkpoint: 2026-09-30, `volume-filter`. This is a source audit and review
map. It does not qualify removal under Driver Verifier or settle a lower stack
that never completes an IRP.

## Admission and transition order

`QcCacheDisconnect` runs at PASSIVE_LEVEL for surprise and final removal. It
acquires the cache Mutex, publishes Gone and a coherent pending-byte snapshot,
wakes Changed/drainers, releases Mutex, and wakes the paging readers. RAM read
and write admission checks Gone under that same Mutex. This defines the cutoff:
an operation already admitted/pinned can finish its copy after the transition;
a later operation cannot begin serving RAM data. Offloaded write buffers and
pinned read versions retain their existing owners until completion.

Dispatch rejects new non-PnP/power/cleanup/close operations after Gone, before
observation, paging-file and inactive direct bypasses. Surprise notification is
forwarded directly with successful local status. Final removal publishes Gone
even when no query or surprise notification preceded it, closes the CSQ admission
gate under QueueLock, waits for remove-lock owners, joins the request worker,
destroys cache threads/allocations, then forwards remove and detaches/deletes.

QUERY_REMOVE is ordered behind earlier requests. Its barrier disables RAM
admission and issues the lower durability flush before forwarding query. A lower
veto restores the prior Enabled setting only while healthy and present. Success
keeps the saved setting until a successful lower CANCEL_REMOVE; duplicate queries
retain the first saved setting. Controls cannot reopen QueryRemovePending.
Windows native physical-disk-to-volume query routing remains unqualified. Product
eject explicitly drains/disables all supported lettered volumes first.

## Locks

| Lock | Protected state | Nesting / waits |
|---|---|---|
| QueueLock (spin) | CSQ, Closing, Routing, DirectCount, caller ownership and worker metadata | Released before cache Mutex acquisition, blocking waits and lower calls. Dispatch snapshot reads may take SnapshotLock while QueueLock is held. |
| Cache Mutex (push lock) | RAM slots, Enabled, query state, generation, barriers and cache snapshots | Publish takes SnapshotLock inside Mutex. Enable briefly takes QueueLock inside Mutex for its usage-path admission check, then releases QueueLock before Publish. Lower completion waits release Mutex. |
| SnapshotLock (spin) | Coherent public snapshots | No acquisition of Mutex or lower calls while held. |
| PagingLock (spin) | Offload entries, queue count, stop flag | Released before Read/Write processing, completion and thread waits. |
| Management mutex | Cooperating CLI/UI mutations on one physical PnP identity | Thread-affine; no await while held. Other disks use separate mutexes. It does not serialize external or older clients. |

Review must preserve these directions: Mutex may take QueueLock, but QueueLock
must never wait for Mutex. No spin lock permits a passive wait.
Queue admission and RAM admission have separate ownership boundaries; neither
permits freeing requests already owned below.

## Request and callback ownership

| Path | Owner and lifetime | Removal settlement |
|---|---|---|
| CSQ pending request | Remove lock acquired at dispatch; CSQ owns cancellation until dequeue | Closing rejects insertion. Cancel callback or request worker completes/releases exactly once. Gone processing fails queued data. |
| Caller RAM request | Remove lock plus DirectCount/CallerActive | Worker waits for DirectIdle before control transitions; RAM admission checks Gone under Mutex. Caller clears counters and releases its request lock before completion. |
| Direct/observation/paging-file forwarded request | Lower stack owns IRP; completion holds extension via remove lock | Completion updates counters if applicable, releases lock as its last extension access, then continues completion. No timeout frees the IRP. |
| Generated lower IRP | Cache drainer/reader/request worker owns buffer and retained IRP | RetainCompletion signals the waiter. Owner waits for actual completion before freeing IRP/MDL/buffer; thread join precedes cache allocation release. |
| `CallLower` work item | Stack context remains owned by calling cache thread | Caller waits for both lower completion and work-item Returned, including synchronous completion, before stack context or reusable item can disappear. |
| Forwarded media-control work item | Heap QC_FORWARD owns item/lower/IRP references; original remove lock lasts until lower completion | Work item does not access QC_CACHE after IoCallDriver returns. Completion's last cache access releases original request. Item is freed on callback return path; IoQueueWorkItem keeps associated device referenced while callback runs. |
| Offloaded read/write | Entry and pins owned by paging reader; original remove lock until CompleteRequest | Reader can still clear entry/counters after original lock release. QcCacheDestroy must join every reader before freeing cache, preserving this tail lifetime. |
| Background drainer | Joined cache thread owns generated lower I/O and staging buffers | Gone ends new selection; active lower completion still owns its buffers. Destroy sets Stop and joins all workers before FreeSlots. |

## Wakeups and remaining boundaries

Gone signals Changed, Wake and PagingWork. Capacity/range/barrier loops recheck
Gone; some progress waits also poll at 100 ms. Requests waiting for an actual
lower completion or outstanding offloaded owner cannot be force-completed by a
wake signal. Final removal can still wait indefinitely if a broken lower stack
never completes; retaining ownership is preferable to freeing a live callback.
Drainers outside configured parallelism sleep on Stopping and are awakened by
destruction, after request ownership is settled.

Native build and real direct-remove, duplicate-query, failed-cancel and concurrent
surprise paths remain verification gates for this source checkpoint. Lifecycle
counter diagnostics and explicit prepare/cancel protocol are still open work.
The current SATA lab connection reports no eject capability; the previous
VirtIO-scsi connection accepted eject without observed disappearance, then
hypervisor detach produced a controller veto followed by guest surprise removal.
Neither observation establishes external Windows orderly-eject coverage.

## Windows contracts consulted

- [Surprise-removal handling](https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/handling-an-irp-mn-surprise-removal-request): stop new I/O, continue PnP/power/close/cleanup handling, and forward the notification.
- [Remove-lock ownership](https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/using-remove-locks): final deletion follows outstanding owners.
- [Work-item device references](https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/releasing-driver-allocated-resources): device reference while the callback runs.
