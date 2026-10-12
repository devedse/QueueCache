# OSFMount 3.3.1000, physical emulation

[Back to the comparison](../COMPARISON.md). Status: **Measured**.

| | |
|---|---|
| Product | OSFMount |
| Version | 3.3.1000 |
| Mode | physical emulation |
| Kind | Block device |
| Price | Free |
| `compare-snapshot` run | `QueueCache-Verify-20261012-183145-4c86bdd15dfe4b1b9baced91de392453` |
| `compare-ram` run | `QueueCache-Verify-20261012-184334-3f626af52ad2464c8ab5d320c615f2bd` |

## Notes

The run went fine (measured a second time; the first attempt's file-workload times were slowed by virus scanning). Physical emulation (the disk appears as a normal disk with a partition) is about as fast as OSFMount's logical mode for large reads, about 15 GB/s, but roughly half as fast for small 4 KiB requests.

## CrystalDiskMark

<img src="../images/comparison/R04-osfmount-physical.png" width="480" alt="CrystalDiskMark, OSFMount 3.3.1000, physical emulation">

Kept: run 1 (the faster SEQ1M Q8T1 read of two). MB/s.

| Test | Run 1 read / write | Run 2 read / write |
|---|---:|---:|
| SEQ1M Q8T1 | 15,180.80 / 17,081.22 | 15,036.58 / 16,988.29 |
| SEQ1M Q1T1 | 15,277.74 / 17,151.67 | 15,129.11 / 17,232.62 |
| RND4K Q32T1 | 863.16 / 782.90 | 871.00 / 797.60 |
| RND4K Q1T1 | 917.92 / 807.46 | 933.42 / 809.18 |

## Detailed measurements (DiskSpd)

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| ram-seq1m-q1t1-read | 3 | 15,408.9 (15,378.4–15,439.8) | 14,695 | 0.064 / 0.102 / 0.190 | - / - / - | 25.3 |
| ram-seq1m-q8t1-read | 3 | 15,180.4 (15,127.8–15,232.8) | 14,477 | 0.065 / 0.111 / 0.226 | - / - / - | 25.5 |
| ram-seq1m-q8t4-read | 3 | 22,864.1 (22,811.5–22,952.2) | 21,805 | 0.181 / 0.231 / 0.446 | - / - / - | 100.0 |
| ram-rnd4k-q1t1-read | 3 | 934.1 (933.4–936.8) | 228,051 | 0.004 / 0.014 / 0.033 | - / - / - | 25.2 |
| ram-rnd4k-q32t1-read | 3 | 876.3 (868.8–883.9) | 213,942 | 0.004 / 0.016 / 0.041 | - / - / - | 25.4 |
| ram-rnd4k-q32t4-read | 3 | 3,107.8 (3,105.2–3,119.6) | 758,747 | 0.005 / 0.017 / 0.039 | - / - / - | 100.0 |
| ram-seq1m-q1t1-write | 3 | 17,399.2 (17,183.7–17,523.8) | 16,593 | - / - / - | 0.056 / 0.095 / 0.172 | 25.4 |
| ram-seq1m-q8t1-write | 3 | 17,206.3 (17,144.7–17,311.2) | 16,409 | - / - / - | 0.056 / 0.103 / 0.169 | 25.4 |
| ram-seq1m-q8t4-write | 3 | 25,590.9 (25,289.5–25,911.6) | 24,405 | - / - / - | 0.163 / 0.221 / 0.436 | 100.0 |
| ram-rnd4k-q1t1-write | 3 | 804.8 (803.2–806.6) | 196,492 | - / - / - | 0.005 / 0.017 / 0.036 | 25.2 |
| ram-rnd4k-q32t1-write | 3 | 779.4 (777.9–780.0) | 190,274 | - / - / - | 0.005 / 0.019 / 0.044 | 25.4 |
| ram-rnd4k-q32t4-write | 3 | 2,697.7 (2,588.9–2,740.3) | 658,609 | - / - / - | 0.005 / 0.020 / 0.044 | 100.0 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| fill | 1 | MB/s | 8,666.74 | 8,666.74–8,666.74 |
| tree | 3 | create seconds | 5.08 | 4.88–5.17 |
| tree | 3 | copy seconds (4 threads) | 4.48 | 4.43–4.57 |
| tree | 3 | read and hash seconds | 2.94 | 2.92–3.05 |
| tree | 3 | delete seconds | 4.54 | 4.40–4.61 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| before the disk existed | 13,093 | 2,325 | 200 |
| memory-start | 9,048 | 6,422 | 214 |
| memory-before-fill | 9,067 | 6,390 | 214 |
| memory-filled | 9,067 | 6,391 | 214 |
| memory-after-delete | 9,066 | 6,391 | 214 |
| memory-end | 8,663 | 6,731 | 297 |

## Environment

Windows 26H1 build 28000.2956; target Q: NTFS, cluster 4096 bytes, 199.98 GiB.

Loaded third-party drivers: vioscsi.sys 100.100.104.27100 (Red Hat, Inc.), balloon.sys 100.100.104.27100 (Red Hat, Inc.), netkvm.sys 100.100.104.27100 (Red Hat, Inc.), fwcfg.sys 100.100.104.27100 (Red Hat, Inc.).
