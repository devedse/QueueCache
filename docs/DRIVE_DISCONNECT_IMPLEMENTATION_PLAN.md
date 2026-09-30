# Drive disconnection implementation plan

Status: implementation delivered in stages; qualification incomplete, 2026-09-30. The checklist below remains the acceptance contract; source changes alone do not mark the VM cases complete.

Audience: implementation and subsequent independent review.
Baseline inspected: branch `volume-filter`, verification plan 70. Re-read current source and AGENTS.md before starting; do not overwrite concurrent changes or assume these line locations remain current.

## 1. Objective and agreed scope

Make orderly disk ejection preserve pending cache data, and make unexpected disconnection terminate safely without freezing Windows, corrupting another volume, or serving stale data after reconnect.

The owner accepts that sudden removal in volatile Fast mode can lose already acknowledged writes and damage the filesystem. Do not attempt to recover or replay that RAM data onto a returning disk. A journal does not make volatile acknowledged writes durable. Strict mode protects only operations for which the existing durability contract actually completed successfully, subject to lower storage honouring its flushes.

Deliver driver lifecycle handling, supported verification, CLI safe eject, and desktop eject/disconnected-state handling. The proposed cached virtual-drive product feature remains postponed. Sleep/hibernate, general recovery, new caching algorithms and ReFS performance work are outside this change.

## 2. Findings to audit, not presumed defects

- `driver/qcache/driver.cpp`, `QcDispatch`: QUERY_STOP/QUERY_REMOVE rejects special-file paths and otherwise queues or forwards. SURPRISE_REMOVAL sets `Cache.Gone` and signals Changed/Wake. REMOVE_DEVICE closes admission, waits for remove locks and the request worker, destroys the cache, forwards removal and deletes the device.
- `driver/qcache/writecache.cpp`: `QcCacheBarrier` observes Gone; processing rejects Gone; `QcCacheDestroy` stops/joins worker threads and frees dirty slots, currently logging remaining dirty bytes with DbgPrint.
- Many independent execution paths exist: caller-thread service, ordered worker, paging readers, offloaded copies, drain workers, generated lower IRPs and forwarded control work items. A flag in the main worker alone is insufficient.
- `CacheTasks.RemoveAsync` removes a cache task, not a physical device. Preserve that distinction and its existing command behaviour.
- `VolumeCatalog`, `DiskTarget`, `SavedConfigurations` provide identity and profiles; the desktop service currently lists mounted volumes. Investigate missing-volume presentation and profile restore behaviour before adding automatic actions.
- Previous snapshot/shutdown deadlocks involved lower calls needing paging or same-volume I/O. Preserve the work-item and paging-service mechanisms that resolved those problems.

First implementation checkpoint: document the actual lock order, ownership of each IRP/work item, events waited upon, removal-lock coverage and Windows removal sequence observed for the selected test bus. Resolve unknowns before changing teardown.

## 3. Required invariants

1. A safe-eject success means Windows accepted removal after all affected cache volumes passed their required durability boundary. Never equate an empty dirty counter, a detached drive letter, or a successful cache release with successful physical ejection.
2. The admission cutoff is atomic with cache admission across every path. Writes admitted before it must be included in the orderly drain. Requests arriving later follow the documented PnP pending policy; they cannot sneak into a cache being destroyed.
3. A drain/flush error vetoes QUERY_REMOVE where veto is permitted. REMOVE_DEVICE and SURPRISE_REMOVAL are notifications, not opportunities to veto or keep waiting for an absent disk.
4. Failed or cancelled query removal restores operational eligibility according to Windows sequencing without clearing genuine cache faults or overwriting user settings. Duplicate/cancel-without-query requests must be harmless.
5. Once Gone is published, no newly admitted request succeeds from cached data. Requests already completing at the transition need a clearly documented linearization rule.
6. Complete/cancel every owned IRP exactly once. Do not free buffers, MDLs, cache state, work items or device extensions while lower I/O or callbacks can still reference them.
7. No timeout authorizes freeing an IRP still owned below. If a lower stack never completes, preserve evidence and report the limitation; inventing success or unsafe forced teardown is unacceptable.
8. Device removal cannot block unrelated volumes. Do not hold global configuration locks across indefinite device I/O.
9. Reattachment starts with empty cache contents and fresh device lifetime state. Disk numbers and letters are not identities; no dirty-data replay.
10. Preserve Fast admission performance outside lifecycle transitions. No whole-cache drains added to ordinary observations or partial writes.

## 4. Driver implementation work

### 4.1 Explicit lifecycle model

