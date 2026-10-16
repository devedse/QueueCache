# QueueCache 0.4.687.1, Fast, 2 GiB, 1024 KiB × 4

[Back to the comparison](../COMPARISON.md). Status: **Measured**.

## Short write plus drain comparison (2026-10-16)

Three complete repetitions per workload, plan 148; same temporary snapshot present,
Defender/Search disabled, all storage guards passed. Fast used 2 GiB, 1024 KiB batches and four drainers; native driver 0.4.687.1 was unchanged.

| Workload | MB/s, median (range) | App seconds | Drain/confirmation seconds | Total seconds | Mean backing write KiB |
|---|---:|---:|---:|---:|---:|
| One new 1 GiB file | **52.71** (42.25–62.13) | 0.139 | 20.230 | 20.369 | 1009.6 |
| 4,096 new 64 KiB files | **41.71** (18.36–41.94) | 1.142 | 5.293 | 6.435 | 341.9 |

Exact run: `QueueCache-Verify-20261016-100617-00f50445cdc04f5e897b625d384e1bec`; all 12,291 cache-bypassed file hashes passed.
The headline includes filesystem metadata, explicit drain and pending/disk queue
confirmation; verification is untimed. Application/drain/total columns are separate
medians and need not add up. Backing-write sizes
include metadata at the observed virtual-disk counter layer. These scores do not
replace the different historical workloads below or prove power-loss durability. One post-score, pre-pause
observation had 56 KiB newly dirty; untimed Disable drained it. Its source is untraced.
The oracle validates files after that confirmation.

