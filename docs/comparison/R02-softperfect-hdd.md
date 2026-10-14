# SoftPerfect RAM Disk 26.10, hard-disk emulation

[Back to the comparison](../COMPARISON.md). Status: **Measured**.

| | |
|---|---|
| Product | SoftPerfect RAM Disk |
| Version | 26.10 |
| Mode | hard-disk emulation |
| Kind | Block device |
| Price | $29 per PC (30-day trial used) |
| `compare-snapshot` run | `QueueCache-Verify-20261012-090507-b8aecb09bae74436aabc7636c77c43c5` |
| `compare-ram` run | `QueueCache-Verify-20261012-091648-98c3352dd21d4000ba9e91adfed46b65` |

## Notes

The run went fine. Hard-disk emulation (the disk appears as a normal disk with a partition) is a bit faster than SoftPerfect's default mode for large reads, but about a third slower for small 4 KiB requests.

## CrystalDiskMark

<img src="../images/comparison/R02-softperfect-hdd.png" width="480" alt="CrystalDiskMark, SoftPerfect RAM Disk 26.10, hard-disk emulation">

Kept: run 1 (the faster SEQ1M Q8T1 read of two). MB/s.

| Test | Run 1 read / write | Run 2 read / write |
|---|---:|---:|
| SEQ1M Q8T1 | 34,473.56 / 21,945.12 | 32,801.55 / 20,661.04 |
| SEQ1M Q1T1 | 13,470.43 / 10,927.40 | 14,190.55 / 12,237.10 |
| RND4K Q32T1 | 973.65 / 873.08 | 978.74 / 856.30 |
| RND4K Q1T1 | 1,040.51 / 908.69 | 1,048.10 / 907.73 |

## Detailed measurements (DiskSpd)

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| ram-seq1m-q1t1-read | 3 | 14,146.2 (12,526.0–14,184.8) | 13,491 | 0.071 / 0.108 / 0.168 | - / - / - | 25.3 |
| ram-seq1m-q8t1-read | 3 | 35,386.2 (35,199.6–35,740.6) | 33,747 | 0.080 / 0.192 / 0.255 | - / - / - | 67.2 |
| ram-seq1m-q8t4-read | 3 | 29,850.7 (29,845.7–30,395.2) | 28,468 | 0.152 / 0.581 / 1.148 | - / - / - | 100.0 |
| ram-rnd4k-q1t1-read | 3 | 1,052.8 (1,033.9–1,057.4) | 257,024 | 0.004 / 0.011 / 0.028 | - / - / - | 25.3 |
| ram-rnd4k-q32t1-read | 3 | 986.0 (985.4–995.8) | 240,727 | 0.004 / 0.012 / 0.030 | - / - / - | 25.2 |
| ram-rnd4k-q32t4-read | 3 | 3,481.8 (3,474.2–3,490.4) | 850,046 | 0.004 / 0.014 / 0.027 | - / - / - | 100.0 |
| ram-seq1m-q1t1-write | 3 | 10,746.9 (10,445.5–11,852.2) | 10,249 | - / - / - | 0.096 / 0.132 / 0.185 | 24.3 |
| ram-seq1m-q8t1-write | 3 | 21,316.9 (20,893.8–21,954.0) | 20,329 | - / - / - | 0.165 / 0.236 / 0.306 | 65.1 |
| ram-seq1m-q8t4-write | 3 | 26,002.7 (25,585.9–26,112.8) | 24,798 | - / - / - | 0.177 / 0.594 / 0.994 | 100.0 |
| ram-rnd4k-q1t1-write | 3 | 921.4 (920.4–921.6) | 224,951 | - / - / - | 0.004 / 0.014 / 0.030 | 25.3 |
| ram-rnd4k-q32t1-write | 3 | 886.2 (883.7–886.6) | 216,351 | - / - / - | 0.004 / 0.014 / 0.030 | 25.3 |
| ram-rnd4k-q32t4-write | 3 | 3,023.4 (2,987.1–3,032.5) | 738,127 | - / - / - | 0.005 / 0.018 / 0.032 | 99.8 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| fill | 1 | MB/s | 8,768.08 | 8,768.08–8,768.08 |
| tree | 3 | create seconds | 4.42 | 4.29–4.52 |
| tree | 3 | copy seconds (4 threads) | 4.49 | 4.47–4.90 |
| tree | 3 | read and hash seconds | 2.94 | 2.91–2.94 |
| tree | 3 | delete seconds | 4.44 | 4.39–4.58 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| before the disk existed | 13,038 | 2,451 | 258 |
| memory-start | 8,975 | 6,534 | 208 |
| memory-before-fill | 9,025 | 6,502 | 209 |
| memory-filled | 9,029 | 6,497 | 209 |
| memory-after-delete | 9,031 | 6,494 | 209 |
| memory-end | 8,847 | 6,647 | 296 |

## Environment

Windows 26H1 build 28000.2956; target Q: NTFS, cluster 4096 bytes, 199.98 GiB.

Loaded third-party drivers: vioscsi.sys 100.100.104.27100 (Red Hat, Inc.), spvdbus.sys 26.10.0.0 (SoftPerfect Pty Ltd), spvve.sys 26.10.0.0 (SoftPerfect Pty Ltd), balloon.sys 100.100.104.27100 (Red Hat, Inc.), netkvm.sys 100.100.104.27100 (Red Hat, Inc.), fwcfg.sys 100.100.104.27100 (Red Hat, Inc.).
