# Why older resident sequential reads were faster

The remaining gap is reproducible through **cache allocation history**. On the
same installed driver, file, VM and normal-priority launcher, a fresh allocation
read at 36.9 GB/s (Q8) and 15.5 GB/s (Q1). Reusing the allocation after drop-clean
fell to 29.8 and 10.8 GB/s. Reallocating restored the faster scores in all six
groups. Driver Verifier was off before and after the run; detailed timing was off.

This reproduces the scale of the older screenshot's 34.8/14.9 GB/s versus the
later screenshot's 29.4/10.5 GB/s sequential reads. It establishes a mechanism
that can explain that gap, not the unrecorded allocation state of an old run.

## Controlled comparison

Use the maintained [`cache-layout` suite](DEVELOPER_VERIFICATION.md#cache-allocation-history-comparison-plan-92).
Plan 92 ran three repetitions of four stages at both Q1 and Q8, alternating
queue-depth order between repetitions. All 24 cases completed; runtime state
restoration completed independently without error. Host-safe management contracts
also passed. No native driver or production policy changes were made.

Each row below contains the median and full range of three five-second scores,
in decimal GB/s. These are CDM's bundled DiskSpd measurements, not a new
CrystalDiskMark GUI run or replacement README screenshot.

| Allocation history before sequential refill | Q1 median (range) | Q8 median (range) |
|---|---:|---:|
| Fresh allocation | 15.462 (15.339–15.569) | 36.850 (36.768–36.977) |
| Sequential use, then drop-clean; same allocation | 10.754 (10.631–10.772) | 29.809 (29.754–29.905) |
| Random resident reads, then drop-clean; same allocation | 8.563 (8.507–8.962) | 23.177 (22.949–23.516) |
| Recreated allocation, after those two reuse stages | 15.375 (15.364–15.410) | 37.073 (35.497–37.129) |

The sequential reuse penalty was 30% at Q1 and 19% at Q8 relative to the fresh
medians. Random reuse increased it to 45% and 37%. An earlier three-repetition
`sequential-resident` baseline on the already-used allocation measured Q8 at
31.369, 28.887 and 31.625 GB/s, also fully resident.

Conditions: Windows 11 VM, four vCPUs, approximately 16 GB assigned RAM, AMD Ryzen
9 9955HX host, Balanced power plan. The test used a non-OS NTFS volume, a 2 GiB
Fast/Automatic cache with Idle draining, and the same owned 1 GiB file throughout.
The tray UI and competing benchmarks were stopped. Delay/fault hooks were cleared.
Host configuration and CPU placement were not changed. The launcher was normal
priority; this was not another task-priority comparison.

Each stage used a ten-second sequential warm pass and a three-second residency
proof before scoring 1 MiB sequential reads with one submitting thread. Random
reuse first touched the resident file for five seconds at 4 KiB, Q32/T1, seed 42.
The runner retains its normal DiskSpd affinity behavior; CDM's GUI command also
uses `-ag`, so the command lines are not claimed to be identical.

Evidence checked in the completed run:

- Every score had exactly 1 GiB of clean read data resident before and after,
  zero read misses, and zero lower read, write or flush attempts during the
  surrounding counter interval. Dirty/in-flight bytes stayed zero, drained bytes
  did not change, and there were no new errors.
- Allocation generation stayed constant within the reuse stages and changed
  when recreated. The driver instance stayed constant throughout.
- Telemetry was ready before each score and covered each process interval with
  26–27 samples; the largest sample gap was 0.596 seconds, below the two-second
  limit. Raw XML, command lines, process IDs, snapshots and restoration evidence
  are preserved privately. `MEASURED` alone was not used as an acceptance verdict.
- Q1 used the caller path for 95.7–99.6% of recorded read requests; Q8 used copy
  offload throughout. A wholesale change of execution path does not explain the
  speed tiers. Counter intervals include startup/close and observations; only
  XML score bytes/seconds determine the table's throughput.
- The original 2 GiB Fast profile and timing-off state were restored, with zero
  errors or pending dirty data. The tray app was restarted. The user's stopped
  RAM disk was left stopped; there was no reboot or driver replacement.

## What the source suggests

The driver allocates 4 KiB slots in 256 KiB slabs and initially links free slots
in ascending slot order
([writecache.cpp](../driver/qcache/writecache.cpp)). `AllocateSlot` pops the free
list, while `RetireSlot` pushes onto its head. `ClearClean` evicts in clean LRU
order ([cacheblocks.inl](../driver/qcache/cacheblocks.inl)). Consequently,
drop-clean empties the data but does not restore the initial free-slot order.
Random access before clearing further changes that order. Reapplying an unchanged
budget also preserves the allocation.

A 1 MiB hit copies up to 256 separate 4 KiB slots. Less favorable source-address
order can therefore hurt sequential throughput even when every byte is in RAM.
That is the leading source-level explanation for the controlled result. We did
not instrument slot addresses or hardware cache/TLB/prefetch counters, so those
individual costs are not proven. Allocation recreation also changes physical
pages and other per-allocation state.

The next driver experiment should preserve slot locality during recycling and
run this same matrix against the same DiskSpd binary. A fix only for an explicit
drop-clean should not be described as a fix for ordinary sustained eviction.
Changing allocator behavior needs integrity, ordering and capacity checks as
well as throughput comparison. Reallocation is a diagnostic control, not an
automatic production workaround.

## Same-allocation reset confirms free-slot order (plan 93)

Driver 0.4.423.1 (loaded SHA-256 `75A4F833365BC19FCE72DB1DE52A7EA29275A2709C57C210577743AB6BD09801`,
Verifier and timing off, same CDM DiskSpd hash) added a diagnostic control that,
only on a verified empty cache, rewrites the free-slot links to ascending order.
It keeps every buffer, the allocation instance/generation and the counters.
`cache-layout-reset` ran 36/36 cases (three repeats each) with no read misses,
lower writes, errors or reallocation inside any score window; every reset case
used the same allocation as the slow reuse case before it.

| Median GB/s | Q1 | Q8 |
|---|---:|---:|
| Fresh allocation | 15.44 | 36.27 |
| Sequential reuse after drop-clean | 10.75 | 29.70 |
| Same allocation, free order reset | 15.24 | 36.26 |
| Random reuse after drop-clean | 8.75 | 23.81 |
| Same allocation, free order reset | 15.15 | 36.62 |
| Recreated allocation | 15.41 | 36.37 |

Rewriting only the free-list order recovers the full fresh-allocation speed, so
free-slot order explains the whole gap; physical-page changes from recreation do
not. The order comes from `RetireSlot` pushing onto the free-list head while
eviction retires oldest first: a cleared sequential file is refilled in exactly
reverse slot order, and random use leaves a random permutation. Hardware
prefetch/TLB costs per order remain unmeasured.

## What layout is fast enough, and how a used cache drifts (plan 94)

Same driver build 0.4.426.1 (loaded SHA-256
`C9497328A9C85EA46C02E969DFB1226D2EF7FC49CE2B45BFA185E0F32F0B6B3E`), Verifier and
timing off, same CDM DiskSpd hash, `quick` passed first. Both suites completed
(30/30 and 18/18) with no misses, lower I/O, errors or reallocation in any score
window; every non-fresh case used the preceding allocation. "Contiguous" is the
share of cached disk neighbours that are also the next 4 KiB in memory, measured
just before each score (`LabMeasureLayout`).

| Free-slot order on the same allocation | Q1 GB/s | Q8 GB/s | Contiguous | Reversed |
|---|---:|---:|---:|---:|
| Fresh allocation | 15.39 | 36.65 | 100% | 0% |
| Every slot scattered | 8.48 | 22.64 | 0% | 0% |
| 256 KiB chunks shuffled, ascending inside | 14.77 | 35.28 | 98.4% | 0% |
| Chunks in order, descending inside | 10.71 | 29.54 | 0% | 98.4% |
| Ascending (same-allocation control) | 15.49 | 36.29 | 100% | 0% |

| Never-cleared cache | Q1 GB/s | Q8 GB/s | Contiguous | Whole free chunks |
|---|---:|---:|---:|---:|
| Fresh allocation | 15.24 | 36.22 | 100% | 3,929 |
| After 60 s random-read churn, file re-read | 13.86 | 30.66 | 92% | ~1,500 |
| Ascending reset control | 15.24 | 36.39 | 100% | 3,929 |

Findings:

- **Order inside each 256 KiB chunk mattered in these tests.** Shuffled chunks with
  ascending slots run within 4% of fresh; chunk order is irrelevant. Descending
  inside a chunk costs as much as the old sequential reuse; scattering is worst.
- **A cache in use drifts without ever being cleared.** 60 s of 4K random reads
  over a file twice the cache (about 600 MiB of churn at the lower disk's miss
  rate) displaced 8% of the file's neighbours and cost 9% (Q1) and 16% (Q8).
- **Whole free chunks were available but unused.** About 1,500 chunks were
  entirely free, yet re-read blocks went into just-evicted scattered slots
  because the free list hands back the most recently freed slot first.

Limits: churn was read-only and short; the cache was never completely full;
writes, retained-write turnover and longer use are not measured. The hardware
reason (prefetch, TLB or DRAM locality) for the direction effect is unmeasured.

## What was built and measured (plans 95-96)

Every change was measured on the test VM with the maintained runner, the same CDM
DiskSpd binary, Verifier and timing off, loaded driver hashes checked against CI,
and only clean score windows (no misses, lower I/O, errors or reallocation).
Medians of three repeats, GB/s, 1 MiB sequential reads of a 1 GiB file in a
2 GiB Fast cache.

**1. Chunk allocator (kept, 0.4.431.1+).** Per-chunk 64-bit free maps replace the
LIFO free list: one open chunk fills upwards, wholly free chunks are used first,
then the partial chunk that changed state last; O(1) bookkeeping.
`quick`, `policies` and `pressure` pass.

| Read after... | Old allocator Q1 / Q8 | Chunk allocator Q1 / Q8 |
|---|---:|---:|
| Fresh allocation | 15.46 / 36.85 | 15.13 / 35.94 |
| Reuse after a sequential file + clear | 10.75 / 29.81 | 14.61 / 35.66 |
| Reuse after random reads + clear | 8.56 / 23.18 | 14.50 / 35.48 |

It does not change partial eviction: after 60 s of random-read churn (steady)
13.79 / 30.05 vs 13.86 / 30.66 before; in a full cache after 120 s of churn
14.28 / 32.56 vs 14.15 / 33.17. Churn evicts parts of the file, and the parts
that stay are elsewhere, which no placement policy can join without moving data.

Writes (`write-performance`, 72 cases, versus the old allocator on the same day):
random 4K Q1/Q32 unchanged (-1.4% / +2.7%), sequential 1 MiB Q1 **+28%**
(7.0 to 9.0 GB/s), sequential 1 MiB Q8 with write-back running **-8% to -14%**
(within 5% with write-back off). The drain rate was about the same (155 vs 171
MB/s, no capacity waits), so the Q8 drop is not explained yet; it is recorded in
[known issues](KNOWN_ISSUES.md).

**2. Copy experiments for RAM hits (kept, default on in 0.4.434.1).** Lab flags,
A/B tested within one boot:

| Copy flags | Fresh Q1 / Q8 | Reuse (seq) Q1 / Q8 | Reuse (random) Q1 / Q8 |
|---|---:|---:|---:|
| 0: one copy per 4 KiB block | 15.13 / 35.94 | 14.61 / 35.66 | 14.50 / 35.48 |
| 1: prefetch the next block | 18.25 / 37.44 | 17.36 / 35.29 | 17.40 / 36.19 |
| 2: copy runs, uncapped | 14.88 / **22.10** | 19.33 / 37.91 | 19.37 / 37.02 |
| 2: copy runs within one chunk | 20.60 / 39.51 | 19.22 / 37.79 | 19.41 / 34.55 |
| 3: prefetch + runs within one chunk | **20.85 / 39.36** | **19.34 / 37.71** | **19.32 / 37.17** |

Uncapped runs could join neighbouring chunks into one 1 MiB copy and collapsed
to 22 GB/s at Q8 on a fresh cache; capping a run at the 256 KiB chunk fixed
that, which confirms the copy size was the cause (the exact system copy
routine behaviour was not profiled). Prefetch also helps scattered data:
Q1 +19% after steady and full-cache churn (16.42 and 17.01 GB/s). `quick`,
`policies` and `pressure` pass with both flags on. Both are on by default;
`qcache developer driver copy-flags <drive> 0` restores per-block copies.

**3. Memory views (kept).** A read-only layout map per 256 KiB chunk drives the
app's memory map (in-disk-order share, free chunks), and the RAM provider's
physical map shows where a RAM disk's locked pages sit. See
[desktop UI](DESKTOP_UI.md#memory-views).

**Not built, with reasons:**

- *One open chunk per stream.* The measured workloads were single-stream; a
  1 MiB request already takes four consecutive chunk positions under the lock.
  Needs a concurrent-stream test before it can be justified.
- *Chunk-aware eviction and an idle cleaner.* In a full cache, data stayed
  98% in disk order with the old and the new allocator (eviction reuses the
  slot it just freed, in LRU order), so there was little to win.
- *Large pages.* Shuffled 256 KiB chunks ran within 4% of a fresh layout. This
  did not isolate address translation or rule out a large-page benefit; no
  large-page A/B or TLB profile was performed. Deferred for a separate measured
  investigation.
- *Coalesced copies for writes.* Plausible fix for the Q8 write drop, but it
  touches how write data is placed; postponed until it can get the same
  integrity coverage.

## Idle defragmentation: evaluated, not built

What remains after partial eviction is holes: parts of a cached file evicted and
re-read elsewhere. Idle defragmentation would move the surviving blocks next to
each other. Measured on the final defaults (0.4.434.1, copy flags 3, clean windows):

| Q1 / Q8 GB/s | Steady churn (60 s) | Full-cache churn (120 s) |
|---|---:|---:|
| Fresh | 20.65 / 40.59 | 20.65 / 40.01 |
| After churn, file re-read | 19.15 / 34.45 | 19.54 / 36.02 |
| Neighbours still in disk order | 97.7% | 99.2% |

So the most idle defragmentation could recover here is 5-7% at Q1 and 10-15% at
Q8, only for files that were partly evicted and are later read sequentially;
even the churned file now reads faster at Q1 than a fresh cache did before these
changes (15.5 GB/s). Moving cached blocks needs new locking around pins, drains
and fills, and a mistake would silently corrupt cached data. Decision: not built
now. The app's memory map shows the in-disk-order share of real caches; if real
use shows much lower values than these tests, defragment-on-read (below) is the
targeted next step.

## Follow-up experiments (0.4.439.1-0.4.440.1)

- **Quiet runs.** NTFS last-access updates wrote 12-16 KiB to the test volume at random
  moments and five strictly checked runs were rejected in one day. They are now off on
  the test VM, and the runner waits for a quiet cache before layout windows (plan 97).
- **RAM disk reads at Q8 (removed).** One DiskSpd thread reads a RAM disk at 26.5 GB/s at
  both Q1 and Q8, four threads at 40-42 GB/s: Direct access copies each read inline (split
  over the provider's workers), so one caller's queued reads run one after another.
  Copying large reads on system worker threads did not help: only while another was
  queued (with a 1-in-16 probe) Q8 25 vs 26 GB/s, always Q1 13 and Q8 17 vs 26 GB/s. A
  different design is needed (for example the provider's own threads taking whole
  requests); not pursued now.
- **One copy per run for writes (removed).** +1.2-1.8% at Q1, -2.7% to +8.4% at Q8
  (noise), and it did not close the Q8 write gap. quick, policies and pressure passed.
- **Q8 sequential writes.** With the chunk allocator the request path waits less (lock
  waits 269 to 62 ms/s, queue waits 870 to 290 ms/s) yet Q8 writes measure 3-14% lower,
  and fewer copy calls did not help. Neighboring-memory placement was a hypothesis,
  not a demonstrated cause: warmed writes ordinarily reuse existing slots, and a
  1 MiB request already fills four chunks under one lock. Concurrent separate-target
  controls and a contemporary old/new comparison are needed before changing the
  allocator. Open.
- **Memory map cost.** A full 2 GiB cache reads in 1.3-1.5 ms, about 45 us per
  256-chunk lock hold (independent of cache size). Polling 8x faster than the app left
  4K read hits at p50/p99/p99.9 0.003/0.009/0.037 ms (p99.99 0.18 vs 0.09 ms). No change
  needed.

## Remaining ideas

1. Q8 sequential write gap: reproduce with contemporary old/new controls, then
   investigate a demonstrated cause (coalesced write copies were measured and did
   not help). Per-request open chunks remain conditional on placement evidence.
2. *Defragment on read*: when a sequential read finds a file's blocks in several
   places, re-place just that run at idle. Only worth it if real workloads show
   larger gaps than the tests above.
3. A concurrent multi-stream layout test (two files read at once) before adding
   per-stream open chunks.
4. A long-running steady test including writes and retained-write turnover.

## Scope and provenance

This investigation covers resident **cache sequential reads**. It does not explain
the older write scores, measure RAM-disk throughput, or prove all host scheduling
effects absent. No README result was changed. The earlier scheduled-task priority
penalty is a separate finding; CPU, I/O and memory priorities changed together,
so its internal cause remains unisolated (see [benchmarking](BENCHMARKING.md)).

The loaded native module was enumerated and its file hash matched the CI package,
rather than inferring loaded identity from a version string:

- Installed driver 0.4.414.1, SHA-256
  `F4719CA267E6FE0C842A0C7101E1423A99C8623EEF9B83E733843791541D39D0`.
- CDM 9.0.3's x64 DiskSpd 2.2, SHA-256
  `7281BF6DA6C03797016EDDF2E8AAEC4C644AE893D403D57A030B7E2E14B61079`.
- Plan 92 used a separately published managed runner; the installed driver was
  unchanged. Raw manifests also retain runner binary hashes. Private evidence
  stays outside Git, including machine identities and connection details.
