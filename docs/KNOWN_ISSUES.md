# Known issues and stability

What is known to be limited, not yet proven, or fixed in QueueCache, and the
correctness checks behind it. The overview lists every issue; then come the open
issues, what is not yet proven, a fix with a remaining gap, the verification record
and the fixed issues. Verification
happens on one recoverable 4-vCPU Windows 11 VM on Ceph-backed storage, including
under Driver Verifier; that is not production qualification. Each result names the
build it ran on; everything else describes the latest build. Do not report a fixed
issue as a bug again without a reproduction on the latest build. Performance results and
decisions are in [performance](PERFORMANCE.md); how things are
tested is in [testing](TESTING.md).

## Overview

✅ fixed, with the first build that has the fix. ❌ open: by design, not planned, or
still to do. Open items link to their details.

| Issue | Fixed | Build or status |
|---|:-:|---|
| [Fast mode: a crash loses pending writes and can damage the file system](#fast-mode-loses-pending-writes-filesystem-damage-remains-an-open-safety-issue) | ❌ | Open safety issue, [#13](https://github.com/devedse/QueueCache/issues/13) |
| [The driver and installer are test-signed](#the-driver-is-test-signed) | ❌ | Production signing not done |
| [Raw disk reads and writes bypass the cache](#raw-disk-reads-and-writes-bypass-the-cache-by-design) | ❌ | By design |
| [Strict mode can stall writes after a burst of random writes](#strict-mode-can-stall-writes-after-a-burst-of-random-writes-performance) | ❌ | Open, [#16](https://github.com/devedse/QueueCache/issues/16): since 0.4.810.1 a write-through write no longer drains the cache; a flush after a random burst still does |
| [Low-priority programs read cached data up to 30% slower](#low-priority-programs-read-cached-data-slower-performance) | ❌ | Not planned (Windows scheduling) |
| [ReFS: fewer requests take the fast caller-thread path](#refs-fewer-requests-take-the-fast-caller-thread-path-performance) | ❌ | Not planned (measured trade-off) |
| [A re-read file briefly takes space from a random workload](#a-re-read-file-briefly-takes-space-from-a-random-workload-performance) | ❌ | By design (most recent read wins) |
| [A request only partly in RAM is read entirely from disk](#a-request-only-partly-in-ram-is-read-entirely-from-disk-performance) | ❌ | Not planned (under 1% of disk traffic) |
| [An older build cannot be installed over a newer one](#an-older-build-cannot-be-installed-over-a-newer-one) | ❌ | By design (no migration) |
| [Desktop telemetry is partial](#desktop-telemetry-is-partial) | ❌ | Open |
| [Power loss not re-tested on recent builds](#not-yet-proven) | ❌ | Last tested on 0.4.604.1 |
| [Hibernate, Fast Startup, crash dumps and surprise removal](#not-yet-proven) | ❌ | Not in the maintained matrix; sleep (S3) passed on one real PC |
| [Cached system disk (C:): full boot, paging, hibernation and dump acceptance](#not-yet-proven) | ❌ | Restart soaks on the VM; restart, sleep and start-with-Windows checks on one real PC |
| [Strict and foreground cost of the new drain default](#not-yet-proven) | ❌ | Not measured in isolation |
| [Asynchronous TRIM: the fence's holding path is unexercised](#trim-no-longer-waits-for-unrelated-drain-writes) | ❌ | NTFS never reaches it |
| [Faults during the busy window, every fault point at every parallelism, Windows memory pressure](#not-yet-proven) | ❌ | Open |
| [Recovery of interrupted suites beyond `read-tail-decision`](#not-yet-proven) | ❌ | Shared restore path, host-tested |
| [Hard disks, SATA and NVMe SSDs](#not-yet-proven) | ❌ | One Ceph-backed VM measured; one iSCSI-booted PC ran functional checks |
| [Test-VM fixture margins (RAM-disk headroom, fragmented lab volume)](#not-yet-proven) | ❌ | Test harness only |
| Files read through Windows' file cache (Explorer copies, most programs) and mapped files never entered the read cache ([#19](https://github.com/devedse/QueueCache/issues/19)) | ✅ | 0.4.810.1 (page-ins of file data are kept from a driver-owned copy of the disk data; a 1 GiB re-read after Windows' standby memory is purged: 275 → 2,254 MiB/s) |
| Detaching a virtual disk (VHD/VHDX) lost a Fast cache's pending writes: Windows removes the volume without asking first ([#21](https://github.com/devedse/QueueCache/issues/21)) | ✅ | 0.4.803.1 (written out when the file system releases the volume; `volumes/volume-detach`, plan 172) |
| A saved cache, or a RAM disk that starts with Windows, stayed off after a restart when RAM was briefly short ([#12](https://github.com/devedse/QueueCache/issues/12)) | ✅ | 0.4.803.1 (retried every 15 s for 10 minutes; the app and `qcache volume list` show a cache waiting for RAM or not restored) |
| A large image in RAM on a slow disk was not saved at shutdown: the service stopped trying after 110 s, without telling anyone ([#22](https://github.com/devedse/QueueCache/issues/22)) | ✅ | 0.4.803.1 (no time limit while the save makes progress; the app shows the expected time before and the outcome after the restart) |
| Every Windows shutdown waited for the virtual-disk service (Service Control Manager event 7043), and image-in-RAM saves at shutdown ran only in the short final phase: the service answered the wrong control code for Windows' early shutdown notice | ✅ | 0.4.796.1 ([#23](https://github.com/devedse/QueueCache/issues/23)) |
| QueueCache's System log entry for a removed volume (`qcachelab` event 157) had no text, and was written for every removed volume, cached or not | ✅ | 0.4.796.1: only cached volumes; event 1 (writes were pending, with the byte count) or 2 (nothing pending), with text |
| A refused image or evidence folder's error did not say how to fix it | ✅ | 0.4.796.1 (names the `icacls` command; never for a drive root) |
| Setup failed, and the app listed no volumes, on a PC whose Storage Management provider lists no disks (seen on an iSCSI-booted Windows 11) | ✅ | Setup 0.4.781.1; app, CLI and cache targets 0.4.784.1 (disks and volumes are read natively, without `Get-Disk`/`Get-Volume`) |
| A failed setup could leave the new boot-start filter registered without the rest of the installation | ✅ | 0.4.782.1 (the filter is registered last; the previous registration is restored on failure; forced on the VM with 0.4.788.1) |
| Every install rewrote the desktop update files and shortcut, moving their icons | ✅ | 0.4.782.1 (written only when missing or changed) |
| [Writing new files, including the drain to disk, was too slow (#15)](PERFORMANCE.md#drain-15-status) | ✅ | Native 0.4.776.1: Eager 2 MiB × 8, read-aware limit 2, asynchronous TRIM for new caches |
| [Each TRIM waited for every drain write in flight, stalling writes for seconds](#trim-no-longer-waits-for-unrelated-drain-writes) | ✅ | Native 0.4.766.1 (overlapping writes only); asynchronous since 0.4.771.1, default with 0.4.776.1 |
| Ordinary cache misses reached the disk one at a time, and requeued service reads could repeatedly delay older misses (#14) | ✅ | 0.4.687.1: bounded misses overlap and requeueing keeps arrival order |
| New cache tasks drained in small 256 KiB batches with two writes in flight | ✅ | 0.4.687.1 (1024 KiB × 4); 2 MiB × 8 Eager since native 0.4.776.1. Saved profiles keep their settings |
| With "keep written data" off, re-reading a large file (reads over 1 MiB) while it drained could return an older 256 KiB piece; the disk was always right | ✅ | 0.4.601.1 (found by `stress-races`) |
| One program reading a RAM disk topped out at about 26 GB/s whatever its queue depth | ✅ | 0.4.583.1: queued reads overlap, 47,583 MB/s ([findings](PERFORMANCE.md#ram-disk-asynchronous-split-reads-accepted-pr-9)) |
| Sequential cached reads up to 45% slower after drop-clean and refill | ✅ | 0.4.431.1 chunk allocator; 0.4.434.1 prefetch and coalesced copies |
| A read miss was kept from the application's own buffer, so a program changing its buffer mid-read changed what others read | ✅ | 0.4.169.1 (driver-owned miss buffer) |
| Programs on a cached C: crashed: page-in misses were kept from a buffer that can repeat a shared page (0.4.148.1–0.4.162.1) | ✅ | 0.4.166.1 |
| Random 4K Q32 writes fell from about 300,000/s to 35,000/s while write-back ran | ✅ | 0.4.154.1 |
| Restart with a C: profile hung: a paging read queued behind the worker forwarding a shutdown request (0.4.117.1–0.4.125.1) | ✅ | 0.4.128.1 |
| Bugcheck 0x7A restarting with dirty C: data: requests failed after the shutdown drain (0.4.111.1 and earlier) | ✅ | 0.4.117.1 |
| Bugcheck 0x7E (divide by zero) after a cache release (0.4.99.1–0.4.104.1) | ✅ | The build after 0.4.104.1 |
| Processes faulted after booting with a C: Fast profile and fixed pagefile (0.4.83.1) | ✅ | 0.4.87.1 (paging I/O bypasses RAM caching) |
| PC reset during boot with C:'s volume cached; volume extend failed; shadow copies froze the PC or timed out; a cache on a raw volume could not be removed; a test left Q: at 256 MiB | ✅ | Volume filter, 2026-09-28 |
| SMART and pass-through commands were refused or drained the cache | ✅ | Volume filter (commands no longer reach the cache) |
| One failed lower allocation during a drain faulted the cache | ✅ | Plan 48 (retried for about 5 s) |

## Open issues

### Fast mode loses pending writes; filesystem damage remains an open safety issue

In Fast mode a save can finish when the data is in RAM, including application
flushes and write-through requests; QueueCache writes it to disk later. A crash
can lose those writes and damage the filesystem. The requested goal in
[#13](https://github.com/devedse/QueueCache/issues/13) is to allow loss of pending
writes while keeping NTFS healthy and untouched, durably checkpointed files
intact. Filesystem damage is an unresolved safety issue, not the desired
volatile-cache contract. The power-cut evidence is historical (0.4.604.1), not a
qualification of the latest build. Another write-back cache tested the
same way also damaged NTFS when cut during its write-out; one clean early cut does
not establish a general guarantee.
[Evidence and scope](COMPARISON.md#power-cut-while-the-cache-is-writing).

Strict mode and explicit QueueCache Flush/Pause/Remove keep their disk boundary;
an ordinary application flush in Fast mode does not provide that boundary.
FAT32/exFAT have no journal and cannot inherit an NTFS-specific safety claim.
A RAM disk is empty after a crash, and an image in RAM is back at its last save.

### The driver is test-signed

The driver and installer are test-signed development builds; production signing
and certification are not done. Windows loads the driver only with test signing on
and Secure Boot off ([getting started](../README.md#getting-started)).

### Raw disk reads and writes bypass the cache (by design)

The cache sits on the volume. A program that reads the physical disk directly
(`\\.\PhysicalDriveN`, some imaging tools) while a Fast cache holds pending writes
sees the disk without them; one that writes there leaves the cache with stale
copies. Health and SMART tools are unaffected, and backups through shadow copies
are consistent. Flush or pause the cache before a tool reads the raw disk, and
never write raw to a disk under a cached volume.

### Strict mode can stall writes after a burst of random writes (performance)

Strict honors each application flush by writing out the whole dirty cache and
flushing the lower disk; later writes wait meanwhile. After a burst of random 4K
writes the disk writes that backlog at a few MB/s, so a flush (NTFS sends them on
its own) can stop writing for a long time: in `compare-cache` one 10 s random-write
window completed nothing on 0.4.803.1 and on 0.4.810.1. Since 0.4.810.1 a write-through write (NTFS's
journal) goes straight to the disk after only older versions of its own blocks. The
drain defaults of 0.4.776.1 made most file work fast again (40,000 files: create
34 s, delete 23 s, against 213/264 s on 0.4.604.1). A dirty cap sized from the
measured drain rate was tried and removed: it throttled sequential writes and did
not bound the random case ([results](PERFORMANCE.md#strict-16)). Bounding the backlog
by estimated drain time remains open in [issue #16](https://github.com/devedse/QueueCache/issues/16).

### Low-priority programs read cached data slower (performance)

A program started below normal priority (Task Scheduler's default) read 1 MiB Q8
cached data 30% slower than at normal priority on the test VM; with Windows free
to place its thread (unbound affinity) the loss was 3%. One processor stayed busy
while others idled, so it is a scheduling effect. Data is unaffected. Benchmark at
normal priority; a scheduling change would need a real-world case first.

### ReFS: fewer requests take the fast caller-thread path (performance)

ReFS often splits one program request into two at once; the fast path needs
exactly one in flight, so the second sends the next 256 requests through the
ordered worker (64% instead of about 99% on the fast path in a 64 KiB random test).
Data is unaffected. A selective caller policy was considered and not built: a
shorter cooldown gained at most 5.3% (NTFS) and 3.8% (ReFS) on mixed Q8 and lost
21% on large NTFS reads.

### A re-read file briefly takes space from a random workload (performance)

Read recall gives cache space to whatever was read most recently. A random
workload that resumes right after another file was re-read has about 7 percentage
points fewer RAM hits for roughly a minute. The re-read file gets RAM speed in
return (10-230 times faster). `qcache developer driver read-recall <volume> 0`
restores the earlier rule for comparison.

### A request only partly in RAM is read entirely from disk (performance)

If any 4 KiB block of a request is missing, the whole request is read from disk
and the cached blocks are copied over it. In measured churn this re-read less than
1% of the disk traffic, so reading only the missing parts is not worth its
complexity yet.

### An older build cannot be installed over a newer one

Once the RAM-disk protocol changed (0.4.441.1), an older installer's update check
cannot read the newer driver's state and refuses. That matches the no-migration
policy; old/new comparisons use a runtime switch in the current build instead of
downgrading. The `qcachelab` service name and `labWriteCache` package key are kept
for upgrades and do not mean a separate driver edition.

### Desktop telemetry is partial

The app shows live state and bounded history, but interval versus lifetime labels,
stale-sample marking and every trigger/wait reason are not all shown. Unavailable
values stay unavailable rather than showing zero. Closing the app never stops a
cache or a disk.

## Not yet proven

- **Recovery coverage:** copy-flag recovery passed an interrupted
  `read-tail-decision` VM smoke, restoring flags 15 and the original profile.
  Other flag-mutating suites share the tested restore path and host contracts;
  they were not individually interrupted on the VM. Older recovery snapshots
  without original copy-flag evidence cannot be reported fully restored.
- **Foreground interference and Strict impact:** neither has a valid isolated
  comparison for the new drain defaults. The removed overlap fixture drained its
  large seed after the default dirty-age trigger; the removed Strict fixture saw
  unattributed application/write-through barriers drain its seed. The driver
  refuses option changes while enabled or dirty, so seeding under held-off
  settings and switching afterwards is not supported. This leaves the known
  whole-cache Strict barrier cost (#16) open, rather than proving a regression of
  the new defaults. Failed runs and counters remain in [findings](PERFORMANCE.md).
- **Power-loss evidence:** no power-cut test has run since 0.4.604.1. Those and the
  competitor results remain historical and do not establish a newer build's crash
  behavior.
- **Removal and power:** Windows eject with pending writes passes, and so does
  detaching a virtual disk since 0.4.803.1 (`volumes/volume-detach`); live reconnect
  failed in Proxmox hotplug and stays unqualified. Surprise removal under load,
  FAT32/ReFS removal, sleep, hibernate, Fast Startup and crash-dump handling have
  not passed the maintained matrix. Sleep (S3) passed on one real PC (Windows 11
  booted from iSCSI, 0.4.784.1, 2026-10-22). Five `managed-lifecycle` fixtures kept
  their creations, states and bytes. A Fast cache holding 512 MiB of unwritten data
  kept it through 3 minutes of sleep, wrote it out on flush and read back correctly
  from the disk. Hibernate and Fast Startup could not run on that PC: its iSCSI boot
  has no crash-dump driver (`volmgr` 45/46), and hibernation writes through that driver.
- **System disk:** a saved C: profile with a fixed pagefile passed restart soaks
  (paging I/O bypasses the RAM cache), but full boot, paging, hibernation and dump
  acceptance is open. A pre-alpha large-image crash on the old engine left no dump
  and was never reproduced. On the iSCSI-booted PC above, a 2 GiB Fast cache on C:
  passed normal use (7.4 GB written and drained), a restart, a sleep and a saved
  profile across restarts that included Windows updates, with 2,000 hashed files
  unchanged throughout. A short Strict run had no errors. That PC cannot write a
  crash dump, so a crash there leaves no evidence.
- **Lab fixture margins (plans 165–171):** on the 16 GiB test VM, `stress-races`'
  2 GiB RAM-disk churn has about 200 MiB of headroom margin. It retries creations
  the headroom guard refuses (8 of 19 attempts in one run), and `ram-read-reference`
  waits up to 60 s for headroom before each disk (plan 168). The likely cause of the
  frequent refusals: on 2026-10-22 the Proxmox host auto-ballooned the VM to
  8.0–12.6 of its 16 GiB. Campaign preflight and every clean-cache capture wait,
  bounded, for a lab image's writes to drain on their own (plans 169/171).
  `parallel-copies` and `foreground-background` refuse a file whose extent
  boundary would split one of their requests, and since plan 170 reserve their whole
  allocation first. Before that, a run's own small files could leave a gap that
  split them on every attempt (release attempt 16).
- **Edge cases:** `stress-races` now covers flush, live resize, teardown,
  drop-clean, slowed and transiently failing lower writes, refused and rolled-back
  settings, Strict switches, cancelled requests, 4 MiB requests and RAM disks
  created and removed, all at once on two volumes under concurrent I/O, plus a
  failed lower write and Retry at drain parallelism 1, 2 and 4 (with and without
  Driver Verifier). Still open: faults injected during that busy window rather
  than after it, every fault-injection point at every parallelism, and memory
  pressure from Windows itself rather than from QueueCache's own reservation. A
  lower request that cannot be allocated is retried for about 5 s before the
  cache faults.
- **Managed disks:** sleep passed once on a real PC (above); hibernate, Fast
  Startup and a crash during a checkpoint commit are not covered. Shutdown saves of
  136–151 s (a 2 GiB image on a deliberately slowed host volume) committed on the VM
  (0.4.803.1); longer saves, Windows' 3-hour limit and what a real PC's shutdown screen
  shows meanwhile are not covered.
- **Event `partmgr` 58 while saving an image in RAM (explained, harmless):** "the
  disk signature of disk X is equal to the disk signature of disk Y". A checkpoint is
  verified by attaching the new image read-only while the RAM disk it was copied
  from, with the same GPT disk ID, is online. Windows keeps the copy offline and logs
  58; the verify then reads it back sector by sector, so a passing save proves the
  image unchanged.
- **Hardware:** everything above was measured on one Ceph-backed VM. Hard disks,
  SATA and NVMe SSDs were not measured. An iSCSI-booted PC ran the app, a RAM disk
  and the functional checks above, not a performance qualification. Performance
  rates on the VM also depend on host load: RAM-path rates on 2026-10-22 were 35–73%
  below October 14 on both the final build and 0.4.687.1
  ([findings](PERFORMANCE.md#final-164)).
- **Setup rollback:** forced once on the VM (0.4.788.1, 2026-10-22). With one
  injected failure after activation, setup exited 1 and restored the filter list,
  start type, driver path and group exactly. The steps before activation (RAM
  provider, broker, sign-in task, PATH) stay installed by design; rerunning setup
  completes them.

Do not use automatic reboot, driver deletion or discarding dirty data as failure
recovery: a faulted cache must be inspected and recovered explicitly.

## Fixed, with a remaining verification gap

### TRIM no longer waits for unrelated drain writes

Before native 0.4.766.1 a TRIM ran on the ordered request path and waited until
every lower write already issued had finished, whether or not it overlapped the
trimmed ranges; later writes waited behind it. Under Eager draining each TRIM cost
about one round trip of the whole drain window (up to 16 MiB at 2 MiB × 8). In
plan 158, 144 and 272 TRIMs stretched a new 1 GiB file's application time from
about 0.16 s to 6.7 s and 25.1 s, so fitting Fast writes waited for lower I/O,
contrary to the RAM-first goal ([evidence](PERFORMANCE.md#matched-158)).
0.4.687.1 and earlier keep that behavior.

- **0.4.766.1 (plans 159/160):** a TRIM waits only for in-flight writes that
  overlap it. Under Driver Verifier a TRIM outside the in-flight batches returned in
  2 ms while 3 MiB stayed in flight; an overlapping one waited 2,008 ms for its
  write ([evidence](PERFORMANCE.md#trim-160)).
- **0.4.771.1 (plans 162/163), TRIM mode 3:** small TRIMs use an index lookup, and
  an offload thread sends the lower TRIM while the ordered request path continues.
  A fence keeps newer versions of the trimmed ranges from draining before the lower
  TRIM completes, and forwarded writes to those ranges wait for it. Eager
  mass-delete cases fell from 2.52–2.58 to 2.01 s. Default for new caches since
  plan 164.
- **0.4.776.1:** the caller-thread path stays on while a slow lower TRIM is
  outstanding; before, the lab VHDX's TRIMs switched it off
  ([results](PERFORMANCE.md#final-164)).

Under Driver Verifier, `trim-cache` passed 12/12 and the focused campaign 98/98
with mode 3 on. **Verification gap:** the fence's holding path and the forwarded-write wait have
not been reached. NTFS keeps a file's writes behind that file's outstanding
file-level TRIM, so a rewrite always arrived after the TRIM. Reaching the fence
needs a writer outside that ordering, such as raw volume writes, which the
verifier never performs. `trim-cache/async-eager-ordering` reports which path
occurred.
[Evidence](PERFORMANCE.md#prio-162).

The test VM's disk now accepts TRIM. On 2026-10-18 the hypervisor counted about
891,000 successful unmap operations on it, none failed, and QueueCache's TRIM
counter rose in 32 of 132 cases. That counter only counts TRIMs the lower disk
accepted. The earlier VirtIO rejection (virtio-win issue #1574) therefore no
longer applies to this VM; when that changed was not recorded. TRIM correctness
is verified on a VHDX (`trim-cache`): trimmed pending data is dropped, not
written, and everything else reads back exactly.

## Verification record

Correctness checks and their evidence. Performance evidence is in [performance](PERFORMANCE.md).

### Correctness qualification

**Issues #14 / #15, plan 126:** signed 0.4.675.1 completed the focused Driver
Verifier campaign `QueueCache-Campaign-20261013-175418-7aa42644d0cc4a90912f7fb85199ff9c`:
six phases, 98 correctness checks, all PASS, restoration RESTORED. This includes
12 rounds of 16 overlapping cold/partial readers, exact byte checks and a
post-drain/drop-clean check for leaked pins. The 304-second stress window checked
168,502 reads on V: and 224,676 on W:, cancelled 185,091 and 243,829 overlapped
requests, exercised every chaos action at least seven times per volume, and
created and removed 23 RAM disks. Fault and Retry passed at drain parallelism
1, 2 and 4; the shared reservation returned exactly to its starting value.
The native filter hash was `4EA5DCACCFFC09DB362C6BB5361764676D5090300DFE0737777142812549F744`,
the RAM provider hash `A5510BEB87D3190739021A3A4D99BD931259831F795BED7DF86E2064415D2905`;
Verifier flags `0x209bb` targeted both. Final performance qualification on
0.4.687.1 was completed under plan 132; this paragraph records the older plan-126
campaign only.

The first non-Verifier stress attempt on 0.4.677.1 passed the 302-second busy
window but stopped at the first Retry check with 8 KiB dirty. Between the faulted
and retried snapshots, 16 KiB of new writes were admitted and 56 MiB plus 8 KiB
drained; the original dirty set was 56 MiB. The fixture had not flushed lazy
filesystem metadata around its closed handles. Plan 128 adds those filesystem
flushes and a retained-byte drainage check, while preserving the zero-dirty,
zero-in-flight and stable-identity requirements. The failed attempt is retained
as incomplete (`20261013-181833-229f91cc19354cee801dd03e63e11da1`); restoration
succeeded. The corrected plan-128 fixture completed on signed 0.4.679.1
(`QueueCache-Campaign-20261013-225440-cf15f15164264cbcb2b63c3886f8b2c8`), all five
checks PASS, restoration RESTORED. Its 307-second busy window checked 380,986
reads on V: and 603,948 on W:, exercised every chaos action at least three times
per volume and created and removed 13 RAM disks. At drain parallelism 1, 2 and 4,
each retry drained at least the original captured dirty bytes and left zero dirty
and in-flight bytes; every region then matched from disk after cache release.
The shared reservation returned exactly to 2 GiB. The loaded filter hash was
`71BB75DC99CBF6B138AC30F8448961CC17711C76138AC8D68F8E16623EAA2955`, provider
`5236851804D9A016017C7B4145865DB8271B7E2BDC038BEB82FEBCA924083A4A`.

**Plan 120 stress suite.** `stress-races` runs both lab NTFS volumes at once:
writers of 4 KiB, 64 KiB, 1 MiB and 4 MiB blocks and an overlapped writer/reader
whose requests are cancelled at random, every read checked per 4 KiB sector
against the versions written, while a chaos actor per volume flushes, resizes,
tears down, drops clean data, slows the disk, injects transient allocation
failures, applies refused and rolled-back settings and switches to Strict, and
owned 2 GiB RAM disks are created and removed against the same reservation. It
then faults each drain parallelism (1, 2, 4) with dirty data, requires reads to
fail rather than return data, retries, and reads every byte back from disk.

Its first long runs found one real driver bug and three of its own: (1) **stale
cached data after large partly cached reads** (fixed in plan 120, see
[known issues](KNOWN_ISSUES.md#fixed-issues)): with retained writes off, a read
over 1 MiB fetched the range from disk while the dirty version of some blocks
finished draining, and kept those blocks from the older disk bytes. Under Driver
Verifier this gave 38 stale reads in 90 s, always whole 256 KiB drain batches of
an older version in 4 MiB blocks, with no unwritten (poisoned) sectors and the
disk verified correct; it reproduced with copy flags 3, so it predates the
bookkeeping change. After the fix, two 300 s busy windows under Verifier checked
about 390,000 reads with no stale data. (2) A RAM churn disk was stopped with a
generation read before its oracle was written. (3) The "refused budget" request
was sized from the free reservation, which a concurrent RAM disk removal could
enlarge, so the driver correctly accepted it. (4) Weighted random actions left
rare ones out of slow Verifier windows; actions now run in shuffled rounds. Wiring
it into campaigns also exposed three runner gaps, now fixed: restoration accepted
deliberately raised lab-fault error counts only for `ordering-faults` (and
`verify-recover` never did); later phases compared against the preflight baseline
instead of the one updated after such errors; and the final capture of the backing
volume could catch the lab image's late writes right after its drain.

**Final Driver Verifier run:** the focused campaign with `stress-races`
(`QueueCache-Campaign-20261011-214930-07f2ed10a5cf42978fbf1cc002c0db35`, signed
0.4.601.1 drivers with the stale-read fix, runner from the final source) completed
**6/6 phases, restored**: partial reads, paging coherence, policies, pressure,
`stress-races` and ordering faults all PASS. Its busy window ran 312 s with
254,894 (W:) and 182,739 (V:) verified reads, 268,875 and 198,640 cancelled
overlapped requests, every chaos action at least three times per volume and 11
RAM disks created and removed; fault and Retry passed at drain parallelism 1, 2
and 4, and the shared reservation returned exactly to its start. Later builds
change only managed code. Without Verifier on the final signed 0.4.604.1
(`QueueCache-Campaign-20261011-221957-8b19fe05c36b4caabb02c12e5149f237`, COMPLETED,
restored) the 306 s window checked 431,437 (W:) and 342,337 (V:) reads, cancelled
362,277 and 308,174 overlapped requests, ran every chaos action at least seven
times per volume and churned 23 RAM disks, with every check passing.

Current: the final `release-performance` regression on 0.4.583.1 passed all 118
retained correctness sub-checks (see the accepted change above). Earlier, the
signed 0.4.514.1 native driver and managed `1788970` preview completed
these maintained plan-113 scenarios on the owned NTFS lab V:. Each is an
independent run with one outer scenario, not a combined performance matrix.
Every inner check is PASS; no check was skipped. Raw reports, worker replies,
native hashes, ownership/exits, control/restoration traces and exact original
settings were inspected. Each has four successful owned-process exit records.

| Suite | Inner checks | Exact run ID suffix | Runtime UTC |
|---|---:|---|---|
| `partial-read-accounting` | 10 | `20261010-081109-aad11964b4d8470e9573e64f19b7787a` | 08:11:09–08:11:15 |
| `paging-coherence` | 8 | `20261010-081454-87a8b7c777be4ea19ccdc2c653758b83` | 08:14:54–08:15:07 |
| `policies` | 42 | `20261010-081712-d0b3498f7f144372871f769fc7c0ec91` | 08:17:12–08:18:52 |
| `pressure` | 13 | `20261010-082056-811b8c23b4c4417088273d84fdf83686` | 08:20:56–08:21:42 |
| `ordering-faults` | 8 | `20261010-082430-0afa9979205a4ea6b2988c9ad7458685` | 08:24:30–08:24:54 |

Coverage includes partial sectors, mapped/cached coherence, observed and gated
old-write ordering, parallel pinned copies, zero-lower-I/O fitting admission,
retention/policy variants, capacity backpressure, failed/short drains, cancellation,
allocation retry/exhaustion, release under load and paging-write failures. The
fault suite deliberately increments the lab error counter by three; final state
is non-faulted, with last error zero. Other suites have zero errors. All restore
disabled/zero-budget state, original options, no pending bytes and backoff 256.
This qualifies retained behavior at default 256; it does not qualify a new
request-size policy or the removed queue's lifecycle paths.
Immutable five-run raw archive SHA-256:
`3E475F93750E588D0A172DF09F4F5C991384B77027D763AB9CA642E789900DFD`.

Final cleanup detached both owned lab VHDX files and resumed Q: runtime-only on
its original active 2 GiB Fast/Idle configuration, with no errors or pending bytes.
Saved profile text matches the before capture exactly; the installed tray is
running again. Private evidence and lab files remain preserved.

### Recorded verification time

These selected completed runs total approximately **8 hours 38 minutes**. This
is measured run time, not a reconstruction of all engineering work or every
earlier investigation. Installation/restarts, CI, coding, analysis, incomplete
attempts and inactive conversation gaps add time. The NTFS/ReFS gap from 04:54
to 06:55 was not two hours spent executing that benchmark.

| Completed run | Wall time |
|---|---:|
| PR #8 write matrix, 72 windows | 54 min 05 s |
| Original partial-read accounting | 7 s |
| RAM reference, 36 windows | 15 min 29 s |
| RAM queue comparison, 54 windows | 22 min 37 s |
| NTFS caller comparison, 24 windows | 19 min 31 s |
| ReFS caller comparison, 24 windows | 20 min 08 s |
| Complete priority retry, 36 windows | 19 min 03 s |
| Traced priority subset, four windows | 2 min 38 s |
| Focused churn, six episodes | 9 min 38 s |
| Five retained-driver scenarios | 3 min 09 s |
| Plan-114 campaign smoke, two phases | 59 s |
| Plan-114 focused campaign, 81 correctness checks + 30 RAM windows | 17 min 21 s |
| Controller/restoration retry smoke, two phases | 1 min 07 s |
| Plan-116 RAM attribution, 81 correctness checks + 3 traced windows | 7 min 38 s |
| Plan-116 cached priority/affinity, 81 correctness checks + 36 untraced windows | 25 min 02 s |
| Plan-117 coordination diagnostic, nine windows | 4 min 33 s |
| Plan-117 controller smoke, two phases | 1 min 18 s |
| Wake-up smoke, two phases | 1 min 03 s |
| Plan-118 asynchronous-read comparison, 18 windows | 8 min 50 s |
| Plan-119 revision comparison, 18 windows | 8 min 52 s |
| Plan-119 lifecycle checks, with / without Driver Verifier | 1 min 10 s / 55 s |
| Final `release-performance` regression, 11 phases / 106 cases | 1 h 56 min 23 s |
| Plan-120 cached-read attribution, 18 windows | 9 min 45 s |
| Plan-120 RAM write reference, 36 windows | 6 min 43 s |
| Plan-120 streaming-store comparison, 18 windows | 5 min 30 s |
| Plan-120 read-bookkeeping comparison, 18 windows | 9 min 57 s |
| Plan-120 Driver Verifier campaign with `stress-races`, 6 phases | 10 min 56 s |
| Plan-120 `stress-races` without Verifier | 5 min 55 s |
| Plan-120 final `release-performance` regression, 11 phases / 106 cases | 1 h 47 min 34 s |

The original priority run's main scores total about six minutes within its nineteen-minute
run. Preparation, residency proofs, warmup, worker/process ownership and
restoration account for the rest. The write matrix includes explicit 45–53
second drains between some ten-second scores. A single command removes repeated
agent orchestration; it does not remove required disk preparation or durability
checks. The [campaign plan](TESTING.md#3-running-tests-one-command) records phase times and
delivers terminal events so those costs become visible without periodic agent
polling.

The latest two plan-116 campaigns together take **32 min 40 s**. The cache
priority/affinity phase accounts for 20 min 29 s of its 25 min 02 s campaign;
the RAM trace phase takes 2 min 40 s of its 7 min 38 s campaign. The surrounding
policy/pressure checks, preparation and restoration explain much of the remaining
time. Tool/bootstrap failures and engineering/CI time remain separate from these
completed measurements.

Not counted above (preserved separately; none contributes a result): the Driver
Verifier run that bugchecked (0x3B, fixed before measurement), a Verifier run and
a lifecycle run stopped by over-strict activation checks, one comparison refused
at start because Q: still had unwritten data, and the first final regression,
which stopped after 33 minutes in the sustained phase: a 197 GB resident reread
saw six background metadata reads (4.5 KiB) on Q:, most likely the Everything
file indexer that had restarted at sign-in. Everything and Steam were closed and
the complete regression rerun passed. Plan 120 adds the first RAM write reference run
(stopped by its oracle rewrite bug), 13 stress, Verifier and stale-data diagnosis
runs that found and fixed the issues described under
[correctness qualification](#correctness-qualification), and their recoveries.

### Volume filter: boot reset on C:'s volume (found and fixed before merge)

0.4.183.1 booted with the filter on Q:'s volume only, but with it on C:'s volume
the VM reset silently a few seconds into boot (no stop screen, no dump, no event),
with or without Driver Verifier, until Automatic Repair gave up. Recovery each
time: the Windows recovery command prompt, loading the offline SYSTEM hive and
restricting the filter to Q:'s volume again (`ClassCoverage` 0 and
`LabAllowedDriverKey`).

Bisected with a lab `DiagnosticMode` service value (0.4.185.1) on C:'s volume:
pure pass-through booted; the full driver booted with the power-pageable mirror,
dispatch paging classification and the post-start queries all disabled, and with
each of the first two re-enabled; with only the post-start queries enabled it
reset again. The driver sent `IOCTL_DISK_GET_LENGTH_INFO` and
`IOCTL_DISK_GET_DRIVE_GEOMETRY` down the volume stack from its
`IRP_MN_START_DEVICE` handler. On the disk stack that was harmless; on the boot
volume (snapshots, BitLocker and the volume manager below) it resets the machine.

Fix (0.4.187.1, `e7150b7`): the length and sector size are queried once, on first
need, from a PASSIVE_LEVEL thread: the caller of the first QueueCache management
request or the request worker. With it the filter covers every volume and boots.

### Volume filter verification (2026-09-28)

Maintained checks: `qcache developer verify <volume> --suite volumes` (registration,
raw disk commands, shared disk, resize, snapshot) and `--suite trim-cache` on the lab
VHDX from `qcache developer lab-disk create`; the raw `write-tests` on its unformatted
volume; host contract tests and desktop fixture tests in CI. Results on 0.4.219.1
(`7a18a57`) under standard Driver Verifier, VM with 16 GB:

| Check | Result |
|---|---|
| `volumes` on V: (lab VHDX) | 5/5 PASS, including the shadow copy containing 32 MiB that were pending in RAM |
| `volumes` on Q: (VirtIO disk) | registration, raw disk commands and snapshot PASS; shared-disk and resize SKIP by design (no lab volume there) |
| `trim-cache` on V: | PASS (pending dropped, clean released, TRIM during a drain) |
| `quick`, `policies`, `paging-coherence`, `ordering-faults`, `app-write-profile`, `pressure` on V: | PASS |
| `policies` on Q: | PASS |
| `write-tests` on X: (base, concurrent, toggle, write-through, verify) and `developer test` on V: | PASS |
| Install over the disk filter, uninstall (drained Q:'s 2 GiB cache), restart without the filter, reinstall (profile restored at startup) | PASS (0.4.191.1 - 0.4.217.1) |
| `Recover-Registration.ps1` on copies of the SYSTEM hive | Restores the backed-up lists; live registry untouched |
| Desktop on the VM console | Volumes grouped by disk; live Q: card; unformatted X: shown without settings |
| FAT32 lab disk (0.4.229.1, Driver Verifier): `volumes`, `quick`, `policies`, `paging-coherence`, `ordering-faults`, `app-write-profile`, `pressure` | PASS; snapshot SKIP (Windows takes no shadow copies of FAT), resize SKIP (Windows cannot resize FAT), `trim-cache` SKIP (FAT takes no file-level TRIM) |
| ReFS Dev Drive lab disk (0.4.229.1, Driver Verifier): same suites plus `trim-cache` | PASS, including the snapshot and TRIM; resize SKIP (Windows cannot shrink ReFS); the caller-thread share is lower on ReFS (KNOWN_ISSUES) |
| Saved-C:-profile restart soak (10 cycles, 512 MiB C: profile) | 10/10 PASS; paging recognition unexercised in 2 cycles (16 GB: no page-file I/O under the applied pressure) |
| CrystalDiskMark on Q: (Verifier off, 3 runs) | SEQ1M Q8T1 36.6-37.2 / 20.6-21.4 GB/s; RND4K Q32T1 1,576-1,638 / 1,555-1,648 MB/s; RND4K Q1T1 1,230-1,328 / 1,023-1,073 MB/s: same as or faster than the disk filter |

Earlier branch results (0.4.187.1): every volume boots; the C: program-file check
passes 3/3; a 10-cycle saved-C:-profile restart soak passed 10/10 under Driver
Verifier. CrystalDiskMark on Q: (0.4.183.1): SEQ1M Q8T1 about 36/21.6 GB/s, RND4K
Q32T1 about 1,570/1,630 MB/s, RND4K Q1T1 about 1,340/1,030 MB/s (disk filter:
36.5/21.3 GB/s, 1,550/1,310, 1,010/855).

The volume suites found six defects that the disk-level suites could not (boot reset,
resize, two shadow-copy problems, removal of a cache on an unformatted volume, and a
restoration RAM wait); all are fixed and each has a maintained check
([known issues](KNOWN_ISSUES.md)).

## Fixed issues

Fixed issues are the ✅ rows of the [overview](#overview). Details of each fix are in
Git history and [the former tracker](history/RAM_FIRST_IMPLEMENTATION_TRACKER.md).
