using System.ComponentModel;
using System.Text.Json;
using System.Runtime.Versioning;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

/// <summary>Guarded C: scenario; children are the runner's ordinary owned qcache workers.</summary>
[SupportedOSPlatform("windows")]
public static class SystemPagingOrdering
{
    public const string Suite = "system-paging-ordering";
    public const int Files = PagingWriteEvidence.Files, Seconds = 120;
    internal static string FileName(string work, int i) => Path.Combine(work, $"ordered-{i:D4}.bin");
    internal static byte[] Pattern(int i) => PagingWriteEvidence.Pattern(i, 0);

    public static async Task<IReadOnlyList<CheckResult>> RunAsync(DiskTarget target, string work, string output,
        Func<WorkerJob, CancellationToken, int, Task<string>> worker, CancellationToken token)
    {
        var start = Path.Combine(output, "paging-order.start"); var stop = Path.Combine(output, "paging-order.stop");
        var observerStop = Path.Combine(output, "paging-order-observer.stop");
        WorkerJob Job(string operation, string reply) => new(operation, target.Device, reply, target)
        {
            Suite = Suite, BudgetMiB = 2048, WorkDirectory = work, Seconds = Seconds,
            SystemInstance = target.Instance, SystemBytes = target.DiskBytes, RecoverableVm = true,
            StartFile = start, StopFile = stop
        };
        await worker(Job("paging-order-prepare", Path.Combine(output, "paging-order-prepared.json")), token, 900);
        var jobs = new List<Task<string>>();
        var observerReply = Path.Combine(output, "paging-order-telemetry.jsonl");
        var observerReady = observerReply + ".ready.json";
        var observer = worker(Job("telemetry", observerReply) with
            { StopFile = observerStop, ReadyFile = observerReady, Seconds = 300 }, token, 360);
        try
        {
            await TelemetryCoverage.WaitReadyAsync(observerReady, observer, TimeSpan.FromSeconds(30), token);
            for (ulong role = 0; role < 4; ++role)
            {
                var reply = Path.Combine(output, $"paging-order-role{role}.json"); var ready = reply + ".ready.json";
                var task = worker(Job("paging-order-pressure", reply) with { Value = role, ReadyFile = ready }, token, 240);
                jobs.Add(task);
                await TelemetryCoverage.WaitReadyAsync(ready, task, TimeSpan.FromSeconds(60), token);
            }
            var begin = TelemetryClock.Capture();
            File.WriteAllText(start, "start");
            var replies = await Task.WhenAll(jobs);
            var end = TelemetryClock.Capture();
            // The observer must capture a real trailing sample; never fill missing coverage.
            await Task.Delay(1000, token);
            File.WriteAllText(observerStop, "stop"); await observer;
            var rows = File.ReadLines(observerReply).Select(line => JsonSerializer.Deserialize<TelemetrySnapshot>(line)
                ?? throw new InvalidDataException("Missing paging observation.")).ToArray();
            var children = replies.Select(path => JsonSerializer.Deserialize<PagingPressureResult>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Missing pressure completion record.")).ToArray();
            var held = SystemPagingOrderEvidence.Validate(rows, begin, end, children);
            RunStorage.AtomicJson(Path.Combine(output, "paging-order-window.json"), new { Start = begin, End = end, Children = children, HeldBypassIntervals = held });
            await worker(Job("paging-order-finish", Path.Combine(output, "paging-order-bytes.json")) with
                { Value = checked((ulong)children.Single(c => c.Role == 3).Operations) }, token, 900);
            var a = rows[0].Diagnostics.PagingOrder!; var b = rows[^1].Diagnostics.PagingOrder!;
            return [new("system-paging-ordering/pressure-and-held-bypass", "PASS",
                $"{held} sampled intervals had page-file bypasses while earlier saves remained pending. " +
                $"Bypasses {b.Bypasses - a.Bypasses}, learning barriers {b.LearnBarriers - a.LearnBarriers}, " +
                $"unidentified barriers {b.UnidentifiedDirectBarriers - a.UnidentifiedDirectBarriers}; no safe overlap. " +
                $"Longest heartbeat gap {children.Single(c => c.Role == 2).LongestGapSeconds:F3}s; all {Files} files matched the lower disk.")];
        }
        finally
        {
            File.WriteAllText(stop, "stop"); File.WriteAllText(observerStop, "stop");
            // Only owned children are joined. A live kernel-blocked child prevents restoration.
            try { await Task.WhenAll(jobs.Append(observer)).WaitAsync(TimeSpan.FromSeconds(20)); }
            finally
            {
                await worker(Job("paging-order-release-delay", Path.Combine(output, "paging-order-delay-cleared.json")), CancellationToken.None, 180);
            }
        }
    }
    public static Task<int> WorkerAsync(WorkerJob job)
    {
        if (job.Operation == "paging-order-pressure")
        {
            RunStorage.AtomicJson(job.Reply, PagingPressureWorker.Run(job)); return Task.FromResult(0);
        }
        return PrepareOrFinishAsync(job);
    }
    private static async Task<int> PrepareOrFinishAsync(WorkerJob job)
    {
        var target = job.Expected ?? throw new IOException("No guarded target."); target.ValidateCurrent();
        using var device = new CacheDevice(target.Device, writable: true);
        if (device.GetDiagnostics().PagingOrder is null) throw new NotSupportedException("Update the loaded driver for system ordering.");
        if (job.Operation == "paging-order-release-delay")
        {
            device.Control(WriteCacheAction.LabDelay, value: 0);
            RunStorage.AtomicJson(job.Reply, new { DelayCleared = true }); return 0;
        }
        if (job.WorkDirectory is null || !string.Equals(Path.GetDirectoryName(job.WorkDirectory), target.Root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid owned C: workload directory.");
        if (job.Operation == "paging-order-prepare")
        {
            if (Directory.Exists(job.WorkDirectory)) throw new IOException("Owned directory already exists.");
            Directory.CreateDirectory(job.WorkDirectory);
            for (var i = 0; i < Files; ++i) File.WriteAllBytes(FileName(job.WorkDirectory, i), Pattern(i));
            using (var volume = new FileTests.CheckedVolume(target.Letter, target.Number, target.DiskBytes, true)) volume.Flush();
            device.Control(WriteCacheAction.Flush);
            ConfigurationManager.Apply(target, new CacheConfiguration(2048, CachePreset.Fast)
                { WriteOrdering = true, Options = new(Drain: DrainAlgorithm.Eager, BatchKiB: 2048, Parallelism: 8) }, true);
            var seeded = false;
            for (var attempt = 0; attempt < 3 && !seeded; ++attempt)
            {
                var sequence = device.GetDiagnostics().PagingOrder!.SeedSequence;
                var map = SpecialFileMap.PagingFiles(target);
                if (map.Ranges.Count == 0) throw new IOException("No paging-file extents to seed.");
                try { device.SetSpecialRanges(map.Ranges, SpecialRangeKind.SafePaging, sequence); seeded = true; }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1237) { await Task.Delay(100); }
            }
            if (!seeded) throw new IOException("Paging layout changed during every seed attempt.");
            device.Control(WriteCacheAction.LabDelay, value: 100);
            RunStorage.AtomicJson(job.Reply, new { State = device.GetWriteCacheState(), Diagnostics = device.GetDiagnostics(), Files });
            return 0;
        }
        if (job.Operation != "paging-order-finish") throw new IOException("Unknown paging-order step.");
        device.Control(WriteCacheAction.LabDelay, value: 0);
        using (var volume = new FileTests.CheckedVolume(target.Letter, target.Number, target.DiskBytes, true)) volume.Flush();
        device.Control(WriteCacheAction.Flush); device.Control(WriteCacheAction.Disable); device.Control(WriteCacheAction.Release);
        var writes = checked((long)job.Value);
        if (writes < Files) throw new IOException("Pressure writer did not replace every initial file pattern.");
        for (var i = 0; i < Files; ++i)
        {
            using var file = new AlignedFile(FileName(job.WorkDirectory, i), 4096, create: false);
            var actual = new byte[4096]; file.Read(0, actual);
            var generation = PagingWriteEvidence.LastGeneration(writes, i);
            if (!actual.AsSpan().SequenceEqual(PagingWriteEvidence.Pattern(i, generation)))
                throw new IOException($"Owned C: file {i} differs from acknowledged generation {generation} after lower flush/release.");
        }
        RunStorage.AtomicJson(job.Reply, new { VerifiedFiles = Files, AcknowledgedWrites = writes, State = device.GetWriteCacheState() });
        return 0;
    }
}
