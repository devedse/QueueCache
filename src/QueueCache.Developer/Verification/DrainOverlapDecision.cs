using System.Globalization;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

public sealed record DrainOverlapCase(string Id, int Repeat, int Position, string Workload,
    int BatchKiB, int Parallelism);

public sealed record DrainOverlapMetrics(int SharedTelemetryIntervals, ulong ForegroundBytesDuringDrain,
    ulong DrainedBytesDuringForeground, ulong LowerWriteAttemptsDuringOverlap,
    ulong DrainBatchesDuringForeground, ulong DriverDrainBytesDuringForeground,
    ulong CapacityWaits, ulong TotalDrainedBytes, ulong RemainingDirtyBytes,
    double MaximumSharedIntervalMilliseconds, double BackgroundDrainCompletionSeconds);

public sealed record DrainOverlapEvidence(DrainOverlapCase Case, DrainSeedResult Seed,
    bool SeedVerified, CacheLayoutSnapshot IdleCompletion, ProcessIdentity WorkloadProcess,
    DateTimeOffset SeedCompletedUtc, DateTimeOffset WorkloadFinishedUtc, DateTimeOffset DrainStartedUtc,
    DateTimeOffset BackgroundCompletedUtc,
    string WorkloadProcessEvidence, string DiskSpdSha256, string TelemetryEvidence,
    int TelemetrySamples, DiskSpdScore Score, DrainOverlapMetrics Metrics);

public sealed record DrainOverlapSummaryRow(string Workload, int BatchKiB, int Parallelism,
    int Samples, double IopsMedian, double IopsMin, double IopsMax,
    double P99MillisecondsMedian, double ForegroundMiBDuringDrainMedian,
    double DrainedMiBDuringForegroundMedian, double CapacityWaitsMedian,
    double DrainBatchBytesMedian, double MaximumSharedIntervalMilliseconds,
    double BackgroundDrainCompletionSecondsMedian,
    IReadOnlyList<string> CaseIds);

public sealed record DrainOverlapSummary(int PlanVersion, int CaseCount,
    IReadOnlyList<DrainOverlapSummaryRow> Rows);

/// <summary>Timing-off comparison of real Idle background drain overlapping foreground I/O.</summary>
public static class DrainOverlapDecision
{
    public const int RequiredRepeats = 3;
    public const int RequiredBudgetMiB = 2048;
    public const int SeedPercentOfBudget = 75;
    public const int MinimumConcurrentMiB = 1;
    public const int MaximumSharedIntervalMilliseconds = 500;
    public const int SeedPreparationTimeoutSeconds = 600;
    public const int DrainStartDeadlineSeconds = 30;
    public const int CompletionAllowanceSeconds = 300;
    public const string OriginalArm = "b256-p2";
    public const string CandidateArm = "b1024-p4";

    private sealed record Shape(string Name, int BatchKiB, int Parallelism);
    private static readonly Shape[] Settings =
    [
        new(OriginalArm, 256, 2),
        new(CandidateArm, 1024, 4)
    ];

    public static IReadOnlyList<DrainOverlapCase> Cases(VerificationOptions options)
    {
        if (options.Suite != "drain-overlap-decision") return [];
        var cases = new List<DrainOverlapCase>();
        for (var repeat = 1; repeat <= options.Repeats; repeat++)
        {
            var workloads = repeat % 2 == 1
                ? new[] { "random-write", "cold-read" }
                : new[] { "cold-read", "random-write" };
            var settings = repeat % 2 == 1 ? Settings : Settings.Reverse().ToArray();
            foreach (var workload in workloads)
                foreach (var setting in settings)
                {
                    var position = cases.Count % 4 + 1;
                    var id = $"drain-overlap-r{repeat:D2}-p{position:D2}-{workload}-{setting.Name}";
                    cases.Add(new(id, repeat, position, workload, setting.BatchKiB, setting.Parallelism));
                }
        }
        return cases;
    }

