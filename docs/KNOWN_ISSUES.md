# Known issues and verification gaps

What is known to be limited, not yet proven, or fixed in QueueCache. Verification
happens on one recoverable 4-vCPU Windows 11 VM on Ceph-backed storage, including
under Driver Verifier; that is not production qualification. Production evidence
uses the unchanged 0.4.687.1 native driver; experiment-branch builds through
2026-10-22 (native 0.4.776.1) are scoped below. Do not report a fixed issue as a
current bug without a reproduction on the current driver. Performance results and
decisions are in [performance findings](PERFORMANCE_FINDINGS.md); how things are
tested is in [testing](TESTING.md).

## Overview

✅ fixed, with the first build that has the fix. ❌ open: by design, not planned, or
still to do. Open items link to their details.

| Issue | Fixed | Build or status |
|---|:-:|---|
| [Fast mode: a crash loses pending writes and can damage the file system](#fast-mode-loses-pending-writes-filesystem-damage-remains-an-open-safety-issue) | ❌ | Open safety issue, [#13](https://github.com/devedse/QueueCache/issues/13) |
| [The driver and installer are test-signed](#the-driver-is-test-signed) | ❌ | Production signing not done |
| [Raw disk reads and writes bypass the cache](#raw-disk-reads-and-writes-bypass-the-cache-by-design) | ❌ | By design |
| [Strict mode can stall writes while an unrelated backlog drains](#strict-mode-can-stall-writes-while-an-unrelated-backlog-drains-performance) | ❌ | Open, [#16](https://github.com/devedse/QueueCache/issues/16) |
| [Low-priority programs read cached data up to 30% slower](#low-priority-programs-read-cached-data-slower-performance) | ❌ | Not planned (Windows scheduling) |
| [ReFS: fewer requests take the fast caller-thread path](#refs-fewer-requests-take-the-fast-caller-thread-path-performance) | ❌ | Not planned (measured trade-off) |
| [A re-read file briefly takes space from a random workload](#a-re-read-file-briefly-takes-space-from-a-random-workload-performance) | ❌ | By design (most recent read wins) |
| [A request only partly in RAM is read entirely from disk](#a-request-only-partly-in-ram-is-read-entirely-from-disk-performance) | ❌ | Not planned (under 1% of disk traffic) |
| [Files read through Windows' file cache (Explorer copies, most programs) never enter the read cache](#buffered-reads-enter-the-cache-only-through-writes-or-unbuffered-reads) | ❌ | By design for now (page-in safety); change proposed in [#19](https://github.com/devedse/QueueCache/issues/19) |
| [An older build cannot be installed over a newer one](#an-older-build-cannot-be-installed-over-a-newer-one) | ❌ | By design (no migration) |
| [Desktop telemetry is partial](#desktop-telemetry-is-partial) | ❌ | Open |
| [Power loss not re-tested on the current driver](#not-yet-proven) | ❌ | Last tested on 0.4.604.1 |
| [Hibernate, Fast Startup, crash dumps and surprise removal](#not-yet-proven) | ❌ | Not in the maintained matrix; sleep (S3) passed on one real PC |
| [Cached system disk (C:): full boot, paging, hibernation and dump acceptance](#not-yet-proven) | ❌ | Restart soaks on the VM; restart, sleep and start-with-Windows checks on one real PC |
| [Strict and foreground cost of the new drain default](#not-yet-proven) | ❌ | Not measured in isolation |
| [Asynchronous TRIM: the fence's holding path is unexercised](#trim-no-longer-waits-for-unrelated-drain-writes) | ❌ | NTFS never reaches it |
| [Faults during the busy window, every fault point at every parallelism, Windows memory pressure](#not-yet-proven) | ❌ | Open |
| [Recovery of interrupted suites beyond `read-tail-decision`](#not-yet-proven) | ❌ | Shared restore path, host-tested |
| [Hard disks, SATA and NVMe SSDs](#not-yet-proven) | ❌ | One Ceph-backed VM measured; one iSCSI-booted PC ran functional checks |
| [The virtual-disk service once missed its 120 s preshutdown time (event 7043)](#not-yet-proven) | ❌ | Open: seen once, during a restart with a Windows update |
| QueueCache driver events show no text in Event Viewer (`qcachelab` event 157) | ❌ | Open |
| Two test disks briefly shared a disk signature while `managed-lifecycle` prepared them (`partmgr` event 58) | ❌ | Open: to check which disks |
| [Test-VM fixture margins (RAM-disk headroom, fragmented lab volume)](#not-yet-proven) | ❌ | Test harness only |
| Setup failed, and the app listed no volumes, on a PC whose Storage Management provider lists no disks (seen on an iSCSI-booted Windows 11) | ✅ | Setup 0.4.781.1; app, CLI and cache targets 0.4.784.1 (disks and volumes are read natively, without `Get-Disk`/`Get-Volume`) |
| A failed setup could leave the new boot-start filter registered without the rest of the installation | ✅ | 0.4.782.1 (the filter is registered last; the previous registration is restored on failure; forced on the VM with 0.4.788.1) |
| Every install rewrote the desktop update files and shortcut, moving their icons | ✅ | 0.4.782.1 (written only when missing or changed) |
| [New files plus their drain were slower than with PrimoCache (#15)](#drain-speed-against-primocache-15-met-on-the-test-vm-with-limits) | ✅ | Native 0.4.776.1: Eager 2 MiB × 8, read-aware limit 2, asynchronous TRIM for new caches |
| [Each TRIM waited for every drain write in flight, stalling writes for seconds](#trim-no-longer-waits-for-unrelated-drain-writes) | ✅ | Native 0.4.766.1 (overlapping writes only); asynchronous since 0.4.771.1, default with 0.4.776.1 |
| Ordinary cache misses reached the disk one at a time, and requeued service reads could repeatedly delay older misses (#14) | ✅ | 0.4.687.1: bounded misses overlap and requeueing keeps arrival order |
| New cache tasks drained in small 256 KiB batches with two writes in flight | ✅ | 0.4.687.1 (1024 KiB × 4); 2 MiB × 8 Eager since native 0.4.776.1. Saved profiles keep their settings |
| With "keep written data" off, re-reading a large file (reads over 1 MiB) while it drained could return an older 256 KiB piece; the disk was always right | ✅ | 0.4.601.1 (found by `stress-races`) |
| One program reading a RAM disk topped out at about 26 GB/s whatever its queue depth | ✅ | 0.4.583.1: queued reads overlap, 47,583 MB/s ([findings](PERFORMANCE_FINDINGS.md#ram-disk-asynchronous-split-reads-accepted-pr-9)) |
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
qualification of the current 0.4.687.1 driver. PrimoCache also damaged NTFS in
partial-drain cuts; its clean early cut does not establish a general guarantee.
[Evidence and scope](COMPARISON_DETAILS.md#power-cut-while-the-cache-is-writing).

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

### Strict mode can stall writes while an unrelated backlog drains (performance)

Strict honors each write-through write and application flush by draining the
whole dirty cache and flushing the lower disk. The ordered request worker keeps
later writes waiting meanwhile. After a burst of random writes, the comparison
on 0.4.604.1 measured stops of more than 100 seconds and file operations several
times slower than the bare disk. The read-miss and drain-default fixes do not
change this durability boundary. Bounded Strict admission and overlapping-version
ordering remain open in [issue #16](https://github.com/devedse/QueueCache/issues/16).

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

### Buffered reads enter the cache only through writes or unbuffered reads

Page-in misses are never kept (a clustered page-in can repeat the memory manager's
shared dummy page). Data that programs read through Windows' own file cache
therefore enters QueueCache's read cache only when it was written or read
unbuffered; Windows' file cache holds it otherwise. NTFS metadata write-back is
admitted like any other write.

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
- **File-tree environment:** tracing attributed the 160–170-second read/hash
  slowdown to Defender. The full disabled-baseline generic refresh took
  2.84 s Fast, 2.90 s paused and 2.87 s PrimoCache, with every hash verified
  (one repetition each). The earlier 6.86-second diagnostic and historical
  4.9-second session retain their own scope; the latter did not record Defender
  state. PrimoCache also has a post-run Ceph near-full warning; its onset and
  effect are unknown; the warnings cleared after restoration. See [findings](PERFORMANCE_FINDINGS.md) for controls and exact runs.
- **Foreground interference and Strict impact:** neither has a valid isolated
  comparison for the new drain defaults. The removed overlap fixture drained its
  large seed after the default dirty-age trigger; the removed Strict fixture saw
  unattributed application/write-through barriers drain its seed. The driver
  refuses option changes while enabled or dirty, so seeding under held-off
  settings and switching afterwards is not supported. This leaves the known
  whole-cache Strict barrier cost (#16) open, rather than proving a regression of
  the new defaults. Failed runs and counters remain in [findings](PERFORMANCE_FINDINGS.md).
- **Power-loss evidence for 0.4.687.1:** no power-cut test was repeated on the
  fix build. Existing 0.4.604.1 and competitor results remain historical and do
  not establish the newer build's crash behavior.
- **Removal and power:** Windows eject with pending writes passes; live reconnect
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
  Startup and a crash during a checkpoint commit are not covered.
- **Shutdown:** on that PC the virtual-disk service once did not stop within its
  120 s preshutdown time (Service Control Manager event 7043), during a restart that
  installed a Windows update. The cached files were unaffected (0 mismatches). The
  cause is not established.
- **Hardware:** everything above was measured on one Ceph-backed VM. Hard disks,
  SATA and NVMe SSDs were not measured. An iSCSI-booted PC ran the app, a RAM disk
  and the functional checks above, not a performance qualification. Performance
  rates on the VM also depend on host load: RAM-path rates on 2026-10-22 were 35–73%
  below October 14 on both the final and the production driver
  ([findings](PERFORMANCE_FINDINGS.md#final-164)).
- **Setup rollback:** forced once on the VM (0.4.788.1, 2026-10-22). With one
  injected failure after activation, setup exited 1 and restored the filter list,
  start type, driver path and group exactly. The steps before activation (RAM
  provider, broker, sign-in task, PATH) stay installed by design; rerunning setup
  completes them.

Do not use automatic reboot, driver deletion or discarding dirty data as failure
recovery: a faulted cache must be inspected and recovered explicitly.

## Fixed, with limits

### Drain speed against PrimoCache (#15): met on the test VM, with limits

Since plan 164 new caches drain Eager with 2 MiB batches and eight drainers, with
read-aware limit 2 and asynchronous TRIM. In a same-window A–B–A at matched
backend speed (paused anchors 95.9 / 96.3 MB/s, drain latency 53.1 / 53.7 ms), the
default measured 292.4 / 90.7 MB/s for a new 1 GiB file / 4,096 new 64 KiB files
including the drain. PrimoCache 4.4.1 measured 261.0 / 52.5, and a concurrent disk
reader got 757 against 170 IOPS. The third block ran on a degraded backend (the
small-file anchor fell 25%); its raw small-file median of 49.6 was below Primo's,
while per anchor it stayed ahead. Release qualification on the final build passed (11/11 phases, 106 cases). Limits:
- One Ceph-backed VM; hard disks and SATA SSDs were not measured.
- The 8 GiB sustained workload trailed Primo by 7–9% in plans 159/160 and was not
  repeated.
- Saved profiles keep their old settings: an upgraded machine with a saved
  1 MiB × 4 Idle profile keeps it until it is changed.
- Strict caches also inherit the new default, and their boundaries were not
  measured under it (#16).
[Results](PERFORMANCE_FINDINGS.md#final-164).

Earlier steps, each with its own results and limits: the October 15/16 8 GiB
refresh ([comparison](COMPARISON_DETAILS.md#current-cache-refresh-2026-10-1516)), plan 148's
short new-file comparison ([method and ranges](COMPARISON_DETAILS.md#short-write-plus-drain-comparison-2026-10-16)),
the October 16/17 decision screen ([details](COMPARISON_DETAILS.md#drain-decision-screen-2026-10-1617)),
plan 156's rejected aligned gather ([result](PERFORMANCE_FINDINGS.md#small-file-gather-156)),
plan 158's matched research ([results](PERFORMANCE_FINDINGS.md#matched-158)), the
plan 159/160 same-window comparison ([results](PERFORMANCE_FINDINGS.md#trim-160)) and plan
161's reads during a drain ([plan](PERFORMANCE_FINDINGS.md#finish-15)). Plan 161 found that
PrimoCache does not protect reads while it drains (a 4 KiB Q1 disk reader got 163
IOPS, p99 64 ms) and that eight drainers alone halved that reader's IOPS; the
read-aware limit of plans 162/163 kept it at the old default's level.

### TRIM no longer waits for unrelated drain writes

Before native 0.4.766.1 a TRIM ran on the ordered request path and waited until
every lower write already issued had finished, whether or not it overlapped the
trimmed ranges; later writes waited behind it. Under Eager draining each TRIM cost
about one round trip of the whole drain window (up to 16 MiB at 2 MiB × 8). In
plan 158, 144 and 272 TRIMs stretched a new 1 GiB file's application time from
about 0.16 s to 6.7 s and 25.1 s, so fitting Fast writes waited for lower I/O,
contrary to the RAM-first goal ([evidence](PERFORMANCE_FINDINGS.md#matched-158)).
Production 0.4.687.1 keeps that behavior.

- **0.4.766.1 (plans 159/160):** a TRIM waits only for in-flight writes that
  overlap it. Under Driver Verifier a TRIM outside the in-flight batches returned in
  2 ms while 3 MiB stayed in flight; an overlapping one waited 2,008 ms for its
  write ([evidence](PERFORMANCE_FINDINGS.md#trim-160)).
- **0.4.771.1 (plans 162/163), TRIM mode 3:** small TRIMs use an index lookup, and
  an offload thread sends the lower TRIM while the ordered request path continues.
  A fence keeps newer versions of the trimmed ranges from draining before the lower
  TRIM completes, and forwarded writes to those ranges wait for it. Eager
  mass-delete cases fell from 2.52–2.58 to 2.01 s. Default for new caches since
  plan 164.
- **0.4.776.1:** the caller-thread path stays on while a slow lower TRIM is
  outstanding; before, the lab VHDX's TRIMs switched it off
  ([results](PERFORMANCE_FINDINGS.md#final-164)).

Under Driver Verifier, `trim-cache` passed 12/12 and the focused campaign 98/98
with mode 3 on. **Verification gap:** the fence's holding path and the forwarded-write wait have
not been reached. NTFS keeps a file's writes behind that file's outstanding
file-level TRIM, so a rewrite always arrived after the TRIM. Reaching the fence
needs a writer outside that ordering, such as raw volume writes, which the
verifier never performs. `trim-cache/async-eager-ordering` reports which path
occurred.
[Evidence](PERFORMANCE_FINDINGS.md#prio-162).

The test VM's disk now accepts TRIM. On 2026-10-18 the hypervisor counted about
891,000 successful unmap operations on it, none failed, and QueueCache's TRIM
counter rose in 32 of 132 cases. That counter only counts TRIMs the lower disk
accepted. The earlier VirtIO rejection (virtio-win issue #1574) therefore no
longer applies to this VM; when that changed was not recorded. TRIM correctness
is verified on a VHDX (`trim-cache`): trimmed pending data is dropped, not
written, and everything else reads back exactly.

## Fix branch verification (#14 / #15)

The branch overlaps ordinary read misses, preserves each read's original pin
identities, and defaults new tasks to Eager draining with 2048 KiB batches, eight
drainers, read-aware limit 2 and TRIM mode 3 (1024 KiB × 4 Idle from 0.4.687.1 until plan 164). Saved
profiles retain their settings. Misses spanning more than 256 cache blocks
(1 MiB when aligned), or arriving when the offload table is full, use the ordered
worker.

| Area | Qualified evidence | Limit |
|---|---|---|
| Read misses (#14) | Latest complete 24-case same-build screen: cold random 9.95×, sequential 3.13×, every cold p99 improves; no resident pair loses 3%, and resident misses/lower reads are zero | Both arms use the new pool and queue order; this isolates miss offload, not every old-driver difference |
| Equal-work read tails | Complete 24-case, 100,000-operation comparison: cold-random p99.9 191.7 → 60.7 ms; median case maximum 271.6 → 123.5 ms; every cold pair improves | Fixed-duration maxima sample unequal amounts of work; backing-store profile and pool size were not varied |
| Drain settings (#15) | Complete timing-off 512 MiB comparison: 12.7 → 8.0 s, every pair improves; separate cold-read/write-drain comparisons also improve | Qualifies explicit drain time, not disk-speed parity, general foreground throughput or Strict impact |
| Short completed writes | Plan 148: all 18 provider cases, 36,873 file hashes, zero pending/disk queue and restored state | New files fitting in cache; no new native code, sustained/Strict qualification or power-loss test |
| Experimental drain ceilings / gather | Plans 153/154 retain the earlier provider screen. Later plans 155/156 complete paired current/aligned selection: 227.95/219.98 MB/s large, 111.85/93.58 small, 12 performance cases / 24,582 hashes and bypass proofs; separate Verifier matrix passed. Aligned policy rejected/removed, diagnostics retained. | Defaults unchanged. Fresh same-session Primo is needed; current request sizes changed substantially. Strict / default Idle foreground / release qualification unrun. The Primo post-oracle path ran on Primo in plan 158; the retired-policy native build was not separately VM-exercised. |
| Matched provider research | Plan 158: latency-matched, default 96.5 / 59.0 / 92.6 and Eager 2 MiB × 8 141.3 / 173.3 / 125.5 against Primo 120.5 / 59.9 / 126.9 MB/s. Plans 159/160 same-window A–B–A: Primo 285.5 / 70.4 / 315.8, Eager 2 MiB × 8 304.9 and 298.3 / 142.6 and 132.2 / 294.1 and 288.9, default 184.1 and 170.0 / 86.6 and 80.0; 22 runs / 233,538 hashes | Configuration, not defaults; large-file ranges overlap; sustained 7–9% behind; release/Strict qualification unrun |
| Read during drain, provider-neutral (plan 161) | A–B–A against PrimoCache: concurrent disk reader 163 IOPS under Primo, 277–331 under Eager 2 MiB × 8 and 959–1,153 under the default, at drain rates of about 360 / 360–420 / 250 MB/s; 10 complete runs | One Ceph-backed VM; uncached Q1 reads alone were 20–35% slower than Primo's |
| New default, same-window PrimoCache (plans 164–167) | Native 0.4.776.1, Eager 2 MiB × 8 with read-aware 2 and TRIM mode 3 as shipped. A–B–A at matched anchors: 292.4 / 90.7 MB/s against Primo 261.0 / 52.5; disk reader 757 against 170 IOPS; 12 runs, 110,619 hashes. Under Verifier: `trim-cache` 12/12, focused campaign 98/98, integrity 12,291 hashes. `release-performance` on the final build: 11/11 phases, 106/106 cases, restored (plan 171); its RAM-path rates were host-bound that day and matched by a same-window 0.4.687.1 control. Block C repeat: 301.1 / 74.9 MB/s. | One Ceph-backed VM; block C on a degraded backend; 8 GiB sustained not repeated; Strict boundaries under the new default unmeasured |
| Read-aware limit and asynchronous TRIM (plans 162/163) | Lab switches on native 0.4.771.1. Read-aware 2 keeps a concurrent disk reader at 1,031–1,141 IOPS (p99 10–11 ms) during Eager 2 MiB × 8 drains, against 340 without it, while #15 medians stay 300.3 / 122.2 MB/s with 0 gate waits; Verifier campaign 98/98 and `trim-cache` 12/12 with both on | One Ceph-backed VM, one session; default-off; the TRIM fence's holding path is unexercised through NTFS |
| Read/drain lock diagnostic (plan 160 repeat) | Complete 48/48 on native 0.4.766.1, one noisy standalone window remeasured: eight versus four drainers +15% drainage, disk reader 1,094 → 545 IOPS, p99 15.4 → 28.5 ms | Same Eager screen; reproduces plan 151's magnitude; read-aware limit unbuilt |
| Read/drain lock diagnostic | Plan 151: 48 windows; at 2 MiB, eight versus four drainers raised drainage about 34% during disk reads but reduced reader IOPS 56%, p99 13.684 → 24.473 ms; global lock held about 1% | Short Eager screen with write retention off and separate pure RAM/disk paths; no Idle-default, Strict or sustained qualification. Storage scheduling is the first follow-up; a locking speedup is unproven. Exact methods, variability and excluded setup failure are in [findings](PERFORMANCE_FINDINGS.md). |
| Correctness | Qualification includes 98 Driver Verifier checks, the 37-case concurrency matrix with 24 zero-lower-I/O RAM-only windows, and the October 14 release campaign's 11 phases / 106 cases / 93 retained correctness checks | Exact builds, loaded hashes, byte oracles, telemetry and run identities are in [performance findings](PERFORMANCE_FINDINGS.md) |

The experimental branch now bounds drain staging at small cache budgets and
checks the remaining allocation before unsigned subtraction. This fixes the
source-reviewed 16 MiB underflow introduced by the larger experimental staging
ceiling. Plan 152's fourteen small-budget Fast/Strict byte-oracle checks passed on native
0.4.756.1 under Driver Verifier, as did the maximum-settings quick file-integrity
smoke. Broader promotion qualification remains pending. Production 0.4.687.1 is unchanged.

The earlier same-build Defender/Search-off drain-settings repeat has no complete matrix: extra small writes
invalidated seed/end snapshots. A failed file-handle experiment was removed in
plan 146; its failed case never reached the byte oracle. The write source is
untraced. Earlier accepted drain evidence remains valid within its recorded scope.

The October 15 release repeat stopped at 8/11 phases: map-cost case 7 recorded
six lower reads / 4,608 miss bytes in a RAM-only window, without lower writes,
flushes, draining or driver errors. Hit-byte growth exceeded scored bytes, but
the counters cannot identify the read source. Restoration succeeded. Separate
unchanged map 9/9, write 72/72 and ordering/fault 8/8 follow-ups completed and
restored; they do not turn the original campaign into a completed one.

After sustained churn, the first Q1 reread missed entirely; the following Q8
reread reached 99.750–99.988% hits but still made lower reads. These are sequential
recovery stages, not independent queue-depth controls. Natural idle left
768–897 MiB dirty with 56–70-second oldest ages: the 5-second age starts scheduling
and cannot guarantee a drain deadline. Timing diagnostics cost about 6–10% in
cached random writes and must match between comparisons. Product comparisons and
CrystalDiskMark keep their own sessions; see [comparison](COMPARISON.md).

## Fixed issues

Fixed issues are the ✅ rows of the [overview](#overview). Details of each fix are in
Git history and [the former tracker](history/RAM_FIRST_IMPLEMENTATION_TRACKER.md).
