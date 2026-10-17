using System.Globalization;
using System.Xml.Linq;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

/// <summary>One ordered step of an external-product comparison. Kind "diskspd" is a scored DiskSpd window;
/// the other kinds are the runner's own timed steps (memory snapshot, sequential writer, flush, file tree).
/// File names are relative to the run's comparison folder on the target. Ids are unique per repetition.</summary>
public sealed record ComparisonCase(string Id, string Kind, int Repeat, string File = "", char Access = 'R',
    int BlockKiB = 0, int Queue = 0, int Threads = 0, bool Random = false, int Seconds = 0,
    string Buffering = ComparisonPlan.Unbuffered, int WritePercent = 0, long Bytes = 0, int SettleSeconds = 0,
    DrainGatherPolicy? GatherPolicy = null);

/// <summary>Driver-independent comparisons of QueueCache with other RAM disks and RAM caches
/// (docs/TESTING.md). Generic suites use no QueueCache controls; compare-drain explicitly selects a
/// provider for drain, pending counters and temporary verification bypass. Disk evidence is timestamped.</summary>
public static class ComparisonPlan
{
    public const string Unbuffered = "unbuffered", WriteThrough = "write-through", Buffered = "buffered";
    public const int RamFileMiB = 1024, RamFillMiB = 2048, WarmupSeconds = 2;
    public const long RamMinimumFreeBytes = 3686L << 20; // test file + two file trees + NTFS metadata
    public const int TreeFiles = 40_000, TreeSeed = 120_000_121, DurabilitySetMiB = 256, DurabilitySmallFiles = 10_000;
    /// <summary>Cache workload files, kept between runs in the target's QCBench folder so later runs
    /// overwrite the same disk areas (bounded snapshot growth on the test VM).</summary>
    public static readonly (string Name, long Bytes)[] CacheFiles =
    [
        ("hot-1g.dat", 1L << 30), ("write-1g.dat", 1L << 30), ("flush-512m.dat", 512L << 20),
        ("mixed-4g.dat", 4L << 30), ("big-8g.dat", 8L << 30)
    ];
    public static readonly string[] Suites =
        ["compare-drain", DrainBatching.Suite, "compare-snapshot", "compare-ram", "compare-cache", "compare-cache-cold", "compare-durability-write", "compare-durability-churn",
         "compare-durability-verify"];
    public static bool IsComparison(string suite) => Suites.Contains(suite);
    public static bool UsesDiskSpd(string suite) => suite is "compare-ram" or "compare-cache" or "compare-cache-cold";
    /// <summary>Write phases of a power-cut test: they write the oracle, and the VM is reset once FINISHED.txt is on disk.</summary>
    public static bool IsDurabilityWrite(string suite) => suite is "compare-durability-write" or "compare-durability-churn";

    /// <summary>Where a suite keeps its files on the target: RAM runs use a run-owned folder (removed after a
    /// completed run); cache and durability runs reuse fixed folders.</summary>
    public static string? WorkDirectory(string suite, string root, string runName) => suite switch
    {
        "compare-ram" or "compare-drain" or DrainBatching.Suite => Path.Combine(root, runName),
        "compare-cache" or "compare-cache-cold" => Path.Combine(root, "QCBench"),
        "compare-durability-write" or "compare-durability-verify" => Path.Combine(root, "QCBench-durability"),
        "compare-durability-churn" => Path.Combine(root, "QCBench-churn"),
        _ => null
    };

    public static int TimeoutSeconds(string suite, int repeats) => suite switch
    {
        "compare-drain" or DrainBatching.Suite => 600 + repeats * 2400,
        "compare-ram" => 600 + repeats * 900,
        "compare-cache" => 2400 + repeats * 2400,
        "compare-cache-cold" => 1200,
        "compare-durability-write" or "compare-durability-churn" => 900,
        "compare-durability-verify" => 1800,
        _ => 300
    };

