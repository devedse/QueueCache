# Managed disk implementation handoff: Luna / Sol

Revision 7, 2026-10-04. **Native provider, broker, checkpoint, product CLI and UI
action source is implemented on the feature branch, and all three modes passed the
maintained VM suites on signed 0.4.328.1 under Driver Verifier, including a real
Windows restart lifecycle and broker restart. Sleep/hibernate/Fast Startup, crash
during checkpoint commit and a manual UI walkthrough remain outstanding.** See
[installed foundation verification](MANAGED_DISKS_FOUNDATION_VERIFICATION_20261003.md).
This document and [RAM_DISK_UI_PLAN.md](RAM_DISK_UI_PLAN.md)
replace the earlier two-mode proposal. All three modes are in planned scope.
Implementation is on `feature/managed-disks`; product commands are documented in
[MANAGED_DISKS.md](MANAGED_DISKS.md). Execute remaining work packages
in dependency order and record implementation and verification separately in
[RAM_FIRST_IMPLEMENTATION_TRACKER.md](RAM_FIRST_IMPLEMENTATION_TRACKER.md).

## 1. Product contracts and defaults

Use stable internal mode names `EphemeralRam`, `CachedVhdx`, `ImageInRam`.

| Contract | EphemeralRam | CachedVhdx | ImageInRam |
|---|---|---|---|
| Data plane | Entire logical disk in nonpageable RAM | Native mounted VHDX + existing volume cache | Entire decoded logical disk in nonpageable RAM |
| Capacity | User chooses new virtual disk size | Image virtual size | Existing image virtual size, or chosen new size |
| Memory | Full capacity + metadata/bounded transfer overhead | Independent existing cache budget | Full virtual capacity + metadata/bounded transfer overhead |
| Filesystem creation | Fresh initialize/format on every new creation/boot | Format only new blank image or explicit user action | Preserve imported filesystem; format only new blank RAM disk or explicit RAM-copy format |
| Ordinary flush/FUA | Complete preceding RAM writes; no persistence | Existing Strict/Fast semantics including host flush chain | Complete preceding RAM writes; no implicit image save |
| Explicit persistence | None | Flush pending writes through backing | Consistent complete VHDX checkpoint |
| Stop | Explicit discard | Drain/flush/detach | Save and stop by default; explicit discard alternative |
| Startup | Recreate/format empty | Reopen original image/cache | Reload last committed image |

Recommended defaults: GPT, one NTFS data volume, dynamic new VHDX, startup off.
CachedVhdx defaults Strict; Fast requires the existing explicit volatility choice.
ImageInRam defaults writable RAM copy, Save before stopping on, shutdown save
attempt off. All modes keep configuration independently of auto-start selection.
No RAM paging to a hidden host file, no partial-load fallback and no memory
compression in the first implementation. No image-path access on the RAM device's
normal I/O path. Disk and file capacity are distinct from cache/RAM reservation.

S16 now has an explicit UI/CLI initialization choice bound to the inspected source
identity. The initial implementation accepts only completely zero logical images;
it checks all sectors, including tails, rather than treating an absent/corrupt
partition table as permission to erase. Backed mode initializes the chosen blank
image; ImageInRam initializes its copy and commits a separate checkpoint without
changing the source. This consent is consumed on success and never becomes an
automatic startup format recipe for image modes. Stopped recipe editing supports
preferred letter/label and new pure-RAM capacity; image resize remains deferred.
Resource-scoped image I/O attempt counters carry an observation epoch; status and
runtime tests do not substitute completed transfer totals for actual attempts.
Maintained plan-81 broker-restart and lifecycle prepare/verify/cleanup phases now
preserve independent byte oracles and native creation/startup identities across
operator-controlled transitions. They perform no reboot, sleep or process kill;
actual transition provenance and native crash/power qualification remain required.

Maintained plan 82 extends the provider and three product suites to both logical
sector sizes, with distinct resource identities/evidence. Native range errors,
physical read-only write rejection and redundant cache refusal are required
checks rather than inferred from advertised flags. Actual Windows qualification
is recorded separately in the execution tracker.

Plan 83 adds mandatory native RAM TRIM zero/adjacent-guard/rewrite proof for both
geometries, using file-relative filesystem requests on owned fixture files.
Provider counters must prove actual TRIM completion; a successful filesystem
request alone is insufficient. Native/product evidence retains the primary
failure independently of any teardown failure.

Plan 84 additionally exercises the actual product CLI for all three modes and
both geometries through the existing binary/maintained runner. Its uniquely owned
fixtures cover action bindings, strict JSON, Windows/stale-identity vetoes,
format/restart bytes and source-preserving removal. Every product child is
recorded; independent exact-owner cleanup prevents a CLI output failure from
orphaning storage silently. Windows CLI execution remains a separate acceptance
gate from service API fixtures and parser-only CI tests.

Plan 85 exercises physical allocation rollback after early, middle and final
allocated slabs, with native boundary/resource proof and no persistent fault
setting. The release IRP is prepared before reservation and included in its
metadata accounting, so low-memory teardown needs no fresh allocation. Native
Verifier/VM proof remains required for this lifetime change.

Plan 86 adds native checkpoint failure checks: Save with a held open file and
Export to an existing destination must retain the prior image pointer and live
dirty RAM, leave existing destination bytes intact, and restore writable operation.
Precommit cancellation is reported as cancellation only after independent cleanup
has succeeded; cleanup or catalog failures remain explicit recovery failures.
Host-full, interrupted commit and power qualification remain separate gates.

