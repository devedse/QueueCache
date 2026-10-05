# Managed disks foundation: installed cache verification, 2026-10-03

The shared allocator/accounting refactor passed focused ordinary-cache tests on
the disposable Windows VM after installation and an owner-approved reboot.
All **9 runner cases completed successfully: 74 raw checks passed, 4 skipped,
0 failed**, with successful independent restoration in every run. This qualifies
the exercised existing-cache paths; all three new managed-disk modes remain
unavailable pending their native provider, ownership and lifecycle implementation.

## Build and installation evidence

| Item | Observed value |
|---|---|
| Branch | `feature/managed-disks` |
| Installed source | `8e29bb159db10660ffcaa73ab07c54059993f66e` |
| Native implementation source | `e64f163e1338e85a7ff443fd34db57b8b9a4b516`; following commit changes documentation only |
| CI | [Successful run 37104360105](https://github.com/devedse/QueueCache/actions/runs/37104360105) |
| Package | Release x64 lab installer `0.4.264.1`, artifact `11268016112` |
| Installer SHA-256 | `F0194B8910421E0DBCAB52FEFECC1BBECB06506FCC5A51AC898AD74F716A9356` |
| Loaded driver | `C:\Windows\System32\drivers\QueueCache-0.4.264.1-775282E8A965.sys` |
| Signed-artifact and loaded-module SHA-256 | `775282E8A965100B32D188C6DA6281F100711A4FD2B9A6E607DDDE8F5FC37D6A` |
| Driver Verifier | `0x209bb`, `resetonbootfail`, exact new module load 1 / unload 0 |
| New Windows boot | `2026-10-03T07:11:25.5000000Z` |
| Final inspection | `2026-10-03T07:36:02.6158936Z` |

The installer hash, embedded commit, staged driver hash and lab signer were
checked before reboot. Actual loaded-module enumeration and its immutable path
hash were checked after reboot and again after testing; a displayed version or
service registration alone was not accepted as loaded-driver identity.
Installer exit `3010` indicated success requiring restart. Verifier configuration
exit `2` indicated restart required; read-back confirmed the intended module,
flags and boot mode before the single approved reboot. Runtime queries after
reboot and testing confirmed Verifier was active on the new module.

Existing Q: pending writes were flushed and its cache paused using
`policy pause Q: --runtime-only` before installation. Its saved configuration
remained unchanged and startup restoration activated it after reboot. Registration
was checked strictly: volume upper filters `volsnap`, `qcachelab`; disk upper
filter `partmgr`; automatic volume coverage enabled, diagnostic mode zero,
no registration problems.

## Targets and maintained commands

T: was the existing disposable non-OS/non-paging SATA disk, 32 GiB physical
capacity, NTFS. Q: retained its original 2 GiB Fast/Idle cache throughout testing,
so temporary test reservations coexisted with an existing allocation.

The existing `C:\QueueCache-Lab\VolumeLab.vhdx` was initially detached and was
attached through `qcache developer lab-disk` solely for `volumes` and `trim-cache`.
Its 24 GiB disk exposed V: (8 GiB NTFS), E: (8 GiB NTFS) and X: (RAW); the actual
sibling letter was E:, not an assumed W:. No lab image was created or formatted.
The volume suite temporarily shrank E: and restored its original capacity.
The lab image was detached again after caches were released. The previously
detached eject-test backing disk was left alone.

All commands used the installed `qcache.exe`, elevated, in the foreground, with
the same private evidence parent and unique runner-created subdirectories:

```powershell
qcache developer verify T: --suite quick --output <evidence-parent>
qcache developer verify T: --suite policies --output <evidence-parent>
qcache developer verify T: --suite pressure --output <evidence-parent>
qcache developer lab-disk attach C:\QueueCache-Lab\VolumeLab.vhdx
qcache developer verify V: --suite volumes --output <evidence-parent>
qcache developer verify V: --suite trim-cache --output <evidence-parent>
qcache developer lab-disk detach C:\QueueCache-Lab\VolumeLab.vhdx
```

Runner workload contract remains **plan 77**; no new workload or native
managed-disk scenario was added for this verification.

| Suite | Exact run directory | Main cases | Raw checks |
|---|---|---:|---|
| quick | `QueueCache-Verify-20261003-071524-a88b8c4631bc4c53bf2d619a3c1fb61f` | 1/1 PASS | 6 PASS, 4 SKIP |
| policies | `QueueCache-Verify-20261003-071759-8369d0323e954a3fb533d09ead3a2183` | 1/1 PASS | 42 PASS |
| pressure | `QueueCache-Verify-20261003-072336-8d4379898ac4447d90fd31e563fe3ea6` | 1/1 PASS | 13 PASS |
| volumes | `QueueCache-Verify-20261003-072735-97efa1938ee749a4858a0f64a50e466e` | 5/5 PASS | 10 PASS |
| trim-cache | `QueueCache-Verify-20261003-073412-19c53d58fcd146f3a24221901ffd2ecb` | 1/1 PASS | 3 PASS |

## What the tests proved

- **Policies and byte integrity:** partial/crossing/full-sector writes, retained
  and non-retained data at drain parallelism 1/2/4, warm reads, exact persisted
  bytes, concurrent whole-version copies and read-miss buffer isolation passed.
  All six sector-admission intervals had unchanged lower read/write/flush attempt
  counters. The first fitting foreground write also had zero lower-I/O attempts.
- **Allocation failure and rollback:** both maintained resize allocation faults
  (6 and 7) restored the previous 64 MiB Fast cache, options and data. A subsequent
  128 MiB Strict configuration succeeded after each fault was consumed. These
  exercise the cache using the extracted allocator/accounting helpers.
- **Capacity and drain triggers:** deferred age, idle and high-watermark checks
  passed. Automatic, Fixed/50 and Fixed/100 configurations preserved exact disk
  bytes when writing beyond the payload capacity, with capacity backpressure.
  Their first fitting writes had zero lower-I/O attempts. Fixed/0 deliberately
  bypasses cache writes and issued lower I/O, as required by that configuration.
- **Independent volumes and harmless observations:** V: and E: caches maintained
  separate pending data; flushing V: left E:'s pending bytes intact. Physical-disk
  descriptor/geometry/SCSI discovery caused zero drained bytes and zero flushes.
  Volume registration, temporary shrink/extend recovery and exact VSS snapshot
  contents passed. The snapshot intentionally drained pending writes.
- **TRIM:** trimming the middle 1 MiB of 4 MiB pending dropped exactly 1 MiB with
  zero drained bytes; untrimmed RAM guards and later disk bytes were exact. The
  clean-copy case released exactly 1 MiB without draining. TRIM during a delayed
  drain observed 20,480 bytes in flight and preserved untrimmed disk guards.

The quick file-only run preserved T:'s original zero-cache state, so it was a
pass-through integrity smoke test, not allocator evidence. Its four explicit
skips were file-level TRIM rejected by the SATA target (Win32 326), informational
TRIM observation, intentionally excluded reboot/fault/raw/policy-toggle paths,
and ranges/races/capacity/concurrency/cancellation outside that file-only suite.
The dedicated suites exercised the paths described above; the skips themselves
remain skips, and the SATA file-level TRIM guard/reuse path was not exercised.

## Evidence and restoration

Each exact run had `FINISHED.txt`, `COMPLETED` status, matching expected/collected
cases and null failure/restoration failure. Final summaries, results, logs, raw
worker replies, owned PIDs/exits, recovery snapshots, restoration stages and
control traces were inspected and retained privately. All 24 owned worker
processes exited normally with code zero. Every control trace passed its ready
handshake; sample counts were 9/4/4/4/4 for the suites above, with no observed
driver errors. These correctness suites have no DiskSpd score intervals;
`*-interval.json` and benchmark scores are consequently absent, not zero.

Final state: Q: active at its original 2 GiB Fast/Idle configuration, zero pending
or in-flight bytes, zero errors. C: and T: have no cache reservation. The global
reservation counter returned to exactly **2,147,483,648 bytes** after every suite
and at final inspection; the global limit was 12,831,753,216 bytes. Saved profile
JSON text/settings match the pre-install capture exactly. No test/UI benchmark
process remains. The lab image is detached, all three original physical disks
remain, and there were no post-boot unexpected-shutdown/bugcheck events in the
checked System event IDs 41/1001. Test-owned files and raw evidence were retained.

## Remaining qualification

No new RAM disk was created. The native RAM provider, secure single budget
authority endpoint, production broker/catalog, image import/checkpoint adapters,
boot formatting and power/removal contracts remain outstanding. Typed production
Windows VHDX primitives were not exercised by the developer lab-disk helper.
Fully controlled old/new write completion ordering remains unverified; observing
in-flight replacement alone does not close that gap. This is not an exhaustive
memory-leak, global-limit contention, cancellation or power-state qualification.

There was no CrystalDiskMark/DiskSpd benchmark, matched older-driver comparison,
72-case write-performance matrix, or new FAT32/ReFS run. Correctness diagnostics
from policy workloads do not establish performance acceptance. RD02–RD11 gates
in [the implementation plan](RAM_DISK_IMPLEMENTATION_PLAN.md) still govern real
managed-disk activation; [the tracker](RAM_FIRST_IMPLEMENTATION_TRACKER.md)
records implementation and verification independently.