    public static IReadOnlyList<ComparisonCase> CasesFor(string suite, int repeats)
    {
        if (repeats is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(repeats), "Comparison suites take 1..3 repeats.");
        var cases = suite switch
        {
            "compare-drain" => ComparisonDrain.Cases(repeats),
            DrainBatching.Suite => DrainBatching.Cases(repeats),
            "compare-snapshot" => [new("snapshot-memory", "memory", 1)],
            "compare-ram" => Ram(repeats),
            "compare-cache" => Cache(repeats),
            "compare-cache-cold" => CacheCold(),
            "compare-durability-write" => [new("durability-write", "durability-write", 1)],
            "compare-durability-churn" => [new("durability-churn", "durability-churn", 1)],
            "compare-durability-verify" => [new("durability-verify", "durability-verify", 1)],
            _ => throw new ArgumentException("Not a comparison suite: " + suite)
        };
        if (cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != cases.Count)
            throw new InvalidOperationException("Comparison case IDs must be unique.");
        return cases;
    }

    private static ComparisonCase Window(string prefix, int repeat, string file, char access, int blockKiB, int queue, int threads,
        bool random, int seconds, string buffering = Unbuffered, int writePercent = -1)
    {
        var shape = $"{(random ? "rnd" + blockKiB + "k" : "seq" + blockKiB / 1024 + "m")}-q{queue}t{threads}";
        var mode = access switch { 'R' => "read", 'W' => "write", _ => "mixed" };
        var tail = buffering == Unbuffered ? "" : "-" + buffering;
        return new($"{prefix}-r{repeat}-{shape}-{mode}{tail}", "diskspd", repeat, file, access, blockKiB, queue, threads, random, seconds,
            buffering, writePercent >= 0 ? writePercent : access == 'W' ? 100 : 0);
    }

    private static readonly (int BlockKiB, int Queue, int Threads, bool Random)[] RamShapes =
        [(1024, 1, 1, false), (1024, 8, 1, false), (1024, 8, 4, false), (4, 1, 1, true), (4, 32, 1, true), (4, 32, 4, true)];
    private static readonly (int BlockKiB, int Queue, int Threads, bool Random)[] CdmShapes =
        [(1024, 8, 1, false), (1024, 1, 1, false), (4, 32, 1, true), (4, 1, 1, true)];

    private static List<ComparisonCase> Ram(int repeats)
    {
        var cases = new List<ComparisonCase> { new("canary", "canary", 1), new("memory-start", "memory", 1),
            new("prepare", "prepare", 1, "test-1g.dat", Bytes: (long)RamFileMiB << 20) };
        for (var r = 1; r <= repeats; r++)
            foreach (var access in "RW")
                foreach (var s in RamShapes)
                    cases.Add(Window("ram", r, "test-1g.dat", access, s.BlockKiB, s.Queue, s.Threads, s.Random, 10));
        cases.Add(new("memory-before-fill", "memory", 1));
        cases.Add(new("fill", "fill", 1, "fill-2g.dat", 'W', Bytes: (long)RamFillMiB << 20));
        cases.Add(new("memory-filled", "memory", 1));
        cases.Add(new("fill-delete", "delete", 1, "fill-2g.dat"));
        cases.Add(new("memory-after-delete", "memory", 1));
        for (var r = 1; r <= repeats; r++)
            cases.Add(new($"tree-r{r}", "tree", r, $"tree-r{r}"));
        cases.Add(new("integrity", "integrity", 1, "test-1g.dat", Bytes: (long)RamFileMiB << 20));
        cases.Add(new("memory-end", "memory", 1));
        return cases;
    }