Plan 87 requires native shared-budget evidence to include MDL/PFN/slab metadata,
along with payload and bounded transfer workspace. Cache and provider use one
locked-page metadata sizing helper; cache payload stays inside its hard budget.
The managed headroom quote is conservative, while actual native reservations
remain authoritative. Full matched cache regression measurements remain required.

ImageInRam always preserves the imported source file. Saves create standalone
versioned VHDX checkpoints in a selected managed local directory. The resource's
committed-image pointer determines the next load. Save As creates a new standalone
file without changing that pointer unless explicitly requested. This is a
concrete initial save design, not an unresolved choice between in-place writes
and snapshots. It deliberately avoids overwriting arbitrary user-owned images.

The imported disk's logical bytes and layout are preserved; VHDX container
metadata/file hash can differ when exporting a new image. Do not promise a
byte-identical VHDX file. Do not regenerate GPT/partition/filesystem identities
silently. Simultaneous online clones with colliding identities are refused.

Initial activation: supported Windows versions already targeted by QueueCache,
local self-contained fixed/dynamic VHDX, GPT with one NTFS data volume and optional
normal reserved partition(s). Validate complete layout, logical/physical sector
sizes and alignment; separately qualify 512e/4Kn. Inspection may describe other
layouts, but MBR/multiple data volumes/other filesystems/encrypted images remain
explicit capability gates. No network, differencing parent chains, boot/system/
pagefile/crashdump disks, live resize, live background snapshots, or automatic
mode conversion. Do not interpret a locked or corrupt image as blank.

## 2. Review of current repository and integration map

| Existing seam | Current behavior | Planned change |
|---|---|---|
| `src/QueueCache.Desktop/MainWindow.cs`, `CacheTaskService.cs` | Per-volume cache UI with headless service seam | Add managed-disk wizard/cards and separate `IManagedDiskService`; keep cache removal non-destructive |
| `src/QueueCache.Operations/CacheTasks.cs`, `SavedConfigurations.cs` | Cache operations; saved volume GUID, PnP identity and size | Reuse for CachedVhdx after resolving owned image to current volume; preserve strict identity checks |
| `src/QueueCache.Operations/DiskTarget.cs`, `VolumeCatalog.cs`, `DiskEjection.cs` | Target discovery and physical eject | Add owned virtual-disk identity/extent discovery and separate managed stop/detach operations |
| `src/QueueCache.Developer/LabDisk.cs` | DiskPart/PowerShell three-volume test VHDX | Keep its test layout separate; production image operations use typed Windows APIs |
| `driver/qcache/driver.cpp` | WDM volume upper filter | Add secure management/budget endpoint and owned-RAM identification; never pretend the filter already creates disks |
| `driver/qcache/writecache.cpp` | Static process-wide-in-driver `GlobalBudget` / `GlobalLimit` | Extract one atomic kernel budget authority shared by cache allocations and RAM-provider reservations |
| `src/QueueCache.Cli` | Normal commands and same-binary developer worker | Add internal broker-host mode and product `disk` commands using the same operations as the UI |
| `packaging/QueueCache.iss`, `packaging/Install-Driver.ps1`, `build/Build-Installer.ps1` | Current driver package/startup cache restore | Extend the same installer/artifacts with RAM provider and broker lifetime/dependencies |
| `src/QueueCache.Developer/Verification`, `VerificationRunnerTests.cs` | Maintained runner, strict evidence/recovery | Add opt-in managed-disk scenarios and host-safe contracts |
| `tests/QueueCache.Desktop.Tests` | Headless frontend tests | Add wizard/state/action coverage through fake services |

New suggested files (names may follow local conventions):
`ManagedDisks/Models.cs`, `ManagedDiskCoordinator.cs`, `ManagedDiskStore.cs`,
`VirtualDiskApi.cs`, `RamDiskApi.cs`, `ImageTransfer.cs`, `ImageCheckpointStore.cs`,
`StartupCoordinator.cs` under Operations; versioned native protocol bindings under
Management; `ManagedDiskWizard.cs`, `ManagedDiskCard.cs`, `ManagedDiskService.cs`
under Desktop. Do not turn the desktop into an interpreter for CLI output.

### Sharing with normal cached volumes

The owner's requirement is to share as much code as practical across normal
cached volumes and all three managed disk modes. Compose existing components;
avoid a second cache implementation, memory limit or policy validator. Share
mechanisms while preserving each mode's durability and storage contracts.

