# SoftPerfect RAM File System 26.10, default

[Back to the comparison](../COMPARISON.md). Status: **Measured**.

| | |
|---|---|
| Product | SoftPerfect RAM File System |
| Version | 26.10 |
| Mode | default |
| Kind | File system (not a disk) |
| Price | $29 per PC (30-day trial used) |
| `compare-snapshot` run | `QueueCache-Verify-20261012-143016-92d9629be2d44e00982830ac9dc00881` |
| `compare-ram` run | `QueueCache-Verify-20261012-144134-f1303a724e854e269a380dc52b80a092` |

## Notes

The run went fine. This is a file system kept in RAM rather than a disk, and it is clearly the fastest for small requests and many files: about 3 GB/s for single 4 KiB requests (2 microseconds, about twice QueueCache) and creating, copying and deleting 40,000 files about three times faster than the RAM disks. Large reads from one program reach about 30 GB/s, a third below QueueCache. Memory is taken only as data arrives.

## CrystalDiskMark

<img src="../images/comparison/R08-softperfect-ramfs.png" width="480" alt="CrystalDiskMark, SoftPerfect RAM File System 26.10, default">

Kept: run 2 (the faster SEQ1M Q8T1 read of two). MB/s.

| Test | Run 1 read / write | Run 2 read / write |
|---|---:|---:|
| SEQ1M Q8T1 | 30,953.29 / 21,275.60 | 30,958.62 / 21,891.08 |
| SEQ1M Q1T1 | 24,760.73 / 23,391.46 | 25,093.38 / 23,478.64 |
| RND4K Q32T1 | 2,542.33 / 2,153.08 | 2,524.15 / 2,162.68 |
| RND4K Q1T1 | 2,941.71 / 2,336.89 | 2,931.65 / 2,385.91 |

## Detailed measurements (DiskSpd)

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| ram-seq1m-q1t1-read | 3 | 25,144.8 (24,832.1–25,551.3) | 23,980 | 0.041 / 0.060 / 0.108 | - / - / - | 25.3 |
| ram-seq1m-q8t1-read | 3 | 30,496.7 (30,119.6–30,709.6) | 29,084 | 0.113 / 0.178 / 0.230 | - / - / - | 62.9 |
| ram-seq1m-q8t4-read | 3 | 31,288.6 (30,926.0–31,368.8) | 29,839 | 0.139 / 0.514 / 1.360 | - / - / - | 100.0 |
| ram-rnd4k-q1t1-read | 3 | 2,977.8 (2,969.8–3,002.0) | 726,993 | 0.001 / 0.002 / 0.013 | - / - / - | 25.2 |
| ram-rnd4k-q32t1-read | 3 | 2,614.1 (2,558.7–2,617.5) | 638,200 | 0.001 / 0.002 / 0.014 | - / - / - | 25.5 |
| ram-rnd4k-q32t4-read | 3 | 8,623.7 (8,399.1–8,654.6) | 2,105,392 | 0.002 / 0.003 / 0.015 | - / - / - | 100.0 |
| ram-seq1m-q1t1-write | 3 | 23,712.9 (23,593.6–24,033.8) | 22,614 | - / - / - | 0.043 / 0.061 / 0.098 | 25.7 |
| ram-seq1m-q8t1-write | 3 | 21,626.0 (21,358.4–21,942.2) | 20,624 | - / - / - | 0.169 / 0.212 / 0.273 | 69.8 |
| ram-seq1m-q8t4-write | 3 | 25,157.4 (24,932.0–25,210.2) | 23,992 | - / - / - | 0.170 / 0.615 / 1.004 | 100.0 |
| ram-rnd4k-q1t1-write | 3 | 2,364.4 (2,360.3–2,372.1) | 577,240 | - / - / - | 0.002 / 0.002 / 0.015 | 25.2 |
| ram-rnd4k-q32t1-write | 3 | 2,206.1 (2,194.1–2,225.7) | 538,600 | - / - / - | 0.002 / 0.002 / 0.017 | 25.3 |
| ram-rnd4k-q32t4-write | 3 | 7,476.4 (7,211.3–7,572.5) | 1,825,298 | - / - / - | 0.002 / 0.004 / 0.016 | 100.0 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| fill | 1 | MB/s | 7,352.49 | 7,352.49–7,352.49 |
| tree | 3 | create seconds | 1.62 | 1.62–1.64 |
| tree | 3 | copy seconds (4 threads) | 1.22 | 1.18–1.23 |
| tree | 3 | read and hash seconds | 2.57 | 2.55–2.57 |
| tree | 3 | delete seconds | 1.76 | 1.75–1.78 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| before the disk existed | 13,246 | 2,245 | 181 |
| memory-start | 13,267 | 2,233 | 186 |
| memory-before-fill | 12,346 | 3,172 | 191 |
| memory-filled | 10,283 | 5,235 | 200 |
| memory-after-delete | 10,284 | 3,216 | 191 |
| memory-end | 12,060 | 3,349 | 199 |

## Environment

Windows 26H1 build 28000.2956; target Q: NTFS, cluster 4096 bytes, 199.98 GiB.

Loaded third-party drivers: vioscsi.sys 100.100.104.27100 (Red Hat, Inc.), ramfs.sys 26.10.0.0 (SoftPerfect Pty Ltd), balloon.sys 100.100.104.27100 (Red Hat, Inc.), netkvm.sys 100.100.104.27100 (Red Hat, Inc.), fwcfg.sys 100.100.104.27100 (Red Hat, Inc.).
