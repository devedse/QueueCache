using QueueCache.Management;

namespace QueueCache.Developer.Verification;

public static class CacheExerciseEvidence
{
    public static void Validate(CacheLayoutSnapshot before, CacheLayoutSnapshot after, long bytes, string workload, bool drain)
    {
        var a = before.State; var b = after.State;
        if (!a.Operational || !b.Operational || a.Instance == 0 || a.Instance != b.Instance || a.Generation != b.Generation ||
            a.PayloadCapacity != b.PayloadCapacity || a.Errors != b.Errors || a.LastError != 0 || b.LastError != 0 ||
            before.Performance.TimingEnabled != 0 || after.Performance.TimingEnabled != 0 || bytes <= 0)
            throw new InvalidDataException("Cache exercise changed allocation, encountered an error or lacks a valid timing-off score.");
        if (workload == "mixed" || drain)
            return; // Disk I/O is intended; residency is not claimed for mixed or draining workloads.
        var x = before.Diagnostics.Attribution ?? throw new InvalidDataException("Missing lower-I/O attribution.");
        var y = after.Diagnostics.Attribution ?? throw new InvalidDataException("Missing lower-I/O attribution.");
        if (x.LowerReadAttempts != y.LowerReadAttempts || x.LowerWriteAttempts != y.LowerWriteAttempts ||
            x.LowerFlushAttempts != y.LowerFlushAttempts || a.DrainedBytes != b.DrainedBytes || a.ReadMissBytes != b.ReadMissBytes ||
            (workload == "write" ? b.AcceptedBytes < a.AcceptedBytes || b.AcceptedBytes - a.AcceptedBytes < (ulong)bytes
                                  : b.ReadHitBytes < a.ReadHitBytes || b.ReadHitBytes - a.ReadHitBytes < (ulong)bytes))
            throw new InvalidDataException("The fitting RAM-only exercise used lower I/O or failed to account for all workload bytes.");
    }
}
