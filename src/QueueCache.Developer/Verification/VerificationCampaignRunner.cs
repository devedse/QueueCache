using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QueueCache.Developer.Verification;

public sealed class CampaignRestorationException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>Sequential foreground campaign. Its terminal event is published after evidence and cleanup.</summary>
[SupportedOSPlatform("windows")]
public sealed class VerificationCampaignRunner(IVerificationCampaignHost host)
{
    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string message) => report(message);
    }
    public string? DirectoryPath { get; private set; }

    public async Task<int> RunAsync(VerificationCampaignOptions selected, IProgress<string>? progress, CancellationToken token)
    {
        var phases = VerificationCampaignPlan.Create(selected);
        var options = selected with { Verification = selected.Verification with { Output = Path.GetFullPath(selected.Verification.Output),
            DiskSpd = selected.Verification.DiskSpd is null ? null : Path.GetFullPath(selected.Verification.DiskSpd) } };
        var diskHash = options.Verification.DiskSpd is null ? null : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(options.Verification.DiskSpd)));
        var storage = new RunStorage(options.Verification.Output, campaign: true);
        DirectoryPath = storage.DirectoryPath;
        using var runLock = new FileStream(storage.PathFor("run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var id = Path.GetFileName(storage.DirectoryPath);
        var started = DateTimeOffset.UtcNow; var elapsed = Stopwatch.StartNew();
        var results = new List<CampaignPhaseResult>();
        var children = new Dictionary<string, string>();
        IReadOnlyList<CampaignTargetEvidence> targets = [];
        string? currentPhase = null, currentChild = null, failure = null, restorationFailure = null;
        var expected = phases.Sum(p => p.ExpectedCases.Count);
        var logGate = new object();
        void Log(string message)
        {
            lock (logGate)
            {
                var line = $"[{DateTimeOffset.UtcNow:O}] [Campaign {currentPhase ?? "preflight/finalization"}] {message}";
                File.AppendAllText(storage.PathFor("run.log"), line + Environment.NewLine);
                progress?.Report(line);
            }
        }
        int? Collected()
        {
            var count = results.Sum(r => r.Collected);
            if (currentChild is null || results.Any(r => r.Id == currentPhase)) return count;
            try
            {
                var rows = JsonSerializer.Deserialize<CaseResult[]>(File.ReadAllText(Path.Combine(currentChild, "results.json")));
                var phase = phases.Single(p => p.Id == currentPhase);
                if (rows is null || rows.Select(r => r.Id).Distinct().Count() != rows.Length || rows.Any(r => !phase.ExpectedCases.Contains(r.Id))) return null;
                return count + rows.Length;
            }
            catch (Exception ex) when (ex is IOException or JsonException) { return null; }
        }
        void Status(string status, string restoration) => storage.Write("status.json", new
        {
            SchemaVersion = 1, CampaignId = id, Status = status, Started = started, Updated = DateTimeOffset.UtcNow,
            ExpectedPhases = phases.Count, CompletedPhases = results.Count(r => r.Status == "COMPLETED"),
            ExpectedCases = expected, CollectedCases = Collected(), CurrentPhase = currentPhase,
            CurrentChildDirectory = currentChild, Restoration = restoration, Failure = failure, RestorationFailure = restorationFailure,
            ElapsedSeconds = elapsed.Elapsed.TotalSeconds
        });
        void Freeze(bool accepted) => storage.Write("manifest.json", new
        {
            SchemaVersion = 1, Kind = "VerificationCampaign", CampaignId = id, PlanVersion = VerificationPlan.Version,
            Options = options, Phases = phases, Targets = targets, PreflightAccepted = accepted, DiskSpdSha256 = diskHash,
            ExpectedCases = phases.SelectMany(p => p.ExpectedCases.Select(c => p.Id + "/" + c)).ToArray()
        });
        storage.Write("request.json", options); storage.Write("results.json", results);
        Status("RUNNING", "PENDING");
        Log("Campaign directory: " + storage.DirectoryPath);
        Log($"Profile {options.Profile}; {phases.Count} phases / {expected} outer cases. Overall deadline: {(options.Verification.DeadlineMinutes == 0 ? "unlimited" : options.Verification.DeadlineMinutes + " minutes")}; independent restoration deadlines remain enabled.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (options.Verification.DeadlineMinutes > 0) deadline.CancelAfter(TimeSpan.FromMinutes(options.Verification.DeadlineMinutes));
        try
        {
            targets = await host.PreflightAsync(options, storage.DirectoryPath, new InlineProgress(Log), deadline.Token);
            VerificationCampaignEvidence.ValidateTargets(options, targets);
            Freeze(true);
            foreach (var phase in phases)
            {
                deadline.Token.ThrowIfCancellationRequested();
                currentPhase = phase.Id; currentChild = null;
                Status("RUNNING", "PENDING");
                Log($"Phase {results.Count + 1} of {phases.Count}: {phase.Options.Suite} on {phase.Options.Volume}; {phase.ExpectedCases.Count} outer cases, {phase.MeasurementWindows} measurement windows.");
                var target = targets.Single(t => t.Role == phase.Role);
                var result = await host.RunPhaseAsync(phase, target, targets, storage.DirectoryPath, diskHash, child =>
                {
                    var root = Path.GetFullPath(Path.Combine(storage.DirectoryPath, "phases", phase.Id)) + Path.DirectorySeparatorChar;
                    if (!Path.GetFullPath(child).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Child evidence is outside the campaign phase namespace.");
                    currentChild = child; children.Add(phase.Id, child);
                    storage.Write("phase-index.json", children); Status("RUNNING", "PENDING");
                }, new InlineProgress(message =>
                {
                    Log(message);
                    if (message.Contains("Case ", StringComparison.Ordinal) || message.Contains("[Restoring", StringComparison.Ordinal)) Status("RUNNING", "PENDING");
                }), deadline.Token);
                if (result.Id != phase.Id || result.Expected != phase.ExpectedCases.Count || result.Collected < 0 || result.Collected > result.Expected)
                    throw new InvalidDataException("Phase result differs from the frozen campaign plan.");
                results.Add(result); storage.Write("results.json", results);
                Log($"Phase {result.Status}; {result.Collected}/{result.Expected} cases; elapsed {result.Seconds:F1}s; restoration {result.Restoration}.");
                if (result.Status != "COMPLETED")
                {
                    failure = result.Failure ?? "Phase collection is incomplete; see its exact run directory.";
                    restorationFailure = result.RestorationFailure;
                    break;
                }
                currentPhase = null; currentChild = null;
            }
        }
        catch (Exception ex)
        {
            failure = ex is OperationCanceledException && deadline.IsCancellationRequested && !token.IsCancellationRequested
                ? "Campaign overall deadline reached; unfinished phases remain incomplete.\n" + ex : ex.ToString();
            if (ex is CampaignRestorationException) restorationFailure = ex.ToString();
            Log("ERROR: " + failure);
        }
        finally
        {
            var restoring = Stopwatch.StartNew();
            Log("Checking owned processes and final restoration under independent deadlines.");
            try { await host.CleanupAsync(storage.DirectoryPath, new InlineProgress(Log)); }
            catch (Exception ex) { restorationFailure = ex.ToString(); Log("RESTORATION ERROR: " + ex); }
            try { await host.DisposeAsync(); }
            catch (Exception ex) { restorationFailure = ex.ToString(); Log("OWNERSHIP RELEASE ERROR: " + ex); }
            storage.Write("restoration.json", new { Status = restorationFailure is null ? "RESTORED" : "FAILED", Seconds = restoring.Elapsed.TotalSeconds, Failure = restorationFailure });
        }
        if (!File.Exists(storage.PathFor("manifest.json"))) Freeze(false);
        var complete = failure is null && restorationFailure is null && results.Count == phases.Count &&
            results.All(r => r.Status == "COMPLETED" && r.Restoration == "RESTORED" && r.Collected == r.Expected);
        var status = complete ? "COMPLETED" : restorationFailure is not null ? "RESTORATION_FAILED" : token.IsCancellationRequested ? "CANCELLED" : "INCOMPLETE";
        var restoration = restorationFailure is null ? "RESTORED" : "FAILED";
        storage.Write("results.json", results); Status(status, restoration);
        var report = new StringBuilder($"# QueueCache verification campaign\n\nStatus: **{status}**. Profile: `{options.Profile}`. Plan: {VerificationPlan.Version}.\n\n" +
            $"Completed phases: {results.Count(r => r.Status == "COMPLETED")}/{phases.Count}; recorded outer cases: {Collected()?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}/{expected}. Elapsed: {elapsed.Elapsed.TotalSeconds:F1}s. Restoration: {restoration}.\n\n" +
            "COMPLETED means collection and restoration completed. MEASURED is not performance acceptance; correctness subchecks and RAM measurement windows remain in each child's raw evidence. Missing/incomplete repetitions are never combined.\n\n" +
            "| Phase | Volume | Status | Cases | Elapsed s | Restoration | Evidence |\n|---|---|---|---:|---:|---|---|\n");
        foreach (var phase in phases)
        {
            var result = results.SingleOrDefault(r => r.Id == phase.Id);
            var child = children.GetValueOrDefault(phase.Id);
            var link = child is null ? "—" : $"[SUMMARY.md]({Path.GetRelativePath(storage.DirectoryPath, Path.Combine(child, "SUMMARY.md")).Replace('\\', '/')})";
            report.AppendLine($"| {phase.Id} | {phase.Options.Volume} | {result?.Status ?? (phase.Id == currentPhase ? "INTERRUPTED/EVIDENCE_FAILED" : "NOT_RUN")} | {result?.Collected.ToString(CultureInfo.InvariantCulture) ?? "—"}/{phase.ExpectedCases.Count} | {result?.Seconds.ToString("F1", CultureInfo.InvariantCulture) ?? "—"} | {result?.Restoration ?? "—"} | {link} |");
        }
        if (failure is not null) report.AppendLine("\nFailure:\n\n```text\n" + failure + "\n```");
        if (restorationFailure is not null) report.AppendLine("\nRestoration failure:\n\n```text\n" + restorationFailure + "\n```");
        File.WriteAllText(storage.PathFor("SUMMARY.md"), report.ToString());
        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(storage.PathFor("manifest.json"))));
        var finished = DateTimeOffset.UtcNow;
        var completion = new CampaignCompletionEvent(1, id + ":terminal:1", id, digest, storage.DirectoryPath, status,
            phases.Count, results.Count(r => r.Status == "COMPLETED"), expected, Collected(), restoration, finished);
        storage.Write("completion-delivery.json", new { EventId = completion.EventId, Status = "PENDING_CONTROLLER", Note = "No Manager wake API is exposed; controller consumes the durable event after process exit and deduplicates by EventId." });
        Log($"{status}: {completion.CompletedPhases}/{phases.Count} phases. Report: {storage.PathFor("SUMMARY.md")}");
        File.WriteAllText(storage.PathFor("FINISHED.txt"), $"{status}\nFinished UTC: {finished:O}\nResults: {storage.DirectoryPath}\n");
        // Final durable publication. No benchmarks, restoration or report writes follow this event.
        storage.Write("completion.json", completion);
        return complete ? 0 : token.IsCancellationRequested ? 130 : 1;
    }
}
