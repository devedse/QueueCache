# PrimoCache 4.4.1, L1 2 GiB, Defer-Write

[Back to the comparison](../COMPARISON.md). Status: **Measured**.

| | |
|---|---|
| Product | PrimoCache |
| Version | 4.4.1 |
| Mode | L1 2 GiB, Defer-Write |
| Kind | Disk filter, write-back |
| Price | $29.95-49.95 (30-day trial used) |
| `compare-cache` run | `QueueCache-Verify-20261013-002452-e4771b7af06749578b57e581ce05f787` |
| `compare-cache-cold` run | `QueueCache-Verify-20261013-011634-72663c0defda442c8b8824a822a9fa3b` |
| `compare-durability-write` run | `QueueCache-Verify-20261013-011859-69cedcd7017d481c8103d02628b08ea3` |
| `compare-durability-verify` run | `QueueCache-Verify-20261013-012105-3fc6f95878554734b25fce2d28ca813f` |

## Problems

- ⚠ All 10,000 new small files written in the last seconds before a simulated power cut were lost (the file system stayed clean); flushes are answered from RAM, expected with Defer-Write
- ⚠ Power cuts while part of its held writes were on the disk (churn test): in 1 of 5 checked cuts NTFS needed an offline repair (volume bitmap corruption) and in another NTFS reported a file "corrupted and unreadable"; in the others files written 10-20 s earlier came back with zeros or another file's data, and deleted files came back

## Notes

The run went fine. Cached data is fast (about 17 GB/s reads and 19 GB/s writes; QueueCache reads cached data about twice as fast). Reading data that is not in the cache yet runs at the bare disk's speed (first read of 1 GiB at 512 MB/s, small random reads at 72 MB/s), because PrimoCache sends several such reads to the disk at once. After a 1 GiB burst the disk caught up in about 7 seconds at about 149 MB/s. With Defer-Write, PrimoCache answers flushes and write-through requests from RAM (a 256 MiB flush returned in 0.17 s, faster than the disk can write) and writes the data to the disk after a delay (10 seconds here). After the simulated power cut the file system was clean and data written earlier had reached the disk, but all 10,000 small files created in the last seconds before the cut were gone.

## CrystalDiskMark

<img src="../images/comparison/C02-primocache-defer.png" width="480" alt="CrystalDiskMark, PrimoCache 4.4.1, L1 2 GiB, Defer-Write">

Kept: run 2 (the faster SEQ1M Q8T1 read of two). MB/s.

| Test | Run 1 read / write | Run 2 read / write |
|---|---:|---:|
| SEQ1M Q8T1 | 16,400.21 / 18,279.61 | 16,445.19 / 18,034.83 |
| SEQ1M Q1T1 | 14,241.58 / 4,287.23 | 14,087.26 / 4,139.49 |
| RND4K Q32T1 | 487.49 / 380.46 | 504.71 / 335.93 |
| RND4K Q1T1 | 1,001.33 / 775.05 | 1,003.38 / 776.41 |