    public static void ValidateOptions(VerificationOptions options)
    {
        if (options.Suite != "drain-overlap-decision" || options.Repeats != RequiredRepeats ||
            options.BudgetMiB != RequiredBudgetMiB || options.CaseFilter is not null ||
            options.DiskSpd is null || options.SoakSeconds is not null)
            throw new ArgumentException("drain-overlap-decision requires --budget-mib 2048, exactly three repetitions, --diskspd, and no case filter or soak.");
        if (Cases(options).Count != RequiredRepeats * 4)
            throw new InvalidDataException("Drain-overlap plan did not create the complete 12-case matrix.");
    }

    public static int SeedBytes(int budgetMiB)
    {
        if (budgetMiB != RequiredBudgetMiB)
            throw new ArgumentOutOfRangeException(nameof(budgetMiB), "Drain-overlap uses the fixed 2 GiB comparison budget.");
        return checked(budgetMiB * SeedPercentOfBudget / 100 * (1 << 20));
    }

    public static void ValidateCase(DrainOverlapCase test)
    {
        if (string.IsNullOrWhiteSpace(test.Id) || test.Repeat is < 1 or > RequiredRepeats ||
            test.Position is < 1 or > 4 ||
            !Cases(new VerificationOptions("Q:", "drain-overlap-decision", BudgetMiB: RequiredBudgetMiB,
                Repeats: RequiredRepeats)).Contains(test))
            throw new InvalidDataException("Drain-overlap case is outside the declared plan-137 workload bounds.");
    }

