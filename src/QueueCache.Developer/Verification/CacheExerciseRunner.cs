using System.Text.Json;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

public sealed partial class VerificationRunner
{
    private async Task<DiskSpdScore> DiskTargets(string id, IReadOnlyList<string> files, string[] arguments, CancellationToken token)
    {
        var result = await RunProcess(id, options.DiskSpd!, [.. arguments, "-Rxml", "-L", "-S", .. files],
            TimeSpan.FromSeconds(int.Parse(arguments.Single(a => a.StartsWith("-d", StringComparison.Ordinal))[2..],
                System.Globalization.CultureInfo.InvariantCulture) + 120), token);
        if (result.ExitCode != 0)
            throw new IOException($"DiskSpd XML-mode exit {result.ExitCode}: {ErrorDetail(result.Error)}.");
        var score = DiskSpdParser.Parse(result.Output);
        storage.Write(id + ".score.json", score);
        return score;
    }

    private async Task WarmExercise(string id, IReadOnlyList<string> targets, int perFileMiB, CancellationToken token)
    {
        var fileBytes = checked((ulong)perFileMiB * (ulong)targets.Count << 20);
        // Scan-resistant insertion can retain a previous working set. Repeated reads
        // may promote this one naturally; never clear the sustained cache to force it.
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var prefix = $"{id}-warm{attempt}";
            var first = await DiskTargets(prefix + "-fill", targets, ["-b1M", "-o8", "-t1", "-w0", $"-f{perFileMiB}M", "-d10", "-W0"], token);
            WarmResidentEvidence.ValidateFirstPass(first.Bytes, fileBytes);
            var before = await LayoutSnapshot(prefix + "-before.json", token);
            var warm = await DiskTargets(prefix + "-proof", targets, ["-b1M", "-o8", "-t1", "-w0", $"-f{perFileMiB}M", "-d3", "-W0"], token);
            var after = await LayoutSnapshot(prefix + "-after.json", token);
            try
            {
                WarmResidentEvidence.Validate(before.State, after.State, warm.Bytes, fileBytes);
                storage.Write(id + "-warm-summary.json", new { Attempts = attempt, FileBytes = fileBytes, Proven = true });
                return;
            }
            catch (InvalidDataException) when (attempt < 5 &&
                before.State.Operational && after.State.Operational && before.State.Instance != 0 &&
                before.State.Instance == after.State.Instance && before.State.Generation == after.State.Generation &&
                after.State.ReadMissBytes > before.State.ReadMissBytes &&
                before.State.Errors == after.State.Errors && after.State.LastError == 0)
            {
                // Preserve every failed residency proof; identity/error failures are never retried.
            }
        }
    }

    private async Task ExerciseMap(string id, CancellationToken token) =>
        await Worker(Job("layout-map") with { Reply = storage.PathFor(id + "-map.json") }, token);

    private async Task<DiskSpdScore?> MeasureExercise(CacheExerciseCase scenario, CancellationToken token)
    {
        var sustained = options.Suite == "cache-sustained";
        await Control(WriteCacheAction.LabDelay, token);
        if (!sustained || scenario.Repeat == 1)
        {
            await Control(WriteCacheAction.Flush, token);
            await Worker(Job("configure") with
            {
                Configuration = new CacheConfiguration(options.BudgetMiB, CachePreset.Fast, true)
                {
                    Options = new CacheOptions(Drain: scenario.BackgroundDrain ? sustained ? DrainAlgorithm.Idle : DrainAlgorithm.Eager : DrainAlgorithm.Deferred,
                        HighPercent: scenario.BackgroundDrain ? 80 : 100, MaxDirtyAgeMs: scenario.BackgroundDrain ? 5000 : 3600000)
                }
            }, token, 300);
            await Control(WriteCacheAction.Flush, token);
            await Control(WriteCacheAction.DropClean, token);
        }
        await Control(WriteCacheAction.PerformanceTiming, token, 0);
        var targets = Enumerable.Range(0, scenario.Streams).Select(i => Path.Combine(workDirectory, $"stream-{i}.dat")).ToArray();
        var perFileMiB = options.BudgetMiB / 2 / scenario.Streams;
        if (options.Suite == "cache-map-cost")
            await Disk(scenario.Id + "-fill", Path.Combine(workDirectory, "writer.dat"),
                ["-b1M", "-o8", "-t1", "-w0", $"-d{Math.Max(10, options.BudgetMiB / 128)}", "-W0"], token);
        if (!sustained)
            await WarmExercise(scenario.Id, targets, perFileMiB, token);
        if (sustained && scenario.Repeat == 1)
            await ResidentReread(scenario.Id + "-fresh", token);
        await WaitForQuiet(scenario.Id + "-start", token);
        await ExerciseMap(scenario.Id + "-before", token);
        var before = await LayoutSnapshot(scenario.Id + "-before.json", token);
        var stop = storage.PathFor(scenario.Id + ".stop");
        var ready = storage.PathFor(scenario.Id + ".ready.json");
        using var children = CancellationTokenSource.CreateLinkedTokenSource(token);
        var telemetry = Worker(Job("telemetry") with
        {
            Reply = storage.PathFor(scenario.Id + "-telemetry.jsonl"), StopFile = stop, ReadyFile = ready,
            Seconds = scenario.Seconds + 180
        }, children.Token, scenario.Seconds + 190);
        Task<string>? polling = null, oracle = null;
        try
        {
            await TelemetryCoverage.WaitReadyAsync(ready, telemetry, TimeSpan.FromSeconds(45), token);
            var pollReady = storage.PathFor(scenario.Id + "-poll.ready.json");
            if (scenario.MapIntervalMs > 0)
                polling = Worker(Job("map-poll") with
                {
                    Reply = storage.PathFor(scenario.Id + "-poll.jsonl"), StopFile = stop, ReadyFile = pollReady,
                    Value = (ulong)scenario.MapIntervalMs, Seconds = scenario.Seconds + 120
                }, children.Token, scenario.Seconds + 130);
            if (polling is not null)
                await TelemetryCoverage.WaitReadyAsync(pollReady, polling, TimeSpan.FromSeconds(45), token);
            var start = DateTimeOffset.UtcNow;
            if (sustained)
                oracle = Worker(Job("concurrent-oracle") with
                {
                    Reply = storage.PathFor(scenario.Id + "-oracle.json"), WorkDirectory = workDirectory,
                    Seconds = scenario.Seconds, Value = (ulong)scenario.Repeat
                }, children.Token, scenario.Seconds + 120);
            var arguments = new List<string> { scenario.Workload is "mixed" ? "-b64K" : scenario.Workload == "random-read" ? "-b4K" : "-b1M",
                $"-o{scenario.QueueDepth}", "-t1", scenario.Workload == "write" ? "-w100" : scenario.Workload == "mixed" ? "-w30" : "-w0",
                "-Z1M", $"-d{scenario.Seconds}", "-W0" };
            if (!sustained) arguments.Add($"-f{perFileMiB}M");
            if (scenario.Workload is "mixed" or "random-read") arguments.Add(scenario.Workload == "mixed" ? "-r64K" : "-r4K");
            var score = await DiskTargets(scenario.Id + "-workload", sustained ? [Path.Combine(workDirectory, "writer.dat")] : targets,
                arguments.ToArray(), children.Token);
            if (oracle is not null) await oracle;
            var end = DateTimeOffset.UtcNow;
            storage.Write(scenario.Id + "-interval.json", new { Start = start, End = end, MaximumSampleGapSeconds = 2 });
            File.WriteAllText(stop, "stop");
            await telemetry;
            if (polling is not null)
            {
                await polling;
                var polls = File.ReadAllLines(storage.PathFor(scenario.Id + "-poll.jsonl"))
                    .Select(line => JsonSerializer.Deserialize<ExercisePoll>(line) ?? throw new InvalidDataException("Missing map poll.")).ToArray();
                if (polls.Length < 2 || polls[0].Utc > start || polls[^1].Utc < end ||
                    polls.Any(p => p.Generation != before.State.Generation || p.Instance != before.State.Instance || p.Chunks != p.TotalChunks ||
                                   p.Errors != before.State.Errors || p.LastError != 0) ||
                    polls.Zip(polls.Skip(1)).Any(p => p.Second.Utc <= p.First.Utc ||
                        (p.Second.Utc - p.First.Utc).TotalMilliseconds > scenario.MapIntervalMs + 2000))
                    throw new InvalidDataException("Map polling did not cover the workload with a stable, complete cache allocation.");
            }
            var samples = File.ReadAllLines(storage.PathFor(scenario.Id + "-telemetry.jsonl"))
                .Select(line => JsonSerializer.Deserialize<ExerciseSample>(line) ?? throw new InvalidDataException("Missing telemetry sample.")).ToArray();
            TelemetryCoverage.Validate(samples.Select(s => s.Utc).ToArray(), start, end);
            if (samples.Any(s => s.State.Instance != before.State.Instance || s.State.Generation != before.State.Generation ||
                                 s.State.Errors != before.State.Errors || s.State.LastError != 0))
                throw new IOException("Cache identity or error state changed during the exercise.");
            var after = await LayoutSnapshot(scenario.Id + "-after.json", token);
            CacheExerciseEvidence.Validate(before, after, score.Bytes, scenario.Workload, scenario.BackgroundDrain);
            await ExerciseMap(scenario.Id + "-after", token);
            if (sustained)
            {
                // A real period of low usage precedes any explicit flush. Preserve both boundaries.
                await Task.Delay(TimeSpan.FromSeconds(20), token);
                await LayoutSnapshot(scenario.Id + "-idle.json", token);
                await ResidentReread(scenario.Id + "-reread", token);
                if (scenario.Repeat == 6)
                {
                    await Control(WriteCacheAction.Disable, token);
                    for (var round = 1; round <= 6; round++)
                        await Worker(Job("verify-concurrent-oracle") with
                        {
                            WorkDirectory = workDirectory, OraclePath = storage.PathFor(CacheExercisePlan.Cases(options)[round - 1].Id + "-oracle.json")
                        }, token);
                }
            }
            return score;
        }
        finally
        {
            File.WriteAllText(stop, "stop");
            children.Cancel();
            foreach (var child in new Task?[] { telemetry, polling, oracle })
                if (child is not null)
                    try { await child; } catch (Exception) { /* Retain the original failure. */ }
        }
    }

    private async Task ResidentReread(string id, CancellationToken token)
    {
        var files = new[] { Path.Combine(workDirectory, "resident.dat") };
        await WarmExercise(id, files, options.BudgetMiB / 2, token);
        await WaitForQuiet(id, token);
        await ExerciseMap(id, token);
        foreach (var depth in new[] { 1, 8 })
        {
            var before = await LayoutSnapshot($"{id}-q{depth}-before.json", token);
            var score = await DiskTargets($"{id}-q{depth}", files, ["-b1M", $"-o{depth}", "-t1", "-w0", $"-f{options.BudgetMiB / 2}M", "-d5", "-W0"], token);
            var after = await LayoutSnapshot($"{id}-q{depth}-after.json", token);
            CacheExerciseEvidence.Validate(before, after, score.Bytes, "read", false);
        }
    }

    private sealed record ExercisePoll(DateTimeOffset Utc, ulong Generation, ulong Instance, int Chunks, int TotalChunks, ulong Errors, int LastError);

    private sealed record ExerciseSample(DateTimeOffset Utc, WriteCacheState State);
}