Define named lifecycle states or an equivalent synchronized model: Running, QueryRemoving, RemovePending, SurpriseRemoved, Removing, Removed. Keep user Enabled/Paused settings distinct from lifecycle admission state. Specify transitions for lower-query failure, CANCEL_REMOVE, orderly remove, surprise remove during drain, startup failure followed by remove, and direct remove without a preceding query.

Store only the configuration/eligibility needed to undo a query transition. Do not resurrect a gone device. Coordinate QUERY_STOP/CANCEL_STOP with existing semantics without treating stop as permanent removal. Publish state safely to caller, paging and drain paths; document lock ordering and memory visibility.

### 4.2 Orderly removal

Close cache admission at the query boundary, settle previously accepted writes, and issue the required lower flush. Confirm the actual filesystem/volume/disk PnP ordering on the VM: QueueCache is a volume filter, so do not assume a physical-disk query directly arrives on each volume stack.

Forward QUERY_REMOVE with a completion path that handles lower vetoes and follows WDM sequencing. Retain enough state for CANCEL_REMOVE. Audit pending IRPs and owned handles so the product does not veto its own eject. No unconditional waiting on a worker whose needed I/O has been suspended by the same transition.

If native disk ejection does not deliver a usable volume query boundary, implement an explicit per-volume prepare/cancel protocol for the product eject operation and separately document native Windows-eject coverage. Preparation must remain quiesced through the PnP transaction; a one-time flush followed by renewed admission is not sufficient. Any new control needs typed management support, capability detection and tests. Do not claim coverage of external eject paths until measured.

### 4.3 Surprise removal and final teardown

Publish Gone promptly, wake all relevant waiters (capacity, ranges, offload, barriers, drain, queued work), reject new data operations, and cancel/drain owned queues using their cancellation-safe ownership rules. Forward required PnP requests without routing them behind unavailable storage work.

Inventory generated lower I/O and forwarded-control callbacks, including the interval after completion when a work-item callback may still execute. Use appropriate lifetime references until the callback's last access, not merely the original IRP completion.

Join threads and free allocations only after ownership is settled. Final removal must also handle never-started and partly initialized devices. Record unresolved dirty/in-flight data as potential loss, not an exact number of bytes lost on disk.

### 4.4 Evidence that survives removal

Add minimal lifecycle diagnostics: attachment generation, state, transition reason, query/veto/cancel counts, surprise-removal count, pending-byte snapshot, outstanding request counts and status codes. Follow existing versioned diagnostics patterns; capability absence is explicit, never zero-filled.

A removed volume cannot answer IOCTLs. Emit a durable Windows event or supported driver tracing event at transition time, with a tested collection path. The verifier records pre-removal snapshots to another disk. Distinguish sampled dirty bytes from proven lost bytes. Avoid synchronous logging to the disappearing volume or high-frequency event flooding.

## 5. Shared management, CLI and desktop

### 5.1 Shared eject operation

Add an operations-layer service (suggested `DiskEjection.cs`) and typed result/preview models; CLI and UI bind to it. Resolve volume GUID -> extents -> disk PnP instance -> ejectable device node and its removal relations. Enumerate every affected volume, including unlettered ones. Never walk upward blindly and eject a controller shared with other disks.

Refuse OS/boot/pagefile/hibernation/crash-dump targets and ambiguous multi-disk layouts. Revalidate identity immediately before mutation. Unsupported hardware reports NotSupported; do not substitute disk offline, forced dismount, device disable or VHDX detach and call it eject.

Use `CM_Request_Device_EjectW` for supported Windows eject operations, retain CONFIGRET and veto type/name, and reconcile final device presence. Close preview/management handles before the request where required. Serialize competing local configuration operations without making the UI or unrelated disks wait indefinitely.

Saved profiles survive eject. A failure/cancel preserves settings and reports any incomplete rollback separately. Never automatically remove user profiles, format storage, force applications closed, or reboot.

### 5.2 Proposed CLI

- `qcache disk eject <volume> --preview`: report stable disk identity, all affected volumes, cache state and eject capability; no mutations.
- `qcache disk eject <volume>`: request safe removal of the containing disk, with synchronous progress and meaningful nonzero results for veto, unsupported target or failure.
- Keep `qcache policy remove` as cache removal only.

Follow existing exit-code conventions after inspecting the CLI. Do not add `--force`, surprise-unplug product commands or a new executable. Explain whole-disk scope in help/output. Cancellation after Windows accepts the request means reconcile the result, not pretend it was undone.

### 5.3 Desktop

Add Eject at disk-group level when eligible; confirmation lists all affected volumes and pending cache data. Show Preparing, Writing pending data, Requesting removal and final state based on actual evidence. Windows vetoes appear with actionable details; unsupported hardware has a clear reason.