    public static DrainOverlapMetrics ValidateEvidence(DrainOverlapCase test, DrainSeedResult seed,
        DateTimeOffset seedCompletedUtc, DateTimeOffset drainStartedUtc, ProcessIdentity process,
        DateTimeOffset processFinishedUtc, DateTimeOffset backgroundCompletedUtc,
        CacheLayoutSnapshot idleCompletion,
        DiskSpdScore score, IReadOnlyList<TelemetrySnapshot> samples,
        ulong expectedInstance, ulong expectedErrors)
    {
        ValidateCase(test);
        bool MatchesPolicy(WriteCacheState state) => state.Operational && state.Enabled && state.UnsafeDefer &&
            state.SupportsReadWrite &&
            state.Options is { Drain: DrainAlgorithm.Idle, WritePercent: 100, RetainWrites: true,
                PromoteOnRead: true, LowPercent: 40, HighPercent: 80, MaxDirtyAgeMs: 5000,
                IdleMs: 250, Allocation: CacheAllocation.Automatic } policy &&
            policy.BatchKiB == test.BatchKiB && policy.Parallelism == test.Parallelism;
        if (seed.Bytes != SeedBytes(RequiredBudgetMiB) || seed.OwnedBytes < (ulong)seed.Bytes ||
            seed.After.Instance != expectedInstance || seed.After.Errors != expectedErrors ||
            seed.Before.Instance != expectedInstance || seed.Before.Errors != expectedErrors ||
            !MatchesPolicy(seed.Before) || !MatchesPolicy(seed.After) || seed.After.DirtyBytes != seed.OwnedBytes ||
            seed.After.AcceptedBytes < seed.Before.AcceptedBytes ||
            seed.After.AcceptedBytes - seed.Before.AcceptedBytes != seed.OwnedBytes || seed.After.InFlightBytes != 0 ||
            seed.PerformanceAfter.TimingEnabled != 0 ||
            seed.AttributionAfter.LowerReadAttempts != seed.AttributionBefore.LowerReadAttempts ||
            seed.AttributionAfter.LowerWriteAttempts != seed.AttributionBefore.LowerWriteAttempts ||
            seed.AttributionAfter.LowerFlushAttempts != seed.AttributionBefore.LowerFlushAttempts)
            throw new InvalidDataException("The drain-overlap dirty seed was not fully admitted without lower writes or flushes.");

        var processStart = new DateTimeOffset(DateTime.SpecifyKind(process.StartedUtc, DateTimeKind.Utc));
        if (samples.Count < 2 || process.Pid <= 0 || samples[0].Utc > seedCompletedUtc ||
            processFinishedUtc <= processStart || drainStartedUtc >= processStart ||
            backgroundCompletedUtc < processFinishedUtc || score.Operations <= 0 || score.Seconds <= 0 ||
            (test.Workload == "random-write" ? score.WriteP99Milliseconds : score.ReadP99Milliseconds) is null)
            throw new InvalidDataException("Drain-overlap process and drain-start timestamps are invalid.");
        TelemetryCoverage.Validate(samples.Select(sample => sample.Utc).ToArray(), processStart, processFinishedUtc);

        var firstDrain = samples.SingleOrDefault(sample => sample.Utc == drainStartedUtc)
            ?? throw new InvalidDataException("The recorded first-drain sample is missing from telemetry.");
        if (firstDrain.State.DrainedBytes <= seed.After.DrainedBytes ||
            firstDrain.State.DirtyBytes + firstDrain.State.InFlightBytes == 0 ||
            firstDrain.State.AcceptedBytes < seed.After.AcceptedBytes || drainStartedUtc < seedCompletedUtc)
            throw new InvalidDataException("Background drain was not observed progressing with dirty data before foreground launch.");
        var backgroundCompletion = samples.SingleOrDefault(sample => sample.Utc == backgroundCompletedUtc)
            ?? throw new InvalidDataException("The recorded background completion sample is missing from telemetry.");
        if (backgroundCompletedUtc <= drainStartedUtc || backgroundCompletion.State.DirtyBytes != 0 ||
            backgroundCompletion.State.InFlightBytes != 0 ||
            backgroundCompletion.State.DrainedBytes < seed.After.DrainedBytes ||
            backgroundCompletion.State.DrainedBytes - seed.After.DrainedBytes < seed.OwnedBytes)
            throw new InvalidDataException("Default Idle draining did not finish the full dirty backlog without an explicit flush.");

        ValidateIdleCompletion(test, seed, idleCompletion, expectedInstance, expectedErrors);

        foreach (var sample in samples)
        {
            if (sample.State.Instance != expectedInstance || sample.State.Errors != expectedErrors ||
                sample.State.LastError != 0 || sample.Performance.TimingEnabled != 0 ||
                sample.Performance.Frequency == 0 || !MatchesPolicy(sample.State))
                throw new InvalidDataException("Drain-overlap telemetry reports a cache identity/health change, timing enabled, or a counter reset.");
        }
        if (samples.Where(sample => sample.Utc >= seedCompletedUtc)
            .Any(sample => sample.State.AcceptedBytes < seed.After.AcceptedBytes))
            throw new InvalidDataException("Accepted-byte telemetry decreased below the completed dirty seed.");

        ulong Delta(ulong current, ulong previous, string name) => current >= previous
            ? current - previous : throw new InvalidDataException("Drain-overlap " + name + " counter decreased.");

        var intervals = 0;
        double maximumSharedIntervalMilliseconds = 0;
        ulong foregroundBytes = 0, drainedBytes = 0, lowerAttempts = 0, drainBatches = 0, driverDrainBytes = 0;
        for (var index = 1; index < samples.Count; index++)
        {
            var before = samples[index - 1];
            var after = samples[index];
            var elapsedIntervalSeconds = after.ElapsedSeconds - before.ElapsedSeconds;
            if (!double.IsFinite(elapsedIntervalSeconds) || elapsedIntervalSeconds <= 0)
                throw new InvalidDataException("Drain-overlap monotonic telemetry elapsed time is not strictly increasing.");
            var drain = Delta(after.State.DrainedBytes, before.State.DrainedBytes, "drained-bytes");
            _ = Delta(after.State.AcceptedBytes, before.State.AcceptedBytes, "accepted-bytes");
            _ = Delta(after.State.ReadHitBytes, before.State.ReadHitBytes, "read-hit-bytes");
            _ = Delta(after.State.ReadMissBytes, before.State.ReadMissBytes, "read-miss-bytes");
            _ = Delta(after.State.ThrottleWaits, before.State.ThrottleWaits, "capacity-waits");
            _ = Delta(after.Performance.DrainBatches, before.Performance.DrainBatches, "drain-batches");
            _ = Delta(after.Performance.DrainBytes, before.Performance.DrainBytes, "driver-drain-bytes");
            var beforeAttribution = before.Diagnostics.Attribution
                ?? throw new NotSupportedException("Drain-overlap requires lower-I/O attempt counters.");
            var afterAttribution = after.Diagnostics.Attribution
                ?? throw new NotSupportedException("Drain-overlap requires lower-I/O attempt counters.");
            _ = Delta(afterAttribution.LowerWriteAttempts, beforeAttribution.LowerWriteAttempts, "lower-write-attempts");
            var foreground = test.Workload == "random-write"
                ? Delta(after.State.AcceptedBytes, before.State.AcceptedBytes, "accepted-bytes")
                : checked(Delta(after.State.ReadHitBytes, before.State.ReadHitBytes, "read-hit-bytes") +
                    Delta(after.State.ReadMissBytes, before.State.ReadMissBytes, "read-miss-bytes"));
            if (before.Utc < processStart || after.Utc > processFinishedUtc || drain == 0 || foreground == 0 ||
                before.State.DirtyBytes + before.State.InFlightBytes == 0 ||
                after.State.DirtyBytes + after.State.InFlightBytes == 0)
                continue;
            var intervalMilliseconds = elapsedIntervalSeconds * 1000;
            if (intervalMilliseconds > MaximumSharedIntervalMilliseconds)
                continue;

            intervals++;
            maximumSharedIntervalMilliseconds = Math.Max(maximumSharedIntervalMilliseconds, intervalMilliseconds);
            foregroundBytes = checked(foregroundBytes + foreground);
            drainedBytes = checked(drainedBytes + drain);
            drainBatches = checked(drainBatches + Delta(after.Performance.DrainBatches,
                before.Performance.DrainBatches, "drain-batches"));
            driverDrainBytes = checked(driverDrainBytes + Delta(after.Performance.DrainBytes,
                before.Performance.DrainBytes, "driver-drain-bytes"));
            lowerAttempts = checked(lowerAttempts + Delta(afterAttribution.LowerWriteAttempts,
                beforeAttribution.LowerWriteAttempts, "lower-write-attempts"));
        }

        var minimumBytes = (ulong)MinimumConcurrentMiB << 20;
        if (intervals == 0 || foregroundBytes < minimumBytes || drainedBytes < minimumBytes ||
            lowerAttempts == 0 || drainBatches == 0 || driverDrainBytes < minimumBytes)
            throw new InvalidDataException("The foreground DiskSpd interval did not show at least 1 MiB of verified foreground and background-drain progress in the same telemetry intervals.");

        var processSamples = samples.Where(sample => sample.Utc >= processStart && sample.Utc <= processFinishedUtc).ToArray();
        if (processSamples.Length < 2)
            throw new InvalidDataException("Drain-overlap has fewer than two telemetry samples inside the workload process interval.");
        var backgroundDrainCompletionSeconds = backgroundCompletion.ElapsedSeconds - firstDrain.ElapsedSeconds;
        if (!double.IsFinite(backgroundDrainCompletionSeconds) || backgroundDrainCompletionSeconds <= 0)
            throw new InvalidDataException("Background drain completion duration is invalid on the monotonic telemetry clock.");
        return new(intervals, foregroundBytes, drainedBytes, lowerAttempts, drainBatches, driverDrainBytes,
            Delta(processSamples[^1].State.ThrottleWaits, processSamples[0].State.ThrottleWaits, "capacity-waits"),
            Delta(backgroundCompletion.State.DrainedBytes, seed.After.DrainedBytes, "total-drained-bytes"),
            backgroundCompletion.State.DirtyBytes + backgroundCompletion.State.InFlightBytes,
            maximumSharedIntervalMilliseconds, backgroundDrainCompletionSeconds);
    }

