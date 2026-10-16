using QueueCache.Management;

namespace QueueCache.Developer.Verification;

/// <summary>Retry must persist the retained dirty payload and leave the fixture clean.
/// New filesystem writes do not excuse a dirty result; settle them before capturing it.</summary>
public static class StressRetryEvidence
{
    public static void Validate(WriteCacheState faulted, WriteCacheState retried)
    {
        if (!faulted.Faulted || faulted.DirtyBytes == 0 || faulted.Instance == 0 ||
            retried.Instance != faulted.Instance || retried.Generation != faulted.Generation ||
            retried.BudgetBytes != faulted.BudgetBytes || retried.PayloadCapacity != faulted.PayloadCapacity ||
            !retried.Operational || retried.DirtyBytes != 0 || retried.InFlightBytes != 0 ||
            retried.Errors != faulted.Errors || retried.AcceptedBytes < faulted.AcceptedBytes ||
            retried.DrainedBytes < faulted.DrainedBytes ||
            retried.DrainedBytes - faulted.DrainedBytes < faulted.DirtyBytes ||
            retried.DiscardedBytes != faulted.DiscardedBytes)
            throw new InvalidDataException("Retry did not persist the retained payload with a clean, unchanged cache identity.");
    }
}
