using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using QueueCache.Management;

namespace QueueCache.Operations;

/// <summary>Bounded file-only mixed access on a validated disposable non-OS volume.</summary>
[SupportedOSPlatform("windows")]
public static class PagingCoherenceScenarios
{
    private const int MiB = 1 << 20;
    private const int FileBytes = 8 * MiB;
    private const int Offset = 4 * MiB;

    public static IReadOnlyList<CheckResult> Run(DiskTarget target, CacheDevice device, string workDirectory)
    {
        target.ValidateCurrent();
        if (target.IsBoot || target.IsSystem || target.IsPaging ||
            !string.Equals(Path.GetDirectoryName(Path.GetFullPath(workDirectory)), target.Root,
                StringComparison.OrdinalIgnoreCase) || Directory.Exists(workDirectory))
            throw new IOException("Mixed paging/file check requires a fresh owned directory on a non-OS disk.");
        if (device.GetDiagnostics().PagingRoute is null)
            throw new NotSupportedException("Mixed paging/file check requires routed paging Diagnostics V7.");
        Directory.CreateDirectory(workDirectory);
        var path = Path.Combine(workDirectory, "mapped-coherence.bin");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            file.SetLength(FileBytes);
        ConfigurationManager.Apply(target, new CacheConfiguration(64, CachePreset.Fast)
        {
            Options = new CacheOptions(Drain: DrainAlgorithm.Idle)
        }, true);
        var enabled = device.GetWriteCacheState();
        var before = device.GetDiagnostics();
        var a = new byte[MiB];
        var b = new byte[MiB];
        new Random(104729).NextBytes(a);
        new Random(104759).NextBytes(b);
        using (var file = new AlignedFile(path, MiB, create: false))
        {
            file.Write(Offset, a);
            file.Flush();
        }
        var admitted = device.GetWriteCacheState();
        if (admitted.AcceptedBytes < enabled.AcceptedBytes + MiB)
            throw new IOException("Fast cache did not accept the first owned write.");
        var actual = new byte[MiB];
        using (var map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0,
                   MemoryMappedFileAccess.ReadWrite))
        using (var view = map.CreateViewAccessor(0, FileBytes, MemoryMappedFileAccess.ReadWrite))
        {
            view.ReadArray(Offset, actual, 0, actual.Length);
            if (!actual.AsSpan().SequenceEqual(a))
                throw new IOException("Mapped read returned bytes older than the accepted unbuffered write.");
            view.WriteArray(Offset, b, 0, b.Length);
            view.Flush();
        }
        Array.Clear(actual);
        using (var file = new AlignedFile(path, MiB, create: false))
            file.Read(Offset, actual);
        if (!actual.AsSpan().SequenceEqual(b))
            throw new IOException("Unbuffered read did not observe the latest mapped write.");
        var after = device.GetDiagnostics();
        var first = before.PagingRoute!;
        var last = after.PagingRoute ?? throw new IOException("Routed paging diagnostics disappeared.");
        if (last.ReadRequests < first.ReadRequests || last.WriteRequests < first.WriteRequests ||
            last.ReadFailures != first.ReadFailures || last.WriteFailures != first.WriteFailures)
            throw new IOException("Routed paging counters regressed or reported a failure.");
        device.Control(WriteCacheAction.Flush);
        device.Control(WriteCacheAction.Disable);
        device.Control(WriteCacheAction.Release);
        Array.Clear(actual);
        using (var file = new AlignedFile(path, MiB, create: false))
            file.Read(Offset, actual);
        if (!actual.AsSpan().SequenceEqual(b))
            throw new IOException("Released-cache disk bytes did not match the latest mapped write.");
        return
        [
            new("paging-coherence/mapped-after-cache", "PASS", "Mapped read matched the accepted unbuffered write."),
            new("paging-coherence/cache-after-mapped", "PASS", "Unbuffered read and released-cache disk bytes matched the mapped overwrite."),
            new("paging-coherence/observed-routing",
                last.ReadRequests > first.ReadRequests && last.WriteRequests > first.WriteRequests ? "PASS" : "SKIP",
                $"Routed paging-marked requests: reads {last.ReadRequests - first.ReadRequests}, " +
                $"writes {last.WriteRequests - first.WriteRequests}, overlap waits {last.OverlapWaits - first.OverlapWaits}. " +
                "These counters are process-wide; zero overlap does not prove the forced drainer order.")
        ];
    }
}
