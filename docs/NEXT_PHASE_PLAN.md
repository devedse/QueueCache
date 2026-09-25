# Next phase plan

Written 2026-09-25 after installed 0.4.117.1 / tools plan 47. The
[tracker](RAM_FIRST_IMPLEMENTATION_TRACKER.md) stays the source of truth for
status and evidence; this page orders the remaining work. Each item names the
tracker IDs it closes and when it counts as done.

## Where things stand

- Ordinary application writes (buffered, flushed, memory-mapped) are cached in
  RAM; paging-file traffic is recognised per request and never cached (T085).
- Cache memory is page-backed, not kernel nonpaged pool (T085).
- Paging reads cannot be stuck behind the write path (T082); ordering, fault and
  cancellation orders are proven with a lab gate (T083); lower I/O is attributed
  by source (T084).
- C: active restarts: 1 runtime-only and 4 saved-profile cycles with up to 137 MB
  dirty and memory pressure beyond available RAM; every byte survived. A real
  Paint/Photos session survived drain (T080, T081, T086).
- Fixed this phase: bugcheck 0x7E after cache release; bugcheck 0x7A during
  restart with dirty C: data.

## Phase 1: stabilise (finish A06a-A10)

Order matters: the open hang first, because everything after restarts machines.

1. **Shutdown hang (open, A09).** One saved-profile restart hung on
   "Restarting" (1 of 6). The VM now writes a kernel dump on NMI.
   - Add shutdown-phase breadcrumbs to diagnostics (barrier start, drain
     progress, lower flush, D3) so a dump shows where it stopped.
   - Bound the shutdown drain wait. If the disk stops making progress, report
     and continue instead of waiting forever.
   - Soak it: run unattended restart cycles (at least 20) with a saved C:
     profile. The restarts are driven from outside the VM, never by the runner.
   - Done when: the cause is known and fixed, or 20+ clean cycles pass with the
     bound in place.
2. **Power transitions (A09, T072).** Sleep/resume, and Strict-mode restart.
   Hibernate/Fast Startup need a VM that supports them.
   - Done when: each transition passes a byte oracle with the cache active.
3. **Remaining fault and teardown cases (A06a/A08, T052-T053, T083).**
   - Cancel an in-flight paging request.
   - Inject a fault on a direct paging write.
   - Allocation failure under memory pressure.
   - Races between Release/Remove and I/O.
   - Done when: each is a maintained `qcache developer verify` case that passes.
4. **Offline recovery and Retry UI (A10, T054).**
   - Real Safe Mode/offline recovery of a machine whose cache cannot start.
   - A Retry button in the desktop app for a faulted cache.
   - Done when: recovery is exercised on the VM from a genuinely unbootable
     state, and the UI is covered by desktop tests.

## Phase 2: performance (A11)

5. **Drain tuning (T050).** Use `drain-decision` results to choose batch size and
   parallelism defaults. Remember that NTFS metadata and zero-fill write-back now
   share drain intervals.
6. **Keep read misses in RAM.** Paging read misses (what apps read from disk) are
   not kept yet. Retain them as clean entries so the next read is served from
   RAM.
7. **Shrink the cache under memory pressure.** The budget is fixed today. Give
   memory back when Windows runs low, without dropping dirty data.
8. **Final measurement matrix.**
   - `write-performance` (72 cases), `full`, `app-write-profile` timings, and
     CrystalDiskMark with the same DiskSpd binary.
   - Before/after tables for the release notes.

## Phase 3: release readiness (A12-A16)

9. Private-alpha freeze and reporting handoff (A12).
10. Support contract and safety gaps (A13), signing/servicing/security (A14).
11. Endurance and environment matrix (A15), release process (A16).

## Test VM notes

- Fixed 4 GiB C: pagefile (a 512 MiB pagefile never reaches the pagefile under
  bounded pressure).
- Kernel memory dump, dump on NMI (`CrashDumpEnabled=2`, `NMICrashDump=1`).
  Minidumps stay enabled.
- On an 8 GiB VM, Q:'s saved 2 GiB profile plus a 1 GiB C: cache leaves too
  little headroom right after boot. Release Q: for the boot (runtime-only), or
  remove its profile, before C: tests.
- Verification output belongs on Q:; test directories on C:
  (`QueueCache-System-*`) are retained evidence.
