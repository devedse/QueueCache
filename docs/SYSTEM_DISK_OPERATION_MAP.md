# Active system-disk operation map

Updated: 2026-09-24. This is the A07/T023 map of paths that can reach the current
disk filter. It records implemented behavior and gaps. C: is a normal product
target; current activation blocks are temporary migration state, not intended
release policy. Source references name the owning function rather than a historical
test wrapper.

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
| Ordinary read/write, cache inactive | `QcDispatch` forwards directly and holds the remove lock through lower completion. Diagnostics V5 observes `IRP_PAGING_IO` reads/writes before this routing choice. | Plan 25 passed the disabled 349 MiB workload byte-for-byte on exact 0.4.80.1 while measuring bidirectional paging. | Preserve this pass-through behavior in A13 compatibility qualification. |
| Ordinary read/write, cache active | `QcDispatch` queues to the cancel-safe foreground worker; `QcCacheProcess` calls `Read`/`Write`. Fitting Fast writes complete from preallocated RAM. Paging data bypasses admission, so ordinary fitting writes can use the full configured write quota. | Exact 0.4.87.1 Plan-31 Fast/Strict passed complete 349 MiB byte checks and clean release. | Remaining A09 memory-pressure/application workflow and T052/T053. |
| Paging, hibernation or dump path registration | Registration is reserved and ordered against Enable. Normal Enable accepts existing counts. A new in-path request takes one drain/lower-flush boundary and invalidates clean raw blocks, but keeps routing enabled. Failed/cancelled registration rolls its count back. | Revised after the exact 0.4.83.1 configured-pagefile failure. Query-stop/remove remains correctly rejected while Windows owns a special-file path. | Exact installed registration race/state and pagefile restart evidence. Hibernation/Fast Startup are unavailable on this VM. |
| Paging read/write data | Paging requests remain serialized by the foreground worker but bypass RAM admission, clean-read retention and dependency-lane service. Caching swapped-out memory in nonpaged RAM is circular and the 0.4.83.1 pagefile reboot produced process corruption. | Exact 0.4.87.1 fixed-pagefile saved startup stayed active/error-free, passed restart bytes and recorded zero reserve, mapping failures, waits and serviced misses. | Low-memory/fault/cancellation and dynamic-registration proof remain. |
| Queued cancellation | `IO_CSQ` owns queued requests; cancellation releases the request remove lock. A dequeued capacity-waiting request also checks `irp->Cancel`. | Implementation exists; raw disposable-disk cancellation evidence is not in the supported runner. | T053 chooses reachable paging/teardown cases and adds maintained proof. |
| Application/OS flush | Strict calls `QcCacheBarrier` and a lower flush. Explicit administrative flush always does so. Fast may acknowledge an application flush in RAM but never hides an existing cache error. | Secondary-disk Strict/Fast and lower-flush recovery evidence exists. | T052 forces queued-later-write cutoff ordering; A09 normal restart proof. |
| Shutdown | Last-chance shutdown notification is registered. `IRP_MJ_SHUTDOWN` is queued, drains and disables through `QcShutdownBarrier`, lower-flushes, then forwards the original shutdown request. Failure is returned. | Code path is ordered; no active-system-disk restart evidence. | A08/A09 normal restart with independent bytes; T054 recovery first. |
| Device power down/up | Device-power transitions are queued. Leaving D0 records whether caching was active, drains/disables, lower-flushes, marks suspended and forwards. Successful D0 clears suspension and restores the prior active state when capacity and health remain valid. | Implemented plan 27; host policy/native checks pass. | Exact sleep/resume and hibernate/Fast Startup byte/state evidence. |
| Query stop/query remove | When routing is active, PnP query is queued; the generic PnP boundary drains, disables, invalidates clean data and forwards. Inactive queries forward directly. | Ordered implementation. | A09 device lifecycle checks. |
| Surprise removal/final remove | Surprise removal marks the cache gone and wakes waiters; later destruction reports and releases any volatile dirty data because the lower device is already unavailable. Final remove closes admission, waits remove locks and worker exit, attempts its removal barrier, destroys cache state, detaches and deletes the device. | No false persistence promise on surprise loss. Normal removal ordering exists. | T053 lifetime/single-completion proof; A13 hot-remove scope decision. |
| Storage controls and TRIM | Read-only observation controls bypass the worker without draining. Other controls are ordered; media-changing/unknown controls drain then invalidate clean data. METHOD_NEITHER and raw controller pass-through are rejected from the system worker because caller pointers/context cannot be preserved there. | Conservative ordering; file-level TRIM is unsupported on the current VM. | T056 supported-control/TRIM scope. |
| Direct/buffered data | The filter copies the lower device's direct/buffered flags. Cache buffers are nonpaged; data mapping uses the request MDL when present. All dispatch/cache code is nonpageable. | Necessary foundation, not memory-pressure qualification. | T053 allocation/pin/progress and A09 bounded memory-pressure exercise. |
| Saved-profile startup | Installer task runs `qcache policy restore` as SYSTEM after a 30-second startup delay. Restore validates schema, volume, PnP identity, disk size, policy and volatile-flush acknowledgement, then uses the same unrestricted public Apply path. | Exact 0.4.87.1 task completed successfully with fixed 4 GiB C: pagefile, dump registration and a passing post-restart oracle. | Broader startup failure, power and servicing matrix remains. |

## Activation migration decision

The previously exported `PagingPathCount` was always zero, so non-OS verification
could not actually enforce its documented pagefile guard. The current implementation
populates it for paging, hibernation and dump usage notifications, atomically
reserves newly introduced paths against Enable, and orders an active path behind
dirty data. Plan 27 removes the normal Enable and management rejections: all callers
now use the same system-capable policy. A later registration establishes a lower-
media boundary without disabling active routing; its paging data remains ordered
but is not retained in RAM.
On exact installed 0.4.75.1, a configured C: pagefile and dump produced split
counts `Paging=4`, `Hibernation=0`, `Dump=1`. Removing both and rebooting changed
them to `Paging=2`, `Hibernation=0`, `Dump=0`; WMI and the filesystem report no
pagefile, swapfile or hiberfile. Therefore the remaining combined count is now
localized to paging-type kernel usage notifications, but its owner is not yet
explained. An ordinary Q: pagefile previously failed to activate and the attempted
Q: dump logged volmgr Event 46, so neither is valid recovery evidence.

Installed-kernel interleaving evidence remains open. Supporting a pagefile-bearing
C: requires the paging-capable path plus T053/A09 evidence. The end state is normal
activation, not a permanent guard or warning.

Crash/power-loss survival is still not promised by Fast mode. Surprise removal after
the lower device is gone cannot persist volatile data. The product must keep that
distinction separate from normal shutdown/restart persistence.
