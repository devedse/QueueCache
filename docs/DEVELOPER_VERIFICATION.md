# Current-boot verification

One foreground CLI, one shared C# runner, one unique output directory. No separate
test application, installer, scheduled task or `--detach` option is needed.

```powershell
# Elevated terminal on the test machine
qcache developer verify Q: --suite quick
qcache developer verify Q: --suite paging-coherence --output C:\QueueCache-Results
qcache developer verify Q: --suite flush-interference --repeats 2 --diskspd C:\Tools\DiskSpd\diskspd.exe --output C:\QueueCache-Results
qcache developer verify Q: --suite full --diskspd C:\Tools\DiskSpd\diskspd.exe --output C:\QueueCache-Results
# Volume-filter suites on the lab VHDX (see "Volume-filter lab disk" below)
qcache developer lab-disk create C:\QueueCache-Lab\VolumeLab.vhdx
qcache developer verify V: --suite volumes --output C:\QueueCache-Results
qcache developer verify V: --suite trim-cache --output C:\QueueCache-Results
```

The target is always a volume (`Q:`): QueueCache filters volumes, and every volume
has its own cache. Every suite that changes cache settings first requires the
supported registration (`qcache developer driver registration`): QueueCache as the
last Volume-class upper filter, not on the disk class, every volume covered, no
lab `DiagnosticMode`.

Before qualification, run `qcache developer driver loaded`. It observes loaded
QueueCache module paths through PSAPI and reports current file hashes. Compare
the filter's immutable loaded filename and hash with the intended signed artifact;
SCM registration alone is insufficient. An unavailable observation, including
Windows 11 24H2 returning null addresses without SeDebugPrivilege, is not proof
that a driver is absent. The process temporarily enables its eligible debug
privilege and restores it after observation. The same snapshot is recorded in
each run's provenance; file hashes are not hashes of kernel memory.

`--output` is a parent directory (default `.`). Each invocation creates
`QueueCache-Verify-<UTC>-<GUID>` beneath it and prints the absolute path. Workload
files must live on the selected disk; their distinct retained directory is recorded
in `workloads.json` or the integrity worker's report/log. Reports should live on a
different disk so telemetry writes do not contaminate the workload.

## Suites (plan version 89)

Plan 89 stops taking read-only VHDX views offline: changing the disk attributes of a
read-only attachment made its detach wait out a 180-second Windows timeout. The
provider's isolated logical read-back and product image imports/verification now use a
read-only, letterless attachment without attribute changes. Writable staging disks are
still taken offline. Score workloads are unchanged.

Plan 88 corrects the `managed-provider` read-only check. The provider answers a
write to a read-only RAM disk with DATA PROTECT / WRITE PROTECTED (sense 7/27h/00h),
which is correct SCSI, but Windows classpnp maps that ASC to STATUS_IO_DEVICE_ERROR
for raw writes (only other DATA PROTECT codes become STATUS_MEDIA_WRITE_PROTECTED).
The check now requires IOCTL_DISK_IS_WRITABLE to succeed before and to fail with
ERROR_WRITE_PROTECT while read-only, the raw write to fail with ERROR_WRITE_PROTECT or
ERROR_IO_DEVICE, exactly one new provider error, and unchanged sectors/generation.

Plan 87 requires `managed-provider` to prove that the native reservation includes
all x64 MDL/PFN/slab metadata and its bounded transfer workspace, and that the
existing authority increases by exactly that reservation. Underreported native
metadata fails even if two incorrect counters agree. Allocation failure and final
removal must still restore the exact previous authority total. Score workloads
remain unchanged; shared allocator changes require the full matched 72-case run.

Plan 86 extends `image-in-ram` at both sector sizes with actual open-file Save
vetoes and an export destination collision. The preceding checkpoint/pointer and
exact live dirty RAM must survive, the existing export's container hash must stay
unchanged, and a subsequent filesystem write must succeed. Raw before/after
records and errors remain in the same run. Host cancellation contracts separately
require a cancellation result before commit; cleanup/reporting failures must
remain failures requiring recovery. These additions do not change score workloads.

Plan 81 adds opt-in managed lifecycle phases. `managed-broker-restart` prepares
five 64 MiB fixtures, stops/starts only QueueCache's named SCM service, then proves
the same native creations/content survive and an intentionally stopped automatic
recipe stays stopped. It requires an empty managed catalog, a Strict/disabled
image host, trusted result directories and no competing workloads. Service PID
and creation time are retained as process ownership evidence; they do not classify
Windows startup.

`managed-lifecycle-prepare` retains those fixtures and a durable
`managed-lifecycle-manifest.json` in its exact run directory. The fixtures cover
automatic pure RAM, manual pure RAM, stopped automatic pure RAM, Strict backed
VHDX and a full-RAM image with a committed file plus a later unsaved file. The
manifest stores native identities, startup epoch, source/checkpoint pointers and
independent byte hashes on another physical disk. Preparation success is not a
power/lifecycle acceptance verdict. Do not reboot before its `FINISHED.txt` and
complete manifest exist.

Perform the intended external transition, then run `managed-lifecycle-verify`
with the exact prior `--managed-oracle` and declared `--managed-transition`:
`Restart`, `ColdStart`, `FastStartup`, `Sleep`, `Hibernate`, `BrokerRestart` or
`BrokerCrash`. The runner never invokes shutdown, sleep, hibernate, reset or process
kill. Retain hypervisor/Windows event/console evidence of the actual transition
separately; the argument is a declaration, not proof that the transition happened.
Native startup must change for a new startup and remain unchanged for resume or
broker restart/crash. New-startup RAM must discard the unsaved session file while
the committed full-image file and Strict backed bytes survive. Same-session RAM
must keep its native creation/content. A stopped automatic recipe stays stopped
on broker restart/resume but starts empty at a new Windows startup.

After all checks pass, only manifest-owned fixtures are stopped/forgotten; all
images/evidence remain. Failure preserves resources and raw trace instead of
automatic destructive recovery. Explicit `managed-lifecycle-cleanup` consumes the
completed manifest and fresh exact identities. If preparation failed before a
complete manifest, inspect its `.preparing.json`, raw trace and `disk list`; use
the product Stop/recover commands for those recorded owned resources. Independent
cache reservations/profiles must remain stable across the transition; a global
reservation mismatch is reported, never repaired by changing driver defaults.
These cases remain outside `full`. Crash during checkpoint commit, physical
persistence, installer, unavailable power capabilities and injected native faults
remain independent qualification gates.

```powershell
qcache developer verify T: --suite managed-broker-restart --output C:\QueueCache-Results
qcache developer verify T: --suite managed-lifecycle-prepare --output C:\QueueCache-Results
# After the externally controlled transition; use the exact printed manifest:
qcache developer verify T: --suite managed-lifecycle-verify --managed-oracle C:\QueueCache-Results\QueueCache-Verify-<id>\managed-lifecycle-manifest.json --managed-transition Restart --output C:\QueueCache-Results
qcache developer verify T: --suite managed-lifecycle-cleanup --managed-oracle C:\QueueCache-Results\QueueCache-Verify-<id>\managed-lifecycle-manifest.json --output C:\QueueCache-Results
```

Plan 80 extends the image product cases with uniquely owned blank existing VHDX
fixtures and explicit initialization. Backed mode initializes that owned file;
full-image mode preserves it and commits a separate initial checkpoint. Normal
RAM runtime must leave the resource's logical-image read/write/flush attempt
counters unchanged in the same observation epoch. Missing counters or an epoch
change fail that check; completed-transfer totals alone are insufficient. These
counters include image-sector access and explicit candidate host flushes, not
container metadata inspection or unrelated processes. Existing score workloads
are unchanged.

Plan 82 runs the provider and all three product suites with both 512-byte and
4096-byte logical sectors. Each geometry uses distinct owned resources and raw
evidence; provider checks additionally require native invalid-range rejection,
an actual physical write-protection failure with unchanged bytes/generation,
and refusal of redundant cache allocation without changing shared reservations.
Provider geometry evidence has a retained index with an explicit completion flag;
a failure in the first geometry cannot qualify the second. Existing score
workloads, case IDs, deadlines and the 72-case performance matrix are unchanged.

Plan 83 additionally requires file-relative TRIM on each owned native RAM fixture:
the provider's TRIM count and write generation must advance without errors,
the discarded middle range must read as zero, adjacent guards must remain intact,
and a flushed rewrite must match exactly. Unsupported TRIM fails this provider
contract; it is not a hardware-dependent SKIP. Primary test failures and separate
teardown failures are both retained in native/product evidence. Existing score
workloads and preparation/restoration deadlines are unchanged.

Plan 84 adds opt-in `managed-cli`, executing the same qcache binary's product
`disk` commands for all three modes and both sector geometries. Unique 64 MiB
fixtures cover create/list/status, startup settings/recovery, flush, image
save/export/inspect/owned-image deletion, backed-cache settings, Windows stop
veto, stale erase refusal, explicit format, stop/configure/start and definition
removal with source retention. Every child has immutable command/PID/exit/stdout/
stderr evidence next to the worker reply; `*.cli.json` records ownership and
separate primary/cleanup outcomes. Exact requested recipe and exclusion of
preexisting resource IDs guard cleanup even if CLI output fails after creation.
Fallback cleanup uses the broker independently and preserves images. The suite
is excluded from `full`, does not reboot, and does not use DiskSpd; existing
score workloads are unchanged. Image modes also reopen an existing image without
formatting, infer its geometry, verify persisted bytes and retain the imported
source on removal; full-image RAM uses the CLI read-only load option.

```powershell
qcache developer verify T: --suite managed-cli --output C:\QueueCache-Results
```