    public static void ValidateIdleCompletion(DrainOverlapCase test, DrainSeedResult seed,
        CacheLayoutSnapshot snapshot, ulong expectedInstance, ulong expectedErrors)
    {
        bool MatchesPolicy(WriteCacheState state) => state.Operational && state.Enabled && state.UnsafeDefer &&
            state.SupportsReadWrite &&
            state.Options is { Drain: DrainAlgorithm.Idle, WritePercent: 100, RetainWrites: true,
                PromoteOnRead: true, LowPercent: 40, HighPercent: 80, MaxDirtyAgeMs: 5000,
                IdleMs: 250, Allocation: CacheAllocation.Automatic } policy &&
            policy.BatchKiB == test.BatchKiB && policy.Parallelism == test.Parallelism;

        var state = snapshot.State;
        var attribution = snapshot.Diagnostics.Attribution;
        if (!MatchesPolicy(state) || state.Instance != expectedInstance || state.Errors != expectedErrors ||
            state.LastError != 0 || state.Faulted || state.Suspended || state.Removed ||
            state.DirtyBytes != 0 || state.InFlightBytes != 0 || state.AcceptedBytes < seed.After.AcceptedBytes ||
            state.DrainedBytes < seed.After.DrainedBytes ||
            state.DrainedBytes - seed.After.DrainedBytes < seed.OwnedBytes ||
            snapshot.Performance.TimingEnabled != 0 || snapshot.Performance.Frequency == 0 || attribution is null)
            throw new InvalidDataException("Idle drain must leave a healthy, clean cache after draining the recorded seed before cache disable and the unbuffered byte oracle.");
    }