    private static List<ComparisonCase> Cache(int repeats)
    {
        var cases = new List<ComparisonCase> { new("canary", "canary", 1), new("memory-start", "memory", 1),
            new("prepare", "prepare-cache", 1) };
        for (var r = 1; r <= repeats; r++)
        {
            // One read pass does not make every product keep a whole file; three recorded passes before the
            // first repetition show how each one warms up and start the hot windows from resident data.
            for (var pass = 1; pass <= (r == 1 ? 3 : 1); pass++)
                cases.Add(new($"hot-r{r}-warm{pass}", "warm", r, "hot-1g.dat"));
            foreach (var s in CdmShapes) cases.Add(Window("hot", r, "hot-1g.dat", 'R', s.BlockKiB, s.Queue, s.Threads, s.Random, 10));
            foreach (var s in CdmShapes) cases.Add(Window("hot", r, "hot-1g.dat", 'W', s.BlockKiB, s.Queue, s.Threads, s.Random, 10));
            cases.Add(new($"hot-r{r}-settle", "settle", r, SettleSeconds: 45));
        }
        for (var r = 1; r <= repeats; r++)
        {
            cases.Add(new($"buffered-r{r}-warm", "warm", r, "hot-1g.dat"));
            foreach (var access in "RW")
                foreach (var s in new[] { CdmShapes[0], CdmShapes[3] })
                    cases.Add(Window("buffered", r, "hot-1g.dat", access, s.BlockKiB, s.Queue, s.Threads, s.Random, 10, Buffered));
            cases.Add(new($"buffered-r{r}-settle", "settle", r, SettleSeconds: 45));
        }
        for (var r = 1; r <= repeats; r++)
        {
            foreach (var s in new[] { CdmShapes[0], CdmShapes[3] })
                cases.Add(Window("through", r, "hot-1g.dat", 'W', s.BlockKiB, s.Queue, s.Threads, s.Random, 10, WriteThrough));
            cases.Add(new($"through-r{r}-settle", "settle", r, SettleSeconds: 45));
        }
        for (var r = 1; r <= Math.Min(repeats, 2); r++)
        {
            cases.Add(Window("big", r, "big-8g.dat", 'R', 4, 32, 1, true, 60));
            cases.Add(Window("big", r, "big-8g.dat", 'R', 1024, 8, 1, false, 60));
        }
        for (var r = 1; r <= repeats; r++)
            cases.Add(new($"absorb-r{r}", "absorb", r, "write-1g.dat", 'W', Bytes: 1L << 30, SettleSeconds: 60));
        for (var r = 1; r <= repeats; r++)
            cases.Add(new($"flush-r{r}", "flush", r, "flush-512m.dat", 'W', Bytes: 512L << 20, SettleSeconds: 45));
        for (var r = 1; r <= Math.Min(repeats, 2); r++)
            cases.Add(new($"sustained-r{r}", "sustained", r, "big-8g.dat", 'W', Bytes: 8L << 30, SettleSeconds: 90));
        cases.Add(Window("mixed", 1, "mixed-4g.dat", 'M', 4, 8, 1, true, 300, Unbuffered, 30));
        cases.Add(new("mixed-r1-settle", "settle", 1, SettleSeconds: 60));
        for (var r = 1; r <= Math.Min(repeats, 2); r++)
            cases.Add(new($"tree-r{r}", "tree", r, $"tree-r{r}", SettleSeconds: 60));
        cases.Add(new("memory-end", "memory", 1));
        return cases;
    }

    /// <summary>Needs a cache the operator emptied (product command or restart); runs once.</summary>
    private static List<ComparisonCase> CacheCold() =>
    [
        new("memory-start", "memory", 1), new("prepare", "prepare-cache", 1, Bytes: 1),
        new("cold-r1-first", "warm", 1, "hot-1g.dat"), new("cold-r1-second", "warm", 1, "hot-1g.dat"),
        Window("cold", 1, "big-8g.dat", 'R', 4, 32, 1, true, 60) with { Id = "cold-r1-rnd4k-q32t1-read-first" },
        Window("cold", 1, "big-8g.dat", 'R', 4, 32, 1, true, 60) with { Id = "cold-r1-rnd4k-q32t1-read-second" },
        new("memory-end", "memory", 1)
    ];

