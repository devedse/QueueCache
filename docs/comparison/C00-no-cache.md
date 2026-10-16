# Q: disk baseline

[Back to the comparison](../COMPARISON.md). Status: **Measured**.

## Short write plus drain comparison (2026-10-16)

Three complete repetitions per workload, plan 148; same temporary snapshot present,
Defender/Search disabled, all storage guards passed. QueueCache drivers and PrimoCache were absent.

| Workload | MB/s, median (range) | App seconds | Drain/confirmation seconds | Total seconds | Mean backing write KiB |
|---|---:|---:|---:|---:|---:|
| One new 1 GiB file | **48.41** (42.63–53.43) | 22.152 | 0.089 | 22.179 | 988.8 |
| 4,096 new 64 KiB files | **1.25** (1.23–1.33) | 213.555 | 0.489 | 213.897 | 5.0 |

Exact run: `QueueCache-Verify-20261016-084338-620bac6863d24b9d8b4c6614fd43d0cf`; all 12,291 cache-bypassed file hashes passed.
The headline includes filesystem metadata, explicit drain and pending/disk queue
confirmation; verification is untimed. Application/drain/total columns are separate
medians and need not add up. Backing-write sizes
include metadata at the observed virtual-disk counter layer. These scores do not
replace the different historical workloads below or prove power-loss durability.

