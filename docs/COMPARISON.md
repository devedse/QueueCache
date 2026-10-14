# QueueCache compared with other RAM disks and RAM caches

> All 12 RAM-disk and 6 cache configurations measured, 2026-10-12 to 2026-10-14.

Every product was installed alone on the same clean Windows 11 virtual machine, measured, and removed by restoring
the machine to the same snapshot. QueueCache was measured the same way, by tools that do not use any QueueCache
feature. The historical QueueCache 0.4.604.1 and competitor rows retain their original 2026-10-12 to 2026-10-13 measurement dates.
QueueCache 0.4.687.1 Fast is a separate 2026-10-14 refresh; the RAM-disk and power-cut evidence was not repeated.
Cross-date figures are descriptive; causal gains require alternated same-build A/B measurements. Scores are MB/s (1 MB = 1,000,000 bytes) unless a column says otherwise.

**Summary.**

- **RAM disks.** QueueCache's RAM disk is the fastest at large reads by one program (45 GB/s with eight requests
  in flight; next best 35 GB/s) and shares first place for single large reads (25 GB/s) with SoftPerfect RAM File
  System. For small 4 KiB requests it is among the fastest true disks, about 8% behind OSFMount. SoftPerfect RAM
  File System is a file system in RAM rather than a disk: it handles small requests almost twice as fast, and creates,
  copies and deletes files two to four times as fast as any disk. With four programs reading at once the faster disks all land between
  20 and 31 GB/s (QueueCache 25). AIM Toolkit and ManagedDrive were much slower (3 to 4.5 GB/s for large reads, against
  15 to 45 GB/s for the others).
