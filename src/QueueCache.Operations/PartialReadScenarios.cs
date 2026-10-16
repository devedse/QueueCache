using System.Runtime.Versioning;
using System.Security.Cryptography;
using QueueCache.Management;

namespace QueueCache.Operations;

public sealed record PartialReadObservation(string Id, int Bytes, ulong ExpectedCachedBytes,
    CacheStagedReads Before, CacheStagedReads After, CacheStagedReads Delta, string Sha256);
public sealed record ConcurrentMissObservation(string Id, int Readers, long VerifiedBytes,
    WriteCacheState Before, WriteCacheState After, CacheDiagnostics DiagnosticsBefore, CacheDiagnostics DiagnosticsAfter);

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

    /// <summary>Simultaneous reads of the same cold/partly dirty blocks. A completing miss
    /// may fill blocks another read originally found absent; that read must not release
    /// somebody else's pins. Large worker reads also overlap the small offloaded reads.</summary>
    public static IReadOnlyList<CheckResult> ConcurrentMisses(DiskTarget target, CacheDevice device,
        string directory, int budgetMiB, Action<ConcurrentMissObservation> record)
    {
        using var gate = ConfigurationGate.Enter(target.Instance);
        const int bytes = 4 << 20, readers = 16;
        var path = Path.GetFullPath(Path.Combine(directory, "concurrent-read-misses.dat"));
        if (!path.StartsWith(target.Root, StringComparison.OrdinalIgnoreCase) || File.Exists(path))
            throw new IOException("Concurrent read misses require a new owned file on the selected volume.");
        device.Control(WriteCacheAction.Flush);
        device.Control(WriteCacheAction.Disable);
        var expected = new byte[bytes];
        new Random(1415).NextBytes(expected);
        using var writer = new AlignedFile(path, bytes, create: true, alignment: 512, shareRead: true);
        writer.Write(0, expected);
        writer.Flush();
        ConfigurationManager.Apply(target, new CacheConfiguration(budgetMiB, CachePreset.Fast)
        {
            Options = new CacheOptions(Drain: DrainAlgorithm.Deferred, HighPercent: 100, MaxDirtyAgeMs: 3600000)
        }, true);
        var checks = new List<CheckResult>();
        for (var round = 0; round < 12; round++)
        {
            device.Control(WriteCacheAction.Flush);
            device.Control(WriteCacheAction.DropClean);
            if (round % 2 == 1)
                for (var block = 0; block < bytes; block += 4096)
                {
                    var sector = new byte[512];
                    new Random(round * 1009 + block).NextBytes(sector);
                    writer.Write(block, sector);
                    sector.CopyTo(expected, block);
                }
            using var ready = new CountdownEvent(readers);
            using var start = new ManualResetEventSlim();
            var before = device.GetWriteCacheState();
            var diagnosticBefore = device.GetDiagnostics();
            var tasks = new List<Task<long>>();
            var allReady = false;
            try
            {
                foreach (var reader in Enumerable.Range(0, readers))
                    tasks.Add(Task.Factory.StartNew(() =>
                    {
                        var length = reader == 0 ? bytes : reader % 3 == 0 ? 4096 : 1 << 20;
                        AlignedFile? file = null;
                        try
                        {
                            try { file = new AlignedFile(path, length, create: false, sharedReadOnly: true); }
                            finally { ready.Signal(); }
                            start.Wait();
                            var actual = new byte[length];
                            // Each handle owns its buffer. Every byte is checked, including the
                            // dirty sector overlays; poison catches short or missing copies.
                            for (var pass = 0; pass < 3; pass++)
                            {
                                Array.Fill(actual, (byte)0xA5);
                                file.Read(0, actual);
                                if (!actual.AsSpan().SequenceEqual(expected.AsSpan(0, length)))
                                    throw new InvalidDataException($"Concurrent miss round {round}, reader {reader}: byte mismatch.");
                            }
                            return 3L * length;
                        }
                        finally { file?.Dispose(); }
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
                allReady = ready.Wait(TimeSpan.FromSeconds(30));
            }
            finally
            {
                // Never leave an owned thread waiting if a handle/thread failed to start.
                start.Set();
                Task.WaitAll(tasks.ToArray());
            }
            if (!allReady) throw new TimeoutException("Concurrent read workers did not become ready.");
            var after = device.GetWriteCacheState();
            var diagnosticAfter = device.GetDiagnostics();
            var id = $"concurrent-misses-r{round + 1:D2}-{(round % 2 == 0 ? "cold" : "partial")}";
            var verified = tasks.Sum(t => t.Result);
            record(new(id, readers, verified, before, after, diagnosticBefore, diagnosticAfter));
            if (before.Instance != after.Instance || before.Generation != after.Generation ||
                before.Errors != after.Errors || after.LastError != 0 ||
                diagnosticBefore.StagedReads is not { } first || diagnosticAfter.StagedReads is not { } last ||
                last.Requests <= first.Requests)
                throw new InvalidDataException(id + ": missing lower-read coverage or changed cache identity/health.");
            // Leaked pins keep clean entries from being evicted. After draining and
            // dropping them, the whole fixture must miss and still match every byte.
            device.Control(WriteCacheAction.Flush);
            device.Control(WriteCacheAction.DropClean);
            var coldBefore = device.GetWriteCacheState();
            var persisted = new byte[bytes];
            writer.Read(0, persisted);
            var coldAfter = device.GetWriteCacheState();
            if (!persisted.AsSpan().SequenceEqual(expected) || coldAfter.ReadMissBytes < coldBefore.ReadMissBytes ||
                coldAfter.ReadMissBytes - coldBefore.ReadMissBytes < bytes)
                throw new InvalidDataException(id + ": persisted bytes mismatch or clean entries remained pinned.");
            checks.Add(new(id, "PASS", $"{readers} readers verified {verified} bytes, plus {bytes} after drain/drop-clean."));
        }
        return checks;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector,
        out uint freeClusters, out uint clusters);
}
