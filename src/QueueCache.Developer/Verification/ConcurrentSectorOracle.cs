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
    public static void ValidateTarget(string fileSystem, uint sector, uint sectorsPerCluster)
    {
        if (!fileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || sector != 512 ||
            sectorsPerCluster == 0 || (ulong)sector * sectorsPerCluster % 4096 != 0)
            throw new NotSupportedException("Concurrent shared-block sector oracle requires NTFS, 512-byte logical sectors and clusters aligned to 4K.");
    }

    public static void ValidateDisabled(WriteCacheState active, WriteCacheState drained)
    {
        // Disable drains, clears clean slots and advances Generation once. It retains
        // the instance and allocation; an unchanged generation is not a successful disable.
        if (drained.Enabled || drained.Faulted || drained.Suspended || drained.Removed || drained.Draining ||
            drained.Instance != active.Instance || drained.Generation != unchecked(active.Generation + 1) ||
            drained.PayloadCapacity != active.PayloadCapacity || drained.ReservedBytes != active.ReservedBytes ||
            drained.DirtyBytes != 0 || drained.InFlightBytes != 0 || drained.OccupiedSlots != 0 ||
            drained.Errors != active.Errors || drained.LastError != 0)
            throw new InvalidDataException("Concurrent sector oracle disable did not drain and clear the same allocation with one generation advance.");
    }

    public static async Task<CheckResult[]> Run(DiskTarget target, CacheDevice device, string directory, string evidence)
    {
        directory = Path.GetFullPath(directory + "-sector-oracle");
        if (!directory.StartsWith(target.Root, StringComparison.OrdinalIgnoreCase) || Directory.Exists(directory))
            throw new IOException("Sector oracle requires a new owned directory on the selected volume.");
        if (!GetDiskFreeSpaceW(target.Root, out var sectorsPerCluster, out var sector, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        ValidateTarget(new DriveInfo(target.Root).DriveFormat, sector, sectorsPerCluster);
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
        RunStorage.AtomicJson(evidence + ".before.json", before);
        for (var epoch = 1; epoch <= 128; epoch++)
        {
            var first = ConcurrentCacheOracle.Pattern(199, epoch, 512);
            var second = ConcurrentCacheOracle.Pattern(200, epoch, 512);
            using var gate = new Barrier(2);
            void Write(long offset, byte[] bytes)
            {
                if (!gate.SignalAndWait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Concurrent sector writers did not become ready together.");
                UnbufferedFileWrite.WriteRange(path, offset, bytes);
            }
            var a = Task.Run(() => Write(0, first));
            var b = Task.Run(() => Write(512, second));
            await Task.WhenAll(a, b);
            first.CopyTo(expected, 0);
            second.CopyTo(expected, 512);
            if (!UnbufferedFileWrite.ReadPrefix(path, expected.Length).AsSpan().SequenceEqual(expected))
                throw new InvalidDataException($"Concurrent neighboring-sector oracle mismatch at epoch {epoch}.");
        }
        var active = device.GetWriteCacheState();
        RunStorage.AtomicJson(evidence + ".active.json", active);
        if (!before.Operational || !active.Operational || before.Instance != active.Instance || before.Generation != active.Generation ||
            before.Errors != active.Errors || active.LastError != 0 || active.AcceptedBytes < before.AcceptedBytes ||
            active.AcceptedBytes - before.AcceptedBytes < 128UL * 1024)
            throw new IOException("Concurrent sector oracle lacks stable, error-free cache admission.");
        device.Control(WriteCacheAction.Disable);
        var drained = device.GetWriteCacheState();
        RunStorage.AtomicJson(evidence + ".disabled.json", drained);
        ValidateDisabled(active, drained);
        var diskBytes = UnbufferedFileWrite.ReadPrefix(path, expected.Length);
        File.WriteAllBytes(evidence + ".expected.bin", expected);
        File.WriteAllBytes(evidence + ".disk.bin", diskBytes);
        if (!diskBytes.AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("Concurrent neighboring-sector oracle differs after cache drain.");
        return [new("concurrent-neighbor-sectors", "PASS",
            $"128 concurrent pairs of 512-byte writes to one 4K block matched active and drained bytes, including 3072 untouched guard bytes. " +
            $"SHA256={Convert.ToHexString(SHA256.HashData(expected))}. Owned file: {path}. " +
            $"NTFS, logical sector {sector} bytes, cluster {(ulong)sector * sectorsPerCluster} bytes. " +
            "Concurrent submission does not force an in-flight ordering or prove zero lower I/O.")];
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster, out uint bytesPerSector,
        out uint freeClusters, out uint clusters);
}
