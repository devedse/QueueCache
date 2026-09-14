using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

/// <summary>Foreground coordinator. Driver calls live in owned child processes; no benchmark logic in the CLI.</summary>
[SupportedOSPlatform("windows")]
public sealed class VerificationRunner(string executable, IReadOnlyList<string>? executablePrefix = null, string? leaseDirectory = null)
{
    private readonly IReadOnlyList<string> prefix = executablePrefix ?? [];
    private int sequence;
    private RunStorage storage = null!;
    private RecoverySnapshot original = null!;
    private VerificationOptions options = null!;
    private string workDirectory = "";
    private string LeaseDirectory => leaseDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "QueueCache", "Verification");

    private async Task<string> Worker(WorkerJob job, CancellationToken token, int timeoutSeconds = 120)
    {
        if (job.Operation is not ("control" or "configure" or "restore"))
            return await ExecuteWorker(job, token, timeoutSeconds);
        // Continue sampling while a management command is stuck, including during final recovery.
        var trace = storage.PathFor($"control-trace-{Interlocked.Increment(ref sequence):D5}");
        var stop = trace + ".stop";
        using var observerToken = new CancellationTokenSource();
        var observer = ExecuteWorker(job with { Operation = "telemetry", Reply = trace + ".jsonl", StopFile = stop,
            Seconds = timeoutSeconds + 10 }, observerToken.Token, timeoutSeconds + 20);
        try
        {
            var result = await ExecuteWorker(job, token, timeoutSeconds);
            File.WriteAllText(stop, "stop");
            await observer;
            return result;
        }
        finally
        {
            File.WriteAllText(stop, "stop");
            observerToken.Cancel();
            try { await observer; } catch (Exception) { /* primary error and raw trace retained */ }
        }
    }

    private async Task<string> ExecuteWorker(WorkerJob job, CancellationToken token, int timeoutSeconds = 120)
    {
        var id = $"worker-{Interlocked.Increment(ref sequence):D5}-{job.Operation}";
        job = job with { Reply = string.IsNullOrEmpty(job.Reply) ? storage.PathFor(id + ".reply.json") : job.Reply };
        var path = storage.PathFor(id + ".job.json");
        RunStorage.AtomicJson(path, job);
        var result = await OwnedProcess.RunAsync(executable, [.. prefix, "--verification-worker", path],
            storage.PathFor(id), TimeSpan.FromSeconds(timeoutSeconds), token);
        if (result.ExitCode != 0) throw new IOException($"Worker {job.Operation} failed ({result.ExitCode}); see {id}.stderr.txt and reply.");
        if (!File.Exists(job.Reply)) throw new InvalidDataException("Worker did not write its result: " + id);
        return job.Reply;
    }
    private WorkerJob Job(string operation) => new(operation, options.Volume, "", original.Target);
    private Task<string> Control(WriteCacheAction action, CancellationToken token, ulong value = 0) =>
        Worker(Job("control") with { Action = action, Value = value }, token, 180);

    public async Task<int> RunAsync(VerificationOptions selected, IProgress<string>? progress, CancellationToken token)
    {
        VerificationPlan.Validate(selected);
        options = selected with { Output = Path.GetFullPath(selected.Output), DiskSpd = selected.DiskSpd is null ? null : Path.GetFullPath(selected.DiskSpd) };
        storage = new RunStorage(options.Output);
        progress?.Report("Run directory: " + storage.DirectoryPath);
        // An open lock prevents a second coordinator/recovery process from owning this run concurrently.
        using var runLock = new FileStream(storage.PathFor("run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var performance = options.Suite is "performance" or "full" or "flush-interference" ? VerificationPlan.Performance(options) : [];
        var expected = new List<string>();
        if (options.Suite is "quick" or "full") expected.Add("file-integrity");
        if (options.Suite is "policies" or "full") expected.Add("policy-integrity");
        expected.AddRange(performance.Select(c => c.Id));
        storage.Write("manifest.json", new { SchemaVersion = 1, PlanVersion = VerificationPlan.Version, Options = options,
            ExpectedCases = expected, Provenance = VerificationWorker.Provenance(executable),
            DiskSpdSha256 = options.DiskSpd is null ? null : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(options.DiskSpd))) });
        storage.Write("status.json", new { Status = "RUNNING", Started = DateTimeOffset.UtcNow });
        storage.Write("results.json", storage.Results);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(options.DeadlineMinutes));
        string? failure = null, restorationFailure = null;
        var captured = false;
        FileStream? diskLease = null;
        try
        {
            var recovery = await Worker(new("capture", options.Volume, storage.PathFor("recovery.json")), deadline.Token);
            original = JsonSerializer.Deserialize<RecoverySnapshot>(await File.ReadAllTextAsync(recovery, deadline.Token))!;
            var leases = LeaseDirectory;
            Directory.CreateDirectory(leases);
            diskLease = new FileStream(Path.Combine(leases, original.Target.Device + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            captured = true;
            workDirectory = Path.Combine(original.Target.Root, Path.GetFileName(storage.DirectoryPath));
            storage.Write("workloads.json", new { Directory = workDirectory, Retained = true });
            foreach (var test in expected.TakeWhile(id => id is "file-integrity" or "policy-integrity"))
                await Case(test, async () => { await Worker(Job(test == "file-integrity" ? "files" : "policies"), deadline.Token, 900); return null; }, progress);
            if (performance.Count > 0)
            {
                await Worker(Job("prepare") with { WorkDirectory = workDirectory, BudgetMiB = options.BudgetMiB }, deadline.Token, 900);
                foreach (var scenario in performance)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    await Case(scenario.Id, () => Measure(scenario, deadline.Token), progress);
                }
            }
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            if (captured)
            {
                progress?.Report("Restoring original runtime state (independent cleanup deadline).");
                try { OwnedProcess.EnsureStopped(storage.DirectoryPath); await Worker(Job("restore") with { Recovery = storage.PathFor("recovery.json"),
                    Reply = storage.PathFor("restored.json") }, CancellationToken.None, 300); }
                catch (Exception ex) { restorationFailure = ex.ToString(); }
            }
            diskLease?.Dispose();
        }
        var complete = failure is null && restorationFailure is null && RunStorage.Complete(expected, storage.Results);
        var status = complete ? "COMPLETED" : restorationFailure is not null ? "RESTORATION_FAILED" :
            token.IsCancellationRequested ? "CANCELLED" : "INCOMPLETE";
        if (complete && performance.Count > 0)
        {
            static double Median(IEnumerable<double> values)
            {
                var sorted = values.Order().ToArray();
                return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
            }
            storage.Write("aggregates.json", performance.GroupBy(c => new { c.Workload, c.Allocation, c.Drain, c.DelayMs, c.QueueDepth, c.Writer, c.ApplicationFlush })
                .Select(g => new { Configuration = g.Key, Samples = g.Count(),
                    IopsMedian = Median(g.Select(c => storage.Results.Single(r => r.Id == c.Id).Score!.Iops)),
                    IopsMin = g.Min(c => storage.Results.Single(r => r.Id == c.Id).Score!.Iops),
                    IopsMax = g.Max(c => storage.Results.Single(r => r.Id == c.Id).Score!.Iops),
                    CaseIds = g.Select(c => c.Id).ToArray() }).ToArray());
        }
        storage.Write("status.json", new { Status = status, Finished = DateTimeOffset.UtcNow, Expected = expected.Count,
            Collected = storage.Results.Count, Failure = failure, RestorationFailure = restorationFailure });
        var report = new StringBuilder($"# QueueCache verification\n\nStatus: **{status}**\n\nCases: {storage.Results.Count}/{expected.Count}. Plan version: {VerificationPlan.Version}.\n\n");
        report.AppendLine("PASS means the case's collection/checks succeeded, not that latency met a performance target or that every driver path is verified. No medians are computed from partial runs.\n");
        report.AppendLine("| Case | Status | IOPS | Read p99 ms |\n|---|---|---:|---:|");
        foreach (var row in storage.Results) report.AppendLine($"| {row.Id} | {row.Status} | {row.Score?.Iops.ToString("F2", CultureInfo.InvariantCulture) ?? "—"} | {row.Score?.ReadP99Milliseconds?.ToString("F3", CultureInfo.InvariantCulture) ?? "N/A"} |");
        if (failure is not null) report.AppendLine("\n## Failure\n\n```text\n" + failure + "\n```");
        if (restorationFailure is not null) report.AppendLine("\n## Restoration failure\n\n```text\n" + restorationFailure + "\n```");
        File.WriteAllText(storage.PathFor("SUMMARY.md"), report.ToString());
        var csv = new StringBuilder("CaseId,Status,Seconds,Operations,IOPS,ReadP99Ms,ReadP999Ms,ReadMaxMs\n");
        foreach (var r in storage.Results)
            csv.AppendLine(string.Join(',', r.Id, r.Status, r.Seconds.ToString("R", CultureInfo.InvariantCulture),
                r.Score?.Operations.ToString(CultureInfo.InvariantCulture) ?? "", r.Score?.Iops.ToString("R", CultureInfo.InvariantCulture) ?? "",
                r.Score?.ReadP99Milliseconds?.ToString("R", CultureInfo.InvariantCulture) ?? "",
                r.Score?.ReadP999Milliseconds?.ToString("R", CultureInfo.InvariantCulture) ?? "",
                r.Score?.ReadMaxMilliseconds?.ToString("R", CultureInfo.InvariantCulture) ?? ""));
        File.WriteAllText(storage.PathFor("results.csv"), csv.ToString());
        File.WriteAllText(storage.PathFor("FINISHED.txt"), $"{status}\nFinished UTC: {DateTimeOffset.UtcNow:O}\nResults: {storage.DirectoryPath}\n");
        progress?.Report(status + ": " + storage.PathFor("SUMMARY.md"));
        return complete ? 0 : token.IsCancellationRequested ? 130 : 1;
    }

    private async Task Case(string id, Func<Task<DiskSpdScore?>> run, IProgress<string>? progress)
    {
        progress?.Report("Starting " + id);
        storage.Write("status.json", new { Status = "RUNNING", CurrentCase = id, CompletedCases = storage.Results.Count, Updated = DateTimeOffset.UtcNow });
        var started = DateTimeOffset.UtcNow; var timer = Stopwatch.StartNew();
        try { var score = await run(); storage.Add(new(id, score is null ? "PASS" : "MEASURED", "Case completed; raw evidence retained. Performance thresholds require comparison.", started, timer.Elapsed.TotalSeconds, score)); }
        catch (Exception ex) { storage.Add(new(id, "FAIL", ex.Message, started, timer.Elapsed.TotalSeconds)); throw; }
    }

    private async Task<DiskSpdScore> Disk(string id, string file, string[] arguments, CancellationToken token)
    {
        // Every command uses the same typed argument vector; shell parsing and automatic $args cannot interfere.
        var result = await OwnedProcess.RunAsync(options.DiskSpd!, [.. arguments, "-Rxml", "-L", "-S", file],
            storage.PathFor(id), TimeSpan.FromSeconds(int.Parse(arguments.Single(a => a.StartsWith("-d", StringComparison.Ordinal))[2..], CultureInfo.InvariantCulture) + 120), token);
        if (result.ExitCode != 0) throw new IOException($"DiskSpd XML-mode exit {result.ExitCode}; both supported variants must return zero in XML mode. See {id}.stderr.txt and stdout.txt.");
        var score = DiskSpdParser.Parse(result.Output);
        storage.Write(id + ".score.json", score);
        return score;
    }

    private async Task<DiskSpdScore?> Measure(PerformanceCase scenario, CancellationToken token)
    {
        await Control(WriteCacheAction.LabDelay, token);
        await Worker(Job("configure") with { Configuration = new CacheConfiguration(options.BudgetMiB, CachePreset.Fast, scenario.Drain != "Off")
            { Options = new CacheOptions(Enum.Parse<CacheAllocation>(scenario.Allocation), 50,
                Drain: scenario.Drain == "Off" ? DrainAlgorithm.Eager : Enum.Parse<DrainAlgorithm>(scenario.Drain)) } }, token, 300);
        await Control(WriteCacheAction.Flush, token);
        await Control(WriteCacheAction.DropClean, token);
        await Control(WriteCacheAction.PerformanceTiming, token, 1);
        var hot = Path.Combine(workDirectory, "hot.dat");
        if (scenario.Workload == "interference")
        {
            await Disk(scenario.Id + "-warm", hot, ["-b1M", "-o4", "-t1", "-w0", $"-d{Math.Max(4, options.BudgetMiB / 64)}", "-W0"], token);
            var warmPath = await Worker(Job("snapshot"), token);
            using var warm = JsonDocument.Parse(await File.ReadAllTextAsync(warmPath, token));
            var resident = warm.RootElement.GetProperty("State").Deserialize<WriteCacheState>()!;
            if (resident.CleanReadBytes + resident.CleanWriteBytes < ((ulong)options.BudgetMiB / 4 << 20))
                throw new IOException("Warm hot set is not fully resident; do not interpret this as a cache-hit interference test.");
        }
        await Control(WriteCacheAction.LabDelay, token, (ulong)scenario.DelayMs);
        await Worker(Job("snapshot") with { Reply = storage.PathFor(scenario.Id + "-before.json") }, token);
        var stop = storage.PathFor(scenario.Id + ".stop");
        using var children = CancellationTokenSource.CreateLinkedTokenSource(token);
        var telemetry = Worker(Job("telemetry") with { Reply = storage.PathFor(scenario.Id + "-telemetry.jsonl"),
            StopFile = stop, Seconds = options.DurationSeconds + 120 }, children.Token, options.DurationSeconds + 130);
        Task<DiskSpdScore>? writer = null;
        Task<string>? flush = null;
        try
        {
            if (scenario.Writer)
            {
                writer = Disk(scenario.Id + "-writer", Path.Combine(workDirectory, "writer.dat"),
                    ["-b1M", $"-o{scenario.QueueDepth}", "-t1", "-w100", $"-d{options.DurationSeconds + 6}", "-W0", "-Zr"], children.Token);
                await Task.Delay(TimeSpan.FromSeconds(3), token);
                if (scenario.ApplicationFlush)
                    flush = Worker(Job("application-flush") with { StopFile = stop, WorkDirectory = workDirectory,
                        Seconds = options.DurationSeconds }, children.Token, options.DurationSeconds + 120);
            }
            var arguments = scenario.Workload == "interference" ? new List<string> { "-b4K", "-r4K", "-o8", "-t2", "-w0" } :
                new List<string> { scenario.Workload.StartsWith("sequential") ? "-b1M" : "-b4K", $"-o{scenario.QueueDepth}", "-t1",
                    scenario.Workload.EndsWith("write") ? "-w100" : scenario.Workload == "mixed" ? "-w30" : "-w0", "-Zr" };
            if (scenario.Workload.StartsWith("random") || scenario.Workload == "mixed") arguments.Add("-r4K");
            arguments.AddRange([$"-d{options.DurationSeconds}", "-W0"]);
            var score = await Disk(scenario.Id + "-reader", scenario.Workload == "interference" ? hot : Path.Combine(workDirectory, "writer.dat"),
                arguments.ToArray(), children.Token);
            if (writer is not null) await writer;
            if (flush is not null) await flush;
            File.WriteAllText(stop, "stop");
            await telemetry;
            var lines = File.ReadAllLines(storage.PathFor(scenario.Id + "-telemetry.jsonl"));
            if (lines.Length < 2) throw new InvalidDataException("Insufficient telemetry.");
            foreach (var line in lines)
            {
                using var sample = JsonDocument.Parse(line);
                var state = sample.RootElement.GetProperty("State").Deserialize<WriteCacheState>()!;
                if (state.Errors != original.State.Errors || state.LastError != 0 || state.Instance != original.State.Instance)
                    throw new IOException("Driver error or instance change during measurement.");
            }
            await Worker(Job("snapshot") with { Reply = storage.PathFor(scenario.Id + "-after.json") }, token);
            return score;
        }
        finally
        {
            File.WriteAllText(stop, "stop");
            children.Cancel();
            // Observe all child completions before restoring or starting another case.
            foreach (var child in new Task?[] { telemetry, writer, flush })
                if (child is not null) { try { await child; } catch (Exception) { /* original case failure retained */ } }
        }
    }

    public async Task<int> RecoverAsync(string directory, CancellationToken token)
    {
        var path = Path.GetFullPath(directory);
        using var runLock = new FileStream(Path.Combine(path, "run.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        OwnedProcess.EnsureStopped(path);
        var snapshotPath = Path.Combine(path, "recovery.json");
        original = JsonSerializer.Deserialize<RecoverySnapshot>(await File.ReadAllTextAsync(snapshotPath, token))!;
        var leases = LeaseDirectory;
        Directory.CreateDirectory(leases);
        using var diskLease = new FileStream(Path.Combine(leases, original.Target.Device + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        options = new($"{original.Target.Letter}:", Output: path);
        storage = new RunStorage(path);
        await Worker(Job("restore") with { Recovery = snapshotPath }, token, 300);
        storage.Write("recovery-result.json", new { Status = "RESTORED", At = DateTimeOffset.UtcNow });
        return 0;
    }
}
