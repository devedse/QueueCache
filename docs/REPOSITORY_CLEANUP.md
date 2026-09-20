# Current-product repository cleanup inventory

Prepared 2026-09-20 for discussion. This is a cleanup inventory, not the final
alpha release plan. No deletions or runtime changes have been made by this work.
Product decisions: [ALPHA_PRODUCT_DECISIONS.md](ALPHA_PRODUCT_DECISIONS.md).

## Verified build boundary

CI invokes `build/Build.ps1 -LabWriteCache`. That configuration compiles
`driver/qcache/lab.cpp` and `writecache.cpp`, plus common ABI checks/resources.
The legacy branch instead compiles `ioctl.cpp`, `mainwdm.cpp`, `partialirp.cpp`,
`queue.cpp`, `read.cpp`, `thread.cpp` and `write.cpp`. It is still selectable and
is currently the default when no lab build switch is specified.

The current cache engine and driver dispatch are replacements, not those legacy
translation units running under a new label. The product still shares protocol
definitions, repository/build infrastructure and compatibility surfaces. Do not
claim that every line, interface or dependency is entirely new.

## Remove after reference and coverage checks

| Candidate | Evidence / necessary companion work |
|---|---|
| Legacy driver branch and its seven translation units above | Not compiled in the current CI product configuration. Make the current driver the sole/default build, remove obsolete project items and check header/resource dependencies before deletion. |
| `legacy/qcachecmd`, `legacy/scsichk`, `legacy/scsilog` | Historical utilities still referenced by `QueueCache.sln`, not the current managed product solution. Remove those solution entries and remaining references with the source. |
| Legacy-only headers and project metadata | Determine actual includes first; `qcstats.h` is actively shared and must not be deleted. Remove obsolete source-control bindings such as `QueueCache.vssscc` together with their solution/project references. |
| Standalone pass-through and serialized-only build variants | Current product uses the combined cache variant. Remove redundant branches only after preserving inactive/pass-through behavior within the current driver and required diagnostics. |
| `developer/scripts/Manage-Lab.ps1` | Historical per-device installation/recovery, not the current class-filter installer. Retire after confirming current offline recovery instructions cover the needed recovery actions. |
| `developer/scripts/Test-LabReads.ps1` | Historical compatibility wrapper. Retain its useful read-only checks through the maintained CLI, then remove the wrapper and stale references. |
| `developer/scripts/Test-CacheFaults.ps1` | Historical fault orchestration. Compare its scenarios with maintained developer commands before removal; migrate any unique useful coverage, not another standalone runner. |
| `developer/scripts/Measure-Performance.ps1` | Older orchestration overlapping maintained verification. Map experiments to current suites; preserve unique needed experiments before deleting the duplicate orchestration. |
| `tests/QueueCache.FileTests`, `tests/QueueCache.LabTests`, `tests/QueueCache.WriteTests` | These directories currently contain only `bin`/`obj`, no project files. Local generated-output cleanup, not removal of the active developer-library scenarios. |
| Empty root `lab` directory | Empty at inspection. No active source to preserve there. |
| Obsolete generated packages/build outputs | Local housekeeping only. Preserve exact binaries, symbols and raw evidence needed for ongoing comparisons/recovery; do not bulk-delete `.lab` or run evidence. |

Git history is sufficient for obsolete source; do not create another in-tree
legacy archive just to preserve the development journey. Removing a name from
the current product does not justify removing an upgrade cleanup rule that still
needs to handle installations containing that old name.

## Rewrite or consolidate, not blindly delete

