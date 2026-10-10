# RAM-disk read scheduling investigation, 2026-10-10

The approximately 26 GB/s single-submitter and 40–42 GB/s four-submitter
observations motivate this work. Aggregate bandwidth does not establish that
one submitter can reach the same score. No new scheduling gain is verified yet.

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

## Opt-in shared sleeping queue

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

Host ABI/plan/traffic/ownership/restoration tests pass. Windows compilation,
VM collection, speed acceptance and explicit cancellation/binding withdrawal/
stop/removal/freeze/snapshot/overlapping-write qualification are pending. The
experiment stays disabled by default. The [tracker](RAM_FIRST_IMPLEMENTATION_TRACKER.md)
separates implementation from verification.


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
unchanged. Native queue speed acceptance and lifecycle qualification remain
pending.
