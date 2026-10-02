# Sequential peak reproduction, 2026-09-30

## Environment and identity

Latest successful CI at the start of this investigation was run 36771636323,
version **0.4.259.1**, branch source head `58e5467`, embedded CI merge
`6e2cfe2d8f1cfc2b98ada90c6f84f3f0900f3007`. The installer completed successfully.
After the approved restart, native loaded-module enumeration identified
`\SystemRoot\System32\drivers\QueueCache-0.4.259.1-625D2AA0A974.sys`.
Its SHA-256 matched the signed CI artifact exactly:
`625D2AA0A97404199D5103E9C2A90C271532E309171E9F18A95E60CC2DE8ED54`.
All compared historical native drivers were Release builds; this was not a
Debug-versus-Release comparison.

The Windows VM retained four vCPUs, 16 GiB RAM, and its non-OS Q: volume on
physical disk 2. Q: used 2048 MiB Fast/Idle, drain parallelism 2, age 5000 ms,
Idle 250 ms. Runtime Verifier reported no verified drivers during measurement.
No fault/delay hooks were armed; detailed timing was off. The detached eject
lab disk was left alone.

The same CDM 9.0.3 bundled DiskSpd 2.2 x64 binary was used throughout, SHA-256
`7281BF6DA6C03797016EDDF2E8AAEC4C644AE893D403D57A030B7E2E14B61079`.
The locally published maintained runner adds the sequential-resident suite;
it is distinct from the installed CI CLI and does not replace the CI native driver.

## Verified warm-cache read and per-I/O random write results

Plan 76 run `QueueCache-Verify-20260930-204030-f95dbd5c55834e9a9f2a42d62267cd00`
completed **6/6**, with clean independent restoration. Each case used a 1 GiB
fitting file, 1 MiB requests, Q8/T1 and a five-second XML score window.
Before scoring, a ten-second cold warm pass had to complete at least one full
file read; a second three-second pass had to prove stable identity/generation,
full retention and enough new RAM-hit bytes for all completed reads, with zero
new read misses. All six cases passed that evidence check. Each score had its
own ready handshake, 26 telemetry samples and maximum sample gap 0.226–0.238 s.

All speeds below are decimal **MB/s**, calculated from XML completed bytes and
score seconds; they are not MiB/s or whole-process counter-derived rates.

| Repetition | Read | Write, fresh random data per I/O (`-Zr`) |
|---|---:|---:|
| 1 | 36,399.219 | 16,957.361 |
| 2 | 36,185.309 | 16,520.525 |
| 3 | 33,054.052 | 14,614.843 |
| Median | **36,185.309** | **16,520.525** |

The **36,000 MB/s read target is reproduced** on the latest installed build.
The fresh-random write workload remains below 21,000 MB/s. Those write samples
are not equivalent to CrystalDiskMark's precomputed Random write buffers.

Read snapshot intervals had zero new read misses, errors, capacity waits,
lower writes, or drained bytes. Writes had zero new errors or capacity waits;
copy-offload counts advanced by 80,862 / 78,779 / 69,692. The small generated
background drain during each write interval was approximately 20.2 / 29.9 /
17.6 MB, with 81 / 117 / 71 lower-write attempts. These snapshot intervals
include process close/observations and are not exactly the XML score windows.
No new lower reads or flush attempts were recorded in those intervals.

