# RAM-disk read scheduling investigation, 2026-10-10

The approximately 26 GB/s single-submitter and 40–42 GB/s four-submitter
observations motivate this work. Aggregate bandwidth does not establish that
one submitter can reach the same score. The completed same-build comparison below
rejects the shared sleeping queue; its kernel implementation is removed.

## Preliminary CPU attribution

Installed 0.4.497.1, source `2a315c148cb8ee81102c6d3df81f569bbae79a5b`,
filter SHA-256 `48DDF427B0455E813CDCE79A489A22ED88698F842CA245A733AA5BC8E6830F06`,
provider `1C4B281FE9A79812489BD789EDDA10BB97A67E08ED320897DF3F2E9766DB540C`.
The filter hash below is recorded from the loaded module; raw ETLs, offline
symbol summaries and benchmark outputs are preserved privately with each run.

| Direct shape | Non-idle CPU share | Provider memcpy, busy samples | Provider WorkerMain, busy samples | Enclosing process interval, UTC |
|---|---:|---:|---:|---|
| SEQ1M Q1T1 | 94.9% | 46.4% | 33.8% | 02:32:03.947699–02:32:14.314949 |
| SEQ1M Q8T1 | 96.9% | 46.7% | 35.1% | 02:50:13.829520–02:50:23.910038 |
| SEQ1M Q2T4 | 99.7% | 69.5% | 3.7% | 02:50:37.255790–02:50:47.341281 |

These are sampled function-level shares over the process interval, including
startup/teardown. `WorkerMain` contains both queue coordination and polling;
these percentages are not line-level proof that every such sample is spin.
Lost ETW events are rejected. Q1 comes from the incomplete plan-106 run
`QueueCache-Verify-20261010-023141-a4da217566994afbb6b70d71287a3e86`;
Q8/four-thread from the incomplete plan-107 run
`QueueCache-Verify-20261010-024925-e957193997ef4dd7a415ed233edf97d5`.
The latter passed its first two guarded byte-checked windows and stopped on
telemetry coverage in the third. Neither is a completed performance baseline.

The difference supports an experiment that spends more CPU on copies and less
on helper coordination. It does not justify another use of the earlier rejected
per-CPU queued workers, or a claim that the experiment will reach 42 GB/s.

## Rejected shared sleeping queue (historical implementation)

Diagnostics V22 append mode and queued/completed/cancelled/inline/full/fallback
counters, retaining all older wire prefixes. `LabRamReadQueue` is runtime-only:
0 is the unchanged synchronous strategy and the default; 1 queues whole large
reads; 2 adapts back to synchronous copying after sixteen isolated admissions,
with one queued probe in sixty-four. Small and paging reads and writes retain
the existing paths. Only ordinary Direct reads from 512 KiB through 1 MiB are
eligible; no new RAM disk payload allocation or copy-helper thread is created.

The experiment uses the existing three unbound sleeping copy executors and
bounded 64-entry pool. Admission takes binding rundown and maps the original
locked MDL before publication. The IRP is marked pending before any executor can
see it. Each executor copies a whole read, without the provider's split helper,
then completes exactly once and releases ownership. Cancellation is checked
before copying; cancellation after copying begins remains best effort. Store
rundown spans admission to cancellation/copy completion. A changed boot
signature releases rundown before standard-path fallback. Control/lifecycle
waits close queue admission so new requests cannot keep the drain alive.
Full capacity retains synchronous fallback. Mode changes do not abandon
already admitted requests.

```powershell
qcache developer verify Q: --suite ram-read-queue --budget-mib 2048 --repeats 3 --duration-seconds 10 --diskspd C:\Tools\DiskSpd\diskspd.exe --output C:\QueueCache-Results
```

Plan 109 compares synchronous Direct, adaptive Direct and Standard in 54 windows,
using the reference's six shapes and three alternating repetitions. It requires
V22 diagnostics, the same DiskSpd/hash, normal priorities, the payload write
guard, exact 1 GiB byte checks, native readiness/coverage and independent fixture
cleanup. Mode/counters must remain present and monotonic; quiescent queued and
completed counts must agree. Large adaptive windows must actually queue reads;
small/synchronous/Standard controls must not. Cancellation, full-capacity and
fallback activity reject a scored reference window and remain recorded.

