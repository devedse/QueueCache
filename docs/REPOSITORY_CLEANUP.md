# Current-product repository cleanup

Completed in private-alpha milestone A04. Git history is the archive for removed
material; no duplicate legacy source archive is kept in the current tree.

## Current build boundary

`driver/qcache/QueueCache.Driver.vcxproj` has one x64 driver path. It compiles the
current dispatch, bounded cache and native ABI/policy checks. A build with no extra
switches is the product build. Debug and Release use the same source boundary.

`build/Build.ps1` builds that driver unless `-ManagedOnly` is selected. The removed
`-LabPassThrough`, `-LabSerialized` and `-LabWriteCache` variants no longer select
alternative implementations. GitHub Actions uses the no-switch current build.

The generated binary and installed service remain `qcachelab` for upgrade
compatibility. The `labWriteCache`, `labSerialized` and `labPassThrough` manifest
fields are temporarily emitted as fixed compatibility values for existing signing
and installer validation. `LabAllowedDriverKey` is likewise an internal installed
registry compatibility key. These names do not describe separate product editions.

## Removed material

- Legacy engine sources: `ioctl.cpp`, `mainwdm.cpp`, `partialirp.cpp`, `queue.cpp`,
  `read.cpp`, `thread.cpp`, `write.cpp`, and their legacy-only `qcache.h`/`wkmem.hpp`.
- Obsolete native project/source-control metadata and Team Foundation bindings.
- `legacy/qcachecmd`, `legacy/scsichk` and `legacy/scsilog`.
- Standalone pass-through and serialized-only compile variants.

The current `qcstats.h`, protocol ABI, pass-through-while-inactive behavior,
serialized transition ordering, diagnostics and installer upgrade rules are
retained. Packaging removal rules for older installed filenames are functional
compatibility behavior and remain in place.

## Licensing result

All source remaining in the current tree is MIT-licensed. Removed revisions
contained Microsoft sample-derived files distributed under MS-LPL. The bundled
MS-LPL text remains so historical checkouts have an adjacent copy of those terms;
the current [third-party notices](../THIRD_PARTY_NOTICES.md) and
[licensing review](LICENSING_REVIEW.md) record that boundary.

## Still separate work

Installer/service identity migration is intentionally deferred to coordinated A10
upgrade testing. Raw evidence, `.lab` files and generated packages remain private
and untracked.

Validation for this cleanup requires native Debug/Release builds, the current
native solution, managed host/UI contracts, CLI/package checks and a clean VM
policy regression after installing the resulting signed build.