[All three providers and method](../COMPARISON_DETAILS.md#short-write-plus-drain-comparison-2026-10-16);
per-case evidence and build identity are in the existing [data](data/C01-queuecache-fast-fix687.json).

## Current cache refresh (2026-10-15/16)

QueueCache Fast, 2 GiB, 1024 KiB × 4. Defender/Search disabled; one complete repetition per comparison and two clean CDM runs. Normal priority, Verifier/timing off. [Common setup and sampled-disk timing limits](../COMPARISON_DETAILS.md#current-cache-refresh-2026-10-1516).

Runs: `QueueCache-Verify-20261016-012344-9b75c20c35e64c84b81d54007349cc62`, `QueueCache-Verify-20261015-231123-5c5ab6cf34744b68b23521781a523fd6`.
All declared steps and raw XML totals, duration, CPU and latency fields were reviewed; every tree hash check passed.

| Workload | Current observation |
|---|---:|
| Hot sequential 1M Q8 read / write, MB/s | 35,016.8 / 20,937.2 |
| First / second 1 GiB read after clearing, MB/s | 563.7 / 22,802.1 |
| 8 GiB file random 4K Q32 / sequential 1M Q8 read, MB/s | 104.8 / 654.2 |
| 8 GiB sustained write / slowest full second, MB/s | 267.5 / 192.9 |
| 8 GiB payload through last busy backing-disk sample, MB/s | 200.1 |
| Backing-disk activity after 1 GiB burst, seconds | 6.28 |
| File tree create / copy / read-hash / delete, seconds | 4.03 / 5.36 / 2.84 / 11.98 |

<img src="../images/comparison/on-refresh-20261016.png" width="480" alt="CrystalDiskMark, QueueCache Fast, 2 GiB, 1024 KiB × 4">

CrystalDiskMark retained `run2`, selected by sequential Q8 read from the two clean runs below. MB/s.

| Shape | run1 read / write | run2 read / write |
|---|---:|---:|
| SEQ1M Q8T1 | 38,747.11 / 20,556.19 | 40,591.32 / 20,907.88 |
| SEQ1M Q1T1 | 19,782.88 / 13,398.29 | 20,202.83 / 13,545.81 |
| RND4K Q32T1 | 1,753.62 / 1,469.58 | 1,764.46 / 1,468.38 |
| RND4K Q1T1 | 1,299.89 / 1,096.45 | 1,306.24 / 1,095.32 |

All current shapes, percentiles, memory snapshots and backing-write measurements: [comparison data](data/C01-queuecache-fast-fix687.json), `Refresh20261016`.

## Earlier measurement (retained)

The following results keep their original sessions and environment.

| | |
|---|---|
| Product | QueueCache |
| Version | 0.4.687.1 |
| Mode | Fast, 2 GiB, 1024 KiB × 4 |
| Kind | Volume filter, write-back |
| Price | Free (MIT) |
| `compare-cache` run | `QueueCache-Verify-20261014-103546-64b8c94e36eb475084cf48d92c58a5a4` |
| `compare-cache-cold` run | `QueueCache-Verify-20261014-113824-70b3db176e494efea4f7823e9199e453` |

### Notes

Measured on 2026-10-14 with signed QueueCache 0.4.687.1, Fast, 2 GiB RAM,
Idle write-back, 1024 KiB batches and four drainers. This is a separate refresh
of the Fast cache configuration. Historical 0.4.604.1, competitor and RAM-disk
rows retain their original measurement dates and evidence. Cross-date figures
are descriptive; causal gains are qualified only by alternated same-build A/B
comparisons in [performance findings](../PERFORMANCE_FINDINGS.md).

Both plan-132 runs completed with no run or restoration failure:
`QueueCache-Verify-20261014-103546-64b8c94e36eb475084cf48d92c58a5a4`
(`compare-cache`, three repetitions) and
`QueueCache-Verify-20261014-113824-70b3db176e494efea4f7823e9199e453`
(`compare-cache-cold`, one repetition). The main run contains 422 SHA-256-verified
evidence files; the cold run contains 53. Loaded native hashes, environment,
completion records and all 49 raw DiskSpd XML byte totals were reviewed; all
DiskSpd windows completed operations. Generic comparison workloads use no
QueueCache controls, hooks or telemetry.

Selected results (decimal MB/s):

| Workload | Result | Repetitions |
|---|---:|---:|
| First / second sequential read of 1 GiB, cache emptied | 571 / 19,638 MB/s | 1 |
| 8 GiB file, random 4K Q32 / sequential 1M Q8 read | 80.90 / 621.88 MB/s | 2 |
| 8 GiB sustained write, average / slowest-second median | 259.62 / 177.73 MB/s | 2 |
| File-tree read and hash | 154.23 seconds | 2 |
| Hot sequential 1M Q8 read / write | 34,774.80 / 20,655.48 MB/s | 3 |
| 1 GiB burst admission | 2,959.75 MB/s | 3 |

The burst-drain table uses actual hypervisor scsi1 cumulative write counters:
3,938 one-second samples cover both verification runs, maximum gap 1.036 seconds,
with monotonic counters and the owned sampler stopped afterwards. Each of the
three absorb passes was joined to its recorded `compare.absorb-rN.interval.json`:
the preceding host sample brackets the pass start within one second, and the
last busy sample plus five quiet samples lies inside that pass's settle interval,
before the next step. The existing generator reports median 7.02 seconds and
152.95 MB/s from pass completion to the last busy sample. These are sampled
virtual-disk observations, with roughly one-second timing resolution; no missing
counter is treated as zero and they do not certify physical-media durability.

CrystalDiskMark has two real runs; run 2 was retained for its higher SEQ1M Q8T1
read. Its captured read/write scores are 37,374.91 / 19,980.67 (SEQ1M Q8T1),
19,874.66 / 13,575.57 (SEQ1M Q1T1), 1,704.05 / 1,411.14 (RND4K Q32T1), and
1,297.95 / 1,029.48 (RND4K Q1T1). The screenshot is the original retained capture.

This refresh does not establish a win in every workload. Later cache-off controls
and tracing attributed the 160–170-second file-tree read/hash times to Defender
scanning (151.6 seconds of on-open scans in a 164.2-second diagnostic phase).
The numbers on this page retain their original environment; shared
network-storage conditions also vary across dates. Follow-up measurements are
recorded in [performance findings](../PERFORMANCE_FINDINGS.md).
The cold suite is a single observation; larger-file, sustained and tree results
have two repetitions rather than three. Fast acknowledges explicit flushes from
volatile RAM (the measured 512 MiB flush median is 0.000131 seconds), so hot
write-through and flush scores do not establish disk durability. Strict mode was
not refreshed. Power-cut tests were not repeated on 0.4.687.1: this row has no
new power-loss result, and all original power-cut evidence remains historical.

### CrystalDiskMark

<img src="../images/comparison/C01-queuecache-fast-fix687.png" width="480" alt="CrystalDiskMark, QueueCache 0.4.687.1, Fast, 2 GiB, 1024 KiB × 4">

Kept: run 2 (the faster SEQ1M Q8T1 read of two). MB/s.

| Test | Run 1 read / write | Run 2 read / write |
|---|---:|---:|
| SEQ1M Q8T1 | 36,975.01 / 20,447.36 | 37,374.91 / 19,980.67 |
| SEQ1M Q1T1 | 19,967.90 / 13,333.99 | 19,874.66 / 13,575.57 |
| RND4K Q32T1 | 1,778.06 / 1,396.98 | 1,704.05 / 1,411.14 |
| RND4K Q1T1 | 1,294.73 / 1,025.89 | 1,297.95 / 1,029.48 |

### Detailed measurements (DiskSpd and timed steps)

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| hot-seq1m-q8t1-read | 3 | 34,774.8 (33,605.9–37,336.2) | 33,164 | 0.219 / 0.489 / 1.570 | - / - / - | 97.9 |
| hot-seq1m-q1t1-read | 3 | 16,186.7 (15,624.3–19,836.9) | 15,437 | 0.061 / 0.101 / 0.174 | - / - / - | 25.4 |
| hot-rnd4k-q32t1-read | 3 | 1,714.6 (1,713.0–1,741.7) | 418,602 | 0.067 / 0.105 / 0.200 | - / - / - | 49.5 |
| hot-rnd4k-q1t1-read | 3 | 1,297.2 (1,291.5–1,303.7) | 316,688 | 0.003 / 0.009 / 0.035 | - / - / - | 25.6 |
| hot-seq1m-q8t1-write | 3 | 20,655.5 (20,386.9–21,307.8) | 19,699 | - / - / - | 0.372 / 0.856 / 1.915 | 97.2 |
| hot-seq1m-q1t1-write | 3 | 12,980.8 (12,918.8–13,526.0) | 12,379 | - / - / - | 0.074 / 0.152 / 0.526 | 26.2 |
| hot-rnd4k-q32t1-write | 3 | 1,383.8 (1,382.6–1,414.4) | 337,853 | - / - / - | 0.008 / 0.107 / 0.287 | 50.7 |
| hot-rnd4k-q1t1-write | 3 | 1,008.0 (1,006.1–1,011.7) | 246,097 | - / - / - | 0.004 / 0.014 / 0.040 | 26.1 |
| buffered-seq1m-q8t1-read-buffered | 3 | 12,488.8 (12,446.2–12,535.7) | 11,910 | 0.078 / 0.135 / 0.597 | - / - / - | 25.4 |
| buffered-rnd4k-q1t1-read-buffered | 3 | 1,444.6 (1,418.5–1,446.5) | 352,686 | 0.002 / 0.010 / 0.038 | - / - / - | 25.6 |
| buffered-seq1m-q8t1-write-buffered | 3 | 5,091.3 (5,089.8–5,128.1) | 4,855 | - / - / - | 0.195 / 0.415 / 0.807 | 26.0 |
| buffered-rnd4k-q1t1-write-buffered | 3 | 1,248.5 (1,246.7–1,257.1) | 304,802 | - / - / - | 0.003 / 0.009 / 0.041 | 26.0 |
| through-seq1m-q8t1-write-write-through | 3 | 20,630.1 (20,355.3–20,700.5) | 19,674 | - / - / - | 0.373 / 0.858 / 2.055 | 97.9 |
| through-rnd4k-q1t1-write-write-through | 3 | 968.0 (962.8–968.6) | 236,326 | - / - / - | 0.004 / 0.015 / 0.044 | 26.1 |
| big-rnd4k-q32t1-read | 2 | 80.9 (71.5–90.3) | 19,751 | 1.026 / 9.997 / 24.502 | - / - / - | 30.8 |
| big-seq1m-q8t1-read | 2 | 621.9 (606.8–636.9) | 593 | 10.745 / 45.006 / 103.272 | - / - / - | 5.1 |
| mixed-rnd4k-q8t1-mixed | 1 | 34.4 (34.4–34.4) | 8,387 | 0.293 / 12.638 / 36.927 | 0.031 / 2.125 / 8.538 | 13.5 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| hot-warm1 | 3 | MB/s | 12,699.41 | 545.41–15,678.31 |
| hot-warm2 | 1 | MB/s | 23,287.03 | 23,287.03–23,287.03 |
| hot-warm3 | 1 | MB/s | 21,374.46 | 21,374.46–21,374.46 |
| buffered-warm | 3 | MB/s | 19,899.62 | 15,877.53–24,518.46 |
| absorb | 3 | MB/s | 2,959.75 | 2,893.06–3,018.43 |
| flush | 3 | MB/s | 7,590.52 | 3,782.78–7,754.07 |
| flush | 3 | flush seconds | 0.00 | 0.00–0.00 |
| sustained | 2 | MB/s | 259.62 | 251.68–267.57 |
| sustained | 2 | slowest second MB/s | 177.73 | 161.48–193.99 |
| tree | 2 | create seconds | 50.23 | 47.65–52.82 |
| tree | 2 | copy seconds (4 threads) | 13.68 | 10.26–17.09 |
| tree | 2 | read and hash seconds | 154.23 | 154.16–154.29 |
| tree | 2 | delete seconds | 15.90 | 15.69–16.10 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| memory-start | 10,612 | 4,700 | 376 |
| memory-end | 10,136 | 5,121 | 471 |

### Empty cache

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| cold-rnd4k-q32t1-read-first | 1 | 68.6 (68.6–68.6) | 16,746 | 1.230 / 10.378 / 24.683 | - / - / - | 30.7 |
| cold-rnd4k-q32t1-read-second | 1 | 75.9 (75.9–75.9) | 18,519 | 1.006 / 11.981 / 31.060 | - / - / - | 28.0 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| cold-first | 1 | MB/s | 570.54 | 570.54–570.54 |
| cold-second | 1 | MB/s | 19,637.80 | 19,637.80–19,637.80 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| memory-start | 10,343 | 4,925 | 391 |
| memory-end | 10,381 | 4,909 | 390 |

### Power loss

Not run yet.

### Environment

Windows 26H1 build 28000.3231; target Q: NTFS, cluster 4096 bytes, 199.98 GiB.

Loaded third-party drivers: vioscsi.sys 100.100.104.27100 (Red Hat, Inc.), balloon.sys 100.100.104.27100 (Red Hat, Inc.), netkvm.sys 100.100.104.27100 (Red Hat, Inc.), fwcfg.sys 100.100.104.27100 (Red Hat, Inc.); QueueCache: C:\WINDOWS\System32\drivers\QueueCache-0.4.687.1-FE00891A0D6A.sys, C:\WINDOWS\System32\DriverStore\FileRepository\qcramdisk.inf_amd64_ab2607323c1d5628\qcramdisk.sys.
