# Sustained cache validation, 2026-10-09

This follows the [allocation investigation](CACHE_LAYOUT_INVESTIGATION_20261008.md).
The first milestone adds maintained measurements and fixes memory-map lifecycle.
The second replaces how re-read data enters the cache ([read recall](#read-recall-plan-104-044691),
driver 0.4.469.1, on by default): a fitting file read again after other activity now
returns to RAM speed on its third pass instead of staying at disk speed. The
allocator and copy flags are unchanged. Performance acceptance is separate from
successfully collecting a complete matrix.

## What changed

| Item | Implementation | Verification |
|---|---|---|
| Concurrent stream placement | Opt-in `cache-concurrency`, fixed total Q8 across one, two and four files; separate Q1 reference. | Plan-103 VM matrix completed 37/37 with clean restoration; all RAM-only controls had zero lower attempts. Host contracts and Windows CI pass. |
| Neighboring-sector correctness | 128 synchronized pairs of 512-byte writes in one cache block, unchanged guard bytes, cached and post-drain rereads. NTFS/512-byte sectors/4K cluster alignment required. | Plan-101 focused VM run completed 2/2: all sector/guard bytes matched and the strict RAM-read control completed. Host boundaries and Windows runner failure contracts pass. Submission does not force kernel overlap. |
| Sustained mixed I/O | Six episodes without intervening clear/reallocation, independent byte oracles, natural idle boundaries and reread recovery. | Plan-100 VM run completed 6/6 with 24,414 exact write/read checks, all 24 post-drain files verified and clean restoration. |
| Map polling cost | Complete allocation maps, ready handshake and interval coverage; off, two-second and 250 ms polling. | 2 GiB VM matrix completed 9/9 with clean restoration and zero lower attempts. The 4 GiB matrix is in progress. |
| Cache and RAM-disk map lifecycle | Reject obsolete/partial maps, clear unavailable state, stop cache polling on other pages and while hidden, request fresh data on return. | Frontend tests and Windows Debug/Release CI pass; generated README screenshots updated. |
| Larger map display | Group at most 2,048 displayed cells and explain the grouping accurately. | 8/32 GiB frontend fixtures pass. A 32 GiB allocation has not been exercised on the 16 GiB VM. |
| Failure cleanup | Oracle faults stop other streams, cancel/await the owned workload before restoration. | Host coordinator tests and Windows runner contracts pass. |
| Read recall (driver) | History of evicted blocks; a re-read block used more recently than the oldest used cached block enters as recent. Replaces one-in-16 insertion; lab switch and V20 diagnostics. | `cache-recall` 12/12: fitting reread 158 → 36,943 MiB/s at Q8 (third pass on), scan resistance unchanged. 30-minute soak 6/6: churned Q8 reread 130–237 → 15,620–19,877 MiB/s (8.8–17.5% → 99.6–99.8% hits), mixed hit rate unchanged. Integrity suites and Windows CI pass. |
| Exact warm passes | Exercise warm-ups read each file once completely instead of a timed window. | Used by every plan-104 exercise; the 4 GiB map-cost rerun is in progress. |

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
See [measurement contracts](DEVELOPER_VERIFICATION.md#concurrent-sustained-and-memory-map-exercises-plans-98103).

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

Plan-101 concurrency attempt
`QueueCache-Verify-20261009-134913-90855f89941f4b609682c2836fea2491`
is INCOMPLETE, 10/37, clean restoration. The two-stream Deferred-write score had
two lower paging-read attempts despite zero data-miss bytes, drain writes or
errors. The driver recorded four paging reads (262,144 bytes) and seven admitted
paging writes (25,088 bytes). Cold filesystem metadata on write-open is a working
explanation, not proven file-level attribution. Plan 102 adds a separate one-second
write preparation using the exact files/shape/payload before the existing explicit
drain and quiet boundary. Its snapshots/XML are retained and must remain healthy.
Scored controls still require zero lower attempts; no contamination allowance or
retry is introduced. The original 72-case write-performance preparation is unchanged.

Plan-102 focused attempt
`QueueCache-Verify-20261009-140224-11f1238469ee41dda6df158cba547538`
also remains INCOMPLETE, 2/2, clean restoration: two paging reads during the prime
and two during the score. The last recorded read PID, 3636, identifies Defender's
`MsMpEng.exe`, whose creation predates the run. Its 65,504,141,312-byte volume
offset maps through the retained NTFS extents to file offset 1,073,676,288:
the last 64 KiB of `stream-1.dat`. That file was 1 GiB, but the scored/warmed
prefix was only 512 MiB. The cold tail explains the recorded read; it does not
establish a data-write admission fallback. Attribution recorded two forwarded
paging reads, zero data-miss bytes and zero drain writes.

Plan 103 uses separate one/two/four-stream fixtures sized exactly to their
combined half-budget working set, records those names/sizes and validates actual
lengths before warming. The ineffective write-prime pass is removed. Defender
stays enabled without exclusions. Strict zero-lower-attempt score checks remain
unchanged. This isolates fitting data from an accidentally oversized fixture;
it does not subtract antivirus I/O or accept contaminated scores.

Plan-103 focused run
`QueueCache-Verify-20261009-141621-12627218a95d4a1dbd593cb888788b9e`
completed 2/2 with clean restoration. Its sector/guard bytes matched active and
post-drain data; the two-stream Deferred-write window measured 20,347 MiB/s with
zero lower read/write/flush attempts and zero drain/data-miss bytes. Defender was
still enabled. This confirms the focused fixture correction, not a complete
matrix or a native optimization. Concurrent writes use precomputed `-Z1M`, so
their scores cannot be compared directly with the `-Zr` write-performance matrix.

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

### Concurrent stream shapes

Run `QueueCache-Verify-20261009-142400-9963ca8d9903485bacc5610b4d5f4153`,
plan 103, runner from `3360135`, native 0.4.441.1, completed 37/37 with clean
restoration. Three repetitions, five-second scores, 2 GiB cache and a combined
1 GiB working set. Read and Deferred-write controls had exactly zero lower
read/write/flush attempts; all score windows retained copy flags 3 and stable
allocation/error state. The neighboring-sector oracle also passed.

MiB/s median [minimum, maximum] across all three complete repetitions:

| Streams × queue per stream | RAM reads | Deferred writes (`-Z1M`) | Eager writes (`-Z1M`) |
|---|---:|---:|---:|
| 1 × Q1 reference | 18,300 [17,931, 19,175] | 13,256 [13,120, 13,330] | 12,970 [12,921, 13,209] |
| 1 × Q8 | 34,704 [26,813, 36,028] | 20,603 [20,472, 20,638] | 20,491 [20,483, 20,541] |
| 2 × Q4 | 34,154 [32,715, 35,722] | 20,332 [20,244, 20,368] | 20,009 [19,982, 20,092] |
| 4 × Q2 | 31,999 [30,102, 32,296] | 20,028 [19,911, 20,266] | 19,909 [19,748, 19,937] |

Four-stream read median is 7.8% below single-stream Q8, but the single-stream
range overlaps both multiple-stream ranges. Four-stream write medians are about
2.8% lower, with tighter ranges. In-chunk order before scoring is 99.6–100% across
these shapes. This establishes measured stream-shape differences; it does not
identify allocation placement as their cause. Thread/queue/copy costs and
between-chunk locality are not isolated here. No per-stream allocator change is
justified by this matrix alone. Profile if this workload matters before selecting
an optimization.

### Full-allocation map polling

2 GiB run `QueueCache-Verify-20261009-145657-f0e75731497d457abf6db793df301ed3`,
plan 103, completed 9/9 with clean restoration. Three repetitions per cadence,
ten-second fitting random 4K Q8 reads. Every scored control had zero lower
read/write/flush attempts. Maps covered all 8,024 chunks and 513,536 occupied slots
(the complete payload capacity), with stable identity and interval coverage.

| Polling interval | Throughput MiB/s median [min, max] | Read p99 ms, median | Steady map ms, median [max] | Poll-worker CPU ms, median [min, max] |
|---|---:|---:|---:|---:|
| Off | 1,389.55 [1,376.20, 1,421.44] | 0.045 | N/A | N/A |
| 2 seconds | 1,383.92 [1,340.45, 1,434.60] | 0.047 | 1.622 [19.897] | 46.875 [15.625, 62.500] |
| 250 ms | 1,399.04 [1,377.76, 1,407.13] | 0.045 | 1.404 [10.910] | 375.000 [281.250, 437.500] |

Normal polling's median differs by -0.4% from Off; the ranges overlap. The stress
cadence does not establish a throughput gain. These measurements support keeping
the existing two-second interval. Hidden-page/window gating avoids unnecessary
queries; its frontend tests prove zero requests in those states.

Steady map durations exclude the initial ready sample. CPU is the recorded
poll-worker delta between first and last samples, covering roughly 12.05–12.07 s
at two-second cadence and 10.26–11.02 s at 250 ms, including the ending coverage
sample. The process-time counter has coarse increments. This measures native/API
polling and recording cost, not Avalonia rendering or total VM CPU. Generated
frontend fixtures validate grouping/lifecycle; they are not runtime rendering
benchmarks.

The 4 GiB polling and old/current Q8 write results are still being collected.
No new native speed-up is claimed.

## Read recall (plan 104, 0.4.469.1)

**What was going on.** A block read from disk for the first time goes to the
eviction end of the clean list ("just read"), so a single big read can't push out
data that is in use. Only one such block in 16 went to the recently used end. In a
full cache the next new block then evicts the one before it, so a file read again
after other activity evicted its own blocks: only about 1/16 of it stayed per pass.
That is why the soak's churned reread stayed at disk speed (1–18% hits). The first
reread after churn has to come from disk; the later ones did not need to.

**The change.** The cache keeps a short history of evicted blocks and when each was
last used (`driver/qcache/readrecall.h`): one 32-bit entry per cache slot, in
four-way sets, which is 4 bytes per 4 KiB slot (0.1%) counted in the fixed budget.
A read miss whose block is in the history *and* was last used more recently than
the oldest used block still cached is kept at the recently used end. Every other
miss stays at the eviction end. This is the order a least-recently-used cache with
a longer memory would choose. It replaces the one-in-16 rule. Comparing last use,
not eviction time, matters: with eviction times a loop larger than the cache kept
re-admitting itself and fell to 0% hits in the model. The history never affects
what a read returns, only which clean data stays. `drop-clean` clears it.
`qcache developer driver read-recall <device> 0|1` switches back to the earlier rule
in the same build (default 1); diagnostics V20 count both decisions.

**Modelled before implementation.** A block-level model of the clean list, then a
second one using a bit-exact Python port of `readrecall.h` with a millisecond clock
(misses at 250 MiB/s), scaled to the suite's file ratios. Hit rate per pass, %:

| Workload | Earlier rule | Read recall |
|---|---|---|
| Fitting file (half the cache) read 4× after stale data filled the cache | 0, 6, 12, 18 | 0, 0, 100, 100 |
| Hot set (quarter) after a one-off scan / after the same scan repeated | 100 / 100 | 100 / 100 |
| Loop 1.5× the cache, steady state | 47–64 | 50–67 |
| Two files of 0.6× the cache read in turn | 64–65 | 67 |
| Uniform random over 1.2× / 2× the cache | 83 / 50 | 83 / 50 |
| Zipf-skewed random over 4× the cache (a = 0.8 / 1.0) | 65 / 82 | 67 / 84 |
| Hot set read between blocks of a one-off 3× scan | 100 | 100 |

Keeping the one-in-16 rule alongside recall lost 10% of a hot set in the loop cases
(those kept blocks push out the oldest data), so it is replaced, not combined.
Pass 2 of a reread is always from disk: those blocks were evicted during pass 1.

### VM results

`cache-recall` run `QueueCache-Verify-20261009-161142-fbaac21da41a4410b05e44dcbf28ef01`,
plan 104, runner `10dc815`, native 0.4.469.1 (loaded filter SHA-256 `ED1B3F1B…`, the
CI package's), completed 12/12 with clean restoration (mode 1 captured and restored).
Three repetitions per workload and mode, alternating order; 2 GiB Fast cache,
Defender on. Whole-file passes are Q1 unbuffered 1 MiB reads; the last row of each
workload is a three-second DiskSpd Q8 read. Medians [min, max]:

| Reread workload | Earlier rule: hits | MiB/s | Read recall: hits | MiB/s |
|---|---:|---:|---:|---:|
| Stale file, pass 1 / 2 | 0% / 78.3% | 158 / 618 | 0% / 78.3% | 178 / 685 |
| Fitting file, pass 1 (from disk) | 0% | 159 | 0% | 169 |
| Pass 2 | 6.2% | 170 | 0% (all 262,144 blocks recalled) | 162 |
| Pass 3 | 12.1% | 178 [172, 222] | **100%** | **19,052** [18,975, 19,527] |
| Pass 4 | 17.6% | 171 [160, 197] | **100%** | **18,988** [16,580, 19,264] |
| Q8 afterwards | 22.8% | **158** [156, 170] | **100%** | **36,943** [33,769, 37,029] |

With read recall, passes 3 and 4 and the Q8 window made zero disk reads; with the
earlier rule every pass still sent about 1,024 requests to disk. The model predicted
both columns (0/6/12/18% and 0/0/100/100%).

| Scan workload | Earlier rule: hits | MiB/s | Read recall: hits | MiB/s |
|---|---:|---:|---:|---:|
| Hot set, pass 1 / 2 | 0% / 100% | 157 / 17,871 | 0% / 100% | 171 / 19,183 |
| One-off scan (1.5× the cache) | 0% | 167 | 0% | 171 |
| Hot set afterwards | 100% | 18,895 | 100% | 18,966 |
| Same scan again | 48.6% | 312 | 48.6% (1 recalled, 401,246 denied) | 336 |
| Hot set afterwards | 100% | 18,593 | 100% | 18,957 |
| Scan a third time (loop) | 48.6% | 300 | 48.6% | 335 |
| Hot set, Q8 afterwards | 100% | 36,971 | 100% | 36,891 |

Scan resistance is unchanged: the hot set stays fully cached after a one-off scan and
after the scan repeats, and the loop keeps the same hit rate; read recall turned down
the loop's history matches against data in use. `quick`, `policies` and `pressure`
pass on 0.4.469.1 with read recall on.

**Thirty-minute soak with read recall.** Run
`QueueCache-Verify-20261009-162618-74151ee0157b494cb40783406885fd6b` (plan 104, same driver)
completed 6/6 with clean restoration: 22,123 exact oracle write/read checks during
the mixed windows and all 24 oracle files verified after drain. Fresh reference
19,087/37,263 MiB/s (Q1/Q8). Compared with the plan-100 soak above (earlier rule,
0.4.441.1, same workload):

| Episode | Earlier rule: Q8 reread after churn | Read recall | Mixed-window RAM hits, earlier → recall |
|---|---:|---:|---:|
| 1 | 237 MiB/s, 17.5% hits | **19,877 MiB/s, 99.8%** | 43.3% → 53.6% |
| 2 | 233, 17.5% | **19,512, 99.8%** | 53.3% → 54.9% |
| 3 | 233, 17.1% | **19,551, 99.8%** | 53.2% → 54.9% |
| 4 | 229, 17.0% | **15,620, 99.6%** | 53.2% → 55.0% |
| 5 | 143, 11.2% | **17,057, 99.8%** | 54.1% → 54.9% |
| 6 | 130, 8.8% | **18,988, 99.8%** | 58.2% → 55.1% |

The Q1 reread window before each Q8 one is the first pass and comes from disk in
both (0.8–4.1% hits before, 0.0% now: the one-in-16 rule had kept a few blocks).
The Q8 window is then RAM-bound except the part of the file the Q1 window had not
reached yet. The mixed random workload keeps the same RAM hit rate (the model
predicts no change for uniform random reads over twice the cache). Its MiB/s
(74–84 now, 69–175 then) is disk-bound, and the disk was slower in this run: the
first-pass Q1 rereads, from disk in both, were 144–175 MiB/s here against 224–229
MiB/s in episodes 1–4 then. A same-build A/B of the mixed workload follows below.

## Speed-up implementation plan

Ordered by expected benefit for everyday use. Each item says what is going on in
plain terms, the change, and what a run must show before it is kept. Percentages
elsewhere in this report are measurements; the expectations below are not.

| # | What is going on | Change | Status | Must show before keeping |
|---|---|---|---|---|
| 1 | **A file read again after other activity stays slow.** The cache is full, the first reread comes from disk, and the earlier rule then let each new block evict the one before it. | **Read recall** (above): remember evicted blocks and let one back in when it was used more recently than the oldest used block still cached. | Implemented in 0.4.469.1 (`5e17604`), on by default. Verified: `cache-recall` third pass on 100% hits, Q8 158 → 36,943 MiB/s, hot set and loop unchanged; 30-minute soak churned reread 130–237 → 15,620–19,877 MiB/s with the same mixed-window hit rate. | `cache-recall`: a fitting file read again is (nearly) all hits by its third pass where the earlier rule stays near 12–18%; the hot set is not lower after a one-off or repeated scan; the loop larger than the cache is not lower. `cache-sustained`: churned rereads improve. Integrity suites pass. |
| 2 | **A request that is only partly in RAM is read entirely from disk.** One missing 4 KiB block sends the whole request (up to 16 MiB) to the disk, so a file 90% in RAM can still read at disk speed. | (a) Count first: staged reads that already had cached blocks, and the bytes read from disk that RAM already held, in diagnostics and the exercise evidence. (b) Only if (a) shows real waste after read recall: read just the missing runs from disk (merging short gaps to bound the number of disk requests) and copy the rest from RAM, keeping today's ordering and error handling. | Not started. With read recall, the second reread pass finds nothing in RAM and the third finds everything, so this matters mainly for random/mixed workloads; (a) decides. | Disk bytes equal missing bytes (plus merge slack); byte checks with patterned partial residency; full-hit and full-miss speed unchanged. |
| 3 | **Q8 sequential writes looked 3–14% slower with the new allocator.** | None yet. | The comparison is not controlled. The disk alone ("Off", cache disabled) ran at 255 MiB/s on one day and 115 MiB/s on the other, and Eager/Idle drain to that disk during the 10-second window. Within one build, Deferred and Eager Q8 writes differ by 0.5% (20,603 vs 20,491 MiB/s, `-Z1M`). Reinstalling 0.4.426.1 to repeat it is not possible: its installer's check cannot read the newer RAM-disk driver's state ("Invalid native RAM disk identity, geometry or lifetime state.") and refuses. | Only worth reopening with a same-build switch for the old slot order, compared on the same day with Deferred and Eager. |
| 4 | **One reader on a RAM disk tops out at about 26 GB/s; four readers reach 40–42 GB/s.** The RAM disk handles one reader's requests one after another. | Profile the provider's read path first; then try one design (for example whole requests on the provider's own threads). | Two offload designs were measured and removed (no gain, or 13–17 GB/s). | A repeatable Q8 gain without slowing Q1 or small reads; byte round trips. |
| 5 | **Buffered application reads never fill QueueCache's read cache.** Windows' file cache refills them with paging reads, which are deliberately not kept: keeping them once served another block's data on C:. | No change planned. Windows' own file cache holds that data. A safe design would need to prove it never caches data older than what Windows has in memory. | Correctness boundary, documented in [KNOWN_ISSUES](KNOWN_ISSUES.md#application-caching-scope-t085) and [cache policies](CACHE_POLICIES.md). | n/a |

Not pursued, with reasons:

- **Idle relocation/defragmentation:** cached data is already 99.6–100% in order
  within chunks before scoring, and the slow reread was data missing from RAM,
  which moving data around cannot fix.
- **Per-stream chunk placement:** four streams were at most 2.8% (writes) and 7.8%
  (reads, overlapping ranges) below one stream.
- **Write-copy batching:** measured in 0.4.439.1, +1.5% at Q1 and noise at Q8
  ([layout investigation](CACHE_LAYOUT_INVESTIGATION_20261008.md#follow-up-experiments-04391-04401)).
- **Map polling:** no measurable cost at the app's two-second interval on 2 GiB.
- **Large pages:** not ruled out, but nothing here points at address translation;
  it would need a profile and a controlled A/B.

The memory-map percentage measures consecutive disk-block links inside cache
chunks. It is neither file-specific fragmentation nor proof of contiguous physical
RAM. RAM-disk physical maps report allocated page locations; cache maps report
logical slot use. Neither view promises automatic relocation.

If resident placement later proves limiting, RAM permits bounded copying without
SSD erase/wear costs. A relocation experiment would need a small copy budget,
idle detection that stops immediately when demand returns, and migration only
when concurrent readers/writers/drainers can retain correct ownership. Cheap
copies do not make synchronization free; neither copying nor physical page
adjacency is justified merely by a low map percentage.
