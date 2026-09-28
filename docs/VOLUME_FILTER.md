# Volume-level filtering (branch `volume-filter`)

Status: experimental branch, 2026-09-28. The tracker records verification.

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
| Saved profiles | Disk size | Volume size; profiles saved with the disk size are still accepted |

Driver changes are small: the filter mirrors `DO_POWER_PAGABLE` (volume stacks are
power-pageable, so a filter above them must be too), and the ordered worker refuses
neither-I/O and controller controls only from applications, not from kernel
components (snapshots and encryption send kernel pointers). Everything else in the
cache (ordering, caller-thread service, offloaded copies, TRIM ranges) works on
device offsets and is unchanged.

Installer: Volume-class registration, a version-2 registration backup that also
records the Volume class list, uninstall that drains lettered volumes and removes
both class entries, and `Recover-Registration.ps1` that restores the Volume class
list (or, for a version-1 backup, removes only the QueueCache entry).

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

## Known limitations on the branch

- The desktop keeps one card per disk and uses that disk's first lettered volume
  as its cache.
- The destructive raw-disk developer tests (`write-tests`) address a disk with no
  volume and do not apply to a volume filter.
- Crash dumps and hibernation write through the dump stack below every filter, as
  before.
- A volume spanning several disks (spanned/striped dynamic volumes) is refused by
  the tools, as before.

## Test plan

Results so far (0.4.187.1, `e7150b7`): every volume covered, boots; all six Q:
suites pass under Driver Verifier (`policies` 42/42); the C: program-file check
passes 3/3; a 10-cycle saved-C:-profile restart soak passes 10/10 under Driver
Verifier, with the program-file check in every cycle. Raw SCSI INQUIRY on Q:'s
disk works while Q: is cached (0.4.183.1). CrystalDiskMark on Q: (0.4.183.1):
SEQ1M Q8T1 about 36/21.6 GB/s, RND4K Q32T1 about 1,570/1,630 MB/s, RND4K Q1T1
about 1,340/1,030 MB/s (disk filter: 36.5/21.3 GB/s, 1,550/1,310, 1,010/855).

1. Install the branch build with the cache restricted to Q:'s volume
   (`ClassCoverage` 0, `LabAllowedDriverKey` = Q:'s volume driver key), so the C:
   volume stack is not attached on the first boot.
2. Management requests on `\\.\Q:`, the owner's saved Q: profile, CrystalDiskMark
   rows, and read-only SCSI INQUIRY on Q:'s disk while Q: is cached.
3. The Q: suites under Driver Verifier.
4. Class coverage for every volume, then the C: program-file check and a saved-C:
   restart soak under Driver Verifier.
