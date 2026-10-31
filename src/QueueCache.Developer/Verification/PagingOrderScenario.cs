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
    public const int CheckCount = 9;
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
    private static CachePageFileGrowth Growth(CacheDiagnostics d) =>
        d.PageFileGrowth ?? throw new InvalidDataException("Missing V31 page-file growth evidence.");
    /// <summary>Plan 219: the first write into a new page-file range waited for its earlier held group in the offload table,
    /// not on the request worker; a later write and read of another block completed meanwhile (before plan 219 they queued
    /// behind its barrier, and after page-file growth on C: that was every request for minutes).</summary>
    public static void ValidateLearnOffloaded(CacheDiagnostics before, CacheDiagnostics pending, CacheDiagnostics moving,
        double seconds, bool learnWaiting)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > 1 || !learnWaiting ||
            moving.LabGate is not { State: 2, OldLowerDoneSeq: > 0, OldRetireSeq: 0, DirectSubmitSeq: 0 })
            throw new InvalidDataException("A later write and read did not complete within a second while the new page-file range waited for a held group.");
        var a = Growth(before); var b = Growth(pending); var m = Growth(moving);
        var o = Order(before); var p = Order(pending);
        if (b.LearnOffloaded != a.LearnOffloaded + 1 || b.LearnFallbacks != a.LearnFallbacks || b.LearnPending != 1 ||
            m.LearnOffloaded != b.LearnOffloaded || m.LearnFallbacks != b.LearnFallbacks || m.LearnPending != 1 ||
            p.LearnBarriers != o.LearnBarriers + 1 || p.Bypasses != o.Bypasses ||
            pending.FlushOrder is not { Mode: 1 } order || order.DrainingEpoch >= order.CurrentEpoch)
            throw new InvalidDataException("The new page-file range did not wait off the request worker for its earlier group.");
    }
    /// <summary>The offloaded learner wrote only after the held group retired, flushed, and none is left waiting.</summary>
    public static void ValidateLearnFinished(CacheDiagnostics pending, CacheDiagnostics after)
    {
        var b = Growth(pending); var c = Growth(after);
        var p = Order(pending); var q = Order(after);
        if (c.LearnPending != 0 || c.LearnOffloaded != b.LearnOffloaded || c.LearnFallbacks != b.LearnFallbacks ||
            q.LearnBarriers != p.LearnBarriers || q.SafeRanges == 0 || q.OverlapFound != 0 ||
            after.LabGate is not { OldRetireSeq: > 0 } gate || gate.DirectSubmitSeq <= gate.OldRetireSeq ||
            gate.DirectDoneSeq <= gate.DirectSubmitSeq ||
            after.FlushOrder is not { } done || pending.FlushOrder is not { } held || done.Flushes <= held.Flushes)
            throw new InvalidDataException("The offloaded page-file write did not follow the held group's retirement and flush.");
    }
    /// <summary>Plan 220: an overlapping effect invalidates only the overlapping part of the safe set (counted as one clear);
    /// the scenario proves separately that the written range learns again and an untouched range still bypasses.</summary>
    public static void ValidateClear(CacheDiagnostics before, CacheDiagnostics after, ulong reason)
    {
        var a = Order(before); var b = Order(after);
        if (a.SafeRanges == 0 || b.Clears != a.Clears + 1 || b.LastClearReason != reason || b.SafeRanges > a.SafeRanges + 1)
            throw new InvalidDataException($"Overlapping effect did not invalidate its part of the safe set with reason {reason}. Before: {a}; after: {b}.");
    }
    // Plan 220: a write to the seeded range b, which no overlap touched, must still skip the queue.
    private static void UntouchedStaysSafe(CacheDevice d, string path, AlignedFile file, int pattern)
    {
        var before = d.GetDiagnostics(); MapWrite(path, Offset, Pattern(pattern)); var after = d.GetDiagnostics();
        if (Order(after).Bypasses != Order(before).Bypasses + 1 || Order(after).LearnBarriers != Order(before).LearnBarriers)
            throw new IOException("An overlap elsewhere removed an untouched safe range: its next write did not bypass.");
        Bytes(file, Offset, Pattern(pattern));
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
        // The later request of the plan-219 learner check uses its own file: NTFS serialises a non-cached write with a
        // pending paging write to the same file (the first VM run waited 3.99 s there, outside the cache), whereas the
        // real page file shares no file with other requests.
        var otherPath = OrderingFaultScenarios.CreateOwned(work, "paging-order-other.bin");
        var b = new DiskRange(PagingCoherenceScenarios.DiskOffsetOf(path, Offset, target.Root), Block);
        var a = new DiskRange(PagingCoherenceScenarios.DiskOffsetOf(path, Offset + Block, target.Root), Block);
        var c = new DiskRange(PagingCoherenceScenarios.DiskOffsetOf(path, Offset + 2 * Block, target.Root), Block);
        var e = new DiskRange(PagingCoherenceScenarios.DiskOffsetOf(path, Offset + 3 * Block, target.Root), Block);
        var low = Math.Min(Math.Min(a.Start, e.Start), Math.Min(b.Start, c.Start));
        var high = Math.Max(Math.Max(a.Start, e.Start), Math.Max(b.Start, c.Start)) + Block;
        if (low % Block != 0 || high - low > uint.MaxValue) throw new IOException("Owned gate range cannot be represented.");
        void Gate(ulong milliseconds = 5000) => d.Control(WriteCacheAction.LabGate, (ulong)low, (milliseconds << 32) | (ulong)(high - low));
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

            // Plan 219 (#32): the first write into a new page-file range waits for the held earlier group off the
            // request worker, so a later write and read in another file complete meanwhile.
            d.SetSpecialRanges([e], SpecialRangeKind.LabPaging); Gate(4000);
            file.Write(Offset + Block, Pattern(13)); file.Flush(); await Held(d);
            before = d.GetDiagnostics();
            var learning = Task.Run(() => MapWrite(path, Offset + 3 * Block, Pattern(14)));
            CacheDiagnostics pending;
            var timer = Stopwatch.StartNew();
            while ((pending = d.GetDiagnostics()).PageFileGrowth is not { LearnPending: 1 })
            {
                if (learning.IsCompleted || timer.Elapsed.TotalSeconds > 1)
                {
                    await learning;
                    throw new IOException("The new page-file range did not wait off the request worker.");
                }
                await Task.Delay(10);
            }
            const long laterOffset = Offset + 6 * Block;
            using var other = new AlignedFile(otherPath, Block, create: false, shareAll: true);
            var clock = Stopwatch.StartNew();
            other.Write(laterOffset, Pattern(15)); Bytes(other, laterOffset, Pattern(15));
            var seconds = clock.Elapsed.TotalSeconds;
            var moving = d.GetDiagnostics(); var learnWaiting = !learning.IsCompleted;
            RunStorage.AtomicJson(evidence + ".paging-learn-moving.json", new { Before = before, Pending = pending, Moving = moving, Seconds = seconds, LearnWaiting = learnWaiting });
            try { ValidateLearnOffloaded(before, pending, moving, seconds, learnWaiting); }
            finally { await learning; } // The gate releases itself; join the owned paging write before anything else.
            var finished = d.GetDiagnostics();
            RunStorage.AtomicJson(evidence + ".paging-learn-finished.json", finished);
            ValidateLearnFinished(pending, finished);
            d.Control(WriteCacheAction.LabGate, value: 0);
            before = d.GetDiagnostics(); MapWrite(path, Offset + 3 * Block, Pattern(16)); second = d.GetDiagnostics();
            if (Order(second).Bypasses != Order(before).Bypasses + 1 || Order(second).LearnBarriers != Order(before).LearnBarriers)
                throw new IOException("The range learned off the request worker did not bypass on its next write.");
            Bytes(file, Offset + 3 * Block, Pattern(16)); Bytes(other, laterOffset, Pattern(15)); Bytes(file, Offset + Block, Pattern(13));
            checks.Add(new("flush-order/paging-learn-leaves-requests-moving", "PASS",
                $"The first write into a new range waited for the held earlier group off the request worker; a later write and read in another file completed in {seconds:F3}s meanwhile. It was submitted after the held write retired, flushed, learned (its next write bypassed), and every block matched."));

            d.SetSpecialRanges([], SpecialRangeKind.ForceDirect);
            before = d.GetDiagnostics(); file.Write(Offset + 2 * Block, Pattern(8)); var cleared = d.GetDiagnostics();
            RunStorage.AtomicJson(evidence + ".paging-cached-clear.json", new { Before = before, After = cleared });
            ValidateClear(before, cleared, 7);
            d.SetSpecialRanges([b, c], SpecialRangeKind.LabPaging);
            before = d.GetDiagnostics(); MapWrite(path, Offset + 2 * Block, Pattern(9)); learned = d.GetDiagnostics();
            ValidateLearn(before, learned, held: false); Bytes(file, Offset + 2 * Block, Pattern(9));
            RunStorage.AtomicJson(evidence + ".paging-cached-invalidation.json", new { Cleared = cleared, Learned = learned });
            UntouchedStaysSafe(d, path, file, 17);
            checks.Add(new("flush-order/paging-cached-overlap-removes-range", "PASS", "A cached write over one learned range removed only that range: its next direct write learned again, and an untouched safe range still bypassed."));

            before = d.GetDiagnostics(); file.Trim(Offset + 2 * Block, Block); cleared = d.GetDiagnostics();
            RunStorage.AtomicJson(evidence + ".paging-trim-clear.json", new { Before = before, After = cleared });
            ValidateClear(before, cleared, 9);
            before = d.GetDiagnostics(); MapWrite(path, Offset + 2 * Block, Pattern(10)); learned = d.GetDiagnostics();
            ValidateLearn(before, learned, held: false); Bytes(file, Offset + 2 * Block, Pattern(10));
            RunStorage.AtomicJson(evidence + ".paging-trim-invalidation.json", new { Cleared = cleared, Learned = learned });
            UntouchedStaysSafe(d, path, file, 18);
            checks.Add(new("flush-order/paging-trim-overlap-removes-range", "PASS", "A TRIM over one learned range (as a page-file shrink sends) removed only that range: a later paging write to it learned again, and an untouched safe range still bypassed."));
            // The explicit lab override keeps worker routing active while
            // disabled, so both learning and the other direct write are real
            // worker operations rather than inactive dispatch pass-through.
            d.Control(WriteCacheAction.Disable); d.Control(WriteCacheAction.Release);
            MapWrite(path, Offset + 2 * Block, Pattern(11));
            before = d.GetDiagnostics(); file.Write(Offset + 2 * Block, Pattern(12)); cleared = d.GetDiagnostics();
            RunStorage.AtomicJson(evidence + ".paging-disabled-invalidation.json", new { Before = before, After = cleared });
            ValidateClear(before, cleared, 8); Bytes(file, Offset + 2 * Block, Pattern(12));
            checks.Add(new("flush-order/paging-disabled-overlap-clears", "PASS", "Other direct writes invalidate the learned range they overwrite while the cache is disabled."));
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
