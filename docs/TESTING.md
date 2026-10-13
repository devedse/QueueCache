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
  runner's strict "no disk I/O" checks. Start them again afterwards.
- For performance: Verifier off, at least 15 minutes after a restart, normal
  process priority, NTFS last-access updates off
  (`fsutil behavior set disablelastaccess 1`), and `qcache policy flush Q:` so the
  cache starts clean.
- Enough free space on `Q:`: preparation refuses below 3-5 times the cache budget.
- No delay or fault hooks armed; the runner cannot read their original values.

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
| `experiment` | Exactly one narrow suite (`ram-read-async`, `ram-read-async-lifecycle`, `ram-read-coordination`, `ram-write-reference`, `cache-read-reference`, `cache-read-bookkeeping`, `stress-races`) | 1-15 min |
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
| `paging-coherence`, `partial-read-accounting`, `ordering-faults` | Mapped/paging coherence, partly cached reads, write ordering under injected lower failures | In every campaign; `ordering-faults` runs last |
| `volumes`, `trim-cache` | Several volumes on one disk; TRIM | Lab VHDX volume |
| `stress-races` | Both lab NTFS volumes at once under concurrent writers of 4 KiB to 4 MiB, cancelled overlapped I/O, cache flush/resize/teardown/drop/delay/allocation faults, refused settings and RAM disk churn; then fault and Retry at drain parallelism 1, 2 and 4; every byte read back | Lab VHDX; `--soak-seconds` (default 240); run under Driver Verifier and without |
| `managed-provider`, `ram-disk`, `vhdx-backed`, `image-in-ram`, `managed-cli`, `managed-broker-restart`, `managed-lifecycle-*` | Managed disk creation, checkpoints, broker restart and restart lifecycle | Owned fixtures only |
| `disk-removal`, `disk-removal-windows` | Eject with pending writes, reconnect | Disposable disk identity required |
| `system-*` | Guarded checks on the OS disk | Explicit acknowledgement and off-disk output |
| `write-performance` | 72 fitting-file write cases: random 4K Q1/Q32, sequential 1M Q1/Q8, Off/Eager/Idle, timing off/on | `--budget-mib 2048`; ~35-55 min |
| `cache-recall`, `cache-sustained`, `cache-map-cost`, `cache-concurrency` | Read recall, long mixed use with byte oracles, map polling cost, concurrent streams | `cache-sustained` defaults to a 30-minute soak |
| `ram-read-reference` | RAM-disk Direct and Standard reads, 1M and 4K, one and four threads | Product default |
| `ram-read-async` | Same-build off/adaptive comparison of asynchronous split reads, 18 windows | Prints a screen verdict |
| `ram-read-async-lifecycle` | Verified overlapped reads, the in-flight cap, ending Direct access under load | Correctness only |
| `ram-write-reference` | RAM-disk 1M writes from one and four programs, plus copies into and out of cold memory without the driver | Tells the machine's copy limit from the driver's |
| `cache-read-reference` | Cached 1M reads from one and four programs, with lock and queue time per read | Attribution only |
| `cache-read-bookkeeping` | Same-build comparison of cached read bookkeeping (copy flags 3 and 7) | Prints a screen verdict |
| `sequential-resident`, `drain-decision`, `flush-interference`, `performance`, `full` | Older focused performance matrices; `drain-decision` compares drain batch 256/1024 KiB x parallelism 1/2/4 (plan 125) | Use when the area changes |
| `cache-layout*`, `caller-backoff`, `priority-cost`, `priority-affinity`, `ram-read-scheduling`, `ram-read-coordination`, `ram-read-attribution` | Concluded comparisons and diagnostics | Results are in the findings; rerun only for a new question |
| `compare-ram`, `compare-cache`, `compare-cache-cold`, `compare-durability-write`/`-churn`/`-verify`, `compare-snapshot` | Other products against QueueCache: see below | `--product` required; no QueueCache controls |

**Comparisons with other products** (plan 121). These suites measure any RAM
disk or cached volume the same way, without QueueCache's controls, hooks or
telemetry, so QueueCache and its competitors get identical treatment. Each needs
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

A campaign folder holds one child run per phase plus `completion.json`.
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
