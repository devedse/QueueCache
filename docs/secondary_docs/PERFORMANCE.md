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

## Phase 2 results, 2026-09-11 (driver 0.4.14.1)

The first properly controlled round: `Measure-Performance.ps1`, **3 repeats**, Eager and Idle
interleaved, 1024 MiB budget (small enough that capacity pressure is reachable), 8 s windows,
256 MiB hot read set, 4 GiB fresh-data set, 102 minutes total. Values are **medians of 3**.

### Foreground/background interference: the dominant problem

A hot RAM-resident reader, and an independent writer on a *different* file:

| Case | Reader IOPS | Reader p99 | Reader read misses |
|---|---|---|---|
| Reader alone | 317,797 | **0.087 ms** | 0 MiB |
| Reader + writer | 52.8 | **320.6 ms** | 2.3 MiB |
| Reader + writer, lower device slowed 25 ms | 14.0 | **1,034 ms** | 0.7 MiB |

That is a **6,000x throughput collapse and a 3,700x p99 increase** from an unrelated writer, and
the reader's p99 **tracks the injected lower-device delay** (0.087 ms → 320 ms → 1,034 ms). A RAM
hit inherits disk latency. Identical for Eager and Idle in all three repeats, so this is
architecture, not policy. Acceptance scenario 1 in the [plan](PERFORMANCE_PLAN.md) currently
fails by three orders of magnitude.

Two mechanisms are mixed here and the next round must separate them: queueing behind the writer's
lower I/O (dominant — eviction alone cannot explain a 1 second p99 when a miss costs ~1–2 ms),
and the writer evicting the hot set under Automatic allocation (~81,000 evicted blocks, the whole
256 MiB read set). The second is worth testing against a Fixed allocation with a reserved read
share.

### Allocation follow-up: eviction is secondary, serialization is dominant

Repeat of the interference pair with a reserved read quota (`--allocation Fixed`), 3 repeats each,
same session, medians:

| Allocation | Reader alone | Reader + writer | Reader p99 under load | Reader misses |
|---|---|---|---|---|
| Automatic | 305,508 IOPS | 53.6 IOPS | 313.9 ms | 2.4 MiB |
| Fixed, 50% write share | 308,514 IOPS | **177.8 IOPS** | **126.6 ms** | **0 MiB** |
| Fixed, 25% write share | 309,060 IOPS | 175.0 IOPS | 119.9 ms | **0 MiB** |

With a slowed lower device (25 ms), Fixed allocation helps nothing at all: 14.0 IOPS / 1,008 ms
versus 16.5 IOPS / 1,003 ms.

This separates the two mechanisms cleanly:

- **Eviction of the hot set is real and cheaply mitigated.** A reserved read quota removes read
  misses entirely (2.4 MiB → 0) and improves the loaded reader by **3.3x**. That is a
  configuration-level mitigation available today, not a redesign.
- **Serialization dominates.** Even fully protected from eviction, the reader still runs about
  **1,700x slower** than it does alone, and it still inherits the full lower-device delay. No
  allocation setting can fix that; it is the request-path and locking work in phases 3–6 of the
  [plan](PERFORMANCE_PLAN.md).

Side effect worth noting: Fixed allocation makes the *writer* thrash its smaller quota (throttle
waits 694 → 3,759, evictions 83,573 → 241,152). Protecting reads has a real write-side cost.

### Reads scale with threads, writes do not

| Cached 4 KiB workload | QD1 T1 | QD32 T1 | QD8 T4 |
|---|---|---|---|
| Read | 26,061 IOPS | 103,846 IOPS | **286,899 IOPS** |
| Write | 20,201 IOPS | 29,137 IOPS | 25,823 IOPS |

This refines the earlier baseline. The **write** admission path plateaus near 29,000 IOPS
(~114 MiB/s) with ~30 requests outstanding, which is genuine queueing. The **read** path does not
hit that plateau: it reaches 287,000 IOPS, consistent with one worker thread saturating one core
at a few microseconds per request. The single-thread read cells are limited by the *submitter*
(average latency implies only ~2.4 outstanding at QD32 T1), not by the driver — so QD32 T1 read
numbers must not be read as a driver ceiling.

### Drain parallelism: 2 helps, 4 wrecks the foreground

Same random write phase, then a timed drain:

| Parallelism | Foreground write | Throttle waits | Drain rate |
|---|---|---|---|
| 1 (default) | 25,579 IOPS | 0 | 5.31 MiB/s |
| 2 | **34,662 IOPS** | 605 | 6.05 MiB/s |
| 4 | **4,302 IOPS** | 7,998 | 7.29 MiB/s |

Four drain threads drain 37% faster and cost the foreground **83% of its throughput**. This is
direct evidence that background work competes with foreground admission for shared resources, and
the reason drain concurrency must become a scheduling decision separate from the Eager/Balanced/
Idle eligibility policy. Do not raise the default on the strength of drain rate alone.

### Capacity pressure, now actually exercised

