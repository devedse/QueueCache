# Volume-level filtering

Status: implemented on branch `volume-filter` (PR #2), 2026-09-28; VM results are
below and in the tracker. QueueCache caches volumes: the filter is the topmost
volume filter and every volume has its own cache.

## Why

The driver filtered whole disks (a disk-class upper filter below `partmgr`). Every
request to the disk passed through it, including raw commands that health and disk
tools send straight to the disk (SCSI/ATA pass-through, SMART). While a disk was
cached, those commands were refused, and each SMART read drained all pending writes
and emptied the clean cache (KNOWN_ISSUES). Caching each volume instead removes that
class of problem: raw disk commands go to the disk's own device stack, which the
cache is no longer part of. Comparable caching products work per volume.

## What changes

| Area | Disk filter (master) | Volume filter (this branch) |
|---|---|---|
| Registration | Disk class `{4d36e967-...}` UpperFilters, directly below `partmgr` | Volume class `{71a27cdd-...}` UpperFilters, last entry (topmost, directly below the file system); the disk-class entry is removed |
| What it sees | Disk offsets, every request to the disk | Volume offsets, requests to one volume (from the file system, or a volume handle such as `\\.\Q:`) |
| Below it | `partmgr`, the disk class driver, the storage port | `volsnap` (snapshot copy-on-write), `volume.sys`, BitLocker (`fvevol`), `iorate`, ReadyBoost (`rdyboost`), `volmgr`, then the disk stack |
| Raw disk commands | Pass through it; refused while caching | Never reach it |
| Management requests | Sent to `\\.\PhysicalDriveN` | Sent to `\\.\X:` (the file system passes device controls to the top of the volume stack, which is this filter) |
| Target identity in the tools | `Device` = `PhysicalDriveN`, `Bytes` = disk size | `Device` = `X:`, `Bytes` = volume size (what the driver reports); `Number`, `Instance` and new `DiskBytes` still identify the physical disk |
| Page-file / gate ranges | Disk offsets (partition start + cluster) | Volume offsets (cluster only), page files of the target volume only |
| Saved profiles | Disk identity and size | Volume GUID and volume size (one format; no conversion of disk-filter profiles) |

Driver changes are small: the filter mirrors `DO_POWER_PAGABLE` (volume stacks are
power-pageable, so a filter above them must be too), and the ordered worker refuses
neither-I/O and controller controls only from applications, not from kernel
components (snapshots and encryption send kernel pointers). Everything else in the
cache (ordering, caller-thread service, offloaded copies, TRIM ranges) works on
device offsets and is unchanged.

Installer: Volume-class registration only, a registration backup (version 3: the
Volume class list and service values), uninstall that drains lettered volumes and
removes the Volume class entry, and `Recover-Registration.ps1` that restores that
backup. There is no migration from the disk filter (owner decision, 2026-09-28: no
other users); the lab VM was converted by hand.

## Boot reset on C:'s volume (found and fixed on the branch)

0.4.183.1 booted with the filter on Q:'s volume only, but with it on C:'s volume
the VM reset silently a few seconds into boot (no stop screen, no dump, no event),
with or without Driver Verifier, until Automatic Repair gave up. Recovery each
time: the Windows recovery command prompt, loading the offline SYSTEM hive and
restricting the filter to Q:'s volume again (`ClassCoverage` 0 and
`LabAllowedDriverKey`).

Bisected with a lab `DiagnosticMode` service value (0.4.185.1) on C:'s volume:
pure pass-through booted; the full driver booted with the power-pageable mirror,
dispatch paging classification and the post-start queries all disabled, and with
each of the first two re-enabled; with only the post-start queries enabled it
reset again. The driver sent `IOCTL_DISK_GET_LENGTH_INFO` and
`IOCTL_DISK_GET_DRIVE_GEOMETRY` down the volume stack from its
`IRP_MN_START_DEVICE` handler. On the disk stack that was harmless; on the boot
volume (snapshots, BitLocker and the volume manager below) it resets the machine.

Fix (0.4.187.1, `e7150b7`): the length and sector size are queried once, on first
need, from a PASSIVE_LEVEL thread: the caller of the first QueueCache management
request or the request worker. With it the filter covers every volume and boots.

## How it behaves

