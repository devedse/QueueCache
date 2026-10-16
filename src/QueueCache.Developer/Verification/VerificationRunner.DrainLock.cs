using System.Text.Json;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

public sealed partial class VerificationRunner
{
    private async Task<DiskSpdScore?> MeasureDrainLock(PerformanceCase scenario, CancellationToken token)
    {
        await Control(WriteCacheAction.LabDelay, token);
        await Control(WriteCacheAction.PerformanceTiming, token, 0);
        await Control(WriteCacheAction.Flush, token);
        await Worker(Job("configure") with
        {
            Configuration = new CacheConfiguration(2048, CachePreset.Fast)
            {
                Options = new CacheOptions(WritePercent: 50, RetainWrites: false, PromoteOnRead: scenario.Resident,
                    Drain: DrainAlgorithm.Eager, BatchKiB: 2048, Parallelism: scenario.Parallelism)
            }
        }, token, 300);
        await Control(WriteCacheAction.DropClean, token);
        if (scenario.Resident)
        {
            await Worker(Job("read-pass") with { WorkDirectory = workDirectory, Files = [Path.Combine(workDirectory, "hot.dat")] }, token, 180);
            var warm = await LayoutSnapshot(scenario.Id + "-warm.json", token);
            if (warm.State.CleanReadBytes < 512UL << 20) throw new IOException("Diagnostic hot file is not fully resident.");
        }
        await WaitForQuiet(scenario.Id, token);
        await Control(WriteCacheAction.PerformanceTiming, token, scenario.Timing ? 1UL : 0UL);
        var stop = storage.PathFor(scenario.Id + ".stop");
        var ready = storage.PathFor(scenario.Id + ".ready.json");
        using var children = CancellationTokenSource.CreateLinkedTokenSource(token);
        var telemetry = Worker(Job("telemetry") with { Reply = storage.PathFor(scenario.Id + "-telemetry.jsonl"),
            StopFile = stop, ReadyFile = ready, Seconds = 90 }, children.Token, 100);
        Task<DiskSpdScore>? writer = null;
        try
        {
            await TelemetryCoverage.WaitReadyAsync(ready, telemetry, TimeSpan.FromSeconds(45), token);
            var allStart = DateTimeOffset.UtcNow;
            if (scenario.Writer)
            {
                writer = Disk(scenario.Id + "-writer", Path.Combine(workDirectory, "writer.dat"),
                    ["-b1M", "-o8", "-t1", "-w100", "-d14", "-W0", "-Z1M"], children.Token, 60);
                var first = await LayoutSnapshot(scenario.Id + "-writer-start.json", token);
                var began = DateTimeOffset.UtcNow;
                for (var poll = 1; ; poll++)
                {
                    var active = await LayoutSnapshot(scenario.Id + $"-writer-ready{poll:D2}.json", token);
                    if (active.State.DrainedBytes >= first.State.DrainedBytes + (8UL << 20) && active.State.InFlightBytes > 0) break;
                    if (writer.IsCompleted || DateTimeOffset.UtcNow - began > TimeSpan.FromSeconds(5))
                        throw new IOException("Writer did not establish drain activity before the read window.");
                    await Task.Delay(100, token);
                }
            }
            var start = DateTimeOffset.UtcNow;
            var before = await LayoutSnapshot(scenario.Id + "-before.json", token);
            var score = await Disk(scenario.Id + "-reader", Path.Combine(workDirectory, scenario.Resident ? "hot.dat" : "resident.dat"),
                scenario.Resident ? ["-b1M", "-o8", "-t1", "-w0", "-d6", "-W0"] :
                    ["-b4K", "-r4K", "-o1", "-t1", "-w0", "-d6", "-W0", "-z150"], token, 60);
            var after = await LayoutSnapshot(scenario.Id + "-after.json", token);
            var end = DateTimeOffset.UtcNow;
            if (writer is not null && writer.IsCompleted)
                throw new IOException("Writer ended before the read envelope completed.");
            DiskSpdScore? writeScore = writer is null ? null : await writer;
            var allEnd = DateTimeOffset.UtcNow;
            File.WriteAllText(stop, "stop");
            await telemetry;
            var samples = File.ReadLines(storage.PathFor(scenario.Id + "-telemetry.jsonl"))
                .Select(line => JsonSerializer.Deserialize<TelemetrySnapshot>(line) ?? throw new InvalidDataException("Missing telemetry row.")).ToArray();
            TelemetryCoverage.Validate(samples.Select(s => s.Utc).ToArray(), allStart, allEnd);
            foreach (var s in samples)
                if (!s.State.Operational || s.State.Errors != before.State.Errors || s.State.LastError != 0 ||
                    s.State.Instance != before.State.Instance || s.State.Options != before.State.Options)
                    throw new IOException("Cache health/profile changed during diagnostic.");
            var overlapping = samples.Where(s => s.Utc >= start && s.Utc <= end).ToArray();
            var pendingShare = overlapping.Length == 0 ? 0 : overlapping.Count(s => s.State.DirtyBytes > 0) / (double)overlapping.Length;
            if (scenario.Writer && pendingShare < 0.75)
                throw new IOException("Drain activity did not cover at least 75% of reader-envelope telemetry observations.");
            var window = new DrainLockWindow(scenario, before, after, start, end, score, writeScore);
            var metrics = DrainLockDiagnostic.Metrics(window);
            storage.Write(scenario.Id + "-evidence.json", new { Window = window, Metrics = metrics,
                CounterScope = "Whole-reader-process envelope; global counters include every caller and observers. Lower wait sums concurrent drainers.",
                PendingSampleShare = pendingShare, TelemetryStart = allStart, TelemetryEnd = allEnd, MaximumSampleGapSeconds = 2 });
            return score;
        }
        finally
        {
            File.WriteAllText(stop, "stop"); children.Cancel();
            foreach (var child in new Task?[] { telemetry, writer })
                if (child is not null) try { await child; } catch (Exception) { /* original failure preserved */ }
        }
    }
}
