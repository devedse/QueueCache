# Testing QueueCache

Everything needed to test QueueCache, in one place: host tests, VM verification
runs, comparing performance, and the README benchmark. Results and decisions go
in [performance findings](PERFORMANCE_FINDINGS.md); confirmed gaps in
[known issues](KNOWN_ISSUES.md).

There are three levels:

| Level | Where | Proves |
|---|---|---|
| Host tests | Any OS with the .NET SDK, and CI | Protocol layouts, plans, parsing, orchestration and UI contracts. Never touches a driver. |
| Verification runs | The Windows test VM, installed build | Real driver behavior: bytes, policies, lifecycle and performance. |
| CrystalDiskMark | The Windows test VM | The README's numbers and screenshots. |

A successful build or host test is not driver verification, and a VM run is not
production certification.

## 1. Host tests

```powershell
dotnet run --project tests/QueueCache.Management.Tests -c Release
dotnet run --project tests/QueueCache.Desktop.Tests -c Release
```

Windows CI also builds the drivers (warnings are errors), runs the CLI contract
checks in `build/Test-Cli.ps1`, test-signs the package and builds the installer.
Some runner contracts only run on Windows; the cross-platform subset runs on Linux.
To smoke-test the DiskSpd XML parser with a real binary, set `QCACHE_TEST_DISKSPD`
to either supported DiskSpd first; it creates and deletes one 16 MiB temp file.

## 2. The test VM

**Machine.** A recoverable 4-vCPU Windows 11 VM. `Q:` is a non-OS disk with a
2 GiB Fast cache; never test on the OS disk unless a `system-*` suite asks for it.
Volume-level suites run on a lab VHDX:

```powershell
qcache developer lab-disk create C:\QueueCache-Lab\VolumeLab.vhdx   # two 8 GiB NTFS volumes + one raw volume
qcache developer lab-disk attach C:\QueueCache-Lab\VolumeLab.vhdx   # Windows does not reattach it after a restart
qcache developer lab-disk detach C:\QueueCache-Lab\VolumeLab.vhdx
```

**Installing a build.** Run the CI installer, restart, then confirm the loaded
drivers are the intended ones: `qcache developer driver loaded` lists their file
hashes. A version string or the registered path is not proof of what is loaded.
Run correctness checks of new driver code under Driver Verifier
(`verifier /flags 0x209bb /driver <filter>.sys qcramdisk.sys`, restart). For any
performance number, Verifier must be off (`verifier /reset`, restart): it slows
the drivers several times over.

**Before every run:**

- Elevated terminal; nothing else testing or benchmarking; close the QueueCache
  tray app if the run changes caches it shows.
- Close background indexers and launchers, which start again at every sign-in:
  Steam (`steam.exe -shutdown`), Everything (`Everything.exe -exit`) and its
  `Everything` service (`Stop-Service Everything`). Their disk activity fails the
  runner's strict "no disk I/O" checks. Recheck after the post-restart wait:
  delayed startup apps can appear after an early closure. Start them again afterwards.
- For performance: Verifier off, at least 15 minutes after a restart, normal
  process priority, NTFS last-access updates off
  (`fsutil behavior set disablelastaccess 1`), and `qcache policy flush Q:` so the
  cache starts clean.
- Enough free space on `Q:`: preparation refuses below 3-5 times the cache budget.
- No delay or fault hooks armed; the runner cannot read their original values.

**Dedicated performance VM baseline (owner requested, 2026-10-15).** Microsoft
Defender Antivirus and Windows Search indexing are disabled between runs as well
as during them. Before measuring, verify that `WinDefend`, `WdNisSvc`, `MDCoreSvc`
and `WSearch` are stopped with Disabled startup, the Defender `WdFilter` is absent
from `fltmc filters`, and Defender's scheduled scan/maintenance tasks are disabled.
Record these effective states with the result; policy values alone did not stop
Defender on this Windows build. Do not restore these components to their former
enabled state after a run. Recheck after Windows updates and restarts.

This is a change to the test environment, not a measured QueueCache speedup.
Historical figures keep their original environment. For a new comparison, measure
every product/control under the same recorded Defender and indexing settings;
do not combine the new baseline with old results to claim a driver improvement.

## 3. Running tests: one command

`qcache developer verify` is the only supported runner. Pick a campaign (several
suites in a row with one combined report) or a single suite:

