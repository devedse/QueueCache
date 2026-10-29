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
| [Fast mode without write ordering: a crash can damage the file system](#fast-mode-without-write-ordering-can-damage-the-file-system-in-a-crash) | ❌ | By design: write ordering stays off by default because programs that flush very often slow down. Qualified for NTFS data drives; C: qualification is tracked below. See the fixed #13 row |
| [Write ordering on C:](https://github.com/devedse/QueueCache/issues/32) | ❌ | Qualification in progress. Baseline paging recognition and traffic limits passed; safe-range driver changes are not yet VM-qualified. The app offers it only with a matching V28 driver. |
| [High-watermark pressure check stopped the plan-189 release campaign](#high-watermark-pressure-check-stopped-the-plan-189-release-campaign) | ❌ | Not reproduced, [#30](https://github.com/devedse/QueueCache/issues/30): two standalone runs and the complete plan-192 release campaign (11/11 phases, 106/106 cases, restored) passed it with exact counters; the original cause is unproven |
| [The driver and installer are test-signed](#the-driver-is-test-signed) | ❌ | Production signing not done |
| [Raw disk reads and writes bypass the cache](#raw-disk-reads-and-writes-bypass-the-cache-by-design) | ❌ | By design |
| [Strict: scattered writes run at the disk's speed](#strict-scattered-writes-run-at-the-disks-speed-by-design) | ❌ | By design since 0.4.825.1 ([#16](https://github.com/devedse/QueueCache/issues/16): a flush no longer stops writing for minutes) |
| [Low-priority programs read cached data up to 30% slower](#low-priority-programs-read-cached-data-slower-performance) | ❌ | Not planned (Windows scheduling) |
| [ReFS: fewer requests take the fast caller-thread path](#refs-fewer-requests-take-the-fast-caller-thread-path-performance) | ❌ | Not planned (measured trade-off) |
| [A re-read file briefly takes space from a random workload](#a-re-read-file-briefly-takes-space-from-a-random-workload-performance) | ❌ | By design (most recent read wins) |
| [A request only partly in RAM is read entirely from disk](#a-request-only-partly-in-ram-is-read-entirely-from-disk-performance) | ❌ | Not planned (under 1% of disk traffic) |
| [An older build cannot be installed over a newer one](#an-older-build-cannot-be-installed-over-a-newer-one) | ❌ | By design (no migration) |
| [Desktop telemetry is partial](#desktop-telemetry-is-partial) | ❌ | Open |
| [Hibernate, Fast Startup and surprise removal](#not-yet-proven) | ❌ | Not in the maintained matrix; sleep (S3) passed on one real PC; the test VM's firmware offers no hibernation |
| [Cached system disk (C:): full boot, paging and hibernation acceptance](#not-yet-proven) | ❌ | Restart soaks and a crash dump on the VM (0.4.810.1); restart, sleep and start-with-Windows checks on one real PC |
| [Strict and foreground cost of the new drain default](#not-yet-proven) | ❌ | Not measured in isolation |
| [Asynchronous TRIM: the fence's holding path is unexercised](#trim-no-longer-waits-for-unrelated-drain-writes) | ❌ | NTFS never reaches it |
| [Faults during the busy window, every fault point at every parallelism, Windows memory pressure](#not-yet-proven) | ❌ | Open |
| [Recovery of interrupted suites beyond `read-tail-decision`](#not-yet-proven) | ❌ | Shared restore path, host-tested |
| [Hard disks, SATA and NVMe SSDs](#not-yet-proven) | ❌ | One Ceph-backed VM measured; one iSCSI-booted PC ran functional checks |
| [Test-VM fixture margins (RAM-disk headroom, fragmented lab volume)](#not-yet-proven) | ❌ | Test harness only |
| A crash in Fast mode could damage NTFS, with no setting to prevent it ([#13](https://github.com/devedse/QueueCache/issues/13)) | ✅ | 0.4.882.1 with write ordering on (off by default; that release did not support C:): seven VM power cuts on an NTFS data volume left NTFS clean, while the same test with ordering off damaged it. Physical power loss and other file systems are not tested |
| Write ordering only: a TRIM arriving while ordering was selected on a disabled cache could leave drainer zero's group flush in flight across a cache release, which then reported the draining group past the open group (found by plan 197) | ✅ | 0.4.868.1 (release waits for the group flush; a finished group flush never moves past the open group). No lab hook forces the race; the sustained suite that hit it passed afterwards |
| Strict stopped all writing for 100 s or more after a burst of random writes: every flush (NTFS sends them on its own) waited for the whole backlog ([#16](https://github.com/devedse/QueueCache/issues/16)) | ✅ | 0.4.825.1 (backlog limited to about 2 s of write-out; 0.4.810.1 sent write-through writes direct) |
| Files read through Windows' file cache (Explorer copies, most programs) and mapped files never entered the read cache ([#19](https://github.com/devedse/QueueCache/issues/19)) | ✅ | 0.4.810.1 (page-ins of file data are kept from a driver-owned copy of the disk data; a 1 GiB re-read after Windows' standby memory is purged: 275 → 2,254 MiB/s) |
| Detaching a virtual disk (VHD/VHDX) lost a Fast cache's pending writes: Windows removes the volume without asking first ([#21](https://github.com/devedse/QueueCache/issues/21)) | ✅ | 0.4.803.1 (written out when the file system releases the volume; `volumes/volume-detach`, plan 172) |
| A saved cache, or a RAM disk that starts with Windows, stayed off after a restart when RAM was briefly short ([#12](https://github.com/devedse/QueueCache/issues/12)) | ✅ | 0.4.803.1 (retried every 15 s for 10 minutes; the app and `qcache volume list` show a cache waiting for RAM or not restored) |
| A large image in RAM on a slow disk was not saved at shutdown: the service stopped trying after 110 s, without telling anyone ([#22](https://github.com/devedse/QueueCache/issues/22)) | ✅ | 0.4.803.1 (no time limit while the save makes progress; the app shows the expected time before and the outcome after the restart); 0.4.825.1: Windows waits at most 1 hour |
| Detailed Windows shutdown messages required a manual policy edit; VHDX creation did not recommend them ([#22](https://github.com/devedse/QueueCache/issues/22)) | ✅ | Settings and VHDX creation expose the actual Windows policy, refresh external edits, report suppression/errors and explain the limits. Dynamic percentages were investigated but are not a supported feature; faster saves moved to #28 |
| Saving a small change always copied the whole image to a new checkpoint ([#28](https://github.com/devedse/QueueCache/issues/28), split from #22) | ✅ | 0.4.840.1: eligible older checkpoints receive direct payload updates, with full verification, safe fallback and export protection. Nine Windows checks passed. Half-filled 1 GiB images saved 21–22% elapsed time in a short observation; mostly empty images had no consistent time gain. [Measured scope](PERFORMANCE.md#checkpoint-reuse-28). A live crash during commit remains untested |
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

### Fast mode without write ordering can damage the file system in a crash

In Fast mode a save can finish when the data is in RAM, including application
flushes and write-through requests; QueueCache writes it to disk later. A crash
can lose those writes; that is Fast mode's accepted volatility. With write
ordering off, the default, a crash can also damage the file system, because
pending writes reach the disk in any order. On 0.4.810.1, with the default drain, six power cuts (three
right after 10,000 new unflushed files, three during a churn's write-out) left NTFS
clean and every file older than about 10 seconds intact; up to 9,994 of the newest
unflushed files were lost or damaged. On 0.4.604.1 the same burst left NTFS needing
offline repair, and on 0.4.882.1 an early-churn cut damaged NTFS (below). Clean
cuts are therefore evidence, not a guarantee. Another write-back cache tested
the same way also damaged NTFS when cut during its write-out.
[Evidence and scope](COMPARISON.md#power-cut-while-the-cache-is-writing).

**Write ordering** prevents that damage; it closed
[#13](https://github.com/devedse/QueueCache/issues/13) on 2026-10-28 for NTFS data volumes. With it on, writes after a filesystem
flush or a write-through write stay in RAM until
everything before that point and a real lower flush have completed; old dirty
versions are kept across those boundaries, and TRIM is ordered with them. Since
plan 196 there is one mode (the former lab mode 3); the original mode 1, which also
sealed before each write-through, and mode 2, which woke every drain thread for each
write, were removed. It is an advanced per-cache setting, off by default, in the
app's Advanced tuning and as `qcache policy apply --write-ordering`; saved profiles
keep it ([details](CACHE_POLICIES.md#fast-write-ordering-13)). It is
being qualified on the Windows volume in [#32](https://github.com/devedse/QueueCache/issues/32),
with a matching V28 driver required. Older drivers refuse volumes registered for
paging, hibernation or dumps. Windows registered C: for paging even with page files
and crash dumps off; moving them to Q: was refused because the VM disks are hot-pluggable.

- **Checks:** 14/14 targeted live-driver checks passed under Driver Verifier on
  0.4.882.1 (plan 203), on a disposable non-OS NTFS volume; 13/13 passed on
  0.4.865.1 (plan 196). The earlier variants
  passed 9 (mode 1), 22 (plan 188), 26 (plans 191/192, Verifier off) and 39
  (plan 195, modes 1–3) checks.
- **Crash cuts:** six bounded cuts kept NTFS clean and untouched durable guards
  exact: three with mode 1 (a burst, a partial-drain boundary and a completed
  explicit checkpoint) and three with mode-2 write-through workloads (see the
  records below). On 2026-10-28, the current ordinary mode on 0.4.882.1 passed
  six NTFS data-volume VM-reset scenarios plus an extra middle-drain repeat:
  burst, write-through boundary, early/middle/late churn and explicit checkpoint.
  Every scan was clean and untouched durable guards matched exactly. The
  ordering-off early-churn control damaged NTFS; the two later controls stopped.
  Its preserved image is post-scan because the official scan repaired part of
  the bitmap online before still requiring offline index repair. This does not
  establish physical power-loss or Windows-volume safety.
- **Not covered:** physical power loss, other storage stacks, other file
  systems, every drain configuration, and system, paging, hibernation and dump
  volumes. Ordering stays off by default (see Cost); the warning above applies
  whenever it is off.
- **Cost:** with ordering on, writing into the cache takes the same time as with
  it off (plan 196: a 1.5 GiB file, 25,600 flushed files and 25,600 write-through
  pairs, all ranges overlapping). Each bookmark needs its own disk flush, so
  reaching the disk took about 7× as long for the files and 54× for the pairs
  ([single-mode result](PERFORMANCE.md#fast-ordering-13-single)). That matters
  only for nonstop flush-heavy writing: flushing every small file for five
  minutes filled the 2 GiB cache after about 1.5 minutes, after which the
  application ran at about 230 files/s instead of about 2,000. Windows' `tar` and
  `robocopy` on a 16,000-file tree showed no difference
  ([sustained result](PERFORMANCE.md#fast-ordering-13-sustained)). Mode 2 had
  also slowed the application itself (2.5× and 43× in plan 194) because every
  write woke all drain threads, which searched the whole backlog under the cache
  lock ([cause and fix](PERFORMANCE.md#fast-ordering-13-fix)).
- **Lower path:** ordinary ordering uses explicit flushes. The lab W: VHDX
  reports no write-through support, although Q: and the temporary virtual SCSI
  disk T: report support. Lab mode 2 (#31) requires that report and uses forced
  writes for eligible small groups. Its complete plan-203 cost comparison
  shortened durable time 8.8% for files and 7.2% for write-through pairs, with
  every pair improved and separated ranges ([B result](PERFORMANCE.md#fast-ordering-31-fua)).
  Its complete three-repetition sustained comparison still filled the cache and
  throttled in every flushed-file run: five-minute application throughput changed
  −0.33%, +3.62% and +2.59% against ordinary ordering. The final drain shortened,
  but the file counts differ. Tree controls established no application benefit
  ([sustained B result](PERFORMANCE.md#fast-ordering-31-fua-sustained)).
  The [#31 investigation](https://github.com/devedse/QueueCache/issues/31) is
  complete: the forced-write variant remains a developer-only experiment; the
  app's checkbox selects ordinary ordering. Its question-mark help explains the
  behavior and limits. No default or crash-safety qualification changed.
  Reported support and live byte checks do not qualify crash persistence;
  shared bus type alone is insufficient to select the optimization.
- **Releases:** the plan-189 release attempt stopped at a mode-zero pressure
  check (see the separate issue below); the plan-192 release campaign then
  completed with ordering off
  ([exact cost and release results](PERFORMANCE.md#fast-ordering-13-write-through)).
  Older comparisons are under
  [direct toggle](PERFORMANCE.md#fast-ordering-13-toggle) and
  [minute scale](PERFORMANCE.md#fast-ordering-13-minute).
  Two plan-203 final-build attempts stopped at the sustained fresh-read
  RAM-only guard (8/16 KiB of lower writes), both with restoration complete.
  Short standalone probes did not reproduce them. Plan 204's filesystem flush
  was insufficient in a focused regression; its traced repeat identified two
  4 KiB System writes to NTFS `$LogFile` during the read. Plan 205 requires a
  longer recorded quiet observation without relaxing the score guard; its
  focused run passed all 11 cases and 94 retained checks. The full plan-205
  attempt restored INCOMPLETE after a 1.809-second Windows clock adjustment
  made a 0.207-second actual sample interval appear as 2.016 seconds in UTC.
  Plan 206 records common monotonic sample/process clocks for cache exercises,
  retaining UTC and the strict two-second coverage limit. Its final campaign
  `20261028-152119-c71f832ab68c4eca94fdc350401519bb` completed **11/11 phases,
  106/106 cases, all 94 retained checks and six persisted oracles, RESTORED**.
  Controller 0.4.885.1 (`033127a`) used the unchanged loaded 0.4.882.1 drivers;
  both Windows CI configurations and Linux host contracts passed. The raw
  score/telemetry audits passed. These are complete checks and collection,
  without a cross-day performance verdict
  ([attempts and contract](PERFORMANCE.md#release-203-read-preparation)).

The follow-up audit found that a new paging/hibernation/dump registration could
arrive after ordering was selected. Selection and registration were serialized under the routing lock and new
registrations refused for the data-volume-only scope. The #32 candidate now permits
ordinary ordered registrations, clears safe extents and runs the existing full
boundary before registration. Lab FUA mode remains data-volume-only. Compile-time
contracts cover both reservation orders; a real new special-file registration
with ordering on has not yet been exercised on Windows.

**Decided 2026-10-25: no separate QueueCache journal.** A write-ahead journal on
the disk was considered as a way to speed up ordered write-out and rejected.
Every write would reach the disk twice; after a crash the volume would only be
consistent once QueueCache replayed it, so recovery tools, another PC or an
uninstalled QueueCache would see damage; and it cannot protect C:, because the
Windows boot loader reads C: before any QueueCache code runs.

**Also decided 2026-10-25: no reuse of NTFS's own journal.** Merging flush groups
by writing NTFS's journal (`$LogFile`) blocks first, then everything else, was
also rejected. It depends on undocumented NTFS internals, deliberately creates
an order NTFS never requested, works only for NTFS, and would mainly shorten
background write-out.

Strict mode and explicit QueueCache Flush/Pause/Remove keep their disk boundary;
an ordinary application flush in Fast mode does not provide that boundary.
FAT32/exFAT have no journal and cannot inherit an NTFS-specific safety claim.
A RAM disk is empty after a crash, and an image in RAM is back at its last save.

### High-watermark pressure check stopped the plan-189 release campaign

On native 0.4.851.1 with controller 0.4.852.1, plan 189's `release-performance`
campaign stopped at `trigger/high-watermark: lower I/O started below the configured
high watermark`. Experimental ordering was off. Partial-read accounting,
paging coherence and policies completed; deferred-age and idle triggers passed
inside pressure. The campaign completed 3/11 phases, and restoration succeeded.
It is not a release pass. No automatic retry, repair or rollback was used.

The assertion combines an already-crossed dirty-byte threshold, changed drained
bytes and changed lower drain attempts. It does not record which condition fired;
its restoration telemetry starts after the worker restores its state. Additional
NTFS metadata crossing the fixture's narrow margin is plausible but unproven;
a driver trigger problem remains possible. Capture the exact before/after state
and attempts in the maintained standalone `pressure` suite before changing either
the fixture or policy. Keep the acceptance conditions strict. A changed workload
requires a new plan/immutable case contract. Track diagnosis and a complete final
release run in [#30](https://github.com/devedse/QueueCache/issues/30).

Plan 190 retains these exact checkpoint counters before restoration and reports
the three conditions separately without changing the workload or guard. Two
standalone pressure runs on the same loaded native pair passed all 13 checks,
including one after the policies suite. In the first, dirty/admitted file bytes
were exactly 9,764,864 below a 9,804,186-byte threshold, with zero additional
accepted bytes or lower drain attempts. This does not explain or fix the original
failure.

**Complete release campaign, plan 192 (2026-10-25).** Published controller
0.4.856.1 (`52ff318`) beside the same loaded native 0.4.851.1 pair (hashes
checked) ran `release-performance` in
`QueueCache-Campaign-20261025-163917-513dbdddc39a49798ef125787a8fb197`, with
experimental ordering and Verifier off. It **COMPLETED 11/11 phases and 106/106
outer cases in 1 h 46 min 25 s, every phase RESTORED**, and `verify-completion`
accepted its durable terminal event. All 94 retained correctness sub-checks
passed: partial-read accounting 22, paging coherence 9, policies 42, pressure 13
and ordering/faults 8. The high-watermark checkpoint was exact again: 9,764,864
dirty bytes below the 9,804,186-byte threshold, an accepted delta equal to the
file payload, and unchanged drain-write (19,176) and lower-write (19,648)
attempts. Crossing it produced one drain-write attempt and 262,144 drained bytes;
persisted bytes matched after Disable. Speed results are in
[performance](PERFORMANCE.md#release-192).

The plan-189 failure has not recurred in three runs that record the separate
conditions. Its cause stays unproven because that run did not record which
condition fired; no driver, fixture or threshold change is claimed to fix it, and
the strict conditions are unchanged. #30 stays open for that cause.

Campaign `QueueCache-Campaign-20261025-134513-8fabb9dbe61e44cbac42f6b0f6092ffe`,
pressure child `20261025-134703-32780205d17844d3b2acd79793fae664`;
failure at 13:47:06 UTC on October 25. The exact worker reply/error, status,
results, summary, log, restoration trace and fixture are retained separately from
the successful 22 ordering checks and 18-case cost matrix. No evidence establishes
that this failure was caused by the mode-2 change.

The exact Windows evidence archive contains 1,124 files, including the three
failed-pressure fixture files and restoration receipt; SHA-256
`D36B4AE59160B595FB578B9CA1FAD3B6A18621FAD2608810271F615CDD813D20`.
Every entry was independently hash-checked after export before deleting only
this run's fixtures and guest staging. External Ceph/guest observations remain
separate, with a host evidence hash manifest. Q: was restored to its original
2 GiB Fast/Idle 1024 KiB × 4 profile, W: unconfigured, ordering/Verifier off,
Everything/service, Steam and tray running, and the existing R: definition stopped.
No snapshot or crash test was made in this iteration. VM space after cleanup:
C: 59.3 GB, Q: 183.5 GB, V: 8.56 GB, W: 8.51 GB free (decimal).

The plan-192 release evidence archive contains 19,739 files; SHA-256
`6DD7C4CE9E91BAD6AFFD05798B9B555469CCC66DC2656E80076C9E188B08826A`. Every
entry was hash-verified on the host before the guest copy and this run's staging
were removed. Restoration matched the same state: the original Q: profile with an
unchanged saved profile, W: unconfigured, ordering mode 0, Verifier off,
Everything/service, Steam and tray running, the R: definition stopped and the
same loaded driver hashes. VM space afterwards: C: 59.3 GB, Q: 183.5 GB,
V: 8.56 GB, W: 8.51 GB free (decimal).

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

### Strict: scattered writes run at the disk's speed (by design)

Strict answers a flush only once every write before it is on the disk, as a disk
with its own write cache does. A flush covers the whole volume, so it waits for all
unwritten data, and NTFS sends flushes on its own. Scattered small writes reach the
disk at a few MB/s, so until 0.4.825.1 a burst of them in RAM made the next flush stop
all writing for 100 s or more (#16). Since 0.4.825.1 Strict holds at most about two
seconds of that work: it counts separate pieces of unwritten data (a contiguous run
is one piece) against what the drain completes per second, and a write that would
add more waits. A flush now finds at most about two seconds of work. The cost, measured in
`compare-cache` (Strict, 2 GiB, same evening): random writes run at the disk's
speed (4K Q32 304 → 7 MB/s, where the old build then stopped completely), sequential
writes lost 20% (Q8) and 57% (Q1) and the mixed test 69%, while creating and
copying 40,000 files got faster (15.4/14.3 → 6.6/8.0 s). One run started 17 minutes
after a restart held writes, sequential ones included, for whole 10 s windows; it
was not explained. Use Fast for write speed. `policy diagnostics` shows the pieces,
the limit and the waits as `StrictBacklog` ([results](PERFORMANCE.md#strict-16)).

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
- **Power-loss evidence:** six cuts on 0.4.810.1 (three bursts, three churn cuts at
  and during the write-out) on one Ceph-backed VM, with the default drain only. Other
  drain settings, Strict, other file systems, a cached C: during writes and real
  hardware were not cut ([results](COMPARISON.md#power-cut-while-the-cache-is-writing)).
- **Removal and power:** Windows eject with pending writes passes, and so does
  detaching a virtual disk since 0.4.803.1 (`volumes/volume-detach`); live reconnect
  failed in Proxmox hotplug and stays unqualified. Surprise removal under load,
  FAT32/ReFS removal, sleep, hibernate and Fast Startup have not passed the maintained
  matrix. The test VM cannot hibernate: its firmware hides S4 (Proxmox's default), and
  only the host's root account can change that. Sleep (S3) passed on one real PC (Windows 11
  booted from iSCSI, 0.4.784.1, 2026-10-22). Five `managed-lifecycle` fixtures kept
  their creations, states and bytes. A Fast cache holding 512 MiB of unwritten data
  kept it through 3 minutes of sleep, wrote it out on flush and read back correctly
  from the disk. Hibernate and Fast Startup could not run on that PC: its iSCSI boot
  has no crash-dump driver (`volmgr` 45/46), and hibernation writes through that driver.
- **System disk:** a saved C: profile with a fixed pagefile passed restart soaks
  (paging I/O bypasses the RAM cache). A bug check with a saved Fast 1 GiB C: profile
  (0.4.810.1, raised from user mode) wrote its kernel dump (3.9 GiB, through a
  dedicated dump file), restarted on its own with the profile restored, and kept 2,000
  files flushed beforehand; `chkdsk /scan C:` found no problems. Nothing unwritten was
  in C:'s cache at that moment. Full boot, paging, hibernation and a crash during
  writes on C: remain open. A pre-alpha large-image crash on the old engine left no dump
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
  (0.4.803.1 and 0.4.810.1). Throughout a 139 s save the VM's screen showed Windows' own
  "Restarting"; QueueCache adds no message there. With Windows' "Display highly detailed
  status messages" setting on (VerboseStatus 1), the screen said "Shutting down service:
  QueueCache managed disks." throughout a save. In the October 24 display-name probe
  on 0.4.825.1, Windows accepted a synthetic "80%" name during a 19 s save, but the
  console samples showed Microsoft Passport Container or a spinner. The first helper
  was interrupted during a separate 61 s save and provides no rename evidence. These
  were Windows UI/API probes, not maintained driver correctness or performance runs.
  Neither a live percentage nor consistent display of QueueCache's name is established;
  Windows chooses the message. A longer 4 GiB image / 3 GiB changed-file retest
  confirmed stale text: Passport Container was already Stopped with PID 0 while
  QueueCache was StopPending, copying at 20%, with its new name read back from
  Windows. The console still named Passport Container. That save committed in
  508 s; none of the 702 captured frames showed QueueCache progress despite copy
  and verification name changes held for at least 10 s. A helper interrupted by
  shutdown left its final name after reboot until cleanup restored it. These
  results establish stale Windows text in this run, not that a displayed QueueCache
  label can never refresh. An unchanged-name control subsequently displayed
  QueueCache in 477 of 500 frames and committed its save. In a further save,
  changing the name only after QueueCache was visibly selected still left the
  original QueueCache text on screen, despite an independent live service query
  reading back the accepted "copying 0%" name while Stop Pending. Thus the rename
  did not refresh an already displayed label in this VM; the cause of the earlier
  Passport-only selection remains unproven. The test restored the original name
  and removed its backing delay. The new #28 path updates already allocated payload
  in an older owned VHDX without a Windows read-write attachment and still verifies
  every logical sector read-only. Plan 178 found Windows left dynamic zero regions
  unallocated; the safe fallback completed but did not satisfy the reuse check.
  Plan 179 adds unchanged sparse-block eligibility and pre-mutation full fallback
  for changes needing allocation. Its dedicated qualification is separate
  from these shutdown-screen checks. Longer saves, the 1-hour shutdown limit and a
  real PC's shutdown are not covered by the screen experiment.
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

**Issue #13, current ordering and final regression (2026-10-28):** native
0.4.882.1 passed 14/14 targeted checks under Verifier `0x209bb`, six ordering-on
NTFS data-volume reset scenarios and an extra middle-drain repeat. The
ordering-off early-churn control damaged NTFS; later controls stopped. Every
completed cut retained the durable guard's exact bytes and file lengths.
After failed-image preservation and approved snapshot recovery, plan-206
`release-performance` campaign
`20261028-152119-c71f832ab68c4eca94fdc350401519bb` completed/restored all 11 phases,
106 cases and 94 retained checks with Verifier off. All six sustained file
oracles passed after cache release. `verify-completion`, exact loaded-module
hashes, raw score/sample audits and the final state were checked. Q:'s original
profile, the original stopped R: definition and apps/services were restored;
the healthy lab image detached normally. The failed post-scan image and recovery
snapshot were deleted after review. [Result, hashes and limits](PERFORMANCE.md#release-206).

**Issue #28, plan 182, populated images (2026-10-25):**
`QueueCache-Verify-20261025-000011-ed9d80275808461ab30bfab008f8d87b`
completed all nine checks on direct NTFS Q: with its cache paused. Controller
0.4.842.1 used the unchanged 0.4.840.1 save backend, loaded filter 0.4.839.1 and
RAM provider 0.4.840.1; actual hashes were checked against signed packages.
Both 1 GiB fixtures included 512 MiB of existing nonzero data, whose SHA-256 was
retained and matched after committed reload. Each small-change reuse wrote 9 MiB
plus 8192 header bytes and read the full logical image, with protected current,
original and user-export hashes unchanged. Sparse-growth and invalid-map fallbacks
passed. Worker 258.8 s; restoration completed, fixtures removed and the original
Q: cache resumed. Verifier off; timing findings are in
[performance](PERFORMANCE.md#checkpoint-reuse-28). Exact archive SHA-256:
`A217DAC0D691D888A2640208FA1B64817B393AA4B549CE1415019F9E6C785D84`.

**Issue #28, plan 180, final save backend 0.4.840.1 (2026-10-24):**
`QueueCache-Campaign-20261024-193633-267d9c1dcbe24a84b402ceb342e73c73`
passed all nine checks, restoration RESTORED, terminal event validated. This adds
committed-export protection to the seven checks below: an export with an
automatic-looking name remains byte-identical after pointer rotation and two
later automatic saves. Both 128 MiB fixtures wrote 11 MiB of reuse payload plus
8192 header bytes and read back the entire logical image. The loaded filter was
0.4.839.1 and RAM provider 0.4.840.1, with hashes matched to their signed packages;
the filter source was unchanged. Verifier was off. Campaign 48 s, worker 39.1 s.
Exact archive SHA-256:
`E773923E5111AB01974CD067B7F6BFD78C39BF335023594B7466FF89766EB8ED`.
The plan-181 1 GiB follow-up also passed all nine checks; its mostly empty fixtures
and times are recorded separately in [performance](PERFORMANCE.md#checkpoint-reuse-28).
Host tests cover interrupted candidate writes and recovery metadata, invalid
layouts and hints; a live VM crash during checkpoint commit has not been run.

**Issue #28, plan 179, 0.4.839.1 (2026-10-24):**
`QueueCache-Campaign-20261024-192341-fc96567ee45c40d18d5530321d8866d7`
completed all seven changed-region checkpoint checks on NTFS W:, restoration
RESTORED, terminal event validated. Both loaded hashes matched the signed package;
Q:'s backing cache was paused. The two owned 128 MiB fixtures were dynamic/512-byte
and fixed/4096-byte sector images. Each reuse wrote 11 MiB of logical payload and
8192 VHDX header bytes, then read the entire 128 MiB through Windows for verification.
The protected current and original containers retained their hashes. Dynamic growth
requiring allocation selected a new complete candidate before mutation, as did
corrupt fingerprints; committed reloads preserved payload and untouched guard bytes.
Campaign 37.9 s, worker 29.4 s. This run was shortly after restart: its times are
correctness evidence, not a performance verdict. Exact archive SHA-256:
`38D71E535667A5BFF06B3E6165EDE15A28A3C04B15841CACC1CD512AE0051AC8`.

The existing `image-in-ram` regression on 0.4.837.1 passed all 28 subchecks under
Verifier `0x209bb` in 108 s, restoring its state. It covered save/export, open-file
vetoes, destination collisions, source-unavailable RAM runtime and committed reload
for both sector sizes with Direct/Standard variants. Plan 178's first reuse probe
was INCOMPLETE: Windows left dynamic zero regions unallocated, so it safely used
a full save instead of the required reuse. That evidence remains separate. Plan
180 adds explicit automatic-checkpoint provenance: user-chosen committed exports
and older records without the marker cannot be consumed, even with a generated-looking
filename. General retention, background saving and a crash during commit remain
separate work.

**Issues #13/#31 forced-write lab variant and checkpoint metadata (2026-10-26):**
on native 0.4.875.1 (`88331c0`; loaded filter
`73281443FE5D00E5A0536D6F55C7CDFE3E34F012F364233D3E357196D049B4E0`, RAM driver
`06723ACB5DC9E2B7BC1F5FD04217951903A3B4068D2038A6F345D417327030F5`),
with the plan-201 controller from `e35eefc`, mode 1 passed 14/14 on the lab W:
VHDX and the new lab mode 2 passed 11/11 on a temporary 16 GiB virtual SCSI disk
T:, under Verifier `0x209bb`. Both restored. Mode 2 forces every write in an
eligible small sealed group to the lower device before advancing without a
separate flush; the disk must report write-through support. The T: suite omits
file-level TRIM, which this VM's virtual SCSI disk refused, and the per-cache
setting check. This is no power-cut or broader storage qualification.

The first three-arm cost run
`QueueCache-Verify-20261026-175505-1bf4e36e3fb44506b83945aa3787e8c6`
stopped at its final mode-1 checkpoint: Windows' System process admitted 56 KiB
of metadata during the long drain, then 4 KiB in each of two rapid retries.
The cache stayed healthy and restoration succeeded. Its 26 measurements remain
an incomplete matrix. Plan 203 flushes the filesystem before each of the same
three permitted cache flushes; previously only the first attempt did that.
Host contracts cover retry order, the unchanged cap, retained failure evidence
and immediate fault refusal. The ordering performance cases have new IDs;
the completed plan-203 cost and sustained runs each collected all 27 inner cases,
passed their prescribed post-release byte checks with no driver errors and
restored successfully. Their evidence and the incomplete attempts were archived;
all 966 files were independently hash-verified before the temporary disk was
detached and its backing volume deleted. Final guest and Proxmox checks confirmed
its absence, the original disks and cache settings, stopped managed RAM disk,
restored applications and matching tested driver hashes. This does not add TRIM
or power-cut coverage. Results and evidence identities are in
[performance](PERFORMANCE.md#fast-ordering-31-fua-sustained).

**Issue #13 per-cache write-ordering setting, plans 198/199 (2026-10-26):**
write ordering became an advanced per-cache setting (app, `qcache policy apply
--write-ordering`, saved profiles), with no driver change. On 0.4.871.1 (`6b78d8a`)
under Driver Verifier `0x209bb`, `flush-order` passed 14/14, including the new
`flush-order/per-cache-setting` (apply with ordering, pause/resume, Strict with
ordering refused unchanged, a configuration without a choice keeps it, off clears
it). A CLI check refused ordering on C: with nothing changed, refused Strict with
ordering, and turned it on for W:, but `qcache policy remove W:` left the driver's
mode on for a volume without a cache. 0.4.872.1 (`0afffea`; loaded filter
`A8C18E0A717787C45C88ECEDCCE9B30679572B476958228B53E3168699ACEC4E`, RAM driver
`0D142C8F371B16DA8832AF0F99F983DE8185688F1D5E40A7E02A4C039E3E5581`) turns ordering
off when a cache is removed; under Verifier
`QueueCache-Campaign-20261026-154902-951a86e3365b4019824e24f97d38711c` passed 14/14
with that extra step, restored and passed `verify-completion`, and the same CLI
check ended with mode 0 after removal. Saved-profile restore at startup was not
exercised on the VM; host tests cover the profile's JSON and Apply's rollback.

**Issue #13 group-flush race fix, plan 197 (2026-10-26):** the first
`flush-order-sustained` run on 0.4.865.1
(`QueueCache-Campaign-20261026-122516-3327e5c4920a4f229e9a23819720b6bf`) stopped
after three of twelve cases when the diagnostics decoder rejected a draining group
past the open group. Telemetry showed the cause: right after the tar case deleted
its 16,000-file source tree, ordering was selected on the disabled cache, a TRIM
sealed a group, drainer zero began its lower flush with the cache lock dropped,
and the following cache release reset both group counters underneath it.
Restoration succeeded. Release 0.4.868.1 (`be1ad24`; loaded filter
`94044F378A60F96E5178393CC362C05F001D6542FE9C4EA85585034A36FE9BE7`, RAM driver
`97BD8FFB0E650FBD97B02BCB537F188F5BA96544CBD93E0C9206CD4379BB5D92`) makes release
wait for the group flush and bounds the frontier by the open group. Under Driver
Verifier `0x209bb`, `QueueCache-Campaign-20261026-130618-1873a3d813454f61b4b59ee95eb778a8`
passed all 13 ordering checks in 26 s. A second attempt stopped before any case
because run 1's retained 1.3 GiB payload left 5.89 GiB free (6 GiB required); that
payload was recorded with its SHA-256 and removed. The complete rerun then passed
all twelve cases with Verifier off
([result](PERFORMANCE.md#fast-ordering-13-sustained)). **Gap:** no lab hook holds a
group flush, so the race is not forced deterministically.

**Issue #13 single ordering mode, plan 196 (2026-10-26):** published release
0.4.865.1 (`f9ce425`), with modes 1 and 2 removed, was installed and its loaded
filter `BFC1994800A773C703963D178FC5E22D693B0154E2A9D33DDCF5B27B781C8902` and RAM
driver `CD7128DDDF63EA9B0737C3184C1FC7486C2ED9011B01FF0025DF74123B443235` hashes
checked. Under Driver Verifier `0x209bb` on both drivers,
`QueueCache-Campaign-20261026-113739-c2f4c4d8c6af4a939a8c2b0076af5e19` ran all
**13 ordering checks** with ordering on; the campaign completed in 26 s,
restored and passed `verify-completion`. The isolated write-through sealed exactly
one group. This is targeted correctness evidence for the single mode, not crash
or power-loss qualification.

**Issue #13 lab mode 3, plan 195 (2026-10-26):** published release 0.4.862.1
(`b6ec0e1`) was installed and its loaded filter
`1A19AFBD7FF2399A8E30504D5E7CE2EEF6EEF8A05A2637C08D7E11282660CAF3` and RAM driver
`9DE0E2ABD088A7B86BF02A5E29FBFC4A8860F2EED88E841B7D62A312F5D821E7` hashes
checked. Under Driver Verifier `0x209bb` on both drivers,
`QueueCache-Campaign-20261026-030922-25102e15f9a14d688c852a62482b2ec2` ran all
**39 ordering checks**, thirteen for each of modes 1, 2 and 3, in 34 s; the
campaign completed, restored and passed `verify-completion`. Mode 3 passed the
same held-predecessor, failed-flush/retry, write-through, TRIM, capacity,
mode-change, disable/reapply and direct-write fallback checks as modes 1 and 2.
The first attempt
(`QueueCache-Campaign-20261026-030530-542dbcfbeb6c443dbb0f77bcd7af1e28`) ran all
three modes, but the scenario's own completeness check still expected two modes
(26 checks) and rejected the result; `b410142` derives the count from the mode
list, with a host test, and the passing run used a controller published from it.
The driver was unchanged between the attempts. This is targeted correctness
evidence for mode 3, not crash or power-loss qualification.

**Issue #13 mode-2 follow-up, plans 191/192 (2026-10-25):** the loaded native
pair remained 0.4.851.1, with the same signed-package hashes recorded in
[the cost comparison](PERFORMANCE.md#fast-ordering-13-write-through). Published
controller 0.4.856.1 (`52ff318`) ran all **26 ordering checks**, thirteen per mode,
in 20.3 s; the complete campaign took 25.7 s and restored successfully. The new
checks exercised an oversized 2 MiB direct write over a sealed predecessor in a
1 MiB cache, and a deliberately unmappable paging write falling back to the
ordered direct path. Both modes preserved the latest bytes and an untouched
neighbor after cache release; the map fallback stayed healthy. The first
plan-191 attempt stopped at a fixture handle-sharing violation. Plan 192 fixes
that sharing and adds a real Windows host regression; it does not change the
driver. These 26 checks ran with Verifier **off**; the earlier 22 checks under
Verifier remain separate evidence.

The three cuts used the official durability writer, a fresh owned directory per
cut, Fast/Deferred with a 1 GiB budget, 1 s dirty age, 1024 KiB × 4 drains and an
intentional 100 ms drain delay. Q:'s backing cache was paused. Each oracle recorded
mode 2 and at least 256 observed write-through requests (258, 258 and 257).
The first writer used controller 0.4.855.1; subsequent writers and independent
guard checks used 0.4.856.1, with unchanged native code. These are correctness
observations, not speed measurements.

Each large file contains 65,536 checked 4 KiB chunks. An application flush in
Fast mode only seals an ordering group; the first column below is not a completed
QueueCache durability checkpoint.

| Cut | Dirty / in-flight bytes at witness | Application-flushed file: new / old / other / missing chunks | Write-through file: new / old / other / missing chunks | New small files intact / damaged / missing | NTFS scan |
|---|---:|---:|---:|---:|---|
| Pending burst | 519,680,000 / 4,194,304 | 14,848 / 50,688 / 0 / 0 | 0 / 65,536 / 0 / 0 | 0 / 0 / 10,000 | Exit 0, no problems |
| Advancing partial-drain boundary | 316,805,120 / 1,048,576 | 65,536 / 0 / 0 / 0 | 1,024 / 64,512 / 0 / 0 | 0 / 0 / 10,000 | Exit 0, no problems |
| Completed filesystem + QueueCache checkpoint | 0 / 0 | 65,536 / 0 / 0 / 0 | 65,536 / 0 / 0 / 0 | 10,000 / 0 / 0 | Exit 0, no problems |

The independently prepared, untouched durable guard (two 256 MiB files and 10,000
small files) matched exactly after **every** cut, with clean scans. The boundary
observer saw completed group flushes advance from 44 to 47 while data remained
pending. Reset requests followed the final healthy witnesses by 0.81, 0.53 and
0.40 s. The first two witnesses retained exactly two and four lifetime errors
from the explicitly owned ordering-suite fault checks; LastError was zero and no
additional error was accepted. The third witness had zero errors. No offline
repair or snapshot rollback was used. A VM reset does not test loss of power to
the physical storage, nor prove atomicity of a pending file update.

Writer / post-cut verification IDs, respectively:
`20261025-155934-25aca6727695430e8190104a568099cd` /
`20261025-161604-6fe105984ef1438a880f522cccde88c6`;
`20261025-161822-e03fedb6a2964fa8bc1ceaf8d8ade3cc` /
`20261025-162052-edc39f0554914c32bfafd30c34955940`;
`20261025-162157-186435c5776040b8ae38f0c611ba9993` /
`20261025-162500-ab49224200af4a739049d72e643347da`.
The 26-check campaign was
`QueueCache-Campaign-20261025-161709-0f0e836b85724fe2826e1e0a37fad255`.
UTC-aligned backend observations covered all writer, verification and cut
intervals at the documented cadences: no backup, scrub, recovery/backfill or new
health warning was observed, and serving-pool free space stayed above 256.047 GB.
Shared guest traffic continued; unavailable/reset guest counters remained unknown.
Both ordering modes stay default off; #13 remains open for its broader scope.

The safety evidence archive contains 22,018 files, including the recovered
payloads and failed fixture attempt; SHA-256
`51CB85054D775C59B74D05E28842E50991A04A2F12A3B85C78C01A88B08B1B41`.
Every archive entry was independently hash-verified after export. Only then were
20,012 owned fixture files in five W: directories removed, restoring 8.512 GB
free on W: and leaving 183.514 GB free on Q:. The temporary snapshot was deleted
without rollback before performance testing.

**Issue #13 follow-up, plans 186/187 (2026-10-25):** native 0.4.847.1
included the late usage-registration guard; both loaded hashes matched its signed
package. Controller 0.4.848.1 ran the nine-check suite and the two volatile cuts;
controller 0.4.849.1 ran the completed explicit-checkpoint cut. Native source was
unchanged between these packages. Verifier `0x209bb` targeted both drivers, Q:'s
backing cache was paused, and W: was the disposable non-OS NTFS lab volume.

All nine checks passed in 8.5 s, about 18 s for the complete campaign, with
restoration RESTORED and its terminal event validated:
`QueueCache-Campaign-20261025-051540-3863f11229624760a86c7e19feb79046`.
In addition to the six original checks, retained versions forced capacity waits
in an 8 MiB allocation; live mode changes returned ERROR_BUSY without changing
state; and disable/release/reapply preserved the newest bytes and untouched
neighbor. The RAM-first admission windows still added zero lower-I/O attempts.
The one deliberate lower-flush fault was explicitly retried by its own check.

| Cut | Dirty / in-flight bytes at witness | Result after normal NTFS recovery |
|---|---:|---|
| Burst, Fast Deferred 10 s, 2 GiB, 1024 KiB × 4 | 564,465,664 / 0; 537,063,424 already drained | Both changed 256 MiB files reverted completely to generation 1; all 10,000 new small files missing. Scan exit 0. |
| Partial drain after another group flush, 512 MiB, 128 KiB × 2, controlled 40 ms drain delay | 54,738,944 / 262,144; 175,497,216 already drained | All 1,000 kept base files and all 3,500 kept churn files at least 20 s old intact. Of 1,751 aged 10–20 s, 1,324 intact, 237 zero-filled, 190 missing; all 1,874 newest files missing. Some deletions rolled back. No foreign-content or unreadable files; scan exit 0. |
| Completed filesystem + QueueCache checkpoint, Fast Deferred 10 s, 2 GiB | 0 / 0 | Both 256 MiB files entirely generation 2 and all 10,000 small files exact; scan exit 0. |

The independent, untouched guard (two 256 MiB files plus 10,000 small files,
prepared uncached with a completed explicit checkpoint) was exact after **each**
cut, with a clean scan. The boundary observer saw completed group flushes advance
while later data remained pending. Reset was requested approximately 0.48, 1.04
and 0.84 s after the respective healthy witnesses; these are observed cut windows,
not a claim to interrupt one exact kernel instruction. The final witness retained
exactly the one lifetime error deliberately injected by the completed nine-check
suite; LastError was zero and no additional error was accepted. No offline repair
or snapshot rollback was used.

Writer/oracle run IDs, respectively:
`20261025-050204-688c6c258a864d0dbcae4bf3c7d4f11e` /
`20261025-050415-1af7720576594cc185d8b95c811a8f97`;
`20261025-050604-8ff2e4d4caad4a939f4abf9eb278600d` /
`20261025-050900-7957c27970164ca8a6b82da576666db7`;
`20261025-052318-a4d4ddd8257043e7b1e87e0cf91a7e74` /
`20261025-052538-d21416ca27ae47ba9a3c3873a2d1490c`.

Two preparations were not cuts: plan 185's new durability directory was removed
by general cleanup (fixed in plan 186), and plan 186's checkpoint check stopped
with 8 KiB newly dirty and no driver error. Plan 187 records admission immediately
around Flush and permits at most three calls only if accepted writes advanced.
The successful checkpoint used two: the first observed a new 4 KiB admission;
the second drained the later tail, with no error increase. Existing pending work
without new admission, errors and identity changes still fail. All incomplete
attempts and their raw evidence remain separate.

UTC-aligned Ceph observations covered these runs without gaps beyond their 5 s
traffic / 15 s pool-and-task cadences. No backup, scrub, recovery or new health
warning overlapped them; ordinary other-guest I/O remained present. Minimum pool
free space across the cut intervals was 261.2 GB. The intentionally offline pools
were separate from the VM's pool. The same-build plan-187 short cost screen
completed all 12 cases but measured 4.9–5.0× total durable time for 256 flush-heavy
files. Sequential pairs disagreed, and metadata I/O left the resident read timing
control unqualified ([measurements and limits](PERFORMANCE.md#fast-ordering-13-cost)).
This bounded evidence supports keeping the prototype default off; it does not
close #13 or qualify system/special-file paths. Actual late usage registration
under mode 1 was not exercised live; compile-time contracts cover that guard.
Direct/oversized bypasses, special-file growth and storage-wide power loss still
need their own qualification if the supported scope expands.

The complete guest evidence archive contains 690 hash-verified files, including
all incomplete preparations and raw scans: `QC13Followup-20261025-evidence.zip`,
SHA-256 `822A4BFE58B52EA479254F595961B07E73BEC81DCDEDC9C7F4A62B4B3E115B63`.
The temporary snapshot was deleted without rollback; installed 0.4.849.1, ordering
off, Verifier off, the original Q: profile and applications were restored. After
export, five run-owned W: workload directories were removed. Free space at cleanup
was C: 55.24 GiB, Q: 170.91 GiB, V: 7.97 GiB and W: 7.93 GiB. The original stopped
1 GiB R: definition was preserved. The separately transferred cleanup receipt has
SHA-256 `7472143A1303746783B5F5449987CCC8356D6E0C1881CF62B2996B5EF925E432`.

**Issue #13 experimental ordering, plan 176 (2026-10-24):** controller 0.4.835.1
against native 0.4.834.1 (driver source unchanged) completed
`QueueCache-Campaign-20261024-173926-2160192872334652b812e53814703163`:
all six checks PASS, restoration RESTORED, durable completion event validated.
The test took 4.6 s; the complete one-phase campaign took about 16 s. Both loaded
module hashes were matched to the signed 0.4.834.1 package; Verifier `0x209bb`
targeted the filter and RAM provider. Q:'s backing cache was paused by the campaign.

The admission and write-through windows added zero lower read/write/flush
attempts while retaining an old dirty version. With two drainers and an older
write held after lower completion, the highest submitted group remained 1 while
the current writing group was 2. An injected group-flush failure retained the
newer 4096 dirty bytes and frontier 1; explicit Retry and post-release byte checks
passed. Asynchronous and synchronous TRIM/rewrite checks passed, including the
untouched neighbor. No power cut or performance comparison is claimed by this run.
The earlier plan-175 run was INCOMPLETE because its observer used the same
synchronous device handle as the blocking Flush; plan 176 uses a separate handle.
Both runs restored their original cache state. The prototype remains off by default.

**Issue #13 single churn cut, native/controller 0.4.837.1 (2026-10-24):**
`QueueCache-Verify-20261024-191159-6ff745d708354afdba06e682ef614397`
used the maintained `compare-durability-churn` workload on disposable NTFS W:,
experimental ordering enabled, Fast Deferred 10 s, 512 MiB, 1024 KiB × 2,
Verifier `0x209bb`; Q:'s backing cache was paused. Both loaded hashes matched
the published package. The external hypervisor observer reset VM109 immediately
after a completed write phase and a healthy observation of 12,165,120 dirty bytes,
475,004,928 drained bytes and no in-flight bytes. A sample 23 s earlier showed
372,363,264 drained bytes: write-out progressed while newer work remained pending.
Ordering showed current group 183, draining group 181, 180 completed group flushes
and zero flush failures. The first attempt drained before reset and is not a cut.

After normal Windows startup and lab reattachment, the exact oracle check
`20261024-191653-c33408231e4640729fe6af5a4ff35186` found all 1,000 kept base files
and all 5,251 churn files at least 10 s old intact. Of 1,874 newest kept files,
1,026 were intact, 271 contained zeros and 577 were missing. Some recent deletions
were lost. There were no unreadable files; `chkdsk /scan` returned 0. No manual
repair or snapshot rollback was used; normal NTFS recovery still applies.

An independent untouched guard was prepared with `compare-durability-write`
while W: was uncached, then an explicit QueueCache lower Flush, before enabling
the experimental cache. Post-cut run
`20261024-191706-9e2a9544c0c74985a4f8dcc9e3bbc7ab` found both 256 MiB files
100% generation-2 bytes and all 10,000 small files exact; its scan also returned 0.
The complete five-run evidence archive SHA-256 is
`E33A9EB0892F7DABAECBFCBA953E17E803008412E7DD9EA6C08DD1A1BDA2DBB3`.
This single cut is supporting evidence, not a guarantee, a Strict control or a
performance comparison. Default promotion, system/special-file paths and broader
crash/capacity/lifecycle qualification remain open in #13.

**Ordinary Fast smoke, 0.4.839.1 (2026-10-24):**
`20261024-192914-cc5b5f3b9fcc4ae9adc38ee29c366573` used the original enabled
2 GiB Fast Q: profile with experimental ordering off, Verifier off. The six
current-boot file checks passed: four 64 MiB write/overwrite passes with live reads,
2048 random 4 KiB overwrites, explicit flush/reopen, hashed copy/rename, owned
deletion and settings/health. State and error count were preserved. Four scope
checks were SKIP, including file-level TRIM (Windows refused it with error 326),
TRIM notification observation and the deliberately excluded crash/race/pressure
paths. An initial paused-cache run also passed but does not prove the Fast path.
Both exact-run records are retained; archive SHA-256
`8C018D1C4043BC7E845B01FAC5DBCAF198E17FD5E77A07B4B91A0E06953C0118`.


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
