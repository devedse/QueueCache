using System.Text.Json;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

/// <summary>One whole-file pass: its bytes and time, and what the cache did with it.</summary>
public sealed record RecallPass(string Id, string Phase, long Bytes, double Seconds, double MiBPerSecond,
    ulong HitBytes, ulong MissBytes, ulong? Fills, ulong? Recalled, ulong? Denied, ulong Evictions, ulong? LowerReads)
{
    public double HitPercent => HitBytes + MissBytes == 0 ? 0 : 100.0 * HitBytes / (HitBytes + MissBytes);
}

public static class CacheRecallEvidence
{
    /// <summary>Disk misses are intended; identity, health, the selected mode and complete byte
    /// accounting are required. Other readers (for example antivirus) may add bytes, never remove them.</summary>
    public static RecallPass Pass(string id, string phase, CacheLayoutSnapshot before, CacheLayoutSnapshot after,
        ReadPassResult pass, int mode)
    {
        var a = before.State; var b = after.State;
        if (!a.Operational || !b.Operational || a.Instance == 0 || a.Instance != b.Instance || a.Generation != b.Generation ||
            a.Errors != b.Errors || a.LastError != 0 || b.LastError != 0 || pass.Bytes <= 0 || pass.Seconds <= 0)
            throw new InvalidDataException("A recall pass changed the cache's identity or allocation, or hit an error.");
        if (before.Diagnostics.ReadRecall?.Mode != (ulong)mode || after.Diagnostics.ReadRecall?.Mode != (ulong)mode)
            throw new InvalidDataException($"The driver did not report read-recall mode {mode} throughout the pass.");
        if (b.ReadHitBytes < a.ReadHitBytes || b.ReadMissBytes < a.ReadMissBytes || b.Evictions < a.Evictions ||
            checked((b.ReadHitBytes - a.ReadHitBytes) + (b.ReadMissBytes - a.ReadMissBytes)) < (ulong)pass.Bytes)
            throw new InvalidDataException("A recall pass failed to account for all its bytes as RAM hits or disk misses.");
        static ulong? Delta(ulong? first, ulong? second) => first is { } x && second is { } y && y >= x ? y - x : null;
        return new(id, phase, pass.Bytes, pass.Seconds, pass.Bytes / 1048576.0 / pass.Seconds,
            b.ReadHitBytes - a.ReadHitBytes, b.ReadMissBytes - a.ReadMissBytes,
            Delta(before.Diagnostics.ReadFills, after.Diagnostics.ReadFills),
            Delta(before.Diagnostics.ReadRecall?.Recalled, after.Diagnostics.ReadRecall?.Recalled),
            Delta(before.Diagnostics.ReadRecall?.Denied, after.Diagnostics.ReadRecall?.Denied),
            b.Evictions - a.Evictions,
            Delta(before.Diagnostics.Attribution?.LowerReadAttempts, after.Diagnostics.Attribution?.LowerReadAttempts));
    }
}

public sealed partial class VerificationRunner
{
    private async Task<ReadPassResult> ReadPass(string id, IReadOnlyList<string> files, CancellationToken token)
    {
        var path = await Worker(Job("read-pass") with
        {
            Reply = storage.PathFor(id + ".pass.json"), WorkDirectory = workDirectory, Files = files.ToArray()
        }, token, 900);
        return JsonSerializer.Deserialize<ReadPassResult>(await File.ReadAllTextAsync(path, token))
            ?? throw new InvalidDataException("Missing read-pass result.");
    }

    /// <summary>cache-recall: how the clean list treats data that is read again, with QcLabReadRecall
    /// 0 (bimodal insertion) or 1 (read recall). Every pass reads whole files exactly once.
    /// reread: stale data fills the cache (read twice, so part of it was in use), then a fitting file
    /// is read four times. scan: a hot set in use, a scan larger than the cache read once, the hot set,
    /// the scan again, the hot set, and the scan a third time (a loop larger than the cache).</summary>
    private async Task<DiskSpdScore?> MeasureRecall(CacheExerciseCase scenario, CancellationToken token)
    {
        await Control(WriteCacheAction.LabDelay, token);
        await Control(WriteCacheAction.Flush, token);
        await Worker(Job("configure") with
        {
            Configuration = new CacheConfiguration(options.BudgetMiB, CachePreset.Fast, true)
            {
                Options = new CacheOptions(Drain: DrainAlgorithm.Deferred, HighPercent: 100, MaxDirtyAgeMs: 3600000)
            }
        }, token, 300);
        await Control(WriteCacheAction.LabReadRecall, token, (ulong)scenario.Recall);
        await Control(WriteCacheAction.Flush, token);
        await Control(WriteCacheAction.DropClean, token); // Also clears the recall history.
        await Control(WriteCacheAction.PerformanceTiming, token, 0);
        string Owned(string name) => Path.Combine(workDirectory, name);
        var passes = new List<RecallPass>();
        async Task Pass(string phase, string name)
        {
            var id = $"{scenario.Id}-{passes.Count + 1:D2}-{phase}";
            var before = await LayoutSnapshot(id + "-before.json", token);
            var pass = await ReadPass(id, [Owned(name)], token);
            var after = await LayoutSnapshot(id + "-after.json", token);
            passes.Add(CacheRecallEvidence.Pass(id, phase, before, after, pass, scenario.Recall));
            storage.Write(scenario.Id + "-passes.json", passes);
        }
        string scored;
        if (scenario.Workload == "reread")
        {
            await Pass("stale", "recall-stale.dat");
            await Pass("stale", "recall-stale.dat");
            await WaitForQuiet(scenario.Id + "-stale", token); // Also separates the two uses in time.
            for (var i = 0; i < 4; i++)
                await Pass("file", "recall-file.dat");
            scored = "recall-file.dat";
        }
        else
        {
            await Pass("hot", "recall-hot.dat");
            await Pass("hot", "recall-hot.dat");
            await WaitForQuiet(scenario.Id + "-hot", token);
            foreach (var phase in new[] { "scan", "hot", "scan", "hot", "scan" })
                await Pass(phase, phase == "scan" ? "recall-scan.dat" : "recall-hot.dat");
            scored = "recall-hot.dat";
        }
        // Speed afterwards at Q8. Disk misses are allowed: with bimodal insertion the file may still miss.
        var mib = CacheExercisePlan.RecallTargets(options.BudgetMiB).Single(t => t.Name == scored).MiB;
        var start = await LayoutSnapshot(scenario.Id + "-final-before.json", token);
        var score = await DiskTargets(scenario.Id + "-final-q8", [Owned(scored)],
            ["-b1M", $"-o{scenario.QueueDepth}", "-t1", "-w0", $"-f{mib}M", $"-d{scenario.Seconds}", "-W0"], token);
        var end = await LayoutSnapshot(scenario.Id + "-final-after.json", token);
        var final = CacheRecallEvidence.Pass(scenario.Id + "-final-q8", "final", start, end,
            new ReadPassResult(score.Bytes, score.Operations, score.Seconds), scenario.Recall);
        storage.Write(scenario.Id + "-recall.json", new
        {
            scenario.Recall, scenario.Workload, Passes = passes, Final = final,
            Measurement = "Whole-file Q1 passes (disk misses allowed) and a final Q8 window; not a RAM-only score"
        });
        return score;
    }
}
