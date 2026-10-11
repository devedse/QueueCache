using System.Text.Json;

namespace QueueCache.Developer.Verification;

public sealed record CacheReadReferenceRow(string Id, int QueueDepth, int Threads, bool Timing, double MiBPerSecond, long Reads,
    double CallerPathShare, double CopyOffloadShare, double? LockAcquiresPerRead, double? LockWaitMicrosecondsPerRead,
    double? LockHoldMicrosecondsPerRead, double? QueueWaitMicrosecondsPerRead, double? QueuedRequestsPerRead)
{
    public string Describe() =>
        FormattableString.Invariant($"{Id}: {MiBPerSecond:F0} MiB/s, {Reads} reads; caller path {CallerPathShare:P1}, copy threads {CopyOffloadShare:P1}") +
        (Timing ? FormattableString.Invariant($"; per read: {LockAcquiresPerRead:F2} lock acquisitions, lock wait {LockWaitMicrosecondsPerRead:F2} µs, lock held {LockHoldMicrosecondsPerRead:F2} µs, worker queue {QueueWaitMicrosecondsPerRead:F2} µs ({QueuedRequestsPerRead:F2} queued)") : "");
}

/// <summary>Plan 120 attribution table, built only from this run's before/after snapshots and scores.
/// Timing values exist only for timing-on windows; a missing counter fails the report, never reads as zero.</summary>
public static class CacheReadReference
{
    public static IReadOnlyList<CacheReadReferenceRow> Report(RunStorage storage, IReadOnlyList<PerformanceCase> cases)
    {
        var rows = new List<CacheReadReferenceRow>();
        foreach (var scenario in cases)
        {
            using var before = JsonDocument.Parse(File.ReadAllText(storage.PathFor(scenario.Id + "-before.json")));
            using var after = JsonDocument.Parse(File.ReadAllText(storage.PathFor(scenario.Id + "-after.json")));
            var score = JsonSerializer.Deserialize<DiskSpdScore>(File.ReadAllText(storage.PathFor(scenario.Id + "-reader.score.json")))
                ?? throw new InvalidDataException("Missing cache read reference score.");
            rows.Add(Row(scenario, before.RootElement, after.RootElement, score));
        }
        storage.Write("cache-read-reference.json", new
        {
            Measurement = "Resident sequential 1 MiB reads; attribution only, no speed acceptance verdict",
            Rows = rows
        });
        return rows;
    }

    public static CacheReadReferenceRow Row(PerformanceCase scenario, JsonElement before, JsonElement after, DiskSpdScore score)
    {
        if (score.Operations <= 0 || score.Bytes != checked(score.Operations * (1L << 20)))
            throw new InvalidDataException("Cache read reference has missing completions or a wrong transfer size.");
        static JsonElement Find(JsonElement root, string[] path)
        {
            foreach (var name in path)
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out root) || root.ValueKind == JsonValueKind.Null)
                    throw new InvalidDataException("Cache read reference snapshot is missing " + string.Join('.', path) + ".");
            return root;
        }
        ulong Delta(params string[] path)
        {
            var first = Find(before, path).GetUInt64(); var second = Find(after, path).GetUInt64();
            if (second < first) throw new InvalidDataException("Cache read reference counter decreased: " + string.Join('.', path));
            return second - first;
        }
        var frequency = Find(after, ["Performance", "Frequency"]).GetUInt64();
        if (frequency == 0 || Find(before, ["Performance", "Frequency"]).GetUInt64() != frequency)
            throw new InvalidDataException("Cache read reference timer frequency is missing or changed.");
        foreach (var snapshot in new[] { before, after })
            if ((Find(snapshot, ["Performance", "TimingEnabled"]).GetUInt64() != 0) != scenario.Timing)
                throw new InvalidDataException("Cache read reference timing state does not match the planned window.");
        double reads = score.Operations;
        double PerRead(ulong ticks) => ticks * 1e6 / frequency / reads;
        var timed = scenario.Timing;
        return new(scenario.Id, scenario.QueueDepth, scenario.Threads, timed, score.MiBPerSecond, score.Operations,
            Delta("Diagnostics", "CallerPath", "Reads") / reads, Delta("Diagnostics", "CopyOffloadReads") / reads,
            timed ? Delta("Performance", "LockAcquires") / reads : null,
            timed ? PerRead(Delta("Performance", "LockWaitTicks")) : null,
            timed ? PerRead(Delta("Performance", "LockHoldTicks")) : null,
            timed ? PerRead(Delta("Performance", "QueueWaitTicks")) : null,
            timed ? Delta("Performance", "QueuedRequests") / reads : null);
    }
}