The write generator's pinned CPU was 99.4–99.7% busy, including 65.6–78.1%
user time. This motivates a buffer-generation comparison rather than attributing
all missing write throughput to the native cache. Microsoft documents `-Zr` as
creating fresh random content per I/O and adding runtime overhead; `-Z1M` uses
a precomputed random source buffer. See the official
[DiskSpd buffer contract](https://github.com/microsoft/diskspd/wiki/command-line-and-parameters).

## Preserved preparation failure

Plan 75 run `QueueCache-Verify-20260930-203416-e78ae9299b8b41539f22a68970a7047b`
ended INCOMPLETE 1/6: its original three-second cold warm read only completed
818,937,856 bytes, and the next pass still incurred misses. It was rejected
before final scoring, with clean restoration. Its raw files are preserved and
are excluded from the successful run. Plan 76 lengthened the first pass and
requires a full-file read; it retained the strict zero-miss second-pass proof.

## Maintained runner changes and verification

Plan 77 extends the opt-in sequential-resident suite to nine cases at three
repeats: reads, per-I/O random writes, and separately named precomputed-buffer
writes. Existing write-performance remains 72 cases with its original `-Zr`
contract. Case selection is recorded as a subset; completion of a selection
never declares the entire nine-case matrix verified.

Linux management contracts and Windows host-safe runner/contracts passed.
Managed Release build passed with zero warnings/errors. No native performance
code or driver runtime defaults were changed. This is focused collection and
comparison, not a durability, removal, full-matrix or performance acceptance gate.

## CrystalDiskMark-style precomputed write result

Plan 77 selected run
`QueueCache-Verify-20260930-205241-8657fa00c7c342e99769827220a2145d`
completed **3/3 selected cases**, independently restored the original healthy
2048 MiB profile, and recorded no collection or restoration failure. This is
the `precomputed` subset, not an assertion that all nine plan-77 cases ran.

| Repetition | Write MB/s (`-Z1M`) | IOPS | Write p99 ms |
|---|---:|---:|---:|
| 1 | 21,422.973 | 20,430.54 | 0.526 |
| 2 | 20,827.865 | 19,863.00 | 0.550 |
| 3 | 21,405.810 | 20,414.17 | 0.542 |
| Median | **21,405.810** | **20,414.17** | **0.542** |

Both requested approximate historical peaks are reproduced on CI 0.4.259.1:
read median **36,185 MB/s**, precomputed-random write median **21,406 MB/s**.
The two collections have independent run IDs and contracts. The fresh-random
write median remains 16,521 MB/s; changing buffer generation defines a different
workload, not an improvement in native driver performance. A matched old-driver
bisect is unnecessary to establish that the requested peaks remain achievable,
and was not performed. Host load, affinity and preparation may still affect GUI
results.

All precomputed cases passed the same complete-file/zero-miss resident proof.
Ready markers were true, with 26/27 samples and maximum gaps 0.226–0.241 s.
Snapshot intervals recorded zero new errors, capacity waits, read misses, lower
reads or lower flushes. Large-write copy offloads advanced by 102,365 / 99,321 /
102,282; generated drain bytes were approximately 59.3 / 29.4 / 30.4 MB, with
229 / 115 / 119 lower-write attempts. These observation intervals include process
close, so they must not be substituted for score-window rates. Generator CPU
user time fell from 65.6–78.1% with `-Zr` to 2.2–4.1% with `-Z1M`, supporting
buffer-generation overhead as the cause of much of that workload difference.
Final/raw outputs, process ownership/exits, control traces, recovery/restored
state, ready markers and telemetry intervals from both exact runs were inspected
and preserved privately. No owned benchmark process remained before the final
planned Verifier restart.

## Reproducing through the normal CrystalDiskMark UI

The normal Random-data UI already uses precomputed block-sized write buffers;
0 Fill is unnecessary. Its official
[write command and scoring code](https://github.com/hiyohiyo/CrystalDiskMark/blob/master/DiskBench.cpp)
selects `-Z<block-size>K` for Random writes, performs a preparation repetition,
and displays the highest scored repetition. The maintained runner reports all
repetitions and medians, so its aggregate is not the GUI's maximum.

Use Q: with 2048 MiB Fast/Idle cache, Verifier off after a planned restart,
1 GiB test size, five repeats, Random data, and the Default **SEQ1M Q8T1** row.
The [official UI settings](https://crystalmark.info/en/software/crystaldiskmark/crystaldiskmark-main-menu/)
allow the same 1 MiB / Q8 / T1 shape. A smaller fitting file can measure RAM peak
speed too, but then it is a different test size. Ordinary reruns create fresh
test files, so repeating All does not prove the next file is already resident.

These settings make comparable RAM-cache peaks possible; they do not guarantee
36/21 GB/s in the GUI. The strict ten-second plus zero-miss read proof in this
runner has no direct GUI equivalent. The actual earlier 0.4.249.1 Verifier-off
Default GUI run measured 23,367/18,452 MB/s on this VM. This latest-build turn
verified the requested peaks through the maintained CLI using the same bundled
DiskSpd; it did not run a new GUI benchmark or establish its remaining gap.
Verifier has been restored for driver-development validation, so another peak
benchmark would require deliberately turning it off and restarting again.

## Final restoration

After all owned benchmarks stopped and an explicit flush succeeded, the approved
planned restart restored Verifier **0x209bb**, resetonbootfail, for
`QueueCache-0.4.259.1-625D2AA0A974.sys`. Runtime `/query` confirmed load 1/unload 0.
The driver file retained the signed CI SHA-256. Q: was Active with saved startup
2048 MiB Fast/Idle, zero dirty/in-flight bytes, errors and last-error zero; C:/T:
had no configured cache. No benchmark/UI process remained. Read-only Proxmox
configuration confirmed the disposable disk was still detached and its
`unused0` backing `vm-109-disk-5` preserved. Nothing was attached or deleted.
