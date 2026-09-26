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

Order matters: the hang first, because everything after restarts machines;
then Driver Verifier, because it can invalidate paths we consider finished.

1. **Shutdown hang (A09): DONE 2026-09-26.** The 0.4.125.1 fix passed 20/20
   soak cycles but was insufficient: under Driver Verifier the first restart
   hung the same way (the fault is inside the worker's own `IoCallDriver`). The
   0.4.128.1 fix forwards PnP/shutdown from a work item and passed 20/20 cycles
   under Driver Verifier. A
   host-driven saved-profile soak reproduced it on cycle 3; the NMI kernel dump
   showed the request worker waiting on a forwarded paging usage notification
   while a pagefile read it needed sat in its own queue (see KNOWN_ISSUES).
   Breadcrumbs and a bounded drain were not needed: the kernel dump showed every
   thread, and a bound that gave up on unwritten Fast data would break
   normal-restart persistence.
   - Each cycle ran `system-files`, `system-paging-recognition`, a host-driven
     restart and `system-post-restart`. Reuse the same cycle for the power and
     Strict work below.
2. **Driver Verifier (A06a/T053, A07-A09).** Before new lifecycle work, rerun
   existing evidence with Windows Driver Verifier on the QueueCache driver so
   IRP, IRQL, pool and kernel-API violations bugcheck at the fault (0xC4).
   - Enable `verifier /standard` for the installed driver file (its name
     changes with every build) plus `/bootmode resetonbootfail`, because C: is
     the boot disk.
   - Pass A: `quick`, `policies`, `pressure`, `paging-coherence` and
     `ordering-faults` on Q:, then the 20-cycle saved-profile restart soak on C:.
   - Pass B: add low-resources simulation (random allocation failures) and
     rerun the Q: suites; this is the T053 allocation-failure evidence. Byte
     errors or unexplained faults fail; cleanly reported allocation failures are
     the expected outcome to inspect.
   - Done when: pass A has no Verifier bugcheck and pass B's failures are all
     reported cleanly with correct bytes.
   - Pass A DONE on 0.4.128.1 (after it found the shutdown deadlock on
     0.4.125.1).
   - Pass B PARTIAL: at 6% injection every `Configure` in `policies`,
     `pressure`, `paging-coherence` and `ordering-faults` failed cleanly
     (Win32 1450, cache untouched, restoration clean, no bugcheck; 16 deliberate
     failures). `quick` completed three times with injection active. Live-I/O
     allocation failures (`LowerIo`'s IRP build, request MDL mapping) were not
     attributably hit; that still needs a targeted T053 case (for example a lab
     fault on those two call sites) rather than random injection.
3. **Power transitions (A09, T072).** Sleep/resume, and Strict-mode restart.
   The current VM offers no sleep state at all (`powercfg /a`: S1-S3, S0 low
   power idle, hibernate and Fast Startup unavailable; firmware and the display
   adapter). Sleep/resume and hibernate need the owner to enable S3/S4 on the
   hypervisor side.
   - Strict restart DONE on 0.4.128.1 under Driver Verifier: 10/10 saved-profile
     cycles, every byte matched.
   - Done when: each transition passes a byte oracle with the cache active.
4. **Remaining fault and teardown cases (A06a/A08, T052-T053, T083).**
   - Cancel an in-flight paging request.
   - Inject a fault on a direct paging write.
   - Allocation failure under memory pressure.
   - Races between Release/Remove and I/O.
   - Done when: each is a maintained `qcache developer verify` case that passes.
5. **Offline recovery and Retry UI (A10, T054).**
   - Real Safe Mode/offline recovery of a machine whose cache cannot start.
   - A Retry button in the desktop app for a faulted cache.
   - Done when: recovery is exercised on the VM from a genuinely unbootable
     state, and the UI is covered by desktop tests.

## Phase 2: performance (A11)

6. **Drain tuning (T050).** Use `drain-decision` results to choose batch size and
   parallelism defaults. Remember that NTFS metadata and zero-fill write-back now
   share drain intervals.
7. **Keep read misses in RAM.** Paging read misses (what apps read from disk) are
   not kept yet. Retain them as clean entries so the next read is served from
   RAM.
8. **Shrink the cache under memory pressure.** The budget is fixed today. Give
   memory back when Windows runs low, without dropping dirty data.
9. **Final measurement matrix.**
   - `write-performance` (72 cases), `full`, `app-write-profile` timings, and
     CrystalDiskMark with the same DiskSpd binary.
   - Before/after tables for the release notes.

## Phase 3: release readiness (A12-A16)

10. Private-alpha freeze and reporting handoff (A12).
11. Support contract and safety gaps (A13), signing/servicing/security (A14).
    Add static analysis (CodeQL driver queries, which production driver signing
    expects, and/or SDV) to CI.
12. Endurance and environment matrix (A15) with Driver Verifier enabled,
    release process (A16).

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
