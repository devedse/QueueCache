using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

[SupportedOSPlatform("windows")]
public sealed record CampaignTargetEvidence(CampaignTargetRole Role, RecoverySnapshot Recovery,
    string FileSystem, string Label, string DiskName, string? LabImage, bool LabLayout,
    string? BackingVolume, bool? BackingCacheEnabled, LoadedDriverObservation Drivers, bool? StagedReadAccounting = null);

public sealed record VerificationTiming(double TotalSeconds, double PreflightSeconds,
    double PreparationSeconds, double CaseSeconds, double RestorationSeconds, double FinalizationSeconds);

public sealed record CampaignPhaseResult(string Id, string Suite, string Volume, string Directory,
    string Status, int Expected, int Collected, double Seconds, VerificationTiming? Timing,
    string? Failure, string? RestorationFailure, string Restoration);

public sealed record CampaignCompletionEvent([property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string EventId, [property: JsonRequired] string CampaignId,
    [property: JsonRequired] string ManifestSha256, [property: JsonRequired] string Directory,
    [property: JsonRequired] string Status, [property: JsonRequired] int ExpectedPhases,
    [property: JsonRequired] int CompletedPhases, [property: JsonRequired] int ExpectedCases,
    [property: JsonRequired] int? CollectedCases, [property: JsonRequired] string Restoration,
    [property: JsonRequired] DateTimeOffset Finished);

/// <summary>Strictly reads the exact child evidence, never searches historical runs or invents missing fields.</summary>
[SupportedOSPlatform("windows")]
public static class VerificationCampaignEvidence
{
    public static void ValidateTargets(VerificationCampaignOptions options, IReadOnlyList<CampaignTargetEvidence> targets)
    {
        var roles = new[] { CampaignTargetRole.Performance, CampaignTargetRole.NtfsLab }
            .Concat(options.LabRefs is null ? [] : new[] { CampaignTargetRole.RefsLab }).ToArray();
        if (targets.Count != roles.Length || targets.Select(t => t.Role).Distinct().Count() != targets.Count ||
            roles.Any(role => targets.All(t => t.Role != role))) throw new InvalidDataException("Campaign target roles are missing or duplicated.");
        foreach (var target in targets)
        {
            var identity = target.Recovery.Target;
            var expected = target.Role switch { CampaignTargetRole.Performance => options.Verification.Volume,
                CampaignTargetRole.NtfsLab => options.LabNtfs, _ => options.LabRefs! };
            if (!identity.Device.Equals(expected, StringComparison.OrdinalIgnoreCase) || identity.IsBoot || identity.IsSystem || identity.IsPaging ||
                identity.Number < 0 || identity.Bytes <= 0 || identity.DiskBytes < identity.Bytes || identity.VolumeId.Length == 0 ||
                identity.Instance.Length == 0 || target.Recovery.State.LastError != 0 || target.Recovery.State.DirtyBytes != 0 || target.Recovery.State.InFlightBytes != 0)
                throw new IOException("Campaign target identity/state is unsafe or differs from its explicit role.");
            if (target.Role != CampaignTargetRole.Performance &&
                (target.FileSystem != (target.Role == CampaignTargetRole.NtfsLab ? "NTFS" : "ReFS") ||
                 target.Label is not ("QC-Lab-1" or "QC-Lab-2") || target.DiskName != "Msft Virtual Disk" ||
                 target.LabImage is null || !target.LabImage.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) || !target.LabLayout ||
                 target.BackingVolume is null || target.BackingCacheEnabled is null))
                throw new IOException("Campaign lab roles require the matching filesystem and attached qcache developer lab-disk VHDX layout.");
            if (target.Role == CampaignTargetRole.Performance && target.DiskName is "Msft Virtual Disk" or "QueueCache RAM Disk")
                throw new IOException("Campaign performance target must be the clean non-OS data disk, rather than a lab/RAM fixture.");
            if (target.Role == CampaignTargetRole.NtfsLab && target.StagedReadAccounting != true)
                throw new NotSupportedException("Campaign correctness requires V21 partial-read accounting before any phase starts.");
            if (options.Profile is "performance" or "release-performance" || options.FocusSuite == "caller-backoff")
                if (target.Role != CampaignTargetRole.Performance && target.Recovery.CallerBackoff is null)
                    throw new NotSupportedException("Caller comparison requires V23 runtime backoff capture/restoration.");
            if (!target.Drivers.Available || target.Drivers.Error is not null || target.Drivers.Modules.Count < 2 ||
                target.Drivers.Modules.Any(m => m.FileSha256 is null || m.FileError is not null))
                throw new IOException("Campaign requires observed loaded filter/provider filenames and available file hashes, not version strings.");
            if (target.BackingCacheEnabled == true &&
                (!options.PauseBackingCache || !string.Equals(target.BackingVolume, options.Verification.Volume, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("An active lab backing cache requires --pause-backing-cache and must be the explicit performance volume. Other backing caches must be stopped before the campaign.");
        }
        if (targets.Select(t => t.Recovery.Target.VolumeId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count ||
            targets.Select(t => t.Recovery.Target.Number).Distinct().Count() != targets.Count)
            throw new IOException("Campaign roles require distinct volumes on distinct disks.");
        var drivers = DriversKey(targets[0].Drivers);
        if (targets.Any(t => DriversKey(t.Drivers) != drivers)) throw new IOException("Loaded driver identity changed during campaign preflight.");
    }

    public static string DriversKey(LoadedDriverObservation observation) => JsonSerializer.Serialize(
        observation.Modules.OrderBy(m => m.ModulePath, StringComparer.OrdinalIgnoreCase).Select(m => new { m.ModulePath, m.FilePath, m.FileSha256 }));

    public static void ValidateConfiguration(RecoverySnapshot expected, RecoverySnapshot actual, bool allowInjectedErrors = false)
    {
        if (expected.Target != actual.Target || expected.Machine != actual.Machine || actual.SchemaVersion != 1 ||
            expected.Profiles != actual.Profiles || expected.Timing != actual.Timing || expected.ReadRecall != actual.ReadRecall ||
            expected.CallerBackoff != actual.CallerBackoff || expected.State.Enabled != actual.State.Enabled ||
            expected.State.BudgetBytes != actual.State.BudgetBytes || expected.State.UnsafeDefer != actual.State.UnsafeDefer ||
            expected.State.Options != actual.State.Options || expected.State.Instance != actual.State.Instance ||
            actual.State.LastError != 0 || actual.State.DirtyBytes != 0 || actual.State.InFlightBytes != 0 ||
            (allowInjectedErrors ? actual.State.Errors < expected.State.Errors : actual.State.Errors != expected.State.Errors))
            throw new IOException("Campaign baseline identity, runtime configuration, hooks or saved profiles changed between phases.");
    }

    public static CampaignPhaseResult ReadPhase(VerificationCampaignPhase phase, string directory, int exitCode,
        double seconds, CampaignTargetEvidence target, string? diskSpdHash)
    {
        directory = Path.GetFullPath(directory);
        if (!File.Exists(Path.Combine(directory, "FINISHED.txt"))) throw new InvalidDataException("Child phase has no completion marker; it is interrupted or running.");
        using var status = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "status.json")));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        var state = status.RootElement; var plan = manifest.RootElement;
        var name = state.GetProperty("Status").GetString() ?? throw new InvalidDataException("Missing phase status.");
        var expected = state.GetProperty("Expected").GetInt32(); var collected = state.GetProperty("Collected").GetInt32();
        var failure = state.GetProperty("Failure").GetString(); var restorationFailure = state.GetProperty("RestorationFailure").GetString();
        var cases = JsonSerializer.Deserialize<CaseResult[]>(File.ReadAllText(Path.Combine(directory, "results.json"))) ?? throw new InvalidDataException("Missing child results.");
        if (plan.GetProperty("PlanVersion").GetInt32() != VerificationPlan.Version ||
            !plan.GetProperty("ExpectedCases").EnumerateArray().Select(x => x.GetString()).SequenceEqual(phase.ExpectedCases) ||
            plan.GetProperty("Options").GetProperty("Suite").GetString() != phase.Options.Suite ||
            !string.Equals(plan.GetProperty("Options").GetProperty("Volume").GetString(), phase.Options.Volume, StringComparison.OrdinalIgnoreCase) ||
            expected != phase.ExpectedCases.Count || collected != cases.Length ||
            cases.Select(c => c.Id).Distinct().Count() != cases.Length || cases.Any(c => !phase.ExpectedCases.Contains(c.Id)))
            throw new InvalidDataException("Child plan/counts do not match the frozen campaign phase.");
        var childHash = plan.GetProperty("DiskSpdSha256").GetString();
        if (childHash != (phase.Options.DiskSpd is null ? null : diskSpdHash)) throw new IOException("Child DiskSpd hash differs from the campaign.");
        var loaded = plan.GetProperty("Provenance").GetProperty("LoadedDrivers").Deserialize<LoadedDriverObservation>() ?? throw new InvalidDataException("Missing child loaded driver identity.");
        if (!loaded.Available || loaded.Error is not null || DriversKey(loaded) != DriversKey(target.Drivers)) throw new IOException("Loaded driver identity changed between phases.");
        var recoveryPath = Path.Combine(directory, "recovery.json");
        if (File.Exists(recoveryPath)) ValidateConfiguration(target.Recovery,
            JsonSerializer.Deserialize<RecoverySnapshot>(File.ReadAllText(recoveryPath)) ?? throw new InvalidDataException("Missing phase recovery snapshot."));
        if (name == "COMPLETED" && (exitCode != 0 || failure is not null || restorationFailure is not null ||
            !RunStorage.Complete(phase.ExpectedCases, cases) || !File.Exists(recoveryPath) || !File.Exists(Path.Combine(directory, "restored.json"))))
            throw new InvalidDataException("Child completion disagrees with results or independent restoration evidence.");
        if (name != "COMPLETED" && exitCode == 0) throw new InvalidDataException("Incomplete child returned a successful exit code.");
        if (!File.ReadAllText(Path.Combine(directory, "FINISHED.txt")).StartsWith(name + "\n", StringComparison.Ordinal) &&
            !File.ReadAllText(Path.Combine(directory, "FINISHED.txt")).StartsWith(name + "\r\n", StringComparison.Ordinal))
            throw new InvalidDataException("Child completion marker disagrees with status.");
        // Read the required human/evidence reports before accepting the phase.
        _ = File.ReadAllText(Path.Combine(directory, "SUMMARY.md")); _ = File.ReadAllText(Path.Combine(directory, "run.log"));
        var timingPath = Path.Combine(directory, "timing.json");
        var timing = File.Exists(timingPath) ? JsonSerializer.Deserialize<VerificationTiming>(File.ReadAllText(timingPath)) : null;
        return new(phase.Id, phase.Options.Suite, phase.Options.Volume, directory, name, expected, collected,
            seconds, timing, failure, restorationFailure, restorationFailure is not null ? "FAILED" : File.Exists(Path.Combine(directory, "restored.json")) ? "RESTORED" : "NOT_CAPTURED");
    }

    public static CampaignCompletionEvent ReadCompletion(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (!File.Exists(Path.Combine(directory, "FINISHED.txt"))) throw new InvalidDataException("Campaign is interrupted or running.");
        var completion = JsonSerializer.Deserialize<CampaignCompletionEvent>(File.ReadAllText(Path.Combine(directory, "completion.json")))
            ?? throw new InvalidDataException("Missing completion event.");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "manifest.json"))));
        using var status = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "status.json")));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        var state = status.RootElement;
        var plan = manifest.RootElement;
        var expectedPhases = plan.GetProperty("Phases").Deserialize<VerificationCampaignPhase[]>() ?? throw new InvalidDataException("Missing frozen campaign phases.");
        var phases = JsonSerializer.Deserialize<CampaignPhaseResult[]>(File.ReadAllText(Path.Combine(directory, "results.json"))) ?? throw new InvalidDataException("Missing campaign phase results.");
        if (phases.Length > expectedPhases.Length || !phases.Select(p => p.Id).SequenceEqual(expectedPhases.Take(phases.Length).Select(p => p.Id)))
            throw new InvalidDataException("Recorded phases differ from the frozen sequential campaign.");
        foreach (var phase in phases)
        {
            var expected = expectedPhases.Single(p => p.Id == phase.Id);
            var root = Path.GetFullPath(Path.Combine(directory, "phases", phase.Id)) + Path.DirectorySeparatorChar;
            if (phase.Suite != expected.Options.Suite || phase.Volume != expected.Options.Volume || phase.Expected != expected.ExpectedCases.Count ||
                phase.Collected < 0 || phase.Collected > phase.Expected || !Path.GetFullPath(phase.Directory).StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                phase.Status == "COMPLETED" && (phase.Collected != phase.Expected || phase.Restoration != "RESTORED" || phase.Failure is not null || phase.RestorationFailure is not null))
                throw new InvalidDataException("Recorded phase counts, identity or restoration disagree with the campaign.");
        }
        using var restoration = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "restoration.json")));
        if (completion.SchemaVersion != 1 || completion.EventId != completion.CampaignId + ":terminal:1" ||
            completion.Directory != directory || completion.ManifestSha256 != hash ||
            plan.GetProperty("Kind").GetString() != "VerificationCampaign" || plan.GetProperty("CampaignId").GetString() != completion.CampaignId ||
            plan.GetProperty("Phases").GetArrayLength() != completion.ExpectedPhases || plan.GetProperty("ExpectedCases").GetArrayLength() != completion.ExpectedCases ||
            state.GetProperty("Status").GetString() != completion.Status ||
            state.GetProperty("ExpectedPhases").GetInt32() != completion.ExpectedPhases ||
            state.GetProperty("CompletedPhases").GetInt32() != completion.CompletedPhases ||
            state.GetProperty("ExpectedCases").GetInt32() != completion.ExpectedCases ||
            (state.GetProperty("CollectedCases").ValueKind == JsonValueKind.Null ? (int?)null : state.GetProperty("CollectedCases").GetInt32()) != completion.CollectedCases ||
            state.GetProperty("Restoration").GetString() != completion.Restoration ||
            restoration.RootElement.GetProperty("Status").GetString() != (completion.Restoration == "RESTORED" ? "RESTORED" : "FAILED") ||
            phases.Select(p => p.Id).Distinct().Count() != phases.Length || phases.Count(p => p.Status == "COMPLETED") != completion.CompletedPhases ||
            completion.CollectedCases is { } collected && (collected < phases.Sum(p => p.Collected) || collected > completion.ExpectedCases) ||
            completion.Status is not ("COMPLETED" or "INCOMPLETE" or "RESTORATION_FAILED" or "CANCELLED") ||
            completion.Status == "COMPLETED" && (completion.CompletedPhases != completion.ExpectedPhases || completion.CollectedCases != completion.ExpectedCases ||
                phases.Sum(p => p.Collected) != completion.CollectedCases || completion.Restoration != "RESTORED") ||
            !File.ReadAllText(Path.Combine(directory, "FINISHED.txt")).StartsWith(completion.Status + "\n", StringComparison.Ordinal))
            throw new InvalidDataException("Completion event disagrees with finalized campaign evidence.");
        _ = File.ReadAllText(Path.Combine(directory, "SUMMARY.md")); _ = File.ReadAllText(Path.Combine(directory, "run.log"));
        _ = File.ReadAllText(Path.Combine(directory, "restoration.json"));
        return completion;
    }
}