Host ABI/plan/traffic/ownership/restoration tests and Windows compilation pass.
The complete VM comparison rejected speed acceptance, so the queue engine was
removed before lifecycle qualification. Current drivers retain the V22 wire
prefix as zeros and action 19 as a mode-0 no-op; modes 1/2 are unsupported.
The archived suite can reproduce this evidence against the historical
experiment driver. The [tracker](RAM_FIRST_IMPLEMENTATION_TRACKER.md) separates
implementation from verification.


## Completed reference on 0.4.497.1

Plan-108 tools 0.4.501.1 (`ccf429e3f6d9091fa273c316376bf21a46a78c28`)
ran against the unchanged installed 0.4.497.1 filter/provider above. Exact run
`QueueCache-Verify-20261010-031011-5ff0b7da045a4b559abcc5cef68060b1`,
03:10:11–03:25:40 UTC, completed all 36 windows and independent restoration.
All raw XML/recognized trailers, exit codes/stderr, 1 GiB byte guards/oracles,
readiness/native coverage, access/traffic, FINISHED/status/summary/results/log
and restoration were inspected. Maximum native sampling gap: 1.137449 seconds.
The original enabled 2 GiB Fast/Idle cache and managed resource IDs/reservation
were restored, with zero errors/dirty/in-flight bytes.

Each entry below is a median of three complete windows in MiB/s (1,048,576
bytes/s). CPU ETW recording was enabled for this reference; it adds overhead,
so scheduling acceptance uses a separate same-build A/B without recording.
DiskSpd SHA-256: `7281BF6DA6C03797016EDDF2E8AAEC4C644AE893D403D57A030B7E2E14B61079`.

| Read shape | Direct MiB/s | Standard MiB/s |
|---|---:|---:|
| 1M Q1T1 | 23,915.08 | 21,264.50 |
| 1M Q8T1 | 22,984.92 | 19,153.05 |
| 1M Q2T4 | 43,807.49 | 43,157.60 |
| 4K Q1T1 | 1,605.10 | 851.87 |
| 4K Q32T1 | 1,425.30 | 784.71 |
| 4K Q8T4 | 4,788.81 | 2,771.62 |

The large Direct Q8/four-thread medians are 24.10/45.94 decimal GB/s. Four
threads use independent sequential cursors in the same resident file, rather
than DiskSpd's globally interlocked sequence. DiskSpd emits its exact known
`consider -si` warning for these six windows; all other stderr is empty. This
control measures aggregate throughput and may include source-data locality
from overlapping streams; it does not promise a 45.94 GB/s single stream.
Plan 110 makes this stderr rule explicit and rejects every other warning/error,
including an extra line after the recognized warning. Workload arguments stay
unchanged. The later comparison below supplies the queue acceptance verdict.

## Completed same-build comparison on 0.4.503.1

Run `QueueCache-Verify-20261010-034755-884b991306b245e7b91b3aea82fa9351`
completed 54/54 windows, 03:47:55–04:10:32 UTC, plan 110. Clean managed preview
tools came from `408e49cb3d3395ad73dacdd5189dbf39756d4e09`; the installed signed
native CI source was `9262493570cb8e437e219b4e0071046d68deca36`.
Loaded filter SHA-256:
`537FA897D4C6C0E961327A798CE6AC9C53A46E9F44D9D2C71D317FE9B03C4578`;
provider `5EFE752DCF8D4D72B4989A6733552836D881B109489117F7082854AFCAFA9427`.
DiskSpd/hash, resident files, normal priority, timing-off state and native build
were identical between modes. No CPU trace ran. The installed tray process
remained resident throughout (hidden at the post-run check), a preflight gap;
it was closed before subsequent measurements. No interactive UI work was done.
This limits claims about observer overhead but does not turn the consistent
negative queue result into a gain.
All raw XML/trailers/exits/stderr, byte guards and whole-file hashes, mode/counter
boundaries, native identity/access/traffic, readiness/coverage and independent
restoration were inspected. Maximum sampling gap: 1.237431 seconds. The original
enabled 2 GiB Fast/Idle cache, managed resource list and exact global reservation
were restored; dirty/in-flight bytes and errors were zero.