| Concern | Shared component / implementation rule | Current source status |
|---|---|---|
| Cached-volume settings | `CacheConfiguration`, `CacheOptions`, existing configuration/identity/drain operations | B uses the existing cache transaction with its exact managed resource owner; ordinary cache commands refuse conflicting ownership |
| Physical headroom | `MemoryBudget.AvailableForReservation` and existing `ValidateIncrease` | Ordinary cache and provider activation use the common estimate before authoritative kernel reservation |
| Locked physical pages | `driver/shared/lockedpages.h`, bounded MDL-backed slabs | Both drivers use the extracted allocator/free routines; provider storage is zeroed before publication and on release |
| Global accounting | `driver/shared/memorybudget.h`, one authority instance in qcache | Source includes the kernel endpoint, owned reservation tokens and provider-held references; cross-driver Windows qualification remains RD03 |
| Managed disk creation | `ManagedDiskCreationCoordinator`, one typed definition/runtime/progress model | All three modes share validation, owned publication and independent cancellation cleanup; native/backend source exists, Windows qualification remains RD02-RD04 |
| Full logical transfer | `ILogicalDisk` and `LogicalImageTransfer` | Import/export share complete sector copy, bounded buffers, hash and read-back verification; Windows/RAM adapters and freeze/commit source exist, native proof remains RD06-RD07 |
| Startup decisions | `ManagedDiskStartup`, authoritative epoch + native generation | Broker uses the shared decision rules and native startup classifier; maintained lifecycle phases exist, actual transition qualification remains RD08/RD10 |
| UI and CLI | `IManagedDiskService`, Operations assembly | Both product frontends call the same SCM broker for all three modes and lifecycle actions; Windows end-to-end qualification remains RD09-RD10 |

Keep the RAM provider's Windows block-device/SCSI presentation separate from the
volume filter. A fully reserved dense RAM disk does not need cache eviction or
dirty-block draining. Ordinary P/I writes, TRIM and flushes must never acquire a
backing-image dependency. B retains the existing cache's Strict/Fast behavior.
Extract additional bounds/sector-copy helpers only where both implementations
actually use them; do not add allocations, indirect calls, full-cache drains or
image access to an existing hot path just to force a common abstraction.

Compiling a common header into two binaries does **not** share a global counter.
RD03's source connects RAM-provider reservations to the same authoritative qcache
instance, including metadata, failure unwinding, rundown and native ownership.
Host tests and compile-time admission checks do not qualify this cross-driver
protocol or prove native performance. Retain the maintained runner and
lower-I/O-attempt evidence for actual Windows qualification.

## 3. Native provider decision and proof gates

Implement an in-tree RAM disk provider as a **separate Storport virtual miniport**
project, proposed `driver/ramdisk/QueueCache.RamDisk.vcxproj`, with one managed
virtual adapter and a logical unit per published RAM disk. Use the same signed
installer/CI distribution. Do not introduce an ImDisk dependency, another test
executable or a second installer. The existing WDM filter remains the volume
cache; the miniport supplies block devices. Microsoft identifies a virtual
miniport as suitable for a RAM disk [R1].

Before product implementation, complete a bounded native prototype gate:

- Establish the supported Storport service/control request mechanism, ACL and
  process authorization, PASSIVE_LEVEL allocation/control worker, root adapter
  installation and LUN publication/removal notifications. Record exact callback
  and API choices in the implementing design note; WDM filter dispatch code must
  not be copied into miniport callbacks blindly.
- Prove that a private allocated RAM object can be filled without exposing a
  writable disk/volume, then published with stable resource identifiers. Either
  unpublished LUN storage or a provider-private prepublication object is valid;
  no filesystem access until an explicit Publish operation succeeds.
- Qualify signed Debug/Release packages, attach/format/normal I/O, flush, TRIM,
  PnP/power, cancellation and removal under Driver Verifier on the disposable VM.
- Prove read-only, no-letter/offline staging of native VHDX devices permits the
  required raw logical-sector read/write operations without filesystem mutation
  or disk-identity collision. `NO_DRIVE_LETTER` alone is not an isolation proof.
- If a platform primitive fails this gate, document and solve it before exposing
  that mode. Do not silently substitute a mounted VHDX for a promised RAM disk.

### Provider ABI and behavior

Version every request/reply; validate version, size, flags, arithmetic bounds,
sector alignment, capacity and caller access before using any address/length.
Use resource ID + kernel boot epoch + creation generation, not disk number.

Required operations: QueryCapabilities, Reserve/CreateUnpublished, Query,
TransferIn/TransferOut, Publish, AcquireStableView/ReleaseStableView,
SetReadOnly, Remove, and EnumerateOwned. Transfers use bounded direct-I/O buffers
(or a verified equivalent), never arbitrary caller pointers or one disk-sized
user allocation. Unknown versions/unsupported operations return explicit errors. Negotiate capabilities
before enabling a UI mode so a newer UI cannot reinterpret an older driver reply.

Normal SCSI/storage operations must include the commands Windows disk/partition/
filesystem layers require: inquiry/identity, capacity, reads/writes, synchronize
cache, write-protection, supported mode/caching pages, TRIM/UNMAP and error/sense
reporting. Unsupported commands fail correctly rather than returning fictitious
success. Advertise volatile caching truthfully. Flush/FUA serialize preceding
RAM writes; they cannot promise persistence without backing. No write may report
more bytes completed than copied. Reject out-of-range and overflowed requests.

Allocate zeroed locked physical pages in bounded slabs plus nonpaged descriptor
metadata. Never require one giant physically contiguous allocation. Partial
allocation failure unwinds all memory and the exact budget reservation. Publish
only once capacity is fully backed. A full-RAM disk's free filesystem space is
still reserved RAM; report that distinction. TRIM must have a documented read
result (choose zero) and must not release the capacity guarantee.

Generation advances on every accepted mutation, including writes, TRIM and
layout changes. Stable-view acquisition fences new writes/mutating controls,
waits for outstanding operations, and exposes one immutable generation for
export. Status queries do not copy/drain/save the disk. Teardown denies new
requests, drains/cancels existing requests, waits for references, unpublishes,
then zeros/frees memory and releases the budget. Do not free backing on a broker
handle close or while a snapshot/transfer/request still owns it.

