using System.Text.Json.Serialization;
using QueueCache.Management;

namespace QueueCache.Developer.Verification;

[JsonConverter(typeof(JsonStringEnumConverter<DrainGatherPolicy>))]
public enum DrainGatherPolicy { Current, Aligned }

/// <summary>Same-build batch selection experiment. Reuses compare-drain's exact bytes and endpoint.</summary>
public static class DrainBatching
{
    public const string Suite = "compare-drain-batching";
    public static IReadOnlyList<ComparisonCase> Cases(int repeats)
    {
        if (repeats != 3) throw new ArgumentException("Batching requires exactly three paired repetitions.");
        var result = new List<ComparisonCase>();
        for (var r = 1; r <= repeats; ++r)
        {
            var order = r % 2 == 1 ? new[] { DrainGatherPolicy.Current, DrainGatherPolicy.Aligned }
                : [DrainGatherPolicy.Aligned, DrainGatherPolicy.Current];
            foreach (var small in new[] { false, true })
                foreach (var policy in small ? order.Reverse() : order)
                {
                    var shape = small ? "small" : "large";
                    var id = $"gather-{policy.ToString().ToLowerInvariant()}-{shape}-r{r}";
                    result.Add(new(id, "drain-" + shape, r, id, 'W', small ? 4 : 1024, small ? 1 : 4,
                        small ? 4 : 1, Bytes: ComparisonDrain.ExpectedBytes("drain-" + shape), GatherPolicy: policy));
                }
        }
        return result;
    }

    public static void ValidateProfile(WriteCacheState state, DrainGatherPolicy policy)
    {
        ComparisonDrain.ValidateQueueCache(state, active: true);
        if (state.Options?.BatchKiB != 2048 || state.Options.Parallelism != 4 ||
            state.Performance?.DrainGatherPolicy != (ulong)policy)
            throw new InvalidDataException("Batching requires 2048 KiB x 4 and observed V5 gather-policy evidence.");
    }

    public sealed record Evidence(string Id, DrainGatherPolicy Policy, ulong Batches, ulong Bytes,
        IReadOnlyDictionary<string, ulong> ForwardStops, IReadOnlyDictionary<string, ulong> BackwardStops,
        IReadOnlyDictionary<string, ulong> Gaps, IReadOnlyDictionary<string, ulong> Sizes);

    public static Evidence Measure(ComparisonCase test, CachePerformance before, CachePerformance after)
    {
        var policy = test.GatherPolicy ?? throw new InvalidDataException("Missing gather policy.");
        if (before.TimingEnabled != 0 || after.TimingEnabled != 0 ||
            before.DrainGatherPolicy != (ulong)policy || after.DrainGatherPolicy != (ulong)policy)
            throw new InvalidDataException("Timing or gather policy changed during measurement.");
        ulong Delta(Func<CachePerformance, ulong?> field)
        {
            var a = field(before); var b = field(after);
            if (a is null || b is null || b < a) throw new InvalidDataException("Missing or regressing batch counter.");
            return b.Value - a.Value;
        }
        var batches = Delta(p => p.DrainBatches); var bytes = Delta(p => p.DrainBytes);
        var forward = new Dictionary<string, ulong> {
            ["limit"] = Delta(p => p.DrainEndLimit), ["gap"] = Delta(p => p.DrainEndGap),
            ["in-flight"] = Delta(p => p.DrainEndInFlight), ["filling"] = Delta(p => p.DrainEndFilling),
            ["partial"] = Delta(p => p.DrainEndPartial), ["fence"] = Delta(p => p.DrainEndFence),
            ["partial-anchor"] = Delta(p => p.DrainPartialAnchor) };
        var backward = new Dictionary<string, ulong> {
            ["limit"] = Delta(p => p.DrainBackLimit), ["gap"] = Delta(p => p.DrainBackGap),
            ["in-flight"] = Delta(p => p.DrainBackInFlight), ["filling"] = Delta(p => p.DrainBackFilling),
            ["partial"] = Delta(p => p.DrainBackPartial), ["fence"] = Delta(p => p.DrainBackFence),
            ["disk-boundary"] = Delta(p => p.DrainBackBoundary), ["aligned-boundary"] = Delta(p => p.DrainBackAligned) };
        var gaps = new Dictionary<string, ulong> {
            ["clean-full"] = Delta(p => p.DrainGapCleanFull), ["clean-partial"] = Delta(p => p.DrainGapCleanPartial),
            ["absent"] = Delta(p => p.DrainGapAbsent), ["filling"] = Delta(p => p.DrainGapFilling) };
        var sizes = new Dictionary<string, ulong> {
            ["at-most-64KiB"] = Delta(p => p.DrainSizeSmall), ["at-most-1MiB"] = Delta(p => p.DrainSizeMedium),
            ["above-1MiB"] = Delta(p => p.DrainSizeLarge) };
        static ulong Sum(IReadOnlyDictionary<string, ulong> values) => values.Values.Aggregate(0UL, (a, b) => checked(a + b));
        if (batches == 0 || bytes == 0 || Sum(forward) != batches || Sum(backward) != batches ||
            Sum(sizes) != batches || Sum(gaps) != forward["gap"] || policy == DrainGatherPolicy.Current && backward["aligned-boundary"] != 0)
            throw new InvalidDataException("Batch reasons, gaps and sizes do not reconcile with submitted batches.");
        return new(test.Id, policy, batches, bytes, forward, backward, gaps, sizes);
    }
}