```powershell
qcache developer verify Q: --campaign release-performance --lab-ntfs W: --budget-mib 2048 --repeats 3 --duration-seconds 10 --diskspd C:\Tools\DiskSpd\DiskSpd64.exe --pause-backing-cache --output C:\QueueCache-Results
qcache developer verify Q: --campaign experiment --focus-suite ram-read-async --lab-ntfs W: --budget-mib 2048 --repeats 3 --diskspd C:\Tools\DiskSpd\DiskSpd64.exe --pause-backing-cache --output C:\QueueCache-Results
qcache developer verify Q: --suite quick --output C:\QueueCache-Results
```

| Campaign | Contains | Typical time |
|---|---|---:|
| `smoke` | `quick` and partial-read accounting on the lab NTFS volume | ~1 min |
| `experiment` | Exactly one narrow suite (`ram-read-async`, `ram-read-async-lifecycle`, `ram-read-coordination`, `ram-write-reference`, `cache-read-reference`, `cache-read-bookkeeping`, `cache-read-misses`, `stress-races`) | 1-15 min |
| `focused` | One named suite plus the retained correctness checks (partial reads, paging, policies, pressure, ordering/faults) | 10-30 min |
| `performance` | Product-default regression: correctness checks, RAM reference, RAM lifecycle, recall, 2-minute sustained, map cost, ordering/faults | ~1 h |
| `release-performance` | `performance` plus the 72-case write matrix and a 30-minute sustained phase | ~2 h |

`--lab-ntfs` names the attached lab NTFS volume. `--pause-backing-cache` lets the
runner pause `Q:`'s cache while lab phases run (the lab VHDX lives on `Q:`) and
restores it afterwards. There is no `--detach`: run the foreground command and let
it finish. Results go to a new folder under `--output`.

Use the smallest thing that answers the question: a single suite or `experiment`
while iterating, `focused` when a change touches the retained correctness paths,
and `release-performance` once on the final build.
From plan 126, the retained correctness suites contain 93 subchecks: 22 partial
reads, 8 paging, 42 policies, 13 pressure and 8 ordering/faults. A focused
`stress-races` campaign adds its five checks, for 98 in total.

**Long runs from an agent.** Start the foreground campaign command with the
harness's built-in background-task mechanism. Wait for completion, then read that
run's evidence and use `verify-completion`.

## 4. Suites

`qcache developer verify --help` lists every suite with its requirements. The
ones in use:

