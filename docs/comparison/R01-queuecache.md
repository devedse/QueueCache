# QueueCache 0.4.604.1, RAM disk

[Back to the comparison](../COMPARISON.md). Status: **Measured**.

| | |
|---|---|
| Product | QueueCache |
| Version | 0.4.604.1 |
| Mode | RAM disk |
| Kind | Block device (Storport) |
| Price | Free (MIT) |
| `compare-snapshot` run | `QueueCache-Verify-20261012-074351-820ddf21962a4310950637d07f8999c2` |
| `compare-ram` run | `QueueCache-Verify-20261012-075529-36f78dfa63d544f6a8affcb337745130` |

## Notes

Measured from the snapshot with QueueCache installed (Q: keeps its 2 GiB Fast cache; T: is a QueueCache RAM disk with default Direct reads). CrystalDiskMark stayed open but idle during the detailed run because a lab script error skipped its close step; both CrystalDiskMark runs had finished before the detailed run started.

## CrystalDiskMark

<img src="../images/comparison/R01-queuecache.png" width="480" alt="CrystalDiskMark, QueueCache 0.4.604.1, RAM disk">

Kept: run 2 (the faster SEQ1M Q8T1 read of two). MB/s.

| Test | Run 1 read / write | Run 2 read / write |
|---|---:|---:|
| SEQ1M Q8T1 | 45,494.49 / 25,015.93 | 45,610.18 / 24,025.50 |
| SEQ1M Q1T1 | 25,333.89 / 16,848.06 | 25,734.94 / 16,831.94 |
| RND4K Q32T1 | 1,445.56 / 1,185.05 | 1,450.68 / 1,184.31 |
| RND4K Q1T1 | 1,612.82 / 1,289.26 | 1,600.88 / 1,269.56 |

## Detailed measurements (DiskSpd)

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| ram-seq1m-q1t1-read | 3 | 25,103.3 (24,966.8–25,137.8) | 23,940 | 0.037 / 0.072 / 0.153 | - / - / - | 96.5 |
| ram-seq1m-q8t1-read | 3 | 45,244.7 (40,777.5–45,320.9) | 43,149 | 0.178 / 0.273 / 1.006 | - / - / - | 92.9 |
| ram-seq1m-q8t4-read | 3 | 25,449.7 (22,666.7–25,472.1) | 24,271 | 0.085 / 2.688 / 9.008 | - / - / - | 100.0 |
| ram-rnd4k-q1t1-read | 3 | 1,610.6 (1,570.4–1,614.9) | 393,209 | 0.002 / 0.006 / 0.022 | - / - / - | 25.5 |
| ram-rnd4k-q32t1-read | 3 | 1,456.7 (1,447.0–1,461.5) | 355,650 | 0.002 / 0.008 / 0.024 | - / - / - | 25.3 |
| ram-rnd4k-q32t4-read | 3 | 5,071.3 (5,032.2–5,096.4) | 1,238,119 | 0.003 / 0.008 / 0.023 | - / - / - | 100.0 |
| ram-seq1m-q1t1-write | 3 | 16,742.9 (16,600.1–16,838.7) | 15,967 | - / - / - | 0.056 / 0.106 / 0.208 | 25.7 |
| ram-seq1m-q8t1-write | 3 | 23,625.3 (22,954.0–24,792.9) | 22,531 | - / - / - | 0.208 / 1.095 / 3.099 | 89.2 |
| ram-seq1m-q8t4-write | 3 | 25,875.7 (24,777.8–26,133.3) | 24,677 | - / - / - | 0.770 / 8.582 / 26.319 | 97.7 |
| ram-rnd4k-q1t1-write | 3 | 1,234.4 (1,224.8–1,261.1) | 301,372 | - / - / - | 0.003 / 0.010 / 0.027 | 25.6 |
| ram-rnd4k-q32t1-write | 3 | 1,156.8 (1,119.1–1,165.8) | 282,415 | - / - / - | 0.003 / 0.012 / 0.028 | 25.3 |
| ram-rnd4k-q32t4-write | 3 | 3,844.1 (3,811.7–3,944.9) | 938,508 | - / - / - | 0.004 / 0.013 / 0.029 | 100.0 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| fill | 1 | MB/s | 14,061.52 | 14,061.52–14,061.52 |
| tree | 3 | create seconds | 4.73 | 4.51–4.89 |
| tree | 3 | copy seconds (4 threads) | 4.51 | 4.45–4.65 |
| tree | 3 | read and hash seconds | 3.00 | 2.94–3.01 |
| tree | 3 | delete seconds | 5.23 | 5.07–5.59 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| before the disk existed | 11,004 | 4,417 | 247 |
| memory-start | 6,927 | 8,576 | 268 |
| memory-before-fill | 6,918 | 8,568 | 270 |
| memory-filled | 6,914 | 8,570 | 270 |
| memory-after-delete | 6,920 | 8,564 | 270 |
| memory-end | 6,650 | 8,827 | 335 |

## Environment

Windows 26H1 build 28000.2956; target Q: NTFS, cluster 4096 bytes, 199.98 GiB.

Loaded third-party drivers: vioscsi.sys 100.100.104.27100 (Red Hat, Inc.), balloon.sys 100.100.104.27100 (Red Hat, Inc.), netkvm.sys 100.100.104.27100 (Red Hat, Inc.), fwcfg.sys 100.100.104.27100 (Red Hat, Inc.); QueueCache: C:\WINDOWS\System32\drivers\QueueCache-0.4.604.1-D9416B70A3A3.sys, C:\WINDOWS\System32\DriverStore\FileRepository\qcramdisk.inf_amd64_fb19ec298857fd7c\qcramdisk.sys.
