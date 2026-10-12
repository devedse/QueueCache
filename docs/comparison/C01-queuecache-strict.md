# QueueCache 0.4.604.1, Strict, 2 GiB

[Back to the comparison](../COMPARISON.md). Status: **Partly measured**.

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

## Problems

- ⚠ Writes stopped for more than 10 seconds after a burst of small random writes (likely a write-through write waiting for the whole RAM queue to reach the disk); being confirmed

## Notes

Reads from the cache are as fast as in Fast mode (about 40 GB/s), and a file read once is kept in RAM. Writes are uneven. After a burst of small random writes, writing stopped completely for more than 10 seconds: one test finished nothing, so the detailed run is being repeated. The likely reason is that in Strict mode a write that must go straight to the disk (Windows' file-system journal makes such writes) first waits until every write queued in RAM is on the disk, and small random writes reach this disk slowly (about 10 MB/s here). The repeat run records the driver's counters to confirm this. After the simulated power cut the file system was clean (chkdsk found no problems) and all flushed data had survived. Some newly created small files that nobody flushed were incomplete (950) or missing (478).

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

Not run yet.

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
