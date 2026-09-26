# Known issues and verification gaps

This file describes the current QueueCache implementation. Removed historical
engine defects remain available in Git history and must not be reported as current
bugs without a reproduction on the current driver.

Current status (2026-09-25, installed 0.4.117.1): T082-T086 are implemented and
VM-verified. Ordinary application writes (buffered, flushed and memory-mapped) are
cached; paging-file traffic is recognised per request and never cached. Cache
memory is page-backed, not nonpaged pool. C: active restarts passed once with a
runtime-only profile and four times with a saved profile; a real Paint/Photos
session passed its post-drain byte check. The saved-profile shutdown hang (below)
was a deadlock, fixed and soak-verified on 0.4.125.1. The
historical pre-A01 BSOD remains undiagnosed. The older version checkpoints below
describe their original scope.

## Fixed: shutdown deadlock on a paging-path usage notification (0.4.117.1-0.4.124.1)

On 2026-09-25 the first saved-profile restart (C: Fast 1 GiB, Deferred, about
83 MB dirty after a bounded memory-pressure run) stayed on "Restarting" for over
20 minutes and was reset without a dump. On 2026-09-26 the same cycle hung again
on 0.4.124.1 (third restart of a saved-profile soak, 208.8 MiB dirty); the VM
answered ping but not SSH. An NMI kernel dump shows a deadlock:

- Shutdown (`CmShutdownSystem2` -> `PiPagePathSetState`) sends a paging-path
  `IRP_MN_DEVICE_USAGE_NOTIFICATION` down the C: stack.
- C:'s single request worker forwards it (`Process` -> `OriginalIo`) and waits.
- Below it, `ACPI!ACPIFilterIrpDeviceUsageNotification` is paged out; the fault
  needs a paging read from the C: pagefile (through the compressed store).
- That read is queued behind the same worker. `OriginalIo` only serviced queued
  paging reads while waiting on a forwarded read, so nothing progressed.

