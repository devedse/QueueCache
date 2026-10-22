using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

[SupportedOSPlatform("windows")]
public interface IVerificationCampaignHost : IAsyncDisposable
{
    Task<IReadOnlyList<CampaignTargetEvidence>> PreflightAsync(VerificationCampaignOptions options, string directory, IProgress<string> progress, CancellationToken token);
    Task<CampaignPhaseResult> RunPhaseAsync(VerificationCampaignPhase phase, CampaignTargetEvidence target,
        IReadOnlyList<CampaignTargetEvidence> targets, string directory, string? diskSpdHash,
        Action<string> created, IProgress<string> progress, CancellationToken token);
    Task CleanupAsync(string directory, IProgress<string> progress);
}

/// <summary>Owns campaign leases; suite execution and bounded control/recovery remain in VerificationRunner.</summary>
[SupportedOSPlatform("windows")]
public sealed class VerificationCampaignHost(string executable, IReadOnlyList<string>? executablePrefix = null,
    string? leaseDirectory = null) : IVerificationCampaignHost
{
    private readonly IReadOnlyList<string> prefix = executablePrefix ?? [];
    private readonly List<FileStream> leases = [];
    private readonly Dictionary<CampaignTargetRole, CampaignTargetEvidence> baselines = [];
    private VerificationCampaignOptions options = null!;
    private int sequence;
    private bool backingPaused;
    private bool preflightAccepted;
    private string? restorationBlocked;

    private static void RequireQuietNormalHost()
    {
        using var current = Process.GetCurrentProcess();
        if (current.PriorityClass != ProcessPriorityClass.Normal)
            throw new IOException("Campaigns require a normal-priority foreground process; Task Scheduler priority 4.");
        var names = new[] { "DiskSpd", "DiskSpd64", "DiskSpd32", "DiskMark64", "DiskMark32", "CrystalDiskMark", "QueueCache.Desktop" };
        var competing = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id != current.Id && names.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                        competing.Add(process.ProcessName + " PID " + process.Id);
                }
                catch (InvalidOperationException) { /* exited during enumeration */ }
            }
        }
        if (competing.Count != 0) throw new IOException("Stop competing benchmark/UI processes before the campaign: " + string.Join(", ", competing));
    }

    private async Task<CampaignTargetEvidence> Inspect(CampaignTargetRole role, string volume, string directory,
        IProgress<string> progress, CancellationToken token, DiskTarget? expected = null)
    {
        var id = $"preflight-{++sequence:D3}-{role}";
        var reply = Path.Combine(directory, id + ".reply.json");
        var job = Path.Combine(directory, id + ".job.json");
        RunStorage.AtomicJson(job, new WorkerJob("campaign-inspect", volume, reply, expected, Value: (ulong)role));
        progress.Report("Inspecting " + role + " " + volume);
        var result = await OwnedProcess.RunAsync(executable, [.. prefix, "--verification-worker", job],
            Path.Combine(directory, id), TimeSpan.FromSeconds(120), token);
        if (result.ExitCode != 0) throw new IOException("Campaign preflight failed; inspect " + id + ".stderr.txt: " + result.Error);
        return JsonSerializer.Deserialize<CampaignTargetEvidence>(await File.ReadAllTextAsync(reply, token)) ??
            throw new InvalidDataException("Missing campaign target evidence.");
    }

    public async Task<IReadOnlyList<CampaignTargetEvidence>> PreflightAsync(VerificationCampaignOptions selected,
        string directory, IProgress<string> progress, CancellationToken token)
    {
        options = selected;
        RequireQuietNormalHost();
        var leaseRoot = leaseDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "QueueCache", "Verification");
        Directory.CreateDirectory(leaseRoot);
        leases.Add(new FileStream(Path.Combine(leaseRoot, "campaign.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        var selectedTargets = new List<(CampaignTargetRole Role, string Volume)>
            { (CampaignTargetRole.Performance, selected.Verification.Volume), (CampaignTargetRole.NtfsLab, selected.LabNtfs) };
        if (selected.LabRefs is not null) selectedTargets.Add((CampaignTargetRole.RefsLab, selected.LabRefs));
        foreach (var (role, volume) in selectedTargets)
            baselines.Add(role, await WhenCleanAsync(() => Inspect(role, volume, directory, progress, token), role, volume, progress, token));
        VerificationCampaignEvidence.ValidateTargets(selected, baselines.Values.ToArray());
        foreach (var disk in baselines.Values.Select(t => t.Recovery.Target.Number).Order())
            leases.Add(new FileStream(Path.Combine(leaseRoot, $"PhysicalDrive{disk}.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        // Revalidate after owning every target: the first inventory was read-only, before taking disk leases.
        foreach (var target in baselines.Values.ToArray())
        {
            var observed = await WhenCleanAsync(() => Inspect(target.Role, target.Recovery.Target.Device, directory, progress, token, target.Recovery.Target),
                target.Role, target.Recovery.Target.Device, progress, token);
            VerificationCampaignEvidence.ValidateConfiguration(target.Recovery, observed.Recovery);
            if (VerificationCampaignEvidence.DriversKey(target.Drivers) != VerificationCampaignEvidence.DriversKey(observed.Drivers))
                throw new IOException("Loaded driver identity changed while acquiring campaign leases.");
        }
        preflightAccepted = true;
        return baselines.Values.ToArray();
    }

    public async Task<CampaignPhaseResult> RunPhaseAsync(VerificationCampaignPhase phase, CampaignTargetEvidence target,
        IReadOnlyList<CampaignTargetEvidence> targets, string directory, string? diskSpdHash,
        Action<string> created, IProgress<string> progress, CancellationToken token)
    {
        RequireQuietNormalHost();
        // A phase that raised the lifetime error count on purpose (InjectsLabFaults) updated its role's
        // baseline after restoration; later phases must compare against that, not the preflight copy.
        target = baselines[target.Role];
        var timer = Stopwatch.StartNew();
        var main = baselines[CampaignTargetRole.Performance];
        var pause = target.Role != CampaignTargetRole.Performance && target.BackingCacheEnabled == true;
        if (pause)
        {
            if (!options.PauseBackingCache) throw new IOException("Backing-cache pause was not selected.");
            // Journal the obligation before attempting a control; a partial failure still requires restoration.
            backingPaused = true;
            RunStorage.AtomicJson(Path.Combine(directory, "backing-restoration.json"), new { Required = true, Phase = phase.Id, Recovery = main.Recovery });
        }
        var runner = new VerificationRunner(executable, prefix, leaseDirectory)
        {
            CampaignOwnsDiskLease = true, CampaignBaseline = target.Recovery, RunCreated = created
        };
        CampaignPhaseResult? result = null;
        try
        {
            if (pause)
                await new VerificationRunner(executable, prefix, leaseDirectory).CampaignMaintenanceAsync(options.Verification,
                    main.Recovery, Path.Combine(directory, "maintenance"), false, progress, token);
            if (target.Role == CampaignTargetRole.Performance)
            {
                // A lab filesystem whose image lives on this volume can still write back metadata after its
                // phase ended; flush it first so those writes reach this cache before its own preparation drains.
                foreach (var lab in LabTargetsBackedBy(baselines.Values, target.Recovery.Target.Device))
                    await new VerificationRunner(executable, prefix, leaseDirectory).CampaignMaintenanceAsync(options.Verification,
                        lab.Recovery, Path.Combine(directory, "maintenance"), false, progress, token, prepare: true);
                await new VerificationRunner(executable, prefix, leaseDirectory).CampaignMaintenanceAsync(options.Verification,
                    target.Recovery, Path.Combine(directory, "maintenance"), false, progress, token, prepare: true);
            }
            var exit = await runner.RunAsync(phase.Options with { Output = Path.Combine(directory, "phases", phase.Id) }, progress, token);
            var child = runner.DirectoryPath ?? throw new InvalidDataException("Phase did not allocate its evidence directory.");
            result = VerificationCampaignEvidence.ReadPhase(phase, child, exit, timer.Elapsed.TotalSeconds, target, diskSpdHash);
            if (result.RestorationFailure is not null) restorationBlocked = "Child restoration failed; preserve its evidence and inspect before restoring the backing cache.";
            if (result.Restoration == "RESTORED" && VerificationPlan.InjectsLabFaults(phase.Options.Suite))
            {
                var restored = JsonSerializer.Deserialize<QueueCache.Management.WriteCacheState>(File.ReadAllText(Path.Combine(child, "restored.json")))
                    ?? throw new InvalidDataException("Missing restored fault-test state.");
                if (restored.Errors != target.Recovery.State.Errors)
                {
                    using var accepted = JsonDocument.Parse(File.ReadAllText(Path.Combine(child, "restored.json.accepted-lab-errors.json")));
                    if (accepted.RootElement.GetProperty("OriginalErrors").GetUInt64() != target.Recovery.State.Errors ||
                        accepted.RootElement.GetProperty("CurrentErrors").GetUInt64() != restored.Errors)
                        throw new IOException("Injected error acceptance does not match the phase recovery evidence.");
                    baselines[target.Role] = target with { Recovery = target.Recovery with { State = target.Recovery.State with { Errors = restored.Errors } } };
                }
            }
        }
        finally
        {
            if (backingPaused && restorationBlocked is null)
            {
                try { await RestoreBacking(directory, progress); }
                catch (Exception ex)
                {
                    restorationBlocked = ex.ToString();
                    throw new CampaignRestorationException("Backing-cache restoration failed; no further phases or recovery retries are allowed.", ex);
                }
            }
        }
        return result! with { Seconds = timer.Elapsed.TotalSeconds };
    }

    /// <summary>Lab targets whose image file is stored on <paramref name="volume"/>.</summary>
    public static IReadOnlyList<CampaignTargetEvidence> LabTargetsBackedBy(IEnumerable<CampaignTargetEvidence> targets, string volume) =>
        targets.Where(t => t.Role != CampaignTargetRole.Performance && t.BackingVolume is { } backing &&
            string.Equals(backing.TrimEnd('\\'), volume.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToArray();

    private static void EnsureAllStopped(string directory)
    {
        foreach (var parent in Directory.EnumerateFiles(directory, "*.process.json", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName).Distinct()) OwnedProcess.EnsureStopped(parent!);
    }

    private async Task RestoreBacking(string directory, IProgress<string> progress)
    {
        EnsureAllStopped(directory);
        await new VerificationRunner(executable, prefix, leaseDirectory).CampaignMaintenanceAsync(options.Verification,
            baselines[CampaignTargetRole.Performance].Recovery, Path.Combine(directory, "maintenance"), true, progress, CancellationToken.None);
        backingPaused = false;
        RunStorage.AtomicJson(Path.Combine(directory, "backing-restoration.json"), new { Required = false, Status = "RESTORED" });
    }

    public async Task CleanupAsync(string directory, IProgress<string> progress)
    {
        EnsureAllStopped(directory);
        if (restorationBlocked is not null) throw new CampaignRestorationException(restorationBlocked);
        if (!preflightAccepted) return; // Read-only preflight failed; no cache action was authorized or attempted.
        if (backingPaused) await RestoreBacking(directory, progress);
        await FinalizeTargetsAsync(baselines.Values,
            target => new VerificationRunner(executable, prefix, leaseDirectory).CampaignMaintenanceAsync(options.Verification,
                target.Recovery, Path.Combine(directory, "maintenance"), false, progress, CancellationToken.None, prepare: true),
            target => Inspect(target.Role, target.Recovery.Target.Device, directory, progress, CancellationToken.None, target.Recovery.Target));
    }

    public const int PreflightCleanAttempts = 15;
    public static TimeSpan PreflightCleanDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>A lab image attached just before the campaign keeps writing to its backing volume for a few seconds
    /// after mounting. Preflight waits, bounded, for those writes to drain on their own: it never drains, and it
    /// retries nothing but the clean-cache refusal. Each attempt is a recorded worker run; the capture stays strict.</summary>
    public static async Task<CampaignTargetEvidence> WhenCleanAsync(Func<Task<CampaignTargetEvidence>> inspect,
        CampaignTargetRole role, string volume, IProgress<string> progress, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await inspect(); }
            catch (IOException exception) when (attempt < PreflightCleanAttempts && exception.Message.Contains(VerificationWorker.NotCleanMessage))
            {
                progress.Report($"{role} {volume} still has pending writes; waiting {PreflightCleanDelay.TotalSeconds:0} s for them to drain " +
                    $"(attempt {attempt} of {PreflightCleanAttempts}).");
                await Task.Delay(PreflightCleanDelay, token);
            }
        }
    }

    public const int FinalCleanAttempts = 5;
    public static async Task FinalizeTargetsAsync(IEnumerable<CampaignTargetEvidence> targets,
        Func<CampaignTargetEvidence, Task> prepare, Func<CampaignTargetEvidence, Task<CampaignTargetEvidence>> inspect)
    {
        foreach (var target in targets)
        {
            // Lab filesystem cleanup can dirty its backing volume after the saved task
            // was resumed. Explicit final restoration preparation flushes that filesystem
            // and drains under its own deadline before the unchanged strict capture.
            var drain = target.Role == CampaignTargetRole.Performance && target.Recovery.State.Enabled;
            CampaignTargetEvidence current;
            for (var attempt = 1; ; attempt++)
            {
                if (drain) await prepare(target);
                try { current = await inspect(target); break; }
                // The lab image's own lazy writes can reach the backing volume just after it was drained.
                // Drain again and re-capture (each attempt is a recorded worker run); the capture stays strict.
                catch (IOException exception) when (drain && attempt < FinalCleanAttempts && exception.Message.Contains(VerificationWorker.NotCleanMessage))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }
            VerificationCampaignEvidence.ValidateConfiguration(target.Recovery, current.Recovery);
            if (VerificationCampaignEvidence.DriversKey(target.Drivers) != VerificationCampaignEvidence.DriversKey(current.Drivers))
                throw new IOException("Loaded driver identity changed before campaign cleanup completed.");
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose();
        leases.Clear(); return ValueTask.CompletedTask;
    }
}