Plan 85 requires native allocation rollback in `managed-provider` for both
geometries. Separate unique new creations fail after 1, 8 and 16 actual 4 MiB
slabs. Request-local injection leaves no persistent hook and cannot target an
existing disk. A native proof counter must advance exactly once and identify
the requested resource/boundary; ordinary out-of-memory rejection before that
boundary cannot pass. The complete prior native creation set and shared
reservation must remain unchanged. Each subsequent normal creation must still
provide zeroed writable storage. Raw snapshots retain the proof and budget.
Existing write-performance workloads and deadlines remain unchanged.

Plan 79 adds opt-in product broker suites `ram-disk`, `vhdx-backed` and
`image-in-ram`. They create uniquely owned 64 MiB GPT/NTFS fixtures, preserve
images and transaction/cleanup evidence, and explicitly stop/forget their
resources after the case. They require the matching running managed service
and native modules. Pure RAM covers Windows open-file veto and fresh recreation;
backed VHDX covers dynamic/fixed images, independent Strict/Fast cache and
flush/detach/reopen byte checks. Whole-image RAM covers full verified checkpoint,
export without pointer change, runtime writes/flush/read with the owned source
temporarily unavailable, dirty generation, discard and committed-image reload.
These cases format only newly created owned devices or explicitly selected blank
fixture images after complete zero-sector validation; ordinary physical targets
are not formatted. Evidence is retained in `*.managed.json` next to worker
replies. They are excluded from `full`. Cross-boot, broker-crash, physical power,
installer and injected native-failure coverage remain separate required gates;
these current-boot cases do not qualify those paths. Existing 72-case score
contracts and historical raw output are unchanged.

```powershell
qcache developer verify T: --suite ram-disk --output C:\QueueCache-Results
qcache developer verify T: --suite vhdx-backed --output C:\QueueCache-Results
qcache developer verify T: --suite image-in-ram --output C:\QueueCache-Results
```

Plan 78 adds opt-in `managed-provider`. Use a disposable, clean non-OS host
volume with the matching signed provider and budget driver loaded. The suite
creates only uniquely owned RAM/VHDX fixtures, proves private-sector transfer,
shared budget accounting, no-letter/offline image raw access, NTFS publication,
open-file lock veto, freeze/thaw and exact reservation release. Native snapshots
and cleanup failures are retained next to the worker reply. It is excluded from
`full`; an incomplete native proof does not qualify product activation. Existing
performance workload/score contracts are unchanged.

Plan 73 requires per-volume lower write/flush attempt evidence, completed cache
disable and filesystem flush before orderly-removal acceptance. Plan 72 added
verified topology and structured present-disk veto restoration to the plan-71 opt-in `disk-removal` suite. Its first case covers exactly one
unconfigured disposable volume: an 8 MiB Fast/Deferred pending-write oracle,
Windows safe eject, and operator live reconnect on the same bus, disk number and
drive letter. It is excluded from `full`. Surprise removal, multiple volumes,
letter/number changes, fault races and repeated-cycle qualification remain open.

```powershell
qcache developer verify W: --suite disk-removal --budget-mib 256 --disposable-instance '<exact disk PnP ID>' --disposable-bytes 8589934592 --output C:\QueueCache-Results
```

The output directory must already exist on another physical disk. Obtain the
identity with `qcache disk eject W: --preview` and the physical size with
`Get-Disk`; confirm that it is the dedicated disposable disk. The runner refuses
boot/system/special-file paths, saved profiles, existing caches and ambiguous
layouts. A successful Windows API return without observed device disappearance
is INCOMPLETE. After observed removal, `removal-ready.json` records the exact
run/case/disk/volume identities and an acknowledgement template. Reconnect that
disk externally, then atomically write the template as `reconnect-ack.json` in
that run directory. The foreground runner waits at most 15 minutes, validates the
acknowledgement, requires a fresh empty cache lifetime and compares all oracle
bytes before restoration. An unknown eject outcome (including a worker failure
or presence-query error), stale acknowledgement or incomplete reconnect
defers restoration and reports RESTORATION_FAILED; preserve evidence, reconnect
the recorded target and use `verify-recover` after owned workers have stopped.
The runner never invokes a hypervisor, reboots, formats or repairs the target.

Plan 70, from the first FAT32 and ReFS runs: `volume-snapshot` is SKIP when Windows
does not take shadow copies of the file system (FAT32/exFAT: Win32_ShadowCopy.Create
4); file-level TRIM accepts the file system reporting more than one processed range
(ReFS reports 2 for one); on ReFS `policies/foreground-background` records the share
of requests on the caller's thread instead of requiring 90% (ReFS splits requests,
KNOWN_ISSUES) and `ordering-faults` remounts the volume afterwards (ReFS takes a volume
offline after the injected write failures, as for real disk errors).

Plan 69: the target volume may use any file system Windows mounts (NTFS, ReFS,
FAT32, exFAT), not only NTFS. File offsets of test ranges and page files now add the
volume's cluster-area offset (`FSCTL_GET_RETRIEVAL_POINTER_BASE`: zero on NTFS and
ReFS, after the FAT tables on FAT32/exFAT); without it gated cases would target the
wrong range on FAT. `volume-resize` stays NTFS-only (Windows cannot shrink ReFS or
FAT). `qcache developer lab-disk create --file-system NTFS|ReFS|FAT32|exFAT` builds
the lab volumes with that file system (ReFS as a Dev Drive, 50 GiB each).

Plan 68: `volume-snapshot` records the cache every 200 ms while the shadow copy is
created (queue, active request phase and age, pending/in-flight bytes, last barrier
control), and a shadow copy that Windows cannot create on the cached volume is a FAIL
with those samples (it was SKIP in plan 67; an uncached volume on the VM snapshots
fine, so a failure means the cache interfered). `volume-shared-disk` checks the bytes
the flush of the first volume wrote instead of requiring zero pending bytes.

Plan 67 adds `volumes/volume-snapshot`. Shadow copies (System Restore, backup and
imaging tools) are taken by `volsnap`, below the cache. With 32 MiB pending in a
256 MiB Fast/Deferred cache on the target, a client-accessible shadow copy is
created (`Win32_ShadowCopy.Create`); the file read from the snapshot device must be
exact and the cache must have drained the pending data before the snapshot. The
shadow copy is deleted afterwards.

Plan 66 adds `volumes/volume-resize` (found on the VM: extending a cached volume
beyond the length the driver had learned failed with Invalid Parameter; fixed by
re-reading the length). On the lab disk only (a volume labelled `QC-Lab-2` on the
same virtual disk as the target, otherwise SKIP): with 32 MiB pending in a 128 MiB
Fast/Deferred cache, the volume is shrunk by 1 GiB and extended back to its original
size. The driver's length must follow both changes (the extend goes beyond the length
it re-read after the shrink), the file system must grow again, a file written
before and one written after must read back exact from the disk, and no error may
be recorded. The volume is left at its original size without a cache task.

Plan 65 changes how restoration proves the drain. Restoration flushes the file
system and the cache, then disables the cache; that disabled state must have
nothing pending or in flight. Afterwards the original settings are re-applied, and
a cache that is enabled again may already hold new writes from Windows (on Q:, 8 KiB
of NTFS metadata arrived within a second in a plan-64 run and failed restoration
although everything had drained). Pending bytes must still be zero when the
restored cache stays disabled. Budget, preset, options, timing, error count,
instance and saved profiles are compared exactly, as before.

Plan 64 adds two suites for the volume filter, both outside `full` because they need
the lab disk:

- `volumes` (three cases). `volume-registration`: the registration is the supported
  one, the driver's device length equals the volume length, the physical disk
  rejects a QueueCache request (no disk-level filter), and the filter answers on
  every lettered fixed volume. `volume-raw-disk-commands`: with 16 MiB pending
  (Fast, Deferred, one-hour age) and 8 MiB clean, a storage device-descriptor
  query, a drive-geometry query and a SCSI INQUIRY pass-through (SKIP-noted when the
  storage driver refuses pass-through) go to `\\.\PhysicalDriveN`; nothing may be
  drained, flushed or evicted and no error recorded; both files must then read
  back exact from the disk. `volume-shared-disk`: a second NTFS volume on the same
  disk (without a cache task or saved profile, otherwise SKIP) gets its own 128 MiB
  cache beside the target's 256 MiB; each accepts only its own 32 MiB file,
  flushing one leaves the other's pending data undrained, both read back exact; a
  version-2 profile saved for the second volume leaves the target's profile alone
  and is removed again. The second volume is left without a cache task.
- `trim-cache`: file-level TRIM with the cache running (Fast, Deferred). Pending
  writes in the trimmed range are dropped (discarded counter, nothing drained),
  clean copies are released, and a TRIM during an in-flight drain (300 ms lab delay)
  waits for the issued write; untrimmed guards and a full rewrite read back exact
  from the disk. A disk that rejects TRIM makes the case SKIP.

Both suites may finish `COMPLETED_WITH_SKIPS`; a skipped case is not a pass. Plan
64 also makes every cache-changing suite refuse an unsupported registration at
capture.

### Volume-filter lab disk

`qcache developer lab-disk create <path.vhdx> [--letters V,W,X] [--size-gib 24]`
creates a new expandable VHDX (diskpart), attaches it and partitions only that
disk: two 8 GiB NTFS volumes (`QC-Lab-1`, `QC-Lab-2`) and one unformatted volume
with the rest. It refuses an existing file and letters in use. Windows does not
reattach a VHDX after a restart: run `lab-disk attach <path.vhdx>`. `lab-disk detach`
refuses while any of its volumes has a cache task. The command prints the exact
`write-tests` arguments for the unformatted volume. A VHDX accepts TRIM, so the
`trim-cache` suite runs there even where the VM's own disks cannot TRIM (known
issues). Keep the VHDX file on a volume without a cache task (C: on the lab VM).

