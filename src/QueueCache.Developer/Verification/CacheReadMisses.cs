using System.Text.Json;

namespace QueueCache.Developer.Verification;

/// <summary>Same-build read-miss comparison with cached controls. No private orchestration or
/// scores from other runs: mode, residency, byte accounting and health are checked per window.</summary>
public static class CacheReadMisses
{
    public const ulong OriginalFlags = 7, CandidateFlags = 15;

    public static IReadOnlyList<PerformanceCase> Cases(VerificationOptions options)
    {
        var result = new List<PerformanceCase>();
        var shapes = new[] { ("random-read", 32, false), ("sequential-read", 8, false),
            ("random-read", 32, true), ("sequential-read", 8, true) };
        for (var repeat = 1; repeat <= options.Repeats; repeat++)
        foreach (var (workload, depth, warm) in repeat % 2 == 1 ? shapes : shapes.Reverse())
        foreach (var candidate in repeat % 2 == 1 ? new[] { false, true } : [true, false])
            result.Add(new($"{result.Count + 1:D4}-r{repeat}-{workload}-q{depth}-{(warm ? "warm" : "cold")}-{(candidate ? "candidate" : "original")}",
                "Automatic", "Idle", 0, depth, false, repeat, Workload: workload,
                Resident: warm, Timing: false, WarmResident: warm,
                CopyFlags: candidate ? CandidateFlags : OriginalFlags));
        return result;
    }

    public sealed record ComparisonWindow(PerformanceCase Case, double MiBPerSecond, double P99Milliseconds,
        double MaxMilliseconds, ulong HitBytes, ulong MissBytes, ulong LowerReads);
    public sealed record ComparisonShape(string Shape, string Role, double OriginalMedianMiBps, double CandidateMedianMiBps,
        double MedianChangePercent, double[] PairChangesPercent, bool Separated,
        double OriginalMaxMilliseconds, double CandidateMaxMilliseconds, string Outcome);

    public static ComparisonWindow Window(PerformanceCase c, JsonElement before, JsonElement after, DiskSpdScore score)
    {
        static ulong Value(JsonElement e, string group, string name) => e.GetProperty(group).GetProperty(name).GetUInt64();
        static ulong Delta(JsonElement a, JsonElement b, string group, string name)
        {
            var first = Value(a, group, name); var last = Value(b, group, name);
            return last >= first ? last - first : throw new InvalidDataException("Counter regressed: " + name);
        }
        if (c.CopyFlags is not (OriginalFlags or CandidateFlags) ||
            Value(before, "Diagnostics", "CopyFlags") != c.CopyFlags || Value(after, "Diagnostics", "CopyFlags") != c.CopyFlags ||
            !before.GetProperty("State").GetProperty("Operational").GetBoolean() ||
            !after.GetProperty("State").GetProperty("Operational").GetBoolean() ||
            Value(before, "State", "Instance") == 0 || Value(before, "State", "Instance") != Value(after, "State", "Instance") ||
            Value(before, "State", "Generation") != Value(after, "State", "Generation") ||
            Value(before, "State", "PayloadCapacity") != Value(after, "State", "PayloadCapacity") ||
            Value(before, "State", "Errors") != Value(after, "State", "Errors") ||
            before.GetProperty("State").GetProperty("LastError").GetInt32() != 0 ||
            after.GetProperty("State").GetProperty("LastError").GetInt32() != 0 ||
            Value(before, "Performance", "TimingEnabled") != 0 || Value(after, "Performance", "TimingEnabled") != 0)
            throw new InvalidDataException(c.Id + ": mode, identity, health or timing changed.");
        var hits = Delta(before, after, "State", "ReadHitBytes");
        var misses = Delta(before, after, "State", "ReadMissBytes");
        var firstIo = before.GetProperty("Diagnostics").GetProperty("Attribution");
        var lastIo = after.GetProperty("Diagnostics").GetProperty("Attribution");
        var firstReads = firstIo.GetProperty("LowerReadAttempts").GetUInt64();
        var lastReads = lastIo.GetProperty("LowerReadAttempts").GetUInt64();
        var block = c.Workload == "random-read" ? 4096 : 1 << 20;
        if (score.Operations <= 0 || score.Bytes != checked(score.Operations * block) ||
            score.ReadP99Milliseconds is null || score.ReadMaxMilliseconds is null ||
            checked(hits + misses) < (ulong)score.Bytes || lastReads < firstReads)
            throw new InvalidDataException(c.Id + ": missing completions, latency or byte accounting.");
        if (c.WarmResident ? misses != 0 || lastReads != firstReads : misses == 0 || lastReads == firstReads)
            throw new InvalidDataException(c.Id + ": cached control missed or cold target did not read the disk.");
        return new(c, score.MiBPerSecond, score.ReadP99Milliseconds.Value, score.ReadMaxMilliseconds.Value,
            hits, misses, lastReads - firstReads);
    }

