# Managed disks: UI plan and scenario matrix

Status: reviewed proposal, revision 6, 2026-10-03. Creation, managed cards and
product actions are implemented on `feature/managed-disks` through the shared
broker API; activation requires the matching installed service/provider.
None of the new disk modes is VM-qualified. The ordinary cache using the shared
allocator/accounting helpers passed
[focused VM checks](MANAGED_DISKS_FOUNDATION_VERIFICATION_20261003.md).
Handoff audience: Luna or Sol.
The owner requested three modes and automatic fresh formatting of an unbacked
RAM disk on every new boot. “Performative it” is interpreted as “format it.”

Read [RAM_DISK_IMPLEMENTATION_PLAN.md](RAM_DISK_IMPLEMENTATION_PLAN.md) for the
backend contracts, work packages, failure recovery and acceptance tests. The
[execution tracker](RAM_FIRST_IMPLEMENTATION_TRACKER.md#managed-disks-planning-2026-10-03)
records implementation and verification separately. The owner's follow-up includes
product CLI commands in the current delivery; UI and CLI share the same operations.

## Review findings and settled design

The original proposal covered two modes and deferred complete image loading.
This revision includes complete image loading in the planned deliverable. It also
resolves five gaps: disk capacity versus actual RAM cost, fresh-boot versus
same-session formatting, image-save consistency, shared memory accounting across
native drivers, and the difference between detach, discard and deleting a file.
Defaults below are design recommendations for implementation; they are not claims
that the owner selected every default individually.

## Three choices under Create disk

Existing **Enable cache on an existing volume** remains a separate action.

| UI choice | Storage while running | RAM allocation | Persistence | New Windows startup |
|---|---|---|---|---|
| **Pure RAM disk** | Entire virtual disk in RAM | Virtual disk capacity plus overhead, fully reserved | None | If enabled: create, partition and format a new empty disk |
| **VHDX-backed disk with RAM cache** | Windows-mounted VHDX plus QueueCache volume cache | Independent cache budget; smaller than disk is supported | Backing image receives writes under Strict/Fast policy | If enabled: reopen image and restore cache; never autoformat |
| **Load VHDX fully into RAM** | Entire logical disk copied into RAM before use | VHDX virtual capacity plus overhead, fully reserved | Explicit image checkpoints; runtime writes stay in RAM between saves | If enabled: load last committed image checkpoint; never autoformat |

For example, a dynamically allocated VHDX with **64 GiB virtual capacity** and a
**3 GiB file on the host** needs at least **64 GiB RAM plus overhead** in the third
mode. With the second mode it can use a 4 GiB RAM cache. The third mode copies the
logical disk, including partition/filesystem metadata, through Windows' VHDX
provider; it does not treat the VHDX container's file bytes as raw disk sectors.

Equal cache budget and disk capacity in mode 2 does not guarantee full residency:
metadata consumes budget and cold data is loaded on demand. Mode 3 must finish
loading every logical sector before Ready and must not fall back to disk reads
or background writeback during normal operation. Its load/save time grows with
virtual capacity; show this before creation.

## Wizard and field rules

1. **Storage:** the three choices above, with one sentence explaining persistence.
2. **Source:** pure RAM starts empty. Both image modes offer **Create new** and
   **Open existing VHDX**. The second mounts the image directly; the third imports
   it into RAM. Opening an existing image never implies formatting it.
3. **Capacity:** one disk-size field for new pure/full-RAM disks, with a separate
   calculated total RAM requirement. New backed disks have independent virtual
   capacity and RAM cache size. Existing image virtual capacity is read-only.
   Image mode 3 has no editable smaller RAM budget.
4. **Volume:** new blank disks use GPT and one NTFS data volume by default, with
   label, allocation-unit default and available drive letter. Existing image
   layout is retained. Inspect other filesystems/partitions and explain unsupported
   layouts; never offer formatting as recovery from an inspection error.
   Existing image modes additionally expose an explicit blank-image initialization
   checkbox and format label. Initialization binds the inspected file identity and
   requires every logical sector to be zero. Backed mode changes that blank file;
   full-RAM mode preserves it and saves a separate checkpoint. Read-only selection
   disables this choice. It is never an automatic fallback.
5. **Persistence:** mode 2 defaults to Strict, with explicit Fast selection.
   Mode 3 offers **Save image now**, **Save image as…**, and **Save before stopping**
   (default on). A separate **Save during Windows shutdown — best effort** setting
   defaults off. With it off, shutdown discards changes since the last save;
   show that directly. Mode 3 has no Strict toggle and ordinary file flushes do
   not save the image. Pure RAM has no save/durability settings.
6. **Startup:** defaults off. Labels are **Create and format an empty disk at
   startup**, **Mount VHDX and restore cache at startup**, or **Load saved image
   into RAM at startup**, according to mode.
7. **Review and progress:** show virtual/usable capacity, total RAM cost, format
   intent, preferred letter, source, save location, startup behavior and exact
   data-loss boundary. Show load/copy progress in bytes, not just a spinner.

Mode 3's original imported VHDX is kept unchanged. Choose a local **Saved images
folder** for versioned image checkpoints; the UI shows the current committed
VHDX path. New images also use this folder. This avoids silently overwriting the
user's source and makes interrupted saves recoverable. **Save image as…** exports
a standalone VHDX to a new path; it does not change the startup image until the
user explicitly chooses **Use this as my startup image**. Replacing an arbitrary
existing file is outside the first release. These semantics must be visible in
the review screen, not hidden behind the word “Save.”

The image allocation choice Dynamic/Fixed applies to created VHDX files, not to
RAM reservation. New images default Dynamic. Show virtual capacity, actual image
allocation, available host space and checkpoint space needs separately.

## Main window

The desktop layout is now described in [DESKTOP_UI.md](DESKTOP_UI.md): a
Virtual disks page lists the disks and shows the selected one in detail. The
fields, actions and rules below still apply.

Use disk-level resource cards with child volume/cache rows where appropriate.
Do not duplicate an owned virtual volume as an unrelated existing-volume task.

| Mode | Main fields | Actions |
|---|---|---|
| Pure RAM | Capacity, filesystem used/free, reserved RAM, temporary-data badge, startup recreation | Stop and discard, Format, Edit stopped configuration, Remove configuration |
| Backed | Capacity, host image allocation/free space, cache budget/residency, pending writes, Strict/Fast, image path | Flush pending writes, Safely detach, Edit cache, Format selected volume, Remove configuration |
| Full image in RAM | Capacity, RAM reserved, load progress, source and last committed image, changes since saved generation, last-save time/result | Save image now, Save image as, Save and stop, Stop without saving, Format RAM copy, Remove configuration |

Mode 3 shows **Changes only in RAM** until a successful image checkpoint. Do not
label a normal filesystem flush as “Saved.” Mount-time filesystem writes may
make it dirty immediately. Source images opened read-only can still seed a
writable RAM copy saved to a new checkpoint; this is different from requesting a
read-only RAM device, which refuses writes and formatting.

Closing the desktop does not stop any disk. Startup, device lifetime and save
operations belong to a privileged broker. Windows lock/dismount vetoes remain
visible. Removing a configuration never silently discards a running RAM disk or
deletes a backing/checkpoint file. Stop, discard, forget and delete are distinct.

## Scenario matrix

Legend: **P** pure RAM, **B** backed VHDX/cache, **I** full image in RAM.
“Gate” means the UI must report unsupported until the named implementation and
verification are complete, rather than silently changing the requested mode.

| ID | Mode | Scenario | Required outcome |
|---|---|---|---|
| S01 | Existing | Cache existing physical volume | Existing flow; no virtual disk created |
| S02 | P | Create new | Reserve full nonpageable storage; initialize/format before Ready |
| S03 | P/I | Insufficient RAM/global budget | Fail before publication; no hidden host-file/pagefile fallback |
| S04 | P/I | Filesystem becomes full | Normal disk-full response; no disk spill or virtual-capacity growth |
| S05 | P | Stop | Respect open-file veto; explicit discard before device removal |
| S06 | All | Close/reopen UI or restart broker | Adopt live resource by identity; no reload, reformat or automatic discard |
| S07 | P | New boot, startup on | Fresh device, GPT, data partition, saved format/label, empty filesystem |
| S08 | P | New boot, startup off | No device until explicitly started; next start is empty and formatted |
| S09 | B | Create Dynamic VHDX | Separate virtual size/cache budget; host file can grow |
| S10 | B | Create Fixed VHDX | Validate space, expose allocation progress; same cache contract |
| S11 | B | RAM smaller than disk | Cold reads use backing; dirty-capacity backpressure is retained |
| S12 | B | RAM equal/larger than disk | Account metadata; warn on excess; do not promise full residency |
| S13 | B | Open existing formatted image | Preserve source layout/data; apply cache only to selected supported volume |
| S14 | B | Read-only attachment | Gate read-cache-only support; no writes/format |
| S15 | B/I | Image has several data volumes | Inspect all; initial activation rejects unsupported layouts with explanation; future saves/detach quiesce all volumes |
| S16 | B/I | Image is RAW/uninitialized | Explicit initialize/format flow; no automatic recovery formatting |
| S17 | B/I | BitLocker locked/unsupported filesystem | Report locked/unsupported; no unlock bypass or format fallback; gate activation |
| S18 | B/I | Invalid, corrupt, inaccessible image | Keep source unchanged, surface native error; never substitute empty storage |
| S19 | B/I | Source in use by another owner | Refuse takeover/detach; identify conflict |
| S20 | B/I | Alias, hard link or duplicate disk identity | Stable file/disk/partition checks; no two writable views of same image |
| S21 | All | Preferred letter occupied | Fail startup with letter-conflict state; interactive creation may select another letter |
| S22 | B | Cache RAM full | Evict clean data or drain/backpressure; disk size is unchanged |
| S23 | B | Host full or write failure | Fault/report error; preserve recoverable pending RAM data; never claim a successful flush/detach |
| S24 | B | Backing disappears | Surface failure; no stale replay onto a newly appearing image |
| S25 | B | Strict application flush | Honor filesystem/cache/host flush chain; qualify persistence on supported host |
| S26 | B | Fast crash/forced shutdown | Unsaved writes can be lost and image/filesystem consistency damaged |
| S27 | B | Flush while mounted | Explicit persistence barrier, volume stays mounted |
| S28 | All | Open-file lock veto | Cancel stop/save that needs exclusive access; keep usable device; report owner/reason when available |
| S29 | B | Successful detach | Drain, flush, detach whole image; retain VHDX file |
| S30 | All | Remove configuration | Require stopped state or explicit stop flow; retain data files |
| S31 | B/I | Delete image/checkpoint | Separate explicit action; reject mounted, referenced startup or in-flight image |
| S32 | P/B/I | Reformat | Confirm exact target; I formats RAM copy only, leaving saved image until a new save succeeds |
| S33 | B | Change cache budget | Transactional cache reconfiguration; image capacity unchanged |
| S34 | P/I | Change virtual disk size | Deferred; never use RAM-budget edit to truncate/reformat data |
| S35 | B | Resize VHDX | Deferred separate image/partition/filesystem transaction |
| S36 | All | Startup recipe/image missing, replaced or invalid | Startup failed/pending; no identity bypass or automatic blank replacement |
| S37 | All | Cancel creation/load/format | Roll back only owned unpublished resources; report leftovers; preserve source |
| S38 | B | Orderly/forced shutdown | Attempt appropriate drain; forced shutdown can interrupt it |
| S39 | P/I | Sleep/hibernate resume | Preserve same-session device/content; no reformat/reload; qualify capability |
| S40 | B/I | Network, parent/differencing, nested/volatile host | Outside initial scope; refuse with explicit support reason |
| S41 | I | Existing VHDX fits RAM | Read complete logical capacity into hidden RAM device, detach import view, then publish; never reformat |
| S42 | I | Small host file, virtual capacity exceeds RAM | Reject using virtual capacity plus overhead, regardless of file allocation |
| S43 | I | Load cancelled/short read/source changes | No partially populated Ready disk; release owned unpublished RAM and keep source |
| S44 | I | Normal read/write/flush after load | RAM only; no dependency on source image or image-save I/O |
| S45 | I | Save image now | Acquire consistent exclusive filesystem/device boundary; stage and verify full image, commit checkpoint pointer, then resume |
| S46 | I | Save blocked by open files | Return blocked without freezing apps indefinitely; prior checkpoint unchanged |
| S47 | I | Cancel/fail save before commit | Retain live RAM and last committed image; no clean/saved indication |
| S48 | I | Crash during checkpoint commit | Recover from durable operation journal and verified generation files; never load a partial candidate |
| S49 | I | Save and stop | Stop only after completed save; failure retains RAM while machine remains running |
| S50 | I | Stop without saving | Explicit loss acknowledgement; last saved image/source remains usable |
| S51 | I | New boot, startup on | Load last committed checkpoint (or original source before first save); preserve its filesystem/data |
| S52 | I | Crash/reboot without completed save | Runtime changes lost; restart from last committed image; do not replay abandoned RAM metadata |
| S53 | I | Source/old checkpoint unavailable after Ready | Runtime I/O continues in RAM; show next-load/save/export constraints separately |
| S54 | I | New blank full-RAM image | Create/format RAM, commit initial checkpoint, then publish; no empty source overwrite |
| S55 | I | Read-only source, writable RAM copy | Import read-only, permit RAM writes, save only to new managed/export path |
| S56 | I | Save as new standalone VHDX | Valid self-contained image; keep startup pointer unless explicitly changed |
| S57 | I | Save finished, subsequent write occurs | New generation becomes dirty again; no stale Saved badge |
| S58 | I | Shutdown save enabled, timeout/failure | Best-effort save, retain last checkpoint; never promise current RAM survives shutdown |
| S59 | All | Concurrent UI/CLI/startup operations | Serialize per resource/image; stale operation/generation rejected |
| S60 | All | Update/uninstall while disks active | Coordinate stop/save/veto; preserve images and definitions according to explicit action |
| S61 | P/I | Windows Fast Startup boundary | Fresh P formatting / I reload for a new startup; distinguish from session resume, gate until verified |
| S62 | P/I | Driver surprise removal with dirty RAM | Record loss state; never show successful save; no destructive replay on replacement device |
| S63 | I | Exported image has same GPT identities | Prevent simultaneous online clones; detect conflict, do not silently rewrite IDs |
| S64 | I | More than one writer changes source/checkpoint | Source/import lease and saved-generation identity detect conflict; refuse destructive overwrite |

## Startup and formatting are mode-specific

Persist the resource recipe separately from startup enablement. A user may save
an image and remember its settings while choosing manual startup.

For **P**, each new startup with auto-start enabled reserves memory, creates a
fresh zeroed device, initializes GPT and formats the selected filesystem/label.
Publish a letter only after success. This is authorized by the saved creation
recipe, with no new erase prompt on each boot. Validate actual device creation
identity before every step; never format “the disk at letter R:.”

For **B**, reopen the existing image, map its current disk/volume identities,
apply cache and publish. For **I**, load the latest committed image fully, then
publish. Neither mode autoformats existing content. A format option on an
existing RAW image remains an explicit user action.

Broker restart, duplicate startup invocation and UI reopen must be idempotent.
Persist operation stages and consult driver boot/creation generations so they
cannot erase a live disk. Test normal restart, cold start, Fast Startup, sleep
and hibernate separately; ordinary resume must not be mistaken for new startup.
Unsupported lifecycle combinations remain blocked until qualified.

## Scope and acceptance

Implement all three modes, first for local self-contained VHDX files and a single
NTFS data volume (GPT, allowing normal reserved GPT partitions). New disks are
GPT/NTFS. Inspection explains existing MBR, multiple data volumes, ReFS/FAT32/
exFAT, encryption and parent-chain cases; activation for each is a separate gate.
No automatic mode conversion. Live resize, network paths, differencing chains,
boot/system/pagefile roles, live snapshot saves, arbitrary in-place image-file
replacement and compression of RAM storage remain deferred.

The implementation handoff defines source changes, native-driver/broker contracts,
checkpoints, phased delivery and test evidence. Completion requires the three
working UI paths and their maintained VM verification, not merely a wizard mock.

Windows behavior supporting the architecture:
[virtual disk attachment](https://learn.microsoft.com/en-us/windows/win32/api/virtdisk/nf-virtdisk-attachvirtualdisk),
[attachment lifetime](https://learn.microsoft.com/en-us/windows/win32/api/virtdisk/ne-virtdisk-attach_virtual_disk_flag),
[logical capacity](https://learn.microsoft.com/en-us/windows/win32/api/virtdisk/ns-virtdisk-create_virtual_disk_parameters),
and [physical view for logical-sector transfer](https://learn.microsoft.com/en-us/windows/win32/api/virtdisk/nf-virtdisk-getvirtualdiskphysicalpath).