| Suite | What it checks | Notes |
|---|---|---|
| `quick` | File integrity through the cache | Default; no DiskSpd |
| `policies`, `pressure` | Sector edge cases, each policy, drain/age/watermark/backpressure, disk bytes afterwards | No DiskSpd |
| `paging-coherence`, `partial-read-accounting`, `ordering-faults` | Mapped/paging coherence, partly cached reads (plan 126: 16 simultaneous cold/partial readers with byte and pin-retirement checks), write ordering under injected lower failures | In every campaign; `ordering-faults` runs last |
| `volumes`, `trim-cache` | Several volumes on one disk; TRIM | Lab VHDX volume |
| `stress-races` | Both lab NTFS volumes at once under concurrent writers of 4 KiB to 4 MiB, cancelled overlapped I/O, cache flush/resize/teardown/drop/delay/allocation faults, refused settings and RAM disk churn; then fault and Retry at drain parallelism 1, 2 and 4; every byte read back. Plan 128 flushes filesystem metadata before fault injection and after retry, and requires retained-byte drainage plus a clean result | Lab VHDX; `--soak-seconds` (default 240); run under Driver Verifier and without |
| `managed-provider`, `ram-disk`, `vhdx-backed`, `image-in-ram`, `managed-cli`, `managed-broker-restart`, `managed-lifecycle-*` | Managed disk creation, checkpoints, broker restart and restart lifecycle | Owned fixtures only |
| `disk-removal`, `disk-removal-windows` | Eject with pending writes, reconnect | Disposable disk identity required |
| `system-*` | Guarded checks on the OS disk | Explicit acknowledgement and off-disk output |
| `write-performance` | 72 fitting-file write cases: random 4K Q1/Q32, sequential 1M Q1/Q8, Off/Eager/Idle, timing off/on | `--budget-mib 2048`; ~35-55 min |
| `cache-recall`, `cache-sustained`, `cache-map-cost`, `cache-concurrency` | Read recall, long mixed use with byte oracles, map polling cost, concurrent streams. Plan 129 primes the write profile before RAM-only write counters start; the scored window still requires zero lower I/O | `cache-sustained` defaults to a 30-minute soak |
| `ram-read-reference` | RAM-disk Direct and Standard reads, 1M and 4K, one and four threads | Product default |
| `ram-read-async` | Same-build off/adaptive comparison of asynchronous split reads, 18 windows | Prints a screen verdict |
| `ram-read-async-lifecycle` | Verified overlapped reads, the in-flight cap, ending Direct access under load | Correctness only |
| `ram-write-reference` | RAM-disk 1M writes from one and four programs, plus copies into and out of cold memory without the driver | Tells the machine's copy limit from the driver's |
| `cache-read-reference` | Cached 1M reads from one and four programs, with lock and queue time per read | Attribution only |
| `cache-read-misses` | Same-build flags 7/15, cold 4K Q32 and 1M Q8 on a file twice the cache plus fully cached controls; three alternated repetitions (24 windows), plan 127 | Reports throughput, p99 and worst-read latency; restores original flags |
| `tree-decision` | Same-build 40,000-file / 1.2 GiB create-copy-read-hash-delete workload; cache-off and flags 7/15 at 256 KiB × 2 / 1024 KiB × 4, three rotated repetitions | Timing OFF, Idle draining, byte/hash oracle, disk-idle settles, per-phase system-cache memory and lower-I/O attempt snapshots; collection only, about 1–2 hours |
| `read-tail-decision` | Fixed 100,000-operation cold-random 4 KiB Q32, resident-random 4 KiB Q32, resident-sequential 1 MiB Q8 and fitting foreground-write 4 KiB Q32; flags 7/15 in three rotated repetitions (24 cases) | Timing OFF; per-operation p50/p99/p99.9/max and exact >50/100/250/500 ms counts, lower-I/O snapshots, resident warm-up and durable write-byte oracle after a stable clean-cache wait; collection only |
| `drain-flush-decision` | Explicit cache Flush of a deterministic 512 MiB dirty seed at 256 KiB × 2 and 1024 KiB × 4, three alternated pairs (6 cases) | Timing OFF; owned control worker ready/start handshake; duration starts immediately before the Flush call; drain batches/bytes, lower write/flush attempts and persisted-byte oracle; collection only |
| `cache-read-bookkeeping` | Same-build comparison of cached read bookkeeping (copy flags 11 and 15; 3 and 7 before plan 125) | Prints a screen verdict |
| `sequential-resident`, `drain-decision`, `flush-interference`, `performance`, `full` | Older focused performance matrices; `drain-decision` compares drain batch 256/1024 KiB x parallelism 1/2/4 (plan 125) | Use when the area changes |
| `cache-layout*`, `caller-backoff`, `priority-cost`, `priority-affinity`, `ram-read-scheduling`, `ram-read-coordination`, `ram-read-attribution` | Concluded comparisons and diagnostics | Results are in the findings; rerun only for a new question |
| `compare-ram`, `compare-cache`, `compare-cache-cold`, `compare-durability-write`/`-churn`/`-verify`, `compare-snapshot` | Other products against QueueCache: see below | `--product` required; no QueueCache controls |
| `compare-drain` | Two fixed new-file workloads through explicit product drain; three repeats, two median/range rows per provider | Explicit `--provider`, `--product`, `--budget-mib 2048 --repeats 3`; no DiskSpd |

**Comparisons with other products** (plan 121). These suites measure any RAM
disk or cached volume the same way, without QueueCache's controls, hooks or
telemetry, so QueueCache and its competitors get identical treatment. The focused
`compare-drain` suite below uses explicit provider controls for drainage and read
verification. Each needs
`--product "<name> <version> <mode>"`. QueueCache's drivers must be loaded
exactly when the product is QueueCache, so one product never runs on top of
another. Every run records the loaded third-party drivers, the storage filter
lists, Secure Boot and Memory Integrity settings, and memory snapshots.

- `compare-ram` runs on a 4 GiB RAM disk:
  - 1M Q1/Q8/Q8T4 and 4K Q1/Q32/Q32T4 reads and writes (36 windows at three
    repeats), with p50/p99/p99.9 latency and CPU;
  - memory used before and after a 2 GiB fill, and after deleting it;
  - a seeded 40,000-file, 1.2 GiB tree: create, copy with four threads, read and
    hash, delete;
  - a final byte check.
