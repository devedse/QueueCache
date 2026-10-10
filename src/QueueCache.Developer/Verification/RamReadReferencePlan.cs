using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Developer.Verification;

public sealed record RamReadReferenceCase(string Id, RamAccess Access, int BlockKiB, bool Random,
    int QueueDepth, int Threads, int Repeat);

/// <summary>Read-only reference shapes; aggregate queue depth stays eight for the large multi-thread control.</summary>
public static class RamReadReferencePlan
{
    public const int DiskMiB = 2048, FileMiB = 1024;
    public static IReadOnlyList<RamReadReferenceCase> Cases(int repeats)
    {
        if (repeats is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(repeats));
        var result = new List<RamReadReferenceCase>();
        var shapes = new[] { (1024, false, 1, 1), (1024, false, 8, 1), (1024, false, 2, 4),
            (4, true, 1, 1), (4, true, 32, 1), (4, true, 8, 4) };
        for (var repeat = 1; repeat <= repeats; repeat++)
        foreach (var access in repeat % 2 == 1 ? new[] { RamAccess.Direct, RamAccess.Standard } : [RamAccess.Standard, RamAccess.Direct])
        foreach (var (block, random, depth, threads) in repeat % 2 == 1 ? shapes : shapes.Reverse())
            result.Add(new($"{result.Count + 1:D4}-r{repeat}-{access}-{(random ? "random" : "sequential")}-{block}k-q{depth}-t{threads}",
                access, block, random, depth, threads, repeat));
        return result;
    }
    public static string[] Arguments(RamReadReferenceCase scenario, int seconds) =>
        [ $"-b{scenario.BlockKiB}K", $"-o{scenario.QueueDepth}", $"-t{scenario.Threads}", "-w0", "-f1024M",
          $"-d{seconds}", "-W0", "-Rxml", "-L", "-S", .. scenario.Random ? new[] { "-r4K", "-z7" } : [] ];
}
