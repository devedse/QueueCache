# Known issues and verification gaps

What is known to be limited or not yet proven in the current QueueCache source.
Production evidence uses the unchanged 0.4.687.1 native driver; experiment-branch builds through 2026-10-20 are scoped below.
Fixed defects are listed at the end with the build that
fixed them; do not report them as current bugs without a reproduction on the
current driver. Performance results and decisions are in
[performance findings](PERFORMANCE_FINDINGS.md); how things are tested is in
[testing](TESTING.md).

Verification so far happens on one recoverable 4-vCPU Windows 11 VM. The
maintained suites pass there, including under Driver Verifier, but that is not
production qualification.

## Current limitations

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
The driver and installer are test-signed development builds; production signing
and certification are not done.

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

### Drain parity with PrimoCache remains unproven; larger defaults resolve only part of it

The October 15/16 disabled-baseline refresh measured one complete 8 GiB write at 200.1
MB/s including disk catch-up, against 257.4 MB/s with the cache paused. Fast accepted
the application write at 267.5 MB/s, then needed a sampled 10.81 s to catch up. The 1024
KiB × 4 defaults improve paired explicit-drain times, but do not establish disk-speed
parity or invariant foreground performance. These are virtual-disk counters with roughly
one-second resolution, with the QueueCache filter still loaded in the control. See
[issue #15](https://github.com/devedse/QueueCache/issues/15) and the [current
comparison](COMPARISON_DETAILS.md#current-cache-refresh-2026-10-1516).

The separate plan 148 short new-file comparison completed three repetitions per
provider. Through explicit drain, QueueCache Fast measured 52.71 MB/s for one
1 GiB file and 41.71 MB/s for 4,096 small files (256 MiB); Direct measured
48.41 / 1.25 and PrimoCache 78.96 / 57.62 MB/s. All cache-bypassed hashes passed.
QueueCache’s small-file repetitions ranged from 18.36 to 41.94 MB/s, for an
unresolved reason. These fitting, unbuffered workloads show workload-specific
results; the sustained 8 GiB gap above remains a separate finding. QueueCache
trails PrimoCache by 33.2% for the large file and 27.6% for small files. The goal
in [#15](https://github.com/devedse/QueueCache/issues/15) is to match or exceed a
contemporaneous PrimoCache control on both workloads, with repeatable results
and correctness preserved. The larger-default fix is merged; this broader goal
remains open. [Method and ranges](COMPARISON_DETAILS.md#short-write-plus-drain-comparison-2026-10-16).

The later October 16/17 decision screen completes fresh Direct/Primo controls
and four same-build QueueCache 0.4.756.1 arms. At 2 MiB × 8, large/small medians
are 84.24 / 36.14 MB/s, versus Primo 109.68 / 54.47 and Direct 47.71 / 1.28.
All 36 cases and 73,746 hashes passed. Control drift and overlapping small-file
ranges prevent a stable small-file tuning claim. Most small-file batches stop
at a missing dirty neighbor; the later follow-up below classifies those gaps.
The eight-drainer foreground-read penalty below also argues against a blanket
default increase. These fitting workloads do not resolve sustained, Strict or
power-loss gaps. [Latest methods and limits](COMPARISON_DETAILS.md#drain-decision-screen-2026-10-1617).

The October 17 same-build aligned-gather experiment is complete and rejected:
current/aligned 227.95/219.98 MB/s large and 111.85/93.58 small (−3.5%/−16.3%).
All 12 performance cases / 24,582 hashes and post-oracle bypass proofs passed;
a separate complete Verifier matrix passed. Current small-file backing requests
already averaged about 1642 KiB by the median per-case ratio, versus aligned
990 KiB; all 202 current forward gaps were uncached. This new session cannot be
compared with old Primo scores to claim parity. Refresh the two narrow provider
controls and placement/dirty-run evidence before another batching policy.
The failed policy is removed in plan 157; diagnostics remain. An excluded early
correctness attempt exposed delayed saved-profile startup during an oracle;
startup readiness, post-oracle pause/counter proof and missing-field rejection
now guard that path. The new Primo proof path is host-tested but not yet run on
Primo. Retirement/parser edits are not separately VM-deployed. Defaults and
broader qualification gaps remain. [Result and full plan](PERFORMANCE_FINDINGS.md#small-file-gather-156).

The October 18–20 matched research (plan 158) compared products at matched
drain latency, because this Ceph-backed disk changed speed up to threefold
within hours. Against PrimoCache's quiet window (120.5 / 59.9 / 126.9 MB/s for
the 1 GiB, 4,096-file and 8 GiB workloads), the 1024 KiB × 4 Idle default
measured 96.5 / 59.0 / 92.6. Draining during writes (Eager) at 2 MiB × 8
measured 141.3 / 173.3 / 125.5. All 47 complete runs and 454,794 hashes passed.
Parity is not qualified: large-file ranges overlap and the runs were not
simultaneous. Eager also has the TRIM stall below, and foreground-read
interference is unmeasured. Defaults are unchanged.
[Results](PERFORMANCE_FINDINGS.md#matched-158).

The October 20–21 same-window comparison (plans 159/160) bracketed PrimoCache
between two QueueCache blocks at matched drain latency. Primo measured
285.5 / 70.4 / 315.8 MB/s. Eager 2 MiB × 8 measured 304.9 and 298.3 / 142.6 and
132.2 / 294.1 and 288.9: it meets both #15 medians, with large-file ranges
overlapping and sustained writes 7–9% behind. The 1024 KiB × 4 Idle default
measured 184.1 and 170.0 / 86.6 and 80.0. It is still not the default: eight
drainers cut a concurrent disk reader's IOPS about in half (545 against 1,094),
and release qualification has not run. [Results](PERFORMANCE_FINDINGS.md#trim-160).

Plan 161 measured reads while each product drains. PrimoCache does not protect
them: a concurrent 4 KiB Q1 disk reader got 163 IOPS (p99 64 ms) during Primo's
drain. It got 277–331 IOPS (p99 29–30 ms) under Eager 2 MiB × 8 at the same
drain rate, and 959–1,153 IOPS under the current default, which drains about
250 MB/s. Making Eager 2 MiB × 8 the default is therefore proposed, followed by a
final qualification. [Plan](PERFORMANCE_FINDINGS.md#finish-15).

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

### Each TRIM waits for every drain write already in flight (performance)

A TRIM runs on the ordered request path. QueueCache pauses its drainers and waits
until every lower write already issued has finished, whether or not it overlaps
the trimmed ranges. Only then does it forward the TRIM, and later writes wait
behind it. With Idle draining, little is in flight while a program writes. With
Eager draining, or under capacity pressure, each TRIM costs about one round trip
of the whole drain window (up to 16 MiB at 2 MiB × 8).

In plan 158 (2026-10-18), NTFS sent TRIMs for 4,096 just-deleted files during the
next measured write. At 2 MiB × 8 Eager, 144 and 272 TRIM requests stretched a
new 1 GiB file's application time from about 0.16 s to 6.7 s and 25.1 s. Three
small-file cases rose from about 1.3 s to 2.2–4.2 s. The driver's queue-wait
counter matched those stalls. Fitting Fast writes then wait for lower I/O,
contrary to the RAM-first goal. [Evidence](PERFORMANCE_FINDINGS.md#matched-158).

The experiment branch (native 0.4.766.1, plans 159/160) waits only for in-flight
writes that overlap a trimmed range. Under Driver Verifier, a TRIM outside the
in-flight batches returned in 2 ms while 3 MiB stayed in flight, and an
overlapping one waited 2,008 ms for its write; all data checks passed. The lower
TRIM itself is still sent synchronously on the ordered path. About 200 TRIMs
therefore still add roughly 0.5–1 s to an Eager small-file case on the test disk.
Production 0.4.687.1 keeps the old behavior.
[Fix evidence](PERFORMANCE_FINDINGS.md#trim-160).

Native 0.4.771.1 (plans 162/163) adds a default-off TRIM mode 3. Small TRIMs use
an index lookup, and an offload thread sends the lower TRIM while the ordered
request path continues. A fence keeps newer versions of the trimmed ranges from
draining before the lower TRIM completes, and forwarded writes to those ranges
wait for it. Under Driver Verifier, `trim-cache` passed 12/12 and the focused
campaign 98/98 with it on. In Eager mass-delete cases, application time fell from
2.52–2.58 to 2.01 s.
**Verification gap:** the fence's holding path and the forwarded-write wait have
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

## Fix branch verification (#14 / #15)

The branch overlaps ordinary read misses, preserves each read's original pin
identities, and defaults new tasks to 1024 KiB batches with four drainers. Saved
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

## Not yet proven on the VM

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
  not passed the maintained matrix.
- **System disk:** a saved C: profile with a fixed pagefile passed restart soaks
  (paging I/O bypasses the RAM cache), but full boot, paging, hibernation and dump
  acceptance is open. A pre-alpha large-image crash on the old engine left no dump
  and was never reproduced.
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
- **Managed disks:** sleep, hibernate, Fast Startup and a crash during a checkpoint
  commit are not covered.

Do not use automatic reboot, driver deletion or discarding dirty data as failure
recovery: a faulted cache must be inspected and recovered explicitly.

## Fixed issues

| What you would have seen | Fixed in |
|---|---|
| Ordinary cache misses reached the disk one at a time, and requeued service reads could repeatedly delay older misses | 0.4.687.1 qualification: bounded ordinary misses overlap and reinsertion preserves arrival order; throughput, p99 and equal-work tails improve within the recorded scope (#14) |
| New cache tasks drained adjacent data in small 256 KiB batches with two writes in flight | 0.4.687.1 qualification: new-task defaults are 1024 KiB × 4, with repeated drain-time gains; saved profiles keep their settings and general write IOPS are not qualified as invariant (#15) |
| With "keep written data" off, a program re-reading a large file (reads over 1 MiB) while QueueCache was still writing it to disk could get an older version of a 256 KiB piece; the disk itself was always right | 0.4.601.1 (found by the new `stress-races` suite) |
| One program reading a RAM disk topped out at about 26 GB/s whatever its queue depth (CrystalDiskMark SEQ1M Q8T1 25,353 MB/s) | 0.4.583.1: queued reads overlap, 47,583 MB/s ([findings](PERFORMANCE_FINDINGS.md#ram-disk-asynchronous-split-reads-accepted-pr-9)) |
| Sequential cached reads up to 45% slower after drop-clean and refill | 0.4.431.1 chunk allocator; 0.4.434.1 prefetch and coalesced copies |
| Programs on a cached C: crashed: page-in misses were kept from a buffer that can repeat a shared page (0.4.148.1-0.4.162.1) | 0.4.166.1 |
| A read miss was kept from the application's own buffer, so a program changing its buffer mid-read changed what others read | 0.4.169.1 (driver-owned miss buffer) |
| Random 4K Q32 writes fell from about 300,000/s to 35,000/s while write-back ran | 0.4.154.1 |
| Restart with a C: profile hung: a paging read queued behind the worker forwarding a shutdown request (0.4.117.1-0.4.125.1) | 0.4.128.1 |
| Bugcheck 0x7A restarting with dirty C: data: requests failed after the shutdown drain (0.4.111.1 and earlier) | 0.4.117.1 |
| Bugcheck 0x7E (divide by zero) after a cache release (0.4.99.1-0.4.104.1) | The following build |
| Processes faulted after booting with a C: Fast profile and fixed pagefile (0.4.83.1) | 0.4.87.1 (paging I/O bypasses RAM caching) |
| PC reset during boot with C:'s volume cached; volume extend failed; shadow copies froze the PC or timed out; a cache on a raw volume could not be removed; a test left Q: at 256 MiB | Volume filter, 2026-09-28 |
| SMART and pass-through commands were refused or drained the cache | Volume filter (commands no longer reach the cache) |
| One failed lower allocation during a drain faulted the cache | Plan 48 (retried for about 5 s) |

Details of each fix are in Git history and
[the former tracker](history/RAM_FIRST_IMPLEMENTATION_TRACKER.md).
