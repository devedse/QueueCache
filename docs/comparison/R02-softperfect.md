# SoftPerfect RAM Disk 26.10, static

[Back to the comparison](../COMPARISON.md). Status: **Measured**.

| | |
|---|---|
| Product | SoftPerfect RAM Disk |
| Version | 26.10 |
| Mode | static |
| Kind | Block device |
| Price | $29 per PC (30-day trial used) |
| `compare-snapshot` run | `QueueCache-Verify-20261012-084419-3a6cbb6b305249ca9cc59bdbec6d0c43` |
| `compare-ram` run | `QueueCache-Verify-20261012-085608-fccbc641c40f4a29869071ce3aba2868` |

## CrystalDiskMark

<img src="../images/comparison/R02-softperfect.png" width="480" alt="CrystalDiskMark, SoftPerfect RAM Disk 26.10, static">

Kept: run 2 (the faster SEQ1M Q8T1 read of two). MB/s.

| Test | Run 1 read / write | Run 2 read / write |
|---|---:|---:|
| SEQ1M Q8T1 | 29,719.72 / 21,958.67 | 30,054.11 / 20,941.77 |
| SEQ1M Q1T1 | 13,894.59 / 12,144.81 | 13,654.70 / 12,967.63 |
| RND4K Q32T1 | 1,502.62 / 1,272.07 | 1,484.23 / 1,256.63 |
| RND4K Q1T1 | 1,563.36 / 1,291.79 | 1,544.25 / 1,278.12 |

## Detailed measurements (DiskSpd)

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| ram-seq1m-q1t1-read | 3 | 12,170.6 (11,731.3–12,262.0) | 11,607 | 0.084 / 0.124 / 0.176 | - / - / - | 24.3 |
| ram-seq1m-q8t1-read | 3 | 30,465.2 (28,808.7–30,588.3) | 29,054 | 0.108 / 0.180 / 0.225 | - / - / - | 63.8 |
| ram-seq1m-q8t4-read | 3 | 30,595.2 (29,940.4–30,947.4) | 29,178 | 0.145 / 0.543 / 1.245 | - / - / - | 100.0 |
| ram-rnd4k-q1t1-read | 3 | 1,585.0 (1,580.9–1,587.8) | 386,969 | 0.002 / 0.004 / 0.022 | - / - / - | 25.4 |
| ram-rnd4k-q32t1-read | 3 | 1,499.6 (1,496.9–1,500.6) | 366,112 | 0.002 / 0.004 / 0.023 | - / - / - | 25.3 |
| ram-rnd4k-q32t4-read | 3 | 5,182.0 (5,180.7–5,197.2) | 1,265,133 | 0.003 / 0.007 / 0.022 | - / - / - | 100.0 |
| ram-seq1m-q1t1-write | 3 | 12,950.9 (10,813.6–12,990.7) | 12,351 | - / - / - | 0.078 / 0.105 / 0.157 | 25.4 |
| ram-seq1m-q8t1-write | 3 | 20,990.3 (20,860.8–21,700.8) | 20,018 | - / - / - | 0.170 / 0.223 / 0.280 | 66.0 |
| ram-seq1m-q8t4-write | 3 | 25,267.2 (25,044.7–25,765.5) | 24,097 | - / - / - | 0.170 / 0.615 / 0.995 | 100.0 |
| ram-rnd4k-q1t1-write | 3 | 1,300.9 (1,299.3–1,307.2) | 317,602 | - / - / - | 0.003 / 0.005 / 0.024 | 25.4 |
| ram-rnd4k-q32t1-write | 3 | 1,286.9 (1,275.8–1,287.0) | 314,184 | - / - / - | 0.003 / 0.005 / 0.024 | 25.4 |
| ram-rnd4k-q32t4-write | 3 | 4,224.4 (4,191.3–4,234.5) | 1,031,357 | - / - / - | 0.003 / 0.013 / 0.029 | 99.5 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| fill | 1 | MB/s | 9,173.60 | 9,173.60–9,173.60 |
| tree | 3 | create seconds | 4.47 | 4.41–4.58 |
| tree | 3 | copy seconds (4 threads) | 4.44 | 4.39–4.54 |
| tree | 3 | read and hash seconds | 2.96 | 2.90–2.96 |
| tree | 3 | delete seconds | 4.51 | 4.41–4.53 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| before the disk existed | 13,299 | 2,196 | 178 |
| memory-start | 9,214 | 6,313 | 191 |
| memory-before-fill | 9,262 | 6,250 | 192 |
| memory-filled | 9,256 | 6,255 | 192 |
| memory-after-delete | 9,257 | 6,253 | 192 |
| memory-end | 8,898 | 6,595 | 279 |

## Environment

Windows 26H1 build 28000.2956; target Q: NTFS, cluster 4096 bytes, 199.98 GiB.

Loaded third-party drivers: vioscsi.sys 100.100.104.27100 (Red Hat, Inc.), spvdbus.sys 26.10.0.0 (SoftPerfect Pty Ltd), spvve.sys 26.10.0.0 (SoftPerfect Pty Ltd), balloon.sys 100.100.104.27100 (Red Hat, Inc.), netkvm.sys 100.100.104.27100 (Red Hat, Inc.), fwcfg.sys 100.100.104.27100 (Red Hat, Inc.).