- `compare-cache` runs on a cached volume, keeping its workload files in
  `<volume>\QCBench` so later runs overwrite the same disk areas:
  - hot CrystalDiskMark shapes, buffered and write-through writes;
  - random and sequential reads of an 8 GiB file larger than the cache;
  - a 1 GiB absorb, 512 MiB plus `FlushFileBuffers`, and 8 GiB sustained writes
    with per-second throughput;
  - a five-minute 70/30 mix and the file tree.

  After writes it waits for the disk below to go quiet: at least 45–90 s, at
  most 300 s.
- `compare-cache-cold` needs a cache that was emptied beforehand, with the
  product's own command or a restart. It times a first and second read of a
  1 GiB file and two random-read windows.
- `compare-durability-write` works in three steps:
  1. It writes generation 1 and lets it settle.
  2. It writes generation 2: an application-style durable save, raw writes
     without a flush, and 10,000 new small files.
  3. It flushes the evidence volume before `FINISHED.txt`.

  Reset the VM as soon as `FINISHED.txt` appears. After the restart, run
  `compare-durability-verify --oracle <write run>\durability-oracle.json`. It
  counts what survived and runs `chkdsk /scan`. These are measurements, not
  verdicts.
- `compare-durability-churn` (plan 122) tests a power cut while a write-back
  cache is part-way through writing its data to the disk:
  1. It writes 2,000 base files (4–32 KiB) and lets them settle.
  2. For 40 s it creates 200 files per second without a flush. Every eighth step
     it deletes a base file (the first half, so their space is reused), and from
     step 1,000 also the file created 1,000 steps earlier.
  3. Its oracle records when each file was created and deleted; `FINISHED.txt`
     follows as for `compare-durability-write`.

  Reset the VM while the disk below is still being written after `FINISHED.txt`
  (the cache writing out the churn). Then `compare-durability-verify` reports
  the base files and the churn files by their age when the churn ended
  (30 s or older, 20–30, 10–20, under 10 s), each intact, damaged or missing, and
  `chkdsk /scan`. From plan 123 it also says what damaged files hold: nothing
  (empty), part of their data (short), zeros, another test file's data (old
  contents of reused space), or other bytes. From plan 124 a file that Windows
  cannot read (NTFS reports it corrupted) counts as damaged, "unreadable", and
  the check continues to `chkdsk`.
- `compare-snapshot` records memory and drivers only, for example before
  creating a disk.

**Focused write plus drain comparison (plan 149; workload unchanged from 148).** `compare-drain` runs two
fixed workloads three times each, alternating large/small order:

| Workload | Requests | Files |
|---|---|---|
| Large file | Sequential 1 MiB, four outstanding writes on an overlapped handle | One new 1 GiB file |
| Small files | Unbuffered 4 KiB, four workers | 4,096 new 64 KiB files (256 MiB total) in a fixed shuffled order |

A unique, incompressible 1 GiB SplitMix64 payload is precomputed with all
SHA-256 oracles outside timing; small files use its first 256 MiB. File/directory
creation and close are timed;
shuffled names do not establish random disk placement. No overwrites are scored.

Run each provider alone on the same non-OS NTFS disk after section 2 preflight.
Direct and PrimoCache require QueueCache drivers absent. QueueCache requires
active Fast, 2 GiB, timing diagnostics off. On the experiment branch, batches may
be 1024/2048 KiB and parallelism 4/8; normal defaults remain 1024 KiB × 4.
Apply each runtime profile explicitly before its run, name it in `--product`,
and check the recorded `compare-drain-recovery.json` original state and case
snapshots. The runner rejects settings changes within a run. Use the same build
for controls and candidates, changing one factor at a time; bracket candidate
runs with a repeated 1024 KiB × 4 control. Restore captured runtime/saved settings
afterward. This branch raises the native/managed experiment limits to 2 MiB × 8
and reserves staging for that ceiling in every arm. Check the lower adapter's
transfer/page/alignment limits before testing 2 MiB; these results are not a
qualification of new production defaults.
PrimoCache requires shared 2 GiB L1, no L2, 4 KiB blocks, Native Defer-Write 10 s,
prefetch off and its GUI closed. The runner checks these settings.

