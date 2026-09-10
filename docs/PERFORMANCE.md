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

The baseline below came from a one-off lab script (`.lab/Bench-QueueCache.ps1`, machine-specific
paths, results in `.lab/bench-<timestamp>.csv`). **New measurements should use the maintained
harness instead:** `developer/scripts/Measure-Performance.ps1`, described under
[Running the harness](#running-the-harness).

Three practical notes for anyone repeating this:

- A benchmark launched over SSH must be **detached** (scheduled task, run as SYSTEM), otherwise
  Windows terminates it when the SSH session ends and the matrix stops halfway.
- Large write scores are **application-acknowledged throughput, not disk throughput**. In the
  sequential run below, 146,058 MiB were accepted while 3,028 MiB reached disk: 98% was
  coalesced by repeatedly overwriting one 2 GiB file. Repeatedly replacing the same cached
  blocks is useful caching behaviour, but it is a different workload from installing new data
  larger than RAM. Always report accepted, coalesced and drained bytes next to a score.
- One sample proves nothing on this hardware. See the drift figures under
  [What this baseline does not show](#what-this-baseline-does-not-show).

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

### Request-path scaling

All cached I/O for one disk is executed by a single request worker thread
(`RequestWorker`, `driver/qcache/lab.cpp`) — that is a fact of the source, not an inference.
The measurements are consistent with it:

| Measure | Value |
|---|---|
| 4 KiB write scaling, QD1 → QD32 | 20,070 → 27,931 IOPS (**1.4x for 32x the queue depth**) |
| 4 KiB write, 4 threads versus 1 at equal total queue depth | 25,204 versus 27,931 IOPS (**worse**) |
| Little's law check, QD32 | 32 / 27,931 IOPS = 1.145 ms versus **1.137 ms measured average** |
| Implied service time per cached write | **~35 µs** (reciprocal of 28,000 IOPS; QD1 average latency 0.049 ms) |
| Implied service time per cached read | **~3.5 µs** (128 / 285,275 IOPS = 0.449 ms versus 0.436 ms measured) |

Be careful with what this does and does not establish.

- **Established:** small cached requests hit a throughput ceiling that added queue depth and
  added submitting threads do not lift, and the code has exactly one execution path that can
  produce such a ceiling.
- **Not established:** that latency is "100% queueing". Little's law relates outstanding
  requests, throughput and latency in parallel systems too; consistency is not proof.
- **Not established:** that ~35 µs is CPU time spent inside the driver. It is the reciprocal of
  aggregate throughput for that cell, which also contains scheduling, interrupt and lower-device
  effects.
- **Hypothesis to test, not a finding:** that fixed per-request work dominates the small-write
  path — cache mutex acquisition, the per-block admission preflight, the full `Publish()`
  snapshot under a spin lock, and a `KeSetEvent` drain wake-up issued on every write. A 4 KiB
  copy should cost well under a microsecond, but nothing here *measures* the split between
  locking, bookkeeping, signalling and background interference. Phase 1 of the
  [performance plan](PERFORMANCE_PLAN.md) exists to settle this before anything is optimized.
- **Not established:** that four threads are consistently slower than one. That is a single
  sample; the size and repeatability of the penalty are unknown.

Implication worth keeping in view: if the ceiling holds, 4 KiB cached writes stay near 28,000
IOPS (~110 MiB/s) regardless of queue depth or process count. That is about 20x this virtual
disk, but *below* what a current NVMe device does unaided, so the cache would slow such a device
down for small random writes until the request path scales.

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
3. **No foreground/background interference measurement.** Each cell ran one DiskSpd workload at
   a time, with no concurrent reader and writer and no deliberately slowed lower device. This
   does **not** mean no overlap occurred — Eager drains while foreground traffic is running, so
   background work was present in the write cells. The experiment simply could not isolate or
   quantify its cost.
4. **Single samples, single machine, virtual disk.** No medians, no confidence interval, and a
   host storage pool shared with other activity.
5. **Nothing about durability.** Fast-preset scores partly reflect volatile acknowledgement.

## Running the harness

`developer/scripts/Measure-Performance.ps1` implements the method the next round requires. It
refuses disk 0 and boot/system volumes, keeps every test file in one directory on the target
volume, and restores the original policy and clears the lab delay hook even after a failure.

```powershell
# Full set: interleaved configurations, three repeats, medians and spread.
./developer/scripts/Measure-Performance.ps1 -Volume Q: -DiskSpd C:\tools\diskspd.exe -Repeats 3

# One question at a time.
./developer/scripts/Measure-Performance.ps1 -Volume Q: -DiskSpd C:\tools\diskspd.exe `
    -Experiments interference,slow-storage -Configs Eager,Idle -Repeats 3
```

| Experiment | What it answers |
|---|---|
| `small-request` | Cached 4 KiB read/write cost at QD1 T1, QD32 T1 and QD8 T4 (equal total outstanding I/O for the last two) |
| `interference` | Hot RAM-hit reader alone versus the same reader with an independent writer on another file: reader p95/p99 |
| `capacity` | Fresh data larger than the payload; warns loudly if the run failed to throttle |
| `slow-storage` | The interference pair repeated with the lab delay hook, to see whether RAM operations inherit lower-I/O latency |
| `random-drain` | Random write phase followed by a timed explicit drain: MiB/s, lower-write count, batched share |
| `drain-parallelism` | The same drain measured at parallelism 1, 2 and 4 before changing any default |
| `flush-under-load` | Flush issued while producers keep writing, versus flush after they stop |

Harness rules that matter more than the experiment list:

- Configurations are **interleaved and repeated**; the summary reports median, min and max.
  A single sample cannot separate a change from 2.2x drift.
- Each measured window records **driver counter deltas** (accepted, drained, coalesced, read
  hit/miss, lower and batched writes, throttle waits, evictions) alongside the DiskSpd numbers.
- Every run starts from a **comparable state**: explicit flush plus `drop-clean`, then an
  explicit warm pass for read experiments.
- The hot reader is **verified to stay a RAM-hit workload** by its read-miss delta; if it is not,
  the row must be read as including misses and eviction.
- Increasing the file size is not enough for capacity pressure: the run must actually touch
  enough distinct blocks, which is why the harness warns when `ThrottleWaits` stays 0.

What the harness still cannot do: attribute time inside the driver. Queue wait, cache-lock
contention, staging-copy time and lower-device time are not separable from user space. That is
Phase 1 instrumentation work — see the [performance plan](PERFORMANCE_PLAN.md).
