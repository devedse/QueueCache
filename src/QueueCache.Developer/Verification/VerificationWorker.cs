using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

public sealed record WorkerJob(string Operation, string Volume, string Reply, DiskTarget? Expected = null,
    CacheConfiguration? Configuration = null, WriteCacheAction Action = WriteCacheAction.Flush,
    ulong Value = 0, string? Recovery = null, int Seconds = 0, string? StopFile = null,
    string? WorkDirectory = null, int BudgetMiB = 1024, string? ReadyFile = null,
    string? SystemInstance = null, long? SystemBytes = null, bool RecoverableVm = false,
    string? OraclePath = null, bool RequireImageEvidence = false);
public sealed record RecoverySnapshot(int SchemaVersion, DiskTarget Target, WriteCacheState State,
    bool Timing, string Profiles, DateTimeOffset Captured, string Machine);

/// <summary>Runs inside a child of the same CLI. A blocking driver call cannot trap the coordinator.</summary>
[SupportedOSPlatform("windows")]
public static class VerificationWorker
{
    public static void ValidateFileTarget(DiskTarget target, DiskDescription inventory)
    {
        if (inventory.IsBoot || inventory.IsSystem || inventory.IsPaging)
            throw new IOException("File-only verification excludes boot/system/paging disks.");
        if (inventory.Number != target.Number || inventory.Bytes != target.Bytes ||
            !string.Equals(inventory.Instance, target.Instance, StringComparison.OrdinalIgnoreCase))
            throw new IOException("File-only target identity changed.");
    }