    public static DrainOverlapSummary BuildSummary(IReadOnlyList<DrainOverlapEvidence> evidence)
    {
        var expectedCases = Cases(new VerificationOptions("Q:", "drain-overlap-decision",
            BudgetMiB: RequiredBudgetMiB, Repeats: RequiredRepeats));
        var expectedById = expectedCases.ToDictionary(test => test.Id, StringComparer.Ordinal);
        if (evidence.Count != expectedCases.Count || evidence.Select(row => row.Case.Id).Distinct().Count() != evidence.Count ||
            evidence.Select(row => row.DiskSpdSha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1 ||
            evidence.Any(row => !expectedById.TryGetValue(row.Case.Id, out var expected) || row.Case != expected ||
                !row.SeedVerified || row.Seed.Bytes != SeedBytes(RequiredBudgetMiB) ||
                row.Seed.OwnedBytes < (ulong)row.Seed.Bytes || row.TelemetrySamples < 2 ||
                row.WorkloadProcess.Pid <= 0 || row.WorkloadProcessEvidence.Length == 0 ||
                row.TelemetryEvidence.Length == 0 || row.DiskSpdSha256.Length != 64 ||
                row.DiskSpdSha256.Any(character => !Uri.IsHexDigit(character)) ||
                row.Score.Operations <= 0 || row.Score.Seconds <= 0 ||
                (row.Case.Workload == "random-write" ? row.Score.WriteP99Milliseconds : row.Score.ReadP99Milliseconds) is null ||
                row.Metrics.SharedTelemetryIntervals == 0 || row.Metrics.ForegroundBytesDuringDrain == 0 ||
                row.Metrics.DrainedBytesDuringForeground == 0 || row.Metrics.LowerWriteAttemptsDuringOverlap == 0 ||
                row.Metrics.DrainBatchesDuringForeground == 0 || row.Metrics.DriverDrainBytesDuringForeground == 0 ||
                !double.IsFinite(row.Metrics.MaximumSharedIntervalMilliseconds) ||
                row.Metrics.MaximumSharedIntervalMilliseconds <= 0 ||
                row.Metrics.MaximumSharedIntervalMilliseconds > MaximumSharedIntervalMilliseconds ||
                row.Metrics.RemainingDirtyBytes != 0 || row.Metrics.TotalDrainedBytes < row.Seed.OwnedBytes))
            throw new InvalidDataException("Drain-overlap summary requires every unique case in the complete 12-case matrix.");
        foreach (var row in evidence)
            ValidateIdleCompletion(row.Case, row.Seed, row.IdleCompletion,
                row.Seed.After.Instance, row.Seed.After.Errors);
        static double Median(IEnumerable<double> source)
        {
            var sorted = source.Order().ToArray();
            return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
        }
        var rows = evidence.GroupBy(row => new { row.Case.Workload, row.Case.BatchKiB, row.Case.Parallelism })
            .Select(group =>
            {
                var cases = group.ToArray();
                return new DrainOverlapSummaryRow(group.Key.Workload, group.Key.BatchKiB, group.Key.Parallelism,
                    cases.Length, Median(cases.Select(row => row.Score.Iops)), cases.Min(row => row.Score.Iops),
                    cases.Max(row => row.Score.Iops), Median(cases.Select(row => row.Case.Workload == "random-write"
                        ? row.Score.WriteP99Milliseconds ?? throw new InvalidDataException("Missing write p99 for a write workload.")
                        : row.Score.ReadP99Milliseconds ?? throw new InvalidDataException("Missing read p99 for a read workload."))),
                    Median(cases.Select(row => row.Metrics.ForegroundBytesDuringDrain / (double)(1 << 20))),
                    Median(cases.Select(row => row.Metrics.DrainedBytesDuringForeground / (double)(1 << 20))),
                    Median(cases.Select(row => (double)row.Metrics.CapacityWaits)),
                    Median(cases.Select(row => row.Metrics.DriverDrainBytesDuringForeground /
                        (double)row.Metrics.DrainBatchesDuringForeground)),
                    cases.Max(row => row.Metrics.MaximumSharedIntervalMilliseconds),
                    Median(cases.Select(row => row.Metrics.BackgroundDrainCompletionSeconds)),
                    cases.Select(row => row.Case.Id).ToArray());
            })
            .OrderBy(row => row.Workload, StringComparer.Ordinal).ThenBy(row => row.BatchKiB).ToArray();
        return new(VerificationPlan.Version, evidence.Count, rows);
    }

    public static string RenderSummary(DrainOverlapSummary summary)
    {
        var text = new System.Text.StringBuilder($"# Drain-overlap comparison\n\nPlan {summary.PlanVersion}; {summary.CaseCount}/12 cases. Timing diagnostics were OFF. Foreground and drain progress are counted only when both advance within the same telemetry interval, with each credited interval bounded to {MaximumSharedIntervalMilliseconds} ms; the maximum credited interval is reported below. This still does not resolve ordering within an interval. Idle completion is recorded before cache disable and the unbuffered seed-byte oracle, with no explicit Flush in between. Collection only; compare paired repetitions and do not infer a winner from pooled medians.\n\n");
        text.AppendLine("| Workload | Batch × parallelism | Cases | IOPS median (range) | p99 median (ms) | Foreground MiB co-observed during drain | Drained MiB co-observed during foreground | Capacity waits median | Mean drain batch (KiB) | Maximum credited interval (ms) | Idle completion observed (s) |");
        text.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var row in summary.Rows)
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "| {0} | {1} KiB × {2} | {3} | {4:F0} ({5:F0}–{6:F0}) | {7:F3} | {8:F1} | {9:F1} | {10:F1} | {11:F1} | {12:F1} | {13:F1} |",
                row.Workload, row.BatchKiB, row.Parallelism, row.Samples, row.IopsMedian, row.IopsMin,
                row.IopsMax, row.P99MillisecondsMedian, row.ForegroundMiBDuringDrainMedian,
                row.DrainedMiBDuringForegroundMedian, row.CapacityWaitsMedian,
                row.DrainBatchBytesMedian / 1024, row.MaximumSharedIntervalMilliseconds,
                row.BackgroundDrainCompletionSecondsMedian));
        return text.ToString();
    }
}
