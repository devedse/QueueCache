# Known issues and verification gaps

This file describes the current QueueCache implementation. Removed historical
engine defects remain available in Git history and must not be reported as current
bugs without a reproduction on the current driver.

Current status (2026-09-28, branch `volume-filter`, installed 0.4.219.1): QueueCache
now caches volumes ([volume filtering](VOLUME_FILTER.md)). Under Driver Verifier every
maintained suite passes on a lab-disk volume, plus the new `volumes` and `trim-cache`
suites; the raw volume tests, uninstall/reinstall and the registration recovery
script were checked on the VM. Six defects were found and fixed along the way (below).
Earlier (disk filter, 0.4.166.1): every maintained suite and a 10-cycle saved-profile
C: restart soak passed under Driver Verifier.

## Fixed on the volume filter (2026-09-28)

| What you would have seen | Why | Fix |
|---|---|---|
| With the cache covering C:'s volume, the PC reset a few seconds into boot | The driver asked the boot volume for its length from its start-up handler; below it are snapshots, BitLocker and the volume manager | The length and sector size are read on first need, from a normal thread (`e7150b7`) |
| Extending a cached volume beyond its original size failed ("Invalid Parameter"); the partition grew but the file system did not | The driver learned the length once and refused writes beyond it | The length is re-read for each QueueCache request and before judging a request beyond the known end (`6ae1e0c`); `volumes/volume-resize` |
| Creating a shadow copy (System Restore, backup) of a cached volume froze the PC: no program could start, not even Task Manager | The cache forwarded the snapshot driver's "prepare" request and waited for it; that request writes a file on the same volume, and that write queued behind the waiting cache | Requests that can change the volume are forwarded without holding the request worker (`fcc0c4c`) |
| Shadow copies of a cached volume then failed after 10 s (Windows event: "flush and hold writes ... timed out waiting for a release writes command") | The snapshot driver's follow-up requests, such as "release writes", waited in the cache's ordered queue behind work that needed a write the snapshot driver was holding | Only "flush and hold" drains the cache (so the snapshot contains data that was pending in RAM); the follow-up requests bypass the queue (`0909d93`, `7a18a57`); `volumes/volume-snapshot` |
| A cache left on an unformatted volume by a raw test could not be removed from the CLI or desktop | Removal used the NTFS-only identity check | Removal works on any volume; the desktop disables Settings and Pause on non-NTFS volumes (`7134514`) |
| A verification run on Q: left the cache at the test's 256 MiB | The RAM guard refused growing back to 2 GiB during a momentary low-memory dip on the 8 GiB VM | Restoration waits up to two minutes for RAM (`0909d93`); the VM now has 16 GB |

## ReFS: fewer requests on the fast caller-thread path (performance)

What you see: on a ReFS volume (for example a Dev Drive) with background write-back,
fewer program requests are served on the program's own thread (64% instead of about
99% in a 64 KiB random read/write test; 818 vs 1,200 MB/s on NTFS in the same test).
Data is unaffected.

Why: ReFS often splits one program request into two requests to the volume at the
same time. The fast path is taken only when exactly one request is in flight, so the
second request finds the first "busy" and sends the next 256 requests through the
ordered worker.

Proposed change: treat a request from the same file-system operation that overlaps an
in-progress caller-path request as a candidate for the fast path instead of opening
the 256-request worker window, then re-measure on ReFS.

## Low-priority programs read cached data at about half speed (performance)

What you see: a program running below normal priority (for example anything started
by Task Scheduler with its default priority 7) reads cached data much slower. On the
4-vCPU test VM, 1 MiB sequential Q8 reads from a 2 GiB Fast cache measured
16.4-17.7 GB/s at below-normal priority against 28.9-30.6 GB/s at normal priority,
same file, same session (2026-10-08, 0.4.414.1). Data is unaffected.

The controlled change was Task Scheduler priority 7 versus 4, which changes CPU,
I/O and memory priorities together. CPU contention with copy workers and lower-I/O
scheduling are possible explanations, but their individual contributions were
not measured. The driver does not inspect request/thread priority itself. The
uncached Ceph disk also improved from 262/116 to 640/270 MB/s; those scores alone
do not prove how many lower requests were outstanding.

Proposed change: measure where the time goes (submitting thread waiting for a
processor, or copy threads spinning) with the developer timing counters, then
consider copying on the caller's thread when it is the only one waiting. Until then,
benchmark at normal priority ([benchmarking](BENCHMARKING.md)).

