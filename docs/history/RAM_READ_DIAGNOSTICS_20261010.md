# RAM read diagnostics, plans 115-117 (2026-10-10/11)

Moved from the performance findings. These diagnostics measured where single-reader
RAM-disk reads spent their time; their outcome is the accepted asynchronous split
reads change described in [performance findings](../PERFORMANCE.md). Not
maintained.

## Plan 115 attribution hypothesis

The unchanged Direct path completes synchronously; provider split copying uses
256 KiB caller/helper work and helper withdrawal/waits. One submitter may be
limited by admission or coordination while several submitters keep more useful
copy work in flight. The maintained three-shape attribution suite samples CPU
and scheduler behavior with matching symbols, preserving the native copy path.
A higher WorkerMain share alone does not prove polling overhead: it contains both
copy and polling. Process intervals include startup/warmup; measured read bytes
remain separate. Exact request overlap and coordination timing remain unknown
without instruction mapping or scoped native counters. No new gain is claimed.

### Attribution bootstrap failures (no performance result)

The first launch rejected phase-only `--trace-symbols` during generic campaign
validation before allocating a campaign. Validation now scopes it to the selected
RAM phase, and all registered RAM suites share the same integrity dispatch.
These fixes pass Windows contracts and Debug/Release CI at `8997447`.

The next attempt, `QueueCache-Campaign-20261010-180641-82bbd17c82f2409c81c198801840e0a6`,
completed four retained correctness phases, then stopped while exporting the WPR
profile: `0x80070006` (invalid handle). No trace or score began. The same installed
WPR exported the same profile successfully to a short path; the nested campaign
filename exceeded its native path handling. The run remained INCOMPLETE (4/6),
took 212.45 s and restored cleanly. Its raw evidence remains separate.

WPR now exports/records/stops in a short unique journal-owned temporary directory;
managed I/O copies the profile and finalized ETL into the original run. Publishing
the ETL uses a same-directory rename after copying, retains the staging source
until publication, and resumes after a worker exits between stop and publication.
Only empty staging directories are removed; unexpected evidence is preserved.
Host contracts cover deep paths, cross-directory refusal and interrupted finalization.
This is a verification-tool compatibility fix, not a driver speedup.

Installed-tool smoke then proved collection/finalization at deep paths, but the
bundled native analysis reader rejected that deep path (`E_UNEXPECTED`). A short,
hash-verified clone makes the reader open the same ETL successfully. The next
smoke was correctly rejected for **45,918 lost events**; its original ETL remains
immutable. No attribution or performance conclusion comes from either smoke.
Plan 116 derives the installed CPU file profile with 128 × 1 MiB buffers per
collector (maximum two collectors/256 MiB configured pool capacity). It records
original/derived definitions and hashes plus actual collector status. Missing
samples remain failures; `AllowLostEvents` stays false. The enlarged diagnostic
pool is a recording-contract change, not a production/default speedup.
The enlarged-pool installed-tool smoke passes on Windows: 350 samples and 299
scheduler activities for its owned test PID, both collectors read back 128 ×
1 MiB buffers with zero lost events, and strict final ETL processing succeeds.
All Windows host contracts pass in that preview. Follow-up ownership hardening
gives each collector a unique name and enforces its startup buffer/loss readbacks.
The final smoke (`Wpr-Smoke-7bd5858fdd7e404ab7666acce19ce941`) passes with
351 owned samples and 409 scheduler activities, zero lost events and all Windows
host contracts passing. Windows Debug/Release build and CLI CI #552 pass at
`78ecfb2`. This qualifies collection/analysis tooling, not driver performance;
the actual three-shape diagnostic is tracked separately.

Schema-2 analysis retains native sampled/return-context addresses and available
source lines, with unknown process samples separate. `verify-attribution` reuses
a finalized owned run in a unique analysis folder: it checks the worker/plan,
original ETL digest and original PDB signature/hash without workloads or driver
access. Reanalysis preserves the original collection verdict. Machine-wide
samples and observed predecessor scheduler waits are not exact score-window CPU
or complete blocked/ready totals; resource-tagged request overlap remains unknown.

### Plan 116 RAM attribution: useful copies versus polling

