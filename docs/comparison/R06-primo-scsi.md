# Primo Ramdisk 6.6.0, SCSI

[Back to the comparison](../COMPARISON.md). Status: **Measured**.

| | |
|---|---|
| Product | Primo Ramdisk |
| Version | 6.6.0 |
| Mode | SCSI |
| Kind | Block device |
| Price | $24.95-54.95 (30-day trial used) |
| `compare-snapshot` run | `QueueCache-Verify-20261012-133637-c903576c3b80473bb95495462e8587cb` |
| `compare-ram` run | `QueueCache-Verify-20261012-134746-9d2aff305a124d3e8bef2d3b8835adba` |

## Notes

The run went fine, but the file-workload times are not comparable yet: Windows Defender kept scanning this disk's files, as with OSFMount's physical mode, which slows the read step. This mode will be run again with that fixed. The speed measurements are not affected: about 28 GB/s for large reads, and small requests about a third slower than Primo's Direct-IO mode one at a time but faster when many are queued.

## CrystalDiskMark

<img src="../images/comparison/R06-primo-scsi.png" width="480" alt="CrystalDiskMark, Primo Ramdisk 6.6.0, SCSI">

Kept: run 1 (the faster SEQ1M Q8T1 read of two). MB/s.

| Test | Run 1 read / write | Run 2 read / write |
|---|---:|---:|
| SEQ1M Q8T1 | 30,309.57 / 20,561.64 | 30,029.91 / 21,886.15 |
| SEQ1M Q1T1 | 18,062.38 / 16,668.49 | 18,108.02 / 16,713.68 |
| RND4K Q32T1 | 940.98 / 828.10 | 952.11 / 830.11 |
| RND4K Q1T1 | 979.12 / 860.19 | 983.37 / 871.83 |

## Detailed measurements (DiskSpd)

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| ram-seq1m-q1t1-read | 3 | 17,793.2 (17,672.1–17,820.2) | 16,969 | 0.055 / 0.087 / 0.164 | - / - / - | 25.4 |
| ram-seq1m-q8t1-read | 3 | 28,590.8 (28,121.6–28,921.3) | 27,266 | 0.112 / 0.202 / 0.250 | - / - / - | 65.3 |
| ram-seq1m-q8t4-read | 3 | 26,952.9 (26,202.4–27,184.8) | 25,704 | 0.163 / 0.601 / 1.363 | - / - / - | 100.0 |
| ram-rnd4k-q1t1-read | 3 | 992.9 (991.8–996.6) | 242,412 | 0.004 / 0.012 / 0.025 | - / - / - | 25.4 |
| ram-rnd4k-q32t1-read | 3 | 964.6 (957.5–969.0) | 235,498 | 0.004 / 0.013 / 0.027 | - / - / - | 25.4 |
| ram-rnd4k-q32t4-read | 3 | 3,426.7 (3,403.8–3,433.8) | 836,607 | 0.004 / 0.014 / 0.026 | - / - / - | 100.0 |
| ram-seq1m-q1t1-write | 3 | 17,069.3 (17,015.5–17,146.6) | 16,279 | - / - / - | 0.058 / 0.087 / 0.148 | 25.3 |
| ram-seq1m-q8t1-write | 3 | 20,500.0 (20,422.9–20,749.2) | 19,550 | - / - / - | 0.173 / 0.235 / 0.289 | 66.9 |
| ram-seq1m-q8t4-write | 3 | 24,929.6 (24,632.4–25,061.7) | 23,775 | - / - / - | 0.174 / 0.544 / 1.045 | 100.0 |
| ram-rnd4k-q1t1-write | 3 | 859.9 (859.6–862.5) | 209,927 | - / - / - | 0.004 / 0.015 / 0.029 | 25.2 |
| ram-rnd4k-q32t1-write | 3 | 824.0 (819.4–827.8) | 201,178 | - / - / - | 0.005 / 0.016 / 0.030 | 25.2 |
| ram-rnd4k-q32t4-write | 3 | 2,936.8 (2,844.9–2,945.7) | 716,996 | - / - / - | 0.005 / 0.018 / 0.030 | 100.0 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| fill | 1 | MB/s | 13,927.36 | 13,927.36–13,927.36 |
| tree | 3 | create seconds | 47.08 | 44.29–47.15 |
| tree | 3 | copy seconds (4 threads) | 12.44 | 11.98–12.50 |
| tree | 3 | read and hash seconds | 147.63 | 147.48–152.92 |
| tree | 3 | delete seconds | 10.37 | 10.26–10.50 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| before the disk existed | 12,986 | 2,488 | 260 |
| memory-start | 8,990 | 6,527 | 216 |
| memory-before-fill | 9,039 | 6,489 | 216 |
| memory-filled | 9,034 | 6,494 | 216 |
| memory-after-delete | 9,039 | 6,487 | 216 |
| memory-end | 8,814 | 6,594 | 224 |

## Environment

Windows 26H1 build 28000.2956; target Q: NTFS, cluster 4096 bytes, 199.98 GiB.

Loaded third-party drivers: fancyrd.sys 6.6.0 (Romex Software), RXBSKNL.SYS 1.2.0 (Windows (R) Win 7 DDK provider), vioscsi.sys 100.100.104.27100 (Red Hat, Inc.), balloon.sys 100.100.104.27100 (Red Hat, Inc.), netkvm.sys 100.100.104.27100 (Red Hat, Inc.), fwcfg.sys 100.100.104.27100 (Red Hat, Inc.).
