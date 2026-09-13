namespace QueueCache.Developer.Verification;

public sealed record PerformanceCase(string Id, string Allocation, string Drain, int DelayMs,
    int QueueDepth, bool Writer, int Repeat, bool ApplicationFlush = false, string Workload = "interference");

/// <summary>Versioned scenarios are data; they never choose filenames themselves.</summary>
public static class VerificationPlan
{
    public const int Version = 1;
    public static readonly string[] Suites = ["quick", "policies", "flush-interference", "performance", "full"];
    public static IReadOnlyList<PerformanceCase> Performance(VerificationOptions options)
    {
        var cases = new List<PerformanceCase>();
        if (options.Suite == "flush-interference")
        {
            for (var repeat = 1; repeat <= options.Repeats; repeat++)
            foreach (var allocation in repeat % 2 == 1 ? new[] { "Automatic", "Fixed" } : ["Fixed", "Automatic"])
            foreach (var flush in new[] { false, true })
                cases.Add(new($"{cases.Count + 1:D4}-r{repeat}-{allocation}-flush{flush}", allocation, "Eager", 25, 128, true, repeat, flush));
            return cases;
        }
        for (var repeat = 1; repeat <= options.Repeats; repeat++)
        // Alternate order between repeats instead of comparing two long fixed-order policy runs.
        foreach (var allocation in repeat % 2 == 1 ? new[] { "Automatic", "Fixed" } : ["Fixed", "Automatic"])
        foreach (var drain in repeat % 2 == 1 ? new[] { "Eager", "Idle" } : ["Idle", "Eager"])
        foreach (var delay in new[] { 0, 25 })
        foreach (var queue in new[] { 8, 32, 128 })
        foreach (var loaded in new[] { false, true })
            cases.Add(new($"{cases.Count + 1:D4}-r{repeat}-{allocation}-{drain}-d{delay}-q{queue}-{(loaded ? "loaded" : "alone")}",
                allocation, drain, delay, queue, loaded, repeat));
        // Independent scaling/cold-read cells complement the hot-reader interference matrix.
        for (var repeat = 1; repeat <= options.Repeats; repeat++)
        foreach (var workload in new[] { "sequential-read", "sequential-write", "random-read", "random-write", "mixed" })
        foreach (var queue in new[] { 1, 32 })
        foreach (var cache in new[] { "Off", "Eager" })
            cases.Add(new($"{cases.Count + 1:D4}-r{repeat}-{workload}-q{queue}-{cache}", "Automatic", cache, 0, queue, false, repeat, false, workload));
        if (options.Suite == "full")
            foreach (var focused in Performance(options with { Suite = "flush-interference" }))
                cases.Add(focused with { Id = $"{cases.Count + 1:D4}-focused-{focused.Id}" });
        return cases;
    }
    public static void Validate(VerificationOptions options)
    {
        if (!Suites.Contains(options.Suite)) throw new ArgumentException("Unknown verification suite.");
        if (options.BudgetMiB is < 256 or > 8192 || options.Repeats is < 1 or > 10 ||
            options.DurationSeconds is < 5 or > 60 || options.DeadlineMinutes is < 1 or > 1440)
            throw new ArgumentException("Budget 256..8192 MiB, repeats 1..10, duration 5..60 seconds, deadline 1..1440 minutes.");
        if (options.Suite is "performance" or "full" or "flush-interference" && (options.DiskSpd is null || !File.Exists(options.DiskSpd)))
            throw new ArgumentException("Performance suites require --diskspd pointing to DiskSpd.exe with XML output support.");
    }
}
