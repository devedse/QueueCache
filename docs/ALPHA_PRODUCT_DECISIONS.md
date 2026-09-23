# QueueCache private alpha product decisions

Initial decisions: 2026-09-20. Scope clarified in the owner's production-readiness
planning review on 2026-09-22. This records product direction and accepted risks,
not a completed implementation or release certification. The
execution plan is [PRIVATE_ALPHA_IMPLEMENTATION_HANDOVER.md](PRIVATE_ALPHA_IMPLEMENTATION_HANDOVER.md).

## Audience and release meaning

- The end goal is production readiness. The first milestone remains a private
  alpha for the owner, colleagues and friends in recoverable VMs (A01-A12).
  Production support, security, servicing, endurance and release qualification
  follow in A13-A16. Private-alpha completion alone does not authorize general
  production deployment or establish its reliability.
- Fast mode is the product default. Fast mode on the disk backing C: is a required
  alpha goal, not excluded in favor of a Strict-only release.
- C: is a normal product target. The released UI and CLI must not hide it, label it
  unsupported, require a developer-only activation action, or present a generic
  "this might not work on C:" warning. Fast still requires the same clear volatile-
  data acknowledgement on every disk; Strict remains the durability choice.
- Current C:-disk restrictions are temporary implementation gates, not product
  policy. Remove them after the already-packaged guarded path passes its first
  active VM run, then prove the same path through normal Apply, saved-profile
  startup and restart. Correctness checks belong in the backend and verification,
  not in warnings that shift responsibility to the user.
- Test signing and an explicitly documented VM setup are acceptable alpha scope;
  broad production distribution/signing requirements are a later milestone.

## C: activation decision (2026-09-23)

The intended end state is one activation path for data and system disks. The
remaining work is implementation and evidence, not a permanent C: exclusion:

1. COMPLETE: exact 0.4.82.1 guarded active-image validated paging admission,
   nonpageable progress, byte correctness and restoration on the snapshot-backed VM.
2. IMPLEMENTED in plan 27: normal kernel Enable uses the paging-capable policy;
   action 12 remains only as an identical compatibility alias.
3. IMPLEMENTED in plan 27: remove boot/system/paging rejection from public Apply. Keep identity, memory,
   configuration and driver-state validation because those apply to every disk.
4. IMPLEMENTED in plan 27: treat new system-path registrations without disabling an otherwise healthy active
   cache. Implement ordered hibernation, Fast Startup and crash-dump behavior; do
   not turn those Windows features into a permanent activation ban.
5. IMPLEMENTED in plan 27: desktop, CLI and saved-profile restore use the same normal path. A C: card
   may state factual disk roles, but must not carry an unsupported-feature warning.
6. Prove normal UI/CLI activation, Fast and Strict behavior, saved-profile reboot,
   shutdown/restart, paging pressure, hibernation/Fast Startup and configured dump
   handling before declaring the corresponding alpha gates complete.

The developer system suites retain exact identity, separate-output-disk, exclusive-
lease, recovery and byte-oracle checks. Those protect the experiment from targeting
the wrong disk; they are not product activation restrictions.

## Accepted volatility and correctness requirements

- The owner accepts Fast mode's volatile acknowledgements, including loss of
  acknowledged writes and possible filesystem corruption after power loss or
  another abrupt failure that destroys pending RAM data. Fast must not be
  described as durable storage. Explain this clearly to alpha participants.
- Accepted volatility does not excuse known silent corruption during normal
  operation, stale reads, incorrect write ordering, freeing unpersisted dirty
  data after an I/O error, or falsely reporting successful explicit persistence.
- Strict remains a distinct supported choice; it is not the required default.
- Report known unfixed defects separately from untested risks. No reproduced
  unfixed corruption defect in the replacement engine has been identified in the
  reviewed evidence; this is not proof that none exists or a completed audit.
- Exhaustive certification is not a prerequisite for this private VM alpha.
  Use existing test evidence plus focused checks for changed and C:-specific
  paths. Do not bypass known failures or label missing coverage as passed.

## RAM-first scheduling

- Normal default operation must start background persistence after a short,
  configurable interval or appropriate policy trigger, not deliberately retain
  dirty data for an hour. A03 implemented Idle with 5,000 ms first-dirty age,
  250 ms write-idle, 40/80 watermarks, 256 KiB batches and parallelism 1.
  Saved explicit settings are preserved. The 0.4.57.1 foreground/background check
  passed; precise trigger/capacity qualification remains separately tracked.
- Foreground supported writes that fit available admission RAM and cached reads
  have priority over background persistence. Reads requiring disk access are not
  promised RAM-only latency.
- Foreground activity and draining should progress in parallel with minimal
  interference. Reduce background aggressiveness when needed; do not make fitting
  Fast writes wait for lower I/O merely because a background drain is running.
- Preserve dirty-capacity backpressure, explicit flush/lifecycle/error semantics
  and bounded drain progress. Priority is not permission to discard data or let
  the cache silently exceed its memory budget. Already-issued disk I/O is not
  assumed cancellable or instantaneous.
- Trigger age means when draining starts, not a guarantee that all data has
  reached disk by that age. A one-hour Deferred setting is not the alpha default;
  retaining that optional mode is a separate scope decision.

## Current priorities and deferred work

- Defer TRIM investigation and range-aware TRIM optimization for now. Do not
  remove conservative correctness handling or imply that TRIM was validated.
- A02 fixed runner restoration ordering and located most observed drain time in
  lower-I/O waits. This is not proof that hardware is the unavoidable limit.
  A06a/T050 compares request shape and existing concurrency before selecting a
  focused fix or documenting an acceptable operational limit. A11 performs final
  acceptance after such fixes; no automatic timeout relaxation.
- Scoped A06 completion does not qualify every pressure, lifetime or ordering
  case. A06a adds the required capacity/ordering/memory-progress checks and an
  independent recovery rehearsal before active C: validation. A07/A08 local work
  can proceed while these gates are prepared.
- The runner's "restoration" means returning test-modified cache settings to
  their saved values and checking pending bytes/errors afterward. It does not
  mean restoring files, a VM snapshot, or recovering known corrupted data.
- Keep the full 15-step overview in completion reports whenever a step is
  completed: implementation TRUE/FALSE, partial work, expected gains, measured
  gains and verification gaps. Do not combine speculative percentages.

## Repository direction

- Present one current product, not a history of successive engines and lab
  experiments. Remove obsolete implementation/build variants and rewrite stale
  product documentation after confirming references and replacement coverage.
- Active code is not disposable merely because its name contains "lab".
  Preserve current driver, verification, fault/delay tooling, installation and
  recovery capabilities until deliberate replacements are verified.
- Keep the implementation trackers and evidence needed by current work for now.
  Preserve private raw evidence and unrelated local research files; do not
  publish credentials, generated artifacts or VM-specific material.
- Keep applicable licenses and attribution for reused code. Removing obsolete
  code does not by itself remove obligations attached to retained code.
- The original 2026-09-20 decision-recording scope was followed by owner-authorized
  implementation/cleanup and scoped VM verification. Consult current session
  authorization and the handover gates before new actions. This planning revision
  itself neither activates C: caching nor authorizes destructive VM recovery or
  production distribution.