## Fixed: sequential reads slowed down after cache allocation reuse (up to 0.4.430.1)

Up to 0.4.430.1 a clean, fully resident cache read sequentially up to 45% slower
after drop-clean and refill than after allocating it anew (Q1/Q8 10.8/29.8 GB/s
after a sequential file, 8.6/23.2 after random reads, versus 15.5/36.9 fresh). The
LIFO free-slot list handed slots back in reverse or random order. Since 0.4.431.1
a chunk allocator fills each 256 KiB chunk upwards: reuse measures 14.6/35.7 and
14.5/35.5 (fresh 15.1/35.9), and since 0.4.434.1 prefetch and in-chunk coalesced
copies raise reads to about 19-21 GB/s Q1 and 37-39 GB/s Q8. See the
[investigation](CACHE_LAYOUT_INVESTIGATION_20261008.md).

Partial eviction leaves holes that no allocator joins (about 2-8% of neighbours
out of order after churn). Not pursued: moving data could gain at most 5-7% at Q1
and 10-15% at Q8, only for a partly evicted file read sequentially, and a mistake
would corrupt cached data ([idle defragmentation](CACHE_LAYOUT_INVESTIGATION_20261008.md#idle-defragmentation-evaluated-not-built)).

Sequential 1 MiB Q8 writes with write-back running once measured 3-14% lower with
the chunk allocator. This is not demonstrated: that comparison spanned two days
in which the uncached disk itself went from 255 to 115 MiB/s, and write-back
writes to that disk during the window. Within one build, Deferred and Eager Q8
writes differ by 0.5%, and four concurrent writers are at most 2.8% below one
([sustained validation](SUSTAINED_CACHE_VALIDATION_20261009.md#speed-up-implementation-plan)).
The old allocator's build can no longer be installed over the current one (see
Installer and compatibility debt), so the 72-case `write-performance` matrix on
the current build is the reference from now on. It completed 72/72 on 0.4.476.1:
Q8 medians are 5–14% below 0.4.426.1, with overlapping ranges and a much slower
uncached disk; cached Q1/random writes are higher. No allocator cause is proved
([complete matrix](WRITE_PERFORMANCE_20261010.md)).

## A program resuming random I/O right after another file was re-read starts with fewer hits (performance, since 0.4.469.1)

What you see: a random read/write workload that runs again right after a different
file was read again from the same cache has about 7 percentage points fewer RAM
hits for roughly a minute, then about 4 points more than before 0.4.469.1 (2 GiB
Fast cache, 64 KiB random I/O over a file twice the cache, 2026-10-09).

Why: read recall gives cache space back to whatever was read most recently. The
re-read file returns to RAM speed (10-230 times faster in the same tests), and that
space comes from the random workload's least recently used data.

Proposed change: none for now; this is the least-recently-used trade-off read
recall was built for. If real use shows it, an adaptive split between recently and
frequently used data (as in ARC) could limit how much one re-read file takes.
`qcache developer driver read-recall <volume> 0` restores the earlier rule for
comparison ([measurements](SUSTAINED_CACHE_VALIDATION_20261009.md#read-recall-plan-104-044691)).

## A request that is only partly in RAM is read entirely from disk (performance)

What you see: a file that is mostly but not completely cached can still read at
close to disk speed. With the earlier insertion rule, a re-read file with 1-4% of
its blocks missing read at 1.6-2.5 GB/s instead of about 30 GB/s (2 GiB Fast cache,
1 MiB requests, 2026-10-09). Read recall usually fills those gaps on the next pass.

Why: when any 4 KiB block of a request is missing, the whole request (up to 16 MiB)
is read from the disk, then the cached blocks are copied over it.

Diagnostics V21 now count staged requests/lower bytes and already-cached overlap;
the patterned timing-off/on scenario passes all ten checks. The 0.4.514.1 focused
churn run completed 6/6: weighted overlap is only 0.788% of mixed lower traffic
and 0.449% of Q1 recovery traffic; Q8 recovery stages nothing. No missing-span
implementation is justified for these shapes. Other scatter patterns remain
conditional on representative overlap and fragmentation evidence, with ordering
and error handling preserved. See the consolidated
[findings](PERFORMANCE_FINDINGS.md#partly-cached-requests-accounting-first).

## One program reading a RAM disk tops out at about 26 GB/s (performance)

What you see: on the 4-vCPU test VM, one DiskSpd thread reads a RAM disk with 1 MiB
requests at about 26 GB/s whether it keeps 1 or 8 requests outstanding (Q1 or Q8);
four threads together reach 40-42 GB/s. CrystalDiskMark's SEQ1M Q8T1 row is that
single-thread case. A cached volume reaches about 39 GB/s from one thread at Q8.

Why: with Direct access the RAM disk copies each read when the request arrives,
splitting one large copy over its worker threads, so one program's queued requests
are copied one after another. Four programs (or threads) give four copies at once.
The cache instead hands queued hits to its own copy threads, so they overlap.

The provider already has dedicated copy workers. A follow-up should profile the
handoff, wake-up and copying costs before choosing another way to overlap whole
requests. Earlier system-worker designs gave no gain (25 vs 26 GB/s) or fell to
13-17 GB/s. Queued Direct copies using the provider's per-processor workers were
also tried and removed (`3fb9d39`, `e8e31c8`): large reads reached 20.8-22.3 GB/s,
with 21% of busy CPU samples spinning versus 28% copying. Adding worker threads
alone therefore is not a demonstrated fix.

The possible gain is for one-thread, deep-queue readers. Four readers' aggregate
40-42 GB/s shows available bandwidth, but does not prove that one reader can reach
it. Retain a scheduling change only after a controlled Q8 gain, with Q1,
small-read and multi-threaded controls and byte/lifecycle checks.

The later shared sleeping whole-read queue also loses in a complete 54-window
same-build comparison: −11.30% single-reader Q8 and −49.62% four-reader throughput.
Its engine is removed; synchronous split copies remain. Full measurements and
limits are in [RAM scheduling](RAM_READ_SCHEDULING_20261010.md) and the
[consolidated findings](PERFORMANCE_FINDINGS.md).

## Raw disk reads and writes bypass the cache (by design)

What it means: the cache sits on the volume. A program that reads the physical disk
directly (`\\.\PhysicalDriveN`, some imaging and forensic tools) while a Fast cache
holds pending writes sees the disk without them; one that writes there leaves the
cache with stale copies. Health tools, SMART and SCSI/ATA queries are not affected
(they no longer reach the cache at all). Backup and imaging through shadow copies are
consistent (`volume-snapshot`). Proposed use: flush or pause the cache before such a
tool reads the raw disk, and never write raw to a disk under a cached volume.

## Fixed: programs on a cached C: read wrong data (0.4.148.1-0.4.162.1)

What you would see: with caching on for C:, programs started crashing
(`qcache.exe` in `coreclr.dll` and `System.Security.Cryptography.dll`, access
violations at the same code offsets in every new process). Found on 0.4.162.1 on
2026-09-27 during a saved-profile C: soak with the owner's 2 GiB Q: profile also
active. 11 of `qcache`'s 192 DLLs hashed wrong when read through the cache; the
files on disk were intact (all matched after caching was released and Windows
restarted), and nothing wrong was written back.

Why: since plan 56 (0.4.148.1) a page-in that missed the cache was kept as clean
cache blocks, copied from the request's own buffer. When Windows reads a group of
pages of a program or mapped file at once and some of them are already in memory,
it points those slots of the buffer at one shared scratch page (the "dummy
page"), which other reads overwrite at the same time. Those slots therefore hold
another block's data, and keeping them served that data to the next page-in of
the file. More simultaneous page-ins (0.4.158.1 added three parallel reader
threads) and heavier memory pressure made it frequent.

Fix (`4f28450`, completed in 0.4.166.1 by `233b501`): page-in misses are never
kept (as before plan 56; resident data still serves page-ins). An ordinary read
miss is kept only when its buffer repeats no physical page, since an application
can also map one page twice. Diagnostics V17 counts page-ins whose buffer
repeated a page: 1,654 in five pressure runs on C:, which confirms the cause.
Verified under Driver Verifier: a 10-cycle saved-C:-profile restart soak with a
new program-file check each cycle (plan 61, `system-paging/program-files-match-disk`),
and every Q: suite (tracker, P6).

## Fixed: a read miss was kept from the application's own buffer (up to 0.4.166.1; fixed in 0.4.169.1)

What could happen: when an unbuffered read missed the cache, the driver kept a
copy of the block taken from the application's buffer after the disk filled it.
If that application changed its buffer before the read completed (a bug, or on
purpose), the changed bytes were kept as the file's contents and other programs
reading that file received them. Reading the file was enough; no write access was
needed. Nothing reached the disk. Found by review on 2026-09-28; the new check
then reproduced it on 0.4.166.1 (all 16 MiB re-read held the other thread's bytes).

Fix (plan 62): a miss that will be kept is read from the disk into a new
driver-owned buffer, copied to the application from there, and kept from there.
The worker still serves other reads while it waits. Misses over 16 MiB, or when
that buffer cannot be allocated, are forwarded as before and not kept
(Diagnostics V17 `ReadFillsSkippedRepeatedPages` now counts those). The
`policies/read-miss-isolation` case overwrites the read buffer from another
thread during every miss and requires the re-read from RAM to match the file.

## Fixed: small writes slowed about 8x while write-back ran (up to 0.4.153.1)

Random 4 KiB writes at queue depth 32 ran at about 300,000/s until background
write-back started, then fell to about 35,000/s for as long as it ran (the
"random Q32 gap" in earlier plans). Only the configured number of drain threads
(parallelism, default 2) write back; the other drain threads idle. Each time an
idle one woke, it cleared the shared wake-up signal, so the next cached write set
it again and woke them to compete for the cache lock: about one wake-up per
write (180,000 in 5 s). Since 0.4.154.1 idle drain threads wait on a separate
stop signal and never touch the shared one; the same run keeps about 300,000/s.

## Fixed: shutdown deadlock on a paging-path usage notification (0.4.117.1-0.4.125.1)

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
- That read is queued behind the same worker, which is itself the thread blocked
  in the fault: the lower drivers run synchronously inside the worker's
  `IoCallDriver` (`OriginalIo+0x98`), so nothing progressed.

Memory pressure is what pages the ACPI handler out, hence the intermittency. The
same pattern could occur whenever Windows adds or removes a pagefile, hive or dump
path while C: routing is active. The crash lost that cycle's dirty Fast data as
expected; `chkdsk C: /scan` found no problems. Dumps and symbols are kept off the
VM (`QueueCache-Evidence/hang-20260926-cycle3`, SHA-256 `02D6A68E...5181`).

The first fix in 0.4.125.1 (`42b5ddd`) was insufficient: it serviced paging reads
in `OriginalIo`'s wait loop, but that loop is only reached after `IoCallDriver`
returns, and here the fault happens inside the call. Its 20/20 saved-profile soak
passed only because ACPI's handler stayed resident. With Driver Verifier (whose
IRQL checking trims pageable memory) the first restart hung again and Verifier
bugchecked 0xC4/0x115 ("shutdown did not finish"); the dump shows the identical
stack (`QueueCache-Evidence/verifier-20260926-c4`, SHA-256 `7F05D86B...564B`).
Second fix, 0.4.128.1 (`19b6192`): PnP and shutdown requests are forwarded from a
preallocated work item (`QcForwardOffWorker`) while the worker services queued
paging reads until the lower completion. Power stays inline because a paging disk
holds I/O until D0. Under the same Driver Verifier checks it passed 20 of 20
saved-profile restart cycles (94.8-133.9 MiB dirty, every byte matched, no
bugcheck). Other controls forwarded inline by the worker (media-
changing IOCTLs) could in principle hit the same pattern if a lower handler pages;
none has been observed.

## Fixed: bugcheck 0x7A while restarting with dirty C: data (0.4.111.1 and earlier)

After `IRP_MJ_SHUTDOWN` drained the cache, the driver marked itself suspended and
failed every later request with `STATUS_DEVICE_NOT_READY`. Windows still pages
during shutdown, so an exiting `dwm.exe` faulting in `win32kbase.sys` code
bugchecked with 0x7A (minidump `092526-7734-01.dmp`). The owned test file was
intact because the drain had finished. Since 0.4.117.1 suspension only blocks
re-enabling the cache; requests keep flowing to the disk.

## Application caching scope (T085)

Paging-marked writes are admitted when their originating file object is not a
paging file. Paging read misses are not kept (plan 56 kept application ones; since
plan 62 none are, because a clustered page-in's buffer can repeat the memory
manager's shared dummy page, see `DistinctPages`). Paging reads of cached data are
still served from RAM. So data that applications read through Windows' file cache
(buffered reads) enters QueueCache's read cache only when it was written or read
unbuffered; Windows' own file cache holds it otherwise. Remaining limit: NTFS
metadata/zero-fill write-back is admitted like any other write, so the cache
shares drain intervals with it.

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

The `volume-filter` branch now has a source implementation for whole-disk safe
eject and an ordered volume QUERY_REMOVE drain. A `qcache disk eject` request
first disables and drains every lettered data volume on the disk. This is still
experimental: GPT metadata counting and dedicated-adapter scope checks are
implemented. CI build 0.4.249.1 accepted Windows eject with 8 MiB of pending Fast
writes and observed disk absence. The live reconnect failed in Proxmox hotplug
handling; the maintained case finished RESTORATION_FAILED and remains unqualified.
External Windows eject routing, veto/cancel, surprise removal under in-flight I/O,
reconnect and FAT32/ReFS removal have not passed the maintained VM matrix. A
successful host build or an empty dirty counter must not be read as that proof.
Management transactions now serialize by physical-disk PnP identity, including
low-level CLI mutations. A slow eject preparation leaves other disks configurable.
The product transaction relies on cooperating management clients until Windows
begins QUERY_REMOVE; a driver prepare/cancel protocol remains open if native
volume-query routing proves insufficient. Old interfaces must be stopped during
qualification, as required by the runner preflight instructions.

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
unknown-origin requests keep ordered lower I/O; since plan 55 recognised paging-file
I/O bypasses the worker entirely. Since plan 56 application paging read misses are
retained. The following remain incomplete:

- bounded allocation-failure, cancellation and teardown races;
- oversized and quota-boundary requests under concurrency;
- exact cutoff semantics for concurrent explicit flush and live policy/resize;
- cold lower reads competing with drains and sustained capacity pressure;
- multi-disk memory pressure and starvation;
- all fault-injection points across every parallelism/retention combination.

Since plan 48 a lower IRP that cannot be allocated for a drain or barrier flush is
retried for about 5 s before it faults the cache; earlier builds faulted on the
first failure, so brief memory pressure could leave a cache faulted until Retry.

These are verification gaps, not permission to relax capacity backpressure,
ordering, failure propagation or explicit durability.

## TRIM is range-aware and verified on a VHDX

Since 2026-09-27 (after 0.4.139.1) any number of sector-aligned TRIM ranges is
handled without emptying the cache: in-flight writes are awaited, the TRIM is
forwarded, and on success cached data inside the ranges is dropped. Unwritten
data for trimmed sectors is discarded rather than written (it would overwrite
space the file system has freed); a partly trimmed unwritten block keeps its
other sectors. Only unknown flags or malformed input use the conservative
write-out-and-invalidate path. A failed TRIM keeps all cached data.

Verified 2026-09-28 on the lab VHDX (`trim-cache`, 0.4.211.1-0.4.219.1 under Driver
Verifier): TRIM of pending data dropped exactly the trimmed 1 MiB and drained
nothing; TRIM of drained, retained data released 1 MiB of clean copies; a TRIM
during an in-flight drain completed with every pending byte either written or
dropped (at most the trimmed range); all untrimmed guards and later rewrites read
back exact from the disk. The VM's own VirtIO disks still cannot TRIM (below).

### Why the test VM sends no TRIM (investigated 2026-09-28)

What you see: `Optimize-Volume -ReTrim` and `defrag /L` fail with "Incorrect
function (0x80070001)", file-level TRIM (`FSCTL_FILE_LEVEL_TRIM`) fails with Win32
326, and deleting a 64 MiB file on Q: produced no TRIM request at the driver.

Why: this is a Windows 11 / VirtIO SCSI problem, not QueueCache. Proxmox already
has Discard and SSD emulation on for both disks, and the virtual disk advertises
UNMAP (read-only SCSI INQUIRY of C:'s disk: VPD B2 UNMAP supported, thin
provisioning; VPD B0 unmap granularity 8 sectors = 4 KiB, the NTFS cluster size).
Since the May 2026 Windows 11 update, Windows then asks such a disk for GET LBA
STATUS (SCSI opcode 0x9E); the Storport log shows the disk rejecting it
(sense 0x5/0x24), and Windows gives up on TRIM. The same failure is reported
publicly for Windows 11 guests with the VirtIO SCSI driver (virtio-win issue
#1574, 0.1.285 on Proxmox 9.2); a fix exists only as an unmerged change for the
other VirtIO block driver (PR #1653). Evidence that QueueCache is not involved:
retrim fails identically on C:, which has no cache, and succeeds on a temporary
VHDX disk that has the same QueueCache filter attached.

Done: TRIM handling is verified on a VHDX (`trim-cache`, above). On 2026-09-29 a
32 GiB SATA test disk with Discard and SSD emulation was added to the VM (volume T:).
Windows trims it (`Optimize-Volume -ReTrim`: 29.9 GB trimmed), unlike the VirtIO
disks. It reports "not thinly provisioned", so Windows offers no file-level TRIM there
(Win32 326) and `trim-cache` SKIPs; retrim through a Fast cache holding 112 MiB of
pending data sent its TRIM through the cache, discarded none of the pending data, and
the file read back exact from the disk after flushing and removing the cache (0.4.219.1).
`volumes` on T: passed registration, raw disk commands and snapshot. Q: itself trims
again once Microsoft or virtio-win ship a fix.

## Fixed: raw disk commands on a cached disk (volume filter)

Health and SMART tools that send raw commands to the disk (SCSI/ATA pass-through)
used to be refused on a cached disk, and SMART reads drained and emptied the cache,
because the filter sat on the disk. The cache now sits on the volume, so these
commands go to the disk's own stack. `volumes/volume-raw-disk-commands` sends a device
descriptor query, a geometry query and a SCSI INQUIRY to the physical disk while 16
MiB are pending and 8 MiB clean: nothing is drained, flushed or evicted (verified on
the VM's VirtIO disk and on a VHDX, 0.4.219.1).

## Configuration changes apply completely or restore the previous settings

Apply drains and disables before changing the preset, budget and policy. If the
drain fails nothing else is touched and the change is reported as not applied.
If a later step fails (most likely the new memory allocation, because the driver
frees the old cache first), the previous preset, options, budget and on/off state
are put back and the error says "previous settings restored". If even that fails,
the error states the cache's resulting state. Saved profiles are updated only after
a successful apply and are identity/size checked during restore. Host contract
tests cover each failure point with a fake device; the rollback has not been
forced on the VM.

## Performance acceptance is incomplete

Historical CrystalDiskMark-shaped runs on 0.4.162.1 and 0.4.166.1 are in
[WRITE_PERFORMANCE_TRAJECTORY.md](WRITE_PERFORMANCE_TRAJECTORY.md). The current
[72-case write matrix](WRITE_PERFORMANCE_20261010.md) completed on 0.4.476.1
before PR #8 merged, using the same DiskSpd binary/hash as the earlier 0.4.426.1
baseline. Random cached writes improved 1–7% and sequential Q1 improved 29–32%;
the sequential Q8 difference overlaps the earlier ranges and accompanies a
slower uncached disk baseline, so its cause remains unproved.

The maintained `cache-concurrency`, `cache-sustained`, `cache-map-cost` and
`cache-recall` suites now have concurrent-stream, sustained mixed-use, map-polling
and read-recovery evidence. Four-reader RAM references are also measured; their
larger aggregate bandwidth is not a promised single-reader score. The subsequent
[RAM scheduling comparison](RAM_READ_SCHEDULING_20261010.md) rejected and removed
the shared sleeping queue because it slowed large reads. The
[caller cooldown comparison](CALLER_BACKOFF_20261010.md) likewise shows a large-read
regression with cooldown zero, despite a fitting NTFS mixed-Q8 gain. Keep default
256 while the remaining filesystem/priority investigations are completed.

These are scoped comparisons rather than universal performance acceptance.
Comparisons must keep the same DiskSpd binary/hash, budget and repetitions.
`MEASURED` means a sample was collected, not that it passed a performance
requirement. Lifetime and enclosing-process counters must not be presented as
exact score-window counters.

## UI and telemetry limitations

The desktop shows live state and bounded history, but interval/lifetime labeling,
sample-staleness presentation and complete trigger/wait visibility remain partial.
Unavailable fields must stay unavailable rather than becoming zero. Closing the UI
does not stop caching.

## Installer and compatibility debt

The `qcachelab` service/binary identity, `LabAllowedDriverKey` registry value and
`labWriteCache` package metadata are retained for upgrade compatibility. They do
not denote a second driver edition. A coordinated identity/schema migration belongs
to installer acceptance work.

An older build cannot be installed over a newer one once their RAM-disk protocols
differ: 0.4.426.1's installer refuses over 0.4.441.1 and later because its update
check cannot read the newer RAM-disk driver's state ("Invalid native RAM disk
identity, geometry or lifetime state."). That matches the no-migration policy; for
old/new comparisons, add a switch for the old behaviour to the current build
(as `developer driver read-recall` and `copy-flags` do) instead of downgrading. Uninstall retains kernel service/binaries when they
may still be needed for post-reboot recovery.

For task order, exact evidence and acceptance boundaries, use the
[private-alpha handover](PRIVATE_ALPHA_IMPLEMENTATION_HANDOVER.md) and
[RAM-first tracker](RAM_FIRST_IMPLEMENTATION_TRACKER.md).
