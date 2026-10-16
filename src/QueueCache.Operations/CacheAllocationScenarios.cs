using System.Runtime.Versioning;
using QueueCache.Management;

namespace QueueCache.Operations;

[SupportedOSPlatform("windows")]
public static class CacheAllocationScenarios
{
    public static readonly int[] BudgetsMiB = [1, 15, 16, 17, 63, 64, 65];

    public static IReadOnlyList<CheckResult> Run(DiskTarget target, CacheDevice device, string directory)
    {
        Directory.CreateDirectory(directory);
        var original = device.GetWriteCacheState();
        var results = new List<CheckResult>();
        try
        {
            foreach (var budget in BudgetsMiB)
            foreach (var preset in new[] { CachePreset.Fast, CachePreset.Strict })
            {
                var id = $"allocation-{budget}-{preset}";
                ConfigurationManager.Apply(target, new CacheConfiguration(budget, preset)
                {
                    Options = new CacheOptions(Drain: DrainAlgorithm.Deferred, BatchKiB: 2048, Parallelism: 8, MaxDirtyAgeMs: 3600000)
                }, true);
                var state = device.GetWriteCacheState();
                if (state.ReservedBytes == 0 || state.ReservedBytes > ((ulong)budget << 20) || state.PayloadCapacity < 128UL << 10)
                    throw new IOException(id + ": reservation exceeds budget or has no usable payload.");
                var expected = new byte[128 << 10];
                new Random(512 + budget).NextBytes(expected);
                var actual = new byte[expected.Length];
                var path = Path.Combine(directory, id + ".bin");
                using (var file = new AlignedFile(path, expected.Length, create: true))
                {
                    file.Write(0, expected);
                    file.Read(0, actual);
                    if (!expected.AsSpan().SequenceEqual(actual)) throw new IOException(id + ": live bytes differ.");
                    device.Control(WriteCacheAction.Flush);
                    device.Control(WriteCacheAction.Disable);
                    file.Read(0, actual);
                    state = device.GetWriteCacheState();
                    if (!expected.AsSpan().SequenceEqual(actual) || state.DirtyBytes != 0 || state.InFlightBytes != 0 ||
                        state.LastError != 0 || state.Errors != original.Errors)
                        throw new IOException(id + ": backing bytes or clean/error state differ.");
                }
                File.Delete(path);
                results.Add(new(id, "PASS", $"Reserved {state.ReservedBytes} of {state.BudgetBytes} bytes; live and unbuffered bypassed bytes matched."));
            }
        }
        finally
        {
            if (original.BudgetBytes == 0) device.Control(WriteCacheAction.Release);
            else ConfigurationManager.Apply(target, CacheConfiguration.FromState(original), true);
        }
        return results;
    }
}
