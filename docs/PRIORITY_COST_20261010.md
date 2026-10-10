# Independent cached-read priority comparison, 2026-10-10

The supported plan-113 `priority-cost` suite separates the settings changed by
Task Scheduler's below-normal default. Three read shapes (resident sequential
1 MiB Q1/Q8 and random 4 KiB Q1) each compare four variants over three alternating
repetitions: explicit normal controls, CPU BelowNormal only, low I/O hint only,
and low process memory default only. The complete suite has 36 windows.

Every case uses a fitting 1 GiB file and 2 GiB Fast/Deferred cache, read recall 1,
copy flags 3 and timing off. The runner proves residency before scoring and
requires zero lower read/write/flush attempts, unchanged miss/drain counters,
accounting for all scored bytes, stable cache identity and complete telemetry.
Scores last ten seconds after a three-second warmup. The owned child's CPU and
memory settings are applied/read back before that warmup ends, within a strict
two-second application deadline. XML independently records the requested I/O hint.

CPU and process memory controls are normal/5 except for the setting under test;
BelowNormal uses CPU class 16384, low process memory uses 2, and low I/O uses
DiskSpd `-I2` instead of normal `-I3`. These are requested process defaults and
I/O hints, not a claim to control every thread/page or to inject memory pressure.
Locked cache pages, the coordinator, driver priorities and user applications are
unchanged. The historical combined scheduled-task result remains in
[BENCHMARKING.md](BENCHMARKING.md); it cannot identify an individual cause.

The signed native driver is 0.4.514.1, source `0b450a2`; clean managed preview
tools are from `1788970`. Actual loaded filter/provider paths and hashes are
recorded in each manifest. Native modules and the CDM 9.0.3 DiskSpd binary are
the same as the [NTFS/ReFS cooldown comparison](CALLER_BACKOFF_20261010.md).
All launches are elevated, interactive and normal priority, with the desktop
closed, no competing workload or trace, and the backing Q: cache paused without
changing saved profiles.

## Preserved incomplete attempt

`QueueCache-Verify-20261010-071617-21a3d11a0fc748f4b1d68be4c7c286cc`
in `C:\QueueCache-Results\PriorityCost514-20261010` ran 07:16:17–07:30:53 UTC
on the owned V: NTFS volume. It recorded 26 successful measurement cases and
failed case 27 (`0027-r3-read-q1-priorityIoLow`). This is **INCOMPLETE**, not a
36-case pass; its samples are not combined with another run for acceptance.

The failed enclosing interval recorded exactly one additional staged ordinary
lower read: 8,192 bytes, entirely missing rather than partly cached. Lower write
and flush attempts, drained/accepted bytes, evictions and error counters were
unchanged. Read-hit accounting covered all scored bytes. No paging-read counter
changed. The counters do not identify the read's file or process, so background
metadata is a possible explanation rather than established attribution. The
zero-lower-I/O rejection remains strict.

Independent restoration succeeded: original disabled/zero-budget cache,
cooldown 256, no dirty/in-flight bytes and no driver error. Owned DiskSpd/worker
processes exited; only the installed management service remained. The immutable
complete raw archive SHA-256 is
`03754C6763DB40212394FC646AD6BBB219BCB576AA4FD3C98E94E0F46FE68BD9`.

## Completed untraced comparison

The complete retry on previously unused W: NTFS completed **36/36**,
07:34:14–07:53:17 UTC, with independent restoration and no driver error.
It has new immutable IDs and does not reuse the incomplete attempt's samples:
`QueueCache-Verify-20261010-073414-2f6dd18225094fc98abc3560ddaca39e`
in `C:\QueueCache-Results\PriorityCost514RetryW-20261010`.
All raw XML/recognized trailers, commands, exits/stderr, priority readbacks,
residency, counters, readiness/coverage and final reports/restoration were
inspected. All 940 owned process exits are zero. Maximum telemetry gap:
0.689031 seconds; maximum setting application time: 0.01089 seconds, below the
two-second limit. All 36 windows had zero lower read/write/flush attempts and
zero staged reads; hit accounting covered all scored bytes. Original W: state
was disabled/zero budget and was restored exactly, with backoff 256, zero
dirty/in-flight bytes, errors and last error. The immutable raw archive SHA-256
is `5A97128327D6C155EDE6DB82252182C14F310D8D339E2581C7296826A054A8DF`.

Each cell is a three-repetition median (minimum–maximum), in MiB/s:

| Shape | Normal | CPU BelowNormal only | Low I/O hint only | Low memory default only |
|---|---:|---:|---:|---:|
| 1M sequential Q1T1 | 18,689.61 (18,626.87–19,428.67) | 18,511.79 (18,496.40–18,548.15) | 18,576.52 (18,308.99–19,697.00) | 18,580.02 (18,554.35–18,631.27) |
| 1M sequential Q8T1 | 36,699.00 (36,541.86–39,198.90) | 24,147.65 (24,024.48–24,663.04) | 36,648.95 (35,647.05–38,984.42) | 36,915.58 (35,531.27–37,099.20) |
| 4K random Q1T1 | 1,245.19 (1,231.68–1,253.52) | 1,241.80 (1,233.50–1,242.83) | 1,271.56 (1,257.17–1,273.88) | 1,253.55 (1,246.32–1,257.44) |

**CPU BelowNormal causes a repeatable 34.20% large-read Q8 loss in this
comparison**, with separated ranges and no disk I/O. CPU Q1 and random Q1
medians change −0.95%/−0.27%; the Q1 CPU ranges are separated but the effect is
small. I/O and memory Q8 medians change −0.14%/+0.59%, with overlapping ranges:
neither reproduces the large Q8 loss under these resident conditions. Low I/O's
random Q1 median is 2.12% higher with separated ranges; this small three-repeat
observation does not establish a general low-I/O advantage, especially for real
disk misses. No memory-pressure behavior was tested.

The historical combined-priority experiment also covered disk-only and RAM-disk
work. These cached-read windows isolate one large CPU-class effect; they do not
fully attribute every historical score. Keep normal-priority benchmark launches.
No application boost or driver-priority/default change is implemented.

## Focused CPU/wait attribution

A separate WPR CPU trace is running with the supported Q8 subset, one repetition
per variant, on the same native/binary hashes and W: target. Its traced scores
are excluded from the untraced comparison above. Matching signed-CI native
symbols are preserved. Inspect context switches, ready/wait time and copy/helper
stacks; reject lost events. Until attribution is complete, helper priority and
handoff are hypotheses rather than proven causes or accepted optimizations.
