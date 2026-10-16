using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using QueueCache.Management;

namespace QueueCache.Developer.Verification;

public sealed record ReadTailCase(string Id, int Repeat, int Position, string Workload,
    ulong CopyFlags, int BlockBytes, int QueueDepth, int OperationCount, ulong Seed, long FileBytes)
{
    public bool Writes => Workload == "foreground-write";
    public bool Resident => Workload is "resident-random" or "resident-sequential";
}

public sealed record ReadTailResult(string CaseId, int CompletedOperations, long Bytes,
    double Seconds, double Iops, double MiBPerSecond, long StopwatchFrequency,
    string AddressSequenceSha256, long[] LatencyTicks,
    double P50Milliseconds, double P99Milliseconds, double P999Milliseconds, double MaxMilliseconds,
    int Over50Milliseconds, int Over100Milliseconds, int Over250Milliseconds, int Over500Milliseconds);

public sealed record ReadTailEvidence(ReadTailCase Case, CacheLayoutSnapshot Before,
    ReadTailResult Result, CacheLayoutSnapshot After);
public sealed record ReadTailCaseMetrics(int CompletedOperations, long Bytes, double Seconds, double Iops,
    double MiBPerSecond, double P50Milliseconds, double P99Milliseconds, double P999Milliseconds,
    double MaxMilliseconds, int Over50Milliseconds, int Over100Milliseconds, int Over250Milliseconds,
    int Over500Milliseconds, ulong LowerReadAttempts, ulong LowerWriteAttempts, ulong LowerFlushAttempts,
    ulong ReadHitBytes, ulong ReadMissBytes, ulong AcceptedBytes, ulong ThrottleWaits);
public sealed record ReadTailCaseSummary(ReadTailCase Case, ReadTailCaseMetrics Metrics);
public sealed record ReadTailPairSummary(int Repeat, string Workload, ReadTailCaseSummary Flags7,
    ReadTailCaseSummary Flags15, double P999DeltaMilliseconds, double MaxDeltaMilliseconds,
    int Over50Delta, int Over100Delta, int Over250Delta, int Over500Delta);
public sealed record ReadTailRunSummary(int PlanVersion, int CaseCount,
    IReadOnlyList<ReadTailCaseSummary> Cases, IReadOnlyList<ReadTailPairSummary> Pairs);

/// <summary>Fixed-operation read-tail screen; separately reports end-to-end latency samples and lower-I/O counters.</summary>
public static class ReadTailDecision
{
    public const int RequiredRepeats = 3;
    public const int OperationsPerCase = 100_000;
    public const ulong OriginalFlags = 7;
    public const ulong CandidateFlags = 15;
    private const ulong SplitMixIncrement = 0x9E3779B97F4A7C15UL;
    private const ulong WriteStride = 0xD6E8FEB86659FD93UL;

    private sealed record Shape(string Name, int BlockBytes, int QueueDepth, long FileBytes, ulong Seed);

    private static readonly Shape[] Shapes =
    [
        new("cold-random", 4096, 32, 4L << 30, 0x4C7E2B19UL),
        new("resident-random", 4096, 32, 1L << 30, 0xB19A6D43UL),
        new("resident-sequential", 1 << 20, 8, 1L << 30, 0x8F2C4175UL),
        new("foreground-write", 4096, 32, 4L << 30, 0x6A3D9E21UL)
    ];

    public static IReadOnlyList<ReadTailCase> Cases(VerificationOptions options)
    {
        if (options.Suite != "read-tail-decision") return [];
        var cases = new List<ReadTailCase>();
        for (var repeat = 1; repeat <= options.Repeats; repeat++)
        {
            var offset = (repeat - 1) % Shapes.Length;
            var flags = repeat % 2 == 1 ? new[] { OriginalFlags, CandidateFlags } : [CandidateFlags, OriginalFlags];
            for (var position = 0; position < Shapes.Length; position++)
            {
                var shape = Shapes[(position + offset) % Shapes.Length];
                var matrixPosition = position + 1;
                foreach (var copyFlags in flags)
                {
                    var id = $"tail-r{repeat:D2}-p{matrixPosition:D2}-{shape.Name}-flags{copyFlags}";
                    cases.Add(new(id, repeat, matrixPosition, shape.Name, copyFlags,
                        shape.BlockBytes, shape.QueueDepth, OperationsPerCase, shape.Seed, shape.FileBytes));
                }
            }
        }
        return cases;
    }

