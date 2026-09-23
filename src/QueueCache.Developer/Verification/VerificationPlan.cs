namespace QueueCache.Developer.Verification;

public sealed record IntegrityCase(string Id, string Operation, bool? CacheEnabled = null);

public sealed record PerformanceCase(
    string Id,
    string Allocation,
    string Drain,
    int DelayMs,
    int QueueDepth,
    bool Writer,
    int Repeat,
    bool ApplicationFlush = false,
    string Workload = "interference",
    bool Resident = false,
    bool Timing = true);

public sealed record DrainDecisionCase(
    string Id,
    string Workload,
    int Parallelism,
    int Repeat,
    bool PendingDrain);

/// <summary>Versioned scenarios are data; they never choose filenames themselves.</summary>
public static class VerificationPlan
{
    public const int Version = 22;
    public const string DiskSpdDownload = "https://github.com/microsoft/diskspd/releases";

    public static readonly string[] Suites =
    [
        "quick",
        "system-preflight",
        "system-files",
        "system-post-restart",
        "system-image-baseline",
        "system-active-image",
        "policies",
        "pressure",
        "drain-decision",
        "trim-diagnostic",
        "trim-file",
        "write-performance",
        "flush-interference",
        "performance",
        "full"
    ];

    public static IReadOnlyList<DrainDecisionCase> DrainDecision(VerificationOptions options)
    {
        if (options.Suite != "drain-decision")
            return [];
        var cases = new List<DrainDecisionCase>();
        for (var repeat = 1; repeat <= options.Repeats; repeat++)
        {
            var workloads = repeat % 2 == 1
                ? new[] { "fitting-write", "cold-read" }
                : ["cold-read", "fitting-write"];
            foreach (var workload in workloads)
            {
                cases.Add(new($"{cases.Count + 1:D4}-r{repeat}-{workload}-control", workload, 1, repeat, false));
                var parallelism = repeat % 2 == 1 ? new[] { 1, 2, 4 } : [4, 2, 1];
                foreach (var workers in parallelism)
                    cases.Add(new($"{cases.Count + 1:D4}-r{repeat}-{workload}-p{workers}", workload, workers, repeat, true));
            }
        }
        return options.CaseFilter is null ? cases :
            cases.Where(test => test.Id.Contains(options.CaseFilter, StringComparison.Ordinal)).ToList();
    }

    public static IReadOnlyList<IntegrityCase> Integrity(VerificationOptions options) => options.Suite switch
    {
        "quick" => [new("file-integrity", "files")],
        "system-preflight" => [new("system-preflight", "system-preflight")],
        "system-files" => [new("system-file-create", "system-file-create")],
        "system-post-restart" => [new("system-file-verify", "system-file-verify")],
        "system-image-baseline" => [new("system-image-baseline", "system-image-baseline")],
        "system-active-image" => [new("system-active-image", "system-active-image")],
        "policies" => [new("policy-integrity", "policies")],
        "pressure" => [new("pressure-integrity", "pressure")],
        "full" => [new("file-integrity", "files"), new("policy-integrity", "policies")],
        "trim-diagnostic" => [new("trim-cache-enabled", "files", true), new("trim-cache-disabled", "files", false)],
        "trim-file" => [new("trim-file", "trim-file")],
        _ => []
    };