### One RAM budget, including existing cache commands

Refactor qcache's static budget operations into a single authority used by the
existing cache and RAM provider. Two independent globals in separate driver
binaries are not a shared limit. Proposed implementation: secure qcache control
device with kernel-only internal reserve/release requests from the RAM provider;
regular cache allocation calls the same authority directly. Initialize it before
provider use and hold device/driver lifetime references while reservations exist.

Reservations contain an unguessable kernel token, owner/provider identity,
resource ID, category and exact bytes. User processes cannot release another
owner's reservation. The provider owns the reservation for as long as pages
exist, even if the broker crashes. Refcounts/rundown prevent filter unload during
provider reservations; provider allocation happens at an allowed passive worker
context, never by calling a blocking allocator in a Storport fast callback.
Include both cache and provider overhead and bounded transfer workspaces in
accounting; define broker buffer headroom separately. Enforce atomic checks so
simultaneous cache and RAM allocations cannot each pass a stale free-space test.
Retain the existing global-limit policy initially; actual page allocation may
still fail below that limit and must unwind cleanly.

Recognize RAM-provider disks through authenticated driver/device identity.
Refuse adding a redundant QueueCache cache to their volumes in both management
and native configuration paths. Do not depend only on hiding the UI action.
Normal Windows I/O still traverses required filters with cache disabled.

## 4. Shared operations, ownership and broker

`IManagedDiskService` exposes InspectSource, ValidatePlan, Create/Start,
Get/List, SaveImage, ExportImage, Stop, Format, ChangeCacheBudget,
SetStartup, RemoveDefinition and RecoverOperation. Each mutation accepts a
resource/operation ID and expected generation, reports structured stage/byte
progress, and returns an explicit terminal result. Stop intent is SaveThenStop,
DiscardThenStop or DrainThenDetach; cancellation is not discard authorization.

Separate typed policy/scenario decisions from native handles, I/O transfer,
transaction journal, progress and UI binding. Keep all destructive steps behind
fresh identity validation. Format consumes an owned-device capability produced
by creation or an explicit existing-volume erase preview; never a drive letter
alone. Use one operation lease per resource, image file and backing dependency.
Serializing only inside one desktop process is insufficient.

Host the privileged broker as a Windows service in the existing qcache executable
with an internal service-host entrypoint. Use an authenticated local IPC endpoint;
restrict mutation to elevated administrators/SYSTEM and validate request access,
paths, identity and bounded payloads in the service. Kernel ownership checks are
still required. No credentials or executable startup scripts in user-writable
settings. UI closure does not cancel device lifetime. Broker recovery enumerates
live provider objects and reconciles ownership before attempting any creation.

Resource definition (schema version 1): resource GUID; mode; desired capacity;
sector/layout/filesystem recipe; preferred letter; image source reference;
checkpoint directory; current committed image generation; cache configuration
for B only; read-only/save/startup preferences; supported-capability version.
Keep this distinct from `SavedConfiguration` for existing cached volumes.

Runtime state: boot epoch, creation generation, resolved disk and volume IDs,
mode/state, reserved/committed RAM, provider write generation, last committed
save generation, pending/current operation, actual backing/source availability
and last structured error. `Saved` means a checkpoint of generation G committed;
current generation > G means changed, even if the save finished milliseconds ago.

Machine-owned catalog/journal records live under an administrator-protected
location (e.g. `%ProgramData%\QueueCache\ManagedDisks`), with staged write, flush,
version/checksum, previous committed record and recoverable pointer switch.
Native identity uses host volume ID + file ID + final handle-resolved path +
virtual disk ID + capacity/sector sizes + image generation/digest as applicable.
An unchanged path/timestamp is not identity. Legitimate checkpoint replacement
changes file identity and updates the catalog in the same committed transaction.

Reject backing/checkpoint directories on pure/full-RAM disks, volatile Fast-cached
host volumes or unsupported managed/nested dependencies. Inspect transitive host
mapping and prevent later policy changes that invalidate a dependency. Initially
support qualified local NTFS host storage; gate other host filesystems/EFS/
compressed-file/provider restrictions rather than guessing. Opening a source
read-only for I does not require its filesystem to be mounted. The managed checkpoint subtree must have verified administrator/SYSTEM-only write
and delete permissions, and handle-resolved paths must prevent reparse/path-swap
redirection. A chosen folder does not automatically become trusted. Source reads
use their own restricted handles; do not grant general filesystem access through
unchecked broker requests. Existing-image
sharing/attach ownership must be verified using Windows handles/provider state,
not just a named mutex honored only by QueueCache.

## 5. Creation and mounting transactions

### A. Pure RAM

Validate recipe/limits/letter → reserve/create private zeroed RAM object → publish
only into controlled initialization state → GPT/data partition/format by exact
resource identity → resolve volume/assign letter → Ready. Suppress normal volume
access during initialization; prove suppression with the native prototype.
Persist transaction stages before irreversible work. Formatting failure never
produces Ready. Clean up only that owned creation, preserving failure evidence.

### B. Cached VHDX

