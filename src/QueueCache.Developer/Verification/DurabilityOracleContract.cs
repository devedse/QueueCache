namespace QueueCache.Developer.Verification;

/// <summary>Keep historical ordinary-write oracles distinct from the flagged-write cut workload.</summary>
public static class DurabilityOracleContract
{
    public static int Version(bool checkpoint, bool writeThrough) => writeThrough ? (checkpoint ? 5 : 4) : (checkpoint ? 3 : 1);

    public static void Validate(DurabilityOracle oracle)
    {
        if (oracle.SetMiB <= 0 || oracle.SmallFiles <= 0 || oracle.SchemaVersion != Version(oracle.DurableCheckpoint, oracle.WriteThroughGeneration2) ||
            (oracle.WriteThroughGeneration2
                ? oracle.Product?.StartsWith("QueueCache", StringComparison.OrdinalIgnoreCase) != true ||
                  oracle.OrderingMode is not (1 or 2) ||
                  oracle.WriteThroughRequestsObserved is not { } requests || requests < (ulong)oracle.SetMiB
                : oracle.OrderingMode is not null || oracle.WriteThroughRequestsObserved is not null))
            throw new InvalidDataException("Unknown/inconsistent durability oracle or unexercised write-through workload.");
    }
}
