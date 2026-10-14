# Known issues and verification gaps

What is known to be limited or not yet proven in the current QueueCache source.
The latest VM checks use 0.4.687.1 (2026-10-14); their scope is recorded below.
Fixed defects are listed at the end with the build that
fixed them; do not report them as current bugs without a reproduction on the
current driver. Performance results and decisions are in
[performance findings](PERFORMANCE_FINDINGS.md); how things are tested is in
[testing](TESTING.md).

Verification so far happens on one recoverable 4-vCPU Windows 11 VM. The
maintained suites pass there, including under Driver Verifier, but that is not
production qualification.

## Current limitations

### Fast mode and RAM disks lose data on a crash (by design)

In Fast mode a save finishes when the data is in RAM; QueueCache writes it to the
disk moments later. A crash or power cut loses those writes and can damage the
file system (FAT32 and exFAT have no journal, so they are most at risk). A RAM disk
is empty after a crash, and an image in RAM is back at its last save. Strict mode
and explicit flushes keep their disk boundary. The driver and installer are
test-signed development builds; production signing and certification are not done.

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

### The test VM's own disks cannot TRIM

Windows 11 with the VirtIO SCSI driver gives up on TRIM (the disk rejects GET LBA
STATUS; virtio-win issue #1574), with or without QueueCache. TRIM handling is
verified on a VHDX (`trim-cache`): trimmed pending data is dropped, not written,
and everything else reads back exactly.

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

The branch overlaps ordinary read misses, preserves each read's exact original
pin identities, and uses 1024 KiB × 4 drain defaults for new tasks. Saved profiles
keep their settings. Misses spanning more than 256 cache blocks (1 MiB when
aligned), or arriving when the offload table is full, use the ordered worker.

- Signed 0.4.675.1 completed six Driver Verifier phases and 98 correctness
  checks, including 12 overlapping cold/partial-read rounds, pin-retirement
  checks and a 304-second stress window on both lab volumes
  (`20261013-175418-7aa42644d0cc4a90912f7fb85199ff9c`).
- The corrected retry fixture completed five checks without Verifier on
  0.4.679.1, with all bytes matching from disk and zero pending bytes after
  retry at parallelism 1, 2 and 4
  (`20261013-225440-cf15f15164264cbcb2b63c3886f8b2c8`).
- Signed 0.4.685.1 completed the full 37-case concurrency matrix on plan 131.
  All 24 RAM-only windows (12 reads and 12 writes) recorded zero lower read,
  write and flush attempts, misses and drained bytes; all scored bytes were
  accounted for. The largest telemetry gap was 0.215 seconds. Restoration was
  healthy and returned the original 2 GiB Fast profile with 256 KiB × 2 settings
  (`20261014-041653-4cf5d812f35744c1acf35af6647fa7ed`).

The final 0.4.685.1 same-build read-miss screen completed 24/24 windows. Cold
random throughput improved 9.4× and sequential throughput 3.35×, with every pair
improving and no repeated resident-control loss. Every cold p99 improved.
Random worst latency increased from 46.219 to 568.738 ms; worst-read latency is
not qualified as improved. The screen isolates miss offload: both arms use the
new read pool and queue order
(`20261014-044032-7009cf8578ec4b178648b13f18e38a2e`).

The complete plan-132 drain comparison finished 42/42 cases using the standard
CLI from `4b0e3c6` and signed 0.4.685.1 native drivers. All 36 seeded backlogs
matched from disk. Against 256 KiB × 2, 1024 KiB × 4 reduced median flush time
during cold reads by 40.7%, with every pair improving and separated ranges.
Fitting-write flush times improved in every pair, but their ranges overlapped;
foreground write changes included one 10.35% loss
(`20261014-053536-741edee09d044ee7b751ef2e8162c487`). A complete 18-case
write-only repeat reduced median flush time 127.61 to 92.16 seconds, with every
pair improving and separated ranges; full process completion improved too
(`20261014-071942-0428cc322079410ca84c019dd44983eb`). Ten-second write IOPS
changed −6.30%, −4.22% and +0.99%. All scored writes had been admitted before
the first observed drain progress, and that interval varied with control-worker
startup. General foreground write throughput is not qualified as improved or
invariant by this diagnostic.

Signed 0.4.687.1 completed the final `release-performance` campaign: all 11
phases and 106 outer cases, including the 93 retained correctness subchecks,
36 RAM read windows, lifecycle checks, recall, 30 minutes of mixed workload,
map polling and the 72-case write matrix
(`20261014-081924-fd5ed11e2abd459aab30498cf5ca8918`). All 186 raw DiskSpd
scores matched their XML byte totals; all 137 declared process intervals had
complete telemetry, with a maximum gap of 1.048 seconds. Final Q: state was
clean, healthy and back at the original 256 KiB × 2 profile; saved profiles
were identical. The lab volume's three injected fault counts remained in its
lifetime counter, with no active fault or pending data. This completes collection
and correctness qualification, not a speed verdict for every measured case.
The generic cached-volume and empty-cache comparisons also completed on this
build; their measurements and remaining slow workloads are in
[the comparison](COMPARISON.md). The README captures the real CrystalDiskMark
results separately.

The sustained measurements retain limits: the first Q1 reread after churn had
no cache hits and ran at 152–202 MiB/s; the following Q8 reread reached
99.750–99.988% hits but still made some lower reads. These are sequential
recovery stages, not independent queue-depth controls. After natural idle,
768–897 MiB remained dirty with 56–70-second oldest ages: the 5-second setting
starts scheduling and cannot guarantee a drain deadline. Diagnostic timing
reduced cached random-write throughput by about 6–10%; it is off by default and
must match between comparisons. See [findings](PERFORMANCE_FINDINGS.md).

Qualification exposed two fixture problems and a recorder delay. Plans 128/129
settle filesystem metadata and prime writable-file access before strict measured
windows. Plan 131 separates actual sampling from serialization/persistence
through a bounded buffer; it still rejects real coverage gaps and overflow.
The full concurrency result above supersedes the incomplete attempts, whose
evidence remains recorded in [performance findings](PERFORMANCE_FINDINGS.md).

## Not yet proven on the VM

- **Interrupted verifier recovery of copy flags:** 0.4.687.1 recovery snapshots
  do not persist the runtime copy flags. `cache-read-misses` normally restores
  its original value in cleanup, but an interruption after changing flags can
  leave miss offload disabled while `verify-recover` reports success. The
  related `cache-read-bookkeeping` cleanup restores product value 15 rather than
  a captured nondefault value. Completed comparisons restored normally. The
  follow-up branch is adding durable capture and readback checks; this remains
  unqualified until those changes pass host and Windows checks.
- **40,000-file read/hash time and random-read tails:** on 0.4.687.1, the tree
  read/hash step took 154.2 s versus 4.9 s in the 0.4.604.1 session, with the
  cause not established. The same-build cold miss-offload screen improved
  throughput and p99 but observed a random-read maximum of 568.7 ms versus
  46.2 ms. Plan 134 adds the maintained `tree-decision` matrix with per-phase
  memory/system-cache samples, cache snapshots, lower-I/O attempt counters and
  disk-idle settles; no result has been collected yet. Equal-operation and
  threshold-count evidence is still needed to determine whether the tail
  reflects the changed scheduling.
- **Drain defaults under active background work:** existing comparisons support
  shorter complete drainage, but timing was enabled and the foreground score
  started before an administrative flush whose startup time varied. A timing-off
  comparison with verified overlap between foreground I/O and default Idle
  draining, plus a focused Strict-mode check, remains open.
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
| Ordinary cache misses reached the disk one at a time, and requeued service reads could repeatedly delay older misses | 0.4.687.1 qualification: bounded ordinary misses overlap and reinsertion preserves arrival order; throughput/p99 improve, with worst-latency limits recorded above (#14) |
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
