using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace QueueCache.Developer.Verification;

public sealed record TraceOwnership(string Instance, string Executable, string Etl, string Directory,
    string State, DateTimeOffset Created, string Machine, string? WorkDirectory = null);

/// <summary>Named WPR ownership survives worker death. Cleanup never issues a global stop/cancel.</summary>
public static class VerificationTraceSession
{
    public static int ConfigureDiagnosticProfile(System.Xml.Linq.XDocument profile, string? instance = null)
    {
        var collectors = profile.Descendants().Where(e => e.Name.LocalName is "SystemCollector" or "EventCollector").ToArray();
        if (collectors.Length is < 1 or > 2) throw new InvalidDataException("Unexpected CPU collector inventory; buffer budget cannot be proven.");
        foreach (var collector in collectors)
        {
            if (instance is not null) collector.SetAttributeValue("Name", instance + (collector.Name.LocalName == "SystemCollector" ? "-System" : "-Events"));
            var size = collector.Elements().Single(e => e.Name.LocalName == "BufferSize");
            var buffers = collector.Elements().Single(e => e.Name.LocalName == "Buffers");
            size.SetAttributeValue("Value", 1024);
            buffers.SetAttributeValue("Value", 128);
            buffers.SetAttributeValue("PercentageOfTotalMemory", "false");
            buffers.SetAttributeValue("Operation", "Set");
        }
        return collectors.Length * 128; // configured upper pool capacity in MiB, not observed allocation
    }