Create uses `CreateVirtualDisk` at a unique new path; Open uses `OpenVirtualDisk`
without overwriting anything. Inspect format, parent chain, capacity/sectors,
existing attachment and ownership. Attach through Windows with explicitly chosen
lifetime and controlled volume publication, retrieve physical path [R2], then
resolve exact disk/partitions. New RAW images alone follow confirmed GPT/format.
Apply the existing cache configuration to the resolved supported volume and
publish Ready only when both image and cache are operational.

If cache setup fails, report that the requested mode did not start; roll back
owned cache/attachment. Do not quietly expose uncached storage as success. Existing
files are never deleted on failure. New orphan images are cataloged and offered
for retry/removal. Persistent attachment lifetime is owned by the service, not
accidentally by the desktop's last native handle [R3].

### C. Full image in RAM

1. Validate source and acquire a stable read-only import/ownership lease. Refuse
   an active writer, unsupported chain/layout or conflicting online disk IDs.
2. Read VHDX virtual capacity and sector properties. Reserve that entire capacity
   plus metadata; file length/allocated ranges do not reduce RAM requirement.
3. Attach a temporary read-only native image view without user mount points.
   Establish and verify the qualified offline/isolation state before transfer.
   Resolve its physical path through the handle, recheck identity, then stream
   `[0, virtualCapacity)` logical sectors into an unpublished RAM object.
4. Use bounded aligned chunks, e.g. an initial 8 MiB transfer window subject to
   sector alignment and measured tuning; handle partial completions precisely.
   Record total bytes and a logical-content digest. A short/error read cannot
   become zero-filled success. Unallocated VHDX regions are read through the
   Windows provider as logical sectors, never skipped from container-file size.
5. Detach the temporary native view and verify it is gone before publishing the
   cloned disk identities. Do not change machine-wide automount settings. Keep
   source identity metadata; close import-only handles that are no longer needed.
6. Publish the complete RAM object, resolve existing partitions, assign letter
   and enter Ready. Do not initialize or format imported content. Record source
   as the initial durable baseline until a first checkpoint is committed.
7. After Ready, removal/unavailability of the source cannot break foreground I/O.
   Show inability to reload/save where relevant; Save As to another approved host
   can still persist RAM. Detect a changed/replaced source on the next load.

Read-only source and read-only exposed RAM device are different flags. A writable
RAM clone of a read-only source is supported with saves to new paths. First-scope
clone identity policy permits only one online instance with those disk/partition
IDs; reject conflicts at publication and surface subsequent Windows conflicts.
Do not claim protection from a privileged external actor forcibly changing disks.

For new I images: create/format a private RAM disk, acquire stable view, commit
its initial image checkpoint, then publish Ready. If initial save fails, keep the
resource in a recoverable initialization state or cleanly abandon it; do not
silently downgrade to P. Raw/unformatted existing images require an explicit
format of their RAM copy before ready; preserve their source file.

## 6. Consistent image save and checkpoint commit

An application flush on I is RAM-only. Only the explicit save operation below
makes an image checkpoint. First implementation saves the complete logical disk;
no incremental bitmap, live snapshot or copy-on-write save engine is required.
Writes are paused during the save. Clearly disclose downtime before starting.

### Acquire a consistent generation

1. Validate destination host/space/permissions and acquire resource/image leases.
2. Enumerate all volumes/extents on the RAM disk. Refuse unknown/unsupported
   layouts. Acquire exclusive filesystem locks and flush filesystem buffers to
   RAM. Windows can refuse a volume lock while files are open [R4]; report a
   blocked save and release already-acquired locks. Never forcibly close apps.
3. After filesystem flushing, acquire the provider stable-view barrier. Wait for
   outstanding reads/writes and mutation controls as required; capture generation
   G. Fence raw writes/TRIM/layout changes too, not just filesystem writes.
4. Export exactly the frozen raw logical sectors, including GPT/backup GPT,
   partition metadata, filesystem journal and unallocated areas. Hold filesystem
   locks and stable view until the snapshot bytes and digest are fully captured.
   VSS/application-consistent online backup is outside this first implementation.

### Stage, verify and commit without destroying the old image

5. Create a uniquely named candidate VHDX in the resource's checkpoint directory
   on the same qualified host volume. Set equal virtual capacity and sector
   properties. Attach only through the qualified no-mount/offline staging path.
   This candidate shares logical disk IDs, so collision isolation is mandatory.
6. Write all logical sectors, flush the native disk/provider/host chain, detach
   the candidate and close write handles. Reopen read-only in isolation and
   verify capacity/layout and the complete logical-sector digest, then detach.
   Require file/host durability flush completion before committing metadata.
7. Record a durable CandidateVerified journal record with operation ID, G,
   candidate file identity/digest and previous committed pointer. Candidate files
   have final unique generation names and are never edited after verification.
8. Commit the resource's current-image pointer through the recoverable catalog
   transaction, retaining its previous valid record and previous image. This
   pointer update is the save commit point. Flush it and read it back; mark G
   saved only after successful verification of the committed record.
9. Release stable view/volume locks and resume operation, or remove the provider
   disk for SaveThenStop. A subsequent write increments generation and marks the
   resource changed again. A stop failure after save is “Saved, still mounted,”
   never silent discard.

The imported source and previous committed generation survive all precommit
errors. Recovery uses checksummed journal/catalog records and image verification,
not the newest filename or mtime. Keep at least current and previous committed
images, plus any unreconciled candidate. Cleanup only owned, unreferenced files;
user-selected source/export files are never automatic cleanup candidates.

