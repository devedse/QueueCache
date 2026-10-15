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
public sealed partial class VerificationRunner(string executable, IReadOnlyList<string>? executablePrefix = null, string? leaseDirectory = null)
{
    private readonly IReadOnlyList<string> prefix = executablePrefix ?? [];
    private int sequence;
    private RunStorage storage = null!;
    private RecoverySnapshot original = null!;
    private DiskTarget? fileTarget;
    private ComparisonTarget? comparisonTarget;
    private VerificationOptions options = null!;
    private string workDirectory = "";
    private readonly object logGate = new();
    private IProgress<string>? progressSink;
    private string progressLabel = "Preflight";
    private bool copyFlagsMutationStarted;
    public string? DirectoryPath => storage?.DirectoryPath;
    internal RecoverySnapshot? CampaignBaseline { get; init; }
    internal bool CampaignOwnsDiskLease { get; init; }
    internal Action<string>? RunCreated { get; init; }
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
        TimeSpan timeout, CancellationToken token, ProcessScheduling? scheduling = null)
    {
        Log($"Starting {id}; timeout {timeout.TotalSeconds:F0}s. Raw output: {storage.PathFor(id)}.*");
        var watch = Stopwatch.StartNew();
        var process = OwnedProcess.RunAsync(tool, arguments, storage.PathFor(id), timeout, token, scheduling);
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
    // T080: the baseline oracle is not "oracle.json", so system restoration never compares the edited image with it.
    public const string AppSessionOracleFile = "baseline-oracle.json";
    public const string AppSessionDoneFile = "operator-done";
    public const int AppSessionSeconds = 1800;
    /// <summary>Fast, Deferred with a 10-minute dirty age so a saved edit is normally still in RAM while it is reopened.</summary>
    public static CacheConfiguration AppSessionConfiguration(int budgetMiB) => new(budgetMiB, CachePreset.Fast)
    {
        Options = new(Drain: DrainAlgorithm.Deferred, MaxDirtyAgeMs: 600000, RetainWrites: true, PromoteOnRead: true)
    };
    /// <summary>System and managed-lifecycle phases leave workloads that a later phase reads, and disk-removal
    /// targets a disposable disk; every other suite removes its own workload folders after completing.</summary>
    public static bool RemovesWorkloads(VerificationOptions options) => !options.KeepWorkloads && !IsSystemSuite(options.Suite) &&
        !options.Suite.StartsWith("managed-", StringComparison.Ordinal) && !options.Suite.StartsWith("disk-removal", StringComparison.Ordinal);
    private static bool IsSystemSuite(string suite) => suite is
        "system-preflight" or "system-files" or "system-post-restart" or "system-image-baseline" or "system-active-image" or "system-paging-recognition" or "system-app-session";
    public static bool IsSystemRecoveryTarget(DiskTarget target) =>
        target.Letter == 'C' && target.IsBoot && target.IsSystem;
    // Leases are per physical disk (one verification per disk), not per cached volume.
    private static string LeaseKey(DiskTarget target) => $"PhysicalDrive{target.Number}";
    private static string SystemLeaseName(DiskTarget target) => "Global\\QueueCache-SystemVerify-" + LeaseKey(target);

    private async Task<string> Worker(WorkerJob job, CancellationToken token, int timeoutSeconds = 120)
    {
        if (job.Operation is not ("control" or "configure" or "restore" or "campaign-prepare"))
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
        if (job.ProcessEvidence is { } processEvidence)
        {
            var ownedProcessEvidence = storage.PathFor(id + ".process.json");
            if (!File.Exists(ownedProcessEvidence))
                throw new InvalidDataException("Owned worker process identity was not recorded for " + id);
            File.Copy(ownedProcessEvidence, processEvidence, overwrite: true);
        }
        if (result.ExitCode != 0)
            throw new IOException($"Worker {job.Operation} failed ({result.ExitCode}): {ErrorDetail(result.Error)}. Full error: {storage.PathFor(id + ".stderr.txt")}");
        if (!File.Exists(job.Reply))
            throw new InvalidDataException("Worker did not write its result: " + id);
        return job.Reply;
    }
    /// <summary>Suites whose cases may SKIP on hardware that lacks the capability (TRIM, a second volume on
    /// the disk). The run then completes as COMPLETED_WITH_SKIPS, never as COMPLETED.</summary>
    public static bool AllowsSkips(string suite) => suite is "trim-file" or "trim-cache" or "volumes";
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
    private Task<string> Control(WriteCacheAction action, CancellationToken token, ulong value = 0,
        int? timeoutSeconds = null) =>
        Worker(Job("control") with
        {
            Action = action,
            Value = value
        }, token, timeoutSeconds ?? (action == WriteCacheAction.Flush ? options.PreparationFlushSeconds : 180));

    public async Task<int> RunAsync(VerificationOptions selected, IProgress<string>? progress, CancellationToken token)
    {
        VerificationPlan.Validate(selected);
        var elapsed = Stopwatch.StartNew();
        double preflightSeconds = 0, preparationSeconds = 0, restorationSeconds = 0;
        if (CampaignOwnsDiskLease && CampaignBaseline is null)
            throw new InvalidOperationException("Campaign ownership requires a captured baseline.");
        layoutGeneration = null;
        ulong? originalCopyFlags = null;
        copyFlagsMutationStarted = false;
        layoutMeasurements = null;
        if ((selected.Suite is "cache-read-misses" or "tree-decision" or "read-tail-decision" || VerificationPlan.IsLayoutSuite(selected.Suite) || CacheExercisePlan.Contains(selected.Suite) || VerificationPlan.IsRamReadSuite(selected.Suite)) && Process.GetCurrentProcess().PriorityClass != ProcessPriorityClass.Normal)
            throw new IOException("Read performance suites require a normal-priority process; use task priority 4 when launching through Task Scheduler.");
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
            OraclePath = selected.OraclePath is null ? null : Path.GetFullPath(selected.OraclePath),
            ManagedOraclePath = selected.ManagedOraclePath is null ? null : Path.GetFullPath(selected.ManagedOraclePath)
        };
        storage = new RunStorage(options.Output);
        progressSink = progress;
        // An open lock prevents a second coordinator/recovery process from owning this run concurrently.
        using var runLock = new FileStream(storage.PathFor("run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var performance = options.Suite is "performance" or "full" or "flush-interference" or "write-performance" or "sequential-resident" or "cache-read-reference" or "cache-read-bookkeeping" or "cache-read-misses" || VerificationPlan.IsLayoutSuite(options.Suite) ? VerificationPlan.Performance(options) : [];
        var exercises = CacheExercisePlan.Cases(options);
        var drainDecision = VerificationPlan.DrainDecision(options);
        var drainFlush = DrainFlushDecision.Cases(options);
        var treeDecision = VerificationPlan.TreeDecision(options);
        var readTailDecision = ReadTailDecision.Cases(options);
        var integrity = VerificationPlan.Integrity(options);
        var expected = integrity.Select(test => test.Id).ToList();
        expected.AddRange(performance.Select(c => c.Id));
        expected.AddRange(drainDecision.Select(c => c.Id));
        expected.AddRange(drainFlush.Select(c => c.Id));
        expected.AddRange(treeDecision.Select(c => c.Id));
        expected.AddRange(readTailDecision.Select(c => c.Id));
        expected.AddRange(exercises.Select(c => c.Id));
        totalCases = expected.Count;
        progressLabel = $"Preflight | 0/{totalCases} completed";
        Log("Run directory: " + storage.DirectoryPath);
        Log($"Suite {options.Suite}; target {options.Volume}; overall limit: {(options.DeadlineMinutes == 0 ? "unlimited" : options.DeadlineMinutes + " minutes")}. Per-operation timeouts remain enabled.");
        if (!IsSystemSuite(options.Suite))
            Log($"Preparation flush deadline: {options.PreparationFlushSeconds}s; measurement windows and independent restoration deadline unchanged.");
        else
            Log(options.Suite == "system-active-image" ? "Bounded active C: image phase; runtime-only 256..512 MiB Fast and Strict cases with independent restoration." :
                options.Suite == "system-app-session" ? $"Operator application session on C:; runtime-only Fast cache, waiting up to {AppSessionSeconds / 60} minutes for {storage.PathFor(AppSessionDoneFile)}, then drain/release and byte comparison. Instructions: {storage.PathFor("operator-instructions.txt")}" :
                options.Suite == "system-image-baseline" ? "Bounded uncached C: image baseline; cache configuration remains disabled and released." :
                options.Suite == "system-files" ? "Bounded owned-file phase; no cache configuration, faults, TRIM or reboot." :
                "Read-only system-disk phase; no workload or cache configuration action.");
        if (options.CaseFilter is not null)
            Log($"Selected case ID substring: {options.CaseFilter}; {performance.Count + drainDecision.Count + treeDecision.Count + readTailDecision.Count + exercises.Count} cases, not the complete suite matrix.");
        if (drainDecision.Count > 0)
            Log($"Drain-decision score: {options.DurationSeconds}s; completion allowance: 300s; telemetry gap limit remains 2s.");
        if (drainFlush.Count > 0)
            Log($"Explicit-drain comparison: fixed {DrainFlushDecision.SeedBytes} byte seed; timing diagnostics OFF; worker-ready/start handshake; timestamped Flush call; three alternated pairs.");
        if (readTailDecision.Count > 0)
            Log("Read-tail decision: 100,000 fixed operations per case; per-request latency samples and exact >50/100/250/500 ms counts; collection only.");
        storage.Write("manifest.json", new
        {
            SchemaVersion = 1,
            PlanVersion = VerificationPlan.Version,
            ProcessPriority = Process.GetCurrentProcess().PriorityClass.ToString(),
            Options = options,
            ExpectedCases = expected,
            PerformanceCases = performance,
            DrainDecisionCases = drainDecision,
            DrainFlushDecisionCases = drainFlush,
            DrainFlushSeedBytes = drainFlush.Count == 0 ? (int?)null : DrainFlushDecision.SeedBytes,
            TreeDecisionCases = treeDecision,
            ReadTailDecisionCases = readTailDecision,
            DrainDecisionTiming = drainDecision.Count == 0 ? null : DrainDecisionTiming.ForScore(options.DurationSeconds),
            CacheExerciseCases = exercises,
            RamReadReferenceWindows = VerificationPlan.IsRamReadSuite(options.Suite) ? RamReadReferencePlan.CasesFor(options) : [],
            ComparisonCases = ComparisonPlan.IsComparison(options.Suite) ? ComparisonPlan.CasesFor(options.Suite, options.Repeats) : [],
            CacheExerciseTargets = exercises.Count == 0 ? [] : options.Suite == "cache-recall"
                ? CacheExercisePlan.RecallTargets(options.BudgetMiB) : options.Suite is "caller-backoff" or "priority-cost" or "priority-affinity"
                ? CacheExercisePlan.Targets(options.BudgetMiB, 1) : CacheExercisePlan.AllTargets(options.BudgetMiB),
            Provenance = VerificationWorker.Provenance(executable),
            DiskSpdSha256 = options.DiskSpd is null ? null : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(options.DiskSpd)))
        });
        storage.Write("status.json", new
        {
            Status = "RUNNING",
            Started = DateTimeOffset.UtcNow
        });
        storage.Write("results.json", storage.Results);
        RunCreated?.Invoke(storage.DirectoryPath);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (options.DeadlineMinutes > 0)
            deadline.CancelAfter(TimeSpan.FromMinutes(options.DeadlineMinutes));
        string? failure = null, restorationFailure = null;
        var captured = false;
        var removalUnresolved = false;
        var systemCaptured = false;
        FileStream? diskLease = null;
        Semaphore? systemLease = null;
        var ownsSystemLease = false;
        try
        {
            DiskTarget target = null!;
            var comparison = ComparisonPlan.IsComparison(options.Suite);
            if (comparison)
            {
                // Other products' volumes: identity only. No QueueCache capture, configuration, hook or telemetry,
                // so every product is measured the same way (see ComparisonPlan).
                var identity = await ExecuteWorker(new WorkerJob("compare-target", options.Volume, storage.PathFor("target.json")) with { Suite = options.Suite }, deadline.Token);
                comparisonTarget = JsonSerializer.Deserialize<ComparisonTarget>(await File.ReadAllTextAsync(identity, deadline.Token))
                    ?? throw new InvalidDataException("Missing comparison target.");
                storage.Write("restoration.json", new { Required = false, Reason = "External comparison: no QueueCache configuration, hooks or driver access." });
            }
            else if (IsSystemSuite(options.Suite))
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
                if (options.Suite is "system-active-image" or "system-app-session")
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
                if (options.Suite is "disk-removal" or "disk-removal-windows")
                    await Worker(new WorkerJob("disk-removal-preflight", options.Volume, storage.PathFor("removal-preflight.json")) with
                    {
                        DisposableInstance = options.DisposableInstance,
                        DisposableBytes = options.DisposableBytes
                    }, deadline.Token);
                var recovery = await Worker(new("capture", options.Volume, storage.PathFor("recovery.json"), CampaignBaseline?.Target), deadline.Token);
                original = JsonSerializer.Deserialize<RecoverySnapshot>(await File.ReadAllTextAsync(recovery, deadline.Token))!;
                if (CampaignBaseline is not null) VerificationCampaignEvidence.ValidateConfiguration(CampaignBaseline, original);
                target = original.Target;
            }
            if (!IsSystemSuite(options.Suite) && !comparison)
            {
                var leases = LeaseDirectory;
                Directory.CreateDirectory(leases);
                if (!CampaignOwnsDiskLease)
                    diskLease = new FileStream(Path.Combine(leases, LeaseKey(target) + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            captured = !comparison && options.Suite != "trim-file" && !IsSystemSuite(options.Suite);
            if (options.Suite is "cache-read-misses" or "cache-read-bookkeeping" or "tree-decision" or "read-tail-decision")
            {
                originalCopyFlags = original.CopyFlags
                    ?? throw new InvalidDataException("Missing original runtime copy flags; refusing to start a suite that changes them.");
                if (options.Suite == "cache-read-misses")
                {
                    var copyFlagsEvidence = await LayoutSnapshot("copy-flags-original.json", deadline.Token);
                    if (copyFlagsEvidence.Diagnostics.CopyFlags != originalCopyFlags ||
                        copyFlagsEvidence.State.Instance != original.State.Instance ||
                        copyFlagsEvidence.State.Generation != original.State.Generation)
                        throw new InvalidDataException("The read-miss pre-mutation copy-flags artifact disagrees with the recovery snapshot.");
                }
            }
            if (comparison)
            {
                workDirectory = ComparisonPlan.WorkDirectory(options.Suite, comparisonTarget!.Root, Path.GetFileName(storage.DirectoryPath)) ?? "";
                storage.Write("workloads.json", new { Directory = workDirectory, Retained = options.Suite != "compare-ram" });
            }
            else if (options.Suite is "system-files" or "system-image-baseline" or "system-active-image" or "system-app-session")
            {
                workDirectory = Path.Combine(target.Root, "QueueCache-System-" + Guid.NewGuid().ToString("N"));
                if (options.Suite == "system-app-session")
                    storage.Write("workloads.json", new { Directory = workDirectory, Retained = true, BaselineOracle = storage.PathFor(AppSessionOracleFile) });
                else if (options.Suite == "system-active-image")
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
            else if (options.Suite is not ("system-preflight" or "system-post-restart" or "system-paging-recognition"))
            {
                workDirectory = Path.Combine(target.Root, Path.GetFileName(storage.DirectoryPath));
                storage.Write("workloads.json", new { Directory = workDirectory, Retained = true });
            }
            preflightSeconds = elapsed.Elapsed.TotalSeconds;
            foreach (var test in integrity)
                await Case(test.Id, async () =>
                {
                    if (comparison)
                    {
                        var plan = ComparisonPlan.CasesFor(options.Suite, options.Repeats);
                        var comparisonReply = await ExecuteWorker(new WorkerJob(test.Operation, options.Volume, "") with
                        {
                            WorkDirectory = workDirectory.Length == 0 ? null : workDirectory, DiskSpd = options.DiskSpd,
                            ReferenceRepeats = options.Repeats, Product = options.Product, Suite = options.Suite,
                            OraclePath = ComparisonPlan.IsDurabilityWrite(options.Suite) ? storage.PathFor("durability-oracle.json") : options.OraclePath
                        }, deadline.Token, ComparisonPlan.TimeoutSeconds(options.Suite, options.Repeats));
                        caseChecks = JsonSerializer.Deserialize<CheckResult[]>(await File.ReadAllTextAsync(comparisonReply, deadline.Token))
                            ?? throw new InvalidDataException("Missing comparison step results.");
                        ComparisonPlan.ValidateChecks(plan, caseChecks);
                        ComparisonReport.Write(storage.DirectoryPath, options.Suite, options.Product!, plan);
                        return null;
                    }
                    if (test.Operation == "disk-removal")
                    {
                        var oraclePath = storage.PathFor(test.Id + ".oracle.json");
                        await Worker(Job("disk-removal-prepare") with
                        {
                            Reply = oraclePath, WorkDirectory = workDirectory, BudgetMiB = options.BudgetMiB
                        }, deadline.Token, 180);
                        // A worker failure or timeout cannot prove that Windows never
                        // started removal. Defer restoration until a typed result or
                        // a verified reconnect settles the outcome.
                        removalUnresolved = true;
                        storage.Write("removal-requested.json", new { Phase = "RequestingRemoval", Target = original.Target, At = DateTimeOffset.UtcNow });
                        var ejectPath = await Worker(Job(options.Suite == "disk-removal-windows" ? "disk-removal-eject-windows" : "disk-removal-eject") with
                        {
                            Reply = storage.PathFor(test.Id + ".eject.json")
                        }, deadline.Token, options.PreparationFlushSeconds);
                        var eject = JsonSerializer.Deserialize<DiskEjectResult>(await File.ReadAllTextAsync(ejectPath, deadline.Token))
                            ?? throw new InvalidDataException("Missing eject result.");
                        if (options.Suite == "disk-removal-windows")
                        {
                            var nativeBefore = JsonSerializer.Deserialize<WindowsEjectPrecondition>(await File.ReadAllTextAsync(ejectPath + ".before.json", deadline.Token))
                                ?? throw new InvalidDataException("Missing native Windows eject precondition.");
                            if (nativeBefore.Target != original.Target || !nativeBefore.State.Enabled ||
                                nativeBefore.State.DirtyBytes < (8UL << 20) || nativeBefore.State.LastError != 0 || nativeBefore.State.Instance == 0)
                                throw new IOException("Native Windows removal did not prove the active dirty-cache precondition.");
                        }
                        if (eject.ConfigurationManagerResult != 0)
                        {
                            removalUnresolved = eject.PresenceResult != 0;
                            throw new IOException($"Windows vetoed removal (Configuration Manager {eject.ConfigurationManagerResult}, veto {eject.VetoType}: {eject.VetoName}). " +
                                string.Join("; ", eject.RollbackErrors ?? []));
                        }
                        if (!eject.RemovalObserved)
                        {
                            removalUnresolved = eject.PresenceResult != 0;
                            throw new IOException("Windows accepted the eject request, but device removal was not observed. Removal qualification is incomplete.");
                        }
                        var runId = Path.GetFileName(storage.DirectoryPath);
                        var acknowledgement = new DiskReconnectAcknowledgement(runId, test.Id, original.Target.Instance, original.Target.VolumeId);
                        storage.Write("removal-ready.json", new
                        {
                            Phase = "AwaitingReconnect", RunId = runId, CaseId = test.Id,
                            Target = original.Target, Acknowledgement = acknowledgement,
                            Instructions = "Reconnect the same disposable disk on the same bus and drive letter, then write Acknowledgement as reconnect-ack.json in this run directory. Do not reboot the guest or format/repair the disk."
                        });
                        Log("Removal observed. Awaiting the same disk and reconnect-ack.json; maximum 15 minutes. See removal-ready.json.");
                        var ackPath = storage.PathFor("reconnect-ack.json");
                        using var reconnectDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                        reconnectDeadline.CancelAfter(TimeSpan.FromMinutes(15));
                        try
                        {
                            while (!File.Exists(ackPath))
                                await Task.Delay(500, reconnectDeadline.Token);
                        }
                        catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
                        {
                            throw new IOException("The 15-minute live reconnect deadline expired. Windows removal was observed, but the original disk was not verified after reconnect; restoration remains deferred.");
                        }
                        var ack = JsonSerializer.Deserialize<DiskReconnectAcknowledgement>(await File.ReadAllTextAsync(ackPath, reconnectDeadline.Token))
                            ?? throw new InvalidDataException("Missing reconnect acknowledgement.");
                        DiskRemovalHandshake.Validate(ack, runId, original.Target, test.Id);
                        var verifyReply = await Worker(Job("disk-removal-verify") with { OraclePath = oraclePath }, deadline.Token);
                        caseChecks = JsonSerializer.Deserialize<CheckResult[]>(await File.ReadAllTextAsync(verifyReply, deadline.Token))
                            ?? throw new InvalidDataException("Missing reconnect oracle checks.");
                        removalUnresolved = false;
                        if (options.Suite != "disk-removal-windows")
                            DiskRemovalEvidence.ValidatePreparation(original.Target.Device, original.Target.VolumeId, original.Target.Bytes, eject.Preparation);
                        storage.Write("removal-verified.json", new { Phase = "Verifying", Target = original.Target, At = DateTimeOffset.UtcNow });
                        return null;
                    }
                    if (test.Operation == "system-app-session")
                    {
                        var sessionReply = await Worker(Job(test.Operation) with
                        {
                            SystemInstance = options.SystemInstance,
                            SystemBytes = options.SystemBytes,
                            RecoverableVm = options.RecoverableVm,
                            WorkDirectory = workDirectory,
                            OraclePath = storage.PathFor(AppSessionOracleFile),
                            StopFile = storage.PathFor(AppSessionDoneFile),
                            Seconds = AppSessionSeconds,
                            Configuration = AppSessionConfiguration(options.BudgetMiB)
                        }, deadline.Token, AppSessionSeconds + 900);
                        caseChecks = JsonSerializer.Deserialize<CheckResult[]>(await File.ReadAllTextAsync(sessionReply, deadline.Token))
                            ?? throw new InvalidDataException("Missing application-session checks.");
                        return null;
                    }
                    if (test.Operation is "system-preflight" or "system-file-create" or "system-file-verify" or "system-image-baseline" or "system-active-image" or "system-paging-recognition")
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
                        }, deadline.Token, test.Operation is "system-image-baseline" or "system-active-image" ? 900 : test.Operation is "system-file-create" or "system-paging-recognition" ? 300 : 120);
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
                    if (test.Operation is "managed-broker-restart" or "managed-lifecycle-prepare" or "managed-lifecycle-verify" or "managed-lifecycle-cleanup")
                    {
                        var lifecycleReply = await Worker(Job(test.Operation) with { WorkDirectory = workDirectory,
                            ManagedOraclePath = options.ManagedOraclePath ?? storage.PathFor("managed-lifecycle-manifest.json"),
                            ManagedTransition = options.ManagedTransition }, deadline.Token, 900);
                        caseChecks = JsonSerializer.Deserialize<CheckResult[]>(await File.ReadAllTextAsync(lifecycleReply, deadline.Token))
                            ?? throw new InvalidDataException("Missing managed lifecycle check results.");
                        return null;
                    }
                    var reply = await Worker(Job(test.Operation) with { WorkDirectory = workDirectory,
                        BudgetMiB = test.Operation == "partial-read-accounting" ? options.BudgetMiB : 1024,
                        DiskSpd = test.Operation == "ram-read-reference" ? options.DiskSpd : null,
                        ReferenceRepeats = options.Repeats,
                        TraceSymbols = options.TraceSymbols,
                        ReferenceKind = VerificationPlan.IsRamReadSuite(options.Suite) ? RamReadReferencePlan.KindFor(options.Suite) : RamReadRunKind.Reference,
                        Seconds = test.Operation == "stress-races" ? options.SoakSeconds ?? VerificationPlan.StressDefaultSeconds : options.DurationSeconds,
                        OraclePath = test.Operation == "ram-read-reference" ? storage.PathFor("ram-read-reference-owned.json") :
                            test.Operation == "stress-races" ? storage.PathFor(StressOwnership) : null,
                        ProductExecutable = test.Operation == "managed-cli" ? executable : null,
                        ProductPrefix = test.Operation == "managed-cli" ? prefix.ToArray() : null }, deadline.Token,
                        test.Operation == "ram-read-reference" ? RamReadReferencePlan.CasesFor(options).Count * (options.DurationSeconds + 30) + 300 :
                        test.Operation == "stress-races" ? (options.SoakSeconds ?? VerificationPlan.StressDefaultSeconds) + 1800 : 900);
                    if (test.Operation is "managed-cli" or "managed-provider" or "ram-disk" or "vhdx-backed" or "image-in-ram" or "trim-file" or "paging-coherence" or "concurrent-sectors" or "partial-read-accounting" or "ram-read-reference" or "ordering-faults" or "stress-races" or "app-write-profile" or
                        "volume-registration" or "volume-raw-disk-commands" or "volume-shared-disk" or "volume-resize" or "volume-snapshot" or "trim-cache")
                        caseChecks = JsonSerializer.Deserialize<CheckResult[]>(await File.ReadAllTextAsync(reply, deadline.Token))
                            ?? throw new InvalidDataException("Missing file-only check results.");
                    if (test.Operation == "ram-read-reference")
                        RamReadReferenceEvidence.ValidateChecks(RamReadReferencePlan.CasesFor(options), caseChecks!);
                    return null;
                });
            foreach (var scenario in treeDecision)
            {
                deadline.Token.ThrowIfCancellationRequested();
                await Case(scenario.Id, () => MeasureTreeDecision(scenario, deadline.Token));
            }
            if (performance.Count > 0 || drainDecision.Count > 0 || drainFlush.Count > 0 || exercises.Count > 0 || readTailDecision.Count > 0)
            {
                progressLabel = $"Preparing workloads | {storage.Results.Count}/{totalCases} completed";
                var preparing = Stopwatch.StartNew();
                try
                {
                    await Worker(Job("prepare") with
                    {
                        WorkDirectory = workDirectory,
                        BudgetMiB = options.BudgetMiB,
                        Value = exercises.Count == 0 ? 0UL : options.Suite == "cache-recall" ? 2UL : options.Suite is "caller-backoff" or "priority-cost" or "priority-affinity" ? 3UL : 1UL
                    }, deadline.Token, 900);
                }
                finally { preparationSeconds = preparing.Elapsed.TotalSeconds; }
                foreach (var scenario in readTailDecision)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    await Case(scenario.Id, () => MeasureReadTailDecision(scenario, deadline.Token));
                }
                foreach (var scenario in performance)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    await Case(scenario.Id, () => Measure(scenario, deadline.Token));
                }
                if (options.Suite == "cache-read-reference" && options.CaseFilter is null)
                    foreach (var row in CacheReadReference.Report(storage, performance))
                        Log("Attribution " + row.Describe());
                if (options.Suite == "cache-read-bookkeeping")
                {
                    await Control(WriteCacheAction.LabCopyFlags, deadline.Token, CacheReadBookkeeping.ProductFlags);
                    Log("Comparison verdict (screen only): " + CacheReadBookkeeping.Report(storage, performance));
                }
                if (options.Suite == "cache-read-misses")
                    Log("Comparison verdict (screen only): " + CacheReadMisses.Report(storage, performance));
                foreach (var scenario in exercises)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    await Case(scenario.Id, () => options.Suite == "cache-recall"
                        ? MeasureRecall(scenario, deadline.Token) : MeasureExercise(scenario, deadline.Token));
                }
                foreach (var scenario in drainDecision)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    await Case(scenario.Id, () => MeasureDrainDecision(scenario, deadline.Token));
                }
                foreach (var scenario in drainFlush)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    await Case(scenario.Id, () => MeasureDrainFlush(scenario, deadline.Token));
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
            if (preflightSeconds == 0) preflightSeconds = elapsed.Elapsed.TotalSeconds;
            var restoring = Stopwatch.StartNew();
            if (captured && removalUnresolved)
            {
                restorationFailure = "Removal outcome or reconnect verification is unresolved. Restoration deferred; preserve the disk and evidence, reconnect the exact original target, then use verify-recover.";
                Log("RESTORATION DEFERRED: " + restorationFailure);
            }
            if (captured && !removalUnresolved)
            {
                progressLabel = $"Restoring | {storage.Results.Count}/{totalCases} recorded";
                Log("Restoring original runtime state (independent cleanup deadline).");
                try
                {
                    OwnedProcess.EnsureStopped(storage.DirectoryPath);
                    if (VerificationPlan.IsRamReadSuite(options.Suite) && File.Exists(storage.PathFor("ram-read-reference-owned.json")))
                        await Worker(Job("ram-read-cleanup") with { OraclePath = storage.PathFor("ram-read-reference-owned.json") }, CancellationToken.None, 300);
                    if (options.Suite == "stress-races" && File.Exists(storage.PathFor(StressOwnership)))
                        await Worker(Job("ram-read-cleanup") with { OraclePath = storage.PathFor(StressOwnership) }, CancellationToken.None, 300);
                    await Worker(Job("restore") with
                    {
                        Recovery = storage.PathFor("recovery.json"),
                        Reply = storage.PathFor("restored.json"),
                        AcceptsLabErrors = VerificationPlan.InjectsLabFaults(options.Suite),
                        RequireCopyFlagsRestore = copyFlagsMutationStarted
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
            restorationSeconds = restoring.Elapsed.TotalSeconds;
        }
        var finalizing = Stopwatch.StartNew();
        var complete = failure is null && restorationFailure is null && RunStorage.Complete(expected, storage.Results, allowSkipped: AllowsSkips(options.Suite));
        if (complete && readTailDecision.Count > 0)
        {
            try
            {
                var evidence = readTailDecision.Select(test =>
                    JsonSerializer.Deserialize<ReadTailEvidence>(File.ReadAllText(storage.PathFor(test.Id + "-evidence.json")))
                    ?? throw new InvalidDataException("Missing finalized read-tail evidence for " + test.Id)).ToArray();
                var summary = ReadTailDecision.AggregateEvidence(evidence);
                storage.Write("read-tail-aggregates.json", summary);
                File.WriteAllText(storage.PathFor("read-tail-summary.md"), ReadTailDecision.RenderSummary(summary));
            }
            catch (Exception ex)
            {
                complete = false;
                failure = "Read-tail final evidence/aggregate validation failed; no complete matrix report was emitted.\n" + ex;
                Log("ERROR: " + failure);
            }
        }
        // A completed run no longer needs its multi-GiB workload files; a failed one keeps them for inspection.
        if (complete && RemovesWorkloads(options) && workDirectory.Length > 0 &&
            Path.GetFileName(workDirectory) == Path.GetFileName(storage.DirectoryPath))
        {
            try
            {
                var removed = WorkloadCleanup.Remove(Path.GetDirectoryName(workDirectory)!, Path.GetFileName(workDirectory));
                storage.Write("workloads.json", new { Directory = workDirectory, Retained = false, Removed = removed, RemovedUtc = DateTimeOffset.UtcNow });
                Log($"Removed {removed.Count} workload folder(s) on the tested volume; --keep-workloads keeps them.");
            }
            catch (Exception ex)
            {
                storage.Write("workloads.json", new { Directory = workDirectory, Retained = true, RemovalError = ex.Message });
                Log("Workload folders were kept, removal failed (the results are unaffected): " + ex.Message);
            }
        }
        if (complete && performance.Count > 0)
        {
            static double Median(IEnumerable<double> values)
            {
                var sorted = values.Order().ToArray();
                return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
            }
            storage.Write("aggregates.json", performance.GroupBy(c => new { c.Workload, c.Allocation, c.Drain, c.DelayMs, c.QueueDepth, c.Writer, c.ApplicationFlush, c.Resident, c.Timing, c.Threads, c.CopyFlags })
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
                .GroupBy(c => new { c.Workload, c.PendingDrain, c.Parallelism, c.BatchKiB })
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
        if (complete && drainFlush.Count > 0)
        {
            var evidence = drainFlush.Select(test =>
            {
                var row = JsonSerializer.Deserialize<DrainFlushEvidence>(
                    File.ReadAllText(storage.PathFor(test.Id + "-evidence.json")))
                    ?? throw new InvalidDataException("Missing completed explicit-drain evidence for " + test.Id);
                if (row.Case != test || !row.SeedVerified)
                    throw new InvalidDataException("Explicit-drain evidence does not match its immutable case or seed byte oracle.");
                return row;
            }).ToArray();
            var summary = DrainFlushDecision.BuildSummary(evidence);
            storage.Write("drain-flush-aggregates.json", summary);
            File.WriteAllText(storage.PathFor("drain-flush-summary.md"), DrainFlushDecision.RenderSummary(summary));
        }
        var status = complete ? (storage.Results.Any(result => result.Status == "SKIP") ? "COMPLETED_WITH_SKIPS" : "COMPLETED") : restorationFailure is not null ? "RESTORATION_FAILED" :
            token.IsCancellationRequested ? "CANCELLED" : "INCOMPLETE";
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
        if (complete && drainFlush.Count > 0)
            report.AppendLine("\nComplete explicit-drain matrix: `drain-flush-summary.md` and `drain-flush-aggregates.json`. Timing starts at the recorded Flush call, not worker launch.\n");
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
        storage.Write("timing.json", new VerificationTiming(elapsed.Elapsed.TotalSeconds, preflightSeconds,
            preparationSeconds, storage.Results.Sum(r => r.Seconds), restorationSeconds, finalizing.Elapsed.TotalSeconds));
        var finished = $"{status}\nFinished UTC: {DateTimeOffset.UtcNow:O}\nResults: {storage.DirectoryPath}\n";
        if (ComparisonPlan.IsDurabilityWrite(options.Suite))
        {
            // The VM is reset right after this marker appears: everything written so far must already be on disk.
            ComparisonScenarios.FlushVolume(storage.DirectoryPath[0]);
            using var marker = new FileStream(storage.PathFor("FINISHED.txt"), FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            marker.Write(Encoding.UTF8.GetBytes(finished));
            marker.Flush(flushToDisk: true);
        }
        else
            File.WriteAllText(storage.PathFor("FINISHED.txt"), finished);
        progressLabel = $"Finished | {storage.Results.Count}/{totalCases} recorded";
        Log($"{status}: {storage.Results.Count}/{expected.Count} cases. " + storage.PathFor("SUMMARY.md"));
        return complete ? 0 : token.IsCancellationRequested ? 130 : 1;
    }

    /// <summary>Campaign backing-cache actions use the same owned workers, control telemetry and restoration deadline.</summary>
    internal async Task<string> CampaignMaintenanceAsync(VerificationOptions selected, RecoverySnapshot baseline,
        string evidenceParent, bool restore, IProgress<string>? progress, CancellationToken token, bool prepare = false)
    {
        options = selected with { Volume = baseline.Target.Device };
        original = baseline; storage = new RunStorage(evidenceParent); progressSink = progress;
        progressLabel = prepare ? "Preparing campaign phase" : restore ? "Restoring campaign backing cache" : "Pausing campaign backing cache";
        storage.Write("recovery.json", baseline);
        storage.Write("manifest.json", new { PlanVersion = VerificationPlan.Version, Options = options });
        try
        {
            if (prepare)
            {
                var prepared = await Worker(Job("campaign-prepare") with { Recovery = storage.PathFor("recovery.json"),
                    Reply = storage.PathFor("prepared.json"), Seconds = selected.PreparationFlushSeconds }, token, selected.PreparationFlushSeconds);
                VerificationCampaignEvidence.ValidateConfiguration(baseline, JsonSerializer.Deserialize<RecoverySnapshot>(File.ReadAllText(prepared))!);
            }
            else if (restore)
            {
                OwnedProcess.EnsureStopped(evidenceParent);
                await Worker(Job("restore") with { Recovery = storage.PathFor("recovery.json"), Reply = storage.PathFor("restored.json") }, CancellationToken.None, 300);
            }
            else
            {
                await Control(WriteCacheAction.Flush, token);
                await Control(WriteCacheAction.Disable, token);
            }
            OwnedProcess.EnsureStopped(storage.DirectoryPath);
            storage.Write("status.json", new { Status = "COMPLETED", Restoration = prepare ? "UNCHANGED" : restore ? "RESTORED" : "PENDING" });
            File.WriteAllText(storage.PathFor("FINISHED.txt"), "COMPLETED\n");
            Log(prepare ? "Explicit phase preparation completed; baseline unchanged and clean." : restore ? "Original campaign backing cache restored." : "Campaign backing cache paused; independent restoration is required.");
            return storage.DirectoryPath;
        }
        catch (Exception ex)
        {
            storage.Write("status.json", new { Status = restore ? "RESTORATION_FAILED" : "INCOMPLETE", Failure = ex.ToString() });
            Log("ERROR: " + ex); throw;
        }
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

    private async Task<DiskSpdScore> Disk(string id, string file, string[] arguments, CancellationToken token,
        int? timeoutSeconds = null)
    {
        // Every command uses the same typed argument vector; shell parsing and automatic $args cannot interfere.
        var result = await RunProcess(id, options.DiskSpd!, [.. arguments, "-Rxml", "-L", "-S", file],
            TimeSpan.FromSeconds(timeoutSeconds ?? (int.Parse(arguments.Single(a => a.StartsWith("-d", StringComparison.Ordinal))[2..], CultureInfo.InvariantCulture) + 120)), token);
        if (result.ExitCode != 0)
            throw new IOException($"DiskSpd XML-mode exit {result.ExitCode}: {ErrorDetail(result.Error)}. Both supported variants must return zero in XML mode. See {storage.PathFor(id)}.stderr.txt and stdout.txt.");
        var score = DiskSpdParser.Parse(result.Output);
        storage.Write(id + ".score.json", score);
        return score;
    }

    private async Task<DiskSpdScore?> MeasureTreeDecision(TreeDecisionCase scenario, CancellationToken token)
    {
        await Control(WriteCacheAction.Flush, token);
        await Worker(Job("flush-volume"), token, 180);
        await Control(WriteCacheAction.Flush, token);
        var configuration = new CacheConfiguration(options.BudgetMiB, CachePreset.Fast, scenario.CacheEnabled)
        {
            Options = new CacheOptions(CacheAllocation.Automatic, 50, RetainWrites: true, PromoteOnRead: true,
                Drain: DrainAlgorithm.Idle, LowPercent: 40, HighPercent: 80, MaxDirtyAgeMs: 5000, IdleMs: 250,
                BatchKiB: scenario.BatchKiB, Parallelism: scenario.Parallelism)
        };
        await Worker(Job("configure") with { Configuration = configuration }, token, 300);
        if (scenario.CopyFlags is { } copyFlags)
        {
            copyFlagsMutationStarted = true;
            await Control(WriteCacheAction.LabCopyFlags, token, copyFlags);
        }
        await Control(WriteCacheAction.PerformanceTiming, token, 0);
        await Control(WriteCacheAction.DropClean, token);
        var settlePath = await Worker(Job("tree-settle") with { Seconds = 30 }, token, 360);
        var preconditionSettle = JsonSerializer.Deserialize<SettleResult>(await File.ReadAllTextAsync(settlePath, token))
            ?? throw new InvalidDataException("Missing tree-decision precondition settle evidence.");
        TreeDecisionEvidenceContract.ValidateSettle(preconditionSettle, 30);

        var beforePath = await Worker(Job("snapshot") with { Reply = storage.PathFor(scenario.Id + "-before.json") }, token);
        var before = JsonSerializer.Deserialize<CacheLayoutSnapshot>(await File.ReadAllTextAsync(beforePath, token))
            ?? throw new InvalidDataException("Missing tree-decision start snapshot.");
        TreeDecisionEvidenceContract.ValidateStart(scenario, before);
        var treePath = await Worker(Job("tree-decision") with
        {
            WorkDirectory = Path.Combine(workDirectory, scenario.Id),
            Seconds = 120,
            Reply = storage.PathFor(scenario.Id + "-tree.json")
        }, token, 1800);
        var tree = JsonSerializer.Deserialize<TreeResult>(await File.ReadAllTextAsync(treePath, token))
            ?? throw new InvalidDataException("Missing tree-decision byte/hash and timing evidence.");
        var afterPath = await Worker(Job("snapshot") with { Reply = storage.PathFor(scenario.Id + "-after.json") }, token);
        var after = JsonSerializer.Deserialize<CacheLayoutSnapshot>(await File.ReadAllTextAsync(afterPath, token))
            ?? throw new InvalidDataException("Missing tree-decision end snapshot.");
        var evidence = new TreeDecisionEvidence(scenario, before, tree, after);
        TreeDecisionEvidenceContract.Validate(evidence);
        storage.Write(scenario.Id + "-evidence.json", evidence);
        caseChecks = [new("Seeded file-tree byte and SHA-256 oracle", "PASS",
            $"{tree.Files} files / {tree.Bytes} bytes; create {tree.CreateSeconds:F2}s, copy {tree.CopySeconds:F2}s, read/hash {tree.ReadSeconds:F2}s, delete {tree.DeleteSeconds:F2}s. Collection only; see per-phase memory and cache snapshots.")];
        return null;
    }

    private async Task<DiskSpdScore?> MeasureReadTailDecision(ReadTailCase scenario, CancellationToken token)
    {
        ReadTailDecision.ValidateCase(scenario);
        await Control(WriteCacheAction.Flush, token, timeoutSeconds: options.PreparationFlushSeconds);
        await Worker(Job("flush-volume"), token, 180);
        await Control(WriteCacheAction.Flush, token, timeoutSeconds: options.PreparationFlushSeconds);
        var configuration = new CacheConfiguration(options.BudgetMiB, CachePreset.Fast, true)
        {
            Options = new CacheOptions(Drain: DrainAlgorithm.Idle, RetainWrites: true, PromoteOnRead: true,
                LowPercent: 40, HighPercent: 80, MaxDirtyAgeMs: 5000, IdleMs: 250,
                BatchKiB: 256, Parallelism: 2)
        };
        await Worker(Job("configure") with { Configuration = configuration }, token, 300);
        copyFlagsMutationStarted = true;
        await Control(WriteCacheAction.LabCopyFlags, token, scenario.CopyFlags);
        await Control(WriteCacheAction.PerformanceTiming, token, 0);
        await Control(WriteCacheAction.DropClean, token);
        var settlePath = await Worker(Job("tree-settle") with { Seconds = 30 }, token, 360);
        var settle = JsonSerializer.Deserialize<SettleResult>(await File.ReadAllTextAsync(settlePath, token))
            ?? throw new InvalidDataException("Missing read-tail precondition settle evidence.");
        TreeDecisionEvidenceContract.ValidateSettle(settle, 30);

        if (scenario.Resident)
        {
            var warmPath = await Worker(Job("read-tail-warm") with
            {
                WorkDirectory = workDirectory,
                Value = checked((ulong)scenario.FileBytes)
            }, token, 900);
            var warm = JsonSerializer.Deserialize<ReadTailWarmResult>(await File.ReadAllTextAsync(warmPath, token))
                ?? throw new InvalidDataException("Missing read-tail resident warm-up evidence.");
            if (warm.Bytes != scenario.FileBytes || warm.CompletedOperations != scenario.FileBytes / (1 << 20) ||
                !double.IsFinite(warm.Seconds) || warm.Seconds <= 0)
                throw new InvalidDataException(scenario.Id + ": resident warm-up did not cover the exact declared file.");
            storage.Write(scenario.Id + "-warm.json", warm);
        }

        // A stray filesystem metadata write must not leave dirty bytes in the start snapshot and fail a long matrix.
        await WaitForQuiet(scenario.Id + "-start", token);
        var beforePath = await Worker(Job("snapshot") with { Reply = storage.PathFor(scenario.Id + "-before.json") }, token);
        var before = JsonSerializer.Deserialize<CacheLayoutSnapshot>(await File.ReadAllTextAsync(beforePath, token))
            ?? throw new InvalidDataException("Missing read-tail start snapshot.");
        var resultPath = await Worker(Job("read-tail") with
        {
            WorkDirectory = workDirectory,
            TailDecision = scenario,
            Reply = storage.PathFor(scenario.Id + "-result.json")
        }, token, 1800);
        var result = JsonSerializer.Deserialize<ReadTailResult>(await File.ReadAllTextAsync(resultPath, token))
            ?? throw new InvalidDataException("Missing exact-operation read-tail evidence.");
        var afterPath = await Worker(Job("snapshot") with { Reply = storage.PathFor(scenario.Id + "-after.json") }, token);
        var after = JsonSerializer.Deserialize<CacheLayoutSnapshot>(await File.ReadAllTextAsync(afterPath, token))
            ?? throw new InvalidDataException("Missing read-tail end snapshot.");
        var evidence = new ReadTailEvidence(scenario, before, result, after);
        ReadTailDecision.ValidateEvidence(evidence);
        storage.Write(scenario.Id + "-evidence.json", evidence);

        if (scenario.Writes)
        {
            await Control(WriteCacheAction.Flush, token, timeoutSeconds: options.PreparationFlushSeconds);
            await Worker(Job("flush-volume"), token, 180);
            await Control(WriteCacheAction.Flush, token, timeoutSeconds: options.PreparationFlushSeconds);
            // Filesystem metadata can arrive immediately after the volume flush. Before the unbuffered
            // oracle, require a clean cache whose lower-I/O counters remain stable for two seconds.
            await WaitForQuiet(scenario.Id + "-durable", token);
            await Control(WriteCacheAction.DropClean, token);
            var durablePath = await Worker(Job("snapshot") with { Reply = storage.PathFor(scenario.Id + "-durable.json") }, token);
            var durable = JsonSerializer.Deserialize<CacheLayoutSnapshot>(await File.ReadAllTextAsync(durablePath, token))
                ?? throw new InvalidDataException("Missing post-flush write state.");
            if (durable.State.DirtyBytes != 0 || durable.State.InFlightBytes != 0 || durable.State.Faulted ||
                durable.State.Suspended || durable.State.Removed || durable.State.LastError != 0 ||
                durable.State.Instance != before.State.Instance || durable.State.Generation != before.State.Generation)
                throw new IOException(scenario.Id + ": foreground write set did not reach a clean, healthy state before disk verification.");
            var verifyPath = await Worker(Job("read-tail-verify") with
            {
                WorkDirectory = workDirectory,
                TailDecision = scenario,
                Reply = storage.PathFor(scenario.Id + "-write-verification.json")
            }, token, 1800);
            var verification = JsonSerializer.Deserialize<ReadTailWriteVerification>(await File.ReadAllTextAsync(verifyPath, token))
                ?? throw new InvalidDataException("Missing read-tail durable write oracle.");
            if (verification.CompletedOperations != scenario.OperationCount || verification.Bytes != result.Bytes ||
                verification.OrderedBlockSha256.Length != 64)
                throw new InvalidDataException(scenario.Id + ": durable write oracle did not verify the complete declared operation set.");
            storage.Write(scenario.Id + "-write-verification.json", verification);
        }

        caseChecks = [new("Fixed-operation read/write tail samples and cache accounting", "PASS",
            $"{result.CompletedOperations:N0} operations; p50/p99/p99.9/max {result.P50Milliseconds:F3}/{result.P99Milliseconds:F3}/{result.P999Milliseconds:F3}/{result.MaxMilliseconds:F3} ms; " +
            $">50/100/250/500 ms: {result.Over50Milliseconds}/{result.Over100Milliseconds}/{result.Over250Milliseconds}/{result.Over500Milliseconds}. Collection only.")];
        return null;
    }

    private const string StressOwnership = "stress-races-owned.json";
    private ulong? layoutGeneration, layoutMeasurements;
    private async Task<CacheLayoutSnapshot> LayoutSnapshot(string name, CancellationToken token)
    {
        var path = await Worker(Job("snapshot") with { Reply = storage.PathFor(name) }, token);
        return JsonSerializer.Deserialize<CacheLayoutSnapshot>(await File.ReadAllTextAsync(path, token))
            ?? throw new InvalidDataException("Missing cache layout snapshot.");
    }

    /// <summary>Flush and wait until the cache stays clean for 2 s (up to 5 attempts, each recorded), so a
    /// stray filesystem metadata write is not already pending when a strictly checked window starts.
    /// The checks that follow stay strict; this only waits, it never accepts a dirty window.</summary>
    private async Task WaitForQuiet(string name, CancellationToken token)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await Control(WriteCacheAction.Flush, token);
            var first = await LayoutSnapshot($"{name}-quiet{attempt}a.json", token);
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            if (CacheLayoutEvidence.IsQuiet(first, await LayoutSnapshot($"{name}-quiet{attempt}b.json", token)))
                return;
        }
        throw new IOException("The cache did not stay quiet after five recorded attempts.");
    }

    private async Task<DiskSpdScore?> Measure(PerformanceCase scenario, CancellationToken token)
    {
        await Control(WriteCacheAction.LabDelay, token);
        var resetLayout = CacheLayoutEvidence.Resets(scenario.Layout);
        var reuseLayout = resetLayout || scenario.Layout is CacheLayoutStage.SequentialReuse or CacheLayoutStage.RandomReuse or CacheLayoutStage.Churned or CacheLayoutStage.ChurnedFull;
        if (scenario.Layout is CacheLayoutStage.Fresh or CacheLayoutStage.Recreated)
        {
            await Control(WriteCacheAction.Disable, token);
            await Control(WriteCacheAction.Release, token);
        }
        if (!reuseLayout)
        {
            await Worker(Job("configure") with
            {
                Configuration = new CacheConfiguration(options.BudgetMiB, CachePreset.Fast, scenario.Drain != "Off")
                {
                    Options = new CacheOptions(Enum.Parse<CacheAllocation>(scenario.Allocation), 50,
                    Drain: scenario.Drain == "Off" ? DrainAlgorithm.Eager : Enum.Parse<DrainAlgorithm>(scenario.Drain))
                }
            }, token, 300);
        }
        await Control(WriteCacheAction.Flush, token);
        if (scenario.Layout == CacheLayoutStage.None)
            await Control(WriteCacheAction.DropClean, token);
        await Control(WriteCacheAction.PerformanceTiming, token, scenario.Timing ? 1UL : 0UL);
        if (scenario.CopyFlags is { } copyFlags)
        {
            copyFlagsMutationStarted = true;
            await Control(WriteCacheAction.LabCopyFlags, token, copyFlags);
        }
        if (scenario.Layout != CacheLayoutStage.None)
        {
            await WaitForQuiet(scenario.Id + "-start", token);
            var layout = await LayoutSnapshot(scenario.Id + "-layout-start.json", token);
            CacheLayoutEvidence.ValidateTransition(layoutGeneration, layout, reuseLayout);
            layoutGeneration = layout.State.Generation;
            if (scenario.Layout == CacheLayoutStage.RandomReuse)
            {
                // Touch already-resident blocks in random order, changing the clean LRU.
                // DropClean then returns slots in that order; refill uses the same allocation.
                var random = await Disk(scenario.Id + "-random-touch", Path.Combine(workDirectory, "resident.dat"),
                    ["-b4K", "-r4K", "-o32", "-t1", "-w0", "-d5", "-W0", "-z42"], token);
                var touched = await LayoutSnapshot(scenario.Id + "-random-touched.json", token);
                CacheLayoutEvidence.ValidateScore(layout, touched, random.Bytes);
            }
            if (scenario.Layout == CacheLayoutStage.Churned)
                // Random 4K reads over a file twice the cache: misses evict in random LRU order and
                // nothing is cleared, like a cache in long use. Misses here are intended.
                await Disk(scenario.Id + "-churn", Path.Combine(workDirectory, "writer.dat"),
                    ["-b4K", "-r4K", "-o32", "-t1", "-w0", "-d60", "-W0", "-z7"], token);
            if (scenario.Layout == CacheLayoutStage.ChurnedFull)
            {
                // Fill every slot (the 4 GiB file is twice the cache), then randomize recency with
                // 16K random reads: hits re-order the LRU, misses evict. The cache stays full.
                var churn = Path.Combine(workDirectory, "writer.dat");
                await Disk(scenario.Id + "-fill", churn, ["-b1M", "-o8", "-t1", "-w0", "-d10", "-W0"], token);
                await Disk(scenario.Id + "-churn", churn, ["-b16K", "-r16K", "-o32", "-t1", "-w0", "-d120", "-W0", "-z7"], token);
                await LayoutSnapshot(scenario.Id + "-churned.json", token);
            }
        }
        if (resetLayout)
            await Worker(Job("cache-layout-reset") with { Reply = storage.PathFor(scenario.Id + "-reset-probe.json") }, token);
        else if (scenario.Layout is not (CacheLayoutStage.None or CacheLayoutStage.Churned or CacheLayoutStage.ChurnedFull))
            await Control(WriteCacheAction.DropClean, token);
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
        if (scenario.WarmResident)
        {
            var file = Path.Combine(workDirectory, "resident.dat");
            var coldWarm = await Disk(scenario.Id + "-warm1", file, ["-b1M", "-o8", "-t1", "-w0", "-d10", "-W0"], token);
            WarmResidentEvidence.ValidateFirstPass(coldWarm.Bytes, (ulong)options.BudgetMiB / 2 << 20);
            var warmBefore = await Worker(Job("snapshot") with { Reply = storage.PathFor(scenario.Id + "-warm-before.json") }, token);
            var warmScore = await Disk(scenario.Id + "-warm2", file, ["-b1M", "-o8", "-t1", "-w0", "-d3", "-W0"], token);
            var warmAfter = await Worker(Job("snapshot") with { Reply = storage.PathFor(scenario.Id + "-warm-after.json") }, token);
            using var beforeWarm = JsonDocument.Parse(await File.ReadAllTextAsync(warmBefore, token));
            using var afterWarm = JsonDocument.Parse(await File.ReadAllTextAsync(warmAfter, token));
            WarmResidentEvidence.Validate(beforeWarm.RootElement.GetProperty("State").Deserialize<WriteCacheState>()!,
                afterWarm.RootElement.GetProperty("State").Deserialize<WriteCacheState>()!, warmScore.Bytes,
                (ulong)options.BudgetMiB / 2 << 20);
        }
        await Control(WriteCacheAction.LabDelay, token, (ulong)scenario.DelayMs);
        var measureLayout = scenario.Layout != CacheLayoutStage.None && options.Suite is "cache-layout-steady" or "cache-layout-full";
        if (scenario.Layout != CacheLayoutStage.None)
            await WaitForQuiet(scenario.Id + "-score", token);
        if (measureLayout)
            await Control(WriteCacheAction.LabMeasureLayout, token);
        var beforePath = await Worker(Job("snapshot") with
        {
            Reply = storage.PathFor(scenario.Id + "-before.json")
        }, token);
        if (measureLayout)
        {
            var measured = JsonSerializer.Deserialize<CacheLayoutSnapshot>(await File.ReadAllTextAsync(beforePath, token))!;
            CacheLayoutEvidence.ValidateLayout(measured, layoutMeasurements);
            layoutMeasurements = measured.Diagnostics.Layout!.Measurements;
        }
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
                new List<string> { scenario.Workload.StartsWith("sequential") ? "-b1M" : "-b4K", $"-o{scenario.QueueDepth}", $"-t{scenario.Threads}",
                    scenario.Workload.EndsWith("write") ? "-w100" : scenario.Workload == "mixed" ? "-w30" : "-w0", scenario.WriteBufferArgument };
            if (scenario.Workload.StartsWith("random") || scenario.Workload == "mixed")
                arguments.Add("-r4K");
            else if (scenario.Threads > 1)
                arguments.AddRange(["-s4M", "-T1M"]); // Interleaved 1 MiB blocks, as the RAM-disk four-reader control.
            if (options.Suite == "cache-read-misses")
                arguments.Add("-z1415"); // Identical random offsets for each comparison arm.
            // A fixed five-second warmup exercises the fitting working set before scoring.
            // Unlike interference, this is not a promise that every block is resident.
            arguments.AddRange([$"-d{options.DurationSeconds}", scenario.Resident && !scenario.WarmResident ? "-W5" : "-W0"]);
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
            var afterPath = await Worker(Job("snapshot") with
            {
                Reply = storage.PathFor(scenario.Id + "-after.json")
            }, token);
            if (options.Suite == "cache-read-misses")
            {
                using var before = JsonDocument.Parse(await File.ReadAllTextAsync(beforePath, token));
                using var after = JsonDocument.Parse(await File.ReadAllTextAsync(afterPath, token));
                CacheReadMisses.Window(scenario, before.RootElement, after.RootElement, score);
            }
            if (scenario.Layout != CacheLayoutStage.None)
                CacheLayoutEvidence.ValidateScore(
                    JsonSerializer.Deserialize<CacheLayoutSnapshot>(File.ReadAllText(storage.PathFor(scenario.Id + "-before.json")))!,
                    JsonSerializer.Deserialize<CacheLayoutSnapshot>(File.ReadAllText(storage.PathFor(scenario.Id + "-after.json")))!, score.Bytes);
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

    private async Task<DiskSpdScore?> MeasureDrainFlush(DrainFlushCase scenario, CancellationToken token)
    {
        DrainFlushDecision.ValidateCase(scenario);
        await Control(WriteCacheAction.LabDelay, token);
        await Control(WriteCacheAction.Flush, token, timeoutSeconds: options.PreparationFlushSeconds);
        await Worker(Job("flush-volume"), token, 180);
        await Control(WriteCacheAction.Flush, token, timeoutSeconds: options.PreparationFlushSeconds);

        var configuration = new CacheConfiguration(options.BudgetMiB, CachePreset.Fast, true)
        {
            Options = new CacheOptions(CacheAllocation.Automatic, 100, RetainWrites: true, PromoteOnRead: true,
                Drain: DrainAlgorithm.Deferred, LowPercent: 40, HighPercent: 80, MaxDirtyAgeMs: 3_600_000,
                IdleMs: 250, BatchKiB: scenario.BatchKiB, Parallelism: scenario.Parallelism)
        };
        await Worker(Job("configure") with { Configuration = configuration }, token, 300);
        await Control(WriteCacheAction.PerformanceTiming, token, 0);
        await Control(WriteCacheAction.DropClean, token);
        var configured = await LayoutSnapshot(scenario.Id + "-configured.json", token);
        if (!configured.State.Operational || !configured.State.Enabled || !configured.State.UnsafeDefer ||
            configured.State.Options != configuration.Options || configured.State.DirtyBytes != 0 ||
            configured.State.InFlightBytes != 0 || configured.Performance.TimingEnabled != 0 ||
            configured.Diagnostics.Attribution is null)
            throw new InvalidDataException("Explicit-drain precondition does not match the timing-off Fast Deferred configuration.");

        var workerId = scenario.Id + "-flush-worker";
        var readyPath = storage.PathFor(scenario.Id + ".flush-ready.json");
        var startPath = storage.PathFor(scenario.Id + ".flush-start");
        var processPath = storage.PathFor(workerId + ".process.json");
        using var workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var workerTask = Worker(Job("timed-cache-flush") with
        {
            ReadyFile = readyPath,
            StartFile = startPath,
            ProcessEvidence = processPath,
            WorkDirectory = workDirectory,
            BudgetMiB = options.BudgetMiB,
            Value = checked((ulong)(104729 + scenario.Repeat))
        }, workerCancellation.Token, 600);
        var released = false;
        try
        {
            await TelemetryCoverage.WaitReadyAsync(readyPath, workerTask, TimeSpan.FromSeconds(600), token);
            using var readyDocument = JsonDocument.Parse(await File.ReadAllTextAsync(readyPath, token));
            var workerReadyUtc = readyDocument.RootElement.GetProperty("At").GetDateTimeOffset();
            var releaseUtc = DateTimeOffset.UtcNow;
            File.WriteAllText(startPath, "start");
            released = true;
            var replyPath = await workerTask;
            var measured = JsonSerializer.Deserialize<SeededDrainFlushResult>(
                await File.ReadAllTextAsync(replyPath, token))
                ?? throw new InvalidDataException("Missing held-seed explicit-flush control evidence.");
            var seed = measured.Seed;
            var seedCompletedUtc = measured.SeedCompletedUtc;
            var control = measured.Control;
            var process = JsonSerializer.Deserialize<ProcessIdentity>(await File.ReadAllTextAsync(processPath, token))
                ?? throw new InvalidDataException("Missing owned explicit-flush worker process identity.");
            var metrics = DrainFlushDecision.ValidateEvidence(scenario, seed, process,
                seedCompletedUtc, workerReadyUtc, releaseUtc, control,
                original.State.Instance, original.State.Errors);

            await Control(WriteCacheAction.Disable, token, timeoutSeconds: options.PreparationFlushSeconds);
            var verifyPath = await Worker(Job("verify-drain") with
            {
                WorkDirectory = workDirectory,
                BudgetMiB = options.BudgetMiB,
                Value = (ulong)seed.Seed,
                DrainSeedBytes = seed.Bytes
            }, token, 600);
            using var verifyDocument = JsonDocument.Parse(await File.ReadAllTextAsync(verifyPath, token));
            if (!verifyDocument.RootElement.GetProperty("Verified").GetBoolean())
                throw new InvalidDataException("Explicit-drain dirty-seed byte oracle did not pass.");

            var evidence = new DrainFlushEvidence(scenario, seed, true, process, seedCompletedUtc,
                workerReadyUtc, releaseUtc, control, processPath,
                verifyPath, metrics);
            storage.Write(scenario.Id + "-evidence.json", evidence);
            storage.Write(scenario.Id + "-configured.json", configured);
            storage.Write(scenario.Id + "-seed-verification.json", verifyDocument.RootElement.Clone());
            caseChecks = [new("Timestamped explicit cache flush and seed persistence", "PASS",
                $"Flush call {metrics.DurationMilliseconds:F1} ms; {metrics.DrainBatches} drain batches wrote {metrics.DriverDrainBytes} bytes; timing off and byte oracle passed.")];
            return null;
        }
        finally
        {
            if (!released) workerCancellation.Cancel();
            try { await workerTask; }
            catch (Exception) { /* original case failure retained */ }
        }
    }

    private async Task<DiskSpdScore?> MeasureDrainDecision(DrainDecisionCase scenario, CancellationToken token)
    {
        var timing = DrainDecisionTiming.ForScore(options.DurationSeconds);
        await Control(WriteCacheAction.LabDelay, token);
        await Control(WriteCacheAction.Flush, token);
        var cacheEnabled = scenario.PendingDrain || scenario.Workload == "fitting-write";
        await Worker(Job("configure") with
        {
            Configuration = new CacheConfiguration(options.BudgetMiB, CachePreset.Fast, cacheEnabled)
            {
                Options = new CacheOptions(CacheAllocation.Automatic, 100, RetainWrites: false,
                    Drain: DrainAlgorithm.Deferred, MaxDirtyAgeMs: 3600000,
                    BatchKiB: scenario.BatchKiB, Parallelism: scenario.Parallelism)
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
            Seconds = timing.TelemetrySeconds
        }, children.Token, timing.TelemetryDeadlineSeconds);
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
                ? new[] { "-b4K", "-r4K", "-o1", "-t1", "-w100", $"-d{timing.ScoreSeconds}", "-W0", "-Zr" }
                : new[] { "-b4K", "-r4K", "-o1", "-t1", "-w0", $"-d{timing.ScoreSeconds}", "-W0" };
            workload = Disk(scenario.Id + "-workload", path, arguments, children.Token, timing.ProcessDeadlineSeconds);
            DateTimeOffset? flushRequested = null;
            DateTimeOffset? flushCompleted = null;
            if (scenario.PendingDrain)
            {
                await Task.Delay(250, token);
                flushRequested = DateTimeOffset.UtcNow;
                flush = FlushAndTimestamp(children.Token, timing.FlushDeadlineSeconds);
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

    private async Task<DateTimeOffset> FlushAndTimestamp(CancellationToken token, int? timeoutSeconds = null)
    {
        await Control(WriteCacheAction.Flush, token, timeoutSeconds: timeoutSeconds);
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
            diskLease = new FileStream(Path.Combine(leases, LeaseKey(original.Target) + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        options = systemRecovery
            ? new($"{original.Target.Letter}:", "system-active-image", path,
                SystemInstance: original.Target.Instance, SystemBytes: original.Target.DiskBytes, RecoverableVm: true)
            : new($"{original.Target.Letter}:", Output: path);
        fileTarget = systemRecovery ? original.Target : null;
        storage = new RunStorage(path);
        progressSink = progress;
        progressLabel = "Recovery";
        Log("Recovery run directory: " + storage.DirectoryPath);
        try
        {
            foreach (var ownership in new[] { "ram-read-reference-owned.json", StressOwnership }.Select(name => Path.Combine(path, name)))
                if (File.Exists(ownership))
                    await Worker(Job("ram-read-cleanup") with { OraclePath = ownership }, token, 300);
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(path, "manifest.json"), token));
            var suite = manifest.RootElement.GetProperty("Options").GetProperty("Suite").GetString()
                ?? throw new InvalidDataException("The run manifest has no suite.");
            var mutatesCopyFlags = suite is "cache-read-misses" or "cache-read-bookkeeping" or "tree-decision" or "read-tail-decision";
            ulong? recoveryCopyFlags = null;
            if (mutatesCopyFlags && original.CopyFlags is null && suite == "cache-read-misses")
            {
                var evidencePath = Path.Combine(path, "copy-flags-original.json");
                if (File.Exists(evidencePath))
                {
                    try
                    {
                        var evidence = JsonSerializer.Deserialize<CacheLayoutSnapshot>(await File.ReadAllTextAsync(evidencePath, token))
                            ?? throw new InvalidDataException("Legacy copy-flags evidence is empty.");
                        recoveryCopyFlags = VerificationWorker.ValidateLegacyCopyFlags(original, evidence);
                        storage.Write("copy-flags-recovery-source.json", new
                        {
                            Status = "VALIDATED",
                            Source = "copy-flags-original.json",
                            SourceSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(evidencePath, token))),
                            Target = original.Target,
                            OriginalInstance = original.State.Instance,
                            OriginalGeneration = original.State.Generation,
                            CopyFlags = recoveryCopyFlags
                        });
                    }
                    catch (Exception ex) when (ex is IOException || ex is InvalidDataException ||
                                               ex is UnauthorizedAccessException || ex is JsonException)
                    {
                        storage.Write("copy-flags-recovery-source.json", new
                        {
                            Status = "REJECTED",
                            Source = "copy-flags-original.json",
                            Target = original.Target,
                            Reason = ex.Message
                        });
                    }
                }
                else storage.Write("copy-flags-recovery-source.json", new
                {
                    Status = "MISSING",
                    Source = "copy-flags-original.json",
                    Target = original.Target,
                    Reason = "No trustworthy pre-mutation copy-flags evidence is available."
                });
            }
            var operation = systemRecovery ? "system-restore" : "restore";
            var resultsPath = Path.Combine(path, "results.json");
            var priorResults = File.Exists(resultsPath)
                ? JsonSerializer.Deserialize<CaseResult[]>(await File.ReadAllTextAsync(resultsPath, token)) ?? []
                : [];
            await Worker(Job(operation) with
            {
                Recovery = snapshotPath,
                SystemInstance = systemRecovery ? original.Target.Instance : null,
                SystemBytes = systemRecovery ? original.Target.DiskBytes : null,
                RecoverableVm = systemRecovery,
                OraclePath = systemRecovery ? Path.Combine(path, "oracle.json") : null,
                RequireImageEvidence = systemRecovery && VerificationWorker.RequiresSystemImageEvidence(priorResults),
                AcceptsLabErrors = !systemRecovery && VerificationPlan.InjectsLabFaults(suite),
                RecoveryCopyFlags = recoveryCopyFlags,
                RequireCopyFlagsRestore = mutatesCopyFlags,
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
