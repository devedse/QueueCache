# Managed RAM and VHDX disks

All three modes are implemented. On 2026-10-05 signed build 0.4.362.1 passed the
maintained Windows VM suites under Driver Verifier (filter and RAM provider):
`managed-provider`, `ram-disk`, `vhdx-backed`, `image-in-ram` and `managed-cli` at
512-byte and 4096-byte sectors, `managed-broker-restart`, a real Windows restart through
`managed-lifecycle-prepare`/`-verify`/`-cleanup`, and a three-minute mixed-load stress run
with byte-exact integrity checks. Installer upgrade refusal while a managed disk is live,
a desktop UI walkthrough, and a matched 72-case write-performance run (no regression for
ordinary caches) were also observed. Not yet covered: sleep, hibernate and Fast Startup
(postponed; the test VM has no sleep states), a crash during checkpoint commit and
physical hardware (see [known issues](KNOWN_ISSUES.md)). Since 0.4.583.1 (PR #9),
large queued Direct reads are asynchronous; their lifecycle checks pass with and
without Driver Verifier.

Performance on the 4-core test VM (CrystalDiskMark 9.0.3 rows, 1 GiB, Verifier off):

| Disk | Build | SEQ1M Q8 read/write | SEQ1M Q1 read/write | RND4K Q32 read/write | RND4K Q1 read/write |
|---|---|---|---|---|---|
| RAM disk, Direct | 0.4.583.1 | 47.6 / 24.4 GB/s | 26.6 / 16.5 GB/s | 358k / 292k IOPS | 404k / 320k IOPS |
| RAM disk, Standard | 0.4.362.1 | 24 / 25 GB/s | 25 / 17 GB/s | 227k / 193k IOPS | 232k / 194k IOPS |
| Cached VHDX (2 GiB Fast cache) | 0.4.362.1 | 37 / 21 GB/s | 14 / 13 GB/s | 412k / 386k IOPS | 319k / 255k IOPS |

A RAM disk is a real Windows disk, so on the Standard path each request passes the volume,
partition, class and Storport layers. Direct access answers normal reads and writes above
them, which roughly doubles small random I/O. A large read (512 KiB or more) is split
into 256 KiB pieces copied by several processors; when a program queues several, Direct
access hands all pieces to the RAM disk's helper threads and returns, so the program
keeps issuing while other processors copy (adaptive; single reads and several
submitting threads stay on the inline path).

RAM-backed disks (pure RAM and image-in-RAM) have an **access path**. *Direct* (the
default) lets QueueCache's volume filter copy normal file reads and writes straight from
the disk's memory, skipping the Windows layers below the volume; the data still exists
only once. *Standard* sends everything through the full Windows disk stack. Direct access
steps aside automatically, keeping data correct on the standard path, when BitLocker is
used on the volume, when a shadow copy exists or is taken (writes only), when the volume
is write-protected, or when an unrecognized control request reaches the volume; `disk
list` and the desktop card show the reason. Change it with `disk configure <id> --access
standard|direct` while the disk is stopped.

Image hosts (the volume holding a VHDX or checkpoint) must not have a Fast cache: Fast
can lose data the service treats as saved. Use Strict or no cache on that volume.

Image host directories (and their parents) must be owned by Administrators or SYSTEM
and must not give unprivileged accounts delete, permission or ownership rights. Volumes
formatted through the Windows storage service are often owned by NETWORK SERVICE; take
ownership (`icacls X:\ /setowner Administrators`) or choose another volume. Windows'
default root ACL is accepted. A folder created in Explorer directly under a drive
inherits "Authenticated Users: Modify" and is refused; the error names the command
that restricts it to Administrators and SYSTEM:
`icacls "X:\Folder" /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F"`.
Folders that QueueCache creates itself already have that protection.

The desktop **New disk** dialog and `qcache disk` use one LocalSystem Windows
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

A RAM disk or image in RAM that starts with Windows, refused only because RAM is
briefly short at that moment, is retried every 15 s for 10 minutes; its last error
says so meanwhile (#12).

### Saving at shutdown

With save-on-shutdown on, an image in RAM that has changes since its last save is
saved while Windows shuts down. A save copies the whole image and reads it back,
so it takes about twice its size divided by the disk's speed; the disk's details
in the app give the last save's time as the estimate. Windows waits for it for at
most 1 hour: the service gives the shutdown saves up after 3 minutes without
progress, or when they have run for 58 minutes, and records that; its log records
each tenth of the progress. A save that takes longer than that will not finish at
shutdown: save before shutting down (the app's disk details warn when the last save
took an hour or more). Meanwhile Windows shows its own shutdown
screen. **Settings → Windows shutdown → Show detailed Windows startup and
shutdown messages** changes Windows' actual machine policy. It is also offered
with a recommendation when creating either kind of VHDX-based disk. It does not
turn on automatically when a disk is created. The control rereads Windows while
visible and when its window is activated, so changes made outside QueueCache are
shown too. There is no duplicate per-user QueueCache preference.

Detailed messages can show "Shutting down service: QueueCache managed disks."
instead of "Restarting". This identifies a stopping service; it does not report
whether an image is saved or show a percentage. Windows chooses the message and
can leave an earlier message on screen. The setting applies to the whole machine's
startup, sign-in, sign-out and shutdown screens, including other services. If
Windows suppresses status messages, QueueCache reports that override and leaves
that separate policy untouched. Read/write failures are displayed; an unreadable
state is unavailable rather than off. The Windows policy is `VerboseStatus` under
`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System`; the separate
`DisableStatusMessages=1` policy suppresses these messages even when detailed
status is enabled ([Microsoft's policy documentation](https://learn.microsoft.com/en-us/troubleshoot/windows-server/performance/enable-verbose-startup-shutdown-logon-logoff-status-messages)).
The app does not hold the shutdown to show progress: the VM experiment found
Windows cancelling an application-held shutdown after about a minute unless the
user intervened. This is separate from the service's preshutdown save and its
one-hour maximum. A power
cut, holding the power button or a forced shutdown can still stop a save. An
unfinished save is never used: the image starts from its previous save. After the
restart the app says whether the shutdown save finished; `disk status` shows it
as `LastShutdownSave` (#22).

The shutdown-percentage investigation is complete for [#22](https://github.com/devedse/QueueCache/issues/22):
Windows accepted service display-name changes, but did not refresh an already
visible QueueCache label in the VM probe. Other runs retained Passport Container's
name after that service had stopped. No reliable percentage feature was established;
QueueCache does not rename its service to simulate one. The detailed evidence is
recorded in #22 and [known issues](KNOWN_ISSUES.md).

Faster saves are tracked separately in [#28](https://github.com/devedse/QueueCache/issues/28).
Each committed save is still a complete standalone VHDX and is read back in full
before QueueCache switches its startup pointer. An eligible older owned checkpoint
can now be reused: QueueCache fingerprints the frozen RAM image in 1 MiB regions
and writes only changed regions directly into the VHDX payload, without mounting
that candidate read-write. The newest committed checkpoint stays untouched during
the attempt. After commit, it becomes the previous checkpoint; the consumed older
file now contains the new save, so checkpoint filenames do not form an immutable
history. Imported/original images and explicit export destinations are not reused.

Reuse requires an intact fingerprint sidecar, matching file identity/size/time,
valid headers and allocation metadata, an empty VHDX log and fully allocated payload
blocks. Missing/stale hints or unsupported layouts take the complete-new-image path
before payload mutation. All logical sectors are verified through a separate
read-only Windows VHDX view, including regions skipped during writing. An interrupted
candidate is not a committed checkpoint; its predecessor reference and reuse hint
are consumed before writes, while the current checkpoint remains available.

The sidecar is named `<checkpoint>.qcache-chunks`; removing it is safe and makes
that candidate ineligible for reuse. The conservative free-space check still keeps
worst-case room for a complete image plus metadata reserve, even for reuse, because
host sparse/COW allocation can grow on overwrite. `LastSave.Bytes` remains the full
logical image size for time estimates; `PayloadBytes`, `ContainerHeaderBytes` and
`ReusedCandidate` describe the actual save strategy. Header bytes exclude host
filesystem metadata. See [#28](https://github.com/devedse/QueueCache/issues/28) for
qualification and measured scope. General automatic checkpoint retention and
background saving without pausing writes remain separate features.

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
| `disk save <id>` | Complete verified checkpoint; may reuse an older owned candidate while retaining the newest checkpoint until commit |
| `disk export <id> --path <new.vhdx>` | Verified standalone export; startup pointer unchanged |
| `disk export <id> --path <new.vhdx> --commit` | Use verified export as the committed startup image |
| `disk format <id> --accept-erase [--label Name]` | Erase the exact owned NTFS volume; for image-in-RAM this changes only RAM until Save |
| `disk cache <id> --budget-mib N --preset Strict` | Apply/remember backed-image cache settings; all ordinary allocation/drain options are available |
| `disk configure <id> --letter S --label Name [--size-mib N]` | Edit a stopped creation recipe; capacity changes apply only to a fresh pure RAM creation, never an existing image |
| `disk startup <id> --enabled true` | Remember automatic startup without starting/formatting immediately |
| `disk startup <id> --save-before-stop true --save-on-shutdown false` | Image-in-RAM stop policy and optional save at shutdown ([below](#saving-at-shutdown)) |
| `disk remove <id>` | Forget a stopped definition; preserve images and recovery evidence |
| `disk delete-image <id> --path <owned.vhdx> --accept-erase` | Delete an unreferenced owned image by exact file handle identity; refuse imported/referenced/mounted/in-flight files |
| `disk recover <id>` | Reconcile journal and exact surviving native generation; no automatic live reload, format or discard |
| `disk timing <id> --enabled true` | Time every read and write of a running RAM disk, on the standard path and Direct access, from zero; `false` stops. Off at every creation |

`--json` is available on list/status/create and action commands. Progress goes to
stderr; stdout JSON remains parseable. Cancellation before a save commit preserves
the prior pointer. Once commit or Windows formatting begins, await and report its
actual outcome; late cancellation is not rollback. A save may commit while stop
or cleanup fails: inspect status/recovery details before retrying. Candidate and
previous files are retained across failures.

Status JSON includes actual native reservations/counters and `ImageIo`, whose
observation epoch changes on broker restart. Read/write/flush attempts are counted
before image I/O, including failed attempts; completed bytes are separate.
Compare only snapshots in the same epoch. RAM-backed disks also report
`Statistics` (requests on the provider's standard path; Direct requests are in
`Direct`) and, while timing is on, timed reads and writes with total and longest
ticks. Every counter starts at zero with each creation. An image in RAM records
`LastSave`: duration, bytes copied and the disk's written total when frozen, so
"written since the last save" is an upper bound (rewrites count each time). Legacy `ImageTransferAttempts` and
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

Use the maintained opt-in verification suites ([testing](TESTING.md)):
`managed-provider`, `ram-disk`, `vhdx-backed`, `image-in-ram` and `checkpoint-reuse`, plus
`ram-read-reference`, `ram-read-async` and `ram-read-async-lifecycle` for reads. The legacy
`developer lab-disk` helper remains for the multi-volume/raw legacy verification
fixtures; product managed disks do not replace those test layouts.

The maintained runner also provides `managed-broker-restart` and explicit managed
lifecycle prepare/verify/cleanup phases. They retain owned fixtures and byte
oracles across externally controlled startup/resume. They never reboot or kill a
process automatically. Read their scope and failure-preservation instructions
before preparing retained RAM resources; host contract success is not Windows
power or native-driver qualification.
