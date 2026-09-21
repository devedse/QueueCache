# Licensing and provenance

The current QueueCache source tree is MIT-licensed. The A04 cleanup removed the
legacy driver, diagnostic filter and log-reader sources that carried whole-file
Microsoft Limited Public License exceptions. No retained current source file was
identified as containing those compared DiskPerf or CLASSPNP implementations.

The repository history still contains those files. In revisions where they are
present, their restored copyright notices, the historical
`THIRD_PARTY_NOTICES.md` and the bundled `LICENSES/MS-LPL.txt` describe the
applicable MS-LPL terms. The license copy remains in the current tree to support
historical checkouts and source archaeology; it does not change the license of
current MIT files.

The prior comparison covered the old `mainwdm.cpp`/`qcache.h` lifecycle code,
`legacy/scsichk` DiskPerf-derived driver and `legacy/scsilog/debug.cpp`
CLASSPNP-derived diagnostic helpers. Git history is the archive for that removed
material; no replacement copy is kept in the product tree.

Current driver dispatch, cache, policy and verification sources were retained as
the product implementation. Ordinary use of Windows driver APIs and structures is
not treated as incorporation of the removed samples. If future provenance evidence
shows otherwise, update the notices before distribution.

External SDK/WDK headers, Visual Studio components, NuGet packages, .NET runtime,
Avalonia and installer tooling remain separately licensed dependencies. Review
their redistribution terms independently of this source license.
