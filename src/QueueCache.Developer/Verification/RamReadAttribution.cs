using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Cpu;
using Microsoft.Windows.EventTracing.Symbols;

namespace QueueCache.Developer.Verification;

public sealed record AttributionCpu(int Cpu, int Samples, int BusySamples, int UnknownSamples);
public sealed record AttributionFunction(string Function, int Samples);
public sealed record AttributionThread(int Pid, int Tid, int Samples);
public sealed record AttributionWait(int Tid, int SwitchIns, double? WaitingMs, double? ReadyMs);
public sealed record AttributionInstruction(string Image, string? Function, string Rva,
    string? SourceFile, int? SourceLine, string Kind, int Samples);
public sealed record AttributionInputs(string Journal, string Evidence, RamReadReferenceCase[] Cases);
public sealed record RamAttributionWindow(string Id, ProcessIdentity Process, DateTimeOffset End, long ScoreBytes,
    int Samples, int NativeSamples, int ResolvedNativeSamples, AttributionCpu[] Cpus, AttributionThread[] Threads,
    AttributionFunction[] Exclusive, AttributionFunction[] Inclusive, AttributionWait[] SubmitterScheduling,
    string Scope, string RequestOverlap, string HelperCoordination, AttributionInstruction[] NativeInstructions);

/// <summary>Statistical CPU/scheduler attribution in enclosing owned-process windows, never a speed verdict.</summary>
public static class RamReadAttribution
{
    public static AttributionInputs ReadInputs(string runDirectory)
    {
        var run = Path.GetFullPath(runDirectory);
        if (!File.Exists(Path.Combine(run, "FINISHED.txt"))) throw new InvalidDataException("Run is not finalized; do not analyze a live collection.");
        var jobs = Directory.GetFiles(run, "worker-*-ram-read-reference.job.json");
        if (jobs.Length != 1) throw new InvalidDataException("Require exactly one recorded RAM attribution worker.");
        var job = JsonSerializer.Deserialize<WorkerJob>(File.ReadAllText(jobs[0])) ?? throw new InvalidDataException("Missing worker binding.");
        if (job.Operation != "ram-read-reference" || job.ReferenceKind != RamReadRunKind.Attribution || job.OraclePath is null ||
            Path.GetDirectoryName(Path.GetFullPath(job.Reply)) != run || Path.GetDirectoryName(Path.GetFullPath(job.OraclePath)) != run)
            throw new InvalidDataException("Cross-run or unsupported attribution worker binding.");
        using var plan = JsonDocument.Parse(File.ReadAllText(job.Reply + ".plan.json"));
        var cases = plan.RootElement.GetProperty("Cases").Deserialize<RamReadReferenceCase[]>() ?? throw new InvalidDataException("Missing cases.");
        if (plan.RootElement.GetProperty("Seconds").GetInt32() != job.Seconds ||
            !cases.SequenceEqual(RamReadReferencePlan.CasesFor(job.ReferenceKind, job.ReferenceRepeats)))
            throw new InvalidDataException("Frozen attribution cases/settings mismatch.");
        return new(job.OraclePath + ".trace.json", job.Reply, cases);
    }

