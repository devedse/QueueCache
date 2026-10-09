using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using QueueCache.Developer.FileTests;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

/// <summary>Two neighboring sectors share one 4K cache block; their untouched bytes are guards.
/// Concurrent submission is exercised, not a forced kernel ordering or lower-I/O proof.</summary>
[SupportedOSPlatform("windows")]
public static class ConcurrentSectorOracle
{
    public static async Task<CheckResult[]> Run(DiskTarget target, CacheDevice device, string directory)
    {
        directory = Path.GetFullPath(directory + "-sector-oracle");
        if (!directory.StartsWith(target.Root, StringComparison.OrdinalIgnoreCase) || Directory.Exists(directory))
            throw new IOException("Sector oracle requires a new owned directory on the selected volume.");
        if (!GetDiskFreeSpaceW(target.Root, out _, out var sector, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (sector != 512)
            throw new NotSupportedException("Concurrent sub-block sector oracle requires 512-byte logical sectors.");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "neighbors.dat");
        var expected = ConcurrentCacheOracle.Pattern(198, 0, 4096);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
        {
            file.Write(expected);
            file.Flush(true);
        }
        ConfigurationManager.Apply(target, new CacheConfiguration(64, CachePreset.Fast)
        {
            Options = new CacheOptions(Drain: DrainAlgorithm.Deferred, HighPercent: 100, MaxDirtyAgeMs: 3600000)
        }, true);
        device.Control(WriteCacheAction.PerformanceTiming, value: 0);
        var before = device.GetWriteCacheState();
        for (var epoch = 1; epoch <= 128; epoch++)
        {
            var first = ConcurrentCacheOracle.Pattern(199, epoch, 512);
            var second = ConcurrentCacheOracle.Pattern(200, epoch, 512);
            using var gate = new ManualResetEventSlim();
            var a = Task.Run(() => { gate.Wait(); UnbufferedFileWrite.WriteRange(path, 0, first); });
            var b = Task.Run(() => { gate.Wait(); UnbufferedFileWrite.WriteRange(path, 512, second); });
            gate.Set();
            await Task.WhenAll(a, b);
            first.CopyTo(expected, 0);
            second.CopyTo(expected, 512);
            if (!UnbufferedFileWrite.ReadPrefix(path, expected.Length).AsSpan().SequenceEqual(expected))
                throw new InvalidDataException($"Concurrent neighboring-sector oracle mismatch at epoch {epoch}.");
        }
        var active = device.GetWriteCacheState();
        if (!before.Operational || !active.Operational || before.Instance != active.Instance || before.Generation != active.Generation ||
            before.Errors != active.Errors || active.LastError != 0 || active.AcceptedBytes < before.AcceptedBytes ||
            active.AcceptedBytes - before.AcceptedBytes < 128UL * 1024)
            throw new IOException("Concurrent sector oracle lacks stable, error-free cache admission.");
        device.Control(WriteCacheAction.Disable);
        var drained = device.GetWriteCacheState();
        if (drained.DirtyBytes != 0 || drained.InFlightBytes != 0 || drained.Errors != active.Errors || drained.LastError != 0 ||
            !UnbufferedFileWrite.ReadPrefix(path, expected.Length).AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("Concurrent neighboring-sector oracle differs after cache drain.");
        return [new("concurrent-neighbor-sectors", "PASS",
            $"128 concurrent pairs of 512-byte writes to one 4K block matched active and drained bytes, including 3072 untouched guard bytes. " +
            $"SHA256={Convert.ToHexString(SHA256.HashData(expected))}. Owned file: {path}. " +
            "Concurrent submission does not force an in-flight ordering or prove zero lower I/O.")];
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster, out uint bytesPerSector,
        out uint freeClusters, out uint clusters);
}
