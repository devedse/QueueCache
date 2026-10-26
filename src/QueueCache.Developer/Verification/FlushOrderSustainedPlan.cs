namespace QueueCache.Developer.Verification;

public sealed record FlushOrderSustainedCase(string Id, int Repeat, string Workload, ulong Mode);

/// <summary>Whether ordering's slower write-out matters in sustained use: does the cache fill and
/// slow the application, and how many bookmarks do ordinary tools create. Never combines runs.</summary>
public static class FlushOrderSustainedPlan
{
    // Plan 197, fixed 2 GiB Fast cache. files-5min writes and flushes 4 KiB files nonstop for
    // five minutes (time-bounded, so the arms may write different counts). tar-extract and
    // robocopy-tree run Windows' own tools on a tree of 16,000 files (1,328 MiB) without
    // application flushes.
    public const int BudgetMiB = 2048, FileSeconds = 300, FilesPerDirectory = 1000, MaxFiles = 1_200_000;
    public const int TreeFiles = 16_000;
    public static readonly int[] TreeFileKiB = [4, 16, 64, 256];
    public static long TreeBytes => (long)TreeFiles / TreeFileKiB.Length * TreeFileKiB.Sum() * 1024;
    public static readonly string[] Workloads = ["files-5min", "tar-extract", "robocopy-tree"];
    private static readonly ulong[][] Orders = [[0, 1], [1, 0]];

    public static IReadOnlyList<FlushOrderSustainedCase> Cases(int repeats)
    {
        if (repeats is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(repeats));
        return [.. Enumerable.Range(1, repeats).SelectMany(repeat => Workloads.SelectMany(workload =>
            Orders[repeat - 1].Select(mode => new FlushOrderSustainedCase(
                $"flush-order-sustained-v197-r{repeat}-{workload}-{(mode == 0 ? "off" : "on")}", repeat, workload, mode))))];
    }

    public static void ValidateChecks(IReadOnlyList<FlushOrderSustainedCase> expected, IReadOnlyList<QueueCache.Operations.CheckResult> actual)
    {
        if (!actual.Select(c => c.Name).SequenceEqual(expected.Select(c => c.Id)) || actual.Any(c => c.Result != "PASS"))
            throw new InvalidDataException("Sustained ordering evidence is incomplete, duplicated or out of plan order.");
    }
}
