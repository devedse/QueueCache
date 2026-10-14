# OSFMount 3.3.1000, logical emulation

[Back to the comparison](../COMPARISON.md). Status: **Measured**.

| | |
|---|---|
| Product | OSFMount |
| Version | 3.3.1000 |
| Mode | logical emulation |
| Kind | Block device |
| Price | Free |
| `compare-snapshot` run | `QueueCache-Verify-20261012-112112-bf71e9fb6683410e9f7ed1bbc568717c` |
| `compare-ram` run | `QueueCache-Verify-20261012-113229-89626247edd3453c9eaec8644623065b` |

## Notes

The run went fine. The best small-request results so far: single 4 KiB requests at about 1.7 GB/s with the lowest delay (3 microseconds), and the fastest random requests from four programs, while using little processor time (about 26% busy). Large reads from one program reach about 16 GB/s, about a third of QueueCache.

## CrystalDiskMark

<img src="../images/comparison/R04-osfmount-logical.png" width="480" alt="CrystalDiskMark, OSFMount 3.3.1000, logical emulation">

Kept: run 2 (the faster SEQ1M Q8T1 read of two). MB/s.

| Test | Run 1 read / write | Run 2 read / write |
|---|---:|---:|
| SEQ1M Q8T1 | 16,156.80 / 17,782.78 | 16,339.57 / 17,886.98 |
| SEQ1M Q1T1 | 16,158.10 / 17,991.35 | 16,210.72 / 17,946.84 |
| RND4K Q32T1 | 1,659.54 / 1,354.55 | 1,652.57 / 1,406.23 |
| RND4K Q1T1 | 1,709.56 / 1,374.99 | 1,713.38 / 1,416.01 |

## Detailed measurements (DiskSpd)

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| ram-seq1m-q1t1-read | 3 | 16,400.4 (16,386.6–16,573.6) | 15,641 | 0.060 / 0.087 / 0.153 | - / - / - | 25.5 |
| ram-seq1m-q8t1-read | 3 | 16,410.2 (16,372.0–16,541.7) | 15,650 | 0.060 / 0.088 / 0.188 | - / - / - | 25.5 |
| ram-seq1m-q8t4-read | 3 | 23,165.1 (23,037.6–23,311.7) | 22,092 | 0.181 / 0.228 / 0.370 | - / - / - | 100.0 |
| ram-rnd4k-q1t1-read | 3 | 1,744.4 (1,708.8–1,746.8) | 425,871 | 0.002 / 0.003 / 0.020 | - / - / - | 25.4 |
| ram-rnd4k-q32t1-read | 3 | 1,679.5 (1,678.9–1,693.9) | 410,025 | 0.002 / 0.003 / 0.021 | - / - / - | 25.2 |
| ram-rnd4k-q32t4-read | 3 | 5,697.0 (5,667.8–5,724.4) | 1,390,862 | 0.003 / 0.006 / 0.020 | - / - / - | 100.0 |
| ram-seq1m-q1t1-write | 3 | 18,128.1 (17,984.1–18,192.0) | 17,288 | - / - / - | 0.054 / 0.084 / 0.135 | 25.7 |
| ram-seq1m-q8t1-write | 3 | 18,212.3 (17,873.5–18,232.8) | 17,369 | - / - / - | 0.054 / 0.080 / 0.132 | 25.5 |
| ram-seq1m-q8t4-write | 3 | 25,591.1 (25,263.7–25,858.9) | 24,406 | - / - / - | 0.166 / 0.204 / 0.364 | 100.0 |
| ram-rnd4k-q1t1-write | 3 | 1,390.0 (1,368.4–1,392.7) | 339,364 | - / - / - | 0.003 / 0.004 / 0.023 | 25.4 |
| ram-rnd4k-q32t1-write | 3 | 1,368.2 (1,356.1–1,372.3) | 334,036 | - / - / - | 0.003 / 0.005 / 0.023 | 25.3 |
| ram-rnd4k-q32t4-write | 3 | 4,534.1 (4,474.0–4,564.5) | 1,106,963 | - / - / - | 0.003 / 0.010 / 0.024 | 100.0 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| fill | 1 | MB/s | 14,480.38 | 14,480.38–14,480.38 |
| tree | 3 | create seconds | 4.47 | 4.02–4.55 |
| tree | 3 | copy seconds (4 threads) | 4.29 | 4.29–4.34 |
| tree | 3 | read and hash seconds | 2.91 | 2.89–2.94 |
| tree | 3 | delete seconds | 4.25 | 4.20–4.44 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| before the disk existed | 13,365 | 2,212 | 190 |
| memory-start | 9,155 | 6,397 | 203 |
| memory-before-fill | 9,222 | 6,334 | 204 |
| memory-filled | 9,220 | 6,342 | 204 |
| memory-after-delete | 9,221 | 6,337 | 204 |
| memory-end | 8,806 | 6,712 | 296 |

## Environment

Windows 26H1 build 28000.2956; target Q: NTFS, cluster 4096 bytes, 199.98 GiB.

Loaded third-party drivers: vioscsi.sys 100.100.104.27100 (Red Hat, Inc.), osfdisk.sys 1.2.0.0 (PassMark Software), balloon.sys 100.100.104.27100 (Red Hat, Inc.), netkvm.sys 100.100.104.27100 (Red Hat, Inc.), fwcfg.sys 100.100.104.27100 (Red Hat, Inc.).
