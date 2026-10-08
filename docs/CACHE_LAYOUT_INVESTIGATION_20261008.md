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
