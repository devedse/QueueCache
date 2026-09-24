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
    private DiskTarget? fileTarget;
    private VerificationOptions options = null!;
    private string workDirectory = "";
    private readonly object logGate = new();
    private IProgress<string>? progressSink;
    private string progressLabel = "Preflight";
    private void Log(string message)
    {
        lock (logGate)
        {
            var line = $"[{DateTimeOffset.UtcNow:O}] [{progressLabel}] {message}";
            File.AppendAllText(storage.PathFor("run.log"), line + Environment.NewLine);
            progressSink?.Report(line);
        }
    }

    private async Task<ProcessResult> RunProcess(string id, string tool, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken token)
    {
        Log($"Starting {id}; timeout {timeout.TotalSeconds:F0}s. Raw output: {storage.PathFor(id)}.*");
        var watch = Stopwatch.StartNew();
        var process = OwnedProcess.RunAsync(tool, arguments, storage.PathFor(id), timeout, token);
        using var heartbeatStop = new CancellationTokenSource();
        async Task Heartbeat()
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
                while (await timer.WaitForNextTickAsync(heartbeatStop.Token))
                    Log($"Still running {id} ({watch.Elapsed.TotalSeconds:F0}s); waiting for child process. This is not a progress percentage.");
            }
            catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested) { }
        }
        var heartbeat = Heartbeat();
        try
        {
            var result = await process;
            Log($"Finished {id}: exit {result.ExitCode}, {watch.Elapsed.TotalSeconds:F1}s.");
            return result;
        }
        finally { heartbeatStop.Cancel(); await heartbeat; }
    }

    private static string ErrorDetail(string error) => error.Length > 4096 ? error[..4096] + " [truncated; see raw stderr]" : error.Trim();
    private string LeaseDirectory => leaseDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "QueueCache", "Verification");
    private static bool IsSystemSuite(string suite) => suite is
        "system-preflight" or "system-files" or "system-post-restart" or "system-image-baseline" or "system-active-image";
    public static bool IsSystemRecoveryTarget(DiskTarget target) =>
        target.Letter == 'C' && target.IsBoot && target.IsSystem;
    private static string SystemLeaseName(DiskTarget target) => "Global\\QueueCache-SystemVerify-" + target.Device;

    private async Task<string> Worker(WorkerJob job, CancellationToken token, int timeoutSeconds = 120)
    {
        if (job.Operation is not ("control" or "configure" or "restore"))
            return await ExecuteWorker(job, token, timeoutSeconds);
        // Continue sampling while a management command is stuck, including during final recovery.
        var trace = storage.PathFor($"control-trace-{Interlocked.Increment(ref sequence):D5}");
        var stop = trace + ".stop";
        var ready = trace + ".ready.json";
        using var observerToken = new CancellationTokenSource();
        var observer = ExecuteWorker(job with
        {
            Operation = "telemetry",
            Reply = trace + ".jsonl",
            StopFile = stop,
            ReadyFile = ready,
            Seconds = timeoutSeconds + 60
        }, observerToken.Token, timeoutSeconds + 70);
        try
        {
            await TelemetryCoverage.WaitReadyAsync(ready, observer, TimeSpan.FromSeconds(45), token);
            var result = await ExecuteWorker(job, token, timeoutSeconds);
            File.WriteAllText(stop, "stop");
            await observer;
            return result;
        }
        finally
        {
            File.WriteAllText(stop, "stop");
            observerToken.Cancel();
            try
            {
                await observer;
            }
            catch (Exception) { /* primary error and raw trace retained */ }
        }
    }

    private async Task<string> ExecuteWorker(WorkerJob job, CancellationToken token, int timeoutSeconds = 120)
    {
        var id = $"worker-{Interlocked.Increment(ref sequence):D5}-{job.Operation}";
        job = job with
        {
            Reply = string.IsNullOrEmpty(job.Reply) ? storage.PathFor(id + ".reply.json") : job.Reply
        };
        var path = storage.PathFor(id + ".job.json");
        RunStorage.AtomicJson(path, job);
        var result = await RunProcess(id, executable, [.. prefix, "--verification-worker", path],
            TimeSpan.FromSeconds(timeoutSeconds), token);
        if (result.ExitCode != 0)
            throw new IOException($"Worker {job.Operation} failed ({result.ExitCode}): {ErrorDetail(result.Error)}. Full error: {storage.PathFor(id + ".stderr.txt")}");
        if (!File.Exists(job.Reply))
            throw new InvalidDataException("Worker did not write its result: " + id);
        return job.Reply;
    }
    private WorkerJob Job(string operation) => new(operation, options.Volume, "", fileTarget ?? original.Target);

    public static (string WorkDirectory, string OracleFile) SystemImageArtifacts(string workDirectory, string caseId)
    {
        if (string.IsNullOrWhiteSpace(workDirectory) ||
            !caseId.StartsWith("system-active-image-", StringComparison.Ordinal) ||
            Path.GetFileName(caseId) != caseId)
            throw new ArgumentException("Expected a system-active-image case ID and workload root.");
        return (workDirectory + "-" + caseId, caseId + ".oracle.json");
    }
    public static string[] PassedSystemImageOracles(string runDirectory, IEnumerable<CaseResult> results) =>
        results.Where(result => result.Id.StartsWith("system-active-image-", StringComparison.Ordinal) &&
                                result.Status == "PASS")
            .Select(result => Path.Combine(runDirectory, SystemImageArtifacts(runDirectory, result.Id).OracleFile))
            .ToArray();
    private Task<string> Control(WriteCacheAction action, CancellationToken token, ulong value = 0) =>
        Worker(Job("control") with
        {
            Action = action,
            Value = value
        }, token, action == WriteCacheAction.Flush ? options.PreparationFlushSeconds : 180);

    public async Task<int> RunAsync(VerificationOptions selected, IProgress<string>? progress, CancellationToken token)
    {
        VerificationPlan.Validate(selected);
        fileTarget = null;
        if (IsSystemSuite(selected.Suite))
        {
            var outputVolume = SystemPreflightGuard.OutputVolume(selected.Output);
            var system = await DiskTarget.InspectAsync(selected.Volume, token);
            var output = await DiskTarget.InspectAsync(outputVolume, token);
            system.ValidateCurrent(token);
            output.ValidateCurrent(token);
            SystemPreflightGuard.ValidateTargets(system, output, selected.SystemInstance!, selected.SystemBytes!.Value);
            if (selected.Suite == "system-post-restart")
            {
                var oraclePath = Path.GetFullPath(selected.OraclePath!);
                var oracleVolume = SystemPreflightGuard.OutputVolume(Path.GetDirectoryName(oraclePath)!);
                var oracleDisk = await DiskTarget.InspectAsync(oracleVolume, token);
                oracleDisk.ValidateCurrent(token);
                SystemPreflightGuard.ValidateTargets(system, oracleDisk, selected.SystemInstance!, selected.SystemBytes!.Value);
                SystemPreflightGuard.ValidateRecordedTarget(
                    SystemFileScenarios.ReadOracle(oraclePath).Target, system);
            }
            fileTarget = system;
        }
        options = selected with
        {
            Output = Path.GetFullPath(selected.Output),
            DiskSpd = selected.DiskSpd is null ? null : Path.GetFullPath(selected.DiskSpd),
            OraclePath = selected.OraclePath is null ? null : Path.GetFullPath(selected.OraclePath)
        };
        storage = new RunStorage(options.Output);
        progressSink = progress;
        // An open lock prevents a second coordinator/recovery process from owning this run concurrently.
        using var runLock = new FileStream(storage.PathFor("run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var performance = options.Suite is "performance" or "full" or "flush-interference" or "write-performance" ? VerificationPlan.Performance(options) : [];
        var drainDecision = VerificationPlan.DrainDecision(options);
        var integrity = VerificationPlan.Integrity(options);
        var expected = integrity.Select(test => test.Id).ToList();
        expected.AddRange(performance.Select(c => c.Id));
        expected.AddRange(drainDecision.Select(c => c.Id));
        totalCases = expected.Count;
        progressLabel = $"Preflight | 0/{totalCases} completed";
        Log("Run directory: " + storage.DirectoryPath);
        Log($"Suite {options.Suite}; target {options.Volume}; overall limit: {(options.DeadlineMinutes == 0 ? "unlimited" : options.DeadlineMinutes + " minutes")}. Per-operation timeouts remain enabled.");
        if (!IsSystemSuite(options.Suite))
            Log($"Preparation flush deadline: {options.PreparationFlushSeconds}s; measurement windows and independent restoration deadline unchanged.");
        else
            Log(options.Suite == "system-active-image" ? "Bounded active C: image phase; runtime-only 256..512 MiB Fast and Strict cases with independent restoration." :
                options.Suite == "system-image-baseline" ? "Bounded uncached C: image baseline; cache configuration remains disabled and released." :
                options.Suite == "system-files" ? "Bounded owned-file phase; no cache configuration, faults, TRIM or reboot." :
                "Read-only system-disk phase; no workload or cache configuration action.");
        if (options.CaseFilter is not null)
            Log($"Selected case ID substring: {options.CaseFilter}; {performance.Count + drainDecision.Count} cases, not the complete suite matrix.");
        storage.Write("manifest.json", new
        {
            SchemaVersion = 1,
            PlanVersion = VerificationPlan.Version,
            Options = options,
            ExpectedCases = expected,
            PerformanceCases = performance,
            DrainDecisionCases = drainDecision,
            Provenance = VerificationWorker.Provenance(executable),
            DiskSpdSha256 = options.DiskSpd is null ? null : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(options.DiskSpd)))
        });
        storage.Write("status.json", new
        {
            Status = "RUNNING",
            Started = DateTimeOffset.UtcNow
        });
        storage.Write("results.json", storage.Results);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (options.DeadlineMinutes > 0)
            deadline.CancelAfter(TimeSpan.FromMinutes(options.DeadlineMinutes));
        string? failure = null, restorationFailure = null;
        var captured = false;
        var systemCaptured = false;
        FileStream? diskLease = null;
        Semaphore? systemLease = null;
        var ownsSystemLease = false;
        try
        {
            DiskTarget target;
            if (IsSystemSuite(options.Suite))
            {
                target = fileTarget!;
                // Own the system disk before taking the recovery snapshot so no
                // competing guarded run can change the state between capture and use.
                // Semaphore release is not thread-affine: async continuations may
                // resume on a different thread after an owned worker completes.
                systemLease = new Semaphore(1, 1, SystemLeaseName(target));
                ownsSystemLease = systemLease.WaitOne(0);
                if (!ownsSystemLease)
                    throw new IOException("Another guarded system-disk verification owns this physical disk.");
                if (options.Suite == "system-active-image")
                {
                    await Worker(Job("system-capture") with
                    {
                        Reply = storage.PathFor("recovery.json"),
                        SystemInstance = options.SystemInstance,
                        SystemBytes = options.SystemBytes,
                        RecoverableVm = options.RecoverableVm
                    }, deadline.Token, 120);
                    systemCaptured = true;
                }
                else
                    storage.Write("restoration.json", new { Required = false, Reason = "System suite does not change cache configuration or hooks." });
            }
            else if (options.Suite == "trim-file")
            {
                var identity = await Worker(new("capture-file", options.Volume, storage.PathFor("target.json")), deadline.Token);
                target = fileTarget = JsonSerializer.Deserialize<DiskTarget>(await File.ReadAllTextAsync(identity, deadline.Token))!;
                storage.Write("restoration.json", new { Required = false, Reason = "File-only probe; no cache configuration or driver access." });
            }
            else
            {
                var recovery = await Worker(new("capture", options.Volume, storage.PathFor("recovery.json")), deadline.Token);
                original = JsonSerializer.Deserialize<RecoverySnapshot>(await File.ReadAllTextAsync(recovery, deadline.Token))!;
                target = original.Target;
            }
            if (!IsSystemSuite(options.Suite))
            {
                var leases = LeaseDirectory;
                Directory.CreateDirectory(leases);
                diskLease = new FileStream(Path.Combine(leases, target.Device + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            captured = options.Suite != "trim-file" && !IsSystemSuite(options.Suite);
            if (options.Suite is "system-files" or "system-image-baseline" or "system-active-image")
            {
                workDirectory = Path.Combine(target.Root, "QueueCache-System-" + Guid.NewGuid().ToString("N"));
                if (options.Suite == "system-active-image")
                    storage.Write("workloads.json", new
                    {
                        Retained = true,
                        Cases = integrity.Select(test =>
                        {
                            var artifacts = SystemImageArtifacts(workDirectory, test.Id);
                            return new { test.Id, Directory = artifacts.WorkDirectory, Oracle = storage.PathFor(artifacts.OracleFile) };
                        }).ToArray()
                    });
                else
                    storage.Write("workloads.json", new { Directory = workDirectory, Retained = true, Oracle = storage.PathFor("oracle.json") });
            }
            else if (options.Suite is not ("system-preflight" or "system-post-restart"))
            {
                workDirectory = Path.Combine(target.Root, Path.GetFileName(storage.DirectoryPath));
                storage.Write("workloads.json", new { Directory = workDirectory, Retained = true });
            }
            foreach (var test in integrity)
                await Case(test.Id, async () =>
                {
                    if (test.Operation is "system-preflight" or "system-file-create" or "system-file-verify" or "system-image-baseline" or "system-active-image")
                    {
                        var imageArtifacts = test.Operation == "system-active-image"
                            ? SystemImageArtifacts(workDirectory!, test.Id)
                            : default;
                        var systemReply = await Worker(Job(test.Operation) with
                        {
                            SystemInstance = options.SystemInstance,
                            SystemBytes = options.SystemBytes,
                            RecoverableVm = options.RecoverableVm,
                            WorkDirectory = test.Operation == "system-active-image" ? imageArtifacts.WorkDirectory :
                                test.Operation is "system-file-create" or "system-image-baseline" ? workDirectory : null,
                            OraclePath = test.Operation == "system-active-image" ? storage.PathFor(imageArtifacts.OracleFile) :
                                test.Operation is "system-file-create" or "system-image-baseline" ? storage.PathFor("oracle.json") : options.OraclePath,
                            Configuration = test.Operation == "system-active-image"
                                ? new CacheConfiguration(options.BudgetMiB,
                                    test.Id.EndsWith("-strict", StringComparison.Ordinal)
                                        ? CachePreset.Strict : CachePreset.Fast)
                                {
                                    Options = new(Drain: DrainAlgorithm.Idle, RetainWrites: true, PromoteOnRead: true)
                                }
                                : null
                        }, deadline.Token, test.Operation is "system-image-baseline" or "system-active-image" ? 900 : test.Operation == "system-file-create" ? 300 : 120);
                        if (test.Operation != "system-preflight")
                            caseChecks = JsonSerializer.Deserialize<CheckResult[]>(await File.ReadAllTextAsync(systemReply, deadline.Token))
                                ?? throw new InvalidDataException("Missing system-file checks.");
                        return null;
                    }
                    if (test.CacheEnabled is { } enabled)
                    {
                        var configuration = CacheConfiguration.FromState(original.State) with
                        {
                            Enabled = enabled,
                            BudgetMiB = original.State.BudgetBytes == 0 ? options.BudgetMiB : (int)(original.State.BudgetBytes >> 20)
                        };
                        await Worker(Job("configure") with { Configuration = configuration }, deadline.Token);
                    }
                    var reply = await Worker(Job(test.Operation) with { WorkDirectory = workDirectory }, deadline.Token, 900);
                    if (test.Operation == "trim-file")
                        caseChecks = JsonSerializer.Deserialize<CheckResult[]>(await File.ReadAllTextAsync(reply, deadline.Token))
                            ?? throw new InvalidDataException("Missing file-only check results.");
                    return null;
                });
            if (performance.Count > 0 || drainDecision.Count > 0)
            {
                progressLabel = $"Preparing workloads | {storage.Results.Count}/{totalCases} completed";
                await Worker(Job("prepare") with
                {
                    WorkDirectory = workDirectory,
                    BudgetMiB = options.BudgetMiB
                }, deadline.Token, 900);
                foreach (var scenario in performance)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    await Case(scenario.Id, () => Measure(scenario, deadline.Token));
                }
                foreach (var scenario in drainDecision)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    await Case(scenario.Id, () => MeasureDrainDecision(scenario, deadline.Token));
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex is OperationCanceledException && deadline.IsCancellationRequested && !token.IsCancellationRequested
                ? $"Overall time limit reached ({options.DeadlineMinutes} minutes). The suite is incomplete.\n{ex}"
                : ex.ToString();
            Log("ERROR: " + failure);
        }
        finally
        {
            if (captured)
            {
                progressLabel = $"Restoring | {storage.Results.Count}/{totalCases} recorded";
                Log("Restoring original runtime state (independent cleanup deadline).");
                try
                {
                    OwnedProcess.EnsureStopped(storage.DirectoryPath);
                    await Worker(Job("restore") with
                    {
                        Recovery = storage.PathFor("recovery.json"),
                        Reply = storage.PathFor("restored.json")
                    }, CancellationToken.None, 300);
                }
                catch (Exception ex) { restorationFailure = ex.ToString(); Log("RESTORATION ERROR: " + restorationFailure); }
            }
            if (systemCaptured)
            {
                progressLabel = $"Restoring system disk | {storage.Results.Count}/{totalCases} recorded";
                Log("Restoring the disabled/released C: runtime baseline (independent cleanup deadline).");
                try
                {
                    OwnedProcess.EnsureStopped(storage.DirectoryPath);
                    var requiredImageOracles = PassedSystemImageOracles(storage.DirectoryPath, storage.Results);
                    await Worker(Job("system-restore") with
                    {
                        Recovery = storage.PathFor("recovery.json"),
                        Reply = storage.PathFor("restored.json"),
                        SystemInstance = options.SystemInstance,
                        SystemBytes = options.SystemBytes,
                        RecoverableVm = options.RecoverableVm,
                        OraclePath = storage.PathFor("oracle.json"),
                        ImageOraclePaths = requiredImageOracles,
                        RequireImageEvidence = VerificationWorker.RequiresSystemImageEvidence(storage.Results)
                    }, CancellationToken.None, 300);
                }
                catch (Exception ex) { restorationFailure = ex.ToString(); Log("SYSTEM RESTORATION ERROR: " + restorationFailure); }
            }
            diskLease?.Dispose();
            if (ownsSystemLease) systemLease!.Release();
            systemLease?.Dispose();
        }
        var complete = failure is null && restorationFailure is null && RunStorage.Complete(expected, storage.Results, allowSkipped: options.Suite == "trim-file");
        var status = complete ? (storage.Results.Any(result => result.Status == "SKIP") ? "COMPLETED_WITH_SKIPS" : "COMPLETED") : restorationFailure is not null ? "RESTORATION_FAILED" :
            token.IsCancellationRequested ? "CANCELLED" : "INCOMPLETE";
        if (complete && performance.Count > 0)
        {
            static double Median(IEnumerable<double> values)
            {
                var sorted = values.Order().ToArray();
                return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
            }
            storage.Write("aggregates.json", performance.GroupBy(c => new { c.Workload, c.Allocation, c.Drain, c.DelayMs, c.QueueDepth, c.Writer, c.ApplicationFlush, c.Resident, c.Timing })
                .Select(g => new
                {
                    Configuration = g.Key,
                    Samples = g.Count(),
                    IopsMedian = Median(g.Select(c => storage.Results.Single(r => r.Id == c.Id).Score!.Iops)),
                    IopsMin = g.Min(c => storage.Results.Single(r => r.Id == c.Id).Score!.Iops),
                    IopsMax = g.Max(c => storage.Results.Single(r => r.Id == c.Id).Score!.Iops),
                    CaseIds = g.Select(c => c.Id).ToArray()
                }).ToArray());
        }
        if (complete && drainDecision.Count > 0)
        {
            static double MedianDrain(IEnumerable<double> values)
            {
                var sorted = values.Order().ToArray();
                return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
            }
            double? Metric(DrainDecisionCase test, string name)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(storage.PathFor(test.Id + "-drain.json")));
                var property = document.RootElement.GetProperty(name);
                return property.ValueKind == JsonValueKind.Null ? null : property.GetDouble();
            }
            storage.Write("drain-aggregates.json", drainDecision
                .GroupBy(c => new { c.Workload, c.PendingDrain, c.Parallelism })
                .Select(group => new
                {
                    Configuration = group.Key,
                    Samples = group.Count(),
                    IopsMedian = MedianDrain(group.Select(c => storage.Results.Single(r => r.Id == c.Id).Score!.Iops)),
                    P99MillisecondsMedian = MedianDrain(group.Select(c =>
                        c.Workload == "fitting-write"
                            ? storage.Results.Single(r => r.Id == c.Id).Score!.WriteP99Milliseconds!.Value
                            : storage.Results.Single(r => r.Id == c.Id).Score!.ReadP99Milliseconds!.Value)),
                    FlushSecondsMedian = group.All(c => Metric(c, "FlushSeconds") is null) ? (double?)null :
                        MedianDrain(group.Select(c => Metric(c, "FlushSeconds")!.Value)),
                    PendingBytesAfterMedian = MedianDrain(group.Select(c => Metric(c, "PendingBytesAfter")!.Value)),
                    LowerWriteAttemptsMedian = MedianDrain(group.Select(c => Metric(c, "LowerWriteAttempts")!.Value)),
                    LowerWritesCompletedMedian = MedianDrain(group.Select(c => Metric(c, "LowerWritesCompleted")!.Value)),
                    LowerIoMillisecondsMedian = MedianDrain(group.Select(c => Metric(c, "LowerIoMilliseconds")!.Value)),
                    CaseIds = group.Select(c => c.Id).ToArray()
                }).ToArray());
        }
        storage.Write("status.json", new
        {
            Status = status,
            Finished = DateTimeOffset.UtcNow,
            Expected = expected.Count,
            Collected = storage.Results.Count,
            Failure = failure,
            RestorationFailure = restorationFailure
        });
        var report = new StringBuilder($"# QueueCache verification\n\nStatus: **{status}**\n\nCases: {storage.Results.Count}/{expected.Count}. Plan version: {VerificationPlan.Version}.\n\n");
        if (options.CaseFilter is not null)
            report.AppendLine($"Selected case ID substring: `{options.CaseFilter}`. This is not the complete suite matrix.\n");
        report.AppendLine("PASS means the case's collection/checks succeeded, not that latency met a performance target or that every driver path is verified. No medians are computed from partial runs.\n");
        report.AppendLine("| Case | Status | IOPS | Read p99 ms | Write p99 ms |\n|---|---|---:|---:|---:|");
        foreach (var row in storage.Results)
            report.AppendLine($"| {row.Id} | {row.Status} | {row.Score?.Iops.ToString("F2", CultureInfo.InvariantCulture) ?? "—"} | {row.Score?.ReadP99Milliseconds?.ToString("F3", CultureInfo.InvariantCulture) ?? "N/A"} | {row.Score?.WriteP99Milliseconds?.ToString("F3", CultureInfo.InvariantCulture) ?? "N/A"} |");
        if (failure is not null)
            report.AppendLine("\n## Failure\n\n```text\n" + failure + "\n```");
        if (restorationFailure is not null)
            report.AppendLine("\n## Restoration failure\n\n```text\n" + restorationFailure + "\n```");
        File.WriteAllText(storage.PathFor("SUMMARY.md"), report.ToString());
        var csv = new StringBuilder("CaseId,Status,Seconds,Operations,IOPS,ReadP99Ms,ReadP999Ms,ReadMaxMs,WriteP99Ms,MBPerSecond\n");
        foreach (var r in storage.Results)
            csv.AppendLine(string.Join(',', r.Id, r.Status, r.Seconds.ToString("R", CultureInfo.InvariantCulture),
                r.Score?.Operations.ToString(CultureInfo.InvariantCulture) ?? "", r.Score?.Iops.ToString("R", CultureInfo.InvariantCulture) ?? "",
                r.Score?.ReadP99Milliseconds?.ToString("R", CultureInfo.InvariantCulture) ?? "",
                r.Score?.ReadP999Milliseconds?.ToString("R", CultureInfo.InvariantCulture) ?? "",
                r.Score?.ReadMaxMilliseconds?.ToString("R", CultureInfo.InvariantCulture) ?? "",
                r.Score?.WriteP99Milliseconds?.ToString("R", CultureInfo.InvariantCulture) ?? "",
                r.Score is { } score ? (score.Bytes / score.Seconds / 1_000_000).ToString("R", CultureInfo.InvariantCulture) : ""));
        File.WriteAllText(storage.PathFor("results.csv"), csv.ToString());
        File.WriteAllText(storage.PathFor("FINISHED.txt"), $"{status}\nFinished UTC: {DateTimeOffset.UtcNow:O}\nResults: {storage.DirectoryPath}\n");
        progressLabel = $"Finished | {storage.Results.Count}/{totalCases} recorded";
        Log($"{status}: {storage.Results.Count}/{expected.Count} cases. " + storage.PathFor("SUMMARY.md"));
        return complete ? 0 : token.IsCancellationRequested ? 130 : 1;
    }

    private int totalCases;
    private IReadOnlyList<CheckResult>? caseChecks;
    private async Task Case(string id, Func<Task<DiskSpdScore?>> run)
    {
        caseChecks = null;
        progressLabel = $"Test {storage.Results.Count + 1} of {totalCases}";
        Log("Starting " + id);
        storage.Write("status.json", new
        {
            Status = "RUNNING",
            CurrentCase = id,
            CompletedCases = storage.Results.Count,
            Updated = DateTimeOffset.UtcNow
        });
        var started = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        try
        {
            var score = await run();
            if (caseChecks is { Count: 0 } || caseChecks?.Any(check => check.Result is not ("PASS" or "SKIP")) == true)
                throw new InvalidDataException("Missing or failed file-only checks.");
            var resultStatus = caseChecks?.Any(check => check.Result == "SKIP") == true ? "SKIP" : score is null ? "PASS" : "MEASURED";
            var detail = id == "system-preflight" ? "Expected C: identity and separate output disk verified; read-only cache state recorded. No workload or cache changes." :
                caseChecks is null ? "Case completed; raw evidence retained. Performance thresholds require comparison." :
                string.Join("; ", caseChecks.Select(check => $"{check.Name}: {check.Detail}"));
            storage.Add(new(id, resultStatus, detail, started, timer.Elapsed.TotalSeconds, score));
            Log($"Case {id}: {resultStatus} ({timer.Elapsed.TotalSeconds:F1}s). {detail}");
        }
        catch (Exception ex) { storage.Add(new(id, "FAIL", ex.Message, started, timer.Elapsed.TotalSeconds)); throw; }
    }

    private async Task<DiskSpdScore> Disk(string id, string file, string[] arguments, CancellationToken token)
    {
        // Every command uses the same typed argument vector; shell parsing and automatic $args cannot interfere.
        var result = await RunProcess(id, options.DiskSpd!, [.. arguments, "-Rxml", "-L", "-S", file],
            TimeSpan.FromSeconds(int.Parse(arguments.Single(a => a.StartsWith("-d", StringComparison.Ordinal))[2..], CultureInfo.InvariantCulture) + 120), token);
        if (result.ExitCode != 0)
            throw new IOException($"DiskSpd XML-mode exit {result.ExitCode}: {ErrorDetail(result.Error)}. Both supported variants must return zero in XML mode. See {storage.PathFor(id)}.stderr.txt and stdout.txt.");
        var score = DiskSpdParser.Parse(result.Output);
        storage.Write(id + ".score.json", score);
        return score;
    }

    private async Task<DiskSpdScore?> Measure(PerformanceCase scenario, CancellationToken token)
    {
        await Control(WriteCacheAction.LabDelay, token);
        await Worker(Job("configure") with
        {
            Configuration = new CacheConfiguration(options.BudgetMiB, CachePreset.Fast, scenario.Drain != "Off")
            {
                Options = new CacheOptions(Enum.Parse<CacheAllocation>(scenario.Allocation), 50,
                Drain: scenario.Drain == "Off" ? DrainAlgorithm.Eager : Enum.Parse<DrainAlgorithm>(scenario.Drain))
            }
        }, token, 300);
        await Control(WriteCacheAction.Flush, token);
        await Control(WriteCacheAction.DropClean, token);
        await Control(WriteCacheAction.PerformanceTiming, token, scenario.Timing ? 1UL : 0UL);
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
        await Worker(Job("snapshot") with
        {
            Reply = storage.PathFor(scenario.Id + "-before.json")
        }, token);
        var stop = storage.PathFor(scenario.Id + ".stop");
        var ready = storage.PathFor(scenario.Id + ".ready.json");
        using var children = CancellationTokenSource.CreateLinkedTokenSource(token);
        var telemetry = Worker(Job("telemetry") with
        {
            Reply = storage.PathFor(scenario.Id + "-telemetry.jsonl"),
            StopFile = stop,
            ReadyFile = ready,
            Seconds = options.DurationSeconds + 120
        }, children.Token, options.DurationSeconds + 130);
        Task<DiskSpdScore>? writer = null;
        Task<string>? flush = null;
        try
        {
            Log("Waiting for telemetry readiness before workload: " + scenario.Id);
            await TelemetryCoverage.WaitReadyAsync(ready, telemetry, TimeSpan.FromSeconds(45), token);
            Log("Telemetry ready: " + scenario.Id);
            var workloadStart = DateTimeOffset.UtcNow;
            if (scenario.Writer)
            {
                writer = Disk(scenario.Id + "-writer", Path.Combine(workDirectory, "writer.dat"),
                    ["-b1M", $"-o{scenario.QueueDepth}", "-t1", "-w100", $"-d{options.DurationSeconds + 6}", "-W0", "-Zr"], children.Token);
                await Task.Delay(TimeSpan.FromSeconds(3), token);
                if (scenario.ApplicationFlush)
                    flush = Worker(Job("application-flush") with
                    {
                        StopFile = stop,
                        WorkDirectory = workDirectory,
                        Seconds = options.DurationSeconds
                    }, children.Token, options.DurationSeconds + 120);
            }
            var arguments = scenario.Workload == "interference" ? new List<string> { "-b4K", "-r4K", "-o8", "-t2", "-w0" } :
                new List<string> { scenario.Workload.StartsWith("sequential") ? "-b1M" : "-b4K", $"-o{scenario.QueueDepth}", "-t1",
                    scenario.Workload.EndsWith("write") ? "-w100" : scenario.Workload == "mixed" ? "-w30" : "-w0", "-Zr" };
            if (scenario.Workload.StartsWith("random") || scenario.Workload == "mixed")
                arguments.Add("-r4K");
            // A fixed five-second warmup exercises the fitting working set before scoring.
            // Unlike interference, this is not a promise that every block is resident.
            arguments.AddRange([$"-d{options.DurationSeconds}", scenario.Resident ? "-W5" : "-W0"]);
            var score = await Disk(scenario.Id + "-reader", scenario.Workload == "interference" ? hot : Path.Combine(workDirectory, scenario.Resident ? "resident.dat" : "writer.dat"),
                arguments.ToArray(), children.Token);
            if (writer is not null)
                await writer;
            if (flush is not null)
                await flush;
            var workloadEnd = DateTimeOffset.UtcNow;
            storage.Write(scenario.Id + "-interval.json", new
            {
                Start = workloadStart,
                End = workloadEnd,
                MaximumSampleGapSeconds = 2
            });
            File.WriteAllText(stop, "stop");
            await telemetry;
            var lines = File.ReadAllLines(storage.PathFor(scenario.Id + "-telemetry.jsonl"));
            if (lines.Length < 2)
                throw new InvalidDataException("Insufficient telemetry.");
            var timestamps = new List<DateTimeOffset>();
            foreach (var line in lines)
            {
                using var sample = JsonDocument.Parse(line);
                timestamps.Add(sample.RootElement.GetProperty("Utc").GetDateTimeOffset());
                var state = sample.RootElement.GetProperty("State").Deserialize<WriteCacheState>()!;
                if (state.Errors != original.State.Errors || state.LastError != 0 || state.Instance != original.State.Instance)
                    throw new IOException("Driver error or instance change during measurement.");
            }
            TelemetryCoverage.Validate(timestamps, workloadStart, workloadEnd);
            await Worker(Job("snapshot") with
            {
                Reply = storage.PathFor(scenario.Id + "-after.json")
            }, token);
            return score;
        }
        finally
        {
            File.WriteAllText(stop, "stop");
            children.Cancel();
            // Observe all child completions before restoring or starting another case.
            foreach (var child in new Task?[] { telemetry, writer, flush })
                if (child is not null)
                {
                    try
                    {
                        await child;
                    }
                    catch (Exception) { /* original case failure retained */ }
                }
        }
    }

    private async Task<DiskSpdScore?> MeasureDrainDecision(DrainDecisionCase scenario, CancellationToken token)
    {
        await Control(WriteCacheAction.LabDelay, token);
        await Control(WriteCacheAction.Flush, token);
        var cacheEnabled = scenario.PendingDrain || scenario.Workload == "fitting-write";
        await Worker(Job("configure") with
        {
            Configuration = new CacheConfiguration(options.BudgetMiB, CachePreset.Fast, cacheEnabled)
            {
                Options = new CacheOptions(CacheAllocation.Automatic, 100, RetainWrites: false,
                    Drain: DrainAlgorithm.Deferred, MaxDirtyAgeMs: 3600000,
                    BatchKiB: 256, Parallelism: scenario.Parallelism)
            }
        }, token, 300);
        await Control(WriteCacheAction.Flush, token);
        await Control(WriteCacheAction.DropClean, token);
        await Control(WriteCacheAction.PerformanceTiming, token, 1);

        DrainSeedResult? seed = null;
        if (scenario.PendingDrain)
        {
            var seedPath = await Worker(Job("seed-drain") with
            {
                WorkDirectory = workDirectory,
                BudgetMiB = options.BudgetMiB,
                Value = checked((ulong)(104729 + scenario.Repeat))
            }, token, 300);
            seed = JsonSerializer.Deserialize<DrainSeedResult>(await File.ReadAllTextAsync(seedPath, token)) ??
                throw new InvalidDataException("Missing deterministic drain seed result.");
        }

        var beforePath = await Worker(Job("snapshot") with
        {
            Reply = storage.PathFor(scenario.Id + "-before.json")
        }, token);
        using var beforeDocument = JsonDocument.Parse(await File.ReadAllTextAsync(beforePath, token));
        var before = beforeDocument.RootElement.GetProperty("State").Deserialize<WriteCacheState>()!;
        var performanceBefore = beforeDocument.RootElement.GetProperty("Performance").Deserialize<CachePerformance>()!;
        var diagnosticsBefore = beforeDocument.RootElement.GetProperty("Diagnostics").Deserialize<CacheDiagnostics>()!;
        var stop = storage.PathFor(scenario.Id + ".stop");
        var ready = storage.PathFor(scenario.Id + ".ready.json");
        using var children = CancellationTokenSource.CreateLinkedTokenSource(token);
        var telemetry = Worker(Job("telemetry") with
        {
            Reply = storage.PathFor(scenario.Id + "-telemetry.jsonl"),
            StopFile = stop,
            ReadyFile = ready,
            Seconds = options.DurationSeconds + 120
        }, children.Token, options.DurationSeconds + 130);
        Task<DateTimeOffset>? flush = null;
        Task<DiskSpdScore>? workload = null;
        try
        {
            Log("Waiting for telemetry readiness before workload: " + scenario.Id);
            await TelemetryCoverage.WaitReadyAsync(ready, telemetry, TimeSpan.FromSeconds(45), token);
            Log("Telemetry ready: " + scenario.Id);
            var workloadStart = DateTimeOffset.UtcNow;
            var path = scenario.Workload == "fitting-write" ?
                Path.Combine(workDirectory, "resident.dat") : Path.Combine(workDirectory, "writer.dat");
            var arguments = scenario.Workload == "fitting-write"
                ? new[] { "-b4K", "-r4K", "-o1", "-t1", "-w100", $"-d{options.DurationSeconds}", "-W0", "-Zr" }
                : new[] { "-b4K", "-r4K", "-o1", "-t1", "-w0", $"-d{options.DurationSeconds}", "-W0" };
            workload = Disk(scenario.Id + "-workload", path, arguments, children.Token);
            DateTimeOffset? flushRequested = null;
            DateTimeOffset? flushCompleted = null;
            if (scenario.PendingDrain)
            {
                await Task.Delay(250, token);
                flushRequested = DateTimeOffset.UtcNow;
                flush = FlushAndTimestamp(children.Token);
            }
            var score = await workload;
            var workloadEnd = DateTimeOffset.UtcNow;
            if (flush is not null)
                flushCompleted = await flush;
            storage.Write(scenario.Id + "-interval.json", new
            {
                Start = workloadStart,
                End = workloadEnd,
                FlushRequested = flushRequested,
                MaximumSampleGapSeconds = 2
            });
            File.WriteAllText(stop, "stop");
            await telemetry;
            var lines = File.ReadAllLines(storage.PathFor(scenario.Id + "-telemetry.jsonl"));
            if (lines.Length < 2)
                throw new InvalidDataException("Insufficient drain-decision telemetry.");
            var timestamps = new List<DateTimeOffset>();
            foreach (var line in lines)
            {
                using var sample = JsonDocument.Parse(line);
                timestamps.Add(sample.RootElement.GetProperty("Utc").GetDateTimeOffset());
                var state = sample.RootElement.GetProperty("State").Deserialize<WriteCacheState>()!;
                if (state.Errors != original.State.Errors || state.LastError != 0 || state.Instance != original.State.Instance)
                    throw new IOException("Driver error or instance change during drain-decision measurement.");
            }
            TelemetryCoverage.Validate(timestamps, workloadStart, workloadEnd);
            var afterPath = await Worker(Job("snapshot") with
            {
                Reply = storage.PathFor(scenario.Id + "-after.json")
            }, token);
            using var afterDocument = JsonDocument.Parse(await File.ReadAllTextAsync(afterPath, token));
            var after = afterDocument.RootElement.GetProperty("State").Deserialize<WriteCacheState>()!;
            var performanceAfter = afterDocument.RootElement.GetProperty("Performance").Deserialize<CachePerformance>()!;
            var diagnosticsAfter = afterDocument.RootElement.GetProperty("Diagnostics").Deserialize<CacheDiagnostics>()!;
            var attributionBefore = diagnosticsBefore.Attribution ?? throw new NotSupportedException("Missing lower-I/O attempt counters.");
            var attributionAfter = diagnosticsAfter.Attribution ?? throw new NotSupportedException("Missing lower-I/O attempt counters.");
            if (seed is not null && after.DrainedBytes - seed.Before.DrainedBytes < seed.OwnedBytes)
                throw new IOException("Explicit drain did not persist the complete deterministic dirty set.");
            string? verification = null;
            if (seed is not null)
            {
                await Control(WriteCacheAction.Disable, token);
                verification = await Worker(Job("verify-drain") with
                {
                    WorkDirectory = workDirectory,
                    BudgetMiB = options.BudgetMiB,
                    Value = (ulong)seed.Seed
                }, token, 300);
            }
            storage.Write(scenario.Id + "-drain.json", new
            {
                scenario.Workload,
                scenario.Parallelism,
                scenario.Repeat,
                scenario.PendingDrain,
                Seed = seed,
                SeedPayloadBytes = seed?.Bytes,
                SeedMetadataBytes = seed is null ? (ulong?)null : seed.OwnedBytes - (ulong)seed.Bytes,
                SeedLowerReadAttempts = seed is null ? (ulong?)null :
                    seed.AttributionAfter.LowerReadAttempts - seed.AttributionBefore.LowerReadAttempts,
                BeforeSnapshot = beforePath,
                AfterSnapshot = afterPath,
                PersistedVerification = verification,
                FlushSeconds = flushRequested is null || flushCompleted is null ? (double?)null :
                    (flushCompleted.Value - flushRequested.Value).TotalSeconds,
                PendingBytesAfter = after.DirtyBytes + after.InFlightBytes,
                DrainedBytes = after.DrainedBytes - before.DrainedBytes,
                LowerWriteAttempts = attributionAfter.LowerWriteAttempts - attributionBefore.LowerWriteAttempts,
                LowerWritesCompleted = after.LowerWrites - before.LowerWrites,
                DrainBatches = performanceAfter.DrainBatches - performanceBefore.DrainBatches,
                DriverDrainBytes = performanceAfter.DrainBytes - performanceBefore.DrainBytes,
                LowerIoMilliseconds = performanceAfter.Milliseconds(performanceAfter.LowerIoTicks - performanceBefore.LowerIoTicks),
                DrainSelectionMilliseconds = performanceAfter.Milliseconds(performanceAfter.DrainSelectionTicks - performanceBefore.DrainSelectionTicks),
                DrainCopyMilliseconds = performanceAfter.Milliseconds(performanceAfter.DrainCopyTicks - performanceBefore.DrainCopyTicks),
                DrainRetirementMilliseconds = performanceAfter.Milliseconds(performanceAfter.DrainRetirementTicks - performanceBefore.DrainRetirementTicks),
                CapacityWaits = performanceAfter.CapacityWaits - performanceBefore.CapacityWaits,
                Score = score
            });
            return score;
        }
        finally
        {
            File.WriteAllText(stop, "stop");
            children.Cancel();
            foreach (var child in new Task?[] { telemetry, workload, flush })
                if (child is not null)
                {
                    try { await child; }
                    catch (Exception) { /* original case failure retained */ }
                }
        }
    }

    private async Task<DateTimeOffset> FlushAndTimestamp(CancellationToken token)
    {
        await Control(WriteCacheAction.Flush, token);
        return DateTimeOffset.UtcNow;
    }

    public async Task<int> RecoverAsync(string directory, CancellationToken token, IProgress<string>? progress = null)
    {
        var path = Path.GetFullPath(directory);
        using var runLock = new FileStream(Path.Combine(path, "run.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        OwnedProcess.EnsureStopped(path);
        var snapshotPath = Path.Combine(path, "recovery.json");
        original = JsonSerializer.Deserialize<RecoverySnapshot>(await File.ReadAllTextAsync(snapshotPath, token))!;
        var systemRecovery = IsSystemRecoveryTarget(original.Target);
        FileStream? diskLease = null;
        Semaphore? systemLease = null;
        var ownsSystemLease = false;
        if (systemRecovery)
        {
            systemLease = new Semaphore(1, 1, SystemLeaseName(original.Target));
            ownsSystemLease = systemLease.WaitOne(0);
            if (!ownsSystemLease)
            {
                systemLease.Dispose();
                throw new IOException("Another guarded system-disk verification owns this physical disk.");
            }
        }
        else
        {
            var leases = LeaseDirectory;
            Directory.CreateDirectory(leases);
            diskLease = new FileStream(Path.Combine(leases, original.Target.Device + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        options = systemRecovery
            ? new($"{original.Target.Letter}:", "system-active-image", path,
                SystemInstance: original.Target.Instance, SystemBytes: original.Target.Bytes, RecoverableVm: true)
            : new($"{original.Target.Letter}:", Output: path);
        fileTarget = systemRecovery ? original.Target : null;
        storage = new RunStorage(path);
        progressSink = progress;
        progressLabel = "Recovery";
        Log("Recovery run directory: " + storage.DirectoryPath);
        try
        {
            var operation = systemRecovery ? "system-restore" : "restore";
            var resultsPath = Path.Combine(path, "results.json");
            var priorResults = File.Exists(resultsPath)
                ? JsonSerializer.Deserialize<CaseResult[]>(await File.ReadAllTextAsync(resultsPath, token)) ?? []
                : [];
            await Worker(Job(operation) with
            {
                Recovery = snapshotPath,
                SystemInstance = systemRecovery ? original.Target.Instance : null,
                SystemBytes = systemRecovery ? original.Target.Bytes : null,
                RecoverableVm = systemRecovery,
                OraclePath = systemRecovery ? Path.Combine(path, "oracle.json") : null,
                RequireImageEvidence = systemRecovery && VerificationWorker.RequiresSystemImageEvidence(priorResults),
                ImageOraclePaths = systemRecovery ? PassedSystemImageOracles(path, priorResults) : null
            }, token, 300);
            storage.Write("recovery-result.json", new
            {
                Status = "RESTORED",
                At = DateTimeOffset.UtcNow
            });
            Log("RESTORED: " + storage.PathFor("recovery-result.json"));
            return 0;
        }
        catch (Exception ex) { Log("RESTORATION ERROR: " + ex); throw; }
        finally
        {
            diskLease?.Dispose();
            if (ownsSystemLease) systemLease!.Release();
            systemLease?.Dispose();
        }
    }
}
