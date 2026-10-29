namespace QueueCache.Developer.Verification;

/// <summary>Keep historical ordinary-write oracles distinct from the flagged-write cut workload.</summary>
public static class DurabilityOracleContract
{
    public static int Version(bool checkpoint, bool writeThrough, bool growthMarkers = false) => growthMarkers ? 6 : writeThrough ? (checkpoint ? 5 : 4) : (checkpoint ? 3 : 1);

    public static void Validate(DurabilityOracle oracle)
    {
        if (oracle.GrowthMarkers < 0 || oracle.GrowthMarkers == 0 &&
            (oracle.MarkerDeleteStartEpoch is not null || oracle.MarkerDeleteEndEpoch is not null) ||
            oracle.SetMiB <= 0 || oracle.SmallFiles <= 0 || oracle.SchemaVersion != Version(oracle.DurableCheckpoint, oracle.WriteThroughGeneration2, oracle.GrowthMarkers > 0) ||
            (oracle.GrowthMarkers > 0
                ? oracle.Product?.StartsWith("QueueCache", StringComparison.OrdinalIgnoreCase) != true ||
                  oracle.GrowthMarkers != GrowthMarkerEvidence.Count || oracle.DurableCheckpoint || oracle.WriteThroughGeneration2 ||
                  oracle.MarkerDeleteStartEpoch is null or 0 || oracle.MarkerDeleteEndEpoch is null ||
                  oracle.MarkerDeleteEndEpoch < oracle.MarkerDeleteStartEpoch || oracle.OrderingMode != 1 ||
                  oracle.WriteThroughRequestsObserved is not null
                : oracle.WriteThroughGeneration2
                ? oracle.Product?.StartsWith("QueueCache", StringComparison.OrdinalIgnoreCase) != true ||
                  oracle.OrderingMode != 1 ||
                  oracle.WriteThroughRequestsObserved is not { } requests || requests < (ulong)oracle.SetMiB
                : oracle.OrderingMode is not null || oracle.WriteThroughRequestsObserved is not null))
            throw new InvalidDataException("Unknown/inconsistent durability oracle or unexercised write-through workload.");
    }
}
