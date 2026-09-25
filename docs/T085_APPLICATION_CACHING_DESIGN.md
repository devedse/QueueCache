# T085: caching ordinary application traffic (design, for owner approval)

Status: proposal, 2026-09-25. Nothing here is implemented yet.

## Problem, measured

Windows keeps ordinary file writes in its own file cache and later writes them to
disk as *paging-marked* requests. Memory-mapped files write the same way. The
driver currently sends every paging-marked request straight to the disk, so
ordinary applications get no RAM caching from QueueCache. Plan-41
`app-write-profile` on Q: with a 1 GiB Fast cache on installed 0.4.106.1 (run
`QueueCache-Verify-20260925-062302-a0513f1b...`) measured 256 MiB per mode:

| Mode | Application wait | Reached QueueCache as |
|---|---|---|
| Buffered write + close | 1.6 s (Windows file cache); write-out took 33 s | 0 MiB admitted, 256 MiB paging writes forwarded |
| Buffered write + explicit flush | 9.1 s (28 MiB/s) | 0 MiB admitted, 256 MiB paging writes forwarded |
| Memory-mapped write + flush | 3.7 s | 0 MiB admitted, 256 MiB paging writes forwarded |

The explicit-flush case is where a Fast RAM cache helps most. The flush could
complete at RAM speed while disk writes continue in the background.

## Why the current rule exists

`IRP_PAGING_IO` marks pagefile traffic too. Holding swapped-out memory in a RAM
cache is circular: the system pages memory out to *free* RAM. It can also
deadlock if completing a pagefile write ever depends on the memory it is trying
to free. A deliberately narrower rule is common practice for block caches:
cache every block request except the pagefile.

## Proposed change

1. **Special-file range map per disk.** For each cached volume the controller
   (SYSTEM) computes the disk ranges of `pagefile.sys`, `swapfile.sys`,
   `hiberfil.sys` and configured dump files. It uses `FSCTL_QUERY_FILE_LAYOUT`
   on the volume handle, which reports extents of in-use files, plus the
   volume's disk extent. It sends the map to the driver with a new IOCTL. The
   driver stores a bounded sorted range list with a generation number.
2. **Classification in the driver.** A paging-marked read or write that lies
   entirely outside the special ranges, on a disk whose map is current, is
   treated like any other write or read. A write is admitted to RAM (same
   admission, capacity backpressure, ordering and Strict/Fast semantics). A read
   may be served from RAM and may populate clean read cache. Anything inside or
   overlapping a special range, and any request while the map is missing or
   stale, keeps today's path: ordered direct I/O without RAM retention. Unknown
   always means today's behaviour.
3. **Freshness.** The map is rebuilt on every paging/hibernation/dump usage
   notification, on Enable, and periodically (for example every 30 s). A
   system-managed pagefile can grow into new extents between refreshes. For
   the system disk, activation therefore requires a fixed-size pagefile (the
   VM already uses one) or it keeps paging traffic uncached. The controller
   reports which mode is active.
4. **No new memory on the paging path.** Admission uses only the preallocated
   slots. If mapping the request buffer fails under pressure, the request falls
   back to the ordered direct path instead of failing.
5. **Flush semantics unchanged.** Fast acknowledges application flushes from RAM
   (the existing volatile contract the owner accepted). Strict drains to disk
   before completing a flush, as today.

## Verification before enabling on C:

- Q: `app-write-profile` must show the 256 MiB arriving as RAM-admitted bytes
  and a much shorter explicit-flush wait. It must also verify all bytes after
  release.
- Re-run `paging-coherence`, `ordering-faults`, `policies` and `pressure`
  unchanged.
- New Q: case: a synthetic "special range" map covering part of a test file
  must keep that part on the direct path while the rest is admitted.
- C: only after that: a bounded memory-pressure run with the fixed pagefile
  active and paging observed, then the Paint/Photos and reboot checks (T080/T081).

## Open questions for the owner

- Accept "fixed-size pagefile required for full caching on the system disk"?
- Start with non-system disks only (lowest risk), then enable C: after the
  pressure test?