    public static long FileBytes(string suite, string file) => suite == "compare-ram"
        ? (long)RamFileMiB << 20
        : CacheFiles.Single(f => f.Name == file).Bytes;

    public static string[] Arguments(ComparisonCase window, long fileBytes)
    {
        if (window.Kind != "diskspd") throw new ArgumentException("Not a DiskSpd window.");
        return
        [
            $"-b{window.BlockKiB}K", $"-o{window.Queue}", $"-t{window.Threads}", $"-w{window.WritePercent}",
            $"-f{fileBytes >> 20}M", $"-d{window.Seconds}", $"-W{WarmupSeconds}", "-Rxml", "-L",
            window.Buffering switch { Unbuffered => "-Su", WriteThrough => "-Sh", Buffered => "-Sb", _ => throw new ArgumentException("Unknown buffering.") },
            .. window.Access != 'R' ? new[] { "-Z1M" } : [],
            .. window.Random ? new[] { $"-r{window.BlockKiB}K", "-z7" } : window.Threads > 1 ? new[] { "-si" } : []
        ];
    }

    /// <summary>The DiskSpd profile must echo exactly what was asked; a mismatch is a collection failure.</summary>
    public static void ValidateProfile(ComparisonCase window, XElement results, long fileBytes)
    {
        var profile = results.Element("Profile") ?? throw new InvalidDataException("Missing DiskSpd profile.");
        var span = profile.Element("TimeSpans")?.Elements("TimeSpan").SingleOrDefault() ?? throw new InvalidDataException("Missing single DiskSpd profile timespan.");
        var target = span.Element("Targets")?.Elements("Target").SingleOrDefault() ?? throw new InvalidDataException("Missing single DiskSpd profile target.");
        static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
        void Expect(XElement element, string name, string expected)
        {
            if (element.Element(name)?.Value != expected)
                throw new InvalidDataException($"DiskSpd profile {name} is '{element.Element(name)?.Value ?? "missing"}', expected '{expected}'.");
        }
        Expect(span, "Duration", Number(window.Seconds)); Expect(span, "Warmup", Number(WarmupSeconds));
        Expect(target, "BlockSize", Number(window.BlockKiB * 1024L));
        Expect(target, "RequestCount", Number(window.Queue)); Expect(target, "ThreadsPerFile", Number(window.Threads));
        Expect(target, "WriteRatio", Number(window.WritePercent)); Expect(target, "MaxFileSize", Number(fileBytes));
        // DiskSpd writes these two flags only when they are set.
        void Flag(string name, bool expected)
        {
            var value = target.Element(name)?.Value;
            if (expected ? value != "true" : value is not (null or "false"))
                throw new InvalidDataException($"DiskSpd profile {name} is '{value ?? "missing"}', expected {(expected ? "true" : "unset")}.");
        }
        Flag("DisableOSCache", window.Buffering != Buffered);
        Flag("WriteThrough", window.Buffering == WriteThrough);
        if (window.Random) Expect(target, "Random", Number(window.BlockKiB * 1024L));
        else Expect(target, "InterlockedSequential", window.Threads > 1 ? "true" : "false");
    }