## Detailed measurements (DiskSpd and timed steps)

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| hot-seq1m-q8t1-read | 3 | 17,171.7 (17,129.1–17,205.1) | 16,376 | 0.214 / 0.395 / 2.046 | - / - / - | 71.5 |
| hot-seq1m-q1t1-read | 3 | 14,171.6 (12,895.5–14,452.7) | 13,515 | 0.070 / 0.104 / 0.209 | - / - / - | 27.0 |
| hot-rnd4k-q32t1-read | 3 | 463.0 (177.9–490.1) | 113,047 | 0.031 / 0.125 / 0.460 | - / - / - | 56.2 |
| hot-rnd4k-q1t1-read | 3 | 1,020.5 (863.0–1,027.4) | 249,153 | 0.004 / 0.010 / 0.025 | - / - / - | 25.2 |
| hot-seq1m-q8t1-write | 3 | 19,036.5 (14,271.8–19,203.8) | 18,155 | - / - / - | 0.201 / 0.426 / 6.304 | 71.5 |
| hot-seq1m-q1t1-write | 3 | 2,364.9 (2,258.4–2,846.0) | 2,255 | - / - / - | 0.066 / 17.274 / 20.963 | 4.8 |
| hot-rnd4k-q32t1-write | 3 | 332.4 (327.9–339.7) | 81,149 | - / - / - | 0.036 / 0.158 / 4.000 | 49.0 |
| hot-rnd4k-q1t1-write | 3 | 522.1 (503.8–523.1) | 127,472 | - / - / - | 0.005 / 0.018 / 0.053 | 16.8 |
| buffered-seq1m-q8t1-read-buffered | 3 | 13,687.3 (13,678.5–13,695.6) | 13,053 | 0.072 / 0.123 / 0.230 | - / - / - | 25.3 |
| buffered-rnd4k-q1t1-read-buffered | 3 | 1,458.1 (1,456.1–1,459.2) | 355,984 | 0.002 / 0.009 / 0.037 | - / - / - | 25.6 |
| buffered-seq1m-q8t1-write-buffered | 3 | 5,299.3 (5,293.0–5,318.3) | 5,054 | - / - / - | 0.189 / 0.318 / 0.550 | 25.9 |
| buffered-rnd4k-q1t1-write-buffered | 3 | 1,246.5 (1,240.0–1,253.7) | 304,329 | - / - / - | 0.003 / 0.009 / 0.042 | 25.9 |
| through-seq1m-q8t1-write-write-through | 3 | 19,013.2 (18,785.1–19,076.1) | 18,132 | - / - / - | 0.203 / 0.418 / 2.192 | 70.2 |
| through-rnd4k-q1t1-write-write-through | 3 | 376.5 (376.3–381.0) | 91,925 | - / - / - | 0.005 / 0.018 / 0.050 | 12.4 |
| big-rnd4k-q32t1-read | 2 | 129.9 (126.6–133.1) | 31,708 | 0.324 / 8.993 / 22.413 | - / - / - | 34.5 |
| big-seq1m-q8t1-read | 2 | 384.0 (348.6–419.3) | 366 | 14.857 / 118.994 / 201.230 | - / - / - | 6.3 |
| mixed-rnd4k-q8t1-mixed | 1 | 23.7 (23.7–23.7) | 5,787 | 0.287 / 20.828 / 70.512 | 0.050 / 21.546 / 85.395 | 11.4 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| hot-warm1 | 3 | MB/s | 12,490.47 | 566.22–14,970.76 |
| hot-warm2 | 1 | MB/s | 15,447.32 | 15,447.32–15,447.32 |
| hot-warm3 | 1 | MB/s | 18,522.79 | 18,522.79–18,522.79 |
| buffered-warm | 3 | MB/s | 16,280.04 | 10,582.12–17,232.86 |
| absorb | 3 | MB/s | 8,443.77 | 7,359.02–8,907.97 |
| flush | 3 | MB/s | 7,120.39 | 6,827.80–7,125.29 |
| flush | 3 | flush seconds | 0.00 | 0.00–0.00 |
| sustained | 2 | MB/s | 486.17 | 472.48–499.85 |
| sustained | 2 | slowest second MB/s | 374.87 | 363.86–385.88 |
| tree | 2 | create seconds | 21.35 | 10.98–31.73 |
| tree | 2 | copy seconds (4 threads) | 8.43 | 6.44–10.41 |
| tree | 2 | read and hash seconds | 4.91 | 4.89–4.92 |
| tree | 2 | delete seconds | 14.95 | 14.38–15.52 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| memory-start | 10,509 | 4,996 | 286 |
| memory-end | 10,124 | 5,226 | 393 |

## Empty cache

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| cold-rnd4k-q32t1-read-first | 1 | 72.0 (72.0–72.0) | 17,573 | 0.444 / 18.120 / 48.445 | - / - / - | 22.2 |
| cold-rnd4k-q32t1-read-second | 1 | 79.9 (79.9–79.9) | 19,497 | 0.319 / 18.349 / 49.064 | - / - / - | 23.4 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| cold-first | 1 | MB/s | 511.91 | 511.91–511.91 |
| cold-second | 1 | MB/s | 13,882.80 | 13,882.80–13,882.80 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| memory-start | 10,349 | 5,053 | 325 |
| memory-end | 10,378 | 5,043 | 325 |

## Power loss


| Data set | New | Old | Other or missing |
|---|---:|---:|---:|
| Durable save (buffered writes, then flush) | 100.0% | 0.0% | 0.0% |
| Raw writes without a flush | 100.0% | 0.0% | 0.0% |

Small files: 0 intact, 0 damaged, 10000 missing of 10000; chkdsk /scan exit 0.

## Environment

Windows 26H1 build 28000.2956; target Q: NTFS, cluster 4096 bytes, 199.98 GiB.

Loaded third-party drivers: vioscsi.sys 100.100.104.27100 (Red Hat, Inc.), rxfcv.sys 4.4.0 (Romex Software), RXBSKNL.SYS 1.2.0 (Windows (R) Win 7 DDK provider), balloon.sys 100.100.104.27100 (Red Hat, Inc.), netkvm.sys 100.100.104.27100 (Red Hat, Inc.), fwcfg.sys 100.100.104.27100 (Red Hat, Inc.).