Plan 63 adds `policies/settings-rollback/lab-fault-6` and `-7` (N1 on the real
driver). From 64 MiB Fast/Idle with a file cached, the lab fault makes the next
cache allocation fail once, and Apply of 128 MiB Strict/Fixed/Balanced must fail
with "The previous settings were restored"; the driver must then be enabled,
healthy, 64 MiB Fast with the previous options, the file must read back intact,
and the same change must apply once the fault is spent. The fault is disarmed in
all cases.

Plan 62 adds `policies/read-miss-isolation`: a 16 MiB file is written, flushed
and its clean cached copy dropped, then read in 1 MiB unbuffered misses while
another thread keeps overwriting that read's buffer. A separate handle re-reads
each MiB; every re-read must match the file and, with Diagnostics V13, all 4,096
blocks must have been kept and re-read from RAM (otherwise isolation was not
exercised and the check fails).

Plan 61 adds `system-paging/program-files-match-disk` to
`system-paging-recognition` (and so to every restart-soak cycle). It drops the
cache's clean data, hashes the DLLs next to `qcache` with unbuffered reads (from
the disk, as nothing of them is dirty), applies the bounded memory pressure,
starts `qcache --version` ten times, then hashes the files again through the
normal cached read path. Every file must match and every process must exit
normally. On 0.4.162.1 with a 512 MiB C: cache this failed immediately (11 DLLs
differed, `qcache` crashed); see KNOWN_ISSUES. With Diagnostics V17 the PASS text
reports paging reads whose buffer repeated a physical page.

Plan 60 replaces `paging-coherence/mapped-read-retained` with
`mapped-read-not-kept`: the mapped read and a later unbuffered read must still
return the file's bytes, and the paging read fill counter must not move. Paging
read misses are no longer kept (KNOWN_ISSUES: programs on a cached C: read wrong
data). With Diagnostics V17 the PASS text reports how many paging reads repeated
a physical page during the check.

Plan 59 adds `policies/parallel-copies` (256 MiB Fast, Eager, parallelism 2): for
20 s four threads each rewrite their own 8 MiB file in 1 MiB blocks and read each
block straight back, while two threads read random blocks of those files. Every
read-back and every concurrent read must return one whole written version, the
cache must not fault, and after Disable the disk must hold each block's last
version. With Diagnostics V16 the offloaded read and write copy counts must both
rise (large requests from several threads go to the worker, which hands their
copies to the offloaded-request threads); older drivers report them unavailable.

Plan 58 adds a caller-thread check to `policies`' `foreground-background` case.
With Diagnostics V14, at least 90% of its serialized 64 KiB write/read pairs
(fitting writes and RAM read hits on an otherwise idle disk) must be counted as
served on the calling thread (`CallerPath.Reads + Writes`); the PASS text reports
served and declined counts. Every 1024th candidate is deliberately sent to the
request worker, and other disk activity can only add to the device-wide counts.
Older drivers report the counters as unavailable and the check is omitted. The
existing persisted-bytes oracle after Disable still checks every block.

Plan 57 corrects `policies`' 60-second hot-set check. It required zero lower
reads on the whole device, which background activity (indexing, scanning of the
day's test files) broke on 0.4.148.1 (`...-165751-aa5944de...`, and 1 of 3 reruns:
every owned read hit RAM, 7 unrelated lower reads, 0 evictions). The owned blocks
are dirty or retained and dirty data is never evicted, so unrelated lower reads
are now tolerated only when no block was evicted during the window; hit bytes must
still cover every owned read, and the failure message reports every counter.

Plan 56 adds `paging-coherence/mapped-read-retained`: an 8 MiB file written while
caching is released is read through a mapping (application paging reads), then
re-read unbuffered. Diagnostics V13 `PagingReadFills` must rise by at least 90% of
its blocks and the re-read must hit at least 90% of its bytes in RAM, with
matching bytes. Older drivers report SKIP. Plan 56 also changes the default drain
parallelism for new tasks from 1 to 2 (T050 re-measurement, 2026-09-27).

Plan 55 adds `system-paging/paging-file-io-bypasses-worker` to
`system-paging-recognition`: with Diagnostics V12 every recognised paging-file
request must also be counted as forwarded straight to the disk from dispatch
(`PagingFileBypasses`). With older drivers the check is omitted.

Plan 54 corrects plan 53's map-failure evidence: the fallback is recorded as a
paging map failure plus a forwarded original paging write (V8
`LowerSources.PagingForwardedWrites`), not as a classification-time direct
write. The plan-53 run (`...-121443-e0d0bc4a...` on 0.4.139.1) recorded the map
failure and a successful save but stopped on that wrong counter.

Plan 53 (T053) adds `ordering-faults/paging-write-map-failure`: lab fault 11
makes the next paging write's buffer unmappable. The write must fall back to the
ordered direct path (paging map-failure and forwarded paging-write counters rise), the
mapped save must succeed, the cache must stay healthy and the block must match
after release. Fault 11 only affects paging writes, whose fallback cannot fail
them, so an unrelated paging write that takes it is harmless.

Plan 52 opens `release-under-load`'s final read-back through a shared read-only
handle; plan 51's exclusive open failed with a sharing violation while the
writer handle was still open (`...-115059-1b0c9b68...`, INCOMPLETE, no cache
fault).

Plan 51 (T053) adds `ordering-faults/release-under-load`: an unbuffered writer
rewrites a 32 MiB owned file in passes of seeded 64 KiB blocks while a second
read-only handle checks random blocks, and the cache is repeatedly applied
(alternating Eager/Idle and write retention), flushed, disabled and released.
Every concurrent read must be a whole previous-or-current version, the cache must
not fault, and the released file must equal the last pass. Fewer than 10
lifecycle cycles during the load is SKIP (unexercised). Physical device removal
still needs a hypervisor hot-unplug and is not covered.

Plan 50 fixes a race in plan 48/49's allocation stages: they run Deferred, so
NTFS metadata admitted after the recovery Retry legitimately stayed dirty and
failed the old zero-dirty check (plan-49 run `...-065833-6298f410...` on
0.4.133.1 stopped there). Their recovery now requires no fault, nothing in
flight and a successful second flush; the owned bytes are proven after release.

Plan 49 (T083) adds `ordering-faults/direct-paging-write-failure`: lab fault 10
reports the next direct paging write inside a force-direct range as failed after
it reached the disk. The mapped flush must fail, the cache must stay healthy
(no fault), and a later mapped save must succeed and match after release. The
forced write really landed, so this does not prove that the failed range's clean
view was dropped; source review covers that invalidation.

Plan 48 (T053) adds two `ordering-faults` stages for lower IRP allocation failure,
using new driver lab faults (Diagnostics V11 `LowerAllocationRetries`):

- `drain-allocation-retry`: lab fault 8 fails three lower IRP builds on one drain
  batch. The flush must succeed with at least three recorded retries and no fault;
  the owned 1 MiB must match after release.
- `drain-allocation-exhaustion-faults`: lab fault 9 fails every build until
  cleared. After 250 attempts (about 5 s; 7.9 s under Driver Verifier) the flush must fail with
  `STATUS_INSUFFICIENT_RESOURCES`, the cache must fault with the owned version
  still dirty, and Retry must drain it to matching bytes.

Plan 47 adds the guarded operator suite `system-app-session` (T080/T086). It needs
a person at the VM desktop; the runner never drives Paint or Photos itself.

```powershell
qcache developer verify C: --suite system-app-session --budget-mib 1024 --recoverable-vm --system-instance <exact-PnP-ID> --system-bytes <exact-bytes> --output Q:\QueueCache-App-Results
# Follow <run>\operator-instructions.txt, then create <run>\operator-done.
```

Plan 46: `system-paging-recognition` reads an in-use paging file's layout from its
NTFS file record (file ID from the directory listing, `FSCTL_GET_NTFS_FILE_RECORD`
on the volume). NTFS refuses every open of such a file, even attribute-only
(plan 45 failed with error 32). A layout continued in an attribute list is
refused rather than reported partially.

Plan 45: `paging-coherence` writes each owned 8 MiB file completely before
configuring the cache, as `ordering-faults` does. Since T085 the NTFS zero-fill of
a length-only file is admitted and drained ahead of the owned sparse write (a
256 KiB batch was in flight when plan 44 timed out on 0.4.111.1).

Plan 44: `paging-coherence`'s observed overlap stage accepts admitted 4 KiB NTFS
metadata pages in flight beside the owned 1,536-byte sparse write (the in-flight
remainder modulo 4 KiB must be 1,536). Since T085 that metadata write-back is
admitted, so the plan-43 exact-equality wait timed out on 0.4.111.1.

Plan 43: `app-write-profile` waits until each file has arrived counting an admitted
paging write once. It is both accepted and a paging write; plan 42 counted it
twice and closed the buffered-close window at half the file.

Plan 42 (T085, see [design](T085_APPLICATION_CACHING_DESIGN.md)):
- `app-write-profile` now also requires every application write mode to admit at
  least 90% of its bytes to RAM, and a 1 GiB Configure to add at most 10% of the
  budget to kernel nonpaged pool (page-backed cache memory). Older drivers
  report SKIP.
- `paging-coherence` and `ordering-faults` mark their owned block force-direct
  where they verify the direct paging path, because ordinary mapped writes are
  now admitted.
- New guarded `system-paging-recognition` (C:, same opt-in arguments as
  `system-preflight`): no cache configuration, workload file or raw write. It
  sends observe-only paging-file extents, applies bounded private-memory pressure
  (capped by commit headroom), and requires zero reference misses. If no
  paging-file I/O occurs, the recognition check is SKIP (unexercised).
