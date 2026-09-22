# Active system-disk operation map

Updated: 2026-09-22. This is the A07/T023 map of paths that can reach the current
disk filter. It records implemented behavior and gaps; it is not a C: support
claim. Source references name the owning function rather than a historical test
wrapper.

Review correction: this is an initial map, not a completed synchronization or
resource-lifetime audit. T068 identifies a race in `437ee42`: dispatch releases
`QueueLock` after choosing inactive forwarding but before `ForwardUsage` increments
the usage count. Enable can run in that gap. The table's intended exclusion is
therefore incomplete and must not be relied on for C: qualification. A zero count
also does not exclude every boot/system disk. T067 separately records the owner's
earlier large-image crash; this new race cannot explain an older build's incident.

| Operation / transition | Current path and ordering | Current disposition | Remaining gate |
|---|---|---|---|
| Ordinary read/write, cache inactive | `QcDispatch` forwards directly and holds the remove lock through lower completion. | Normal pass-through. | Preserve in A13 compatibility qualification. |
| Ordinary read/write, cache active | `QcDispatch` queues to the cancel-safe foreground worker; `QcCacheProcess` calls `Read`/`Write`. Fitting Fast writes can complete from preallocated RAM; misses, partial coverage, capacity and durability boundaries can issue lower I/O. | Qualified only on the secondary disk and declared sector geometry. | A07/T024, A08/A09 system-disk proof; T050/T052/T053 gaps. |
| Paging, hibernation or dump path registration | `IRP_MN_DEVICE_USAGE_NOTIFICATION` is now counted after lower acceptance. An in-path notification is counted conservatively while pending. If routing is active it is ordered behind dirty data, drains, disables caching and then forwards. `QcEnable` and managed Apply reject a nonzero count. | Enforced interim restriction: volatile caching cannot be active under one of these paths. The legacy `PagingPathCount` field represents the combined count. | Implement and qualify each desired path in T024/T025, then narrow the restriction. Do not call this active-C: support. |
| Queued cancellation | `IO_CSQ` owns queued requests; cancellation releases the request remove lock. A dequeued capacity-waiting request also checks `irp->Cancel`. | Implementation exists; raw disposable-disk cancellation evidence is not in the supported runner. | T053 chooses reachable paging/teardown cases and adds maintained proof. |
| Application/OS flush | Strict calls `QcCacheBarrier` and a lower flush. Explicit administrative flush always does so. Fast may acknowledge an application flush in RAM but never hides an existing cache error. | Secondary-disk Strict/Fast and lower-flush recovery evidence exists. | T052 forces queued-later-write cutoff ordering; A09 normal restart proof. |
| Shutdown | Last-chance shutdown notification is registered. `IRP_MJ_SHUTDOWN` is queued, drains and disables through `QcShutdownBarrier`, lower-flushes, then forwards the original shutdown request. Failure is returned. | Code path is ordered; no active-system-disk restart evidence. | A08/A09 normal restart with independent bytes; T054 recovery first. |
| Device power down/up | Device-power transitions are queued. Leaving D0 drains/disables, lower-flushes, marks suspended and forwards; successful D0 return clears suspension. System-power IRPs are forwarded and the later device-power transition is the persistence boundary. | Code path exists; sleep/hibernate behavior is unqualified. | T025 enforced supported feature set and A09 power-cycle tests. |
| Query stop/query remove | When routing is active, PnP query is queued; the generic PnP boundary drains, disables, invalidates clean data and forwards. Inactive queries forward directly. | Ordered implementation. | A09 device lifecycle checks. |
| Surprise removal/final remove | Surprise removal marks the cache gone and wakes waiters; later destruction reports and releases any volatile dirty data because the lower device is already unavailable. Final remove closes admission, waits remove locks and worker exit, attempts its removal barrier, destroys cache state, detaches and deletes the device. | No false persistence promise on surprise loss. Normal removal ordering exists. | T053 lifetime/single-completion proof; A13 hot-remove scope decision. |
| Storage controls and TRIM | Read-only observation controls bypass the worker without draining. Other controls are ordered; media-changing/unknown controls drain then invalidate clean data. METHOD_NEITHER and raw controller pass-through are rejected from the system worker because caller pointers/context cannot be preserved there. | Conservative ordering; file-level TRIM is unsupported on the current VM. | T056 supported-control/TRIM scope. |
| Direct/buffered data | The filter copies the lower device's direct/buffered flags. Cache buffers are nonpaged; data mapping uses the request MDL when present. All dispatch/cache code is nonpageable. | Necessary foundation, not memory-pressure qualification. | T053 allocation/pin/progress and A09 bounded memory-pressure exercise. |
| Saved-profile startup | Installer task runs `qcache policy restore` as SYSTEM after a 30-second startup delay. Restore validates schema, volume, PnP identity, disk size, policy and volatile-flush acknowledgement before Apply. The driver starts inactive. | Prevents guessing a disk and does not activate merely because the driver loads. | T026 capability/role/fault-state policy and suspend/startup failure evidence. |

## Immediate safety decision

The previously exported `PagingPathCount` was always zero, so non-OS verification
could not actually enforce its documented pagefile guard. The current implementation
populates it for paging, hibernation and dump usage notifications, orders a newly
introduced path behind dirty data, and refuses Enable while the combined count is
nonzero. This is intended to fail closed, but the T068 race remains open. Supporting a
pagefile-bearing C: requires an explicit later change with T053/A09 evidence; simply
removing this guard is not an implementation.

Crash/power-loss survival is still not promised by Fast mode. Surprise removal after
the lower device is gone cannot persist volatile data. The product must keep that
distinction separate from normal shutdown/restart persistence.
