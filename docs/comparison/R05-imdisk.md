# ImDisk Toolkit 20250206, AWE

[Back to the comparison](../COMPARISON.md). Status: **Measured**.

| | |
|---|---|
| Product | ImDisk Toolkit |
| Version | 20250206 |
| Mode | AWE |
| Kind | Block device (legacy driver) |
| Price | Free |
| `compare-snapshot` run | `QueueCache-Verify-20261012-123557-b4230aa6ee2f4a4590fcdaa6eadfd6c1` |
| `compare-ram` run | `QueueCache-Verify-20261012-124710-1e056551eec1469a8c8dd1d6ab995025` |

## Notes

The run went fine. Large reads reach about 15 GB/s, but small requests are slow: about 0.6 GB/s for single 4 KiB requests, and four programs at once add little (about 1 GB/s in total), so this older driver does not spread work over several processors. The standard install leaves out the driver that this mode (AWE, memory that cannot be swapped out) needs; it had to be added by hand.

## CrystalDiskMark

<img src="../images/comparison/R05-imdisk.png" width="480" alt="CrystalDiskMark, ImDisk Toolkit 20250206, AWE">

Kept: run 2 (the faster SEQ1M Q8T1 read of two). MB/s.

| Test | Run 1 read / write | Run 2 read / write |
|---|---:|---:|
| SEQ1M Q8T1 | 14,515.97 / 16,829.37 | 14,555.75 / 16,840.72 |
| SEQ1M Q1T1 | 14,559.83 / 16,950.43 | 14,546.43 / 16,787.07 |
| RND4K Q32T1 | 584.49 / 542.77 | 590.90 / 551.09 |
| RND4K Q1T1 | 579.37 / 554.02 | 577.29 / 551.42 |

## Detailed measurements (DiskSpd)

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| ram-seq1m-q1t1-read | 3 | 14,800.2 (14,688.3–14,849.3) | 14,115 | 0.068 / 0.097 / 0.205 | - / - / - | 25.5 |
| ram-seq1m-q8t1-read | 3 | 14,887.7 (14,743.9–14,906.9) | 14,198 | 0.067 / 0.097 / 0.179 | - / - / - | 25.3 |
| ram-seq1m-q8t4-read | 3 | 20,128.0 (20,051.5–20,216.1) | 19,196 | 0.188 / 0.259 / 0.973 | - / - / - | 100.0 |
| ram-rnd4k-q1t1-read | 3 | 584.3 (583.4–597.5) | 142,649 | 0.005 / 0.020 / 0.078 | - / - / - | 27.3 |
| ram-rnd4k-q32t1-read | 3 | 548.3 (546.3–550.9) | 133,854 | 0.006 / 0.019 / 0.038 | - / - / - | 25.6 |
| ram-rnd4k-q32t4-read | 3 | 981.9 (969.9–1,039.0) | 239,730 | 0.007 / 0.043 / 0.076 | - / - / - | 100.0 |
| ram-seq1m-q1t1-write | 3 | 17,102.3 (17,094.2–17,152.5) | 16,310 | - / - / - | 0.059 / 0.086 / 0.175 | 25.5 |
| ram-seq1m-q8t1-write | 3 | 17,107.9 (16,985.0–17,448.1) | 16,315 | - / - / - | 0.059 / 0.087 / 0.160 | 25.2 |
| ram-seq1m-q8t4-write | 3 | 22,247.0 (22,209.7–22,328.3) | 21,216 | - / - / - | 0.168 / 0.230 / 0.723 | 100.0 |
| ram-rnd4k-q1t1-write | 3 | 536.6 (530.6–553.1) | 131,011 | - / - / - | 0.006 / 0.021 / 0.057 | 26.5 |
| ram-rnd4k-q32t1-write | 3 | 515.7 (514.2–518.0) | 125,901 | - / - / - | 0.006 / 0.020 / 0.041 | 25.7 |
| ram-rnd4k-q32t4-write | 3 | 988.8 (983.7–1,002.3) | 241,407 | - / - / - | 0.007 / 0.044 / 0.079 | 100.0 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| fill | 1 | MB/s | 12,324.40 | 12,324.40–12,324.40 |
| tree | 3 | create seconds | 4.52 | 4.21–4.75 |
| tree | 3 | copy seconds (4 threads) | 4.43 | 4.40–4.53 |
| tree | 3 | read and hash seconds | 2.92 | 2.92–2.95 |
| tree | 3 | delete seconds | 4.37 | 4.33–4.46 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| before the disk existed | 13,288 | 2,217 | 182 |
| memory-start | 9,140 | 6,308 | 195 |
| memory-before-fill | 7,576 | 6,642 | 196 |
| memory-filled | 7,573 | 6,647 | 196 |
| memory-after-delete | 9,179 | 6,247 | 196 |
| memory-end | 8,804 | 6,609 | 284 |

## Environment

Windows 26H1 build 28000.2956; target Q: NTFS, cluster 4096 bytes, 199.98 GiB.

Loaded third-party drivers: vioscsi.sys 100.100.104.27100 (Red Hat, Inc.), balloon.sys 100.100.104.27100 (Red Hat, Inc.), netkvm.sys 100.100.104.27100 (Red Hat, Inc.), fwcfg.sys 100.100.104.27100 (Red Hat, Inc.), awealloc.sys 2.1.1.24 (Olof Lagerkvist).
