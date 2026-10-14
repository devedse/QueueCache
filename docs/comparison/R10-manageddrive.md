# ManagedDrive 2.3.0, WinFsp 2.1

[Back to the comparison](../COMPARISON.md). Status: **Measured**.

| | |
|---|---|
| Product | ManagedDrive |
| Version | 2.3.0 |
| Mode | WinFsp 2.1 |
| Kind | File system (WinFsp, user mode) |
| Price | Free (MIT) |
| `compare-snapshot` run | `QueueCache-Verify-20261012-174937-38d934579c504e669c98b94472523819` |
| `compare-ram` run | `QueueCache-Verify-20261012-180140-10af9b4737194d55ad82c6066c512116` |

## Notes

The run went fine. This is a file system in RAM that runs as an ordinary program (through WinFsp) rather than as a driver, and it is the slowest here: about 3 GB/s for large reads, under 0.1 GB/s for single small requests, and two to four times slower than the RAM disks at creating and deleting many files. Its drive is only visible to the user who started the app, and Windows Defender's folder exclusion does not cover it, so for these measurements the benchmark program itself was excluded from scanning instead.

## CrystalDiskMark

<img src="../images/comparison/R10-manageddrive.png" width="480" alt="CrystalDiskMark, ManagedDrive 2.3.0, WinFsp 2.1">

Kept: run 1 (the faster SEQ1M Q8T1 read of two). MB/s.

| Test | Run 1 read / write | Run 2 read / write |
|---|---:|---:|
| SEQ1M Q8T1 | 3,992.38 / 8,925.17 | 3,294.48 / 8,725.49 |
| SEQ1M Q1T1 | 3,026.58 / 6,978.19 | 3,451.35 / 7,029.95 |
| RND4K Q32T1 | 87.17 / 109.87 | 87.18 / 116.16 |
| RND4K Q1T1 | 84.94 / 78.98 | 88.24 / 74.95 |

## Detailed measurements (DiskSpd)

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| ram-seq1m-q1t1-read | 3 | 2,745.3 (2,625.0–2,826.4) | 2,618 | 0.398 / 0.655 / 1.137 | - / - / - | 30.1 |
| ram-seq1m-q8t1-read | 3 | 3,208.5 (2,694.7–3,394.2) | 3,060 | 2.298 / 5.064 / 5.879 | - / - / - | 33.7 |
| ram-seq1m-q8t4-read | 3 | 2,697.7 (2,693.0–3,087.5) | 2,573 | 12.182 / 21.261 / 28.213 | - / - / - | 33.7 |
| ram-rnd4k-q1t1-read | 3 | 84.4 (82.6–88.5) | 20,606 | 0.047 / 0.080 / 0.176 | - / - / - | 28.4 |
| ram-rnd4k-q32t1-read | 3 | 85.7 (84.9–86.0) | 20,932 | 1.476 / 2.149 / 4.011 | - / - / - | 44.5 |
| ram-rnd4k-q32t4-read | 3 | 93.1 (83.0–100.1) | 22,731 | 4.139 / 5.427 / 6.529 | - / - / - | 87.7 |
| ram-seq1m-q1t1-write | 3 | 6,811.7 (6,743.5–6,895.5) | 6,496 | - / - / - | 0.140 / 0.349 / 1.310 | 25.9 |
| ram-seq1m-q8t1-write | 3 | 8,830.0 (8,521.4–8,919.0) | 8,421 | - / - / - | 0.888 / 2.358 / 2.778 | 36.0 |
| ram-seq1m-q8t4-write | 3 | 8,654.5 (7,329.9–8,745.3) | 8,254 | - / - / - | 3.384 / 7.629 / 8.189 | 64.5 |
| ram-rnd4k-q1t1-write | 3 | 75.3 (75.2–83.6) | 18,377 | - / - / - | 0.052 / 0.090 / 0.262 | 28.3 |
| ram-rnd4k-q32t1-write | 3 | 109.7 (96.8–110.9) | 26,791 | - / - / - | 1.157 / 1.785 / 2.963 | 48.0 |
| ram-rnd4k-q32t4-write | 3 | 122.4 (101.3–127.5) | 29,882 | - / - / - | 3.144 / 4.332 / 5.367 | 88.2 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| fill | 1 | MB/s | 2,316.75 | 2,316.75–2,316.75 |
| tree | 3 | create seconds | 16.95 | 16.54–17.20 |
| tree | 3 | copy seconds (4 threads) | 8.37 | 8.19–8.88 |
| tree | 3 | read and hash seconds | 9.22 | 9.19–9.38 |
| tree | 3 | delete seconds | 15.36 | 15.06–15.45 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| before the disk existed | 13,201 | 2,301 | 184 |
| memory-start | 12,839 | 3,491 | 196 |
| memory-before-fill | 12,186 | 3,437 | 212 |
| memory-filled | 10,119 | 5,503 | 212 |
| memory-after-delete | 11,870 | 5,527 | 213 |
| memory-end | 9,402 | 6,688 | 227 |

## Environment

Windows 26H1 build 28000.2956; target Q: NTFS, cluster 4096 bytes, 199.98 GiB.

Loaded third-party drivers: vioscsi.sys 100.100.104.27100 (Red Hat, Inc.), balloon.sys 100.100.104.27100 (Red Hat, Inc.), netkvm.sys 100.100.104.27100 (Red Hat, Inc.), fwcfg.sys 100.100.104.27100 (Red Hat, Inc.).