    public static void ReportFailures(IEnumerable<CheckResult> checks, TextWriter error)
    {
        foreach (var check in checks.Where(c => c.Result != "PASS"))
            error.WriteLine($"{check.Result}: {check.Name}: {check.Detail}");
    }
    public static void ValidateActiveImageRouting(WriteCacheState enabled, WriteCacheState observed,
        ulong requiredAcceptedBytes)
    {
        if (!observed.Enabled || observed.Faulted || observed.Suspended || observed.Removed ||
            observed.LastError != 0 || observed.BudgetBytes == 0 || observed.ReservedBytes == 0 ||
            observed.PayloadCapacity == 0 || observed.Instance != enabled.Instance ||
            observed.Errors != enabled.Errors || observed.SupportsReadWrite && !observed.RoutingConfirmed)
            throw new IOException("The C: cache did not remain enabled, routed and error-free throughout the image write.");
        if (observed.AcceptedBytes < enabled.AcceptedBytes ||
            observed.AcceptedBytes - enabled.AcceptedBytes < requiredAcceptedBytes)
            throw new IOException("The C: cache did not report accepting the complete image write.");
    }
    public static bool RequiresSystemImageEvidence(IEnumerable<CaseResult> results) =>
        results.Any(result => result.Id == "system-active-image" && result.Status == "PASS");
    public static bool RequiresClearSystemUsagePaths(string operation) => operation != "system-image-baseline";
    public static void ValidateSystemUsageDiagnostics(CacheStatistics statistics, CacheDiagnostics diagnostics)
    {
        var paths = diagnostics.UsagePaths ??
            throw new NotSupportedException("System verification requires split usage-path diagnostics.");
        var activity = diagnostics.UsageActivity ??
            throw new NotSupportedException("System verification requires usage-notification lifecycle diagnostics.");
        static ulong Outstanding(CacheUsageActivity value, string name)
        {
            if (value.InRequests != value.InSuccesses + value.InFailures ||
                value.OutRequests != value.OutSuccesses + value.OutFailures)
                throw new IOException($"{name} usage notification is still pending or its lifecycle counters are inconsistent.");
            if (value.OutSuccesses > value.InSuccesses)
                throw new IOException($"{name} usage notification has more successful removals than additions.");
            return value.InSuccesses - value.OutSuccesses;
        }
        var paging = Outstanding(activity.Paging, "Paging");
        var hibernation = Outstanding(activity.Hibernation, "Hibernation");
        var dump = Outstanding(activity.Dump, "Dump");
        if (paging != paths.Paging || hibernation != paths.Hibernation || dump != paths.Dump ||
            statistics.PagingPathCount < 0 || (ulong)statistics.PagingPathCount != paging + hibernation + dump)
            throw new IOException("Current system usage paths do not reconcile with completed lifecycle notifications.");
    }
    public static string Profiles() => JsonSerializer.Serialize(SavedConfigurations.List().OrderBy(p => p.Instance));
    public static void FlushForRestoration(Action flushVolume, Action flushCache, Action disableCache,
        Func<WriteCacheState> snapshot, Action<string, WriteCacheState> record)
    {
        record("before-volume-flush", snapshot());
        flushVolume();
        record("after-volume-flush", snapshot());
        flushCache();
        record("after-cache-flush", snapshot());
        disableCache();
        record("after-cache-disable", snapshot());
    }
    public static IReadOnlyList<string> RestorationMismatches(RecoverySnapshot original,
        WriteCacheState restored, string profiles, ulong timing)
    {
        var mismatches = new List<string>();
        void Compare<T>(string name, T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                mismatches.Add($"{name}: expected {JsonSerializer.Serialize(expected)}, actual {JsonSerializer.Serialize(actual)}");
        }
        Compare(nameof(restored.DirtyBytes), 0UL, restored.DirtyBytes);
        Compare(nameof(restored.InFlightBytes), 0UL, restored.InFlightBytes);
        Compare(nameof(restored.Errors), original.State.Errors, restored.Errors);
        Compare(nameof(restored.Instance), original.State.Instance, restored.Instance);
        Compare(nameof(original.Profiles), original.Profiles, profiles);
        Compare(nameof(restored.BudgetBytes), original.State.BudgetBytes, restored.BudgetBytes);
        Compare(nameof(restored.Enabled), original.State.Enabled, restored.Enabled);
        Compare(nameof(restored.UnsafeDefer), original.State.UnsafeDefer, restored.UnsafeDefer);
        Compare(nameof(restored.Options), original.State.Options, restored.Options);
        Compare(nameof(original.Timing), original.Timing ? 1UL : 0UL, timing);
        return mismatches;
    }
    public static async Task<int> ExecuteAsync(string jobPath)
    {
        var job = JsonSerializer.Deserialize<WorkerJob>(await File.ReadAllTextAsync(jobPath)) ?? throw new InvalidDataException("Missing worker job.");
        void Stage(string name)
        {
            Console.Error.WriteLine($"[{DateTimeOffset.UtcNow:O}] Worker {job.Operation}: {name}");
            Console.Error.Flush();
        }
        Stage("job loaded; validating target");
        DiskTarget target;
        if (job.Expected is { } expected)
        {
            // Repeated Get-Disk/CIM discovery can block behind a loaded storage stack. The coordinator already
            // captured the identity. Native calls are synchronous; the coordinator bounds the worker lifetime.
            if (!string.Equals(job.Volume, $"{expected.Letter}:", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Worker volume disagrees with the recorded target.");
            try
            {
                expected.ValidateCurrent();
            }
            catch (Exception ex) { throw new IOException($"Worker {job.Operation}: native target validation failed for {job.Volume}.", ex); }
            target = expected;
        }
        else
            target = await DiskTarget.InspectAsync(job.Volume);
        if (job.Operation is "system-preflight" or "system-file-create" or "system-file-verify" or
            "system-capture" or "system-image-baseline" or "system-active-image" or "system-restore")
        {
            if (!job.RecoverableVm || string.IsNullOrWhiteSpace(job.SystemInstance) || job.SystemBytes is null or <= 0)
                throw new IOException("Missing explicit recoverable-VM acknowledgement or expected system-disk identity.");
            var outputVolume = SystemPreflightGuard.OutputVolume(Path.GetDirectoryName(job.Reply)!);
            var output = await DiskTarget.InspectAsync(outputVolume);
            target.ValidateCurrent();
            output.ValidateCurrent();
            SystemPreflightGuard.ValidateTargets(target, output, job.SystemInstance, job.SystemBytes.Value);
            if (job.Operation is "system-capture" or "system-image-baseline" or "system-active-image" or "system-restore")
            {
                if (job.Operation is "system-image-baseline" or "system-active-image")
                {
                    if (string.IsNullOrWhiteSpace(job.OraclePath))
                        throw new IOException("Missing off-target system-image oracle path.");
                    var oracleOutput = await DiskTarget.InspectAsync(SystemPreflightGuard.OutputVolume(
                        Path.GetDirectoryName(Path.GetFullPath(job.OraclePath))!));
                    oracleOutput.ValidateCurrent();
                    SystemPreflightGuard.ValidateTargets(target, oracleOutput, job.SystemInstance, job.SystemBytes.Value);
                }
                using var systemDevice = new CacheDevice(target.Device, writable: true);
                var before = systemDevice.GetWriteCacheState();
                var statistics = systemDevice.GetStatistics();
                ValidateSystemUsageDiagnostics(statistics, systemDevice.GetDiagnostics());
                if (RequiresClearSystemUsagePaths(job.Operation) &&
                    (target.IsPaging || statistics.PagingPathCount != 0))
                    throw new IOException("Active system verification requires C: to have no paging, hibernation or dump usage path.");
                if (before.DeviceBytes != (ulong)target.Bytes || before.Faulted || before.Errors != 0 || before.LastError != 0)
                    throw new IOException("System-disk driver identity/state is not clean enough for active verification.");
                if (job.Operation == "system-capture")
                {
                    if (before.Enabled || before.BudgetBytes != 0 || before.DirtyBytes != 0 || before.InFlightBytes != 0)
                        throw new IOException("Active system verification must start with C: disabled, released and clean.");
                    if (SavedConfigurations.List().Any(profile =>
                            profile.Instance.Equals(target.Instance, StringComparison.OrdinalIgnoreCase)))
                        throw new IOException("Remove the saved C: profile before active system verification.");
                    RunStorage.AtomicJson(job.Reply, new RecoverySnapshot(1, target, before,
                        systemDevice.GetPerformance().TimingEnabled != 0, Profiles(), DateTimeOffset.UtcNow, Environment.MachineName));
                    return 0;
                }
                if (job.Operation == "system-restore")
                {
                    var original = JsonSerializer.Deserialize<RecoverySnapshot>(await File.ReadAllTextAsync(job.Recovery!)) ??
                        throw new InvalidDataException("Missing system recovery snapshot.");
                    if (original.SchemaVersion != 1 || original.Machine != Environment.MachineName ||
                        original.Target != target || original.State.Enabled || original.State.BudgetBytes != 0)
                        throw new IOException("System recovery identity or disabled/released baseline changed.");
                    FlushForRestoration(() =>
                    {
                        using var volume = new FileTests.CheckedVolume(target.Letter, target.Number, target.Bytes, writable: true);
                        volume.Flush();
                    }, () => systemDevice.Control(WriteCacheAction.Flush), () => systemDevice.Control(WriteCacheAction.Disable),
                        systemDevice.GetWriteCacheState,
                        (phase, snapshot) => RunStorage.AtomicJson(job.Reply + "." + phase + ".json", snapshot));
                    systemDevice.Control(WriteCacheAction.Release);
                    systemDevice.Control(WriteCacheAction.FlushPolicy, value: original.State.UnsafeDefer ? 1UL : 0UL);
                    if (original.State.Options is not null)
                        systemDevice.SetOptions(original.State.Options);
                    var restored = systemDevice.GetWriteCacheState();
                    RunStorage.AtomicJson(job.Reply, restored);
                    var mismatches = RestorationMismatches(original, restored, Profiles(), systemDevice.GetPerformance().TimingEnabled);
                    if (mismatches.Count != 0)
                        throw new IOException("System restoration mismatch: " + string.Join("; ", mismatches));
                    if (job.RequireImageEvidence &&
                        (string.IsNullOrWhiteSpace(job.OraclePath) || !File.Exists(job.OraclePath)))
                        throw new IOException("Required post-release system-image oracle is missing.");
                    if (!string.IsNullOrWhiteSpace(job.OraclePath) && File.Exists(job.OraclePath))
                    {
                        var oracle = SystemImageScenarios.ReadOracle(job.OraclePath);
                        var postReleaseChecks = File.Exists(oracle.FilePath)
                            ? SystemImageScenarios.Verify(target, oracle)
                            : new[] { new CheckResult("system-image/post-release-bytes", "SKIP",
                                "The active operation failed before creating its owned image; restoration itself completed.") };
                        RunStorage.AtomicJson(job.Reply + ".post-release-checks.json", postReleaseChecks);
                        ReportFailures(postReleaseChecks, Console.Error);
                        if (job.RequireImageEvidence && postReleaseChecks.Any(check => check.Result != "PASS"))
                            throw new IOException("Required system-image bytes were unavailable after cache disable/release restoration.");
                        if (postReleaseChecks.Any(check => check.Result is not ("PASS" or "SKIP")))
                            throw new IOException("System-image bytes failed after cache disable/release restoration.");
                    }
                    return 0;
                }
                if (job.Operation == "system-image-baseline")
                {
                    if (string.IsNullOrWhiteSpace(job.WorkDirectory) || string.IsNullOrWhiteSpace(job.OraclePath))
                        throw new IOException("Missing uncached system-image workload or oracle path.");
                    if (before.Enabled || before.BudgetBytes != 0 || before.DirtyBytes != 0 || before.InFlightBytes != 0)
                        throw new IOException("Uncached system-image baseline requires C: disabled, released and clean.");
                    var baselineChecks = new List<CheckResult>();
                    baselineChecks.AddRange(SystemImageScenarios.Create(target, job.WorkDirectory, job.OraclePath));
                    var afterWrite = systemDevice.GetWriteCacheState();
                    RunStorage.AtomicJson(job.Reply + ".after-application-flush.json", afterWrite);
                    if (afterWrite.Enabled || afterWrite.BudgetBytes != 0 || afterWrite.Instance != before.Instance ||
                        afterWrite.Errors != before.Errors || afterWrite.Faulted || afterWrite.LastError != 0)
                        throw new IOException("C: cache state changed during the uncached image baseline.");
                    baselineChecks.AddRange(SystemImageScenarios.Verify(target, SystemImageScenarios.ReadOracle(job.OraclePath)));
                    RunStorage.AtomicJson(job.Reply, baselineChecks);
                    ReportFailures(baselineChecks, Console.Error);
                    return baselineChecks.All(check => check.Result == "PASS") ? 0 : 1;
                }
                if (string.IsNullOrWhiteSpace(job.WorkDirectory) || string.IsNullOrWhiteSpace(job.OraclePath) ||
                    job.Configuration is null)
                    throw new IOException("Missing active system-image workload, oracle or configuration.");
                if (before.Enabled || before.BudgetBytes != 0 || before.DirtyBytes != 0 || before.InFlightBytes != 0)
                    throw new IOException("Active system-image workload must start from the captured disabled/released state.");
                var active = ConfigurationManager.ApplyForRecoverableSystemVerification(target, job.Configuration, true);
                RunStorage.AtomicJson(job.Reply + ".enabled.json", active);
                var checks = new List<CheckResult>();
                checks.AddRange(SystemImageScenarios.Create(target, job.WorkDirectory, job.OraclePath));
                var afterApplicationFlush = systemDevice.GetWriteCacheState();
                RunStorage.AtomicJson(job.Reply + ".after-application-flush.json", afterApplicationFlush);
                ValidateActiveImageRouting(active, afterApplicationFlush, (ulong)SystemImageScenarios.FileBytes);
                checks.Add(new("system-image/cache-accepted-bytes", "PASS",
                    $"The active C: cache accepted at least the complete image ({afterApplicationFlush.AcceptedBytes - active.AcceptedBytes} bytes observed)."));
                checks.AddRange(SystemImageScenarios.Verify(target, SystemImageScenarios.ReadOracle(job.OraclePath)));
                systemDevice.Control(WriteCacheAction.Flush);
                var afterAdministrativeFlush = systemDevice.GetWriteCacheState();
                RunStorage.AtomicJson(job.Reply + ".after-administrative-flush.json", afterAdministrativeFlush);
                ValidateActiveImageRouting(active, afterAdministrativeFlush, (ulong)SystemImageScenarios.FileBytes);
                checks.Add(new("system-image/administrative-flush", "PASS",
                    "The administrative flush returned while the cache remained routed and error-free; unrelated live C: writes may already be dirty again."));
                checks.AddRange(SystemImageScenarios.Verify(target, SystemImageScenarios.ReadOracle(job.OraclePath)));
                RunStorage.AtomicJson(job.Reply, checks);
                ReportFailures(checks, Console.Error);
                return checks.All(check => check.Result == "PASS") ? 0 : 1;
            }
            if (job.Operation != "system-preflight")
            {
                if (string.IsNullOrWhiteSpace(job.OraclePath))
                    throw new IOException("Missing off-target system-file oracle path.");
                var oracleOutput = await DiskTarget.InspectAsync(SystemPreflightGuard.OutputVolume(Path.GetDirectoryName(Path.GetFullPath(job.OraclePath))!));
                oracleOutput.ValidateCurrent();
                SystemPreflightGuard.ValidateTargets(target, oracleOutput, job.SystemInstance, job.SystemBytes.Value);
                using var beforeObservation = new CacheDevice(target.Device, writable: false);
                var before = beforeObservation.GetWriteCacheState();
                if (before.DeviceBytes != (ulong)target.Bytes)
                    throw new IOException("System-disk driver size disagrees with inventory.");
                if (before.Faulted || before.Errors != 0 || before.LastError != 0)
                    throw new IOException("System-disk cache reports a fault; do not start a file workload or treat a byte read as recovery.");
                RunStorage.AtomicJson(job.Reply + ".before.json", before);
                IReadOnlyList<CheckResult> checks = job.Operation == "system-file-create"
                    ? SystemFileScenarios.Create(target, job.WorkDirectory!, job.OraclePath)
                    : SystemFileScenarios.Verify(target, SystemFileScenarios.ReadOracle(job.OraclePath));
                RunStorage.AtomicJson(job.Reply, checks);
                var after = beforeObservation.GetWriteCacheState();
                RunStorage.AtomicJson(job.Reply + ".after.json", after);
                if (after.Faulted || after.Errors != before.Errors || after.LastError != before.LastError)
                    throw new IOException("System-disk cache reported a new error during the file check.");
                ReportFailures(checks, Console.Error);
                return checks.Count > 0 && checks.All(check => check.Result == "PASS") ? 0 : 1;
            }
            using var observation = new CacheDevice(target.Device, writable: false);
            var state = observation.GetWriteCacheState();
            if (state.DeviceBytes != (ulong)target.Bytes)
                throw new IOException("System-disk driver size disagrees with inventory.");
            RunStorage.AtomicJson(job.Reply, new
            {
                Target = target, Output = output, State = state,
                Statistics = observation.GetStatistics(), Observed = DateTimeOffset.UtcNow,
                CacheMutation = false, WorkloadMutation = false
            });
            return 0;
        }
        if (job.Operation is "capture-file" or "trim-file")
        {
            var inventory = (await DiskCatalog.ListAsync()).Single(d => d.Number == target.Number);
            ValidateFileTarget(target, inventory);
            target.ValidateCurrent();
            if (job.Operation == "capture-file")
            {
                RunStorage.AtomicJson(job.Reply, target);
                return 0;
            }
            var checks = DiskWorkloads.TrimFile(target, job.WorkDirectory!);
            RunStorage.AtomicJson(job.Reply, checks);
            ReportFailures(checks, Console.Error);
            return checks.All(check => check.Result != "FAIL") ? 0 : 1;
        }
        Stage("target validated; opening cache device");
        using var device = new CacheDevice(target.Device, writable: true);
        Stage("cache device opened; validating device length");
        if (job.Expected is not null)
            DiskTarget.ValidateDeviceLength(target, device.GetWriteCacheState().DeviceBytes);
        Stage("device length validated; dispatching operation");
        object result;
        switch (job.Operation)
        {
            case "capture":
                // Testing a data partition on the OS physical disk is also excluded.
                var inventory = (await DiskCatalog.ListAsync()).Single(d => d.Number == target.Number);
                if (inventory.IsBoot || inventory.IsSystem || device.GetStatistics().PagingPathCount != 0)
                    throw new IOException("Verification excludes boot/system/paging disks.");
                var state = device.GetWriteCacheState();
                ConfigurationManager.EnsureHealthy(state);
                if (!state.SupportsPerformance || !state.SupportsReadWrite || !state.SupportsDropClean)
                    throw new NotSupportedException("Verification requires the current cache/performance protocol.");
                if (state.DirtyBytes != 0)
                    throw new IOException("Start with a clean cache; drain your workload first.");
                result = new RecoverySnapshot(1, target, state, device.GetPerformance().TimingEnabled != 0,
                    Profiles(), DateTimeOffset.UtcNow, Environment.MachineName);
                break;
            case "restore":
                var original = JsonSerializer.Deserialize<RecoverySnapshot>(await File.ReadAllTextAsync(job.Recovery!))!;
                if (original.SchemaVersion != 1 || original.Machine != Environment.MachineName || original.Target != target)
                    throw new IOException("Recovery identity/version mismatch.");
                // This suite never sets fault injection. Clear only its delay hook; do not hide device faults.
                device.Control(WriteCacheAction.LabDelay, value: 0);
                FlushForRestoration(() =>
                {
                    Stage("flushing filesystem volume before cache drain");
                    using var volume = new FileTests.CheckedVolume(target.Letter, target.Number, target.Bytes, writable: true);
                    volume.Flush();
                }, () =>
                {
                    Stage("filesystem volume flushed; draining cache");
                    device.Control(WriteCacheAction.Flush);
                }, () =>
                {
                    Stage("cache flushed; disabling and draining remaining writes");
                    device.Control(WriteCacheAction.Disable);
                }, device.GetWriteCacheState,
                    (phase, snapshot) => RunStorage.AtomicJson(job.Reply + "." + phase + ".json", snapshot));
                if (original.State.BudgetBytes == 0)
                {
                    device.Control(WriteCacheAction.Release);
                    device.Control(WriteCacheAction.FlushPolicy, value: original.State.UnsafeDefer ? 1UL : 0UL);
                    if (original.State.Options is not null)
                        device.SetOptions(original.State.Options);
                }
                else
                {
                    ConfigurationManager.WaitForHealthyState(device.GetWriteCacheState, original.State);
                    ConfigurationManager.Apply(target, CacheConfiguration.FromState(original.State), true);
                }
                device.Control(WriteCacheAction.PerformanceTiming, value: original.Timing ? 1UL : 0UL);
                var restored = ConfigurationManager.WaitForHealthyState(device.GetWriteCacheState, original.State);
                var restoredProfiles = Profiles();
                var restoredTiming = device.GetPerformance().TimingEnabled;
                var mismatches = RestorationMismatches(original, restored, restoredProfiles, restoredTiming);
                if (mismatches.Count != 0)
                {
                    RunStorage.AtomicJson(job.Reply + ".mismatch.json", new
                    {
                        State = restored, Profiles = restoredProfiles, Timing = restoredTiming, Mismatches = mismatches
                    });
                    throw new IOException("Restoration/state/profile verification failed: " + string.Join("; ", mismatches));
                }
                result = restored;
                break;
            case "configure":
                result = ConfigurationManager.Apply(target, job.Configuration!, true);
                break;
            case "control":
                device.Control(job.Action, value: job.Value);
                result = device.GetWriteCacheState();
                break;
            case "snapshot":
                result = new
                {
                    State = device.GetWriteCacheState(),
                    Performance = device.GetPerformance(),
                    Diagnostics = device.GetDiagnostics()
                };
                break;
            case "files":
                var report = await DiskWorkloads.TestAsync(target, new Progress<string>(Console.WriteLine));
                RunStorage.AtomicJson(job.Reply, report);
                if (!report.Passed)
                    ReportFailures(report.Checks, Console.Error);
                return report.Passed ? 0 : 1;
            case "policies":
                var checks = await CacheScenarios.RunAsync(target, new Progress<string>(Console.WriteLine));
                RunStorage.AtomicJson(job.Reply, checks);
                ReportFailures(checks, Console.Error);
                if (checks.Count == 0)
                    Console.Error.WriteLine("Policy suite returned no checks.");
                return checks.Count > 0 && checks.All(c => c.Result == "PASS") ? 0 : 1;
            case "pressure":
                var pressureChecks = await PressureScenarios.RunAsync(target, new Progress<string>(Console.WriteLine));
                RunStorage.AtomicJson(job.Reply, pressureChecks);
                ReportFailures(pressureChecks, Console.Error);
                if (pressureChecks.Count == 0)
                    Console.Error.WriteLine("Pressure suite returned no checks.");
                return pressureChecks.Count > 0 && pressureChecks.All(c => c.Result == "PASS") ? 0 : 1;
            case "seed-drain":
                var seedBytes = checked(job.BudgetMiB / 4 * (1 << 20));
                result = DrainDecisionScenarios.PrepareDirtySet(device,
                    Path.Combine(job.WorkDirectory!, "drain.dat"), seedBytes,
                    checked((int)job.Value));
                break;
            case "verify-drain":
                var verifyBytes = checked(job.BudgetMiB / 4 * (1 << 20));
                DrainDecisionScenarios.VerifyDirtySet(
                    Path.Combine(job.WorkDirectory!, "drain.dat"), verifyBytes,
                    checked((int)job.Value));
                result = new { Seed = job.Value, Bytes = verifyBytes, Verified = true };
                break;
            case "prepare":
                var directory = Path.GetFullPath(job.WorkDirectory!);
                if (!directory.StartsWith(target.Root, StringComparison.OrdinalIgnoreCase) || Directory.Exists(directory))
                    throw new IOException("Workload directory must be a new directory on the selected volume.");
                if (new DriveInfo(target.Root).AvailableFreeSpace < ((long)job.BudgetMiB * 3 + 1024) * 1024 * 1024)
                    throw new IOException("Insufficient free space for unique workloads and headroom.");
                Directory.CreateDirectory(directory);
                var block = new byte[1 << 20];
                Random.Shared.NextBytes(block);
                foreach (var (name, length) in new[] { ("hot.dat", job.BudgetMiB / 4), ("writer.dat", job.BudgetMiB * 2), ("resident.dat", job.BudgetMiB / 2), ("drain.dat", job.BudgetMiB / 4), ("flush.dat", 1) })
                {
                    using var file = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    for (var i = 0; i < length; i++)
                        file.Write(block);
                    file.Flush(true);
                }
                result = new
                {
                    Directory = directory
                };
                break;
            case "application-flush":
                using (var file = new FileStream(Path.Combine(job.WorkDirectory!, "flush.dat"), FileMode.Open,
                    FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    var timer = Stopwatch.StartNew();
                    var count = 0;
                    while (timer.Elapsed.TotalSeconds < job.Seconds && !File.Exists(job.StopFile))
                    {
                        file.Flush(true);
                        count++;
                        await Task.Delay(250);
                    }
                    result = new
                    {
                        Flushes = count
                    };
                }
                break;
            case "telemetry":
                Stage("opening telemetry output");
                await using (var writer = new StreamWriter(job.Reply, append: false))
                {
                    var timer = Stopwatch.StartNew();
                    while (timer.Elapsed.TotalSeconds < job.Seconds)
                    {
                        // On stop, take one final sample covering workload completion.
                        var stopping = File.Exists(job.StopFile);
                        var observedState = device.GetWriteCacheState();
                        var observedPerformance = device.GetPerformance();
                        var observedDiagnostics = device.GetDiagnostics();
                        await writer.WriteLineAsync(JsonSerializer.Serialize(new
                        {
                            Utc = DateTimeOffset.UtcNow,
                            ElapsedSeconds = timer.Elapsed.TotalSeconds,
                            State = observedState,
                            Performance = observedPerformance,
                            Diagnostics = observedDiagnostics
                        }));
                        await writer.FlushAsync();
                        if (job.ReadyFile is not null && !File.Exists(job.ReadyFile))
                            RunStorage.AtomicJson(job.ReadyFile, new
                            {
                                Ready = true
                            });
                        if (stopping)
                            break;
                        await Task.Delay(200);
                    }
                }
                return 0;
            default:
                throw new ArgumentException("Unknown verification worker operation.");
        }
        RunStorage.AtomicJson(job.Reply, result);
        return 0;
    }

    public static object Provenance(string executable)
    {
        using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\qcachelab");
        var registered = service?.GetValue("ImagePath")?.ToString();
        var binary = registered?.Trim('"').Replace(@"\??\", "");
        if (binary?.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase) == true)
            binary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), binary[12..]);
        string? Hash(string? path) => path is not null && File.Exists(path) ?
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;
        return new
        {
            Machine = Environment.MachineName,
            OS = Environment.OSVersion.ToString(),
            Cli = executable,
            CliSha256 = Hash(executable),
            CliVersion = FileVersionInfo.GetVersionInfo(executable).FileVersion,
            EntryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.Location,
            EntryAssemblySha256 = Hash(System.Reflection.Assembly.GetEntryAssembly()?.Location),
            RegisteredDriverPath = registered,
            RegisteredDriverSha256 = Hash(binary),
            Note = "Registered binary is not proof of loaded binary after an upgrade. Record/reboot to the intended release before comparison."
        };
    }
}
