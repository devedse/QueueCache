using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace QueueCache.Developer.Verification;

public sealed record TraceOwnership(string Instance, string Executable, string Etl, string Directory,
    string State, DateTimeOffset Created, string Machine);

/// <summary>Named WPR ownership survives worker death. Cleanup never issues a global stop/cancel.</summary>
public static class VerificationTraceSession
{
    public static void ValidateOwnership(TraceOwnership journal, string journalPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(journalPath))!;
        if (journal.Machine != Environment.MachineName || journal.Instance.Length != 40 ||
            !journal.Instance.StartsWith("QCTrace-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(journal.Instance[8..], "N", out _) ||
            Path.GetFullPath(journal.Directory) != directory ||
            Path.GetDirectoryName(Path.GetFullPath(journal.Etl)) != directory ||
            journal.Etl != journalPath + ".etl" ||
            journal.Executable != Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wpr.exe") ||
            journal.State is not ("STARTING" or "RECORDING" or "FINALIZING" or "FINISHED" or "START_FAILED"))
            throw new InvalidDataException("Trace ownership/path/machine mismatch; do not stop a trace.");
    }

    public static async Task StartAsync(string journalPath)
    {
        if (File.Exists(journalPath)) throw new IOException("Trace journal already exists.");
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wpr.exe");
        if (!File.Exists(executable)) throw new IOException("Windows Performance Recorder is unavailable.");
        var journal = new TraceOwnership("QCTrace-" + Guid.NewGuid().ToString("N"), executable,
            journalPath + ".etl", Path.GetDirectoryName(Path.GetFullPath(journalPath))!, "STARTING",
            DateTimeOffset.UtcNow, Environment.MachineName);
        ValidateOwnership(journal, journalPath);
        var profile = await Command(executable, ["-profiles"], journalPath + ".profiles");
        if (!profile.Output.Split('\n').Any(l => l.Trim().StartsWith("CPU ", StringComparison.Ordinal))) throw new IOException("Installed WPR has no CPU profile.");
        var status = await Command(executable, ["-status"], journalPath + ".preflight");
        if (!status.Output.Contains("WPR is not recording", StringComparison.Ordinal))
            throw new IOException("Another WPR recording is active or its status is unrecognized; preserve it.");
        var definition = await Command(executable, ["-exportprofile", "CPU.Verbose", journalPath + ".wprp", "-filemode"], journalPath + ".profile");
        _ = definition;
        var xml = System.Xml.Linq.XDocument.Load(journalPath + ".wprp");
        var keywords = xml.Descendants().Where(e => e.Name.LocalName == "Keyword").Select(e => e.Attribute("Value")?.Value).ToArray();
        if (!keywords.Contains("SampledProfile") || !keywords.Contains("CSwitch") || !keywords.Contains("ReadyThread"))
            throw new InvalidDataException("WPR CPU profile lacks sampling/context-switch/ready-thread providers.");
        RunStorage.AtomicJson(journalPath + ".tool.json", new { Path = executable,
            Version = FileVersionInfo.GetVersionInfo(executable).FileVersion, Profile = "CPU.Verbose",
            ProfileSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(journalPath + ".wprp"))) });
        RunStorage.AtomicJson(journalPath, journal); // before the external side effect
        try
        {
            await Command(executable, ["-start", "CPU.Verbose", "-filemode", "-recordtempto", journal.Directory,
                "-instancename", journal.Instance], journalPath + ".start");
            await Command(executable, ["-status", "-instancename", journal.Instance], journalPath + ".started");
            RunStorage.AtomicJson(journalPath, journal with { State = "RECORDING" });
        }
        catch
        {
            // Start may have succeeded before its transport/process failed. Named cleanup resolves ownership.
            await CleanupAsync(journalPath);
            throw;
        }
    }

    public static async Task CleanupAsync(string journalPath,
        Func<string, string[], string, bool, Task<ProcessResult>>? execute = null)
    {
        if (!File.Exists(journalPath)) return;
        var journal = JsonSerializer.Deserialize<TraceOwnership>(await File.ReadAllTextAsync(journalPath))
            ?? throw new InvalidDataException("Missing trace ownership.");
        ValidateOwnership(journal, journalPath);
        if (journal.State is "FINISHED" or "START_FAILED") return;
        execute ??= Command;
        var status = await execute(journal.Executable, ["-status", "-instancename", journal.Instance],
            journalPath + ".cleanup-status", true);
        if (status.Output.Contains("WPR is not recording", StringComparison.Ordinal) || status.ExitCode == unchecked((int)0xc5583000))
        {
            if (File.Exists(journal.Etl) && new FileInfo(journal.Etl).Length > 0)
                RunStorage.AtomicJson(journalPath, journal with { State = "FINISHED" });
            else if (journal.State == "STARTING") RunStorage.AtomicJson(journalPath, journal with { State = "START_FAILED" });
            else throw new IOException("Owned trace disappeared without ETL; preserve the journal.");
            return;
        }
        RunStorage.AtomicJson(journalPath, journal with { State = "FINALIZING" });
        await execute(journal.Executable, ["-stop", journal.Etl, "-instancename", journal.Instance], journalPath + ".stop", false);
        if (!File.Exists(journal.Etl) || new FileInfo(journal.Etl).Length == 0) throw new IOException("Missing owned ETL.");
        RunStorage.AtomicJson(journalPath + ".hash.json", new { Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(journal.Etl))),
            Bytes = new FileInfo(journal.Etl).Length, Finished = DateTimeOffset.UtcNow });
        RunStorage.AtomicJson(journalPath, journal with { State = "FINISHED" });
    }

    private static async Task<ProcessResult> Command(string executable, string[] arguments, string prefix, bool allowNoRecording = false)
    {
        var result = await OwnedProcess.RunAsync(executable, arguments, prefix, TimeSpan.FromSeconds(120), CancellationToken.None);
        if (result.ExitCode != 0 && !(allowNoRecording && result.ExitCode == unchecked((int)0xc5583000)))
            throw new IOException("WPR command failed; inspect " + prefix + ".stderr.txt and stdout.");
        return result;
    }
}
