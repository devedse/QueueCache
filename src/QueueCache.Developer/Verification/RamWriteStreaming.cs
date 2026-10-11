using System.Globalization;
using QueueCache.Management;

namespace QueueCache.Developer.Verification;

public sealed record RamStreamWindow(RamReadReferenceCase Case, Guid ResourceId, Guid BootEpoch,
    ulong CreationGeneration, RamStreamWrites Before, RamStreamWrites After, double StreamShare, DiskSpdScore Score);

/// <summary>Same-build ordinary/streaming write comparison with the plan-118 gates (docs/TESTING.md section 6).
/// The verdict is a screen; raw evidence remains authoritative.</summary>
public static class RamWriteStreaming
{
    public const double TargetGainPercent = 5, ControlLossPercent = -3, MinimumCandidateShare = 0.9;
    public static bool IsTarget(RamReadReferenceCase c) => c.QueueDepth == 1 && c.Threads == 1;

    public static string Report(string evidence, IReadOnlyList<RamReadReferenceCase> plan, IReadOnlyList<RamStreamWindow> windows)
    {
        if (windows.Count != plan.Count || windows.Select(w => w.Case.Id).Distinct().Count() != plan.Count ||
            plan.Any(c => !windows.Any(w => w.Case == c && w.ResourceId != Guid.Empty && w.BootEpoch != Guid.Empty && w.CreationGeneration != 0)))
            throw new InvalidDataException("RAM write streaming report is missing an exact case or resource identity.");
        if (windows.GroupBy(w => w.Case.Repeat).Any(g => g.Select(w => (w.ResourceId, w.BootEpoch, w.CreationGeneration)).Distinct().Count() != 1))
            throw new InvalidDataException("Original and candidate windows of one repetition must share one owned disk.");
        var repeats = plan.Max(c => c.Repeat);
        var shapes = windows.GroupBy(w => (w.Case.QueueDepth, w.Case.Threads)).Select(g =>
        {
            var pairs = g.GroupBy(w => w.Case.Repeat).OrderBy(p => p.Key).Select(p =>
            {
                var original = p.SingleOrDefault(w => !w.Case.StreamWrites) ?? throw new InvalidDataException("Missing original window.");
                var candidate = p.SingleOrDefault(w => w.Case.StreamWrites) ?? throw new InvalidDataException("Missing candidate window.");
                return new
                {
                    Repeat = p.Key, OriginalMiBps = original.Score.MiBPerSecond, CandidateMiBps = candidate.Score.MiBPerSecond,
                    ChangePercent = 100 * (candidate.Score.MiBPerSecond / original.Score.MiBPerSecond - 1),
                    OriginalP99Ms = original.Score.WriteP99Milliseconds, CandidateP99Ms = candidate.Score.WriteP99Milliseconds,
                    candidate.StreamShare, StreamedWrites = candidate.After.Writes
                };
            }).ToArray();
            var originalMedian = Median(pairs.Select(p => p.OriginalMiBps));
            var candidateMedian = Median(pairs.Select(p => p.CandidateMiBps));
            var change = 100 * (candidateMedian / originalMedian - 1);
            var allUp = pairs.All(p => p.ChangePercent > 0);
            var separated = pairs.Min(p => p.CandidateMiBps) > pairs.Max(p => p.OriginalMiBps);
            var target = g.Key.QueueDepth == 1 && g.Key.Threads == 1;
            var outcome = target
                ? change >= TargetGainPercent && allUp && separated ? "WIN" : change >= TargetGainPercent ? "AMBIGUOUS" : "NO_WIN"
                : change <= ControlLossPercent && pairs.All(p => p.ChangePercent < 0) ? "LOSS" : "OK";
            return new
            {
                Shape = $"1M Q{g.Key.QueueDepth}T{g.Key.Threads} write", Role = target ? "target" : "control",
                OriginalMedianMiBps = originalMedian, CandidateMedianMiBps = candidateMedian, MedianChangePercent = change,
                OriginalRange = $"{pairs.Min(p => p.OriginalMiBps):F2}–{pairs.Max(p => p.OriginalMiBps):F2}",
                CandidateRange = $"{pairs.Min(p => p.CandidateMiBps):F2}–{pairs.Max(p => p.CandidateMiBps):F2}",
                Outcome = outcome, Pairs = pairs
            };
        }).OrderBy(s => s.Role == "target" ? 0 : 1).ToArray();
        var targetOutcome = shapes.Single(s => s.Role == "target").Outcome;
        var verdict = repeats < 3 ? "INCOMPLETE_FOR_ACCEPTANCE"
            : targetOutcome == "NO_WIN" ? "REJECT"
            : targetOutcome == "AMBIGUOUS" ? "AMBIGUOUS_ONE_MORE_BATCH"
            : shapes.Any(s => s.Outcome == "LOSS") ? "CONTROL_LOSS_REVISION_OR_REJECT" : "ACCEPT_SCREEN";
        RunStorage.AtomicJson(evidence + ".stream-summary.json", new
        {
            Contract = "Plan 118 screen: target median >= +5%, every pair up, candidate range above original range; " +
                       "controls fail only on a repeatable loss of 3% or more. Not a release qualification.",
            Verdict = verdict, Shapes = shapes, Cases = windows
        });
        var lines = new List<string>
        {
            "# RAM-disk streaming write copies: same-build comparison", "",
            $"Verdict: **{verdict}** (screen only; inspect raw evidence before acting).", "",
            "| Shape | Role | Original median / range MiB/s | Candidate median / range MiB/s | Change | Outcome |",
            "|---|---|---:|---:|---:|---|"
        };
        foreach (var s in shapes)
            lines.Add(FormattableString.Invariant($"| {s.Shape} | {s.Role} | {s.OriginalMedianMiBps:F2} / {s.OriginalRange} | {s.CandidateMedianMiBps:F2} / {s.CandidateRange} | {s.MedianChangePercent:+0.00;-0.00}% | {s.Outcome} |"));
        lines.AddRange(["", "| Shape | Repeat | Original | Candidate | Change | write p99 ms original / candidate | Streamed share / writes |",
            "|---|---:|---:|---:|---:|---:|---:|"]);
        static string Ms(double? value) => value?.ToString("F3", CultureInfo.InvariantCulture) ?? "N/A";
        foreach (var s in shapes)
        foreach (var p in s.Pairs)
            lines.Add(FormattableString.Invariant($"| {s.Shape} | {p.Repeat} | {p.OriginalMiBps:F2} | {p.CandidateMiBps:F2} | {p.ChangePercent:+0.00;-0.00}% | {Ms(p.OriginalP99Ms)} / {Ms(p.CandidateP99Ms)} | {p.StreamShare:P1} / {p.StreamedWrites} |"));
        File.WriteAllLines(evidence + ".stream-summary.md", lines);
        return verdict;
    }
    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
