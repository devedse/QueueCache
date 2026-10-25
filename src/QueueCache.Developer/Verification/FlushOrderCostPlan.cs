namespace QueueCache.Developer.Verification;

public sealed record FlushOrderCostCase(string Id, int Repeat, string Workload, ulong Mode);

/// <summary>Equal work, opposite arm order on alternate repetitions; never combines unrelated runs.</summary>
public static class FlushOrderCostPlan
{
    public const int BudgetMiB = 256, SequentialMiB = 1024, SmallFiles = 256, WriteThroughPairs = 256;
    public static IReadOnlyList<FlushOrderCostCase> Cases(int repeats)
    {
        if (repeats is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(repeats));
        return [.. Enumerable.Range(1, repeats).SelectMany(repeat =>
            new[] { "sequential-write", "flush-heavy-files", "write-through-pairs" }.SelectMany(workload =>
                (repeat % 2 == 1 ? new ulong[] { 1, 2 } : [2UL, 1UL]).Select(mode =>
                    new FlushOrderCostCase($"flush-order-cost-v188-r{repeat}-{workload}-mode{mode}", repeat, workload, mode))))];
    }
    public static void ValidateChecks(IReadOnlyList<FlushOrderCostCase> expected, IReadOnlyList<QueueCache.Operations.CheckResult> actual)
    {
        if (!actual.Select(c => c.Name).SequenceEqual(expected.Select(c => c.Id)) || actual.Any(c => c.Result != "PASS"))
            throw new InvalidDataException("Ordering cost evidence is incomplete, duplicated or out of plan order.");
    }
}