- **RAM caches.** In the original comparison, QueueCache 0.4.604.1 Fast served cached data at 39 GB/s reads and
  21 GB/s writes, against 17 and 19 GB/s for PrimoCache with Defer-Write. It exposed three weaknesses:
  - Missing data was read one request at a time: 217 MB/s against 560 for the bare disk and 512 for PrimoCache
    ([#14](https://github.com/devedse/QueueCache/issues/14)). The fix branch overlaps bounded ordinary misses.
    A same-build flags 7/15 screen measured 9.4× cold random and 3.35× cold sequential throughput, with improved
    p99 and no repeated resident-control loss. Both arms use the new read pool and queue order; this isolates
    miss offload, not the complete old driver. Some maximum latency samples increased.
  - The old 256 KiB × 2 default wrote pending data at 105–130 MB/s. New-task defaults are 1024 KiB × 4
    ([#15](https://github.com/devedse/QueueCache/issues/15)); saved profiles keep their settings.
    Complete same-build drain tests reduced cold-read flush median 6.52 to 3.86 seconds; a separate write-only
    repeat reduced flush median 127.61 to 92.16 seconds, with every pair improving and separated ranges.
    Fixed-window write IOPS varied with administrative-flush timing and are not qualified as invariant.
    The completed 0.4.687.1 Fast comparison (2026-10-14) measured first/second 1 GiB reads at
    571 / 19,638 MB/s, larger-file random/sequential reads at 80.90 / 621.88 MB/s, and sustained writes
    at 259.62 MB/s (slowest-second median 177.73 MB/s). Tree read and hash took 154.23 seconds.
    These results do not establish a win in every workload or a causal gain over the historical rows;
    the cold suite has one repetition, larger-file, sustained and tree steps two, and hot shapes three.
  - Strict mode made writing and file operations 5 to 11 times slower than no cache, with stops exceeding
    100 seconds ([#16](https://github.com/devedse/QueueCache/issues/16)). The fix branch preserves its durability
    boundary; bounded Strict writes remain open.
- **Power cuts (original 0.4.604.1 comparison, not repeated on the fix build).** On every product everything that had been flushed survived. The two settings that answer flushes
  from RAM (QueueCache Fast and PrimoCache Defer-Write) lost far more of the newest, unflushed files, and QueueCache
  Fast also left the file system needing a repair ([#13](https://github.com/devedse/QueueCache/issues/13)).
  PrimoCache's did not there, because it had written none of the newest work yet. Cut while part of its held work
  was on the disk, PrimoCache also damaged NTFS in one of five fully checked cuts (NTFS reported a corrupted file in a
  sixth), and in every cut files came back with wrong contents, often another file's data
  ([Power cut while the cache is writing](#power-cut-while-the-cache-is-writing)). At that
  test's pace QueueCache Fast kept up with the disk and lost only the newest seconds, like the disk without a cache.

Contents: [Problems found](#problems-found) · [RAM disks: CrystalDiskMark](#ram-disks-crystaldiskmark) ·
[RAM caches: CrystalDiskMark](#ram-caches-crystaldiskmark) ·
[RAM disks: complete comparison](#ram-disks-complete-comparison) ·
[RAM caches: complete comparison](#ram-caches-complete-comparison) ·
[Power cut while the cache is writing](#power-cut-while-the-cache-is-writing) ·
[Results per tool](#results-per-tool) · [Not tested](#not-tested) · [How this was measured](#how-this-was-measured)

## Problems found

Failed runs, data loss and other problems worth knowing. They are also marked in the Problems column of both
comparison tables.

- ⚠ **[QueueCache 0.4.604.1, Fast, 2 GiB](comparison/C01-queuecache-fast.md)**: File system needed an offline repair (chkdsk /f) after a simulated power cut; expected for Fast mode (as designed for now); tracked in GitHub issue [#13](https://github.com/devedse/QueueCache/issues/13); Reads of data not yet in the cache are 2.5 to 6 times slower than without any cache (first read of 1 GiB 217 against 560 MB/s; random 4 KiB 11 against 65 MB/s): QueueCache fetches missing data one request at a time; GitHub issue [#14](https://github.com/devedse/QueueCache/issues/14); Pending writes reach the disk at about half the disk's speed (105-130 MB/s in 256 KiB pieces), so long writes are slower than without a cache; GitHub issue [#15](https://github.com/devedse/QueueCache/issues/15)
- ⚠ **[QueueCache 0.4.604.1, Strict, 2 GiB](comparison/C01-queuecache-strict.md)**: 6 measurement window(s) completed nothing in 10 s (stalled): hot-r1-rnd4k-q1t1-write, hot-r1-rnd4k-q32t1-write, hot-r2-rnd4k-q1t1-write, hot-r3-rnd4k-q1t1-write, hot-r3-rnd4k-q32t1-write, hot-r3-seq1m-q8t1-write; Writes stop completely for 100 seconds or more after a burst of writes: every flush or write-through request from Windows makes all writes wait until everything queued in RAM is on the disk (confirmed with the driver's counters); GitHub issue [#16](https://github.com/devedse/QueueCache/issues/16); Writing and file operations are 5 to 11 times slower than without any cache (creating 40,000 files 213 against 32 s; 8 GiB write 53 against 242 MB/s; write-through 52 against 251 MB/s); Reads of data not yet in the cache are 3 to 6 times slower than without any cache (first read of 1 GiB 187 against 560 MB/s; random 4 KiB 11 against 65 MB/s): QueueCache fetches missing data one request at a time; GitHub issue [#14](https://github.com/devedse/QueueCache/issues/14)
- ⚠ **[PrimoCache 4.4.1, L1 2 GiB, Defer-Write](comparison/C02-primocache-defer.md)**: All 10,000 new small files written in the last seconds before a simulated power cut were lost (the file system stayed clean); flushes are answered from RAM, expected with Defer-Write; Power cuts while part of its held writes were on the disk (churn test): in 1 of 5 checked cuts NTFS needed an offline repair (volume bitmap corruption) and in another NTFS reported a file "corrupted and unreadable"; in the others files written 10-20 s earlier came back with zeros or another file's data, and deleted files came back

## RAM disks: CrystalDiskMark

A 4 GiB RAM disk at T:, NTFS. CrystalDiskMark 9.0.3 defaults (1 GiB, 5 passes), the better of two runs, the same way
as the [README](../README.md#performance). Click a screenshot for full size, or a product for its full results.

| Product | CrystalDiskMark | SEQ1M Q8T1 read / write | SEQ1M Q1T1 read / write | RND4K Q32T1 read / write | RND4K Q1T1 read / write |
|---|---|---:|---:|---:|---:|
| [QueueCache 0.4.604.1, RAM disk](comparison/R01-queuecache.md) | <a href="images/comparison/R01-queuecache.png"><img src="images/comparison/R01-queuecache.png" width="300" alt="CrystalDiskMark, QueueCache 0.4.604.1, RAM disk"></a> | 45,610 / 24,026 | 25,735 / 16,832 | 1,451 / 1,184 | 1,601 / 1,270 |
| [SoftPerfect RAM Disk 26.10, static](comparison/R02-softperfect.md) | <a href="images/comparison/R02-softperfect.png"><img src="images/comparison/R02-softperfect.png" width="300" alt="CrystalDiskMark, SoftPerfect RAM Disk 26.10, static"></a> | 30,054 / 20,942 | 13,655 / 12,968 | 1,484 / 1,257 | 1,544 / 1,278 |
| [SoftPerfect RAM Disk 26.10, hard-disk emulation](comparison/R02-softperfect-hdd.md) | <a href="images/comparison/R02-softperfect-hdd.png"><img src="images/comparison/R02-softperfect-hdd.png" width="300" alt="CrystalDiskMark, SoftPerfect RAM Disk 26.10, hard-disk emulation"></a> | 34,474 / 21,945 | 13,470 / 10,927 | 974 / 873 | 1,041 / 909 |
| [AIM Toolkit 20260420, static](comparison/R03-aim-static.md) | <a href="images/comparison/R03-aim-static.png"><img src="images/comparison/R03-aim-static.png" width="300" alt="CrystalDiskMark, AIM Toolkit 20260420, static"></a> | 4,435 / 4,326 | 3,802 / 3,859 | 227 / 223 | 94 / 80 |
| [AIM Toolkit 20260420, dynamic (RamDyn)](comparison/R03-aim-dynamic.md) | <a href="images/comparison/R03-aim-dynamic.png"><img src="images/comparison/R03-aim-dynamic.png" width="300" alt="CrystalDiskMark, AIM Toolkit 20260420, dynamic (RamDyn)"></a> | 3,838 / 3,358 | 3,481 / 3,031 | 178 / 177 | 79 / 80 |
| [OSFMount 3.3.1000, logical emulation](comparison/R04-osfmount-logical.md) | <a href="images/comparison/R04-osfmount-logical.png"><img src="images/comparison/R04-osfmount-logical.png" width="300" alt="CrystalDiskMark, OSFMount 3.3.1000, logical emulation"></a> | 16,340 / 17,887 | 16,211 / 17,947 | 1,653 / 1,406 | 1,713 / 1,416 |
| [OSFMount 3.3.1000, physical emulation](comparison/R04-osfmount-physical.md) | <a href="images/comparison/R04-osfmount-physical.png"><img src="images/comparison/R04-osfmount-physical.png" width="300" alt="CrystalDiskMark, OSFMount 3.3.1000, physical emulation"></a> | 15,181 / 17,081 | 15,278 / 17,152 | 863 / 783 | 918 / 807 |
| [ImDisk Toolkit 20250206, AWE](comparison/R05-imdisk.md) | <a href="images/comparison/R05-imdisk.png"><img src="images/comparison/R05-imdisk.png" width="300" alt="CrystalDiskMark, ImDisk Toolkit 20250206, AWE"></a> | 14,556 / 16,841 | 14,546 / 16,787 | 591 / 551 | 577 / 551 |
| [Primo Ramdisk 6.6.0, Direct-IO](comparison/R06-primo-directio.md) | <a href="images/comparison/R06-primo-directio.png"><img src="images/comparison/R06-primo-directio.png" width="300" alt="CrystalDiskMark, Primo Ramdisk 6.6.0, Direct-IO"></a> | 27,748 / 21,574 | 17,625 / 17,358 | 624 / 329 | 1,456 / 1,228 |
| [Primo Ramdisk 6.6.0, SCSI](comparison/R06-primo-scsi.md) | <a href="images/comparison/R06-primo-scsi.png"><img src="images/comparison/R06-primo-scsi.png" width="300" alt="CrystalDiskMark, Primo Ramdisk 6.6.0, SCSI"></a> | 26,404 / 20,704 | 16,601 / 17,068 | 927 / 803 | 975 / 833 |
| [SoftPerfect RAM File System 26.10, default](comparison/R08-softperfect-ramfs.md) | <a href="images/comparison/R08-softperfect-ramfs.png"><img src="images/comparison/R08-softperfect-ramfs.png" width="300" alt="CrystalDiskMark, SoftPerfect RAM File System 26.10, default"></a> | 30,959 / 21,891 | 25,093 / 23,479 | 2,524 / 2,163 | 2,932 / 2,386 |
| [ManagedDrive 2.3.0, WinFsp 2.1](comparison/R10-manageddrive.md) | <a href="images/comparison/R10-manageddrive.png"><img src="images/comparison/R10-manageddrive.png" width="300" alt="CrystalDiskMark, ManagedDrive 2.3.0, WinFsp 2.1"></a> | 3,992 / 8,925 | 3,027 / 6,978 | 87 / 110 | 85 / 79 |

## RAM caches: CrystalDiskMark

A 2 GiB RAM cache on Q:, a 200 GB virtual SSD on network storage. The test file fits in the cache, so this shows
cached data. Writes in write-back modes are acknowledged from RAM; [the complete comparison](#ram-caches-complete-comparison)
shows what happens when they have to reach the disk.

| Product | CrystalDiskMark | SEQ1M Q8T1 read / write | SEQ1M Q1T1 read / write | RND4K Q32T1 read / write | RND4K Q1T1 read / write |
|---|---|---:|---:|---:|---:|
| [Q: without any cache Windows 11, baseline](comparison/C00-no-cache.md) | <a href="images/comparison/C00-no-cache.png"><img src="images/comparison/C00-no-cache.png" width="300" alt="CrystalDiskMark, Q: without any cache Windows 11, baseline"></a> | 628 / 259 | 278 / 123 | 117 / 11 | 20 / 5 |
| [QueueCache 0.4.604.1, Fast, 2 GiB](comparison/C01-queuecache-fast.md) | <a href="images/comparison/C01-queuecache-fast.png"><img src="images/comparison/C01-queuecache-fast.png" width="300" alt="CrystalDiskMark, QueueCache 0.4.604.1, Fast, 2 GiB"></a> | 40,322 / 21,471 | 19,534 / 13,560 | 1,701 / 1,327 | 1,307 / 1,028 |
| [QueueCache 0.4.687.1, Fast, 2 GiB, 1024 KiB × 4](comparison/C01-queuecache-fast-fix687.md) | <a href="images/comparison/C01-queuecache-fast-fix687.png"><img src="images/comparison/C01-queuecache-fast-fix687.png" width="300" alt="CrystalDiskMark, QueueCache 0.4.687.1, Fast, 2 GiB, 1024 KiB × 4"></a> | 37,375 / 19,981 | 19,875 / 13,576 | 1,704 / 1,411 | 1,298 / 1,029 |
| [QueueCache 0.4.604.1, Strict, 2 GiB](comparison/C01-queuecache-strict.md) | <a href="images/comparison/C01-queuecache-strict.png"><img src="images/comparison/C01-queuecache-strict.png" width="300" alt="CrystalDiskMark, QueueCache 0.4.604.1, Strict, 2 GiB"></a> | 39,989 / 21,310 | 19,414 / 12,937 | 1,659 / 331 | 1,267 / 871 |
| [PrimoCache 4.4.1, L1 2 GiB, Defer-Write](comparison/C02-primocache-defer.md) | <a href="images/comparison/C02-primocache-defer.png"><img src="images/comparison/C02-primocache-defer.png" width="300" alt="CrystalDiskMark, PrimoCache 4.4.1, L1 2 GiB, Defer-Write"></a> | 16,445 / 18,035 | 14,087 / 4,139 | 505 / 336 | 1,003 / 776 |
| [PrimoCache 4.4.1, L1 2 GiB, no Defer-Write](comparison/C02-primocache-through.md) | <a href="images/comparison/C02-primocache-through.png"><img src="images/comparison/C02-primocache-through.png" width="300" alt="CrystalDiskMark, PrimoCache 4.4.1, L1 2 GiB, no Defer-Write"></a> | 17,304 / 261 | 14,656 / 123 | 474 / 12 | 1,044 / 5 |

## RAM disks: complete comparison

Medians of three repetitions measured with DiskSpd on a 1 GiB file (ranges on each tool's page). Q is the number of
requests one program keeps in flight, T the number of programs (threads). The 4K Q1 p99 column is the latency that
99% of single 4 KiB requests stay under. CPU is the share of all four processors busy during the 1 MiB Q8 read.
RAM used is how much less memory Windows had available right after the empty 4 GiB disk was created: disks that reserve all their memory up front show about 4,096 MiB, disks that allocate memory as data arrives show little (each tool's page has memory snapshots after writing 1 GiB and 2 GiB more). The file tree is 40,000
files (1.2 GiB): create, copy with four threads, read back and check, delete.

| Product | SEQ1M Q1T1 R / W | SEQ1M Q8T1 R / W | SEQ1M Q8T4 R / W | RND4K Q1T1 R / W | RND4K Q32T1 R / W | RND4K Q32T4 R / W | 4K Q1 p99 R / W (µs) | CPU, 1M Q8 read | RAM used by the empty disk (MiB) | File tree create / copy / read / delete (s) | Price | Problems | Status |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---|---|
| [QueueCache 0.4.604.1, RAM disk](comparison/R01-queuecache.md) | 25,103 / 16,743 | 45,245 / 23,625 | 25,450 / 25,876 | 1,611 / 1,234 | 1,457 / 1,157 | 5,071 / 3,844 | 6 / 10 | 93 % | 4,077 | 4.7 / 4.5 / 3.0 / 5.2 | Free (MIT) | None | Measured |
| [SoftPerfect RAM Disk 26.10, static](comparison/R02-softperfect.md) | 12,171 / 12,951 | 30,465 / 20,990 | 30,595 / 25,267 | 1,585 / 1,301 | 1,500 / 1,287 | 5,182 / 4,224 | 4 / 5 | 64 % | 4,084 | 4.5 / 4.4 / 3.0 / 4.5 | $29 per PC (30-day trial used) | None | Measured |
| [SoftPerfect RAM Disk 26.10, hard-disk emulation](comparison/R02-softperfect-hdd.md) | 14,146 / 10,747 | 35,386 / 21,317 | 29,851 / 26,003 | 1,053 / 921 | 986 / 886 | 3,482 / 3,023 | 11 / 14 | 67 % | 4,063 | 4.4 / 4.5 / 2.9 / 4.4 | $29 per PC (30-day trial used) | None | Measured |
| [AIM Toolkit 20260420, static](comparison/R03-aim-static.md) | 3,773 / 3,703 | 4,428 / 4,385 | 4,392 / 4,417 | 82 / 90 | 227 / 220 | 220 / 217 | 77 / 58 | 32 % | 1,031 | 5.1 / 5.6 / 3.0 / 5.4 | Free (AGPL) | None | Measured |
| [AIM Toolkit 20260420, dynamic (RamDyn)](comparison/R03-aim-dynamic.md) | 3,227 / 2,906 | 3,739 / 3,247 | 3,711 / 3,253 | 75 / 82 | 179 / 176 | 180 / 174 | 83 / 69 | 31 % | 4 | 5.2 / 6.1 / 3.1 / 5.5 | Free (AGPL) | None | Measured |
| [OSFMount 3.3.1000, logical emulation](comparison/R04-osfmount-logical.md) | 16,400 / 18,128 | 16,410 / 18,212 | 23,165 / 25,591 | 1,744 / 1,390 | 1,679 / 1,368 | 5,697 / 4,534 | 3 / 4 | 26 % | 4,209 | 4.5 / 4.3 / 2.9 / 4.2 | Free | None | Measured |
| [OSFMount 3.3.1000, physical emulation](comparison/R04-osfmount-physical.md) | 15,409 / 17,399 | 15,180 / 17,206 | 22,864 / 25,591 | 934 / 805 | 876 / 779 | 3,108 / 2,698 | 14 / 17 | 25 % | 4,045 | 5.1 / 4.5 / 2.9 / 4.5 | Free | None | Measured |
| [ImDisk Toolkit 20250206, AWE](comparison/R05-imdisk.md) | 14,800 / 17,102 | 14,888 / 17,108 | 20,128 / 22,247 | 584 / 537 | 548 / 516 | 982 / 989 | 20 / 21 | 25 % | 4,149 | 4.5 / 4.4 / 2.9 / 4.4 | Free | None | Measured |
| [Primo Ramdisk 6.6.0, Direct-IO](comparison/R06-primo-directio.md) | 17,967 / 17,793 | 27,548 / 20,958 | 27,211 / 25,344 | 1,480 / 1,208 | 587 / 342 | 2,882 / 2,135 | 4 / 7 | 62 % | 4,073 | 4.6 / 4.9 / 3.0 / 4.6 | $24.95-54.95 (30-day trial used) | None | Measured |
| [Primo Ramdisk 6.6.0, SCSI](comparison/R06-primo-scsi.md) | 17,031 / 17,440 | 26,656 / 20,988 | 27,135 / 24,998 | 994 / 828 | 941 / 806 | 3,392 / 2,913 | 12 / 15 | 62 % | 3,994 | 4.6 / 4.7 / 3.0 / 4.6 | $24.95-54.95 (30-day trial used) | None | Measured |
| [SoftPerfect RAM File System 26.10, default](comparison/R08-softperfect-ramfs.md) | 25,145 / 23,713 | 30,497 / 21,626 | 31,289 / 25,157 | 2,978 / 2,364 | 2,614 / 2,206 | 8,624 / 7,476 | 2 / 2 | 63 % | 0 | 1.6 / 1.2 / 2.6 / 1.8 | $29 per PC (30-day trial used) | None | Measured |
| [ManagedDrive 2.3.0, WinFsp 2.1](comparison/R10-manageddrive.md) | 2,745 / 6,812 | 3,208 / 8,830 | 2,698 / 8,654 | 84 / 75 | 86 / 110 | 93 / 122 | 80 / 90 | 34 % | 361 | 17.0 / 8.4 / 9.2 / 15.4 | Free (MIT) | None | Measured |

## RAM caches: complete comparison

Cached work first, then what the cache does with data that has to reach the disk:

- **Hot**: reads and writes of a 1 GiB file already in the cache.
- **First / second read**: with the cache emptied, reading a 1 GiB file from the disk and then once more: does
  the cache keep what it read?
- **8 GiB file**: a file four times the cache size.
- **Burst write**: 1 GiB written in one go; how fast it is accepted.
- **Drain**: after that burst, how long the disk below kept writing and at what speed, counted by the hypervisor
  below every driver. This is the time until the data is really on disk.
- **Sustained write**: 8 GiB in one go, more than the cache can hold; the slowest second shows stalls.
- **Write-through**: writes that ask to go straight to disk. A cache that honors this runs at disk speed here.
- **Flush**: how long `FlushFileBuffers` takes after 512 MiB of writes. Near zero means the cache did not write the
  data to disk before reporting it saved.
- **Power loss**: the virtual machine is reset right after a durable save (buffered writes, then a flush) and after
  raw writes without a flush; the share of each that survived, and whether `chkdsk /scan` found file-system
  damage afterwards.
- **70/30 mix**: five minutes of random 4 KiB reads and writes over a 4 GiB file.

| Product | Hot SEQ1M Q8T1 R / W | Hot RND4K Q1T1 R / W | First / second read of 1 GiB, cache emptied | 8 GiB file: RND4K Q32 / SEQ1M Q8 read | 1 GiB burst write | Drain after the burst: seconds / MB/s | 8 GiB sustained write: average / slowest second | Write-through 1M Q8 | Flush after 512 MiB (s) | Power loss: saved / unsaved data kept, chkdsk | 70/30 mix | File tree create / copy / read / delete (s) | Problems | Status |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---|---|
| [Q: without any cache Windows 11, baseline](comparison/C00-no-cache.md) | 571 / 251 | 12.1 / 4.1 | 560 / 573 | 86.2 / 668.6 | 256 | 2 / 230 | 242.5 / 218.6 | 251 | 0.001 | 100% / 100%, file system clean | 11.0 | 31.9 / 28.3 / 4.9 / 23.9 | None | Measured |
| [QueueCache 0.4.604.1, Fast, 2 GiB](comparison/C01-queuecache-fast.md) | 39,487 / 21,214 | 1,294.6 / 982.4 | 217 / 26,416 | 11.8 / 262.0 | 2,665 | 10 / 105 | 178.3 / 116.9 | 21,077 | 0.000 | 100% / 100%, file system needs repair (chkdsk 13) | 11.7 | 12.1 / 8.6 / 4.9 / 16.9 | ⚠ File system needed an offline repair (chkdsk /f) after a simulated power cut; expected for Fast mode (as designed for now); tracked in GitHub issue [#13](https://github.com/devedse/QueueCache/issues/13); Reads of data not yet in the cache are 2.5 to 6 times slower than without any cache (first read of 1 GiB 217 against 560 MB/s; random 4 KiB 11 against 65 MB/s): QueueCache fetches missing data one request at a time; GitHub issue [#14](https://github.com/devedse/QueueCache/issues/14); Pending writes reach the disk at about half the disk's speed (105-130 MB/s in 256 KiB pieces), so long writes are slower than without a cache; GitHub issue [#15](https://github.com/devedse/QueueCache/issues/15) | Measured |
| [QueueCache 0.4.687.1, Fast, 2 GiB, 1024 KiB × 4](comparison/C01-queuecache-fast-fix687.md) | 34,775 / 20,655 | 1,297.2 / 1,008.0 | 571 / 19,638 | 80.9 / 621.9 | 2,960 | 7 / 153 | 259.6 / 177.7 | 20,630 | 0.000 | - | 34.4 | 50.2 / 13.7 / 154.2 / 15.9 | None | Measured |
| [QueueCache 0.4.604.1, Strict, 2 GiB](comparison/C01-queuecache-strict.md) | 40,874 / 3,477 | 1,300.3 / 0.0 | 187 / 16,409 | 6.0 / 221.4 | 1,112 | 25 / 41 | 52.9 / 0.0 | 52 | 12.564 | 100% / 100%, file system clean | 4.1 | 212.9 / 129.8 / 4.9 / 263.8 | ⚠ 6 measurement window(s) completed nothing in 10 s (stalled): hot-r1-rnd4k-q1t1-write, hot-r1-rnd4k-q32t1-write, hot-r2-rnd4k-q1t1-write, hot-r3-rnd4k-q1t1-write, hot-r3-rnd4k-q32t1-write, hot-r3-seq1m-q8t1-write; Writes stop completely for 100 seconds or more after a burst of writes: every flush or write-through request from Windows makes all writes wait until everything queued in RAM is on the disk (confirmed with the driver's counters); GitHub issue [#16](https://github.com/devedse/QueueCache/issues/16); Writing and file operations are 5 to 11 times slower than without any cache (creating 40,000 files 213 against 32 s; 8 GiB write 53 against 242 MB/s; write-through 52 against 251 MB/s); Reads of data not yet in the cache are 3 to 6 times slower than without any cache (first read of 1 GiB 187 against 560 MB/s; random 4 KiB 11 against 65 MB/s): QueueCache fetches missing data one request at a time; GitHub issue [#14](https://github.com/devedse/QueueCache/issues/14) | Measured |
| [PrimoCache 4.4.1, L1 2 GiB, Defer-Write](comparison/C02-primocache-defer.md) | 17,172 / 19,036 | 1,020.5 / 522.1 | 512 / 13,883 | 129.9 / 384.0 | 8,444 | 7 / 149 | 486.2 / 374.9 | 19,013 | 0.001 | 100% / 100%, file system clean | 23.7 | 21.4 / 8.4 / 4.9 / 14.9 | ⚠ All 10,000 new small files written in the last seconds before a simulated power cut were lost (the file system stayed clean); flushes are answered from RAM, expected with Defer-Write; Power cuts while part of its held writes were on the disk (churn test): in 1 of 5 checked cuts NTFS needed an offline repair (volume bitmap corruption) and in another NTFS reported a file "corrupted and unreadable"; in the others files written 10-20 s earlier came back with zeros or another file's data, and deleted files came back | Measured |
| [PrimoCache 4.4.1, L1 2 GiB, no Defer-Write](comparison/C02-primocache-through.md) | 17,253 / 250 | 1,056.1 / 4.1 | 409 / 16,775 | 128.9 / 413.9 | 259 | 2 / 289 | 259.4 / 237.5 | 255 | 0.001 | 100% / 100%, file system clean | 12.7 | 28.1 / 23.1 / 4.9 / 24.2 | None | Measured |

## Power cut while the cache is writing

The power-loss column above cuts the power right after a test, before a delayed-write cache has written anything new,
so the disk still holds the older, consistent state. This test cuts it while part of the newer work is on the disk and
part is not (`compare-durability-churn`):

1. 2,000 files are written and given time to reach the disk.
2. For 40 seconds, 200 new files per second are created without a flush. Every eighth step one of the first 1,000 old
   files is deleted and, from step 1,000 on, the new file created 1,000 steps earlier, so freed disk space is reused.
3. The virtual machine is reset about 2 seconds after the last file (or, in the first cuts, the moment the disk below
   was written afterwards). Each cut starts from the same restored snapshot.
4. After the restart every file is checked, grouped by its age when the churn ended, and `chkdsk /scan` runs.

**Damaged** means the file exists but holds the wrong contents: zeros, part of its data, or **another file's data**
(old contents of reused disk space). A deleted file that comes back means its deletion was lost. The disk without a
cache is the yardstick: Windows itself only loses the newest seconds, and those come back as zeros or missing.

| Product | Cut | Power cut | `chkdsk /scan` | Files 20-30 s old: missing / damaged | 10-20 s old: missing / damaged | Newest 10 s: intact / damaged / missing | Damaged files holding another file's data | Deleted files back (intact / damaged) |
|---|---|---|---|---:|---:|---:|---:|---:|
| Q: without a cache task (PrimoCache driver idle) | 1 | about 2 s after the churn ended | clean | 0 / 0 | 0 / 0 | 1833 / 41 / 0 | 0 | 0 / 0 |
| Q: without any cache | 2 | during the write-out, 20 s after the churn | clean | 0 / 0 | 0 / 0 | 1830 / 0 / 0 | not classified | 0 / 0 |
| QueueCache 0.4.604.1 Fast 2 GiB | 1 | about 2 s after the churn ended | clean | 0 / 0 | 0 / 0 | 1568 / 265 / 42 | 0 | 11 / 0 |
| QueueCache 0.4.604.1 Fast 2 GiB | 2 | about 2 s after the churn ended | clean | 0 / 0 | 0 / 0 | 1567 / 266 / 41 | 0 | 11 / 0 |
| QueueCache 0.4.604.1 Fast 2 GiB | 3 | during the write-out, 5 s after the churn | clean | 0 / 0 | 0 / 0 | 1875 / 0 / 0 | not classified | 0 / 0 |
| QueueCache 0.4.604.1 Fast 2 GiB | 4 | during the write-out, 10 s after the churn | clean | 0 / 0 | 0 / 0 | 1875 / 0 / 0 | not classified | 0 / 0 |
| PrimoCache 4.4.1 Defer-Write 2 GiB | 1 | about 2 s after the churn ended | clean | 0 / 0 | 0 / 316 | 0 / 44 / 1831 | not classified | 304 / 65 |
| PrimoCache 4.4.1 Defer-Write 2 GiB | 2 | about 2 s after the churn ended | clean | 0 / 0 | 0 / 49 | 286 / 232 / 1356 | not classified | 265 / 36 |
| PrimoCache 4.4.1 Defer-Write 2 GiB | 3 | about 2 s after the churn ended | ⚠ not run: the check stopped, NTFS reported a file "corrupted and unreadable" | - | - | - | - | - |
| PrimoCache 4.4.1 Defer-Write 2 GiB | 4 | about 2 s after the churn ended | ⚠ needs offline repair (exit 13), 26 files unreadable | 369 / 50 | 1751 / 0 | 0 / 0 / 1875 | 152 | 275 / 320 |
| PrimoCache 4.4.1 Defer-Write 2 GiB | 5 | about 2 s after the churn ended | clean | 0 / 0 | 0 / 189 | 446 / 309 / 1120 | 144 | 221 / 47 |
| PrimoCache 4.4.1 Defer-Write 2 GiB | 6 | about 2 s after the churn ended | clean | 0 / 0 | 0 / 92 | 530 / 232 / 1113 | 88 | 235 / 31 |
| PrimoCache 4.4.1 Defer-Write 2 GiB | 7 | during the write-out, 11 s after the churn | clean | 0 / 0 | 0 / 0 | 1875 / 0 / 0 | not classified | 0 / 0 |

## Results per tool

Each product links to its own page: both CrystalDiskMark runs, every DiskSpd shape with latency percentiles and CPU,
the timed steps, memory snapshots and the drivers that were loaded.

- [QueueCache 0.4.604.1, RAM disk](comparison/R01-queuecache.md): Measured
- [SoftPerfect RAM Disk 26.10, static](comparison/R02-softperfect.md): Measured
- [SoftPerfect RAM Disk 26.10, hard-disk emulation](comparison/R02-softperfect-hdd.md): Measured
- [AIM Toolkit 20260420, static](comparison/R03-aim-static.md): Measured
- [AIM Toolkit 20260420, dynamic (RamDyn)](comparison/R03-aim-dynamic.md): Measured
- [OSFMount 3.3.1000, logical emulation](comparison/R04-osfmount-logical.md): Measured
- [OSFMount 3.3.1000, physical emulation](comparison/R04-osfmount-physical.md): Measured
- [ImDisk Toolkit 20250206, AWE](comparison/R05-imdisk.md): Measured
- [Primo Ramdisk 6.6.0, Direct-IO](comparison/R06-primo-directio.md): Measured
- [Primo Ramdisk 6.6.0, SCSI](comparison/R06-primo-scsi.md): Measured
- [SoftPerfect RAM File System 26.10, default](comparison/R08-softperfect-ramfs.md): Measured
- [ManagedDrive 2.3.0, WinFsp 2.1](comparison/R10-manageddrive.md): Measured
- [Q: without any cache Windows 11, baseline](comparison/C00-no-cache.md): Measured
- [QueueCache 0.4.604.1, Fast, 2 GiB](comparison/C01-queuecache-fast.md): Measured
- [QueueCache 0.4.687.1, Fast, 2 GiB, 1024 KiB × 4](comparison/C01-queuecache-fast-fix687.md): Measured
- [QueueCache 0.4.604.1, Strict, 2 GiB](comparison/C01-queuecache-strict.md): Measured
- [PrimoCache 4.4.1, L1 2 GiB, Defer-Write](comparison/C02-primocache-defer.md): Measured
- [PrimoCache 4.4.1, L1 2 GiB, no Defer-Write](comparison/C02-primocache-through.md): Measured

## Not tested

| Product | Kind | Why not |
|---|---|---|
| GiliSoft RAMDisk 7.2.0 | RAM disk | Installed and tried: the trial limits disks to 512 MB, too small for the 1 GiB tests on a 4 GiB disk. A licence costs $39.95. |
| RamDrive 0.4.7 (WinFsp) | RAM file system | Installed and tried: it is built for newer processor instructions than this VM's virtual CPU offers ("The required instruction sets are not supported by the current CPU"), so its service stops at start. It would likely run on the physical machine. |
| StarWind RAM Disk | RAM disk | Download requires a registration form; can be added if the owner downloads it. |
| Condusiv DymaxIO | RAM read cache | Trial requires an account, and its EULA (s.5.2(vii)) forbids publishing benchmark results without consent. |
| Arsenal Image Mounter (RAM disk) | RAM disk | Same Storport driver as AIM Toolkit, which is tested. |
| Ultra RAMDisk Lite | RAM disk | The vendor site serves a self-signed certificate; no trustworthy download. |
| AMD Radeon RAMDisk / Dataram RAMDisk | RAM disk | Discontinued; no vendor download any more. |
| WinRamTech (QSoft) Ramdisk Enterprise | RAM disk | Development ended in 2017; the free evaluation is limited to 256 MB, too small for the 1 GiB test. |
| ERAM | RAM disk | Unsigned 2010-era driver; needs driver signature enforcement turned off, a boot setting we do not change. |
| Dimmdrive | RAM disk | Abandoned in 2016; sold only through Steam. |
| Gavotte RamDisk | RAM disk | 32-bit, unsigned driver. |
| SuperSpeed RamDisk Plus | RAM disk | Vendor gone; no legitimate download. |
| VSuite Ramdisk | RAM disk | Old name of Primo Ramdisk, which is tested. |
| Windows built-in RAM disk | RAM disk | Windows 11 has none for users. |
| MaxVeloSSD | RAM/SSD cache | No vendor download any more. |
| Samsung RAPID (Magician) | RAM cache | Needs a Samsung SATA SSD; the VM's disk is virtual. |
| Crucial Momentum Cache | RAM write cache | Needs a supported Crucial boot SSD; caches writes only. |
| ASUS RAMCache II/III | RAM cache | Checks for an ASUS motherboard. |
| AMD StoreMI 2.x | SSD cache | No RAM cache since version 1.x ended in 2020; needs an AMD chipset. |
| eBoostr | RAM cache | Sales ended in 2022; the vendor warns its RAM cache freezes or crashes Windows 10/11. |
| VeloSSD / VeloRAM | RAM/SSD cache | Vendor domain parked; no trustworthy installer. |
| SuperCache (SuperSpeed) | RAM cache | 2013-era driver; vendor gone. |
| FancyCache | RAM cache | Old name of PrimoCache, which is tested. |
| O&O CleverCache | File-cache tuner | Discontinued; tunes the Windows file cache rather than caching blocks. |
| Condusiv V-locity | RAM read cache | Business sibling of DymaxIO with the same engine. |
| Intel RST / Optane Memory | Optane cache | Uses Optane hardware, not RAM; end of life. |
| Windows ReadyBoost | Flash cache | Uses USB flash, not RAM. |
| SSD-tier caches (PrimoCache L2, FuzeDrive and others) | SSD cache | Not RAM caches. |

## How this was measured

| | |
|---|---|
| Host | AMD Ryzen 9 9955HX (16 cores), 96 GB DDR5-5600, Proxmox VE 9.2 |
| Virtual machine | Windows 11 (26H1, build 28000), 4 vCPUs, 16 GB RAM, no Driver Verifier, Memory Integrity off |
| Q: | 200 GB virtual SSD on Ceph network storage |
| Tools | CrystalDiskMark 9.0.3 and its DiskSpd 2.2 for every product; QueueCache's `qcache developer verify` comparison suites (plan 121), which use no QueueCache feature |

- **One product at a time.** Each product was installed on the same snapshot of the machine without QueueCache,
  measured, and removed by restoring that snapshot. QueueCache was measured from a snapshot with QueueCache installed.
- **Same conditions.** At least 15 minutes after a restart, normal priority, no other programs running (the file
  indexer and game launcher were closed), Windows Update paused, NTFS last-access updates off. Windows Defender stayed
  on; the benchmark folders (`T:\` and `Q:\QCBench`) were excluded from scanning, as is common for RAM disks, because
  scanning every new file otherwise dominates the file-tree times.
- **RAM disks**: 4 GiB, NTFS, static memory unless the mode says otherwise, no image or persistence.
- **Caches**: 2 GiB of RAM, no SSD tier, no prefetch at start-up. Each cache was emptied before its empty-cache runs.
- **Limits.** A virtual machine with four processors, whose processor model hides some copy features (ERMS), so a
  physical PC can be faster for every product. Q: sits on shared network storage, so numbers that depend on the disk
  (first reads, drains, sustained writes) vary between sessions. Product rows from different dates describe their
  recorded sessions; causal speedup claims come from the alternated same-build comparisons in the findings. Trial versions
  were used where a product is paid. File systems in RAM (marked as such) are a different design from RAM disks.