    /// <summary>
    /// Expands the selected suite into a stable, ordered list of immutable cases.
    /// The order is part of the recorded plan: console test numbers and raw-evidence
    /// filenames are derived from these IDs.
    /// </summary>
    public static IReadOnlyList<PerformanceCase> Performance(VerificationOptions options)
    {
        var cases = new List<PerformanceCase>();

        if (options.Suite == "write-performance")
        {
            for (var repeat = 1; repeat <= options.Repeats; repeat++)
            foreach (var (workload, depth) in new[] { ("random-write", 1), ("random-write", 32), ("sequential-write", 1), ("sequential-write", 8) })
            foreach (var drain in repeat % 2 == 1 ? new[] { "Off", "Eager", "Idle" } : new[] { "Idle", "Eager", "Off" })
            foreach (var timing in repeat % 2 == 1 ? new[] { false, true } : new[] { true, false })
                cases.Add(new PerformanceCase($"{NextNumber(cases)}-r{repeat}-{workload}-q{depth}-{drain}-timing{timing}",
                    "Automatic", drain, 0, depth, false, repeat, Workload: workload, Resident: true, Timing: timing));
            return options.CaseFilter is null ? cases : cases.Where(test => test.Id.Contains(options.CaseFilter, StringComparison.Ordinal)).ToList();
        }

        if (options.Suite == "flush-interference")
        {
            AddFlushInterferenceCases(cases, options.Repeats);
            return cases;
        }

        AddInterferenceCases(cases, options.Repeats);
        AddScalingCases(cases, options.Repeats);

        if (options.Suite == "full")
        {
            AddFocusedCases(cases, options);
        }

        return cases;
    }

    private static void AddInterferenceCases(List<PerformanceCase> cases, int repeats)
    {
        for (var repeat = 1; repeat <= repeats; repeat++)
        {
            // Alternate order between repeats instead of comparing two long,
            // fixed-order policy runs.
            var allocations = repeat % 2 == 1
                ? new[] { "Automatic", "Fixed" }
                : ["Fixed", "Automatic"];

            var drainPolicies = repeat % 2 == 1
                ? new[] { "Eager", "Idle" }
                : ["Idle", "Eager"];

            foreach (var allocation in allocations)
            {
                foreach (var drain in drainPolicies)
                {
                    foreach (var delayMs in new[] { 0, 25 })
                    {
                        foreach (var queueDepth in new[] { 8, 32, 128 })
                        {
                            foreach (var writer in new[] { false, true })
                            {
                                // "loaded" means the foreground reader runs alongside an
                                // independent writer; "alone" is the matching control case.
                                var id = $"{NextNumber(cases)}-r{repeat}-{allocation}-{drain}" +
                                    $"-d{delayMs}-q{queueDepth}-{(writer ? "loaded" : "alone")}";

                                cases.Add(new PerformanceCase(
                                    id,
                                    allocation,
                                    drain,
                                    delayMs,
                                    queueDepth,
                                    writer,
                                    repeat));
                            }
                        }
                    }
                }
            }
        }
    }

    private static void AddScalingCases(List<PerformanceCase> cases, int repeats)
    {
        string[] workloads =
        [
            "sequential-read",
            "sequential-write",
            "random-read",
            "random-write",
            "mixed"
        ];

        // Independent scaling and cold-read cells complement the hot-reader
        // interference matrix.
        for (var repeat = 1; repeat <= repeats; repeat++)
        {
            foreach (var workload in workloads)
            {
                foreach (var queueDepth in new[] { 1, 32 })
                {
                    foreach (var cacheMode in new[] { "Off", "Eager" })
                    {
                        var id = $"{NextNumber(cases)}-r{repeat}-{workload}" +
                            $"-q{queueDepth}-{cacheMode}";

                        cases.Add(new PerformanceCase(
                            id,
                            "Automatic",
                            cacheMode,
                            DelayMs: 0,
                            queueDepth,
                            Writer: false,
                            repeat,
                            ApplicationFlush: false,
                            workload));
                    }
                }
            }
        }
    }

    private static void AddFlushInterferenceCases(List<PerformanceCase> cases, int repeats)
    {
        for (var repeat = 1; repeat <= repeats; repeat++)
        {
            var allocations = repeat % 2 == 1
                ? new[] { "Automatic", "Fixed" }
                : ["Fixed", "Automatic"];

            foreach (var allocation in allocations)
            {
                foreach (var applicationFlush in new[] { false, true })
                {
                    var id = $"{NextNumber(cases)}-r{repeat}-{allocation}" +
                        $"-flush{applicationFlush}";

                    cases.Add(new PerformanceCase(
                        id,
                        allocation,
                        "Eager",
                        DelayMs: 25,
                        QueueDepth: 128,
                        Writer: true,
                        repeat,
                        applicationFlush));
                }
            }
        }
    }

