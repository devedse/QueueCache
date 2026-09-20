# QueueCache private alpha product decisions

Agreed with the project owner on 2026-09-20. This records product direction and
accepted risks, not a completed implementation or release certification. The
execution plan is [PRIVATE_ALPHA_IMPLEMENTATION_HANDOVER.md](PRIVATE_ALPHA_IMPLEMENTATION_HANDOVER.md).

## Audience and release meaning

- "Production" in the current discussion means a first private alpha for the
  owner, colleagues and friends to try in recoverable VMs, not general public
  deployment on valuable physical systems.
- Fast mode is the product default. Fast mode on the disk backing C: is a required
  alpha goal, not excluded in favor of a Strict-only release.
- This decision does not mean active C:-disk caching is already qualified. The
  concrete implementation and minimum VM checks belong in the final alpha plan.
- Test signing and an explicitly documented VM setup are acceptable alpha scope;
  broad production distribution/signing requirements are a later milestone.

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
  dirty data for an hour. The exact default interval is still to be selected.
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
- Investigate the long explicit drain using existing evidence before assuming
  data corruption, deadlock, slow hardware, or a need to increase timeouts.
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
- This discussion authorizes recording decisions and preparing a cleanup list,
  not deleting files, changing driver defaults, enabling C: caching, or running
  disruptive VM operations yet.