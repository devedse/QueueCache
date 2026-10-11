using System.Globalization;
using QueueCache.Management;

namespace QueueCache.Developer.Verification;

public sealed record RamCoordinationWindow(RamReadReferenceCase Case, Guid ResourceId,
    Guid BootEpoch, ulong CreationGeneration, RamCoordination Before, RamCoordination After,
    DiskSpdScore Score);

/// <summary>Reports perturbation separately from collection and preserves individual
/// timing populations. No sum of these metrics is declared total request latency.</summary>
public static class RamReadCoordination
{
    public static void Report(string evidence, IReadOnlyList<RamReadReferenceCase> plan,
        IReadOnlyList<RamCoordinationWindow> windows)
    {
        if (windows.Count != plan.Count || windows.Select(w => w.Case.Id).Distinct().Count() != plan.Count ||
            plan.Any(c => !windows.Any(w => w.Case == c && w.ResourceId != Guid.Empty && w.BootEpoch != Guid.Empty && w.CreationGeneration != 0)))
            throw new InvalidDataException("RAM coordination report is missing an exact case or resource identity.");
        var rows = windows.GroupBy(w => new { w.Case.Repeat, w.Case.QueueDepth, w.Case.Threads }).Select(g =>
        {
            var batch = g.ToArray();
            if (batch.Length != 3 || batch.Count(w => w.Case.CoordinationEnabled) != 1 ||
                !batch[0].Case.Id.EndsWith("off-before", StringComparison.Ordinal) ||
                !batch[2].Case.Id.EndsWith("off-after", StringComparison.Ordinal) ||
                batch.Select(w => (w.ResourceId, w.BootEpoch, w.CreationGeneration)).Distinct().Count() != 1)
                throw new InvalidDataException("RAM coordination off/on/off scope or order changed.");
            var first = batch[0].Score.MiBPerSecond; var last = batch[2].Score.MiBPerSecond;
            var on = batch[1].Score.MiBPerSecond; var data = batch[1].After;
            var outsideBrackets = on < Math.Min(first, last) * 0.98 || on > Math.Max(first, last) * 1.02;
            var offDrift = 100 * (last / first - 1);
            double? Us(ulong ticks, ulong count) => count == 0 ? null : 1e6 * ticks / count / data.Frequency;
            return new
            {
                g.Key, OffBeforeMiBps = first, OnMiBps = on, OffAfterMiBps = last,
                OffDriftPercent = offDrift, OnVsOffMeanPercent = 100 * (on / ((first + last) / 2) - 1),
                Reliability = outsideBrackets || Math.Abs(offDrift) > 2 ? "PERTURBATION_OR_DRIFT" : "WITHIN_DIAGNOSTIC_GUARD",
                data.PeakActive, data.Started, data.Completed, data.Splits, data.Posted, data.Taken, data.Withdrawn,
                HelperBytePercent = 100.0 * data.HelperBytes / data.SplitBytes,
                SampledDirectMicroseconds = Us(data.DirectTicks, data.DirectSamples),
                SampledPostingMicroseconds = Us(data.PostTicks, data.SampledSplits),
                SampledWithdrawalMicroseconds = Us(data.WithdrawTicks, data.SampledSplits),
                SampledFinalWaitMicroseconds = Us(data.WaitTicks, data.SampledSplits),
                SampledPostToTakeMicroseconds = Us(data.TakeTicks, data.TakeSamples),
                data.DirectSamples, data.SampledSplits, data.TakeSamples, data.HelperCpuMask
            };
        }).ToArray();
        RunStorage.AtomicJson(evidence + ".coordination-summary.json", new
        {
            Contract = "Off/on/off diagnostic; counters enclose process/warmup, not exact score windows. No speed acceptance.",
            Rows = rows, Cases = windows
        });
        var lines = new List<string>
        {
            "# RAM request/helper coordination", "",
            "Collection is not a speedup verdict. Timings have different sampled populations; do not sum them as request latency.", "",
            "| Shape | Off before / on / off after MiB/s | Guard | Peak reads | Helper bytes | Direct / post / withdraw / wait / post-to-take μs |",
            "|---|---:|---|---:|---:|---:|"
        };
        string F(double? value) => value?.ToString("F3", CultureInfo.InvariantCulture) ?? "N/A";
        foreach (var r in rows)
            lines.Add(FormattableString.Invariant($"| r{r.Key.Repeat} Q{r.Key.QueueDepth}T{r.Key.Threads} | {r.OffBeforeMiBps:F2} / {r.OnMiBps:F2} / {r.OffAfterMiBps:F2} | {r.Reliability} | {r.PeakActive} | {r.HelperBytePercent:F2}% | {F(r.SampledDirectMicroseconds)} / {F(r.SampledPostingMicroseconds)} / {F(r.SampledWithdrawalMicroseconds)} / {F(r.SampledFinalWaitMicroseconds)} / {F(r.SampledPostToTakeMicroseconds)} |"));
        File.WriteAllLines(evidence + ".coordination-summary.md", lines);
    }
}