Retain disconnected saved profiles in a distinct unavailable state. Disable live cache operations on absent volumes; do not display old counters as live or show zero dirty bytes as a successful flush. Invalidate stale asynchronous results by attachment generation when disks disappear/reappear.

For this scope, do not introduce automatic hotplug profile activation. On reconnect rediscover identity, start with empty cache contents, and allow explicit reapplication through existing controls. Preserve existing startup restore behaviour. Any future automatic reconnect restore is a separately reviewed policy.

## 6. Maintained verification design

Extend `src/QueueCache.Developer/Verification/{VerificationPlan,VerificationRunner,VerificationWorker,RunStorage}.cs`, with typed scenarios in Operations and host contracts in `VerificationRunnerTests.cs`. Suggested suite: `disk-removal`, explicitly opted in and excluded from `full` because it physically disconnects storage. Bump plan version when its contract lands; do not manually change build versions.

Preflight must bind an explicitly disposable physical-disk identity and every affected volume, prove the output/oracle directory is on another disk, reject protected targets, check loaded CI driver identity/Verifier, and exclude competing tests or armed fault hooks. An expected removal must not be mistaken for ordinary restoration failure and trigger writes to the next device occupying that letter.

Use a persistent run manifest and explicit phases: Prepared -> ReadyForExternalRemoval -> RemovalObserved -> AwaitingReconnect -> Verifying -> Restoring -> Finished. Record exact owned PIDs, device identities, timestamps and attachment generations. Add a typed resume/acknowledgement mechanism to the same foreground runner for physical actions; it must reject stale run/case IDs and wrong devices. Do not create another private suite orchestration script or executable.

Proxmox credentials and control stay outside shipped software. A host operator/controller only performs the requested external unplug/reconnect after the recorded ready handshake; all workloads, validation and reporting belong to the supported runner. A hypervisor command returning success is not evidence of guest surprise removal: require matching guest PnP evidence. A guest-cooperative hot-remove is an orderly test, not a cable-pull test.

Use bounded operation deadlines and the existing independent restoration deadline. Missing phases, callbacks, counters or completion markers yield INCOMPLETE/UNEXERCISED, not PASS. No automatic reset or reformat on failure. Preserve the failing backing disk for review.

## 7. Acceptance matrix

| Case | Required evidence and pass criteria |
|---|---|
| Orderly eject, Fast with pending writes | Prove pending data before eject; all acknowledged test bytes match after reconnect, lower flush boundary recorded, Windows accepted eject. |
| Two cached volumes on one disk | Different patterns/budgets on each; both survive orderly eject and neither is omitted from preparation. |
| Veto/cancel | Induce a real supported veto and verify settings and I/O resume; capture query/cancel if exercised. An open handle alone does not prove CANCEL_REMOVE occurred. |
| Drain failure during query | Use scoped existing lab fault machinery; eject denied, dirty data not silently discarded, fault visible; cleanup never retries fault as ordinary draining. |
| Idle surprise removal | Guest surprise event confirmed; no new cache reads accepted afterward; unrelated control volume stays usable. |
| Surprise during queued/capacity-blocked writes | Prove blocked requests before unplug; they settle with valid failure/cancel status and no double completion. |
| Surprise during lower drain/read/offloaded copy | Prove the targeted operation was in flight; callbacks and allocations settle without Verifier failures or stale access. |
| Strict and explicit durable baseline | Write and verify a durable oracle before unplug; no later writes to that oracle region; bytes match after reconnect, subject to honest filesystem availability. |
| Fast unflushed data | No byte-survival requirement; report potential loss and check containment/cleanup. Filesystem damage is possible, not automatically a driver failure. |
| Reconnect identity | Same disk/new letter starts empty; different disk/old letter cannot inherit configuration or data. No automatic RAM replay. |
| Repeated removal | At least 10 orderly and 10 surprise cycles on the qualified bus; no accumulating driver allocations, handles or threads beyond documented noise. |

Start NTFS with one and then two volumes. Follow with focused FAT32 and ReFS orderly/unexpected tests; ReFS can take itself offline after write errors and FAT can be damaged. Do not silently remount/repair to hide a failure. Retain results by filesystem and bus. VHDX detach provides supplementary evidence only. If deterministic targeting of a path is unavailable, report that gap rather than extending a timing race until it happens to pass.

## 8. VM execution boundaries

Use VM 109 and a dedicated disposable hot-pluggable test device. Do not remove C:, Q:, their backing disks, a shared controller or the VM's existing SATA test disk just because its number is convenient. Disk numbers changed when SATA was added previously.

