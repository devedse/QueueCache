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
    string? WorkDirectory = null, int BudgetMiB = 1024, string? ReadyFile = null);
public sealed record RecoverySnapshot(int SchemaVersion, DiskTarget Target, WriteCacheState State,
    bool Timing, string Profiles, DateTimeOffset Captured, string Machine);

/// <summary>Runs inside a child of the same CLI. A blocking driver call cannot trap the coordinator.</summary>
[SupportedOSPlatform("windows")]
public static class VerificationWorker
{
    public static void ReportFailures(IEnumerable<CheckResult> checks, TextWriter error)
    {
        foreach (var check in checks.Where(c => c.Result != "PASS"))
            error.WriteLine($"{check.Result}: {check.Name}: {check.Detail}");
    }
    public static string Profiles() => JsonSerializer.Serialize(SavedConfigurations.List().OrderBy(p => p.Instance));
    public static async Task<int> ExecuteAsync(string jobPath)
    {
        var job = JsonSerializer.Deserialize<WorkerJob>(await File.ReadAllTextAsync(jobPath)) ?? throw new InvalidDataException("Missing worker job.");
        DiskTarget target;
        if (job.Expected is { } expected)
        {
            // Repeated Get-Disk/CIM discovery can block behind a loaded storage stack. The coordinator already
            // captured the identity. Native calls are synchronous; the coordinator bounds the worker lifetime.
            if (!string.Equals(job.Volume, $"{expected.Letter}:", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Worker volume disagrees with the recorded target.");
            try { expected.ValidateCurrent(); }
            catch (Exception ex) { throw new IOException($"Worker {job.Operation}: native target validation failed for {job.Volume}.", ex); }
            target = expected;
        }
        else target = await DiskTarget.InspectAsync(job.Volume);
        using var device = new CacheDevice(target.Device, writable: true);
        if (job.Expected is not null) DiskTarget.ValidateDeviceLength(target, device.GetWriteCacheState().DeviceBytes);
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
                if (state.DirtyBytes != 0) throw new IOException("Start with a clean cache; drain your workload first.");
                result = new RecoverySnapshot(1, target, state, device.GetPerformance().TimingEnabled != 0,
                    Profiles(), DateTimeOffset.UtcNow, Environment.MachineName);
                break;
            case "restore":
                var original = JsonSerializer.Deserialize<RecoverySnapshot>(await File.ReadAllTextAsync(job.Recovery!))!;
                if (original.SchemaVersion != 1 || original.Machine != Environment.MachineName || original.Target != target)
                    throw new IOException("Recovery identity/version mismatch.");
                // This suite never sets fault injection. Clear only its delay hook; do not hide device faults.
                device.Control(WriteCacheAction.LabDelay, value: 0);
                device.Control(WriteCacheAction.Flush);
                if (original.State.BudgetBytes == 0)
                {
                    device.Control(WriteCacheAction.Release);
                    device.Control(WriteCacheAction.FlushPolicy, value: original.State.UnsafeDefer ? 1UL : 0UL);
                    if (original.State.Options is not null) device.SetOptions(original.State.Options);
                }
                else
                {
                    ConfigurationManager.WaitForHealthyState(device.GetWriteCacheState, original.State);
                    ConfigurationManager.Apply(target, CacheConfiguration.FromState(original.State), true);
                }
                device.Control(WriteCacheAction.PerformanceTiming, value: original.Timing ? 1UL : 0UL);
                var restored = ConfigurationManager.WaitForHealthyState(device.GetWriteCacheState, original.State);
                if (restored.DirtyBytes != 0 || restored.InFlightBytes != 0 || restored.Errors != original.State.Errors ||
                    restored.Instance != original.State.Instance || Profiles() != original.Profiles ||
                    restored.BudgetBytes != original.State.BudgetBytes || restored.Enabled != original.State.Enabled ||
                    restored.UnsafeDefer != original.State.UnsafeDefer || restored.Options != original.State.Options ||
                    device.GetPerformance().TimingEnabled != (original.Timing ? 1UL : 0UL))
                    throw new IOException("Restoration/state/profile verification failed. Inspect recovery.json and the VM.");
                result = restored;
                break;
            case "configure": result = ConfigurationManager.Apply(target, job.Configuration!, true); break;
            case "control": device.Control(job.Action, value: job.Value); result = device.GetWriteCacheState(); break;
            case "snapshot": result = new { State = device.GetWriteCacheState(), Performance = device.GetPerformance(), Diagnostics = device.GetDiagnostics() }; break;
            case "files":
                var report = await DiskWorkloads.TestAsync(target, new Progress<string>(Console.WriteLine));
                RunStorage.AtomicJson(job.Reply, report);
                if (!report.Passed) ReportFailures(report.Checks, Console.Error);
                return report.Passed ? 0 : 1;
            case "policies":
                var checks = await CacheScenarios.RunAsync(target, new Progress<string>(Console.WriteLine));
                RunStorage.AtomicJson(job.Reply, checks);
                ReportFailures(checks, Console.Error);
                if (checks.Count == 0) Console.Error.WriteLine("Policy suite returned no checks.");
                return checks.Count > 0 && checks.All(c => c.Result == "PASS") ? 0 : 1;
            case "prepare":
                var directory = Path.GetFullPath(job.WorkDirectory!);
                if (!directory.StartsWith(target.Root, StringComparison.OrdinalIgnoreCase) || Directory.Exists(directory))
                    throw new IOException("Workload directory must be a new directory on the selected volume.");
                if (new DriveInfo(target.Root).AvailableFreeSpace < ((long)job.BudgetMiB * 3 + 1024) * 1024 * 1024)
                    throw new IOException("Insufficient free space for unique workloads and headroom.");
                Directory.CreateDirectory(directory);
                var block = new byte[1 << 20];
                Random.Shared.NextBytes(block);
                foreach (var (name, length) in new[] { ("hot.dat", job.BudgetMiB / 4), ("writer.dat", job.BudgetMiB * 2), ("flush.dat", 1) })
                {
                    using var file = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    for (var i = 0; i < length; i++) file.Write(block);
                    file.Flush(true);
                }
                result = new { Directory = directory };
                break;
            case "application-flush":
                using (var file = new FileStream(Path.Combine(job.WorkDirectory!, "flush.dat"), FileMode.Open,
                    FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    var timer = Stopwatch.StartNew();
                    var count = 0;
                    while (timer.Elapsed.TotalSeconds < job.Seconds && !File.Exists(job.StopFile))
                    { file.Flush(true); count++; await Task.Delay(250); }
                    result = new { Flushes = count };
                }
                break;
            case "telemetry":
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
                        await writer.WriteLineAsync(JsonSerializer.Serialize(new { Utc = DateTimeOffset.UtcNow,
                            ElapsedSeconds = timer.Elapsed.TotalSeconds, State = observedState,
                            Performance = observedPerformance, Diagnostics = observedDiagnostics }));
                        await writer.FlushAsync();
                        if (job.ReadyFile is not null && !File.Exists(job.ReadyFile))
                            RunStorage.AtomicJson(job.ReadyFile, new { Ready = true });
                        if (stopping) break;
                        await Task.Delay(200);
                    }
                }
                return 0;
            default: throw new ArgumentException("Unknown verification worker operation.");
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
        return new { Machine = Environment.MachineName, OS = Environment.OSVersion.ToString(),
            Cli = executable, CliSha256 = Hash(executable), CliVersion = FileVersionInfo.GetVersionInfo(executable).FileVersion,
            EntryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.Location,
            EntryAssemblySha256 = Hash(System.Reflection.Assembly.GetEntryAssembly()?.Location),
            RegisteredDriverPath = registered, RegisteredDriverSha256 = Hash(binary),
            Note = "Registered binary is not proof of loaded binary after an upgrade. Record/reboot to the intended release before comparison." };
    }
}
