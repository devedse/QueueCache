namespace QueueCache.Developer.Verification;

public sealed record CacheExerciseCase(string Id, string Workload, int Streams, int QueueDepth,
    bool BackgroundDrain, int MapIntervalMs, int Seconds, int Repeat);

/// <summary>Focused workloads; the existing write-performance matrix remains unchanged.</summary>
public static class CacheExercisePlan
{
    public static bool Contains(string suite) => suite is "cache-concurrency" or "cache-sustained" or "cache-map-cost";

    public static IReadOnlyList<CacheExerciseCase> Cases(VerificationOptions options)
    {
        var cases = new List<CacheExerciseCase>();
        void Add(string work, int streams, int depth, bool drain, int polling, int seconds, int repeat) =>
            cases.Add(new($"{cases.Count + 1:D4}-r{repeat}-{work}-s{streams}-q{depth}-drain{drain}-map{polling}",
                work, streams, depth, drain, polling, seconds, repeat));
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