    private static void AddFocusedCases(
        List<PerformanceCase> cases,
        VerificationOptions options)
    {
        var focusedCases = Performance(options with
        {
            Suite = "flush-interference"
        });

        foreach (var focusedCase in focusedCases)
        {
            cases.Add(focusedCase with
            {
                Id = $"{NextNumber(cases)}-focused-{focusedCase.Id}"
            });
        }
    }

    private static string NextNumber(IReadOnlyCollection<PerformanceCase> cases) =>
        $"{cases.Count + 1:D4}";

    /// <summary>
    /// Rejects an unknown suite, unsafe resource/time bounds, and unusable DiskSpd
    /// configuration before the runner captures state or opens the workload disk.
    /// Quick correctness suites do not need DiskSpd, so its path is checked only for
    /// suites that contain performance measurements.
    /// </summary>
    public static void Validate(VerificationOptions options)
    {
        if (!Suites.Contains(options.Suite))
        {
            throw new ArgumentException("Unknown verification suite.");
        }
        SystemPreflightGuard.ValidateOptions(options);
        if (options.CaseFilter is not null &&
            (options.Suite is not ("write-performance" or "drain-decision") || string.IsNullOrWhiteSpace(options.CaseFilter)))
            throw new ArgumentException("--case-filter requires write-performance or drain-decision and a nonempty case-sensitive ID substring.");

        // These are runner safety limits, not driver limits. They bound VM RAM use,
        // repeated work, individual sample duration, and unattended run duration.
        if (options.BudgetMiB is < 256 or > 8192 ||
            options.Repeats is < 1 or > 10 ||
            options.DurationSeconds is < 5 or > 60 ||
            options.DeadlineMinutes is < 0 or > 1440 || options.PreparationFlushSeconds is < 180 or > 3600)
        {
            throw new ArgumentException(
                "Budget 256..8192 MiB, repeats 1..10, duration 5..60 seconds, " +
                "deadline 0 (unlimited) or 1..1440 minutes.");
        }

        if (options.CaseFilter is not null &&
            (options.Suite == "write-performance" ? Performance(options).Count : DrainDecision(options).Count) == 0)
            throw new ArgumentException("--case-filter matched no cases; no tests started.");

        if (options.Suite == "drain-decision" && options.BudgetMiB > 4096)
            throw new ArgumentException("drain-decision requires --budget-mib 256..4096 so its deterministic 25% dirty set remains bounded.");

        if (options.Suite is not ("performance" or "full" or "flush-interference" or "write-performance" or "drain-decision"))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.DiskSpd))
        {
            throw new ArgumentException(
                $"Suite '{options.Suite}' requires --diskspd <path-to-exe>.\n" +
                $"Use Microsoft DiskSpd ({DiskSpdDownload}, extracted amd64\\diskspd.exe) " +
                "or CrystalDiskMark's CdmResource\\DiskSpd\\DiskSpd64.exe on x64 Windows.\n" +
                $"Example: qcache developer verify Q: --suite {options.Suite} " +
                "--diskspd \"C:\\Tools\\DiskSpd\\amd64\\diskspd.exe\"");
        }

        var path = Path.GetFullPath(options.DiskSpd);

        if (Directory.Exists(path))
        {
            throw new ArgumentException(
                $"--diskspd points to a directory, not an executable: {path}\n" +
                "Select Microsoft's amd64\\diskspd.exe or CrystalDiskMark's " +
                "CdmResource\\DiskSpd\\DiskSpd64.exe on x64 Windows.");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"DiskSpd executable was not found or is not accessible: {path}\n" +
                "Check the exact filename and quote paths containing spaces.\n" +
                $"Supported: Microsoft's amd64\\diskspd.exe ({DiskSpdDownload}) or " +
                "CrystalDiskMark's CdmResource\\DiskSpd\\DiskSpd64.exe.",
                path);
        }
    }
}
