using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

/// <summary>A mapped page-in behind a queued application flush during an owned drain hold.</summary>
[SupportedOSPlatform("windows")]
public static class PagingReadProgressScenario
{
    public const string Suite = "paging-read-progress";
    public const int BudgetMiB = 256;
    private const int Block = 4096, MiB = 1 << 20, ProbeBytes = 64 * 1024, WriteOffset = 4 * MiB;

    public static void Validate(CacheDiagnostics before, CacheDiagnostics during, WriteCacheState held,
        double readSeconds, bool flushCompleted, bool applicationFlushCompleted)
    {
        var a = before.PagingIo ?? throw new InvalidDataException("Missing paging-request baseline.");
        var b = during.PagingIo ?? throw new InvalidDataException("Missing paging-request evidence.");
        var ap = before.PagingProgress ?? throw new InvalidDataException("Missing read-service baseline.");
        var bp = during.PagingProgress ?? throw new InvalidDataException("Missing read-service evidence.");
        var ao = before.PagingOffload ?? throw new InvalidDataException("Missing offloaded-read baseline.");
        var bo = during.PagingOffload ?? throw new InvalidDataException("Missing offloaded-read evidence.");
        if (b.ReadRequests <= a.ReadRequests ||
            bp.ServicedReadMisses <= ap.ServicedReadMisses && bo.Completions <= ao.Completions)
            throw new InvalidDataException("The lab must witness a new page-in completed by the service lane.");
        if (!double.IsFinite(readSeconds) || readSeconds < 0 || readSeconds > 2 || flushCompleted || applicationFlushCompleted ||
            during.LabGate is not { State: 2, OldLowerDoneSeq: > 0, OldRetireSeq: 0 } ||
            !held.Draining || !held.Enabled || held.Faulted || held.LastError != 0 || held.Errors != 0 ||
            held.DirtyBytes + held.InFlightBytes == 0)
            throw new InvalidDataException("The page-in must finish within two seconds while both flushes remain pending and the cache is healthy.");
    }

    public static void ValidateChecks(IReadOnlyList<CheckResult> checks)
    {
        if (checks.Count != 2 || checks.Any(c => c.Result != "PASS") ||
            !checks.Select(c => c.Name).ToHashSet(StringComparer.Ordinal).SetEquals(
                ["paging-read-progress/flush-fence-page-in", "paging-read-progress/durable-write"]))
            throw new InvalidDataException("The paging-read progress checks are incomplete or duplicated.");
    }

