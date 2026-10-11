using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Developer.Verification;

public enum RamReadRunKind { Reference, ArchivedQueue, Scheduling, Attribution, Coordination }

public sealed record RamReadReferenceCase(string Id, RamAccess Access, int BlockKiB, bool Random,
    int QueueDepth, int Threads, int Repeat, int RamReadQueueMode = 0,
    bool DisableAffinity = false, bool Interleaved = false, bool SchedulingControl = false,
    bool CoordinationEnabled = false);

/// <summary>Read-only reference shapes; aggregate queue depth stays eight for the large multi-thread control.</summary>
public static class RamReadReferencePlan
{
    public const int DiskMiB = 2048, FileMiB = 1024;
    public const string IndependentSequentialWarning = "WARNING: target access pattern will not be sequential, consider -si";
    public static RamReadRunKind KindFor(string suite) => suite switch
    {
        "ram-read-reference" => RamReadRunKind.Reference,
        "ram-read-queue" => RamReadRunKind.ArchivedQueue,
        "ram-read-scheduling" => RamReadRunKind.Scheduling,
        "ram-read-attribution" => RamReadRunKind.Attribution,
        "ram-read-coordination" => RamReadRunKind.Coordination,
        _ => throw new ArgumentException("Not a RAM read suite.", nameof(suite))
    };
    public static IReadOnlyList<RamReadReferenceCase> CasesFor(VerificationOptions options) =>
        CasesFor(KindFor(options.Suite), options.Repeats);
    public static IReadOnlyList<RamReadReferenceCase> CasesFor(RamReadRunKind kind, int repeats) => kind switch
    {
        RamReadRunKind.Reference => Cases(repeats),
        RamReadRunKind.ArchivedQueue => Cases(repeats, true),
        RamReadRunKind.Scheduling => SchedulingCases(repeats),
        RamReadRunKind.Attribution => AttributionCases(repeats),
        RamReadRunKind.Coordination => CoordinationCases(repeats),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    public static IReadOnlyList<RamReadReferenceCase> CoordinationCases(int repeats)
    {
        if (repeats is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(repeats));
        var result = new List<RamReadReferenceCase>();
        var shapes = new[] { (1, 1), (8, 1), (2, 4) };
        for (var repeat = 1; repeat <= repeats; repeat++)
        foreach (var (depth, threads) in repeat % 2 == 1 ? shapes : shapes.Reverse())
        foreach (var mode in new[] { "off-before", "on", "off-after" })
            result.Add(new($"{result.Count + 1:D4}-r{repeat}-coordination-q{depth}-t{threads}-{mode}",
                RamAccess.Direct, 1024, false, depth, threads, repeat, Interleaved: threads == 4,
                SchedulingControl: true, CoordinationEnabled: mode == "on"));
        return result;
    }
    public static IReadOnlyList<RamReadReferenceCase> AttributionCases(int repeats)
    {
        if (repeats is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(repeats));
        var result = new List<RamReadReferenceCase>();
        var shapes = new[] { (1, 1), (8, 1), (2, 4) };
        for (var repeat = 1; repeat <= repeats; repeat++)
        foreach (var (depth, threads) in repeat % 2 == 1 ? shapes : shapes.Reverse())
            result.Add(new($"{result.Count + 1:D4}-r{repeat}-attribution-q{depth}-t{threads}",
                RamAccess.Direct, 1024, false, depth, threads, repeat,
                Interleaved: threads == 4, SchedulingControl: true));
        return result;
    }

    /// <summary>Copy/driver path is unchanged. Isolate affinity and overlapping sequential cursors.</summary>
    public static IReadOnlyList<RamReadReferenceCase> SchedulingCases(int repeats)
    {
        if (repeats is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(repeats));
        var result = new List<RamReadReferenceCase>();
        var shapes = new[] { (1024, false, 1, 1), (1024, false, 8, 1), (1024, false, 2, 4), (4, true, 1, 1) };
        for (var repeat = 1; repeat <= repeats; repeat++)
        foreach (var (block, random, depth, threads) in repeat % 2 == 1 ? shapes : shapes.Reverse())
        {
            var variants = threads == 4 ? new[] { (false, false), (true, false), (false, true), (true, true) } : [(false, false), (true, false)];
            foreach (var (unbound, interleaved) in repeat % 2 == 1 ? variants : variants.Reverse())
                result.Add(new($"{result.Count + 1:D4}-r{repeat}-{(random ? "random" : "sequential")}-{block}k-q{depth}-t{threads}-affinity{!unbound}-interleaved{interleaved}",
                    RamAccess.Direct, block, random, depth, threads, repeat, DisableAffinity: unbound,
                    Interleaved: interleaved, SchedulingControl: true));
        }
        return result;
    }

    public static void ValidateProfile(RamReadReferenceCase scenario, string output, int seconds)
    {
        if (!scenario.SchedulingControl) return;
        var profile = DiskSpdParser.ParseXml(output).Element("Profile") ?? throw new InvalidDataException("Missing DiskSpd profile.");
        var span = profile.Element("TimeSpans")?.Elements("TimeSpan").SingleOrDefault() ?? throw new InvalidDataException("Missing single DiskSpd profile timespan.");
        var target = span.Element("Targets")?.Elements("Target").SingleOrDefault() ?? throw new InvalidDataException("Missing single DiskSpd profile target.");
        void Expect(System.Xml.Linq.XElement element, string name, string expected)
        {
            if (element.Element(name)?.Value != expected) throw new InvalidDataException("Unexpected/missing DiskSpd profile " + name);
        }
        static string Number(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Expect(span, "DisableAffinity", scenario.DisableAffinity ? "true" : "false");
        Expect(span, "Duration", Number(seconds)); Expect(span, "Warmup", "3");
        Expect(target, "BlockSize", Number(scenario.BlockKiB * 1024L));
        Expect(target, "RequestCount", Number(scenario.QueueDepth)); Expect(target, "ThreadsPerFile", Number(scenario.Threads));
        Expect(target, "WriteRatio", "0"); Expect(target, "IOPriority", "3");
        Expect(target, "MaxFileSize", Number(FileMiB * (1L << 20)));
        Expect(target, "DisableOSCache", "true"); Expect(target, "UseLargePages", "false");
        if (!scenario.Random)
        {
            Expect(target, "StrideSize", Number(scenario.Interleaved ? 4L << 20 : scenario.BlockKiB * 1024L));
            Expect(target, "ThreadStride", Number(scenario.Interleaved ? 1L << 20 : 0));
            Expect(target, "InterlockedSequential", "false");
        }
        else Expect(target, "Random", Number(4096));
    }
    public static void ValidateStandardError(RamReadReferenceCase scenario, string error)
    {
        var text = error.Trim();
        // Each thread has its own sequential file cursor. The four-thread control
        // is aggregate throughput, without DiskSpd's globally interlocked stride.
        if (text.Length == 0 || (!scenario.Random && scenario.Threads > 1 && text == IndependentSequentialWarning)) return;
        throw new InvalidDataException("Unexpected RAM reference DiskSpd stderr; inspect the preserved raw output.");
    }
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
          $"-d{seconds}", scenario.SchedulingControl ? "-W3" : "-W0", "-Rxml", "-L", "-S",
          .. scenario.DisableAffinity ? new[] { "-n" } : [],
          .. scenario.Interleaved ? new[] { "-s4M", "-T1M" } : [],
          .. scenario.Random ? new[] { "-r4K", "-z7" } : [] ];
}
