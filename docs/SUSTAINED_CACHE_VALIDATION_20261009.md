# Sustained cache validation, 2026-10-09

This follows the [allocation investigation](CACHE_LAYOUT_INVESTIGATION_20261008.md).
The first milestone adds maintained measurements and fixes memory-map lifecycle.
It does not change the native allocator, copy flags or driver defaults. Performance
acceptance is separate from successfully collecting a complete matrix.

## What changed

| Item | Implementation | Verification |
|---|---|---|
| Concurrent stream placement | Opt-in `cache-concurrency`, fixed total Q8 across one, two and four files; separate Q1 reference. | Host contracts and Windows CI pass; VM matrix in progress. |
| Neighboring-sector correctness | 128 synchronized pairs of 512-byte writes in one cache block, unchanged guard bytes, cached and post-drain rereads. NTFS/512-byte sectors/4K cluster alignment required. | Host boundaries and Windows runner failure contracts pass; final VM check in progress. Submission does not force kernel overlap. |
| Sustained mixed I/O | Six episodes without intervening clear/reallocation, independent byte oracles, natural idle boundaries and reread recovery. | Plan-100 VM run completed 6/6 with 24,414 exact write/read checks, all 24 post-drain files verified and clean restoration. |
| Map polling cost | Complete allocation maps, ready handshake and interval coverage; off, two-second and 250 ms polling. | Host contracts pass; 2/4 GiB VM matrices in progress. |
| Cache and RAM-disk map lifecycle | Reject obsolete/partial maps, clear unavailable state, stop cache polling on other pages and while hidden, request fresh data on return. | Frontend tests and Windows Debug/Release CI pass; generated README screenshots updated. |
| Larger map display | Group at most 2,048 displayed cells and explain the grouping accurately. | 8/32 GiB frontend fixtures pass. A 32 GiB allocation has not been exercised on the 16 GiB VM. |
| Failure cleanup | Oracle faults stop other streams, cancel/await the owned workload before restoration. | Host coordinator tests and Windows runner contracts pass. |

## Conditions and contracts

Windows 11, four vCPUs, 16 GiB RAM, Q: on a non-OS 200 GB Ceph virtual SSD.
Q: is NTFS with 512-byte logical sectors and 4 KiB clusters. Driver Verifier is
off, last-access updates disabled, copy flags 3 and normal process priority.
The native current driver is 0.4.441.1. The current driver SHA-256 is
`F6964C6A17467F2C5A8CBBDAFD15425A135DA0F48983B501B99897529B5BB078`;
the RAM provider is
`C80959C52E583F103D2F689611AEFBB37237F2A8014C75174A79E62EE371C273`.
Loaded-module evidence, not just a version string, identifies each run.