    public static async Task<IReadOnlyList<CheckResult>> RunAsync(DiskTarget target, CacheDevice device,
        string work, string evidence)
    {
        if (target.IsBoot || target.IsSystem || target.IsPaging ||
            !new DriveInfo(target.Root).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Read progress requires the disposable non-OS NTFS lab.");
        Directory.CreateDirectory(work);
        device.Control(WriteCacheAction.LabDelay, value: 0);
        device.Control(WriteCacheAction.Disable);
        device.Control(WriteCacheAction.Release);
        device.Control(WriteCacheAction.FlushOrder, value: 0);
        // Unbuffered initialization leaves a fresh file whose mapped pages have never been touched.
        var readPath = OrderingFaultScenarios.CreateOwned(work, "page-in.bin");
        var dirtyPath = OrderingFaultScenarios.CreateOwned(work, "held-drain.bin");
        var physical = PagingCoherenceScenarios.DiskOffsetOf(dirtyPath, WriteOffset, target.Root);
        using var backing = new FileStream(readPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var mapping = MemoryMappedFile.CreateFromFile(backing, null, 0, MemoryMappedFileAccess.Read,
            HandleInheritability.None, leaveOpen: true);
        using var view = mapping.CreateViewAccessor(0, ProbeBytes, MemoryMappedFileAccess.Read);
        using var dirty = new AlignedFile(dirtyPath, Block, create: false, shareAll: true);
        ConfigurationManager.Apply(target, new(BudgetMiB)
        {
            WriteOrdering = true,
            Options = new(Drain: DrainAlgorithm.Deferred, MaxDirtyAgeMs: 3600000,
                BatchKiB: 4, Parallelism: 1, RetainWrites: false, PromoteOnRead: false)
        }, true);
        device.Control(WriteCacheAction.PerformanceTiming, value: 1);
        var expected = ConcurrentCacheOracle.Pattern(215, 1, Block);
        // Arm before the filesystem flush can seal and drain the owned block.
        // The existing native gate permits at most five seconds and releases itself.
        device.Control(WriteCacheAction.LabGate, (ulong)physical, (5000UL << 32) | Block);
        Task? flush = null;
        Task? applicationFlush = null;
        Task<double>? read = null;
        var bytes = new byte[ProbeBytes];
        try
        {
            dirty.Write(WriteOffset, expected); dirty.Flush();
            flush = Task.Run(() =>
            {
                using var controller = new CacheDevice(target.Device, writable: true);
                controller.Control(WriteCacheAction.Flush);
            });
            var waiting = Stopwatch.StartNew();
            while (device.GetDiagnostics().LabGate is not { State: 2, OldLowerDoneSeq: > 0 })
            {
                if (flush.IsCompleted || waiting.Elapsed.TotalSeconds > 5)
                    throw new IOException("The explicit flush did not reach its owned drain hold.");
                await Task.Delay(10);
            }
            var beforePerformance = device.GetPerformance();
            var flushEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            applicationFlush = Task.Run(() => { flushEntered.SetResult(); dirty.Flush(); });
            await flushEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            waiting.Restart();
            CachePerformance queued;
            // Queue depth and arrival count are read live under QueueLock. Detailed
            // selection fields are published only when cache state changes, so a
            // fully held drainer cannot provide a fresh selection-field witness.
            do
            {
                queued = device.GetPerformance();
                if (queued.TimingEnabled == 0 || applicationFlush.IsCompleted || flush.IsCompleted ||
                    waiting.Elapsed.TotalSeconds > 3)
                    throw new IOException("The application flush did not remain queued behind the held explicit flush.");
                await Task.Delay(10);
            } while (queued.QueueDepth <= beforePerformance.QueueDepth ||
                queued.QueuedRequests <= beforePerformance.QueuedRequests);
            var before = device.GetDiagnostics();
            RunStorage.AtomicJson(evidence + ".before.json", new { Diagnostics = before, QueuedFlush = queued });
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            read = Task.Run(() =>
            {
                var clock = Stopwatch.StartNew();
                entered.SetResult();
                if (view.ReadArray(0, bytes, 0, bytes.Length) != bytes.Length)
                    throw new IOException("The mapped byte oracle was not read completely.");
                GC.KeepAlive(view);
                return clock.Elapsed.TotalSeconds;
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            double seconds;
            try { seconds = await read.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException)
            {
                RunStorage.AtomicJson(evidence + ".blocked.json", new
                {
                    State = device.GetWriteCacheState(), Diagnostics = device.GetDiagnostics(),
                    Performance = device.GetPerformance(), FlushCompleted = flush.IsCompleted,
                    ApplicationFlushCompleted = applicationFlush.IsCompleted
                });
                throw new IOException("The page-in remained blocked behind the queued application flush for more than two seconds.");
            }
            var during = device.GetDiagnostics();
            var held = device.GetWriteCacheState();
            RunStorage.AtomicJson(evidence + ".during.json", new { State = held, Diagnostics = during,
                ReadSeconds = seconds, FlushCompleted = flush.IsCompleted, ApplicationFlushCompleted = applicationFlush.IsCompleted });
            Validate(before, during, held, seconds, flush.IsCompleted, applicationFlush.IsCompleted);
            if (bytes.Any(b => b != 0)) throw new IOException("The untouched mapped file's byte oracle changed.");
            await flush.WaitAsync(TimeSpan.FromSeconds(15));
            await applicationFlush.WaitAsync(TimeSpan.FromSeconds(15));
            device.Control(WriteCacheAction.Disable);
            var actual = new byte[Block]; dirty.Read(WriteOffset, actual);
            if (!actual.AsSpan().SequenceEqual(expected)) throw new IOException("The held write did not reach the disk intact.");
            RunStorage.AtomicJson(evidence + ".after.json", new { State = device.GetWriteCacheState(), Diagnostics = device.GetDiagnostics() });
            return
            [
                new("paging-read-progress/flush-fence-page-in", "PASS", $"A new native page-in completed in {seconds:F3}s while the explicit flush was held and the application flush stayed queued; the mapped 64 KiB was exact."),
                new("paging-read-progress/durable-write", "PASS", "The held write reached the disk byte-exact after release; the read did not acknowledge or complete the explicit flush.")
            ];
        }
        finally
        {
            try
            {
                await Task.WhenAll(new Task?[] { read, applicationFlush, flush }.OfType<Task>())
                    .WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally { device.Control(WriteCacheAction.LabGate, value: 0); }
            RunStorage.AtomicJson(evidence + ".joined.json", new
            {
                Diagnostics = device.GetDiagnostics(), Performance = device.GetPerformance(),
                FlushCompleted = flush?.IsCompleted, ApplicationFlushCompleted = applicationFlush?.IsCompleted,
                ReadCompleted = read?.IsCompleted
            });
        }
    }
}
