# Comparison details and findings

[Comparison tables](COMPARISON.md) contain the scores. This document keeps the
methods, explanations, failed attempts and limits behind them. The original
comparison is dated October 12–14; the disabled-background-service refresh and
short drain comparisons are dated October 15–17, 2026. Cross-date scores describe
their sessions; they do not isolate a driver change or the effect of disabling
Defender/Search.

The read-miss fix is merged ([#14](https://github.com/devedse/QueueCache/issues/14)).
Larger drain defaults are also merged, but **matching or exceeding PrimoCache on
both new-file workloads remains the goal**
([#15](https://github.com/devedse/QueueCache/issues/15)). Filesystem safety after a
Fast-mode power cut is a separate open goal
([#13](https://github.com/devedse/QueueCache/issues/13)); the desired behavior is
loss of pending writes with a healthy filesystem. The results below do not certify
that behavior. Strict-mode backlog stalls remain tracked in
[#16](https://github.com/devedse/QueueCache/issues/16).

Contents: [Short write plus drain](#short-write-plus-drain-comparison-2026-10-16) ·
[Latest drain decision](#drain-decision-screen-2026-10-1617) ·
[Cache refresh](#current-cache-refresh-2026-10-1516) ·
[Focused checks](#focused-fix-checks-2026-10-15) ·
[Earlier findings](#earlier-comparison-findings) · [Problems](#problems-found) ·
[Column definitions](#cache-column-definitions) ·
[Power cuts](#power-cut-while-the-cache-is-writing) ·
[Original setup](#how-this-was-measured)

## Short write plus drain comparison (2026-10-16)

Plan 148 measures the same two new-file workloads on the same VM and Q: disk,
with each product alone, Defender/Search disabled, other indexers/launchers closed,
Verifier/timing diagnostics off and normal process priority. Every published mode
has the same temporary snapshot present and three complete repetitions; values
are medians with the full observed range in parentheses. The snapshot was removed
after verified restoration. Storage guards passed before and after each run;
no near-full warning was present at those recorded guard checks. The backend
still had existing OSD/PG/scrub warnings; their performance effect is unknown.

| Unbuffered write + explicit drain, MB/s ↑ | Direct disk | QueueCache Fast | PrimoCache Defer-Write |
|---|---:|---:|---:|
| One new 1 GiB file | **48.41** (42.63–53.43) | **52.71** (42.25–62.13) | **78.96** (72.99–84.00) |
| 4,096 new 64 KiB files (256 MiB total) | **1.25** (1.23–1.33) | **41.71** (18.36–41.94) | **57.62** (50.64–65.24) |

The large file uses sequential 1 MiB requests with four writes outstanding.
The small-file test creates 4,096 files of 64 KiB, using unbuffered 4 KiB writes
and four workers. The large file writes 1 GiB; small files write its first 256 MiB. The payload
is unique and incompressible, with no scored overwrites.
File/directory creation and closing handles are included. Windows file-data
caching is bypassed to isolate the disk caches; ordinary buffered copies can
behave differently. Shuffled file names do not establish random physical placement.

The headline includes the final filesystem flush, explicit product drain and
confirmation of zero pending data and disk queue. It measures completed work,
including Primo CLI/control overhead, rather than RAM acceptance speed. Every
file is then read unbuffered with the product paused and checked by SHA-256,
outside timing: 12,291 file checks per provider, all passed. Preparation and
verification are outside the measured interval. These are virtual-disk
observations, not power-loss durability tests. Zero pending/queue is a sampled endpoint,
not a quiet-period guarantee. In one QueueCache case, 56 KiB became dirty after
that endpoint and before untimed Disable; its source is untraced. Disable drained
it before the verification reads. The oracle therefore validates files after
that untimed confirmation, without proving their exact state at the scored endpoint.

| Observed mean backing write, KiB | Direct disk | QueueCache Fast | PrimoCache Defer-Write |
|---|---:|---:|---:|
| Large file | 988.8 | 1009.6 | 1975.6 |
| Small files | 5.0 | 341.9 | 1746.2 |

Write sizes are median per-run write-byte/write-operation ratios at the observed
physical-disk counter layer and include filesystem metadata. They help explain
batching; they are not a histogram or a measurement of physical-media writes.

QueueCache’s small-file median is 33.2× Direct, while trailing PrimoCache by
27.6%; the large-file median trails PrimoCache by 33.2%. Its large-file range
overlaps Direct’s, and the slowest small-file repetition fell to 18.36 MB/s.
The source of that variation is unresolved; these observations do not establish
a stable disk-speed ceiling or isolate batching as the only cause.

These are separate workloads from the historical burst, 8 GiB sustained-write
and buffered file-tree tests below. The unique full-GiB payload also avoids the
repeated-buffer compression opportunity in the earlier sequential writer.
Lower or higher figures across those methods do not establish a driver change.
Between controllers, fixes covered the Primo pending-counter parser and untimed
QueueCache pause confirmation; workload and scored timing stayed unchanged.
The native QueueCache driver remains 0.4.687.1. These fitting workloads do not settle
larger-than-cache throughput, general foreground interference or Strict latency.

The first complete QueueCache run preceded snapshot creation and is retained as
a diagnostic only. Published QueueCache results come from its complete repeat
with the snapshot present. The original 1 GiB small-file direct-disk pilot was
stopped when three repetitions were projected to take about an hour; the final
256 MiB small-file workload is identical for all providers. An earlier runner
attempt failed before writes due to a read-only control handle; it was fixed
and excluded. A later QueueCache attempt stopped at pause confirmation and
restored successfully; its incomplete matrix is also excluded. The rejected
state was not captured, so that failure’s original cause remains unproven.

Exact run identities and complete per-case scores are retained on the existing
[direct-disk](comparison/C00-no-cache.md),
[QueueCache](comparison/C01-queuecache-fast-fix687.md) and
[PrimoCache](comparison/C02-primocache-defer.md) pages and their linked data.
Method and invocation: [testing](TESTING.md#4-suites).

## Drain decision screen (2026-10-16/17)

This completes the short follow-up on branch `experiment/drain-batch-concurrency`.
QueueCache native 0.4.756.1 fixes the experimental small-budget allocation bug
and adds batch diagnostics; it does not change drain selection or defaults.
All QueueCache arms use that same native build and runner `d58b2e1` / plan 153.
Direct and Primo use runner `48f8966` / plan 154, whose only further change is
an unscored wait for Direct's cleanup writes to settle. Workload, payload,
ready/start handshake, scored endpoint and byte oracle are identical.

Each row has three complete repetitions of both original new-file workloads.
Numbers are median and full observed range, decimal MB/s, including creation,
unbuffered writes, filesystem flush, explicit product drain and sampled zero
pending/disk queue. Backing sizes are median per-case write-byte/write-operation
ratios, include metadata, and describe virtual-disk requests rather than media.

| Mode | New 1 GiB + drain MB/s, median (range) | New 4,096 × 64 KiB + drain MB/s, median (range) | Mean backing write KiB, large / small |
|---|---:|---:|---:|
| QueueCache 1024 KiB × 4, starting control | 57.81 (49.43–61.94) | 20.05 (18.59–24.66) | 1009.6 / 182.0 |
| QueueCache 2048 KiB × 4 | 74.66 (65.18–101.26) | 27.07 (25.42–65.67) | 2002.3 / 180.9 |
| QueueCache 2048 KiB × 8 | 84.24 (73.98–127.51) | 36.14 (34.15–49.95) | 2009.4 / 190.2 |
| QueueCache 1024 KiB × 4, ending control | 66.54 (59.86–73.39) | 42.54 (29.28–51.28) | 1015.4 / 207.0 |
| Direct disk, QueueCache absent | 47.71 (43.60–54.87) | 1.28 (1.25–1.48) | 986.9 / 5.0 |
| PrimoCache 4.4.1, shared L1 2 GiB, Native defer 10 s | 109.68 (100.31–117.36) | 54.47 (45.71–67.54) | 1939.0 / 1339.3 |

All **36 cases and 73,746 cache-bypassed file hashes passed**, with complete-run
and restoration records. Each provider ran alone on the same Q: disk with the
same temporary snapshot; provider changes used rollback to its base layout.
QueueCache used Fast 2 GiB Idle, flags 15; Primo used shared 2 GiB L1, no L2,
4 KiB blocks, Native defer 10 s, prefetch off. Timing/Verifier were off, priority
normal, Defender/Search disabled, and other background apps/service closed.
Loaded native hashes, exact run identities, allocation checks and CI are in
[performance findings](PERFORMANCE_FINDINGS.md#drain-decision-153).

QueueCache's controls changed +15.1% large / +112.2% small across grouped arms.
Its 2 MiB × 8 large-file range narrowly exceeds both control ranges, but small-file
ranges overlap; a stable small-file gain is unproven. That arm's medians remain
23.2% / 33.7% below fresh Primo. Compared with the earlier plan-148 session,
Primo changes 78.96 → 109.68 MB/s large and 57.62 → 54.47 small. Both sessions
already had Defender/Search disabled; the difference is not an isolated effect
of those settings. Direct stays near 48 / 1.3 MB/s. Ceph availability/scrub
warnings remain a storage uncertainty; capacity/near-full guards passed.

Nineteen QueueCache cases recorded 8–68 KiB newly dirty after the scored endpoint
and before untimed Disable. Their source remains untraced. Disable drained them;
all final paused states were clean and all hashes passed. These checks validate
that later state, not exact scored-instant persistence or power-loss safety.
Initial incomplete QueueCache 152 and Direct 153 attempts are wholly excluded;
their strict preparation failures led to bounded, recorded waits outside timing,
without relaxing fault/invalid-evidence rejection or reusing partial samples.

The next throughput investigation is **small-file batch formation**: 94–96% of
the 2 MiB QueueCache small-file batches ended at a missing dirty neighbor, with
software batches averaging about 210–218 KiB. This can mean clean cached data
or uncached gaps; it does not prove physical fragmentation. Classify those gaps
before one bounded gather-policy experiment. A reader-aware drain limit is the
separate responsiveness candidate: the earlier mixed-read test found a large
read penalty at eight drainers with little global lock occupancy. A lock rewrite
or further size/thread increases are not the first supported change. Defaults
remain 1024 KiB × 4; broader Strict, sustained, Idle foreground and release
qualification, and the separate power-loss issue, remain open. Historical generic
and CrystalDiskMark scores were not rerun or relabeled as 0.4.756.1 results.

## Current cache refresh (2026-10-15/16)

Defender Antivirus and Windows Search were effectively disabled; Everything, Steam and the QueueCache tray were closed, Verifier and timing diagnostics were off, and processes used normal priority. Each mode completed the entire one-repetition `compare-cache` and `compare-cache-cold` suites using the same pinned DiskSpd. These observations are not three-repetition medians or an isolated effect of disabling Defender/Search. The paused control retains the QueueCache filter and its 2 GiB reservation. PrimoCache ran with QueueCache absent, 2 GiB L1, no L2 and Defer-Write 10 s. No power-cut test was repeated.

Storage guard passed before PrimoCache; its post-run check reported Ceph near-full warnings with 131.3 GB free. The onset and performance effect were not measured. Treat its storage-dependent scores as observations under that additional uncertainty. The warnings cleared after restoration and snapshot cleanup.

Backing-disk figures use actual hypervisor counters, with roughly one-second resolution. The full settling interval is inspected for delayed writes; samples after the step are excluded. The 8 GiB payload rate includes time through the last interval writing more than 1 MB. It is a sampled virtual-disk observation, not proof of physical-media durability. Sub-second tails are below the recorder’s timing resolution.

| Mode | Hot sequential read / write MB/s | 8 GiB write app / through last disk activity MB/s | Burst tail seconds | Tree read/hash seconds |
|---|---:|---:|---:|---:|
| [QueueCache paused](comparison/C00-no-cache.md) | 572.8 / 250.7 | 259.5 / 257.4 | 0.66 | 2.90 |
| [QueueCache Fast, 2 GiB, 1024 KiB × 4](comparison/C01-queuecache-fast-fix687.md) | 35,016.8 / 20,937.2 | 267.5 / 200.1 | 6.28 | 2.84 |
| [PrimoCache 4.4.1, L1 2 GiB, Defer-Write 10 s](comparison/C02-primocache-defer.md) | 17,215.1 / 18,757.6 | 425.1 / 279.6 | 10.92 | 2.87 |

| Mode | Real CrystalDiskMark capture | SEQ1M Q8T1 R / W | SEQ1M Q1T1 R / W | RND4K Q32T1 R / W | RND4K Q1T1 R / W |
|---|---|---:|---:|---:|---:|
| [QueueCache paused](comparison/C00-no-cache.md) | <a href="images/comparison/off-refresh-20261016.png"><img src="images/comparison/off-refresh-20261016.png" width="240" alt="CrystalDiskMark, QueueCache paused"></a> | 662 / 266 | 286 / 121 | 130 / 12 | 19 / 5 |
| [QueueCache Fast, 2 GiB, 1024 KiB × 4](comparison/C01-queuecache-fast-fix687.md) | <a href="images/comparison/on-refresh-20261016.png"><img src="images/comparison/on-refresh-20261016.png" width="240" alt="CrystalDiskMark, QueueCache Fast, 2 GiB, 1024 KiB × 4"></a> | 40,591 / 20,908 | 20,203 / 13,546 | 1,764 / 1,468 | 1,306 / 1,095 |
| [PrimoCache 4.4.1, L1 2 GiB, Defer-Write 10 s](comparison/C02-primocache-defer.md) | <a href="images/comparison/primo-refresh-20261016.png"><img src="images/comparison/primo-refresh-20261016.png" width="240" alt="CrystalDiskMark, PrimoCache 4.4.1, L1 2 GiB, Defer-Write 10 s"></a> | 17,277 / 19,515 | 14,423 / 4,172 | 473 / 370 | 1,028 / 783 |

The tables below retain the original dated measurements. Full fresh scores, raw-run identities and the earlier results are on each product page and in its linked data.

## Focused fix checks (2026-10-15)

The current checks use the unchanged 0.4.687.1 native driver with Defender/Search
disabled. Read/drain pairs change only miss offload or drain settings within
each session. The earlier accepted drain result predates this disabled baseline;
its fresh repeat is incomplete. The earlier read screen used 0.4.685.1; these are controlled
behavior comparisons, rather than a rerun of every historical product workload.
The generic product and CrystalDiskMark rows below retain their original scores.

| Check | Earlier observation | Current observation | What it establishes |
|---|---|---|---|
| Cold random 4 KiB Q32, offload off → on | 10.435 → 98.054 MiB/s | 8.802 → 87.568 MiB/s | 9.95× current gain; all three pairs improve throughput and p99, ranges separate. |
| Cold sequential 1 MiB Q8, offload off → on | 340.500 → 1140.580 MiB/s | 310.095 → 970.115 MiB/s | 3.13× current gain; all three pairs improve throughput and p99, ranges separate. |
| Cached random / sequential controls | −0.17% / +0.40% | −0.23% / −0.58% | No current pair loses 3%; all resident controls have zero misses/lower reads. |
| Explicit Flush of 512 MiB, 256 KiB × 2 → 1024 KiB × 4 | 12.713 → 8.012 seconds, complete three pairs | Incomplete: late small writes invalidate start/end evidence | Earlier 37% less drain time remains qualified; no accepted current aggregate. A held-handle fixture attempt also failed and was removed. |
| 40,000-file read/hash | 164.2 seconds with Defender active, diagnostic | 6.857 seconds with Defender/Search disabled, one observation | Hash oracle passes; consistent with removing scan overhead. Different date/cache path, not an isolated driver gain or full matrix. |
| General foreground interference / Strict impact | Unqualified | Unqualified | Existing limits remain open. |

The read repeat completed 24/24 cases and restored. The one-case tree observation
also restored; incomplete drain runs are excluded from aggregates. Exact runs,
methods and limits are in [performance findings](PERFORMANCE_FINDINGS.md).

## Earlier comparison findings

- **RAM disks.** QueueCache's RAM disk is the fastest at large reads by one program (45 GB/s with eight requests
  in flight; next best 35 GB/s) and shares first place for single large reads (25 GB/s) with SoftPerfect RAM File
  System. For small 4 KiB requests it is among the fastest true disks, about 8% behind OSFMount. SoftPerfect RAM
  File System is a file system in RAM rather than a disk: it handles small requests almost twice as fast, and creates,
  copies and deletes files two to four times as fast as any disk. With four programs reading at once the faster disks all land between
  20 and 31 GB/s (QueueCache 25). AIM Toolkit and ManagedDrive were much slower (3 to 4.5 GB/s for large reads, against
  15 to 45 GB/s for the others).
- **RAM caches.** In the original comparison, QueueCache 0.4.604.1 Fast served cached data at 39 GB/s reads and
  21 GB/s writes, against 17 and 19 GB/s for PrimoCache with Defer-Write. It exposed three weaknesses:
  - Missing data was read one request at a time: 217 MB/s against 560 for the bare disk and 512 for PrimoCache
    ([#14](https://github.com/devedse/QueueCache/issues/14)). The merged fix overlaps bounded ordinary misses.
    The 2026-10-15 same-build flags 7/15 repeat with Defender/Search disabled measured 9.95× cold random
    and 3.13× cold sequential throughput, with improved
    p99 and no repeated resident-control loss. Both arms use the new read pool and queue order; this isolates
    miss offload, not the complete old driver. A separate October 14 equal-work comparison (100,000 operations per case)
    improved cold-random p99.9 from 191.7 to 60.7 ms and the median of per-case maxima from 271.6 to 123.5 ms,
    with every pair improving. The earlier fixed-duration maxima sampled different amounts of work.
  - The old 256 KiB × 2 default wrote pending data at 105–130 MB/s. New-task defaults are 1024 KiB × 4
    ([#15](https://github.com/devedse/QueueCache/issues/15)); saved profiles keep their settings.
    Complete same-build drain tests reduced cold-read flush median 6.52 to 3.86 seconds; a separate write-only
    repeat reduced flush median 127.61 to 92.16 seconds, with every pair improving and separated ranges.
    Fixed-window write IOPS varied with administrative-flush timing and are not qualified as invariant.
    The completed 0.4.687.1 Fast comparison (2026-10-14) measured first/second 1 GiB reads at
    571 / 19,638 MB/s, larger-file random/sequential reads at 80.90 / 621.88 MB/s, and sustained writes
    at 259.62 MB/s (slowest-second median 177.73 MB/s). Tree read and hash took 154.23 seconds;
    subsequent cache-off controls and tracing attributed the later 160–170-second times to Defender scanning.
    These results do not establish a win in every workload or a causal gain over the historical rows;
    the cold suite has one repetition, larger-file, sustained and tree steps two, and hot shapes three.
  - Strict mode made writing and file operations 5 to 11 times slower than no cache, with stops exceeding
    100 seconds ([#16](https://github.com/devedse/QueueCache/issues/16)). The merged fix preserves its durability
    boundary; bounded Strict writes remain open.
- **Power cuts (original 0.4.604.1 comparison, not repeated on the fix build).** The two checked large files survived in each sampled mode; this does not prove that Fast-mode application flushes persist before acknowledgment. The two settings that answer flushes
  from RAM (QueueCache Fast and PrimoCache Defer-Write) lost far more of the newest, unflushed files, and QueueCache
  Fast also left the file system needing a repair ([#13](https://github.com/devedse/QueueCache/issues/13)).
  PrimoCache's did not there, because it had written none of the newest work yet. Cut while part of its held work
  was on the disk, PrimoCache also damaged NTFS in one of five fully checked cuts (NTFS reported a corrupted file in a
  sixth); several cuts also returned wrong contents, including another file's data
  ([Power cut while the cache is writing](#power-cut-while-the-cache-is-writing)). At that
  test's pace QueueCache Fast kept up with the disk and lost only the newest seconds, like the disk without a cache.

## Problems found

Failed runs, data loss and other problems worth knowing. They are also marked in the Problems column of both
comparison tables.

- ⚠ **[QueueCache 0.4.604.1, Fast, 2 GiB](comparison/C01-queuecache-fast.md)**: File system needed an offline repair (chkdsk /f) after a simulated power cut; a known Fast-mode risk that the requested safety goal must eliminate; tracked in GitHub issue [#13](https://github.com/devedse/QueueCache/issues/13); Reads of data not yet in the cache are 2.5 to 6 times slower than without any cache (first read of 1 GiB 217 against 560 MB/s; random 4 KiB 11 against 65 MB/s): QueueCache fetches missing data one request at a time; GitHub issue [#14](https://github.com/devedse/QueueCache/issues/14); Pending writes reach the disk at about half the disk's speed (105-130 MB/s in 256 KiB pieces), so long writes are slower than without a cache; GitHub issue [#15](https://github.com/devedse/QueueCache/issues/15)
- ⚠ **[QueueCache 0.4.604.1, Strict, 2 GiB](comparison/C01-queuecache-strict.md)**: 6 measurement window(s) completed nothing in 10 s (stalled): hot-r1-rnd4k-q1t1-write, hot-r1-rnd4k-q32t1-write, hot-r2-rnd4k-q1t1-write, hot-r3-rnd4k-q1t1-write, hot-r3-rnd4k-q32t1-write, hot-r3-seq1m-q8t1-write; Writes stop completely for 100 seconds or more after a burst of writes: every flush or write-through request from Windows makes all writes wait until everything queued in RAM is on the disk (confirmed with the driver's counters); GitHub issue [#16](https://github.com/devedse/QueueCache/issues/16); Writing and file operations are 5 to 11 times slower than without any cache (creating 40,000 files 213 against 32 s; 8 GiB write 53 against 242 MB/s; write-through 52 against 251 MB/s); Reads of data not yet in the cache are 3 to 6 times slower than without any cache (first read of 1 GiB 187 against 560 MB/s; random 4 KiB 11 against 65 MB/s): QueueCache fetches missing data one request at a time; GitHub issue [#14](https://github.com/devedse/QueueCache/issues/14)
- ⚠ **[QueueCache 0.4.687.1, Fast, 2 GiB](comparison/C01-queuecache-fast-fix687.md)**: the 40,000-file read/hash step took 154.2 s, versus 4.9 s historically. Later cache-off controls and tracing attributed the 160–170-second times to Defender scanning; the older session's scanning state is unknown. The initial fixed-duration miss screen observed a larger maximum while completing much more work; a later equal-operation test improved every cold-random pair's p99.9 and maximum. No power-cut test was repeated on this build.
- ⚠ **[PrimoCache 4.4.1, L1 2 GiB, Defer-Write](comparison/C02-primocache-defer.md)**: All 10,000 new small files written in the last seconds before a simulated power cut were lost (the file system stayed clean); flushes are answered from RAM, expected with Defer-Write; Power cuts while part of its held writes were on the disk (churn test): in 1 of 5 checked cuts NTFS needed an offline repair (volume bitmap corruption) and in another NTFS reported a file "corrupted and unreadable"; in the others files written 10-20 s earlier came back with zeros or another file's data, and deleted files came back

## Cache column definitions

Cached work first, then what the cache does with data that has to reach the disk:

- **Hot**: reads and writes of a 1 GiB file already in the cache.
- **First / second read**: with the cache emptied, reading a 1 GiB file from the disk and then once more: does
  the cache keep what it read?
- **8 GiB file**: a file four times the cache size.
- **Burst write**: 1 GiB written in one go; how fast it is accepted.
- **Drain**: after that burst, how long the virtual disk's lower block counters observed writes and at what rate,
  counted by the hypervisor below every driver. This records virtual-device write activity; it does not prove that
  data reached physical media or survived power loss.
- **Sustained write**: 8 GiB in one go, more than the cache can hold; the slowest second shows stalls.
- **Write-through**: writes that ask to go straight to disk. A cache that honors this runs at disk speed here.
- **Flush**: how long `FlushFileBuffers` takes after 512 MiB of writes. Near zero means the cache did not write the
  data to disk before reporting it saved.
- **Power loss**: the virtual machine is reset after buffered writes plus an application flush and after
  raw writes without a flush; the share of each that survived, and whether `chkdsk /scan` found file-system
  damage afterwards. In Fast mode that application flush is a volatile acknowledgment, not proof of durability.
- **70/30 mix**: five minutes of random 4 KiB reads and writes over a 4 GiB file.

## Power cut while the cache is writing

These are historical results on QueueCache **0.4.604.1**, not a power-cut repeat
on 0.4.687.1. The initial burst left NTFS needing offline repair in Fast mode.
PrimoCache's initial clean result came before its newest work had started to drain;
later cuts during partial write-out also damaged NTFS. A clean check in one cut
does not establish a crash-safety guarantee.

The requested QueueCache goal is to permit loss of unpersisted writes while
keeping NTFS healthy, without damage to untouched, durably checkpointed files.
This supersedes the earlier acceptance of filesystem damage as merely expected
Fast-mode behavior; it does not change the current driver's warning or make
ordinary Fast-mode application flushes durable.
[Issue #13](https://github.com/devedse/QueueCache/issues/13) defines the scope,
remaining design work and acceptance checks. PrimoCache is a useful performance
reference, but its vendor also documents that deferred writes can damage a
filesystem after power loss
([Romex: Defer-Write](https://www.romexsoftware.com/en-us/primo-cache/term-defer-write.html)).

The churn test cuts power while some newer work is on disk and some is still
volatile (`compare-durability-churn`):

1. 2,000 files are written and given time to reach the disk.
2. For 40 seconds, 200 new files per second are created without a flush. Every eighth step one of the first 1,000 old
   files is deleted and, from step 1,000 on, the new file created 1,000 steps earlier, so freed disk space is reused.
3. The virtual machine is reset about 2 seconds after the last file (or, in the first cuts, the moment the disk below
   was written afterwards). Each cut starts from the same restored snapshot.
4. After the restart every file is checked, grouped by its age when the churn ended, and `chkdsk /scan` runs.

**Damaged** means the file exists but holds the wrong contents: zeros, part of its data, or **another file's data**
(old contents of reused disk space). A deleted file that comes back means its deletion was lost. The disk without a
cache is the yardstick: Windows itself only loses the newest seconds, and those come back as zeros or missing.

| Product | Cut | Power cut | `chkdsk /scan` | Files 20-30 s old: missing / damaged | 10-20 s old: missing / damaged | Newest 10 s: intact / damaged / missing | Damaged files holding another file's data | Deleted files back (intact / damaged) |
|---|---|---|---|---:|---:|---:|---:|---:|
| Q: without a cache task (PrimoCache driver idle) | 1 | about 2 s after the churn ended | clean | 0 / 0 | 0 / 0 | 1833 / 41 / 0 | 0 | 0 / 0 |
| Q: without any cache | 2 | during the write-out, 20 s after the churn | clean | 0 / 0 | 0 / 0 | 1830 / 0 / 0 | not classified | 0 / 0 |
| QueueCache 0.4.604.1 Fast 2 GiB | 1 | about 2 s after the churn ended | clean | 0 / 0 | 0 / 0 | 1568 / 265 / 42 | 0 | 11 / 0 |
| QueueCache 0.4.604.1 Fast 2 GiB | 2 | about 2 s after the churn ended | clean | 0 / 0 | 0 / 0 | 1567 / 266 / 41 | 0 | 11 / 0 |
| QueueCache 0.4.604.1 Fast 2 GiB | 3 | during the write-out, 5 s after the churn | clean | 0 / 0 | 0 / 0 | 1875 / 0 / 0 | not classified | 0 / 0 |
| QueueCache 0.4.604.1 Fast 2 GiB | 4 | during the write-out, 10 s after the churn | clean | 0 / 0 | 0 / 0 | 1875 / 0 / 0 | not classified | 0 / 0 |
| PrimoCache 4.4.1 Defer-Write 2 GiB | 1 | about 2 s after the churn ended | clean | 0 / 0 | 0 / 316 | 0 / 44 / 1831 | not classified | 304 / 65 |
| PrimoCache 4.4.1 Defer-Write 2 GiB | 2 | about 2 s after the churn ended | clean | 0 / 0 | 0 / 49 | 286 / 232 / 1356 | not classified | 265 / 36 |
| PrimoCache 4.4.1 Defer-Write 2 GiB | 3 | about 2 s after the churn ended | ⚠ not run: the check stopped, NTFS reported a file "corrupted and unreadable" | - | - | - | - | - |
| PrimoCache 4.4.1 Defer-Write 2 GiB | 4 | about 2 s after the churn ended | ⚠ needs offline repair (exit 13), 26 files unreadable | 369 / 50 | 1751 / 0 | 0 / 0 / 1875 | 152 | 275 / 320 |
| PrimoCache 4.4.1 Defer-Write 2 GiB | 5 | about 2 s after the churn ended | clean | 0 / 0 | 0 / 189 | 446 / 309 / 1120 | 144 | 221 / 47 |
| PrimoCache 4.4.1 Defer-Write 2 GiB | 6 | about 2 s after the churn ended | clean | 0 / 0 | 0 / 92 | 530 / 232 / 1113 | 88 | 235 / 31 |
| PrimoCache 4.4.1 Defer-Write 2 GiB | 7 | during the write-out, 11 s after the churn | clean | 0 / 0 | 0 / 0 | 1875 / 0 / 0 | not classified | 0 / 0 |

## How this was measured

This is the original comparison setup. The October 15/16 refreshes above instead
disabled Defender/Search; their separate storage qualifications still apply.

| | |
|---|---|
| Host | AMD Ryzen 9 9955HX (16 cores), 96 GB DDR5-5600, Proxmox VE 9.2 |
| Virtual machine | Windows 11 (26H1, build 28000), 4 vCPUs, 16 GB RAM, no Driver Verifier, Memory Integrity off |
| Q: | 200 GB virtual SSD on Ceph network storage |
| Tools | CrystalDiskMark 9.0.3 and its DiskSpd 2.2 for every product; QueueCache's `qcache developer verify` comparison suites (plan 121), which use no QueueCache feature |

- **One product at a time.** Each product was installed on the same snapshot of the machine without QueueCache,
  measured, and removed by restoring that snapshot. QueueCache was measured from a snapshot with QueueCache installed.
- **Same conditions.** At least 15 minutes after a restart, normal priority, no other programs running (the file
  indexer and game launcher were closed), Windows Update paused, NTFS last-access updates off. Windows Defender stayed
  on; the benchmark folders (`T:\` and `Q:\QCBench`) were excluded from scanning, as is common for RAM disks, because
  scanning every new file otherwise dominates the file-tree times.
- **RAM disks**: 4 GiB, NTFS, static memory unless the mode says otherwise, no image or persistence.
- **Caches**: 2 GiB of RAM, no SSD tier, no prefetch at start-up. Each cache was emptied before its empty-cache runs.
- **Limits.** A virtual machine with four processors, whose processor model hides some copy features (ERMS), so a
  physical PC can be faster for every product. Q: sits on shared network storage, so numbers that depend on the disk
  (first reads, drains, sustained writes) vary between sessions. Product rows from different dates describe their
  recorded sessions; causal speedup claims come from the alternated same-build comparisons in the findings. Trial versions
  were used where a product is paid. File systems in RAM (marked as such) are a different design from RAM disks.