The corrected diagnostic completes **6/6 phases** in **458.47 s (7 min 38 s)**,
run `QueueCache-Campaign-20261010-215743-5ce1b38bf4c747a0868bf6eab778c43d`.
Managed preview source is `78ecfb2`, plan 116; the native baseline remains signed
0.4.514.1/source `0b450a2`. Filter/provider SHA-256:
`C8856CCD2DABAC66B9648FBFE6CF0A3697A2F2EB4CA8D936806E683E6348B764` /
`8BC6DF3260FA77B447E7DF737800370706A10D324F345FC2A4C6A5AE88EA9A8C`.
DiskSpd remains CDM 9.0.3's x64 2.2,
`7281BF6DA6C03797016EDDF2E8AAEC4C644AE893D403D57A030B7E2E14B61079`.
The fitting file is 1 GiB on an owned 2 GiB Direct RAM fixture; 256 KiB split
copies, default affinity, Normal CPU, memory priority 5, I/O hint 3, W3 and timing
off are unchanged. This is one traced repetition, not speed acceptance.
Tracing runs alongside these reads and can alter scheduling. There is no same-run
untraced control, so its effect on the difference from earlier approximately
26 GB/s scores is unquantified. The earlier untraced matched controls remain the
performance baseline; these scores do not establish a new-driver regression.

| Exact diagnostic case | CPU samples / resolved native samples | Provider memcpy samples | Helper poll samples at `transfer.cpp:109` | Caller helper-wait samples at `transfer.cpp:173` | Traced score, decimal GB/s |
|---|---:|---:|---:|---:|---:|
| `0001-r1-attribution-q1-t1` | 52,318 / 44,975 (100% native resolved) | 24,367 (46.58%) | 17,326 (33.12%) | 1,075 (2.05%) | 24.372 |
| `0002-r1-attribution-q8-t1` | 52,271 / 46,224 (100%) | 24,279 (46.45%) | 16,779 (32.10%) | 1,495 (2.86%) | 20.799 |
| `0003-r1-attribution-q2-t4` | 52,284 / 45,713 (100%) | 35,494 (67.89%) | 3,673 (7.03%) | 1,196 (2.29%) | 37.270 |

Percentages use **all machine-wide CPU samples in each enclosing benchmark PID
lifetime**, including startup/warmup, not score-window CPU time. Provider memcpy
uses the exclusive sampled function. Poll/wait counts use sampled instruction
addresses, excluding stack return contexts; native inclusive stack counts overlap
and must not be summed as CPU time. Polling maps to RVAs `0x2696`/`0x2699` in
the bounded PAUSE loop, rather than assuming all `WorkerMain` samples are waste.
Known busy-sample fractions are 99.74/96.92/96.21/95.49% across CPUs 0–3 at Q1,
99.87/96.01/94.90/93.88% at Q8, and about 99.7–99.8% with four readers. These
are sampling fractions, not a new utilization or latency acceptance contract.

Observed benchmark-thread predecessor ready-time sums are 251.27, 354.87 and
15,597.59 ms respectively. They sum multiple threads, include preceding waits
at in-window switch-ins and are not complete window totals. Waiting-duration
fields are unavailable and remain null. Exact simultaneous Direct requests,
post-to-take latency and owned-resource helper participation remain unknown.

**Decision: B2 completed; throughput cause remains inconclusive/no justified
optimization.** The trace proves polling CPU cost, and four submitters keep more
useful copy work supplied. It does not prove that shortening polling improves
throughput: polling keeps helpers responsive, may be a consequence of a single
submitter supplying work slowly, and earlier duration tuning found no gain.
The relatively small sampled completion-wait share also does not demonstrate a
dominant handoff stall. Stage C is skipped under the handoff's B exit gate;
no new native mode/default or promised 42 GB/s single-reader score is added.
If revisiting this, B3 should first answer only the missing question: per-resource
Direct active/peak requests plus helper posted/taken/withdrawn counts, useful
caller/helper bytes and sampled post/take/wait timing, default-off with an
off/on/off perturbation comparison. It must distinguish supply from handoff
latency before choosing an asynchronous or helper-admission change.

