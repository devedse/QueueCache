using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

public sealed record StrictImpactCase(string Id, int Repeat, int Position, int BatchKiB,
    int Parallelism, string SeedShape, string Boundary, int Seed);

public sealed record StrictImpactSnapshot(WriteCacheState State, CachePerformance Performance,
    CacheDiagnostics Diagnostics);

public sealed record StrictImpactSeedResult(StrictImpactCase Case, long PreparedFileBytes,
    int PayloadBytes, long ElapsedTicks, long TimestampFrequency,
    StrictImpactSnapshot Before, StrictImpactSnapshot After);

public sealed record StrictImpactBoundaryControlResult(string Boundary, long OverwriteOffset,
    DateTimeOffset OperationEnteredUtc, DateTimeOffset OperationCompletedUtc,
    long ElapsedTicks, long TimestampFrequency,
    StrictImpactSnapshot Before, StrictImpactSnapshot After);

public sealed record StrictImpactOracleResult(long FileBytes, int BlocksChecked, long BytesChecked,
    string ExpectedSha256, string ActualSha256, bool? OverwrittenBlockVerified);

public sealed record StrictImpactCounterDelta(ulong AcceptedBytes, ulong DrainedBytes,
    long DirtyBytes, long InFlightBytes, ulong ReadHitBytes, ulong ReadMissBytes,
    ulong LowerWrites, ulong Flushes,
    ulong DrainBatches, ulong DrainBytes, ulong CapacityWaits, ulong LowerIoTicks,
    ulong ApplicationFlushes, ulong DeferredFlushes, ulong WriteThroughWrites,
    ulong DeferredWriteThroughWrites, ulong ApplicationBarriers, ulong StrictWriteBarriers,
    ulong LowerReadAttempts, ulong LowerWriteAttempts, ulong LowerFlushAttempts);

public sealed record StrictImpactMetrics(double SeedWriteMilliseconds, double BoundaryMilliseconds,
    StrictImpactCounterDelta Seed, StrictImpactCounterDelta Boundary,
    StrictImpactCounterDelta Oracle);

public sealed record StrictImpactEvidence(StrictImpactCase Case, StrictImpactSeedResult Seed,
    ProcessIdentity WorkerProcess, DateTimeOffset WorkerReadyUtc, DateTimeOffset ReleaseUtc,
    StrictImpactBoundaryControlResult Control, StrictImpactSnapshot BeforeOracle,
    StrictImpactSnapshot AfterOracle,
    StrictImpactOracleResult Oracle, StrictImpactMetrics Metrics,
    string WorkerProcessEvidence, string SeedEvidence);

public sealed record StrictImpactSummaryRow(int BatchKiB, int Parallelism, string SeedShape,
    string Boundary, int Samples, double SeedWriteMillisecondsMedian,
    double BoundaryMillisecondsMedian, double BoundaryMillisecondsMin,
    double BoundaryMillisecondsMax, double DrainedMiBMedian,
    double DrainBatchesMedian, double DrainBatchKiBMedian,
    IReadOnlyList<string> CaseIds);

public sealed record StrictImpactSummary(int PlanVersion, int CaseCount,
    IReadOnlyList<StrictImpactSummaryRow> Rows);

/// <summary>Maintained diagnostic matrix for Strict durability-barrier impact; it does not tune the driver.</summary>
public static class StrictImpactDecision
{
    public const int RequiredRepeats = 3;
    public const int RequiredCases = 24;
    public const int RequiredBudgetMiB = 2048;
    public const int SeedBlockBytes = 1 << 20;
    public const int SeedBlockCount = 512;
    public const int SeedPayloadBytes = SeedBlockBytes * SeedBlockCount;
    public const int OverwriteBytes = SeedBlockBytes;
    public const long PreparedFileBytes = 4L << 30;
    public const int SeedMetadataAllowanceBytes = 64 << 20;
    public const int WorkerTimeoutSeconds = 600;
    public const string Sequential = "sequential";
    public const string Scattered = "scattered";
    public const string ApplicationFlush = "application-flush";
    public const string WriteThrough = "write-through";
    public const string OriginalArm = "b256-p2";
    public const string CandidateArm = "b1024-p4";

    private sealed record Shape(int BatchKiB, int Parallelism, string Name);
    private sealed record Combination(Shape Shape, string SeedShape, string Boundary);

