namespace QueueCache.Developer.Verification;

/// <summary>The oracle can finish before the score, but a failed oracle must stop
/// the case immediately. The caller cancels and awaits its owned processes.</summary>
public static class CacheExerciseTasks
{
    public static async Task<T> CompleteWorkload<T>(Task<T> workload, Task? oracle)
    {
        if (oracle is not null && await Task.WhenAny(workload, oracle) == oracle)
            await oracle;
        var result = await workload;
        if (oracle is not null)
            await oracle;
        return result;
    }
}