Evidence inspection: all 75 recorded owned exits are successful; 26 readiness
files, three raw XML profiles and enclosing native telemetry intervals are
present. Each window has 15 native samples; maximum gaps are 1.020724,
1.010109 and 1.013838 s, below the unchanged two-second bound. Native errors and
recorded image read/write attempts remain zero; Direct byte accounting and all
three independent whole-file hashes pass. WPR startup reads back two unique
collectors at 128 × 1 MiB; strict final TraceProcessor analysis accepts zero lost
events. Matching PDB GUID/age gates pass and every native sample resolves.
ETL is 264,241,152 bytes, SHA-256
`FE68D2A227F39C02A819D51E1491066047C5A33D4BC748EEE765E3B596133F6B`.
All five retained correctness phases complete, including fault/ordering last.
Final restoration succeeds in 8.86 s; the controller validates the durable event
and records REVIEWED after inspection. Raw evidence remains private and immutable.

The first separate priority/affinity campaign
`QueueCache-Campaign-20261010-181219-edcb92e1d3bd4a49877854b8675108c0`
stopped before benchmarking: the first sector policy case did not observe a clean
admission boundary after preparing a new NTFS file. It completed 2/6 phases in
47.44 s and restored cleanly. The writer/file was not attributed. Source review
found that the test drained only the cache after the setup write, allowing delayed
filesystem setup writes to arrive afterwards; this is a plausible preparation race.
Preparation now explicitly flushes the owned file before the existing cache
flush/drop-clean boundary; the unchanged guard also reports dirty/in-flight/error
values on failure. The same setup boundary is used for the observed replacement
case. This is a scoped test-preparation fix; the failed campaign contributes no
performance samples and the guard is not relaxed.

### Plan 116 cached priority/affinity: most Q8 penalty is affinity-sensitive

The scoped preparation fix passes both focused campaigns. The new, **untraced**
priority/affinity campaign completes **6/6 phases, 41/41 outer cases** in
**1501.60 s (25 min 02 s)**, run
`QueueCache-Campaign-20261010-220615-fac181574f6f4518b1b088547357530f`.
Its inner checks are 81 retained correctness checks plus **36 score windows**,
separate from the earlier 36-window CPU/I/O/memory `priority-cost` experiment.
Managed preview/source, signed native filter/provider and DiskSpd hashes are the
same as the plan-116 RAM run above. Roles are Q: non-OS physical performance disk
and W: validated NTFS lab VHDX; Q:'s active backing cache is explicitly paused
while measuring W:. ReFS is not part of this comparison.

Each shape compares Normal/BelowNormal CPU and default/`-n` benchmark affinity,
three repetitions with reversed order in repeat 2. File size is 1 GiB in a 2 GiB
cache, one benchmark thread, memory priority 5, I/O hint 3, W3, duration 10 s,
read-only unbuffered OS mode, copy flags 3, recall 1, caller backoff 256 and
detailed timing off. Rows report **MiB/s**; R1–R3 follow repetition identity,
not execution order. All exact IDs/settings/scores remain in this run's immutable
manifest/results/raw files.

