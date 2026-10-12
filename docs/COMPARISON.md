# QueueCache compared with other RAM disks and RAM caches

> **Work in progress.** Measured so far: 8 of 14 RAM-disk configurations and 0 of 5 cache configurations (updated 2026-10-12). Rows marked *Not run yet* are planned. This page is regenerated from the measurements after every tool.

Every product was installed alone on the same clean Windows 11 virtual machine, measured, and removed by restoring
the machine to the same snapshot. QueueCache was measured the same way, by tools that do not use any QueueCache
feature. Scores are MB/s (1 MB = 1,000,000 bytes) unless a column says otherwise.

**Summary.** Written once every tool has run.

Contents: [RAM disks: CrystalDiskMark](#ram-disks-crystaldiskmark) ·
[RAM caches: CrystalDiskMark](#ram-caches-crystaldiskmark) ·
[RAM disks: complete comparison](#ram-disks-complete-comparison) ·
[RAM caches: complete comparison](#ram-caches-complete-comparison) ·
[Results per tool](#results-per-tool) · [Not tested](#not-tested) · [How this was measured](#how-this-was-measured)

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
| [OSFMount 3.3.1000, physical emulation](comparison/R04-osfmount-physical.md) | <a href="images/comparison/R04-osfmount-physical.png"><img src="images/comparison/R04-osfmount-physical.png" width="300" alt="CrystalDiskMark, OSFMount 3.3.1000, physical emulation"></a> | 15,432 / 16,613 | 15,422 / 16,820 | 902 / 786 | 965 / 818 |
| [ImDisk Toolkit 20250206, AWE](comparison/R05-imdisk.md) | <a href="images/comparison/R05-imdisk.png"><img src="images/comparison/R05-imdisk.png" width="300" alt="CrystalDiskMark, ImDisk Toolkit 20250206, AWE"></a> | 14,556 / 16,841 | 14,546 / 16,787 | 591 / 551 | 577 / 551 |
| [Primo Ramdisk 6.6.0, Direct-IO](comparison/R06-primo-directio.md) | Not run yet | - | - | - | - |
| [Primo Ramdisk 6.6.0, SCSI](comparison/R06-primo-scsi.md) | Not run yet | - | - | - | - |
| [GiliSoft RAMDisk 7.2.0, default](comparison/R07-gilisoft.md) | Not run yet | - | - | - | - |
| [SoftPerfect RAM File System 26.10, default](comparison/R08-softperfect-ramfs.md) | Not run yet | - | - | - | - |
| [RamDrive 0.4.7, WinFsp 2.1](comparison/R09-ramdrive.md) | Not run yet | - | - | - | - |
| [ManagedDrive 2.3.0, WinFsp 2.1](comparison/R10-manageddrive.md) | Not run yet | - | - | - | - |

## RAM caches: CrystalDiskMark

A 2 GiB RAM cache on Q:, a 200 GB virtual SSD on network storage. The test file fits in the cache, so this shows
cached data. Writes in write-back modes are acknowledged from RAM; [the complete comparison](#ram-caches-complete-comparison)
shows what happens when they have to reach the disk.

| Product | CrystalDiskMark | SEQ1M Q8T1 read / write | SEQ1M Q1T1 read / write | RND4K Q32T1 read / write | RND4K Q1T1 read / write |
|---|---|---:|---:|---:|---:|
| [Q: without any cache Windows 11, baseline](comparison/C00-no-cache.md) | Not run yet | - | - | - | - |
| [QueueCache 0.4.604.1, Fast, 2 GiB](comparison/C01-queuecache-fast.md) | Not run yet | - | - | - | - |
| [QueueCache 0.4.604.1, Strict, 2 GiB](comparison/C01-queuecache-strict.md) | Not run yet | - | - | - | - |
| [PrimoCache 4.4.1, L1 2 GiB, Defer-Write](comparison/C02-primocache-defer.md) | Not run yet | - | - | - | - |
| [PrimoCache 4.4.1, L1 2 GiB, no Defer-Write](comparison/C02-primocache-through.md) | Not run yet | - | - | - | - |

## RAM disks: complete comparison

Medians of three repetitions measured with DiskSpd on a 1 GiB file (ranges on each tool's page). Q is the number of
requests one program keeps in flight, T the number of programs (threads). The 4K Q1 p99 column is the latency that
99% of single 4 KiB requests stay under. CPU is the share of all four processors busy during the 1 MiB Q8 read.
RAM used is how much less memory Windows had available right after the empty 4 GiB disk was created: disks that reserve all their memory up front show about 4,096 MiB, disks that allocate memory as data arrives show little (each tool's page has memory snapshots after writing 1 GiB and 2 GiB more). The file tree is 40,000
files (1.2 GiB): create, copy with four threads, read back and check, delete.

| Product | SEQ1M Q1T1 R / W | SEQ1M Q8T1 R / W | SEQ1M Q8T4 R / W | RND4K Q1T1 R / W | RND4K Q32T1 R / W | RND4K Q32T4 R / W | 4K Q1 p99 R / W (µs) | CPU, 1M Q8 read | RAM used by the empty disk (MiB) | File tree create / copy / read / delete (s) | Price | Status |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---|
| [QueueCache 0.4.604.1, RAM disk](comparison/R01-queuecache.md) | 25,103 / 16,743 | 45,245 / 23,625 | 25,450 / 25,876 | 1,611 / 1,234 | 1,457 / 1,157 | 5,071 / 3,844 | 6 / 10 | 93 % | 4,077 | 4.7 / 4.5 / 3.0 / 5.2 | Free (MIT) | Measured |
| [SoftPerfect RAM Disk 26.10, static](comparison/R02-softperfect.md) | 12,171 / 12,951 | 30,465 / 20,990 | 30,595 / 25,267 | 1,585 / 1,301 | 1,500 / 1,287 | 5,182 / 4,224 | 4 / 5 | 64 % | 4,084 | 4.5 / 4.4 / 3.0 / 4.5 | $29 per PC (30-day trial used) | Measured |
| [SoftPerfect RAM Disk 26.10, hard-disk emulation](comparison/R02-softperfect-hdd.md) | 14,146 / 10,747 | 35,386 / 21,317 | 29,851 / 26,003 | 1,053 / 921 | 986 / 886 | 3,482 / 3,023 | 11 / 14 | 67 % | 4,063 | 4.4 / 4.5 / 2.9 / 4.4 | $29 per PC (30-day trial used) | Measured |
| [AIM Toolkit 20260420, static](comparison/R03-aim-static.md) | 3,773 / 3,703 | 4,428 / 4,385 | 4,392 / 4,417 | 82 / 90 | 227 / 220 | 220 / 217 | 77 / 58 | 32 % | 1,031 | 5.1 / 5.6 / 3.0 / 5.4 | Free (AGPL) | Measured |
| [AIM Toolkit 20260420, dynamic (RamDyn)](comparison/R03-aim-dynamic.md) | 3,227 / 2,906 | 3,739 / 3,247 | 3,711 / 3,253 | 75 / 82 | 179 / 176 | 180 / 174 | 83 / 69 | 31 % | 4 | 5.2 / 6.1 / 3.1 / 5.5 | Free (AGPL) | Measured |
| [OSFMount 3.3.1000, logical emulation](comparison/R04-osfmount-logical.md) | 16,400 / 18,128 | 16,410 / 18,212 | 23,165 / 25,591 | 1,744 / 1,390 | 1,679 / 1,368 | 5,697 / 4,534 | 3 / 4 | 26 % | 4,209 | 4.5 / 4.3 / 2.9 / 4.2 | Free | Measured |
| [OSFMount 3.3.1000, physical emulation](comparison/R04-osfmount-physical.md) | 15,729 / 17,279 | 15,537 / 17,122 | 23,019 / 25,051 | 980 / 822 | 912 / 784 | 3,135 / 2,729 | 13 / 17 | 25 % | 4,036 | 47.4 / 12.1 / 148.5 / 10.2 | Free | Measured |
| [ImDisk Toolkit 20250206, AWE](comparison/R05-imdisk.md) | 14,800 / 17,102 | 14,888 / 17,108 | 20,128 / 22,247 | 584 / 537 | 548 / 516 | 982 / 989 | 20 / 21 | 25 % | 4,149 | 4.5 / 4.4 / 2.9 / 4.4 | Free | Measured |
| [Primo Ramdisk 6.6.0, Direct-IO](comparison/R06-primo-directio.md) | - | - | - | - | - | - | - | - | - | - | $24.95-54.95 (30-day trial used) | Not run yet |
| [Primo Ramdisk 6.6.0, SCSI](comparison/R06-primo-scsi.md) | - | - | - | - | - | - | - | - | - | - | $24.95-54.95 (30-day trial used) | Not run yet |
| [GiliSoft RAMDisk 7.2.0, default](comparison/R07-gilisoft.md) | - | - | - | - | - | - | - | - | - | - | $39.95 (trial used) | Not run yet |
| [SoftPerfect RAM File System 26.10, default](comparison/R08-softperfect-ramfs.md) | - | - | - | - | - | - | - | - | - | - | $29 per PC (30-day trial used) | Not run yet |
| [RamDrive 0.4.7, WinFsp 2.1](comparison/R09-ramdrive.md) | - | - | - | - | - | - | - | - | - | - | Free (MIT) | Not run yet |
| [ManagedDrive 2.3.0, WinFsp 2.1](comparison/R10-manageddrive.md) | - | - | - | - | - | - | - | - | - | - | Free (MIT) | Not run yet |

## RAM caches: complete comparison

Cached work first, then what the cache does with data that has to reach the disk:

- **Hot**: reads and writes of a 1 GiB file already in the cache.
- **First / third read**: reading a 1 GiB file from the disk, then again after it was read twice: does the cache
  keep what it read?
- **8 GiB file**: a file four times the cache size.
- **Burst write**: 1 GiB written in one go; how fast it is accepted.
- **Drain**: after that burst, how long the disk below kept writing and at what speed, counted by the hypervisor
  below every driver. This is the time until the data is really on disk.
- **Sustained write**: 8 GiB in one go, more than the cache can hold; the slowest second shows stalls.
- **Write-through**: writes that ask to go straight to disk. A cache that honors this runs at disk speed here.
- **Flush**: how long `FlushFileBuffers` takes after 512 MiB of writes. Near zero means the cache did not write the
  data to disk before reporting it saved.
- **Power loss**: the virtual machine is reset right after a durable save (buffered writes, then a flush) and after
  raw writes without a flush; the share of each that survived, and whether `chkdsk /scan` found damage (0 = none).
- **70/30 mix**: five minutes of random 4 KiB reads and writes over a 4 GiB file.

| Product | Hot SEQ1M Q8T1 R / W | Hot RND4K Q1T1 R / W | First / third read of 1 GiB | 8 GiB file: RND4K Q32 / SEQ1M Q8 read | 1 GiB burst write | Drain after the burst: seconds / MB/s | 8 GiB sustained write: average / slowest second | Write-through 1M Q8 | Flush after 512 MiB (s) | Power loss: saved / unsaved data kept, chkdsk | 70/30 mix | File tree create / copy / read / delete (s) | Status |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---|
| [Q: without any cache Windows 11, baseline](comparison/C00-no-cache.md) | - | - | - | - | - | - | - | - | - | - | - | - | Not run yet |
| [QueueCache 0.4.604.1, Fast, 2 GiB](comparison/C01-queuecache-fast.md) | - | - | - | - | - | - | - | - | - | - | - | - | Not run yet |
| [QueueCache 0.4.604.1, Strict, 2 GiB](comparison/C01-queuecache-strict.md) | - | - | - | - | - | - | - | - | - | - | - | - | Not run yet |
| [PrimoCache 4.4.1, L1 2 GiB, Defer-Write](comparison/C02-primocache-defer.md) | - | - | - | - | - | - | - | - | - | - | - | - | Not run yet |
| [PrimoCache 4.4.1, L1 2 GiB, no Defer-Write](comparison/C02-primocache-through.md) | - | - | - | - | - | - | - | - | - | - | - | - | Not run yet |

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
- [Primo Ramdisk 6.6.0, Direct-IO](comparison/R06-primo-directio.md): Not run yet
- [Primo Ramdisk 6.6.0, SCSI](comparison/R06-primo-scsi.md): Not run yet
- [GiliSoft RAMDisk 7.2.0, default](comparison/R07-gilisoft.md): Not run yet
- [SoftPerfect RAM File System 26.10, default](comparison/R08-softperfect-ramfs.md): Not run yet
- [RamDrive 0.4.7, WinFsp 2.1](comparison/R09-ramdrive.md): Not run yet
- [ManagedDrive 2.3.0, WinFsp 2.1](comparison/R10-manageddrive.md): Not run yet
- [Q: without any cache Windows 11, baseline](comparison/C00-no-cache.md): Not run yet
- [QueueCache 0.4.604.1, Fast, 2 GiB](comparison/C01-queuecache-fast.md): Not run yet
- [QueueCache 0.4.604.1, Strict, 2 GiB](comparison/C01-queuecache-strict.md): Not run yet
- [PrimoCache 4.4.1, L1 2 GiB, Defer-Write](comparison/C02-primocache-defer.md): Not run yet
- [PrimoCache 4.4.1, L1 2 GiB, no Defer-Write](comparison/C02-primocache-through.md): Not run yet

## Not tested

| Product | Kind | Why not |
|---|---|---|
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
  (first reads, drains, sustained writes) vary between sessions; compare them within one table only. Trial versions
  were used where a product is paid. File systems in RAM (marked as such) are a different design from RAM disks.
