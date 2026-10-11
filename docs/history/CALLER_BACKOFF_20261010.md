# Caller-path cooldown comparison, 2026-10-10

Keep the production cooldown at 256. Removing it improves fitting NTFS mixed
Q8 throughput by 5.26%, but reduces resident sequential 1 MiB Q8 throughput by
21.18%. The smaller read and mixed Q1 controls stay within overlapping ranges.
These results do not justify a global default change or an untested policy that
changes the cooldown according to request size. The completed ReFS/Dev Drive
comparison gains 3.79% mixed Q8; its large-read ranges overlap. Neither result
justifies applying cooldown zero globally.

The supported `caller-backoff` suite alternates cooldown 256 and 0 over three
repetitions of four shapes: 64 KiB random 70/30 read/write Q1/Q8, random 4 KiB
read Q1, and sequential 1 MiB read Q8. Every shape uses one submitting thread
and a fully warmed 1 GiB file with a 2 GiB Fast/Deferred cache, read recall on,
copy flags 3 and detailed timing off. Each score lasts ten seconds. The original
runtime cache settings and cooldown are independently restored afterwards.

| NTFS workload | Cooldown 256, median MiB/s (range) | Cooldown 0, median MiB/s (range) | Change |
|---|---:|---:|---:|
| 64 KiB mixed Q1 | 8,897.40 (8,798.95–8,908.75) | 8,811.69 (8,802.93–8,945.55) | −0.96% |
| 64 KiB mixed Q8 | 7,685.96 (7,486.00–7,853.78) | 8,090.55 (8,079.86–8,127.74) | +5.26% |
| 4 KiB random read Q1 | 1,242.31 (1,241.99–1,252.36) | 1,240.75 (1,231.62–1,246.82) | −0.13% |
| 1 MiB sequential read Q8 | 35,992.91 (35,919.68–36,704.70) | 28,370.53 (27,473.63–28,543.46) | −21.18% |

| ReFS/Dev Drive workload | Cooldown 256, median MiB/s (range) | Cooldown 0, median MiB/s (range) | Change |
|---|---:|---:|---:|
| 64 KiB mixed Q1 | 9,224.79 (9,075.89–9,309.96) | 9,264.40 (8,876.42–9,277.05) | +0.43% |
| 64 KiB mixed Q8 | 7,783.93 (7,735.61–7,820.05) | 8,078.71 (7,917.85–8,136.13) | +3.79% |
| 4 KiB random read Q1 | 1,335.16 (1,316.75–1,360.88) | 1,346.03 (1,327.95–1,347.87) | +0.81% |
| 1 MiB sequential read Q8 | 33,832.97 (33,294.51–35,861.74) | 35,027.47 (34,057.94–35,840.06) | +3.53% |

Both filesystems completed all 24 collection and correctness windows. Raw XML,
exit/stderr files, residency proofs, routing deltas, telemetry readiness and
coverage, post-score concurrent byte oracles and disabled-cache persisted oracles
were inspected. Every read control used zero lower read/write/flush attempts.
The fitting NTFS mixed windows also happened to record zero lower attempts; this is
an observation, not their acceptance contract. All 48 windows staged zero disk
reads, so they provide no partly cached read overlap evidence.

The V23 counters explain the routing change without equating candidates to
successful caller service. At NTFS mixed Q8, cooldown 256 served essentially no
scored requests on the caller path; cooldown 0 served a median 398,098 reads and
170,690 writes there. Queued arrivals still account for a median 728,834 first
declines with cooldown 0. At sequential Q8, worker/offloaded-copy occupancy still
prevents most caller admissions in both modes. A median 65,268 caller reads with
cooldown 0 accompanies a lower aggregate score. More caller service is therefore
not itself a performance win. The counters cover the enclosing process interval,
including startup and close, rather than the exact DiskSpd score window.

ReFS mixed Q8 shows the same routing shift: median caller-served reads/writes
rise from 0/0 to 321,567/137,934. Queued arrivals remain the dominant first-decline
reason (841,379 with cooldown zero). ReFS mixed Q1 already serves nearly all
requests on the caller path at cooldown 256, with only 1,285 cooldown declines
among about 1.48 million eligible candidates. The results do not support treating
ReFS itself as a reason to bypass all cooldowns. Filesystem tables compare modes
within each run; they are not a controlled ranking of NTFS against ReFS.

After each score, four independent deterministic 1 MiB files were repeatedly
overwritten and read concurrently for five seconds, then checked again after
Disable drained the cache. These validate those files, not DiskSpd's random
payload. The cooldown experiment never bypasses queued controls, pending work,
an active worker/caller owner or offloaded copies. Forced ordering, error and
capacity scenarios are separate qualification evidence.

NTFS run: `QueueCache-Verify-20261010-043439-25f6f295740048c3a933ecc726d9f4f0`
in `C:\QueueCache-Results\CallerNTFS514-20261010`, 04:34:39–04:54:10 UTC,
verification plan 113. All 800 retained owned-process exit records are successful.
Maximum telemetry gap was 0.250415 seconds against the unchanged two-second
limit. The original disabled/zero-budget lab cache was restored with cooldown
256, no dirty/in-flight bytes and no driver error. The backing Q: cache stayed
paused to isolate the lab VHDX from another cache; saved user profiles were
unchanged. No UI, competing DiskSpd or profiling trace ran during scoring.

The loaded signed CI driver was 0.4.514.1, native source `0b450a2`:

| Module | SHA-256 |
|---|---|
| Volume filter | `C8856CCD2DABAC66B9648FBFE6CF0A3697A2F2EB4CA8D936806E683E6348B764` |
| RAM provider | `8BC6DF3260FA77B447E7DF737800370706A10D324F345FC2A4C6A5AE88EA9A8C` |
| CDM 9.0.3 bundled DiskSpd64 | `7281BF6DA6C03797016EDDF2E8AAEC4C644AE893D403D57A030B7E2E14B61079` |

Actual loaded module paths and hashes were recorded, not inferred from a version
string. Managed tooling was a clean committed `1788970` self-contained preview;
its native source is unchanged from `0b450a2`. The elevated interactive launcher
and owned children ran at normal priority. NTFS used the new owned V: lab volume
on a non-OS disk. All raw evidence remains private and immutable; the complete
archive SHA-256 is
`404A57BDD3B680B67651D6D90E04E1BD47967BB98BA6CD61CC69AB2249ECA07C`.

ReFS/Dev Drive run:
`QueueCache-Verify-20261010-065536-e440846e99264ddb86145783b78c8615`
in `C:\QueueCache-Results\CallerReFS514-20261010`, 06:55:36–07:15:44 UTC,
same plan/tools/native modules/DiskSpd. The new owned R: Dev Drive was created by
the supported lab-disk command on a separate fresh VHDX. All 788 owned-process
exits are successful, all per-score concurrent/persisted byte checks pass, and
maximum telemetry gap is 0.391809 seconds. Its original disabled/zero-budget
cache and cooldown 256 restored with zero dirty/in-flight bytes and errors.
There were no capacity waits during either filesystem's scoring windows. The
complete ReFS raw archive SHA-256 is
`9BCC8B2EF2AB5675D5709A234A293612ED0F5894D89A5136AB69358C526A49B1`.
