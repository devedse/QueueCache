# Drive disconnect delivery summary

Checkpoint: 2026-09-30, `volume-filter`, verification plan 73.
This summarizes the continued implementation and its prior session checkpoints.
The feature is implemented in stages and is **not fully VM qualified**.

## Product and driver changes

- Volume QUERY_REMOVE disables cache admission, drains accepted writes and issues
  the lower durability boundary. A lower veto restores the previous Enabled state
  only on a healthy present device. Duplicate queries retain the first setting;
  only successful CANCEL_REMOVE restores it. Genuine faults are not cleared.
- Surprise and direct final removal publish Gone under the same cache mutex used
  for RAM admission. Changed/drainer/paging waiters are awakened. Later data and
  control requests fail before inactive/observation/paging bypasses; required
  PnP, power, close and cleanup requests still pass. Previously admitted/pinned
  operations retain their owners through completion.
- Final teardown retains remove-lock, lower-IRP, buffer and callback ownership,
  joins workers, and frees allocations after ownership settles. It cannot safely
  force completion of a broken lower stack that never returns an IRP.
- Surprise removal emits a one-shot System event carrying a synchronized pending
  byte snapshot. This is potential volatile loss, not an exact lost-byte claim.
- Shared physical-disk eject drains/disables and filesystem-flushes supported
  lettered data volumes, closes its handles, requests native Windows eject and
  separately verifies disk disappearance. Saved profiles survive.
- Preview/eject refuses protected and ambiguous targets. A dedicated immediate
  storage adapter may be selected only when its sole child is the selected disk.
  Every descendant/removal relation is checked; related volume devnodes require
  normal/hidden native interfaces with single-disk extents wholly on that disk.
  No opaque instance-name parsing or broad controller traversal establishes scope.
- Confirmed previews bind PnP identity, physical size, volume GUIDs and removal
  membership. Native veto results retain Configuration Manager/veto/presence
  codes and rollback errors. Unknown outcomes remain unresolved.
- CLI: `qcache disk eject <volume> --preview` and `qcache disk eject <volume>`.
  Cache-task removal remains a separate operation.
- Desktop: disk-level eject, saved disconnected profiles without invented live
  counters, identity-aware inventory refresh and rejection of retired samples.
  Settings, pause/resume, removal, flush and clean-block release bind the selected
  GUID, PnP identity, disk number and physical/volume sizes before mutation.
- Cooperating management clients serialize per physical disk. A slow operation on
  one disk does not take a global configuration gate for other disks.

## Maintained verification

The opt-in `disk-removal` suite remains excluded from `full`. It requires an exact
explicit disposable disk identity/size and an unconfigured volume. It prepares an
8 MiB Fast/Deferred oracle, records pending bytes off-disk, requests Windows eject,
then waits for an exact run/case/disk/GUID acknowledgement after live reconnect.
Verification requires a fresh empty cache instance and the exact file hash.

Plan 73 additionally records before/disabled cache state, lower write/flush attempt
counters and filesystem-flush completion. Missing, unchanged or regressed counters,
wrong identities, unresolved pending I/O and absent evidence cannot produce PASS.
These are volume lifetime counter differences, not benchmark score windows.
A verified reconnect permits restoration even when preparation evidence is missing;
qualification still fails. Present-disk vetoes restore normally; unknown outcomes
and unverified reconnects defer restoration. Timeout reporting names the missing
reconnect phase. All orchestration stays in the supported foreground runner.

## Verification actually completed