All comparisons use the same CrystalDiskMark 9.0.3 bundled DiskSpd 2.2 x64 binary,
SHA-256 `7281BF6DA6C03797016EDDF2E8AAEC4C644AE893D403D57A030B7E2E14B61079`.
The maintained foreground runner collects immutable case IDs, raw XML, interval
telemetry, process ownership, control traces and independent restoration evidence.
These are DiskSpd measurements, not CrystalDiskMark GUI scores.
See [measurement contracts](DEVELOPER_VERIFICATION.md#concurrent-sustained-and-memory-map-exercises-plans-98101).

Fresh-reference, concurrency read and fitting Deferred-write controls require
zero lower read/write/flush attempts, stable allocation and accounting for every
scored byte. Eager writes and mixed I/O intentionally reach disk. After churn,
Q1 then Q8 rereads measure natural recovery with actual hit/miss accounting; Q8
benefits from the preceding Q1 reads and is not an independent queue-depth A/B.
Their ready handshake and telemetry coverage remain mandatory.

## Preserved failures and the contract correction

Plan 98 smoke `QueueCache-Verify-20261009-121724-e294932cf71f46baa28681a5fa0e98ce`
stopped before scoring because preparation warmed competing files: the second
proof still missed 37,789,696 bytes. It is INCOMPLETE; restoration completed.
Plan 99 smoke `QueueCache-Verify-20261009-122536-2130df0b40f142e5abc883d8ab1c0508`
completed six short episodes: 1,682 exact write/read checks and 24 final files
verified after drain. Its 120 seconds of mixed I/O is not a sustained-use pass.

The plan-99 long attempt
`QueueCache-Verify-20261009-123638-526002062ef94655aad8118e88279f01`
stopped after its first five-minute mixed episode. Five residency proof passes
still missed 676/533/430/350/279 MB; throughput stayed near 239 MiB/s. The last
proof read less than the full 1 GiB prefix. No byte mismatch or driver fault was
reported, and restoration completed. This remains INCOMPLETE, not relabeled as a
successful soak.

Plan 100 measures this recovery directly instead of forcing a fitting file to
become resident before continuing. Strict RAM-only controls remain strict.
Complete collection proves the stated correctness/lifecycle checks and supplies
performance evidence; it does not make slow recovery acceptable.

Windows CI initially rejected the fake concurrency fixtures because the test
host ran below normal priority. Fixtures now set and restore Normal, with a
separate negative test proving the production gate rejects below-normal runs
before accessing the target. The production gate was not relaxed.

Plan-100 concurrency attempt
`QueueCache-Verify-20261009-134053-c604aa926e8d4582804bbbeffcd8bdac`
stopped in its first oracle, before scoring. The runner erroneously required the
generation to remain unchanged on Disable, although the driver deliberately clears
clean slots and advances generation. Its combined check short-circuited before
the disk reread; its original message does not establish a byte mismatch. The run
remains INCOMPLETE, 1/37, with clean restoration. Plan 101 requires exactly one
generation advance, unchanged instance/reservation/capacity and empty, error-free
state, then compares disk bytes separately. It records lifecycle snapshots and
the expected/disk byte files. Host tests reject unchanged/extra generation changes,
wrong identity, retained data, errors and reservation changes.

## Performance findings

### Thirty-minute soak

Run `QueueCache-Verify-20261009-130343-20af98641401425ab5705572c3d07e1c`,
plan 100, managed runner from `ad7db99`, native 0.4.441.1, completed 6/6.
The later runner changes concern fixture priority, failure cleanup, the sector
target guard and Disable evidence; this healthy soak uses its recorded plan-100
contracts. This is not a rerun with the latest managed binary.

The fresh 1 GiB RAM-only reference measured Q1 **18,716.58 MiB/s** and Q8
**36,858.24 MiB/s**, with zero lower attempts. All six episodes retained instance 6,
generation 78 and the same payload capacity. No driver error was reported.
Twenty score/reread intervals have complete telemetry coverage; the largest
observed gap is 1.019 seconds, below the two-second bound. All 24 independent
oracle files matched after drain; 24,414 exact 1 MiB write/read checks completed
during the mixed windows. This verifies those oracle files, not DiskSpd's random
write payload or forced kernel interleavings.

| Episode | Mixed MiB/s | Recovery Q1 MiB/s | Recovery Q8 MiB/s | Q1 byte-hit % | Q8 byte-hit % | Map in-order % after mixed window |
|---|---:|---:|---:|---:|---:|---:|
| 1 | 152.56 | 228.07 | 236.56 | 4.05 | 17.48 | 19.26 |
| 2 | 169.66 | 229.47 | 233.37 | 4.10 | 17.50 | 6.95 |
| 3 | 171.47 | 223.98 | 232.77 | 3.91 | 17.13 | 2.77 |
| 4 | 174.87 | 223.68 | 228.87 | 3.90 | 17.01 | 1.37 |
| 5 | 129.26 | 143.86 | 142.70 | 1.81 | 11.20 | 0.90 |
| 6 | 69.12 | 116.88 | 129.57 | 0.78 | 8.79 | 0.67 |

Every natural 20-second idle boundary still had a full cache, with zero dirty and
in-flight bytes. Idle draining worked without emptying the cache. Recovery
remained disk-bound; low residency prevents attributing this gap to RAM placement.
Later mixed and disk-bound reread windows slowed together. This run does not
isolate backend variability from admission behavior or prove that declining map
order caused the decline.

Source inspection suggests two mechanisms worth testing: `DemoteReadFill` keeps
only one new fill in 16 recent, whereas existing hits become recent, and a hole
in a staged read causes the whole request to be read from disk. The plan-99 proof
passes increased the byte-hit share without reaching full residency. These are
explanations supported by code and observations, not a causal optimization A/B.

The concurrent-stream, polling-cost and old/current Q8 write results are still
being collected. No new native speed-up is claimed.

## Next decisions

| Idea | Judgment | Evidence needed before implementation |
|---|---|---|
| Faster admission of repeatedly read sequential data | Continue; highest priority if slow recovery persists. | Compare an admission policy with current one-in-16 recent insertion. Preserve one-off scan resistance, Fast admission, capacity backpressure and byte/order checks. |
| Read only missing ranges of a partly cached request | Investigate after admission. | Compare coalesced lower ranges against whole-request reads, including lower bytes, attempt count, ordering and errors. One hole currently sends the entire staged request to disk; a high byte-hit ratio alone may remain slow. |
| Per-stream chunk placement | Conditional on concurrent-stream evidence. | Demonstrate a repeatable placement penalty before changing allocation. Warmed overwrites reuse their existing slots. |
| Idle relocation/defragmentation | Defer pending a fully resident placement bottleneck. | Demonstrate a throughput gain with a bounded migration budget and concurrent byte/order coverage. Relocation cannot restore data absent from RAM. |
| Large-page allocations | Defer; not ruled out by these runs. | Profile translation/copy costs and perform a controlled A/B; map appearance is insufficient. |
| RAM-disk Q8 read redesign | Separate follow-up. | Profile the provider path and try one design at a time. Prior system-worker offload experiments did not improve it. |
| 32 GiB runtime polling | Remains open. | A larger VM or host; frontend grouping fixtures are not a large-allocation runtime measurement. |

The memory-map percentage measures consecutive disk-block links inside cache
chunks. It is neither file-specific fragmentation nor proof of contiguous physical
RAM. RAM-disk physical maps report allocated page locations; cache maps report
logical slot use. Neither view promises automatic relocation.
