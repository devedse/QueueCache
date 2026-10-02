# Native Windows eject and CrystalDiskMark — 2026-09-30

## Environment

Windows 11 Pro 26H1 build 28000, Proxmox VM 109. Loaded CI driver
0.4.249.1 (`1752b13`), Verifier 0x209bb, module load 1/unload 0:
`QueueCache-0.4.249.1-A31F6D68C608.sys`, SHA-256
`A31F6D68C608A94B09B0A45AD8F1BD08D1494783E4E2895AFBF3595401755F91`.
Native driver source was unchanged during these tests. The eject run used a
locally published maintained plan-74 runner, not a newly installed CI release.

## Native Windows eject: removal observed, durability unverified

Run: `QueueCache-Verify-20260930-174817-7361fc07ecb34be9aa87d1f462c4557d`,
suite `disk-removal-windows`, case `disk-windows-eject-reconnect`.
Reports are in that exact subdirectory of `C:\QueueCache-Results`.

The disposable 8 GiB disk was temporarily attached as virtio2; W: was its
single mounted volume. Native topology also identified its hidden volume and
verified both extents belonged to the same disk. The removal node was the
immediate dedicated VirtIO PCI adapter. The maintained worker requested
`CM_Request_Device_Eject` directly; it did not run the product eject preparation,
disable/release the cache, or explicitly flush the filesystem before requesting
removal. This exercises native Windows PnP eject, not a tray-button UI click.
The preparation worker establishes the workload and dirty-cache configuration;
its name does not mean product eject preparation occurred.

Immediately before requesting removal:

| Observation | Value |
|---|---:|
| Cache enabled | true |
| Cache budget | 256 MiB |
| Policy | Fast / Deferred, UNSAFE-DEFER |
| Dirty bytes | 8,437,760 |
| Accepted bytes | 8,437,760 |
| Drained bytes | 0 |
| In-flight bytes | 0 |
| Cache errors / last error | 0 / 0 |
| Cache instance | 12 |
| Native eject result / veto type | 0 / 0 |
| Disk removal observed | true |
| Subsequent native presence result | 13 (no such devnode) |
| Product preparation report | null |

The 8 MiB workload oracle was recorded before removal. Windows accepted the
request and disk absence was observed at 17:48:35 UTC. This establishes that
native eject can proceed with pending cached writes on this tested virtual bus.
It does **not** establish that the driver drained them or that the data survived.
There is no post-removal hash, fresh cache-instance check or lifecycle-counter
trace proving the drain/query route. The pre-request lifetime diagnostics include
other lower I/O; zero drained bytes must not be presented as zero lifetime I/O.

QEMU removed the device/backend but retained `throttle-drive-virtio2`.
One recorded same-slot reattachment failed with an `object-add` duplicate-property
error. Scoped guest reenumeration did not bring W: back. The failed pending
attachment was cleared; the backing disk remains preserved as unused storage and
virtio2 is absent. Existing OS/data disk configuration was unchanged. No reset,
reboot, formatting or recovery writes were performed.

The reconnect deadline expired at 18:03:35 UTC. Final status is
**RESTORATION_FAILED**, case **FAIL** because reconnect verification was missing.
FINISHED.txt, status.json, SUMMARY.md, results.json, run.log, recovery snapshot,
removal handshake and every nonempty raw file (35 total) were inspected and
preserved privately. All four owned workers exited with code 0. This case does
not collect a continuous telemetry/interval window; those files are absent, not
zero-valued observations. Restoration remains deferred; Q:'s saved and active
2 GiB Fast/Idle cache remains unchanged. The disposable disk is left detached.

Plan 74 now records the dirty/enabled native precondition and rejects missing
proof; it still requires exact-disk reconnect and the data oracle before PASS.
Host contracts cover successful native coordination and missing precondition
proof. A final scope recheck and unique cryptographic oracle generation were
added after this VM runner was published; they have build/host coverage, not VM
qualification. The observed VM oracle used the previous deterministic bytes.

## CrystalDiskMark Default: complete

CrystalDiskMark **9.0.3 x64**, Default profile, **5 runs × 1 GiB**, all four
read/write rows; 5-second measurements and 5-second intervals, elevated.
Target Q: was 15% used (31/200 GiB), with **2 GiB QueueCache Fast/Idle** active.
Driver Verifier stayed enabled. No competing verification workload ran during
measurement. These numbers include RAM caching and VM/Verifier overhead; they
are not bare-device speeds or a controlled before/after performance comparison.

| Workload | Read MB/s | Write MB/s | Read IOPS | Write IOPS | Read latency µs | Write latency µs |
|---|---:|---:|---:|---:|---:|---:|
| SEQ 1 MiB Q8 T1 | 13188.047 | 13724.662 | 12577.1 | 13088.9 | 452.14 | 543.67 |
| SEQ 1 MiB Q1 T1 | 8165.327 | 6934.433 | 7787.1 | 6613.2 | 128.03 | 150.70 |
| RND 4 KiB Q32 T1 | 77.030 | 69.959 | 18806.2 | 17079.8 | 1688.98 | 1807.15 |
| RND 4 KiB Q1 T1 | 115.577 | 94.561 | 28217.0 | 23086.2 | 35.31 | 43.18 |

Values are copied from the complete CDM text export. MB/s uses decimal million
bytes; latency is CDM's exported latency, not a p99 score. Export timestamp:
19:45:36 guest local / 17:45:36 UTC. The unchanged full export is
`C:\QueueCache-Results\CDM-Default-20260930-1741\result.txt` on the VM;
private screenshots and a host copy are preserved. The application was closed
and both temporary launch/export scheduled tasks were removed after completion.

## Source verification

Managed solution Release build: zero warnings/errors. Linux management protocol
checks and Windows self-contained management/runner contracts passed. These host
checks execute no driver workload and cannot substitute for the missing live
reconnect/hash qualification. No desktop implementation changed in this update.
