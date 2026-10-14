# PrimoCache 4.4.1, L1 2 GiB, no Defer-Write

[Back to the comparison](../COMPARISON.md). Status: **Measured**.

| | |
|---|---|
| Product | PrimoCache |
| Version | 4.4.1 |
| Mode | L1 2 GiB, no Defer-Write |
| Kind | Disk filter, write-through |
| Price | $29.95-49.95 (30-day trial used) |
| `compare-cache` run | `QueueCache-Verify-20261013-015057-c5c75c26f1a24c5d92429b435e3756a8` |
| `compare-cache-cold` run | `QueueCache-Verify-20261013-023412-a92f513f12ef44b59227b5f850ea3a02` |
| `compare-durability-write` run | `QueueCache-Verify-20261013-023635-b1b9a23a260049d0a4ee7f1d75184703` |
| `compare-durability-verify` run | `QueueCache-Verify-20261013-023823-b2580fc2fa0f4db8bc113e4d58ce43ac` |

## Notes

The run went fine. Reads from the cache are fast (about 17 GB/s), and data not yet in the cache is read at close to the bare disk's speed (first read of 1 GiB at 409 MB/s, small random reads at 80 MB/s). Without Defer-Write every write goes straight to the disk, so writes run at the disk's speed, as without a cache. After the simulated power cut the file system was clean and all written data had survived. Of 10,000 new small files that nobody flushed, 9,895 were intact and 105 incomplete. How many such files survive depends on timing, because Windows itself keeps new files in memory for a few seconds.

## CrystalDiskMark

<img src="../images/comparison/C02-primocache-through.png" width="480" alt="CrystalDiskMark, PrimoCache 4.4.1, L1 2 GiB, no Defer-Write">

Kept: run 2 (the faster SEQ1M Q8T1 read of two). MB/s.

| Test | Run 1 read / write | Run 2 read / write |
|---|---:|---:|
| SEQ1M Q8T1 | 16,956.74 / 261.44 | 17,304.34 / 261.49 |
| SEQ1M Q1T1 | 14,514.50 / 122.94 | 14,655.77 / 122.91 |
| RND4K Q32T1 | 474.26 / 13.46 | 473.69 / 12.39 |
| RND4K Q1T1 | 1,054.20 / 4.73 | 1,043.98 / 4.82 |

