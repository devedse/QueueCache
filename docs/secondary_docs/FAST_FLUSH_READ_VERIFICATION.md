# Fast-flush cached-read verification

## Change and ordering argument

The progressive selector used to stop at every queued application flush. Even
Fast mode deferred the flush only when the sole worker eventually reached it.
Unrelated RAM hits therefore waited behind capacity-blocked older writes.

The selector may now cross IRP_MJ_FLUSH_BUFFERS only for an enabled, healthy Fast
cache. The flush stays in its original queue position and is not acknowledged by
the read lane. No writes bypass it. Every older queued write (before AND after
the flush), plus the active write, is checked for overlap before selecting a read.
Application flushes do not change cached bytes. The read can therefore observe
the same coherent bytes before the deferred flush executes, without changing the
flush's completion or durability contract. Fast mode already does not promise
durability of acknowledged application flushes.

Policy changes, explicit qcache flush/drain controls, TRIM, shutdown/power and
unknown requests remain barriers. Strict application flushes remain barriers.
The same worker owns policy mutation and read service, so policy cannot change
during the callback. SnapshotLock is released before QueueLock; no cache mutex
is taken inside CSQ callbacks. A change in eligibility resets progressive cursors
so partially completed dependency validation cannot carry across the change.

Only full hits are completed by the read lane. A miss follows the existing
cancel-safe head-reinsertion rule, crossing only previously validated independent
operations (now also eligible Fast flushes). It is stamped to avoid repeat attempts
in the same owner epoch. It performs no lower I/O in the callback; the normal worker
may later execute that independent read before the still-queued Fast flush. This
does not change bytes or acknowledge the flush. Strict and mutation barriers were
not crossed, so reinsertion cannot move the read across those barriers.

## Handoff: verify, do not redesign

1. Install the successful CI artifact containing this change and reboot. Match the
   loaded driver hash with the release artifact and record commit, CLI identity,
   Q: disk identity, runtime settings and saved profiles. Local build version strings
   may be reused; a version string alone is insufficient. Do not touch C:.
2. Run the corrected seeded correctness suite first. Add/check these explicit cases:

| Scenario | Required result |
|---|---|
| Fast: unrelated hot read behind queued application flush and blocked writer | Exact bytes; read makes progress while flush remains queued |
| Older overlapping write before flush | Read must not return pre-write bytes after that write completes |
| Overlapping write between flush and read | Same ordering guarantee; checking only writes before flush is insufficient |
| Multiple flushes, queued read beyond position 64 | Progressive validation still reaches eligible hit |
| Strict mode | Flush remains a selector fence; verify post-flush disk bytes |
| Fast with intervening policy change, explicit management flush, TRIM or unknown control | That operation remains a fence |
| Cache miss beyond Fast flush; retention disabled; cancellation during search/reinsertion | No inline lower read, stale data, double completion or hang |
| Switch Fast/Strict between workloads; injected I/O error/removal in existing supported tests | No stale selector permission, hidden error or invalid ownership |

Exact IRP positions cannot be inferred from process launch. If available evidence
cannot establish a scenario, report NOT EXERCISED, not PASS. Do not add production
hooks just to fill a coverage table. Large concurrent writes are not assumed atomic.

3. Use the source-controlled C# runner, not another copy of the private PowerShell
   harness: `qcache developer verify Q: --suite flush-interference --repeats 2
   --diskspd C:\Tools\DiskSpd\diskspd.exe --output C:\QueueCache-Results`.
   See `../DEVELOPER_VERIFICATION.md` for the output contract and recovery commands.
4. This selects eight cases: Automatic/Fixed50, with/without requested application
   flush, two repetitions, writer QD128, 25 ms lower-write delay. Defaults retain
   the 1 GiB cache, 256 MiB hot reader, separate 2 GiB writer, 256 KiB batch and one
   drainer. The XML runner supports Microsoft and CDM executables, but its workload
   contract differs from the old scripts: establish matching-engine/version and
   workload baselines before claiming speedup ratios.
   A control may still contain OS flushes; classify by observed counters, not only
   requested configuration. The existing explicit byte-order tests above remain
   required; the runner does not claim to establish exact queue positions.
5. Report each repetition's reader IOPS, actual completions, p99.9/max (N/A for zero
   completions), read misses, clean occupancy, bypass completions, fence rejections,
   scan yields, drain progress, application/deferred flush deltas and injected-flush
   START/END timestamps. Keep whole counter-window duration separate from DiskSpd's
   measured interval. Last-selection fields are samples, not a complete event trace.
6. Compare with prior evidence: Fixed50 without a fence completed 134,111 bypass
   reads; its explicitly flushed counterpart completed 16 with 615 fence rejects.
   Do not use the previous Fixed50 median as proof every repetition succeeded.

## Previous runner timeout: keep collection failure separate

The prior run collected six of eight cases, then `lab-delay Q: 0` timed out after
90 seconds. Restoration subsequently succeeded. Do not merely increase this limit
and call it fixed. Before/while each post-workload reset command, collect native
telemetry on a bounded observation loop: active major/phase/age, queue depth/head
age, dirty/in-flight bytes, barriers and drain progress. This will distinguish a
queued management command from a stalled CLI or lower I/O. Preserve its PID, exact
arguments, timestamps and stdout/stderr. Do not repeatedly submit reset commands.

Use unique output directories/case IDs, explicit expected-row checks, bounded
process waits and a measurement deadline that does not prevent cleanup. Stop on
identity mismatch, byte mismatch, driver error, hang or unexpected output. Stop
owned helpers only; clear delay, drain and restore captured runtime options/timing.
Compare saved profiles unchanged. Report restoration failure explicitly. Never
format or reboot as automatic error recovery. Write FINISHED.txt/status.json only
with an honest COMPLETED/FAILED result; six cases is not a completed eight-case run.

## Acceptance and next decisions

- Correctness first; existing local compile-time tests are not live WDM validation.
- In Fast mode the injected application flush must no longer by itself prevent
  independent resident reads from progressing. The flush itself may still be slow.
- Strict/control/TRIM fences and overlap protection must remain intact.
- Do not expect this to fix small-write throughput, cold reads, drain throughput or
  the independent management timeout. Do not change allocation/defaults or launch
  a broad matrix. Return the focused report for review.
