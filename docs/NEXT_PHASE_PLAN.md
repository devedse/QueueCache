# Next phase plan

Written 2026-09-27 after installed 0.4.148.1 / tools plan 57. The
[tracker](RAM_FIRST_IMPLEMENTATION_TRACKER.md) stays the source of truth for
status and evidence; this page orders the remaining work. Fast mode is the product
focus; Strict stays correct but is not tuned (owner decision, 2026-09-27).

## Where things stand

Update 2026-09-28 (installed 0.4.166.1): the Fast-mode request-path work (items
4-6 below) is done: CrystalDiskMark SEQ1M Q8T1 about 36/21 GB/s read/write
(was 14.4/14.0), RND4K Q1T1 about 960/850 MB/s (was 100/79). The saved-C: soak
found that plan 56's read retention of page-ins served wrong data to programs on
C:; it is withdrawn (KNOWN_ISSUES) and a program-file check now runs every soak
cycle. New open item: a read miss is kept from the application's own buffer
(KNOWN_ISSUES; proposed staging-buffer fix, to do before any further read-caching
work).

Earlier status (2026-09-27):

- Every maintained suite passes under Driver Verifier (standard checks) on
  0.4.148.1, plus saved-profile C: restart soaks (20/20 on 0.4.146.1, 10/10 on
  0.4.148.1) with about 100 MiB dirty and pagefile pressure.
- Fixed this phase: a shutdown deadlock on the paging-path usage notification
  (0.4.128.1), a failed lower IRP allocation faulting the cache (0.4.131.1), and a
  failed settings change leaving the cache disabled (tools, `3b33de7`).
- New behaviour: paging-file I/O bypasses the request worker; application read
  misses are kept with scan-resistant insertion; range-aware TRIM; default drain
  parallelism 2; status output separates live values from totals.
- Measured (Fast/Idle vs off, Microsoft DiskSpd 2.3, 2 GiB): random 4 KiB Q1
  ~18x, Q32 ~19x, sequential 1 MiB Q1 ~43x, Q8 ~32x; an application's buffered
  save+flush waits 107 ms instead of 9.1 s.

## Postponed by the owner (2026-09-27)

- Sleep/resume, hibernate and Fast Startup (the VM offers no sleep state).
- Offline/Safe Mode recovery and the desktop Retry button (T054).
- Physical disk removal (hot-unplug) tests.

## Done (Fast-mode request path, 0.4.153.1-0.4.166.1)

Parallel copies (SEQ1M Q8T1 about 36/21 GB/s), the random Q32 gap (idle drainers
woke on every write), and caller-thread service (random 4 KiB Q1 about 10x).
Details: [WRITE_PERFORMANCE_TRAJECTORY.md](WRITE_PERFORMANCE_TRAJECTORY.md).

## Step 1: safety first (in progress, 2026-09-28)

1. **Read misses kept only from driver-owned memory: DONE (plan 62, 0.4.169.1).**
   A program changing its buffer mid-read could put its bytes in the cache for
   other programs. `policies/read-miss-isolation` failed on 0.4.166.1 (16 of
   16 MiB poisoned) and passes on 0.4.169.1 under Driver Verifier.
2. **Settings rollback on the driver: DONE (plan 63, 0.4.169.1).** Lab faults 6/7
   fail a resize once; the previous settings were back and the same change
   applied afterwards, under Driver Verifier.
3. **TRIM on a discard-capable disk (N6): blocked by Windows, not the VM
   settings.** Discard and SSD emulation are on; Windows 11 stopped sending TRIM
   to VirtIO SCSI disks after its May 2026 update (KNOWN_ISSUES). Options: test on
   a VHDX-backed disk inside Windows (works through our filter), and/or a small
   SATA test disk. Then add a maintained case: trimmed dirty data never reaches
   the disk, a partly trimmed block keeps its other sectors, no whole-cache wipe.
3a. **Raw disk commands on a cached disk (new).** Forward read-only SCSI/ATA
   pass-through and SMART reads from the caller's thread instead of refusing them
   or draining the cache for them (KNOWN_ISSUES).

## Step 2: measure what we have

4. Re-run `write-performance` for a new maintained baseline.
5. Multi-threaded rows (T1/T2/T4); if limited, allow several read hits at once.
6. Read-caching benefit: read, evict Windows' cache, re-read, compare with caching
   off; a hot set must survive a large one-off read.

## Step 3: optional speed work

7. SEQ1M Q1 (about 14.5 GB/s): split one large caller-path copy across threads.
8. Release cache space held by deleted files' data on disks without TRIM.
9. Drain shape: larger, disk-ordered batches when a backlog builds (confirm on
   physical NVMe/HDD first).
10. Warm start: reload the hot read set after boot.

## Step 4: normal-use robustness

11. Adding or removing a pagefile while caching is active (A08).
12. Exact flush cutoff under concurrent writers (T052), now more important with
    caller-thread service and parallel copies.
13. Installer, upgrade and uninstall failure matrix and final user documentation
    (A10).

## Step 5: release readiness (A12-A16)

14. Private-alpha freeze and reporting handoff (A12).
15. Support contract and safety gaps (A13); signing, servicing and security,
    including CodeQL/SDV in CI (A14).
16. Endurance and environment matrix with Driver Verifier enabled (A15); release
    process (A16).

## Test VM notes

- Fixed 4 GiB C: pagefile; kernel memory dump to `Q:\MEMORY.DMP`, dump on NMI.
- Standard Driver Verifier is left enabled on the installed driver for
  correctness runs; turn it off (`verifier /reset`, reboot) before performance
  runs. Verifier settings name the driver file, which changes with every build.
- DiskSpd: `C:\Tools\DiskSpd\amd64\diskspd.exe` (Microsoft 2.3, SHA-256
  `DD4E57E1...FAEA2`). WinDbg's `cdb` is copied to `C:\Tools\WinDbg\amd64`.
- The host-side restart driver used for soaks lives outside the repository
  (`~/QueueCache-Evidence`); cycles run the supported `system-files`,
  `system-paging-recognition` and `system-post-restart` suites.