Budget host space for old image + candidate + any retained previous generation;
use worst-case logical capacity plus VHDX overhead for the candidate, not merely
filesystem used bytes. Dynamic files can still encounter ENOSPC after preflight.
On save failure, restore writable RAM operation where possible and retain dirty
state. If an unfreeze/unlock/detach step fails, report its actual recovery state;
never free the only live copy while the machine is still running.

Save As uses the same stable-view and verification protocol at a new standalone
path and does not change startup source by default. If the new file appears but
final catalog work fails, record a recoverable orphan rather than deleting a
possibly committed export. Do not implement “safe save” as unchecked in-place
raw writes or assume `ReplaceFileW` guarantees durable replacement: its
WRITE_THROUGH flag is unsupported and failure outcomes differ [R5].

Cancellation is allowed before commit and releases locks/buffers with a separate
cleanup deadline. Once commit begins, complete/reconcile it and report the actual
committed generation; late cancellation cannot claim rollback. After broker
crash on a live Windows session, enumerate and adopt the provider object. Frozen
objects have explicit state/operation tokens; release abandoned freezes only
after journal reconciliation. Do not format/reload a surviving dirty disk.

## 7. Stop, formatting, startup, power and recovery

P Stop: lock/quiesce supported volumes → explicit discard intent → orderly
provider removal. Veto/failure retains live storage if still present. B Detach:
quiesce/drain/flush all image volumes using existing ordering semantics, invoke
virtual disk detach, verify disappearance; restore reversible cache states after
veto. This is distinct from USB eject capability and must not bypass its own
Windows locks. I SaveThenStop follows section 6; I DiscardThenStop requires
explicit acknowledgement of the unsaved generation. Same defaults in future CLI.

Formatting existing devices is a distinct transaction: identity/erase preview,
exclusive ownership, mode-specific flush/quiesce, format, refresh identities and
state. For I it changes only RAM until a save commits. For P it remains temporary;
for B it changes the backing image. Refuse live resource-definition removal;
route through explicit stop, then remove configuration only. Image deletion is
separate and refuses referenced, mounted or in-flight files.

Use one startup coordinator for managed resources and dependent cache profiles;
avoid races with the existing scheduled cache-restore task. Restore regular
physical-volume profiles through their strict existing identity checks. Resolve
owned VHDX to its current PnP/volume identities explicitly; do not weaken those
checks globally to make virtual disks restore. Remove duplicate ownership of
managed-image cache profiles from the generic restore path by migration/association.

At every new Windows startup, P with auto-start on is freshly created, partitioned
and formatted using its stored recipe before publication. B reopens its image;
I loads the committed checkpoint/source, preserving filesystem and files. No
reformat for I. Startup off leaves a remembered stopped resource. Broker restart,
UI reopen, repeated restore and retry within the same live generation are
idempotent. Letter conflict or missing/replaced image means startup blocked,
not selection of another disk or automatic creation of an empty image.

Define an explicit device boot epoch and startup-session classification. Test
cold boot, restart, Fast Startup, sleep and hibernate individually. Fast Startup
is a new startup for the promised P recreation/I reload behavior, while resume
of the same sleeping/hibernated session retains contents. Do not use desktop PID,
service restart or uptime alone as the classifier. Reject unsupported combinations
until the provider/broker lifecycle proof exists. Host hibernation may itself
persist RAM as part of Windows session state; do not market P/I as a guarantee
that no RAM bytes can ever appear in host hibernation/dump files.

Broker preshutdown handler can attempt an opted-in I save while host storage is
available, with bounded deadlines and progress. It cannot guarantee completion
before forced shutdown/power loss or application lock veto. With shutdown-save
off, a new boot loads the previous image and loses intervening RAM changes.
Before shutdown, UI shows the last committed save time and unsaved state. Never
advertise normal file flush or shutdown alone as a durability guarantee for I.

Journal recovery table:

| Last durable stage | Same boot, RAM still exists | After new boot |
|---|---|---|
| Creating/Loading | Reconcile owned unpublished object; retry or abandon safely | Clear stale runtime record, keep definitions/source; retry startup only by recipe |
| Ready dirty | Adopt exact generation; do not reload | P recreates empty; I reloads last committed image; record lost unsaved state where knowable |
| Exporting/CandidateVerified | Keep RAM; validate candidate; release abandoned lock only after reconciliation | Validate candidate as recoverable, use prior committed image by default |
| Pointer commit uncertain | Read/validate both catalog records and candidate; report recovered outcome | Same; never infer success from candidate existence |
| Committed before cleanup | Retain committed generation, resume/finish explicit stop | Load committed generation; cleanup only unreferenced owned files |
| Source/image identity mismatch | Leave live RAM running; block conflicting load/delete | Block startup, retain files and recovery details |

Installer changes use the existing distribution: register signed provider,
management endpoint and broker, order service dependencies, migrate startup
ownership and record installed versions. Update/uninstall must first reconcile
active managed disks; an interactive open-file/save veto stops the operation.
Never forcibly discard RAM or delete images to finish uninstall. Preserve
recovery definitions until owned devices are stopped and state is reconciled.

## 8. Observability and performance contract