    public static void ValidateCollectorStatus(string output, int collectors)
    {
        static int[] Values(string output, string label) => System.Text.RegularExpressions.Regex.Matches(output,
            @"(?m)^" + System.Text.RegularExpressions.Regex.Escape(label) + @"\s*:\s*(\d+)\s*$")
            .Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        foreach (var (label, expected) in new[] { ("Buffer Size (KB)", 1024), ("Number of Buffers", 128), ("Events Lost", 0) })
        {
            var values = Values(output, label);
            if (values.Length != collectors || values.Any(value => value != expected))
                throw new InvalidDataException("WPR collector readback missing/mismatched: " + label);
        }
    }

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
            (journal.WorkDirectory is not null && journal.WorkDirectory != Path.Combine(Path.GetTempPath(), journal.Instance)) ||
            journal.State is not ("STARTING" or "RECORDING" or "FINALIZING" or "FINISHED" or "START_FAILED"))
            throw new InvalidDataException("Trace ownership/path/machine mismatch; do not stop a trace.");
    }

    public static async Task StartAsync(string journalPath,
        Func<string, string[], string, bool, Task<ProcessResult>>? execute = null)
    {
        if (File.Exists(journalPath)) throw new IOException("Trace journal already exists.");
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wpr.exe");
        if (execute is null && !File.Exists(executable)) throw new IOException("Windows Performance Recorder is unavailable.");
        execute ??= Command;
        var instance = "QCTrace-" + Guid.NewGuid().ToString("N");
        var journal = new TraceOwnership(instance, executable,
            journalPath + ".etl", Path.GetDirectoryName(Path.GetFullPath(journalPath))!, "STARTING",
            DateTimeOffset.UtcNow, Environment.MachineName, Path.Combine(Path.GetTempPath(), instance));
        ValidateOwnership(journal, journalPath);
        var profile = await execute(executable, ["-profiles"], journalPath + ".profiles", false);
        if (!profile.Output.Split('\n').Any(l => l.Trim().StartsWith("CPU ", StringComparison.Ordinal))) throw new IOException("Installed WPR has no CPU profile.");
        var status = await execute(executable, ["-status"], journalPath + ".preflight", false);
        if (!status.Output.Contains("WPR is not recording", StringComparison.Ordinal))
            throw new IOException("Another WPR recording is active or its status is unrecognized; preserve it.");
        if (System.IO.Directory.Exists(journal.WorkDirectory)) throw new IOException("Trace staging directory already exists.");
        RunStorage.AtomicJson(journalPath, journal); // before staging or recording side effects
        System.IO.Directory.CreateDirectory(journal.WorkDirectory!);
        try
        {
            // WPR's native path handling rejects nested campaign paths over MAX_PATH.
            // Its own files use a short unique path; managed I/O retains the final evidence in the run.
            var profilePath = Path.Combine(journal.WorkDirectory!, "profile.wprp");
            var definition = await execute(executable, ["-exportprofile", "CPU.Verbose", profilePath, "-filemode"], journalPath + ".profile", false);
            _ = definition;
            File.Copy(profilePath, journalPath + ".installed.wprp", overwrite: false);
            var xml = System.Xml.Linq.XDocument.Load(profilePath);
            var keywords = xml.Descendants().Where(e => e.Name.LocalName == "Keyword").Select(e => e.Attribute("Value")?.Value).ToArray();
            if (!keywords.Contains("SampledProfile") || !keywords.Contains("CSwitch") || !keywords.Contains("ReadyThread"))
                throw new InvalidDataException("WPR CPU profile lacks sampling/context-switch/ready-thread providers.");
            var maximumMiB = ConfigureDiagnosticProfile(xml, journal.Instance);
            xml.Save(profilePath);
            File.Copy(profilePath, journalPath + ".wprp", overwrite: false);
            RunStorage.AtomicJson(journalPath + ".tool.json", new { Path = executable,
                Version = File.Exists(executable) ? FileVersionInfo.GetVersionInfo(executable).FileVersion : null, Profile = "CPU.Verbose",
                BufferKiB = 1024, BuffersPerCollector = 128, ConfiguredMaximumMiB = maximumMiB,
                InstalledProfileSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(journalPath + ".installed.wprp"))),
                ProfileSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(journalPath + ".wprp"))) });
            await execute(executable, ["-start", profilePath + "!CPU.Verbose", "-filemode", "-recordtempto", journal.WorkDirectory!,
                "-instancename", journal.Instance], journalPath + ".start", false);
            var started = await execute(executable, ["-status", "collectors", "-details", "-instancename", journal.Instance], journalPath + ".started", false);
            ValidateCollectorStatus(started.Output, maximumMiB / 128);
            RunStorage.AtomicJson(journalPath, journal with { State = "RECORDING" });
        }
        catch
        {
            // Start may have succeeded before its transport/process failed. Named cleanup resolves ownership.
            await CleanupAsync(journalPath, execute);
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
        var toolEtl = journal.WorkDirectory is null ? journal.Etl : Path.Combine(journal.WorkDirectory, "trace.etl");
        var status = await execute(journal.Executable, ["-status", "-instancename", journal.Instance],
            journalPath + ".cleanup-status", true);
        if (status.Output.Contains("WPR is not recording", StringComparison.Ordinal) || status.ExitCode == unchecked((int)0xc5583000))
        {
            if (File.Exists(journal.Etl) || File.Exists(toolEtl)) FinalizeEtl();
            else if (journal.State == "STARTING") RunStorage.AtomicJson(journalPath, journal with { State = "START_FAILED" });
            else throw new IOException("Owned trace disappeared without ETL; preserve the journal.");
            return;
        }
        RunStorage.AtomicJson(journalPath, journal with { State = "FINALIZING" });
        await execute(journal.Executable, ["-stop", toolEtl, "-instancename", journal.Instance], journalPath + ".stop", false);
        FinalizeEtl();

        void FinalizeEtl()
        {
            // Resume safely if WPR stopped but the worker died before moving its short-path ETL.
            if (toolEtl != journal.Etl && !File.Exists(journal.Etl))
            {
                // Keep the source until a same-directory rename publishes the complete copy,
                // including when the evidence volume differs from the system temp volume.
                File.Copy(toolEtl, journal.Etl + ".transfer", overwrite: true);
                File.Move(journal.Etl + ".transfer", journal.Etl, overwrite: false);
                File.Delete(toolEtl);
            }
            if (!File.Exists(journal.Etl) || new FileInfo(journal.Etl).Length == 0) throw new IOException("Missing owned ETL.");
            using var etl = File.OpenRead(journal.Etl);
            RunStorage.AtomicJson(journalPath + ".hash.json", new { Sha256 = Convert.ToHexString(SHA256.HashData(etl)),
                Bytes = etl.Length, Finished = DateTimeOffset.UtcNow });
            RunStorage.AtomicJson(journalPath, journal with { State = "FINISHED" });
            if (journal.WorkDirectory is not null)
            {
                var profile = Path.Combine(journal.WorkDirectory, "profile.wprp");
                if (File.Exists(profile) && File.Exists(journalPath + ".wprp") &&
                    File.ReadAllBytes(profile).AsSpan().SequenceEqual(File.ReadAllBytes(journalPath + ".wprp"))) File.Delete(profile);
            }
            if (journal.WorkDirectory is not null && System.IO.Directory.Exists(journal.WorkDirectory) &&
                !System.IO.Directory.EnumerateFileSystemEntries(journal.WorkDirectory).Any())
                System.IO.Directory.Delete(journal.WorkDirectory); // never recursive; preserve unexpected evidence
        }
    }

    private static async Task<ProcessResult> Command(string executable, string[] arguments, string prefix, bool allowNoRecording = false)
    {
        var result = await OwnedProcess.RunAsync(executable, arguments, prefix, TimeSpan.FromSeconds(120), CancellationToken.None);
        if (result.ExitCode != 0 && !(allowNoRecording && result.ExitCode == unchecked((int)0xc5583000)))
            throw new IOException("WPR command failed; inspect " + prefix + ".stderr.txt and stdout.");
        return result;
    }
}