| Active surface | Desired result |
|---|---|
| `driver/qcache/lab.cpp`, `writecache.cpp` and active headers | Keep the implementation. Give active files/flags product-oriented names and remove obsolete build conditionals in a separately validated change. |
| `qcachelab` service/binary identity and package `labWriteCache` metadata | Installer, signing and package validation depend on these identities. Decouple internal cleanup from identity migration; preserve upgrade/uninstall compatibility. |
| `README.md` | Replace historical release walkthroughs and contradictory obsolete statements with current product behavior, private-alpha scope, setup, Fast risks and recovery. |
| `docs/KNOWN_ISSUES.md` | Replace historical-engine findings with current confirmed issues and clearly separated limitations/verification gaps. Do not list legacy corruption defects as current defects. Old text can remain in Git history. |
| `docs/CACHE_POLICIES.md` | Reconcile with current code and authoritative tracker; document Fast default, short configurable draining, RAM-first priorities and explicit persistence boundaries. Remove stale claims such as fixes still awaiting verification where newer evidence exists. |
| `developer/README.md`, `docs/DEVELOPER_VERIFICATION.md` | Keep current supported CLI workflows and safety checks; remove retired wrappers and outdated installation paths. |
| `build/Build.ps1`, driver project, solution, CI, signing and packaging | One current product path. Fix the surprising no-switch legacy default. Update package docs, schema checks and tests together; `KNOWN_ISSUES.md` is currently explicitly packaged. |
| `packaging/QueueCache.iss` old-file removal entries | Keep entries still needed to clean older installations even after their source files are removed. Historical filenames here are functional compatibility, not documentation clutter. |
| `LICENSE`, `LICENSES`, `THIRD_PARTY_NOTICES.md`, `docs/LICENSING_REVIEW.md` | Keep applicable legal terms. After source removal, review exact retained-code provenance and update notices accurately; do not erase attribution by assumption. |

Active `src/QueueCache.Developer` verification, file/raw workloads and fault/delay
diagnostics remain useful engineering tools. "Lab" naming alone is not evidence
that functionality is unused. Keep host-safe management/desktop tests, native
policy/ABI checks, packaging tests and recovery tooling.

## Keep for now: implementation and evidence

- `docs/RAM_FIRST_IMPLEMENTATION_TRACKER.md`: authoritative implementation and
  verification status; retain stable 15-step IDs.
- `docs/RAM_FIRST_PERFORMANCE_PLAN.md`: detailed design/rationale and open work.
- `docs/WRITE_PERFORMANCE_TRAJECTORY.md`: comparison provenance and interpretation.
- `docs/CONCURRENCY_VERIFICATION.md`, `docs/FAST_FLUSH_READ_VERIFICATION.md`,
  `docs/OBSERVER_FIX_VERIFICATION.md`, `docs/PROGRESSIVE_SELECTION_VERIFICATION.md`:
  retain until outstanding ordering/scheduler/observer checks and useful reasoning
  are transferred to maintained tests and the tracker, then consolidate/remove.
- `docs/PERFORMANCE.md`, `docs/PERFORMANCE_PLAN.md`: tracked versions remain;
  modified research copies are preserved under ignored
  `.lab/private-handover-20260920/docs/`. Reconcile before deleting or rewriting.
- The former `docs/FOCUSED_ATTRIBUTION_HANDOFF.md`,
  `docs/NEXT_DIAGNOSTIC_HANDOFF.md` and root `codex-session-*.md` files are preserved
  under ignored `.lab/private-handover-20260920/`, retaining relative paths.
  They are private working material, not release docs; do not stage them.
- Exact `.lab` run evidence and frozen binaries: preserve privately while they
  support ongoing investigations; do not package or commit them.
- `docs/ALPHA_PRODUCT_DECISIONS.md` and this inventory: current decision records,
  not obsolete engine history. The execution plan is
  [PRIVATE_ALPHA_IMPLEMENTATION_HANDOVER.md](PRIVATE_ALPHA_IMPLEMENTATION_HANDOVER.md).

## Cleanup validation

Perform cleanup in bounded changes: build/legacy source, developer-tool overlap,
then documentation consolidation. Check references before deletion; run native
and managed builds and relevant host-safe/packaging tests afterward. Renaming
service identities, changing active preprocessor branches or removing recovery
behavior needs more than a documentation/link check. Preserve unrelated changes,
raw failed verdicts, and current VM configuration throughout.