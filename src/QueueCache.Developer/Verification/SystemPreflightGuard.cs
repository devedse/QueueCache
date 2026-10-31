using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

/// <summary>Read-only OS-disk inventory gate. It never enables caching or writes a workload.</summary>
public static class SystemPreflightGuard
{
    public static bool IsSystemDurability(string volume, string suite) =>
        volume.Equals("C:", StringComparison.OrdinalIgnoreCase) &&
        suite is "compare-durability-write" or "compare-durability-churn" or "compare-durability-verify";

    public static bool ChangesSystemCache(string? suite) => suite is SystemPagingOrdering.Suite or SystemOrderingDrain.Suite or SystemOrderingDrain.PressureSuite;

    public static void ValidateOptions(VerificationOptions options)
    {
        var durability = IsSystemDurability(options.Volume, options.Suite);
        if (!durability && options.Suite is not ("system-preflight" or "system-files" or "system-post-restart" or "system-image-baseline" or "system-active-image" or "system-paging-recognition" or "system-app-session" or "system-paging-ordering" or "system-ordering-drain" or "system-paging-ordering-drain"))
        {
            if (options.SystemInstance is not null || options.SystemBytes is not null || options.RecoverableVm ||
                options.OraclePath is not null && options.Suite != "compare-durability-verify")
                throw new ArgumentException("System-disk opt-in arguments are only valid for guarded system suites.");
            return;
        }
        if (!string.Equals(options.Volume, "C:", StringComparison.OrdinalIgnoreCase) ||
            !options.RecoverableVm || string.IsNullOrWhiteSpace(options.SystemInstance) ||
            options.SystemBytes is null or <= 0)
            throw new ArgumentException("System suites require C:, --recoverable-vm, --system-instance and --system-bytes.");
        if (options.DiskSpd is not null || options.CaseFilter is not null)
            throw new ArgumentException("System suites do not accept DiskSpd or case filters.");
        if (durability)
        {
            if (options.Product?.StartsWith("QueueCache", StringComparison.OrdinalIgnoreCase) != true)
                throw new ArgumentException("Guarded C: durability requires a QueueCache product label.");
            if ((options.Suite == "compare-durability-verify") != (options.OraclePath is not null))
                throw new ArgumentException("C: durability verification requires its off-disk oracle.");
            return;
        }
        if (ChangesSystemCache(options.Suite) && options.BudgetMiB != 2048)
            throw new ArgumentException($"{options.Suite} requires --budget-mib 2048.");
        if ((options.Suite == "system-post-restart") != (options.OraclePath is not null))
            throw new ArgumentException("--oracle is required only for system-post-restart.");
        if (options.Suite == "system-active-image" && options.BudgetMiB is < 256 or > 512)
            throw new ArgumentException("system-active-image requires a conservative --budget-mib between 256 and 512.");
        // The saved 349 MiB image must fit the write quota (half the budget by default).
        if (options.Suite == "system-app-session" && options.BudgetMiB is < 1024 or > 2048)
            throw new ArgumentException("system-app-session requires --budget-mib between 1024 and 2048.");
    }

    /// <summary>The guarded options a worker job stands for. A durability writer's OraclePath is the run's own output
    /// (durability-oracle.json beside its reply), not a user --oracle; only the verify job carries the user's.</summary>
    public static VerificationOptions WorkerOptions(WorkerJob job)
    {
        var suite = job.Operation == "compare-target" || ChangesSystemCache(job.Suite) ? job.Suite! : job.Operation;
        var writer = job.Operation == suite && ComparisonPlan.IsDurabilityWrite(suite);
        if (writer && (job.OraclePath is null || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(job.OraclePath)),
                Path.GetDirectoryName(Path.GetFullPath(job.Reply)), StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A C: durability writer records its oracle only in its own run directory.");
        return new(job.Volume, suite, BudgetMiB: job.BudgetMiB, SystemInstance: job.SystemInstance, SystemBytes: job.SystemBytes,
            RecoverableVm: job.RecoverableVm, Product: job.Product,
            OraclePath: ChangesSystemCache(job.Suite) || writer ? null : job.OraclePath);
    }

    public static void ValidateTargets(DiskTarget target, DiskTarget output,
        string expectedInstance, long expectedBytes)
    {
        if (target.Letter != 'C' || !target.IsBoot || !target.IsSystem ||
            target.DiskBytes != expectedBytes ||
            !string.Equals(target.Instance, expectedInstance, StringComparison.OrdinalIgnoreCase))
            throw new IOException("C: boot/system disk identity does not match the explicit expected target.");
        if (output.Number == target.Number ||
            string.Equals(output.Instance, target.Instance, StringComparison.OrdinalIgnoreCase))
            throw new IOException("System-disk results must be on a different physical disk.");
    }

    public static void ValidateRecordedTarget(DiskTarget recorded, DiskTarget current)
    {
        DiskTarget.ValidateRecordedSystemTarget(recorded, current);
    }

    public static string OutputVolume(string outputDirectory)
    {
        var full = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(full))
            throw new IOException("system-preflight output directory must already exist on another physical disk.");
        var root = Path.GetPathRoot(full);
        if (root is null || root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\')
            throw new IOException("system-preflight output must use an explicit local drive letter on another disk.");
        for (var path = new DirectoryInfo(full); path is not null; path = path.Parent)
            if ((path.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("System-disk result path cannot traverse a reparse point.");
        return root[..2];
    }
}