    public static void ValidateCase(ReadTailCase test)
    {
        var shape = Shapes.SingleOrDefault(candidate => candidate.Name == test.Workload);
        if (shape is null || string.IsNullOrWhiteSpace(test.Id) || test.Repeat is < 1 or > RequiredRepeats ||
            test.Position is < 1 or > 4 || test.CopyFlags is not (OriginalFlags or CandidateFlags) ||
            test.BlockBytes != shape.BlockBytes || test.QueueDepth != shape.QueueDepth ||
            test.OperationCount != OperationsPerCase || test.Seed != shape.Seed || test.FileBytes != shape.FileBytes ||
            test.FileBytes % test.BlockBytes != 0 || (test.FileBytes / test.BlockBytes & (test.FileBytes / test.BlockBytes - 1)) != 0)
            throw new InvalidDataException("Read-tail case is outside the declared plan-135 workload bounds.");
        if (test.Writes && test.OperationCount > test.FileBytes / test.BlockBytes)
            throw new InvalidDataException("Read-tail write addresses would overlap; the oracle requires unique fitting writes.");
    }

    public static IReadOnlyList<long> AddressOffsets(ReadTailCase test)
    {
        ValidateCase(test);
        var offsets = new long[test.OperationCount];
        var blockCount = (ulong)(test.FileBytes / test.BlockBytes);
        var mask = blockCount - 1;
        var state = test.Seed;
        for (var i = 0; i < offsets.Length; i++)
        {
            ulong block;
            if (test.Workload == "resident-sequential")
                block = (ulong)i & mask;
            else if (test.Writes)
                block = (test.Seed + (ulong)i * WriteStride) & mask;
            else
            {
                state += SplitMixIncrement;
                var value = state;
                value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
                value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
                value ^= value >> 31;
                block = value & mask;
            }
            offsets[i] = checked((long)block * test.BlockBytes);
        }
        return offsets;
    }