    private static readonly Shape[] Shapes =
    [
        new(256, 2, OriginalArm),
        new(1024, 4, CandidateArm)
    ];

    private static readonly Combination[] RotatingOrder =
    [
        new(Shapes[0], Sequential, ApplicationFlush),
        new(Shapes[1], Sequential, ApplicationFlush),
        new(Shapes[0], Sequential, WriteThrough),
        new(Shapes[1], Sequential, WriteThrough),
        new(Shapes[0], Scattered, ApplicationFlush),
        new(Shapes[1], Scattered, ApplicationFlush),
        new(Shapes[0], Scattered, WriteThrough),
        new(Shapes[1], Scattered, WriteThrough)
    ];

    public static CacheOptions Options(StrictImpactCase test) => new(CacheAllocation.Automatic,
        WritePercent: 100, RetainWrites: true, PromoteOnRead: true,
        Drain: DrainAlgorithm.Deferred, LowPercent: 40, HighPercent: 80,
        MaxDirtyAgeMs: 3_600_000, IdleMs: 60_000,
        BatchKiB: test.BatchKiB, Parallelism: test.Parallelism);

    public static CacheConfiguration Configuration(StrictImpactCase test) =>
        new(RequiredBudgetMiB, CachePreset.Strict, true) { Options = Options(test) };

    public static IReadOnlyList<StrictImpactCase> Cases(VerificationOptions options)
    {
        if (options.Suite != "strict-impact-decision") return [];
        var cases = new List<StrictImpactCase>(RequiredCases);
        for (var repeat = 1; repeat <= options.Repeats; repeat++)
        {
            var rotation = (repeat - 1) * 3;
            for (var index = 0; index < RotatingOrder.Length; index++)
            {
                var combination = RotatingOrder[(index + rotation) % RotatingOrder.Length];
                var position = index + 1;
                var seed = 108_000 + repeat * 100 + (combination.SeedShape == Scattered ? 1 : 0);
                var id = $"strict-impact-r{repeat:D2}-p{position:D2}-{combination.Shape.Name}-" +
                    $"{combination.SeedShape}-{combination.Boundary}";
                cases.Add(new(id, repeat, position, combination.Shape.BatchKiB,
                    combination.Shape.Parallelism, combination.SeedShape, combination.Boundary, seed));
            }
        }
        return cases;
    }

    public static void ValidateOptions(VerificationOptions options)
    {
        if (options.Suite != "strict-impact-decision" || options.Repeats != RequiredRepeats ||
            options.BudgetMiB != RequiredBudgetMiB || options.DiskSpd is not null ||
            options.CaseFilter is not null || options.SoakSeconds is not null)
            throw new ArgumentException("strict-impact-decision requires --budget-mib 2048, exactly three repetitions, and no DiskSpd, case filter or soak.");
        var cases = Cases(options);
        if (cases.Count != RequiredCases || cases.Select(test => test.Id).Distinct(StringComparer.Ordinal).Count() != RequiredCases)
            throw new InvalidDataException("Strict-impact plan did not create the complete 24-case matrix with unique IDs.");
    }

    public static void ValidateCase(StrictImpactCase test)
    {
        var expected = Cases(new VerificationOptions("Q:", "strict-impact-decision",
            BudgetMiB: RequiredBudgetMiB, Repeats: RequiredRepeats));
        if (string.IsNullOrWhiteSpace(test.Id) || !expected.Contains(test))
            throw new InvalidDataException("Strict-impact case is outside the immutable plan-138 matrix.");
    }

    public static long SeedOffset(StrictImpactCase test, int blockIndex)
    {
        ValidateCase(test);
        if (blockIndex is < 0 or >= SeedBlockCount)
            throw new ArgumentOutOfRangeException(nameof(blockIndex));
        var offset = test.SeedShape == Sequential
            ? (long)blockIndex * SeedBlockBytes
            : (7L + (long)blockIndex * 8) * SeedBlockBytes;
        if (offset < 0 || offset + SeedBlockBytes > PreparedFileBytes || offset % SeedBlockBytes != 0)
            throw new InvalidDataException("Strict-impact seed block falls outside the prepared 4 GiB file.");
        return offset;
    }

