using System.Globalization;
using System.Text;
using System.Text.Json;

namespace QueueCache.Developer.Verification;

/// <summary>Summarizes one completed comparison run from its own evidence files (compare.&lt;step&gt;.*):
/// COMPARISON.md for reading and comparison.json for the cross-product report. Medians of the repetitions with
/// their range; a value that is missing from the evidence stops the summary instead of becoming zero.</summary>
public static class ComparisonReport
{
    public sealed record ShapeRow(string Shape, int Samples, double MBPerSecond, double MinMBPerSecond, double MaxMBPerSecond, double Iops,
        double? ReadP50Ms, double? ReadP99Ms, double? ReadP999Ms, double? WriteP50Ms, double? WriteP99Ms, double? WriteP999Ms, double CpuPercent);
    public sealed record StepRow(string Shape, int Samples, string Measure, double Median, double Min, double Max);
    public sealed record DrainRow(ComparisonProvider Provider, string Shape, int Samples, double MBPerSecond,
        double MinMBPerSecond, double MaxMBPerSecond, double ApplicationSeconds, double DrainSeconds,
        double TotalSeconds, double DiskBytesWritten, double DiskWriteOperations, double AverageWriteBytes);

    public static void Write(string directory, string suite, string product, IReadOnlyList<ComparisonCase> plan, ComparisonProvider? provider = null)
    {
        var shapes = Shapes(directory, plan);
        var steps = Steps(directory, plan);
        var drains = ComparisonDrain.IsSuite(suite) ? Drains(directory, plan,
            provider ?? throw new InvalidDataException("Missing comparison provider."), suite == DrainBatching.Suite) : [];
        var memory = plan.Where(c => c.Kind == "memory")
            .Select(c => (c.Id, Snapshot: Read<MemorySnapshot>(directory, c.Id, "memory"))).ToArray();
        // A verify run writes result.json for the generations test and churn.json for the churn test.
        var verify = plan.Any(c => c.Kind == "durability-verify");
        var durability = verify && File.Exists(Path.Combine(directory, "compare.durability-verify.result.json"))
            ? Read<DurabilityResult>(directory, "durability-verify", "result") : null;
        var churn = verify && durability is null ? Read<ChurnResult>(directory, "durability-verify", "churn") : null;
        RunStorage.AtomicJson(Path.Combine(directory, "comparison.json"), new
        {
            SchemaVersion = 1, Suite = suite, Product = product, PlanVersion = VerificationPlan.Version,
            Provider = provider, Shapes = shapes, Steps = steps, Drains = drains, Memory = memory.Select(m => new { m.Id, m.Snapshot }), Durability = durability, Churn = churn
        });
        var md = new StringBuilder($"# Comparison: {product}\n\nSuite `{suite}`, plan {VerificationPlan.Version}. Medians of the repetitions " +
            "(range in brackets). MB/s are decimal (1,000,000 bytes/s). Raw evidence: `compare.<step>.*`. Not a verdict.\n");
        static string N(double? value, string format = "F1") => value?.ToString(format, CultureInfo.InvariantCulture) ?? "N/A";
        if (drains.Count > 0)
        {
            md.AppendLine("\n| Write through explicit drain | Provider | n | MB/s (range) | App s | Drain/confirmation s | Total s | Disk write bytes | Disk write ops | Avg write bytes |\n|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var r in drains)
                md.AppendLine($"| {r.Shape} | {r.Provider} | {r.Samples} | {N(r.MBPerSecond)} ({N(r.MinMBPerSecond)}–{N(r.MaxMBPerSecond)}) | {N(r.ApplicationSeconds, "F3")} | {N(r.DrainSeconds, "F3")} | {N(r.TotalSeconds, "F3")} | {N(r.DiskBytesWritten, "F0")} | {N(r.DiskWriteOperations, "F0")} | {N(r.AverageWriteBytes, "F0")} |");
            md.AppendLine("\nApplication time includes new file/directory creation and close. Drain time includes FlushVolume metadata, explicit product drain, zero pending and disk queue confirmation. Verification and preparation are outside timing. Disk counters describe the observed virtual disk stack, not physical-media/power-loss durability. Shuffled small-file names do not establish random LBA placement.");
        }
        if (shapes.Count > 0)
        {
            md.AppendLine("\n| DiskSpd shape | n | MB/s | IOPS | Read p50 / p99 / p99.9 ms | Write p50 / p99 / p99.9 ms | CPU % |\n|---|---:|---:|---:|---|---|---:|");
            foreach (var r in shapes)
                md.AppendLine($"| {r.Shape} | {r.Samples} | {N(r.MBPerSecond)} ({N(r.MinMBPerSecond)}–{N(r.MaxMBPerSecond)}) | {N(r.Iops, "F0")} | " +
                    $"{N(r.ReadP50Ms, "F3")} / {N(r.ReadP99Ms, "F3")} / {N(r.ReadP999Ms, "F3")} | {N(r.WriteP50Ms, "F3")} / {N(r.WriteP99Ms, "F3")} / {N(r.WriteP999Ms, "F3")} | {N(r.CpuPercent)} |");
        }
        if (steps.Count > 0)
        {
            md.AppendLine("\n| Step | n | Measure | Median | Range |\n|---|---:|---|---:|---|");
            foreach (var r in steps)
                md.AppendLine($"| {r.Shape} | {r.Samples} | {r.Measure} | {N(r.Median, "F2")} | {N(r.Min, "F2")}–{N(r.Max, "F2")} |");
        }
        if (memory.Length > 0)
        {
            md.AppendLine("\n| Memory snapshot | Available MiB | Commit MiB | Nonpaged pool MiB | System cache MiB |\n|---|---:|---:|---:|---:|");
            foreach (var (id, s) in memory)
                md.AppendLine($"| {id} | {s.AvailablePhysicalBytes >> 20} | {s.CommitBytes >> 20} | {s.KernelNonpagedBytes >> 20} | {s.SystemCacheBytes >> 20} |");
        }
        if (durability is not null)
            md.AppendLine($"\nDurability after the reset: durable save {Share(durability.Flushed)}; raw writes without a flush {Share(durability.Unflushed)}; " +
                $"small files {durability.SmallFilesIntact} intact, {durability.SmallFilesDamaged} damaged, {durability.SmallFilesMissing} missing of " +
                $"{durability.SmallFilesExpected}; chkdsk /scan exit {durability.ChkdskExitCode}.");
        if (churn is not null)
        {
            md.AppendLine($"\nChurn power cut: chkdsk /scan exit {churn.ChkdskExitCode}. For deleted files, intact means the deletion was lost.\n\n" +
                "| Group | Files | Intact | Damaged | Missing | Damaged files hold |\n|---|---:|---:|---:|---:|---|");
            foreach (var g in churn.Groups)
                md.AppendLine($"| {g.Group} | {g.Expected} | {g.Intact} | {g.Damaged} | {g.Missing} | {string.Join(", ", g.DamagedAs.Select(k => $"{k.Key} {k.Value}"))} |");
            if (churn.Unreadable.Count > 0)
                md.AppendLine($"\n{churn.Unreadable.Count} file(s) could not be read: " + string.Join("; ", churn.Unreadable.Take(5)) + (churn.Unreadable.Count > 5 ? "; ..." : ""));
        }
        File.WriteAllText(Path.Combine(directory, "COMPARISON.md"), md.ToString());
    }

    private static string Share(SectorCounts c) => string.Create(CultureInfo.InvariantCulture,
        $"{c.New * 100.0 / c.Expected:F1}% new, {c.Old * 100.0 / c.Expected:F1}% old, {(c.Other + c.Missing) * 100.0 / c.Expected:F1}% other or missing");

    public static IReadOnlyList<DrainRow> Drains(string directory, IReadOnlyList<ComparisonCase> plan, ComparisonProvider provider, bool batching = false)
    {
        if (!plan.SequenceEqual(batching ? DrainBatching.Cases(3) : ComparisonDrain.Cases(3)) || batching && provider != ComparisonProvider.QueueCache)
            throw new InvalidDataException("Cannot publish an incomplete or mixed compare-drain plan.");
        using var oracle = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "compare-drain-oracle.json")));
        var root = oracle.RootElement;
        if (root.GetProperty("PayloadBytes").GetInt64() != ComparisonDrain.PayloadBytes ||
            root.GetProperty("LargeBytes").GetInt64() != ComparisonDrain.LargeBytes ||
            root.GetProperty("SmallBytes").GetInt64() != ComparisonDrain.SmallBytes ||
            root.GetProperty("SmallFileCount").GetInt32() != ComparisonDrain.SmallFiles || root.GetProperty("Seed").GetInt32() != ComparisonDrain.Seed)
            throw new InvalidDataException("Wrong compare-drain oracle contract.");
        var small = root.GetProperty("SmallFiles").Deserialize<ComparisonFileHash[]>() ?? throw new InvalidDataException("Missing small-file hashes.");
        if (small.Length != ComparisonDrain.SmallFiles || !small.Select(f => f.File).SequenceEqual(Enumerable.Range(0, small.Length).Select(ComparisonDrainFiles.SmallName)))
            throw new InvalidDataException("Incomplete compare-drain small-file oracle.");
        var scores = plan.Select(test =>
        {
            var score = Read<ComparisonDrainScore>(directory, test.Id, "score");
            ComparisonDrain.ValidateScore(test, score, provider);
            if (provider == ComparisonProvider.QueueCache)
                ComparisonDrain.ValidateBypass(Read<QueueCache.Management.WriteCacheState>(directory, test.Id, "bypass-before.cache"),
                    Read<QueueCache.Management.WriteCacheState>(directory, test.Id, "bypass-after.cache"));
            else if (provider == ComparisonProvider.PrimoCache)
            {
                var recovery = JsonSerializer.Deserialize<ComparisonDrainRecovery>(File.ReadAllText(Path.Combine(directory, "compare-drain-recovery.json")))!;
                if (ComparisonDrain.PrimoTaskId(Read<ComparisonPrimoObservation>(directory, test.Id, "bypass-primo"), recovery.Volume, paused: true) != recovery.OriginalPrimoTaskId)
                    throw new InvalidDataException("Primo oracle bypass ownership changed.");
            }
            if (batching)
            {
                var before = Read<QueueCache.Management.WriteCacheState>(directory, test.Id, "before.cache");
                var after = Read<QueueCache.Management.WriteCacheState>(directory, test.Id, "after.cache");
                DrainBatching.ValidateProfile(before, test.GatherPolicy!.Value);
                DrainBatching.ValidateProfile(after, test.GatherPolicy.Value);
                if (before.Instance != after.Instance || before.Generation != after.Generation || before.Errors != after.Errors || before.Options != after.Options ||
                    before.DirtyBytes != score.Before.DirtyBytes || before.InFlightBytes != score.Before.InFlightBytes ||
                    after.DirtyBytes != score.After.DirtyBytes || after.InFlightBytes != score.After.InFlightBytes)
                    throw new InvalidDataException("Batching cache identity/profile changed.");
                var evidence = DrainBatching.Measure(test, before.Performance!, after.Performance!);
                if (JsonSerializer.Serialize(evidence) != JsonSerializer.Serialize(Read<DrainBatching.Evidence>(directory, test.Id, "batching")))
                    throw new InvalidDataException("Batching summary contradicts raw counters.");
            }
            var verify = Read<ComparisonDrainVerification>(directory, test.Id, "verify");
            ComparisonFileHash[] expected = test.Kind == "drain-small" ? small : [new("large.bin", ComparisonDrain.LargeBytes, root.GetProperty("LargeSha256").GetString()!)];
            if (verify.Id != test.Id || !verify.Bypassed || !verify.Restored || !verify.Verified || !verify.Files.SequenceEqual(expected) ||
                expected.Any(f => f.Bytes != (test.Kind == "drain-small" ? ComparisonDrain.SmallFileBytes : ComparisonDrain.LargeBytes) ||
                    f.Sha256 is null || f.Sha256.Length != 64 || !f.Sha256.All(char.IsAsciiHexDigit)))
                throw new InvalidDataException("Missing, failed or unrestored cache-bypassed file verification: " + test.Id);
            return score;
        }).ToArray();
        double Median(IEnumerable<double> values) => ComparisonPlan.Spread(values.ToArray()).Median;
        return scores.GroupBy(s => ComparisonPlan.Shape(s.Id)).Select(group =>
        {
            var speed = ComparisonPlan.Spread(group.Select(s => s.MBPerSecond).ToArray());
            return new DrainRow(provider, group.Key, group.Count(), speed.Median, speed.Min, speed.Max,
                Median(group.Select(s => s.ApplicationSeconds)), Median(group.Select(s => s.DrainSeconds)), Median(group.Select(s => s.TotalSeconds)),
                Median(group.Select(s => (double)(s.DiskAfter.BytesWritten - s.DiskBefore.BytesWritten))),
                Median(group.Select(s => (double)(s.DiskAfter.WriteOperations - s.DiskBefore.WriteOperations))),
                Median(group.Select(s => (double)(s.DiskAfter.BytesWritten - s.DiskBefore.BytesWritten) / (s.DiskAfter.WriteOperations - s.DiskBefore.WriteOperations))));
        }).ToArray();
    }

    private static T Read<T>(string directory, string id, string suffix)
    {
        var json = File.ReadAllText(Path.Combine(directory, $"compare.{id}.{suffix}.json"));
        if (typeof(T) == typeof(QueueCache.Management.WriteCacheState))
        {
            // Snapshot DTO defaults preserve old recovery journals. They cannot turn
            // missing observed counters into zero in new measurement/oracle evidence.
            using var document = JsonDocument.Parse(json);
            foreach (var field in new[] { "Flags", "LastError", "Instance", "BudgetBytes", "DirtyBytes", "InFlightBytes",
                "Errors", "ReadHitBytes", "ReadMissBytes", "AcceptedBytes" })
                if (!document.RootElement.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Number)
                    throw new InvalidDataException("Missing observed cache field: " + field);
            foreach (var (group, fields) in new[] {
                ("Options", new[] { "Allocation", "WritePercent", "RetainWrites", "PromoteOnRead", "Drain", "LowPercent", "HighPercent", "MaxDirtyAgeMs", "IdleMs", "BatchKiB", "Parallelism" }),
                ("Performance", new[] { "Frequency", "TimingEnabled", "DrainGatherPolicy" }) })
            {
                if (!document.RootElement.TryGetProperty(group, out var value) || value.ValueKind != JsonValueKind.Object ||
                    fields.Any(field => !value.TryGetProperty(field, out _)))
                    throw new InvalidDataException("Missing observed cache evidence: " + group);
            }
        }
        return JsonSerializer.Deserialize<T>(json)
            ?? throw new InvalidDataException($"Missing comparison evidence compare.{id}.{suffix}.json.");
    }

    public static IReadOnlyList<ShapeRow> Shapes(string directory, IReadOnlyList<ComparisonCase> plan)
    {
        static double? Median(IEnumerable<double?> values)
        {
            // Repetitions that completed nothing have no latency; the median covers the ones that did.
            var list = values.Where(v => v is not null).Select(v => v!.Value).ToArray();
            return list.Length == 0 ? null : ComparisonPlan.Spread(list).Median;
        }
        return plan.Where(c => c.Kind == "diskspd").GroupBy(c => ComparisonPlan.Shape(c.Id)).Select(group =>
        {
            var scores = group.Select(c => Read<ComparisonScore>(directory, c.Id, "score")).ToArray();
            var speed = ComparisonPlan.Spread(scores.Select(s => s.MBPerSecond).ToArray());
            return new ShapeRow(group.Key, scores.Length, speed.Median, speed.Min, speed.Max,
                ComparisonPlan.Spread(scores.Select(s => s.Iops).ToArray()).Median,
                Median(scores.Select(s => s.ReadP50Ms)), Median(scores.Select(s => s.ReadP99Ms)), Median(scores.Select(s => s.ReadP999Ms)),
                Median(scores.Select(s => s.WriteP50Ms)), Median(scores.Select(s => s.WriteP99Ms)), Median(scores.Select(s => s.WriteP999Ms)),
                ComparisonPlan.Spread(scores.Select(s => s.CpuPercent).ToArray()).Median);
        }).ToList();
    }

    public static IReadOnlyList<StepRow> Steps(string directory, IReadOnlyList<ComparisonCase> plan)
    {
        var rows = new List<StepRow>();
        void Add(string shape, string measure, IReadOnlyCollection<double> values)
        {
            var spread = ComparisonPlan.Spread(values);
            rows.Add(new(shape, values.Count, measure, spread.Median, spread.Min, spread.Max));
        }
        foreach (var group in plan.Where(c => c.Kind is "warm" or "absorb" or "sustained" or "flush" or "fill").GroupBy(c => ComparisonPlan.Shape(c.Id)))
        {
            var passes = group.Select(c =>
            {
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, $"compare.{c.Id}.pass.json")));
                var pass = document.RootElement.TryGetProperty("Pass", out var inner) ? inner : document.RootElement;
                return JsonSerializer.Deserialize<SequentialPass>(pass.GetRawText()) ?? throw new InvalidDataException("Missing pass " + c.Id);
            }).ToArray();
            Add(group.Key, "MB/s", passes.Select(p => p.MBPerSecond).ToArray());
            if (group.First().Kind == "flush")
                Add(group.Key, "flush seconds", passes.Select(p => p.FlushSeconds ?? throw new InvalidDataException("Missing flush time.")).ToArray());
            if (group.First().Kind == "sustained")
                Add(group.Key, "slowest second MB/s", passes.Select(p => (p.BytesPerSecond ?? throw new InvalidDataException("Missing per-second series."))
                    .Take(Math.Max(1, p.BytesPerSecond.Length - 1)).Min() / 1_000_000.0).ToArray());
        }
        var trees = plan.Where(c => c.Kind == "tree").Select(c => Read<TreeResult>(directory, c.Id, "tree")).ToArray();
        if (trees.Length > 0)
        {
            Add("tree", "create seconds", trees.Select(t => t.CreateSeconds).ToArray());
            Add("tree", "copy seconds (4 threads)", trees.Select(t => t.CopySeconds).ToArray());
            Add("tree", "read and hash seconds", trees.Select(t => t.ReadSeconds).ToArray());
            Add("tree", "delete seconds", trees.Select(t => t.DeleteSeconds).ToArray());
        }
        return rows;
    }
}
