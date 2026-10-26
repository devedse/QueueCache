namespace QueueCache.Developer.Verification;

public sealed record FlushOrderCostCase(string Id, int Repeat, string Workload, ulong Mode);

/// <summary>Equal work; the arms alternate their order between repetitions; never combines unrelated runs.</summary>
public static class FlushOrderCostPlan
{
    // Plan 196: ordering off and on. The sequential file fits in the cache, so its
    // application time is RAM speed; the other two are bookmark-heavy.
    public const int BudgetMiB = 2048, SequentialMiB = 1536, SmallFiles = 25_600, WriteThroughPairs = 25_600;
    public const int WriteThroughMiB = WriteThroughPairs * 2 * 4096 / (1 << 20);
    private static readonly ulong[][] Orders = [[0, 1], [1, 0], [0, 1]];
    public static IReadOnlyList<FlushOrderCostCase> Cases(int repeats)
    {
        if (repeats is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(repeats));
        return [.. Enumerable.Range(1, repeats).SelectMany(repeat =>
            new[] { "sequential-fit", "flush-heavy-files", "write-through-pairs" }.SelectMany(workload =>
                Orders[repeat - 1].Select(mode =>
                    new FlushOrderCostCase($"flush-order-cost-v196-r{repeat}-{workload}-{(mode == 0 ? "off" : "on")}", repeat, workload, mode))))];
    }
    public static void ValidateChecks(IReadOnlyList<FlushOrderCostCase> expected, IReadOnlyList<QueueCache.Operations.CheckResult> actual)
    {
        if (!actual.Select(c => c.Name).SequenceEqual(expected.Select(c => c.Id)) || actual.Any(c => c.Result != "PASS"))
            throw new InvalidDataException("Ordering cost evidence is incomplete, duplicated or out of plan order.");
    }
}
