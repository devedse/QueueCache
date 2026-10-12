# Known issues and verification gaps

What is known to be limited or not yet proven in the current QueueCache build
(0.4.604.1, 2026-10-12). Fixed defects are listed at the end with the build that
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

## Not yet proven on the VM

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
