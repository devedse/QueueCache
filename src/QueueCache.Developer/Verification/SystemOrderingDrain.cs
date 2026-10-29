using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

public sealed record SystemDrainPoint(TelemetryClock Clock, DateTimeOffset Utc, WriteCacheState State,
    CacheDiagnostics Diagnostics, CachePerformance Performance);

/// <summary>Focused C: final-write-out diagnosis. No resets or delay/fault hooks.
/// An independent heartbeat and telemetry worker cover writing, explicit flush and deletion.</summary>
[SupportedOSPlatform("windows")]
public static class SystemOrderingDrain
{
    public const string Suite = "system-ordering-drain";
    public const int WriteSeconds = 300, ObserverSeconds = 1800;

    public static async Task<IReadOnlyList<CheckResult>> RunAsync(DiskTarget target, string work, string output,
        Func<WorkerJob, CancellationToken, int, Task<string>> worker, Action<string> progress, CancellationToken token)
    {
        WorkerJob Job(string operation, string reply) => new(operation, target.Device, Path.Combine(output, reply), target)
        {
            Suite = Suite, BudgetMiB = 2048, WorkDirectory = work,
            RecoverableVm = true, SystemInstance = target.Instance, SystemBytes = target.DiskBytes
        };
        await worker(Job("ordering-drain-prepare", "drain-prepared.json"), token, 180);
        var stop = Path.Combine(output, "drain-observers.stop");
        var telemetryPath = Path.Combine(output, "drain-telemetry.jsonl");
        var heartbeatPath = Path.Combine(output, "drain-heartbeat.jsonl");
        var telemetry = worker(Job("telemetry", "drain-telemetry.jsonl") with
            { StopFile = stop, ReadyFile = telemetryPath + ".ready", Seconds = ObserverSeconds }, token, ObserverSeconds + 30);
        var heartbeat = worker(Job("ordering-drain-heartbeat", "drain-heartbeat.jsonl") with
            { StopFile = stop, ReadyFile = heartbeatPath + ".ready", Seconds = ObserverSeconds }, token, ObserverSeconds + 30);
        TelemetryClock? begin = null, end = null;
        try
        {
            await TelemetryCoverage.WaitReadyAsync(telemetryPath + ".ready", telemetry, TimeSpan.FromSeconds(45), token);
            await TelemetryCoverage.WaitReadyAsync(heartbeatPath + ".ready", heartbeat, TimeSpan.FromSeconds(45), token);
            begin = TelemetryClock.Capture();
            progress("Writing fresh flushed files for five minutes; heartbeat and queue timing remain active through cleanup.");
            await worker(Job("ordering-drain-write", "drain-write.json"), token, WriteSeconds + 300);
            progress("Flushed-file writer ended; waiting for the explicit cache flush, then deleting only this run's payload.");
            await worker(Job("ordering-drain-finish", "drain-finished.json"), token, 900);
            end = TelemetryClock.Capture();
            RunStorage.AtomicJson(Path.Combine(output, "drain-window.json"), new { Begin = begin, End = end });
            await Task.Delay(1000, token);
        }
        finally
        {
            File.WriteAllText(stop, "stop");
            // Live owned processes prevent the runner's independent restoration.
            await Task.WhenAll(telemetry, heartbeat).WaitAsync(TimeSpan.FromSeconds(30));
        }
        var rows = File.ReadLines(telemetryPath).Select(l => JsonSerializer.Deserialize<TelemetrySnapshot>(l)
            ?? throw new InvalidDataException("Missing drain observation.")).ToArray();
        var beats = File.ReadLines(heartbeatPath).Select(l => JsonSerializer.Deserialize<HeartbeatSample>(l)
            ?? throw new InvalidDataException("Missing drain heartbeat.")).ToArray();
        TelemetryWorkingSet.Validate(JsonSerializer.Deserialize<TelemetryResidency>(File.ReadAllText(telemetryPath + ".residency.json"))
            ?? throw new InvalidDataException("Missing drain observer residency evidence."));
        // Always retain both independent diagnoses even when either coverage check fails.
        string? telemetryFailure = null, heartbeatFailure = null; double? longest = null;
        try { TelemetryCoverage.Validate(rows.Select(r => r.Clock).ToArray(), begin!, end!); }
        catch (InvalidDataException error) { telemetryFailure = error.Message; }
        try { longest = ProgressHeartbeat.Validate(beats, begin!, end!); }
        catch (InvalidDataException error) { heartbeatFailure = error.Message; }
        RunStorage.AtomicJson(Path.Combine(output, "drain-coverage.json"), new
        { TelemetrySamples = rows.Length, HeartbeatSamples = beats.Length, TelemetryFailure = telemetryFailure,
            HeartbeatFailure = heartbeatFailure, LongestHeartbeatSeconds = longest });
        if (telemetryFailure is not null || heartbeatFailure is not null)
            throw new InvalidDataException($"Final-write-out coverage failed. Telemetry: {telemetryFailure ?? "complete"}; heartbeat: {heartbeatFailure ?? "complete"}. See drain-coverage.json and raw samples.");
        if (rows.Any(r => r.State.Faulted || r.State.LastError != 0 || r.State.Errors != rows[0].State.Errors ||
            r.Performance.TimingEnabled != 1 || r.Diagnostics.FlushOrder?.Mode != 1 ||
            r.Diagnostics.PagingOrder is null || r.Diagnostics.PagingOrder.OverlapFound != 0))
            throw new InvalidDataException("Drain observations show cache errors, wrong ordering or missing/unsafe paging evidence.");
        return [new("system-ordering-drain/final-write-out-progress", "PASS",
            $"Full writing, explicit flush and cleanup coverage; heartbeat maximum {longest:F3}s. Diagnostic collection only, not an off/on cost or C: crash verdict.")];
    }

