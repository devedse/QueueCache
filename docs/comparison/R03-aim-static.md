# AIM Toolkit 20260420, static

[Back to the comparison](../COMPARISON.md). Status: **Measured**.

| | |
|---|---|
| Product | AIM Toolkit |
| Version | 20260420 |
| Mode | static |
| Kind | Block device (Arsenal Storport) |
| Price | Free (AGPL) |
| `compare-snapshot` run | `QueueCache-Verify-20261012-095410-a1f62216046646f09fb9d381fb1ad1ea` |
| `compare-ram` run | `QueueCache-Verify-20261012-100536-12753d7fc9764f4f990496f542163c93` |

## CrystalDiskMark

<img src="../images/comparison/R03-aim-static.png" width="480" alt="CrystalDiskMark, AIM Toolkit 20260420, static">

Kept: run 1 (the faster SEQ1M Q8T1 read of two). MB/s.

| Test | Run 1 read / write | Run 2 read / write |
|---|---:|---:|
| SEQ1M Q8T1 | 4,435.30 / 4,325.95 | 4,398.84 / 4,273.19 |
| SEQ1M Q1T1 | 3,801.83 / 3,858.91 | 3,653.08 / 3,749.35 |
| RND4K Q32T1 | 227.03 / 223.19 | 227.39 / 221.51 |
| RND4K Q1T1 | 93.81 / 80.12 | 93.83 / 90.49 |

## Detailed measurements (DiskSpd)

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| ram-seq1m-q1t1-read | 3 | 3,773.4 (3,710.1–3,817.8) | 3,599 | 0.282 / 0.353 / 0.477 | - / - / - | 29.0 |
| ram-seq1m-q8t1-read | 3 | 4,428.3 (4,344.0–4,435.7) | 4,223 | 1.876 / 2.164 / 2.885 | - / - / - | 31.9 |
| ram-seq1m-q8t4-read | 3 | 4,391.8 (4,338.5–4,425.1) | 4,188 | 5.657 / 6.559 / 10.817 | - / - / - | 32.1 |
| ram-rnd4k-q1t1-read | 3 | 81.7 (78.8–95.6) | 19,958 | 0.049 / 0.077 / 0.109 | - / - / - | 24.2 |
| ram-rnd4k-q32t1-read | 3 | 226.9 (226.9–227.0) | 55,401 | 0.574 / 0.662 / 1.023 | - / - / - | 40.7 |
| ram-rnd4k-q32t4-read | 3 | 219.6 (219.5–221.6) | 53,623 | 1.774 / 2.038 / 2.557 | - / - / - | 42.6 |
| ram-seq1m-q1t1-write | 3 | 3,702.9 (3,693.6–3,847.9) | 3,531 | - / - / - | 0.283 / 0.369 / 0.458 | 28.9 |
| ram-seq1m-q8t1-write | 3 | 4,385.4 (4,329.8–4,443.7) | 4,182 | - / - / - | 1.898 / 2.154 / 2.767 | 32.0 |
| ram-seq1m-q8t4-write | 3 | 4,417.4 (4,393.1–4,456.2) | 4,213 | - / - / - | 5.643 / 6.364 / 821.060 | 32.5 |
| ram-rnd4k-q1t1-write | 3 | 90.5 (89.8–90.9) | 22,095 | - / - / - | 0.044 / 0.058 / 0.092 | 25.3 |
| ram-rnd4k-q32t1-write | 3 | 220.1 (218.6–221.6) | 53,738 | - / - / - | 0.591 / 0.663 / 0.877 | 42.0 |
| ram-rnd4k-q32t4-write | 3 | 216.6 (215.3–216.8) | 52,884 | - / - / - | 1.812 / 1.970 / 2.499 | 43.4 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| fill | 1 | MB/s | 3,076.29 | 3,076.29–3,076.29 |
| tree | 3 | create seconds | 5.13 | 5.02–5.21 |
| tree | 3 | copy seconds (4 threads) | 5.64 | 5.57–5.94 |
| tree | 3 | read and hash seconds | 2.99 | 2.94–3.02 |
| tree | 3 | delete seconds | 5.39 | 5.24–5.48 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| before the disk existed | 13,368 | 2,167 | 180 |
| memory-start | 12,337 | 6,277 | 186 |
| memory-before-fill | 11,363 | 6,224 | 189 |
| memory-filled | 10,313 | 6,225 | 190 |
| memory-after-delete | 10,315 | 6,224 | 188 |
| memory-end | 9,022 | 6,570 | 272 |

## Environment

Windows 26H1 build 28000.2956; target Q: NTFS, cluster 4096 bytes, 199.98 GiB.

Loaded third-party drivers: vioscsi.sys 100.100.104.27100 (Red Hat, Inc.), balloon.sys 100.100.104.27100 (Red Hat, Inc.), netkvm.sys 100.100.104.27100 (Red Hat, Inc.), fwcfg.sys 100.100.104.27100 (Red Hat, Inc.), phdskmnt.sys 1.2.28.93 (Arsenal Recon).
