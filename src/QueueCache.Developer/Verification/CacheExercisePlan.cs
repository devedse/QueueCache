namespace QueueCache.Developer.Verification;

/// <summary>Recall: cache-recall's QcLabReadRecall mode for the case (0 bimodal insertion, 1 read recall); -1 elsewhere.</summary>
public sealed record CacheExerciseCase(string Id, string Workload, int Streams, int QueueDepth,
    bool BackgroundDrain, int MapIntervalMs, int Seconds, int Repeat, int Recall = -1, int CallerBackoff = -1, ReadPriority? Priority = null);
public sealed record CacheExerciseTarget(string Name, int MiB);
public enum ReadPriority { Normal, CpuBelowNormal, IoLow, MemoryLow }

/// <summary>Focused workloads; the existing write-performance matrix remains unchanged.</summary>
public static class CacheExercisePlan
{
    public static bool Contains(string suite) => suite is "cache-concurrency" or "cache-sustained" or "cache-map-cost" or "cache-recall" or "caller-backoff" or "priority-cost";

    /// <summary>cache-recall files: stale data larger than the cache, a fitting file that is read again,
    /// a hot set and a scan larger than the cache (read once, then repeated as a loop).</summary>
    public static IReadOnlyList<CacheExerciseTarget> RecallTargets(int budgetMiB) =>
    [
        new("recall-stale.dat", budgetMiB * 5 / 4), new("recall-file.dat", budgetMiB / 2),
        new("recall-hot.dat", budgetMiB / 4), new("recall-scan.dat", budgetMiB * 3 / 2)
    ];

    public static IReadOnlyList<CacheExerciseTarget> Targets(int budgetMiB, int streams)
    {
        if (streams is not (1 or 2 or 4) || budgetMiB < 8 || budgetMiB % (2 * streams) != 0)
            throw new ArgumentException("Stream targets require one, two or four streams and an evenly divided half-budget working set.");
        return Enumerable.Range(0, streams)
            .Select(i => new CacheExerciseTarget($"stream-s{streams}-{i}.dat", budgetMiB / 2 / streams)).ToArray();
    }

    public static IReadOnlyList<CacheExerciseTarget> AllTargets(int budgetMiB) =>
        [.. Targets(budgetMiB, 1), .. Targets(budgetMiB, 2), .. Targets(budgetMiB, 4)];

    public static IReadOnlyList<CacheExerciseCase> Cases(VerificationOptions options)
    {
        var cases = new List<CacheExerciseCase>();
        void Add(string work, int streams, int depth, bool drain, int polling, int seconds, int repeat) =>
            cases.Add(new($"{cases.Count + 1:D4}-r{repeat}-{work}-s{streams}-q{depth}-drain{drain}-map{polling}",
                work, streams, depth, drain, polling, seconds, repeat));
        if (options.Suite == "caller-backoff")
        {
            var shapes = new[] { ("mixed", 1), ("mixed", 8), ("random-read", 1), ("read", 8) };
            for (var repeat = 1; repeat <= options.Repeats; repeat++)
            foreach (var (work, depth) in repeat % 2 == 1 ? shapes : shapes.Reverse())
            foreach (var backoff in repeat % 2 == 1 ? new[] { 256, 0 } : [0, 256])
                cases.Add(new($"{cases.Count + 1:D4}-r{repeat}-{work}-q{depth}-backoff{backoff}",
                    work, 1, depth, false, 0, options.DurationSeconds, repeat, CallerBackoff: backoff));
            return options.CaseFilter is null ? cases : cases.Where(c => c.Id.Contains(options.CaseFilter, StringComparison.Ordinal)).ToList();
        }
        if (options.Suite == "priority-cost")
        {
            var shapes = new[] { ("read", 1), ("read", 8), ("random-read", 1) };
            var priorities = Enum.GetValues<ReadPriority>();
            for (var repeat = 1; repeat <= options.Repeats; repeat++)
            foreach (var (work, depth) in repeat % 2 == 1 ? shapes : shapes.Reverse())
            foreach (var priority in repeat % 2 == 1 ? priorities : priorities.Reverse())
                cases.Add(new($"{cases.Count + 1:D4}-r{repeat}-{work}-q{depth}-priority{priority}",
                    work, 1, depth, false, 0, options.DurationSeconds, repeat, Priority: priority));
            return options.CaseFilter is null ? cases : cases.Where(c => c.Id.Contains(options.CaseFilter, StringComparison.Ordinal)).ToList();
        }
        if (options.Suite == "cache-recall")
        {
            // Alternate both the workload and the mode order between repetitions.
            for (var repeat = 1; repeat <= options.Repeats; repeat++)
                foreach (var work in repeat % 2 == 1 ? new[] { "reread", "scan" } : ["scan", "reread"])
                foreach (var mode in repeat % 2 == 1 ? new[] { 0, 1 } : [1, 0])
                    cases.Add(new($"{cases.Count + 1:D4}-r{repeat}-{work}-recall{mode}", work, 1, 8, false, 0, 3, repeat, mode));
            return options.CaseFilter is null ? cases : cases.Where(c => c.Id.Contains(options.CaseFilter, StringComparison.Ordinal)).ToList();
        }
        if (options.Suite == "cache-sustained")
        {
            for (var round = 1; round <= 6; round++)
                Add("mixed", 1, 8, true, 0, (options.SoakSeconds ?? 1800) / 6, round);
            return cases;
        }
        for (var repeat = 1; repeat <= options.Repeats; repeat++)
        {
            if (options.Suite == "cache-concurrency")
            {
                var shapes = repeat % 2 == 1 ? new[] { (1, 1), (1, 8), (2, 4), (4, 2) } : [(4, 2), (2, 4), (1, 8), (1, 1)];
                foreach (var work in new[] { "read", "write" })
                foreach (var (streams, depth) in shapes)
                foreach (var drain in work == "read" ? new[] { false } : repeat % 2 == 1 ? [false, true] : [true, false])
                    Add(work, streams, depth, drain, 0, options.DurationSeconds, repeat);
            }
            if (options.Suite == "cache-map-cost")
                foreach (var polling in repeat % 2 == 1 ? new[] { 0, 2000, 250 } : [250, 2000, 0])
                    Add("random-read", 1, 8, false, polling, options.DurationSeconds, repeat);
        }
        return options.CaseFilter is null ? cases : cases.Where(c => c.Id.Contains(options.CaseFilter, StringComparison.Ordinal)).ToList();
    }
}
