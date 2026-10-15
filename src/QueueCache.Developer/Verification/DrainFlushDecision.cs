using System.Globalization;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

public sealed record DrainFlushCase(string Id, int Repeat, int Position, int BatchKiB, int Parallelism);

/// <summary>The owned worker waits for a start signal, timestamps the actual Flush call, then records its result.</summary>
public sealed record DrainFlushControlResult(DateTimeOffset EnteredUtc, DateTimeOffset CompletedUtc,
    long ElapsedTicks, long TimestampFrequency,
    WriteCacheState Before, WriteCacheState After, CachePerformance PerformanceBefore,
    CachePerformance PerformanceAfter, CacheDiagnostics DiagnosticsBefore,
    CacheDiagnostics DiagnosticsAfter, bool SeedHandleHeld = false);

public sealed record SeededDrainFlushResult(DrainSeedResult Seed, DateTimeOffset SeedCompletedUtc,
    DrainFlushControlResult Control);

public sealed record DrainFlushMetrics(double DurationMilliseconds, ulong DrainedBytes,
    ulong DrainBatches, ulong DriverDrainBytes, ulong LowerWriteAttempts,
    ulong LowerFlushAttempts, ulong LowerWritesCompleted, ulong PendingBytesAfter);

public sealed record DrainFlushEvidence(DrainFlushCase Case, DrainSeedResult Seed, bool SeedVerified,
    ProcessIdentity WorkerProcess, DateTimeOffset SeedCompletedUtc, DateTimeOffset WorkerReadyUtc,
    DateTimeOffset ReleaseUtc, DrainFlushControlResult Control, string WorkerProcessEvidence,
    string SeedVerificationEvidence, DrainFlushMetrics Metrics);

public sealed record DrainFlushSummaryRow(int BatchKiB, int Parallelism, int Samples,
    double DurationMillisecondsMedian, double DurationMillisecondsMin, double DurationMillisecondsMax,
    double MiBPerSecondMedian, double DrainBatchKiBMedian,
    double LowerWriteAttemptsMedian, IReadOnlyList<string> CaseIds);

public sealed record DrainFlushSummary(int PlanVersion, int CaseCount,
    IReadOnlyList<DrainFlushSummaryRow> Rows);

/// <summary>Timing-off explicit-flush comparison, with process startup excluded by a ready/start handshake.</summary>
public static class DrainFlushDecision
{
    public const int RequiredRepeats = 3;
    public const int RequiredBudgetMiB = 2048;
    public const int SeedBytes = RequiredBudgetMiB / 4 * (1 << 20);
    public const string OriginalArm = "b256-p2";
    public const string CandidateArm = "b1024-p4";

    public static IReadOnlyList<DrainFlushCase> Cases(VerificationOptions options)
    {
        if (options.Suite != "drain-flush-decision") return [];
        var cases = new List<DrainFlushCase>(RequiredRepeats * 2);
        for (var repeat = 1; repeat <= options.Repeats; repeat++)
        {
            var candidateFirst = repeat % 2 == 0;
            var settings = candidateFirst
                ? new[] { (Name: CandidateArm, Batch: 1024, Parallelism: 4), (Name: OriginalArm, Batch: 256, Parallelism: 2) }
                : new[] { (Name: OriginalArm, Batch: 256, Parallelism: 2), (Name: CandidateArm, Batch: 1024, Parallelism: 4) };
            for (var index = 0; index < settings.Length; index++)
            {
                var setting = settings[index];
                var position = index + 1;
                cases.Add(new($"drain-flush-r{repeat:D2}-p{position:D2}-{setting.Name}", repeat,
                    position, setting.Batch, setting.Parallelism));
            }
        }
        return cases;
    }

    public static void ValidateOptions(VerificationOptions options)
    {
        if (options.Suite != "drain-flush-decision" || options.Repeats != RequiredRepeats ||
            options.BudgetMiB != RequiredBudgetMiB || options.DiskSpd is not null ||
            options.CaseFilter is not null || options.SoakSeconds is not null)
            throw new ArgumentException("drain-flush-decision requires --budget-mib 2048, exactly three repetitions, and no DiskSpd, case filter or soak.");
        if (Cases(options).Count != RequiredRepeats * 2)
            throw new InvalidDataException("Explicit-drain plan did not create its complete six-case matrix.");
    }

    public static void ValidateCase(DrainFlushCase test)
    {
        if (test.Repeat is < 1 or > RequiredRepeats || test.Position is < 1 or > 2 ||
            !Cases(new VerificationOptions("Q:", "drain-flush-decision", BudgetMiB: RequiredBudgetMiB,
                Repeats: RequiredRepeats)).Contains(test))
            throw new InvalidDataException("Explicit-drain case is outside the declared plan-137 workload bounds.");
    }

