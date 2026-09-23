# Active system-disk operation map

Updated: 2026-09-23. This is the A07/T023 map of paths that can reach the current
disk filter. It records implemented behavior and gaps; it is not a C: support
claim. Source references name the owning function rather than a historical test
wrapper.

Review correction: this is an initial map, not a completed resource-lifetime or
system-disk audit. T068's source race is repaired: dispatch reserves an incoming
usage path under the same routing lock that Enable holds through activation, and
failure/cancellation paths undo the reservation. Management also rejects boot and
system targets even if Windows has registered no tracked usage path. Native and
host contracts pass, but exact installed-driver notification interleavings remain
unproved. T067 separately records the owner's earlier large-image crash; this newer
race cannot explain an older build's incident.

| Operation / transition | Current path and ordering | Current disposition | Remaining gate |
|---|---|---|---|
| Ordinary read/write, cache inactive | `QcDispatch` forwards directly and holds the remove lock through lower completion. | Normal pass-through. | Preserve in A13 compatibility qualification. |
| Ordinary read/write, cache active | `QcDispatch` queues to the cancel-safe foreground worker; `QcCacheProcess` calls `Read`/`Write`. Fitting Fast writes can complete from preallocated RAM; misses, partial coverage, capacity and durability boundaries can issue lower I/O. | Qualified only on the secondary disk and declared sector geometry. | A07/T024, A08/A09 system-disk proof; T050/T052/T053 gaps. |
| Paging, hibernation or dump path registration | `IRP_MN_DEVICE_USAGE_NOTIFICATION` reserves an in-path count under `QueueLock` before routing selection. `QcEnable` holds that lock through count validation and activation. Direct lower failure, queued cancellation/rejection and worker/lower failure undo the reservation; successful out-path completion removes it. If already routed, the request drains/disables before forwarding. Plan-23 Diagnostics V4 records each type's in/out requests, successes, failures and last requesting PID on every direct, queued, rejected and cancelled path. | Enforced interim restriction: volatile caching cannot be active under one of these paths. The legacy `PagingPathCount` field remains the combined gate. Diagnostics V3 reports current paging, hibernation and dump counts; V4 adds lifecycle attribution without changing policy. Exact 0.4.75.1 evidence identified a configured C: pagefile/dump as `Paging=4`, `Dump=1`; after both were removed and the VM rebooted, the disk still reports `Paging=2`, `Hibernation=0`, `Dump=0` with no pagefile, swapfile or hiberfile. Source synchronization and compile-time interleaving contracts exist; installed lifecycle proof is pending. | Install/reboot plan 23 and explain the two remaining paging references without equating an empty WMI pagefile list with an unused kernel device path. Then design nonpageable progress and dirty-overlap ordering before supporting paging I/O. The disabled/pass-through image baseline may run without weakening this gate. |
| Queued cancellation | `IO_CSQ` owns queued requests; cancellation releases the request remove lock. A dequeued capacity-waiting request also checks `irp->Cancel`. | Implementation exists; raw disposable-disk cancellation evidence is not in the supported runner. | T053 chooses reachable paging/teardown cases and adds maintained proof. |
| Application/OS flush | Strict calls `QcCacheBarrier` and a lower flush. Explicit administrative flush always does so. Fast may acknowledge an application flush in RAM but never hides an existing cache error. | Secondary-disk Strict/Fast and lower-flush recovery evidence exists. | T052 forces queued-later-write cutoff ordering; A09 normal restart proof. |
| Shutdown | Last-chance shutdown notification is registered. `IRP_MJ_SHUTDOWN` is queued, drains and disables through `QcShutdownBarrier`, lower-flushes, then forwards the original shutdown request. Failure is returned. | Code path is ordered; no active-system-disk restart evidence. | A08/A09 normal restart with independent bytes; T054 recovery first. |
| Device power down/up | Device-power transitions are queued. Leaving D0 drains/disables, lower-flushes, marks suspended and forwards; successful D0 return clears suspension. System-power IRPs are forwarded and the later device-power transition is the persistence boundary. | Code path exists; sleep/hibernate behavior is unqualified. | T025 enforced supported feature set and A09 power-cycle tests. |
| Query stop/query remove | When routing is active, PnP query is queued; the generic PnP boundary drains, disables, invalidates clean data and forwards. Inactive queries forward directly. | Ordered implementation. | A09 device lifecycle checks. |
| Surprise removal/final remove | Surprise removal marks the cache gone and wakes waiters; later destruction reports and releases any volatile dirty data because the lower device is already unavailable. Final remove closes admission, waits remove locks and worker exit, attempts its removal barrier, destroys cache state, detaches and deletes the device. | No false persistence promise on surprise loss. Normal removal ordering exists. | T053 lifetime/single-completion proof; A13 hot-remove scope decision. |
| Storage controls and TRIM | Read-only observation controls bypass the worker without draining. Other controls are ordered; media-changing/unknown controls drain then invalidate clean data. METHOD_NEITHER and raw controller pass-through are rejected from the system worker because caller pointers/context cannot be preserved there. | Conservative ordering; file-level TRIM is unsupported on the current VM. | T056 supported-control/TRIM scope. |
| Direct/buffered data | The filter copies the lower device's direct/buffered flags. Cache buffers are nonpaged; data mapping uses the request MDL when present. All dispatch/cache code is nonpageable. | Necessary foundation, not memory-pressure qualification. | T053 allocation/pin/progress and A09 bounded memory-pressure exercise. |
| Saved-profile startup | Installer task runs `qcache policy restore` as SYSTEM after a 30-second startup delay. Restore validates schema, volume, PnP identity, disk size, policy and volatile-flush acknowledgement. Apply now rechecks the mounted extent and PnP identity immediately before opening the disk; the driver starts inactive. | Prevents guessing a disk and narrows a remap window between inventory and activation. Host checks and a split-CLI Q: Apply smoke pass; saved-profile restore and exact-build VM proof are pending. | T026 capability/role/fault-state policy and suspend/startup failure evidence. |

## Immediate safety decision

The previously exported `PagingPathCount` was always zero, so non-OS verification
could not actually enforce its documented pagefile guard. The current implementation
populates it for paging, hibernation and dump usage notifications, atomically
reserves newly introduced paths against Enable, orders an active path behind dirty
data and refuses Enable while the combined count is nonzero. Management also
rejects every boot/system target while active-system-disk support is unqualified.
On exact installed 0.4.75.1, a configured C: pagefile and dump produced split
counts `Paging=4`, `Hibernation=0`, `Dump=1`. Removing both and rebooting changed
them to `Paging=2`, `Hibernation=0`, `Dump=0`; WMI and the filesystem report no
pagefile, swapfile or hiberfile. Therefore the remaining combined count is now
localized to paging-type kernel usage notifications, but its owner is not yet
explained. An ordinary Q: pagefile previously failed to activate and the attempted
Q: dump logged volmgr Event 46, so neither is valid recovery evidence.

Installed-kernel interleaving evidence remains open. Supporting a pagefile-bearing
C: requires an explicit later change with T053/A09 evidence; simply removing these
guards is not an implementation.

Crash/power-loss survival is still not promised by Fast mode. Surprise removal after
the lower device is gone cannot persist volatile data. The product must keep that
distinction separate from normal shutdown/restart persistence.