    public static (string Verdict, ComparisonShape[] Shapes) Evaluate(IReadOnlyList<ComparisonWindow> windows)
    {
        if (windows.Count == 0 || windows.Select(w => w.Case.Id).Distinct().Count() != windows.Count ||
            windows.Any(w => !double.IsFinite(w.MiBPerSecond) || w.MiBPerSecond <= 0))
            throw new InvalidDataException("Missing, duplicate or invalid read-miss windows.");
        var repeats = windows.Max(w => w.Case.Repeat);
        var expected = Cases(new("Q:", "cache-read-misses", Repeats: repeats));
        if (windows.Count != expected.Count || !expected.Select(c => c.Id).ToHashSet().SetEquals(windows.Select(w => w.Case.Id)))
            throw new InvalidDataException("Incomplete read-miss matrix; never combine partial runs.");
        var shapes = windows.GroupBy(w => (w.Case.Workload, w.Case.WarmResident)).Select(g =>
        {
            var pairs = g.GroupBy(w => w.Case.Repeat).OrderBy(p => p.Key).Select(p =>
                (Original: p.Single(w => w.Case.CopyFlags == OriginalFlags), Candidate: p.Single(w => w.Case.CopyFlags == CandidateFlags))).ToArray();
            var original = Median(pairs.Select(p => p.Original.MiBPerSecond));
            var candidate = Median(pairs.Select(p => p.Candidate.MiBPerSecond));
            var change = 100 * (candidate / original - 1);
            var changes = pairs.Select(p => 100 * (p.Candidate.MiBPerSecond / p.Original.MiBPerSecond - 1)).ToArray();
            var separated = pairs.Min(p => p.Candidate.MiBPerSecond) > pairs.Max(p => p.Original.MiBPerSecond);
            var outcome = g.Key.WarmResident ? change <= -3 && changes.All(x => x < 0) ? "LOSS" : "OK"
                : change >= 5 && changes.All(x => x > 0) && separated ? "WIN" : change >= 5 ? "AMBIGUOUS" : "NO_WIN";
            return new ComparisonShape(g.Key.Workload, g.Key.WarmResident ? "control" : "target", original, candidate,
                change, changes, separated, pairs.Max(p => p.Original.MaxMilliseconds), pairs.Max(p => p.Candidate.MaxMilliseconds), outcome);
        }).ToArray();
        var verdict = repeats < 3 ? "INCOMPLETE_FOR_ACCEPTANCE"
            : shapes.Any(s => s.Outcome == "LOSS") ? "CONTROL_LOSS_REVISION_OR_REJECT"
            : shapes.Any(s => s.Outcome == "NO_WIN") ? "REJECT"
            : shapes.Any(s => s.Outcome == "AMBIGUOUS") ? "AMBIGUOUS_ONE_MORE_BATCH" : "ACCEPT_SCREEN";
        return (verdict, shapes);
    }

    public static string Report(RunStorage storage, IReadOnlyList<PerformanceCase> cases)
    {
        var windows = cases.Select(c =>
        {
            using var before = JsonDocument.Parse(File.ReadAllText(storage.PathFor(c.Id + "-before.json")));
            using var after = JsonDocument.Parse(File.ReadAllText(storage.PathFor(c.Id + "-after.json")));
            var score = JsonSerializer.Deserialize<DiskSpdScore>(File.ReadAllText(storage.PathFor(c.Id + "-reader.score.json")))
                ?? throw new InvalidDataException("Missing read-miss comparison score.");
            return Window(c, before.RootElement, after.RootElement, score);
        }).ToArray();
        var (verdict, shapes) = Evaluate(windows);
        storage.Write("cache-read-misses.json", new { Verdict = verdict, Shapes = shapes, Windows = windows });
        return verdict;
    }

    private static double Median(IEnumerable<double> values)
    {
        var a = values.Order().ToArray();
        return (a[(a.Length - 1) / 2] + a[a.Length / 2]) / 2;
    }
}
