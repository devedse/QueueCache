# Known issues and verification gaps

This file describes the current QueueCache implementation. Removed historical
engine defects remain available in Git history and must not be reported as current
bugs without a reproduction on the current driver.

## Experimental trust and durability

The driver and installer are test-signed development artifacts. Fast mode is
intentionally volatile: eligible writes, write-through requests and application
flushes may complete before the lower device. Sudden loss can lose acknowledged
data and damage the filesystem. Strict mode and explicit administrative flushes
preserve their lower-I/O boundary, but neither makes an experimental driver a
production storage product.

Only isolated VMs and clean secondary disks are currently supported for testing.
Production signing, unattended recovery and certification are not implemented.

## Lifecycle scope remains incomplete

Normal secondary-disk restart checks exist, but boot/system-disk, paging,
hibernation, crash-dump, surprise-removal and full power-transition acceptance are
still open. The class filter starts inactive and forwards I/O until a task is
explicitly configured. That pass-through design is not a substitute for the A07–A09
lifecycle campaign.

Exact installed 0.4.75.1 identified the protected C: registrations by type. With
a live C: pagefile and kernel dump configured it reported `Paging=4`, `Dump=1`.
After both were removed and the VM rebooted it reported `Paging=2`,
`Hibernation=0`, `Dump=0`, while WMI and the filesystem showed no pagefile,
swapfile or hiberfile. Active C: verification therefore remains blocked until
the remaining paging-type kernel paths are explained or supported. The combined
count remains authoritative; do not bypass it merely because
`Win32_PageFileUsage` is empty. Plan 22 allows only the disabled/pass-through
image baseline in this state.

The pre-A01 large-BMP/Paint/Photos BSOD has no surviving dump or BugCheck event
in the restored snapshot and is unresolved. Plan 21 supplies matching uncached
and active-cache 349 MiB deterministic image workloads with required admission
and post-release evidence, not a root-cause fix. A 4 GiB cache on the
8 GiB guest is only a memory-pressure hypothesis; new admission retains at least
2 GiB or 25% physical-memory headroom and the first active test is capped at
512 MiB.

Do not use automatic reboot, driver deletion or dirty-data discard as failure
recovery. Preserve the VM snapshot and recorded installation backup outside the
guest. A faulted cache must be inspected and explicitly recovered.

## Remaining RAM-first and ordering coverage

Fitting aligned and partial writes have maintained zero-lower-attempt admission
checks, and delayed overwrite/retention cases have passed on the current test VM.
The following remain incomplete:

- bounded allocation-failure, cancellation and teardown races;
- oversized and quota-boundary requests under concurrency;
- exact cutoff semantics for concurrent explicit flush and live policy/resize;
- cold lower reads competing with drains and sustained capacity pressure;
- multi-disk memory pressure and starvation;
- all fault-injection points across every parallelism/retention combination.

These are verification gaps, not permission to relax capacity backpressure,
ordering, failure propagation or explicit durability.

## TRIM support is conservative

Range-aware TRIM retirement is not complete. The maintained file-only comparison
currently observes unsupported file TRIM (Win32 326) on the test VM with and
without the filter, so it cannot prove partial-range behavior. Unknown or
media-changing controls retain conservative ordering and invalidation. Malformed,
overlapping and failed TRIM requests remain open work.

## Configuration changes are not transactional

Apply drains and disables before changing the preset, budget and policy. If a later
step fails, it reports failure and does not pretend to roll back atomically. A
failed resize can therefore leave the cache disabled. Saved profiles are updated
only after a successful apply and are identity/size checked during restore.

Live cutoff flush, policy change and resize semantics are planned work. Current
operators should use the supported task commands and inspect state after failures.

## Performance acceptance is incomplete

Focused Q1/Q32 and drain-attribution runs exist, but they are not a complete
performance verdict. Full 72-case small-write and broader mixed-workload matrices
must use the same DiskSpd binary/hash, budget and repetitions. `MEASURED` means a
sample was collected, not that it passed a performance requirement. Lifetime
counters must not be presented as score-window counters.

## UI and telemetry limitations

The desktop shows live state and bounded history, but interval/lifetime labeling,
sample-staleness presentation and complete trigger/wait visibility remain partial.
Unavailable fields must stay unavailable rather than becoming zero. Closing the UI
does not stop caching.

## Installer and compatibility debt

The `qcachelab` service/binary identity, `LabAllowedDriverKey` registry value and
`labWriteCache` package metadata are retained for upgrade compatibility. They do
not denote a second driver edition. A coordinated identity/schema migration belongs
to installer acceptance work. Uninstall retains kernel service/binaries when they
may still be needed for post-reboot recovery.

For task order, exact evidence and acceptance boundaries, use the
[private-alpha handover](PRIVATE_ALPHA_IMPLEMENTATION_HANDOVER.md) and
[RAM-first tracker](RAM_FIRST_IMPLEMENTATION_TRACKER.md).