Plan 41 added `app-write-profile` (measurement). Plan 40 added `ordering-faults`.

Plan 39

Plan 39 adds three stages to `paging-coherence` (needs the plan-39 driver, Diagnostics V9):

- `gated-submitted-old-before-direct` (T083): a one-shot driver lab gate holds
  the owned 4 KiB drain in flight *after* its real lower write completed. A mapped
  overwrite is then flushed. The case passes only if the gate's per-disk sequence
  is old submit < old lower completion < direct paging write waiting < old
  retirement < direct submission < direct completion, with newest bytes. Any
  missing or out-of-order event fails.
- `later-cached-after-direct`: a cached write after that direct write stays the
  newest view and released-cache media.
- `capacity-blocked-page-in` (T082): with a 16 MiB cache and a bounded 200 ms
  drain delay, a 32 MiB writer becomes capacity-blocked. Four page faults on an
  uncached file must complete while the writer is still blocked and appear as
  V8 offloaded paging-read completions. Otherwise it is SKIP (unproven), and the
  run is incomplete.

Gate safety: the driver refuses `LabGate` on a disk hosting any paging,
hibernation or dump path. It is one-shot, limited to 1 MiB and a 5-second hold,
disarmed in the scenario's `finally` and again by the maintained restore job.
Fault, short-completion and cancellation orders remain open.

Plan 38 (unchanged below)

Plan 38 changes admission verdicts (T084). Byte correctness and the
zero-lower-I/O assertion are separate check IDs (`.../admission-bytes`,
`.../zero-lower-io`, `.../first-write-zero-lower-io`,
`.../first-fitting-write-zero-lower-io`). With Diagnostics V8 (plan-38 driver)
each lower attempt carries its issuing path. QueueCache-generated writes or
flushes in the admission window fail. A forwarded non-paging request is SKIP
(unproven), so the run is incomplete, not passed. Forwarded paging-marked
requests are reported but cannot be the owned unbuffered request. With an older
driver any lower attempt is SKIP; equal aggregate counts never produce PASS.
Pressure trigger/watermark timing counts generated (drainer) writes when V8 is
present. V8 also reports offloaded paging reads (T082). Earlier plan results
keep their recorded scope.

Review correction, planning revision 5: the plan-37 `InFlightBytes` observation
occurs after selection/pinning and can precede lower submission; the synthetic
delay is before `LowerIo`. It does not force an already-submitted completion
race. Equal lower-write/paging-request counts also cannot establish owned-request
admission causally. Preserve existing byte/policy results, but do not upgrade
them to those stronger proofs. V7/cache counters are device-wide across processes,
despite current messages saying process-wide. T083/T084 in the
[implementation review](IMPLEMENTATION_REVIEW_20260924.md) define the correction;
bump the executable plan only when implementing the changed measurement/verdict.

Plan 37 adds an observed in-flight mapped-overwrite stage to `paging-coherence`.
On a 512-byte-sector non-OS volume it waits for an isolated 1,536-byte older
sparse write under a bounded 2-second drainer delay, then flushes an overlapping
mapped rewrite. Successful routed paging writes and an overlap-wait increase
are required; newest bytes and untouched guards are checked while active and
after release. A missing observation fails the case. The delay is cleared in
`finally`, with no fault hook or raw-device write. Process-wide counters do not
prove exact file-range ownership. The CI-built 0.4.95.1 plan-37 tool passed
against the unchanged loaded 0.4.92.1 driver, with clean restoration.

Plan 36 applies plan 35's routed-paging admission accounting to the separate
first-write window in `foreground-background` too; plan 35 covered the six
sector windows but missed this second caller. The failed plan-35 run remains
incomplete and retains its raw counters. Failures now print the actual before/
after routing values rather than an internal sentinel. The 60-second workload
and its later drain/read/latency assertions are unchanged.

Plan 35 keeps the sector-admission check strict about lower reads, flushes and
actual cache drains. When lower writes occur during its process-wide measurement
window, it accepts only an exact match with successful routed paging writes from
Diagnostics V7; missing or failed routing, extra lower writes, and counter
regressions still fail. The report names the exception and states that equal
process-wide counts do not identify a file range or establish causation. This
is needed because plan-34 `policies` on 0.4.92.1 twice observed exactly one lower
write in the first sector window; the old check could not distinguish a normal
paging-marked NTFS write from the owned data write. The original incomplete
results remain evidence, not passes. A local plan-36 tool and the CI-built
plan-37 tool passed the complete policy case against loaded 0.4.92.1.

Plan 34 source candidate (2026-09-24) allows the existing `system-files` and
`system-post-restart` cases to run with reconciled system usage paths and records
actual active cache admission for a newly created owned file. The normal restart
remains an explicit separate action; creation while active does not by itself
prove dirty bytes survived a reboot. Plan 33 added a focused non-OS `paging-coherence`
case to plan 32's routed paging Diagnostics V7 and
per-case post-release image checks. It uses normal retained-write/read-promotion
options and one stable system-target comparison across preflight and workers.
Each passed Fast/Strict case now verifies its own bytes after release, and final
restoration verifies every passed image oracle. This is a changed verification
contract; plan-31 raw results remain intact. The exact installed 0.4.92.1
driver passed the plan-34 case. Its first stage uses
an owned 8 MiB Q: file, one unbuffered Fast write, a mapped read and overwrite,
then unbuffered live and released-cache byte checks. It records routed paging
read/write and overlap-wait deltas. A zero routed read or write produces an
incomplete case, and even nonzero process-wide counts do not prove a forced
old-drain/new-write interleaving. Plan 37 adds the observed second stage above;
neither stage closes the fault/cancellation/dependency gates.

