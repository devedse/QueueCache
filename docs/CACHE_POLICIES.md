# RAM cache policies

QueueCache uses one block index: dirty writes, retained clean writes and clean reads never need separate copies of the same current block. An older in-flight write can temporarily coexist with its newer replacement. Reads select the newest version; the older version must finish before its replacement can be written to disk.

## Allocation and retention

| Setting | Behaviour |
|---|---|
| Automatic (default) | Read and write data share the payload pool. The least recently used clean block yields space; dirty data is never evicted. This is demand-driven sharing, not workload prediction. |
| Fixed | `--write-percent` reserves that proportion of payload for dirty and retained-write data; the remainder holds clean reads. 0 is read-only, 100 write-only. |
| Retain writes (default) | Successful background writes become clean cached blocks instead of immediately being discarded. |
| Promote on read (default) | Reading a retained clean write moves it into the read quota without copying its payload. Dirty writes remain in the write quota until drained. |
| Discard drained | Release successful writes immediately. Read misses can still populate the read quota. |

Clean data is meant to stay resident for as long as the workload keeps it useful: a game's files can remain in RAM for hours of play. Clean blocks leave the cache only when (1) newly admitted reads or writes need the slot, LRU order first, (2) a write, TRIM or unknown media-changing control makes them stale, or (3) the cache is paused, removed, resized or reconfigured. There is no time-based expiry. Read-only controls are forwarded without a drain or invalidation. A control code whose access bits omit `FILE_WRITE_ACCESS` cannot modify stored data, so geometry/layout/attribute queries, media presence checks, SMART, `IOCTL_STORAGE_FIRMWARE_GET_INFO` and vendor queries no longer touch cached blocks; media-swap, bus/device reset, block reassignment and unoptimized `MANAGE_DATA_SET_ATTRIBUTES` (TRIM) requests keep the conservative drain-and-invalidate path.

This replaced a per-code allow list that treated every other control as possibly destructive. Measured on the lab VM (driver 0.4.12.1): 168 MiB of clean read data vanished within two seconds of an idle disk, with one extra `OtherBarriers` count and exactly the resident block count added to `Evictions`; `LastBarrierCode` was `0x2D1C00` = `IOCTL_STORAGE_FIRMWARE_GET_INFO`, which Windows' storage service polls (also triggered by every `Get-Disk`-based inventory refresh, so opening the QueueCache desktop emptied the cache it was displaying). With the same driver and no poller running, 64 MiB of clean read data stayed resident with zero evictions. The fix itself is a source change awaiting live-driver verification.

For example, an 8 GiB budget with a fixed 50% write share can retain a 3 GiB installation, even after it reaches disk. Subsequent reads use that data from RAM and can promote it to the read quota. Metadata and staging buffers count against the total budget, so payload is slightly smaller than 8 GiB. Another workload may evict clean data; retention is not pinning.

## Background draining versus durability

| Algorithm | Behaviour |
|---|---|
| Eager (default) | Each pending write starts draining to disk as soon as it is accepted. Smallest window of volatile data and the most disk traffic; repeated overwrites of the same block are still coalesced in RAM. |
| Balanced | Pending writes stay in RAM until dirty usage reaches the high watermark or the oldest dirty block reaches its maximum age; draining then continues down to the low watermark. Absorbs bursts and repeated overwrites, leaving more data in volatile RAM. |
| Idle | The Balanced triggers, plus draining whenever no new cached write has arrived for the configured write-idle interval. Keeps the disk quiet during a burst and catches up between bursts. |

Draining applies to pending **writes** only. The watermark percentages measure dirty bytes against the write pool: the whole payload pool under Automatic allocation, or the fixed `--write-percent` share otherwise. Cached read data is not counted and is never drained; it is evicted when space is needed.

Each algorithm reads only some tuning settings; the desktop editor shows the relevant ones in its Background draining panel, each with hover help.

| Setting | Eager | Balanced | Idle |
|---|---|---|---|
| `--low-percent` / `--high-percent` | not used | yes | yes |
| `--max-dirty-age-ms` | not used | yes | yes |
| `--idle-ms` | not used | not used | yes |
| `--batch-kib` / `--drain-parallelism` | yes | yes | yes |