    public static async Task<int> WorkerAsync(WorkerJob job)
    {
        if (job.Operation == "ordering-drain-heartbeat")
        {
            if (job.StopFile is null || job.ReadyFile is null || job.Seconds != ObserverSeconds)
                throw new IOException("Invalid final-write-out heartbeat contract.");
            await ProgressHeartbeat.RecordAsync(job.Reply, job.StopFile, job.ReadyFile, job.Seconds);
            return 0;
        }
        var target = job.Expected ?? throw new IOException("Missing guarded drain target.");
        target.ValidateCurrent();
        if (job.WorkDirectory is null || !string.Equals(Path.GetDirectoryName(job.WorkDirectory), target.Root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid owned system-drain directory.");
        using var device = new CacheDevice(target.Device, writable: true);
        SystemDrainPoint Point() => new(TelemetryClock.Capture(), DateTimeOffset.UtcNow,
            device.GetWriteCacheState(), device.GetDiagnostics(), device.GetPerformance());
        if (job.Operation == "ordering-drain-prepare")
        {
            if (Directory.Exists(job.WorkDirectory)) throw new IOException("Drain payload already exists.");
            if (!new DriveInfo(target.Root).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Drain diagnosis requires NTFS.");
            if (new DriveInfo(target.Root).AvailableFreeSpace < 6L << 30) throw new IOException("Drain diagnosis requires 6 GiB free.");
            await FlushOrderCostScenario.RequireRuntimeConditions(job.Reply);
            var state = device.GetWriteCacheState(); ConfigurationManager.EnsureHealthy(state);
            var diagnostics = device.GetDiagnostics();
            if (diagnostics.PagingOrder is null || diagnostics.PagingAdmission?.ForceDirectRanges != 0 || diagnostics.LabGate?.State != 0)
                throw new IOException("Drain diagnosis needs current paging diagnostics and no lab hooks.");
            device.Control(WriteCacheAction.Flush);
            ConfigurationManager.Apply(target, new CacheConfiguration(2048, CachePreset.Fast)
            { WriteOrdering = true, Options = new(Drain: DrainAlgorithm.Eager, MaxDirtyAgeMs: 5000, BatchKiB: 2048, Parallelism: 8) }, true);
            device.Control(WriteCacheAction.DropClean);
            device.Control(WriteCacheAction.PerformanceTiming, value: 1);
            Directory.CreateDirectory(job.WorkDirectory);
            RunStorage.AtomicJson(job.Reply, Point()); return 0;
        }
        if (job.Operation == "ordering-drain-write")
        {
            RunStorage.AtomicJson(job.Reply + ".before.json", Point());
            var data = new byte[4096]; var clock = Stopwatch.StartNew(); long files = 0;
            while (clock.Elapsed.TotalSeconds < WriteSeconds)
            {
                using (var file = new FileStream(Path.Combine(job.WorkDirectory, $"f{files:D8}.bin"),
                    FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096))
                { file.Write(data); file.Flush(true); }
                ++files;
            }
            RunStorage.AtomicJson(job.Reply, new { Files = files, Seconds = clock.Elapsed.TotalSeconds, After = Point() });
            return 0;
        }
        if (job.Operation != "ordering-drain-finish") throw new IOException("Unknown guarded drain operation.");
        RunStorage.AtomicJson(job.Reply + ".flush-before.json", Point());
        device.Control(WriteCacheAction.Flush);
        RunStorage.AtomicJson(job.Reply + ".flush-after.json", Point());
        Directory.Delete(job.WorkDirectory, recursive: true);
        RunStorage.AtomicJson(job.Reply, Point());
        return 0;
    }
}