First inspect available bus capabilities and select a mechanism that actually produces surprise removal without guest preparation. If unavailable with the existing Proxmox permissions, report the precise missing capability; do not substitute shutdown/start or graceful detach. Do not broaden token scope. Preserve backing storage on unplug; deletion is not part of the test.

Restore Verifier/debug settings and temporary profiles to their recorded pre-test state after successful tests. Leave failed evidence intact and report required manual recovery. Existing reset authorization does not justify hiding failed lifecycle tests through automatic resets.

## 9. Work packages and review gates

1. **Audit and design:** ownership/lock table, lifecycle transitions, chosen guest/hypervisor mechanism, actual volume PnP routing evidence. Review before teardown implementation.
2. **Driver and diagnostics:** narrow changes with native state/ownership checks; retain lower-attempt evidence for unchanged Fast admission. Build through CI.
3. **Runner:** implement focused scenarios, external-action handshake, immutable evidence and disconnect-aware recovery; host tests for wrong identity, timeouts and missing evidence.
4. **Management and CLI:** preview/eject/results, identity revalidation and veto handling. Host tests using injected platform dependencies, not live disks.
5. **Desktop:** disk-level actions, absent profiles, race-safe refresh; run desktop fixture tests.
6. **VM qualification:** focused matrix under standard Driver Verifier; exact CI binary identity; targeted quick/policy regression after relevant fixes. Do not run the 72-case performance matrix unless evidence indicates a request-path performance change.
7. **Documentation and final review:** update tracker implementation and verification separately, KNOWN_ISSUES, developer guide, user help and volume-filter reference. Preserve gaps and raw output.

Each package should be a reviewable scoped commit. Follow the user's current requested branch/remote; do not merge or create a new PR merely because this document exists. The current handoff branch is `volume-filter`.

## 10. Definition of done and reviewer packet

Done requires verified safe eject on a supported device, verified unexpected-removal containment, safe reconnect, truthful UI/CLI results, maintained tests and documented unsupported paths. A zero dirty counter or a clean host test run is not enough.

Provide: commit/CI identifiers; driver hash and loaded identity; lifecycle/lock/ownership table; API decisions; exact run directories and FINISHED/status/summary/results/log files; raw PnP trace and external action timestamps; per-case durable oracle results; Verifier/event checks; resource trend evidence; gaps by bus/filesystem; and final VM/profile state. Highlight any modified durability or cancellation semantics for independent review.

## 11. Windows references to consult during implementation

- [CM_Request_Device_EjectW](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_request_device_ejectw): supported eject request and veto reporting.
- [IRP_MN_REMOVE_DEVICE](https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/irp-mn-remove-device): removal sequences, including startup failure; final remove cannot be vetoed. Do not synthesize PnP remove IRPs.
- [Handling CANCEL_REMOVE](https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/handling-an-irp-mn-cancel-remove-device-request): restore pre-query state with lower-stack completion ordering.
- [Device Fundamentals PnP tests](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnp-tests--device-fundamentals-): possible supplementary query/cancel/removal coverage. These do not replace the cache's data oracles.

## 12. Current delivery and review handoff (2026-09-30)

Implemented: atomic Gone admission cutoff for surprise/direct final removal;
query drain/disable with lower-veto and successful-cancel state restoration;
durable surprise event; shared physical-disk eject with bounded dedicated-adapter
resolution and native volume-extent scope proof; CLI preview/eject; desktop disk
eject, disconnected profiles, stale sample rejection and mutation identity binding;
per-disk configuration gates; maintained orderly pending-write/reconnect suite
(plan 73), structured veto restoration and preparation lower-attempt evidence.
Ownership review is in DRIVE_DISCONNECT_OWNERSHIP_AUDIT.md; exact implementation
and independent verification status is in RAM_FIRST_IMPLEMENTATION_TRACKER.md.

VM evidence covers ordinary cached NTFS operations, a real disk-node veto with
supported recovery, and successful Windows dedicated-adapter eject with pending
Fast writes. The latter reconnect failed in host hotplug handling and the run is
RESTORATION_FAILED; it is not a passed removal/reconnect case. The owner requested
that the detached disk stay detached. No further host attach/recovery is authorized
by this checkpoint.

Remaining acceptance gates: explicit driver prepare/cancel capability if native
volume query routing is inadequate; full versioned lifecycle diagnostics; external
Windows eject routing; successful live reconnect with plan-73 durability evidence;
two-volume ejection; dirty/queued/capacity/lower-I/O surprise races; Strict/durable
baseline, attachment identity variations and ten-cycle orderly/surprise matrices.
The current source audit does not prove every lower callback ordering. Do not
label this feature fully qualified until these gates have real evidence.
