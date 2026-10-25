namespace QueueCache.Developer.Verification;

public sealed record FlushOrderCostCase(string Id, int Repeat, string Workload, ulong Mode);

/// <summary>Equal work, opposite arm order on alternate repetitions; never combines unrelated runs.</summary>
public static class FlushOrderCostPlan
{
    // Plan 194: work sized so the slower arm needs about a minute to reach the disk.
    public const int BudgetMiB = 2048, SequentialMiB = 4096, SequentialPasses = 2, SmallFiles = 25_600, WriteThroughPairs = 25_600;
    public const int WriteThroughMiB = WriteThroughPairs * 2 * 4096 / (1 << 20);
    public static IReadOnlyList<FlushOrderCostCase> Cases(int repeats)
    {
        if (repeats is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(repeats));
        return [.. Enumerable.Range(1, repeats).SelectMany(repeat =>
            new[] { "sequential-write", "flush-heavy-files", "write-through-pairs" }.SelectMany(workload =>
                (repeat % 2 == 1 ? new ulong[] { 0, 2 } : [2UL, 0UL]).Select(mode =>
                    new FlushOrderCostCase($"flush-order-cost-v194-r{repeat}-{workload}-mode{mode}", repeat, workload, mode))))];
    }
    public static void ValidateChecks(IReadOnlyList<FlushOrderCostCase> expected, IReadOnlyList<QueueCache.Operations.CheckResult> actual)
    {
        if (!actual.Select(c => c.Name).SequenceEqual(expected.Select(c => c.Id)) || actual.Any(c => c.Result != "PASS"))
            throw new InvalidDataException("Ordering cost evidence is incomplete, duplicated or out of plan order.");
    }
}
