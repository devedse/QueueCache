# How the cache works

Where QueueCache sits in Windows' storage stack, how it serves requests, and what
Fast and Strict promise.

Fast mode is the optimized, recommended mode; Strict mode is supported for
correctness but is not tuned for speed (owner decision, 2026-09-27).

Current implementation boundary (2026-09-25): RAM admission applies to eligible
writes, including ordinary application file-cache write-back and memory-mapped
writes (paging-marked requests whose originating file is not a paging file).
Paging-file and unknown-origin requests take coherent ordered lower I/O. Paging
read misses are not newly retained; resident sectors still serve coherent reads.
See the [T085 design](history/T085_APPLICATION_CACHING_DESIGN.md).

## Where the cache sits (volume filter)

QueueCache filters volumes: it is the topmost volume filter, directly below the file
system, and every volume has its own cache (state, budget, counters, worker and
drainers), also when several volumes share one disk. Offsets are volume offsets.
Requests reach it from the file system, or from an application that opens the volume
(`\\.\Q:`). Requests sent to the physical disk (`\\.\PhysicalDriveN`: SMART
and health queries, SCSI/ATA pass-through, firmware polls) go to the disk's own
stack and never reach the cache, so they cannot drain it, empty it or be refused by
it. Raw reads and writes sent to the physical disk also bypass it; see
[known issues](KNOWN_ISSUES.md) for what that means for disk-imaging tools.
Background: [why a volume filter](#why-a-volume-filter).

QueueCache uses one block index: dirty writes, retained clean writes and clean reads never need separate copies of the same current block. An older in-flight write can temporarily coexist with its newer replacement. Reads select the newest version; the older version must finish before its replacement can be written to disk.

### Why a volume filter

Status: implemented (PR #2, merged 2026-09-28). The VM results from that time are in
the [verification record](KNOWN_ISSUES.md#volume-filter-verification-2026-09-28); current gaps are in [known issues](KNOWN_ISSUES.md). QueueCache caches volumes: the filter is the topmost
volume filter and every volume has its own cache.

The driver filtered whole disks (a disk-class upper filter below `partmgr`). Every
request to the disk passed through it, including raw commands that health and disk
tools send straight to the disk (SCSI/ATA pass-through, SMART). While a disk was
cached, those commands were refused, and each SMART read drained all pending writes
and emptied the clean cache (KNOWN_ISSUES). Caching each volume instead removes that
class of problem: raw disk commands go to the disk's own device stack, which the
cache is no longer part of. Comparable caching products work per volume.

### What changed from the disk filter

| Area | Disk filter (before) | Volume filter (now) |
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

### How the volume filter behaves

| Situation | Behaviour |
|---|---|
| Install | The installer registers `qcachelab` as the last Volume-class upper filter. Every volume gets the filter after a restart; volumes that appear later (a new partition, an attached VHDX) get it immediately. No cache is enabled. `qcache developer driver registration` / `qcache volume list` report the registration. |
| Several volumes on one disk | Each has its own cache, budget, counters, worker and drainers; flushing or removing one never touches another. The shared RAM limit covers all of them. Verification leases are per physical disk. |
| Saved profiles | One per volume, named by the volume GUID and recording the volume size and the disk's PnP identity; a profile follows the volume if its letter changes. Restore refuses a different volume, disk or size. At startup a restore refused only because RAM is briefly short is retried every 15 s for 10 minutes; `qcache volume list` and the app show a profile that is waiting for RAM or was not restored, and why (#12). |
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
| Orderly disk eject | `qcache disk eject W: --preview` checks the whole disk and all its lettered partitions. `qcache disk eject W:` disables and drains every affected cache, flushes each file system, then asks Windows to eject the physical disk. A Windows veto reports its reason and attempts to restore each still-present cache's enabled state. The desktop offers the same disk-level action. The command refuses boot/system/paging, offline/read-only and ambiguous or unlettered layouts. Hardware must report removable/ejectable capability. The result distinguishes Windows accepting a request from observed device disappearance. |
| Detaching a virtual disk (`diskpart detach vdisk`, Disk Management, `Dismount-DiskImage`) | Windows dismounts the volume, then removes it without asking first (no query-remove). When the file system releases the volume (NTFS sends `IOCTL_DISK_MEDIA_REMOVAL`, allow removal, as it dismounts), the cache writes its pending data out before passing the request on; clean data stays cached. This is a deliberate drain: the volume is about to go and no later request would reach the disk. Counted as an ordered barrier (`volumes/volume-detach`, plan 172, #21). |
| Unexpected disconnect | Already acknowledged volatile Fast writes may be lost and the file system may be damaged. The driver marks the volume gone and frees its cache on final removal. A cached volume's removal is logged in the System log: `qcachelab` event 1 when writes were still pending (with the byte count, now lost), event 2 when none were; the entry's binary data lists the volume's last 16 requests other than reads and writes. No data is replayed on reconnect. |
| Uninstall | Drains and disables each lettered volume with a cache (volumes without the filter loaded are skipped), removes the Volume class entry, keeps the service and binary until the restart. `Recover-Registration.ps1` restores the backed-up Volume class list and service values (verified on copies of the SYSTEM hive). |

### Advantages and disadvantages compared with the disk filter

| Advantages | |
|---|---|
| Disk tools work normally | SMART/health monitors, SCSI/ATA pass-through and firmware polls no longer get "not supported", and no longer drain the cache or empty its read data. |
| Cache exactly what you choose | A cache belongs to one volume (for example a games volume), not to everything on the disk. Volumes on one disk can have different sizes and policies, and flushing or removing one does not stall the others. |
| Profiles follow the volume | Named by the volume GUID, so a drive-letter change does not lose or misapply a profile. |
| Automatic coverage | Volumes created later, attached VHDX files and removable fixed volumes get the filter without a restart. |
| Same speed | CrystalDiskMark rows match or beat the disk filter ([verification record](KNOWN_ISSUES.md#volume-filter-verification-2026-09-28)). |

| Disadvantages | What it means / mitigation |
|---|---|
| Raw disk I/O bypasses the cache | A program reading `\\.\PhysicalDriveN` directly misses data still pending in a Fast cache; one writing there leaves stale cached copies. Backup/imaging via shadow copies is consistent; otherwise flush or pause first, and never write raw to a disk under a cached volume. |
| More lifecycle to handle | Volumes are resized, taken offline, dismounted, snapshotted and created at runtime; the boot volume's stack (snapshots, BitLocker, volume manager) is sensitive: querying it at start-up reset the machine. Each case is now handled and has a check, but it is a larger surface than a disk. |
| Plaintext above BitLocker | The cache holds unencrypted data in RAM, like the Windows file cache; dump/hibernation files stay encrypted on a BitLocker C:. |
| RAM is per volume | Two cached volumes on one disk each need their own budget; there is no pool shared per disk, and each drains independently, so the disk sees two write-back streams. |
| One more stack layer everywhere | A filter instance on every volume (including EFI/recovery), passing through when no task exists; no measurable cost found. |
| Scope | Only lettered volumes on one disk can be cached (no mount-point-only or spanned/striped volumes). FAT32/exFAT carry the no-journal risk above. |
| No upgrade path from the disk filter | By decision there is no migration code: disk-filter profiles and registrations are not converted (re-create tasks after installing). |

### Volume filter lab switches

`DiagnosticMode` (service DWORD, read at driver load) exists for bisecting boot
problems and support only; it must be absent or 0 in normal use, and every
cache-changing verification suite refuses to run otherwise: 1 = pure pass-through,
2 = do not mirror `DO_POWER_PAGABLE`, 4 = skip paging classification at dispatch,
8 = never query the length or sector size. `ClassCoverage` 0 with
`LabAllowedDriverKey` restricts the filter to one volume's driver key (first-boot
bring-up); the installer always sets `ClassCoverage` 1.

### Volume filter limitations

- Surprise hot-unplug containment, external Windows-eject routing, and reconnect
  are not yet VM-qualified. Sleep/hibernate and offline recovery
  remain separate work.
- Raw reads and writes sent to the physical disk bypass the cache (above).
- Crash durability of Fast data is unchanged: pending data is lost on a crash.

## How requests are served (since 0.4.153.1-0.4.162.1)

Every cached volume has one ordered request worker, sixteen offloaded-read threads
and the drainers. The sixteen threads are created when the volume cache is
initialized, even while caching is disabled. Their bounded table has 64 entries;
each request can pin at most 256 4 KiB cache blocks. Request order is decided in
one place, while independent lower reads and eligible large resident copies can
run in parallel.

| Request | Where it runs |
|---|---|
| RAM read hit or fitting write, disk otherwise idle | On the application's own thread (the caller path): no queue hand-off, worker wake-up or cross-thread completion. |
| Several requests outstanding (queue depth > 1) | Queued to the worker, which overlaps with the submitting thread and polls 30 us before sleeping. After finding it busy, the next 256 candidates follow; every 1024th caller-path candidate is sent to the worker to detect a deep queue. |
| Read of 256 KiB or more fully in RAM; payload copy of a fitting write that size | Admitted in order by the worker, copied on an offloaded-request thread. Overlapping writes and reads wait for it; controls wait for all of them. |
| Paging read that needs the disk | Offloaded-request thread, as before. |
| Ordinary read that needs the disk, up to 256 cache blocks (since plan 125) | Found by the worker, then read and kept on an offloaded-read thread, allowing concurrent disk reads. Writes overlapping it wait for it. Before plan 125 the worker waited for each one, so misses reached the disk one at a time. Copy flag 8 (`qcache developer driver copy-flags`, default 15) switches this for comparisons; when the 64-entry offload table is full the worker reads it as before. An unaligned request of 1 MiB or less can span more than 256 blocks and uses the ordered worker. |
| Paging-file I/O | Forwarded from dispatch, as before. |
| Larger misses, capacity waits, Strict write-through (written directly after older versions of its blocks), controls, flushes | The ordered worker, as before. |

While the worker waits for the disk, it completes queued reads that are already in RAM and hands queued misses
to the offloaded-request threads. A read it cannot do either keeps its place in the queue (before plan 125 it went
back to the front, so newer reads could overtake it for as long as reads kept arriving).
Every read retains the identities of its pinned cache entries, including entries
that were absent. Concurrent fills cannot cause it to copy or release a version
it never pinned. Ordinary misses are retained from driver-owned staging buffers;
paging misses are never retained.

`qcache developer performance Q: --caller-path false` turns the caller path off
until the next boot (for comparisons); Diagnostics V14-V16 count caller-path
requests (`CallerPath`), declined attempts, and offloaded copies
(`CopyOffloadReads`, `CopyOffloadWrites`).

## Allocation and retention

| Setting | Behaviour |
|---|---|
| Automatic (default) | Read and write data share the payload pool. Writes evict retained clean writes first, protecting currently resident read data up to half the payload capacity. Unused read allowance is borrowable. An individual write larger than the remaining allowance reduces protection enough to admit that whole request. Dirty data is never evicted. This is demand-driven protection, not workload prediction. |
| Fixed | `--write-percent` reserves that proportion of payload for dirty and retained-write data; the remainder holds clean reads. 0 is read-only, 100 write-only. |
| Retain writes (default) | Successful background writes become clean cached blocks instead of immediately being discarded. |
| Promote on read (default) | Reading a retained clean write moves it into the read quota without copying its payload. Dirty writes remain in the write quota until drained. |
| Discard drained | Release successful writes immediately. Read misses can still populate the read quota. |
| Read misses | An unbuffered read that misses the cache (up to 16 MiB) is read into a driver-owned buffer, copied to the application, and kept from that copy as clean read blocks (plan 62), so another program's buffer never becomes cached data. Paging reads (Windows file-cache refills, mapped files, programs) are never kept since plan 60: a clustered page-in's buffer can repeat Windows' shared dummy page, which holds another block's data (see KNOWN_ISSUES). Plan 56 kept them and served that data on C:. Data already in the cache still serves paging reads. |
| Scan resistance and read recall (since 0.4.469.1) | A block read from disk once enters its clean list at the eviction end; a later hit moves it to the recent end. A one-off large read (a copy, a scan) therefore mostly evicts its own blocks instead of the data you use repeatedly. The cache also remembers recently evicted blocks and when each was last used (4 bytes per cached block). A block read again that was used more recently than the oldest used block still cached goes straight to the recent end, so a file that fits comes back into RAM on its second read instead of after dozens. A scan or loop larger than the cache never qualifies against data in use. `drop-clean` clears this history. Earlier drivers kept one new block in 16 at the recent end instead; `qcache developer driver read-recall <volume> 0` restores that for comparison ([design and measurements](history/SUSTAINED_CACHE_VALIDATION_20261009.md#read-recall-plan-104-044691)). |

**Clear read cache** (`qcache policy drop-clean Q:`, or the card button) releases clean read and retained-write blocks on demand. It is not a flush: pending writes, in-flight writes and draining are untouched, and it never discards data the disk has not accepted. New drivers advertise this control with state flag 1024; the button stays hidden otherwise.

Clean data is meant to stay resident for as long as the workload keeps it useful: a game's files can remain in RAM for hours of play. Clean blocks leave the cache only when (1) newly admitted reads or writes need the slot, LRU order first, (2) a write, TRIM or unknown media-changing control makes them stale, or (3) the cache is paused, removed, resized or reconfigured. There is no time-based expiry. Read-only controls are forwarded without a drain or invalidation. A control code whose access bits omit `FILE_WRITE_ACCESS` cannot modify stored data, so geometry/layout/attribute queries, media presence checks, SMART, `IOCTL_STORAGE_FIRMWARE_GET_INFO` and vendor queries no longer touch cached blocks; media-swap, bus/device reset, block reassignment and unoptimized `MANAGE_DATA_SET_ATTRIBUTES` (TRIM) requests keep the conservative drain-and-invalidate path.

This replaced a per-code allow list that treated every other control as possibly destructive. Measured on the lab VM (driver 0.4.12.1): 168 MiB of clean read data vanished within two seconds of an idle disk, with one extra `OtherBarriers` count and exactly the resident block count added to `Evictions`; `LastBarrierCode` was `0x2D1C00` = `IOCTL_STORAGE_FIRMWARE_GET_INFO`, which Windows' storage service polls (also triggered by every `Get-Disk`-based inventory refresh, so opening the QueueCache desktop emptied the cache it was displaying). With the same driver and no poller running, 64 MiB of clean read data stayed resident with zero evictions. The safe observation allowlist and retained-data policy cases have since passed on the current secondary-disk VM. Unknown media-changing requests remain conservative.

For example, an 8 GiB budget with a fixed 50% write share can retain a 3 GiB installation, even after it reaches disk. Subsequent reads use that data from RAM and can promote it to the read quota. Metadata and staging buffers count against the total budget, so payload is slightly smaller than 8 GiB. Another workload may evict clean data; retention is not pinning.

## Background draining versus durability

| Algorithm | Behaviour |
|---|---|
| Eager (default) | Each pending write starts draining to disk as soon as it is accepted. Smallest window of volatile data and the most disk traffic; repeated overwrites of the same block are still coalesced in RAM. Default since plan 164, with 2048 KiB batches and parallelism 8. |
| Balanced | Pending writes stay in RAM until dirty usage reaches the high watermark or the oldest dirty block reaches its maximum age; draining then continues down to the low watermark. Absorbs bursts and repeated overwrites, leaving more data in volatile RAM. |
| Idle | The Balanced triggers, plus draining whenever no new cached write has arrived for the configured write-idle interval. The baseline is 5,000 ms age, 250 ms idle and 40/80 watermarks. Idle with 1024 KiB batches and parallelism 4 was the default from plan 125 to plan 163 (256 KiB and 2 before; parallelism 1 before plan 56). |
| Deferred | Age-only background scheduling. It ignores idle and watermarks, but explicit flush, capacity and lifecycle boundaries still apply. Optional one-hour deferral is not the default. |

Draining applies to pending **writes** only. The watermark percentages measure dirty bytes against the write pool: the whole payload pool under Automatic allocation, or the fixed `--write-percent` share otherwise. Cached read data is not counted and is never drained; it is evicted when space is needed.

Each algorithm reads only some tuning settings; the desktop editor shows the relevant ones in its Background draining panel, each with hover help.

| Setting | Eager | Balanced | Idle | Deferred |
|---|---|---|---|---|
| `--low-percent` / `--high-percent` | not used | yes | yes | not used |
| `--max-dirty-age-ms` | not used | yes | yes | yes |
| `--idle-ms` | not used | not used | yes | not used |
| `--batch-kib` / `--drain-parallelism` | yes | yes | yes | yes |

All algorithms yield to explicit flushes, shutdown barriers and writers waiting for capacity. Maximum age is a scheduling trigger, not a promise that slow/failing storage will finish by a deadline. No mandatory 30-second delay is imposed.

Fast versus Strict is independent: Fast permits application flushes/write-through writes to complete in volatile RAM; Strict honours them. In Strict, a flush writes out every dirty block before it completes; a write-through write (NTFS writes its journal this way) goes straight to the disk after only the older versions of its own blocks. Strict also holds at most about two seconds of separate pieces of unwritten data, measured as pieces the drain completes per second (at least 256; 1,024 before the first measurement): a write that would add dirty data beyond that waits for the drain, so scattered small writes slow down to the disk's speed and a flush finishes in seconds. A contiguous run counts as one piece, so long sequential writes are not slowed; `policy diagnostics` shows the pieces, the limit and the waits as `StrictBacklog` (#16). `qcache policy flush`, Pause and Remove always drain. Abrupt failure can lose Fast-mode data and corrupt filesystems. The desired safety goal is loss of pending writes with a healthy filesystem, tracked in [#13](https://github.com/devedse/QueueCache/issues/13); that goal is not yet implemented and qualified, so the current warning still applies.

Adjacent 4 KiB blocks are gathered into configurable 4..2048 KiB lower writes
(default 2048 since plan 164; 1024 from plan 125, 256 before). `--drain-parallelism 1..8`
(default 8 since plan 164; 4 from plan 125, 2 from plan 56, 1 before) bounds concurrent
writes; each drain thread waits for its write before gathering the next. Overlapping
versions remain ordered. Small budgets below 16 MiB cap each staging buffer at 64 KiB.
Saved profiles keep their configured settings.

While a foreground read is waiting on the disk, or one finished in the last 10 ms,
drainers start a new write only while fewer than two are outstanding. A concurrent
disk reader then keeps the responsiveness it had under the earlier 1024 KiB × 4
default, and draining runs at full concurrency again once reads stop. Explicit flushes,
shutdown and paging-write fences are never held back. TRIMs drop cached data for their
ranges at once and reach the disk from a background thread; newer writes to those
ranges are not drained before that TRIM. Both are driver defaults since plan 164; the
`lab-read-aware` and `lab-trim-mode` switches still change them for A/B tests
([findings](PERFORMANCE.md#prio-162)). The complete plan-132 comparison
(three alternated repetitions) reduced median drain time during cold reads from
6.52 to 3.86 seconds at 1024 KiB × 4 versus 256 KiB × 2. A separate write-only
repeat reduced median flush time from 127.61 to 92.16 seconds, with every pair
improving and separated ranges. Fixed-window write IOPS varied with the interval
before the administrative flush began; this is not a general write-IOPS gain.
See [findings](PERFORMANCE.md)
for the exact workloads and qualification limits.

## CLI examples

```powershell
qcache policy apply Q: --budget-mib 4096 --accept-volatile-flush --save
qcache policy apply Q: --budget-mib 8192 --allocation Fixed --write-percent 50 --save
qcache policy apply Q: --budget-mib 4096 --drain Balanced --low-percent 40 --high-percent 80 --max-dirty-age-ms 5000 --batch-kib 1024 --drain-parallelism 2 --save
qcache policy apply Q: --budget-mib 2048 --allocation Fixed --write-percent 0 --preset Strict
qcache policy apply Q: --budget-mib 4096 --accept-volatile-flush --write-ordering --save   # experimental (#13)
qcache policy status PhysicalDrive1 --json
qcache developer cache-scenarios Q:
```

Use `--discard-drained` or `--no-promotion` to disable those retention behaviours. These settings are also in the desktop cache editor. CLI and desktop call the same management library, not each other.

`developer cache-scenarios` temporarily applies several 64 MiB configurations, writes new files, checks RAM hits and compares bytes with caching disabled, then restores runtime settings. It leaves files for inspection and does not modify saved profiles. It is not an exhaustive correctness or lifecycle certification.

## Status output

`qcache policy status` (and `cache-status`) prints three labelled lines: the
state line (status, mode, reserved RAM, any error), **Now** (dirty data, data
being written, cached reads and writes) and **Since Windows started** (read hit
rate and bytes served from RAM, writes merged in RAM, waits for cache space,
errors). The driver's totals count from when it loaded, which is at boot; they
are not reset when settings change. `qcache watch` prints the same on one line;
`--json` gives every field. The desktop card uses the same "In RAM now" and
"Since Windows started" split and greys out when its data is stale.

## Memory and confirmed state

The shared driver limit is 75% of physical RAM, capped at 128 GiB. Before increasing a budget, management preserves the greater of 2 GiB or 25% of physical RAM from currently available memory for Windows/applications. Include the budgets of all cached volumes in experiment planning; volumes on one disk each have their own budget. Availability is an estimate; kernel allocation can still fail. A failed resize leaves the cache disabled and reports failure, rather than pretending to restore the previous allocation. Cache payload is preallocated physical pages (not kernel nonpaged pool); only bookkeeping uses nonpaged pool. It does not shrink automatically under later Windows memory pressure.

The driver advertises a versioned state/policy contract while retaining its old ABI. A new-driver **Active** state requires enabled routing, allocated payload, and no fault, suspension, removal or barrier. Settings are read back and compared after Apply. Instance/revision identify the live cache, not a saved profile; unavailable samples must not be displayed as live. A status sample confirms driver state at that instant, not filesystem correctness, physical durability, or a throughput guarantee.

Mixed-hit reads currently use an original lower read plus cached-block overlays; fully cached reads avoid disk. Unsupported media-changing controls conservatively invalidate clean data after draining. Paging/hibernation/dump paths are counted and ordered. Plan 27 uses the system-capable policy for normal Enable, public Apply and saved-profile startup. After the exact 0.4.83.1 pagefile failure, paging-file data bypasses RAM caching (application paging data is admitted since T085); a new registration takes one persistence/invalidation boundary without disabling routing. Shutdown and device-power down drain and disable; requests after them keep flowing to the disk, and successful D0 resumes prior active state. Exact installed lifecycle qualification is still tracked separately. SSD/L2 caching is not planned.

## Dashboard

Review caveat: the usage-path notification/Enable race found in A07/T068 is repaired
by one routing-lock protocol. Exact 0.4.82.1 active Fast passed; plan 27 removes the
management rejection and uses normal activation. Exact installed 0.4.87.1 passed
Fast/Strict 349 MiB byte checks and a saved Fast reboot with a fixed 4 GiB C:
pagefile plus dump registration. Dynamic registration while already active,
sleep/hibernate/Fast Startup, low-memory/fault/cancellation and broader lifecycle
proof remain open.

Validation status: maintained policy runs have verified retained hot data across an unrelated small-file write and disk discovery. CI-built plan-37 policies passed against loaded 0.4.92.1. Plan-14 trigger/capacity checks passed on 0.4.64.1. These remain scoped results; T083/T084 require stronger ordering and owned-request admission attribution.

The desktop shows one card per lettered volume, grouped under its disk. An unformatted
or non-NTFS volume shows why it cannot get a cache. Each card shows **Readable from RAM** (clean read-fill plus retained drained writes, both served without touching the disk), pending writes, and incoming/drain rates, with read cache, retained writes, read hits and evicted-block count in the residency line.

The occupancy bar shows resident RAM: blue for clean read data, teal for retained clean writes, purple for pending writes, pale for the unused budget. Both the bar and the throughput chart carry a colour key drawn from the same palette constants. Draining changes pending writes into retained writes when retention is enabled; it does not empty the read cache. The history chart separately shows read, incoming-write and drain throughput.

One header selector sets the live update interval (0.5/1/2/5/10 s) for every value on every card: metrics, residency line and the history chart, which keeps one point per sample, so its window is 60 x the interval. Staleness marking and inventory rediscovery scale with the same interval.

Each volume has one independent in-flight telemetry request. Slow inventory discovery or another volume cannot hold up healthy volumes. Samples older than three seconds are shown as unavailable, never as a live Active state.

Uncached/partial writes invalidate only intersecting clean blocks. Known geometry and descriptor queries preserve cached payload; unknown media-changing operations still drain and invalidate conservatively. Descriptor query semantics follow [Microsoft's storage-property contract](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-storage_property_query).

## Experimental Fast write ordering (#13)

Write ordering is an advanced per-cache setting, off by default: **Write to disk in
save order (experimental)** under Advanced tuning in the app, or `--write-ordering`
on `qcache policy apply`. The question-mark button beside the checkbox explains
the on/off behavior, RAM-only saves, crash-safety limits, performance cost and
restrictions. Hover over it, focus it with the keyboard or press it to read the
explanation, including when the checkbox is unavailable. It is experimental: the goal is
that a crash or power cut can lose recent writes but should not damage NTFS, and that
is not yet qualified (see [known issues](KNOWN_ISSUES.md)). It applies to Fast only;
Strict already waits for the disk, so Strict with ordering is refused. Apply refuses
it, before draining anything, on a volume that holds a paging, hibernation or crash
dump file. That always includes the Windows volume: on the test VM, Windows' Session
Manager (`smss.exe`) registered C: for paging at every boot even with the page file
and crash dumps turned off (two registrations on each of two boots, October 26). Like the preset, it changes only while the
cache is disabled and empty, which Apply arranges; a brief busy answer while a lower
TRIM or group flush finishes is retried for up to 5 s. A saved profile keeps the
choice and the startup restore reapplies it; pause/resume keeps it, and removing the
cache turns it off. A configuration
that does not name a choice (an older saved profile, or one rebuilt from driver
state) leaves the current mode unchanged. The low-level
`qcache developer driver flush-order <device> <0|1>` hook remains for the lab, and
the `flush-order` verification suites set and restore it themselves.

With ordering on (1), a filesystem flush seals a group of RAM writes without waiting
for disk. A write-through write joins the open group and seals it after itself;
unrelated earlier writes are not ordered before it. This follows the per-request
[Windows write-through contract](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdm/ns-wdm-_io_stack_location):
an unrelated ordinary write need not become durable before the write-through
request. A successful group flush still makes the write-through and its group
durable before any later group can submit. Overwriting a sealed dirty block retains
the older payload as a separate version. Drainers may write only the oldest group;
a real lower flush must succeed before any later group is submitted. Because
versions join the dirty list in group order, drain selection stops at the first
version of a later group, and drainers wake only for work they may start: a write
into the oldest group, the seal of that group with none of its writes in flight,
or the lower flush that moves on to the next group. TRIM is also an ordered effect:
sealed dirty history drains before the lower discard, and newer writes cannot drain
ahead of it. Empty boundaries coalesce. A failed ordering flush faults the cache and
retains its boundary for explicit Retry.

Plan 196 removed two earlier variants. The original mode 1 also sealed before every
write-through. Mode 2 had today's groups but woke every drainer for each write and
seal, and each searched the whole dirty list under the cache lock; that made
applications 2.6–40× slower ([plan 195](PERFORMANCE.md#fast-ordering-13-fix)).
Value 1 now selects the former lab mode 3.

Supported fitting writes still enter RAM without lower I/O for admission. Retained
versions consume the existing budget and can cause capacity backpressure. An
unsupported/unmappable direct write, an oversized write, or a synchronous TRIM
fallback reconciles earlier history and flushes its lower effect before returning.
These conservative fallbacks can wait for unrelated dirty data. Strict and explicit
QueueCache Flush/Pause/Remove retain their durable boundary. This does not make Fast
application flushes durable or promise that recently acknowledged data survives a
crash.

Each group needs its own lower flush, and the next group waits for it, so every
application flush or write-through costs one disk round trip before later data can
reach the disk. With ordering on, 25,600 flushed 4 KiB files took about 7× and
25,600 write-through pairs about 60× as long to reach the disk as with it off,
while their time into the cache matched ordering off
([plan 195](PERFORMANCE.md#fast-ordering-13-fix)).

Diagnostics V26 appends the mode, current/draining group, seal and successful/failed
ordering-flush counts, retained-version and TRIM-wait counts, and the highest group
selected for a drain in this allocation. A lower group flush must be supported and
honoured by the complete storage stack. See [Microsoft's write-cache contract](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/querying-for-the-write-cache-property).
The lower-I/O path continues to use ordinary writes and explicit group flushes.
A read-only October 25 capability query reported write-through support on the VM's
Q: device but **not** on its W: VHDX lab device; both reported flush support.
Their shared SCSI interface therefore does not justify assuming FUA support.
No FUA optimization is enabled or qualified by that capability report.
Targeted live-driver checks passed under Driver Verifier, and bounded crash cuts
with the earlier variants kept NTFS clean and durable guards exact. The current
mode keeps mode 2's groups and changes only drain scheduling, but it has not been
cut itself; broader crash/storage/system-volume qualification remains open
([records and limits](KNOWN_ISSUES.md#correctness-qualification)). The plan-192
release campaign covers the default path with ordering off.

The lab switch rejects currently registered paging/hibernation/dump paths. Mode selection,
cache activation and usage reservation share the routing lock; a new registration
is refused while experimental ordering is selected, including after the cache is
disabled. This closes the late-registration race without waiting for I/O in the
dispatch path. Resetting the mode to zero remains possible at a clean disabled
boundary. System volumes and dynamic special-file growth are not qualified.
Registration already establishes a lower barrier, but later bypassed writes and allocation reuse need their own audit.
FAT/exFAT and arbitrary media-changing controls are not qualified.
