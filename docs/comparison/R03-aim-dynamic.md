# AIM Toolkit 20260420, dynamic (RamDyn)

[Back to the comparison](../COMPARISON.md). Status: **Measured**.

| | |
|---|---|
| Product | AIM Toolkit |
| Version | 20260420 |
| Mode | dynamic (RamDyn) |
| Kind | Block device (Arsenal Storport) |
| Price | Free (AGPL) |
| `compare-snapshot` run | `QueueCache-Verify-20261012-101439-c0f74e69081b40e8b1cff91de0971bc1` |
| `compare-ram` run | `QueueCache-Verify-20261012-102554-c0f5c8b3ed76443baf784d93bbc8a6e6` |

## CrystalDiskMark

<img src="../images/comparison/R03-aim-dynamic.png" width="480" alt="CrystalDiskMark, AIM Toolkit 20260420, dynamic (RamDyn)">

Kept: run 1 (the faster SEQ1M Q8T1 read of two). MB/s.

| Test | Run 1 read / write | Run 2 read / write |
|---|---:|---:|
| SEQ1M Q8T1 | 3,838.02 / 3,357.67 | 3,824.41 / 3,312.15 |
| SEQ1M Q1T1 | 3,481.35 / 3,030.51 | 3,280.05 / 2,917.81 |
| RND4K Q32T1 | 177.71 / 177.16 | 178.58 / 176.16 |
| RND4K Q1T1 | 79.15 / 79.87 | 86.05 / 71.23 |

## Detailed measurements (DiskSpd)

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| ram-seq1m-q1t1-read | 3 | 3,227.1 (3,216.3–3,308.8) | 3,078 | 0.324 / 0.424 / 0.561 | - / - / - | 27.5 |
| ram-seq1m-q8t1-read | 3 | 3,738.6 (3,687.4–3,777.5) | 3,565 | 2.219 / 2.565 / 3.129 | - / - / - | 30.9 |
| ram-seq1m-q8t4-read | 3 | 3,711.0 (3,673.4–3,764.9) | 3,539 | 6.748 / 8.151 / 1,003.197 | - / - / - | 31.1 |
| ram-rnd4k-q1t1-read | 3 | 74.6 (72.7–80.4) | 18,213 | 0.054 / 0.083 / 0.118 | - / - / - | 23.8 |
| ram-rnd4k-q32t1-read | 3 | 178.9 (178.4–180.7) | 43,680 | 0.727 / 1.242 / 1.394 | - / - / - | 37.7 |
| ram-rnd4k-q32t4-read | 3 | 179.9 (179.9–180.3) | 43,921 | 2.614 / 7.526 / 18.783 | - / - / - | 37.3 |
| ram-seq1m-q1t1-write | 3 | 2,906.4 (2,868.0–2,964.1) | 2,772 | - / - / - | 0.359 / 0.453 / 0.566 | 27.7 |
| ram-seq1m-q8t1-write | 3 | 3,247.3 (3,215.5–3,255.9) | 3,097 | - / - / - | 2.562 / 2.849 / 4.214 | 30.3 |
| ram-seq1m-q8t4-write | 3 | 3,252.9 (3,233.0–3,263.5) | 3,102 | - / - / - | 7.688 / 8.718 / 789.409 | 30.9 |
| ram-rnd4k-q1t1-write | 3 | 82.4 (69.2–82.7) | 20,126 | - / - / - | 0.048 / 0.069 / 0.116 | 25.4 |
| ram-rnd4k-q32t1-write | 3 | 176.4 (175.1–176.6) | 43,071 | - / - / - | 0.737 / 0.965 / 1.458 | 38.8 |
| ram-rnd4k-q32t4-write | 3 | 174.5 (174.3–174.9) | 42,596 | - / - / - | 2.228 / 26.243 / 100.084 | 40.0 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| fill | 1 | MB/s | 2,473.16 | 2,473.16–2,473.16 |
| tree | 3 | create seconds | 5.22 | 5.04–5.35 |
| tree | 3 | copy seconds (4 threads) | 6.07 | 5.87–6.11 |
| tree | 3 | read and hash seconds | 3.08 | 3.00–3.13 |
| tree | 3 | delete seconds | 5.53 | 5.39–5.61 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| before the disk existed | 13,068 | 2,440 | 258 |
| memory-start | 13,064 | 2,435 | 202 |
| memory-before-fill | 12,100 | 3,424 | 205 |
| memory-filled | 10,051 | 5,471 | 206 |
| memory-after-delete | 10,055 | 5,469 | 204 |
| memory-end | 11,836 | 3,639 | 292 |

## Environment

Windows 26H1 build 28000.2956; target Q: NTFS, cluster 4096 bytes, 199.98 GiB.

Loaded third-party drivers: vioscsi.sys 100.100.104.27100 (Red Hat, Inc.), balloon.sys 100.100.104.27100 (Red Hat, Inc.), netkvm.sys 100.100.104.27100 (Red Hat, Inc.), fwcfg.sys 100.100.104.27100 (Red Hat, Inc.), phdskmnt.sys 1.2.28.93 (Arsenal Recon).
