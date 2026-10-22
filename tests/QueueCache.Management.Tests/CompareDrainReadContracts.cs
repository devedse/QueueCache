using QueueCache.Developer.Verification;

internal static class CompareDrainReadContracts
{
    public static void Run(Action<bool, string> check, Action<Action> reject)
    {
        var plan = ComparisonPlan.CasesFor(ComparisonDrainRead.Suite, 3);
        check(plan.Select(c => c.Id).SequenceEqual(["read-alone-r1", "read-loaded-r1", "read-loaded-r2", "read-alone-r2", "read-alone-r3", "read-loaded-r3"]) &&
            plan.All(c => c.File == ComparisonDrainRead.ReaderFile && c.BlockKiB == 4 && c.Queue == 1 && c.Threads == 1 && c.Random) &&
            ComparisonPlan.UsesDiskSpd(ComparisonDrainRead.Suite) && ComparisonDrain.IsSuite(ComparisonDrainRead.Suite) &&
            VerificationPlan.Suites.Contains(ComparisonDrainRead.Suite) &&
            ComparisonPlan.WorkDirectory(ComparisonDrainRead.Suite, "root", "run-a") == Path.Combine("root", "run-a"),
            "plan 161 compare-drain-read: three alternating alone/loaded pairs of the same 4 KiB random Q1 reader, fresh root, DiskSpd required");
        try { ComparisonDrainRead.Cases(2); throw new Exception("Accepted an incomplete read-during-drain matrix."); }
        catch (ArgumentException) { }
        var reader = ComparisonPlan.Arguments(ComparisonDrainRead.ReaderWindow(plan[1]), ComparisonDrainRead.ReaderBytes);
        var writer = ComparisonPlan.Arguments(ComparisonDrainRead.WriterWindow(plan[1]), ComparisonDrainRead.WriterBytes);
        check(new[] { "-b4K", "-o1", "-t1", "-w0", "-f8192M", "-d10", "-Su", "-r4K" }.All(reader.Contains) && !reader.Contains("-Z1M") &&
            new[] { "-b1024K", "-o8", "-t1", "-w100", "-f8192M", "-d30", "-Su", "-Z1M" }.All(writer.Contains) && !writer.Any(a => a.StartsWith("-r")),
            "the reader is unbuffered 4 KiB random Q1 on 8 GiB; the writer is unbuffered sequential 1 MiB Q8 for 30 s");
        var options = new VerificationOptions("Q:", ComparisonDrainRead.Suite, BudgetMiB: 2048, Product: "label", Provider: ComparisonProvider.QueueCache,
            DiskSpd: Environment.ProcessPath);
        VerificationPlan.Validate(options);
        VerificationPlan.Validate(options with { Provider = ComparisonProvider.PrimoCache, PrimoCli = Environment.ProcessPath });
        foreach (var invalid in new[] { options with { DiskSpd = null }, options with { Provider = null }, options with { Repeats = 2 },
            options with { BudgetMiB = 1024 }, options with { CaseFilter = "loaded" } })
        {
            try { VerificationPlan.Validate(invalid); throw new Exception("Accepted invalid compare-drain-read options."); }
            catch (ArgumentException) { }
        }

        var t0 = DateTimeOffset.UnixEpoch;
        var loadedCase = plan[1];
        ComparisonReadScore Loaded(double lead = 12.5, double writerTail = 8, long writeRate = 30_000_000, ulong dirtyAtStart = 1UL << 31)
        {
            var writerStart = t0.AddSeconds(0.1);
            var readerStart = writerStart.AddSeconds(lead);
            var readerEnd = readerStart.AddSeconds(12);
            var writerEnd = readerEnd.AddSeconds(writerTail);
            var after = writerEnd.AddSeconds(2);
            var ended = after.AddSeconds(0.1);
            var samples = new List<ComparisonDiskCounters>();
            long bytes = 0; uint ops = 0;
            for (var at = t0; at <= ended; at = at.AddMilliseconds(100))
            {
                var writing = at > writerStart && at <= writerEnd;
                if (writing) { bytes += writeRate; ops += 15; }
                samples.Add(new(at, bytes, ops, writing ? 8u : 0u));
            }
            var score = new ComparisonScore(loadedCase.Id + "-reader", 4096L * 5000, 5000, 10, 2.048, 500, 1.5, 28, 60, null, null, null, 6);
            var writes = new ComparisonScore(loadedCase.Id + "-writer", 9_000_000_000, 8583, 30, 300, 286, null, null, null, 25, 60, 90, 9);
            return new(loadedCase.Id, ComparisonProvider.QueueCache, true, t0, writerStart, readerStart, readerEnd, writerEnd, ended,
                score, writes, new(t0, ComparisonProvider.QueueCache, 0, 0, null),
                new(readerStart.AddSeconds(-0.2), ComparisonProvider.QueueCache, dirtyAtStart, 16UL << 20, null),
                new(readerEnd.AddSeconds(0.1), ComparisonProvider.QueueCache, 1UL << 31, 16UL << 20, null),
                new(after, ComparisonProvider.QueueCache, 0, 0, null), samples[0], samples[^1], samples.ToArray(),
                ComparisonDrainRead.WindowBytes(samples, readerStart, readerEnd));
        }
        var loaded = Loaded();
        ComparisonDrainRead.ValidateScore(loadedCase, loaded, ComparisonProvider.QueueCache);
        check(loaded.ReaderWindowDiskBytesWritten > ComparisonDrainRead.LoadedMinDiskBytes,
            "a loaded window proves the backing disk drained throughout the reader window");
        reject(() => ComparisonDrainRead.ValidateScore(loadedCase, Loaded(lead: 5), ComparisonProvider.QueueCache));
        reject(() => ComparisonDrainRead.ValidateScore(loadedCase, Loaded(dirtyAtStart: 0), ComparisonProvider.QueueCache));
        reject(() => ComparisonDrainRead.ValidateScore(loadedCase, Loaded(writeRate: 1000), ComparisonProvider.QueueCache));
        reject(() => ComparisonDrainRead.ValidateScore(loadedCase, loaded with { WriterEndedUtc = loaded.ReaderEndedUtc.AddSeconds(-1) }, ComparisonProvider.QueueCache));
        reject(() => ComparisonDrainRead.ValidateScore(loadedCase, loaded with { ReaderWindowDiskBytesWritten = loaded.ReaderWindowDiskBytesWritten + 1 }, ComparisonProvider.QueueCache));
        reject(() => ComparisonDrainRead.ValidateScore(loadedCase, loaded with { Reader = loaded.Reader with { Operations = 0, Bytes = 0 } }, ComparisonProvider.QueueCache));
        reject(() => ComparisonDrainRead.ValidateScore(loadedCase, loaded with { Writer = null }, ComparisonProvider.QueueCache));
        reject(() => ComparisonDrainRead.ValidateScore(loadedCase, loaded with { After = loaded.After with { DirtyBytes = 4096 } }, ComparisonProvider.QueueCache));
        reject(() => ComparisonDrainRead.ValidateScore(loadedCase, loaded, ComparisonProvider.PrimoCache));
        reject(() => ComparisonDrainRead.ValidateScore(plan[0], loaded, ComparisonProvider.QueueCache));

        var aloneCase = plan[0];
        var aloneSamples = Enumerable.Range(0, 150).Select(i => new ComparisonDiskCounters(t0.AddMilliseconds(100 * i), 4096L * (i / 50), (uint)(i / 50), 0)).ToArray();
        var readerStarted = t0.AddSeconds(0.5);
        var aloneScore = new ComparisonScore(aloneCase.Id + "-reader", 4096L * 30000, 30000, 10, 12.3, 3000, 0.3, 0.7, 1.2, null, null, null, 10);
        var alone = new ComparisonReadScore(aloneCase.Id, ComparisonProvider.QueueCache, false, t0, null, readerStarted, readerStarted.AddSeconds(12), null,
            t0.AddSeconds(14.9), aloneScore, null, new(t0, ComparisonProvider.QueueCache, 0, 0, null), null, null,
            new(t0.AddSeconds(14.5), ComparisonProvider.QueueCache, 0, 0, null), aloneSamples[0], aloneSamples[^1], aloneSamples,
            ComparisonDrainRead.WindowBytes(aloneSamples, readerStarted, readerStarted.AddSeconds(12)));
        ComparisonDrainRead.ValidateScore(aloneCase, alone, ComparisonProvider.QueueCache);
        var noisy = aloneSamples.Select(s => s with { BytesWritten = s.BytesWritten * 10_000 }).ToArray();
        reject(() => ComparisonDrainRead.ValidateScore(aloneCase, alone with { DiskSamples = noisy, DiskAfter = noisy[^1],
            ReaderWindowDiskBytesWritten = ComparisonDrainRead.WindowBytes(noisy, readerStarted, readerStarted.AddSeconds(12)) }, ComparisonProvider.QueueCache));
        reject(() => ComparisonDrainRead.ValidateScore(aloneCase, alone with { Writer = loaded.Writer }, ComparisonProvider.QueueCache));
        Console.WriteLine("Read-during-drain plan, DiskSpd windows, options, drain coverage and alone-window contracts passed (no driver access).");
    }
}
