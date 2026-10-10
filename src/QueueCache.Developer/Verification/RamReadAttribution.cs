using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Cpu;
using Microsoft.Windows.EventTracing.Symbols;

namespace QueueCache.Developer.Verification;

public sealed record AttributionCpu(int Cpu, int Samples, int BusySamples);
public sealed record AttributionFunction(string Function, int Samples);
public sealed record AttributionThread(int Pid, int Tid, int Samples);
public sealed record AttributionWait(int Tid, int SwitchIns, double? WaitingMs, double? ReadyMs);
public sealed record RamAttributionWindow(string Id, ProcessIdentity Process, DateTimeOffset End, long ScoreBytes,
    int Samples, int NativeSamples, int ResolvedNativeSamples, AttributionCpu[] Cpus, AttributionThread[] Threads,
    AttributionFunction[] Exclusive, AttributionFunction[] Inclusive, AttributionWait[] SubmitterScheduling,
    string Scope, string RequestOverlap, string HelperCoordination);

/// <summary>Statistical CPU/scheduler attribution in enclosing owned-process windows, never a speed verdict.</summary>
public static class RamReadAttribution
{
    public static void RequireSymbols(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) ||
            !File.Exists(Path.Combine(directory, "qcachelab.pdb")) || !File.Exists(Path.Combine(directory, "qcramdisk.pdb")))
            throw new InvalidDataException("Attribution requires --trace-symbols with matching qcachelab.pdb and qcramdisk.pdb.");
    }

    public static void ValidateWindow(ProcessIdentity identity, DateTimeOffset end, int samples, int native, int resolved)
    {
        if (identity.Pid <= 0 || end <= new DateTimeOffset(identity.StartedUtc) || samples <= 0 || native <= 0 ||
            resolved < 0 || resolved > native || (double)resolved / native < 0.95)
            throw new InvalidDataException("Unusable trace/PID interval or missing/mismatched native symbols (require 95% native stack coverage).");
    }

    public static async Task AnalyzeAsync(string journalPath, IReadOnlyList<RamReadReferenceCase> plan, string evidence, string symbolsPath)
    {
        RequireSymbols(symbolsPath);
        var journal = JsonSerializer.Deserialize<TraceOwnership>(File.ReadAllText(journalPath))
            ?? throw new InvalidDataException("Missing trace ownership.");
        VerificationTraceSession.ValidateOwnership(journal, journalPath);
        if (journal.State != "FINISHED" || !File.Exists(journal.Etl)) throw new InvalidDataException("Trace is not finalized.");
        using var trace = TraceProcessor.Create(journal.Etl, new TraceProcessorSettings { AllowLostEvents = false });
        var cpu = trace.UseCpuSamplingData(); var scheduling = trace.UseCpuSchedulingData(); var symbols = trace.UseSymbols();
        trace.Process(); // missing providers and lost events fail, never relaxed
        // TraceProcessor resolves symbols against the image's recorded PDB signature, not its version string.
        await symbols.Result.LoadSymbolsAsync(SymCachePath.Automatic, new RawSymbolPath(symbolsPath));
        var windows = new List<RamAttributionWindow>();
        foreach (var scenario in plan)
        {
            var prefix = evidence + "." + scenario.Id;
            var process = JsonSerializer.Deserialize<ProcessIdentity>(File.ReadAllText(prefix + ".process.json"))
                ?? throw new InvalidDataException("Missing owned benchmark PID.");
            using var exit = JsonDocument.Parse(File.ReadAllText(prefix + ".exit.json"));
            if (!exit.RootElement.GetProperty("Exited").GetBoolean() || exit.RootElement.GetProperty("ExitCode").GetInt32() != 0)
                throw new InvalidDataException("Trace benchmark did not exit successfully.");
            var end = exit.RootElement.GetProperty("FinishedUtc").GetDateTimeOffset();
            var start = new DateTimeOffset(process.StartedUtc);
            if (start < journal.Created) throw new InvalidDataException("Cross-run trace process interval.");
            bool InWindow(DateTimeOffset at) => at >= start && at <= end;
            var samples = cpu.Result.Samples.Where(s => InWindow(s.Timestamp.DateTimeOffset)).ToArray();
            if (!samples.Any(s => s.Process?.Id == process.Pid)) throw new InvalidDataException("Owned PID absent from trace.");
            static bool Native(StackFrame f) => f.Image?.FileName is { } name &&
                (name.Equals("qcachelab.sys", StringComparison.OrdinalIgnoreCase) || name.Equals("qcramdisk.sys", StringComparison.OrdinalIgnoreCase) ||
                 name.StartsWith("QueueCache-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".sys", StringComparison.OrdinalIgnoreCase));
            var native = samples.Where(s => s.Stack?.Frames.Any(Native) == true).ToArray();
            var resolved = native.Count(s => s.Stack!.Frames.Where(Native).All(f => f.Symbol?.FunctionName is { Length: > 0 }));
            ValidateWindow(process, end, samples.Length, native.Length, resolved);
            static string Frame(StackFrame f) => (f.Image?.FileName ?? "?") + "!" + (f.Symbol?.FunctionName ?? "?");
            var busy = samples.Where(s => s.Process?.ImageName != "Idle").ToArray();
            var functions = busy.SelectMany(s => s.Stack?.Frames.Select(Frame).Distinct() ?? []);
            var acts = scheduling.Result.ThreadActivity.Where(a => a.Thread?.Process?.Id == process.Pid && InWindow(a.StartTime.DateTimeOffset)).ToArray();
            if (acts.Length == 0) throw new InvalidDataException("Owned submitter scheduler data is unavailable.");
            var score = JsonSerializer.Deserialize<DiskSpdScore>(File.ReadAllText(prefix + ".score.json"))
                ?? throw new InvalidDataException("Missing score evidence.");
            windows.Add(new(scenario.Id, process, end, score.Bytes, samples.Length, native.Length, resolved,
                samples.GroupBy(s => s.Processor).Select(g => new AttributionCpu(g.Key, g.Count(), g.Count(s => s.Process?.ImageName != "Idle"))).ToArray(),
                busy.Where(s => s.Process is not null && s.Thread is not null).GroupBy(s => (s.Process!.Id, s.Thread!.Id))
                    .Select(g => new AttributionThread(g.Key.Item1, g.Key.Item2, g.Count())).OrderByDescending(g => g.Samples).ToArray(),
                busy.GroupBy(s => s.Stack?.Frames.Count > 0 ? Frame(s.Stack.Frames[0]) : "?")
                    .Select(g => new AttributionFunction(g.Key, g.Count())).OrderByDescending(g => g.Samples).Take(80).ToArray(),
                functions.GroupBy(f => f).Select(g => new AttributionFunction(g.Key, g.Count())).OrderByDescending(g => g.Samples).Take(120).ToArray(),
                acts.GroupBy(a => a.Thread!.Id).Select(g => new AttributionWait(g.Key, g.Count(),
                    g.Any(a => a.WaitingDuration is null) ? null : g.Sum(a => (double)a.WaitingDuration!.Value.TotalMicroseconds) / 1000,
                    g.Any(a => a.ReadyDuration is null) ? null : g.Sum(a => (double)a.ReadyDuration!.Value.TotalMicroseconds) / 1000)).ToArray(),
                "Enclosing process lifetime includes startup/warmup; ScoreBytes is the separate measured read-byte count.",
                "UNKNOWN: sampled stacks do not establish concurrent Direct requests.",
                "UNKNOWN: WorkerMain includes useful copying and polling; its whole share cannot be called wasted CPU."));
        }
        RunStorage.AtomicJson(evidence + ".attribution.json", new { SchemaVersion = 1, Trace = journal.Instance,
            EtlSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(journal.Etl))),
            Analyzer = typeof(TraceProcessor).Assembly.GetName().Version?.ToString(),
            Symbols = new[] { "qcachelab.pdb", "qcramdisk.pdb" }.Select(name => new { Name = name,
                Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(symbolsPath, name)))) }),
            Windows = windows, SpeedAcceptance = false });
        File.WriteAllText(evidence + ".attribution.md", "# RAM read attribution\n\nStatistical CPU and scheduling evidence; traced throughput is not a release comparison.\n\n" +
            "| Shape | CPU samples | Native samples | Resolved native | Measured read bytes |\n|---|---:|---:|---:|---:|\n" +
            string.Join("\n", windows.Select(w => $"| {w.Id} | {w.Samples} | {w.NativeSamples} | {w.ResolvedNativeSamples} | {w.ScoreBytes} |")) +
            "\n\nExact request overlap and helper poll/copy decomposition remain unknown without instruction mapping or scoped native counters.\n");
    }
}
