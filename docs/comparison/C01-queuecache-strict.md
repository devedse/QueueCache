# QueueCache 0.4.604.1, Strict, 2 GiB

[Back to the comparison](../COMPARISON.md). Status: **Measured**.

| | |
|---|---|
| Product | QueueCache |
| Version | 0.4.604.1 |
| Mode | Strict, 2 GiB |
| Kind | Volume filter, write-through |
| Price | Free (MIT) |
| `compare-cache-cold` run | `QueueCache-Verify-20261012-220240-10298e9395ac4b038669f187583d78cb` |
| `compare-durability-write` run | `QueueCache-Verify-20261012-220507-589b9019b8554cee82f16efe9ff5e1b8` |
| `compare-durability-verify` run | `QueueCache-Verify-20261012-220731-75b5027b8e7e4d20aed3c886c33280c9` |
| `compare-cache` run | `QueueCache-Verify-20261013-025808-749c36a355ea450d9dd07ee1b1069961` |

## Problems

- ⚠ 6 measurement window(s) completed nothing in 10 s (stalled): hot-r1-rnd4k-q1t1-write, hot-r1-rnd4k-q32t1-write, hot-r2-rnd4k-q1t1-write, hot-r3-rnd4k-q1t1-write, hot-r3-rnd4k-q32t1-write, hot-r3-seq1m-q8t1-write
- ⚠ Writes stop completely for 100 seconds or more after a burst of writes: every flush or write-through request from Windows makes all writes wait until everything queued in RAM is on the disk (confirmed with the driver's counters)
- ⚠ Writing and file operations are 5 to 11 times slower than without any cache (creating 40,000 files 213 against 32 s; 8 GiB write 53 against 242 MB/s; write-through 52 against 251 MB/s)
- ⚠ Reads of data not yet in the cache are 3 to 6 times slower than without any cache (first read of 1 GiB 187 against 560 MB/s; random 4 KiB 11 against 65 MB/s): QueueCache fetches missing data one request at a time

## Notes

Reads from the cache are as fast as in Fast mode (about 41 GB/s), and a file read once is kept in RAM. As in Fast mode, reading data that is not in the cache yet is slower than without any cache (first read of 1 GiB 187 against 560 MB/s), because missing data is fetched one request at a time. Writing is the problem: in Strict mode writing was much slower than with no cache at all. Writes first go into RAM very quickly. But whenever Windows asks for data to be safely on the disk (a flush, or a write that must go straight to the disk; the file system does both all the time), QueueCache makes every write wait until everything queued in RAM is on the disk. After a burst of small random writes that took 100 seconds or more, because this disk writes scattered data slowly (about 10 MB/s). As a result 6 of the 12 write tests finished nothing in 10 seconds, creating 40,000 files took 213 seconds (32 without a cache), and a long 8 GiB write averaged 53 MB/s (242 without a cache). The driver's counters confirmed the cause: each stalled test received one write-through write and two flushes from the file system. After the simulated power cut the file system was clean and all flushed data had survived. Some newly created small files that nobody flushed were incomplete (950) or missing (478).

## CrystalDiskMark

<img src="../images/comparison/C01-queuecache-strict.png" width="480" alt="CrystalDiskMark, QueueCache 0.4.604.1, Strict, 2 GiB">

Kept: run 2 (the faster SEQ1M Q8T1 read of two). MB/s.

| Test | Run 1 read / write | Run 2 read / write |
|---|---:|---:|
| SEQ1M Q8T1 | 39,743.57 / 19,716.73 | 39,989.21 / 21,310.04 |
| SEQ1M Q1T1 | 20,909.02 / 14,037.49 | 19,413.95 / 12,937.26 |
| RND4K Q32T1 | 1,682.95 / 1,301.37 | 1,659.49 / 331.45 |
| RND4K Q1T1 | 1,277.07 / 897.67 | 1,266.80 / 871.17 |

## Detailed measurements (DiskSpd and timed steps)

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| hot-seq1m-q8t1-read | 3 | 40,874.2 (40,303.6–41,829.0) | 38,981 | 0.192 / 0.338 / 1.237 | - / - / - | 98.6 |
| hot-seq1m-q1t1-read | 3 | 20,906.2 (20,602.2–21,214.3) | 19,938 | 0.046 / 0.094 / 0.200 | - / - / - | 29.2 |
| hot-rnd4k-q32t1-read | 3 | 1,716.4 (1,689.6–1,732.7) | 419,039 | 0.065 / 0.112 / 0.212 | - / - / - | 49.6 |
| hot-rnd4k-q1t1-read | 3 | 1,300.3 (1,296.1–1,306.0) | 317,459 | 0.003 / 0.009 / 0.035 | - / - / - | 25.8 |
| hot-seq1m-q8t1-write | 3 | 3,476.6 (0.0–4,135.2) | 3,316 | - / - / - | 0.386 / 0.579 / 1.757 | 16.3 |
| hot-seq1m-q1t1-write | 3 | 2,838.8 (2,611.1–5,461.8) | 2,707 | - / - / - | 0.069 / 0.121 / 0.238 | 5.9 |
| hot-rnd4k-q32t1-write | 3 | 0.0 (0.0–280.8) | 0 | - / - / - | 0.008 / 0.076 / 0.680 | 1.6 |
| hot-rnd4k-q1t1-write | 3 | 0.0 (0.0–0.0) | 0 | - / - / - | - / - / - | 1.1 |
| buffered-seq1m-q8t1-read-buffered | 3 | 13,750.7 (13,662.6–13,804.2) | 13,114 | 0.071 / 0.129 / 0.287 | - / - / - | 25.6 |
| buffered-rnd4k-q1t1-read-buffered | 3 | 1,483.7 (1,482.8–1,489.8) | 362,229 | 0.002 / 0.009 / 0.032 | - / - / - | 25.8 |
| buffered-seq1m-q8t1-write-buffered | 3 | 5,333.1 (5,285.4–5,333.9) | 5,086 | - / - / - | 0.189 / 0.293 / 0.646 | 25.9 |
| buffered-rnd4k-q1t1-write-buffered | 3 | 1,273.0 (1,255.0–1,280.7) | 310,801 | - / - / - | 0.002 / 0.009 / 0.042 | 25.9 |
| through-seq1m-q8t1-write-write-through | 3 | 52.5 (43.7–54.7) | 50 | - / - / - | 147.690 / 570.460 / 780.590 | 0.7 |
| through-rnd4k-q1t1-write-write-through | 3 | 0.8 (0.8–0.8) | 190 | - / - / - | 3.714 / 42.554 / 123.589 | 1.6 |
| big-rnd4k-q32t1-read | 2 | 6.0 (5.7–6.3) | 1,470 | 0.762 / 9.797 / 338.541 | - / - / - | 4.4 |
| big-seq1m-q8t1-read | 2 | 221.4 (217.3–225.5) | 211 | 9.184 / 43.091 / 139.231 | - / - / - | 1.9 |
| mixed-rnd4k-q8t1-mixed | 1 | 4.1 (4.1–4.1) | 1,010 | 1.100 / 49.458 / 122.466 | 7.878 / 82.764 / 203.934 | 2.9 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| hot-warm1 | 3 | MB/s | 18,693.90 | 214.26–19,834.59 |
| hot-warm2 | 1 | MB/s | 21,497.54 | 21,497.54–21,497.54 |
| hot-warm3 | 1 | MB/s | 19,627.57 | 19,627.57–19,627.57 |
| buffered-warm | 3 | MB/s | 23,749.47 | 19,728.44–23,936.68 |
| absorb | 3 | MB/s | 1,112.47 | 109.33–1,318.43 |
| flush | 3 | MB/s | 8,691.49 | 3,690.11–8,888.21 |
| flush | 3 | flush seconds | 12.56 | 11.47–13.16 |
| sustained | 2 | MB/s | 52.85 | 51.87–53.83 |
| sustained | 2 | slowest second MB/s | 0.00 | 0.00–0.00 |
| tree | 2 | create seconds | 212.95 | 195.76–230.13 |
| tree | 2 | copy seconds (4 threads) | 129.78 | 71.12–188.44 |
| tree | 2 | read and hash seconds | 4.90 | 4.88–4.92 |
| tree | 2 | delete seconds | 263.81 | 221.52–306.09 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| memory-start | 10,242 | 5,080 | 366 |
| memory-end | 10,293 | 5,073 | 385 |

## Empty cache

Median of the repetitions, range in brackets. MB/s are decimal.

| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |
|---|---:|---:|---:|---|---|---:|
| cold-rnd4k-q32t1-read-first | 1 | 10.8 (10.8–10.8) | 2,641 | 0.758 / 1.517 / 4.752 | - / - / - | 6.1 |
| cold-rnd4k-q32t1-read-second | 1 | 11.7 (11.7–11.7) | 2,866 | 0.729 / 1.646 / 14.915 | - / - / - | 6.8 |

| Step | n | Measure | Median | Range |
|---|---:|---|---:|---|
| cold-first | 1 | MB/s | 186.66 | 186.66–186.66 |
| cold-second | 1 | MB/s | 16,408.69 | 16,408.69–16,408.69 |

| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB |
|---|---:|---:|---:|
| memory-start | 10,901 | 4,529 | 275 |
| memory-end | 10,913 | 4,504 | 274 |

## Power loss


| Data set | New | Old | Other or missing |
|---|---:|---:|---:|
| Durable save (buffered writes, then flush) | 100.0% | 0.0% | 0.0% |
| Raw writes without a flush | 100.0% | 0.0% | 0.0% |

Small files: 8572 intact, 950 damaged, 478 missing of 10000; chkdsk /scan exit 0.

## Environment

Windows 26H1 build 28000.2956; target Q: NTFS, cluster 4096 bytes, 199.98 GiB.

Loaded third-party drivers: vioscsi.sys 100.100.104.27100 (Red Hat, Inc.), balloon.sys 100.100.104.27100 (Red Hat, Inc.), netkvm.sys 100.100.104.27100 (Red Hat, Inc.), fwcfg.sys 100.100.104.27100 (Red Hat, Inc.); QueueCache: C:\WINDOWS\System32\drivers\QueueCache-0.4.604.1-D9416B70A3A3.sys, C:\WINDOWS\System32\DriverStore\FileRepository\qcramdisk.inf_amd64_fb19ec298857fd7c\qcramdisk.sys.