| Shape | CPU | Benchmark affinity | R1 MiB/s | R2 MiB/s | R3 MiB/s | Median MiB/s | Min–max MiB/s |
|---|---|---|---:|---:|---:|---:|---:|
| 1M Q1T1 | Normal | default | 19,465.83 | 17,363.04 | 17,446.05 | 17,446.05 | 17,363.04–19,465.83 |
| 1M Q1T1 | Normal | unbound | 17,719.28 | 17,663.14 | 17,925.87 | 17,719.28 | 17,663.14–17,925.87 |
| 1M Q1T1 | BelowNormal | default | 18,641.06 | 17,836.96 | 15,681.42 | 17,836.96 | 15,681.42–18,641.06 |
| 1M Q1T1 | BelowNormal | unbound | 17,635.86 | 17,360.84 | 16,111.99 | 17,360.84 | 16,111.99–17,635.86 |
| 1M Q8T1 | Normal | default | 37,995.70 | 30,688.21 | 26,462.74 | 30,688.21 | 26,462.74–37,995.70 |
| 1M Q8T1 | Normal | unbound | 33,077.42 | 30,007.29 | 25,342.36 | 30,007.29 | 25,342.36–33,077.42 |
| 1M Q8T1 | BelowNormal | default | 21,418.18 | 22,594.11 | 20,414.39 | 21,418.18 | 20,414.39–22,594.11 |
| 1M Q8T1 | BelowNormal | unbound | 29,064.04 | 29,173.83 | 26,551.15 | 29,064.04 | 26,551.15–29,173.83 |
| 4K random Q1T1 | Normal | default | 1,206.06 | 1,152.28 | 1,121.14 | 1,152.28 | 1,121.14–1,206.06 |
| 4K random Q1T1 | Normal | unbound | 1,208.03 | 1,146.52 | 1,116.65 | 1,146.52 | 1,116.65–1,208.03 |
| 4K random Q1T1 | BelowNormal | default | 1,178.45 | 1,180.91 | 1,135.63 | 1,178.45 | 1,135.63–1,180.91 |
| 4K random Q1T1 | BelowNormal | unbound | 1,148.34 | 1,147.47 | 1,105.71 | 1,147.47 | 1,105.71–1,148.34 |

**Finding:** cached Q8 BelowNormal versus Normal changes **−30.21%** at default
affinity and **−3.14%** unbound. Default-affinity ranges do not overlap; unbound
ranges overlap substantially. The loss shrinks 27.06 percentage points. Within
BelowNormal, unbound Q8 improves its median **35.70%**; each paired repetition
improves by about 29–36%. Normal Q8 unbound changes −2.22% with overlapping
ranges, so this is not a general recommendation to unbind or boost workloads.
Q1 CPU changes are +2.24%/−2.02% default/unbound; random Q1 +2.27%/+0.08%, with
overlapping ranges and no consistent penalty. Normal Q8 spans a wide range and
drifts downward across repeats; three-repeat medians are diagnostic, not a new
release comparison or proof that the residual 3.14% is real.

The implicated interaction was reviewed against the **preserved matched-514 CPU
profiles**, without rerunning the completed priority matrix. The earlier Q8
Normal/BelowNormal trace (`20261010-075450-78a3c72b51f74c20839c855175eb9f1c`)
uses the same native PDB hashes as above, rejects lost events, and records ETL
SHA-256 `26C2F946D8F06DAC5D3321E443D8ED04FBA294C6AA6ACBFBC92465CD5B9C598A`.
Its default-affinity BelowNormal CPU 0 is 99.3% busy while CPUs 1–3 are 44.0–49.1%
idle, and enclosing benchmark-thread ready time is 6.358 s versus Normal 1.454 s.
That evidence supports a scheduling/CPU-placement explanation for the new
factorial result. It does not identify a specific worker-priority fix: exclusive
`RequestWorker` contains useful work as well as polling; some readying-process
attribution is unknown, and summed predecessor waits include startup/termination.
No new unbound CPU trace was collected, so restored multi-core utilization and
the exact responsible threads in that cell remain unverified.

**Decision:** preserve Normal priority in benchmark guidance and keep application,
driver and user-process defaults unchanged. Do not repeat `priority-cost` or this
36-window factorial just to rediscover the penalty. If pursuing a product fix,
the next discriminating measurement is a maintained, separate traced Q8-only
Normal/BelowNormal × default/unbound subset with CPU/thread/source attribution,
using the owned trace machinery and matching symbols. Keep traced scores separate
from this completed untraced evidence; do not silently reinterpret the saved
default-only profiles as an unbound measurement. No priority boost or native
candidate is accepted from this diagnostic.

Raw inspection: all 36 XML profiles and owned scheduling readbacks match; maximum
priority-application time is 0.0174825 s. All score boundaries have zero lower
read/write/flush-attempt, staged-request and read-miss deltas, stable instance/
generation, timing off and read-hit bytes covering score bytes. The lab cache's
historical error count stays 3 during scoring, with LastError 0; planned faults
run afterwards. Maximum telemetry gap is 0.845574 s. All 1004 recorded owned
process exits are zero, 313 readiness files are present, all 81 retained inner
correctness checks pass, and restoration completes in 10.43 s. The controller
validates the immutable terminal event. Subsequent session cleanup detaches the
task-attached NTFS lab and resumes the original installed tray in session 1;
Q: returns to its original 2 GiB Fast/Idle state, timing off, dirty/in-flight/
errors zero and the same native hashes. Saved profiles match the captured bytes.

