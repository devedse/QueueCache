using System.Runtime.Versioning;
using QueueCache.Management;

namespace QueueCache.Operations;

public sealed record DrainSeedResult(int Seed, long Bytes, ulong OwnedBytes, WriteCacheState Before,
    WriteCacheState After, CachePerformance PerformanceBefore, CachePerformance PerformanceAfter,
    CacheAttribution AttributionBefore, CacheAttribution AttributionAfter);

/// <summary>Deterministic dirty-set preparation for the maintained drain-decision suite.</summary>
[SupportedOSPlatform("windows")]
public static class DrainDecisionScenarios
{
    private const int TransferBytes = 1 << 20;

    public static DrainSeedResult PrepareDirtySet(CacheDevice device, string path, int bytes,
        int seed, CancellationToken token = default)
    {
        if (bytes <= 0 || bytes % TransferBytes != 0)
            throw new ArgumentException("Drain seed size must be a positive whole number of MiB.");
        if (!File.Exists(path) || new FileInfo(path).Length != bytes)
            throw new IOException("Drain seed file is missing or has the wrong fixed length.");

        // Establish the seed baseline in the same worker that writes it. A previous
        // case can leave a late NTFS metadata write after the coordinator's cleanup
        // worker exits; measuring that write as part of this seed would make the
        // parallelism comparison neither isolated nor reproducible.
        device.Control(WriteCacheAction.Flush);
        device.Control(WriteCacheAction.DropClean);
        var before = device.GetWriteCacheState();
        var performanceBefore = device.GetPerformance();
        var attributionBefore = device.GetDiagnostics().Attribution ??
            throw new NotSupportedException("Drain decision requires lower-I/O attempt counters.");
        if (before.DirtyBytes != 0 || before.InFlightBytes != 0)
            throw new IOException("Drain seed preparation requires a clean cache.");

        using (var file = new AlignedFile(path, TransferBytes, create: false))
        {
            for (var offset = 0; offset < bytes; offset += TransferBytes)
            {
                token.ThrowIfCancellationRequested();
                var block = new byte[TransferBytes];
                new Random(unchecked(seed * 397 ^ offset / TransferBytes)).NextBytes(block);
                file.Write(offset, block);
            }
        }

        var after = device.GetWriteCacheState();
        var performanceAfter = device.GetPerformance();
        var attributionAfter = device.GetDiagnostics().Attribution ??
            throw new NotSupportedException("Drain decision requires lower-I/O attempt counters.");
        var accepted = after.AcceptedBytes - before.AcceptedBytes;
        if (accepted != after.DirtyBytes || accepted < (ulong)bytes || accepted > (ulong)bytes + (64 << 10) ||
            after.InFlightBytes != 0 ||
            attributionAfter.LowerWriteAttempts != attributionBefore.LowerWriteAttempts ||
            attributionAfter.LowerFlushAttempts != attributionBefore.LowerFlushAttempts)
            throw new IOException("Dirty-set preparation was not an isolated fitting RAM admission: " +
                $"requested={bytes}, acceptedDelta={after.AcceptedBytes - before.AcceptedBytes}, " +
                $"dirty={after.DirtyBytes}, inFlight={after.InFlightBytes}, " +
                $"lowerAttemptDelta={attributionAfter.LowerReadAttempts - attributionBefore.LowerReadAttempts}/" +
                $"{attributionAfter.LowerWriteAttempts - attributionBefore.LowerWriteAttempts}/" +
                $"{attributionAfter.LowerFlushAttempts - attributionBefore.LowerFlushAttempts}.");

        return new(seed, bytes, accepted, before, after, performanceBefore, performanceAfter,
            attributionBefore, attributionAfter);
    }

    public static void VerifyDirtySet(string path, int bytes, int seed,
        CancellationToken token = default)
    {
        var actual = new byte[TransferBytes];
        using var file = new AlignedFile(path, TransferBytes, create: false);
        for (var offset = 0; offset < bytes; offset += TransferBytes)
        {
            token.ThrowIfCancellationRequested();
            var expected = new byte[TransferBytes];
            new Random(unchecked(seed * 397 ^ offset / TransferBytes)).NextBytes(expected);
            file.Read(offset, actual);
            if (!expected.AsSpan().SequenceEqual(actual))
                throw new IOException($"Persisted drain seed mismatch at offset {offset}.");
        }
    }
}
