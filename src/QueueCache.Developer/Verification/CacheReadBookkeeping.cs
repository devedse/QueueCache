using System.Text.Json;

namespace QueueCache.Developer.Verification;

/// <summary>Plan 120 same-build comparison of cached read bookkeeping (copy flags 3 against 7) with the
/// plan-118 screen. Built only from this run's snapshots and scores; raw evidence remains authoritative.</summary>
public static class CacheReadBookkeeping
{
    public const ulong DefaultFlags = 3, CandidateFlags = 7;
    public const double TargetGainPercent = 5, ControlLossPercent = -3;
    public static bool IsTarget(PerformanceCase c) => c.QueueDepth == 8 && c.Threads == 1;

    public static string Report(RunStorage storage, IReadOnlyList<PerformanceCase> cases)
    {
        var windows = cases.Select(c =>
        {
            using var before = JsonDocument.Parse(File.ReadAllText(storage.PathFor(c.Id + "-before.json")));
            var score = JsonSerializer.Deserialize<DiskSpdScore>(File.ReadAllText(storage.PathFor(c.Id + "-reader.score.json")))
                ?? throw new InvalidDataException("Missing cached read comparison score.");
            return Window(c, before.RootElement, score);
        }).ToArray();
        var (verdict, shapes) = Evaluate(windows);
        storage.Write("cache-read-bookkeeping.json", new
        {
            Contract = "Plan 118 screen: target median >= +5%, every pair up, candidate range above original range; " +
                       "controls fail only on a repeatable loss of 3% or more. Not a release qualification.",
            Verdict = verdict, Shapes = shapes, Windows = windows
        });
        return verdict;
    }

    public sealed record ComparisonWindow(PerformanceCase Case, ulong CopyFlags, double MiBPerSecond);
    public sealed record ComparisonShape(string Shape, string Role, double OriginalMedianMiBps, double CandidateMedianMiBps,
        double MedianChangePercent, double[] PairChangesPercent, bool Separated, string Outcome);

    public static ComparisonWindow Window(PerformanceCase c, JsonElement before, DiskSpdScore score)
    {
        if (c.CopyFlags is not { } planned) throw new InvalidDataException("Comparison case has no planned copy flags.");
        if (!before.TryGetProperty("Diagnostics", out var diagnostics) || !diagnostics.TryGetProperty("CopyFlags", out var flags) ||
            flags.ValueKind != JsonValueKind.Number || flags.GetUInt64() != planned)
            throw new InvalidDataException($"{c.Id}: the cache did not report the planned copy flags {planned} before scoring.");
        if (score.Operations <= 0 || score.Bytes != checked(score.Operations * (1L << 20)))
            throw new InvalidDataException($"{c.Id}: missing completions or a wrong transfer size.");
        return new(c, planned, score.MiBPerSecond);
    }

    public static (string Verdict, ComparisonShape[] Shapes) Evaluate(IReadOnlyList<ComparisonWindow> windows)
    {
        if (windows.Count == 0 || windows.Select(w => w.Case.Id).Distinct().Count() != windows.Count)
            throw new InvalidDataException("Cached read comparison has missing or duplicate windows.");
        var repeats = windows.Max(w => w.Case.Repeat);
        var shapes = windows.GroupBy(w => (w.Case.QueueDepth, w.Case.Threads)).Select(g =>
        {
            var pairs = g.GroupBy(w => w.Case.Repeat).OrderBy(p => p.Key).Select(p =>
            {
                var original = p.SingleOrDefault(w => w.CopyFlags == DefaultFlags) ?? throw new InvalidDataException("Missing original window.");
                var candidate = p.SingleOrDefault(w => w.CopyFlags == CandidateFlags) ?? throw new InvalidDataException("Missing candidate window.");
                return (Original: original.MiBPerSecond, Candidate: candidate.MiBPerSecond);
            }).ToArray();
            var originalMedian = Median(pairs.Select(p => p.Original));
            var candidateMedian = Median(pairs.Select(p => p.Candidate));
            var change = 100 * (candidateMedian / originalMedian - 1);
            var changes = pairs.Select(p => 100 * (p.Candidate / p.Original - 1)).ToArray();
            var separated = pairs.Min(p => p.Candidate) > pairs.Max(p => p.Original);
            var target = g.Key.QueueDepth == 8 && g.Key.Threads == 1;
            var outcome = target
                ? change >= TargetGainPercent && changes.All(x => x > 0) && separated ? "WIN" : change >= TargetGainPercent ? "AMBIGUOUS" : "NO_WIN"
                : change <= ControlLossPercent && changes.All(x => x < 0) ? "LOSS" : "OK";
            return new ComparisonShape($"1M Q{g.Key.QueueDepth}T{g.Key.Threads}", target ? "target" : "control",
                originalMedian, candidateMedian, change, changes, separated, outcome);
        }).OrderBy(s => s.Role == "target" ? 0 : 1).ToArray();
        var targetOutcome = shapes.Single(s => s.Role == "target").Outcome;
        var verdict = repeats < 3 ? "INCOMPLETE_FOR_ACCEPTANCE"
            : targetOutcome == "NO_WIN" ? "REJECT"
            : targetOutcome == "AMBIGUOUS" ? "AMBIGUOUS_ONE_MORE_BATCH"
            : shapes.Any(s => s.Outcome == "LOSS") ? "CONTROL_LOSS_REVISION_OR_REJECT" : "ACCEPT_SCREEN";
        return (verdict, shapes);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
