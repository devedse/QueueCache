# Current-boot verification

One foreground CLI, one shared C# runner, one unique output directory. No separate
test application, installer, scheduled task or `--detach` option is needed.

```powershell
# Elevated terminal on the test machine
qcache developer verify Q: --suite quick
qcache developer verify Q: --suite flush-interference --repeats 2 --diskspd C:\Tools\DiskSpd\diskspd.exe --output C:\QueueCache-Results
qcache developer verify Q: --suite full --diskspd C:\Tools\DiskSpd\diskspd.exe --output C:\QueueCache-Results
```

`--output` is a parent directory (default `.`). Each invocation creates
`QueueCache-Verify-<UTC>-<GUID>` beneath it and prints the absolute path. Workload
files must live on the selected disk; their distinct retained directory is recorded
in `workloads.json` or the integrity worker's report/log. Reports should live on a
different disk so telemetry writes do not contaminate the workload.

## Suites (plan version 14)

| Suite | Scope |
|---|---|
| `quick` | Existing file-integrity checks: seeded writes/overwrites, random updates, live reads, flush and filesystem checks. No policy sweep. |
| `policies` | Sector regressions with diagnostics-V2 zero-lower-attempt admission proof; a 60-second fitting hot-set test of serialized foreground writes and cached reads while Idle draining progresses; then six cache configurations, retained-data checks and disabled-cache byte oracles. Restores runtime configuration and requires the attribution driver. |
| `pressure` | Focused opt-in trigger and capacity checks on new files: Deferred first-dirty age under repeated overwrites, Idle last-write timing and an isolated Balanced high-watermark boundary; Automatic and Fixed 50/100 capacity backpressure; Fixed 0 ordered quota fallback; final disabled-cache byte oracles. Uses lower-write-attempt counters to distinguish eligibility/start from completion, a temporary 64 MiB cache, controlled 25 ms lower-write delay and an 80 MiB capacity file. No DiskSpd. Not included in `full`. |
| `trim-diagnostic` | Existing file-integrity workload on fresh files with cache routing enabled, then disabled; records exact file-level TRIM rejection codes and restores original settings. No DiskSpd. Filter remains attached; unsupported TRIM stays SKIP. Not included in `full`. |
| `trim-file` | Driver-independent file-only probe: new 3 MiB file, middle 1 MiB TRIM, untouched guards and flushed rewrite oracle. Rejects boot/system/paging disks and changed disk identity. No cache controls, recovery snapshot or driver telemetry; restoration is explicitly not required. Unsupported TRIM is top-level SKIP with run status COMPLETED_WITH_SKIPS (diagnostic collected, not correctness passed). Not in `full`. |
| `flush-interference` | Automatic/Fixed50 × requested application flush/control × repetitions. Eager, QD128 writer, 25 ms lower-write delay, hot reader. `--repeats 2` gives eight cases. |
| `performance` | 144 hot-reader cells at defaults: allocation × Eager/Idle × delay 0/25 ms × writer QD8/32/128 × alone/loaded × three repeats. Plus 60 sequential/random read/write and mixed scaling cells, cache off/on, QD1/32. |
| `full` | `quick` + `policies` + `performance` + focused flush matrix (218 top-level cases at defaults). |
| `write-performance` | Separate focused matrix: random 4 KiB Q1/32 and sequential 1 MiB Q1/8, one thread, Automatic allocation, cache Off/Eager/Idle, detailed driver timing off/on, three repeats (72 cases). Not implicitly included in `full`. |

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
Only one runner/recovery may own a physical disk. Do not change the same cache from
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
