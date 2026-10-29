using System.ComponentModel;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

/// <summary>Deterministic page-file ordering checks on owned NTFS extents, using the explicit lab classifier.</summary>
[SupportedOSPlatform("windows")]
public static class PagingOrderScenario
{
    private const int Block = 4096, Offset = 4 << 20;
    public const int CheckCount = 8;
    private static CachePagingOrder Order(CacheDiagnostics d) => d.PagingOrder ?? throw new InvalidDataException("Missing V28 paging-order evidence.");
    public static void ValidateBypass(CacheDiagnostics before, CacheDiagnostics after)
    {
        var a = Order(before); var b = Order(after);
        var g = after.LabGate ?? throw new InvalidDataException("Missing range-gate sequence evidence.");
        if (b.Bypasses != a.Bypasses + 1 || b.BypassBytes != a.BypassBytes + Block ||
            b.LearnBarriers != a.LearnBarriers || b.UnidentifiedDirectBarriers != a.UnidentifiedDirectBarriers ||
            g.State != 2 || g.OldLowerDoneSeq == 0 || g.OldRetireSeq != 0 ||
            g.DirectSubmitSeq <= g.OldLowerDoneSeq || g.DirectDoneSeq <= g.DirectSubmitSeq || b.OverlapFound != 0)
            throw new InvalidDataException("Safe paging write did not complete below the held earlier group without a barrier.");
    }
    public static void ValidateLearn(CacheDiagnostics before, CacheDiagnostics after, bool held)
    {
        var a = Order(before); var b = Order(after);
        if (b.LearnBarriers != a.LearnBarriers + 1 || b.Bypasses != a.Bypasses || b.SafeRanges == 0 || b.OverlapFound != 0)
            throw new InvalidDataException("New paging range did not take exactly one learning barrier.");
        if (held && (after.LabGate is not { OldRetireSeq: > 0 } gate || gate.DirectSubmitSeq <= gate.OldRetireSeq))
            throw new InvalidDataException("The new range reached disk before the earlier held write retired.");
    }
    public static void ValidateClear(CacheDiagnostics before, CacheDiagnostics after, ulong reason)
    {
        var a = Order(before); var b = Order(after);
        if (a.SafeRanges == 0 || b.SafeRanges != 0 || b.Clears != a.Clears + 1 || b.LastClearReason != reason)
            throw new InvalidDataException($"Overlapping effect did not invalidate the entire safe set with reason {reason}. Before: {a}; after: {b}.");
    }
    private static void Reset(CacheDevice d)
    {
        d.Control(WriteCacheAction.LabGate, value: 0);
        d.SetSpecialRanges([], SpecialRangeKind.ForceDirect);
        d.Control(WriteCacheAction.Disable); d.Control(WriteCacheAction.Release);
    }
    private static void Apply(DiskTarget target, CacheDevice d, DrainAlgorithm drain)
    {
        Reset(d);
        ConfigurationManager.Apply(target, new CacheConfiguration(64, CachePreset.Fast)
        {
            WriteOrdering = true,
            Options = new(Drain: drain, MaxDirtyAgeMs: 3600000, BatchKiB: 4, Parallelism: 2, RetainWrites: false)
        }, true);
    }
    private static void Seed(CacheDevice d, DiskRange range) =>
        d.SetSpecialRanges([range], SpecialRangeKind.SafePaging, Order(d.GetDiagnostics()).SeedSequence);
    private static void MapWrite(string path, long offset, byte[] pattern)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        using var map = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
        using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
        view.WriteArray(offset, pattern, 0, pattern.Length); view.Flush();
    }
    private static byte[] Pattern(int n) => ConcurrentCacheOracle.Pattern(132, n, Block);
    private static void Bytes(AlignedFile file, long offset, byte[] expected)
    {
        var actual = new byte[Block]; file.Read(offset, actual);
        if (!actual.AsSpan().SequenceEqual(expected)) throw new IOException("Paging-order byte oracle failed.");
    }
    private static async Task Held(CacheDevice d)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < 8)
        {
            if (d.GetDiagnostics().LabGate is { State: 2, OldLowerDoneSeq: > 0 }) return;
            await Task.Delay(10);
        }
        throw new IOException("Earlier owned write did not reach its lower-completion hold.");
    }
    public static async Task<IReadOnlyList<CheckResult>> RunAsync(DiskTarget target, CacheDevice d, string work, string evidence)
    {
        if (d.GetDiagnostics().PagingOrder is null) throw new NotSupportedException("Paging ordering requires V28.");
        var checks = new List<CheckResult>();
        Reset(d);
        var path = OrderingFaultScenarios.CreateOwned(work, "paging-order.bin");
        var b = new DiskRange(PagingCoherenceScenarios.DiskOffsetOf(path, Offset, target.Root), Block);
        var a = new DiskRange(PagingCoherenceScenarios.DiskOffsetOf(path, Offset + Block, target.Root), Block);
        var c = new DiskRange(PagingCoherenceScenarios.DiskOffsetOf(path, Offset + 2 * Block, target.Root), Block);
        var low = Math.Min(a.Start, Math.Min(b.Start, c.Start));
        var high = Math.Max(a.Start, Math.Max(b.Start, c.Start)) + Block;
        if (low % Block != 0 || high - low > uint.MaxValue) throw new IOException("Owned gate range cannot be represented.");
        void Gate() => d.Control(WriteCacheAction.LabGate, (ulong)low, (5000UL << 32) | (ulong)(high - low));
        try
        {
            using var file = new AlignedFile(path, Block, create: false, shareAll: true);
            Apply(target, d, DrainAlgorithm.Deferred);
            file.Write(Offset + Block, Pattern(1)); file.Flush();
            var before = d.GetDiagnostics(); Seed(d, b); var seeded = d.GetDiagnostics();
            RunStorage.AtomicJson(evidence + ".paging-seed.json", new { Before = before, After = seeded });
            if (seeded.Attribution!.LowerWriteAttempts <= before.Attribution!.LowerWriteAttempts ||
                seeded.Attribution.LowerFlushAttempts <= before.Attribution.LowerFlushAttempts || Order(seeded).SafeRanges == 0)
                throw new IOException("Seeding did not write out the earlier sealed data and flush the disk.");
            checks.Add(new("flush-order/paging-seed-boundary", "PASS", "Seeding wrote the older group and a lower flush before installing extents."));
            var stale = Order(seeded).SeedSequence;
            file.Write(Offset + Block, Pattern(2));
            var rejected = false;
            try { d.SetSpecialRanges([b], SpecialRangeKind.SafePaging, stale); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1237) { rejected = true; }
            if (!rejected || d.GetDiagnostics().Attribution!.LowerFlushAttempts != seeded.Attribution.LowerFlushAttempts)
                throw new IOException("Stale seed token was accepted or forced a flush.");
            checks.Add(new("flush-order/paging-stale-seed-refused", "PASS", "An intervening write invalidated the layout token; refusal did not flush the cache."));

            Apply(target, d, DrainAlgorithm.Eager); Seed(d, b);
            d.SetSpecialRanges([b], SpecialRangeKind.LabPaging); Gate();
            file.Write(Offset + Block, Pattern(3)); file.Flush(); await Held(d);
            before = d.GetDiagnostics(); MapWrite(path, Offset, Pattern(4)); var bypassed = d.GetDiagnostics();
            RunStorage.AtomicJson(evidence + ".paging-safe-held.json", new { Before = before, After = bypassed });
            ValidateBypass(before, bypassed);
            d.Control(WriteCacheAction.LabGate, value: 0); d.Control(WriteCacheAction.Flush);
            Bytes(file, Offset, Pattern(4)); Bytes(file, Offset + Block, Pattern(3));
            checks.Add(new("flush-order/paging-safe-held-bypass", "PASS", "Direct submit/completion preceded older retirement; both blocks matched after release."));

            d.SetSpecialRanges([c], SpecialRangeKind.LabPaging); Gate();
            file.Write(Offset + Block, Pattern(5)); file.Flush(); await Held(d);
            before = d.GetDiagnostics(); MapWrite(path, Offset + 2 * Block, Pattern(6)); var learned = d.GetDiagnostics();
            RunStorage.AtomicJson(evidence + ".paging-learn.json", new { Before = before, After = learned });
            ValidateLearn(before, learned, held: true);
            d.Control(WriteCacheAction.LabGate, value: 0);
            before = d.GetDiagnostics(); MapWrite(path, Offset + 2 * Block, Pattern(7)); var second = d.GetDiagnostics();
            if (Order(second).Bypasses != Order(before).Bypasses + 1 || Order(second).LearnBarriers != Order(before).LearnBarriers)
                throw new IOException("Learned range did not bypass on its next write.");
            Bytes(file, Offset + 2 * Block, Pattern(7));
            checks.Add(new("flush-order/paging-new-range-learns", "PASS", "New range submitted after older retirement; its next write bypassed and bytes matched."));

            d.SetSpecialRanges([], SpecialRangeKind.ForceDirect);
            before = d.GetDiagnostics(); file.Write(Offset + 2 * Block, Pattern(8)); var cleared = d.GetDiagnostics();
            RunStorage.AtomicJson(evidence + ".paging-cached-clear.json", new { Before = before, After = cleared });
            ValidateClear(before, cleared, 7);
            d.SetSpecialRanges([c], SpecialRangeKind.LabPaging);
            before = d.GetDiagnostics(); MapWrite(path, Offset + 2 * Block, Pattern(9)); learned = d.GetDiagnostics();
            ValidateLearn(before, learned, held: false); Bytes(file, Offset + 2 * Block, Pattern(9));
            RunStorage.AtomicJson(evidence + ".paging-cached-invalidation.json", new { Cleared = cleared, Learned = learned });
            checks.Add(new("flush-order/paging-cached-overlap-clears", "PASS", "Cached overlap cleared all extents; the next direct write learned behind a barrier."));

            before = d.GetDiagnostics(); file.Trim(Offset + 2 * Block, Block); cleared = d.GetDiagnostics();
            RunStorage.AtomicJson(evidence + ".paging-trim-clear.json", new { Before = before, After = cleared });
            ValidateClear(before, cleared, 9);
            before = d.GetDiagnostics(); MapWrite(path, Offset + 2 * Block, Pattern(10)); learned = d.GetDiagnostics();
            ValidateLearn(before, learned, held: false); Bytes(file, Offset + 2 * Block, Pattern(10));
            RunStorage.AtomicJson(evidence + ".paging-trim-invalidation.json", new { Cleared = cleared, Learned = learned });
            checks.Add(new("flush-order/paging-trim-overlap-clears", "PASS", "TRIM cleared all extents; a later paging write took the learning barrier and matched."));
            // The explicit lab override keeps worker routing active while
            // disabled, so both learning and the other direct write are real
            // worker operations rather than inactive dispatch pass-through.
            d.Control(WriteCacheAction.Disable); d.Control(WriteCacheAction.Release);
            MapWrite(path, Offset + 2 * Block, Pattern(11));
            before = d.GetDiagnostics(); file.Write(Offset + 2 * Block, Pattern(12)); cleared = d.GetDiagnostics();
            RunStorage.AtomicJson(evidence + ".paging-disabled-invalidation.json", new { Before = before, After = cleared });
            ValidateClear(before, cleared, 8); Bytes(file, Offset + 2 * Block, Pattern(12));
            checks.Add(new("flush-order/paging-disabled-overlap-clears", "PASS", "Other direct writes invalidate learned ranges while the cache is disabled."));
            var final = d.GetDiagnostics();
            RunStorage.AtomicJson(evidence + ".paging-final.json", final);
            if (Order(final).OverlapFound != 0 || Order(final).TableFull != 0)
                throw new IOException("Safe-range overlap or unexpected capacity exhaustion was recorded.");
            checks.Add(new("flush-order/paging-no-safe-overlap", "PASS", "No safe-range pending overlap was observed; hooks are cleared on exit."));
        }
        finally { Reset(d); }
        return checks;
    }
}
