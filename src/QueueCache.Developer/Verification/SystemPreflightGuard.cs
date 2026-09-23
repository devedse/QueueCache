using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

/// <summary>Read-only OS-disk inventory gate. It never enables caching or writes a workload.</summary>
public static class SystemPreflightGuard
{
    public static void ValidateOptions(VerificationOptions options)
    {
        if (options.Suite is not ("system-preflight" or "system-files" or "system-post-restart" or "system-image-baseline" or "system-active-image"))
        {
            if (options.SystemInstance is not null || options.SystemBytes is not null || options.RecoverableVm || options.OraclePath is not null)
                throw new ArgumentException("System-disk opt-in arguments are only valid for guarded system suites.");
            return;
        }
        if (!string.Equals(options.Volume, "C:", StringComparison.OrdinalIgnoreCase) ||
            !options.RecoverableVm || string.IsNullOrWhiteSpace(options.SystemInstance) ||
            options.SystemBytes is null or <= 0)
            throw new ArgumentException("System suites require C:, --recoverable-vm, --system-instance and --system-bytes.");
        if (options.DiskSpd is not null || options.CaseFilter is not null)
            throw new ArgumentException("System suites do not accept DiskSpd or case filters.");
        if ((options.Suite == "system-post-restart") != (options.OraclePath is not null))
            throw new ArgumentException("--oracle is required only for system-post-restart.");
        if (options.Suite == "system-active-image" && options.BudgetMiB is < 256 or > 512)
            throw new ArgumentException("system-active-image requires a conservative --budget-mib between 256 and 512.");
    }

    public static void ValidateTargets(DiskTarget target, DiskTarget output,
        string expectedInstance, long expectedBytes)
    {
        if (target.Letter != 'C' || !target.IsBoot || !target.IsSystem ||
            target.Bytes != expectedBytes ||
            !string.Equals(target.Instance, expectedInstance, StringComparison.OrdinalIgnoreCase))
            throw new IOException("C: boot/system disk identity does not match the explicit expected target.");
        if (output.Number == target.Number ||
            string.Equals(output.Instance, target.Instance, StringComparison.OrdinalIgnoreCase))
            throw new IOException("System-disk results must be on a different physical disk.");
    }

    public static void ValidateRecordedTarget(DiskTarget recorded, DiskTarget current)
    {
        // A pagefile may be added or removed between the create and post-restart
        // phases. That changes IsPaging, not the physical disk identity. Keep every
        // stable identity and system-role field exact.
        if (recorded.Letter != current.Letter ||
            recorded.Number != current.Number ||
            recorded.Bytes != current.Bytes ||
            !string.Equals(recorded.Instance, current.Instance, StringComparison.OrdinalIgnoreCase) ||
            recorded.IsBoot != current.IsBoot ||
            recorded.IsSystem != current.IsSystem)
            throw new IOException("Post-restart oracle target identity changed.");
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
