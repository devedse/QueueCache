using System.Text.Json;
using QueueCache.Developer.Verification;
using QueueCache.Management;

internal static class CacheExerciseTests
{
    public static void Run()
    {
        void Check(bool value, string name) { if (!value) throw new Exception(name); }
        void Reject(Action action)
        {
            try { action(); } catch (Exception e) when (e is ArgumentException or InvalidDataException) { return; }
            throw new Exception("Expected exercise rejection.");
        }
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mismatch = new IOException("fixture byte mismatch");
        var stopped = CacheExerciseTasks.CompleteWorkload(pending.Task, Task.FromException(mismatch));
        Check(stopped.IsFaulted && !pending.Task.IsCompleted, "A failed oracle is reported before the workload completes.");
        try { stopped.GetAwaiter().GetResult(); } catch (IOException error) { Check(ReferenceEquals(error, mismatch), "Oracle failure retains its original error."); }
        var finishing = CacheExerciseTasks.CompleteWorkload(pending.Task, Task.CompletedTask);
        Check(!finishing.IsCompleted, "An early successful oracle still waits for the workload.");
        pending.SetResult(7);
        Check(finishing.GetAwaiter().GetResult() == 7, "Successful score and oracle both complete before recording.");
        ConcurrentSectorOracle.ValidateTarget("ntfs", 512, 8);
        foreach (var (fileSystem, sector, cluster) in new[] { ("NTFS", 4096u, 1u), ("NTFS", 512u, 4u), ("FAT32", 512u, 8u) })
        {
            try { ConcurrentSectorOracle.ValidateTarget(fileSystem, sector, cluster); }
            catch (NotSupportedException) { continue; }
            throw new Exception("Sector oracle accepted an unsupported or unaligned shared-block target.");
        }
        var options = new VerificationOptions("Q:", "cache-concurrency", DiskSpd: Environment.ProcessPath, BudgetMiB: 2048);
        VerificationPlan.Validate(options);
        var concurrency = CacheExercisePlan.Cases(options);
        foreach (var streams in new[] { 1, 2, 4 })
        {
            var targets = CacheExercisePlan.Targets(2048, streams);
            Check(targets.Count == streams && targets.Sum(file => file.MiB) == 1024 &&
                  targets.All(file => file.MiB == 1024 / streams), "Complete stream files fit the fixed half-budget working set.");
        }
        Check(CacheExercisePlan.AllTargets(2048).Select(file => file.Name).Distinct().Count() == 7,
            "Each stream shape owns distinct complete files; no unaccessed tails require disk reads.");
        Reject(() => CacheExercisePlan.Targets(2048, 3));
        Reject(() => CacheExercisePlan.Targets(257, 4));
        Reject(() => VerificationPlan.Validate(options with { BudgetMiB = 257 }));
        Check(VerificationPlan.Integrity(options).Single().Operation == "concurrent-sectors", "Concurrency verifies neighboring-sector bytes before performance.");
        Check(concurrency.Count == 36 && concurrency.Select(c => c.Id).Distinct().Count() == 36, "Concurrent plan has unique complete repetitions.");
        Check(concurrency.Where(c => c.QueueDepth != 1 || c.Streams != 1).All(c => c.Streams * c.QueueDepth == 8), "Concurrent shapes keep total queue depth at eight.");
        Check(concurrency.Where(c => c.Workload == "read").All(c => !c.BackgroundDrain), "Read controls never enable background drain.");
        Check(concurrency[0].Streams == 1 && concurrency[12].Streams == 4, "Concurrent repetitions reverse shape order.");
        VerificationPlan.Validate(options with { Suite = "cache-map-cost" });
        Check(CacheExercisePlan.Cases(options with { Suite = "cache-map-cost" }).Select(c => c.MapIntervalMs).SequenceEqual([0, 2000, 250, 250, 2000, 0, 0, 2000, 250]), "Map controls alternate normal and stress polling.");
        var sustained = options with { Suite = "cache-sustained", Repeats = 1 };
        VerificationPlan.Validate(sustained);
        Check(CacheExercisePlan.Cases(sustained).Sum(c => c.Seconds) == 1800, "Sustained default contains thirty minutes of mixed I/O.");
        Reject(() => VerificationPlan.Validate(sustained with { Repeats = 3 }));
        Reject(() => VerificationPlan.Validate(sustained with { SoakSeconds = 121 }));
        Reject(() => VerificationPlan.Validate(sustained with { CaseFilter = "mixed" }));
        Reject(() => VerificationPlan.Validate(options with { SoakSeconds = 1800 }));
        Reject(() => VerificationPlan.Validate(options with { CaseFilter = "missing" }));
        Reject(() => VerificationPlan.Validate(options with { DiskSpd = null }));
        var recall = options with { Suite = "cache-recall" };
        VerificationPlan.Validate(recall);
        var recallCases = CacheExercisePlan.Cases(recall);
        Check(recallCases.Count == 12 && recallCases.Select(c => c.Id).Distinct().Count() == 12 &&
              recallCases.All(c => c.Recall is 0 or 1 && c.Workload is "reread" or "scan"), "Recall plan has unique repetitions of both workloads and modes.");
        Check(recallCases.GroupBy(c => (c.Workload, c.Recall)).All(g => g.Count() == 3), "Each workload/mode pair repeats three times.");
        Check(recallCases.Take(4).Select(c => (c.Workload, c.Recall)).SequenceEqual([("reread", 0), ("reread", 1), ("scan", 0), ("scan", 1)]) &&
              recallCases.Skip(4).Take(4).Select(c => (c.Workload, c.Recall)).SequenceEqual([("scan", 1), ("scan", 0), ("reread", 1), ("reread", 0)]),
            "Recall repetitions reverse both workload and mode order.");
        Check(CacheExercisePlan.Cases(options).All(c => c.Recall == -1), "Other exercises never change the recall mode.");
        var recallFiles = CacheExercisePlan.RecallTargets(2048);
        Check(recallFiles.Single(f => f.Name == "recall-stale.dat").MiB > 2048 && recallFiles.Single(f => f.Name == "recall-scan.dat").MiB > 2048 &&
              recallFiles.Single(f => f.Name == "recall-file.dat").MiB == 1024 && recallFiles.Single(f => f.Name == "recall-hot.dat").MiB == 512,
            "Stale data and the scan exceed the cache; the reread file and hot set fit.");
        Check(CacheExercisePlan.Cases(recall with { CaseFilter = "scan-recall1" }).Count == 3, "Recall cases can be selected by ID.");
        Reject(() => VerificationPlan.Validate(recall with { BudgetMiB = 512 }));
        Check(CacheExercisePlan.Cases(options with { Suite = "quick" }).Count == 0, "New exercises are opt-in.");

        var state = new WriteCacheState(1 | 32 | 256 | 512, 0, 200UL << 30, 2UL << 30, 2UL << 30, 0, 0, 1UL << 30, 1, 0, 0, 0, 0, 0, 0, 0)
        { Instance = 7, Generation = 3 };
        var disabled = state with { Flags = state.Flags & ~1u, Generation = 4, OccupiedSlots = 0 };
        ConcurrentSectorOracle.ValidateDisabled(state, disabled);
        Reject(() => ConcurrentSectorOracle.ValidateDisabled(state, disabled with { Generation = 3 }));
        Reject(() => ConcurrentSectorOracle.ValidateDisabled(state, disabled with { Generation = 5 }));
        Reject(() => ConcurrentSectorOracle.ValidateDisabled(state, disabled with { Instance = 8 }));
        Reject(() => ConcurrentSectorOracle.ValidateDisabled(state, disabled with { Flags = state.Flags }));
        Reject(() => ConcurrentSectorOracle.ValidateDisabled(state, disabled with { DirtyBytes = 512 }));
        Reject(() => ConcurrentSectorOracle.ValidateDisabled(state, disabled with { OccupiedSlots = 1 }));
        Reject(() => ConcurrentSectorOracle.ValidateDisabled(state, disabled with { Errors = 1 }));
        Reject(() => ConcurrentSectorOracle.ValidateDisabled(state, disabled with { ReservedBytes = 0 }));
        var performance = JsonSerializer.Deserialize<CachePerformance>("{\"Frequency\":10000000}")!;
        var diagnostics = new CacheDiagnostics(0, 0, 0, 0, 0, 0, 0, 0, 0) { Attribution = JsonSerializer.Deserialize<CacheAttribution>("{}")! };
        var before = new CacheLayoutSnapshot(state, performance, diagnostics);
        var after = before with { State = state with { AcceptedBytes = 4096, ReadHitBytes = 4096 } };
        CacheExerciseEvidence.Validate(before, after, 4096, "write", false);
        CacheExerciseEvidence.Validate(before, after, 4096, "read", false);
        var recallState = state;
        CacheLayoutSnapshot Recall(ulong mode, ulong hits, ulong misses, ulong evictions = 0) => new(recallState with { ReadHitBytes = hits, ReadMissBytes = misses, Evictions = evictions },
            JsonSerializer.Deserialize<CachePerformance>("{}")!, new CacheDiagnostics(0, 0, 0, 0, 0, 0, 0, 0, 0) { ReadRecall = new(mode, 0, 0) });
        var pass = CacheRecallEvidence.Pass("p", "file", Recall(1, 0, 0), Recall(1, 3 << 20, 1 << 20, 5), new(4 << 20, 4, 0.5), 1);
        Check(pass.HitPercent == 75 && pass.Evictions == 5 && Math.Abs(pass.MiBPerSecond - 8) < 1e-9, "Recall pass records hits, misses, evictions and speed.");
        Check(CacheRecallEvidence.Pass("p", "file", Recall(1, 0, 0), Recall(1, 4 << 20, 1 << 20), new(4 << 20, 4, 1), 1).HitBytes == 4 << 20,
            "Other readers may add bytes to a pass.");
        Reject(() => CacheRecallEvidence.Pass("p", "file", Recall(1, 0, 0), Recall(1, 1 << 20, 1 << 20), new(4 << 20, 4, 1), 1));
        Reject(() => CacheRecallEvidence.Pass("p", "file", Recall(0, 0, 0), Recall(0, 4 << 20, 0), new(4 << 20, 4, 1), 1));
        Reject(() => CacheRecallEvidence.Pass("p", "file", Recall(1, 0, 0), Recall(1, 4 << 20, 0) with { State = recallState with { ReadHitBytes = 4 << 20, Generation = recallState.Generation + 1 } }, new(4 << 20, 4, 1), 1));
        Reject(() => CacheExerciseEvidence.Validate(before, after with { State = after.State with { Generation = 4 } }, 4096, "mixed", true));
        Reject(() => CacheExerciseEvidence.Validate(before, after with { State = after.State with { AcceptedBytes = 0 } }, 4096, "write", false));
        var io = after with { Diagnostics = diagnostics with { Attribution = diagnostics.Attribution! with { LowerWriteAttempts = 1 } } };
        Reject(() => CacheExerciseEvidence.Validate(before, io, 4096, "write", false));
        CacheExerciseEvidence.Validate(before, io, 4096, "mixed", true);
        CacheExerciseEvidence.ValidateChurnedRead(before, io with { State = state with { ReadHitBytes = 2048, ReadMissBytes = 2048 } }, 4096);
        Reject(() => CacheExerciseEvidence.ValidateChurnedRead(before, io, 8192));
        Reject(() => CacheExerciseEvidence.ValidateChurnedRead(before, io with { State = state with { Generation = 4 } }, 4096));
        Reject(() => CacheExerciseEvidence.ValidateChurnedRead(before, io with { State = state with { ReadMissBytes = 4096, Errors = 1 } }, 4096));
        Reject(() => CacheExerciseEvidence.Validate(before, after with { State = after.State with { Errors = 1 } }, 4096, "mixed", true));
        Check(!CacheLayoutEvidence.IsQuiet(before, before with { State = state with { Generation = 4 } }), "Quiet waits reject allocation changes.");
        Check(ConcurrentCacheOracle.Pattern(1, 2, 4096).SequenceEqual(ConcurrentCacheOracle.Pattern(1, 2, 4096)) &&
              !ConcurrentCacheOracle.Pattern(1, 2, 4096).SequenceEqual(ConcurrentCacheOracle.Pattern(1, 3, 4096)), "Oracle epochs have deterministic distinct bytes.");
        Console.WriteLine("Cache exercise contracts passed (no driver access).");
    }
}
