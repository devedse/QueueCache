# Next phase plan

Written 2026-09-27 after installed 0.4.148.1 / tools plan 57. The
[tracker](RAM_FIRST_IMPLEMENTATION_TRACKER.md) stays the source of truth for
status and evidence; this page orders the remaining work. Fast mode is the product
focus; Strict stays correct but is not tuned (owner decision, 2026-09-27).

## Where things stand

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

## Phase 1: verify and measure what was just built

1. **TRIM on a discard-capable disk (N6).** Enable discard on Q:'s virtual disk
   (owner), then run `trim-file` and `trim-diagnostic` and add a maintained case:
   cached dirty data inside a trimmed range must not reach the disk, a partly
   trimmed block keeps its other sectors, and no whole-cache wipe happens.
2. **Read-caching benefit (N2).** Add a measurement: read a data set, evict it
   from Windows' own cache, read it again and compare with caching off. Add a
   scan-resistance case: a hot set stays cached across a large one-off read.
3. **Settings rollback on the VM (N1).** Force a failed resize with existing lab
   fault 6/7 during Apply and check the previous settings are restored.

## Phase 2: Fast-mode performance

4. **Parallel data copies: DONE (0.4.158.1-0.4.162.1).** Large RAM-hit reads and
   the payload copies of large fitting writes run on three offloaded-request
   threads; the cache lock is a push lock. CrystalDiskMark SEQ1M Q8T1 rose from
   about 14.4/14.0 to 36.4/20.7 GB/s read/write (WRITE_PERFORMANCE_TRAJECTORY).
   Follow-ups: measure multi-threaded rows (T4); release cache space held by
   deleted files' data on disks without TRIM; optionally split one large
   caller-path copy across threads (SEQ1M Q1 stays near one copy's speed).
5. **Random Q32 gap: EXPLAINED AND FIXED (0.4.154.1).** Drainers above the
   configured parallelism woke on every cached write while write-back ran,
   collapsing random Q32 writes to about 35,000/s, which is where 0.4.148.1's
   `write-performance` Q32 medians sat. Now about 310,000/s with CrystalDiskMark's
   DiskSpd. Re-run `write-performance` for a new maintained baseline.
6. **Request-path cost: DONE (0.4.153.1-0.4.162.1).** RAM hits and fitting writes
   on an otherwise idle disk are served on the caller's thread (random 4 KiB Q1
   about 10x); deep queues stay on the worker, which polls 30 us before sleeping.
7. **Drain shape.** Larger batches (512 KiB-1 MiB) and disk-order batching when a
   backlog builds, measured with `drain-decision` and `write-performance`; confirm
   on a physical NVMe or HDD before changing defaults.
8. **Optional: warm start.** Remember the hot read set at shutdown and reload it
   in the background after boot.

## Phase 3: remaining normal-use robustness

9. Adding or removing a pagefile while the cache is active (paging-role
   transitions, A08).
10. Exact flush cutoff under concurrent writers (T052).
11. Installer, upgrade and uninstall failure matrix and final user documentation
    (A10).

## Phase 4: release readiness (A12-A16)

12. Private-alpha freeze and reporting handoff (A12).
13. Support contract and safety gaps (A13); signing, servicing and security,
    including CodeQL/SDV in CI (A14).
14. Endurance and environment matrix with Driver Verifier enabled (A15); release
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
