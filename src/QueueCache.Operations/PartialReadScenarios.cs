using System.Runtime.Versioning;
using System.Security.Cryptography;
using QueueCache.Management;

namespace QueueCache.Operations;

public sealed record PartialReadObservation(string Id, int Bytes, ulong ExpectedCachedBytes,
    CacheStagedReads Before, CacheStagedReads After, CacheStagedReads Delta, string Sha256);

/// <summary>Owns one small file and holds its unbuffered handle across quiescent counter boundaries.
/// Counter accounting and bytes are checked separately; the coordinator owns restoration.</summary>
[SupportedOSPlatform("windows")]
public static class PartialReadScenarios
{
    public const int FileBytes = 1 << 20;
    public static IReadOnlyList<CheckResult> Run(DiskTarget target, CacheDevice device, string directory,
        int budgetMiB, Action<PartialReadObservation> record)
    {
        if (device.GetDiagnostics().StagedReads is null)
            throw new NotSupportedException("Partial-read accounting requires driver diagnostics V21.");
        using var gate = ConfigurationGate.Enter(target.Instance);
        // Every block must have a partly valid mask, even if NTFS splits a large read.
        // The owned file's 512-byte writes require this logical sector size.
        if (!new DriveInfo(target.Root).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Partial-sector accounting requires the NTFS lab volume.");
        var root = target.Root;
        if (!GetDiskFreeSpace(root, out var sectorsPerCluster, out var sector, out _, out _) ||
            sector != 512 || (sectorsPerCluster * (ulong)sector) % 4096 != 0)
            throw new NotSupportedException("Partial-sector accounting requires 512-byte logical sectors and 4K-aligned NTFS clusters.");
        directory = Path.GetFullPath(Path.Combine(directory, "partial-reads"));
        if (!directory.StartsWith(target.Root, StringComparison.OrdinalIgnoreCase) || Directory.Exists(directory))
            throw new IOException("Partial-read accounting requires a new owned directory on the selected volume.");
        Directory.CreateDirectory(directory);
        var checks = new List<CheckResult>();
        foreach (var timing in new[] { false, true })
        {
            device.Control(WriteCacheAction.Flush);
            device.Control(WriteCacheAction.Disable);
            var path = Path.Combine(directory, $"partial-sectors-timing-{timing}.dat");
            var expected = new byte[FileBytes];
            new Random(timing ? 1701 : 1700).NextBytes(expected);
            using var file = new AlignedFile(path, FileBytes, create: true, alignment: 512);
            file.Write(0, expected);
            file.Flush();
            ConfigurationManager.Apply(target, new CacheConfiguration(budgetMiB, CachePreset.Fast)
            {
                Options = new CacheOptions(Drain: DrainAlgorithm.Deferred, HighPercent: 100, MaxDirtyAgeMs: 3600000)
            }, true);
            device.Control(WriteCacheAction.PerformanceTiming, value: timing ? 1UL : 0UL);
            device.Control(WriteCacheAction.DropClean);
            var actual = new byte[FileBytes];
            var prefix = timing ? "timed" : "untimed";
            void Probe(string name, int offset, int length, ulong stagedBytes, ulong cachedBytes, bool mixed)
            {
                var beforeState = device.GetWriteCacheState();
                var before = device.GetDiagnostics().StagedReads ?? throw new InvalidDataException("V21 counters disappeared.");
                var read = length == FileBytes ? actual : new byte[length];
                file.Read(offset, read);
                var after = device.GetDiagnostics().StagedReads ?? throw new InvalidDataException("V21 counters disappeared.");
                var afterState = device.GetWriteCacheState();
                if (beforeState.Instance != afterState.Instance || beforeState.Generation != afterState.Generation ||
                    beforeState.Errors != afterState.Errors || afterState.LastError != 0)
                    throw new InvalidDataException("Partial-read driver identity/health changed.");
                var delta = after.Since(before);
                var id = prefix + "-" + name;
                record(new(id, length, cachedBytes, before, after, delta, Convert.ToHexString(SHA256.HashData(read))));
                if (!read.AsSpan().SequenceEqual(expected.AsSpan(offset, length)))
                    throw new InvalidDataException(id + ": byte mismatch.");
                if (delta.Bytes != stagedBytes || delta.CachedBytes != cachedBytes ||
                    (mixed ? delta.MixedRequests == 0 : delta.MixedRequests != 0) ||
                    (stagedBytes == 0 ? delta.Requests != 0 : delta.Requests == 0))
                    throw new InvalidDataException(id + ": staged-read accounting mismatch; inspect recorded boundaries and filesystem request splitting.");
                checks.Add(new(id, "PASS", $"{length} bytes verified; {delta.Requests} staged attempts, {delta.Bytes} lower bytes, {delta.CachedBytes} already cached bytes."));
            }
            for (var block = 0; block < FileBytes; block += 4096)
            {
                var replacement = new byte[512];
                new Random(block + 19).NextBytes(replacement);
                file.Write(block, replacement);
                replacement.CopyTo(expected, block);
            }
            // Add sector 7 of block zero; sector 0 of block one was already valid.
            var crossing = new byte[1024];
            new Random(83).NextBytes(crossing);
            file.Write(3584, crossing);
            crossing.CopyTo(expected, 3584);
            Probe("crossing-hit", 3584, 1024, 0, 0, false);
            Probe("partial-sectors", 0, FileBytes, FileBytes, (ulong)FileBytes / 8 + 512, true);
            // An explicit drain makes the changed bytes durable; dropping clean is
            // confined to this owned scenario, never an incidental read side effect.
            device.Control(WriteCacheAction.Flush);
            device.Control(WriteCacheAction.DropClean);
            Probe("full-miss", 0, FileBytes, FileBytes, 0, false);
            Probe("full-hit", 0, FileBytes, 0, 0, false);
            new Random(97).NextBytes(expected);
            file.Write(0, expected);
            Probe("overwritten-hit", 0, FileBytes, 0, 0, false);
            device.Control(WriteCacheAction.Flush);
        }
        return checks;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector,
        out uint freeClusters, out uint clusters);
}