    public static async Task<string> ReanalyzeAsync(string runDirectory, string symbols)
    {
        var input = ReadInputs(runDirectory);
        var directory = Path.Combine(Path.GetFullPath(runDirectory), "attribution-analysis-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        RunStorage.AtomicJson(Path.Combine(directory, "inputs.json"), new { Source = input, Symbols = Path.GetFullPath(symbols), Created = DateTimeOffset.UtcNow });
        try
        {
            await AnalyzeAsync(input.Journal, input.Cases, input.Evidence, symbols, Path.Combine(directory, "analysis"));
            File.WriteAllText(Path.Combine(directory, "FINISHED.txt"), "ANALYZED; source collection verdict unchanged.");
            return directory;
        }
        catch (Exception ex) { RunStorage.AtomicJson(Path.Combine(directory, "failure.json"), new { Error = ex.ToString() }); throw; }
    }

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

    public static async Task AnalyzeAsync(string journalPath, IReadOnlyList<RamReadReferenceCase> plan, string evidence, string symbolsPath,
        string? reportPrefix = null)
    {
        RequireSymbols(symbolsPath);
        var journal = JsonSerializer.Deserialize<TraceOwnership>(File.ReadAllText(journalPath))
            ?? throw new InvalidDataException("Missing trace ownership.");
        VerificationTraceSession.ValidateOwnership(journal, journalPath);
        if (journal.State != "FINISHED" || !File.Exists(journal.Etl)) throw new InvalidDataException("Trace is not finalized.");
        string traceHash;
        using (var etl = File.OpenRead(journal.Etl))
        {
            traceHash = Convert.ToHexString(SHA256.HashData(etl));
            using var digest = JsonDocument.Parse(File.ReadAllText(journalPath + ".hash.json"));
            if (digest.RootElement.GetProperty("Sha256").GetString() != traceHash || digest.RootElement.GetProperty("Bytes").GetInt64() != etl.Length)
                throw new InvalidDataException("Owned ETL digest/size mismatch.");
        }
        var nativeSymbols = JsonSerializer.Deserialize<NativeTraceSymbol[]>(File.ReadAllText(evidence + ".native-symbols.json"))
            ?? throw new InvalidDataException("Missing captured native symbols.");
        if (nativeSymbols.Length != 2 || !nativeSymbols.Select(s => s.Pdb).Order().SequenceEqual(new[] { "qcachelab.pdb", "qcramdisk.pdb" }))
            throw new InvalidDataException("Native symbol inventory mismatch.");
        foreach (var symbol in nativeSymbols)
        {
            var pdb = File.ReadAllBytes(Path.Combine(symbolsPath, symbol.Pdb));
            if (NativeTraceSymbols.ReadPdbIdentity(pdb) != (symbol.Signature, symbol.Age) || Convert.ToHexString(SHA256.HashData(pdb)) != symbol.PdbSha256)
                throw new InvalidDataException("PDB does not match the original native symbol evidence.");
        }
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
            static (string Image, string? Function, string Rva, string? SourceFile, int? SourceLine) Instruction(StackFrame f)
            {
                var line = (int?)f.Symbol?.SourceLineNumber;
                return (f.Image!.FileName, f.Symbol?.FunctionName, f.RelativeVirtualAddress.ToString(),
                    f.Symbol?.SourceFileName is { Length: > 0 } source ? Path.GetFileName(source) : null, line > 0 ? line : null);
            }
            var busy = samples.Where(s => s.Process is { Id: not 0 }).ToArray();
            var functions = busy.SelectMany(s => s.Stack?.Frames.Select(Frame).Distinct() ?? []);
            var acts = scheduling.Result.ThreadActivity.Where(a => a.Thread?.Process?.Id == process.Pid && InWindow(a.StartTime.DateTimeOffset)).ToArray();
            if (acts.Length == 0) throw new InvalidDataException("Owned submitter scheduler data is unavailable.");
            var score = JsonSerializer.Deserialize<DiskSpdScore>(File.ReadAllText(prefix + ".score.json"))
                ?? throw new InvalidDataException("Missing score evidence.");
            windows.Add(new(scenario.Id, process, end, score.Bytes, samples.Length, native.Length, resolved,
                samples.GroupBy(s => s.Processor).Select(g => new AttributionCpu(g.Key, g.Count(), g.Count(s => s.Process is { Id: not 0 }), g.Count(s => s.Process is null))).ToArray(),
                busy.Where(s => s.Process is not null && s.Thread is not null).GroupBy(s => (s.Process!.Id, s.Thread!.Id))
                    .Select(g => new AttributionThread(g.Key.Item1, g.Key.Item2, g.Count())).OrderByDescending(g => g.Samples).ToArray(),
                busy.GroupBy(s => s.TopStackFrame is { } frame ? Frame(frame) : "?")
                    .Select(g => new AttributionFunction(g.Key, g.Count())).OrderByDescending(g => g.Samples).Take(80).ToArray(),
                functions.GroupBy(f => f).Select(g => new AttributionFunction(g.Key, g.Count())).OrderByDescending(g => g.Samples).Take(120).ToArray(),
                acts.GroupBy(a => a.Thread!.Id).Select(g => new AttributionWait(g.Key, g.Count(),
                    g.Any(a => a.WaitingDuration is null) ? null : g.Sum(a => (double)a.WaitingDuration!.Value.TotalMicroseconds) / 1000,
                    g.Any(a => a.ReadyDuration is null) ? null : g.Sum(a => (double)a.ReadyDuration!.Value.TotalMicroseconds) / 1000)).ToArray(),
                "Enclosing process lifetime includes startup/warmup; ScoreBytes is the separate measured read-byte count.",
                "UNKNOWN: sampled stacks do not establish concurrent Direct requests.",
                "UNKNOWN: WorkerMain includes useful copying and polling; source lines/RVAs aid mapping but its whole share cannot be called wasted CPU.",
                busy.SelectMany(s => s.Stack?.Frames.Where(Native).Distinct().Select(f =>
                    (Instruction: Instruction(f), Kind: f.Address.Equals(s.InstructionPointer) ? "sampled-instruction" : "stack-return-context")) ?? [])
                    .GroupBy(s => s)
                    .Select(g => new AttributionInstruction(g.Key.Instruction.Image, g.Key.Instruction.Function,
                        g.Key.Instruction.Rva, g.Key.Instruction.SourceFile, g.Key.Instruction.SourceLine, g.Key.Kind, g.Count()))
                    .OrderByDescending(g => g.Samples).ToArray()));
        }
        reportPrefix ??= evidence;
        RunStorage.AtomicJson(reportPrefix + ".attribution.json", new { SchemaVersion = 2, Trace = journal.Instance,
            EtlSha256 = traceHash,
            Analyzer = typeof(TraceProcessor).Assembly.GetName().Version?.ToString(),
            Symbols = new[] { "qcachelab.pdb", "qcramdisk.pdb" }.Select(name => new { Name = name,
                Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(symbolsPath, name)))) }),
            Windows = windows, SpeedAcceptance = false });
        File.WriteAllText(reportPrefix + ".attribution.md", "# RAM read attribution\n\nStatistical CPU and scheduling evidence; traced throughput is not a release comparison.\n\n" +
            "| Shape | CPU samples | Native samples | Resolved native | Measured read bytes |\n|---|---:|---:|---:|---:|\n" +
            string.Join("\n", windows.Select(w => $"| {w.Id} | {w.Samples} | {w.NativeSamples} | {w.ResolvedNativeSamples} | {w.ScoreBytes} |")) +
            "\n\nNative instruction RVAs and available source lines are in schema-2 JSON; missing lines remain null. Exact request overlap and helper poll/copy decomposition require interpreting those instructions or scoped native counters.\n");
    }
}