## Detailed measurements (DiskSpd and timed steps)

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| hot-seq1m-q8t1-read | 3 | 17,253.3 (17,208.9–17,296.2) | 16,454 | 0.213 / 0.397 / 2.026 | - / - / - | 71.5 |
| hot-seq1m-q1t1-read | 3 | 14,608.0 (14,575.3–14,820.6) | 13,931 | 0.067 / 0.097 / 0.171 | - / - / - | 25.5 |
| hot-rnd4k-q32t1-read | 3 | 494.0 (491.6–498.2) | 120,596 | 0.031 / 0.133 / 0.278 | - / - / - | 54.9 |
| hot-rnd4k-q1t1-read | 3 | 1,056.1 (1,056.0–1,065.9) | 257,842 | 0.004 / 0.010 / 0.024 | - / - / - | 25.5 |
| hot-seq1m-q8t1-write | 3 | 249.7 (247.1–253.8) | 238 | - / - / - | 33.130 / 66.190 / 77.433 | 1.5 |
| hot-seq1m-q1t1-write | 3 | 117.1 (116.3–117.7) | 112 | - / - / - | 9.111 / 12.781 / 14.745 | 0.9 |
| hot-rnd4k-q32t1-write | 3 | 14.4 (10.5–15.8) | 3,528 | - / - / - | 4.547 / 55.773 / 128.885 | 5.2 |
| hot-rnd4k-q1t1-write | 3 | 4.1 (3.0–4.2) | 1,005 | - / - / - | 0.894 / 3.000 / 5.542 | 1.5 |
| buffered-seq1m-q8t1-read-buffered | 3 | 13,697.3 (11,197.4–13,787.9) | 13,063 | 0.072 / 0.119 / 0.260 | - / - / - | 25.6 |
| buffered-rnd4k-q1t1-read-buffered | 3 | 1,456.4 (1,404.1–1,473.5) | 355,574 | 0.002 / 0.009 / 0.036 | - / - / - | 25.4 |
| buffered-seq1m-q8t1-write-buffered | 3 | 5,169.9 (139.3–5,249.5) | 4,930 | - / - / - | 0.194 / 0.296 / 0.478 | 25.8 |
| buffered-rnd4k-q1t1-write-buffered | 3 | 1,220.1 (1,219.5–1,248.8) | 297,877 | - / - / - | 0.003 / 0.010 / 0.041 | 25.7 |
| through-seq1m-q8t1-write-write-through | 3 | 255.0 (247.4–256.4) | 243 | - / - / - | 33.064 / 67.588 / 75.948 | 1.2 |
| through-rnd4k-q1t1-write-write-through | 3 | 4.8 (4.4–4.8) | 1,164 | - / - / - | 0.841 / 2.045 / 3.603 | 1.6 |
| big-rnd4k-q32t1-read | 2 | 128.9 (122.1–135.8) | 31,479 | 0.325 / 9.525 / 22.705 | - / - / - | 34.3 |
| big-seq1m-q8t1-read | 2 | 413.9 (408.7–419.1) | 395 | 14.492 / 111.133 / 208.608 | - / - / - | 6.8 |
| mixed-rnd4k-q8t1-mixed | 1 | 12.7 (12.7–12.7) | 3,100 | 0.319 / 9.524 / 22.501 | 4.636 / 29.066 / 66.613 | 5.2 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| hot-warm1 | 3 | MB/s | 15,058.61 | 567.58–15,207.73 |
| hot-warm2 | 1 | MB/s | 16,283.17 | 16,283.17–16,283.17 |
| hot-warm3 | 1 | MB/s | 18,429.19 | 18,429.19–18,429.19 |
| buffered-warm | 3 | MB/s | 14,911.78 | 11,630.59–17,397.77 |
| absorb | 3 | MB/s | 258.74 | 197.27–259.54 |
| flush | 3 | MB/s | 266.40 | 264.81–268.46 |
| flush | 3 | flush seconds | 0.00 | 0.00–0.00 |
| sustained | 2 | MB/s | 259.41 | 259.25–259.56 |
| sustained | 2 | slowest second MB/s | 237.50 | 236.98–238.03 |
| tree | 2 | create seconds | 28.12 | 27.75–28.50 |
| tree | 2 | copy seconds (4 threads) | 23.06 | 20.75–25.37 |
| tree | 2 | read and hash seconds | 4.91 | 4.90–4.93 |
| tree | 2 | delete seconds | 24.21 | 20.48–27.93 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| memory-start | 10,366 | 5,064 | 317 |
| memory-end | 10,150 | 5,226 | 383 |

## Empty cache

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| cold-rnd4k-q32t1-read-first | 1 | 80.4 (80.4–80.4) | 19,628 | 0.412 / 14.641 / 39.146 | - / - / - | 24.2 |
| cold-rnd4k-q32t1-read-second | 1 | 110.7 (110.7–110.7) | 27,017 | 0.321 / 10.459 / 23.204 | - / - / - | 29.6 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| cold-first | 1 | MB/s | 408.94 | 408.94–408.94 |
| cold-second | 1 | MB/s | 16,775.09 | 16,775.09–16,775.09 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| memory-start | 10,334 | 5,077 | 330 |
| memory-end | 10,361 | 5,055 | 328 |

## Power loss


| Data set | New | Old | Other or missing |
|---|---:|---:|---:|
| Durable save (buffered writes, then flush) | 100.0% | 0.0% | 0.0% |
| Raw writes without a flush | 100.0% | 0.0% | 0.0% |

Small files: 9895 intact, 105 damaged, 0 missing of 10000; chkdsk /scan exit 0.

## Environment

Windows 26H1 build 28000.2956; target Q: NTFS, cluster 4096 bytes, 199.98 GiB.

Loaded third-party drivers: vioscsi.sys 100.100.104.27100 (Red Hat, Inc.), rxfcv.sys 4.4.0 (Romex Software), RXBSKNL.SYS 1.2.0 (Windows (R) Win 7 DDK provider), balloon.sys 100.100.104.27100 (Red Hat, Inc.), netkvm.sys 100.100.104.27100 (Red Hat, Inc.), fwcfg.sys 100.100.104.27100 (Red Hat, Inc.).
