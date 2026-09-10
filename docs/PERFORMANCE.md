# Performance baseline and method

Measured numbers for the bounded RAM cache, plus the method used to get them. Every figure
below is **experimental lab data from one virtual machine**, not a product benchmark or a
certification. Read [cache policies](CACHE_POLICIES.md) for the settings these runs used.

## Method

| Item | Value |
|---|---|
| Target | Disposable secondary NTFS volume (`Q:`) on a non-OS QEMU/Proxmox virtual disk, 200 GiB |
| Cache | 4096 MiB, Fast preset, Automatic allocation, retention and promotion on, batch 256 KiB, parallelism 1 |
| Tool | The CrystalDiskMark-bundled `DiskSpd64.exe`, `-S -L -Zr`, 3–10 s warm-up plus 10 s measured |
| File | One pre-created 2 GiB file on the cached volume; no raw-device or whole-disk targets |
| Counters | `qcache policy status --json` deltas around each run: accepted, drained, coalesced, lower/batched writes, read hits/misses, throttle waits, evictions |
| Drain | Explicit `qcache policy flush` after each measured window, timed separately from the score |

Reproduce with the lab harness (ignored, machine-specific paths): `.lab/Bench-QueueCache.ps1`,
raw results in `.lab/bench-<timestamp>.csv` plus one DiskSpd transcript per cell.

Two practical notes for anyone repeating this:

- A benchmark launched over SSH must be **detached** (scheduled task, run as SYSTEM), otherwise
  Windows terminates it when the SSH session ends and the matrix stops halfway.
- Write scores are **RAM acceptance scores, not disk throughput**. In the sequential run below,
  146,058 MiB were accepted while 3,028 MiB reached disk: 98% was coalesced by repeatedly
  overwriting one 2 GiB file. Always report accepted, coalesced and drained bytes next to a score.

## Baseline, 2026-09-10 (driver 0.4.14.1)

One sample per cell. `off` means the cache is paused, same disk and file.

### Throughput and latency

| Workload | off | Eager | Balanced | Idle |
|---|---|---|---|---|
| seq write 1 MiB QD8 T1 | 111 MiB/s | 11,233 MiB/s | 8,013 MiB/s | 7,761 MiB/s |
| seq read 1 MiB QD8 T1 | 237 MiB/s | 11,835 MiB/s | 7,986 MiB/s | 7,571 MiB/s |
| rnd write 4 KiB QD1 T1 | 1,216 IOPS | 20,070 IOPS | 21,205 IOPS | 21,108 IOPS |
| rnd write 4 KiB QD32 T1 | 1,234 IOPS | 27,931 IOPS | 27,055 IOPS | 29,625 IOPS |
| rnd write 4 KiB QD8 T4 | 1,225 IOPS | 25,204 IOPS | 24,504 IOPS | 24,674 IOPS |
| rnd read 4 KiB QD32 T4 | 2,871 IOPS | 285,275 IOPS | 283,735 IOPS | 277,568 IOPS |
| mixed 70/30 4 KiB QD8 T4 | 2,018 IOPS | 55,805 IOPS | 54,563 IOPS | 54,433 IOPS |

### Request-path serialization

All cached I/O for one disk runs on a single request worker thread
(`RequestWorker`, `driver/qcache/lab.cpp`). The measurements match that exactly:

| Measure | Value |
|---|---|
| 4 KiB write scaling, QD1 → QD32 | 20,070 → 27,931 IOPS (**1.4x for 32x the queue depth**) |
| 4 KiB write, 4 threads versus 1 at equal total queue depth | 25,204 versus 27,931 IOPS (**worse**) |
| Little's law check, QD32 | 32 / 27,931 IOPS = 1.145 ms versus **1.137 ms measured average** |
| Cost per cached write | **~35 µs** (QD1 average latency 0.049 ms) |
| Cost per cached read | **~3.5 µs** (128 / 285,275 IOPS = 0.449 ms versus 0.436 ms measured) |

Observed latency is therefore almost entirely queueing behind that one worker, not device or
copy time. A 4 KiB payload copy costs roughly 0.3 µs, so the ~35 µs write cost is dominated by
**fixed per-request work**: cache mutex acquisition, the per-block admission preflight, a full
`Publish()` state snapshot under a spin lock, and a `KeSetEvent` drain wake-up on every write.
Reducing per-request overhead matters more for small writes than moving payload copies out of
the lock; the copy dominates only for large blocks.

Implication for faster storage: 4 KiB cached writes cap at roughly 28,000 IOPS (~110 MiB/s)
regardless of queue depth or process count. That is about 20x this virtual disk, but *below*
what a current NVMe device does unaided, so the cache would slow such a device down for small
random writes until the request path scales.

### Drain behaviour

| Drain shape | Data | Time | Rate | Lower writes | Batched |
|---|---|---|---|---|---|
| Sequential | 3,028 MiB | 24.8 s | 122 MiB/s | 12,120 (~256 KiB each) | 99.9% |
| Random 4 KiB | 1,057 MiB | 161 s | **6.6 MiB/s** | 190,740 (~5.6 KiB each) | 27% |

Gathering works when pending blocks are disk-adjacent and barely helps a scattered working set.
Random draining, not admission, is the limiting factor for sustained random writes.

### Read caching

Random cached reads served 22,198 MiB of hits in a single 10 s window with zero misses and zero
evictions, and no clean-cache invalidation occurred during the whole 42-minute matrix. This
confirms on a live driver that read-only storage controls (notably the storage service's
`IOCTL_STORAGE_FIRMWARE_GET_INFO` poll) no longer discard cached blocks.

## What this baseline does not show

Be explicit about these when quoting any number above.

1. **Drain-policy comparison is inconclusive.** Sequential write suggests Eager is 40% faster
   than Idle, but sequential *read*, which performs no draining at all, shows the same 11,835 /
   7,986 / 7,571 pattern. The spread is run-order drift. Confirmed independently: the identical
   uncached sequential write measured 249.67 MiB/s and 111.47 MiB/s eleven minutes apart on a
   shared host pool. The 4 KiB cells differ by at most 9% with no consistent winner.
2. **No capacity pressure.** `ThrottleWaits` and `Evictions` were 0 in every cell: a 2 GiB
   working set inside a 4 GiB cache never throttles a writer or evicts a clean block.
3. **No foreground/background interference measurement.** Each cell ran one workload at a time,
   with no concurrent reader and writer and no deliberately slowed lower device.
4. **Single samples, single machine, virtual disk.** No medians, no confidence interval, and a
   host storage pool shared with other activity.
5. **Nothing about durability.** Fast-preset scores partly reflect volatile acknowledgement.

## Required method changes for the next round

Interference work must not be evaluated with the matrix above. Before drawing conclusions:

1. **Interleave and repeat** configurations (A/B/A/B), at least three samples, report medians and
   spread. Mandatory given the 2.2x same-config drift.
2. **Exceed the budget**: working set larger than the cache (for example an 8 GiB file with a
   4 GiB cache) so throttling and eviction actually occur.
3. **Run two workloads at once**: a hot RAM-resident reader alongside a sequential writer, and
   report the *reader's* p99 latency with and without the writer.
4. **Slow the lower device deliberately** with the lab delay hook to separate avoidable
   serialization from unavoidable device latency.
5. **Report latency, CPU and actually-drained bytes**, not only headline throughput, when
   comparing Eager, Balanced and Idle.