[All three providers and method](../COMPARISON_DETAILS.md#short-write-plus-drain-comparison-2026-10-16);
per-case evidence and build identity are in the existing [data](data/C00-no-cache.json).

## Current cache refresh (2026-10-15)

QueueCache paused. Defender/Search disabled; one complete repetition per comparison and two clean CDM runs. Normal priority, Verifier/timing off. [Common setup and sampled-disk timing limits](../COMPARISON_DETAILS.md#current-cache-refresh-2026-10-1516).

Runs: `QueueCache-Verify-20261015-230825-83e24bca1049435592297d32084887d6`, `QueueCache-Verify-20261015-224743-b2572bf0e2a745129b59744d3ff3d31c`.
All declared steps and raw XML totals, duration, CPU and latency fields were reviewed; every tree hash check passed.

| Workload | Current observation |
|---|---:|
| Hot sequential 1M Q8 read / write, MB/s | 572.8 / 250.7 |
| First / second 1 GiB read after clearing, MB/s | 579.3 / 581.4 |
| 8 GiB file random 4K Q32 / sequential 1M Q8 read, MB/s | 91.2 / 658.7 |
| 8 GiB sustained write / slowest full second, MB/s | 259.5 / 243.3 |
| 8 GiB payload through last busy backing-disk sample, MB/s | 257.4 |
| Backing-disk activity after 1 GiB burst, seconds | 0.66 |
| File tree create / copy / read-hash / delete, seconds | 23.69 / 21.19 / 2.90 / 13.42 |

<img src="../images/comparison/off-refresh-20261016.png" width="480" alt="CrystalDiskMark, QueueCache paused">

CrystalDiskMark retained `run2`, selected by sequential Q8 read from the two clean runs below. MB/s.

| Shape | run2 read / write | run3 read / write |
|---|---:|---:|
| SEQ1M Q8T1 | 661.58 / 266.18 | 660.78 / 266.53 |
| SEQ1M Q1T1 | 285.68 / 121.39 | 283.07 / 121.44 |
| RND4K Q32T1 | 130.08 / 12.43 | 115.98 / 13.16 |
| RND4K Q1T1 | 19.35 / 5.13 | 19.12 / 4.73 |

All current shapes, percentiles, memory snapshots and backing-write measurements: [comparison data](data/C00-no-cache.json), `Refresh20261016`.

## Earlier measurement (retained)

The following results keep their original sessions and environment.

| | |
|---|---|
| Product | Q: without any cache |
| Version | Windows 11 |
| Mode | baseline |
| Kind | Baseline |
| Price | - |
| `compare-cache` run | `QueueCache-Verify-20261012-223926-de1ac352bbb54bbe881e634041a8ea5f` |
| `compare-cache-cold` run | `QueueCache-Verify-20261012-232339-6a1dafeb61ae4b02810f287cd1564ff4` |
| `compare-durability-write` run | `QueueCache-Verify-20261012-232604-91dbf1f2c4c643869b00996f8d8663c6` |
| `compare-durability-verify` run | `QueueCache-Verify-20261012-232810-5db0f01079f4422eac252f3e000490a3` |

### Notes

The run went fine. This is the disk itself, without any cache, as the yardstick for the caches: about 570 MB/s for large reads, 250 MB/s for large writes, and 12 MB/s (reads) and 4 MB/s (writes) for small random requests one at a time. Windows' own file cache still speeds up programs that use it (the "buffered" rows). After the simulated power cut the file system was clean and all flushed data had survived. Of 10,000 new small files that nobody flushed, 8,236 were intact, 1,066 incomplete and 698 missing. That is normal Windows behaviour: it keeps new files in memory for a few seconds.

### CrystalDiskMark

<img src="../images/comparison/C00-no-cache.png" width="480" alt="CrystalDiskMark, Q: without any cache Windows 11, baseline">

Kept: run 2 (the faster SEQ1M Q8T1 read of two). MB/s.

| Test | Run 1 read / write | Run 2 read / write |
|---|---:|---:|
| SEQ1M Q8T1 | 627.28 / 256.47 | 627.55 / 259.02 |
| SEQ1M Q1T1 | 274.11 / 123.50 | 278.31 / 123.33 |
| RND4K Q32T1 | 135.12 / 11.62 | 116.61 / 10.82 |
| RND4K Q1T1 | 19.77 / 4.76 | 19.64 / 4.67 |

### Detailed measurements (DiskSpd and timed steps)

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| hot-seq1m-q8t1-read | 3 | 570.8 (570.3–572.1) | 544 | 14.302 / 28.856 / 33.207 | - / - / - | 1.3 |
| hot-seq1m-q1t1-read | 3 | 229.8 (228.7–236.1) | 219 | 5.517 / 7.131 / 9.054 | - / - / - | 1.1 |
| hot-rnd4k-q32t1-read | 3 | 118.3 (112.3–122.8) | 28,892 | 0.410 / 5.251 / 11.082 | - / - / - | 22.7 |
| hot-rnd4k-q1t1-read | 3 | 12.1 (11.8–12.5) | 2,949 | 0.339 / 0.649 / 1.577 | - / - / - | 2.7 |
| hot-seq1m-q8t1-write | 3 | 251.3 (242.5–252.8) | 240 | - / - / - | 33.512 / 68.818 / 78.559 | 1.2 |
| hot-seq1m-q1t1-write | 3 | 119.2 (118.4–119.2) | 114 | - / - / - | 8.924 / 12.549 / 14.126 | 0.7 |
| hot-rnd4k-q32t1-write | 3 | 10.7 (10.0–10.7) | 2,601 | - / - / - | 10.237 / 58.151 / 106.293 | 2.3 |
| hot-rnd4k-q1t1-write | 3 | 4.1 (2.5–4.3) | 990 | - / - / - | 0.883 / 3.917 / 8.967 | 1.2 |
| buffered-seq1m-q8t1-read-buffered | 3 | 13,461.8 (13,311.9–13,516.5) | 12,838 | 0.073 / 0.131 / 0.229 | - / - / - | 25.4 |
| buffered-rnd4k-q1t1-read-buffered | 3 | 1,470.6 (1,456.9–1,472.7) | 359,038 | 0.002 / 0.009 / 0.039 | - / - / - | 25.6 |
| buffered-seq1m-q8t1-write-buffered | 3 | 5,294.7 (140.2–5,295.6) | 5,049 | - / - / - | 0.190 / 0.284 / 0.497 | 25.6 |
| buffered-rnd4k-q1t1-write-buffered | 3 | 1,234.9 (1,232.3–1,237.6) | 301,482 | - / - / - | 0.003 / 0.010 / 0.042 | 25.9 |
| through-seq1m-q8t1-write-write-through | 3 | 250.7 (250.3–254.8) | 239 | - / - / - | 32.701 / 69.404 / 77.580 | 0.8 |
| through-rnd4k-q1t1-write-write-through | 3 | 4.8 (4.7–4.8) | 1,182 | - / - / - | 0.836 / 1.952 / 2.953 | 1.7 |
| big-rnd4k-q32t1-read | 2 | 86.2 (85.9–86.4) | 21,036 | 0.406 / 10.785 / 28.559 | - / - / - | 16.3 |
| big-seq1m-q8t1-read | 2 | 668.6 (667.1–670.1) | 638 | 10.692 / 28.618 / 33.686 | - / - / - | 1.5 |
| mixed-rnd4k-q8t1-mixed | 1 | 11.0 (11.0–11.0) | 2,682 | 0.556 / 12.226 / 29.364 | 4.393 / 29.454 / 68.431 | 3.1 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| hot-warm1 | 3 | MB/s | 568.21 | 567.48–569.03 |
| hot-warm2 | 1 | MB/s | 570.09 | 570.09–570.09 |
| hot-warm3 | 1 | MB/s | 569.76 | 569.76–569.76 |
| buffered-warm | 3 | MB/s | 575.95 | 567.55–577.22 |
| absorb | 3 | MB/s | 256.14 | 230.85–260.04 |
| flush | 3 | MB/s | 260.34 | 229.25–271.93 |
| flush | 3 | flush seconds | 0.00 | 0.00–0.00 |
| sustained | 2 | MB/s | 242.48 | 225.24–259.73 |
| sustained | 2 | slowest second MB/s | 218.63 | 205.52–231.74 |
| tree | 2 | create seconds | 31.91 | 29.14–34.68 |
| tree | 2 | copy seconds (4 threads) | 28.30 | 27.24–29.37 |
| tree | 2 | read and hash seconds | 4.93 | 4.89–4.97 |
| tree | 2 | delete seconds | 23.92 | 22.10–25.75 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| memory-start | 13,082 | 2,366 | 204 |
| memory-end | 12,817 | 2,618 | 284 |

### Empty cache

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| cold-rnd4k-q32t1-read-first | 1 | 65.0 (65.0–65.0) | 15,861 | 0.434 / 16.767 / 48.423 | - / - / - | 13.0 |
| cold-rnd4k-q32t1-read-second | 1 | 67.0 (67.0–67.0) | 16,353 | 0.428 / 15.384 / 41.970 | - / - / - | 13.4 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| cold-first | 1 | MB/s | 560.41 | 560.41–560.41 |
| cold-second | 1 | MB/s | 572.93 | 572.93–572.93 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| memory-start | 13,018 | 2,428 | 216 |
| memory-end | 13,042 | 2,406 | 214 |

### Power loss


| Data set | New | Old | Other or missing |
|---|---:|---:|---:|
| Durable save (buffered writes, then flush) | 100.0% | 0.0% | 0.0% |
| Raw writes without a flush | 100.0% | 0.0% | 0.0% |

Small files: 8236 intact, 1066 damaged, 698 missing of 10000; chkdsk /scan exit 0.

### Environment

Windows 26H1 build 28000.2956; target Q: NTFS, cluster 4096 bytes, 199.98 GiB.

Loaded third-party drivers: vioscsi.sys 100.100.104.27100 (Red Hat, Inc.), balloon.sys 100.100.104.27100 (Red Hat, Inc.), netkvm.sys 100.100.104.27100 (Red Hat, Inc.), fwcfg.sys 100.100.104.27100 (Red Hat, Inc.).
