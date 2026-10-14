# Primo Ramdisk 6.6.0, SCSI

[Back to the comparison](../COMPARISON.md). Status: **Measured**.

| | |
|---|---|
| Product | Primo Ramdisk |
| Version | 6.6.0 |
| Mode | SCSI |
| Kind | Block device |
| Price | $24.95-54.95 (30-day trial used) |
| `compare-snapshot` run | `QueueCache-Verify-20261012-191146-ffd6e6cd502544db8b827d5c4c859b06` |
| `compare-ram` run | `QueueCache-Verify-20261012-192301-bc644b9e08ff4f39b28cc4a8d5e78af7` |

## Notes

The run went fine (measured a second time; the first attempt's file-workload times were slowed by virus scanning). Large reads from one program reach about 27 GB/s. Single small requests are about a third slower than Primo's Direct-IO mode, but unlike Direct-IO it keeps up when many requests are queued.

## CrystalDiskMark

<img src="../images/comparison/R06-primo-scsi.png" width="480" alt="CrystalDiskMark, Primo Ramdisk 6.6.0, SCSI">

Kept: run 2 (the faster SEQ1M Q8T1 read of two). MB/s.

| Test | Run 1 read / write | Run 2 read / write |
|---|---:|---:|
| SEQ1M Q8T1 | 25,841.62 / 21,208.92 | 26,403.74 / 20,704.39 |
| SEQ1M Q1T1 | 16,804.75 / 16,981.72 | 16,600.96 / 17,068.31 |
| RND4K Q32T1 | 937.55 / 798.87 | 927.10 / 803.38 |
| RND4K Q1T1 | 979.17 / 835.79 | 975.39 / 833.17 |

## Detailed measurements (DiskSpd)

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| ram-seq1m-q1t1-read | 3 | 17,031.0 (16,939.3–17,061.6) | 16,242 | 0.058 / 0.090 / 0.170 | - / - / - | 25.3 |
| ram-seq1m-q8t1-read | 3 | 26,656.0 (26,093.3–26,854.6) | 25,421 | 0.123 / 0.197 / 0.254 | - / - / - | 61.9 |
| ram-seq1m-q8t4-read | 3 | 27,135.0 (26,745.6–27,159.1) | 25,878 | 0.161 / 0.597 / 1.417 | - / - / - | 100.0 |
| ram-rnd4k-q1t1-read | 3 | 994.2 (989.4–996.3) | 242,729 | 0.004 / 0.012 / 0.025 | - / - / - | 25.2 |
| ram-rnd4k-q32t1-read | 3 | 941.4 (933.6–948.1) | 229,841 | 0.004 / 0.012 / 0.027 | - / - / - | 25.5 |
| ram-rnd4k-q32t4-read | 3 | 3,392.2 (3,368.6–3,412.6) | 828,164 | 0.004 / 0.014 / 0.026 | - / - / - | 100.0 |
| ram-seq1m-q1t1-write | 3 | 17,440.4 (17,411.6–17,460.0) | 16,632 | - / - / - | 0.057 / 0.085 / 0.145 | 25.6 |
| ram-seq1m-q8t1-write | 3 | 20,987.8 (20,446.4–21,164.6) | 20,016 | - / - / - | 0.169 / 0.230 / 0.301 | 66.3 |
| ram-seq1m-q8t4-write | 3 | 24,997.9 (24,988.9–25,508.7) | 23,840 | - / - / - | 0.171 / 0.561 / 1.127 | 100.0 |
| ram-rnd4k-q1t1-write | 3 | 827.7 (826.4–833.4) | 202,067 | - / - / - | 0.005 / 0.015 / 0.029 | 25.4 |
| ram-rnd4k-q32t1-write | 3 | 805.9 (803.4–807.7) | 196,751 | - / - / - | 0.005 / 0.016 / 0.030 | 25.4 |
| ram-rnd4k-q32t4-write | 3 | 2,913.1 (2,906.6–2,934.0) | 711,203 | - / - / - | 0.005 / 0.018 / 0.030 | 100.0 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| fill | 1 | MB/s | 11,701.61 | 11,701.61–11,701.61 |
| tree | 3 | create seconds | 4.57 | 4.53–4.99 |
| tree | 3 | copy seconds (4 threads) | 4.67 | 4.64–4.68 |
| tree | 3 | read and hash seconds | 2.97 | 2.91–2.98 |
| tree | 3 | delete seconds | 4.57 | 4.56–4.58 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| before the disk existed | 13,086 | 2,360 | 198 |
| memory-start | 9,092 | 6,393 | 214 |
| memory-before-fill | 9,131 | 6,345 | 215 |
| memory-filled | 9,132 | 6,341 | 215 |
| memory-after-delete | 9,133 | 6,340 | 215 |
| memory-end | 8,760 | 6,676 | 302 |

## Environment

Windows 26H1 build 28000.2956; target Q: NTFS, cluster 4096 bytes, 199.98 GiB.

Loaded third-party drivers: fancyrd.sys 6.6.0 (Romex Software), RXBSKNL.SYS 1.2.0 (Windows (R) Win 7 DDK provider), vioscsi.sys 100.100.104.27100 (Red Hat, Inc.), balloon.sys 100.100.104.27100 (Red Hat, Inc.), netkvm.sys 100.100.104.27100 (Red Hat, Inc.), fwcfg.sys 100.100.104.27100 (Red Hat, Inc.).