Memory pressure is what pages the ACPI handler out, hence the intermittency. The
same pattern could occur whenever Windows adds or removes a pagefile, hive or dump
path while C: routing is active. Fixed in 0.4.125.1 (`42b5ddd`): the worker now
services queued paging reads while it waits on a forwarded PnP or shutdown request
(`QcServiceReadsDuringLowerWait`). Power requests are excluded because a paging
disk holds I/O until D0. The same saved-profile soak then passed 20 of 20 cycles
(see the tracker's T081 row). The crash lost that cycle's dirty Fast data as
expected; `chkdsk C: /scan` found no problems. The dump and symbols are kept off
the VM (`QueueCache-Evidence/hang-20260926-cycle3`, SHA-256 `02D6A68E...5181`).

## Fixed: bugcheck 0x7A while restarting with dirty C: data (0.4.111.1 and earlier)

After `IRP_MJ_SHUTDOWN` drained the cache, the driver marked itself suspended and
failed every later request with `STATUS_DEVICE_NOT_READY`. Windows still pages
during shutdown, so an exiting `dwm.exe` faulting in `win32kbase.sys` code
bugchecked with 0x7A (minidump `092526-7734-01.dmp`). The owned test file was
intact because the drain had finished. Since 0.4.117.1 suspension only blocks
re-enabling the cache; requests keep flowing to the disk.

## Application caching scope (T085)

Paging-marked writes are admitted when their originating file object is not a
paging file. Remaining limits: read misses are not retained as clean entries, the
budget does not shrink under system memory pressure, and NTFS metadata/zero-fill
write-back is admitted like any other write, so the cache shares drain intervals
with it.

## Fixed: bugcheck 0x7E after cache release (0.4.99.1-0.4.104.1)

Builds 0.4.99.1 through 0.4.104.1 can crash Windows (bugcheck 0x7E, integer
divide-by-zero in `FindSlot` called from `FullyResident`) when a paging read
reaches the request worker just after a cache Release, while routing is still
active. The T082 offload check looked up the released (zero-capacity) index.
The VM crashed twice during `ordering-faults` runs (2026-09-25 00:18 and 00:42).
The second crash produced minidump `092526-9562-01.dmp`, analysed against the
CI 0.4.104.1 PDB. Fixed in the following build by the same zero-capacity guard
the other lookups use. Crash dumps were previously disabled on the VM
(`CrashDumpEnabled=0`, dump folder on Q:). Small memory dumps to
`C:\Windows\Minidump` are now enabled.

## Experimental trust and durability

The driver and installer are test-signed development artifacts. Fast mode is
intentionally volatile: eligible writes, write-through requests and application
flushes may complete before the lower device. Sudden loss can lose acknowledged
data and damage the filesystem. Strict mode and explicit administrative flushes
preserve their lower-I/O boundary, but neither makes an experimental driver a
production storage product.

Testing is currently confined to the snapshot-backed VM, clean secondary-disk
workloads and bounded owned-file C: scenarios. Exact 0.4.87.1 passed active Fast,
Strict and saved-profile/fixed-pagefile restart checks, but that scoped VM evidence
is not production qualification. C: is a normal product target, not a planned
unsupported feature. Production signing, unattended recovery and certification
are not implemented.

## Lifecycle scope remains incomplete

Normal secondary-disk restart checks exist, but boot/system-disk, paging,
hibernation, crash-dump, surprise-removal and full power-transition acceptance are
still open. The class filter starts inactive and forwards I/O until a task is
explicitly configured. That pass-through design is not a substitute for the A07–A09
lifecycle campaign.

Exact installed 0.4.75.1 identified the protected C: registrations by type. With
a live C: pagefile and kernel dump configured it reported `Paging=4`, `Dump=1`.
After both were removed and the VM rebooted it reported `Paging=2`,
`Hibernation=0`, `Dump=0`, while WMI and the filesystem showed no pagefile,
swapfile or hiberfile. At that stage active C: verification remained blocked until
the paging-type kernel paths were supported. The combined
count remains authoritative; do not bypass it merely because
`Win32_PageFileUsage` is empty. Plan 22 allows only the disabled/pass-through
image baseline in this state.

That baseline has now passed with the split plan-22 CLI on installed 0.4.75.1:
the deterministic 349 MiB image matched its off-target oracle and C: remained
disabled/released/clean. A paired active request failed during pre-mutation
capture exactly because `Paging=2`; no recovery snapshot or workload was created.
The open defect/qualification question is safe paging-path support and ownership,
not baseline file integrity.

Installed/rebooted 0.4.78.1 plan-23 evidence resolved the accounting question.
Session Manager (`smss.exe`, PID 556 on that boot) submitted exactly two paging
in-path requests; both completed successfully, none failed and no out-path request
occurred. `Paging=2` is therefore two accepted outstanding Windows/storage-path
registrations, not a QueueCache decrement leak. A notification can be propagated
from a related device stack, so the count must not be treated as proof of two
visible page files. Plan 24 adds the required not-disableable PnP state and rejects
query-stop/query-remove while any special-file registration remains. The earlier
active C: gate selected the implemented paging-bypass design; remaining forced
overlap/lifetime cases still belong to T052/T053. This follows Microsoft's
[special-file usage-notification contract](https://learn.microsoft.com/windows-hardware/drivers/kernel/irp-mn-device-usage-notification).

Exact installed/rebooted 0.4.79.1 proved that plan-24 PnP state. Plan 25 adds
pass-through observation of actual paging reads/writes and integrates a windowed
delta into the maintained 349 MiB disabled-cache baseline. It does not enable C:
or diagnose the historical crash by itself; its result selects the next bounded
forward-progress/overlap test instead of guessing at paging behavior.

Exact 0.4.80.1 plan-25 evidence observed 983 paging reads and 427 paging writes
during the safe 13.5-second large-image window, with every byte correct and C:
disabled. Plan 26 is the first guarded active candidate: paging writes receive a
reserved admission region, paging MDLs request high-priority mapping, and safe
non-overlapping paging-read misses can progress past an unrelated capacity wait.
Diagnostics V6 makes any mapping failure or paging capacity wait fail the active
case. Exact 0.4.82.1 passed that active case with complete bytes and clean
restoration. Plan 27 removes normal C: and saved-profile activation blocks, keeps
new usage registrations active after establishing its then-current boundary,
restores active state after device-power resume, and tests normal Fast plus Strict.
Plan 29 replaced paging admission with bypass; exact Plan-31 evidence is below.

Exact 0.4.83.1 is not a C: pagefile candidate. A saved Fast profile with a fixed
4 GiB C: pagefile booted, after which qcache/CoreCLR repeatedly faulted with stack
overflow/access violations and an unrelated Edge updater also access-violated.
WER evidence is retained on the VM's Q: evidence disk. Removing the saved profile,
reverting the pagefile/dump settings and rebooting restored stable pass-through.
The fix keeps paging I/O ordered but bypasses RAM caching and takes a one-time
persistence/invalidation boundary on new system-file registration. Exact installed
0.4.87.1 passed the same saved-profile/fixed-4-GiB-pagefile restart without new
qcache/CoreCLR, Edge or other application faults. The startup task restored C:
active, a separate 64 MiB post-restart oracle matched every byte, dump registration
coexisted with the cache, and paging diagnostics remained at zero reserve, mapping
failures, capacity waits and serviced read misses. This is a short non-reproduction
under the changed implementation; it does not establish a causal fix for the
earlier process failures or low-memory/fault/cancellation/lifecycle qualification.

The pre-A01 large-BMP/Paint/Photos BSOD has no surviving dump or BugCheck event
in the restored snapshot and is unresolved. Plan 21 supplies matching uncached
and active-cache 349 MiB deterministic image workloads with required admission
and post-release evidence, not a root-cause fix. A 4 GiB cache on the
8 GiB guest is only a memory-pressure hypothesis; new admission retains at least
2 GiB or 25% physical-memory headroom and the first active test is capped at
512 MiB.

Do not use automatic reboot, driver deletion or dirty-data discard as failure
recovery. Preserve the VM snapshot and recorded installation backup outside the
guest. A faulted cache must be inspected and explicitly recovered.

## Remaining RAM-first and ordering coverage

Fitting aligned and partial writes have admission and byte checks, and delayed
overwrite/retention cases have passed on the current test VM. Plan 35-37's
aggregate paging-count exception does not prove zero lower I/O for the owned
write; T084 replaces that attribution. Since T085, application paging writes
(file-cache write-back and mapped files) use RAM admission; paging-file and
unknown-origin requests keep ordered lower I/O. Paging read misses are still not
newly retained. The following remain incomplete:

- bounded allocation-failure, cancellation and teardown races;
- oversized and quota-boundary requests under concurrency;
- exact cutoff semantics for concurrent explicit flush and live policy/resize;
- cold lower reads competing with drains and sustained capacity pressure;
- multi-disk memory pressure and starvation;
- all fault-injection points across every parallelism/retention combination.

These are verification gaps, not permission to relax capacity backpressure,
ordering, failure propagation or explicit durability.

## TRIM support is conservative

Range-aware TRIM retirement is not complete. The maintained file-only comparison
currently observes unsupported file TRIM (Win32 326) on the test VM with and
without the filter, so it cannot prove partial-range behavior. Unknown or
media-changing controls retain conservative ordering and invalidation. Malformed,
overlapping and failed TRIM requests remain open work.

## Configuration changes are not transactional

Apply drains and disables before changing the preset, budget and policy. If a later
step fails, it reports failure and does not pretend to roll back atomically. A
failed resize can therefore leave the cache disabled. Saved profiles are updated
only after a successful apply and are identity/size checked during restore.

Live cutoff flush, policy change and resize semantics are planned work. Current
operators should use the supported task commands and inspect state after failures.

## Performance acceptance is incomplete

Focused Q1/Q32 and drain-attribution runs exist, but they are not a complete
performance verdict. Full 72-case small-write and broader mixed-workload matrices
must use the same DiskSpd binary/hash, budget and repetitions. `MEASURED` means a
sample was collected, not that it passed a performance requirement. Lifetime
counters must not be presented as score-window counters.

## UI and telemetry limitations

The desktop shows live state and bounded history, but interval/lifetime labeling,
sample-staleness presentation and complete trigger/wait visibility remain partial.
Unavailable fields must stay unavailable rather than becoming zero. Closing the UI
does not stop caching.

## Installer and compatibility debt

The `qcachelab` service/binary identity, `LabAllowedDriverKey` registry value and
`labWriteCache` package metadata are retained for upgrade compatibility. They do
not denote a second driver edition. A coordinated identity/schema migration belongs
to installer acceptance work. Uninstall retains kernel service/binaries when they
may still be needed for post-reboot recovery.

For task order, exact evidence and acceptance boundaries, use the
[private-alpha handover](PRIVATE_ALPHA_IMPLEMENTATION_HANDOVER.md) and
[RAM-first tracker](RAM_FIRST_IMPLEMENTATION_TRACKER.md).