    public static DrainFlushMetrics ValidateEvidence(DrainFlushCase test, DrainSeedResult seed,
        ProcessIdentity process, DateTimeOffset seedCompletedUtc, DateTimeOffset workerReadyUtc,
        DateTimeOffset releaseUtc, DrainFlushControlResult control,
        ulong expectedInstance, ulong expectedErrors)
    {
        ValidateCase(test);
        bool MatchesPolicy(WriteCacheState state) => state.Operational && state.Enabled &&
            state.UnsafeDefer && state.SupportsReadWrite &&
            state.Options is { Drain: DrainAlgorithm.Deferred, WritePercent: 100, RetainWrites: true,
                PromoteOnRead: true, LowPercent: 40, HighPercent: 80, MaxDirtyAgeMs: 3_600_000,
                IdleMs: 250 } policy && policy.BatchKiB == test.BatchKiB &&
            policy.Parallelism == test.Parallelism;
        if (seed.Bytes != SeedBytes || seed.OwnedBytes < (ulong)seed.Bytes ||
            seed.Before.Instance != expectedInstance || seed.After.Instance != expectedInstance ||
            seed.Before.Errors != expectedErrors || seed.After.Errors != expectedErrors ||
            !MatchesPolicy(seed.Before) || !MatchesPolicy(seed.After) || seed.After.DirtyBytes != seed.OwnedBytes ||
            seed.After.InFlightBytes != 0 || seed.After.AcceptedBytes < seed.Before.AcceptedBytes ||
            seed.After.AcceptedBytes - seed.Before.AcceptedBytes != seed.OwnedBytes ||
            seed.PerformanceBefore.TimingEnabled != 0 || seed.PerformanceAfter.TimingEnabled != 0 ||
            seed.AttributionAfter.LowerReadAttempts != seed.AttributionBefore.LowerReadAttempts ||
            seed.AttributionAfter.LowerWriteAttempts != seed.AttributionBefore.LowerWriteAttempts ||
            seed.AttributionAfter.LowerFlushAttempts != seed.AttributionBefore.LowerFlushAttempts)
            throw new InvalidDataException("The explicit-drain seed was not admitted as a fitting Deferred dirty set with timing off.");

        var processStart = new DateTimeOffset(DateTime.SpecifyKind(process.StartedUtc, DateTimeKind.Utc));
        if (process.Pid <= 0 || processStart == default || workerReadyUtc == default ||
            seedCompletedUtc == default || releaseUtc == default || control.EnteredUtc == default ||
            control.CompletedUtc == default || processStart > seedCompletedUtc || workerReadyUtc < seedCompletedUtc ||
            releaseUtc < workerReadyUtc)
            throw new InvalidDataException("The explicit-flush worker readiness and operation timestamps are invalid.");

        var before = control.Before;
        var after = control.After;
        var attributionBefore = control.DiagnosticsBefore.Attribution ??
            throw new NotSupportedException("Explicit-drain comparison requires lower-I/O attempt counters.");
        var attributionAfter = control.DiagnosticsAfter.Attribution ??
            throw new NotSupportedException("Explicit-drain comparison requires lower-I/O attempt counters.");
        if (!control.SeedHandleHeld || before.Instance != expectedInstance || after.Instance != expectedInstance ||
            before.Errors != expectedErrors || after.Errors != expectedErrors ||
            before.LastError != 0 || after.LastError != 0 || !MatchesPolicy(before) || !MatchesPolicy(after) ||
            before.DirtyBytes != seed.OwnedBytes || before.InFlightBytes != 0 ||
            before.DrainedBytes != seed.After.DrainedBytes || before.AcceptedBytes != seed.After.AcceptedBytes ||
            after.DirtyBytes != 0 || after.InFlightBytes != 0 || after.DrainedBytes < before.DrainedBytes ||
            after.AcceptedBytes < before.AcceptedBytes ||
            after.DrainedBytes - before.DrainedBytes < seed.OwnedBytes ||
            control.ElapsedTicks <= 0 || control.TimestampFrequency <= 0 ||
            control.PerformanceBefore.TimingEnabled != 0 || control.PerformanceAfter.TimingEnabled != 0 ||
            control.PerformanceBefore.Frequency == 0 || control.PerformanceAfter.Frequency == 0)
            throw new InvalidDataException("Explicit flush did not drain the recorded backlog under the expected timing-off policy.");

        static ulong Delta(ulong current, ulong previous, string counter) => current >= previous
            ? current - previous : throw new InvalidDataException("Explicit-drain " + counter + " counter decreased.");
        var batches = Delta(control.PerformanceAfter.DrainBatches, control.PerformanceBefore.DrainBatches, "drain-batches");
        var driverBytes = Delta(control.PerformanceAfter.DrainBytes, control.PerformanceBefore.DrainBytes, "drain-bytes");
        var writeAttempts = Delta(attributionAfter.LowerWriteAttempts, attributionBefore.LowerWriteAttempts, "lower-write-attempts");
        var flushAttempts = Delta(attributionAfter.LowerFlushAttempts, attributionBefore.LowerFlushAttempts, "lower-flush-attempts");
        var writesCompleted = Delta(after.LowerWrites, before.LowerWrites, "lower-writes-completed");
        var drainedBytes = Delta(after.DrainedBytes, before.DrainedBytes, "drained-bytes");
        if (batches == 0 || driverBytes < seed.OwnedBytes || writeAttempts == 0 || flushAttempts == 0 ||
            writesCompleted == 0 || drainedBytes < seed.OwnedBytes)
            throw new InvalidDataException("Explicit flush lacks complete lower-write, lower-flush or drain-batch evidence.");
        var durationMilliseconds = control.ElapsedTicks * 1000d / control.TimestampFrequency;
        if (!double.IsFinite(durationMilliseconds) || durationMilliseconds <= 0)
            throw new InvalidDataException("Explicit-flush monotonic duration is invalid.");
        return new(durationMilliseconds,
            drainedBytes, batches, driverBytes, writeAttempts, flushAttempts, writesCompleted,
            after.DirtyBytes + after.InFlightBytes);
    }

