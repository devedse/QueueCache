using System.Diagnostics;
using System.Runtime.Versioning;
using QueueCache.Management;

namespace QueueCache.Operations;

/// <summary>Explicit, current-boot policy scenarios. Only fresh files are written;
/// runtime settings are restored, saved profiles are never modified.</summary>
[SupportedOSPlatform("windows")]
public static class CacheScenarios
{
    public static Task<IReadOnlyList<CheckResult>> RunAsync(DiskTarget target, IProgress<string>? progress = null,
        CancellationToken token = default) => Task.Run<IReadOnlyList<CheckResult>>(() =>
    {
        using var gate = ConfigurationGate.Enter();
        using var device = new CacheDevice(target.Device, writable: true);
        var original = device.GetWriteCacheState(); ConfigurationManager.EnsureHealthy(original);
        if (!original.SupportsReadWrite) throw new NotSupportedException("Read/write driver required.");
        var directory = Path.Combine(target.Root, "QueueCache-Scenarios-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var results = new List<CheckResult>();
        CacheOptions[] cases = [new(), new(CacheAllocation.Fixed, 50, Drain: DrainAlgorithm.Balanced, Parallelism: 2),
            new(CacheAllocation.Fixed, 50, PromoteOnRead: false, Drain: DrainAlgorithm.Idle, Parallelism: 4),
            new(CacheAllocation.Fixed, 100, RetainWrites: false), new(CacheAllocation.Fixed, 0), new(Parallelism: 4, BatchKiB: 1024)];
        try
        {
            for (int scenario = 0; scenario < cases.Length; scenario++)
            {
                token.ThrowIfCancellationRequested(); var options = cases[scenario];
                var label = $"{scenario + 1}: {options.Allocation}/{options.WritePercent}%/{options.Drain}/{options.Parallelism} drains";
                progress?.Report(label);
                ConfigurationManager.Apply(target, new CacheConfiguration(64, scenario == 4 ? CachePreset.Strict : CachePreset.Fast) { Options = options }, true);
                var file = Path.Combine(directory, $"scenario-{scenario + 1}.bin");
                var expected = new byte[8 << 20]; new Random(173 + scenario).NextBytes(expected);
                var actual = new byte[expected.Length];
                using (var data = new AlignedFile(file, expected.Length, create: true))
                {
                    data.Write(0, expected); data.Read(0, actual);
                    if (!expected.AsSpan().SequenceEqual(actual)) throw new IOException(label + ": live read mismatch");
                    // Rapid replacements exercise dirty/in-flight version ownership.
                    for (int overwrite = 0; overwrite < 8; overwrite++)
                    { token.ThrowIfCancellationRequested(); expected[overwrite] ^= 0x5A; data.Write(0, expected); }
                    device.Control(WriteCacheAction.Flush);
                    data.Read(0, actual); var first = device.GetWriteCacheState();
                    data.Read(0, actual); var second = device.GetWriteCacheState();
                    if (!expected.AsSpan().SequenceEqual(actual)) throw new IOException(label + ": warm read mismatch");
                    bool cacheReads = options.Allocation == CacheAllocation.Automatic || options.WritePercent < 100 || options.RetainWrites;
                    if (cacheReads && second.ReadHitBytes - first.ReadHitBytes < (ulong)expected.Length)
                        throw new IOException(label + ": warm read did not hit RAM");
                    results.Add(new(label + "/warm-read", "PASS", $"Hit delta {second.ReadHitBytes - first.ReadHitBytes} bytes; contents verified."));
                    if (cacheReads)
                    {
                        // A separate tiny file and inventory queries must not evict this hot file.
                        // The retained payload is much smaller than either configured pool.
                        using (var metadata = new FileStream(Path.Combine(directory, $"tiny-{scenario}.bin"), FileMode.CreateNew, FileAccess.Write))
                        { metadata.Write(new byte[512]); metadata.Flush(true); }
                        var afterMetadata = device.GetWriteCacheState();
                        var metadataBarrier = device.GetDiagnostics().LastBarrierCode;
                        _ = DiskCatalog.ListAsync().GetAwaiter().GetResult();
                        var before = device.GetWriteCacheState();
                        data.Read(0, actual);
                        var after = device.GetWriteCacheState();
                        if (!expected.AsSpan().SequenceEqual(actual)) throw new IOException(label + ": retained read mismatch");
                        if (after.ReadHitBytes - before.ReadHitBytes < (ulong)expected.Length)
                            throw new IOException(label + $": unrelated metadata/discovery evicted hot read data; clean after metadata={afterMetadata.CleanReadBytes + afterMetadata.CleanWriteBytes}, barrier=0x{metadataBarrier:X}; after discovery={before.CleanReadBytes + before.CleanWriteBytes}, barrier=0x{device.GetDiagnostics().LastBarrierCode:X}; hit={after.ReadHitBytes - before.ReadHitBytes}");
                        results.Add(new(label + "/retention", "PASS", "Hot data survives unrelated small-file writes and disk discovery."));
                    }
                    device.Control(WriteCacheAction.Disable);
                    // Disabled routing + unbuffered read is an independent lower-disk oracle.
                    data.Read(0, actual);
                    if (!expected.AsSpan().SequenceEqual(actual)) throw new IOException(label + ": disabled-cache disk read mismatch");
                    var state = device.GetWriteCacheState();
                    if (state.Enabled || state.DirtyBytes != 0 || state.InFlightBytes != 0 || state.LastError != 0 || state.Errors != original.Errors)
                        throw new IOException(label + ": drain/state mismatch");
                    results.Add(new(label + "/disk-oracle", "PASS", "All bytes matched with caching disabled, no pending writes/errors."));
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { results.Add(new("scenario", "FAIL", ex.Message)); }
        finally
        {
            progress?.Report("Restoring original runtime configuration; retained files: " + directory);
            // Report restoration failure rather than pretending the previous cache is active.
            if (original.BudgetBytes == 0) device.Control(WriteCacheAction.Release);
            else ConfigurationManager.Apply(target, CacheConfiguration.FromState(original), true);
        }
        return results;
    }, token);
}