    /// <summary>Scored totals with the extra fields a comparison reports. Missing fields are failures, never zero.</summary>
    public static ComparisonScore Score(ComparisonCase window, string output, long fileBytes)
    {
        var root = DiskSpdParser.ParseXml(output);
        ValidateProfile(window, root, fileBytes);
        var span = root.Elements("TimeSpan").SingleOrDefault() ?? throw new InvalidDataException("Expected exactly one measured time span.");
        var score = DiskSpdParser.Parse(output);
        var targets = span.Elements("Thread").SelectMany(t => t.Elements("Target")).ToArray();
        long Sum(string field) => targets.Sum(t => long.Parse(t.Element(field)?.Value ?? throw new InvalidDataException("Missing DiskSpd " + field), CultureInfo.InvariantCulture));
        if (span.Elements("Thread").Count() != window.Threads) throw new InvalidDataException("DiskSpd thread count differs from the window.");
        if (targets.Any(t => long.Parse(t.Element("FileSize")?.Value ?? "-1", CultureInfo.InvariantCulture) < fileBytes))
            throw new InvalidDataException("DiskSpd target is smaller than the window's file.");
        var reads = Sum("ReadCount"); var writes = Sum("WriteCount");
        // A window in which nothing completed is a measurement (a stall: 0 MB/s, latency N/A), not a collection failure.
        if (window.Access == 'R' && writes != 0 || window.Access == 'W' && reads != 0 || window.Access == 'M' && score.Operations > 0 && (reads == 0 || writes == 0))
            throw new InvalidDataException("DiskSpd read/write split differs from the window.");
        if (score.Bytes != score.Operations * window.BlockKiB * 1024L) throw new InvalidDataException("DiskSpd bytes are not whole blocks.");
        double? Percentile(string field, double percentile, long operations)
        {
            if (operations == 0) return null;
            var bucket = span.Element("Latency")?.Elements("Bucket").SingleOrDefault(b =>
                double.TryParse(b.Element("Percentile")?.Value, CultureInfo.InvariantCulture, out var p) && p == percentile)
                ?? throw new InvalidDataException($"Missing DiskSpd {percentile} percentile.");
            return double.Parse(bucket.Element(field)?.Value ?? throw new InvalidDataException("Missing DiskSpd " + field), CultureInfo.InvariantCulture);
        }
        var cpu = span.Element("CpuUtilization")?.Element("Average")?.Element("UsagePercent")?.Value
            ?? throw new InvalidDataException("Missing DiskSpd average CPU usage.");
        return new(window.Id, score.Bytes, score.Operations, score.Seconds, score.Bytes / score.Seconds / 1_000_000, score.Iops,
            Percentile("ReadMilliseconds", 50, reads), Percentile("ReadMilliseconds", 99, reads), Percentile("ReadMilliseconds", 99.9, reads),
            Percentile("WriteMilliseconds", 50, writes), Percentile("WriteMilliseconds", 99, writes), Percentile("WriteMilliseconds", 99.9, writes),
            double.Parse(cpu, CultureInfo.InvariantCulture));
    }

    /// <summary>Shape name shared by a window's repetitions ("ram-seq1m-q8t1-read").</summary>
    public static string Shape(string id)
    {
        var parts = id.Split('-').ToList();
        var index = parts.FindIndex(p => p.Length > 1 && p[0] == 'r' && p[1..].All(char.IsAsciiDigit));
        if (index < 0) return id;
        parts.RemoveAt(index);
        return string.Join('-', parts);
    }

    public static (double Median, double Min, double Max) Spread(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0) throw new ArgumentException("No values.");
        var sorted = values.Order().ToArray();
        return ((sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2, sorted[0], sorted[^1]);
    }

    /// <summary>Validates every planned step's check and returns the medians per shape for the report.</summary>
    public static void ValidateChecks(IReadOnlyList<ComparisonCase> plan, IReadOnlyList<CheckResult> checks)
    {
        if (checks.Count != plan.Count || !plan.Select(c => c.Id).SequenceEqual(checks.Select(c => c.Name), StringComparer.Ordinal))
            throw new InvalidDataException("Comparison checks do not match the planned steps one to one, in order.");
        if (checks.Any(c => c.Result != "PASS")) throw new InvalidDataException("A comparison step did not pass.");
    }
}

public sealed record ComparisonScore(string Id, long Bytes, long Operations, double Seconds, double MBPerSecond, double Iops,
    double? ReadP50Ms, double? ReadP99Ms, double? ReadP999Ms, double? WriteP50Ms, double? WriteP99Ms, double? WriteP999Ms,
    double CpuPercent);