Review correction: plan 31 did not prove paging coherence or progress. Its image cases used
unbuffered I/O, not Paint/Photos; Fast lacked a separate post-release byte check,
and scenario-worker identity equality still rejected a paging-role change accepted
by preflight. Existing zero paging counters do not establish bypass completion.
The new source still needs forced T079 ordering and T080-T081 installed checks in
[handover revision 4](PRIVATE_ALPHA_IMPLEMENTATION_HANDOVER.md#revision-4-implementation-first-correction-t075-t081).
Preserve prior raw results and their scope.

| Suite | Scope |
|---|---|
| `quick` | Existing file-integrity checks: seeded writes/overwrites, random updates, live reads, flush and filesystem checks. No policy sweep. |
| `paging-coherence` | Two owned-file unbuffered/mapped checks on a validated disposable 512-byte-sector non-OS disk. Requires Diagnostics V7; no raw writes or synthetic faults. The second stage observes a sparse old write in flight before a mapped overwrite, then checks active/released bytes and routed overlap counters. Process-wide counters do not identify the exact file range. |
| `system-preflight` | Read-only C: disk-identity and cache-state observation. Requires `--recoverable-vm`, exact `--system-instance`/`--system-bytes`, and an existing `--output` directory on a different physical disk with no reparse-point path. No workload files, cache controls, faults, TRIM or reboot. |
| `system-files` | Guarded 64 MiB owned-file creation on C:, two same-range overwrites, immediate reads, file flush, and live unbuffered comparison. The independently computed seed/SHA oracle is committed to the off-target result directory before the C: workload write. No cache controls, faults, raw I/O, TRIM or reboot. This is a baseline byte check, not an active-cache persistence verdict. |
| `system-post-restart` | Separate read-only unbuffered comparison of the owned C: file against `--oracle` from a prior `system-files` run on another physical disk. The operator performs any approved normal reboot separately. No workload write or cache configuration. It cannot be called a dirty-cache restart when caching was off. |
| `system-image-baseline` | Matching uncached 349 MiB BMP I/O baseline on C:. It requires the cache to remain disabled/released, records the post-file-flush driver state, and compares every byte with the off-target oracle. Existing paging/hibernation/dump registrations are permitted because this case remains pass-through and performs no cache control. Plan 26's active case separately accepts reconciled paging-only registrations. It is still an automated I/O analogue rather than Paint/Photos. |
| `system-active-image` | Two guarded active-C: cases for a recoverable VM: normal public Apply with Fast/Idle, then Strict/Idle, each using a runtime-only 256..512 MiB cache and deterministic 349 MiB BMP in a unique owned directory. The off-target oracle is written first, followed by application-flush, immediate-read, administrative-flush, full-byte and clean runtime-release boundaries. Reconciled paging/hibernation/dump registrations are accepted. Restoration retains the exclusive system-disk lease and final image evidence. It never creates a saved profile and does not itself launch Paint or Photos. |
| `system-app-session` | Guarded operator session on C: (same opt-in arguments, `--budget-mib` 1024..2048, C: disabled/released with no saved C: profile). Writes and verifies the deterministic 349 MiB BMP while uncached, applies a runtime-only Fast/Deferred cache (10-minute dirty age), writes `operator-instructions.txt`, then samples cache state and file metadata every second (`*.samples.jsonl`) until `operator-done` exists or 30 minutes pass. The operator edits and saves the image in Paint and reopens it in Photos. The saved bytes are hashed through unbuffered reads while the cache is active, the cache is flushed, disabled and released, and the bytes read back must match. An unchanged image, missing completion, cache fault or mismatch fails. The baseline oracle is `baseline-oracle.json`, so restoration never compares the edited image with it. |
| `policies` | Sector regressions with diagnostics-V2 zero-lower-attempt admission proof; a 60-second fitting hot-set test of serialized foreground writes and cached reads while Idle draining progresses; then six cache configurations, retained-data checks and disabled-cache byte oracles. Restores runtime configuration and requires the attribution driver. |
| `pressure` | Focused opt-in trigger and capacity checks on new files: Deferred first-dirty age under repeated overwrites, Idle last-write timing and an isolated Balanced high-watermark boundary; Automatic and Fixed 50/100 capacity backpressure; Fixed 0 ordered quota fallback; final disabled-cache byte oracles. Uses lower-write-attempt counters to distinguish eligibility/start from completion, a temporary 64 MiB cache, controlled 25 ms lower-write delay and an 80 MiB capacity file. No DiskSpd. Not included in `full`. |
| `drain-decision` | Focused T050 comparison: deterministic 25%-of-budget file payload plus recorded bounded filesystem metadata, no-drain controls, fitting random writes and cold random reads, and drain parallelism 1/2/4. Three repeats produce 24 immutable cases with alternating order and identical payload bytes within each matched repetition. Records workload scores, exact flush interval, lower-write attempts/completions, driver drain-phase timing, capacity waits, pending bytes and raw telemetry; disables cache and verifies every seeded payload byte after each drain case. Requires DiskSpd. Not included in `full`. |
| `trim-diagnostic` | Existing file-integrity workload on fresh files with cache routing enabled, then disabled; records exact file-level TRIM rejection codes and restores original settings. No DiskSpd. Filter remains attached; unsupported TRIM stays SKIP. Not included in `full`. |
| `trim-file` | Driver-independent file-only probe: new 3 MiB file, middle 1 MiB TRIM, untouched guards and flushed rewrite oracle. Rejects boot/system/paging disks and changed disk identity. No cache controls, recovery snapshot or driver telemetry; restoration is explicitly not required. Unsupported TRIM is top-level SKIP with run status COMPLETED_WITH_SKIPS (diagnostic collected, not correctness passed). Not in `full`. |
| `volumes` | Plans 64/66/67: `volume-registration`, `volume-raw-disk-commands`, `volume-shared-disk`, `volume-resize` and `volume-snapshot` (above). Needs the lab disk for the shared-disk case (SKIP otherwise). No DiskSpd. Not in `full`. |
| `trim-cache` | Plan 64: TRIM of pending, clean and in-flight data with guard and rewrite oracles (above). Needs a disk that accepts TRIM (lab VHDX); SKIP otherwise. No DiskSpd. Not in `full`. |
| `flush-interference` | Automatic/Fixed50 × requested application flush/control × repetitions. Eager, QD128 writer, 25 ms lower-write delay, hot reader. `--repeats 2` gives eight cases. |
| `performance` | 144 hot-reader cells at defaults: allocation × Eager/Idle × delay 0/25 ms × writer QD8/32/128 × alone/loaded × three repeats. Plus 60 sequential/random read/write and mixed scaling cells, cache off/on, QD1/32. |
| `full` | `quick` + `policies` + `performance` + focused flush matrix (218 top-level cases at defaults). |
| `sequential-resident` | Opt-in fitting 1 GiB sequential Q8/T1 RAM-cache peaks, 2048 MiB budget, Fast/Idle and timing off. Full cold pass plus strictly verified miss-free RAM pass before scoring; reads, fresh per-I/O random writes and precomputed-buffer writes (nine cases at three repeats). Requires DiskSpd; excluded from `full`. |
| `write-performance` | Separate focused matrix: random 4 KiB Q1/32 and sequential 1 MiB Q1/8, one thread, Automatic allocation, cache Off/Eager/Idle, detailed driver timing off/on, three repeats (72 cases). Not implicitly included in `full`. |

For the guarded system phases, use an elevated, restorable test VM; obtain the
exact C: physical disk PnP ID and size from inventory, and choose an existing
result directory on a separate physical disk. These are separate invocations:

```powershell
qcache developer verify C: --suite system-files --recoverable-vm --system-instance <exact-PnP-ID> --system-bytes <exact-bytes> --output Q:\QueueCache-System-File-Results
# Only after an operator-approved normal reboot; use the completed create run's oracle:
qcache developer verify C: --suite system-post-restart --recoverable-vm --system-instance <exact-PnP-ID> --system-bytes <exact-bytes> --oracle Q:\QueueCache-System-File-Results\QueueCache-Verify-<create-run-id>\oracle.json --output Q:\QueueCache-System-File-Results
# Safe disabled/pass-through baseline (usage paths are recorded but do not block this case):
qcache developer verify C: --suite system-image-baseline --recoverable-vm --system-instance <exact-PnP-ID> --system-bytes <exact-bytes> --output Q:\QueueCache-System-Image-Results
# Only after external recovery is ready; reconciled system usage paths are recorded and validated:
qcache developer verify C: --suite system-active-image --budget-mib 512 --recoverable-vm --system-instance <exact-PnP-ID> --system-bytes <exact-bytes> --output Q:\QueueCache-System-Image-Results
```

The create run's `FINISHED.txt`/status/results/log and oracle must be retained;
an interrupted run is not completed evidence. The file is intentionally
retained for the second phase. The file/restart suites do not configure caching,
and no system suite relaxes existing non-OS guards. Their original split managed
CLI baseline used installed 0.4.70.1 with C: disabled; exact Plan-31 active and
saved-profile restart evidence is recorded below. Plan 21 supersedes
the original plan-20 active-image contract before VM use: it adds the matching
uncached image baseline, proves active-cache admission, separates byte checks from
the application flush, tolerates unrelated live C: dirty bytes after an administrative
flush, and makes post-release evidence mandatory after a passed active case.
Plan 22 corrects the baseline gate: existing system usage registrations do not
block the disabled/pass-through control case, but remain a hard stop for active
caching and restoration.

Plan 23 extends diagnostics without changing that gate. `UsageActivity` reports,
for paging, hibernation and dump notifications separately, lifetime in/out
requests, successful completions, failed completions and the process ID that
submitted the last notification. These counters distinguish accepted outstanding
kernel paths from pending, failed or unbalanced lifecycle traffic. They do not
identify a file path and are not permission to ignore `UsagePaths`. V4 retains the
V3 prefix: new controllers accept V1-V4, and a new driver returns V3 when an older
controller supplies only the V3 output buffer.

Plan 24 makes that lifecycle evidence mandatory for guarded system operations and
rejects a snapshot whose current counts do not equal completed successful
additions minus removals. It also brings the filter's PnP behavior in line with
an active special-file path: the filter reports the device not disableable and
fails query-stop/query-remove until the final registration leaves. This does not
permit active caching or prove paging-I/O ordering and forward progress.

Plan 25 adds nullable Diagnostics V5 `PagingIo` counters for paging reads/writes
and bytes plus last-request breadcrumbs. They are recorded before routing choice,
so disabled pass-through is observable without changing I/O behavior. The
`system-image-baseline` case requires V5, preserves immutable before/after
diagnostic files, and reports counter deltas for its workload window. These are
process-wide device counters: concurrent Windows traffic can contribute, and a
zero delta is valid evidence. Active C: remains blocked.

Exact installed 0.4.80.1 run
`QueueCache-Verify-20260923-172845-86c7a49e6f1445178f9ca455390a8ae9`
passed the disabled 365,953,024-byte baseline and observed 983 paging reads
(29,969,408 bytes) plus 427 paging writes (5,344,256 bytes) in its 13.5-second
window. This proves that a per-request whole-cache drain/disable policy would not
be a usable active design.

Plan 26 added the guarded active candidate. Up to 64 MiB of the write quota is
reserved from ordinary admission for paging writes; paging MDLs use high-priority
mapping; and non-overlapping paging-read misses may reach the lower disk while an
ordinary write waits for capacity. Diagnostics V6 reports the reserve, maximum
paging request lengths, mapping failures, paging capacity waits and serviced read
misses. `system-capture`, `system-active-image` and `system-restore` permit only
the reconciled paging-only registration state in a recoverable VM. The active case
rejects any new mapping failure or capacity wait and requires the maximum paging
write to fit the reserve at both application and administrative flush boundaries.
Exact 0.4.82.1 run
`QueueCache-Verify-20260923-185145-55506c69732847a9a8863d3cacd181c0`
passed 1/1: 366,888,960 accepted bytes, two complete oracle matches, zero paging
mapping failures/capacity waits and clean restoration. Plan 29 gives the Fast and
Strict cases separate immutable workload/oracle artifacts and requires paging data
to bypass RAM admission/read service: reserve and serviced-miss counters must stay
zero, as must new mapping failures and capacity waits. It otherwise uses normal public
Apply and covers both Fast and Strict; the old action remains only as an ABI alias.
Plan 30 permits the oracle's paging-role flag to change across the deliberate
pagefile/restart phase, while still requiring the same drive letter, physical disk
number, byte size, PnP instance, boot role and system role.
Plan 31 aligns the owned-path gate with Plan 28's isolated cases: only the exact
`system-active-image-fast` and `system-active-image-strict` suffixes are accepted.
Exact installed 0.4.87.1 run
`QueueCache-Verify-20260924-045431-b85c9a03afc94e6b9c8f7d1ffa501d99`
completed both cases. Fast/Strict accepted 365,977,600/365,957,120 bytes,
matched every byte before and after administrative flush, kept reserve, mapping
failure, capacity-wait and serviced-paging-miss counters at zero, and released C:
cleanly. A saved 512 MiB Fast profile subsequently restored after reboot with a
fixed 4 GiB C: pagefile and `Dump=1`; post-restart run
`QueueCache-Verify-20260924-045930-94c0721be30a4e6fb7ad670cd8b6bf9d`
matched its independent 64 MiB oracle. Cleanup restored the recorded original
settings. This does not claim unavailable hibernation/Fast Startup transitions,
dynamic registration while already active or low-memory/fault/cancellation proof.

The first VM plan-22 baseline used the split plan-22 CLI against the unchanged
installed 0.4.75.1 driver. Run
`QueueCache-Verify-20260923-140039-8c9ee24872d14b6b82639e5590c9bbb7`
completed 1/1: the 349 MiB application file flush returned and every byte matched
the off-target oracle through unbuffered reads; C: remained disabled, released
and error-free. The paired active request
`QueueCache-Verify-20260923-140550-e8e582d46f5f46658a039d4ce29e0ed9`
ended INCOMPLETE by design during `system-capture` because C: still reported two
paging usage paths. It created no recovery snapshot, image/oracle or enabled-state
evidence and did not change C:. This is positive safety-gate evidence, not an
active-cache failure or a passed active case. Exact packaged plan-22 proof remains.

Plan 20 also extends `qcache diagnostics <device>` JSON with nullable
`UsagePaths` (`Paging`, `Hibernation`, `Dump`). New controllers remain compatible
with V1/V2 drivers, and new drivers return V2 to older controllers. The existing
combined `PagingPathCount` remains the activation safety gate; the split fields
are diagnostic evidence, not permission to ignore a nonzero count.

Plan 11 adds the maintained foreground/background case to `policies` and `full`.
It uses an 8 MiB hot set under a 64 MiB Fast/Idle cache for 60 seconds. The first
fitting write and its immediate cached read must issue zero lower-I/O attempts.
The sustained window serializes each known-byte write/read pair so its oracle is
unambiguous, records foreground p99/maximum latency, accepted/drained bytes, read
hits, capacity waits, peak dirty bytes and lower-write attempts, and requires both
foreground completion and nonzero background progress. It finally disables the
cache and verifies every owned byte from disk. The default is Idle with a 5,000 ms
age trigger, 250 ms idle trigger, 40/80 watermarks, 256 KiB batches and parallelism
1. Age starts scheduling; it is not a persistence deadline. Existing saved profiles
retain their explicit policy and Strict/Fast choice.

Plan 12 introduced the separate `pressure` suite. Plan 13 tightened its measurement
contract. A fitting first write and immediate cached read must issue no lower-I/O
attempt. The 1,000 ms Deferred case overwrites the same dirty block at 350 and
700 ms, accepts the first lower-write attempt only in the 850..1,450 ms window
from the original write, and separately waits for completion. The 500 ms Idle case
overwrites at 250 ms and accepts the first attempt only in the 650..1,500 ms window
from the original write (400 ms or more after the last write). These tolerances
allow polling/scheduler jitter while distinguishing first-dirty age from last-write
idle age; they are trigger windows, not persistence deadlines.

The Balanced case disables age as a competing practical trigger, writes to just
below its 20% high watermark, requires the lower-write-attempt counter to remain
unchanged, crosses the watermark with one 64 KiB write, then requires an attempt
and later completion. Capacity cases write 80 MiB through a 64 MiB cache using
Automatic and Fixed 50/100 allocation. They require observed backpressure and
bound sampled dirty, in-flight and occupied-slot ownership to the payload/write
pool, then disable and compare every 64 KiB block. Fixed 0 requires its explicit
quota barrier, no capacity wait and zero write-payload ownership. Host oracle contracts
reject early/late triggers and invalid reservation bounds. Exact-build VM execution
passed on 0.4.64.1, closing T051/T069. Allocation faults, cancellation, a
single request larger than its quota, 4Kn and TRIM remain in the A06a ledger.

Plan 14 corrects the Fixed 0% reservation oracle after the first installed plan-13
run. Fixed 0% forbids dirty, in-flight and retained-write ownership, but it does not
forbid independent read-cache slots in the remaining pool. Total occupied slots
remain bounded by total payload. The failed plan-13 run is preserved as incomplete;
its trigger and Fixed 50/100/Automatic subchecks passed, but it is not relabelled or
combined with a later run.

The exact plan-14 run
`QueueCache-Verify-20260922-130140-5a38d389f7694633bb31d489d7fa816b`
completed all seven subchecks on installed 0.4.64.1 from commit `bd60725`, with
loaded SYS SHA-256
`1EA460969A068E047D6B11BE9C828ECEED9DB229C08A2F8A9E531E60E2A0819E`.
It recorded ready telemetry and a nonempty control trace, preserved independent
persisted-byte checks, and restored the original active 2 GiB Fast/Idle profile
with zero dirty/in-flight bytes and errors.

Plan 16 retains the separate `drain-decision` suite for T050. Each repetition uses
the same deterministic seed and 25%-of-budget payload for its parallelism 1/2/4
conditions and alternates fitting-write/cold-read order. A Deferred one-hour
trigger holds a 25%-of-budget file payload, plus at most 64 KiB of recorded
filesystem metadata, without lower-write or lower-flush attempts. NTFS metadata
ownership can vary between cases; `SeedMetadataBytes` records the exact difference.
Filesystem activity can issue lower reads during seed preparation; the suite
records `SeedLowerReadAttempts` without attributing them to payload admission or
drain speed. The seed worker first establishes its own clean baseline so late
activity from the preceding case is excluded; the
measured workload starts before the explicit drain. Controls retain the same
workload without a pending drain (the cold-read control disables caching). Raw
DiskSpd XML is preserved for CPU/tool
details. `*-drain.json`, snapshots and telemetry record drain duration, pending
bytes, attempts/completions and driver phase timings. This is an implemented
measurement contract, not a performance verdict until its exact-build VM matrix
is complete and reviewed.
For a smoke check, `--case-filter` may select a case-ID substring such as
`fitting-write-p1`; this preserves the full-matrix IDs and is never a complete
T050 comparison.

The first exact installed 0.4.66.1 run, `QueueCache-Verify-20260922-190037-018dafb510ad40258be18c46a8146526`,
stopped at case 4/24 because plan 15 incorrectly required identical cache-owned
bytes across runs. All cases used the same 256 MiB payload, while NTFS metadata
ownership varied from 4 KiB to 16 KiB. The first three cases were measured,
restoration passed, and the raw incomplete run is retained privately as
`drain-plan15-0661-exact-incomplete-4of24`. Plan 16 preserves the 64 KiB metadata
bound and records the exact amount per case; it does not infer a complete
performance result from those first three cases.

A split-CLI diagnostic plan-16 run then stopped at case 2/8 because the seed
worker observed two lower read attempts while updating NTFS metadata. Its lower
write and flush attempt deltas were zero, and restoration passed. The incomplete
run is retained privately as `drain-plan16-preliminary-r1-incomplete`. Plan 16
now records those reads while preserving the zero lower-write/flush seed gate.

A second split-CLI diagnostic run,
`QueueCache-Verify-20260922-191107-fe5ca551e0174877819bd03f2bbe5f39`,
completed all 8 single-repetition cases with clean restoration on the installed
0.4.66.1 driver. The seed payload was 256 MiB in each drain condition, metadata
was 4-8 KiB, lower-write and lower-flush seed attempts stayed zero, and the
recorded capacity-wait delta was zero. The raw result is retained privately as
`drain-plan16-preliminary-r2-completed` (1,336 files). This validates the
revised collection contract only. It mixed a local plan-16 CLI with the older
installed driver, has just one repetition, and does not close T050 or establish
a performance improvement.

The exact-build plan-16 run on installed/rebooted 0.4.67.1,
`QueueCache-Verify-20260922-193654-4921265ec92346c8bc77b0a63a587dbb`,
completed all 24 cases and restored Q: cleanly. Its raw evidence is retained
privately as `drain-plan16-0671-exact-completed`. The per-condition measurements,
limitations and still-open tuning decision are in the execution tracker. Do not
interpret `MEASURED` or the no-pending-drain/uncached controls as a performance
acceptance verdict or physical-disk limit.

Plan 17 adds `sectors/observed-inflight-replacement` to the existing `policies`
suite on 512-byte-sector secondary disks. It uses the bounded 2-second delay,
requires an exact 512-byte in-flight count before and after a same-range
replacement (not an unrelated 4 KiB metadata write),
checks the newest live bytes, then disables caching and checks disk bytes. The
runner still owns hook reset and runtime restoration. This is a useful observed
overlap regression, not a controlled completion-order handshake and not full
T052 proof. The first split-CLI smoke run accepted an unrelated 4 KiB in-flight
count; its raw evidence is retained privately as
`plan17-preliminary-policies-ambiguous4096`. The stricter split-CLI run
`QueueCache-Verify-20260922-201430-a177b8107f4547f0a05d4a1a7ee1f8ee`
completed with 31/31 policy checks, 512/512 in-flight bytes around replacement,
newest RAM/disk bytes and clean restoration; raw evidence is retained as
`plan17-preliminary-policies-exact512`. The exact installed 0.4.69.1 run
`QueueCache-Verify-20260922-203515-6e3b41080fe34c4099633c29153fde9b`
also passed 31/31 checks and restored cleanly; raw evidence is retained privately
as `plan17-exact-0691`. `ConfigurationManager.Apply`
also rechecks the selected disk's mounted extent and PnP identity immediately
before opening it for a state change, including saved-profile restore.

### Small-write investigation

Plan 9 adds optional `--case-filter` to `write-performance`: a case-sensitive
substring of existing case IDs, for example `random-write-q1-Idle-timingFalse`.
At three repeats this selects three cases, preserving full-matrix IDs, workload
parameters, score windows, telemetry and restoration checks. Empty, unmatched or
other-suite selections fail before disk access. The manifest, log and summary
identify the selection; COMPLETED applies only to that selection, never the full
matrix. Omit the option for the unchanged 72-case suite. Compare the same selection,
CLI, DiskSpd hash, budget and repeat count before/after each focused optimization.

Plan 8 strengthens the sector cases in `policies`/`full`: from a clean boundary,
all eight sector positions, a crossing write and a full 4 KiB write plus immediate
cached reads must leave lower read/write/flush attempt counters unchanged. The
later explicit drain and disabled-cache disk read must increment those counters.
The exact before/after values are retained in each admission check. Missing V2
diagnostics fails this proof rather than substituting zero. The 72-case write
matrix and its timing/deadline contract are unchanged.

Diagnostics uses the existing IOCTL with output-size negotiation: old clients
receive the unchanged 80-byte V1 response; new clients accept V1 (Attribution
null) or the 216-byte V2 response. V2 lower counters increment immediately before
`IoCallDriver` for read/write/flush, including generated drains, original requests
and direct inactive routing. They are live atomic lifetime counters, independent
of detailed timing and completion snapshots, not one transactionally coherent
snapshot with the other diagnostics. Allocation failures and injected failures
before submission are not attempts. Control/IOCTL traffic is not a data attempt;
other filters' submissions are outside this filter's counters.

V2 records barrier counts by reason: 1 management control, 2 Strict write-through,
3 disabled-with-dirty write, 4 request exceeds write quota, 5 nondeferred application
flush, 6 shutdown, 7 power, 8 ordered media/unknown/PnP, 9 removal. The last barrier
also retains major function, code (management action or IOCTL), byte offset and
length for data requests. Offset/length modulo 4096 identifies partial-block
alignment. These describe barrier entry, not successful persistence. Capacity
waits remain in existing telemetry. No scheduling/durability rules are changed.
Zero observed attempts is a focused result, not a bounded lower-I/O gate or a
proof of unexercised allocation, fault, cancellation and lifetime paths.

Plan 5 adds `qcache developer verify Q: --suite trim-diagnostic`. A completed
diagnostic run can contain unsupported TRIM skips; inspect each `files` worker
reply, not just the top-level case status. Only errors from the TRIM call itself
can be classified as unsupported; guard/rewrite failures remain failures. This
compares cache routing, not filter attachment or physical-storage isolation.

Plan 6 adds `qcache developer verify Q: --suite trim-file` for an explicitly
driver-independent comparison. It uses the same owned-worker timeouts, run/disk
leases and immutable run folders, but neither opens a cache device nor changes
runtime configuration. It performs fresh inventory and native identity checks
before the file probe. Driver telemetry, its ready handshake and cache recovery
are intentionally inapplicable to this suite only; `restoration.json` records
that distinction. It never removes filters or reboots. To compare attachment,
record the actual post-reboot device stack separately; disabling caching or
checking the registered service alone is insufficient. Keep the same CLI for
both runs. A rejected TRIM produces `COMPLETED_WITH_SKIPS`, not a byte-correctness
PASS, and must not be compared as a performance measurement.

Plan 4 adds partial-write correctness to `policies` (also included in `full`).
On a 512-byte logical-sector volume it checks every sector position, disjoint and
cross-4-KiB writes, cold-neighbour preservation, deferred admission without observed
lower writes/flushes, and sparse-drain disk bytes. Full/partial overwrites run with
25 ms drain delay at parallelism 1/2/4 and retention off/on. Reports state whether
in-flight data was observed; this is not proof of every race. Native 4Kn volumes
are explicitly unsupported by this scenario, not reported as passed. Fault
injection, cancellation and partial TRIM coverage remain separate work.

```powershell
qcache developer verify Q: --suite write-performance --budget-mib 2048 --diskspd "C:\Tools\CrystalDiskMark9_0_3\CdmResource\DiskSpd\DiskSpd64.exe" --output .\results
```

This uses a new file half the cache budget (1 GiB with the example), five seconds
of warmup, then the requested measurement duration. It is CDM-shaped, not an exact
reproduction of CDM's preparation or scoring. Cache state is flushed and clean
blocks dropped between cells; warmup does not guarantee full residency. Policy and
timing order reverse between repetitions. Both timing modes still collect the same
200 ms observer samples; this separates detailed driver instrumentation overhead,
not observer overhead. Raw XML, process intervals, before/after counters and phase
samples are retained. Aggregate keys include timing and fitting-file mode.

Compare Q1 latency/IOPS and Q32 scaling, timing off versus on, and Eager versus
Idle against the uncached control. Inspect `DrainingPendingWrites`, capacity waits,
dirty/drained bytes and queue fences before attributing a slowdown to locks. The
counter window includes warmup/close and must not be treated as the score window.
Keep the original run as the baseline; rerun the same command/binary/budget after
each driver change. Do not change broad ordering/barrier rules on throughput alone.

Parameters: `--budget-mib 1024`, `--repeats 3`, `--duration-seconds 10`,
`--preparation-flush-seconds 180` (explicit range 180..3600; use 600 for the
authorized slow-drain VM baseline). Plan 7 records this deadline in the manifest
and progress log. It affects only pre-workload explicit flushes and their
associated observer lifetime, not score windows, readiness/coverage requirements,
other control deadlines or the independent restoration deadline.
`--deadline-minutes 0` (default: **no overall time limit**). The suite runs until
completion, cancellation or a failure; individual operation timeouts remain enabled.
An optional positive deadline still limits the whole measurement batch. Preparation,
warming, delayed draining and recovery add time. Cleanup
gets a separate five-minute restore-worker deadline, preceded by up to 45 seconds
for observer readiness. Do not reduce the plan silently to make it fit.

Every coordinator progress line, including child-process waiting messages, shows
`[Test N of M]` during a case. Preparation, restoration and final reporting have
their own labels and counters. These count top-level cases, not elapsed-time
percentages; a long-running case stays at the same number. The same lines appear
in `run.log`.

If a worker exceeds its deadline or cannot terminate and its output pipes also
remain open, the primary termination failure is preserved. The separate
`*.pipe-failure.json` records both failures; inspect it with the worker's stderr,
exit record and control trace. A pipe-only failure remains fatal. This does not
extend worker deadlines or make an incomplete run acceptable.

Before a workload or management command starts, its telemetry observer must finish
disk discovery and flush its first sample to disk (45-second readiness limit).
The observer records a final sample after workload completion. `*-interval.json`
records the enclosing workload-process interval; telemetry must bracket it, have
strictly increasing timestamps and no gaps above two seconds (normal cadence:
200 ms). Late-starting, stalled or early-exiting observers invalidate collection;
two samples alone are not sufficient. Plan version 1 did not enforce this coverage.

The coordinator performs full disk inventory during initial capture. Later worker
processes validate that recorded target using native volume extents, PnP identity
and the driver's reported device length; they do not launch `Get-Disk` for every
control or telemetry operation. This keeps identity checks active when storage
inventory is slow under load.
PnP metadata is matched before opening a disk interface, so validation queries
only the recorded disk. Native calls remain synchronous and are bounded by the
coordinator's worker deadline, not by an intrinsic native-call timeout. Worker
stderr retains full exception stacks, including validation failures. Recovery
still requires its observer to become ready before any restoration command.
Volume remapping is rejected before opening the recorded disk, and interface-path
buffers use Windows' reported size. For read-only native identity regression checks,
set `QCACHE_TEST_DISK_TARGET` to an existing NTFS volume (for example `C:`) before
running the management tests. These checks do not configure a cache or write to
the selected disk; they cover successful validation, mismatches and cancellation.

Configuration changes and restoration poll status when it reports transient
draining, for up to 30 seconds per state check within the worker deadline. They do
not replay configuration writes just because the final snapshot was busy. Faults,
increased error counts, removal, suspension, disk-size and identity changes fail
immediately. Original settings, timing and saved
profiles are still checked before reporting restored. After a failed run, confirm
its owned processes have stopped and use the newly installed CLI's
`qcache developer verify-recover <failed-run-directory>` before starting another
suite. Recovery creates its own subfolder/log and does not relabel old results.
Restoration comparison failures report named expected/actual fields and retain
the observed state, profiles, timing and all mismatches in
`<reply-path>.mismatch.json`. Late writes after a flush can still fail the
zero-dirty check; this evidence does not bypass that check or retry mutations.

Plan 10 restoration flushes the identity-checked filesystem volume, flushes the
cache, then disables and drains remaining admissions before reapplying the saved
configuration. The main flush remains separate from disable; draining a large
dirty set directly through disable exceeded the restoration deadline in a VM
probe. Each boundary records a state snapshot beside the reply:
`.before-volume-flush.json`, `.after-volume-flush.json`, `.after-cache-flush.json`
and `.after-cache-disable.json`. A missing boundary means that stage did not
complete, not a zero-dirty result. Any operation failure stops restoration.
The independent restoration deadline and all final state/profile/timing/error
checks are unchanged. Disable clears clean residency; restoration promises saved
settings and durable pending writes, not preservation of cached clean contents.
Workloads, case IDs and score/telemetry windows are unchanged from plan 9.
This does not guarantee quietness after re-enabling against external writers.
Focused Q32 restoration passed; a subsequent Q1 main flush exceeded the existing
deadline before reaching disable. See the RAM-first tracker for retained failed
probes and separate recovery evidence; full restoration acceptance remains open.

Performance workloads use a hot set one quarter of the selected cache budget and
a separate writer file twice the budget. Fresh random-filled files are prepared
outside the measured interval. Hot residency must be established before the
interference measurement. Repeated policy order alternates, all repetitions retain
their own results, and complete runs produce IOPS medians/min/max in
`aggregates.json`. Raw XML retains CPU/latency details for deeper attribution.
Alone QD labels identify the paired writer configuration, not the reader queue
depth: the hot reader always uses two threads and eight requests per thread.

Use [Microsoft DiskSpd](https://github.com/microsoft/diskspd/releases) or
CrystalDiskMark's bundled DiskSpd. Tested with Microsoft 2.3 and DiskSpd 2.2 in CDM
9.0.3. The runner records the executable's SHA256 and does not download or silently
substitute binaries. Both use structured `-Rxml -L` results and must return exit
code zero in XML mode. CDM appends `Score: 0` and `averageLatency: 0.000000` after
the XML; only this recognized two-line numeric trailer is ignored. All measurements
come from XML. Unexpected extra output, malformed XML, missing required fields,
nonzero exit codes and timeouts still fail collection. Text-mode CDM score exit
codes are not used. Keep executable/version and workload settings constant across
comparisons; do not merge baselines from different variants.

To obtain it: open the Microsoft release page, expand **Assets**, download
**DiskSpd.ZIP** (not the source-code ZIP), and extract the entire archive to, for
example, `C:\Tools\DiskSpd`. On x64 Windows use
`C:\Tools\DiskSpd\amd64\diskspd.exe`; amd64 also means Intel x64. Pass that exact
file path to `--diskspd`, quoting it if it contains spaces. This is a standalone
Microsoft tool. Alternatively, use `CdmResource\DiskSpd\DiskSpd64.exe` inside your
CrystalDiskMark folder, for example:

```powershell
qcache developer verify Q: --suite full --diskspd "C:\Tools\CrystalDiskMark9_0_3\CdmResource\DiskSpd\DiskSpd64.exe" --output .\results
```

A missing path/file error is separate from an incompatible executable. Do not
assume the CDM filename is `diskspd.exe`; select its actual `DiskSpd64.exe`.

## Results and recovery

| File | Meaning |
|---|---|
| `manifest.json` | Versioned plan/options, expected case IDs, CLI identity, registered driver path/hash and DiskSpd hash. Registered binary is **not proof of loaded binary** after an upgrade. |
| `recovery.json` | Target identity, original runtime settings/timing and saved profiles, written before mutations. |
| `status.json` | Current case or final `COMPLETED`, `INCOMPLETE`, `CANCELLED`, `RESTORATION_FAILED`. |
| `results.json`, `results.csv`, `SUMMARY.md` | Per-case status/scores. `PASS` is an integrity check; `MEASURED` means collected, **not fast enough**. No complete-run aggregate from partial data. |
| `*.command.json`, `*.process.json`, `*.stdout.txt`, `*.stderr.txt` | Exact argument arrays, timestamps/PIDs, raw process evidence. |
| `*-telemetry.jsonl`, `control-trace-*.jsonl` | State, queue/phase, timing and flush counters during workloads and management commands. |
| `*-before.json`, `*-after.json`, `*.score.json` | Case boundaries and parsed scores. Counter-window duration is not DiskSpd's measured interval. |
| `restored.json`, `FINISHED.txt` | Verified restoration and final completion marker; a marker can explicitly say failure. |

Check without touching the driver:

```powershell
qcache developer verify-status C:\QueueCache-Results\QueueCache-Verify-<run-id>
```

Ctrl+C cancels measurement, stops owned children and attempts restoration. A hard
process kill or OS failure cannot guarantee cleanup. After checking recorded owned
processes stopped, retry explicitly:

```powershell
qcache developer verify-recover C:\QueueCache-Results\QueueCache-Verify-<run-id>
```

Recovery rechecks machine/disk/cache identity, restores runtime settings and timing,
drains to zero, and compares saved profiles unchanged. It never writes profiles.
The delay hook is cleared to zero; **start with no armed delay/fault hooks** because
the protocol cannot capture their original values. The runner never resets faults,
formats, deletes test data, reboots, or touches OS-disk caching. Recovery is bounded
and can fail; inspect its evidence rather than assuming the old configuration won.
Only one runner/recovery may own a physical disk (volumes on one disk share the lease). Do not change the same cache from
the UI, CLI or another test during a run; the lease does not lock out those clients.

## Scope and maintenance

This is repeatable current-boot evidence, not 100% coverage or production
certification. In particular, the private eleven-check concurrency suite's exact
cancellation/reinsertion/IRP-order scenarios are not replaced by `quick` or
`policies`. Boot/power/removal/fault injection, observer-off comparison and exact
flush-queue positioning still require explicit verification work. A requested
flush does not prove its IRP occupied a particular queue position; use counters and
mark that mechanism unexercised if evidence is insufficient. Automatic mode can
evict the hot set during writes; read-miss counters distinguish that from queuing.

Add future scenarios to `VerificationPlan`/the shared runner, with typed results
and regression tests. Do not keep cloning orchestration scripts. Contract tests
cover unique IDs, completion rules, malformed XML, zero-I/O latency, argument
preservation, output capture and cancellation/deadlines without touching a driver.
Runtime testing of new releases still belongs on the VM.

### Removal topology and veto evidence

The orderly removal operation resolves the disk devnode or its immediate,
ejectable storage adapter only when that adapter has exactly one child matching
the selected disk. It verifies the complete bounded child/removal-relation scope.
Related volume-manager devnodes must expose a normal or hidden volume interface
whose native single-disk extent lies entirely on the selected physical disk.
Shared adapters, unknown devices, missing interfaces, multiple extents and foreign
disk extents refuse preparation. Preview records the selected node, scope members,
relation API results and verified volume extents. An absent optional relation
property remains result 37 with null devices, distinct from a reported empty list.

A Windows veto is recorded as a structured native result with veto type/name,
disk presence result and any cache-resume errors. A veto with the original disk
confirmed present permits normal identity-checked runner restoration and leaves
the case INCOMPLETE. A failed worker or uncertain disk presence defers restoration
until inspection and supported recovery. Neither result qualifies removal.

Plan 73 records before/disabled cache snapshots and native lower I/O attempt
counters for the affected volume in the eject result, before its handles close.
The 8 MiB pending-write case requires matching volume identity and cache lifetime,
nonzero pending bytes before preparation, clean disabled state, successful
filesystem flush, and advancing lower write/flush attempt counters. Missing
attribution is unsupported rather than zero-filled. These are volume lifetime
counter differences during controlled preparation, not process-wide score windows.
Successful reconnect/oracle verification permits restoration even if preparation
evidence is incomplete; the case still cannot pass.

Plan 74 adds `disk-removal-windows`: the same explicit disposable target, pending
8 MiB Fast/Deferred oracle and live reconnect contract, but requests the native
Windows eject API directly while the cache is still enabled/dirty. No product
disable, release or filesystem flush is issued before native eject. A final
read-only state/diagnostic snapshot proves that precondition and its handle closes
before the request. This qualifies the native Windows PnP path on the tested bus;
it is separate from the product preparation/lower-counter case and does not
claim that the tray UI itself was automated. Both suites stay excluded from full.

Plan 74 also generates unique cryptographic oracle bytes for each removal case,
so a previous run's data cannot stand in for newly acknowledged pending writes.
Old raw runs and hashes remain unchanged.

## Prewarmed sequential Q8 read/write (plan 77)

`sequential-resident` is an opt-in focused suite, excluded from `full`. It requires
`--budget-mib 2048` and uses a unique 1 GiB `resident.dat`, Fast/Idle at default
parallelism 2, 1 MiB requests, Q8/T1, detailed timing off. Three repetitions
produce nine distinct cases: reads, writes generating fresh random data per I/O
(`-Zr`), and writes using a precomputed 1 MiB random buffer (`-Z1M`).
`--case-filter sequential-read` selects reads; `precomputed` selects the three
precomputed-buffer writes. `sequential-write` selects both write variants. This does not change the 72-case
write-performance contract.

```powershell
qcache developer verify Q: --suite sequential-resident --budget-mib 2048 --repeats 3 --duration-seconds 5 --diskspd C:\Tools\CDM\CdmResource\DiskSpd\DiskSpd64.exe --output C:\QueueCache-Results
```

Each case drains/drops prior clean blocks, applies its configuration, and reads
the fitting file sequentially twice (ten seconds then three seconds, no hidden
DiskSpd warmup). The first pass must complete at least a full file read.
The second pass must read at least the full file, record enough new RAM-hit bytes
for all its completed bytes, produce zero new read-miss bytes, retain at least
the file size, and keep the same healthy cache instance/generation. Raw XML and
before/after state evidence are retained. Missing or insufficient evidence fails
the case; a merely elapsed warmup does not imply residency. Scoring uses `-W0`,
with its separate telemetry ready handshake and recorded interval unchanged.

This approximates the earlier prewarmed CLI benchmark conditions while resetting
residency before every case, rather than warming once for an entire historical
run. The observer still samples every 200 ms even with detailed timing off.
Record Verifier and exact loaded CI driver identity. Thresholds remain an external
comparison; MEASURED means evidence collection completed, not that throughput
met a target. Preserve all repetitions and report ranges/medians, not only the
highest score. Prewarm counters include process close and management observations
and are separate from the scoring interval.

Plan 75's original three-second first pass was insufficient on the test backend
and was rejected before scoring. Plan 76 preserves that raw failure and requires
a complete ten-second first pass before the unchanged strict second-pass proof.

Plan 77 adds the separately named `-precomputed` write cases without changing
existing per-I/O write cases or the 72-case write-performance suite. DiskSpd's
`-Zr` creates fresh random content for each write and adds generator CPU overhead;
`-Z1M` generates its random source buffer once. These are distinct workloads:
do not combine their repetitions. CrystalDiskMark's normal Random setting uses
block-sized precomputed buffers for writes, as shown in its official
[DiskBench.cpp](https://github.com/hiyohiyo/CrystalDiskMark/blob/master/DiskBench.cpp).
The new comparison keeps the exact DiskSpd binary, resident proof and telemetry
handshake unchanged. [Microsoft's buffer contract](https://github.com/microsoft/diskspd/wiki/command-line-and-parameters)
explains why scores with and without `-Zr` cannot be treated as identical tests.