    public static byte[] SeedBlock(StrictImpactCase test, int blockIndex)
    {
        var block = new byte[SeedBlockBytes];
        new Random(unchecked(test.Seed * 397 ^ blockIndex)).NextBytes(block);
        return block;
    }

    public static byte[] NewestBlock(StrictImpactCase test, int blockIndex)
    {
        var block = SeedBlock(test, blockIndex);
        if (test.Boundary == WriteThrough && blockIndex == 0)
            Array.Fill(block, (byte)0xA7);
        return block;
    }

    public static byte[] WriteThroughPayload()
    {
        var bytes = new byte[OverwriteBytes];
        Array.Fill(bytes, (byte)0xA7);
        return bytes;
    }

    public static void ValidateSeed(StrictImpactSeedResult seed, ulong expectedInstance,
        ulong expectedErrors)
    {
        ValidateCase(seed.Case);
        var before = seed.Before;
        var after = seed.After;
        ValidateSnapshot(before, seed.Case, expectedInstance, expectedErrors);
        ValidateSnapshot(after, seed.Case, expectedInstance, expectedErrors);
        var delta = CounterDelta(before, after);
        if (seed.PreparedFileBytes != PreparedFileBytes || seed.PayloadBytes != SeedPayloadBytes ||
            seed.ElapsedTicks <= 0 || seed.TimestampFrequency <= 0 ||
            before.State.DirtyBytes != 0 || before.State.InFlightBytes != 0 ||
            after.State.DirtyBytes < (ulong)SeedPayloadBytes ||
            after.State.DirtyBytes > (ulong)(SeedPayloadBytes + SeedMetadataAllowanceBytes) ||
            after.State.InFlightBytes != 0 || delta.AcceptedBytes < (ulong)SeedPayloadBytes ||
            delta.AcceptedBytes > (ulong)(SeedPayloadBytes + SeedMetadataAllowanceBytes) ||
            after.State.DirtyBytes != delta.AcceptedBytes || delta.DrainedBytes != 0 ||
            delta.DrainBatches != 0 || delta.DrainBytes != 0 ||
            delta.ApplicationFlushes != 0 || delta.DeferredFlushes != 0 ||
            delta.WriteThroughWrites != 0 || delta.DeferredWriteThroughWrites != 0 ||
            delta.ApplicationBarriers != 0 || delta.StrictWriteBarriers != 0)
            throw new InvalidDataException("Strict-impact seed was not the bounded, timing-off dirty set with incidental drains and durability boundaries held off.");
    }