Expose provider capacity/reserved bytes, state/boot/creation generation, accepted
read/write bytes, flushes, TRIM, errors, active transfers, write generation and
stable-view owner. Broker exposes import/export bytes, source/destination IDs,
last committed image/save generation, stages, elapsed time and cleanup outcome.
Unsupported fields are unavailable, not zero. Healthy status/graph polling must
not trigger disk I/O, flushing, full-image copying or cache draining.

For P/I steady-state I/O, prove no source/backing read/write/flush attempts after
Ready, using maintained broker transfer counters plus owned-device/provider
traces; qcache lower counters alone cannot prove absence of user-mode file I/O.
Separate source load, runtime score, save and teardown intervals. Explicit export
is the only normal I data-persistence operation; pause interval is separately
reported. B retains Fast fitting-write admission without incidental lower I/O,
Strict barriers and capacity backpressure. Retain the supported 72-case
write-performance contract if touching shared cache hot paths; use focused
selections for isolated investigations and full required comparisons when the
shared allocator/native changes warrant them. No speculative throughput promise.

## 9. Work packages and acceptance gates

| ID | Dependency | Deliverable | Exit evidence |
|---|---|---|---|
| RD01 | None | Typed modes/requests/states, source inspection, UI wire flow, resource schema | Host-safe validation/state tests; all three mode/format/save distinctions shown |
| RD02 | RD01 | Native prototype and isolated VHDX sector-transfer proof from section 3 | Signed provider works on disposable VM; publication, identity and staging isolation proven |
| RD03 | RD02 | Shared kernel RAM budget, provider allocations/ABI/I/O/lifetime | Concurrent cache/RAM allocations bounded; zero/leak/cancel/teardown tests and Verifier evidence |
| RD04 | RD01, RD02 | Production native VHDX wrapper and managed B transaction | Create/open/Strict/Fast/flush/detach byte checks with original identity guards |
| RD05 | RD01, RD03 | P create/format/stop and standalone memory ownership | Writable filesystem, full capacity, no backing I/O; failure leaves no wrong-device format |
| RD06 | RD01, RD03, RD04 | I full logical import, read-only source handling, private publication | Exact logical bytes loaded; no partial Ready/fallback; all runtime I/O survives source loss |
| RD07 | RD06 | Full consistent export, generation journal/catalog, save/stop/export recovery | Old checkpoint survives failures; saved image reloads exact frozen generation; commit races covered |
| RD08 | RD03–RD07 | Broker IPC, startup/session restore, shutdown attempt, package/update/uninstall | UI/broker restart adoption; P boot formatting and I boot reload; lifecycle and ownership gates |
| RD09 | RD04–RD08 | Complete three-mode UI, field/state/actions, progress and actionable failures | Headless tests plus manual VM UI walkthrough, no duplicate cards or misleading Saved state |
| RD10 | RD04–RD09 | Maintained runner suites and cross-boot evidence workflow | FINISHED/status/summary/results/raw evidence; restore verified independently |
| RD11 | RD10 | Focused correctness/performance qualification and public docs | Installed driver identities, all declared supported scenarios covered; gaps explicit |

Add focused maintained-runner cases with each RD02–RD09 implementation; RD10
consolidates and completes the evidence matrix rather than introducing testing
only after all code exists. Prototype testing also obeys the same owned-target
and maintained-runner requirements.

RD02 is a technical feasibility gate, not permission to omit modes 1 or 3.
Contracts/tests may be written in parallel, but don't expose a UI mode as working
before its backend dependencies pass. Do not schedule RD09's mocks as completion
of RD09. Track checkpoints as source commits with coherent interfaces; keep
native ABI/bindings/tests aligned in each change. GitHub Actions owns versions.
Product CLI commands are included by the owner's follow-up instruction. Implement
`qcache disk create`, `list`, `status`, `start`, `stop`, `flush`, `save`, `export`,
`format`, `cache`, `startup`, `remove`, `delete-image` and `recover` through the
same broker/Operations API as the UI. `create --mode ram|cached-vhdx|image-in-ram`
binds source/new-image, capacity, independent cache, letter, label, read-only,
allocation, startup and save policies. Destructive actions require explicit intent
and fresh expected resource/boot/creation identity; `stop --discard` acknowledges
loss and never deletes images. JSON output exposes typed definitions/runtime,
checkpoint generations and actual failures. Save/export cancellation and source
identity rules are identical in both frontends. Add parser/host contracts and
maintained native CLI scenarios. Internal service/verification plumbing
needed by the UI is in scope and uses the existing binary.

## 10. Verification plan for implementation

Host-safe management/operations contracts: mode-size arithmetic and overflow,
virtual versus allocated file size, complete-import accounting, schema migration,
identity/alias/source-change checks, duplicate operation serialization, forbidden
backing dependencies, exact format target, generation races, failure cleanup and
checkpoint pointer recovery. Exercise the save journal at every failure boundary
using fakes; tests must verify outcomes/invariants, not merely mirror methods.

Desktop tests: all three wizard branches; hidden irrelevant fields; memory errors;
load cancellation; new/existing format distinction; read-only source versus
read-only RAM; save/stop/discard labels; busy/open-file/fault states; unsaved badge;
startup labels and idempotent refresh; keyboard navigation/accessibility.

Commands remain `dotnet run --project tests/QueueCache.Management.Tests -c Release`
and `dotnet run --project tests/QueueCache.Desktop.Tests -c Release`, plus managed
solution and native Debug/Release CI builds as appropriate. Implemented foundation
contracts have passed these host checks; remaining contracts are future gates.
Host success is not native VM verification. Ordinary-cache VM results are recorded
separately from the new managed-disk qualification matrix.

