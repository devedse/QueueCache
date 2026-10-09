namespace QueueCache.Developer.Verification;

/// <summary>A completed run's own workload folders on the tested volume: the run directory's name under the
/// volume root and its "-sector-oracle" sibling. Nothing else is ever removed.</summary>
public static class WorkloadCleanup
{
    public static IReadOnlyList<string> Remove(string root, string runName)
    {
        if (!runName.StartsWith("QueueCache-Verify-", StringComparison.Ordinal) || Path.GetFileName(runName) != runName ||
            runName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Not a verification run's workload folder name: " + runName);
        var removed = new List<string>();
        foreach (var directory in new[] { Path.Combine(root, runName), Path.Combine(root, runName + "-sector-oracle") })
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
                removed.Add(directory);
            }
        return removed;
    }
}