| Evidence | Result and limits |
|---|---|
| Linux management protocol tests | PASS; no native driver execution. |
| Windows management/runner contracts | PASS, including veto, unknown outcome, cancellation, stale acknowledgement, missing preparation and selection identity rejection; no driver workloads. |
| Desktop headless fixtures | PASS, including disconnected profiles, same GUID/new disk number and retired samples; no real UI/native storage transactions. |
| Native CI Debug/Release through `1752b13` | PASS; VM build 0.4.249.1 loaded under Verifier 0x209bb, matching hashed module, load 1/unload 0. |
| Cached NTFS quick run on 0.4.243.1 | COMPLETED 1/1; RAM writes, read/overwrite, drain/reopen and copy/settings checks; declared TRIM/exclusion SKIPs. Clean restoration. |
| SATA removal preflight | INCOMPLETE: hardware does not expose Windows eject capability; no removal. |
| VirtIO disk-node request on 0.4.247.1 | Real native veto after pending writes. Original cache resumed; supported verify-recover restored the unconfigured state. No removal qualified. |
| Dedicated VirtIO adapter eject on 0.4.249.1 | Windows accepted removal and original disk absence was observed after 8,396,800 pending bytes with zero bytes drained beforehand. Live reconnect failed in Proxmox/QEMU handling. |

The final VM run is
`QueueCache-Verify-20260930-152732-c06e5ce0a2de454d88733777e86e8be7`.
It finished **RESTORATION_FAILED** at the 15-minute reconnect deadline. FINISHED,
status, summary, results, log and all 34 raw files were read and preserved. No
reconnect hash or fresh-instance result exists. Preparation snapshot refinement
in plan 73 has host contract coverage but has not run on the detached disk.

The host deletion task succeeded, but reattachment failed on an existing
`throttle-drive-virtio1` object. Inspection found no matching guest device/block
backend; attempted UI cleanup reported that the object was still in use. The
owner reported the Proxmox/Ceph offline-node disk-removal issue, detached the disk,
and requested it stay detached. No further attach, reset, reboot or recovery is
part of this checkpoint. Backing data and failure evidence were preserved.

## Earlier filesystem work in this conversation

Existing commits `f009b3f`, `27cfcf9` and `181afe1` removed the mounted-NTFS-only
policy, added FAT32/exFAT journal warnings and maintained filesystem-aware lab
scenarios. The tracker records FAT32 and ReFS Dev Drive suites on 0.4.229.1 under
Verifier, with declared Windows-feature SKIPs. Fixes included FAT snapshot SKIP,
two ReFS TRIM ranges and ReFS remount after injected write errors. ReFS caller-path
performance remains open. This earlier evidence does not qualify filesystem
removal. The proposed product virtual/cache-disk feature remains postponed.

## Remaining review and qualification

- Establish external Windows disk-to-volume query routing; add explicit driver
  prepare/cancel capability if that routing cannot keep all volumes quiesced.
- Add complete versioned lifecycle state/query/veto/cancel/removal diagnostics.
- Qualify live reconnect with plan-73 lower durability evidence; two cached volumes;
  FAT32/ReFS removal; Strict/durable baselines; changed letters/disk identities.
- Exercise idle and dirty surprise removal, queued/capacity waits, active lower
  drains/reads/offloaded copies, faults and direct/duplicate/failed-cancel ordering
  under Verifier. Complete ten orderly and ten surprise cycles.
- Review every lower callback ownership path independently. The source audit and
  clean builds do not prove every race or all filesystem outcomes.

For exact implementation/verification checkpoints use
RAM_FIRST_IMPLEMENTATION_TRACKER.md. For ownership use
DRIVE_DISCONNECT_OWNERSHIP_AUDIT.md. The original acceptance contract remains in
DRIVE_DISCONNECT_IMPLEMENTATION_PLAN.md; these gaps must not be marked complete.

## Follow-up: native Windows eject and default CDM

The subsequent user-requested plan-74 native Windows eject case accepted removal
with enabled cache and 8,437,760 pending bytes, without product preparation.
Proxmox again blocked live reconnect; this remains RESTORATION_FAILED with no
data-survival verdict. The backing disk is preserved and detached. CrystalDiskMark
Default completed all eight scores with five 1 GiB runs and the existing Q: cache.
Exact evidence, implementation/VM gaps and full benchmark numbers are recorded in
[WINDOWS_EJECT_AND_CDM_20260930.md](WINDOWS_EJECT_AND_CDM_20260930.md).
