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

## Optimization options (not implemented)

**Clean**, **empty** and **freshly allocated** are different states. Clean only
describes whether cached bytes are already on disk. Both fast and slow scores
above used clean, resident data. Clearing data marks slots reusable without
resetting their order. The proposed optimization concerns slot order, rather than
erasing old byte contents from free slots.
See [benchmarking](BENCHMARKING.md#ui-preparation-clearing-contents-versus-recreating-the-allocation)
for the current UI controls and repeatable preparation.

A simplified example: slots numbered 0, 1, 2, 3 are initially handed out in that
order. Retiring them in order 0, 1, 2, 3 pushes each to the free-list head, leaving
3, 2, 1, 0 for the next refill. Random retirement creates another permutation.
Actual order also reflects concurrent requests and the driver's scan-resistant
insertion policy. Slot numbers describe allocator order and nearby virtual
buffers within slabs, not a promise that the whole cache is physically contiguous.

Random hits change recency bookkeeping via `TouchClean`; they do not move the
cached payload. The layout consequence appears when slots are subsequently
retired and reused. This distinction matters when deciding whether to optimize
allocation or to move existing data in the background.

Proposed experiment order:

1. **Isolate free-slot order without reallocating RAM.** At a verified completely
   empty boundary, rebuild the free-slot list in initial slot order while retaining
   the same buffers. Compare before/after against recreation using the maintained
   runner. This isolates the leading hypothesis from changes to physical pages
   and other state caused by recreation. Check active/pinned/filling ownership
   under the appropriate synchronization; a clean-but-occupied cache is not this
   boundary. Bound or measure time spent blocking requests during the rebuild.
2. **Improve placement during ordinary reuse.** Explore tracking free slots per
   existing 256 KiB slab and choosing nearby available slots for sequential fills
   and newly allocated writes. Keep victim selection separate: choosing which
   data to evict protects the hot set, while choosing among already-free slots
   determines placement. Preserve retention, quotas, scan resistance and version
   ownership. If only scattered slots are free, use them without waiting for
   lower I/O just to obtain a prettier layout. A full cache may leave little
   placement choice; do not silently evict extra hot data to improve a benchmark.
3. **Evaluate copying adjacent runs together.** Where both source and destination
   ranges are contiguous and valid, a larger copy may replace repeated 4 KiB
   copies. Existing version pins and valid-sector checks remain essential. This
   complements better placement but cannot make scattered buffers contiguous.
4. **Consider bounded idle work only if still worthwhile.** Reordering free-slot
   metadata need not move payload, but it cannot repair already-resident layout.
   Moving resident blocks is compaction: it consumes memory bandwidth and must
   coordinate with reads, writes, pins and draining. Any such work should have a
   strict budget and yield to demand. Simply dropping useful clean data during
   idle creates future misses and does not itself reset slot order.

A FIFO free list is a cheap candidate comparison, not an assumed general fix:
it can avoid reversing a sequential retirement order but preserves random
retirement order, and may trade reuse locality of hot metadata for other gains.
Likewise, an empty-cache reset alone would address the benchmark preparation case
without proving a fix for continuous eviction while the cache stays populated.

No optimization, benefit percentage or new workload contract has been accepted
yet. Before implementation, extend/version the maintained verification plan for
the relevant comparisons; keep implementation status and measured evidence
separate in the tracker. In addition to the existing 24-case comparison, proposed
verification needs repeated reuse and sustained eviction without explicit clears,
read integrity, overlapping/pinned versions, partial writes, capacity and error
semantics, plus the existing 72-case `write-performance --budget-mib 2048` suite
with the same DiskSpd binary before/after. Check CPU cost and tail latency as well
as throughput, and ensure fitting Fast writes gain no lower-I/O dependency.

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
