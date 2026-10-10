using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Developer.Verification;

public sealed record RamReadReferenceCase(string Id, RamAccess Access, int BlockKiB, bool Random,
    int QueueDepth, int Threads, int Repeat, int RamReadQueueMode = 0);

/// <summary>Read-only reference shapes; aggregate queue depth stays eight for the large multi-thread control.</summary>
public static class RamReadReferencePlan
{
    public const int DiskMiB = 2048, FileMiB = 1024;
    public static IReadOnlyList<RamReadReferenceCase> Cases(int repeats, bool includeQueue = false)
    {
        if (repeats is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(repeats));
        var result = new List<RamReadReferenceCase>();
        var shapes = new[] { (1024, false, 1, 1), (1024, false, 8, 1), (1024, false, 2, 4),
            (4, true, 1, 1), (4, true, 32, 1), (4, true, 8, 4) };
        var variants = includeQueue ? new[] { (RamAccess.Direct, 0), (RamAccess.Direct, 2), (RamAccess.Standard, 0) }
            : new[] { (RamAccess.Direct, 0), (RamAccess.Standard, 0) };
        for (var repeat = 1; repeat <= repeats; repeat++)
        foreach (var (access, mode) in repeat % 2 == 1 ? variants : variants.Reverse())
        foreach (var (block, random, depth, threads) in repeat % 2 == 1 ? shapes : shapes.Reverse())
            result.Add(new($"{result.Count + 1:D4}-r{repeat}-{access}{(mode == 0 ? "" : "-queue" + mode)}-{(random ? "random" : "sequential")}-{block}k-q{depth}-t{threads}",
                access, block, random, depth, threads, repeat, mode));
        return result;
    }
    public static string[] Arguments(RamReadReferenceCase scenario, int seconds) =>
        [ $"-b{scenario.BlockKiB}K", $"-o{scenario.QueueDepth}", $"-t{scenario.Threads}", "-w0", "-f1024M",
          $"-d{seconds}", "-W0", "-Rxml", "-L", "-S", .. scenario.Random ? new[] { "-r4K", "-z7" } : [] ];
}