    public static DrainFlushSummary BuildSummary(IReadOnlyList<DrainFlushEvidence> evidence)
    {
        var expectedCases = Cases(new VerificationOptions("Q:", "drain-flush-decision",
            BudgetMiB: RequiredBudgetMiB, Repeats: RequiredRepeats));
        var expectedById = expectedCases.ToDictionary(test => test.Id, StringComparer.Ordinal);
        foreach (var row in evidence)
        {
            var metrics = ValidateEvidence(row.Case, row.Seed, row.WorkerProcess,
                row.SeedCompletedUtc, row.WorkerReadyUtc, row.ReleaseUtc, row.Control,
                row.Seed.After.Instance, row.Seed.After.Errors);
            if (metrics != row.Metrics)
                throw new InvalidDataException("Explicit-drain derived metrics disagree with their raw worker evidence.");
        }
        if (evidence.Count != expectedCases.Count || evidence.Select(row => row.Case.Id).Distinct().Count() != evidence.Count ||
            evidence.Any(row => !expectedById.TryGetValue(row.Case.Id, out var expected) || row.Case != expected ||
                !row.SeedVerified || row.Seed.Bytes != SeedBytes || row.WorkerProcess.Pid <= 0 || row.WorkerProcessEvidence.Length == 0 ||
                row.SeedVerificationEvidence.Length == 0 || !double.IsFinite(row.Metrics.DurationMilliseconds) || row.Metrics.DurationMilliseconds <= 0 ||
                row.Metrics.PendingBytesAfter != 0 || row.Metrics.DrainBatches == 0 ||
                row.Metrics.DriverDrainBytes < row.Seed.OwnedBytes ||
                row.Metrics.LowerWriteAttempts == 0 || row.Metrics.LowerFlushAttempts == 0 ||
                row.Metrics.LowerWritesCompleted == 0 || row.Metrics.DrainedBytes < row.Seed.OwnedBytes))
            throw new InvalidDataException("Explicit-drain summary requires every verified case in the complete six-case matrix.");
        static double Median(IEnumerable<double> source)
        {
            var sorted = source.Order().ToArray();
            return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
        }
        var rows = evidence.GroupBy(row => new { row.Case.BatchKiB, row.Case.Parallelism })
            .Select(group =>
            {
                var cases = group.ToArray();
                return new DrainFlushSummaryRow(group.Key.BatchKiB, group.Key.Parallelism, cases.Length,
                    Median(cases.Select(row => row.Metrics.DurationMilliseconds)),
                    cases.Min(row => row.Metrics.DurationMilliseconds), cases.Max(row => row.Metrics.DurationMilliseconds),
                    Median(cases.Select(row => row.Metrics.DrainedBytes / (double)(1 << 20) /
                        (row.Metrics.DurationMilliseconds / 1000))),
                    Median(cases.Select(row => row.Metrics.DriverDrainBytes /
                        (double)row.Metrics.DrainBatches / 1024)),
                    Median(cases.Select(row => (double)row.Metrics.LowerWriteAttempts)),
                    cases.Select(row => row.Case.Id).ToArray());
            }).OrderBy(row => row.BatchKiB).ToArray();
        return new(VerificationPlan.Version, evidence.Count, rows);
    }

    public static string RenderSummary(DrainFlushSummary summary)
    {
        var text = new System.Text.StringBuilder($"# Explicit-drain comparison\n\nPlan {summary.PlanVersion}; {summary.CaseCount}/6 cases. Timing diagnostics were OFF. Flush duration uses a monotonic timer around the driver call; UTC timestamps are retained for audit and may move with wall-clock adjustments. The owned worker's ready/start handshake excludes process launch. Collection only.\n\n");
        text.AppendLine("| Batch × parallelism | Cases | Flush median ms (range) | Drain MiB/s median | Mean drain batch KiB | Lower write attempts median |");
        text.AppendLine("|---:|---:|---:|---:|---:|---:|");
        foreach (var row in summary.Rows)
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "| {0} KiB × {1} | {2} | {3:F0} ({4:F0}–{5:F0}) | {6:F2} | {7:F1} | {8:F0} |",
                row.BatchKiB, row.Parallelism, row.Samples, row.DurationMillisecondsMedian,
                row.DurationMillisecondsMin, row.DurationMillisecondsMax, row.MiBPerSecondMedian,
                row.DrainBatchKiBMedian, row.LowerWriteAttemptsMedian));
        return text.ToString();
    }
}
