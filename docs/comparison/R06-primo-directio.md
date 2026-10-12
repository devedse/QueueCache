# Primo Ramdisk 6.6.0, Direct-IO

[Back to the comparison](../COMPARISON.md). Status: **Measured**.

| | |
|---|---|
| Product | Primo Ramdisk |
| Version | 6.6.0 |
| Mode | Direct-IO |
| Kind | Block device |
| Price | $24.95-54.95 (30-day trial used) |
| `compare-snapshot` run | `QueueCache-Verify-20261012-131620-7a3b67797cf64927a992634e3be7157e` |
| `compare-ram` run | `QueueCache-Verify-20261012-132742-609c2939628c497d8bdc55992d90310d` |

## Notes

The run went fine. Large reads from one program reach about 27 GB/s. One odd result: random 4 KiB reads get slower when one program queues many of them (about 0.6 GB/s with 32 queued against 1.5 GB/s one at a time); the SCSI mode does not show this.

## CrystalDiskMark

<img src="../images/comparison/R06-primo-directio.png" width="480" alt="CrystalDiskMark, Primo Ramdisk 6.6.0, Direct-IO">

Kept: run 2 (the faster SEQ1M Q8T1 read of two). MB/s.

| Test | Run 1 read / write | Run 2 read / write |
|---|---:|---:|
| SEQ1M Q8T1 | 27,336.38 / 20,977.13 | 27,747.59 / 21,574.25 |
| SEQ1M Q1T1 | 17,656.65 / 17,313.53 | 17,625.00 / 17,357.83 |
| RND4K Q32T1 | 545.38 / 296.60 | 624.23 / 328.73 |
| RND4K Q1T1 | 1,476.31 / 1,203.17 | 1,456.41 / 1,227.88 |

## Detailed measurements (DiskSpd)

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| ram-seq1m-q1t1-read | 3 | 17,966.8 (17,939.1–18,063.4) | 17,134 | 0.055 / 0.088 / 0.160 | - / - / - | 25.2 |
| ram-seq1m-q8t1-read | 3 | 27,547.6 (26,824.0–27,679.5) | 26,271 | 0.124 / 0.189 / 0.256 | - / - / - | 61.7 |
| ram-seq1m-q8t4-read | 3 | 27,211.2 (27,139.7–27,444.9) | 25,951 | 0.160 / 0.585 / 1.383 | - / - / - | 100.0 |
| ram-rnd4k-q1t1-read | 3 | 1,479.5 (1,478.8–1,481.6) | 361,210 | 0.002 / 0.004 / 0.021 | - / - / - | 25.3 |
| ram-rnd4k-q32t1-read | 3 | 587.3 (547.2–608.7) | 143,386 | 0.017 / 0.091 / 0.132 | - / - / - | 38.0 |
| ram-rnd4k-q32t4-read | 3 | 2,881.7 (2,847.4–2,905.2) | 703,552 | 0.003 / 0.061 / 0.117 | - / - / - | 99.8 |
| ram-seq1m-q1t1-write | 3 | 17,793.5 (17,351.3–17,886.2) | 16,969 | - / - / - | 0.055 / 0.085 / 0.168 | 25.3 |
| ram-seq1m-q8t1-write | 3 | 20,958.0 (20,949.0–20,965.2) | 19,987 | - / - / - | 0.171 / 0.220 / 0.321 | 67.1 |
| ram-seq1m-q8t4-write | 3 | 25,343.6 (25,214.0–25,429.5) | 24,170 | - / - / - | 0.170 / 0.546 / 1.038 | 100.0 |
| ram-rnd4k-q1t1-write | 3 | 1,208.3 (1,206.4–1,211.2) | 294,993 | - / - / - | 0.003 / 0.007 / 0.024 | 25.3 |
| ram-rnd4k-q32t1-write | 3 | 342.3 (337.9–343.2) | 83,568 | - / - / - | 0.031 / 0.069 / 0.117 | 35.9 |
| ram-rnd4k-q32t4-write | 3 | 2,135.2 (2,089.2–2,138.3) | 521,293 | - / - / - | 0.004 / 0.072 / 0.120 | 99.8 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| fill | 1 | MB/s | 12,410.44 | 12,410.44–12,410.44 |
| tree | 3 | create seconds | 4.57 | 4.25–4.78 |
| tree | 3 | copy seconds (4 threads) | 4.88 | 4.64–4.88 |
| tree | 3 | read and hash seconds | 2.97 | 2.93–2.98 |
| tree | 3 | delete seconds | 4.56 | 4.56–4.80 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| before the disk existed | 13,262 | 2,233 | 179 |
| memory-start | 9,190 | 6,338 | 199 |
| memory-before-fill | 9,247 | 6,258 | 199 |
| memory-filled | 9,248 | 6,260 | 199 |
| memory-after-delete | 9,254 | 6,254 | 199 |
| memory-end | 8,852 | 6,625 | 290 |

## Environment

Windows 26H1 build 28000.2956; target Q: NTFS, cluster 4096 bytes, 199.98 GiB.

Loaded third-party drivers: fancyrd.sys 6.6.0 (Romex Software), RXBSKNL.SYS 1.2.0 (Windows (R) Win 7 DDK provider), vioscsi.sys 100.100.104.27100 (Red Hat, Inc.), balloon.sys 100.100.104.27100 (Red Hat, Inc.), netkvm.sys 100.100.104.27100 (Red Hat, Inc.), fwcfg.sys 100.100.104.27100 (Red Hat, Inc.).