### Final campaign restoration gap found by controller smoke

The first plan-115 controller smoke completed both test phases in about 50 s,
but final restoration verification failed: the resumed Q: backing cache had
transient dirty filesystem metadata. The controller validated the nonzero exit's
`RESTORATION_FAILED` event; it did not label it successful or relaunch work.
Exact failed campaign: `QueueCache-Campaign-20261010-173916-42bc00c7467348efa904638243fd9ed3`.
Recorded backing restoration had completed, the native cache remained healthy,
and subsequent inspection found zero dirty/in-flight bytes. Finalization now
uses the existing explicit bounded filesystem/cache preparation on an enabled
performance baseline before the unchanged strict clean capture. Faults still
stop restoration; disabled observations do not drain. Regression smoke `QueueCache-Campaign-20261010-174542-93f8175618624be6b93d7d6d73f58100`
completed 2/2 phases in 67.19 s with exact clean restoration, 28 zero-exit owned
process records and nine readiness files. The real controller validated success
and failure events without rerunning work for delivery. Live Manager wake remains
unverified until the new service/helper is deployed.

## Finalization diagnostic: request overlap and helper coordination, 2026-10-11 (historical)

**Final performance outcome: no additional acceptable speedup found in this
bounded investigation.** The existing ordered allocator, bounded RAM-cache
copies, Direct RAM access, synchronous provider split copies and read recall
remain retained. This closes the current research scope, not the possibility of
all future optimization. No performance candidate was implemented or accepted.
The separate live Manager wake qualification remains unverified.

### Implementation and provenance

Source `0575e978c3a01449da71128aa0e4f5c9e6660e2a` adds default-off, per-store
coordination diagnostics and plan-117 `ram-read-coordination`. Eligible Direct
read-copy intervals count starts/completions, active/peak copies and bytes.
Provider split copies count posted/taken/withdrawn helper entries and caller/helper
bytes. QPC timings are sampled once per 64 operations. Mode changes drain diagnostic
users before resetting their generation. Existing public protocol prefixes remain;
the private filter/provider store contract is version 2, requiring the matching pair.
No copying, chunk size, helper scheduling or priority optimization is bundled.

The new `experiment` campaign composes exactly one selected suite and preserves
owned processes, explicit volume roles, restoration and validated terminal events.
It does not attach the 81 retained policy checks. Candidate suites remain
unimplemented because their evidence gate was not reached.