Each score is a three-repetition median, with minimum–maximum in MiB/s.

| Shape | Synchronous Direct | Adaptive queue Direct | Standard |
|---|---:|---:|---:|
| 1M Q1T1 | 24,729.50 (24,725.10–24,939.46) | 23,834.47 (22,959.64–23,898.80) | 23,158.40 (22,963.14–23,308.49) |
| 1M Q8T1 | 23,977.50 (23,555.30–24,836.33) | 21,268.10 (21,249.95–21,332.37) | 23,058.70 (23,025.37–23,090.90) |
| 1M Q2T4 | 42,934.37 (41,670.73–43,393.91) | 21,628.17 (21,621.08–21,742.10) | 41,836.40 (40,852.10–43,766.03) |
| 4K Q1T1 | 1,663.70 (1,625.95–1,670.54) | 1,668.45 (1,653.26–1,669.30) | 888.90 (886.99–893.41) |
| 4K Q32T1 | 1,481.22 (1,465.89–1,491.21) | 1,487.97 (1,455.27–1,493.78) | 835.88 (831.88–836.27) |
| 4K Q8T4 | 4,981.01 (4,778.79–4,995.47) | 4,976.31 (4,975.64–4,985.17) | 2,859.61 (2,820.00–2,863.13) |

The adaptive queue loses 3.62% Q1, 11.30% Q8 and 49.62% four-thread throughput
on large reads. All large-mode ranges are below their synchronous controls.
Small reads bypass the experiment and stay within measured variation.
Q8/four-thread Direct medians are 25.14/45.02 decimal GB/s synchronously and
22.30/22.68 with the queue. **Reject the queue; retain synchronous split copies.**
The earlier aggregate gap is real, but these results do not establish that a
single submitter can recover it by moving whole requests to sleeping workers.
The exact cause of the queue loss requires copy/completion/wake-up attribution;
no additional scheduling change or priority boost is justified by these scores.

## Plan-114 controls after review

The rejected queue changed both scheduling and copying granularity: its executors
copied whole requests without `CopySplit`. Its loss does not isolate which change
caused the regression. `ram-read-scheduling` keeps the retained Direct/copy path
fixed and measures two independent controls before another kernel experiment.

At three repetitions the suite has thirty immutable windows: large sequential
Q1T1/Q8T1 and random 4K Q1T1 compare default versus disabled affinity (`-n`);
large Q2T4 additionally crosses those affinity modes with overlapping independent
cursors versus `-s4M -T1M` interleaved lanes. Each thread in the latter starts one
1 MiB block after the preceding thread and advances 4 MiB, removing shared source
blocks while preserving four readers, total depth eight, block/file sizes and
the non-interlocked submission path. Contract reference:
[Microsoft DiskSpd threading/stride documentation](https://github.com/microsoft/diskspd/wiki/Threading-and-concurrency).
The [DiskSpd 2.2 offset implementation](https://github.com/microsoft/diskspd/blob/v2.2/Common/Common.h)
wraps sequential offsets to the thread's starting offset modulo sequential
stride. With these sizes, the inferred lanes remain 0/1/2/3 MiB modulo 4 MiB
across the 1 GiB file wrap. XML verifies the selected settings; this is an
inference from documented/versioned semantics, not a captured offset trace.
The actual bundled 2.2 XML is required to match affinity, stride, depth, file size,
duration and normal I/O settings; a three-second warmup and owned Normal CPU /
memory-default 5 readbacks precede every score. Existing whole-file byte guards,
residency/accounting, readiness/gap and ownership checks remain required.

The command can run inside the focused campaign with its retained correctness
checks, final fault checks and independent restoration. Implementation and VM
measurements are separate in the [tracker](RAM_FIRST_IMPLEMENTATION_TRACKER.md).
These controls identify benchmark placement/locality effects; they do not by
themselves measure copy-helper handoff costs or port the disk-cache copy loop.

An earlier attempt, `QueueCache-Verify-20261010-033934-ab5c8b71121448fdaf674d9eb963f978`,
stopped before any score because the control handle was read-only (Win32 5).
It restored cleanly; the managed writable-handle fix is in `408e49c`.
That incomplete attempt is preserved separately and contributes no scores.