| Situation | Behaviour |
|---|---|
| Install | The installer registers `qcachelab` as the last Volume-class upper filter. Every volume gets the filter after a restart; volumes that appear later (a new partition, an attached VHDX) get it immediately. No cache is enabled. `qcache developer driver registration` / `qcache volume list` report the registration. |
| Several volumes on one disk | Each has its own cache, budget, counters, worker and drainers; flushing or removing one never touches another. The shared RAM limit covers all of them. Verification leases are per physical disk. |
| Saved profiles | One per volume, named by the volume GUID and recording the volume size and the disk's PnP identity; a profile follows the volume if its letter changes. Restore refuses a different volume, disk or size. |
| Commands to the physical disk | SMART/health queries, SCSI/ATA pass-through and firmware polls go to the disk's own stack: never refused, never a drain or cache wipe (`volumes/volume-raw-disk-commands`). |
| Raw reads/writes to the physical disk | Also bypass the cache. A tool that reads `\\.\PhysicalDriveN` directly while a Fast cache holds pending writes sees the disk without them; one that writes there leaves the cache with stale copies. Imaging and backup tools normally read a shadow copy, which is consistent (next row); otherwise run `qcache policy flush X:` (or pause the task) first, and never write to a disk under a cached volume. |
| Shadow copies (System Restore, backup, imaging) | `volsnap` is below the cache; Windows' flush-and-hold request passes through the cache first and drains it, so a snapshot contains data that was pending in RAM (`volumes/volume-snapshot`). |
| Resizing a volume | The resize's volume-manager control (seen: `0x56C05C`) drains and invalidates the cache first; the driver re-reads the volume length for every QueueCache request and before judging a read or write beyond the known end, so extending past the original size works (`volumes/volume-resize`; before that fix it failed with Invalid Parameter). |
| TRIM | NTFS sends the data-set TRIM to the volume; pending writes in the range are dropped, clean copies released, issued writes awaited (`trim-cache`, on a VHDX because the VM's VirtIO disks cannot TRIM, KNOWN_ISSUES). |
| BitLocker | The cache is above `fvevol`, so it holds plaintext, as the Windows file cache does; the disk still receives encrypted data. |
| Volume-handle I/O (`\\.\X:`), chkdsk, format | Goes through the cache like file-system I/O, so it is coherent. |
| Crash dumps, hibernation | Written through the dump stack below every filter, as before. Pending Fast data is lost on a crash, as before. |
| File systems | Any file system Windows mounts: NTFS, ReFS (Dev Drives), FAT32, exFAT. FAT32/exFAT have no journal: the CLI and desktop warn that a crash with Fast-mode data in RAM can damage the file system itself. Unformatted volumes pass through; a cache left on one by raw developer tests can be flushed and removed. |
| Volumes without a letter, spanned/striped volumes | The filter passes through; the tools cannot select them (a letter and one disk extent are required). |
| Uninstall | Drains and disables each lettered volume with a cache (volumes without the filter loaded are skipped), removes the Volume class entry, keeps the service and binary until the restart. `Recover-Registration.ps1` restores the backed-up Volume class list and service values (verified on copies of the SYSTEM hive). |

## Advantages and disadvantages compared with the disk filter

| Advantages | |
|---|---|
| Disk tools work normally | SMART/health monitors, SCSI/ATA pass-through and firmware polls no longer get "not supported", and no longer drain the cache or empty its read data. |
| Cache exactly what you choose | A cache belongs to one volume (for example a games volume), not to everything on the disk. Volumes on one disk can have different sizes and policies, and flushing or removing one does not stall the others. |
| Profiles follow the volume | Named by the volume GUID, so a drive-letter change does not lose or misapply a profile. |
| Automatic coverage | Volumes created later, attached VHDX files and removable fixed volumes get the filter without a restart. |
| Same speed | CrystalDiskMark rows match or beat the disk filter (results below). |

| Disadvantages | What it means / mitigation |
|---|---|
| Raw disk I/O bypasses the cache | A program reading `\\.\PhysicalDriveN` directly misses data still pending in a Fast cache; one writing there leaves stale cached copies. Backup/imaging via shadow copies is consistent; otherwise flush or pause first, and never write raw to a disk under a cached volume. |
| More lifecycle to handle | Volumes are resized, taken offline, dismounted, snapshotted and created at runtime; the boot volume's stack (snapshots, BitLocker, volume manager) is sensitive: querying it at start-up reset the machine. Each case is now handled and has a check, but it is a larger surface than a disk. |
| Plaintext above BitLocker | The cache holds unencrypted data in RAM, like the Windows file cache; dump/hibernation files stay encrypted on a BitLocker C:. |
| RAM is per volume | Two cached volumes on one disk each need their own budget; there is no pool shared per disk, and each drains independently, so the disk sees two write-back streams. |
| One more stack layer everywhere | A filter instance on every volume (including EFI/recovery), passing through when no task exists; no measurable cost found. |
| Scope | Only lettered volumes on one disk can be cached (no mount-point-only or spanned/striped volumes). FAT32/exFAT carry the no-journal risk above. |
| No upgrade path from the disk filter | By decision there is no migration code: disk-filter profiles and registrations are not converted (re-create tasks after installing). |

## Lab switches

`DiagnosticMode` (service DWORD, read at driver load) exists for bisecting boot
problems and support only; it must be absent or 0 in normal use, and every
cache-changing verification suite refuses to run otherwise: 1 = pure pass-through,
2 = do not mirror `DO_POWER_PAGABLE`, 4 = skip paging classification at dispatch,
8 = never query the length or sector size. `ClassCoverage` 0 with
`LabAllowedDriverKey` restricts the filter to one volume's driver key (first-boot
bring-up); the installer always sets `ClassCoverage` 1.