    public static StrictImpactMetrics ValidateEvidence(StrictImpactEvidence evidence,
        ulong expectedInstance, ulong expectedErrors)
    {
        ValidateCase(evidence.Case);
        if (evidence.Seed.Case != evidence.Case || evidence.Control.Boundary != evidence.Case.Boundary ||
            evidence.WorkerProcess.Pid <= 0 || evidence.WorkerProcess.StartedUtc == default ||
            evidence.WorkerReadyUtc == default || evidence.ReleaseUtc == default ||
            string.IsNullOrWhiteSpace(evidence.WorkerProcessEvidence) ||
            string.IsNullOrWhiteSpace(evidence.SeedEvidence))
            throw new InvalidDataException("Strict-impact evidence is missing its case, owned worker or ready/start proof.");
        ValidateSeed(evidence.Seed, expectedInstance, expectedErrors);
        ValidateOracle(evidence.Case, evidence.Oracle);

        var control = evidence.Control;
        var processStartedUtc = new DateTimeOffset(DateTime.SpecifyKind(
            evidence.WorkerProcess.StartedUtc, DateTimeKind.Utc));
        if (processStartedUtc > evidence.WorkerReadyUtc || evidence.WorkerReadyUtc > evidence.ReleaseUtc ||
            evidence.ReleaseUtc > control.OperationEnteredUtc ||
            control.OperationEnteredUtc > control.OperationCompletedUtc)
            throw new InvalidDataException("Strict-impact worker identity and ready/start/boundary timestamps are out of order.");
        ValidateSnapshot(control.Before, evidence.Case, expectedInstance, expectedErrors);
        ValidateSnapshot(control.After, evidence.Case, expectedInstance, expectedErrors);
        ValidateSnapshot(evidence.BeforeOracle, evidence.Case, expectedInstance, expectedErrors);
        ValidateSnapshot(evidence.AfterOracle, evidence.Case, expectedInstance, expectedErrors);
        if (evidence.BeforeOracle.State.DirtyBytes != 0 || evidence.BeforeOracle.State.InFlightBytes != 0 ||
            evidence.BeforeOracle.State.AcceptedBytes != control.After.State.AcceptedBytes ||
            evidence.BeforeOracle.State.DrainedBytes != control.After.State.DrainedBytes ||
            evidence.AfterOracle.State.DirtyBytes != 0 || evidence.AfterOracle.State.InFlightBytes != 0 ||
            evidence.AfterOracle.State.AcceptedBytes != control.After.State.AcceptedBytes ||
            evidence.AfterOracle.State.DrainedBytes != control.After.State.DrainedBytes)
            throw new InvalidDataException("Strict-impact cache must remain clean from clean-entry eviction through the oracle and before case cleanup.");
        var seedDelta = CounterDelta(evidence.Seed.Before, evidence.Seed.After);
        var boundaryDelta = CounterDelta(control.Before, control.After);
        var oracleDelta = CounterDelta(evidence.BeforeOracle, evidence.AfterOracle);
        if (control.OperationEnteredUtc == default || control.OperationCompletedUtc == default ||
            control.ElapsedTicks <= 0 || control.TimestampFrequency <= 0 ||
            control.OverwriteOffset != (evidence.Case.Boundary == WriteThrough
                ? SeedOffset(evidence.Case, 0) : -1) ||
            control.Before.State.AcceptedBytes < evidence.Seed.After.State.AcceptedBytes ||
            control.Before.State.AcceptedBytes - evidence.Seed.After.State.AcceptedBytes > SeedMetadataAllowanceBytes ||
            control.Before.State.DirtyBytes < (ulong)SeedPayloadBytes ||
            control.Before.State.DirtyBytes > (ulong)(SeedPayloadBytes + SeedMetadataAllowanceBytes) ||
            control.Before.State.InFlightBytes != 0 ||
            control.Before.State.DrainedBytes != evidence.Seed.After.State.DrainedBytes ||
            control.After.State.DirtyBytes != 0 || control.After.State.InFlightBytes != 0 ||
            boundaryDelta.DrainedBytes < (ulong)SeedPayloadBytes ||
            boundaryDelta.DrainBatches == 0 || boundaryDelta.DrainBytes < (ulong)SeedPayloadBytes ||
            boundaryDelta.LowerWriteAttempts == 0 || boundaryDelta.LowerFlushAttempts == 0 ||
            boundaryDelta.Flushes == 0 ||
            (boundaryDelta.ApplicationBarriers == 0 && evidence.Case.Boundary == ApplicationFlush) ||
            (boundaryDelta.StrictWriteBarriers == 0 && evidence.Case.Boundary == WriteThrough))
            throw new InvalidDataException("Strict-impact boundary did not drain the seed to a clean cache or prove the intended lower durability barrier.");

        if (oracleDelta.LowerReadAttempts < SeedBlockCount || oracleDelta.ReadHitBytes != 0)
            throw new InvalidDataException("Strict-impact unbuffered oracle did not prove lower-device reads without QueueCache cache hits after clean-entry eviction.");

        if ((evidence.Case.Boundary == ApplicationFlush &&
            (boundaryDelta.ApplicationFlushes == 0 || boundaryDelta.DeferredFlushes != 0 ||
             boundaryDelta.WriteThroughWrites != 0 || boundaryDelta.StrictWriteBarriers != 0)) ||
            (evidence.Case.Boundary == WriteThrough &&
            (boundaryDelta.WriteThroughWrites == 0 || boundaryDelta.DeferredWriteThroughWrites != 0 ||
             boundaryDelta.ApplicationFlushes != 0 || boundaryDelta.ApplicationBarriers != 0)))
            throw new InvalidDataException("Strict-impact diagnostic deltas do not identify the requested application Flush or write-through boundary.");

        var seedMilliseconds = evidence.Seed.ElapsedTicks * 1000d / evidence.Seed.TimestampFrequency;
        var boundaryMilliseconds = control.ElapsedTicks * 1000d / control.TimestampFrequency;
        var metrics = new StrictImpactMetrics(seedMilliseconds, boundaryMilliseconds,
            seedDelta, boundaryDelta, oracleDelta);
        if (evidence.Metrics != metrics || !double.IsFinite(seedMilliseconds) || seedMilliseconds <= 0 ||
            !double.IsFinite(boundaryMilliseconds) || boundaryMilliseconds <= 0)
            throw new InvalidDataException("Strict-impact derived metrics disagree with the raw monotonic timing and counter snapshots.");
        return metrics;
    }