    public static string AddressSequenceSha256(ReadTailCase test)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        foreach (var offset in AddressOffsets(test))
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes, offset);
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static ReadTailResult Summarize(ReadTailCase test, long[] latencyTicks, double seconds,
        string? addressSequenceSha256 = null)
    {
        ValidateCase(test);
        if (latencyTicks.Length != test.OperationCount || latencyTicks.Any(tick => tick <= 0) ||
            !double.IsFinite(seconds) || seconds <= 0)
            throw new InvalidDataException("Read-tail operation count, latency samples or elapsed time is missing or invalid.");
        var ordered = latencyTicks.Order().ToArray();
        double Milliseconds(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        double Percentile(double probability) => Milliseconds(ordered[Math.Max(0, (int)Math.Ceiling(probability * ordered.Length) - 1)]);
        int Above(double threshold) => latencyTicks.Count(tick => Milliseconds(tick) > threshold);
        var bytes = checked((long)test.OperationCount * test.BlockBytes);
        var sequence = addressSequenceSha256 ?? AddressSequenceSha256(test);
        if (sequence.Length != 64 || !sequence.All(Uri.IsHexDigit))
            throw new InvalidDataException("Read-tail address-sequence digest is missing or malformed.");
        return new(test.Id, test.OperationCount, bytes, seconds, test.OperationCount / seconds,
            bytes / seconds / (1 << 20), System.Diagnostics.Stopwatch.Frequency, sequence, latencyTicks,
            Percentile(0.50), Percentile(0.99), Percentile(0.999), Milliseconds(ordered[^1]),
            Above(50), Above(100), Above(250), Above(500));
    }

    public static void ValidateResult(ReadTailCase test, ReadTailResult result)
    {
        ValidateCase(test);
        if (result.CaseId != test.Id || result.StopwatchFrequency != System.Diagnostics.Stopwatch.Frequency ||
            result.CompletedOperations != test.OperationCount || result.Bytes != (long)test.OperationCount * test.BlockBytes ||
            result.LatencyTicks is null || result.AddressSequenceSha256 != AddressSequenceSha256(test))
            throw new InvalidDataException(test.Id + ": read-tail result identity, completion, byte count or address sequence is incomplete.");
        var expected = Summarize(test, result.LatencyTicks, result.Seconds, result.AddressSequenceSha256);
        if (!Same(expected.Iops, result.Iops) || !Same(expected.MiBPerSecond, result.MiBPerSecond) ||
            !Same(expected.P50Milliseconds, result.P50Milliseconds) || !Same(expected.P99Milliseconds, result.P99Milliseconds) ||
            !Same(expected.P999Milliseconds, result.P999Milliseconds) || !Same(expected.MaxMilliseconds, result.MaxMilliseconds) ||
            expected.Over50Milliseconds != result.Over50Milliseconds || expected.Over100Milliseconds != result.Over100Milliseconds ||
            expected.Over250Milliseconds != result.Over250Milliseconds || expected.Over500Milliseconds != result.Over500Milliseconds)
            throw new InvalidDataException(test.Id + ": read-tail summary does not match the recorded per-operation samples.");
    }

    public static void ValidateEvidence(ReadTailEvidence evidence)
    {
        var test = evidence.Case;
        var before = evidence.Before;
        var after = evidence.After;
        var start = before.State;
        var end = after.State;
        ValidateCase(test);
        ValidateResult(test, evidence.Result);
        if (!start.Enabled || !start.Operational || start.BudgetBytes != (2UL << 30) || start.Instance == 0 ||
            start.DirtyBytes != 0 || start.InFlightBytes != 0 || start.Faulted || start.Suspended || start.Removed ||
            start.LastError != 0 || start.Options is null || start.Options.Drain != DrainAlgorithm.Idle ||
            start.Options.BatchKiB != 256 || start.Options.Parallelism != 2 ||
            !start.Options.RetainWrites || !start.Options.PromoteOnRead ||
            before.Diagnostics.CopyFlags != test.CopyFlags || before.Performance.TimingEnabled != 0 ||
            after.Diagnostics.CopyFlags != test.CopyFlags || after.Performance.TimingEnabled != 0 ||
            end.Instance != start.Instance || end.Generation != start.Generation || end.Errors != start.Errors ||
            !end.Enabled || !end.Operational || end.BudgetBytes != start.BudgetBytes ||
            end.Options != start.Options || end.LastError != 0 || end.Faulted || end.Suspended || end.Removed ||
            before.Diagnostics.Attribution is null || after.Diagnostics.Attribution is null)
            throw new InvalidDataException(test.Id + ": read-tail cache identity, flags, timing or health evidence is invalid.");

        var readsBefore = before.Diagnostics.Attribution.LowerReadAttempts;
        var readsAfter = after.Diagnostics.Attribution.LowerReadAttempts;
        var writesBefore = before.Diagnostics.Attribution.LowerWriteAttempts;
        var writesAfter = after.Diagnostics.Attribution.LowerWriteAttempts;
        var flushesBefore = before.Diagnostics.Attribution.LowerFlushAttempts;
        var flushesAfter = after.Diagnostics.Attribution.LowerFlushAttempts;
        if (readsAfter < readsBefore || writesAfter < writesBefore || flushesAfter < flushesBefore)
            throw new InvalidDataException(test.Id + ": lower-I/O attempt counters moved backwards.");
        var throttleWaits = Delta(before.State.ThrottleWaits, after.State.ThrottleWaits, "capacity-throttle waits");

        var hits = Delta(before.State.ReadHitBytes, after.State.ReadHitBytes, "read-hit bytes");
        var misses = Delta(before.State.ReadMissBytes, after.State.ReadMissBytes, "read-miss bytes");
        if (test.Writes)
        {
            var accepted = Delta(before.State.AcceptedBytes, after.State.AcceptedBytes, "accepted bytes");
            if (accepted < (ulong)evidence.Result.Bytes || throttleWaits != 0)
                throw new InvalidDataException(test.Id + ": fitting foreground writes were not all admitted.");
        }
        else if (test.Resident)
        {
            if (misses != 0 || hits < (ulong)evidence.Result.Bytes)
                throw new InvalidDataException(test.Id + ": resident control missed the cache or did not account for its bytes.");
        }
        else if (misses == 0 || readsAfter == readsBefore)
            throw new InvalidDataException(test.Id + ": cold random target did not produce lower read misses.");
    }

    public static ReadTailRunSummary AggregateEvidence(IEnumerable<ReadTailEvidence> evidence)
    {
        var expected = Cases(new VerificationOptions("Q:", "read-tail-decision", BudgetMiB: 2048, Repeats: RequiredRepeats));
        var recorded = evidence.ToArray();
        if (recorded.Length != expected.Count || recorded.Select(item => item.Case.Id).Distinct().Count() != expected.Count)
            throw new InvalidDataException("The read-tail matrix is incomplete or contains duplicate cases; no aggregate was written.");
        var byId = recorded.ToDictionary(item => item.Case.Id, StringComparer.Ordinal);
        var rows = new List<ReadTailCaseSummary>(expected.Count);
        foreach (var test in expected)
        {
            if (!byId.TryGetValue(test.Id, out var item) || item.Case != test)
                throw new InvalidDataException("The read-tail matrix has missing or mismatched immutable case identity: " + test.Id);
            ValidateEvidence(item);
            var first = item.Before;
            var last = item.After;
            var firstIo = first.Diagnostics.Attribution!;
            var lastIo = last.Diagnostics.Attribution!;
            var result = item.Result;
            rows.Add(new(test, new(result.CompletedOperations, result.Bytes, result.Seconds, result.Iops,
                result.MiBPerSecond, result.P50Milliseconds, result.P99Milliseconds, result.P999Milliseconds,
                result.MaxMilliseconds, result.Over50Milliseconds, result.Over100Milliseconds,
                result.Over250Milliseconds, result.Over500Milliseconds,
                Delta(firstIo.LowerReadAttempts, lastIo.LowerReadAttempts, "lower-read attempts"),
                Delta(firstIo.LowerWriteAttempts, lastIo.LowerWriteAttempts, "lower-write attempts"),
                Delta(firstIo.LowerFlushAttempts, lastIo.LowerFlushAttempts, "lower-flush attempts"),
                Delta(first.State.ReadHitBytes, last.State.ReadHitBytes, "read-hit bytes"),
                Delta(first.State.ReadMissBytes, last.State.ReadMissBytes, "read-miss bytes"),
                Delta(first.State.AcceptedBytes, last.State.AcceptedBytes, "accepted bytes"),
                Delta(first.State.ThrottleWaits, last.State.ThrottleWaits, "capacity-throttle waits"))));
        }
        var pairs = rows.GroupBy(row => (row.Case.Repeat, row.Case.Workload)).Select(group =>
        {
            var baseline = group.SingleOrDefault(row => row.Case.CopyFlags == OriginalFlags)
                ?? throw new InvalidDataException("A read-tail pair is missing flags 7.");
            var candidate = group.SingleOrDefault(row => row.Case.CopyFlags == CandidateFlags)
                ?? throw new InvalidDataException("A read-tail pair is missing flags 15.");
            return new ReadTailPairSummary(group.Key.Repeat, group.Key.Workload, baseline, candidate,
                candidate.Metrics.P999Milliseconds - baseline.Metrics.P999Milliseconds,
                candidate.Metrics.MaxMilliseconds - baseline.Metrics.MaxMilliseconds,
                candidate.Metrics.Over50Milliseconds - baseline.Metrics.Over50Milliseconds,
                candidate.Metrics.Over100Milliseconds - baseline.Metrics.Over100Milliseconds,
                candidate.Metrics.Over250Milliseconds - baseline.Metrics.Over250Milliseconds,
                candidate.Metrics.Over500Milliseconds - baseline.Metrics.Over500Milliseconds);
        }).OrderBy(pair => pair.Repeat).ThenBy(pair => pair.Workload, StringComparer.Ordinal).ToArray();
        if (pairs.Length != 12)
            throw new InvalidDataException("The read-tail matrix is missing a paired repetition; no aggregate was written.");
        return new(VerificationPlan.Version, rows.Count, rows, pairs);
    }

    public static string RenderSummary(ReadTailRunSummary summary)
    {
        if (summary.PlanVersion != VerificationPlan.Version || summary.CaseCount != 24 ||
            summary.Cases.Count != summary.CaseCount || summary.Pairs.Count != 12)
            throw new InvalidDataException("Read-tail report does not describe the complete plan-135 matrix.");
        var md = new StringBuilder("# Fixed-operation read-tail collection\n\n" +
            $"Plan {summary.PlanVersion}; {summary.CaseCount}/24 complete cases. Collection only; no latency SLA or performance verdict is applied.\n\n" +
            "Each row reports the full 100,000-operation sample. Threshold columns count requests strictly greater than each limit.\n\n" +
            "| Repeat | Position | Workload | Flags | IOPS | MiB/s | p50 ms | p99 ms | p99.9 ms | max ms | >50/>100/>250/>500 ms | Lower read/write/flush attempts | Hit/miss/accepted MiB | Throttle waits |\n" +
            "|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|\n");
        foreach (var row in summary.Cases)
        {
            var metric = row.Metrics;
            md.Append("| ").Append(row.Case.Repeat).Append(" | ").Append(row.Case.Position).Append(" | ")
                .Append(row.Case.Workload).Append(" | ").Append(row.Case.CopyFlags).Append(" | ")
                .Append(F(metric.Iops)).Append(" | ").Append(F(metric.MiBPerSecond)).Append(" | ")
                .Append(F(metric.P50Milliseconds)).Append(" | ").Append(F(metric.P99Milliseconds)).Append(" | ")
                .Append(F(metric.P999Milliseconds)).Append(" | ").Append(F(metric.MaxMilliseconds)).Append(" | ")
                .Append(metric.Over50Milliseconds).Append('/').Append(metric.Over100Milliseconds).Append('/')
                .Append(metric.Over250Milliseconds).Append('/').Append(metric.Over500Milliseconds).Append(" | ")
                .Append(metric.LowerReadAttempts).Append('/').Append(metric.LowerWriteAttempts).Append('/')
                .Append(metric.LowerFlushAttempts).Append(" | ").Append(MiB(metric.ReadHitBytes)).Append('/')
                .Append(MiB(metric.ReadMissBytes)).Append('/').Append(MiB(metric.AcceptedBytes)).Append(" | ")
                .Append(metric.ThrottleWaits).AppendLine(" |");
        }
        md.Append("\n## Paired tail differences\n\n")
            .Append("Flags 15 minus flags 7; differences are descriptive, not acceptance decisions.\n\n")
            .Append("| Repeat | Workload | p99.9 delta ms | max delta ms | >50 delta | >100 delta | >250 delta | >500 delta |\n")
            .Append("|---:|---|---:|---:|---:|---:|---:|---:|\n");
        foreach (var pair in summary.Pairs)
            md.Append("| ").Append(pair.Repeat).Append(" | ").Append(pair.Workload).Append(" | ")
                .Append(F(pair.P999DeltaMilliseconds)).Append(" | ").Append(F(pair.MaxDeltaMilliseconds)).Append(" | ")
                .Append(pair.Over50Delta).Append(" | ").Append(pair.Over100Delta).Append(" | ")
                .Append(pair.Over250Delta).Append(" | ").Append(pair.Over500Delta).AppendLine(" |");
        return md.ToString();

        static string F(double value) => value.ToString("F3", CultureInfo.InvariantCulture);
        static string MiB(ulong bytes) => (bytes / (double)(1 << 20)).ToString("F1", CultureInfo.InvariantCulture);
    }

    private static ulong Delta(ulong first, ulong last, string name) =>
        last >= first ? last - first : throw new InvalidDataException("Counter moved backwards: " + name);

    private static bool Same(double expected, double actual) =>
        double.IsFinite(actual) && Math.Abs(expected - actual) <= Math.Max(1e-9, Math.Abs(expected) * 1e-9);
}