## Known limitations

- Hot-unplug, sleep/hibernate and offline recovery remain postponed (owner
  priorities), as for the disk filter.
- Raw reads and writes sent to the physical disk bypass the cache (above).
- Crash durability of Fast data is unchanged: pending data is lost on a crash.

## Verification (2026-09-28)

Maintained checks: `qcache developer verify <volume> --suite volumes` (registration,
raw disk commands, shared disk, resize, snapshot) and `--suite trim-cache` on the lab
VHDX from `qcache developer lab-disk create`; the raw `write-tests` on its unformatted
volume; host contract tests and desktop fixture tests in CI. Results on 0.4.219.1
(`7a18a57`) under standard Driver Verifier, VM with 16 GB:

| Check | Result |
|---|---|
| `volumes` on V: (lab VHDX) | 5/5 PASS, including the shadow copy containing 32 MiB that were pending in RAM |
| `volumes` on Q: (VirtIO disk) | registration, raw disk commands and snapshot PASS; shared-disk and resize SKIP by design (no lab volume there) |
| `trim-cache` on V: | PASS (pending dropped, clean released, TRIM during a drain) |
| `quick`, `policies`, `paging-coherence`, `ordering-faults`, `app-write-profile`, `pressure` on V: | PASS |
| `policies` on Q: | PASS |
| `write-tests` on X: (base, concurrent, toggle, write-through, verify) and `developer test` on V: | PASS |
| Install over the disk filter, uninstall (drained Q:'s 2 GiB cache), restart without the filter, reinstall (profile restored at startup) | PASS (0.4.191.1 - 0.4.217.1) |
| `Recover-Registration.ps1` on copies of the SYSTEM hive | Restores the backed-up lists; live registry untouched |
| Desktop on the VM console | Volumes grouped by disk; live Q: card; unformatted X: shown without settings |
| FAT32 lab disk (0.4.229.1, Driver Verifier): `volumes`, `quick`, `policies`, `paging-coherence`, `ordering-faults`, `app-write-profile`, `pressure` | PASS; snapshot SKIP (Windows takes no shadow copies of FAT), resize SKIP (Windows cannot resize FAT), `trim-cache` SKIP (FAT takes no file-level TRIM) |
| ReFS Dev Drive lab disk (0.4.229.1, Driver Verifier): same suites plus `trim-cache` | PASS, including the snapshot and TRIM; resize SKIP (Windows cannot shrink ReFS); the caller-thread share is lower on ReFS (KNOWN_ISSUES) |
| Saved-C:-profile restart soak (10 cycles, 512 MiB C: profile) | 10/10 PASS; paging recognition unexercised in 2 cycles (16 GB: no page-file I/O under the applied pressure) |
| CrystalDiskMark on Q: (Verifier off, 3 runs) | SEQ1M Q8T1 36.6-37.2 / 20.6-21.4 GB/s; RND4K Q32T1 1,576-1,638 / 1,555-1,648 MB/s; RND4K Q1T1 1,230-1,328 / 1,023-1,073 MB/s: same as or faster than the disk filter |

Earlier branch results (0.4.187.1): every volume boots; the C: program-file check
passes 3/3; a 10-cycle saved-C:-profile restart soak passed 10/10 under Driver
Verifier. CrystalDiskMark on Q: (0.4.183.1): SEQ1M Q8T1 about 36/21.6 GB/s, RND4K
Q32T1 about 1,570/1,630 MB/s, RND4K Q1T1 about 1,340/1,030 MB/s (disk filter:
36.5/21.3 GB/s, 1,550/1,310, 1,010/855).

The volume suites found six defects that the disk-level suites could not (boot reset,
resize, two shadow-copy problems, removal of a cache on an unformatted volume, and a
restoration RAM wait); all are fixed and each has a maintained check
([known issues](KNOWN_ISSUES.md)).
