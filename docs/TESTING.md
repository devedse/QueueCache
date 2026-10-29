# Testing QueueCache

Everything needed to test QueueCache, in one place: host tests, VM verification
runs, comparing performance, and the README benchmark. Results and decisions go
in [performance findings](PERFORMANCE.md); confirmed gaps in
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

Desktop status-policy contracts cover read-back, external changes, suppression,
failed/overridden writes, unavailable state and VHDX creation guidance. On Windows,
the same host tests exercise the registry adapter against a unique temporary HKCU
key and remove it afterwards; they do not change machine policy. A manual Windows
UI check of the actual machine toggle records and restores both original policy
values. This desktop-only change does not require another driver benchmark matrix.

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

**Installing a build.** Run the CI installer (unattended on the VM:
`setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TRUSTTESTCERTIFICATE`, because nobody
can answer Windows' question about the RAM disk driver there), restart, then confirm the loaded
drivers are the intended ones: `qcache developer driver loaded` lists their file
hashes. A version string or the registered path is not proof of what is loaded.
Run correctness checks of new driver code under Driver Verifier
(`verifier /flags 0x209bb /driver <filter>.sys qcramdisk.sys`, restart). For any
performance number, Verifier must be off (`verifier /reset`, restart): it slows
the drivers several times over.

**Before every run:**

- Elevated terminal; nothing else testing or benchmarking; close the QueueCache
  tray app if the run changes caches it shows.
- Wait for the installed `QueueCache.ManagedDisks` service to reach Running before
  applying a temporary runtime profile, including for correctness runs. Its delayed
  startup initializes managed disks and reapplies saved profiles before reporting
  Running; starting a test earlier can overwrite its profile during a file oracle.
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
| `experiment` | Exactly one narrow suite (`ram-read-async`, `ram-read-async-lifecycle`, `ram-read-coordination`, `ram-write-reference`, `cache-read-reference`, `cache-read-bookkeeping`, `cache-read-misses`, `stress-races`, `flush-order`, `flush-order-cost`, `checkpoint-reuse`) | 1-20 min |
| `focused` | One named suite plus the retained correctness checks (partial reads, paging, policies, pressure, ordering/faults) | 10-30 min |
| `performance` | Product-default regression: correctness checks, RAM reference, RAM lifecycle, recall, 2-minute sustained, map cost, ordering/faults | ~1 h |
| `release-performance` | `performance` plus the 72-case write matrix and a 30-minute sustained phase | ~2 h |

`--lab-ntfs` names the attached lab NTFS volume. `--pause-backing-cache` lets the
runner pause `Q:`'s cache while lab phases run (the lab VHDX lives on `Q:`) and
restores it afterwards. A freshly attached lab image keeps writing to `Q:` for a few
seconds, and so does the lab volume's file system after a lab phase. Since plan 169
the campaign preflight waits up to about 30 s, and since plan 171 every clean-cache
capture waits up to 15 s, for those writes to drain on their own; nothing is drained
for them, and a cache that stays dirty is still refused. There is no `--detach`: run the foreground command and let
it finish. Results go to a new folder under `--output`.

Use the smallest thing that answers the question: a single suite or `experiment`
while iterating, `focused` when a change touches the retained correctness paths,
and `release-performance` once on the final build.
Plan 207 adds eight paging-ordering checks to standalone `flush-order` (case `flush-order-v207`):
seed boundary, stale layout token refusal, safe bypass while an older write is held, new-range learning,
cached-write, disabled direct-write and TRIM invalidation, and zero safe overlaps. Diagnostics V28 is append-only; V27 remains
the FUA layout. C: durability write/churn/verify are accepted only with the guarded system identity,
recoverable-VM opt-in, a QueueCache product label and evidence/oracles on another physical disk.
`--growth-markers` selects a distinct schema-6 C: durability workload: 2,000 markers reach an explicit
filesystem/cache checkpoint before they are deleted; their deletion-group range is recorded before cut readiness.
Post-cut verification accepts absent or exact markers only, and rejects changed or unreadable ones. This does
not force NTFS to reuse the marker clusters for page-file growth.
The retained release matrix is unchanged.
Plan 176 extended the standalone `flush-order` suite without expanding the retained matrix.
Plan 183 adds retained-version capacity, live mode-change refusal and
disable/release/reapply checks (nine total), plus the narrow `flush-order-cost`
screen. Its observer must complete the ready handshake before the worker starts,
cover the whole worker interval with no gap over two seconds, and retain all raw
samples. The retained matrix is unchanged.
Plan 184 includes the filesystem volume flush before the QueueCache lower flush
in the cost screen's durable tail, so deferred NTFS metadata is counted too.
Its power-cut writers also use fresh run-owned directories and refuse an existing
destination; later cuts preserve earlier oracles and independent durable guards.
Verification reads the directory recorded in its selected immutable oracle.
Plan 185 adds `--durable-checkpoint` to QueueCache's `compare-durability-write`:
after writing, flush the filesystem volume and then QueueCache before the durable
ready marker. The schema-3 oracle marks this stronger boundary. Post-cut
verification requires every sector of both files and all small files to match,
plus a clean scan; an old/missing/damaged byte or nonzero scan fails after the raw
classification is saved. Ordinary volatile cuts keep their classification-only
contract. The option is refused for other suites and campaigns.
Plan 186 excludes both durability writers from the general successful-run cleanup:
their fresh directories must remain for the post-cut oracle, including independent
durable guards. A plan-185 guard attempt exposed this cleanup error before any cut;
its removed data is not a prepared guard or crash result.
Plan 187 records state immediately before and after each explicit Flush. If a
healthy cache has new pending work and its accepted-write counter advanced, allow
at most three Flush calls to reach a clean observation. Pending work with no new
admission, an error or an identity change fails immediately; faults are never
retried. The cost screen includes every call in its durable tail. A plan-186
checkpoint attempt stopped with 8 KiB aged 1 ms pending and no driver error;
no cut was made and the incomplete attempt remains separate.
Plan 188 runs eleven `flush-order` checks for each of original mode 1 and
post-write-through mode 2 (22 total), including retained explicit predecessors
and held/failed successors. The equal-work cost screen compares these modes,
retains sequential and explicit-file-flush workloads, and replaces the unqualified
resident read timing control with 256 ordinary/write-through 4 KiB pairs in a
preallocated 2 MiB file. Persistent handles keep open/close work outside its timed
loop. Three order-balanced repetitions produce 18 immutable inner cases; the
runtime default stays off.
Plan 189 closes the workload's persistent writer handles after recording the
application time and before the filesystem/QueueCache durability checkpoint;
last-close metadata is therefore included in total durable time. Verification
uses a new read-only handle after cache release. A plan-188 cost attempt stopped
when fresh metadata kept arriving across its three allowed flushes. That attempt
remains incomplete; the three-attempt/error guards were not relaxed, and plan 189
uses new immutable case IDs.

For the Ceph-backed lab, retain UTC-aligned pool free space, client I/O, OSD
latency, the test VM's block counters, other guests' I/O and active/recent
backup/restore tasks. Check the actual task intervals, not just a schedule whose
timezone may differ. Keep unrelated intentionally offline pools separate from
the pool serving the VM. Flag competing work or recovery during scored windows;
do not attribute all cluster traffic to the test or silently mix affected arms.
Since plan 173, the retained correctness suites contain 94 subchecks: 22 partial
reads, 9 paging, 42 policies, 13 pressure and 8 ordering/faults. A focused
`stress-races` campaign adds its five checks, for 99 in total. Plan 173 (#19) turned
`paging-coherence/mapped-read-not-kept` into `mapped-read-kept` and added
`buffered-reread-from-ram`: a file read through Windows' file cache is kept, and after
Windows' standby memory is purged the next read comes from RAM.

**Long runs from an agent.** Start the foreground campaign command with the
harness's built-in background-task mechanism. Wait for completion, then read that
run's evidence and use `verify-completion`.

Plan 190 gives pressure runs the immutable `pressure-integrity-v190` case ID.
The existing high-watermark observations now retain dirty/in-flight, accepted and
completed bytes, lower drain/write/flush attempts, application flush/write-through
counts, errors and ordering mode before restoration. The three original failure
conditions remain strict and are reported separately. Additional accepted bytes
are distinguished from the planned file payload; missing/reset deltas stay unknown.
The workload, timing windows and trigger thresholds are unchanged.

Plan 191 gives `flush-order` the `flush-order-v191` case ID and thirteen checks per mode (26 total): an oversized
write over sealed history must exercise its quota/direct and lower flush path,
and an unmappable paging-marked write must exercise the direct fallback while
preserving its predecessor. Both verify latest bytes and an untouched neighbor
after release; unexpected faults are not retried.
The standalone QueueCache `compare-durability-write --write-through-generation2`
variant requests write-through for generation-2 unbuffered writes. It requires
ordered Fast mode, records the actual ordering mode and observed write-through
request delta, and refuses cut readiness unless at least one request per 1 MiB
write was observed. Its volatile/durable oracles use schemas 4/5; historical
ordinary-write schemas 1/3 remain distinct. Schema 5 requires the same exact-byte
and clean-scan checkpoint proof as schema 3. This option cannot be used with
campaigns, other suites or other products. It does not perform a power cut itself.

Plan 192 uses `flush-order-v192`. The new mapped fallback opens its mapping
through a stream that shares read/write/delete with the existing unbuffered
predecessor handle. The plan-191 attempt stopped on an incompatible sharing mode
before exercising this check; restoration succeeded, and that attempt stays
incomplete. Required ordering/attempt/byte assertions are unchanged.

Plan 193 changes only the `flush-order-cost` comparison arms to ordering off
(mode 0) versus the current on variant (mode 2), with `flush-order-cost-v193`
case IDs. The three equal-work workloads, three alternating repetitions,
telemetry, durable-tail timing and independent byte checks are unchanged.
This directly measures the toggle on one loaded native build; historical
release runs and the mode-1/mode-2 comparison do not substitute for it.

Plan 194 sizes that comparison to about a minute per case and uses a 2 GiB
cache. Each arm does the same work: a 4 GiB file written twice in 1 MiB writes,
25,600 new 4 KiB files each flushed, or 25,600 ordinary/write-through 4 KiB
pairs in a 200 MiB file. Each case's files are deleted after its byte check, so
the lab volume needs the sequential file plus 1 GiB free. Case IDs are
`flush-order-cost-v194`; the worker allows an hour.

Plan 195 adds lab ordering mode 3: mode 2's groups, but drain selection stops at
the frontier group and drainers wake only for work they may start. `flush-order`
(`flush-order-v195`) runs its thirteen checks for modes 1, 2 and 3 (39 total).
`flush-order-cost` (`flush-order-cost-v195`) compares mode 0, 2 and 3; each arm
takes each position once over the three repetitions (27 cases). The sequential
workload is one 1.5 GiB file in 1 MiB writes, which fits the 2 GiB cache: it
fails if any write waited for cache space, so its application time is RAM
admission. The other two workloads are unchanged. Every arm runs with
performance timing on and records lock, selection and wake-up counters at the
start, at the end of the application phase and after the durable tail. The
worker allows two hours.

Plan 196 keeps one ordering mode: value 1 is the former lab mode 3, and modes 2
and 3 are refused. `flush-order` (`flush-order-v196`) runs its thirteen checks
once, named `flush-order/<check>`; a write-through must now end its group exactly
once. `flush-order-cost` (`flush-order-cost-v196-r<repeat>-<workload>-off|on`)
compares ordering off and on with the plan-195 workloads, alternating which arm
runs first (18 cases for three repetitions).

Plan 197 adds `flush-order-sustained` to ask whether ordering's slower write-out
matters in longer use. Each workload runs once with ordering off and once on per
repetition, alternating which goes first. `files-5min` creates 4 KiB files, each
flushed, nonstop for five minutes (time-bounded, so the arms may write different
counts); `tar-extract` and `robocopy-tree` run Windows' own `tar.exe` and
`robocopy.exe` on a prepared tree of 16,000 files (4/16/64/256 KiB, 1,328 MiB)
that the tools do not flush. Every second it records the application's progress,
unwritten bytes, waits for cache space, bookmarks and group flushes. Bytes are
checked unbuffered after cache release: every tree file, and every 64th flushed
file plus the last thousand. Case IDs are
`flush-order-sustained-v197-r<repeat>-<workload>-off|on`; the worker allows
three hours. The file runs create up to about 840,000 files, which grows the lab
volume's NTFS file table by about 1 GiB for good. A failed run keeps its case
folder for inspection; record and remove it before the next run, or the 6 GiB
free-space check stops it. A complete two-repetition run took 2,739 s.

Plan 198 makes write ordering a per-cache setting and adds a fourteenth
`flush-order` check (`flush-order-v198`), `flush-order/per-cache-setting`: through
the same management code as the app and CLI it applies a Fast cache with ordering
on, pauses and resumes it, tries Strict with ordering (must be refused with nothing
changed), applies a configuration without an ordering choice (must keep it) and
turns it off, checking the driver's mode after each step. It never saves a
profile. Plan 199 (`flush-order-v199`) adds a last step: removing the cache must
also turn ordering off, so a later configuration without a choice does not
inherit it.

Plan 200 adds two suites for lab ordering mode 2 (#31): ordering where a sealed
group of at most 1 MiB is written with forced unit access (FUA) and needs no lower
flush. Both need a lab volume whose disk reports write-through support (the lab W:
VHDX does not; a temporary extra virtual disk does) and refuse otherwise before
measuring. `flush-order-fua` runs the driver checks in mode 2 as
`flush-order-fua/<check>`: the held predecessor must finish by forced writes, and
the two failure checks fail the forced write (lab fault 1) instead of a group
flush. Plan 201 (`flush-order-fua-v201`) leaves out the two TRIM checks (eleven
checks): they need file-level TRIM, which the lab VHDX supports but a plain virtual
SCSI disk (the only write-through-capable lab disk on the VM) refuses. Campaigns
accept only the lab-disk VHDX as lab role, so on such a disk run the suites as single
runs (`qcache developer verify T: --suite flush-order-fua`). `flush-order-fua-cost`
(`flush-order-fua-cost-v200-r<repeat>-<workload>-off|on|fua`) runs the
`flush-order-cost` workloads with off, on and mode 2, each arm once in every
position over three repetitions (27 cases). Diagnostics V27 add the groups
finished by forced writes, those writes, write-through availability and the time
in group flushes.

Plan 202 adds `flush-order-fua-sustained`: the plan-197 five-minute flushed-file,
`tar` and `robocopy` workloads with off, on and lab mode 2, rotating each arm
through each position over three repetitions (27 cases). Case IDs are
`flush-order-fua-sustained-v202-r<repeat>-<workload>-off|on|fua`.
It requires the same FUA-capable disposable disk as `flush-order-fua-cost`, a
2 GiB cache, Verifier off and the normal ordering-performance preflight. Run
it as a single suite on the temporary disk; campaign lab roles still require
a VHDX. Telemetry readiness, full coverage, durable checkpoints, post-release
byte checks and exact mode/timing restoration are unchanged.

Plan 203 flushes the filesystem before **each** bounded checkpoint attempt.
NTFS can generate more metadata during a long cache drain; flushing only before
the first attempt left the following two cache-only retries racing its lazy
writer. The three-attempt cap, refusal of faults or pending data without new
admission, telemetry and byte checks remain unchanged. Both ordering cost suites
and both sustained suites use new `v203` inner case IDs. Earlier `v200`/`v202`
and `v196`/`v197` run identities remain historical; never join their cases with
plan 203. The initial B cost run stopped at the final mode-1 checkpoint and
restored successfully; its 26 measurements are an incomplete matrix.

Plan 204 flushes the filesystem after the sustained suite's fresh resident-read
warm-up handles close, before the existing cache flush and two-second quiet wait.
A clean cache does not prove that NTFS has submitted all of its metadata. The
fresh Q1/Q8 windows still require zero lower read/write/flush attempts, misses and
drained bytes; no noisy window or fault is retried. Later churned rereads and the
six mixed workloads are unchanged. Sustained case IDs contain `sustained-v204`;
keep earlier attempts separate. This changes preparation, not driver behavior.

Plan 205 extends only the sustained fresh-read quiet observation to ten seconds.
The plan-204 focused regression still rejected 8 KiB of lower writes after its
filesystem flush; the traced repeat identified two 4 KiB System writes to Q:'s
NTFS `$LogFile` during the read window, about four seconds after preparation's
metadata writes. A cache that is clean at both ends is still refused if its
lower-I/O or drained-byte counters advanced. Every quiet attempt now records
`*-quietN-interval.json` with UTC boundaries, minimum/monotonic elapsed seconds
and its result. The five-attempt cap, strict score checks and other two-second
quiet observations are unchanged. Sustained case IDs contain `sustained-v205`;
plan-203/204 failures remain separate and do not qualify the release.

Plan 206 uses recorded system-wide Stopwatch ticks and frequency for coverage
of cache-exercise telemetry and map polls, including fresh/churned rereads.
The coordinator records `StartClock`/`EndClock` in each interval; every sample
retains its `Clock` alongside UTC. The same two-second telemetry gap limit and
map cadence plus two seconds apply. Missing/invalid clocks, unequal frequencies,
reversed samples and incomplete process coverage fail without a UTC fallback.
UTC is retained for backend correlation, including any observed clock changes.
Sustained IDs contain `sustained-v206`; other affected exercise IDs contain
`exercise-v206`. Recall and other suites keep their existing coverage contracts.
Do not reinterpret old incomplete runs using inferred monotonic boundaries.

## 4. Suites

`qcache developer verify --help` lists every suite with its requirements. The
ones in use:

| Suite | What it checks | Notes |
|---|---|---|
| `quick` | File integrity through the cache | Default; no DiskSpd |
| `policies`, `pressure` | Sector edge cases, each policy, drain/age/watermark/backpressure, disk bytes afterwards. Files whose requests must not cross an extent boundary (parallel copies, foreground-background) reserve their whole allocation first since plan 170, and are refused if they still split | No DiskSpd |
| `checkpoint-reuse` | Plan 177: two owned 128 MiB image-in-RAM fixtures (dynamic/512-byte sectors and fixed/4K sectors). Warm up two full checkpoints, save a small change into the older candidate, require fewer payload bytes and complete logical read-back, hash the protected current/original containers, corrupt fingerprints to require full fallback, then reload and check payload/guard bytes. Plan 179 adds dynamic growth (80 MiB) to require full fallback before mutation when a changed block is unallocated; unchanged sparse blocks remain eligible. Plan 180 commits a user export with an automatic-looking name and requires unchanged bytes after two later saves. Nine checks across the two fixtures. Since plan 181, `--budget-mib` sets each owned image's RAM size (128..2048 MiB); use 128 for a short correctness check or 1024 for a larger save observation. The chosen size is retained in the manifest and record. Plan 182 fills half of images at least 1024 MiB with deterministic nonzero existing data before the saves, hashes it and checks it after committed reload; plan 181 larger fixtures were mostly empty. A direct non-OS NTFS host with its cache paused can be used for the larger standalone suite; allow space for the retained source, checkpoints and exports. Plan 178 preserves transaction/I/O evidence in a separate `*.reply.json.trace.json`, alongside the worker's final checks. Records save times and I/O; Verifier times are correctness evidence, not a speed verdict | Narrow `experiment` on the NTFS lab role; preserves unrelated managed disks. Full source/candidate verification remains mandatory |
| `flush-order` | Plan 198: fourteen checks with ordering on, including the per-cache setting (plan 196 had thirteen; plan 195 ran them for modes 1/2/3, plan 188 had eleven per mode 1/2). Sealed overwrite versions with zero lower-I/O admission; two-drainer held predecessors; failed flush/Retry; isolated write-through, preserved explicit predecessor and held/failed post-write-through successor; async/sync TRIM; 8 MiB retained-version capacity; live mode-change refusal; disable/release/reapply. Separate blocking handle, exact diagnostics and hook restoration; deliberate faults accounted for | Disposable non-OS NTFS lab volume; a plan-196 driver (value 1 is the single ordering mode). Default ordering stays off. Correctness only; not power-cut qualification |
| `flush-order-cost` | Plan 196: ordering off (0) and on (1), alternating which runs first over three repeats. Fixed 2 GiB Fast/Eager, 1024 KiB x4: one 1.5 GiB file in 1 MiB writes that must fit the cache (no wait for cache space), 25,600 fresh 4 KiB files each explicitly flushed, and 25,600 ordinary/write-through 4 KiB pairs. Use `--budget-mib 2048 --repeats 3`; 18 unique inner cases, about 15 minutes (plan 195's 27 cases took 1,181 s; the on-disk tail depends on the storage backend). Records time into the cache (application) and total time until everything is on disk (writer close, volume flush, QueueCache checkpoint), UTC intervals, state/diagnostics, performance counters with timing on (start, end of application phase, end) and independent unbuffered read-only byte checks after release. Full plan order/count mandatory. Verifier off and Windows up 15 minutes. Fixed-work Q1 comparison, not peak throughput or a complete release verdict | Narrow `experiment` on NTFS lab role; pause backing Q: cache. Owns fresh files, deletes each case after its byte check, restores mode, timing and cache and rejects incomplete matrices. No DiskSpd |
| `flush-order-fua`, `flush-order-fua-cost` | Plan 201, lab mode 2 (#31): the eleven non-TRIM driver checks with forced-unit-access small groups; and the `flush-order-cost` workloads with off, on and mode 2 (27 cases for `--repeats 3`, `--budget-mib 2048`, Verifier off, warm boot) | Single run on a disposable non-OS NTFS volume whose disk reports write-through (FUA) support (campaigns require the lab VHDX, which does not); else the suite refuses before any case. No DiskSpd |
| `flush-order-fua-sustained` | Plan 202: off, on and lab mode 2, five-minute flushed files and Windows tar/robocopy; three rotated repetitions, 27 cases | Single run, disposable non-OS NTFS disk reporting FUA support; `--budget-mib 2048 --repeats 3`, Verifier off, warm boot; no DiskSpd |
| `flush-order-sustained` | Plan 197: ordering off and on, alternating which runs first. Fixed 2 GiB Fast/Eager, 1024 KiB x4: five minutes of new 4 KiB files each explicitly flushed, Windows `tar.exe` extracting and `robocopy.exe` copying a 16,000-file 1,328 MiB tree. Per-second progress and cache samples (unwritten bytes, waits for cache space, bookmarks, group flushes), time into the cache and until everything is on disk, lock/selection timing on, unbuffered byte checks after release (all tree files; every 64th flushed file and the last thousand). Use `--budget-mib 2048 --repeats 1` or `2` (about 25 minutes per repetition). Verifier off and Windows up 15 minutes. Time-bounded file run: compares behavior over time, not equal work | Narrow `experiment` on NTFS lab role; pause backing Q: cache. Needs 6 GiB free on the lab volume; deletes each case's files after its byte check, restores mode, timing and cache and rejects incomplete matrices. No DiskSpd |
| `paging-coherence`, `partial-read-accounting`, `ordering-faults` | Mapped/paging coherence, partly cached reads (plan 126: 16 simultaneous cold/partial readers with byte and pin-retirement checks), write ordering under injected lower failures | In every campaign; `ordering-faults` runs last |
| `parallel-copies` | Plan 165 diagnostic: the `policies` parallel-copies case alone (four 1 MiB rewriters with read-back, two concurrent readers, Eager, parallelism 2) for `--duration-seconds` per case, alternating 2048 KiB (the default since plan 164) and 1024 KiB batches over `--repeats`. A failing read names the pass each 4 KiB chunk matches, which tells torn, stale and zeroed reads apart. Since plan 166, `policies` and this suite write under the run's work directory (removed after a completed run, not the volume root), and `parallel-copies` and `foreground-background` recreate a file whose extent boundary would split one of their requests: a split request reaches the volume as several, which nothing makes atomic or keeps on the caller's thread. A file that stays split fails the case | Lab NTFS volume |
| `volumes`, `trim-cache` | Several volumes on one disk; since plan 172 `volume-detach`, a new 1 GiB VHDX of its own with 64 MiB of pending Fast writes, detached and attached again: the file must be intact and the removal logged as event 2, not 1 (#21); TRIM (plan 160 adds `trim-cache/eager-unrelated-not-awaited` and `eager-overlap-awaited`: with background Eager draining and a 2 s lab delay per batch, a TRIM outside the in-flight batches returns within 1 s with no write landing meanwhile, and one inside them returns only after a write landed. An explicit flush holds the ordered request path, so `during-drain` queues its TRIM behind the whole flush. Plan 162 runs those five checks with `lab-trim-mode` 0 and again with 3 (index lookup + asynchronous lower TRIM, suffix `-async`), then `async-request-path-free` (another file's rewrite and read finish within 1 s while a TRIM is held 2 s) and, since plan 163, `async-eager-ordering`. Under Eager with a TRIM held 2 s, another file's rewrite drains while the TRIM is outstanding, and a queued rewrite of the held range ends on the disk exactly. NTFS normally keeps that rewrite until the TRIM completes, so the check reports whether the fence path was reached; a rewrite completed during the TRIM must stay dirty. The plan-162 `async-fence` check assumed the rewrite reached the cache at once and failed against NTFS's ordering. 12 checks; the original mode is restored. Since plan 164 the Eager TRIM checks pin 1024 KiB × 4, so that only the file's first 4 MiB is in flight) | Lab VHDX volume |
| `stress-races` | Both lab NTFS volumes at once under concurrent writers of 4 KiB to 4 MiB, cancelled overlapped I/O, cache flush/resize/teardown/drop/delay/allocation faults, refused settings and RAM disk churn; then fault and Retry at drain parallelism 1, 2 and 4; every byte read back. Plan 128 flushes filesystem metadata before fault injection and after retry, and requires retained-byte drainage plus a clean result. Since plan 167, a churn RAM-disk creation that the RAM headroom guard refuses (on a 16 GiB VM the previous disk's 2 GiB may not have returned yet) is retried and counted; fewer than two completed cycles still fails | Lab VHDX; `--soak-seconds` (default 240); run under Driver Verifier and without |
| `managed-provider`, `ram-disk`, `vhdx-backed`, `image-in-ram`, `managed-cli`, `managed-broker-restart`, `managed-lifecycle-*` | Managed disk creation, checkpoints, broker restart and restart lifecycle. `managed-lifecycle-*` needs an empty virtual-disk list, a host volume off the Windows disk and an `--output` folder on another disk that only administrators can change (the [image folder rule](MANAGED_DISKS.md)) | Owned fixtures only |
| `disk-removal`, `disk-removal-windows` | Eject with pending writes, reconnect | Disposable disk identity required |
| `system-paging-ordering` | Plan 207: guarded C:, 2 GiB ordered Fast cache, seeded paging extents, two-minute locked-memory + 4 GiB pager workload, flushed-file writer and heartbeat | Explicit system identity/VM opt-in and output on another physical disk; observe safe bypasses while earlier saves are pending, zero safe overlaps, heartbeat ≤2 s, lower-disk byte checks and independent restoration. Original saved profiles are preserved. |
| `system-*` | Guarded checks on the OS disk | Explicit acknowledgement and off-disk output |
| `write-performance` | 72 fitting-file write cases: random 4K Q1/Q32, sequential 1M Q1/Q8, Off/Eager/Idle, timing off/on | `--budget-mib 2048`; ~35-55 min |
| `cache-recall`, `cache-sustained`, `cache-map-cost`, `cache-concurrency` | Read recall, long mixed use with byte oracles, map polling cost, concurrent streams. Plan 129 primes the write profile before RAM-only write counters start; the scored window still requires zero lower I/O | `cache-sustained` defaults to a 30-minute soak |
| `ram-read-reference` | RAM-disk Direct and Standard reads, 1M and 4K, one and four threads. Since plan 168 each owned 2 GiB disk waits up to 60 s for RAM headroom first, because the previous disk's memory can still be returning to Windows after its stop | Product default |
| `ram-read-async` | Same-build off/adaptive comparison of asynchronous split reads, 18 windows | Prints a screen verdict |
| `ram-read-async-lifecycle` | Verified overlapped reads, the in-flight cap, ending Direct access under load | Correctness only |
| `ram-write-reference` | RAM-disk 1M writes from one and four programs, plus copies into and out of cold memory without the driver | Tells the machine's copy limit from the driver's |
| `cache-read-reference` | Cached 1M reads from one and four programs, with lock and queue time per read | Attribution only |
| `cache-read-misses` | Same-build flags 7/15, cold 4K Q32 and 1M Q8 on a file twice the cache plus fully cached controls; three alternated repetitions (24 windows), plan 127 | Reports throughput, p99 and worst-read latency; restores original flags |
| `tree-decision` | Same-build 40,000-file / 1.2 GiB create-copy-read-hash-delete workload; cache-off and flags 7/15 at 256 KiB × 2 / 1024 KiB × 4, three rotated repetitions | Timing OFF, Idle draining, byte/hash oracle, disk-idle settles, per-phase system-cache memory and lower-I/O attempt snapshots; collection only, about 1–2 hours |
| `read-tail-decision` | Fixed 100,000-operation cold-random 4 KiB Q32, resident-random 4 KiB Q32, resident-sequential 1 MiB Q8 and fitting foreground-write 4 KiB Q32; flags 7/15 in three rotated repetitions (24 cases) | Timing OFF; per-operation p50/p99/p99.9/max and exact >50/100/250/500 ms counts, lower-I/O snapshots, resident warm-up and durable write-byte oracle after a stable clean-cache wait; collection only |
| `drain-flush-decision` | Explicit cache Flush of a deterministic 512 MiB dirty seed at 256 KiB × 2 and 1024 KiB × 4, three alternated pairs (6 cases) | Timing OFF; owned control worker ready/start handshake; duration starts immediately before the Flush call; drain batches/bytes, lower write/flush attempts and persisted-byte oracle; collection only |
| `cache-allocation` | Plan 152: 1/15/16/17/63/64/65 MiB caches, Fast and Strict, 2 MiB × 8 settings | Fourteen reservation-bound checks with live reads, explicit flushes and unbuffered bypassed byte oracles; correctness only. |
| `drain-lock-diagnostic` | Plan 151: cached 1 MiB Q8 and uncached random 4 KiB Q1 reads, alone/with an Eager sequential writer, 2 MiB batches with 4/8 drainers, timing off/on, three reversed-order repetitions (48 windows) | Exactly `--budget-mib 2048 --repeats 3 --duration-seconds 6 --diskspd <same binary>`; 4m48s scored reads plus setup/settling/drainage. Cached cases use Automatic 50% writes; disk-read cases use Fixed 100% writes (zero read admission). Pure hit/miss paths, >=75% loaded-window pending observations, telemetry readiness/coverage and original-state restoration required. Global lock timings cover the reader process envelope and all callers; summed drainer waits can exceed wall time. Timing-on is diagnostic; timing-off is the performance score. Plan 160: a standalone window that saw background writes (file-system metadata, TRIM) is never scored; it is kept as `<case>-noisy.json` and remeasured as `<case>-retry<n>`, at most three attempts, and the case's evidence lists those attempts. Any other failure, or noise in all three, stops the run. Collection only; no lock/default change or correctness qualification. |
| `cache-read-bookkeeping` | Same-build comparison of cached read bookkeeping (copy flags 11 and 15; 3 and 7 before plan 125) | Prints a screen verdict |
| `sequential-resident`, `drain-decision`, `flush-interference`, `performance`, `full` | Older focused performance matrices; `drain-decision` compares drain batch 256/1024 KiB x parallelism 1/2/4 (plan 125) | Use when the area changes |
| `cache-layout*`, `caller-backoff`, `priority-cost`, `priority-affinity`, `ram-read-scheduling`, `ram-read-coordination`, `ram-read-attribution` | Concluded comparisons and diagnostics | Results are in the findings; rerun only for a new question |
| `compare-ram`, `compare-cache`, `compare-cache-cold`, `compare-durability-write`/`-churn`/`-verify`, `compare-snapshot` | Other products against QueueCache: see below | `--product` required; no QueueCache controls |
| `compare-drain` | Two fixed new-file workloads through explicit product drain; three repeats, two median/range rows per provider | Explicit `--provider`, `--product`, `--budget-mib 2048 --repeats 3`; no DiskSpd |
| `compare-drain-sustained` | Plan 158: one new 8 GiB file (four times the cache) through explicit product drain; three repeats | Same options as `compare-drain`; at least 12 GiB free |
| `compare-drain-read` | Plan 161: a 4 KiB random Q1 reader on an uncached 8 GiB file, alone and 12 s after a sequential 1 MiB Q8 writer began keeping the product's 2 GiB cache full; three alternating alone/loaded pairs | Same provider options as `compare-drain` plus `--diskspd`; at least 20 GiB free. Loaded windows need pending data at both reader boundaries and ≥64 MiB written to the disk; alone windows ≤16 MiB |

**Comparisons with other products** (plan 121). The `compare-*` suites measure any RAM
disk or cached volume the same way, without QueueCache's own controls, hooks or
telemetry. The opt-in QueueCache-only `--durable-checkpoint` variant additionally
flushes the filesystem and QueueCache; it is a durability check, not an identical
cross-product comparison. Their workloads, provider options and rules are in
[comparison: running a comparison](COMPARISON.md#running-a-comparison).

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
  [performance findings](PERFORMANCE.md).
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
[performance findings](PERFORMANCE.md).

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
([findings](PERFORMANCE.md#ram-disk-asynchronous-split-reads-accepted-pr-9)).

## 7. README benchmark (CrystalDiskMark)

The README's performance table and the `cdm-cache.png` / `cdm-ram-disk.png`
screenshots come from real CrystalDiskMark 9.0.3 runs on the test VM.

**Current README figures.** Default test (1 GiB, 5 passes), the better of two clean
runs per column. Both Q: columns: QueueCache 0.4.687.1 (2026-10-15 UTC),
Defender/Search disabled, Fast with that release's Idle drain, 1024 KiB batches and
four drainers, measured in one session without a restart in between (the drive's
speed on this shared network storage varies). The paused column keeps QueueCache's
filter loaded. RAM disk: QueueCache 0.4.583.1 (2026-10-11), re-measured after RAM-disk
reads became asynchronous (SEQ1M Q8T1 read was 25,353 MB/s before). New caches now
default to Eager draining with 2 MiB batches and eight drainers; the cache columns
have not been re-measured with that default.

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