All algorithms yield to explicit flushes, shutdown barriers and writers waiting for capacity. Maximum age is a scheduling trigger, not a promise that slow/failing storage will finish by a deadline. No mandatory 30-second delay is imposed.

Fast versus Strict is independent: Fast permits application flushes/write-through writes to complete in volatile RAM; Strict honours those barriers. `qcache policy flush`, Pause and Remove always drain. Abrupt failure can lose Fast-mode data and corrupt filesystems.

Adjacent 4 KiB blocks are gathered into configurable 4..1024 KiB lower writes. `--drain-parallelism 1..4` bounds concurrent writes; overlapping versions remain ordered. Small budgets below 16 MiB cap each staging buffer at 64 KiB. The default remains one 256 KiB gather stream.

## CLI examples

```powershell
qcache policy apply Q: --budget-mib 4096 --save
qcache policy apply Q: --budget-mib 8192 --allocation Fixed --write-percent 50 --save
qcache policy apply Q: --budget-mib 4096 --drain Balanced --low-percent 40 --high-percent 80 --max-dirty-age-ms 5000 --batch-kib 1024 --drain-parallelism 2 --save
qcache policy apply Q: --budget-mib 2048 --allocation Fixed --write-percent 0 --preset Strict
qcache policy status PhysicalDrive1 --json
qcache developer cache-scenarios Q:
```

Use `--discard-drained` or `--no-promotion` to disable those retention behaviours. These settings are also in the desktop cache editor. CLI and desktop call the same management library, not each other.

`developer cache-scenarios` temporarily applies several 64 MiB configurations, writes new files, checks RAM hits and compares bytes with caching disabled, then restores runtime settings. It leaves files for inspection and does not modify saved profiles. It is not an exhaustive correctness or lifecycle certification.

## Memory and confirmed state

The old shared 4 GiB ceiling is replaced by a shared limit of 75% of physical RAM, capped at 128 GiB. Management also checks currently available physical memory before increasing a budget, leaving 1 GiB headroom. Availability is an estimate; kernel allocation can still fail. A failed resize leaves the cache disabled and reports failure, rather than pretending to restore the previous allocation. Memory is preallocated nonpaged RAM; it does not shrink automatically under later Windows memory pressure.

The driver advertises a versioned state/policy contract while retaining its old ABI. A new-driver **Active** state requires enabled routing, allocated payload, and no fault, suspension, removal or barrier. Settings are read back and compared after Apply. Instance/revision identify the live cache, not a saved profile; unavailable samples must not be displayed as live. A status sample confirms driver state at that instant, not filesystem correctness, physical durability, or a throughput guarantee.

Mixed-hit reads currently use an original lower read plus cached-block overlays; fully cached reads avoid disk. Unsupported media-changing controls conservatively invalidate clean data after draining. Paging/hibernation/dump and early boot activation still need dedicated engineering and qualification. SSD/L2 caching is not planned.

## Dashboard

Validation status: the file-only retention regression still observed eviction after an unrelated small-file write and disk discovery. Additional read-only-control handling and diagnostic detail are included, but have not yet passed that scenario on a live driver. Treat this as an open retention issue, not a verified fix.

Each card shows **Readable from RAM** (clean read-fill plus retained drained writes, both served without touching the disk), pending writes, and incoming/drain rates, with read cache, retained writes, read hits and evicted-block count in the residency line.

The occupancy bar shows resident RAM: blue for clean read data, teal for retained clean writes, purple for pending writes, pale for the unused budget. Both the bar and the throughput chart carry a colour key drawn from the same palette constants. Draining changes pending writes into retained writes when retention is enabled; it does not empty the read cache. The history chart separately shows read, incoming-write and drain throughput.

Each disk has one independent in-flight telemetry request. Slow inventory discovery or another disk cannot hold up healthy disks. Samples older than three seconds are shown as unavailable, never as a live Active state.

Uncached/partial writes invalidate only intersecting clean blocks. Known geometry and descriptor queries preserve cached payload; unknown media-changing operations still drain and invalidate conservatively. Descriptor query semantics follow [Microsoft's storage-property contract](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-storage_property_query).