Extend `qcache developer verify` with opt-in typed managed-disk suites, proposed
`ram-disk`, `vhdx-backed`, `image-in-ram` and startup prepare/verify phases.
Do not create private orchestration scripts or a separate test executable. Bump
the runner plan version only when implementing its workload contracts. Keep
unique immutable case IDs, exact loaded-module/hash identity, owned process IDs,
control/transaction traces, required ready/coverage proof, explicit intervals,
separate restoration deadlines and complete raw output. No overall deadline by
default, with bounded operations and synchronous progress retained.

Fixtures use only uniquely owned disposable images/devices. Existing lab
`volumes`, `trim-cache`, and raw write suites retain their lab VHDX requirement;
add RAM-provider equivalents explicitly without weakening their guards. For
cross-boot tests, prepare a durable manifest outside RAM, obtain the required
planned restart authorization, then verify via the maintained post-boot phase.
A reboot is not automatic recovery from a failed save/test.

| Test group | Required cases and oracle |
|---|---|
| P contents | New unwritten sectors zero; formatted NTFS read/write; partial-sector/unaligned-bound checks; TRIM zero contract; full volume; flush ordering |
| P boot | Write unique sentinel, reboot, prove empty newly formatted filesystem and saved label/size; startup off produces no disk; duplicate restore does not erase current sentinel |
| Memory | Concurrent existing-cache/P/I reserves; physical allocation failure midway; cancel/create/remove churn; peak accounting; memory returned once after final reference |
| B persistence | New/existing fixed/dynamic images; independent RAM size; Strict flush; Fast fitting writes; detach/reopen byte oracle; readonly/in-use/layout gates |
| I import | Full logical-sector digest including first/last/GPT/unallocated sectors; sparse file much smaller than virtual capacity; 512e/4Kn gates; short reads/cancellation; never reformat |
| I runtime | Detach/release source view, make source unavailable in controlled fixture, read/write still work; no transfer attempts in exact runtime interval; raw and filesystem mutations mark changed |
| I save | Known files plus metadata survive export/reload; full frozen-sector digest; blocked open handles; writes after save cause dirty; save-only/save-stop/save-as pointer semantics |
| Save faults | Host-full, I/O error and cancellation at creation/copy/flush/verify/journal/pointer/cleanup; old image valid; live RAM retained; failure never reported Saved |
| Crash recovery | Broker crash at each journal state and provider freeze; reconcile same-boot memory; separately authorized VM reset at commit boundaries with old-or-new verified image recovery |
| Identity | Letter reuse, source replacement, path aliases/hard links, already-attached image, duplicate GPT IDs, different PnP after legitimate image reopen; no foreign format/delete |
| Lifetime | Open-file stop veto; UI close; broker restart; sleep/hibernate/Fast Startup; ordinary and forced shutdown; loaded driver removal failure paths |
| Installation | Upgrade/uninstall veto with unsaved/in-use RAM; stop/retry; correct signed artifacts/startup dependency; original images retained |
| Performance | Load/steady-state/save separately; same workload binary/hash; fitting RAM access independent of backing; existing cache admission/latency regression coverage when touched |

Before each VM run follow AGENTS.md preflight: correct elevated user, no competing
workloads, verified intended CI module(s), clean disposable target identity and
no armed unrelated hooks. Fault cases declare expected fault injection and clean
up their own hooks. After completion inspect FINISHED, status, SUMMARY, results,
run log and raw evidence; report source implementation and VM coverage separately.
A missing marker, missing counter, unsupported layout, incomplete matrix or
unqualified lifecycle path is never a pass.

## 11. Definition of done and unresolved platform gates

Done means all three UI choices work through shared operations, documented startup
and save semantics are enforced, source files are preserved, P formats fresh each
new boot, I preserves loaded filesystem and saves consistent checkpoints, global
RAM budgeting includes existing caches, and the supported VM matrix has evidence.
Publish the supported-mode/filesystem/power/host-storage matrix and limitations.

Platform gates requiring prototype evidence (not assumptions): Storport control
request and private staging lifetime; shared-budget driver dependency/unload;
native image offline raw access and automount suppression; GPT clone isolation;
filesystem lock plus raw-write fence; catalog flush/commit recovery on the chosen
host filesystem; Fast Startup versus same-session hibernate classification;
shutdown scheduling before host storage teardown. Each has an owner work package
and test above. A failure blocks the corresponding capability until solved; it
must not be hidden by a zero counter or a different storage mode.

## Primary references

- R1: [Virtual miniports and RAM disks](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/storage-virtual-miniport-drivers--when-are-they-appropriate-)
- R2: [GetVirtualDiskPhysicalPath](https://learn.microsoft.com/en-us/windows/win32/api/virtdisk/nf-virtdisk-getvirtualdiskphysicalpath)
- R3: [Attach lifetime flags](https://learn.microsoft.com/en-us/windows/win32/api/virtdisk/ne-virtdisk-attach_virtual_disk_flag)
- R4: [FSCTL_LOCK_VOLUME](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_lock_volume)
- R5: [ReplaceFileW behavior and limitations](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew)
- R6: [System power context distinguishes Fast Startup from hibernate resume](https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/distinguishing-fast-startup-from-wake-from-hibernation)
