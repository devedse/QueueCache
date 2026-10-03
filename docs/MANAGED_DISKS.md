# Managed RAM and VHDX disks

Feature-branch source is implemented; the new native modes are **not yet
VM-qualified**. Installation of signed 0.4.269.1 lost SSH during provider setup;
the live Proxmox console now confirms Windows Recovery and failed automatic
repair. The underlying failure remains undiagnosed. Follow the separate implementation/verification state
in [the tracker](RAM_FIRST_IMPLEMENTATION_TRACKER.md), and the remaining platform
and acceptance gates in [the implementation plan](RAM_DISK_IMPLEMENTATION_PLAN.md).
The examples below describe the implemented command contract, not completed
Windows acceptance evidence.

The desktop **Create disk** flow and `qcache disk` use one LocalSystem Windows
service and Operations API. Disk ownership outlives either frontend. The signed
provider uses the same locked-page allocator and budget authority as ordinary
caches. Filesystem operations identify the owned disk and volume; Windows open
handles may veto stop, save or format.

If creation fails before Windows exposes the owned filesystem, use `disk recover`
to reconcile the exact surviving attachment, then explicit `disk stop` (with
`--discard` for RAM modes). Cleanup verifies ownership and locks any enumerated
volumes; a Windows veto retains the attachment. Recovery never treats a missing
filesystem as consent to format it. An incomplete image creation retains its
image file when detached.

| Mode | Disk capacity / memory | Persistence and startup |
|---|---|---|
| `ram` | Full disk plus metadata/workspace in RAM | Temporary contents; new Windows startup recreates and formats an empty disk if startup is enabled |
| `cached-vhdx` | VHDX capacity; independent cache RAM size | Existing Strict/Fast cache semantics; drain/flush before detach; startup reopens the image |
| `image-in-ram` | Every decoded logical sector in RAM | Flush stays in RAM; Save commits a verified full checkpoint; startup loads the committed image |

Supported activation scope is GPT with one unencrypted NTFS data partition and
optional Microsoft reserved partitions, standalone VHDX, 512/4096-byte sectors,
and local NTFS image/checkpoint hosts. Reparse, compressed/encrypted files,
differencing images, nested managed hosts, volatile Fast host caches, already
attached input and conflicting online GPT identities are refused. Read-only RAM
clones are selectable; backed read-cache-only activation remains explicitly gated.
Power-state support must be qualified separately; do not infer Fast Startup,
sleep or hibernate support from current-boot tests.

Run commands elevated. Choose an unused D–Z letter. New images never overwrite
existing files; `--load` inspects virtual capacity and preserves the filesystem.
Image directories/checkpoints must be protected administrator/SYSTEM directories.

```powershell
qcache disk create --mode ram --size-mib 1024 --letter R --label Scratch --startup
qcache disk create --mode cached-vhdx --size-mib 16384 --new-image C:\Disks\Work.vhdx --budget-mib 2048 --preset Strict --letter W
qcache disk create --mode cached-vhdx --load C:\Disks\Existing.vhdx --budget-mib 1024 --preset Fast --accept-volatile-flush --letter W
qcache disk create --mode image-in-ram --load C:\Disks\Seed.vhdx --checkpoint-directory C:\Disks\Checkpoints --letter R --startup
qcache disk create --mode image-in-ram --size-mib 1024 --new-image C:\Disks\New.vhdx --checkpoint-directory C:\Disks\Checkpoints --letter R
qcache disk list --json
```

Use the returned resource GUID for managed actions. Drive letters and physical
numbers do not identify managed resources. Each command refreshes the recorded
boot/creation identity before mutation. Scripts may provide `--expected-boot`,
`--expected-creation` and `--expected-write` together from a prior status result;
stale erase/discard generations are refused.

For an entirely blank existing VHDX, explicitly select initialization in the
creation dialog, or add `--initialize-raw` to `disk create --load`. The broker
binds consent to the inspected file identity and checks every logical sector is
zero. A RAW partition style alone is insufficient: nonempty, damaged, encrypted
or inaccessible images are refused. Backed mode initializes the selected blank
file. Full-image mode initializes only its RAM copy and commits a new checkpoint
in the selected folder, preserving the original blank image. Initialization is
one-shot; startup and later Start operations never repeat it.

