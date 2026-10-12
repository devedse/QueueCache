# OSFMount 3.3.1000, physical emulation

[Back to the comparison](../COMPARISON.md). Status: **Measured**.

| | |
|---|---|
| Product | OSFMount |
| Version | 3.3.1000 |
| Mode | physical emulation |
| Kind | Block device |
| Price | Free |
| `compare-snapshot` run | `QueueCache-Verify-20261012-114124-18bd51032e614424883f1627a57185d0` |
| `compare-ram` run | `QueueCache-Verify-20261012-115310-732d18d060e648ae999fd8ad59ecdbf7` |

## Notes

The run went fine, but the file-workload times are not comparable yet: this disk first appears under another drive letter and is then moved to T:, and Windows Defender kept scanning its files, which slows the read step. This mode will be run again with that fixed. The speed measurements are not affected: about 15.5 GB/s for large reads and roughly half of OSFMount's logical mode for small requests.

## CrystalDiskMark

<img src="../images/comparison/R04-osfmount-physical.png" width="480" alt="CrystalDiskMark, OSFMount 3.3.1000, physical emulation">

Kept: run 1 (the faster SEQ1M Q8T1 read of two). MB/s.

| Test | Run 1 read / write | Run 2 read / write |
|---|---:|---:|
| SEQ1M Q8T1 | 15,432.18 / 16,612.65 | 15,273.92 / 16,502.65 |
| SEQ1M Q1T1 | 15,421.62 / 16,820.43 | 15,413.61 / 16,865.22 |
| RND4K Q32T1 | 901.65 / 785.84 | 896.10 / 786.45 |
| RND4K Q1T1 | 964.78 / 817.62 | 963.00 / 815.25 |

## Detailed measurements (DiskSpd)

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| ram-seq1m-q1t1-read | 3 | 15,728.7 (15,664.4–15,832.7) | 15,000 | 0.062 / 0.099 / 0.162 | - / - / - | 25.4 |
| ram-seq1m-q8t1-read | 3 | 15,536.9 (15,508.3–15,614.8) | 14,817 | 0.062 / 0.106 / 0.167 | - / - / - | 25.4 |
| ram-seq1m-q8t4-read | 3 | 23,018.9 (22,969.2–23,137.7) | 21,953 | 0.181 / 0.232 / 0.391 | - / - / - | 100.0 |
| ram-rnd4k-q1t1-read | 3 | 980.3 (974.7–984.0) | 239,321 | 0.004 / 0.013 / 0.032 | - / - / - | 25.3 |
| ram-rnd4k-q32t1-read | 3 | 912.0 (908.7–914.4) | 222,647 | 0.004 / 0.015 / 0.041 | - / - / - | 25.2 |
| ram-rnd4k-q32t4-read | 3 | 3,135.2 (3,124.2–3,155.8) | 765,425 | 0.005 / 0.017 / 0.039 | - / - / - | 100.0 |
| ram-seq1m-q1t1-write | 3 | 17,279.0 (17,263.5–17,288.7) | 16,478 | - / - / - | 0.056 / 0.093 / 0.139 | 25.6 |
| ram-seq1m-q8t1-write | 3 | 17,122.5 (17,080.6–17,154.7) | 16,329 | - / - / - | 0.057 / 0.101 / 0.149 | 25.4 |
| ram-seq1m-q8t4-write | 3 | 25,050.7 (24,843.4–26,513.1) | 23,890 | - / - / - | 0.166 / 0.224 / 0.365 | 100.0 |
| ram-rnd4k-q1t1-write | 3 | 821.7 (802.9–823.3) | 200,615 | - / - / - | 0.005 / 0.017 / 0.034 | 25.2 |
| ram-rnd4k-q32t1-write | 3 | 783.8 (772.3–790.5) | 191,363 | - / - / - | 0.005 / 0.019 / 0.043 | 25.3 |
| ram-rnd4k-q32t4-write | 3 | 2,728.6 (2,480.0–2,731.8) | 666,155 | - / - / - | 0.005 / 0.020 / 0.042 | 100.0 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| fill | 1 | MB/s | 14,361.55 | 14,361.55–14,361.55 |
| tree | 3 | create seconds | 47.35 | 45.55–47.51 |
| tree | 3 | copy seconds (4 threads) | 12.14 | 11.90–12.28 |
| tree | 3 | read and hash seconds | 148.53 | 148.22–148.99 |
| tree | 3 | delete seconds | 10.19 | 10.15–10.30 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| before the disk existed | 12,982 | 2,543 | 267 |
| memory-start | 8,946 | 6,589 | 219 |
| memory-before-fill | 8,991 | 6,558 | 220 |
| memory-filled | 8,988 | 6,562 | 220 |
| memory-after-delete | 8,994 | 6,553 | 220 |
| memory-end | 8,830 | 6,628 | 229 |

## Environment

Windows 26H1 build 28000.2956; target Q: NTFS, cluster 4096 bytes, 199.98 GiB.

Loaded third-party drivers: vioscsi.sys 100.100.104.27100 (Red Hat, Inc.), osfdisk.sys 1.2.0.0 (PassMark Software), balloon.sys 100.100.104.27100 (Red Hat, Inc.), netkvm.sys 100.100.104.27100 (Red Hat, Inc.), fwcfg.sys 100.100.104.27100 (Red Hat, Inc.), OSFMount.sys 2.0.1001.0 (PassMark Software).