Fresh data larger than the payload: 15,573 IOPS (versus ~25,500 unthrottled), p99 18.2 ms,
7,785 throttle waits, then a drain of 1,003 MiB at 5.17 MiB/s using 222,051 lower writes of which
only 28,833 (13%) were batched. Flush issued while producers keep writing drains at 4.3 MiB/s.

### Eager versus Idle, controlled

Still no throughput winner: every cell is within run-to-run spread (write QD32 T1: 29,137 versus
28,066 IOPS; interference identical). One real difference did show up in the counters: at equal
throughput, **Idle issued roughly half the lower writes** of Eager in the small-request cells
(4,450 versus 9,717 at QD1 T1), i.e. more RAM coalescing and less disk traffic. That is the
expected behaviour and a better argument for Idle than any score in this matrix.

## Observer and concurrency verification, 2026-09-13 (driver 0.4.21.1)

The latest implementation was tested on the disposable Q: volume using the installed 0.4.21.1
CLI/driver pair from commit `46e0f60`. The old PATH diagnostics copy was not used; all VM control
commands used the installed Program Files CLI. Budget was 1024 MiB and runtime mutations were
restored after every batch.

### Correctness gate

The corrected private runner completed **11/11 checks**:

- in-flight overwrite at drain parallelism 1/2/4;
- overlapping read ordering;
- nested reads with retention disabled;
- deep mixed read/write queues;
- overlapped cancellation smoke test (197 cancelled, 3 completion races, 0 failed);
- Flush fence, TRIM/reuse, whole-file seeded verification and final driver-health checks.

The first mixed-queue attempt reported 39 wrong RAM reads because the test initialized the expected
patterns concurrently with the readers. That was a harness false positive; the test was corrected
to seed and flush the expected patterns before readers started, then passed. The old untracked
harness is not evidence and is not packaged.

### Observer matrix

Fixed 50% allocation, Eager, 25 ms lower-write delay, 256 MiB hot reader and separate 2 GiB writer;
two reversed-order repeats. Values below are medians of the loaded-reader cases:

| Observer | Reader IOPS | p99 | Read misses | New observer/control barriers |
|---|---:|---:|---:|---:|
| None | 95,846 | 0.284 ms | 0 | 0 / 0 |
| Telemetry | 96,647 | 0.281 ms | 0 | 0 / 0 |
| Inventory | 51,221 | 0.284 ms | 0 | 0 / 0 |
| Both | 49,727 | 0.279 ms | 0 | 0 / 0 |

Telemetry isolation passes. Inventory no longer creates a cache-clearing or scheduling fence,
but it has a repeatable roughly 45–50% throughput cost while the writer is active. It did not
restore the old catastrophic one-second tail: p99 stayed below 1 ms, although one maximum reached
about 1.4 s. Inventory-query CPU/storage cost is now the next observer-specific investigation.

### Automatic resident-read protection

Three repeats each, Eager, separate writer and hot reader:

| Hot set | Allocation | Reader + writer | Loaded p99 | Misses |
|---:|---|---:|---:|---:|
| 100 MiB | Automatic | ~104k IOPS | ~0.33 ms | 0 |
| 100 MiB | Fixed 50% | ~104k IOPS | ~0.33 ms | 0 |
| 400 MiB | Automatic | ~100k IOPS | ~0.35 ms | 0 |
| 400 MiB | Fixed 50% | ~102k IOPS | ~0.33 ms | 0 |
| 700 MiB | Automatic | ~20.8 MiB/s | ~45 ms | nonzero |
| 700 MiB | Fixed 50% | ~13.5 MiB/s | ~58 ms | nonzero |

Automatic protection works for hot sets within its bounded allowance and can borrow space more
flexibly than Fixed. It is not a promise to retain a hot set larger than the protected allowance.

### Cold-read interference

With a Fixed 50% quota, a hot 256 MiB reader alone reached about 253k IOPS. Running a disjoint
4 GiB cold reader at the same time reduced it to about 112k IOPS, with p99.9 around 1.2–1.35 ms
and 173–195 MiB of hot-read misses. This is much better than the earlier one-second behavior,
but independent cold-read execution remains a valid next architectural target.

### Small requests and draining

The new small-request medians (Automatic/Eager, three repeats) were approximately:

| Case | Result |
|---|---:|
| Cached read, QD1 T1 | 25.2k IOPS |
| Cached read, QD32 T1 | 248k IOPS |
| Cached read, QD8 T4 | 216k IOPS |
| Cached write, QD1 T1 | 20.1k IOPS |
| Cached write, QD32 T1 | 24.0k IOPS |
| Cached write, QD8 T4 | 24.6k IOPS |

The write-admission ceiling remains. Drain parallelism showed the same trade-off as the earlier
baseline, with the new implementation measured again:

| Parallelism | Foreground write | Drain rate | Result |
|---:|---:|---:|---|
| 1 | ~24.3k IOPS | 5.54 MiB/s | stable |
| 2 | ~30.5k IOPS | 6.52 MiB/s | promising candidate |
| 4 | ~6.4k IOPS | 9.87 MiB/s | unacceptable foreground collapse |