| Command | Contract |
|---|---|
| `disk inspect <image>` | Read detached VHDX identity, virtual size, allocation and geometry |
| `disk list`, `disk status <id>` | Typed definitions, runtime/checkpoint generations, unsaved state and actual errors; no image transfer or drain |
| `disk start <id>` | Start a stopped recipe with the remembered image identity |
| `disk stop <id>` | Backed disk drains/detaches; image-in-RAM follows its save-before-stop policy |
| `disk stop <id> --save` | Verify/commit the frozen image before stopping |
| `disk stop <id> --discard` | Explicit loss of this RAM creation; retain source/checkpoint files |
| `disk flush <id>` | Device/RAM flush; whole-image RAM stays unsaved |
| `disk save <id>` | Unique full verified checkpoint; retain source and previous image |
| `disk export <id> --path <new.vhdx>` | Verified standalone export; startup pointer unchanged |
| `disk export <id> --path <new.vhdx> --commit` | Use verified export as the committed startup image |
| `disk format <id> --accept-erase [--label Name]` | Erase the exact owned NTFS volume; for image-in-RAM this changes only RAM until Save |
| `disk cache <id> --budget-mib N --preset Strict` | Apply/remember backed-image cache settings; all ordinary allocation/drain options are available |
| `disk configure <id> --letter S --label Name [--size-mib N]` | Edit a stopped creation recipe; capacity changes apply only to a fresh pure RAM creation, never an existing image |
| `disk startup <id> --enabled true` | Remember automatic startup without starting/formatting immediately |
| `disk startup <id> --save-before-stop true --save-on-shutdown false` | Image-in-RAM stop policy and optional best-effort preshutdown save |
| `disk remove <id>` | Forget a stopped definition; preserve images and recovery evidence |
| `disk delete-image <id> --path <owned.vhdx> --accept-erase` | Delete an unreferenced owned image by exact file handle identity; refuse imported/referenced/mounted/in-flight files |
| `disk recover <id>` | Reconcile journal and exact surviving native generation; no automatic live reload, format or discard |

`--json` is available on list/status/create and action commands. Progress goes to
stderr; stdout JSON remains parseable. Cancellation before a save commit preserves
the prior pointer. Once commit or Windows formatting begins, await and report its
actual outcome; late cancellation is not rollback. A save may commit while stop
or cleanup fails: inspect status/recovery details before retrying. Candidate and
previous files are retained across failures.

Status JSON includes actual native reservations/counters and `ImageIo`, whose
observation epoch changes on broker restart. Read/write/flush attempts are counted
before image I/O, including failed attempts; completed bytes are separate.
Compare only snapshots in the same epoch. Legacy `ImageTransferAttempts` and
`ImageTransferredBytes` describe completed full-image transactions, not lower
attempts. A missing measurement is unavailable, never an implied zero.

Ordinary cache configuration/pause/removal commands refuse volumes owned by a
managed resource. Use its managed cache settings so runtime and remembered
configuration stay together. Explicit Flush remains available. Read-only broker
inventory/capability observations have a five-second response deadline; image
inspection has thirty seconds. Missing state disables actions and counters.
Mutations still await the real terminal outcome after cancellation.

Physical discovery is `qcache disk physical-list`. Existing `disk eject <volume>`
remains the Windows physical-device eject workflow. Managed Stop is a separate
owned-resource transaction and cannot grant physical eject capability.

The existing installer registers `QueueCache.ManagedDisks` using the installed
qcache executable, delayed automatic start and dependencies on the two native
drivers. It replaces duplicate scheduled startup ownership with the service
coordinator. Update/uninstall preflight refuses live/unresolved resources before
replacing files. Explicitly stop/reconcile resources and retry; the installer
does not discard RAM or delete image data. Native provider/budget ABI mismatches
or a stopped/missing broker make activation unavailable with an error.

Use the maintained opt-in [verification suites](DEVELOPER_VERIFICATION.md):
`managed-provider`, `ram-disk`, `vhdx-backed` and `image-in-ram`. The legacy
`developer lab-disk` helper remains for the multi-volume/raw legacy verification
fixtures; product managed disks do not replace those test layouts.

The maintained runner also provides `managed-broker-restart` and explicit managed
lifecycle prepare/verify/cleanup phases. They retain owned fixtures and byte
oracles across externally controlled startup/resume. They never reboot or kill a
process automatically. Read their scope and failure-preservation instructions
before preparing retained RAM resources; host contract success is not Windows
power or native-driver qualification.
