# QueueCache 0.4.604.1, Fast, 2 GiB

[Back to the comparison](../COMPARISON.md). Status: **Measured**.

| | |
|---|---|
| Product | QueueCache |
| Version | 0.4.604.1 |
| Mode | Fast, 2 GiB |
| Kind | Volume filter, write-back |
| Price | Free (MIT) |
| `compare-cache` run | `QueueCache-Verify-20261012-202536-18b673806b9840f4b0c188722f1b208c` |
| `compare-cache-cold` run | `QueueCache-Verify-20261012-211315-1ccbb761c725479590fd878d4d6ace94` |
| `compare-durability-write` run | `QueueCache-Verify-20261012-211540-d7163ab695f643a199573774e82fe61f` |
| `compare-durability-verify` run | `QueueCache-Verify-20261012-211734-00f5848605014672b2465ca4309b0116` |

## Problems

- ⚠ File system needed an offline repair (chkdsk /f) after a simulated power cut
- ⚠ expected for Fast mode (as designed for now); tracked in GitHub issue [#13](https://github.com/devedse/QueueCache/issues/13)
- ⚠ Reads of data not yet in the cache are 2.5 to 6 times slower than without any cache (first read of 1 GiB 217 against 560 MB/s; random 4 KiB 11 against 65 MB/s): QueueCache fetches missing data one request at a time

## Notes

The run went fine. Cached data is very fast (about 39 GB/s reads, 21 GB/s writes), and a file read once is kept (the second read came from RAM). Reading data that is not in the cache yet is slower than without any cache: the first read of a 1 GiB file ran at 217 MB/s against 560 MB/s on the bare disk, and small random reads at 11 against 65 MB/s. QueueCache fetches missing data from the disk one request at a time, so it cannot use the disk's ability to work on several requests at once. After a 1 GiB burst the disk needed about 10 seconds to catch up, writing at about 105 MB/s, less than half of what the disk can take with several requests at once. Fast mode answers flushes and write-through requests from RAM, as designed. After the simulated power cut the file data had already reached the disk, but the file system needed repair (chkdsk found problems) and about half of 10,000 newly created small files were gone.

## CrystalDiskMark

<img src="../images/comparison/C01-queuecache-fast.png" width="480" alt="CrystalDiskMark, QueueCache 0.4.604.1, Fast, 2 GiB">

Kept: run 2 (the faster SEQ1M Q8T1 read of two). MB/s.

| Test | Run 1 read / write | Run 2 read / write |
|---|---:|---:|
| SEQ1M Q8T1 | 38,610.72 / 21,055.07 | 40,321.54 / 21,470.75 |
| SEQ1M Q1T1 | 20,866.52 / 13,565.70 | 19,534.12 / 13,559.85 |
| RND4K Q32T1 | 1,681.51 / 1,368.39 | 1,701.49 / 1,326.80 |
| RND4K Q1T1 | 1,294.02 / 1,013.63 | 1,307.20 / 1,027.56 |

## Detailed measurements (DiskSpd and timed steps)

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| hot-seq1m-q8t1-read | 3 | 39,486.7 (33,633.0–39,952.0) | 37,657 | 0.203 / 0.339 / 1.338 | - / - / - | 98.2 |
| hot-seq1m-q1t1-read | 3 | 18,837.7 (18,271.8–19,425.7) | 17,965 | 0.051 / 0.091 / 0.193 | - / - / - | 25.9 |
| hot-rnd4k-q32t1-read | 3 | 1,634.6 (1,579.7–1,679.0) | 399,076 | 0.070 / 0.124 / 0.285 | - / - / - | 52.5 |
| hot-rnd4k-q1t1-read | 3 | 1,294.6 (1,284.3–1,295.8) | 316,055 | 0.003 / 0.009 / 0.035 | - / - / - | 25.5 |
| hot-seq1m-q8t1-write | 3 | 21,214.0 (20,609.6–21,379.8) | 20,231 | - / - / - | 0.393 / 0.557 / 1.783 | 94.7 |
| hot-seq1m-q1t1-write | 3 | 13,342.4 (13,136.9–13,503.7) | 12,724 | - / - / - | 0.072 / 0.133 / 0.295 | 26.6 |
| hot-rnd4k-q32t1-write | 3 | 1,350.0 (1,265.5–1,389.7) | 329,588 | - / - / - | 0.008 / 0.073 / 0.183 | 51.2 |
| hot-rnd4k-q1t1-write | 3 | 982.4 (979.9–1,001.3) | 239,832 | - / - / - | 0.004 / 0.016 / 0.040 | 26.2 |
| buffered-seq1m-q8t1-read-buffered | 3 | 13,639.3 (12,204.1–13,678.9) | 13,007 | 0.072 / 0.130 / 0.285 | - / - / - | 25.5 |
| buffered-rnd4k-q1t1-read-buffered | 3 | 1,475.1 (1,467.4–1,480.6) | 360,133 | 0.002 / 0.009 / 0.040 | - / - / - | 25.8 |
| buffered-seq1m-q8t1-write-buffered | 3 | 5,244.1 (5,227.5–5,253.4) | 5,001 | - / - / - | 0.191 / 0.332 / 0.750 | 26.2 |
| buffered-rnd4k-q1t1-write-buffered | 3 | 1,252.4 (1,228.1–1,264.9) | 305,762 | - / - / - | 0.003 / 0.009 / 0.044 | 26.4 |
| through-seq1m-q8t1-write-write-through | 3 | 21,077.4 (21,067.2–21,150.6) | 20,101 | - / - / - | 0.396 / 0.565 / 1.776 | 94.5 |
| through-rnd4k-q1t1-write-write-through | 3 | 961.9 (954.6–963.6) | 234,846 | - / - / - | 0.004 / 0.016 / 0.038 | 26.3 |
| big-rnd4k-q32t1-read | 2 | 11.8 (11.0–12.6) | 2,876 | 0.719 / 1.543 / 60.977 | - / - / - | 6.9 |
| big-seq1m-q8t1-read | 2 | 262.0 (261.9–262.1) | 250 | 9.793 / 13.765 / 23.065 | - / - / - | 2.2 |
| mixed-rnd4k-q8t1-mixed | 1 | 11.7 (11.7–11.7) | 2,862 | 0.879 / 14.554 / 32.663 | 3.670 / 25.758 / 52.871 | 5.8 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| hot-warm1 | 3 | MB/s | 14,329.45 | 212.27–17,391.55 |
| hot-warm2 | 1 | MB/s | 18,304.19 | 18,304.19–18,304.19 |
| hot-warm3 | 1 | MB/s | 21,782.27 | 21,782.27–21,782.27 |
| buffered-warm | 3 | MB/s | 19,735.72 | 18,778.74–20,148.84 |
| absorb | 3 | MB/s | 2,665.10 | 2,336.34–2,698.71 |
| flush | 3 | MB/s | 7,563.32 | 3,485.02–8,096.75 |
| flush | 3 | flush seconds | 0.00 | 0.00–0.00 |
| sustained | 2 | MB/s | 178.26 | 178.18–178.33 |
| sustained | 2 | slowest second MB/s | 116.92 | 105.91–127.93 |
| tree | 2 | create seconds | 12.09 | 9.98–14.20 |
| tree | 2 | copy seconds (4 threads) | 8.60 | 6.09–11.11 |
| tree | 2 | read and hash seconds | 4.91 | 4.90–4.93 |
| tree | 2 | delete seconds | 16.90 | 16.13–17.67 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| memory-start | 11,011 | 4,522 | 267 |
| memory-end | 10,610 | 4,857 | 365 |

## Empty cache

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| cold-rnd4k-q32t1-read-first | 1 | 11.1 (11.1–11.1) | 2,717 | 0.732 / 1.688 / 120.942 | - / - / - | 7.7 |
| cold-rnd4k-q32t1-read-second | 1 | 12.1 (12.1–12.1) | 2,944 | 0.715 / 1.564 / 590.945 | - / - / - | 6.7 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| cold-first | 1 | MB/s | 216.95 | 216.95–216.95 |
| cold-second | 1 | MB/s | 26,416.00 | 26,416.00–26,416.00 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| memory-start | 10,658 | 4,868 | 285 |
| memory-end | 10,835 | 4,662 | 280 |

## Power loss


| Data set | New | Old | Other or missing |
|---|---:|---:|---:|
| Durable save (buffered writes, then flush) | 100.0% | 0.0% | 0.0% |
| Raw writes without a flush | 100.0% | 0.0% | 0.0% |

Small files: 4332 intact, 0 damaged, 5668 missing of 10000; chkdsk /scan exit 13.

## Environment

Windows 26H1 build 28000.2956; target Q: NTFS, cluster 4096 bytes, 199.98 GiB.

Loaded third-party drivers: vioscsi.sys 100.100.104.27100 (Red Hat, Inc.), balloon.sys 100.100.104.27100 (Red Hat, Inc.), netkvm.sys 100.100.104.27100 (Red Hat, Inc.), fwcfg.sys 100.100.104.27100 (Red Hat, Inc.); QueueCache: C:\WINDOWS\System32\drivers\QueueCache-0.4.604.1-D9416B70A3A3.sys, C:\WINDOWS\System32\DriverStore\FileRepository\qcramdisk.inf_amd64_fb19ec298857fd7c\qcramdisk.sys.