Do not raise the default to 4. Parallelism 2 needs a deliberate follow-up decision; it improves
both foreground acceptance and drain rate in this run.

### Verification limitations and decisions

- The queue-limit probe at offered writer QD32/64/128 showed severe pressure, but did not prove
  that the 64-entry scan limit caused it. A synchronized timing-enabled probe is still needed.
- Aggregate QPC counters do not yet attribute CPU time cleanly to copying, locking, publication
  and bookkeeping. Lower per-write overhead remains a hypothesis.
- Native hotplug, boot/paging, power, removal and Driver Verifier coverage were not run.
- A successful filesystem TRIM/reuse check does not prove that a filesystem TRIM notification
  reached the driver; unsupported forms remain `SKIP` unless driver counters prove observation.
- The final runtime state was restored and healthy. A saved-profile comparison detected that the
  profile had become Fixed50 during the long batch despite all current runners using
  `--runtime-only`; the original Automatic profile was manually restored with an explicit
  `--save`. This is a restoration/audit issue to investigate before trusting unattended profile
  preservation, not a performance result.

Private raw results and the decision table are under the ignored `.lab/` directory. The next
architectural priority is independent cold-read execution or better request scheduling, not a
large parallel-write rewrite.

## Source map for the paths measured here

Where each measured behaviour lives, for anyone acting on these numbers.

| Path | Role in these measurements |
|---|---|
| `driver/qcache/lab.cpp` | `RequestWorker` — the single per-device thread every cached read, write, flush and unknown IOCTL is executed on; `QueueRequest`/CSQ insertion, direct pass-through when caching is inactive, and the private telemetry IOCTLs answered outside the queue |
| `driver/qcache/writecache.cpp` | `Write()` admission and capacity wait, `Read()` overlay and read-miss path, `Drainer()` background drain and gather batching, `QcCacheBarrier()` flush/disable, `Publish()` snapshot on every request, `TryTrim()`, and `QcMayChangeMedia()` control classification |
| `driver/qcache/cacheblocks.inl` | Block index, dirty FIFO and clean read/write LRUs, `Evict`/`EvictOldest`/`ClearClean`, `ReadRoom`, `TouchClean`, `WriteLimit`/`ReadLimit` — the eviction behaviour the allocation experiment exercised |
| `driver/qcache/cachepolicy.h` | `QcShouldDrain()` drain eligibility for Eager/Balanced/Idle, watermarks, `QcWriteLimit()` write-pool sizing |
| `driver/qcache/writecache.h` | `QC_CACHE`, `QC_SLOT`, `QC_DRAIN_WORKER` and the control action enum, including `QcDropClean` used to reset cache state between measurements |
| `src/QueueCache.Management/CacheOptions.cs` | Managed mirror of the policy contract: allocation, write share, drain algorithm, watermarks, batch size, parallelism |
| `src/QueueCache.Management/WriteCacheState.cs` | Snapshot decoding for every counter quoted above (accepted, drained, read hit/miss, evictions, throttle waits, lower/batched writes) |
| `src/QueueCache.Management/CacheDevice.cs` | Device handle and IOCTL surface used by the CLI and the harness |
| `src/QueueCache.Cli/Commands.cs`, `src/QueueCache.Cli/LegacyCommands.cs` | `qcache policy apply/pause/flush/drop-clean/status` and `lab-delay`, the exact commands the harness drives |
| `developer/scripts/Measure-Performance.ps1` | The harness itself |
| `docs/PERFORMANCE_PLAN.md` | Phased work plan derived from these results |

## What this baseline does not show

Be explicit about these when quoting any number above.

0. **Scope.** Items 1–5 describe the first (2026-09-10) matrix. The Phase 2 round settled items 2
   and 3; items 1, 4 and 5 still stand, and interference results still come from one machine.
1. **Drain-policy comparison is inconclusive.** Sequential write suggests Eager is 40% faster
   than Idle, but sequential *read*, which performs no draining at all, shows the same 11,835 /
   7,986 / 7,571 pattern. The spread is run-order drift. Confirmed independently: the identical
   uncached sequential write measured 249.67 MiB/s and 111.47 MiB/s eleven minutes apart on a
   shared host pool. The 4 KiB cells differ by at most 9% with no consistent winner.
2. **No capacity pressure** *(addressed by the Phase 2 round above)*. `ThrottleWaits` and
   `Evictions` were 0 in every cell: a 2 GiB working set inside a 4 GiB cache never throttles a
   writer or evicts a clean block.
3. **No foreground/background interference measurement** *(addressed by the Phase 2 round above;
   the caveat applies to the first matrix only)*. Each cell ran one DiskSpd workload at a time,
   with no concurrent reader and writer and no deliberately slowed lower device. This does **not**
   mean no overlap occurred — Eager drains while foreground traffic is running, so background work
   was present in the write cells. The experiment simply could not isolate or quantify its cost.
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
