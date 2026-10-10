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
    string? OraclePath = null, bool RequireImageEvidence = false,
    string[]? ImageOraclePaths = null, bool AcceptsLabErrors = false,
    string? DisposableInstance = null, long? DisposableBytes = null,
    string? ManagedOraclePath = null, ManagedLifecycleTransition? ManagedTransition = null,
    string? ProductExecutable = null, string[]? ProductPrefix = null, string[]? Files = null);
/// <summary>ReadRecall: QcLabReadRecall mode at capture (null: the driver has none), restored afterwards.</summary>
public sealed record RecoverySnapshot(int SchemaVersion, DiskTarget Target, WriteCacheState State,
    bool Timing, string Profiles, DateTimeOffset Captured, string Machine, ulong? ReadRecall = null);
public sealed record ReadPassResult(long Bytes, long Requests, double Seconds);

/// <summary>Runs inside a child of the same CLI. A blocking driver call cannot trap the coordinator.</summary>
[SupportedOSPlatform("windows")]
public static class VerificationWorker
{
    public static void ValidateFileTarget(DiskTarget target, DiskDescription inventory)
    {
        if (inventory.IsBoot || inventory.IsSystem || inventory.IsPaging)
            throw new IOException("File-only verification excludes boot/system/paging disks.");
        if (inventory.Number != target.Number || inventory.Bytes != target.DiskBytes ||
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
        results.Any(result => result.Id.StartsWith("system-active-image-", StringComparison.Ordinal) &&
                              result.Status == "PASS");
    public static bool AllowsSystemUsagePaths(string operation) =>
        operation is "system-capture" or "system-active-image" or "system-restore" or "system-app-session" or
            "system-file-create" or "system-file-verify";
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
    public static CachePagingIo PagingIoDelta(CacheDiagnostics before, CacheDiagnostics after)
    {
        var first = before.PagingIo ??
            throw new NotSupportedException("System image verification requires paging-I/O diagnostics.");
        var last = after.PagingIo ??
            throw new NotSupportedException("System image verification requires paging-I/O diagnostics.");
        if (last.ReadRequests < first.ReadRequests || last.ReadBytes < first.ReadBytes ||
            last.WriteRequests < first.WriteRequests || last.WriteBytes < first.WriteBytes)
            throw new IOException("Paging-I/O lifetime counters moved backwards during the observation window.");
        return last with
        {
            ReadRequests = last.ReadRequests - first.ReadRequests,
            ReadBytes = last.ReadBytes - first.ReadBytes,
            WriteRequests = last.WriteRequests - first.WriteRequests,
            WriteBytes = last.WriteBytes - first.WriteBytes
        };
    }
    public static void ValidateActiveSystemPaths(CacheStatistics statistics, CacheDiagnostics diagnostics)
    {
        ValidateSystemUsageDiagnostics(statistics, diagnostics);
        if (diagnostics.PagingIo is null || diagnostics.PagingProgress is null || diagnostics.PagingRoute is null)
            throw new NotSupportedException(
                "Active C: verification requires Diagnostics V7 routed paging progress data.");
    }
    public static CachePagingProgress ValidatePagingProgressWindow(CacheDiagnostics before, CacheDiagnostics after)
    {
        var first = before.PagingProgress ??
            throw new NotSupportedException("Active system verification requires paging progress diagnostics.");
        var last = after.PagingProgress ??
            throw new NotSupportedException("Active system verification requires paging progress diagnostics.");
        if (last.MapFailures < first.MapFailures || last.CapacityWaits < first.CapacityWaits ||
            last.ServicedReadMisses < first.ServicedReadMisses)
            throw new IOException("Paging progress counters moved backwards during the active window.");
        var delta = last with
        {
            MapFailures = last.MapFailures - first.MapFailures,
            CapacityWaits = last.CapacityWaits - first.CapacityWaits,
            ServicedReadMisses = last.ServicedReadMisses - first.ServicedReadMisses
        };
        if (delta.MapFailures != 0 || delta.CapacityWaits != 0 || last.ReservedBytes != 0)
            throw new IOException(
                "Paging-marked I/O encountered a mapping or ordinary cache-capacity failure.");
        return delta;
    }
    public static CachePagingRoute ValidatePagingRouteWindow(CacheDiagnostics before, CacheDiagnostics after)
    {
        var first = before.PagingRoute ??
            throw new NotSupportedException("Active system verification requires routed paging diagnostics V7.");
        var last = after.PagingRoute ??
            throw new NotSupportedException("Active system verification requires routed paging diagnostics V7.");
        if (last.ReadRequests < first.ReadRequests || last.ReadCompletions < first.ReadCompletions ||
            last.ReadFailures < first.ReadFailures || last.WriteRequests < first.WriteRequests ||
            last.WriteCompletions < first.WriteCompletions || last.WriteFailures < first.WriteFailures ||
            last.OverlapWaits < first.OverlapWaits)
            throw new IOException("Routed paging counters moved backwards during the active window.");
        var delta = new CachePagingRoute(
            last.ReadRequests - first.ReadRequests, last.ReadCompletions - first.ReadCompletions,
            last.ReadFailures - first.ReadFailures, last.WriteRequests - first.WriteRequests,
            last.WriteCompletions - first.WriteCompletions, last.WriteFailures - first.WriteFailures,
            last.OverlapWaits - first.OverlapWaits);
        if (delta.ReadFailures != 0 || delta.WriteFailures != 0)
            throw new IOException("A routed paging-marked I/O request failed during the active window.");
        return delta;
    }
    public static string Profiles() => JsonSerializer.Serialize(SavedConfigurations.List().OrderBy(p => p.Instance));
    /// <summary>Returns the state once the cache is disabled: the point at which every pending byte must have
    /// been drained. Later states of a re-enabled cache can legitimately hold new writes from Windows.</summary>
    public static WriteCacheState FlushForRestoration(Action flushVolume, Action flushCache, Action disableCache,
        Func<WriteCacheState> snapshot, Action<string, WriteCacheState> record)
    {
        record("before-volume-flush", snapshot());
        flushVolume();
        record("after-volume-flush", snapshot());
        flushCache();
        record("after-cache-flush", snapshot());
        disableCache();
        var drained = snapshot();
        record("after-cache-disable", drained);
        return drained;
    }
    /// <param name="drained">The state when the cache was disabled during restoration: nothing may be pending
    /// or in flight there. A restored cache that is enabled again (Fast or Strict) can already hold new writes
    /// from Windows, so pending bytes are required to be zero afterwards only when it stays disabled.</param>
    public static IReadOnlyList<string> RestorationMismatches(RecoverySnapshot original,
        WriteCacheState drained, WriteCacheState restored, string profiles, ulong timing)
    {
        var mismatches = new List<string>();
        void Compare<T>(string name, T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                mismatches.Add($"{name}: expected {JsonSerializer.Serialize(expected)}, actual {JsonSerializer.Serialize(actual)}");
        }
        Compare("DirtyBytes when disabled", 0UL, drained.DirtyBytes);
        Compare("InFlightBytes when disabled", 0UL, drained.InFlightBytes);
        Compare("Enabled when disabled", false, drained.Enabled);
        if (!restored.Enabled)
        {
            Compare(nameof(restored.DirtyBytes), 0UL, restored.DirtyBytes);
            Compare(nameof(restored.InFlightBytes), 0UL, restored.InFlightBytes);
        }
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
            "system-capture" or "system-image-baseline" or "system-active-image" or "system-restore" or "system-app-session" or
            "system-paging-recognition")
        {
            if (!job.RecoverableVm || string.IsNullOrWhiteSpace(job.SystemInstance) || job.SystemBytes is null or <= 0)
                throw new IOException("Missing explicit recoverable-VM acknowledgement or expected system-disk identity.");
            var outputVolume = SystemPreflightGuard.OutputVolume(Path.GetDirectoryName(job.Reply)!);
            var output = await DiskTarget.InspectAsync(outputVolume);
            target.ValidateCurrent();
            output.ValidateCurrent();
            SystemPreflightGuard.ValidateTargets(target, output, job.SystemInstance, job.SystemBytes.Value);
            if (job.Operation is "system-capture" or "system-image-baseline" or "system-active-image" or "system-restore" or "system-app-session")
            {
                if (job.Operation is "system-image-baseline" or "system-active-image" or "system-app-session")
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
                var diagnosticsBefore = systemDevice.GetDiagnostics();
                ValidateSystemUsageDiagnostics(statistics, diagnosticsBefore);
                if (AllowsSystemUsagePaths(job.Operation))
                    ValidateActiveSystemPaths(statistics, diagnosticsBefore);
                else if (job.Operation != "system-image-baseline" && statistics.PagingPathCount != 0)
                    throw new IOException("This system operation does not support a paging, hibernation or dump usage path.");
                if (before.DeviceBytes != (ulong)target.Bytes || before.Faulted || before.Errors != 0 || before.LastError != 0)
                    throw new IOException("System-disk driver identity/state is not clean enough for active verification.");
                if (job.Operation == "system-capture")
                {
                    if (before.Enabled || before.BudgetBytes != 0 || before.DirtyBytes != 0 || before.InFlightBytes != 0)
                        throw new IOException("Active system verification must start with C: disabled, released and clean.");
                    if (SavedConfigurations.IsSaved(target.VolumeId))
                        throw new IOException("Remove the saved C: profile before active system verification.");
                    RunStorage.AtomicJson(job.Reply, new RecoverySnapshot(1, target, before,
                        systemDevice.GetPerformance().TimingEnabled != 0, Profiles(), DateTimeOffset.UtcNow, Environment.MachineName));
                    return 0;
                }
                if (job.Operation == "system-restore")
                {
                    var original = JsonSerializer.Deserialize<RecoverySnapshot>(await File.ReadAllTextAsync(job.Recovery!)) ??
                        throw new InvalidDataException("Missing system recovery snapshot.");
                    DiskTarget.ValidateRecordedSystemTarget(original.Target, target);
                    if (original.SchemaVersion != 1 || original.Machine != Environment.MachineName ||
                        original.State.Enabled || original.State.BudgetBytes != 0)
                        throw new IOException("System recovery identity or disabled/released baseline changed.");
                    var systemDrained = FlushForRestoration(() =>
                    {
                        using var volume = new FileTests.CheckedVolume(target.Letter, target.Number, target.DiskBytes, writable: true);
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
                    var mismatches = RestorationMismatches(original, systemDrained, restored, Profiles(), systemDevice.GetPerformance().TimingEnabled);
                    if (mismatches.Count != 0)
                        throw new IOException("System restoration mismatch: " + string.Join("; ", mismatches));
                    var requiredOracles = job.ImageOraclePaths ?? [];
                    if (job.RequireImageEvidence && requiredOracles.Length == 0)
                        throw new IOException("Required per-case system-image oracle list is missing.");
                    foreach (var oraclePath in requiredOracles)
                    {
                        if (!File.Exists(oraclePath))
                            throw new IOException("Required post-release system-image oracle is missing: " + oraclePath);
                        var oracle = SystemImageScenarios.ReadOracle(oraclePath);
                        if (!File.Exists(oracle.FilePath))
                            throw new IOException("Required post-release system-image file is missing: " + oracle.FilePath);
                        var restoredImageChecks = SystemImageScenarios.Verify(target, oracle);
                        RunStorage.AtomicJson(job.Reply + "." + Path.GetFileName(oraclePath) + ".post-release-checks.json", restoredImageChecks);
                        ReportFailures(restoredImageChecks, Console.Error);
                        if (restoredImageChecks.Any(check => check.Result != "PASS"))
                            throw new IOException("System-image bytes failed after cache disable/release restoration.");
                    }
                    if (requiredOracles.Length == 0 && !string.IsNullOrWhiteSpace(job.OraclePath) &&
                        File.Exists(job.OraclePath))
                    {
                        var oracle = SystemImageScenarios.ReadOracle(job.OraclePath);
                        if (File.Exists(oracle.FilePath))
                        {
                            var fallbackImageChecks = SystemImageScenarios.Verify(target, oracle);
                            RunStorage.AtomicJson(job.Reply + ".post-release-checks.json", fallbackImageChecks);
                            ReportFailures(fallbackImageChecks, Console.Error);
                            if (fallbackImageChecks.Any(check => check.Result != "PASS"))
                                throw new IOException("System-image bytes failed after cache disable/release restoration.");
                        }
                    }
                    return 0;
                }
                if (job.Operation == "system-image-baseline")
                {
                    if (string.IsNullOrWhiteSpace(job.WorkDirectory) || string.IsNullOrWhiteSpace(job.OraclePath))
                        throw new IOException("Missing uncached system-image workload or oracle path.");
                    if (before.Enabled || before.BudgetBytes != 0 || before.DirtyBytes != 0 || before.InFlightBytes != 0)
                        throw new IOException("Uncached system-image baseline requires C: disabled, released and clean.");
                    RunStorage.AtomicJson(job.Reply + ".before-diagnostics.json", diagnosticsBefore);
                    var baselineChecks = new List<CheckResult>();
                    baselineChecks.AddRange(SystemImageScenarios.Create(target, job.WorkDirectory, job.OraclePath));
                    var afterWrite = systemDevice.GetWriteCacheState();
                    RunStorage.AtomicJson(job.Reply + ".after-application-flush.json", afterWrite);
                    if (afterWrite.Enabled || afterWrite.BudgetBytes != 0 || afterWrite.Instance != before.Instance ||
                        afterWrite.Errors != before.Errors || afterWrite.Faulted || afterWrite.LastError != 0)
                        throw new IOException("C: cache state changed during the uncached image baseline.");
                    baselineChecks.AddRange(SystemImageScenarios.Verify(target, SystemImageScenarios.ReadOracle(job.OraclePath)));
                    var diagnosticsAfter = systemDevice.GetDiagnostics();
                    RunStorage.AtomicJson(job.Reply + ".after-diagnostics.json", diagnosticsAfter);
                    var pagingIo = PagingIoDelta(diagnosticsBefore, diagnosticsAfter);
                    baselineChecks.Add(new("system-image/paging-io-window", "PASS",
                        $"Device-wide paging I/O (all processes) observed during this window: reads={pagingIo.ReadRequests} " +
                        $"({pagingIo.ReadBytes} bytes), writes={pagingIo.WriteRequests} ({pagingIo.WriteBytes} bytes). " +
                        "Concurrent Windows activity may contribute to these counts."));
                    RunStorage.AtomicJson(job.Reply, baselineChecks);
                    ReportFailures(baselineChecks, Console.Error);
                    return baselineChecks.All(check => check.Result == "PASS") ? 0 : 1;
                }
                if (job.Operation == "system-app-session")
                {
                    if (string.IsNullOrWhiteSpace(job.WorkDirectory) || string.IsNullOrWhiteSpace(job.OraclePath) ||
                        job.Configuration is null || string.IsNullOrWhiteSpace(job.StopFile) || job.Seconds <= 0)
                        throw new IOException("Missing application-session workload, oracle, configuration or completion file.");
                    if (before.Enabled || before.BudgetBytes != 0 || before.DirtyBytes != 0 || before.InFlightBytes != 0)
                        throw new IOException("The application session must start from the captured disabled/released state.");
                    // Baseline written and verified while C: is uncached, so the session starts from known disk bytes.
                    var sessionChecks = new List<CheckResult>();
                    sessionChecks.AddRange(SystemImageScenarios.Create(target, job.WorkDirectory, job.OraclePath));
                    var baseline = SystemImageScenarios.ReadOracle(job.OraclePath);
                    sessionChecks.AddRange(SystemImageScenarios.Verify(target, baseline));
                    var sessionActive = ConfigurationManager.Apply(target, job.Configuration, true);
                    RunStorage.AtomicJson(job.Reply + ".enabled.json", sessionActive);
                    RunStorage.AtomicJson(job.Reply + ".enabled-diagnostics.json", systemDevice.GetDiagnostics());
                    var instructions = AppSessionScenarios.Instructions(baseline.FilePath, job.StopFile);
                    await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(job.StopFile)!, "operator-instructions.txt"), instructions);
                    Console.Error.WriteLine(instructions);
                    var session = AppSessionScenarios.Observe(baseline.FilePath, job.StopFile,
                        TimeSpan.FromSeconds(job.Seconds), systemDevice.GetWriteCacheState, job.Reply + ".samples.jsonl");
                    RunStorage.AtomicJson(job.Reply + ".session.json", session);
                    RunStorage.AtomicJson(job.Reply + ".operator-done.json", systemDevice.GetWriteCacheState());
                    RunStorage.AtomicJson(job.Reply + ".operator-done-diagnostics.json", systemDevice.GetDiagnostics());
                    var live = session.OperatorDone ? AppSessionScenarios.UnbufferedHash(baseline.FilePath) : default;
                    systemDevice.Control(WriteCacheAction.Flush);
                    systemDevice.Control(WriteCacheAction.Disable);
                    systemDevice.Control(WriteCacheAction.Release);
                    var sessionReleased = systemDevice.GetWriteCacheState();
                    RunStorage.AtomicJson(job.Reply + ".released.json", sessionReleased);
                    if (sessionReleased.Enabled || sessionReleased.BudgetBytes != 0 || sessionReleased.DirtyBytes != 0 ||
                        sessionReleased.InFlightBytes != 0 || sessionReleased.Faulted || sessionReleased.LastError != 0)
                        throw new IOException("The application session did not return C: to a disabled/released state.");
                    var releasedBytes = session.OperatorDone ? AppSessionScenarios.UnbufferedHash(baseline.FilePath) : default;
                    sessionChecks.AddRange(AppSessionScenarios.Evaluate(session, baseline.ExpectedSha256, live, releasedBytes));
                    RunStorage.AtomicJson(job.Reply, sessionChecks);
                    ReportFailures(sessionChecks, Console.Error);
                    return sessionChecks.All(check => check.Result == "PASS") ? 0 : 1;
                }
                if (string.IsNullOrWhiteSpace(job.WorkDirectory) || string.IsNullOrWhiteSpace(job.OraclePath) ||
                    job.Configuration is null)
                    throw new IOException("Missing active system-image workload, oracle or configuration.");
                if (before.Enabled || before.BudgetBytes != 0 || before.DirtyBytes != 0 || before.InFlightBytes != 0)
                    throw new IOException("Active system-image workload must start from the captured disabled/released state.");
                var active = ConfigurationManager.Apply(target, job.Configuration, true);
                RunStorage.AtomicJson(job.Reply + ".enabled.json", active);
                var diagnosticsEnabled = systemDevice.GetDiagnostics();
                RunStorage.AtomicJson(job.Reply + ".enabled-diagnostics.json", diagnosticsEnabled);
                var checks = new List<CheckResult>();
                checks.AddRange(SystemImageScenarios.Create(target, job.WorkDirectory, job.OraclePath));
                var afterApplicationFlush = systemDevice.GetWriteCacheState();
                RunStorage.AtomicJson(job.Reply + ".after-application-flush.json", afterApplicationFlush);
                ValidateActiveImageRouting(active, afterApplicationFlush, (ulong)SystemImageScenarios.FileBytes);
                var diagnosticsAfterApplicationFlush = systemDevice.GetDiagnostics();
                RunStorage.AtomicJson(job.Reply + ".after-application-flush-diagnostics.json",
                    diagnosticsAfterApplicationFlush);
                var pagingProgress = ValidatePagingProgressWindow(diagnosticsEnabled, diagnosticsAfterApplicationFlush);
                var pagingRoute = ValidatePagingRouteWindow(diagnosticsEnabled, diagnosticsAfterApplicationFlush);
                checks.Add(new("system-image/cache-accepted-bytes", "PASS",
                    $"The active C: cache accepted at least the complete image ({afterApplicationFlush.AcceptedBytes - active.AcceptedBytes} bytes observed)."));
                checks.Add(new("system-image/paging-observation", "PASS",
                    $"Routed paging-marked requests during this window: reads {pagingRoute.ReadRequests}, " +
                    $"completed {pagingRoute.ReadCompletions}; writes {pagingRoute.WriteRequests}, " +
                    $"completed {pagingRoute.WriteCompletions}; overlap waits {pagingRoute.OverlapWaits}, " +
                    $"cooperative read completions {pagingProgress.ServicedReadMisses}. " +
                    "No routed failures, mapping failures or cache-capacity waits were observed. " +
                    $"Max observed read/write lengths were {pagingProgress.MaxReadLength}/{pagingProgress.MaxWriteLength} bytes. " +
                    "This is a device-wide window across all processes and does not prove a forced paging dependency."));
                checks.AddRange(SystemImageScenarios.Verify(target, SystemImageScenarios.ReadOracle(job.OraclePath)));
                systemDevice.Control(WriteCacheAction.Flush);
                var afterAdministrativeFlush = systemDevice.GetWriteCacheState();
                RunStorage.AtomicJson(job.Reply + ".after-administrative-flush.json", afterAdministrativeFlush);
                ValidateActiveImageRouting(active, afterAdministrativeFlush, (ulong)SystemImageScenarios.FileBytes);
                var diagnosticsAfterAdministrativeFlush = systemDevice.GetDiagnostics();
                RunStorage.AtomicJson(job.Reply + ".after-administrative-flush-diagnostics.json",
                    diagnosticsAfterAdministrativeFlush);
                ValidatePagingProgressWindow(diagnosticsEnabled, diagnosticsAfterAdministrativeFlush);
                ValidatePagingRouteWindow(diagnosticsEnabled, diagnosticsAfterAdministrativeFlush);
                checks.Add(new("system-image/administrative-flush", "PASS",
                    "The administrative flush returned while the cache remained routed and error-free; unrelated live C: writes may already be dirty again."));
                checks.AddRange(SystemImageScenarios.Verify(target, SystemImageScenarios.ReadOracle(job.OraclePath)));
                systemDevice.Control(WriteCacheAction.Disable);
                systemDevice.Control(WriteCacheAction.Release);
                var released = systemDevice.GetWriteCacheState();
                RunStorage.AtomicJson(job.Reply + ".released.json", released);
                if (released.Enabled || released.BudgetBytes != 0 || released.DirtyBytes != 0 ||
                    released.InFlightBytes != 0 || released.Faulted || released.LastError != 0)
                    throw new IOException("Active system-image case did not return C: to a disabled/released state.");
                checks.Add(new("system-image/runtime-release", "PASS",
                    "The normal product path disabled, drained and released the C: runtime cache cleanly."));
                var postReleaseChecks = SystemImageScenarios.Verify(target,
                    SystemImageScenarios.ReadOracle(job.OraclePath));
                RunStorage.AtomicJson(job.Reply + ".post-release-checks.json", postReleaseChecks);
                checks.AddRange(postReleaseChecks);
                RunStorage.AtomicJson(job.Reply, checks);
                ReportFailures(checks, Console.Error);
                return checks.All(check => check.Result == "PASS") ? 0 : 1;
            }
            if (job.Operation == "system-paging-recognition")
            {
                // No cache configuration, workload file or raw write: only an observe-only reference range set,
                // cleared on exit, and bounded private-memory pressure to exercise the pagefile.
                using var recognitionDevice = new CacheDevice(target.Device, writable: true);
                var recognitionBefore = recognitionDevice.GetWriteCacheState();
                if (recognitionBefore.DeviceBytes != (ulong)target.Bytes || recognitionBefore.Faulted || recognitionBefore.LastError != 0)
                    throw new IOException("System-disk driver identity/state is not healthy enough for the recognition check.");
                var recognitionChecks = PagingRecognitionScenarios.Run(target, recognitionDevice);
                var recognitionAfter = recognitionDevice.GetWriteCacheState();
                if (recognitionAfter.Faulted || recognitionAfter.Errors != recognitionBefore.Errors ||
                    recognitionAfter.Enabled != recognitionBefore.Enabled || recognitionAfter.BudgetBytes != recognitionBefore.BudgetBytes)
                    throw new IOException("The system-disk cache state changed during the recognition check.");
                RunStorage.AtomicJson(job.Reply, recognitionChecks);
                ReportFailures(recognitionChecks, Console.Error);
                return recognitionChecks.All(check => check.Result is "PASS" or "SKIP") ? 0 : 1;
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
                var checks = new List<CheckResult>(job.Operation == "system-file-create"
                    ? SystemFileScenarios.Create(target, job.WorkDirectory!, job.OraclePath)
                    : SystemFileScenarios.Verify(target, SystemFileScenarios.ReadOracle(job.OraclePath)));
                var after = beforeObservation.GetWriteCacheState();
                RunStorage.AtomicJson(job.Reply + ".after.json", after);
                if (after.Faulted || after.Errors != before.Errors || after.LastError != before.LastError)
                    throw new IOException("System-disk cache reported a new error during the file check.");
                if (job.Operation == "system-file-create" && before.Enabled)
                {
                    ValidateActiveImageRouting(before, after, (ulong)SystemFileScenarios.FileMiB << 20);
                    checks.Add(new("system-file/active-admission", "PASS",
                        $"Active C: cache accepted {after.AcceptedBytes - before.AcceptedBytes} bytes during the owned file write."));
                }
                RunStorage.AtomicJson(job.Reply, checks);
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
        if (job.Operation == "disk-removal-preflight")
        {
            var inventory = (await DiskCatalog.ListAsync()).Single(d => d.Number == target.Number);
            ValidateFileTarget(target, inventory);
            if (!string.Equals(target.Instance, job.DisposableInstance, StringComparison.OrdinalIgnoreCase) ||
                target.DiskBytes != job.DisposableBytes)
                throw new IOException("Disposable physical disk identity does not match the explicit removal target.");
            var output = await DiskTarget.InspectAsync(SystemPreflightGuard.OutputVolume(Path.GetDirectoryName(job.Reply)!));
            output.ValidateCurrent();
            if (output.Number == target.Number || output.Instance.Equals(target.Instance, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Removal evidence and oracle must reside on another physical disk.");
            var preview = await DiskEjection.PreviewAsync(target.Device);
            if (!preview.Ejectable || preview.Volumes.Count != 1)
                throw new NotSupportedException(preview.UnsupportedReason ?? "The first removal suite requires exactly one lettered data volume.");
            using var cache = new CacheDevice(target.Device);
            if (cache.GetWriteCacheState().BudgetBytes != 0 || SavedConfigurations.IsSaved(target.VolumeId))
                throw new IOException("Removal verification requires an unconfigured disposable volume without a saved profile.");
            RunStorage.AtomicJson(job.Reply, new { Target = target, Output = output, Preview = preview });
            return 0;
        }
        if (job.Operation is "disk-removal-eject" or "disk-removal-eject-windows")
        {
            // No retained volume/cache handle may veto our own request.
            var expectedEject = await DiskEjection.PreviewAsync(target.Device);
            if (expectedEject.DiskNumber != target.Number || expectedEject.DiskBytes != target.DiskBytes ||
                !expectedEject.Instance.Equals(target.Instance, StringComparison.OrdinalIgnoreCase) ||
                expectedEject.VolumeIds is null || expectedEject.VolumeIds.Count != 1 ||
                !expectedEject.VolumeIds.TryGetValue(target.Device, out var volumeId) ||
                !volumeId.Equals(target.VolumeId, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The recorded removal target changed before eject preview.");
            if (job.Operation == "disk-removal-eject-windows")
            {
                // Observe and close the handle. No Disable, Flush, Release or
                // filesystem flush is issued by this test before native eject.
                using (var observed = new CacheDevice(target.Device))
                {
                    var state = observed.GetWriteCacheState();
                    if (!state.Enabled || state.DirtyBytes < (8UL << 20) || state.LastError != 0)
                        throw new IOException("Native Windows eject requires the active dirty-cache precondition.");
                    RunStorage.AtomicJson(job.Reply + ".before.json", new WindowsEjectPrecondition(target, state,
                        observed.GetDiagnostics(), DateTimeOffset.UtcNow));
                }
                target.ValidateCurrent();
                var current = await DiskEjection.PreviewAsync(target.Device);
                if (!current.Ejectable || current.RemovalInstance != expectedEject.RemovalInstance ||
                    current.RemovalMembers is null || expectedEject.RemovalMembers is null ||
                    !current.RemovalMembers.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expectedEject.RemovalMembers))
                    throw new IOException("Native Windows eject scope changed before request.");
                target.ValidateCurrent();
                RunStorage.AtomicJson(job.Reply, WindowsDiskEjection.Request(current));
                return 0;
            }
            DiskEjectResult eject;
            try { eject = await DiskEjection.EjectAsync(target.Device, expected: expectedEject, capturePreparation: true); }
            catch (DiskEjectVetoException veto) { eject = veto.Result; }
            RunStorage.AtomicJson(job.Reply, eject);
            return 0;
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
            case "managed-cli":
                var cliChecks = await ManagedCliScenarios.RunAsync(job, device);
                RunStorage.AtomicJson(job.Reply, cliChecks); ReportFailures(cliChecks, Console.Error);
                return cliChecks.Count > 0 && cliChecks.All(c => c.Result == "PASS") ? 0 : 1;
            case "managed-broker-restart":
            case "managed-lifecycle-prepare":
            case "managed-lifecycle-verify":
            case "managed-lifecycle-cleanup":
                if (job.ManagedOraclePath is null) throw new ArgumentException("Managed lifecycle oracle path is required.");
                var lifecycleOutput = await DiskTarget.InspectAsync(SystemPreflightGuard.OutputVolume(Path.GetDirectoryName(job.ManagedOraclePath)!));
                lifecycleOutput.ValidateCurrent();
                if (lifecycleOutput.Instance.Equals(target.Instance, StringComparison.OrdinalIgnoreCase) || lifecycleOutput.Number == target.Number)
                    throw new IOException("Managed lifecycle evidence must be off the image host physical disk.");
                var lifecycleChecks = await ManagedLifecycleScenarios.RunAsync(job, target, device);
                RunStorage.AtomicJson(job.Reply, lifecycleChecks); ReportFailures(lifecycleChecks, Console.Error);
                return lifecycleChecks.Count > 0 && lifecycleChecks.All(c => c.Result == "PASS") ? 0 : 1;
            case "ram-disk":
            case "vhdx-backed":
            case "image-in-ram":
                var managedChecks = await ManagedDiskScenarios.RunAsync(job.Operation, device, job.WorkDirectory!, job.Reply + ".managed.json");
                RunStorage.AtomicJson(job.Reply, managedChecks);
                ReportFailures(managedChecks, Console.Error);
                return managedChecks.Count > 0 && managedChecks.All(c => c.Result == "PASS") ? 0 : 1;
            case "managed-provider":
                var providerChecks = await ManagedProviderScenarios.RunAsync(target, device, job.WorkDirectory!, job.Reply + ".native.json");
                RunStorage.AtomicJson(job.Reply, providerChecks);
                ReportFailures(providerChecks, Console.Error);
                return providerChecks.Count > 0 && providerChecks.All(c => c.Result == "PASS") ? 0 : 1;
            case "disk-removal-prepare":
                result = DiskRemovalScenarios.Prepare(target, device, job.WorkDirectory!, job.BudgetMiB);
                break;
            case "disk-removal-verify":
                var removalOracle = JsonSerializer.Deserialize<DiskRemovalOracle>(await File.ReadAllTextAsync(job.OraclePath!))
                    ?? throw new InvalidDataException("Missing removal oracle.");
                if (removalOracle.Target != target)
                    throw new IOException("Removal oracle target identity changed.");
                result = DiskRemovalScenarios.Verify(removalOracle, device);
                break;
            case "capture":
                // Testing a data partition on the OS physical disk is also excluded.
                var inventory = (await DiskCatalog.ListAsync()).Single(d => d.Number == target.Number);
                if (inventory.IsBoot || inventory.IsSystem || device.GetStatistics().PagingPathCount != 0)
                    throw new IOException("Verification excludes boot/system/paging disks.");
                // Only the supported registration is verified: topmost volume filter on every volume, none on disks.
                var registrationProblems = DriverRegistration.Inspect().Problems();
                if (registrationProblems.Count != 0)
                    throw new IOException("Filter registration is not the supported volume-filter registration: " + string.Join(" ", registrationProblems));
                var state = device.GetWriteCacheState();
                ConfigurationManager.EnsureHealthy(state);
                if (!state.SupportsPerformance || !state.SupportsReadWrite || !state.SupportsDropClean)
                    throw new NotSupportedException("Verification requires the current cache/performance protocol.");
                if (state.DirtyBytes != 0)
                    throw new IOException("Start with a clean cache; drain your workload first.");
                result = new RecoverySnapshot(1, target, state, device.GetPerformance().TimingEnabled != 0,
                    Profiles(), DateTimeOffset.UtcNow, Environment.MachineName, device.GetDiagnostics().ReadRecall?.Mode);
                break;
            case "restore":
                var original = JsonSerializer.Deserialize<RecoverySnapshot>(await File.ReadAllTextAsync(job.Recovery!))!;
                if (original.SchemaVersion != 1 || original.Machine != Environment.MachineName || original.Target != target)
                    throw new IOException("Recovery identity/version mismatch.");
                // This suite never sets fault injection. Clear only its delay and range-gate hooks;
                // do not hide device faults. Older drivers without V9 have no gate to disarm.
                device.Control(WriteCacheAction.LabDelay, value: 0);
                if (original.ReadRecall is { } recallMode)
                    device.Control(WriteCacheAction.LabReadRecall, value: recallMode);
                if (device.GetDiagnostics().LabGate is not null)
                    device.Control(WriteCacheAction.LabGate, value: 0);
                if (device.GetDiagnostics().PagingAdmission is not null)
                {
                    device.SetSpecialRanges([], SpecialRangeKind.ForceDirect);
                    device.SetSpecialRanges([], SpecialRangeKind.Reference);
                }
                // Only ordering-faults deliberately raises the lifetime error count through the driver
                // lab gate, and each of its stages recovers with Retry. Accept that count only when the
                // cache is currently healthy, and record exactly what was accepted. Every other suite
                // still treats any error-count change as a restoration failure.
                if (job.AcceptsLabErrors)
                {
                    var current = device.GetWriteCacheState();
                    if (current.LastError == 0 && current.Errors > original.State.Errors)
                    {
                        RunStorage.AtomicJson(job.Reply + ".accepted-lab-errors.json", new
                        {
                            OriginalErrors = original.State.Errors, CurrentErrors = current.Errors,
                            Reason = "ordering-faults lab-gate failures, each recovered by Retry"
                        });
                        original = original with { State = original.State with { Errors = current.Errors } };
                    }
                }
                var drainedState = FlushForRestoration(() =>
                {
                    Stage("flushing filesystem volume before cache drain");
                    using var volume = new FileTests.CheckedVolume(target.Letter, target.Number, target.DiskBytes, writable: true);
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
                    // Available RAM on a small machine dips while other programs run; a moment later
                    // the original budget fits again. Wait for it rather than leave the cache shrunk.
                    var memoryWait = Stopwatch.StartNew();
                    while (true)
                    {
                        try
                        {
                            ConfigurationManager.Apply(target, CacheConfiguration.FromState(original.State), true);
                            break;
                        }
                        catch (InsufficientMemoryForCacheException) when (memoryWait.Elapsed < TimeSpan.FromMinutes(2))
                        {
                            Stage("waiting for enough available RAM to restore the original budget");
                            Thread.Sleep(2000);
                        }
                    }
                }
                device.Control(WriteCacheAction.PerformanceTiming, value: original.Timing ? 1UL : 0UL);
                var restored = ConfigurationManager.WaitForHealthyState(device.GetWriteCacheState, original.State);
                var restoredProfiles = Profiles();
                var restoredTiming = device.GetPerformance().TimingEnabled;
                var mismatches = RestorationMismatches(original, drainedState, restored, restoredProfiles, restoredTiming);
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
            case "read-pass":
                var passRoot = Path.GetFullPath(job.WorkDirectory!) + Path.DirectorySeparatorChar;
                if (job.Files is not { Length: > 0 } passFiles ||
                    passFiles.Any(file => !Path.GetFullPath(file).StartsWith(passRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(file)))
                    throw new IOException("A read pass covers existing files of this run's workload directory only.");
                var (passBytes, passRequests, passSeconds) = FileTests.UnbufferedFileWrite.ReadPass(passFiles);
                result = new ReadPassResult(passBytes, passRequests, passSeconds);
                break;
            case "control":
                device.Control(job.Action, value: job.Value);
                result = device.GetWriteCacheState();
                break;
            case "cache-layout-reset":
                result = CacheLayoutResetProbe.Run(device, (name, evidence) => RunStorage.AtomicJson(job.Reply + "." + name + ".json", evidence));
                break;
            case "concurrent-oracle":
                result = await ConcurrentCacheOracle.Run(job.WorkDirectory!, checked((int)job.Value), job.Seconds,
                    (stream, progress) => RunStorage.AtomicJson(job.Reply + $".stream{stream}.json", progress));
                break;
            case "verify-concurrent-oracle":
                result = ConcurrentCacheOracle.Verify(job.WorkDirectory!, job.OraclePath!);
                break;
            case "layout-map":
                var mapTimer = Stopwatch.StartNew();
                var mapState = device.GetWriteCacheState();
                var map = device.GetLayoutMap(checked((int)(mapState.PayloadCapacity / (256 * 1024))));
                result = new { Map = map, mapState.Instance, Milliseconds = mapTimer.Elapsed.TotalMilliseconds };
                break;
            case "map-poll":
                using (var pollWriter = new StreamWriter(job.Reply, append: false))
                {
                    var pollTimer = Stopwatch.StartNew();
                    using var pollProcess = Process.GetCurrentProcess();
                    var pollCpuStart = pollProcess.TotalProcessorTime;
                    while (pollTimer.Elapsed.TotalSeconds < job.Seconds)
                    {
                        var started = Stopwatch.GetTimestamp();
                        var pollState = device.GetWriteCacheState();
                        var polled = device.GetLayoutMap(checked((int)(pollState.PayloadCapacity / (256 * 1024))));
                        await pollWriter.WriteLineAsync(JsonSerializer.Serialize(new
                        {
                            Utc = DateTimeOffset.UtcNow, polled.Generation, polled.TotalChunks, polled.Chunks,
                            pollState.Instance, pollState.Errors, pollState.LastError,
                            CpuMilliseconds = (pollProcess.TotalProcessorTime - pollCpuStart).TotalMilliseconds,
                            polled.InOrder, polled.FreeChunks, UsedSlots = polled.Used.Sum(x => (long)x),
                            Milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
                        }));
                        await pollWriter.FlushAsync();
                        if (job.ReadyFile is not null && !File.Exists(job.ReadyFile))
                            RunStorage.AtomicJson(job.ReadyFile, new { Ready = true });
                        if (File.Exists(job.StopFile)) break;
                        await Task.Delay(checked((int)job.Value));
                    }
                }
                return 0;
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
            case "paging-coherence":
                var pagingChecks = PagingCoherenceScenarios.Run(target, device, job.WorkDirectory!);
                RunStorage.AtomicJson(job.Reply, pagingChecks);
                ReportFailures(pagingChecks, Console.Error);
                return pagingChecks.All(c => c.Result == "PASS") ? 0 : 1;
            case "concurrent-sectors":
                var sectorChecks = await ConcurrentSectorOracle.Run(target, device, job.WorkDirectory!, job.Reply + ".evidence");
                RunStorage.AtomicJson(job.Reply, sectorChecks);
                return sectorChecks.All(c => c.Result == "PASS") ? 0 : 1;
            case "partial-read-accounting":
                var partialChecks = PartialReadScenarios.Run(target, device, job.WorkDirectory!, job.BudgetMiB,
                    observation => RunStorage.AtomicJson(job.Reply + "." + observation.Id + ".json", observation));
                RunStorage.AtomicJson(job.Reply, partialChecks);
                ReportFailures(partialChecks, Console.Error);
                return partialChecks.Count > 0 && partialChecks.All(c => c.Result == "PASS") ? 0 : 1;
            case "app-write-profile":
                var profileChecks = AppWriteProfileScenarios.Run(target, device, job.WorkDirectory!);
                RunStorage.AtomicJson(job.Reply, profileChecks);
                ReportFailures(profileChecks, Console.Error);
                return profileChecks.All(c => c.Result == "PASS") ? 0 : 1;
            case "volume-registration" or "volume-raw-disk-commands" or "volume-shared-disk" or "volume-resize" or "volume-snapshot" or "trim-cache":
                var volumeChecks = job.Operation switch
                {
                    "volume-registration" => VolumeScenarios.Registration(target, device),
                    "volume-raw-disk-commands" => VolumeScenarios.RawDiskCommands(target, device, job.WorkDirectory!),
                    "volume-shared-disk" => VolumeScenarios.SharedDisk(target, device, job.WorkDirectory!),
                    "volume-resize" => VolumeScenarios.Resize(target, job.WorkDirectory!),
                    "volume-snapshot" => VolumeScenarios.Snapshot(target, device, job.WorkDirectory!),
                    _ => TrimScenarios.Run(target, device, job.WorkDirectory!)
                };
                RunStorage.AtomicJson(job.Reply, volumeChecks);
                ReportFailures(volumeChecks, Console.Error);
                return volumeChecks.Count > 0 && volumeChecks.All(c => c.Result is "PASS" or "SKIP") ? 0 : 1;
            case "ordering-faults":
                var faultChecks = OrderingFaultScenarios.Run(target, device, job.WorkDirectory!);
                RunStorage.AtomicJson(job.Reply, faultChecks);
                ReportFailures(faultChecks, Console.Error);
                return faultChecks.All(c => c.Result == "PASS") ? 0 : 1;
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
                // Value 1 adds the stream files; value 2 prepares only the cache-recall files.
                if (new DriveInfo(target.Root).AvailableFreeSpace < ((long)job.BudgetMiB * (job.Value is 1 ? 5 : job.Value is 2 ? 4 : 3) + 1024) * 1024 * 1024)
                    throw new IOException("Insufficient free space for unique workloads and headroom.");
                Directory.CreateDirectory(directory);
                var block = new byte[1 << 20];
                Random.Shared.NextBytes(block);
                var workloadFiles = job.Value == 2
                    ? CacheExercisePlan.RecallTargets(job.BudgetMiB).Select(file => (Name: file.Name, Length: file.MiB)).ToList()
                    : new List<(string Name, int Length)> { ("hot.dat", job.BudgetMiB / 4), ("writer.dat", job.BudgetMiB * 2), ("resident.dat", job.BudgetMiB / 2), ("drain.dat", job.BudgetMiB / 4), ("flush.dat", 1) };
                if (job.Value == 1)
                    workloadFiles.AddRange(CacheExercisePlan.AllTargets(job.BudgetMiB).Select(file => (file.Name, file.MiB)));
                foreach (var (name, length) in workloadFiles)
                {
                    using var file = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    for (var i = 0; i < length; i++)
                        file.Write(block);
                    file.Flush(true);
                }
                result = new
                {
                    Directory = directory,
                    Files = workloadFiles.Select(file => new { file.Name, MiB = file.Length }).ToArray()
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
        var binary = registered is null ? null : LoadedDriverInspection.NormalizePath(registered);
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
            LoadedDrivers = LoadedDriverInspection.Capture(),
            Note = "PSAPI paths observe loaded modules; file hashes describe current files. Registered paths are not proof of loaded identity. Match the filter's immutable loaded filename and signed artifact hash before comparison."
        };
    }
}
