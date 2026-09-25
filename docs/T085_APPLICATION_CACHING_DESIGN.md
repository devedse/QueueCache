# T085: caching ordinary application traffic, and page-backed cache memory

Status: implemented in source (plan 42), 2026-09-25. See the tracker for VM evidence.

## Problem, measured before the change

Windows keeps ordinary file writes in its own file cache and later writes them to
disk as *paging-marked* requests. Memory-mapped files write the same way. The
driver used to send every paging-marked request straight to the disk, so
ordinary applications got no RAM caching from QueueCache. Plan-41
`app-write-profile` on Q: with a 1 GiB Fast cache on 0.4.106.1 (run
`QueueCache-Verify-20260925-062302-a0513f1b...`) measured, for 256 MiB per mode:

| Mode | Application wait | Reached QueueCache as |
|---|---|---|
| Buffered write + close | 1.6 s (Windows file cache); write-out took 33 s | 0 MiB admitted, 256 MiB paging writes forwarded |
| Buffered write + explicit flush | 9.1 s (28 MiB/s) | 0 MiB admitted, 256 MiB paging writes forwarded |
| Memory-mapped write + flush | 3.7 s | 0 MiB admitted, 256 MiB paging writes forwarded |

## Why paging-file traffic stays uncached

`IRP_PAGING_IO` also marks pagefile traffic. Holding swapped-out memory in a RAM
cache is circular: the system pages memory out to *free* RAM. It can also
deadlock if completing a pagefile write ever depends on the memory it is trying
to free. Only paging-file traffic is excluded. Everything else that reaches the
disk is cached.

## Design

1. **Per-request recognition, no layout map.** Each request from the file system
   still identifies the file it belongs to. Below the volume the filter's own
   stack location has no file object (plan-42 VM evidence: every paging request
   counted `NoFileObject`, nothing admitted). The file is found, in order, in the
   current stack location, the request's original file object (the file system
   passes its paging request down), or, for a request the file system split, the
   master request's original file object or file-system stack location. Each stays
   referenced while the request is outstanding; a pointer that is not a file
   object counts as unknown. A paging-marked request is application traffic,
   eligible for normal RAM admission, when all of these hold:
   - its originating file object is found;
   - the check runs at PASSIVE/APC level (the documented contract of
     `FsRtlIsPagingFile`);
   - `FsRtlIsPagingFile(fileObject)` is false;
   - it touches no *force-direct* range.
   Otherwise it keeps the pre-T085 ordered direct path. A growing pagefile keeps
   its file object, so growth needs no refresh. The same holds for
   `swapfile.sys`, which Windows treats as a paging file. Hibernation and crash
   dump writes use the dump stack and never pass through this filter.
2. **Unknown means direct.** A request without a file object, or seen above
   APC_LEVEL, is never admitted.
3. **Same machinery as any write.** Admitted paging writes use the normal
   admission, capacity backpressure, sector ownership, ordering and Strict/Fast
   semantics. If the request buffer cannot be mapped under memory pressure, the
   write falls back to the ordered direct path instead of failing.
4. **Range sets for verification only** (`IOCTL_QCACHE_SPECIAL_RANGES_V1`):
   - *Force-direct* ranges keep matching paging traffic on the direct path, so
     maintained tests can still exercise it on Q:.
   - *Reference* ranges are observe-only paging-file extents. The driver counts
     any request inside them that recognition would have admitted
     (`PagingReferenceMisses`). The guarded C: suite requires zero.
   The maintained restore job clears both sets.
5. **Cache memory outside the nonpaged pool.** Each 256 KiB payload slab is
   physical memory from `MmAllocatePagesForMdlEx` (fully required, cached),
   mapped once, kernel-only and no-execute. Only small bookkeeping (slot
   descriptors, index, slab table, drain staging) uses nonpaged pool. The
   configured budget covers all of it. Other drivers' nonpaged-pool headroom is
   no longer consumed by the cache.

## Verification (plan 42)

- Q: `app-write-profile`: every mode must admit at least 90% of its bytes to RAM,
  and configuring a 1 GiB cache may add at most 10% of the budget to kernel
  nonpaged pool.
- Q: `paging-coherence`, `ordering-faults`, `policies`, `pressure` unchanged in
  intent. Direct-path stages mark their owned block force-direct.
- C: `system-paging-recognition` (guarded, no cache configuration or files):
  reference paging-file extents plus bounded memory pressure. Paging-file I/O
  must be recognised with zero reference misses.

## Remaining limits

- Paging *reads* are served from RAM when resident, but read misses are still not
  retained as new clean cache entries.
- The cache does not yet shrink under system memory pressure; the budget stays
  fixed.
- Active C: caching of application traffic still needs the T081 pressure and
  restart checks.