Linux host contracts pass. Windows Debug/Release native, managed and CLI checks
pass in [CI run 38096468882](https://github.com/devedse/QueueCache/actions/runs/38096468882),
23:51:41–23:58:27 UTC on 2026-10-10 (**6 min 46 s**). Signed lab release 0.4.568.1
records synthetic PR merge `12771e5f08202a8a3d401a839712e33260e6b397`; its tree
is identical to source `0575e97`, verified from both parents and the tree diff.
Actual loaded filter/provider SHA-256 after the planned deployment restart:

- Filter: `58517D6CFA15D7BD70355BD056C6AE68E777CB5BA0FCECEA27BAC1C2D2EB9467`.
- Provider: `BEC860DF80DAC314E4366E2A49D7AE7D37AE29852A9BA6596DB5A901453CE855`.

The installer took **28.662 s** and returned its expected restart-required exit
3010. Private deployment/restoration archive SHA-256:
`76C6FA9B4334E73248A8D609F13509CCDED895EFA3869B082E26455B08FFFBA0`.
Cache drain,
staged hashes/signature, strict registration and saved profiles were checked
before the planned restart; actual loaded hashes, adapter and broker were checked
afterward. Verifier remained off. This was deployment, not fault recovery.

### Exact run and measurements

Campaign `QueueCache-Campaign-20261011-030244-6c5d802829a74dad95f82656a87be700`
ran 03:02:44.590–03:07:17.608 UTC, **273.019 s (4 min 33 s)**. Its only phase
was `ram-read-coordination`, child
`QueueCache-Verify-20261011-030301-70699e7ac29240bcbc0b0872d22f8980`.
The nine-window case took 245.339 s; phase wall time was 249.753 s;
final campaign restoration took 8.209 s. All nine checks PASS; phase and campaign
are COMPLETED/RESTORED. Raw archive SHA-256:
`9EE925A1B1246387E6A5EEAC6EEFDB89292A9D30CCA53BA21250B093B7ECF861`.

Fixed settings: owned 2 GiB Direct RAM disk, patterned 1 GiB file, sequential
1 MiB reads, 256 KiB split copying, default affinity, Normal CPU, memory 5,
I/O hint 3, timing off, W3 plus 10-second score. Four readers use separate lanes
(`-s4M -T1M`, no `-si`). Same CDM DiskSpd binary/hash as the historical controls.

| Shape | Off before / on / off after MiB/s | Off drift | On versus off mean | Diagnostic guard |
|---|---:|---:|---:|---|
| Q1T1 | 23,179.32 / 24,851.15 / 25,028.27 | +7.98% | +3.10% | Drift; do not infer a timing/overhead effect |
| Q8T1 target | 24,687.21 / 24,381.82 / 24,896.40 | +0.85% | −1.65% | Within the 2% diagnostic guard |
| Q2T4 separate lanes | 37,517.58 / 36,224.78 / 35,935.96 | −4.22% | −1.37% | Drift; do not infer a timing/overhead effect |

These are individual diagnostic windows, not medians or a speedup comparison.
The Q8 off controls are **25.886/26.106 decimal GB/s**; four-reader off controls
are 39.340/37.682 GB/s. No new engine is being compared to the old engine.
Q1's first window also records 116,736 bytes of ancillary Direct writes and 19
write-generation increments; provider and image I/O counters do not change, and
the protected workload file's full hash stays identical. The RAM-reference
contract permits other in-memory volume activity; these writes are not evidence
of lower-disk traffic or payload modification. Their role in drift is unproved.
No collection correction/retry was needed for the primary Q8 diagnostic.

| Diagnostics-on shape | Peak active copies | Started = completed | Posted / taken / withdrawn | Helper share of split bytes |
|---|---:|---:|---:|---:|
| Q1T1 | 1 | 325,353 | 974,791 / 960,219 / 14,572 | 74.11% |
| Q8T1 target | 1 | 319,352 | 956,812 / 950,649 / 6,163 | 74.60% |
| Q2T4 separate lanes | 4 | 472,765 | 1,416,383 / 658,638 / 757,745 | 38.60% |

Q8 unused posts are **0.644%**, versus 1.495% Q1 and 53.499% with four readers.
All snapshots end with active=0, posted=taken+withdrawn, and caller+helper bytes
equal split bytes. Direct byte totals match the enclosing native Direct delta
and cover scored bytes. Each diagnostics-on generation starts cleared; all nine
restore readbacks are disabled/zero-active. Helpers run on CPUs 1–3 in single-
reader windows (mask 14), and CPUs 0–3 with four readers (mask 15).

| Shape | Sampled Direct copy interval μs | Posting μs | Withdrawal μs | Final helper wait μs | Post-to-take μs | Direct / split / take sample counts |
|---|---:|---:|---:|---:|---:|---:|
| Q1T1, drift caveat | 24.103 | 0.618 | 0.337 | 2.549 | 1.146 | 5,084 / 5,079 / 15,013 |
| Q8T1 target | 23.412 | 0.757 | 0.364 | 3.041 | 1.201 | 4,990 / 4,986 / 14,844 |
| Q2T4, drift caveat | 79.827 | 0.574 | 0.436 | 2.272 | 6.569 | 7,387 / 7,380 / 10,292 |

Counter/timing intervals enclose process startup, warmup and closure; Direct
intervals exclude validation, MDL mapping and final IRP completion. Peak measures
simultaneous admitted copies, not every pending Windows request. These timings
have different sampled populations and overlap useful helper copying; do not
sum them as request latency or subtract them from a score as recoverable gain.

### Cause table and final decision

| Observation | Supported mechanism | Limit / competing explanation | Decision |
|---|---|---|---|
| Q8T1 reaches copy peak 1; four submitters reach 4. | The synchronous Direct path admits one active copy with one submitter; more submitters provide overlap. | This measures the copy interval, not all pending requests, and does not qualify an asynchronous implementation. | No small admission change selected. A new asynchronous design would need ownership, cancellation, progress and control proof; it is outside this bounded closure. |
| Q8 helpers copy 74.60% of bytes and take 99.356% of posts. | Helpers already participate promptly in useful copying. | Some taken helpers may copy no chunk, but unused withdrawals are rare. | No demonstrated Q8 gain from suppressing posts. Keep the current split helper path. |
| Sampled Q8 final wait is 3.041 μs. | The caller waits for helpers that joined its stack-backed split to finish. | Waiting overlaps necessary work; removing it would risk unfinished copies or use of expired stack state. | No speculative wait/poll change. Earlier spin-duration tuning also found no gain. |
| Four-reader withdrawals are 53.499%. | More callers can complete chunks before a queued helper joins. | This is a control shape; it does not establish a ≥5% Q8 target benefit. Its timing brackets also drift. | Do not substitute a four-reader helper sweep for the requested single-reader investigation. |

No candidate clears the feasibility gate. The candidate screen, 36 additional
controls, new candidate lifecycle checks and broad release/write/sustained
campaign are **not run**, rather than marked passed. No README score/screenshot
is changed because no product speedup was accepted. Existing validated results
remain authoritative for their original code/conditions.

### Verification, automation and restoration

Raw inspection confirms nine full 1 GiB before/after hashes, nine valid DiskSpd
XML/settings/Normal-memory-5 readbacks, 24 zero-exit owned process records,
12 readiness files and 135 native samples. Maximum native gap is **1.023141 s**,
below the unchanged two-second guard. All windows have zero image read/write
attempts and no provider-read traffic increase; native errors stay zero. The
owned resource's final pre-cleanup ImageIo snapshot also records zero read,
write and flush attempts across its observation epoch.
These checks qualify the retained research path and off/on/off resource lifecycle,
not unimplemented asynchronous request races or every kernel interleaving.

Independent completion smoke
`QueueCache-Campaign-20261010-235349-9ea2613ab5264e8ea7c29eadad2f5de3`
passes 2/2 phases in **78.181 s**, restores in 8.571 s, and has 28 zero-exit
owned records/nine readiness files. Raw archive SHA-256:
`94448EA39B3A1A6C4A7F2CA4C2114897C861DC57BB2B1C5EEBB634C80BDC68CB`.
Manager jobs `9ee931b7-8e73-435b-a8ca-5418b9119ca4` (smoke) and
`76c44478-8c58-41b3-8fa7-1c53b812ee42` (diagnostic) reach VALIDATED after actual
exit and read-only completion validation. Delivery remains pending while busy;
results were inspected in the active turn and acknowledged with `job-reviewed`.
No actual automatic idle wake was observed. The live service deployment/delivery
gap remains; host tests and installed helper do not prove it.

One preliminary launch, job `e2a61b95-2c0c-4554-a155-819cabf5c748`, failed before
qcache started because recursive binary discovery encountered an unrelated
long-path folder. Its empty parent and INSPECTION_REQUIRED record are preserved.
The launch now uses the exact established DiskSpd path/hash; the successful run
has a different immutable parent/ID. No score was repeated or combined from it.

The owned controller waits for process exit and validates completion without
relaunching workloads. The one-phase profile avoids the 81 unrelated checks;
it reduces orchestration work, not benchmark duration. Token savings were not
measured, and fully unattended agent wake is still an explicit qualification gap.

At **03:10:20 UTC**, session cleanup confirms Q: active at its original 2 GiB
Fast/Idle options, dirty/in-flight/errors zero, saved profiles byte-identical,
original managed resource IDs restored, task-attached NTFS lab detached and the
installed tray restored in session 1 (PID 7624). The intended signed 0.4.568.1
diagnostic build remains loaded with counters default-off. PR #9 remains draft
and unmerged. No reliable engineering-time total is available; the measured
smoke, diagnostic, CI and restoration durations above are separate wall times.
