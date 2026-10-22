# QueueCache performance findings

This is the consolidated reference for performance findings and decisions from
the RAM-first, RAM-disk and cache-layout investigations and the competitor comparison through 2026-10-22 (plans 164–167).
Read it before proposing another experiment. Detailed methods and raw-evidence
identities are in the dated reports under [history](history/README.md); their
findings are consolidated here, including negative results and limits. How tests
are run is in [testing](TESTING.md); current gaps in [known issues](KNOWN_ISSUES.md).

**RAM-path findings (2026-10-11): two speedups accepted; RAM-disk writes reach memory bandwidth.**
Asynchronous split reads (PR #9) raise single-thread queued 1 MiB RAM-disk reads
(the CrystalDiskMark SEQ1M Q8T1 shape) from a median **24,234 to 43,583 MiB/s
(+79.8%)**; default for new RAM disks from plan 119. Lighter read-hit bookkeeping
(plan 120) raises resident 1 MiB cached reads **+7.9% at Q8T1** (34,951 to 37,718
MiB/s), +4.5% at Q1T1 and +3.5% with four readers; default from plan 120. Large
RAM-disk writes are at this VM's memory write bandwidth: four writers are no
faster than one, and streaming stores were measured and rejected. Details:
[asynchronous split reads](#ram-disk-asynchronous-split-reads-accepted-pr-9),
[cached reads](#cached-reads-lighter-read-hit-bookkeeping-accepted-plan-120),
[RAM-disk writes](#ram-disk-writes-no-speedup-available-on-this-machine-plan-120).
Everything else retained earlier stays: the ordered chunk allocator, bounded
RAM-hit copy optimizations, read recall and caller backoff 256. The shared
whole-read queue stays removed. Partial-read counters are implemented and
missing-span reads stay conditional (under 1% overlap measured). Low-priority
cache slowdowns are a benchmark-setup effect (launch at normal priority); the
selective NTFS/ReFS caller policy was closed without an experiment (best measured
gain +5.26%, at the acceptance bar). Dedicated 100 ms map-refresh cost testing was
dropped by the owner.

The maintained single-command campaign and durable completion event are
implemented (plan 119, including the one-phase `experiment` profile).

**Competitor comparison and fix qualification (2026-10-15).** QueueCache leads cached reads and
single-program RAM-disk reads, but the comparison with the bare disk and
PrimoCache found three cache weaknesses: read misses do not overlap (#14), the drain
writes small pieces (#15), and Strict stalls all writes behind whole-cache
drains (#16).
The fix branch has completed its correctness and concurrency checks. Same-build
miss offload improves cold random/sequential throughput 9.95×/3.13× in the latest
controlled repeat, with better p99. The earlier equal-work comparison also
improved cold-random latency tails. Larger drain batches and four
drainers improve the cold-read target and repeated write-drain times. Fixed-window
foreground write IOPS are not qualified as invariant. The final release campaign
completed all 11 phases on October 14; the October 15 environment repeat stopped
incomplete at 8/11 phases on a RAM-only guard. Its unchanged nine-case map-cost
repeat completed separately, without reproducing that failure. The focused October 15/16 cache-paused, Fast and PrimoCache refresh completed both generic suites for each mode; it does not complete the interrupted release campaign.
Real CrystalDiskMark captures are recorded
separately in the README.
Details: [competitor comparison](#competitor-comparison-three-cache-weaknesses-plan-121-2026-10-13).

**Disabled-baseline cache refresh (2026-10-15/16, plan 146).** The same pinned tools
completed 33 main and 7 empty-cache steps per mode: 120 steps and 57 raw XML scores
reviewed, all tree hashes verified. Fast wrote 8 GiB at 267.5 MB/s at the application,
but 200.1 MB/s through the last busy backing-disk sample, against 257.4 paused (-22.2%).
Its sampled catch-up tail was 10.81 s. **Disk-speed parity is not established; the
residual gap is measured here.** File-tree read/hash took 2.84 s Fast and 2.90 s paused,
against the earlier Fast 154.23 s scanning episode. PrimoCache application /
catch-up-inclusive 8 GiB rates were 425.1 / 279.6 MB/s. Each generic suite has one
repetition. One-second virtual-disk sampling is not physical-media durability proof;
sub-second tails are below its resolution. PrimoCache's retained CDM sequential
read/write changed +5.1%/+8.2%, queued-random read/write −6.3%/+10.0%; the old Defender
state was not recorded, so these cross-date deltas do not isolate disabling it. The
paused control keeps QueueCache loaded and its 2 GiB reservation. Native code is
unchanged. Keep 1024 KiB × 4 on the earlier paired explicit-drain evidence; this refresh
does not qualify foreground interference or Strict latency. Full scores, six exact run
identities and real captures: [current
comparison](COMPARISON_DETAILS.md#current-cache-refresh-2026-10-1516) and its existing product
data.

**Storage qualification.** The storage guard passed before PrimoCache; its post-run
check reported Ceph near-full warnings with 131.3 GB free. The onset and performance
effect were not measured. Treat its storage-dependent scores as observations under that
additional uncertainty. The warnings cleared after restoration and snapshot cleanup.

**Short completed writes (2026-10-16, plan 148).** Three complete repetitions
per provider, with one identical temporary snapshot present, measured one new
1 GiB file and 4,096 new 64 KiB files (256 MiB). Through filesystem flush, explicit
product drain and zero pending/disk queue, large/small medians were
**48.41 / 1.25 MB/s Direct**, **52.71 / 41.71 QueueCache Fast**,
and **78.96 / 57.62 PrimoCache**. All 18 cases and 36,873 cache-bypassed file
hashes passed, with the same unique payload. QueueCache's small-file rate was
33.2× Direct; its mean observed backing write was
341.9 KiB against
5.0 KiB Direct and
1746.2 KiB PrimoCache, including metadata.
The request sizes are consistent with batching, without establishing the source
of every speed difference. The native driver stayed 0.4.687.1; this is new
measurement evidence, not another driver fix.
QueueCache’s small-file median remains 27.6% below PrimoCache, with a wide
18.36–41.94 MB/s range; its large-file range overlaps Direct’s. The slow
repetition’s cause is untraced. A follow-up should separate backend/OS-barrier
variation from batching limits before choosing another driver change.
One post-score, pre-pause snapshot also had 56 KiB newly dirty, drained by untimed
Disable. Its source is untraced; the byte oracle validates the later paused state,
not the exact scored endpoint.

Defender/Search were disabled; processes had normal priority; Verifier/timing
were off. Direct/Primo had QueueCache absent. Pre/post capacity and near-full
guards passed, while existing Ceph OSD/PG/scrub warnings remain an uncertainty.
The snapshot was removed after verified restoration. These unbuffered new-file,
cache-fitting workloads do not replace the earlier 8 GiB sustained result,
isolate a Defender speedup or qualify Strict/general interference. A slow 1 GiB
small-file pilot and a pre-snapshot QueueCache diagnostic were excluded; every
published provider used the final plan 148 contract. Exact runs, ranges and
per-case scores: [short write plus drain comparison](COMPARISON_DETAILS.md#short-write-plus-drain-comparison-2026-10-16).

**Remaining goal.** The read-miss and larger-default changes are merged in
PRs #17/#18. [Issue #15](https://github.com/devedse/QueueCache/issues/15) remains
the drain follow-up: match or exceed a contemporaneous PrimoCache control on
both new-file workloads, while preserving correctness, RAM-first admission and
explicit durability. Since plan 164 the default meets it in a same-window
comparison ([results](#final-164)).

**The new default (plans 164–167, October 21–22): Eager 2 MiB × 8 with read-aware
limit 2 and asynchronous TRIM is the default for new caches, and it beats a
same-window PrimoCache on both #15 workloads.** With matched backends (paused
anchors 95.9 against 96.3 MB/s, drain latency 53.1 against 53.7 ms), the default
measured 292.4 / 90.7 MB/s (new 1 GiB file / 4,096 × 64 KiB files, each including
the drain) against PrimoCache's 261.0 / 52.5. A concurrent disk reader got 757 IOPS
(p99 11.8 ms) against 170 (50.7 ms). Under Driver Verifier the final native build
passed `trim-cache` 12/12, the focused campaign 98/98 and an integrity run with
12,291 hashes. Qualification found and fixed one driver bug (asynchronous TRIMs
switched the caller path off) and four test-environment faults. `release-performance` on the final build is still running.
[Results](#final-164).

**Read-aware drain limit and asynchronous TRIM (plans 162/163, October 21):
Eager 2 MiB × 8 keeps its #15 speed and reads recover to the default's level.**
With read-aware limit 2, drainers start a batch only while fewer than two are
outstanding, and only while a foreground disk read is in flight or ended within
10 ms. A concurrent 4 KiB Q1 disk reader then got 1,031–1,141 IOPS (p99
10–11 ms), against 340 IOPS (29 ms) without the limit and 1,152 (10 ms) under the
1024 KiB × 4 default. During the reads the drain ran at 245–247 MB/s, the default's rate.
The #15 workloads never engaged the limit and measured the same as
Eager 2 MiB × 8 alone: 300.3 / 122.2 against 299.8 / 113.6 MB/s. Asynchronous
TRIM cut a mass-delete small-file case from 2.5 to 2.0 s. All checks passed under
Driver Verifier; one new TRIM check was redesigned after NTFS showed it could not
reach its target. [Results](#prio-162) and [the updated plan](#finish-15).

**Read during drain (plan 161, October 21): PrimoCache does not protect reads;
Eager 2 MiB × 8 hurts them half as much.** At the same drain and writer rate
(about 360 / 295 MB/s), a concurrent 4 KiB Q1 disk reader got 163 IOPS (p99 64 ms)
under Primo, against 277–331 IOPS (p99 29–30 ms) under QueueCache Eager 2 MiB × 8.
The 1024 KiB × 4 Idle default kept 959–1,153 IOPS (p99 11–12 ms), but drained
only about 250 MB/s. A read-aware limit is therefore not needed for Primo
parity. [Results](#read-161) and [the plan to finish #15](#finish-15).

**Same-window result (plans 159/160, October 20–21): Eager 2 MiB × 8 meets the
#15 medians; defaults unchanged.** TRIM now waits only for overlapping in-flight
drain writes (native 0.4.766.1). Under Driver Verifier, 103/103 subchecks and an
Eager 2 MiB × 8 file-integrity run passed. In an A–B–A bracket with
matched backend latency, PrimoCache measured 285.5 / 70.4 / 315.8 MB/s (1 GiB,
4,096 files, 8 GiB). Eager 2 MiB × 8 measured 304.9 and 298.3 / 142.6 and
132.2 / 294.1 and 288.9 in the two QueueCache blocks; the default 1024 KiB × 4
Idle measured 184.1 and 170.0 / 86.6 and 80.0. Eight drainers still cut a
concurrent disk reader's IOPS about in half (545 against 1,094; plan 151
reproduced). A read-aware drain limit and release qualification come before any
default change. [Results and decision](#trim-160).

**Matched research (plan 158, October 18–20): drain during writes is the #15
candidate.** 47 complete runs and 454,794 bypassed hashes passed. The Ceph-backed
disk changed speed up to threefold within hours, so products were compared by
matched drain latency at the same bytes in flight. Against Primo's quiet window
(120.5 / 59.9 / 126.9 MB/s for the 1 GiB, 4,096-file and 8 GiB workloads), the
default 1024 KiB × 4 Idle measured 96.5 / 59.0 / 92.6, and 2 MiB × 8 Eager
141.3 / 173.3 / 125.5. Drain speed follows bytes in flight. Small files are
limited by writing, then draining, which Eager overlaps; request size is not the
limit. QueueCache absorbs the small files faster than Primo (1.15 versus
1.90 s). Eager has one new blocker: each TRIM waits for every in-flight drain
write, stalling writes for up to 25 s after deletes. Foreground-read
interference is still unmeasured. Defaults unchanged.
[Results, corrections and decision](#matched-158).

**First investigation: drain limits and crash ordering (2026-10-16).** A short
source review and reanalysis of the existing complete plan-148 timelines found
two concrete performance candidates; no new VM run or driver change was made.
`Drainer` gathers adjacent blocks, submits one lower write and waits for it.
`MaxBatchBytes`, the worker array and policy validation cap this at **1 MiB × 4**.
During the three large-file cases, the sampled physical-disk queue was exactly
4 for approximately 98%, 99% and 98% of the scored interval. Primo's queue was
mostly 8–9, with mean backing writes about 1.9 MiB versus QueueCache's 1.0 MiB.
These are total guest-disk counter observations, including metadata, not proof
of Primo's internal thread count or a physical-media queue.

| Existing QueueCache small-file case | RAM/application interval | Completed interval | Backing write operations | Mean backing write | Time sampled queue was 4 |
|---|---:|---:|---:|---:|---:|
| `drain-small-r2` | 1.542 s | 6.400 s | 889 | 341.9 KiB | 74.5% |
| `drain-small-r3` | 1.111 s | 14.619 s | 918 | 331.2 KiB | 92.2% |

The slow case admitted its writes faster, transferred essentially the same
296.9 MiB including metadata, and performed only 3.3% more backing writes.
Its busy queue shifts investigation toward lower-path latency/concurrency;
it does not identify a Ceph cause or exclude driver effects. Queue percentages
weight each observed depth until the next sample and clip to the scored UTC
interval; the 100 ms sampling cannot resolve short gaps. Source runs are the
same published Direct/QueueCache/Primo runs above; no incomplete samples were used.

**Candidate order for #15:** compare 4 versus 8 outstanding drain writes and
1 versus 2 MiB gathers as separate factors, retaining the present defaults until
qualified. Both require a scoped driver/management/verification extension;
they are not currently accepted CLI settings. Check lower-device transfer,
scatter/gather and alignment limits before issuing larger requests
([Microsoft adapter descriptor](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddstor/ns-ntddstor-_storage_adapter_descriptor)).
If small writes remain fragmented, inspect which gaps, partial-sector versions,
in-flight overlaps or oldest-block selection terminate batches; a bounded
address-aware picker is a later candidate. Do not fill gaps by writing unknown
sectors. Broad lock/copy rewrites are lower priority while the lower queue stays
occupied; existing timing counters can distinguish selection/copy/retirement
from lower waits in a separate diagnostic, with scored runs timing off.

**Candidate direction for #13:** Fast's `IRP_MJ_FLUSH_BUFFERS` branch returns
success without a durable barrier or an ordering marker, and `QC_SLOT` has no
flush-group identity. Pending writes to an unpinned, non-in-flight block can be
coalesced across those acknowledgments. A candidate is to retain flush and
write-through boundaries as dependency groups, preserve required older block
versions, and make a predecessor group durable before writing dependent blocks.
This could preserve RAM-first acknowledgment while delaying destination writes;
it is an unproven design direction, not a crash-safety guarantee. Merely sorting
by address, retaining arrival order, or tagging the latest block version is not
enough to establish the required journal dependencies. Persistent redo/recovery
logging is a fallback with extra I/O and boot-recovery work, not the first small
optimization. Passing every Fast flush through as Strict changes the volatile
contract and is not an incidental fix.

Keep the next step narrow: a short lower-I/O diagnostic, then a maintained,
versioned 2 × 2 batch/concurrency experiment with three repetitions of both
existing file shapes (24 cases), hashes and restoration. A fresh full competitor
refresh is unnecessary for screening. For crash safety, first audit one
journal/dependent-write ordering sequence and its overwritten versions; only
then design a targeted partial-drain cut. These are proposals, not scheduled or
completed runs; neither issue is resolved by this inspection.

**Individual VM experiments (plan 149, 2026-10-16).** Branch
`experiment/drain-batch-concurrency`, tested source `b417470`, CI build
**0.4.749.1**. Raise the experimental ceilings to 2 MiB and eight drainers;
keep the 1024 KiB × 4 defaults. Native and managed bounds, compile-time policy
checks and the existing `compare-drain` profile validation were extended together.
The file workload, payload and timing endpoint are unchanged from plan 148.
The lower adapter reports 2,097,152 maximum transfer bytes, 513 physical pages
and alignment mask 3. Every arm uses this same build, including its larger fixed
staging reservation (16 MiB), and the same temporary snapshot.

| Runtime arm | New 1 GiB + drain MB/s, median (range) | New 4,096 × 64 KiB + drain MB/s, median (range) | Mean backing write KiB, large / small |
|---|---:|---:|---:|
| Starting control, 1024 KiB × 4 | 179.57 (153.10–185.13) | 98.82 (80.13–102.23) | 1004.8 / 737.9 |
| More concurrency only, 1024 KiB × 8 | 213.58 (211.81–222.09) | 106.54 (94.70–110.86) | 1007.7 / 333.2 |
| Larger batches only, 2048 KiB × 4 | 225.02 (218.36–232.23) | 103.00 (98.49–107.79) | 1986.7 / 298.3 |
| Ending control, 1024 KiB × 4 | 170.68 (160.01–179.52) | 91.45 (87.04–93.34) | 1012.5 / 188.1 |

All four full six-case runs completed with restoration: **24 workload cases and
49,164 unbuffered file hashes passed**. Each row has three repetitions per shape.
The four comparison workers took about **4 minutes 17 seconds** combined; CI,
deployment, correctness checks, reboots and the required 15-minute post-boot
settle were additional setup time. The maximum-settings 2048 KiB × 8 Eager
file-integrity smoke also passed under Driver Verifier, including repeated
overwrites, live reads, flush/reopen and copy hashes. File TRIM is unsupported
and broader race/fault/lifecycle paths are outside that smoke. Actual loaded
hashes matched the CI artifact; performance runs used Verifier and timing off,
Fast 2 GiB Idle, flags 15, normal priority, Defender/Search disabled and the
background apps/service stopped. Saved profiles were unchanged.

After exporting and verifying all evidence, the original 0.4.687.1 module hashes,
Fast 2 GiB 1024 KiB × 4 runtime/saved profile and original apps/service were
restored and checked. Defender/Search remain disabled. The temporary snapshot
was removed; no snapshots remain, and the final capacity/near-full guard passed
with 148.3 GB free.

Nine cases recorded 8–64 KiB newly dirty between the scored zero-pending endpoint
and untimed Disable; their source is unattributed. Disable drained it and every
case's final paused observation was clean. One closing-control pause also
exercised the transient-Draining observation wait and then confirmed clean,
without replaying Disable. As with plan 148, the hashes validate the later paused
state, not the exact earlier scored instant or power-loss durability.

**Screen decision:** both individual changes merit follow-up for large files.
Eight drainers improved the median **18.9% / 25.1%** versus starting/ending
controls; 2 MiB batches improved it **25.3% / 31.8%**. Every candidate large-file
sample exceeds both control ranges. The controls drifted **−4.9% large / −7.5%
small**, and arms were grouped, not alternated pairs. Small-file median gains
were only **7.8%** (eight drainers) and **4.2%** (2 MiB) against the starting
control, with overlapping ranges and substantially varying backing-write sizes.
Do not qualify either small-file gain as stable or describe this as a causal
percentage against the released driver. The 2 MiB arm doubled mean large-file
write size but did not increase mean small-file write size.

The first candidate selected from this screen was **2 MiB × 4**. The later
[decision screen](#drain-decision-153) completes 2 MiB × 4/8 and fresh provider
controls; broader Strict/sustained/default qualification remains unrun.
Ceph retained hosts/OSDs-down and inactive/stale-PG warnings. Capacity
and near-full guards passed, but shared-storage behavior remains an uncertainty.
Even today's controls are much faster than the earlier plan-148 QueueCache
52.71 / 41.71 MB/s, so these observations cannot replace the matched three-product
comparison or establish Primo parity. No crash-safety change or power cut ran;
[#13](https://github.com/devedse/QueueCache/issues/13) and
[#15](https://github.com/devedse/QueueCache/issues/15) remain open. No production
default change or full release qualification is justified by this screen alone.

**Allocation blocker found after this screen, now fixed:** the larger fixed staging
reservation was 16 MiB even at the default runtime settings. `Configure` then
switched to it at a 16 MiB cache budget, allowing its unsigned slot-count
subtraction to underflow. Native 0.4.756.1 fixes it, with fourteen small-budget
Fast/Strict checks under Verifier described below. The earlier 2 GiB measurements
did not exercise this allocation edge. Further ceiling
increases also need bounded staging and selection storage: the current design
would reserve 256 MiB for sixteen 16 MiB workers, and its local `selected` array
would grow from 2 KiB to 16 KiB on the kernel stack. These are source-derived
costs, not measured failures. On this adapter, 4–16 MiB gathers also exceed the
reported 2 MiB single-transfer limit and require a deliberate split strategy.
The later read/drain diagnostic found a foreground-read penalty at eight drainers;
the decision screen attributes most small-file batch endings to missing dirty
neighbors. Further ceiling increases are therefore deferred in favor of gap
classification and read-aware concurrency. Mixed foreground latency under Idle,
broader Strict/flush correctness and sustained checks must precede a new default.

Exact completed run suffixes (each prefixed `QueueCache-Verify-`): starting
control `20261016-132412-6209369546514c2f974d041502ca6979`; eight drainers
`20261016-132619-c2e501747076483999a489b541daa22d`; 2 MiB batches
`20261016-132900-e8107338c2094977b5890ac873ffd9a8`; ending control
`20261016-133018-7b62779ae82c43aea3c25c11d75550cf`. Verifier smoke:
`20261016-130550-0a059f72bc3e431cba1e18f3d290bcb3`.

<a id="final-164"></a>

**The new default and its final qualification (plans 164–167, October 21–22).**
Commit `ce21c5a` makes new caches drain Eager with 2048 KiB batches and eight
drainers. The driver also enables read-aware limit 2 and TRIM mode 3 (index lookup
and asynchronous lower TRIM) at cache creation. Saved profiles keep their settings:
after the upgrade the VM's startup service reapplied Q:'s saved 1024 KiB × 4 Idle
profile about 150 s after boot. The final native build is 0.4.776.1 (`ec10204`;
loaded filter and RAM-disk hashes `9F572C17…E29F` / `B0382266…BCE6`). It includes
the caller-path fix below. Correctness runs 21 and 23 used its own controller;
run 29, the comparison and the release campaign used controller 0.4.780.1
(`38a667e`, plan 167). That controller changes only verification code, and its rebuilt
native binaries were not loaded.

*Correctness, under Driver Verifier 0x209bb on both drivers, defaults as shipped*
(diagnostics confirmed read-aware 2 and TRIM mode 3 on Q: and the lab volume):
- `trim-cache` 12/12.
- `compare-drain` on the default profile: 6/6 cases and 12,291 bypassed hashes.
- The focused `stress-races` campaign: 6/6 phases and 98/98 subchecks. Its busy
  window completed 11 RAM-disk cycles, retrying 8 creations that the RAM headroom
  guard refused.
- The plan-165/166 `parallel-copies` diagnostic: 11 one-minute cases returned only
  whole versions. The one torn read is explained below; three later cases refused
  a request-splitting file layout instead of running.

*What qualification found, in order:*
1. **Driver: asynchronous TRIMs switched the caller path off** (fixed in `87de299`,
   native 0.4.776.1). They were counted in `PagingQueued`, and dispatch treats any
   queued offload as several outstanding requests. While one slow lower TRIM was
   outstanding, every caller-thread candidate was declined and the next 256 backed
   off. In release attempt 11, `foreground-background` served only 10,792 of
   950,056 requests on the caller's thread; the lab volume's counters showed 962,833
   backoff requests after only 6 asynchronous TRIMs. On the lab VHDX each lower TRIM
   goes through Q: to the Ceph-backed disk. TRIM entries no longer count; the worker
   path already excluded them from range waits.
2. **Runner: a lab-image write-back race** (fixed in `ec10204`). Release attempt 10
   stopped at its first performance phase. The lab volume's image lives on Q:, and its
   cleanup was still writing back after Q:'s own preparation had drained, so the
   strict clean capture failed. A performance phase now first prepares the lab
   targets stored on its volume.
3. **Tests: torn reads and caller-path shortfalls at file-extent splits** (fixed in
   `3487f50` and `6ed3a16`). `policies` and `pressure` left their folders on the lab
   volume root, outside the runner's work directory, until the 8 GiB volume had
   1.7 GiB free. The plan-165 diagnostic then reproduced the torn read in six
   minutes, at 1024 KiB batches: chunks 0–199 from pass 434 and 200–255 from pass
   435. That file's extent boundary was at cluster 0x1c8, i.e. 1 MiB + 800 KiB, inside
   the block at exactly chunk 200. NTFS sent that 1 MiB read and write each as two
   volume requests, and nothing makes such a pair atomic, with or without a cache.
   The two `foreground-background` shortfalls used six-extent hot-set files. Both
   suites now write under the run's work directory. `parallel-copies` and
   `foreground-background` recreate a file whose extent boundary would split one of
   their requests, and fail if it stays split. The lab volume was cleaned and its
   free space consolidated.
4. **Tests: RAM-headroom refusals in `stress-races`** (fixed in `38a667e`). Its owned
   2 GiB RAM-disk churn was refused in 3 of 4 runs on the new builds. Sampling every
   2 s showed 8.2–8.5 GiB available at idle on the 16 GiB VM and about 6.2 GiB while
   a churn disk existed. A creation needs 6 GiB, the disk itself plus 4 GiB of
   headroom, which left about 200 MiB of margin. The broker refused correctly; the
   test now retries a refused creation and still fails with fewer than two cycles.
   Why refusals became common on these builds is not established. Its busy-volume
   caches now inherit Eager instead of Idle.

*Same-window A–B–A, Verifier off:*
- **Block A** (21:02–21:13 UTC): QueueCache on the default profile.
- **Block B** (21:35–21:47): PrimoCache 4.4.1 with QueueCache uninstalled; shared
  2 GiB L1, 4 KiB blocks, Native defer 10 s.
- **Block C** (22:16–22:30): QueueCache again, after rollback to the same snapshot
  and redeployment.

The QueueCache arm ran `policy apply Q: --budget-mib 2048 --preset Fast
--accept-volatile-flush --runtime-only` with no drain options and no lab switches.
Diagnostics confirmed Eager 2048 KiB × 8, read-aware 2 and TRIM mode 3. All 12 runs
completed, with 110,619 bypassed file hashes.

| MB/s, median of 6 (range) | Paused anchor, 1 GiB / small | New 1 GiB + drain | ÷ anchor | 4,096 × 64 KiB + drain | ÷ anchor | Drain latency ms |
|---|---:|---:|---:|---:|---:|---:|
| A: QueueCache default | 95.9 / 6.5 | 292.4 (212.6–322.4) | 3.05 | 90.7 (80.7–99.1) | 13.96 | 53.1 |
| B: PrimoCache 4.4.1 | 96.3 / 6.7 | 261.0 (242.6–290.3) | 2.71 | 52.5 (49.7–59.3) | 7.84 | 53.7 |
| C: QueueCache default | 93.9 / 4.9 | 295.0 (128.2–323.0) | 3.14 | 49.6 (34.8–111.2) | 10.05 | 51.9 |

| Read during drain (4 KiB Q1) | Reader alone, IOPS | Reader while draining, IOPS (range) | p99 ms | Disk writes in the window, MB/s |
|---|---:|---:|---:|---:|
| A: QueueCache default | 1,524 | 757 (732–800) | 11.8 | 234 |
| B: PrimoCache 4.4.1 | 1,944 | 170 (131–243) | 50.7 | 421 |
| C: QueueCache default | 456 | 398 (274–401) | 14.7 | 210 |

**A against B, at matched anchors and drain latency: the default beats PrimoCache
on both #15 workloads.** It is 12% faster for the 1 GiB file and 73% faster for small
files. A concurrent disk reader got 4.5 times Primo's IOPS at a quarter of its p99,
while Primo drained faster during the reads (421 against 234 MB/s). Block C ran on a
degraded backend. The reader alone fell to 456 IOPS and the small-file anchor to
4.9 MB/s, and the default's small-file application phase doubled to 5.3 s. Raw, its
small-file median (49.6) is below block B's Primo (52.5); per anchor it stays ahead
(10.05 against 7.84). Other Ceph clients read 4.8 MB/s on average then, against
0.9–1.5 during A and B; the cause is not established.
A same-deployment repeat of block C on 2026-10-22 is still to be exported.

*Release qualification:* still running. Attempts 13 and 14 stopped in
`ram-read-reference`: the second owned 2 GiB RAM disk, created right after the
first was stopped, was refused by the RAM headroom guard while that memory was
still returning to Windows. Plan 168 waits up to 60 s for headroom first; attempt
15 runs on controller 0.4.784.1 beside native 0.4.776.1.

Evidence: exports `20DE474A…61F0` (10,133 files) and `08B586A2…FE50` (675 files),
checked file by file against guest SHA-256 manifests. Verifier runs
`QueueCache-Verify-20261021-184820-9e4b93dcd4654e1badf709e762fa2750` (`trim-cache`),
`20261021-185202-c0307530a60c494e8fe0fc65674abf3a` (integrity) and
`QueueCache-Campaign-20261021-203107-a44bd25af1bb47f38c8b88260b872897` (focused).
Comparison runs `20261021-210214-…` (block A) to `20261021-222831-3281edf2e47f426f99719ded27979ba3`
(block C). Excluded: release attempts 10 and 11 (finding 1 and 2), the focused
campaigns that stopped on findings 3 and 4, and run 30, whose app-closing helper
collided with an orphaned run 29 left by an agent restart. Run 29 completed on its
own as the block-B paused anchor.

<a id="prio-162"></a>

**Read-aware drain limit and asynchronous TRIM (plans 162/163, October 21).**
Native 0.4.771.1 from source `0406d48`, with loaded filter and RAM-disk hashes
`66769C20…7535` / `3F415741…042F`. Both features were then default-off lab switches
(`qcache developer driver read-aware` / `trim-mode`); they are defaults since
[plan 164](#final-164):
- *Read-aware limit N.* While a foreground lower read is in flight or ended
  within the last 10 ms, a drainer starts a batch only while fewer than N are
  outstanding. Barriers and paging-write fences keep full concurrency.
- *TRIM mode 3.* A TRIM of at most a quarter of the cache visits only its blocks
  through the index instead of scanning every slot under the lock. The lower
  TRIM is sent from an offload thread, so the ordered request path continues.
  The cache side applies at once. A fence keeps any newer version of the trimmed
  ranges from draining before the lower TRIM completes, and forwarded writes to
  those ranges wait for it.
The controller was 0.4.771.1 (plan 162), except for the plan-163 `trim-cache`
run, which used controller 0.4.772.1 from `4e9d0c0`; its rebuilt native binaries
were not loaded.

*Correctness, under Driver Verifier 0x209bb on both drivers:*
- The focused `stress-races` campaign passed 6/6 phases and 98/98 subchecks
  twice: once with both switches off, and once with read-aware 2 and TRIM mode 3
  on Q: and the lab volume. The second run's diagnostics show the switches held
  in every phase; the read-aware gate made drainers wait 903 times, and 7
  asynchronous TRIMs ran, none falling back.
- An Eager 2 MiB × 8 `compare-drain` run with both switches passed 6/6 cases and
  12,291 bypassed file hashes.
- `trim-cache` (plan 163) passed 12/12. Every earlier TRIM check passed in both
  TRIM modes: with mode 3, an unrelated TRIM returned in 13 ms with 3 MiB in
  flight and an overlapping one after 2,022 ms. While a TRIM was held 2 s,
  another file's 4 MiB rewrite and read took 14 ms. In `async-eager-ordering`,
  another file's rewrite drained to zero dirty bytes while that TRIM was held. A
  rewrite of the held range waited until the TRIM completed, and the disk then
  held it exactly.
- The plan-162 `async-fence` check failed (11/12, run excluded). It expected a
  rewrite of the held range to reach the cache at once. It took 1,810 ms
  instead and arrived after the TRIM: NTFS keeps a file's writes behind its
  outstanding file-level TRIM. A file rewrite therefore cannot reach the
  fence, and the verifier performs no raw disk writes. The fence's holding path
  and the forwarded-write wait stay unexercised (see [known issues](KNOWN_ISSUES.md#trim-no-longer-waits-for-unrelated-drain-writes)).

*Performance, Verifier off.* The nine runs took 16:10–16:37 UTC on one boot;
another Ceph client's reads averaged 2.0 MB/s (maximum 12.6). Read during drain
(`compare-drain-read`, three alone and three loaded windows per run; medians, ranges across the loaded
windows):

| Setting | Reader alone, IOPS | Reader while draining, IOPS (range) | p99 ms | Disk writes in the window, MB/s | Writer, MB/s | Gate waits |
|---|---:|---:|---:|---:|---:|---:|
| Eager 2 MiB × 8 | 1,839 | 340 (336–355) | 29.4 | 413 (351–431) | 325 | 0 |
| Eager 2 MiB × 8, read-aware 2, runs 11 / 18 | 2,170 / 2,263 | 1,141 (1,034–1,191) / 1,031 (992–1,127) | 10.2 / 11.3 | 247 / 245 | 285 / 269 | 13,813 / 13,748 |
| Eager 2 MiB × 8, read-aware 1 | 2,067 | 1,232 (1,075–1,560) | 7.8 | 170 (163–175) | 251 | 16,091 |
| 1024 KiB × 4 Idle (default) | 2,190 | 1,152 (1,110–1,162) | 10.4 | 249 (248–251) | 202 | 0 |

The #15 workloads (`compare-drain`) ran four times, alternating control and candidate,
with 12,291 hashes each:

| MB/s, median of 6 (range) | New 1 GiB + drain | 4,096 × 64 KiB + drain |
|---|---:|---:|
| Eager 2 MiB × 8, runs 14 / 17 | 299.8 (293.0–320.0) | 113.6 (88.9–131.6) |
| Eager 2 MiB × 8, read-aware 2, TRIM mode 3, runs 15 / 16 | 300.3 (265.2–314.2) | 122.2 (117.0–132.4) |

**Read-aware 2 restores the default's read responsiveness only while disk reads
happen.** With 2 MiB batches it allows 4 MiB in flight, the same as the default's
4 × 1 MiB. Reader IOPS, p99 and the drain rate during reads all matched the
default. In the #15 runs the gate never engaged (0 waits), and the large- and
small-file medians matched the control, so the limit costs nothing when no disk
read competes. Read-aware 1 protected reads a little more (p99 7.8 ms) but
drained at 170 MB/s, which does not justify it.

**Asynchronous TRIM removed the TRIM stall in mass-delete cases.** All 793
TRIMs in the candidate runs went asynchronous, with no fallbacks. In the
small-file case that received the TRIMs from the previous case's deletes, the
control took 2.52 and 2.58 s of application time (227–290 TRIMs, 3.2–3.9 s of
request queue wait). The candidate took 2.01 s (257 TRIMs, 1.1 s). That matches
the 0.5–1 s that plans 159/160 attributed to synchronous lower TRIMs. Small-file
cases without TRIMs took 1.96–2.98 s in both arms. No PrimoCache ran in this
session, so parity rests on the final same-window check. For orientation only:
the plan-159/160 window measured Eager 2 MiB × 8 at 298.3–304.9 / 132.2–142.6
and Primo at 285.5 / 70.4 MB/s.

Evidence: export `E5A0C5A5…60FC` (3,427 files), checked file by file against the
guest SHA-256 manifest. Verifier campaigns
`QueueCache-Campaign-20261021-153116-9b85e935b38347928044dfc430de543c` (switches
off) and `20261021-154027-54212b91ad2849308758b596f540979c` (on); Eager integrity
`QueueCache-Verify-20261021-154946-3083b8ac44ea496dbe31e9137a5b3c12`; `trim-cache`
plan 163 `20261021-155304-decee315cf2746aca7e2f098b50b9b1e`; excluded plan-162
attempt `20261021-152542-6b3f97ca9801418ba41773860aadeabb`. Performance runs
`20261021-161044-…` to `20261021-163325-48b6b5a9a2cb4c569c0b8a68a6485489`. The VM
was then restored and checked against the recorded original state: 0.4.687.1
hashes, the Fast 2 GiB 1024 KiB × 4 Idle profile, flags 15, timing and Verifier
off, original apps and Everything service, no owned files. The snapshot is
deleted (none remain), and the guard passed.

<a id="read-161"></a>

**Read during drain against PrimoCache (plan 161, October 21).** The question
was whether PrimoCache protects foreground reads while it drains, which would
make a read-aware limit necessary before Eager 2 MiB × 8 could be the default.
The new `compare-drain-read` suite runs the same DiskSpd windows for every
product. A 4 KiB random Q1 reader works on an uncached 8 GiB file, alone, and
12 s after a sequential 1 MiB Q8 writer started keeping the product's 2 GiB
cache full. The product therefore drains at its own full rate throughout.
Loaded windows must show pending data at both reader boundaries and at least
64 MiB written to the disk; alone windows at most 16 MiB.

Native 0.4.766.1 (the plan-159/160 TRIM-fix build; loaded hashes matched
`D68C25AD…0C11` / `489EE1D3…6CC8`) ran with controller 0.4.769.1 (`4441f09`,
plan 161). The session was A–B–A in one afternoon: QueueCache, PrimoCache 4.4.1
(same task settings as before), then QueueCache again after rollback and
redeploy. Foreign Ceph reads averaged 1.8 MB/s. All 10 runs completed; each has
three alone and three loaded windows. Reader figures are medians; ranges are
across the three loaded windows.

| Product and setting | Reader alone, IOPS | Reader while draining, IOPS (range) | p99 ms | Disk writes in the window, MB/s | Writer, MB/s |
|---|---:|---:|---:|---:|---:|
| PrimoCache, 2 runs | 2,598 | 163 (107–366) | 64.0 | 356 | 294 |
| QueueCache Eager 2 MiB × 8, A / C | 1,936 / 1,852 | 331 / 277 (250–377) | 29.4 / 30.0 | 363 / 420 | 296 / 320 |
| QueueCache Eager 2 MiB × 4, A / C | 2,012 / 1,673 | 497 / 414 (400–769) | 23.3 / 21.1 | 244 / 282 | 213 / 246 |
| QueueCache 1024 KiB × 4 Idle (default), A / C | 2,107 / 1,747 | 1,153 / 959 (948–1,216) | 10.7 / 11.6 | 251 / 248 | 203 / 201 |

**PrimoCache does not protect reads while it drains.** At the same drain and
writer rate, Eager 2 MiB × 8 left the reader about twice Primo's IOPS and half
its p99. So making Eager 2 MiB × 8 the default costs nothing relative to Primo,
and a read-aware limit is not needed for parity. The cost is relative to today's
default: during heavy draining a disk reader gets about 300 instead of about
1,000 IOPS, with p99 around 30 instead of 11 ms. In exchange, sustained drains
are 45–70% faster (363–420 against 248–251 MB/s), and the #15 workloads were 65–75% faster
(plans 159/160).
Reader slowdown follows the bytes in flight (4 → 8 → 16 MiB), as the plan-151
and plan-160 diagnostics also showed.

One side observation: alone, QueueCache's uncached 4 KiB Q1 reads ran at
1,673–2,107 IOPS against Primo's 2,598. That is a 20–35% slower single-request
miss path, separate from draining and not investigated here.

Eager 2 MiB × 4 is not a useful middle ground. It drained no faster than the
default under sustained writes, slowed the reader about twice as much, and
measured 238.0 / 235.2 MB/s for the new 1 GiB file (small files 111.3 / 124.5).
That is short of Primo's 285.5 in the plan-159/160 window.

Evidence: exports `BC349AE7…ACC2`, `F2EFA896…8568` and `08BF8AAE…A39C`, checked file
by file against guest SHA-256 manifests. One arm stopped before measuring,
because Steam's first post-boot shutdown exceeded the helper's 45 s wait; it was
rerun from scratch. The VM was then restored and checked: original 0.4.687.1
hashes, the Fast 2 GiB 1024 KiB × 4 Idle profile, flags 15, timing and Verifier
off, original apps and Everything service. Primo is absent, no owned files
remain, the snapshot is deleted (none remain), and the guard passed.

<a id="trim-160"></a>

**TRIM fix and same-window comparison (plans 159/160, October 20–21).** Native
0.4.766.1 from source `402cb79`; the controller is 0.4.767.1 from `115fbf6`
(plan 160), a managed-only change that corrected the new TRIM checks. The loaded
filter and RAM-disk SHA-256 hashes were `D68C25AD…0C11` and `489EE1D3…6CC8`,
matching the 0.4.766.1 artifact; the 0.4.767.1 native binaries were not
substituted. Each drainer now records the disk range of its one outstanding
batch. A TRIM waits only while one of those ranges overlaps a trimmed range;
`TrimPaused` still starts no new batch, and the ordered request path still holds
later writes during the TRIM itself.

*Correctness, under Driver Verifier 0x209bb on both drivers:* the focused
`stress-races` campaign completed 6/6 phases with 98/98 subchecks (22 partial
reads, 8 paging, 42 policies, 13 pressure, 5 stress-races, 8 ordering/faults),
and `trim-cache` passed 5/5. A TRIM outside the in-flight batches returned in
2 ms with no write landing, while 3 MiB of 2 s lab-delayed batches stayed in
flight. A TRIM inside them returned after 2,008 ms, once those writes had landed.
Both left the untrimmed data exact. An Eager 2 MiB × 8 `compare-drain` run under
Verifier passed all 6 cases and 12,291 bypassed file hashes. The first plan-159
`trim-cache` attempt is excluded: its new check started the drain with an explicit
flush, which holds the ordered path, so its TRIM waited 10 s for the whole flush
whatever the TRIM rule. The existing `during-drain` check has the same shape and
therefore never exercised a concurrent TRIM. Plan 160 checks TRIM during
background Eager drains instead.

*Same-window A–B–A comparison, Verifier off:* block A (QueueCache, 22:14–23:01
UTC), block B (PrimoCache, 23:32–23:48), then block C (QueueCache again after
rolling back to the same snapshot and redeploying the same build, 00:17–00:34).
Another Ceph client's reads were near zero. The paused anchors were
72.6 / 2.9 MB/s rising to 114.4 / 7.4 during A, 111.9 / 8.1 and 108.7 / 6.9
around B, and 114.5 / 8.5 and 109.7 / 8.2 around C. Drain latency at 2 MiB × 8
was 52.6 ms (A Eager), 51.9 ms (Primo) and 50.3 ms (C Eager). All **22 complete
`compare-drain` runs and 233,538 bypassed file hashes** passed their proofs.

| MB/s, median (range) | New 1 GiB + drain | 4,096 × 64 KiB + drain | New 8 GiB + drain |
|---|---:|---:|---:|
| PrimoCache 4.4.1, block B | 285.5 (248.7–300.8) | 70.4 (55.6–87.0) | 315.8 (246.0–324.1) |
| QueueCache Eager 2 MiB × 8, block A | 304.9 (116.6–324.6), +6.8% | 142.6 (87.8–193.1), +102% | 294.1 (259.4–304.9), −6.9% |
| QueueCache Eager 2 MiB × 8, block C | 298.3 (281.7–328.1), +4.5% | 132.2 (120.9–163.1), +88% | 288.9 (284.0–314.6), −8.5% |
| QueueCache 2 MiB × 8 Idle, A / C | 284.3 / 214.1 | 91.0 / 69.3 | — |
| QueueCache 1024 KiB × 4 Idle (default), A / C | 184.1 / 170.0 | 86.6 / 80.0 | — |

**Eager 2 MiB × 8 meets both #15 medians in both QueueCache blocks bracketing
Primo.** The large-file ranges overlap: parity, with a 4.5–6.8% median lead.
Small files are about twice as fast, with block C's range wholly above Primo's.
Sustained 8 GiB writes trail Primo by 7–9%, with overlapping ranges. The default
trails Primo by 36–41% for the 1 GiB file and leads by 14–23% for small files.
Primo again took longer to absorb the 4,096 files (2.55 s; QueueCache Idle 1.34–2.06 s,
Eager 1.64–1.97 s). Two first-of-run Eager large cases (runs 11 and 51) took 3.1 and
3.6 s of application time without TRIMs or queue waits; their cause is untraced.

*TRIM in the file workloads.* No large-file case in plans 159/160 received the
100+ TRIMs that caused plan 158's 6.7 / 25.1 s stalls. Eager large cases with 5–32
TRIMs kept 0.20–0.22 s application time. Eager small-file cases with about 200
TRIMs took 2.02–2.34 s instead of 1.3–1.9 s; in plan 158 comparable cases took
2.24–4.21 s. The remainder is the TRIM itself: it is still sent synchronously on
the ordered request path, roughly 10–15 ms each by the queue-wait counter. An asynchronous lower
TRIM with a range fence is the next TRIM candidate.

*Foreground read interference (step 3), complete.* The plan-160
`drain-lock-diagnostic` completed 48/48 windows. One standalone window saw 5
background lower writes (20 KiB) and was remeasured as designed. Read scores are
timing off; drain rates are from the same windows.

| Reader | Drainers | Alone | With the Eager 2 MiB writer | Drain MiB/s |
|---|---:|---:|---:|---:|
| Uncached random 4 KiB Q1 | 4 | 2,824 IOPS, p99 0.698 ms | 1,094 IOPS, p99 15.413 ms | 238.2 |
| Uncached random 4 KiB Q1 | 8 | 3,352 IOPS, p99 0.577 ms | 545 IOPS, p99 28.457 ms | 274.7 |
| Cached 1 MiB Q8 | 4 | 28,109 MiB/s, p99 0.651 ms | 21,498 MiB/s, p99 0.988 ms | 215.5 |
| Cached 1 MiB Q8 | 8 | 25,549 MiB/s, p99 0.726 ms | 25,100 MiB/s, p99 1.007 ms | 282.5 |

Eight drainers gave +15% drain speed but halved concurrent disk-read IOPS (545
against 1,094) and nearly doubled p99 (28.5 against 15.4 ms). That reproduces plan
151 (465 against 1,057 IOPS, 24.5 against 13.7 ms), so plan 151's magnitude
stands. RAM-hit reads ran at about 97% VM CPU, and their alone controls varied;
no cached-read conclusion is drawn.

**Decision.** The #15 throughput goal is met by a configuration, not by the
defaults. Eager 2 MiB × 8 matched or beat a same-window Primo on both new-file
workloads, with byte, Verifier and TRIM checks passing. Defaults stay 1024 KiB × 4
Idle, for three reasons:
- At eight drainers a concurrent disk reader loses about half its IOPS.
- Eager drains compete with foreground I/O by design.
- Release-performance and Strict qualification have not run.

Next, in order:
1. A read-aware outstanding-write limit: full concurrency when no foreground disk
   read waits, fewer drain writes while one does.
2. An asynchronous lower TRIM.
3. A same-window re-check, then `release-performance`, before any default change.

Evidence: performance runs `QueueCache-Verify-20261020-222008-…` to
`20261021-003127-…`. Lock diagnostic `20261020-223909-4aeb1aeec2f34e72b09bf7aef631b305`.
Verifier campaign `QueueCache-Campaign-20261020-215026-659353b48a934421842e098abfa64839`,
`trim-cache` `20261020-220011-38d76dd275064013b8f06f4f573726fc`, Eager integrity
`20261020-220055-7b2ff598d72b4304a409ff60e62a9632`. Excluded check-design attempt:
`20261020-214645-b1b5ba0f19b1496fa8fd0689609afdf1`. All three exports were checked file
by file against guest SHA-256 manifests (archives `68986122…9057`, `635A60A4…80A7`,
`AE3A976F…890C`). The VM was then restored and checked: original 0.4.687.1
hashes, the Fast 2 GiB 1024 KiB × 4 Idle profile, flags 15, timing and Verifier
off, original apps and Everything service. Primo is absent, no owned files
remain, the temporary snapshot is deleted (none remain), and the final guard
passed. The VM work finished before the cluster's 03:00 backups.

<a id="matched-158"></a>

**Matched in-session research (plan 158, October 18–20).** Branch
`experiment/drain-batch-concurrency`, source `d741d05`, CI build 0.4.764.1.
Loaded filter and RAM-disk SHA-256 hashes matched that artifact
(`71BB49F0…2F27`, `F4C23485…2B19`). Plan 158 adds the 8 GiB
`compare-drain-sustained` suite and the `QueueCachePaused` / `PrimoCachePaused`
disk anchors. Settings: Fast 2 GiB, flags 15, timing/Verifier off, normal
priority, Defender/Search disabled, background apps closed, one temporary
snapshot present. Primo 4.4.1 used shared 2 GiB L1, 4 KB blocks, Native defer
10 s, no L2 or prefetch.

| Session (UTC) | Runs |
|---|---|
| S1 QueueCache, October 18 05:52–06:48 | Twenty interleaved `compare-drain` settings arms (1024/2048 KiB × 4/8 Idle, 2048 × 8 Eager), four sustained arms, anchors |
| S3 PrimoCache, October 18 12:28–15:21 | Five `compare-drain` and two sustained arms between three anchors |
| S4a QueueCache, October 18 20:00 | One arm under another client's 50–80 MB/s Ceph reads; the remaining arms were held |
| S4 QueueCache, October 20 16:52–17:49 | After that load ended: same build redeployed from the same snapshot; one 1024 × 4 Idle run, two runs each of 2048 × 8 Idle and Eager, two sustained arms, anchors at both ends |

All **47 complete runs and 454,794 cache-bypassed file hashes** passed their
byte, bypass and restoration proofs. The incomplete runs are excluded and never
combined. Three anchors stopped at 2.0–2.9 s telemetry gaps during backend
stalls. One sustained Eager run stopped before its third repetition, on the
strict busy-disk check after an 8 GiB delete, and was rerun complete. Three
read/drain lock diagnostics did not complete. The Primo post-oracle pause proof
now has real provider runs.

1. **The backend changed more than the products did.** At queue depth 1 the
   anchors measured 104–113 MB/s for the new 1 GiB file at 06:21, then 30–43 at
   11:43 (QueueCache paused), 37–59 at 14:22 and 32–46 at 15:12 (Primo paused).
   `ceph health` reported the same warnings throughout. From 17:30 another Ceph
   client read 50–80 MB/s at 800–1,200 operations/s. During that load,
   QueueCache 1024 KiB × 4 fell from 174.2 / 92.1 MB/s (S1) to 23.0 / 8.9 (S4a).
   Primo measured 120.5 / 59.9 MB/s at 12:28–12:40, when the other client read
   about 6 MB/s, and 81.2 / 37.6 at 14:34–15:12, when foreign reads reached
   18–70 MB/s for part of the window.
   Per-case drain latency (mean queue depth ÷ write IOPS) at 2 MiB × 8 was
   51–58 ms for QueueCache in S1, 111–151 ms in Primo's quiet window and up to
   357 ms later. Cross-session product gaps on this disk are therefore not
   product differences; see the corrections below.
2. **Drain speed follows the bytes in flight, with diminishing returns.** In
   S1's first round, every arm ran within eight minutes. The new 1 GiB file
   drained at 170.7 MB/s with 4 MiB in flight (1024 × 4), 220.9 / 220.2 with
   8 MiB (2048 × 4 / 1024 × 8), 274.5 with 16 MiB (2048 × 8) and 305.2 with
   Eager 2048 × 8. Equal bytes in flight from different shapes performed the
   same. Drain latency rose 23 → 35–36 → 51–58 ms: about 10–13 ms fixed plus
   2.7 ms per MiB in flight, which limits that backend state to about
   350–400 MB/s. Twelve-case session medians were 174.2 / 215.0 / 210.0 / 213.9 /
   293.8 MB/s. A backend dip at 06:11–06:18 slowed whichever arms ran then
   (runs 16–19); the wide ranges of the 2048 KiB arms come from that dip, not
   from the settings. 1024 × 4 happened not to run inside it.
3. **Small files are limited by sequencing, not request size.** Under Idle a
   small-file case is about 1.15 s of application writes, then 1.2–2.3 s of
   drain. Its drain phase reached 182–265 MB/s with 387–1041 KiB writes in S1's
   first round, close to the large file's drain rate at the same bytes in
   flight. Eager drains during the writes: the drain left after the application
   ended fell to 0.07 s, and the first-round median rose 114.5 → 183.9 MB/s at
   2048 × 8 (twelve-case 76.2 → 177.5). Larger gathers have little left to gain
   for this shape. Primo took a median 1.90 s to absorb the same 4,096 files,
   against QueueCache's 1.15 s, in both of its windows. That application-phase
   gap does not depend on the backend; the 1 GiB file took both products about
   0.15 s.
4. **Sustained 8 GiB writes run at the drain rate once the cache fills.** S1:
   1024 × 4 156.1 MB/s (139.0–165.3), 2048 × 8 183.4 (122.4–218.7), only +17%.
   Primo: 126.9 (98.0–134.2) in its quiet window, 82.4 (78.3–122.7) later.
5. **TRIM stalls Eager writes.** NTFS's TRIMs for the previous case's 4,096
   deleted files landed in 32 of 132 S1 cases. Under Eager, 144 / 272 TRIM
   requests stretched the 1 GiB file's application time from 0.16 s to 6.7 s /
   25.1 s, and three small-file cases from about 1.3 s to 2.2–4.2 s. The driver's
   queue wait (6.6 s / 29.5 s) matches. Each TRIM pauses the drainers and waits
   for every in-flight lower write, overlapping or not, while the ordered
   request path holds later writes. Under Idle, TRIMs mostly land when little is
   in flight. The large file's application time stayed 0.15 s; five small-file
   cases with TRIMs took a median 1.74 s instead of 1.15 s. This also shows that
   the VM disk now accepts TRIM ([known issue](KNOWN_ISSUES.md#trim-no-longer-waits-for-unrelated-drain-writes)).
6. **Foreground read interference (step 3) is still unmeasured on this build.**
   Three `drain-lock-diagnostic` attempts stopped at 5/48 and 13/48 (a standalone
   control saw background writes) and 26/48 (a 2.4 s telemetry gap during a
   backend stall). Partial windows point the same way as plan 151, but their
   disk-read-alone controls swung 740 → 137 IOPS; no magnitude is claimed. Why
   standalone windows now see background writes (possibly the newly accepted
   TRIMs) is untraced.
7. **QueueCache versus Primo, matched by backend latency.** S4 ran when the
   drain latency at 2 MiB × 8 was 102–142 ms for QueueCache Idle. Primo's quiet
   window measured 111–151 ms at the same shape: about 16 MiB in flight in
   ~2 MiB writes. Against that window:

   | Mode | New 1 GiB + drain | New 4,096 × 64 KiB + drain | New 8 GiB + drain |
   |---|---:|---:|---:|
   | PrimoCache, S3 quiet window | 120.5 (105.0–141.0) | 59.9 (40.5–67.1) | 126.9 (98.0–134.2) |
   | QueueCache 1024 × 4 Idle (default), S4 | 96.5 (93.8–101.0), −20% | 59.0 (46.4–64.8), −2% | 92.6 (74.0–103.2), −27% |
   | QueueCache 2048 × 8 Idle, S4 | 126.5 (106.0–156.0), +5% | 50.6 (34.9–95.3), −16% | — |
   | QueueCache 2048 × 8 Eager, S4 | 141.3 (79.9–314.3), +17% | 173.3 (38.2–204.1), 2.9× | 125.5 (124.8–144.6), −1% |

   Default settings trail Primo for the large and sustained files. With the same
   bytes in flight, 2048 × 8 Idle matches Primo for the large file. Eager
   2048 × 8 beats Primo's medians for both #15 workloads and matches it
   sustained. The large-file ranges overlap. The match uses a measured
   covariate, not simultaneous runs, so this is evidence for the direction, not
   a parity qualification. S4's own anchors moved from 81.9 / 2.4 MB/s to
   113.3 / 7.2 during the session. S4 Idle small-file writes averaged
   185–272 KiB, against 511–751 KiB in S1, for an unknown reason; Eager's
   small-file result was insensitive to it.

**Decision.** Defaults stay 1024 KiB × 4 Idle. For #15 the candidate is
draining during writes (Eager, or an earlier Idle start) at 2 MiB × 8; it is the
only measured setting that meets both #15 medians here. Before promotion it
needs three things. First, TRIM must wait only for overlapping in-flight writes.
Second, a completed foreground-read measurement, adding a read-aware limit if
eight drainers still hurt readers. Third, a qualification campaign that compares
against Primo within one window. Batching and gap-filling work is deprioritized.

**Corrections to earlier sections.** Plans 153/154 put 2 MiB × 8 23.2% / 33.7%
behind Primo, but the QueueCache arms ran at 22:05–22:20 and Primo at 00:02.
Given the swings above, that gap measures the backend as much as the products.
The plan-153 batch attribution (94–96% of small-file batches end at an uncached
gap) is accurate but does not identify the bottleneck; finding 3 does. The
plan-155/156 aligned gather stays rejected because it showed no gain. Its −16.3%
small-file loss is within the variation one arm showed within an hour here
(2048 × 4 small: 57.6–116.4 MB/s). Plan 151's direction (eight drainers hurt disk
readers more than four) remains plausible. Its −56% magnitude came with
disk-read-alone controls ranging 1,869–3,449 IOPS.

Evidence: S1 runs `QueueCache-Verify-20261018-055219-…` to `-064348-…`, S3
`20261018-122803-…` to `-151158-…`, S4 `20261020-165208-…` to `-174540-…`. Full
identities, per-case phases, contention samples and TRIM counters stay in the
private evidence. All three exports were checked file by file against guest
SHA-256 manifests: archive hashes `F594F10A…22EE`, `8FD1A099…1D75` and
`B2AC6FC3…EB44`. Restoration was verified afterwards. The VM is back on the
original 0.4.687.1 hashes with the Fast 2 GiB 1024 KiB × 4 Idle profile, copy
flags 15, timing and Verifier off. Its original apps and the Everything service
are running, Primo is absent, and no owned files remain. The temporary snapshot
is deleted (none remain), and the final capacity guard passed with 298.4 GB free.

<a id="finish-15"></a>

**Plan to finish #15 (status after plans 164–167: steps 1 and 2 done; step 3 awaits the owner).**
This replaces the step table below, which is kept as history. The research questions are answered:

- *Write speed.* Drain speed follows the bytes in flight, and small files need
  draining during writes. Eager 2 MiB × 8 meets both #15 medians in a same-window
  comparison (+5–7% for the 1 GiB file, about 2× for small files) and is 7–9%
  behind on 8 GiB sustained writes ([plans 159/160](#trim-160)).
- *Reads during drain.* PrimoCache does not protect them ([plan 161](#read-161)).
  Eager 2 MiB × 8 alone cut a concurrent disk reader from about 1,100 to about
  300 IOPS. With read-aware limit 2, the reader keeps the default's level
  (1,031–1,141 IOPS, p99 10–11 ms). The #15 workloads keep their full speed,
  because the limit acts only while disk reads happen ([plans 162/163](#prio-162)).
- *TRIM.* An asynchronous lower TRIM with an index lookup removed the
  remaining 0.5 s that mass-delete TRIMs added to a small-file case.
- *What will not help:* more batching or gap work, lock rewrites, larger batches,
  more drainers, Eager 2 MiB × 4, or read-aware limit 1.

| Step | Work | Done when | Estimate |
|---|---|---|---|
| 1. Done: make the candidate the default | New caches default to Eager, 2048 KiB × 8; the driver enables read-aware 2 and TRIM mode 3 at cache creation; the lab switches stay for A/B tests. Saved profiles keep their drain settings. (`ce21c5a`; driver fix `87de299`.) | Done: host tests and CI pass; under Driver Verifier `trim-cache` 12/12, the focused campaign 98/98 and a 12,291-hash integrity run on native 0.4.776.1 ([results](#final-164)) | Took about 4 h of VM time, mostly the four findings in the results |
| 2. Final qualification | One same-window Primo check (`compare-drain` and `compare-drain-read`, A–B–A), then `release-performance` | A against B: the default at 292.4 / 90.7 MB/s against Primo's 261.0 / 52.5, and the reader at 757 against 170 IOPS. `release-performance`: still running (attempt 15). | About 4 h VM |
| 3. Close #15 | Publish the final numbers in the comparison, findings and issue | Owner agrees | — |

After #15, as separate issues, none blocking:
- *Uncached Q1 read miss path.* Alone, it ran 20–35% slower than Primo's.
- *#13 power-loss safety* and *#16 Strict stalls.*

All evidence so far comes from one Ceph-backed VM. Hard disks and SATA SSDs were
not measured; on them eight 2 MiB drain writes may cost reads more.

<a id="drain-decision-153"></a>

**Plan to match or exceed Primo (updated after the October 17 batching test).**
The aligned-gather experiment is complete and rejected. The current gather already
produced much larger small-file backing writes in this session; explain that
change and refresh the provider controls before choosing another kernel policy.
Parity means matching a contemporaneous Primo run, not beating its older score.

*Superseded by the [plan to finish #15](#finish-15).* *Status after plans 159/160:* the TRIM fix and step 3's foreground-read
measurement are complete, and a same-window comparison shows Eager 2 MiB × 8
meeting both #15 medians ([results](#trim-160)). Next comes a read-aware
outstanding-write limit and an asynchronous lower TRIM. Step 5 (qualification)
follows on the final candidate.

*Status after plan 158:* step 2 is complete ([results](#matched-158)). The next
work is a TRIM path that waits only for overlapping in-flight writes. Then step
3's foreground-read measurement, which failed its strict gates three times in
plan 158. Step 5 then qualifies Eager 2 MiB × 8 rather than a new batching
policy. Step 4 is not justified by the plan-158 evidence.

| Step / status | Work and reason | Completion criterion | Expected test time, excluding engineering, builds, provider swaps and boot settling |
|---|---|---|---|
| 1. Complete: test small-file batching | Same native build, current versus aligned 2 MiB gather, Fast 2 GiB Idle, 2 MiB × 4, flags 15; three alternating pairs per shape. Added gap/backward attribution. | All 12 performance cases and 24,582 bypassed hashes passed. Aligned medians lost 3.5% large / 16.3% small and failed the gain/control gates. Policy removed; diagnostics retained. Separate Verifier run also passed all 12 cases / 24,582 hashes. | Actual worker time: about 2 min performance, 2.5 min correctness; setup took longer |
| 2. Next: establish current controls and placement | Run only the two existing shapes on Direct, QueueCache and Primo, each alone with three repetitions. Use the same recoverable VM layout and disabled services. Capture file extents and dirty-run/gap-distance distributions outside scoring if needed to explain the old ~181 KiB versus current ~1642 KiB small-file requests. If considering defaults, screen 1 MiB × 4 against 2 MiB × 4 on the same build. | Complete provider matrices, stable/bracketed QueueCache controls, all hashes and bypass proof; report every range. State whether parity already holds under these conditions. Do not attribute a cross-session gain to code or to disabled services. | About 20–30 min runner work; optional settings screen 5–15 min; no full competitor refresh |
| 3. Conditional: improve concurrency while protecting reads | If a throughput gap remains despite large backing requests, test one default-off read-aware outstanding-write limit: allow higher concurrency when quiet, reduce it while foreground disk reads wait. Plan 151 supports this responsiveness experiment. | Lower loaded-reader latency, no repeatable 3% loss in declared throughput controls, preserved fairness/error semantics. A code-gain claim needs at least 5%, every relevant pair faster and separated aggregate ranges; otherwise reject or report inconclusive. | About 20–30 min short mixed-read and large/small controls |
| 4. Conditional: one further batching candidate | Only if measured dirty runs still limit representative small files, use gap lengths/extents/available adjacent dirty data to choose one bounded picker or background merge experiment. Clean cached gap filling has low support: 202/202 current forward gaps were absent here. Never write unknown bytes or make fitting Fast admission depend on lower I/O; retain pins, oldest-version ordering and range fences. | Stated hypothesis, default-off candidate, same-build alternating pairs, all byte/telemetry proofs and the same gain/control gates. Reject failed ideas. Lock rewrites, 12/16 threads and 4–16 MiB gathers require new evidence. | 10–30 min per justified candidate; stop after the first discriminating screen |
| 5. Pending: qualify one final candidate | Targeted Verifier, faults, partial sectors, paging, ordering/lifecycle and RAM-first lower-attempt counters; check sustained larger-than-cache writes, default Idle foreground interference and Strict boundaries. Then the maintained focused checks and one final release-performance campaign. | Complete final-build checks, no correctness/read regression or hidden control loss, successful restoration. Record every unexercised path; the short fitting test cannot settle them. | Focused 10–30 min; final release-performance about 2 h |
| 6. Pending: final parity and publication | Repeat the narrow provider comparison if candidate/environment changes require it; publish build identities, all medians/ranges and remaining gaps in the existing comparison, findings and issue #15. Restore VM/apps and remove owned data/snapshot after verified export. | QueueCache median ≥ contemporaneous Primo on both shapes, stable controls, complete hashes/telemetry, and step 5 passed before promotion. Only then consider defaults/closing #15. Filesystem power-loss safety (#13) remains separate. | Narrow provider test about 20–30 min if needed; documentation/cleanup otherwise |

These are short decision cycles followed by one final qualification, not a fixed
multi-day test campaign. Engineering effort and the number of useful candidates
remain uncertain; this experiment provides no basis to promise a parity date.

<a id="small-file-gather-156"></a>

**Completed aligned-gather experiment (plans 155/156, October 17).** Current
selection versus a runtime default-off policy that constrained gathering to the
oldest anchor's aligned 2 MiB region. It did not bridge gaps, add lower reads,
change ownership/fences or change admission. Both arms used Fast 2 GiB Idle,
2048 KiB × 4, flags 15 and timing off, with three paired repetitions per shape
and reversed order in the second repetition. Existing unique SplitMix payload,
new-file workloads and write-plus-explicit-drain endpoint are unchanged.

| Shape | Current gather MB/s, median (range) | Aligned gather MB/s, median (range) | Median change | Mean backing write KiB, current / aligned |
|---|---:|---:|---:|---:|
| One new 1 GiB file | 227.95 (227.65–261.66) | 219.98 (214.26–257.45) | −3.5% | 1964.4 / 1942.7 |
| 4,096 new 64 KiB files | 111.85 (110.17–113.50) | 93.58 (92.16–110.98) | −16.3% | 1641.5 / 990.0 |

These are decimal payload MB/s including creation, writes, metadata flush,
explicit product drain and sampled zero pending/disk queue, not isolated drain
bandwidth. Backing sizes are median per-case byte/operation ratios including
metadata, not physical-media writes. Large-file paired changes were −1.61%,
−3.37%, −6.00%; small-file changes were −16.33%, −18.80%, +0.74%. The candidate
fails the predefined gain gate and repeatedly loses more than 3% in the large
control. **Reject it and retain the original gather.** Plan 158 later saw one
arm range 57.6–116.4 MB/s for small files within an hour; the rejection rests on
the absent gain, not on the size of the loss. Ranges overlap and
application times vary too; the median loss alone does not isolate every cause.
The retired suite is no longer selectable from plan 157; its evidence models
remain readable for historical reports/recovery. No default was changed.

The small-file current/aligned totals were 629/934 batches. Forward gaps were
202/484: all 202 current gaps were absent from cache, and aligned had 483 absent
plus one clean-full gap. Current backward stops were 263 gaps / 366 in-flight;
aligned had 483 gaps / 451 aligned boundaries. No partial/filling/fence backward
stops advanced. Each case reconciles forward reason, backward reason and size
bucket to its batch count, and gap classifications to forward gaps. Aggregate
software batches averaged 1448.8/976.0 KiB (distinct from the table's median
backing-request statistic). Classification does not measure gap distance,
physical allocation, or how much useful adjacent dirty data remains.

The unchanged current gather's ~1642 KiB small-file backing requests are much
larger than the earlier ~181 KiB session, at the same 2 MiB × 4 setting. Current
227.95/111.85 MB/s must **not** be compared with older Primo 109.68/54.47 to claim
parity or a driver gain. No fresh Direct/Primo ran here. Backend/NTFS placement,
request formation and session variation remain unresolved. This changes the
next decision: establish fresh controls before a riskier batching implementation.

**Verification and evidence.** Native 0.4.761.1, source `a2249b1`, came from
[the passing native CI](https://github.com/devedse/QueueCache/actions/runs/39057350363).
Actual loaded filter SHA-256 was
`3037482DB14CE0CAEA5E5222BF8179048E7CF7C283631DBC4D70AF21C1541C3F`;
RAM disk was `6330959F9BC5A36122F7477963BB0833EC84A9EEFC808B64869529EB6229D1C2`.
The complete Verifier run used plan 155 / controller 761; performance retained
those exact native binaries, with the managed-only plan-156 controller from
`e2aa5c8`, [runner CI](https://github.com/devedse/QueueCache/actions/runs/39059367859),
package 0.4.762.1. That package's native binaries were not substituted.
Controller SHA-256 was
`FB66CE0F96C62F40BBD1B55E0A867D62C3102550B207AD58E654F82D7112C859`.

| Evidence | Complete run ID, prefixed QueueCache-Verify- | Outcome |
|---|---|---|
| Correctness, actual Verifier 0x209bb on both drivers | `20261017-183856-b25c1cd7215449b99ba081fc2cb6e3f0` | 12 cases / 24,582 bypassed hashes, about 153 s worker time; not performance scores |
| Performance, Verifier off, 15.6 min boot settling | `20261017-190028-238f1f85f3bd449294f714ed3be8e210` | 12 cases / 24,582 hashes and post-oracle bypass proofs; about 120 s worker time |

FINISHED/status/summary/results/run log and the exact raw scores, counters,
policy states, telemetry coverage and oracles were audited. Every performance
hash oracle also proved unchanged hit/miss/accepted-byte counters across the
same paused identity/profile; policy restoration completed. Five performance
cases acquired 8–72 KiB dirty after the scored endpoint before untimed pause;
its source remains unattributed. Disable drained it. Hashes validate the later
paused state, not persistence at the exact scored instant or power-loss safety.
Defender/Search stayed disabled, indexers/launchers were closed and priority was
normal. Capacity/near-full guards passed; existing Ceph availability/scrub
warnings remain a storage uncertainty.

**Mistakes and fixes.** The first Windows CI failed a stale Windows-only plan
version assertion; fixed before deployment. An early correctness attempt
`20261017-182743-ce6bd0b60a30461cb0c12e0fdd29cfec` began before delayed broker
startup finished. Saved-profile restoration replaced the temporary 2 MiB profile
with 1 MiB and re-enabled the cache during the fourth oracle; strict restoration
correctly refused the changed settings. The entire matrix is excluded. Raw
state/service logs were preserved, owned workers confirmed stopped, original
test settings explicitly restored, and maintained verify-recover completed in
`20261017-183508-51b723075a9644a68730b741b334ccfd`. A fresh complete correctness
matrix followed. The failed run remains failed. No earlier settled performance
session is thereby invalidated.

The maintained runner now refuses an installed startup broker that is not
Running, and requires post-oracle bypass proof for QueueCache and Primo. Plan
157 additionally rejects omitted observed snapshot counters instead of accepting
DTO zero defaults, and retires the failed policy/suite. A private PowerShell
wrapper also miscounted the successful correctness JSON array; raw maintained
results showed all 12 passed, the wrapper was fixed and no successful matrix was
rerun. Host contracts cover missing evidence, startup rejection, false bypass
success and independent failure restoration. The new Primo post-oracle path
still needs a real provider run. The final retirement/parser edits require CI;
their driver behavior was not separately VM-deployed. Broader Strict, sustained,
Idle foreground, faults and power-loss qualification remain open.

All 785 final evidence files were exported and guest SHA-256 manifests checked
before rollback; archive SHA-256
`33A2D5F26D77A6228B5979EA9C6137894EA92CF9B7AB7996E57C242573ADEF90`.
Raw evidence stays private. Final restoration passed: original 0.4.687.1 loaded
module hashes, saved profiles, Fast 2 GiB Idle 1024 KiB × 4 runtime, flags 15,
timing/Verifier off, original applications and Everything service. The broker
was confirmed Running before restoring the profile. Defender/Search remain
disabled, Primo is absent. Restoration records were pulled and SHA-256 checked;
all ten remaining owned helper/evidence files were preserved and hash-verified
before scoped deletion. Only system folders and the reusable QueueCache-Lab
remain at Q:'s root. The temporary snapshot is deleted, zero snapshots remain,
and the final capacity/near-full guard passed with 313.6 GB free. Existing Ceph
availability/scrub warnings remain; no unrelated files or lab disks were removed.

**Completed drain decision screen (plans 152–154, October 16/17).** Native
0.4.756.1 (`d7895b5`), QueueCache runner `d58b2e1` / plan 153, Direct and Primo
runner `48f8966` / plan 154. Fast 2 GiB Idle, flags 15, timing/Verifier off,
normal priority, Defender/Search disabled. Four complete QueueCache six-case
runs used bracket controls; Direct and Primo each completed a fresh six-case
run after snapshot rollback. All used the same unique payload, workload and
scored endpoint, three repetitions per shape and the same temporary snapshot.
All **36 cases and 73,746 unbuffered file hashes passed**. Plan 154 changes only
Direct's unscored preparation wait; QueueCache/Primo preparation and every
scored workload are unchanged from 153. This is a completed decision screen,
not a default qualification; grouped arms and control drift limit attribution.
The six successful comparison workers took **about 23 minutes combined**, including
preparation, file checks and case restoration. CI/build/deployment, excluded
attempts, provider swaps and required post-boot settling are additional time.

| Runtime arm | New 1 GiB + drain MB/s, median (range) | New small files + drain MB/s, median (range) | Mean backing write KiB, large / small |
|---|---:|---:|---:|
| Starting control, 1024 KiB × 4 | 57.81 (49.43–61.94) | 20.05 (18.59–24.66) | 1009.6 / 182.0 |
| 2048 KiB × 4 | 74.66 (65.18–101.26) | 27.07 (25.42–65.67) | 2002.3 / 180.9 |
| 2048 KiB × 8 | 84.24 (73.98–127.51) | 36.14 (34.15–49.95) | 2009.4 / 190.2 |
| Ending control, 1024 KiB × 4 | 66.54 (59.86–73.39) | 42.54 (29.28–51.28) | 1015.4 / 207.0 |
| Direct disk, QueueCache absent | 47.71 (43.60–54.87) | 1.28 (1.25–1.48) | 986.9 / 5.0 |
| PrimoCache 4.4.1, shared 2 GiB, Native defer 10 s | 109.68 (100.31–117.36) | 54.47 (45.71–67.54) | 1939.0 / 1339.3 |

The ending control improved 15.1% for large files and 112.2% for small files.
The combined arm's large-file range narrowly exceeds both control ranges; the
small-file ranges overlap and do not establish a stable improvement. These
results preserve the earlier plan-148 session as historical evidence and do not
complete the default-qualification gates. The 2 MiB × 8 median still trails
fresh Primo by **23.2% large / 33.7% small**; reaching its medians would require
about **30.2% / 50.7%** further throughput. Its small-file median is also 15.0%
below the ending control, with overlapping ranges. Picking its fastest
127.51 MB/s large-file repetition would not establish parity.
*Correction (plan 158):* those QueueCache arms and the Primo run were about two
hours apart on a disk whose speed changed up to threefold within hours, so this
gap does not isolate the products ([plan 158](#matched-158)).

Primo's earlier 78.96 / 57.62 MB/s becomes 109.68 / 54.47 in this session
(+38.9% / −5.5%); Direct changes 48.41 / 1.25 to 47.71 / 1.28 (−1.4% / +1.8%,
using unrounded scores). Defender/Search were already disabled in both sessions.
These differences do not isolate a product improvement or the effect of disabling
services. Existing Ceph availability/scrub warnings remain; capacity and near-full
guards passed. These are virtual-disk, completed-write measurements, not a
physical-media speed limit or a power-loss test.

**Batch attribution:** V4 snapshots count one forward-gather termination reason and size bucket
per batch; their totals reconcile with the batch count in every case. At 2 MiB,
94.3% / 96.1% of small-file batches (four/eight drainers) ended with no adjacent
dirty version; mean software batch size was 209.6 / 217.5 KiB. Partial sectors,
in-flight overlaps, filling and range fences did not advance the other forward
termination counters; backward-walk stops were not attributed. Large-file batches averaged about 2 MiB and mostly reached their size limit.
This points toward investigating gaps before raising the size ceiling again.
*Plan 158:* small-file drain phases later ran close to the large file's rate at
the same bytes in flight; the small-file total is limited by writing, then
draining, in sequence ([finding 3](#matched-158)).
`FindOldestSlot` excludes clean entries: a gap does not establish file fragmentation
or prove that the neighboring bytes are absent from RAM. A bounded first candidate
is gathering within aligned 2 MiB address regions, preserving the oldest anchor,
to avoid leaving short dirty tails; this is a source-derived hypothesis, not a
measured gain, and cannot bridge true gaps. Classify clean cached versus uncached
gaps before considering gap filling; never write unknown sectors or bypass
version/fence ownership.

For example, the four-drainer small-file case at 65.67 MB/s issued 402 backing
writes averaging 755.8 KiB; its 25.42 MB/s case issued 2,269 averaging 134.1 KiB.
Both admitted application writes in about 1.2 s. Request shape therefore varies
substantially even at fixed settings; storage latency alone is not established
as the cause. In all twelve small-file cases, samples at/before application end
recorded no backing-write increase. The 100 ms sampling cannot resolve the last
fraction of that interval; longer admission delay is not the first candidate.

Nineteen of the 24 QueueCache cases recorded 8–68 KiB newly dirty between the
scored zero-pending observation and untimed Disable. Its source remains
unattributed; Disable drained it, then every paused-state observation was clean
and every file hash passed. The oracle validates that later paused state,
without proving file persistence at the exact earlier scored instant.

**Decision at the end of plans 153/154:** the small backing requests justified
classifying gaps and trying one bounded aligned gather. That follow-up is now
[complete and rejected](#small-file-gather-156). Use the updated plan above;
current controls and request sizes changed enough to require a fresh provider
comparison before another batching policy. Defaults remain 1024 KiB × 4;
issues #13/#15/#16 remain open.

**Allocation and preparation:** staging is now bounded at small budgets, and
remaining payload space is checked before unsigned subtraction. Normal 2 GiB
staging and selection are unchanged. All fourteen 1/15/16/17/63/64/65 MiB Fast/Strict
byte-oracle checks and the maximum-settings quick file-integrity smoke passed
under actual Driver Verifier. This resolves the source-reviewed allocation blocker;
broader Strict, sustained and lifecycle qualification is still pending.

The initial plan-152 combined run stopped before its third workload after a
new 4 KiB pending write appeared during preparation (no in-flight bytes, errors
or fault; source unattributed). Restoration succeeded; its two completed samples
are excluded. Plan 153 records bounded unscored preparation drainage, retaining
immediate rejection of faults/missing evidence and the scored ready handshake.
All four arms were collected afresh; no 152/153 matrix was assembled. The new
complete screen needed one preparation attempt per case. The earlier two complete
152 arms remain historical evidence, outside this comparison.

The first Direct plan-153 run stopped before its sixth workload because its
single preparation observation found the backing disk busy after cleanup;
restoration succeeded. All five samples are excluded. Plan 154 adds a bounded,
recorded idle wait only to Direct preparation, preserving the zero-queue start
condition, payload, scored endpoint and complete-matrix checks. Its invalid-counter
and timeout paths are host tested. The fresh complete Direct 154 run recorded one
busy observation followed by idle, exercising the new preparation wait. Direct
and Primo 154 both passed; no incomplete repetitions are combined. QueueCache
153 measurements are unaffected. Both setup failures and their excluded samples
remain in the private evidence; neither changed scored timing or converted
missing/invalid evidence into success.

Exact complete runs (each prefixed `QueueCache-Verify-`):

| Mode | Run suffix |
|---|---|
| Starting control | `20261016-220507-3ebc4a18ad3441deab333c85154695c4` |
| 2 MiB × 4 | `20261016-221007-e5e08b8a0d7c4e23a03f4f1d69dbbf65` |
| 2 MiB × 8 | `20261016-221358-6797e3fb17954ed884ccb80edb8f7f73` |
| Ending control | `20261016-221756-a7c32e274d27444a8674ec0b0b951ab3` |
| Direct | `20261016-231227-cce2dba4f55f495cbadfded7bdcd1c4e` |
| Primo | `20261017-000251-c6fcca8b303b4436ab65d2eca47626f3` |

Loaded native SHA-256 hashes matched the [0.4.756.1 CI artifact](https://github.com/devedse/QueueCache/actions/runs/38968658858):
filter `5EBB7F8D3AED873DC592B5C370A0FCC943B77F1F1222F6BC651F3449A68E5DA7`,
RAM disk `B3F50B4883BA3CE9C298DB95DB8957860F25A28F81F0A977BEA3D54A1D9F17C6`.
The [final runner CI](https://github.com/devedse/QueueCache/actions/runs/38979534587)
passed. Host Release management contracts and Developer builds passed; Linux
management tests emitted platform analyzer warnings. The three exported archives
containing the complete measurements have 1,767 files, all checked against guest
SHA-256 manifests before restoration. The excluded Direct archive was checked
separately. Raw evidence stays private.

Restoration is verified: original 0.4.687.1 loaded module hashes, saved profiles,
Fast 2 GiB Idle 1024 KiB × 4 runtime, flags 15, timing/Verifier off, original
apps and Everything service. Defender/Search remain disabled; Primo is absent.
The first post-boot profile application was refused by the available-memory
guard. After inspecting the clean driver and startup memory, the same captured
profile applied successfully; the guard was not bypassed. The temporary snapshot
and owned C:/Q: test/evidence files are removed; reusable lab fixtures remain.
Zero snapshots remain, C:/Q: have 62.4/199.8 GB free, and the final capacity/
near-full guard passed with 313.9 GB in the backing pool. Existing availability/
scrub warnings remain. All benchmark processes have finished.

**Short lock/drain measurement (plan 151, 2026-10-16).** The complete
`drain-lock-diagnostic` run measured 48 six-second windows: cached sequential
1 MiB Q8 and uncached random 4 KiB Q1 reads, alone/with a continuous sequential
writer, 2 MiB batches at four/eight drainers, timing off/on and three repetitions
with reversed order. The runner took **18m56s**, including preparation,
inter-case flushing/warming and restoration; scored reads total **4m48s**.
Native build **0.4.749.1** is unchanged from the earlier experiments; the tested
managed runner source is `0f44814`. Actual loaded hashes matched that CI artifact.
Verifier was off, post-boot settling exceeded 15 minutes, processes had normal
priority, Defender/Search were disabled and background apps/service were stopped.

The writer uses a separate 4 GiB file, 1 MiB Q8 writes and Eager draining with
write retention off. Cached reads use a warmed 512 MiB file with Automatic
allocation; uncached reads use a separate 1 GiB file with Fixed 100% writes
(zero read-cache admission). Pure hit/miss counters and at least 75% pending
observations during each loaded reader envelope were required. These are two
separate read workloads, not a controlled comparison of cache-hit versus miss
cost or a qualification of the Idle product defaults.

Medians of three windows per cell. Read scores/p99 are **timing off**; lock
occupancy is from separate **timing-on** diagnostics. Arrows mean alone → with
the writer. GiB/s is binary throughput; IOPS is completed reads per second.

| Reader | Drainers | Read throughput, alone → loaded | Read p99 ms, alone → loaded | Global lock held %, alone → loaded |
|---|---:|---:|---:|---:|
| Cached 1 MiB Q8 | 4 | 35.47 → 30.84 GiB/s | 0.436 → 0.665 | 30.27 → 34.80 |
| Cached 1 MiB Q8 | 8 | 34.79 → 30.04 GiB/s | 0.456 → 0.756 | 30.14 → 33.59 |
| Uncached random 4 KiB Q1 | 4 | 2,275 → 1,057 IOPS | 0.803 → 13.684 | 0.64 → 1.12 |
| Uncached random 4 KiB Q1 | 8 | 3,437 → 465 IOPS | 0.598 → 24.473 | 0.78 → 0.88 |

**Decision:** prioritize storage scheduling/concurrency over a reader–writer
lock rewrite for the drain/read problem. During uncached reads, eight drainers
raised median drainage **240.5 → 322.0 MiB/s (+33.9%)**, but reduced reader IOPS
**56.0%** and raised p99 **78.8%** against four. Every loaded eight-drainer read
IOPS/p99 range is worse than four's. *Plan 158:* the disk-read-alone controls
here ranged 1,869–3,449 IOPS, so the magnitude is uncertain; two plan-158
repeats did not complete. The lock was held only about 1% of the
diagnostic envelope and mean global acquisition waits were 1.09 / 3.69 µs;
these global means are not reader-specific latency. Low VM CPU use (about
5–6% in timing-off loaded disk-read windows) and the lower-path timings support
storage competition as the main explanation, without identifying a physical
device or Ceph cause.

For RAM hits, concurrent draining reduced throughput **13.1% / 13.7%** versus
the matching alone controls. Eight drainers drained **251.0 → 332.2 MiB/s**
but served RAM reads 2.6% slower than four; ranges were separated. VM CPU use
was approximately **97%** even for reads alone. Lock occupancy around 30–35%
and global waits of 6.5 µs alone / 11–13 µs loaded show scope for less metadata
work, but do not isolate lock blocking from CPU scheduling or memory-copy costs.
Do not claim a reader–writer lock's gain from this screen.

Across loaded timing-on windows, lower I/O accounted for approximately
**98.9–99.9% of the summed measured drainer selection/copy/retirement/lower
phases**. Concurrent drainer waits sum across threads and exceed wall time;
they are not an application-latency breakdown or an exhaustive account of
thread time. A candidate follow-up is to reduce outstanding drain writes while
foreground disk reads are waiting, retaining higher concurrency when they are
absent. Shorter read-hit bookkeeping is a secondary candidate; sharding/lock-free
lookup is not justified as the first change. No locking or default change was made.

Storage controls varied: four-drainer disk-read-alone IOPS ranged
**1,869–3,449**, and eight-drainer loaded timing-on drainage ranged
**69.8–331.8 MiB/s**. Ceph retained availability and BlueStore slow-operation
warnings; capacity/near-full guards passed. The timing-on disk-read results
cannot isolate instrumentation overhead from that variation. Keep this screen
separate from the matched competitor comparison; no Primo run, Strict test,
sustained qualification or power-cut test was added.

The first plan-150 attempt stopped at case 9 because the fixture incorrectly
treated disabled promotion as disabled read-miss admission. The strict purity
check rejected its cached hits; restoration succeeded with no driver errors.
It remains incomplete and is excluded entirely. Plan 151 fixes the cold arm
with a zero read partition and adds a host contract for that distinction.

Exact completed run: `QueueCache-Verify-20261016-201557-410f1de47e934ba48036c8e4d2a51593`.
Excluded setup attempt: `QueueCache-Verify-20261016-200734-551c990292b04121a9a4a2753ea41294`.
All 11,749 exported evidence and runner files were verified against guest-side
SHA-256 manifests before rollback.
The original 0.4.687.1 native hashes, Fast 2 GiB 1024 KiB × 4 runtime/saved
profile and apps/service were restored and checked. Defender/Search remain
disabled. The temporary snapshot and new VM test/evidence files were removed;
zero snapshots remain, C:/Q: have 62.8/199.8 GB free, and the final capacity/
near-full guard passed with 315.4 GB available in the backing pool.

## Competitor comparison: three cache weaknesses (plan 121, 2026-10-13)

**Question.** How does QueueCache compare with other RAM disks and RAM caches on the
same VM, and what does the comparison show about QueueCache itself? Full results:
[COMPARISON.md](COMPARISON.md) (generated from the `compare-*` suites, plan 121;
products installed one at a time on restored snapshots, measured by tools that use
no QueueCache feature).

**RAM disk.** QueueCache leads single-program large reads (SEQ1M Q8T1 45,245 MB/s;
next SoftPerfect hard-disk emulation 35,386) and ties at Q1T1 with SoftPerfect RAM
File System (about 25,100). Small requests sit in the leading block-device group
(RND4K Q1T1 1,611 / 1,234; OSFMount logical 1,744 / 1,390). SoftPerfect RAM File
System is a file system, not a disk, and wins small requests and the file tree by
a wide margin. No RAM-disk decision changes.

**Cache (2 GiB on Q:, against the bare disk, PrimoCache 4.4.1 L1 2 GiB).** Cached
data is fastest with QueueCache (hot 1M Q8 39,487 / 21,214 MB/s Fast; PrimoCache
17,172 / 19,036). Three measured weaknesses:

| Finding | Evidence | Cause | Candidate |
|---|---|---|---|
| Read misses do not overlap | 1 GiB first read 217 MB/s Fast / 187 Strict, bare disk 560, PrimoCache 512 / 409; cold RND4K Q32 11 MB/s against 65 bare and 72-80 PrimoCache. QueueCache equals the bare disk at Q1 (230 MB/s, 2,949 IOPS). | The request worker reads each miss synchronously (`StagedRead`/`OriginalIo` on the worker), so one miss is in flight per volume. | Several misses in flight (asynchronous lower reads with ordered completion), keeping the driver-owned staging buffer and the pins. |
| Drain uses small writes | After a 1 GiB burst: 10 s at 105 MB/s (PrimoCache 7 s at 149). 8 GiB sustained write 178 MB/s against 242 bare and 486 PrimoCache. During it the disk received 256 KiB writes at 129 MB/s; PrimoCache's averaged about 1.4 MiB at 366 MB/s, DiskSpd's 1 MiB Q8 215 MB/s. | Default 256 KiB batches with drain parallelism 2. | Measure `--batch-kib 1024` and `--drain-parallelism 4` (existing settings) with `compare-cache`; change the defaults only for a repeated gain. |
| Strict stalls all writes behind whole-cache drains | Rerun: 6 of 12 hot write windows completed nothing in 10 s; creating 40,000 files 213 s (bare 32), 8 GiB write 53 MB/s with a 0 MB/s slowest second (bare 242), write-through 1M Q8 52 MB/s (bare 251), flush after 512 MiB 12.6 s. Probe on the same VM (cache flushed first): RND4K Q32 then Q1 writes completed 0 operations in their windows and needed 106 s and 168 s to finish; each window received one write-through write and two flushes from NTFS (`StrictWriteBarriers` +1, `ApplicationBarriers` +2). Random dirty data drains at about 10 MB/s (bare RND4K Q32 write 10.7 MB/s). | Strict admits writes to RAM at RAM speed (about 1 GB during a 2 s warm-up), then honours each write-through write or flush with `QcCacheBarrier`: wait for `DirtyBytes == 0`, lower flush, while the ordered worker holds every other write. | Bound Strict's dirty data by what the disk drains in a short time, so a flush finishes in bounded time, and complete a write-through write after older overlapping versions without draining unrelated dirty data. |

**Investigation (2026-10-13, issues #14 and #15).**

- *Misses (#14):* the caller path serves hits only (`Read(..., hitOnly)`); misses queue to the ordered worker, which
  waits for each lower read (`StagedRead`/`OriginalIo` -> `CallLowerAndWait`). During that wait the service lane takes
  hits and one paging miss, and moves up to 8 queued misses back to the queue head; each worker step rescans from the
  head. In every comparison window with 32 queued misses the slowest read waited 41-59 s of 60 (bare disk and PrimoCache
  under 0.5 s; median 0.7 ms, average 7-19 ms). Proposed: stop the head reinsertion, then several misses in flight
  (offload threads, at most 3, or asynchronous misses with Filling ranges).
- *Drain (#15):* each drainer copies one batch (at most 1 MiB, `MaxBatchBytes`) and waits for its lower write, so
  writes in flight equal the parallelism. `compare-cache` with only batch/parallelism changed (one repetition each,
  default first and last as a drift check): the storage became about 2.3x faster between runs 2 and 3, so only
  same-period runs compare. Same period: 1024 KiB x 4 against 256 KiB x 2 drained a 1 GiB burst at 126 against
  87 MB/s and sustained 8 GiB at 267 against 182 MB/s (slowest second 202 against 131); hot, mixed and tree results
  unchanged. Slow period: 1024 x 2 against 256 x 2 drained the burst equally (37/39 MB/s), sustained 83 against
  57 MB/s. Not yet attributable to batch size or parallelism (256 x 4 not measured; T050 found 256 x 4 no faster).
  Next: an alternated, repeated comparison (drain-decision with a batch option) before any default change.
- *Drain decision (plan 125, `drain-decision`, 42 cases, run
  `QueueCache-Verify-20261013-131647-d5f42aaebc094cf38529b3a18514968a`, installed 0.4.604.1, Verifier off; medians
  of three, batch and parallelism order reversed every other repetition).* Seconds to drain a 256 MiB backlog during
  cold random reads: 256 KiB x 1/2/4 = 3.88/2.67/3.07, 1024 KiB x 1/2/4 = 3.08/3.68/1.86. During fitting random
  writes (the flush also drains writes admitted meanwhile): 256 x 1/2/4 = 32.6/18.2/17.7 s at 14,202/14,413/13,202
  foreground IOPS; 1024 x 1/2/4 = 18.8/15.3/8.4 s at 37,342/26,412/50,859 IOPS. 1024 x 4 beat 256 x 2 in every
  write pairing (11.4/8.4/6.3 against 18.2/19.3/8.8 s) and in two of three read pairings (third equal); median p99
  was unchanged. Foreground write IOPS improved in two pairings but fell 3.7% in the first, and the flush-time
  ranges overlapped. This exploratory decision does not meet the separated-range acceptance bar by itself;
  final-build qualification remains required. 256 x 4 alone was not faster, as in T050.
  **Branch defaults changed to 1024 KiB x 4** (plan 125).

**Fix branch qualification history (plans 126–132).** The initial concurrent-miss
candidate built successfully but needed a correctness fix before acceptance. Review found that using the
old worker path without pin scratch allowed one overlapping miss to unpin a slot
filled by another read. Every read now saves its original pin identities, and the
maintained partial-read suite checks simultaneous cold/partial readers and clean
entry eviction. The first cache-concurrency attempt on 0.4.673.1 failed its
RAM-only guard (36 lower reads, 27 KiB of misses); restoration succeeded. The tray
was left open, so the failure needs a clean repeat and cannot be counted as a
passing correctness or performance result. The completed same-build comparisons
and concurrency matrix follow. Final release qualification on 0.4.687.1 is
complete; the file-tree and latency-tail questions are resolved below, and the
foreground-interference and Strict-impact questions remain unmeasured.

**Read-miss speed screen (plan 127).** Signed 0.4.677.1 completed all 24 windows
of `cache-read-misses`, three alternated repetitions of flags 7/15, timing off,
20-second scores, 2 GiB cache, a 4 GiB cold file and 1 GiB resident controls
(`QueueCache-Verify-20261013-182938-2fbb3355818244b18228ff848afe88c2`). Both modes
use the branch's 16-thread pool and corrected queue order; flag 8 isolates ordinary
miss offload. This is a same-build comparison, not a comparison against the entire
older driver. The screen returned ACCEPT_SCREEN and restoration succeeded.

| Read shape | Serial misses, median MiB/s (range) | Offloaded misses, median MiB/s (range) | Median change | Median p99, serial / offloaded (ms) |
|---|---:|---:|---:|---:|
| Cold random 4K Q32 | 5.55 (4.44–8.53) | 33.27 (28.22–103.18) | +499.9% | 100.664 / 34.456 |
| Cold sequential 1M Q8 | 177.85 (167.28–337.55) | 612.10 (597.15–1009.70) | +244.2% | 160.428 / 121.793 |
| Resident random 4K Q32 | 1653.06 (1646.02–1661.38) | 1636.53 (1621.46–1642.49) | −1.0% | 0.104 / 0.106 |
| Resident sequential 1M Q8 | 37053.70 (36948.05–37118.54) | 36604.50 (35928.65–37096.05) | −1.2% | 0.430 / 0.442 |

Every cold pair improved, and the candidate ranges were above the serial ranges.
Every cold p99 improved. The largest cold-read samples were higher in some
candidate windows: 376.696 ms versus 300.564 ms for random reads, 731.899 ms
versus 602.090 ms for sequential reads. This does not qualify worst-read latency
as improved. All resident controls recorded zero misses and zero lower reads;
one sequential control pair lost 3.04%, while the other two lost 0.93% and 0.06%,
so there was no repeated 3% control loss. The loaded filter hash was
`B7AA752B6F21E00FF8A2027B9DBA679E14DFF6D5A6186CE9BD81820BAB130F73`, provider
`0FDD5279B96D8B20D416FD5C509C7C90D66A3D5299A1F98EFF5AC88E2DCA651C`, DiskSpd
`7281BF6DA6C03797016EDDF2E8AAEC4C644AE893D403D57A030B7E2E14B61079`.
The final release regression remains required.

**Concurrency repeat, plan 128 (incomplete).** On signed 0.4.679.1, all four
first-repetition resident-read shapes passed, including four readers at Q2 that
failed in the initial 0.4.673.1 attempt. The first RAM-only write window then
recorded three lower reads / 24 KiB of misses. All 141,410,959,360 scored write
bytes were admitted; lower writes, lower flushes and drained bytes remained zero,
and identity/health stayed stable. This is consistent with writable-file
metadata activity, rather than lower I/O for payload admission, but the strict
window correctly failed. Plan 129 primes the writable DiskSpd profile before
the quiet wait and measurement boundary. The zero-lower-I/O guard is unchanged.
The incomplete 6/37 run (`20261013-230517-9cd6a418dc51416c9ba8611511f3ba91`)
restored successfully and supplies no complete matrix or performance aggregate.
The focused plan-129 repeat completed 2/2 selected cases and restored
(`20261013-233136-2fe8cbd5ec9244a09b0c8cf685caf66d`, managed runner `d0372d3`
against the same signed 0.4.679.1 native driver). The write score completed
144,997,089,280 bytes, all admitted; lower read/write/flush attempts, misses and
drained-byte deltas were all zero. This qualifies that fixture correction for the
affected window, not the complete 37-case concurrency matrix. The completed full
repeat on the CI package is recorded below.

The full 0.4.681.1 repeat completed the four read shapes and first RAM-only write
window, whose lower read/write/flush attempts, misses and drained-byte deltas
were zero. The next background-drain write score stopped on a 9.84-second
telemetry gap; the two-second coverage guard remained intact. The 7/37 run
(`20261013-235750-a2aa7d221f8740948a568a844368d11f`) restored the original
256 KiB × 2 profile cleanly, with zero errors. Its gap prompted investigation;
no aggregate or complete concurrency qualification is inferred from it.
The unchanged focused background-drain repeat completed 2/2 selected cases
(`20261014-001223-b6ada1ceaea74be0ba911244aa7b1392`), restored cleanly, and
recorded 51 score-window samples with a maximum gap of 0.313 seconds. The
9.84-second gap was not reproduced; its individual cause remains unproven.
That repeat completed the first 12 read/write windows, then stopped during the
second repetition on a 3.04-second telemetry gap in a resident-read window
(`20261014-001931-d4c4bb8d868c4d6187ba49c264af77f1`, 15/37, restored). In both
failed runs the UTC and monotonic gaps agreed, excluding a clock adjustment.
Plan 130 records observer-stage timings to locate the actual delay; coverage
requirements remain unchanged. Neither incomplete matrix is aggregated.
The instrumented plan-130 run reached 20/37 cases, then stopped on a
3.367-second gap (`20261014-005352-afae0a6dbf324a669460071287c75c4a`, signed
0.4.681.1 native driver with the standard CLI built from clean `f9f4c89`). The
next sample recorded 3147.796 ms spent serializing/persisting the previous sample
and 214.456 ms awaiting its delay; driver queries took at most 2.163 ms. The gap
began before the scored process, across the readiness boundary. Restoration was
healthy and clean. Plan 131 separates the actual sampling thread from
serialization/persistence through a bounded 256-sample buffer. It still rejects
real coverage gaps and buffer exhaustion, publishes readiness after the first
persisted sample, and waits for all observations to be written. Host contracts
cover blocked persistence, order, overflow and failure ownership; VM validation
of this correction is complete for the concurrency and read-miss matrices and
the drain comparison.

**Final-build concurrency (plan 131).** Signed 0.4.685.1 completed all 37 cases
(`20261014-041653-4cf5d812f35744c1acf35af6647fa7ed`). All 12 resident-read and
12 fitting-write windows required zero lower read/write/flush attempts, misses
and drained bytes and accounted for every scored byte; all passed. The other
12 windows exercised writes with background draining. Every actual telemetry
interval covered its process window; the largest UTC/monotonic gap was
0.215076/0.215075 seconds. Writer persistence reached 247.289 ms while capture
stages took at most 3.892 ms. The original 2 GiB Fast/Idle 256 KiB × 2 profile
was restored with zero errors, dirty or in-flight bytes. Full raw evidence and
XML byte totals were reviewed; no samples from incomplete runs were aggregated.
The final cold-read screen, complete drain comparison and release campaign below
are finished. The refreshed product comparison is recorded below.

**Final-build read-miss screen (plan 131).** Signed 0.4.685.1 completed 24/24
windows with the same budget, file sizes, alternated three repetitions and
20-second scores as the plan-127 screen
(`20261014-044032-7009cf8578ec4b178648b13f18e38a2e`). The result was
ACCEPT_SCREEN. Both arms still use the 16-thread pool and corrected queue order.

| Read shape | Serial misses, median MiB/s (range) | Offloaded misses, median MiB/s (range) | Median change | Median p99, serial / offloaded (ms) |
|---|---:|---:|---:|---:|
| Cold random 4K Q32 | 10.44 (9.72–11.69) | 98.05 (55.03–98.98) | +839.7% | 19.080 / 7.374 |
| Cold sequential 1M Q8 | 340.50 (338.83–342.80) | 1140.58 (1140.08–1141.20) | +235.0% | 49.879 / 28.576 |
| Resident random 4K Q32 | 1621.66 (1587.31–1635.31) | 1618.97 (1610.94–1629.27) | −0.17% | 0.106 / 0.107 |
| Resident sequential 1M Q8 | 36623.09 (36562.55–36952.67) | 36770.55 (36739.15–36800.40) | +0.40% | 0.451 / 0.446 |

Every cold pair improved, candidate throughput ranges were above the serial
ranges, and every cold p99 improved. No resident pair lost 3%; all resident
windows had zero misses and zero lower reads. The largest random-read sample
increased from 46.219 to 568.738 ms; sequential maximum decreased from 60.706 to
41.321 ms. This accepts throughput and p99, not a blanket worst-latency gain.
The largest telemetry gap was 0.258 seconds. Original settings and copy flags
15 were restored cleanly. All raw XML byte totals and telemetry intervals were
reviewed. Loaded filter:
`894509CAB30A6CE4856B4837905A62AD2FC835C808B43199C663897429658891`;
provider: `7ABCE03E9CBB7ABEB0BF816BA427865150D604EDEDCD43484EA40FE1599EB579`;
DiskSpd: `7281BF6DA6C03797016EDDF2E8AAEC4C644AE893D403D57A030B7E2E14B61079`.

**Fixed-operation read-tail comparison (plan 141, 2026-10-14/15).** The earlier
tails (maximum 568.738 ms versus 46.219 ms) came from fixed 20-second windows in
which the faster arm completed 9× more requests, so the two arms did not sample
equal work. `read-tail-decision` runs the same seeded 100,000-operation address
sequence in each arm. The complete 24/24 run
`20261014-230805-92a9f1ac4191423cbd05b571db4595b2` (controller 0.4.716.1 at
commit `f29c784`, loaded 0.4.687.1 filter `FE00891A…`, provider `0B13C2F5…`,
timing off, 2 GiB Fast, 256 KiB × 2, Idle draining) completed with every start
snapshot clean, all 24 latency summaries recomputed independently from the
100,000 raw samples, six durable write oracles verified, and the original copy
flags 15 read back after the run. Medians (range) over three order-alternated
pairs:

| Workload | Flags 7 | Flags 15 |
|---|---:|---:|
| Cold random 4 KiB Q32, IOPS | 1,547 (1,518–1,568) | 10,675 (9,732–11,097) |
| Cold random p99 / p99.9 / max, ms | 62.0 / 191.7 / 271.6 | 24.4 / 60.7 / 123.5 |
| Cold random >50 / >100 / >250 / >500 ms (per repetition) | 2100·1552·1367 / 457·387·297 / 15·0·37 / 0 | 210·161·149 / 1·2·25 / 0 / 0 |
| Resident random, IOPS | 189,025 | 179,393 |
| Resident sequential 1 MiB Q8, IOPS | 31,902 | 32,756 |
| Fitting foreground write, IOPS | 191,012 | 185,158 |

Every cold pair improved throughput (+541%, +617%, +581%), p99.9 (−92 to −148 ms),
maximum (−119, −148 and −230 ms) and every threshold count; no operation in any
arm exceeded 500 ms, and both arms issued the same number of lower reads (95,336
to 95,339). The 568.7 ms maximum was therefore a fixed-window artifact, not a
property of equal work. The resident and write controls had zero misses, zero
lower reads (writes: no throttle waits) and tails of at most 6.7 ms. Their
throughput differences followed run order, not flags: in all three
resident-random pairs the arm that ran first was faster (−7.2%, +5.2%, −8.5% for
flags 15), and the resident-sequential (+2.9%, +3.3%, −3.7%) and write
(+0.8%, −6.5%, +2.5%) pairs have no consistent direction. Resident reads never
reach the miss path that flag 8 controls, and the plan-131 DiskSpd screen showed
no resident loss (−0.17%). The pairs were not rerun in an order-balanced design.

Limits: two earlier incomplete runs on commit `c64b954` collected 15 and 13 cases
before stopping (a stray 32 KiB metadata write in the start snapshot while
Windows Search ran; a 16 KiB write still dirty in the post-flush snapshot); they
are excluded from the decision. Their cold-random samples are directional only:
flags 15 finished 100,000 reads in 2.7–3.9 s (p99.9 3–5 ms) and flags 7 in
40–42 s (p99.9 38–145 ms). The final run was slower in absolute terms (flags 15
9–10 s; flags 7 about 64 s) because the backing disk was slower that hour
(draining 100,000 scattered 4 KiB writes took 251–258 s against 74–137 s
before), so latencies are not comparable across sessions, only within a pair.
The first attempt on `f29c784` stopped INCOMPLETE at 7/24 when the post-write
Flush exceeded the default 180-second preparation deadline; the complete run
used `--preparation-flush-seconds 900`. Plan 141 adds a bounded two-second quiet
wait before each start snapshot; it was never needed in the final run (every
case's first check passed) while the same wait before the durable oracle needed
a second attempt in five write cases. The pool size (16 threads) was not varied:
no result implicates it, so no executor-limit experiment was run.

**Drain completion budget (plan 132).** The first final-build 42-case drain
repeat stopped at 27/42 (`20261014-045632-97825e31882149d89fbb3ecd56f3cbb2`).
The generic ten-second-score plus 120-second process deadline expired while
the parallelism-1 administrative flush was still draining small, scattered
writes. Telemetry showed 589.8 MiB drained in 129.9 seconds, 27,365 lower-write
attempts and zero driver errors, with 8.6 MiB still dirty when its old observer
duration expired. Restoration succeeded. No aggregates from that incomplete
run are used. Plan 132 gives this diagnostic an explicit 300-second completion
allowance and an observer lasting beyond process completion and readiness.
The scored duration, workload, complete 42-case matrix, coverage limit and
persisted-byte checks are unchanged. Host contracts reject the old truncated
interval and real sample gaps. The corrected complete run is recorded below.

**Final-build drain comparison (plan 132).** All 42 cases completed
(`20261014-053536-741edee09d044ee7b751ef2e8162c487`, standard CLI from clean
`4b0e3c6`, signed 0.4.685.1 native drivers with the hashes above). This is one
complete run: three alternated repetitions, a 1 GiB cache, deterministic 256 MiB
backlogs and ten-second foreground scores. Every one of the 36 seeded backlogs
was verified from disk. All raw XML byte totals and actual telemetry intervals
were checked; the largest sample gap was 0.320 seconds. The original 2 GiB
Fast/Idle 256 KiB × 2 profile was restored with zero errors or pending bytes.

| Measurement | 256 KiB × 2, median (range) | 1024 KiB × 4, median (range) |
|---|---:|---:|
| Flush during cold reads, seconds | 6.517 (6.298–7.613) | 3.863 (3.846–3.868) |
| Foreground cold reads, IOPS | 514.57 (476.00–596.81) | 1157.04 (988.31–1268.10) |
| Cold-read p99, ms | 4.810 (4.058–7.053) | 3.808 (3.533–4.860) |
| Flush during fitting writes, seconds | 101.153 (40.294–114.397) | 88.727 (29.099–90.784) |
| Foreground fitting writes, IOPS | 15797.40 (14109.60–38916.48) | 14537.66 (14163.00–37758.40) |

Cold-read flush time improved 40.7%, with every pair improving and separated
ranges. Every foreground cold-read pair and p99 improved too. Fitting-write
flush times improved 12.3%, 27.8% and 20.6% in their pairs, but their ranges
overlap. Foreground write changes were +3.0%, −2.98% and −10.35%; this does not
establish a fitting-write throughput gain. These flushes include additional
writes admitted while draining (593–742 MiB total, not a fixed 256 MiB alone).
Normalized drain throughput improved in every write pair, with no capacity waits.
The write-only investigation follows; no selected cases are mixed into this
42-case result.

**Write-drain repeat (plan 132).** The maintained `fitting-write-b` selection
completed all 18 selected cases, three alternated repetitions of every batch /
parallelism setting (`20261014-071942-0428cc322079410ca84c019dd44983eb`), on
the same CLI and native drivers. All 18 seed oracles matched from disk; raw XML
totals and complete telemetry intervals were checked (largest gap 0.303 seconds).
Original settings were restored cleanly.

For 256 KiB × 2 versus 1024 KiB × 4, median flush time was 127.608 versus
92.157 seconds, with ranges 111.768–133.770 versus 89.589–94.871. Every pair
improved and ranges separated. Actual drained throughput was 4.692 versus
6.439 MiB/s, again with every pair improving and separated ranges
(4.420–5.379 versus 6.250–6.546). The completed write process improved in every
pair too: scored bytes divided by its full recorded process interval, including
the flush stall, gave 4.436 versus 5.918 MiB/s median, with separated ranges.
These complete observations qualify the drain-time target; they are not combined
with the earlier run's scores.

Fixed ten-second write IOPS changed −6.30%, −4.22% and +0.99% in their pairs;
the ratio of medians was −2.93%. In both complete runs all scored bytes had
already been admitted by the first observed drain progress. The interval before
that progress varied substantially because the requested flush launches an
owned control worker and observer before entering the administrative barrier.
The barrier then holds the outstanding write beyond the ten-second score.
Consequently these IOPS values do not qualify general foreground throughput or
an invariant write control; the raw drops remain recorded. No capacity waits
occurred. Retain 1024 KiB × 4 for the repeated drain-time improvement, with this
measurement limit and the unchanged explicit-flush blocking behavior. Final
product-default release checks and the historical generic comparison refresh are recorded below. A new competitor refresh is not required for these focused checks.

**Explicit-drain comparison, timing off (plan 142, 2026-10-15).** The complete
6/6 `drain-flush-decision` run `20261015-002709-cad2f47e9da14a0ba9a386a4240c607f`
(controller 0.4.718.1 at commit `587b07f`, loaded 0.4.687.1 filter `FE00891A…` and
provider `0B13C2F5…`, timing off, 2 GiB Fast, age-only Deferred) flushed a
deterministic 512 MiB dirty seed. An owned worker was ready before release and
the duration is a monotonic timer around the driver's Flush call, so process
launch is excluded. Every case drained the exact 512 MiB, left no pending bytes,
and its seed matched through unbuffered reads after the runner disabled the cache.
The byte oracle follows that additional control; it does not independently prove
the contents immediately before the disable operation.

| Setting | Flush ms per pair (1, 2, 3) | Median | Drain MiB/s | Lower writes |
|---|---|---:|---:|---:|
| 256 KiB × 2 | 12,846 · 12,713 · 11,536 | 12,713 | 40.3 | 2,050 each |
| 1024 KiB × 4 | 8,012 · 6,629 · 9,394 | 8,012 | 63.9 | 514–517 |

Every pair improved (−37.6%, −47.9%, −18.6%), the median improved 37.0%, and the
ranges separated (6.6–9.4 s against 11.5–12.8 s): the 5% target for explicit-drain
time is met on this sequential seed, consistent with the earlier cold-read flush
result (6.52 → 3.86 s). Only explicit drain time is qualified by this run.
This does not establish parity with the bare disk: its 63.9 MiB/s timed 512 MiB
Flush and the historical bare-disk 230 MB/s post-burst observation have different
workloads, timing boundaries and sessions. A matched baseline and actual backing
I/O are needed; application write completion alone can include RAM admission.

**Disabled-baseline drain repeat: incomplete (plans 144–146).** Runs
`20261015-152403-4df1148acefc487cb8f8585f98016874` and
`20261015-152550-f94d0a83a1094d84a17d653575ea537b` stopped at cases 1 and 5:
12 KiB was admitted between seed and Flush snapshots, or another 8 KiB was
accepted/dirty in the post-Flush snapshot. Plan 145 held the seed handle through
Flush/end snapshots, but `20261015-154542-30466d022cae4ed4bd7a351f60a3d451`
also failed with another 4 KiB dirty. Its drained-byte counter matched the seed
amount, but the failed case never reached its byte oracle. All three restored
without driver errors; source/precise arrival time remain untraced. Plan 146
removed the ineffective held-handle trial, retaining the original measurement
and all guards. No complete fresh drain aggregate exists.

**Read-miss repeat with Defender/Search disabled (2026-10-15).** The complete
24/24 `cache-read-misses` run
`20261015-152826-599322e9accd4e45a7aef7ae4467908e` returned ACCEPT_SCREEN and
restored the original 2 GiB / 256 KiB × 2 profile and copy flags 15. Controller
0.4.726.1 (`22463de`) used the unchanged loaded 0.4.687.1 native hashes recorded
above, the same pinned DiskSpd, timing off, 2 GiB Fast/Idle and 1024 KiB × 4
settings in both arms. Defender services/drivers and Search were disabled;
Everything, Steam and the tray were closed. Three alternated pairs gave:

| Read shape | Serial median MiB/s (range) | Offload median MiB/s (range) | Ratio | Median p99, serial / offload (ms) |
|---|---:|---:|---:|---:|
| Cold random 4 KiB Q32 | 8.802 (7.825–10.204) | 87.568 (78.166–139.892) | 9.95× | 35.199 / 8.920 |
| Cold sequential 1 MiB Q8 | 310.095 (153.073–316.542) | 970.115 (771.050–973.513) | 3.13× | 79.557 / 44.472 |
| Resident random 4 KiB Q32 | 1658.638 | 1654.824 | −0.23% | 0.105 / 0.104 |
| Resident sequential 1 MiB Q8 | 36850.000 | 36635.282 | −0.58% | 0.440 / 0.443 |

Every cold pair improved throughput and p99, and throughput ranges separated.
No resident pair lost 3% (largest loss 2.12%); resident controls had zero misses
and lower reads. All 24 raw XML byte/operation totals and actual telemetry
coverage were independently checked; the maximum gap was 0.215 seconds.
Fixed-window random maxima were 111.543 / 175.209 ms, sequential maxima
385.328 / 268.295 ms. These sample unequal amounts of work; the retained
100,000-operation comparison above addresses equal-work tails. Absolute scores
across sessions cannot isolate an effect from disabling Defender/Search or a
new driver speedup. This repeat confirms the existing #14 improvement.

**New-environment release campaign (2026-10-15, incomplete).** Controller
0.4.730.1 (`d1cf8d2`, plan 146), unchanged native 0.4.687.1 and pinned DiskSpd
ran `release-performance` in
`20261015-160320-a9813078abb44a13acb482133b8f53b4`. The first eight phases
completed, including all 12 recall cases and six five-minute sustained cases.
Map-cost case `0007-r3-random-read-s1-q8-drainFalse-map0` then failed the RAM-only
guard: six lower reads / 4,608 miss bytes, zero lower writes/flushes, zero drained
bytes and zero driver errors. Hit bytes increased by 15,433,596,928 against
15,433,564,160 scored bytes. The first extra read was observed about five seconds
into the score. This is consistent with metadata activity, but its source was
not traced; it is not a passing RAM-only window. The campaign stopped at 8/11
phases / 31 recorded cases and restored successfully. No complete campaign
performance verdict or aggregate is produced. A focused unchanged map-cost
repeat completed all nine cases in
`20261015-173103-968c4d0558e14a24ad3a223508cfacf0` and restored, without changing
the guard or reproducing the lower reads. This does not identify their source.
The separate `write-performance` run
`20261015-202536-cb295eda46644df2bef04a4b7020c5ba` completed all 72 cases in
31 min 45 s and restored. All 72 raw XML byte/operation totals and full process
telemetry intervals were independently checked (maximum gap 0.215 s; no
zero-completion scores). These are collected write scores, not a new accepted
speedup comparison. The separate `ordering-faults` run
`20261015-205732-73bca262a5374343af046e87a9f85525` completed all eight checks and
restored in 30 seconds. Its backing Q: cache was paused for the lab VHDX test
and resumed with its original settings afterwards; Q: stayed clean with zero
errors. Lab V:'s three deliberate lifetime-error increments were accounted for,
with no remaining active fault. These independent suite results cannot turn
the original campaign into a completed one.

**Foreground interference and Strict impact: unmeasured (plans 142/143).**
Both proposed suites failed at their first seed twice and were removed in
`206c5e8`. They supply no performance result:

| Fixture | Failed runs | Why it cannot answer the question |
|---|---|---|
| `drain-overlap-decision` | `20261015-003236-c69bd0f31004430dabe56f4cc0a4c494`; `20261015-004735-c183d0de9ee34efdb9e9fe051c23679f` | A quiet wait removed an initial 20 KiB dirty start. The second 75% seed took about 7 s, exceeding the default 5 s dirty age: 238 lower writes drained about 61 MB before measurement. A no-drain seed contradicts those options. |
| `strict-impact-decision` | `20261015-005332-2141cd24785c42e9bca1ebaf931cb810`; `20261015-011121-03fa9bfa90cb4f6c94b5270a8a2cd861` | In the second attempt, 2 application flushes and 1 write-through write (not issued by the seed worker) caused 2 application barriers, 1 Strict barrier, 3 lower flushes and 1,072 drain batches. Of 536,890,368 accepted bytes, 280,502,272 drained and 256,388,096 remained dirty. Sources are unattributed. |

The driver refuses option changes while enabled or dirty
(`IOCTL_QCACHE_OPTIONS_V1`, `driver/qcache/writecache.cpp`), so seeding under
held-off options and then switching to Idle is unsupported. Overlap needs a
smaller/faster seed or a native capability change. Strict's result exposes its
known whole-cache barrier cost (#16), not a regression of the new defaults;
there is no measured Strict boundary latency.

**Final release campaign (plan 132).** Signed 0.4.687.1 from functional commit
`4b0e3c6` completed `release-performance` in 7918.2 seconds (2 h 12 min), all
11 phases / 106 outer cases
(`20261014-081924-fd5ed11e2abd459aab30498cf5ca8918`). The maintained completion
reader accepted its terminal event and manifest hash. All 18,171 evidence files
were copied with SHA-256 verification. The 93 retained correctness subchecks,
36 RAM read windows, RAM lifecycle, 12 recall cases, six 300-second mixed
episodes, nine map-polling windows and 72 fitting-write windows completed.
Every one of 186 raw XML scores matched its byte total and completed operations;
all 137 declared process intervals had actual complete telemetry. Largest gap:
1.048 seconds in RAM reference, 0.333 seconds elsewhere. Final Q: was restored
to its original 2 GiB Fast/Idle 256 KiB × 2 settings with zero errors or pending
bytes, timing off and unchanged saved profiles. Lab V: was uncached and clean;
its lifetime error count includes the three intentional ordering faults, with
no active fault. Loaded filter:
`FE00891A0D6ADA54CD4FCB587C704DAFFDA97C6C0DD47F6BCF9F444C56A16632`;
provider: `0B13C2F5DBDF45BBCFBEB3B07FFCE5BB915B2A2CA50193D6B2CF8EDA5F34D8FC`.
DiskSpd is the same binary recorded above. Later `43eb208` changes documentation
only; it does not change the qualified workload or native source.

Direct RAM 1M Q8T1 median was 44,489 MiB/s (44,363–44,809), with p99
0.261–0.278 ms and zero image-I/O attempts. Lifecycle verified adaptive reads,
the 16-request cap, inline overflow and ending Direct access under load.
Controlled recall-on final scores reached 100% hits with zero lower attempts;
the hot scan control stayed resident in both recall modes. Map polling at
2 seconds / 250 ms had no repeatable throughput loss of 3%; this does not test
GUI rendering or 100 ms refresh.

The mixed episodes measured 219–258 MiB/s without driver-error deltas and with
concurrent/persisted byte oracles. Post-churn Q1 rereads still missed entirely
(152–202 MiB/s); following Q8 rereads reached 99.750–99.988% hits and
20,992–28,193 MiB/s, with 37–526 lower reads per window. Q8 follows Q1 recovery,
so these are not independent queue-depth comparisons or RAM-only controls.
Natural idle left 768–897 MiB dirty with oldest ages of 56–70 seconds before
explicit drainage. Maximum age schedules work; slow storage is not bounded by
that age. This does not establish the cause of the remaining recovery cost.

All cached write-matrix windows had zero capacity waits, error deltas and
durability barriers; background writes occurred. Two boundaries had small lower
reads, so this matrix is not the zero-I/O admission proof supplied by the separate
37-case concurrency matrix. Timing-on reduced cached random-write throughput
in every pair: about 6–7% at Q1 and 9–10% at Q32. Keep timing off for product
scores and match it between comparisons. Sequential and cache-Off results varied
substantially. These measurements do not infer a causal old-build gain or accept
every latency tail; all windows had completions. The original settings were
restored; the refreshed generic product comparison follows.

**Generic comparison refresh (2026-10-14, 0.4.687.1).** `compare-cache` completed
with three repetitions under its maintained contract (larger-file and tree
steps intentionally use two, the five-minute mix one), run
`20261014-103546-64b8c94e36eb475084cf48d92c58a5a4`. `compare-cache-cold`
completed once after checked flush and drop-clean, run
`20261014-113824-70b3db176e494efea4f7823e9199e453`. Both used the same signed
drivers and DiskSpd as the release campaign, normal priority, timing off,
Fast 2 GiB with 1024 KiB × 4, closed indexers and no competing workloads.
All 475 evidence files were copied with SHA-256 verification; all 49 raw scored
XML files matched their byte totals with completions. The owned hypervisor
sampler recorded 3,938 actual per-disk samples, covered every step, had no missing
read/write counters and a maximum 1.036-second gap; it stopped after the cold run.

Hot DiskSpd 1M Q8 median read/write was 34,775 / 20,655 MB/s. Larger 8 GiB file
reads were 80.90 MB/s random 4K Q32 and 621.88 MB/s sequential 1M Q8. The emptied
cache's first/second 1 GiB reads were 570.54 / 19,637.80 MB/s. Sustained 8 GiB
writes averaged 259.62 MB/s, with a 177.73 MB/s median slowest second. The file
tree's median create/copy/read-and-hash/delete times were
50.23 / 13.68 / 154.23 / 15.90 seconds. These do not establish an improvement in
every workload: cached large reads and tree reads remain slower in this session
than some historical comparison rows. Shared storage and different dates prevent
attributing those changes to code from these rows alone. The repeated same-build
screens above support the causal miss-offload and drain-time decisions.
Fast application flushes still return in volatile RAM; the near-zero measured
flush is not a durable-save claim. Original 0.4.604.1 and competitor rows,
including their power-cut results, retain their evidence and dates. No power-cut
qualification was repeated on this build. Full rows and ranges:
[comparison](COMPARISON.md).

The same seeded 40,000-file tree contains 1,290,078,507 bytes in both sessions.
Its read-and-hash median was 4.914 seconds on 0.4.604.1 and 154.226 seconds on
0.4.687.1 (31.4× slower); the two 0.4.687.1 repetitions were 154.160 and 154.292
seconds. Creation rose from 12.091 to 50.230 seconds, copy from 8.602 to 13.679,
and deletion changed from 16.898 to 15.898. The read/hash timer excludes the
settling wait. Windows changed from build 28000.2956 to 28000.3231 and disk use
from about 19% to 43%. At run start, Windows reported 3.31 GiB versus 2.06 GiB
in `SystemCacheBytes`, while available physical memory was similar (10.75 versus
10.36 GiB). Shared storage and file-cache residency also differed. Hypervisor
read counters across each complete tree step were about 193/0 MiB on 604 and
195/0 MiB on 687 (first/second repetition); those intervals include creation,
settling, copy, read/hash and deletion, so they do not isolate read-phase traffic.
These cross-date numbers identify a large repeated observation in the new
session, not its cause.

**Maintained tree diagnosis (2026-10-14, plan 134).** The complete 15-case run
`20261014-175457-b7459c972da24d5a809c98512e31245b` passed every file-count,
byte-count and SHA-256 check and restored the original 256 KiB × 2 configuration
with zero dirty or in-flight bytes. It used CLI 0.4.702.1 on Windows build
28000.0 and the loaded 0.4.687.1 filter hash recorded in the run manifest;
diagnostic timing was off. Across three repetitions, median create/copy/read+hash/
delete seconds were:

| Arm | Create | Copy | Read + hash | Delete |
| --- | ---: | ---: | ---: | ---: |
| Cache disabled | 51.68 | 30.67 | 161.68 | 19.12 |
| Flags 7, 256 KiB × 2 | 50.98 | 10.61 | 169.30 | 17.68 |
| Flags 15, 256 KiB × 2 | 52.03 | 10.44 | 160.88 | 15.00 |
| Flags 7, 1024 KiB × 4 | 52.44 | 10.55 | 161.01 | 17.36 |
| Flags 15, 1024 KiB × 4 | 50.77 | 10.51 | 160.94 | 15.80 |

Read+hash took 160.07–170.45 s across all arms. The paired 1024 KiB × 4 arms
were effectively identical; the smaller-batch flags-7 arm was noisier, but its
three paired differences did not repeat. During read+hash, driver lower-write and
flush attempts and cache read-hit bytes were zero in every case; lower-read
attempts were 0–33. Windows reported 5.49–6.32 GiB of system cache around this
phase. This points away from QueueCache's read-miss scheduling and drain settings
as the cause of the measured slow phase. The run does not distinguish per-file
open/NTFS work, hashing, security filters, or actual Windows cache residency, and
it cannot explain the cross-date 604/687 difference. No driver change is justified
by this matrix; the historical tree regression remains open at the Windows/file-
tree attribution level. Do not treat the near-zero QueueCache read-hit counter as
proof that all file data was resident in physical memory.

**Tree diagnosis disposition (plan 141 runner, 2026-10-15).** One filtered,
diagnostic (not decision-grade) tree case, `tree-r01-p05-flags15-b1024-p4` in run
`20261015-000302-165f7519bbf14c7fac6b28b0e648ef16`, was repeated with Windows
Search stopped and disabled, controller 0.4.716.1 and an owned observer: a
Microsoft Defender performance recording over the read phase
(`New-MpPerformanceRecording`, `Get-MpPerformanceReport`) plus per-process CPU
time and system counters at about one sample per second. It passed the
40,000-file byte/SHA-256 oracle and reproduced the slow phase: create 57.2 s, copy
17.4 s, read+hash 164.2 s, delete 41.7 s. During the 164.2-second read+hash
window `MsMpEng.exe` used 156.5 CPU-seconds (136.2 user, 20.4 kernel), the tree
worker only 9.2 s and System 2.0 s; Q: disk throughput was zero and the driver
reported 144 lower read attempts and no read-hit bytes. Defender's report for the
240-second recording counted 40,338 real-time scans (153.1 s in total), 40,024 of
them in the tree worker (151.6 s, median 3.47 ms, maximum 595 ms) — 92% of the
phase; each of the ten slowest was an `OnOpen` scan of a tree file. The slow phase
is therefore Windows Defender's real-time scan of each freshly written file at
its first read-open (about 3.8 ms of antimalware CPU per file), above the volume
filter; the cache-off arm of the earlier 15-case matrix had the same time.
**Disposition: environmental, not a QueueCache regression.** Defender was left
enabled with no exclusions for that diagnosis, and no driver change is supported.
The observer and recording added a small CPU load, so this case is not used as a
timing result. What remains unproven is why the 0.4.604.1 session on 2026-10-12
took only 4.9 s for the same step: the comparison's environment record does not
capture Defender state, platform/signature versions or scan caching, and Windows
had moved from revision 2956 to 3231, so the older session's Defender behaviour
cannot be reconstructed. The planned cross-build driver swap (`tree-build-decision`,
four reboots) was therefore removed unrun: the cost does not depend on the filter,
and the measurement could not separate the missing Defender state.

**Later environment change (2026-10-15).** At the owner's request, the dedicated
test VM now has Defender Antivirus and Windows Search indexing disabled as its
persistent baseline; the effective-state checks are in [TESTING.md](TESTING.md).
No performance workload was run during that administration change. A subsequent
single current-settings `tree-decision` observation completed on controller
0.4.728.1 (`cfd0713`) and unchanged native 0.4.687.1:
`20261015-155033-d0d1032a62954ca7be893e4f6ce05e66`, case
`tree-r01-p05-flags15-b1024-p4`. All 40,000 files / 1,290,078,507 payload bytes
passed the hash oracle and the original runtime settings/flags were restored.

| Phase | Defender-active diagnostic, seconds | Disabled-baseline observation, seconds |
|---|---:|---:|
| Create | 57.2 | 8.691 |
| Copy | 17.4 | 9.346 |
| Read/hash | 164.2 | 6.857 |
| Delete | 41.7 | 40.971 |

The current after-create/copy settles lasted 161.3 / 170.4 seconds and observed
disk idle. Read/hash added 1,300,009,984 cache-hit bytes with zero lower
read/write/flush attempts, whereas the earlier diagnostic stayed in the Windows
file cache. This is one observation in a changed environment/cache path, not a
complete paired matrix, an isolated Defender percentage, a new driver speedup or
a replacement for historical product/CrystalDiskMark scores. It is consistent
with removal of the previously observed scanning cost. The 164.2-second figure
remains a diagnostic with observer overhead.

Historical power loss on QueueCache 0.4.604.1 (VM reset 0.03 s after a write test):
the two checked large files survived in each mode; this does not establish that
Fast-mode application flushes are durable. New unflushed small files: bare disk 698 missing and
1,066 incomplete of 10,000; Strict 478 / 950; PrimoCache without Defer-Write 0 /
105; PrimoCache Defer-Write all 10,000 missing with a clean file system; QueueCache
Fast 5,668 missing and `chkdsk` exit 13. Later partial-drain churn also damaged
NTFS with PrimoCache. The current safety goal in
[#13](https://github.com/devedse/QueueCache/issues/13) allows loss of pending
writes while requiring a healthy filesystem and intact untouched durable data.
That supersedes the earlier acceptance of filesystem damage; no new-build
power-cut qualification or safety fix is claimed.
[Full evidence](COMPARISON_DETAILS.md#power-cut-while-the-cache-is-writing).

## RAM-disk asynchronous split reads: accepted (PR #9)

**Why reads were slow.** A Direct RAM-disk read of 1 MiB is split into four
256 KiB chunks copied by four processors, but the submitting thread waited until
all were copied before it could submit its next read, so queue depth 8 behaved
like depth 1. Plan 117 measured one active Direct read at Q8T1; of each ~41 µs
read, ~23 µs was the copy and ~18 µs submission/completion while the helpers
idled. The disk cache already completes queued reads from its own threads and
reached 39,492 MB/s in the README's CrystalDiskMark run versus the RAM disk's
25,353 MB/s.

**Change.** A large, asynchronous, non-paging Direct read posts all of its chunks
to the other processors' existing helpers (same chunk size, copy code and posting)
and returns; the helper that copies the last chunk completes the request. Helpers
receive work on every read, so they stay awake (post-to-take ~1.2 µs). The three
earlier asynchronous designs (`3fb9d39`/`e8e31c8`, `c5d32a8`, `9262493`) instead
gave each whole 1 MiB read to one worker: a 4× slower copy, and round-robin
workers that went to sleep between reads (21% spin versus 28% copy); they lost.
Admission reuses the provider's write policy (asynchronous while another read is
in flight; inline after 16 that found none, probing every 64th), stays inline
while more than one thread submitted a large read within 1 ms, and caps 16 in
flight. The adapter now withdraws Direct views before stopping its workers.
Modes per RAM disk: off, adaptive (default), always (lab correctness checks).

**Same-build comparisons** (owned 2 GiB RAM disk, 1 GiB file, DiskSpd 2.2 from
CDM 9.0.3, 10 s windows with 3 s warmup, normal priority, three alternating pairs,
diagnostics off; MiB/s medians and ranges):

| Run | Shape | Original | Candidate | Change |
|---|---|---:|---:|---:|
| Plan 118, build 575 | 1M Q8T1 target | 24,268 (23,986–24,591) | 44,766 (42,485–44,950) | +84.47% |
| | 1M Q1T1 | 24,441 (24,389–24,978) | 24,249 (24,218–24,536) | −0.79% |
| | 1M Q2T4 | 36,327 (36,202–36,886) | 34,223 (34,144–35,307) | **−5.79%, every pair lower** |
| Plan 119 revision, build 579 | 1M Q8T1 target | 24,234 (23,927–24,548) | **43,583 (43,295–43,696)** | **+79.84%** |
| | 1M Q1T1 | 24,975 (24,881–25,109) | 24,844 (24,600–24,953) | −0.52% |
| | 1M Q2T4 | 36,412 (36,168–37,887) | 36,578 (36,474–36,693) | +0.46% |

The first run lost four-reader throughput because every thread handed off; the
pre-declared revision (inline while several threads submit) kept ~473k of their
reads inline and removed the loss. Candidate target windows completed 99.7–99.9%
of Direct reads asynchronously (the runner fails a run below 50%). Q8T1 p99 read
latency rises from ~0.07 to ~0.3 ms because queued reads now really overlap; the
old figure was each read completing synchronously before the next was submitted.
Both runs: all 18 whole-file hashes identical, 33 owned exits zero, zero image
I/O, maximum telemetry gap 1.157/1.112 s, mode restored after every window.
Runs `QueueCache-Campaign-20261011-080905-c70ad40ec31c42548c7a91415a73afd6` and
`QueueCache-Campaign-20261011-084408-58e5284403004b94a02d67dac82dee06` (PR9-AsyncCompare1/3, private evidence).

**Correctness.** Driver Verifier (special pool, IRQL, I/O verification, deadlock
detection, DDI compliance) first caught a bugcheck 0x3B: the eligibility check
used `IoIsOperationSynchronous`, which dereferences a file object that an NTFS
request to the volume does not carry; it was replaced before any measurement.
`ram-read-async-lifecycle` then passed under Verifier and without it: a
single-thread overlapped reader checks every 32 KiB of each 1 MiB block (poisoned
before each read). Without Verifier: adaptive Q8 59,469 MiB verified (99.8%
asynchronous); forced Q32 60,952 MiB with the in-flight cap reached at exactly 16;
forced Q8 while an unrecognized control ended Direct access: 31,367 asynchronous
reads then the standard path, 58,361 MiB verified, rebound. Under Verifier the
same checks verified 14,452 + 18,690 + 17,558 MiB with no fault. Under Verifier
submission is slower than a copy, so the adaptive policy correctly stays inline.

**README benchmark.** Real CrystalDiskMark 9.0.3 GUI on a new 4 GiB RAM disk T:
(0.4.583.1, default adaptive mode, normal priority, Verifier off, Default theme,
150% zoom, two complete runs, the faster kept): SEQ1M Q8T1 read **47,583 MB/s**
(README before: 25,353), write 24,434 (23,266); SEQ1M Q1T1 26,649 / 16,463;
RND4K Q32T1 1,467 / 1,194; RND4K Q1T1 1,657 / 1,310 (1,641 / 1,317). The other
run: 47,663 / 23,393, 26,206 / 16,830, 1,463 / 1,206, 1,649 / 1,312. Only the RAM
disk column was re-measured; the Q: columns are unaffected and stay from 0.4.476.1.

**Final regression.** Signed 0.4.583.1 (final source; drivers loaded and
Verifier off), lean `release-performance`, one command: **COMPLETED 11/11 phases,
106/106 cases, 1 h 56 min 23 s, restored**, run
`QueueCache-Campaign-20261011-100402-d4f2fc304e714c708305406f06b93426`. All 118
retained correctness sub-checks pass; 2,266 owned process exits are zero. The
RAM reference under the new default measures Direct 1M Q8T1 at a median
43,590 MiB/s (Q1T1 24,821, Q2T4 36,097; 4K reads never reach the new path). The
lifecycle checks, recall, 30-minute sustained phase and map cost pass their
guards. The 72-case write matrix against PR #8's run (0.4.476.1, two days
earlier, same DiskSpd): cached sequential writes are within noise (Q1 +0.6%, Q8
−5% to +1% with overlapping ranges), cached RND4K Q32 ±2%, cached RND4K Q1
1.5–2.7% lower with separated ranges. That last difference is under the 3%
review threshold and within cross-day variation seen before; PR #9's default-off
diagnostics add a few counter updates to the cache path and may contribute.
Uncached results follow the Ceph storage (higher today).

## RAM-disk writes: no speedup available on this machine (plan 120)

**Question.** CrystalDiskMark wrote SEQ1M Q8T1 at 24,434 MB/s to the RAM disk
against 47,583 MB/s read. Queued large writes already return at once and are
copied by the provider's per-processor workers, so the read fix (overlapping one
submitter's requests) was already in place for writes.

**Measurement** (`ram-write-reference`, signed 0.4.596.1, owned 2 GiB RAM disk,
1 GiB file, DiskSpd 2.2 with a fixed 1 MiB source buffer `-Z1M`, 10 s windows with
3 s warmup, three alternating repetitions; medians, MiB/s). The memory rows copy
1 MiB blocks between a hot buffer and a cold 1 GiB region in user mode, with no
driver involved:

| Shape | One thread / submitter | Four threads / submitters |
|---|---:|---:|
| Copy into cold memory, runtime copy | 19,271 | 22,110 |
| Copy into cold memory, explicit SSE2 stores | 18,030 | 22,114 |
| Copy into cold memory, non-temporal (streaming) stores | 23,621 | 24,161 |
| Copy out of cold memory (read), runtime copy | 40,091 | 47,099 |
| RAM-disk 1M writes, Q1 / four submitters at Q2 | 15,791 | 23,101 |
| RAM-disk 1M writes, Q8 / four submitters at Q8 | 23,190 | 24,293 |

Four submitters write 0 to 5% faster than one, and queued writes already exceed
the machine's four-thread ordinary-store copy rate. Writing into memory the
processor has not cached costs about twice a read on this VM (22 against 47 GiB/s),
so the RAM disk's write rate is the machine's, not the driver's. Splitting writes
across processors (as reads now are) cannot help; the September tuning had also
measured split writes slower than worker writes (16.2 against 24.7 GB/s).

**Candidate rejected: streaming stores.** The only lever left was non-temporal
stores, which skip reading each destination line (one user-mode thread: 23.6
against 18.0 GiB/s). A default-off per-disk mode streamed every large write copy;
the same-build comparison (`ram-write-streaming`, 18 windows, 100% of candidate
bytes streamed) found 1M Q1T1 **−0.59%** (target), Q8T1 −0.09%, four submitters
−2.17%: verdict REJECT. The mode, its provider actions (0x105/0x106, reserved)
and the comparison suite were removed; `ram-write-reference` stays. The Q1 write
time is not dominated by the copy's memory traffic.

Runs: `QueueCache-Campaign-20261011-180850-8b826f60a1b041e6a900421e6f25e7f8`
(reference, COMPLETED 36/36, all three disks rewritten and read back) and
`QueueCache-Campaign-20261011-181541-df9636018eab4518b202118baa8f8954`
(comparison, COMPLETED 18/18). An earlier reference run on 0.4.595.1 stopped after
its first repetition on a runner bug (rewriting the oracle over the existing file)
and is preserved but not used. The VM's virtual processor hides some copy
features (ERMS is not reported), so a physical PC may reach a different limit.

## Cached reads: lighter read-hit bookkeeping accepted (plan 120)

**Where the time went** (`cache-read-reference`, signed 0.4.595.1, 2 GiB Fast
cache on Q:, resident 1 GiB file, 10 s windows, three repetitions, timing-on
windows for the lock counters; medians):

| Resident 1M read | MiB/s | Served by | Lock acquisitions / wait / held per read | Worker queue per read |
|---|---:|---|---:|---:|
| Q1T1 | 18,625 | caller thread (99%) | 2.0 / 0.4 µs / 7.5 µs | 0.1 µs |
| Q8T1 | 36,138 | copy threads (100%) | 3.0 / 8.9 µs / 11.5 µs | 20.8 µs |
| Q2T4 (four readers) | 32,072 | copy threads (100%) | 3.0 / 8.8 µs / 12.2 µs | 15.9 µs |

At Q8 the cache lock was busy about 40% of the time, and four readers were slower
than one: the limit was bookkeeping under the lock, not the copy. Each 1 MiB hit
walked its 256 blocks five times under the lock: the request worker's residency
check, the copy thread's residency check, a second lookup to pin, the unpin and a
recency move per block.

**Change.** Copy flag 4: the copy thread records each block's slot during its
residency check and pins from that record (one lookup per block, not two), and
blocks that are already neighbours in their clean recency list move to its end in
one step instead of one relink each. The resulting recency order is identical to
moving them one at a time; dirty blocks, promotions and non-neighbours keep the
per-block path. Same-build comparison (`cache-read-bookkeeping`, signed 0.4.597.1,
18 cases, three alternating repetitions, timing off; MiB/s):

| Shape | Flags 3 (before) median / range | Flags 7 median / range | Change |
|---|---:|---:|---:|
| 1M Q8T1 target | 34,951 (34,159–36,190) | **37,718 (37,217–37,738)** | **+7.92%**, every pair up, ranges separated |
| 1M Q1T1 | 18,782 (18,702–18,847) | 19,631 (19,570–19,860) | +4.52%, ranges separated |
| 1M Q2T4 | 32,689 (32,007–33,352) | 33,818 (33,017–34,080) | +3.46%, every pair up |

Accepted and on by default from plan 120 (copy flags 7). Runs:
`QueueCache-Campaign-20261011-173857-a865de3e67804982a12c91fc40b04568`
(attribution, COMPLETED 18/18) and
`QueueCache-Campaign-20261011-182600-f8dea4c1adaf491ab3996c92fba644e1`
(comparison, COMPLETED 18/18). The request worker's own residency check and the
per-block lookups that confirm a version is still current remain; removing them
would change which reads are offloaded or which versions are touched.

**CrystalDiskMark** (0.4.604.1, the README procedure, one session, MB/s). Copy
flags 7 in three runs: SEQ1M Q8T1 read 38,945 / 39,702 / 39,707, SEQ1M Q1T1
19,825 / 19,564 / 19,887. One run with the old bookkeeping (`lab-copy-flags Q: 3`)
between them: 38,645 and 18,962. CrystalDiskMark therefore shows about +3% at
Q8T1 and +4.5% at Q1T1; its five-second passes include file opening and the
first touches, which the 10-second DiskSpd windows above exclude. The README
cache column (39,702 read, run 2) matches the 0.4.476.1 run two days earlier
(39,492): that earlier evening was simply faster, so cross-day CrystalDiskMark
numbers say nothing about a change.

**Final regression.** Signed 0.4.604.1 (final source; drivers loaded and
Verifier off), lean `release-performance`, one command: **COMPLETED 11/11 phases,
106/106 cases, 1 h 47 min 34 s, restored**, run
`QueueCache-Campaign-20261011-223323-2aa3fb80bfdd4040ad56e7a601f3d09a`. All 118
retained correctness sub-checks pass. Of 2,265 owned process exits one is
non-zero: the first final inspection found Q: still writing back, and the
plan-120 retry found it clean on the next attempt. Against PR #9's final run
(0.4.583.1, the same day, same DiskSpd, same analysis): the RAM reference is
unchanged within run-to-run variation (Direct 1M Q8T1 median 43,845 against
44,215 MiB/s, Q1T1 24,434 against 24,668, Q2T4 36,067 against 37,818; the RAM-disk
driver did not change). Cached 4K random reads at Q8 in the map-cost phase have a
median of 381,055 against 369,516 IOPS (+3.1%); cache recall hits are within 1%.
The write matrix's cached cells are 0.8–4.2% lower at Q1 and RND4K Q32 and between
−0.6% and +2.2% at SEQ1M Q8. Plan 120 changed only the driver's read path, so
these write differences are run-to-run variation (see the
[measurement rules](#measurement-rules) on cross-run comparisons). The 30-minute
sustained phase ran at 2,709–2,990 IOPS against PR #9's 1,415–2,837, following
the Ceph storage below Q:.

## Which changes belong together

| Stage | Changes retained | Evidence and limits |
|---|---|---|
| Already on master before PR #8 | Direct RAM-disk access; large RAM-disk writes use the Standard provider path; provider split-copy helpers; 256 KiB chunk allocator; prefetch and bounded in-chunk RAM-cache hit copies | These earlier optimizations were measured separately. They were not introduced by PR #8 or rejected by PR #9. |
| Merged [PR #8](https://github.com/devedse/QueueCache/pull/8), merge `fb34f878581590123a190b048fdfd23c5d4e9b39` | Read recall; maintained concurrency, sustained-use and map-cost scenarios; neighboring-sector checks; map lifecycle/grouping; map pop-out/full-screen; 100 ms refresh preference and occupancy legend; runner cleanup; regenerated README benchmarks/screenshots | Read recall has controlled positive results and a documented temporary hit-rate trade-off. Full write matrix completed 72/72. Real Windows UI behavior was checked; 100 ms rendering cost was not benchmarked. |
| [PR #9](https://github.com/devedse/QueueCache/pull/9), `perf/ram-read-followups` | **Asynchronous split RAM-disk reads (default adaptive)**; partial-read diagnostics/scenario; caller dispatch attribution and bounded runtime lab control; RAM-reference verification and robust sampling; independent owned-child priority comparison; single-command campaigns; restoration, ownership and parser contracts; consolidated findings | 1M Q8T1 RAM-disk reads +79.84% in the accepted same-build comparison; lifecycle checks pass with and without Driver Verifier. Earlier PR #9 measurements: 10 partial-read checks, 36 RAM-reference windows, 54 RAM-queue comparison windows, 24 windows per NTFS/ReFS caller comparison and 36 priority windows. |
| PR #9 experiment removed | Shared sleeping queue for whole large Direct RAM-disk reads | Same-build comparison loses 3.62% Q1, 11.30% Q8 and 49.62% four-reader throughput. The native engine is removed; wire reservations remain for compatibility. |
| Plan 120, `perf/writes-cache-edge-cases` | **Lighter read-hit bookkeeping (copy flags 7)**; `ram-write-reference`, `cache-read-reference`, `cache-read-bookkeeping` and `stress-races` suites | Cached 1M Q8T1 +7.92% in the accepted same-build comparison. RAM-disk writes measured at the machine's memory write bandwidth. |
| Plan 120 experiment removed | Streaming (non-temporal) stores for large RAM-disk write copies | Same-build comparison: Q1T1 −0.59%, Q8T1 −0.09%, four writers −2.17%. Mode, provider actions 0x105/0x106 (reserved) and comparison suite removed. |

## Measurement rules

- GB/s below means decimal billions of bytes/s; MiB/s means 1,048,576 bytes/s.
  Q8T1 means one submitter with queue depth eight; Q2T4 means four submitters
  with depth two each. Equal total depth does not imply equal execution cost.
- Use the maintained foreground `qcache developer verify` runner, immutable
  run/case IDs and actual loaded-module hashes. A version string does not prove
  which driver is loaded. `MEASURED` proves collection, not speed acceptance.
- Compare the same DiskSpd binary/hash, settings, workload and machine. The
  investigations below use CDM 9.0.3's DiskSpd 2.2 x64, SHA-256
  `7281BF6DA6C03797016EDDF2E8AAEC4C644AE893D403D57A030B7E2E14B61079`.
- Resident controls require zero lower read/write/flush attempts, complete
  telemetry readiness/coverage, stable identity and byte accounting. Missing
  samples or lower-I/O contamination invalidate a window. Preserve incomplete
  runs separately; never join their repetitions into a completed matrix.
- XML supplies scored bytes/seconds. Driver counter and CPU-profile intervals
  enclose process startup, warmup and closure; do not label them exact score
  windows. Traced runs are diagnostic and excluded from untraced acceptance.
- Scopes differ: oracle files prove their own bytes; they do not verify
  DiskSpd's randomized write payload or force every kernel interleaving.

## Priority and background interference

| Finding | Evidence | Decision |
|---|---|---|
| Task Scheduler's default below-normal launch can substantially depress scores | Historical combined CPU/I/O/memory change: cached SEQ1M Q8 reads 16.4–17.7 versus 28.9–30.6 GB/s; RAM-disk Q8 writes 16.3 versus 24.7 GB/s; uncached reads/writes 262/116 versus 640/270 MB/s | Launch normal-priority benchmarks; Task Scheduler priority **4**. This historical comparison did not isolate the three settings. |
| Driver Verifier can dominate benchmark overhead | Same 0.4.249.1, matched maintained random-write Q1 diagnostic: Verifier on median 94.529 MB/s, off 973.255 MB/s (10.30×). GUI random Q32 read/write 77.030/69.959 → 1,790.783/1,402.940 MB/s after the approved restart | Use Verifier for correctness runs and Verifier off for performance. Restart/cache history also changed; do not attribute every remaining sequential difference to Verifier. |
| Normal CPU class alone is insufficient to reproduce the intended launch settings | Scheduler priorities 5/6 also use normal CPU but lower memory defaults | Use priority 4 and record actual child settings. Do not silently boost user applications or driver threads. |
| CPU class alone reproduces a large resident Q8 loss | Complete plan-113 comparison, 36/36: normal 36,699.00 versus CPU BelowNormal 24,147.65 MiB/s, **−34.20%**, separated ranges and zero lower I/O. Q8 low I/O/memory 36,648.95/36,915.58, overlapping normal ranges | Keep explicit normal benchmark launches. Completed CPU/wait analysis supports a priority/affinity follow-up; no driver/application priority change is accepted. Low memory is a process default after launch, without pressure injection or locked-page changes. |
| First isolated-priority attempt is incomplete | Run `20261010-071617-21a3d11a0fc748f4b1d68be4c7c286cc`: 26 measurements then case 27 rejected one ordinary 8 KiB lower read. No paging-read, eviction, drain or driver-error increment; clean restoration | Preserve separately. Counters do not identify the read's file/process. Accepted retry on W: uses new immutable IDs and none of the incomplete run's samples. |
| NTFS last-access updates contaminate strict controls | 12–16 KiB metadata writes caused five rejected layout runs in a day | Disable last-access updates on this benchmark VM and record the setting. Do not relax lower-I/O guards. |
| Defender can read an unscored cold file tail | Earlier concurrency failure identified `MsMpEng.exe`; retained extents mapped the recorded offset to the last 64 KiB of a 1 GiB file, outside its 512 MiB scored prefix | Workload preparation/size contracts were corrected and versioned. This was not proof of write-admission fallback. |

Methods and preserved failure details: [README benchmark method](TESTING.md#7-readme-benchmark-crystaldiskmark),
[priority comparison](history/PRIORITY_COST_20261010.md),
[sustained validation](history/SUSTAINED_CACHE_VALIDATION_20261009.md),
[Verifier comparison](history/PERFORMANCE_INVESTIGATION_20260930.md).

The accepted priority retry is
`20261010-073414-2f6dd18225094fc98abc3560ddaca39e`, 07:34:14–07:53:17 UTC,
signed native 0.4.514.1 / managed preview `1788970`, same native hashes as the
caller comparison below. All 940 owned process exits succeeded; maximum telemetry
gap was 0.689031 seconds and priority application 0.01089 seconds. All windows
had zero staged/lower I/O and clean independent restoration. Its raw archive
SHA-256 is `5A97128327D6C155EDE6DB82252182C14F310D8D339E2581C7296826A054A8DF`.

| Shape, median MiB/s | Normal | CPU BelowNormal | I/O low | Memory default low |
|---|---:|---:|---:|---:|
| 1M Q1T1 | 18,689.61 | 18,511.79 | 18,576.52 | 18,580.02 |
| 1M Q8T1 | 36,699.00 | 24,147.65 | 36,648.95 | 36,915.58 |
| 4K random Q1T1 | 1,245.19 | 1,241.80 | 1,271.56 | 1,253.55 |

CPU Q1/random median changes are −0.95%/−0.27%; low I/O random Q1 is +2.12%
with separated three-repeat ranges. Small control differences do not establish
general improvements. These resident cached-read results do not measure low-I/O
miss performance or isolate every historical RAM-disk/disk-only difference.
Full ranges are in the priority report.

The separate WPR Q8 subset completed 4/4, run
`20261010-075450-78a3c72b51f74c20839c855175eb9f1c`, with clean restoration,
zero lower/staged I/O and maximum telemetry gap 0.281858 seconds. The trace is
preserved and stopped. Traced scores
are excluded from the comparison, so recording overhead cannot be mistaken for
a release regression. Offline analysis has now completed with lost events
rejected and matching native PDBs verified:

| Q8 trace variant | Non-idle CPU | Cache memcpy, busy samples | Cache RequestWorker, busy samples | All DiskSpd threads' ready time |
|---|---:|---:|---:|---:|
| Normal | 95.9% | 44.2% | 5.5% | 1.454 s |
| CPU BelowNormal | 65.0% | 37.2% | 8.3% | 6.358 s |
| I/O low | 96.6% | 42.2% | 5.5% | 1.536 s |
| Memory default low | 94.8% | 42.6% | 5.1% | 1.517 s |

BelowNormal has CPU 0 99.3% busy while the other three are 44.0–49.1% idle;
Normal has all four only 2.4–4.8% idle. XML records affinity enabled in every
variant. This supports scheduler-ready delay and reduced parallel utilization;
it does not establish a specific helper-polling cause or justify a priority boost.
The next diagnostic should isolate priority **and affinity** before selecting a
driver change. Function shares are over enclosing 13.1-second intervals;
RequestWorker includes request work and polling. Ready time sums all benchmark
threads; long control/termination waits are not I/O latency. Many BelowNormal
readying stacks/processes are unavailable, so a specific waker cannot explain
every delay. These cache-worker samples are distinct from the RAM-provider
WorkerMain samples below. Traced scores remain excluded from acceptance.
ETL SHA-256 `26C2F946D8F06DAC5D3321E443D8ED04FBA294C6AA6ACBFBC92465CD5B9C598A`;
analysis archive `A22B6B563DFBA206CE71B61B7012B068C786CC6C98DD629AAD6FC827D992A751`.

## Earlier retained request-path improvements

The copy-offload strategy that works for a RAM cache on a disk volume predates
both recent PRs. These historical before/after figures explain what was reused
as motivation for the RAM-disk experiments; they are not fresh PR #9 scores.

| Change, 0.4.153.1–0.4.162.1 | Measured finding | Retained behavior |
|---|---|---|
| Serve an idle eligible request on its submitter | Random Q1 about 24,500 → 238,000 reads/s and 22,000 → 190,000 writes/s; avoided about 11 μs worker wake for about 1.4 μs cache work | Caller path for eligible idle requests; preserve control/ordering ownership. |
| Stop waking unused drainers | Previously about 180,000 extra wake-ups in five seconds; random Q32 writes could collapse near 35,000/s during write-back | Only configured drainers wake. |
| Keep deep queues on the ordered worker | Q32 worker 333,000–356,000 versus caller 217,000–236,000 reads/s | Periodic probes and 256-request post-overlap backoff, now attributed by PR #9 counters. |
| Parallel copies with fewer lock acquisitions and a push lock | Initial copy offload alone stayed near 15.2 GB/s; one lock release for pinned range reached 21.9–23.8; push lock reached 34.5–36.2 GB/s | Three offloaded-request threads for suitable resident reads/large fitting write copies; metadata/pin ownership remains serialized where required. |
| Brief idle-worker polling | Restored random Q32 writes near 310,000/s | Bounded 30 μs poll before sleeping. |

With the same historical warmed 1 GiB CDM-style contract, 0.4.151.1 → 0.4.162.1
SEQ1M Q8 read ranges changed 14,337–14,437 → 35,949–36,510 MB/s and writes
12,635–13,954 → 20,508–21,483 MB/s. Earlier builds' single-worker ceiling was
confirmed by bisect; more submitting threads alone did not lift it.

Additional reusable findings: detailed timing reduced historical random Q32
write medians about 6.9%/6.5% in separate Eager/Idle comparisons; keep timing modes
separate. On 0.4.259.1, precomputed write data (`-Z1M`) measured 21,406 MB/s versus
16,521 MB/s for fresh randomized data per I/O (`-Zr`), alongside resident reads at
36,185 MB/s. Data generation is part of the workload cost, not a driver speed-up.
Large repeated cached writes measure volatile acknowledgement/coalescing, not
durable storage throughput; capacity pressure and real Flush remain disk-bound.
Historical one-sample baselines must not be treated as current scaling limits.
Methods: [write trajectory](history/WRITE_PERFORMANCE_TRAJECTORY.md),
[sequential reproduction](history/SEQUENTIAL_PEAK_REPRODUCTION_20260930.md).

## Cache allocation and copy optimizations

All figures here are resident sequential 1 MiB read medians in GB/s, with Q1/Q8
shown together. Lower-I/O guards distinguish RAM placement cost from disk misses.

| Controlled comparison | Q1 / Q8 | Finding |
|---|---:|---|
| Old allocator, fresh | 15.46 / 36.85 | Initial ascending free slots are fast. |
| Old allocator, sequential use then clear | 10.75 / 29.81 | Reuse reverses free-slot order and reduces resident throughput. |
| Old allocator, random use then clear | 8.56 / 23.18 | Scattered reuse is slower still, despite zero disk reads. |
| Same allocation, reset free-slot order after sequential reuse | 15.24 / 36.26 | Changing only free-slot order restores fresh speed; changing physical allocation is unnecessary. |
| Shuffled 256 KiB chunks, ascending slots inside | 14.77 / 35.28 | Within 4% of fresh. Ordering within a chunk matters more than ordering all chunks globally. |
| Chunks ordered, slots descending inside | 10.71 / 29.54 | Direction inside each chunk reproduces the reuse loss. |
| Chunk allocator, sequential reuse then clear | 14.61 / 35.66 | Retained from 0.4.431.1; uses per-chunk free bitmaps, ascending allocation and whole-free chunks first. |
| Chunk allocator, random reuse then clear | 14.50 / 35.48 | Reuse penalty largely removed. |

The allocator does not relocate survivors after partial eviction. Before the copy
optimizations, short/full-cache churn rereads changed little between allocators
(13.86/30.66 versus 13.79/30.05; 14.15/33.17 versus 14.28/32.56 GB/s).
Physical TLB/prefetch costs were not isolated by hardware-counter measurements.

| RAM-hit copy mode | Fresh Q1 / Q8 | Sequential reuse Q1 / Q8 | Random reuse Q1 / Q8 | Decision |
|---|---:|---:|---:|---|
| One copy per 4 KiB block | 15.13 / 35.94 | 14.61 / 35.66 | 14.50 / 35.48 | Diagnostic flag 0 control. |
| Prefetch next block | 18.25 / 37.44 | 17.36 / 35.29 | 17.40 / 36.19 | Helps Q1, including about 19% after churn. |
| Unbounded contiguous copy runs | 14.88 / 22.10 | 19.33 / 37.91 | 19.37 / 37.02 | Reject the unbounded form: fresh Q8 falls sharply when a run becomes 1 MiB. |
| Runs bounded to one 256 KiB chunk | 20.60 / 39.51 | 19.22 / 37.79 | 19.41 / 34.55 | Removes that copy-size regression. |
| Prefetch plus bounded runs | 20.85 / 39.36 | 19.34 / 37.71 | 19.32 / 37.17 | Retained as default flags 3 from 0.4.434.1; integrity/policy/pressure checks pass. Plan 120 adds flag 4 (default 7, [cached reads](#cached-reads-lighter-read-hit-bookkeeping-accepted-plan-120)). |

These optimizations affect **RAM-backed cache hits on a disk volume**. A dedicated
RAM disk uses a different payload layout: locked pages and the provider's
existing `CopySplit`/`LargeCopy` helpers. Direct RAM-disk access already reuses
that provider copy path; there is no separate newly accepted port of the cache's
prefetch/coalescing loop into PR #9. Large copies are split into 256 KiB pieces
and can enlist provider helpers. A new whole-request queue changes submission
and completion scheduling, which must be tested independently of the copy loop.

Detailed evidence: [allocation/copy investigation](history/CACHE_LAYOUT_INVESTIGATION_20261008.md).

## Read recall: the retained PR #8 speed-up

Disk rereads are necessary when data was evicted. The old admission rule also
made repeated reads of a fitting file keep evicting their own freshly fetched
blocks. Read recall fixes that second problem: a bounded history of evicted
blocks compares their last-use time with the oldest cached data. It costs four
bytes per cache slot, about 0.1%, within the fixed budget. New one-off scans still
enter near the eviction end; `drop-clean` clears history.

| Workload | Earlier rule | Read recall | Interpretation |
|---|---|---|---|
| Fitting reread, pass 3 | 12.1% hits, 178 MiB/s | 100% hits, 19,052 MiB/s | Subsequent passes become resident; first disk fetches are not eliminated. |
| Fitting reread, Q8 after full passes | 22.8% hits, 158 MiB/s | 100% hits, 36,943 MiB/s | Controlled same-build gain; recalled resident passes make zero disk reads. |
| One-off/repeated scan then hot-set read | Hot set stays 100% resident | Hot set stays 100% resident | Scan resistance retained. Larger-than-cache repeated scan has 48.6% hits in both modes. |
| 30-minute mixed soak, churned Q8 reread | 130–237 MiB/s; 8.8–17.5% hits | 15,620–19,877 MiB/s; 99.6–99.8% hits | Recovery improves substantially with exact concurrent/post-drain byte checks. Q8 follows Q1 recovery, so it is not an independent queue-depth comparison. |
| Random workload resuming after a reread | More of its older working set survives | About 7 percentage points fewer hits initially, catches up in about one minute, then about 4 points more | Measured, accepted recency trade-off; read recall is not faster for every transition. |

The controlled `cache-recall` run completed 12/12 on 0.4.469.1,
`20261009-161142-fbaac21da41a4410b05e44dcbf28ef01`.
The earlier 30-minute soak completed six episodes, 24,414 independent exact
write/read checks and all 24 post-drain oracle files. A short 120-second soak is
not a substitute for that sustained-use result.
Methods/tables: [sustained validation](history/SUSTAINED_CACHE_VALIDATION_20261009.md).

## Dedicated RAM-disk access and rejected scheduling experiments

Earlier Direct access removes volume/provider dispatch overhead for suitable
RAM-disk reads and small writes. Large writes, at least 512 KiB, use the Standard
path because the provider can overlap queued writes. Earlier measurements were
17.4 versus 25.4 GB/s for Direct versus Standard Q8 large writes; after selecting
Standard for them, Direct-mode disks reached about 24.4 GB/s. Small Direct I/O
retains its advantage. These choices predate PR #8.

Earlier attempts to reuse asynchronous copy offload for RAM-disk reads also
predate PR #9 and were removed: per-CPU queued Direct copies gave 20.8–22.3
GB/s versus about 26.2 GB/s inline, and system-worker offload gave no gain or
13–17 GB/s. A historical sample profile had 21% busy samples spinning between
copies versus 28% copying. Moving the same copy to another thread does not
automatically improve throughput.

Earlier provider tuning on signed 0.4.354.1 also found no throughput difference
between three/four workers or 50/200 microsecond polling duration. Streaming
non-temporal copies gave no worker gain and slowed split reads; 512 KiB split
chunks did not engage a helper in time. These negative findings belong here as
well as in the historical tracker. They are not a matched experiment on 514,
but a new poll-duration or copy-size experiment needs a specific new reason.

The new PR #9 experiment instead used a bounded shared queue and three sleeping
whole-read executors. These executors copied each request without the retained
provider split helper. Scheduling **and copy granularity** changed together;
the result rejects this combined implementation, not every possible scheduling
improvement or the earlier cache-copy optimizations. Its 54-window same-build comparison completed on
0.4.503.1, run `20261010-034755-884b991306b245e7b91b3aea82fa9351`:

| Direct large-read shape | Retained synchronous median MiB/s | Experimental queue median MiB/s | Change |
|---|---:|---:|---:|
| 1M Q1T1 | 24,729.50 | 23,834.47 | −3.62% |
| 1M Q8T1 | 23,977.50 | 21,268.10 | −11.30% |
| 1M Q2T4 | 42,934.37 | 21,628.17 | −49.62% |

All large-read ranges fall below their synchronous controls. Small reads bypass
the experiment and remain within variation. Q8/four-reader synchronous medians
are **25.14/45.02 decimal GB/s**, versus 22.30/22.68 with the queue. Exact byte
guards, whole-file hashes, telemetry and restoration passed. The hidden installed
tray remained resident during this comparison, a recorded preflight limitation;
it was closed before later runs. No trace ran during this A/B.

**Decision:** remove the new queue engine and retain synchronous split reads.
Later superseded: asynchronous split reads keep the split and are accepted (above).
The larger four-reader result demonstrates aggregate available bandwidth; the
readers use independent cursors and may overlap source data. It does not prove a
single sequential reader can reach that score. Preliminary enclosing CPU
profiles show about 35% helper `WorkerMain` versus 47% memcpy at Q8T1, and about
4% versus 70% with four readers. These function shares motivate coordination
profiling but do not prove every helper sample is spin. A new design needs a
repeatable gain, preserved Q1/small/multi-reader controls and lifecycle checks.

Complete reference: 36/36, run
`20261010-031011-5ff0b7da045a4b559abcc5cef68060b1`, with ETW enabled; traced scores
are diagnostic, not the acceptance baseline. Full results and compatibility
reservations: [RAM scheduling](history/RAM_READ_SCHEDULING_20261010.md).

## RAM-disk affinity and source-lane controls (before asynchronous reads)

These controls showed the single-reader gap was not caused by affinity or source
lanes; asynchronous split reads later closed it.

The plan-114 focused campaign on unchanged signed 0.4.514.1 completed **6/6
phases, 81 retained correctness checks and 30 RAM measurement windows**, run
`QueueCache-Campaign-20261010-124559-697930c96ddb4e57a97924a301d5a7e3`,
12:45:59–13:03:20 UTC (**17 min 21 s**). Managed preview code matches `4307679`;
Windows Release host contracts and Debug/Release CI pass. Loaded filter/provider
SHA-256 are `C8856CCD2DABAC66B9648FBFE6CF0A3697A2F2EB4CA8D936806E683E6348B764` /
`8BC6DF3260FA77B447E7DF737800370706A10D324F345FC2A4C6A5AE88EA9A8C`.
The DiskSpd hash is the unchanged bundled 2.2 hash in the measurement rules above.
Copy/native code stays fixed; CPU Normal, memory default 5 and XML I/O hint 3,
three-second warmup, ten-second score, no ETW or Desktop workload. Every entry
is the median and full range of three alternating repetitions. GB/s is decimal.

| Read shape | Default affinity GB/s (range) | Unbound GB/s (range) | Median change |
|---|---:|---:|---:|
| 1M Q1T1 | 26.48 (26.10–26.87) | 26.36 (26.04–26.48) | -0.45% |
| 1M Q8T1 | 25.84 (25.59–26.12) | 26.30 (25.89–26.53) | +1.77% |
| 1M Q2T4, overlapping | 38.10 (37.61–38.60) | 39.48 (38.01–40.13) | +3.63% |
| 1M Q2T4, separate lanes | 37.97 (37.00–38.53) | 39.04 (38.31–39.54) | +2.83% |
| 4K random Q1T1 | 1.755 (1.742–1.761) | 1.741 (1.733–1.747) | -0.76% |

**Decision:** no production change. Removing affinity adds only 1.77% to the
single-submitter Q8 median, with overlapping ranges. It does not turn 26 GB/s
into the four-submitter score. Removing shared source blocks changes four-reader
medians by −0.34% with default affinity and −1.11% unbound, also overlapping ranges.
That is evidence against shared source locality explaining most of this gap for
this workload. Four separate readers still reach 37.97–39.04 GB/s. These are
matched controls; the older 45 GB/s reference had different driver/launch/warmup
conditions and is not a release comparison with this run.

The retained Direct path performs its large copy before completing the request
([transfer](../driver/qcache/ramdirect.cpp), [completion](../driver/qcache/driver.cpp));
configured Q8 does not guarantee eight copies overlap from one submitter.
Together with earlier function profiles, this makes request-level concurrency
and split-helper handoff/coordination the next candidates. That is a source-based
inference, not a completed causal attribution. No faster cache-copy loop was
ported into the RAM disk here. The rejected queue changed both scheduling and
whole-copy granularity; a future experiment should keep `CopySplit` fixed first.
Cache BelowNormal × affinity interaction remains a separate open diagnostic.
The completed `-n` control changes DiskSpd thread affinity. Provider copy helpers
still use fixed CPU assignments in `driver/ramdisk/transfer.cpp`; their placement
and handoff cost were not independently varied. The next attribution must also
distinguish useful helper work from polling/coordination before selecting a change.

All thirty raw XML/settings, scored-byte calculations, owned priority readbacks,
1 GiB before/after hashes, native identities/counters, readiness and enclosing
intervals were inspected. Every window has zero image/backing read/write attempts;
maximum RAM sample gap is **1.021481 s**, below the unchanged two-second contract.
Priority application completes within **0.0227392 s**. All 93 owned process exits
are zero; 22 control readiness handshakes have maximum gap **0.246594 s**. All three
RAM fixture groups return the original resource IDs and 2 GiB reservation. Final
fault tests record exactly three accepted injected errors and restore a healthy
lab; no later benchmark follows them.

The original Q: runtime, timing/recall/backoff and saved profiles restore exactly,
with zero pending bytes or error. The owned NTFS lab is detached and the installed
tray resumes in the signed-in session. Private immutable raw archive SHA-256:
`7AF11BA9579E1C73A52F737D3B149308A117916502B8E3C5D614223DFE6BC4F3`.
The earlier dirty-preflight and 4/6 interrupted campaign attempts remain separate;
none of their checks/scores are used to complete this run. The explicit bounded
phase preparation fix preserves strict clean capture and refuses faults before
controls; [orchestration evidence and failure history](TESTING.md#3-running-tests-one-command).

## Caller-path backoff: retain the production default

An overlap can send the next 256 eligible candidates to the ordered worker.
PR #9 appends candidate/first-decline counters and a runtime lab-only 0..256
control. Mandatory control, queue, active-owner, worker and offload checks still
apply. Default remains 256; no persistent product setting was added.

Two complete 24-window comparisons used the same signed 0.4.514.1 native driver,
managed preview `1788970`, normal priority, timing off, 2 GiB Fast/Deferred cache
and fitting 1 GiB files. Three alternating repetitions per mode, with independent
concurrent and persisted byte oracles after scoring:

| Filesystem / shape | Default 256 MiB/s | Lab 0 MiB/s | Change | Interpretation |
|---|---:|---:|---:|---|
| NTFS mixed 64K Q1, 70% reads | 8,897.40 | 8,811.69 | −0.96% | Ranges overlap. |
| NTFS mixed 64K Q8 | 7,685.96 | 8,090.55 | +5.26% | Ranges separate; more caller work. |
| NTFS random 4K read Q1 | 1,242.31 | 1,240.75 | −0.13% | Ranges overlap. |
| NTFS sequential 1M read Q8 | 35,992.91 | 28,370.53 | **−21.18%** | Ranges separate; shorter backoff loses throughput. |
| ReFS mixed 64K Q1 | 9,224.79 | 9,264.40 | +0.43% | Ranges overlap. |
| ReFS mixed 64K Q8 | 7,783.93 | 8,078.71 | +3.79% | Ranges separate; more caller work. |
| ReFS random 4K read Q1 | 1,335.16 | 1,346.03 | +0.81% | Ranges overlap. |
| ReFS sequential 1M read Q8 | 33,832.97 | 35,027.47 | +3.53% | Ranges overlap; no established gain. |

All 48 fitting windows staged zero lower reads. No capacity waits were observed.
More requests served on the caller is not itself a speed criterion. These are
within-filesystem mode comparisons, not a controlled NTFS-versus-ReFS ranking.
Retain 256. A size/filesystem-specific policy is a future hypothesis and requires
a new same-build A/B plus ordering/flush/capacity qualification; it is not already
implemented or proven by this table.

Exact runs: NTFS `20261010-043439-25f6f295740048c3a933ecc726d9f4f0`;
ReFS `20261010-065536-e440846e99264ddb86145783b78c8615`.
Both independently restored disabled/zero-budget lab volumes and backoff 256.
Loaded filter SHA-256
`C8856CCD2DABAC66B9648FBFE6CF0A3697A2F2EB4CA8D936806E683E6348B764`;
provider `8BC6DF3260FA77B447E7DF737800370706A10D324F345FC2A4C6A5AE88EA9A8C`.
Full ranges, routing and evidence: [caller comparison](history/CALLER_BACKOFF_20261010.md).

## Partly cached requests: accounting first

Today a staged ordinary read with one missing block reads the whole request,
up to 16 MiB, from the lower device. RAM overlap can therefore waste disk bytes.
PR #9 adds request/byte/mixed-request/already-cached-byte counters before lower
submission, including failed lower attempts. It does not change read behavior.

The plan-105 patterned NTFS scenario completed ten byte/accounting checks on
0.4.495.1, timing off/on: a 1 MiB partly cached request held 131,584 cached bytes
(12.55% of its lower traffic); full misses held zero; cross-sector/full hits and
overwritten hits staged nothing. Every byte matched and restoration completed.
Exact run `20261010-020527-79bbee34e94c47faa71b8b062523627a`.
This shaped percentage is not a measured speed-up. All fitting caller-comparison
windows staged zero bytes, so they do not establish real churn waste.

The focused plan-113 `cache-sustained` follow-up completed 6/6 on signed
0.4.514.1, run `20261010-075924-c08eff16b204482fb371f9d09265f56e`,
07:59:24–08:09:02 UTC. It uses a 512 MiB Fast/Idle cache, a 1 GiB random 64 KiB
70/30 mixed file at Q8, six 20-second mixed episodes, twenty seconds natural idle
per episode, then Q1/Q8 1 MiB rereads of a fitting 256 MiB file. Read recall 1,
copy flags 3 and timing off; no clears/reallocations between episodes. Q8 follows
Q1 recovery rather than independently starting from the same churn state.

| Episode | Mixed throughput MiB/s | Mixed lower-byte overlap | Q1 recovery MiB/s | Q1 lower-byte overlap | Q8 recovery MiB/s |
|---|---:|---:|---:|---:|---:|
| 1 | 20.43 | 0.000% | 9,803.30 | N/A: no staged reads | 34,511.99 |
| 2 | 39.20 | 0.000% | 8,842.36 | 0.797% | 35,422.08 |
| 3 | 54.84 | 0.378% | 8,206.19 | 1.186% | 33,317.88 |
| 4 | 61.80 | 0.762% | 7,224.88 | 0.562% | 32,282.22 |
| 5 | 78.92 | 1.162% | 6,924.28 | 0.283% | 31,600.60 |
| 6 | 85.91 | 1.185% | 6,438.26 | 0.151% | 30,531.77 |

Weighted across all six mixed boundaries: 51,854 staged attempts / 3,398,303,744
lower bytes, 686 partly cached requests and 26,779,648 already-cached bytes:
**0.788% of staged lower traffic**. Across Q1 recovery: 1,398 attempts /
1,459,617,792 bytes, ten partly cached requests and 6,553,600 cached bytes:
**0.449%**. All Q8 recovery windows staged zero bytes. Percentages use sums of
bytes, not averages of per-episode percentages. Boundaries enclose process/oracle
activity, not just DiskSpd's score or a file-attributed trace.

All 18 mixed/reread XML outputs, counter boundaries, readiness/coverage and native
identity were inspected. Maximum telemetry gap: 0.752202 seconds. All 215 owned
process exits are zero; 1,539 independent concurrent 1 MiB write/read checks and
all 24 disabled-cache persisted oracle files passed. Original zero-budget lab
state and backoff 256 restored with zero errors/dirty/in-flight bytes. Raw archive
SHA-256: `6C0C5D819CD52F5A16C1FEC035C738F49E03A7A0A70F0C2840E8535F86104BAC`.
This has 120 seconds of mixed activity and is an accounting smoke, not another
30-minute sustained-use acceptance or a mode-comparison performance A/B.

**Decision:** do not implement missing-span reads for these measured shapes.
Even eliminating every overlapping byte would remove under 1% of aggregate
staged traffic, before extra lower-request/synchronization costs. That byte
fraction is not an exact throughput bound: storage latency, request count and
coalescing also matter. Other shapes, especially smaller scattered updates followed
by larger reads, could differ; reopen only when representative evidence shows
material overlap. The patterned 12.55% scenario proves counter correctness and
does not override the natural-churn result.

Missing-span reads should be considered only if real overlap is material.
Measure run counts and merge slack as well as saved bytes: several fragmented
small lower requests can cost more than one larger request. Preserve pins,
write ordering, partial-sector validity, error and cancellation semantics.
Full-hit/full-miss controls must not regress. Paging reads remain deliberately
outside ordinary cache admission; Windows' file cache has its own coherence
boundary. Historical unsafe paging admission is not reopened by these counters.

## Writes, stream placement, maps and ideas not pursued

| Item | Finding | Decision / limit |
|---|---|---|
| Complete PR #8 write reference | 72/72 on 0.4.476.1; cached random medians +1–7%, sequential Q1 +29–32%, Q8 −5–14% versus the historical 0.4.426.1 run | Q8 ranges overlap; disk-only Q8 also falls 243.46 → 84.92 MiB/s. Cross-day difference does not establish allocator/PR causality. Retain the complete current reference. |
| Contemporary RAM-only writes | Deferred/Eager Q8 20,603/20,491 MiB/s within 0.5%; PR #8 same-build comparison within 0.6% of its predecessor | Different payload contract (`-Z1M`) from the 72-case matrix (`-Zr`); do not substitute one for the other. |
| Coalesced write copies | About +1.2–1.8% Q1; −2.7% to +8.4% Q8 noise; did not close the historical gap | Removed. No demonstrated benefit warranting changed placement/copy logic. |
| Multiple separate streams | 37/37 matrix: four streams at total Q8 about 2.8% below one for writes, 7.8% for reads with overlapping ranges; 99.6–100% in-chunk order before scoring | Per-stream open chunks not justified. These measurements do not attribute the difference to allocation. |
| Idle relocation/defragmentation | Earlier post-churn upper bound about 5–7% Q1/10–15% Q8; later slow reread was missing data, despite good resident order | Not built. Read recall addresses the measured admission problem without moving pinned/draining data. Reopen only for a measured resident-placement bottleneck. |
| Native full-map polling, 2 GiB | Off/2 s/250 ms medians 1,389.55/1,383.92/1,399.04 MiB/s; steady map about 1.4–1.6 ms | Two-second polling has no measurable throughput cost; ranges overlap. |
| Native full-map polling, 4 GiB | Off/2 s/250 ms medians 1,353.10/1,358.05/1,304.80 MiB/s; steady map about 2.4 ms | Two-second polling again has no measurable cost; fast cadence about 3.6% lower with nearly overlapping ranges. |
| 100 ms UI refresh/full-screen map | Frontend contracts and actual Windows controls pass; shared sampling prevents overlapping map requests, hidden/minimized views release demand | Rendering cost at 100 ms is unmeasured. Default remains one-second app refresh/two-second map refresh. 32 GiB fixtures validate display/grouping, not a real 32 GiB allocation on the 16 GiB VM. |
| Map colors/order percentage | Darker means fuller total occupancy, lighter means partly used. Orange means some dirty data; order measures neighboring cached disk blocks inside a chunk | Color is not temperature or age; order is not whole-file fragmentation or proof of adjacent physical pages. Grouped squares can represent multiple chunks. |
| Large pages | Shuffled ascending chunks were within 4% of fresh; no controlled large-page A/B or TLB profile | Deferred, not disproven. No current evidence justifies adding allocation complexity. |
| Old-build downgrade | Old installer cannot interpret newer provider state and refuses safely | Do not force reinstall to obtain a historical comparison. Use same-build diagnostic switches for future causal A/B. |

The complete 24-configuration write table and exact raw identity are preserved in
[write reference](history/WRITE_PERFORMANCE_20261010.md), run
`20261009-220533-0439cff369ce45c8b577e23951b77317`, 22:05:33–22:59:38 UTC.
Scoring is ten seconds per case; preparation/drains, including 45–53 second
flushes, account for much of the roughly 54-minute runtime.
Map/concurrency methods and full ranges are in
[sustained validation](history/SUSTAINED_CACHE_VALIDATION_20261009.md).

The refreshed README uses actual CDM 9.0.3 GUI runs on 0.4.476.1: Default
profile, five 1 GiB passes, better of two runs per column, normal priority,
Verifier off and `drop-clean` before each complete run. All columns were measured
the same evening without a restart. These are decimal MB/s, not medians from
the verification matrices:

| GUI row, MB/s | Q: cache off | Q: 2 GiB Fast cache | 4 GiB RAM disk |
|---|---:|---:|---:|
| SEQ1M Q8T1 read | 594 | 39,492 | 25,353 |
| SEQ1M Q8T1 write | 112 | 21,504 | 23,266 |
| RND4K Q1T1 read | 9.0 | 1,292 | 1,641 |
| RND4K Q1T1 write | 1.2 | 1,031 | 1,317 |

On this Ceph-backed virtual disk, uncached sequential writes changed from
245 MB/s that morning to 112 MB/s that evening while cached/RAM-disk results
stayed within 6%. Historical comparisons need a contemporary disk-only control.
Leftover cached test data previously reduced reads to 5–20 GB/s by causing
misses; clearing clean data prevents this preparation confound. Windows startup
work and competing tests/UI must finish before scoring. The screenshots are
actual captures; frontend README images are generated by the desktop tests.

## Correctness qualification

**Issues #14 / #15, plan 126:** signed 0.4.675.1 completed the focused Driver
Verifier campaign `QueueCache-Campaign-20261013-175418-7aa42644d0cc4a90912f7fb85199ff9c`:
six phases, 98 correctness checks, all PASS, restoration RESTORED. This includes
12 rounds of 16 overlapping cold/partial readers, exact byte checks and a
post-drain/drop-clean check for leaked pins. The 304-second stress window checked
168,502 reads on V: and 224,676 on W:, cancelled 185,091 and 243,829 overlapped
requests, exercised every chaos action at least seven times per volume, and
created and removed 23 RAM disks. Fault and Retry passed at drain parallelism
1, 2 and 4; the shared reservation returned exactly to its starting value.
The native filter hash was `4EA5DCACCFFC09DB362C6BB5361764676D5090300DFE0737777142812549F744`,
the RAM provider hash `A5510BEB87D3190739021A3A4D99BD931259831F795BED7DF86E2064415D2905`;
Verifier flags `0x209bb` targeted both. Final performance qualification on
0.4.687.1 was completed under plan 132; this paragraph records the older plan-126
campaign only.

The first non-Verifier stress attempt on 0.4.677.1 passed the 302-second busy
window but stopped at the first Retry check with 8 KiB dirty. Between the faulted
and retried snapshots, 16 KiB of new writes were admitted and 56 MiB plus 8 KiB
drained; the original dirty set was 56 MiB. The fixture had not flushed lazy
filesystem metadata around its closed handles. Plan 128 adds those filesystem
flushes and a retained-byte drainage check, while preserving the zero-dirty,
zero-in-flight and stable-identity requirements. The failed attempt is retained
as incomplete (`20261013-181833-229f91cc19354cee801dd03e63e11da1`); restoration
succeeded. The corrected plan-128 fixture completed on signed 0.4.679.1
(`QueueCache-Campaign-20261013-225440-cf15f15164264cbcb2b63c3886f8b2c8`), all five
checks PASS, restoration RESTORED. Its 307-second busy window checked 380,986
reads on V: and 603,948 on W:, exercised every chaos action at least three times
per volume and created and removed 13 RAM disks. At drain parallelism 1, 2 and 4,
each retry drained at least the original captured dirty bytes and left zero dirty
and in-flight bytes; every region then matched from disk after cache release.
The shared reservation returned exactly to 2 GiB. The loaded filter hash was
`71BB75DC99CBF6B138AC30F8448961CC17711C76138AC8D68F8E16623EAA2955`, provider
`5236851804D9A016017C7B4145865DB8271B7E2BDC038BEB82FEBCA924083A4A`.

**Plan 120 stress suite.** `stress-races` runs both lab NTFS volumes at once:
writers of 4 KiB, 64 KiB, 1 MiB and 4 MiB blocks and an overlapped writer/reader
whose requests are cancelled at random, every read checked per 4 KiB sector
against the versions written, while a chaos actor per volume flushes, resizes,
tears down, drops clean data, slows the disk, injects transient allocation
failures, applies refused and rolled-back settings and switches to Strict, and
owned 2 GiB RAM disks are created and removed against the same reservation. It
then faults each drain parallelism (1, 2, 4) with dirty data, requires reads to
fail rather than return data, retries, and reads every byte back from disk.

Its first long runs found one real driver bug and three of its own: (1) **stale
cached data after large partly cached reads** (fixed in plan 120, see
[known issues](KNOWN_ISSUES.md#fixed-issues)): with retained writes off, a read
over 1 MiB fetched the range from disk while the dirty version of some blocks
finished draining, and kept those blocks from the older disk bytes. Under Driver
Verifier this gave 38 stale reads in 90 s, always whole 256 KiB drain batches of
an older version in 4 MiB blocks, with no unwritten (poisoned) sectors and the
disk verified correct; it reproduced with copy flags 3, so it predates the
bookkeeping change. After the fix, two 300 s busy windows under Verifier checked
about 390,000 reads with no stale data. (2) A RAM churn disk was stopped with a
generation read before its oracle was written. (3) The "refused budget" request
was sized from the free reservation, which a concurrent RAM disk removal could
enlarge, so the driver correctly accepted it. (4) Weighted random actions left
rare ones out of slow Verifier windows; actions now run in shuffled rounds. Wiring
it into campaigns also exposed three runner gaps, now fixed: restoration accepted
deliberately raised lab-fault error counts only for `ordering-faults` (and
`verify-recover` never did); later phases compared against the preflight baseline
instead of the one updated after such errors; and the final capture of the backing
volume could catch the lab image's late writes right after its drain.

**Final Driver Verifier run:** the focused campaign with `stress-races`
(`QueueCache-Campaign-20261011-214930-07f2ed10a5cf42978fbf1cc002c0db35`, signed
0.4.601.1 drivers with the stale-read fix, runner from the final source) completed
**6/6 phases, restored**: partial reads, paging coherence, policies, pressure,
`stress-races` and ordering faults all PASS. Its busy window ran 312 s with
254,894 (W:) and 182,739 (V:) verified reads, 268,875 and 198,640 cancelled
overlapped requests, every chaos action at least three times per volume and 11
RAM disks created and removed; fault and Retry passed at drain parallelism 1, 2
and 4, and the shared reservation returned exactly to its start. Later builds
change only managed code. Without Verifier on the final signed 0.4.604.1
(`QueueCache-Campaign-20261011-221957-8b19fe05c36b4caabb02c12e5149f237`, COMPLETED,
restored) the 306 s window checked 431,437 (W:) and 342,337 (V:) reads, cancelled
362,277 and 308,174 overlapped requests, ran every chaos action at least seven
times per volume and churned 23 RAM disks, with every check passing.

Current: the final `release-performance` regression on 0.4.583.1 passed all 118
retained correctness sub-checks (see the accepted change above). Earlier, the
signed 0.4.514.1 native driver and managed `1788970` preview completed
these maintained plan-113 scenarios on the owned NTFS lab V:. Each is an
independent run with one outer scenario, not a combined performance matrix.
Every inner check is PASS; no check was skipped. Raw reports, worker replies,
native hashes, ownership/exits, control/restoration traces and exact original
settings were inspected. Each has four successful owned-process exit records.

| Suite | Inner checks | Exact run ID suffix | Runtime UTC |
|---|---:|---|---|
| `partial-read-accounting` | 10 | `20261010-081109-aad11964b4d8470e9573e64f19b7787a` | 08:11:09–08:11:15 |
| `paging-coherence` | 8 | `20261010-081454-87a8b7c777be4ea19ccdc2c653758b83` | 08:14:54–08:15:07 |
| `policies` | 42 | `20261010-081712-d0b3498f7f144372871f769fc7c0ec91` | 08:17:12–08:18:52 |
| `pressure` | 13 | `20261010-082056-811b8c23b4c4417088273d84fdf83686` | 08:20:56–08:21:42 |
| `ordering-faults` | 8 | `20261010-082430-0afa9979205a4ea6b2988c9ad7458685` | 08:24:30–08:24:54 |

Coverage includes partial sectors, mapped/cached coherence, observed and gated
old-write ordering, parallel pinned copies, zero-lower-I/O fitting admission,
retention/policy variants, capacity backpressure, failed/short drains, cancellation,
allocation retry/exhaustion, release under load and paging-write failures. The
fault suite deliberately increments the lab error counter by three; final state
is non-faulted, with last error zero. Other suites have zero errors. All restore
disabled/zero-budget state, original options, no pending bytes and backoff 256.
This qualifies retained behavior at default 256; it does not qualify a new
request-size policy or the removed queue's lifecycle paths.
Immutable five-run raw archive SHA-256:
`3E475F93750E588D0A172DF09F4F5C991384B77027D763AB9CA642E789900DFD`.

Final cleanup detached both owned lab VHDX files and resumed Q: runtime-only on
its original active 2 GiB Fast/Idle configuration, with no errors or pending bytes.
Saved profile text matches the before capture exactly; the installed tray is
running again. Private evidence and lab files remain preserved.

## Recorded verification time

These selected completed runs total approximately **8 hours 38 minutes**. This
is measured run time, not a reconstruction of all engineering work or every
earlier investigation. Installation/restarts, CI, coding, analysis, incomplete
attempts and inactive conversation gaps add time. The NTFS/ReFS gap from 04:54
to 06:55 was not two hours spent executing that benchmark.

| Completed run | Wall time |
|---|---:|
| PR #8 write matrix, 72 windows | 54 min 05 s |
| Original partial-read accounting | 7 s |
| RAM reference, 36 windows | 15 min 29 s |
| RAM queue comparison, 54 windows | 22 min 37 s |
| NTFS caller comparison, 24 windows | 19 min 31 s |
| ReFS caller comparison, 24 windows | 20 min 08 s |
| Complete priority retry, 36 windows | 19 min 03 s |
| Traced priority subset, four windows | 2 min 38 s |
| Focused churn, six episodes | 9 min 38 s |
| Five retained-driver scenarios | 3 min 09 s |
| Plan-114 campaign smoke, two phases | 59 s |
| Plan-114 focused campaign, 81 correctness checks + 30 RAM windows | 17 min 21 s |
| Controller/restoration retry smoke, two phases | 1 min 07 s |
| Plan-116 RAM attribution, 81 correctness checks + 3 traced windows | 7 min 38 s |
| Plan-116 cached priority/affinity, 81 correctness checks + 36 untraced windows | 25 min 02 s |
| Plan-117 coordination diagnostic, nine windows | 4 min 33 s |
| Plan-117 controller smoke, two phases | 1 min 18 s |
| Wake-up smoke, two phases | 1 min 03 s |
| Plan-118 asynchronous-read comparison, 18 windows | 8 min 50 s |
| Plan-119 revision comparison, 18 windows | 8 min 52 s |
| Plan-119 lifecycle checks, with / without Driver Verifier | 1 min 10 s / 55 s |
| Final `release-performance` regression, 11 phases / 106 cases | 1 h 56 min 23 s |
| Plan-120 cached-read attribution, 18 windows | 9 min 45 s |
| Plan-120 RAM write reference, 36 windows | 6 min 43 s |
| Plan-120 streaming-store comparison, 18 windows | 5 min 30 s |
| Plan-120 read-bookkeeping comparison, 18 windows | 9 min 57 s |
| Plan-120 Driver Verifier campaign with `stress-races`, 6 phases | 10 min 56 s |
| Plan-120 `stress-races` without Verifier | 5 min 55 s |
| Plan-120 final `release-performance` regression, 11 phases / 106 cases | 1 h 47 min 34 s |

The original priority run's main scores total about six minutes within its nineteen-minute
run. Preparation, residency proofs, warmup, worker/process ownership and
restoration account for the rest. The write matrix includes explicit 45–53
second drains between some ten-second scores. A single command removes repeated
agent orchestration; it does not remove required disk preparation or durability
checks. The [campaign plan](TESTING.md#3-running-tests-one-command) records phase times and
delivers terminal events so those costs become visible without periodic agent
polling.

The latest two plan-116 campaigns together take **32 min 40 s**. The cache
priority/affinity phase accounts for 20 min 29 s of its 25 min 02 s campaign;
the RAM trace phase takes 2 min 40 s of its 7 min 38 s campaign. The surrounding
policy/pressure checks, preparation and restoration explain much of the remaining
time. Tool/bootstrap failures and engineering/CI time remain separate from these
completed measurements.

Not counted above (preserved separately; none contributes a result): the Driver
Verifier run that bugchecked (0x3B, fixed before measurement), a Verifier run and
a lifecycle run stopped by over-strict activation checks, one comparison refused
at start because Q: still had unwritten data, and the first final regression,
which stopped after 33 minutes in the sustained phase: a 197 GB resident reread
saw six background metadata reads (4.5 KiB) on Q:, most likely the Everything
file indexer that had restarted at sign-in. Everything and Steam were closed and
the complete regression rerun passed. Plan 120 adds the first RAM write reference run
(stopped by its oracle rewrite bug), 13 stress, Verifier and stale-data diagnosis
runs that found and fixed the issues described under
[correctness qualification](#correctness-qualification), and their recoveries.

## Current decisions and when to repeat tests

| Topic | Decision | Reopen when |
|---|---|---|
| Asynchronous split RAM-disk reads | **Accepted, default adaptive** (1M Q8T1 +79.84%; Q1 and four readers unchanged). | Changes to provider workers, the Direct read path or chunking; a `ram-read-async` or `ram-read-async-lifecycle` regression. |
| Cached read-hit bookkeeping (copy flag 4) | **Accepted, default on** (1M Q8T1 +7.92%, Q1T1 +4.52%, four readers +3.46%). | Changes to the read-hit path, pinning, recency lists or the slot index; a `cache-read-bookkeeping` regression. |
| Large RAM-disk writes | **No speedup available on this VM**: four writers ≤ +5% over one; queued writes already exceed user-mode copy rates into cold memory. Split writes (September) and streaming stores (plan 120) both rejected. | Different hardware with a measured gap between `ram-write-reference` disk writes and its memory-copy rows. |
| Inline split RAM-disk reads | Retained for queue depth 1, several submitters, paging and synchronous callers. | As above. |
| Shared whole-read queue (and the two earlier whole-read offloads) | Removed: each gave a whole 1 MiB read to one worker. | Not as such. |
| Caller backoff | 256. A selective NTFS/ReFS policy is closed without an experiment: best measured gain +5.26% (NTFS mixed Q8), +3.79% (ReFS), at the 5% bar, while touching ordering/flush paths. | New workload evidence of a material gain. |
| Missing-span reads | Not built: overlap 0.788% / 0.449% of lower traffic in churn. | Representative evidence of material waste. |
| Low-priority cache slowdown | Benchmark-setup effect; launch benchmarks at normal priority (Task Scheduler priority 4). | An observed normal-priority problem. |
| 100 ms map refresh cost | Not measured further at the owner's request; native map reads take milliseconds. | Owner request. |
| Read misses one at a time | **Qualified for throughput, p99 and equal-work tails** (plans 131/132/141, issue #14): arrival order retained, ordinary misses offloaded; latest October 15 same-build repeat: cold random 9.95×, sequential 3.13×, all pairs up and ranges separated, every cold p99 improved, no resident pair loses 3%. The 568.7 ms fixed-window maximum is not reproduced with equal work: the complete 24-case fixed-operation run `20261014-230805-92a9f1ac4191423cbd05b571db4595b2` improved every cold-random pair (+541% to +617% IOPS, p99.9 −92 to −148 ms, no operation above 500 ms) and showed no flag-driven control loss (resident differences follow run order). Both arms use the new pool and queue order; the pool size was not varied. | Changes to read pinning, miss admission, queue order, offload workers or the measurement contract; a backing store with a different latency profile; evidence that the 16-thread pool starves another workload. |
| 40,000-file create/copy/read/hash slowdown | **Environmental: Windows Defender real-time scans, not the driver.** The complete plan-134 same-build matrix (`20261014-175457-b7459c972da24d5a809c98512e31245b`) measured 160.07–170.45 s read+hash in every arm including cache-off, with no QueueCache read hits or writes. A diagnostic case (`20261015-000302-165f7519bbf14c7fac6b28b0e648ef16`, not decision-grade) found `MsMpEng.exe` using 156.5 CPU-seconds of the 164.2-second phase and 40,024 `OnOpen` scans (151.6 s) in the tree worker, with zero Q: disk I/O. Defender stayed enabled for that diagnosis; no exclusion or driver change. The owner later disabled Defender and Search on the dedicated VM (2026-10-15); one subsequent observation completed read/hash in 6.86 s with every hash verified. The cache path also changed, so this is not an isolated driver gain or a complete matrix. Why the 0.4.604.1 session took 4.9 s is unresolved (Defender state was not recorded). `tree-build-decision` was removed unrun. | A recorded Defender-off or excluded baseline that shows a driver-dependent cost, a tree phase that stays slow with Defender scan time near zero, or a new driver path in file reads. |
| Random-read latency tails | **Accepted** (plan 141): 24/24 fixed-operation cases (`20261014-230805-92a9f1ac4191423cbd05b571db4595b2`) show flags 15 cold-random p99.9 60.7 versus 191.7 ms, maximum 123.5 versus 271.6 ms, >100 ms counts down in every pair, none above 500 ms, with equal lower-read counts. Resident random/sequential and fitting-write controls had zero misses and no flag-driven loss; run order explained their pair differences. | A repeated order-balanced control loss of 3% or more, a different backing store, or an observed starvation tail in another workload. |
| Drain batch size and parallelism | **Eager 2048 KiB × 8 is the default since plan 164** (with read-aware limit 2 and TRIM mode 3; [results](#final-164)). Same-window A–B–A: 292.4 / 90.7 MB/s against PrimoCache 261.0 / 52.5 on the two #15 workloads at matched anchors, a concurrent disk reader 757 against 170 IOPS. Under it, Idle 1024 KiB × 4 was the default from plan 125 (plan 132 and 142 explicit-drain gains; [history](history/README.md)). Saved profiles keep their settings. Strict caches also inherit the new default; Strict boundaries under Eager were not measured separately (#16). | Storage with a different latency profile (hard disks and SATA SSDs were not measured), a repeated control loss under the release campaign, or Strict measurements. |
| Next drain improvement | **Done for #15 (plans 159–167).** Eager 2 MiB × 8 with read-aware 2 and asynchronous TRIM is the default and beats a same-window PrimoCache on both workloads ([results](#final-164)). Rejected on the way: aligned gather (plans 155/156), Eager 2 MiB × 4 and read-aware limit 1 (plans 161/162). The uncached Q1 miss path, alone 20–35% slower than Primo's, is a separate improvement. | A workload where Primo leads at matched backend latency, or the Q1 miss-path issue. |
| TRIM while draining | **Fixed; default since plan 164.** A TRIM waits only for overlapping in-flight drain writes (native 0.4.766.1, plans 159/160); TRIM mode 3 adds an index lookup and an asynchronous lower TRIM with a range fence (plans 162/163). Native 0.4.776.1 also keeps the caller path while a lower TRIM is outstanding (`87de299`). Under Verifier `trim-cache` passed 12/12. The fence's holding path is unexercised, because NTFS orders a file's writes after its own TRIM. Production 0.4.687.1 still waits for every in-flight write. [Known issue](KNOWN_ISSUES.md#trim-no-longer-waits-for-unrelated-drain-writes). | A file system or tool that writes trimmed ranges before the TRIM completes. |
| Strict durability barriers | **Open weakness, not measured for the new defaults** (plan 121, issue #16): write-through and flushes drain the whole cache while holding all writes; stalls of 100 s or more after random write bursts. Plan 143 `strict-impact-decision` failed twice at its first seed and was removed: in Strict mode 2 application flushes and 1 write-through write, not issued by the seed worker (no write-through flag, no flush); source unattributed, most likely NTFS metadata, drained 280,502,272 of 536,890,368 seeded bytes mid-seed, so a large dirty backlog does not survive between such barriers. No Strict boundary latency was collected and no driver or default change was made for Strict. | A Strict design that bounds barrier cost (issue #16), or a new Strict measurement with a seed that can hold its backlog between barriers. |
| Coordination/attribution counters | Retained, default off; one flag read when off. | When the diagnosis is needed. |

For a missing-span experiment, first record how many missing runs each partly
cached request contains. Start with a runtime default-off implementation using
the existing owned staging buffer: copy known pinned sectors, merge short gaps,
submit bounded missing ranges and fall back to the current whole-range path when
fragmentation exceeds the bound. Choose the run/merge limits from measured costs,
not the overlap percentage alone. Preserve allocation-failure fallback, short
read/error completion, cancellation, concurrent dirty overlays and pin lifetime.
Version the counter/workload contracts explicitly if a logical staged read can
submit multiple lower requests. Compare identical whole-range and missing-span
modes on partial-residency/churn shapes plus full-hit/full-miss controls, then
run partial-sector, neighboring-write, paging, ordering, drain and capacity checks.
Promote only for a repeated gain without correctness or control regressions.

For a caller-policy experiment, preserve 256 for large copies and investigate a
shorter window only for the measured small mixed shape. First separate the lab
maximum from the production-default constant, retain every owner/queue/control
gate and append attribution for the new choice. Alternate modes on both NTFS and
ReFS, including transitions between small mixed and large reads; a gain restricted
to one shape is insufficient if it damages the next workload. The current global
zero-backoff experiment is not a qualified request-size policy.

Do not repeat an established experiment merely because its result was forgotten.
Repeat a relevant regression when native/runner behavior, hardware, power/CPU
placement, benchmark binary, allocation/copy settings, workload or measurement
contract changes, or when a new observation contradicts the finding. Use the
smallest maintained discriminating suite first; request a broad matrix only
when its scope is needed. Keep failures and raw evidence immutable. Update this
file with conclusions, units, identities, limits and decisions.

The step-by-step records of the plan 115-117 diagnostics (attribution, cached
priority/affinity, coordination counters) that led to the accepted change are in
[history/RAM_READ_DIAGNOSTICS_20261010.md](history/RAM_READ_DIAGNOSTICS_20261010.md).