    public static void ValidateOracle(StrictImpactCase test, StrictImpactOracleResult oracle)
    {
        ValidateCase(test);
        var expectedOverwrite = test.Boundary == WriteThrough;
        if (oracle.FileBytes != PreparedFileBytes || oracle.BlocksChecked != SeedBlockCount ||
            oracle.BytesChecked != SeedPayloadBytes ||
            !IsSha256(oracle.ExpectedSha256) || !IsSha256(oracle.ActualSha256) ||
            !string.Equals(oracle.ExpectedSha256, oracle.ActualSha256, StringComparison.OrdinalIgnoreCase) ||
            (expectedOverwrite ? oracle.OverwrittenBlockVerified != true : oracle.OverwrittenBlockVerified is not null))
            throw new InvalidDataException("Strict-impact unbuffered byte oracle is incomplete or did not verify the newest write-through block and every seeded block.");
    }

    public static StrictImpactEvidence ParseEvidence(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Strict-impact evidence must be a JSON object.");
        var root = document.RootElement;
        RequireRecord<StrictImpactEvidence>(root);
        RequireRecord<StrictImpactCase>(root.GetProperty("Case"));
        var seed = root.GetProperty("Seed");
        RequireRecord<StrictImpactSeedResult>(seed);
        RequireRecord<StrictImpactCase>(seed.GetProperty("Case"));
        RequireSnapshot(seed.GetProperty("Before"));
        RequireSnapshot(seed.GetProperty("After"));
        var control = root.GetProperty("Control");
        RequireRecord<StrictImpactBoundaryControlResult>(control);
        RequireSnapshot(control.GetProperty("Before"));
        RequireSnapshot(control.GetProperty("After"));
        RequireSnapshot(root.GetProperty("BeforeOracle"));
        RequireSnapshot(root.GetProperty("AfterOracle"));
        RequireRecord<StrictImpactOracleResult>(root.GetProperty("Oracle"));
        var metrics = root.GetProperty("Metrics");
        RequireRecord<StrictImpactMetrics>(metrics);
        RequireRecord<StrictImpactCounterDelta>(metrics.GetProperty("Seed"));
        RequireRecord<StrictImpactCounterDelta>(metrics.GetProperty("Boundary"));
        RequireRecord<StrictImpactCounterDelta>(metrics.GetProperty("Oracle"));
        var evidence = JsonSerializer.Deserialize<StrictImpactEvidence>(json)
            ?? throw new InvalidDataException("Strict-impact evidence is null.");
        ValidateEvidence(evidence, evidence.Seed.Before.State.Instance, evidence.Seed.Before.State.Errors);
        return evidence;

        static void RequireSnapshot(JsonElement element)
        {
            RequireRecord<StrictImpactSnapshot>(element);
            var state = element.GetProperty("State");
            RequireRecord<WriteCacheState>(state);
            foreach (var property in new[] { "ReadHitBytes", "ReadMissBytes" })
            {
                if (!state.TryGetProperty(property, out var counter) ||
                    counter.ValueKind != JsonValueKind.Number || !counter.TryGetUInt64(out _))
                    throw new InvalidDataException($"Strict-impact snapshot is missing the required {property} cache-accounting counter.");
            }
            RequireRecord<CachePerformance>(element.GetProperty("Performance"));
            var diagnostics = element.GetProperty("Diagnostics");
            RequireRecord<CacheDiagnostics>(diagnostics);
            if (!diagnostics.TryGetProperty("Attribution", out var attribution) || attribution.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Strict-impact snapshot is missing lower-I/O and barrier attribution diagnostics.");
            RequireRecord<CacheAttribution>(attribution);
        }

        static void RequireRecord<T>(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Strict-impact evidence record {typeof(T).Name} is missing or not an object.");
            var constructor = typeof(T).GetConstructors().SingleOrDefault()
                ?? throw new InvalidDataException($"Strict-impact evidence record {typeof(T).Name} has no unique public data constructor.");
            foreach (var parameter in constructor.GetParameters())
            {
                var name = parameter.Name is { Length: > 0 } value
                    ? char.ToUpperInvariant(value[0]) + value[1..]
                    : throw new InvalidDataException("Strict-impact evidence contains an unnamed constructor field.");
                if (!element.TryGetProperty(name, out _))
                    throw new InvalidDataException($"Strict-impact evidence record {typeof(T).Name} is missing required field {name}.");
            }
        }
    }

    public static StrictImpactSummary BuildSummary(IReadOnlyList<StrictImpactEvidence> evidence)
    {
        var options = new VerificationOptions("Q:", "strict-impact-decision",
            BudgetMiB: RequiredBudgetMiB, Repeats: RequiredRepeats);
        var expected = Cases(options);
        var expectedById = expected.ToDictionary(test => test.Id, StringComparer.Ordinal);
        foreach (var row in evidence)
        {
            _ = ValidateEvidence(row, row.Seed.Before.State.Instance, row.Seed.Before.State.Errors);
            if (!expectedById.TryGetValue(row.Case.Id, out var test) || test != row.Case)
                throw new InvalidDataException("Strict-impact summary contains a case outside the immutable plan-138 matrix.");
        }
        if (evidence.Count != RequiredCases || evidence.Select(row => row.Case.Id)
                .Distinct(StringComparer.Ordinal).Count() != RequiredCases)
            throw new InvalidDataException("Strict-impact summary requires the complete unique 24-case matrix.");

        static double Median(IEnumerable<double> values)
        {
            var sorted = values.Order().ToArray();
            return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
        }
        var rows = evidence.GroupBy(row => new
            {
                row.Case.BatchKiB, row.Case.Parallelism, row.Case.SeedShape, row.Case.Boundary
            })
            .Select(group =>
            {
                var cases = group.ToArray();
                return new StrictImpactSummaryRow(group.Key.BatchKiB, group.Key.Parallelism,
                    group.Key.SeedShape, group.Key.Boundary, cases.Length,
                    Median(cases.Select(row => row.Metrics.SeedWriteMilliseconds)),
                    Median(cases.Select(row => row.Metrics.BoundaryMilliseconds)),
                    cases.Min(row => row.Metrics.BoundaryMilliseconds),
                    cases.Max(row => row.Metrics.BoundaryMilliseconds),
                    Median(cases.Select(row => row.Metrics.Boundary.DrainedBytes / (double)(1 << 20))),
                    Median(cases.Select(row => (double)row.Metrics.Boundary.DrainBatches)),
                    Median(cases.Select(row => row.Metrics.Boundary.DrainBytes /
                        (double)row.Metrics.Boundary.DrainBatches / 1024)),
                    cases.Select(row => row.Case.Id).ToArray());
            })
            .OrderBy(row => row.SeedShape, StringComparer.Ordinal)
            .ThenBy(row => row.Boundary, StringComparer.Ordinal)
            .ThenBy(row => row.BatchKiB)
            .ToArray();
        if (rows.Length != 8 || rows.Any(row => row.Samples != RequiredRepeats))
            throw new InvalidDataException("Strict-impact summary must contain three samples for each seed, boundary and drain-setting combination.");
        return new(VerificationPlan.Version, evidence.Count, rows);
    }

    public static string RenderSummary(StrictImpactSummary summary)
    {
        if (summary.PlanVersion != VerificationPlan.Version || summary.CaseCount != RequiredCases || summary.Rows.Count != 8)
            throw new InvalidDataException("Strict-impact report requires its complete plan-138 aggregate.");
        var text = new System.Text.StringBuilder($"# Strict durability-impact comparison\n\nPlan {summary.PlanVersion}; {summary.CaseCount}/24 cases. Collection only: no performance verdict or driver-default change. All arms use Strict mode, timing diagnostics OFF and the same age-only Deferred trigger (one-hour maximum dirty age) to keep incidental idle drains out. Each case seeds 512 MiB in 1 MiB blocks across the sequential prefix or across a 4 GiB file at 8 MiB spacing.\n\n");
        text.AppendLine("| Drain batch × parallelism | Seed shape | Boundary | Cases | Seed ms median | Boundary ms median (range) | Drained MiB median | Drain batches median | Mean batch KiB median |");
        text.AppendLine("|---:|---|---|---:|---:|---:|---:|---:|---:|");
        foreach (var row in summary.Rows)
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "| {0} KiB × {1} | {2} | {3} | {4} | {5:F1} | {6:F1} ({7:F1}–{8:F1}) | {9:F1} | {10:F0} | {11:F1} |",
                row.BatchKiB, row.Parallelism, row.SeedShape, row.Boundary, row.Samples,
                row.SeedWriteMillisecondsMedian, row.BoundaryMillisecondsMedian,
                row.BoundaryMillisecondsMin, row.BoundaryMillisecondsMax, row.DrainedMiBMedian,
                row.DrainBatchesMedian, row.DrainBatchKiBMedian));
        text.AppendLine("\nEach boundary is timed with Stopwatch inside an owned worker after its ready/start gate. Before the unbuffered oracle, clean entries are dropped while Strict remains enabled; each oracle must show at least 512 lower-read attempts and zero QueueCache read-hit bytes. The report is emitted only for all 24 validated cases; raw state, performance and diagnostic snapshots plus the byte oracle remain alongside it.");
        return text.ToString();
    }

    public static StrictImpactSnapshot Capture(CacheDevice device) => new(
        device.GetWriteCacheState(), device.GetPerformance(), device.GetDiagnostics());

    public static StrictImpactSeedResult Seed(StrictImpactCase test, string path, CacheDevice device,
        CancellationToken token = default)
    {
        ValidateCase(test);
        if (!File.Exists(path) || new FileInfo(path).Length != PreparedFileBytes)
            throw new IOException("Strict-impact seed requires the prepared 4 GiB writer file.");
        var before = Capture(device);
        if (before.State.DirtyBytes != 0 || before.State.InFlightBytes != 0)
            throw new IOException("Strict-impact seed requires a clean cache after configuration.");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        using (var file = new AlignedFile(path, SeedBlockBytes, create: false))
        {
            for (var index = 0; index < SeedBlockCount; index++)
            {
                token.ThrowIfCancellationRequested();
                file.Write(SeedOffset(test, index), SeedBlock(test, index));
            }
        }
        timer.Stop();
        var after = Capture(device);
        var result = new StrictImpactSeedResult(test, new FileInfo(path).Length,
            SeedPayloadBytes, timer.ElapsedTicks, System.Diagnostics.Stopwatch.Frequency, before, after);
        ValidateSeed(result, before.State.Instance, before.State.Errors);
        return result;
    }

    public static StrictImpactOracleResult VerifySeed(StrictImpactCase test, string path,
        CancellationToken token = default)
    {
        ValidateCase(test);
        if (!File.Exists(path) || new FileInfo(path).Length != PreparedFileBytes)
            throw new IOException("Strict-impact oracle requires the prepared 4 GiB writer file.");
        using var expectedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var actualHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var file = new AlignedFile(path, SeedBlockBytes, create: false, sharedReadOnly: true);
        var actual = new byte[SeedBlockBytes];
        bool? overwriteVerified = test.Boundary == WriteThrough ? true : null;
        long bytesChecked = 0;
        for (var index = 0; index < SeedBlockCount; index++)
        {
            token.ThrowIfCancellationRequested();
            var expected = NewestBlock(test, index);
            file.Read(SeedOffset(test, index), actual);
            expectedHash.AppendData(expected);
            actualHash.AppendData(actual);
            if (!expected.AsSpan().SequenceEqual(actual))
            {
                if (test.Boundary == WriteThrough && index == 0)
                    overwriteVerified = false;
                throw new IOException($"Strict-impact unbuffered byte oracle mismatch at seeded block {index}, offset {SeedOffset(test, index)}.");
            }
            bytesChecked += actual.Length;
        }
        var result = new StrictImpactOracleResult(new FileInfo(path).Length,
            SeedBlockCount, bytesChecked,
            Convert.ToHexString(expectedHash.GetHashAndReset()),
            Convert.ToHexString(actualHash.GetHashAndReset()), overwriteVerified);
        ValidateOracle(test, result);
        return result;
    }

    public static StrictImpactMetrics Metrics(StrictImpactSeedResult seed,
        StrictImpactBoundaryControlResult control, StrictImpactSnapshot beforeOracle,
        StrictImpactSnapshot afterOracle)
    {
        var seedDelta = CounterDelta(seed.Before, seed.After);
        var boundaryDelta = CounterDelta(control.Before, control.After);
        var oracleDelta = CounterDelta(beforeOracle, afterOracle);
        return new(seed.ElapsedTicks * 1000d / seed.TimestampFrequency,
            control.ElapsedTicks * 1000d / control.TimestampFrequency,
            seedDelta, boundaryDelta, oracleDelta);
    }

    public static StrictImpactCounterDelta CounterDelta(StrictImpactSnapshot before,
        StrictImpactSnapshot after)
    {
        var a = before.State;
        var b = after.State;
        var p = before.Performance;
        var q = after.Performance;
        var d = before.Diagnostics;
        var e = after.Diagnostics;
        var first = d.Attribution ?? throw new NotSupportedException("Strict-impact requires lower-I/O and barrier-reason diagnostics.");
        var last = e.Attribution ?? throw new NotSupportedException("Strict-impact requires lower-I/O and barrier-reason diagnostics.");
        return new(
            Delta(b.AcceptedBytes, a.AcceptedBytes, "accepted bytes"),
            Delta(b.DrainedBytes, a.DrainedBytes, "drained bytes"),
            SignedDelta(b.DirtyBytes, a.DirtyBytes),
            SignedDelta(b.InFlightBytes, a.InFlightBytes),
            Delta(b.ReadHitBytes, a.ReadHitBytes, "cache read-hit bytes"),
            Delta(b.ReadMissBytes, a.ReadMissBytes, "cache read-miss bytes"),
            Delta(b.LowerWrites, a.LowerWrites, "lower writes"),
            Delta(b.Flushes, a.Flushes, "flushes"),
            Delta(q.DrainBatches, p.DrainBatches, "drain batches"),
            Delta(q.DrainBytes, p.DrainBytes, "drain bytes"),
            Delta(q.CapacityWaits, p.CapacityWaits, "capacity waits"),
            Delta(q.LowerIoTicks, p.LowerIoTicks, "lower I/O ticks"),
            Delta(e.ApplicationFlushes, d.ApplicationFlushes, "application flushes"),
            Delta(e.DeferredFlushes, d.DeferredFlushes, "deferred flushes"),
            Delta(e.WriteThroughWrites, d.WriteThroughWrites, "write-through writes"),
            Delta(e.DeferredWriteThroughWrites, d.DeferredWriteThroughWrites, "deferred write-through writes"),
            Delta(last.ApplicationBarriers, first.ApplicationBarriers, "application barriers"),
            Delta(last.StrictWriteBarriers, first.StrictWriteBarriers, "Strict write barriers"),
            Delta(last.LowerReadAttempts, first.LowerReadAttempts, "lower-read attempts"),
            Delta(last.LowerWriteAttempts, first.LowerWriteAttempts, "lower-write attempts"),
            Delta(last.LowerFlushAttempts, first.LowerFlushAttempts, "lower-flush attempts"));
    }

    public static void ValidateSnapshot(StrictImpactSnapshot snapshot, StrictImpactCase test,
        ulong expectedInstance, ulong expectedErrors)
    {
        var state = snapshot.State;
        if (!state.Operational || !state.Enabled || state.UnsafeDefer || !state.SupportsReadWrite ||
            expectedInstance == 0 || state.Instance != expectedInstance || state.Errors != expectedErrors || state.LastError != 0 ||
            state.Options != Options(test) || snapshot.Performance.TimingEnabled != 0 ||
            snapshot.Performance.Frequency == 0 || snapshot.Diagnostics.Attribution is null)
            throw new InvalidDataException("Strict-impact snapshot does not match the intended active Strict policy, timing-off mode and diagnostic protocol.");
    }

    private static ulong Delta(ulong after, ulong before, string name) => after >= before
        ? after - before
        : throw new InvalidDataException("Strict-impact " + name + " counter decreased.");

    private static long SignedDelta(ulong after, ulong before)
    {
        if (after >= before) return checked((long)(after - before));
        return -checked((long)(before - after));
    }

    private static bool IsSha256(string value)
    {
        if (value.Length != 64) return false;
        try { return Convert.FromHexString(value).Length == 32; }
        catch (FormatException) { return false; }
    }
}
