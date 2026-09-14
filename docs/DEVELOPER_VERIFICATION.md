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

## Suites (plan version 2)

| Suite | Scope |
|---|---|
| `quick` | Existing file-integrity checks: seeded writes/overwrites, random updates, live reads, flush and filesystem checks. No policy sweep. |
| `policies` | Existing six cache configurations, retained-data checks and disabled-cache byte oracle; restores runtime configuration. |
| `flush-interference` | Automatic/Fixed50 × requested application flush/control × repetitions. Eager, QD128 writer, 25 ms lower-write delay, hot reader. `--repeats 2` gives eight cases. |
| `performance` | 144 hot-reader cells at defaults: allocation × Eager/Idle × delay 0/25 ms × writer QD8/32/128 × alone/loaded × three repeats. Plus 60 sequential/random read/write and mixed scaling cells, cache off/on, QD1/32. |
| `full` | `quick` + `policies` + `performance` + focused flush matrix (218 top-level cases at defaults). |

Parameters: `--budget-mib 1024`, `--repeats 3`, `--duration-seconds 10`,
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

Restoration retries only the explicit transient-draining condition, for up to
30 seconds within the worker deadline. Faults, removal, suspension and identity
changes are not treated as transient draining. Original settings, timing and saved
profiles are still checked before reporting restored. After a failed run, confirm
its owned processes have stopped and use the newly installed CLI's
`qcache developer verify-recover <failed-run-directory>` before starting another
suite. Recovery creates its own subfolder/log and does not relabel old results.

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