```powershell
qcache developer verify Q: --suite compare-drain --provider Direct --product "Direct disk" --budget-mib 2048 --repeats 3 --output C:\QueueCache-Results
qcache developer verify Q: --suite compare-drain --provider QueueCache --product "QueueCache Fast 2 GiB" --budget-mib 2048 --repeats 3 --output C:\QueueCache-Results
qcache developer verify Q: --suite compare-drain --provider PrimoCache --product "PrimoCache 2 GiB defer-write" --budget-mib 2048 --repeats 3 --output C:\QueueCache-Results
```

`--provider` selects explicit drain and verification controls independently of
`--product`; `--primo-cli` overrides the default `%ProgramFiles%\PrimoCache\rxpcc.exe`.
There is no DiskSpd phase or case filter. Each run needs 7 GiB free and owns a
unique target folder; only successfully verified case data are deleted.

Before each case, flush volume metadata, drain, drop clean contents and require
zero pending data and disk queue. Start the disk observer before releasing ready
writers. The headline **MB/s = payload bytes / monotonic elapsed seconds / 1,000,000**
uses 1,073,741,824 bytes for large and 268,435,456 bytes for small files. It
includes all writes, final volume metadata flush, explicit QueueCache `Flush`
or PrimoCache `dwflush -v q -s`, and confirmation of zero dirty/in-flight bytes
or Deferred Blocks plus zero disk queue. Ordinary Windows flush completion alone
is insufficient. Primo CLI overhead is included and recorded separately.
Primo's [Deferred Blocks](https://www.romexsoftware.com/en-us/primo-cache/terms-performance.html)
counts blocks still awaiting disk writes; parse the integer, with its optional
display percentage, and reject missing or malformed counters.

Record application, explicit-drain and confirmation durations, UTC boundaries,
and physical-disk `IOCTL_DISK_PERFORMANCE` write bytes/operations/queue depth every
100 ms. Telemetry must bracket the score without gaps over two seconds; missing,
regressing or insufficient byte counters fail collection. These virtual-disk
observations do not prove power-loss durability. Preparation, verification and
any quiet hold are outside the score.

Persist each score before untimed verification. Pause the product, confirm
bypass, read and SHA-256-check every file unbuffered, then restore in `finally`,
including after failures. The recovery journal records target identity and
original state. After confirming owned processes stopped, use the standard
`qcache developer verify-recover <run>` if restoration failed.
QueueCache pause confirmation records each full state and permits up to 30 seconds
for a transient draining barrier to settle. Identity/settings changes, faults or
unexpected enabled state fail immediately; Disable is never replayed.

Reports require all six unique cases, every hash, telemetry and successful
restoration. `comparison.json`/`COMPARISON.md` report two medians with ranges,
application/drain times and backing write sizes per provider. Three complete
provider runs supply the six published values; never fill gaps from partial runs.

Results go in `COMPARISON.md` and `comparison.json` in the run folder, as medians
with ranges. Bytes and flushes that reached the virtual disk come from the
hypervisor's per-disk counters, joined by each step's `compare.<step>.interval.json`.

DiskSpd: Microsoft DiskSpd (`amd64\diskspd.exe`) or CrystalDiskMark's bundled
`CdmResource\DiskSpd\DiskSpd64.exe`, both in XML mode. Keep the same binary
(the manifest records its hash) when comparing.

## 5. Reading results

Wait for `FINISHED.txt` (a missing marker means interrupted or still running),
then read the run folder:

| File | Meaning |
|---|---|
| `status.json` | `COMPLETED`, `INCOMPLETE`, `CANCELLED` or `RESTORATION_FAILED`, with the failure |
| `SUMMARY.md`, `results.json` | Per case: `PASS` is a correctness check; `MEASURED` means a score was collected, **not** that it is fast enough |
| `run.log` | The timestamped console progress |
| `manifest.json`, `recovery.json` | Plan, options, expected cases, identities; the settings to restore |
| `restoration.json` / `restored.json` | Independent restoration result |
| per-case `*-before/after.json`, `*.score.json`, `*-telemetry.jsonl`, `*-interval.json`, raw DiskSpd output | The evidence behind each number |

Current contracts (plan 149):

- **Telemetry (plans 130/131):** an owned thread samples every 200 ms, separately
  from persistence, with a bounded 256-sample buffer. The first persisted sample
  publishes readiness; completion waits for every sample to persist. Cover the
  full process interval with no gap over two seconds. Buffer overflow, a real
  coverage gap or missing counters fails collection; timestamps are never filled.
  Per-stage timings distinguish collection delay from serialization/persistence.
