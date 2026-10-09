# RAM cache policies

Fast mode is the optimized, recommended mode; Strict mode is supported for
correctness but is not tuned for speed (owner decision, 2026-09-27).

Current implementation boundary (2026-09-25): RAM admission applies to eligible
writes, including ordinary application file-cache write-back and memory-mapped
writes (paging-marked requests whose originating file is not a paging file).
Paging-file and unknown-origin requests take coherent ordered lower I/O. Paging
read misses are not newly retained; resident sectors still serve coherent reads.
See the [T085 design](T085_APPLICATION_CACHING_DESIGN.md).

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
Background: [volume filtering](VOLUME_FILTER.md).

QueueCache uses one block index: dirty writes, retained clean writes and clean reads never need separate copies of the same current block. An older in-flight write can temporarily coexist with its newer replacement. Reads select the newest version; the older version must finish before its replacement can be written to disk.

## How requests are served (since 0.4.153.1-0.4.162.1)

Every cached volume has one ordered request worker, a pool of three offloaded-request
threads and the drainers. The order of requests that could affect each other is
decided in one place, while copies of independent requests run in parallel.

| Request | Where it runs |
|---|---|
| RAM read hit or fitting write, disk otherwise idle | On the application's own thread (the caller path): no queue hand-off, worker wake-up or cross-thread completion. |
| Several requests outstanding (queue depth > 1) | Queued to the worker, which overlaps with the submitting thread and polls 30 us before sleeping. After finding it busy, the next 256 candidates follow; every 1024th caller-path candidate is sent to the worker to detect a deep queue. |
| Read of 256 KiB or more fully in RAM; payload copy of a fitting write that size | Admitted in order by the worker, copied on an offloaded-request thread. Overlapping writes and reads wait for it; controls wait for all of them. |
| Paging read that needs the disk | Offloaded-request thread, as before. |
| Paging-file I/O | Forwarded from dispatch, as before. |
| Misses, capacity waits, Strict write-through, controls, flushes | The ordered worker, as before. |

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
| Scan resistance and read recall (since 0.4.469.1) | A block read from disk once enters its clean list at the eviction end; a later hit moves it to the recent end. A one-off large read (a copy, a scan) therefore mostly evicts its own blocks instead of the data you use repeatedly. The cache also remembers recently evicted blocks and when each was last used (4 bytes per cached block). A block read again that was used more recently than the oldest used block still cached goes straight to the recent end, so a file that fits comes back into RAM on its second read instead of after dozens. A scan or loop larger than the cache never qualifies against data in use. `drop-clean` clears this history. Earlier drivers kept one new block in 16 at the recent end instead; `qcache developer driver read-recall <volume> 0` restores that for comparison ([design and measurements](SUSTAINED_CACHE_VALIDATION_20261009.md#read-recall-plan-104-044691)). |

**Clear read cache** (`qcache policy drop-clean Q:`, or the card button) releases clean read and retained-write blocks on demand. It is not a flush: pending writes, in-flight writes and draining are untouched, and it never discards data the disk has not accepted. New drivers advertise this control with state flag 1024; the button stays hidden otherwise.

Clean data is meant to stay resident for as long as the workload keeps it useful: a game's files can remain in RAM for hours of play. Clean blocks leave the cache only when (1) newly admitted reads or writes need the slot, LRU order first, (2) a write, TRIM or unknown media-changing control makes them stale, or (3) the cache is paused, removed, resized or reconfigured. There is no time-based expiry. Read-only controls are forwarded without a drain or invalidation. A control code whose access bits omit `FILE_WRITE_ACCESS` cannot modify stored data, so geometry/layout/attribute queries, media presence checks, SMART, `IOCTL_STORAGE_FIRMWARE_GET_INFO` and vendor queries no longer touch cached blocks; media-swap, bus/device reset, block reassignment and unoptimized `MANAGE_DATA_SET_ATTRIBUTES` (TRIM) requests keep the conservative drain-and-invalidate path.

This replaced a per-code allow list that treated every other control as possibly destructive. Measured on the lab VM (driver 0.4.12.1): 168 MiB of clean read data vanished within two seconds of an idle disk, with one extra `OtherBarriers` count and exactly the resident block count added to `Evictions`; `LastBarrierCode` was `0x2D1C00` = `IOCTL_STORAGE_FIRMWARE_GET_INFO`, which Windows' storage service polls (also triggered by every `Get-Disk`-based inventory refresh, so opening the QueueCache desktop emptied the cache it was displaying). With the same driver and no poller running, 64 MiB of clean read data stayed resident with zero evictions. The safe observation allowlist and retained-data policy cases have since passed on the current secondary-disk VM. Unknown media-changing requests remain conservative.

For example, an 8 GiB budget with a fixed 50% write share can retain a 3 GiB installation, even after it reaches disk. Subsequent reads use that data from RAM and can promote it to the read quota. Metadata and staging buffers count against the total budget, so payload is slightly smaller than 8 GiB. Another workload may evict clean data; retention is not pinning.

## Background draining versus durability

| Algorithm | Behaviour |
|---|---|
| Eager | Each pending write starts draining to disk as soon as it is accepted. Smallest window of volatile data and the most disk traffic; repeated overwrites of the same block are still coalesced in RAM. |
| Balanced | Pending writes stay in RAM until dirty usage reaches the high watermark or the oldest dirty block reaches its maximum age; draining then continues down to the low watermark. Absorbs bursts and repeated overwrites, leaving more data in volatile RAM. |
| Idle (default) | The Balanced triggers, plus draining whenever no new cached write has arrived for the configured write-idle interval. The alpha baseline is 5,000 ms age, 250 ms idle, 40/80 watermarks, 256 KiB batches and parallelism 2 (1 before plan 56). |
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

Fast versus Strict is independent: Fast permits application flushes/write-through writes to complete in volatile RAM; Strict honours those barriers. `qcache policy flush`, Pause and Remove always drain. Abrupt failure can lose Fast-mode data and corrupt filesystems.

Adjacent 4 KiB blocks are gathered into configurable 4..1024 KiB lower writes. `--drain-parallelism 1..4` (default 2 since plan 56; 1 before) bounds concurrent writes; overlapping versions remain ordered. Small budgets below 16 MiB cap each staging buffer at 64 KiB. The default remains one 256 KiB gather stream.

## CLI examples

```powershell
qcache policy apply Q: --budget-mib 4096 --accept-volatile-flush --save
qcache policy apply Q: --budget-mib 8192 --allocation Fixed --write-percent 50 --save
qcache policy apply Q: --budget-mib 4096 --drain Balanced --low-percent 40 --high-percent 80 --max-dirty-age-ms 5000 --batch-kib 1024 --drain-parallelism 2 --save
qcache policy apply Q: --budget-mib 2048 --allocation Fixed --write-percent 0 --preset Strict
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
