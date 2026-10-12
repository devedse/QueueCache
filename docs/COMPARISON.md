# QueueCache compared with other RAM disks and RAM caches

> **Work in progress.** Measured so far: 0 of 14 RAM-disk configurations and 0 of 5 cache configurations (updated 2026-10-12). Rows marked *Not run yet* are planned. This page is regenerated from the measurements after every tool.

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
| [QueueCache 0.4.604.1, RAM disk](comparison/R01-queuecache.md) | Not run yet | - | - | - | - |
| [SoftPerfect RAM Disk 26.10, static](comparison/R02-softperfect.md) | Not run yet | - | - | - | - |
| [SoftPerfect RAM Disk 26.10, hard-disk emulation](comparison/R02-softperfect-hdd.md) | Not run yet | - | - | - | - |
| [AIM Toolkit 20260420, static](comparison/R03-aim-static.md) | Not run yet | - | - | - | - |
| [AIM Toolkit 20260420, dynamic (RamDyn)](comparison/R03-aim-dynamic.md) | Not run yet | - | - | - | - |
| [OSFMount 3.3.1000, logical emulation](comparison/R04-osfmount-logical.md) | Not run yet | - | - | - | - |
| [OSFMount 3.3.1000, physical emulation](comparison/R04-osfmount-physical.md) | Not run yet | - | - | - | - |
| [ImDisk Toolkit 20250206, AWE](comparison/R05-imdisk.md) | Not run yet | - | - | - | - |
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
RAM used is how much less memory Windows had available after the 4 GiB disk was created. The file tree is 40,000
files (1.2 GiB): create, copy with four threads, read back and check, delete.

| Product | SEQ1M Q1T1 R / W | SEQ1M Q8T1 R / W | SEQ1M Q8T4 R / W | RND4K Q1T1 R / W | RND4K Q32T1 R / W | RND4K Q32T4 R / W | 4K Q1 p99 R / W (µs) | CPU, 1M Q8 read | RAM used (MiB) | File tree create / copy / read / delete (s) | Price | Status |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---|
| [QueueCache 0.4.604.1, RAM disk](comparison/R01-queuecache.md) | - | - | - | - | - | - | - | - | - | - | Free (MIT) | Not run yet |
| [SoftPerfect RAM Disk 26.10, static](comparison/R02-softperfect.md) | - | - | - | - | - | - | - | - | - | - | $29 per PC (30-day trial used) | Not run yet |
| [SoftPerfect RAM Disk 26.10, hard-disk emulation](comparison/R02-softperfect-hdd.md) | - | - | - | - | - | - | - | - | - | - | $29 per PC (30-day trial used) | Not run yet |
| [AIM Toolkit 20260420, static](comparison/R03-aim-static.md) | - | - | - | - | - | - | - | - | - | - | Free (AGPL) | Not run yet |
| [AIM Toolkit 20260420, dynamic (RamDyn)](comparison/R03-aim-dynamic.md) | - | - | - | - | - | - | - | - | - | - | Free (AGPL) | Not run yet |
| [OSFMount 3.3.1000, logical emulation](comparison/R04-osfmount-logical.md) | - | - | - | - | - | - | - | - | - | - | Free | Not run yet |
| [OSFMount 3.3.1000, physical emulation](comparison/R04-osfmount-physical.md) | - | - | - | - | - | - | - | - | - | - | Free | Not run yet |
| [ImDisk Toolkit 20250206, AWE](comparison/R05-imdisk.md) | - | - | - | - | - | - | - | - | - | - | Free | Not run yet |
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

- [QueueCache 0.4.604.1, RAM disk](comparison/R01-queuecache.md): Not run yet
- [SoftPerfect RAM Disk 26.10, static](comparison/R02-softperfect.md): Not run yet
- [SoftPerfect RAM Disk 26.10, hard-disk emulation](comparison/R02-softperfect-hdd.md): Not run yet
- [AIM Toolkit 20260420, static](comparison/R03-aim-static.md): Not run yet
- [AIM Toolkit 20260420, dynamic (RamDyn)](comparison/R03-aim-dynamic.md): Not run yet
- [OSFMount 3.3.1000, logical emulation](comparison/R04-osfmount-logical.md): Not run yet
- [OSFMount 3.3.1000, physical emulation](comparison/R04-osfmount-physical.md): Not run yet
- [ImDisk Toolkit 20250206, AWE](comparison/R05-imdisk.md): Not run yet
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