- **Drain completion (plan 132):** `drain-decision` allows 300 seconds for process
  completion because an explicit flush may hold the final I/O beyond the unchanged
  score window. Full-process telemetry and independent restoration still apply.
- **Copy flags (plan 133):** capture, restore and read back the exact original
  value, including zero. Legacy read-miss recovery may use its recorded
  `copy-flags-original.json` only when identity/generation/configuration match.
  Missing original evidence or wrong readback cannot produce RESTORED.
- **File tree (plan 134):** five arms, three rotated repetitions, exact file/byte
  counts and hashes, disk-idle settles, system-cache memory and lower-I/O attempts.
  A `--case-filter` selection is diagnostic, not a complete matrix. To attribute
  scanning costs, use Defender's performance recorder/report and process CPU;
  keep the documented environment fixed across arms. Findings belong in
  [performance findings](PERFORMANCE_FINDINGS.md).
- **Equal-work tails (plans 135/140/141):** 100,000 operations per case, exact
  latency samples/threshold counts, cold and resident controls, and deterministic
  foreground-write content checks after flush/eviction. Before each start and
  write oracle, require two clean seconds with unchanged lower-I/O counters,
  allowing at most five recorded attempts. Timing OFF; no DiskSpd or case filter.
- **Explicit drain (plans 137/142/146):** three alternated pairs of 256 KiB × 2
  versus 1024 KiB × 4, each with a deterministic 512 MiB dirty seed. An owned
  worker is ready before release and times the Flush call itself. Timing stays
  OFF; require seed, lower-I/O, health, clean-end and byte-oracle evidence. The
  byte oracle follows cache Disable, an additional control that must remain
  visible when judging durability. Plan 146 removed the ineffective held-handle
  trial. The disabled-baseline repeat is incomplete; do not aggregate its samples.

```powershell
qcache developer verify Q: --suite tree-decision --budget-mib 2048 --repeats 3 --output C:\QCResults\tree-decision
qcache developer verify Q: --suite read-tail-decision --budget-mib 2048 --repeats 3 --preparation-flush-seconds 900 --output C:\QCResults\read-tail-decision
qcache developer verify Q: --suite drain-flush-decision --budget-mib 2048 --repeats 3 --preparation-flush-seconds 900 --output C:\QCResults\drain-flush-decision
```

These suites collect evidence, not a performance verdict or latency SLA. Compare
all paired repetitions, raw samples/counters and content checks. Read-tail's
scattered write oracle took 251–258 seconds on slow storage, hence the larger
preparation deadline above. `drain-flush-summary.md` requires all six cases;
partial runs cannot produce an accepted aggregate.

There is no maintained foreground-overlap or Strict-impact suite: both failed
their seed contracts and were removed. Reasons and run identities remain in
[performance findings](PERFORMANCE_FINDINGS.md).

A campaign folder holds one child run per phase plus `completion.json`.
When copying a campaign from Windows, verify every file against a guest-side
SHA-256 manifest. A plain recursive SFTP copy omitted long case filenames in the
nested campaign output while returning success. Archive using Windows extended
paths (`\\?\`) and verify the archive entries/hashes after extraction; a
successful transfer exit alone does not prove that the evidence is complete.

`qcache developer verify-status <run>` and `verify-completion <campaign>` read a
run without touching the driver. Never combine incomplete repetitions into a
result. If restoration fails, stop: check the recorded owned processes have
exited, then run `qcache developer verify-recover <run>`, which refuses an
identity mismatch or a live owned process. The runner never formats, reboots or
deletes test data on its own.

## 6. Deciding whether a change is faster

1. Add the change behind a runtime switch so old and new run on **the same
   build**; compare them in the smallest suite that covers the change.
2. Alternate old/new order across at least three repetitions, same DiskSpd binary,
   diagnostics and tracing off.
3. Accept when the target improves by at least 5%, every pair improves and the new
   range sits above the old one; investigate any repeatable loss of 3% or more in
   a control. One pre-declared fix is allowed; otherwise remove the change.
4. Check correctness of the new path (Driver Verifier, byte checks), then run
   `release-performance` once on the final build.

Do not judge a change by comparing with a different day's run: shared VM storage
and Windows background work move results. `ram-read-async` is the worked example
([findings](PERFORMANCE_FINDINGS.md#ram-disk-asynchronous-split-reads-accepted-pr-9)).

## 7. README benchmark (CrystalDiskMark)

The README's performance table and the `cdm-cache.png` / `cdm-ram-disk.png`
screenshots come from real CrystalDiskMark 9.0.3 runs on the test VM.

**Setup.** Installed release, Verifier off, the pre-run checklist above, Balanced
power plan. Cache column: `qcache policy apply Q: --budget-mib 2048
--accept-volatile-flush` and `qcache policy drop-clean Q:` before each run. RAM
disk column: `qcache disk create --mode ram --size-mib 4096 --letter T`, removed
afterwards with `qcache disk stop <id> --discard` and `qcache disk remove <id>`.
Without-cache column: `qcache policy pause Q:` (then `resume`). CrystalDiskMark
defaults (5 passes, 1 GiB, MB/s), Default theme, Theme → Zoom → 150%, a one-line
comment such as `QueueCache RAM disk T: (4 GiB)`. Run **All** twice per column and
keep the faster run; measure the columns you change in one session and note the
version in the README.

**Driving it remotely.** CrystalDiskMark must run in the signed-in user's session:
start it from a scheduled task for that user with `-RunLevel Highest` and
**`New-ScheduledTaskSettingsSet -Priority 4`** (normal priority; the default is
below normal). Settings live in `DiskMark64.ini` next to the program (back it up;
`TestCount=4` is 5 passes, `TestSize=6` is 1 GiB, `DriveLetter` is an index from A,
`ZoomType=150`). Press buttons with `SetCursorPos`/`mouse_event` from a helper in
the same session, read scores from the UI Automation tree (each score box's name
is its value), and capture the window's visible frame
(`DWMWA_EXTENDED_FRAME_BOUNDS`). Close it with its own close button so it saves
its settings. Lab helper scripts are kept outside the repository.

**Pitfalls measured on the test VM:**

| Pitfall | Effect | Avoid by |
|---|---|---|
| Below-normal priority (Task Scheduler's default) | Cached SEQ1M Q8 read 16-18 instead of 29-31 GB/s | Priority 4; check `Get-Process DiskMark64` shows Normal |
| Leftover data in the cache | Part of the test file read from disk: 5-20 GB/s | `qcache policy drop-clean Q:` before each run |
| Last-access timestamps | A few KiB of metadata writes inside score windows | `fsutil behavior set disablelastaccess 1` |
| Background indexers and launchers | Unrelated disk reads; failed strict checks | Close Everything (and its service) and Steam |
| Driver Verifier left on | SEQ1M Q8 about 15 → 6 GB/s | `verifier /reset` and restart |
| Right after a restart | Lower, noisier results | Wait 15 minutes |
| A notification over the window | Covers part of the screenshot | Dismiss it first |
| Shared network storage | Uncached numbers vary 2× within a day | Compare uncached numbers only within one session |

CrystalDiskMark runs DiskSpd per test with these options, useful to reproduce one
by hand (add `-Rxml` for parseable output):

```text
SEQ1M Q8T1 read   -b1024K -o8  -t1 -W0 -S -w0         -ag -d5 -L <file>
SEQ1M Q1T1 read   -b1024K -o1  -t1 -W0 -S -w0         -ag -d5 -L <file>
RND4K Q32T1 read  -b4K    -o32 -t1 -W0 -S -w0   -r    -ag -d5 -L <file>
RND4K Q1T1 read   -b4K    -o1  -t1 -W0 -S -w0   -r    -ag -d5 -L <file>
writes            the same with -w100 -Z1024K
```

## 8. Adding or changing a suite

Scenarios live in `src/QueueCache.Developer/Verification`; the CLI only binds
arguments. Add contract tests to `tests/QueueCache.Management.Tests`
(`VerificationRunnerTests.cs`, and `DiagnosticContracts.cs` for checks that must
also run on Linux). When a workload or measurement contract changes, bump
`VerificationPlan.Version`, keep every case ID unique and immutable, and update
the suite table above and `--help`. Keep the byte, identity, telemetry and
restoration guards: never turn a missing field into zero or a missing completion
into a pass. Detailed per-suite contracts up to plan 119 are kept as history in
[history/DEVELOPER_VERIFICATION.md](history/DEVELOPER_VERIFICATION.md); the code
and `--help` are authoritative.
