using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

public sealed record FlushOrderCostMeasurement(FlushOrderCostCase Case, DateTimeOffset Start,
    DateTimeOffset ScoreEnd, DateTimeOffset DurableEnd, long Bytes, int Requests,
    double ApplicationSeconds, double DurableSeconds, WriteCacheState Before, WriteCacheState After,
    CacheDiagnostics DiagnosticsBefore, CacheDiagnostics DiagnosticsAfter,
    CachePerformance PerformanceBefore, CachePerformance PerformanceApplication, CachePerformance PerformanceAfter);

/// <summary>A small live-driver cost screen: RAM acknowledgement and complete durable tail are separate.
/// This does not replace DiskSpd or the release-performance matrix.</summary>
[SupportedOSPlatform("windows")]
public static class FlushOrderCostScenario
{
    private const int MiB = 1 << 20;
    public static async Task<IReadOnlyList<CheckResult>> RunAsync(DiskTarget target, CacheDevice device,
        string work, string evidence, int repeats, int budgetMiB, bool writeThroughArm = false)
    {
        var plan = FlushOrderCostPlan.Cases(repeats, writeThroughArm);
        if (budgetMiB != FlushOrderCostPlan.BudgetMiB)
            throw new IOException($"Ordering cost requires --budget-mib {FlushOrderCostPlan.BudgetMiB}.");
        // Room for the sequential file plus 1 GiB.
        await RequireMeasurementConditions(target, device, work, evidence, (FlushOrderCostPlan.SequentialMiB + 1024L) * MiB);
        var original = device.GetDiagnostics().FlushOrder?.Mode ?? throw new NotSupportedException("Diagnostics V26 required.");
        if (writeThroughArm)
        {
            // Lab mode 2 is refused unless the lab disk reports write-through (FUA) support: find out before measuring.
            Reset(device);
            try { device.Control(WriteCacheAction.FlushOrder, value: 2); }
            catch (Win32Exception) { }
            var available = device.GetDiagnostics().WriteThroughGroups?.Available == 1;
            device.Control(WriteCacheAction.FlushOrder, value: original);
            if (!available)
                throw new NotSupportedException("The write-through arm needs a lab volume whose disk reports write-through (FUA) support.");
        }
        var originalTiming = device.GetPerformance().TimingEnabled;
        Directory.CreateDirectory(work);
        RunStorage.AtomicJson(evidence + ".plan.json", plan);
        var checks = new List<CheckResult>();
        try
        {
            // Every arm records lock, selection and wake-up costs (plan 195); the counters add a few QPC reads.
            device.Control(WriteCacheAction.PerformanceTiming, value: 1);
            foreach (var item in plan)
            {
                Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] {item.Id}: preparing equal work");
                Reset(device);
                var directory = Path.Combine(work, item.Id); Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "data.bin");
                var data = ConcurrentCacheOracle.Pattern(215, 1, MiB);
                var isWriteThrough = item.Workload == "write-through-pairs";
                if (item.Workload != "flush-heavy-files")
                {
                    var sizeMiB = isWriteThrough ? FlushOrderCostPlan.WriteThroughMiB : FlushOrderCostPlan.SequentialMiB;
                    using var prepare = new AlignedFile(path, MiB, create: true);
                    prepare.Reserve((long)sizeMiB * MiB);
                    var zeros = new byte[MiB];
                    for (var i = 0; i < sizeMiB; i++)
                    {
                        Stamp(data, i);
                        prepare.Write((long)i * MiB, zeros);
                    }
                    prepare.Flush();
                }
                device.Control(WriteCacheAction.FlushOrder, value: item.Mode);
                ConfigurationManager.Apply(target, new CacheConfiguration(budgetMiB, CachePreset.Fast)
                {
                    Options = new(Drain: DrainAlgorithm.Eager, BatchKiB: 1024, Parallelism: 4,
                        RetainWrites: true, PromoteOnRead: true)
                }, true);
                var smallData = data.AsSpan(0, 4096).ToArray();
                WriteCacheState before; CacheDiagnostics diagBefore; CachePerformance perfBefore, perfApplication;
                DateTimeOffset started, scoreEnd; double applicationSeconds;
                var timer = new Stopwatch(); long bytes = 0; var requests = 0;
                using (var file = item.Workload == "flush-heavy-files" ? null : new AlignedFile(path, MiB, create: false, shareAll: true))
                using (var writeThrough = isWriteThrough
                    ? new AlignedFile(path, 4096, create: false, shareAll: true, writeThrough: true) : null)
                {
                    before = device.GetWriteCacheState(); diagBefore = device.GetDiagnostics(); perfBefore = device.GetPerformance();
                    if (perfBefore.TimingEnabled != 1) throw new IOException("Ordering cost timing was not enabled.");
                    started = DateTimeOffset.UtcNow; timer.Start();
                    if (item.Workload == "sequential-fit")
                    {
                        for (var i = 0; i < FlushOrderCostPlan.SequentialMiB; i++)
                        {
                            Stamp(data, i);
                            file!.Write((long)i * MiB, data); bytes += MiB; requests++;
                        }
                    }
                    else if (item.Workload == "flush-heavy-files")
                    {
                        for (var i = 0; i < FlushOrderCostPlan.SmallFiles; i++)
                        {
                            using var small = new FileStream(Path.Combine(directory, $"file-{i:D5}.bin"), FileMode.CreateNew,
                                FileAccess.Write, FileShare.None, 4096);
                            Stamp(data, i); small.Write(data, 0, 4096); small.Flush(true); bytes += 4096; requests++;
                        }
                    }
                    else
                    {
                        for (var i = 0; i < FlushOrderCostPlan.WriteThroughPairs * 2; i++)
                        {
                            Stamp(smallData, i);
                            (i % 2 == 0 ? file! : writeThrough!).Write((long)i * 4096, smallData);
                            bytes += 4096; requests++;
                        }
                    }
                    applicationSeconds = timer.Elapsed.TotalSeconds; scoreEnd = DateTimeOffset.UtcNow;
                    perfApplication = device.GetPerformance();
                } // Last-close metadata belongs to the running durable-tail timer.
                // Include filesystem metadata that has not reached our volume
                // cache yet; flushing only QueueCache would undercount the tail.
                var checkpoint = DurableCheckpoint.Complete(target.Letter, device, evidence + "." + item.Id + ".checkpoint.json");
                var durableSeconds = timer.Elapsed.TotalSeconds; var durableEnd = DateTimeOffset.UtcNow;
                var after = checkpoint.Final.After; var diagAfter = checkpoint.Final.DiagnosticsAfter; var perfAfter = device.GetPerformance();
                RunStorage.AtomicJson(evidence + "." + item.Id + ".checkpoint.json", checkpoint);
                if (after.LastError != 0 || after.Errors != before.Errors || after.DirtyBytes != 0 || after.InFlightBytes != 0 ||
                    diagAfter.FlushOrder?.Mode != item.Mode || diagBefore.FlushOrder?.Mode != item.Mode)
                    throw new IOException("Ordering cost ended unhealthy, dirty or in the wrong mode.");
                if (isWriteThrough && diagAfter.WriteThroughWrites < diagBefore.WriteThroughWrites + FlushOrderCostPlan.WriteThroughPairs)
                    throw new IOException("The paired workload did not exercise its write-through requests.");
                RunStorage.AtomicJson(evidence + "." + item.Id + ".json", new FlushOrderCostMeasurement(item,
                    started, scoreEnd, durableEnd, bytes, requests, applicationSeconds, durableSeconds, before, after, diagBefore, diagAfter,
                    perfBefore, perfApplication, perfAfter));
                // Its application time is RAM admission only while the whole file fits.
                if (item.Workload == "sequential-fit" && after.ThrottleWaits != before.ThrottleWaits)
                    throw new IOException("The sequential file waited for cache space; it no longer fits the cache.");
                Reset(device);
                // Check the lower device with unbuffered reads after cache release.
                if (item.Workload == "flush-heavy-files")
                {
                    for (var i = 0; i < FlushOrderCostPlan.SmallFiles; i++)
                    {
                        using var small = new AlignedFile(Path.Combine(directory, $"file-{i:D5}.bin"), 4096, create: false);
                        Stamp(data, i); Check(small, 0, data.AsSpan(0, 4096).ToArray());
                    }
                }
                else
                {
                    using var verify = new AlignedFile(path, MiB, create: false, sharedReadOnly: true);
                    if (isWriteThrough)
                        for (var i = 0; i < FlushOrderCostPlan.WriteThroughPairs * 2; i++)
                        { Stamp(smallData, i); Check(verify, (long)i * 4096, smallData); }
                    else
                        for (var i = 0; i < FlushOrderCostPlan.SequentialMiB; i++)
                        { Stamp(data, i); Check(verify, (long)i * MiB, data); }
                }
                // The lab volume holds one case's files at a time; the cache is released here.
                Directory.Delete(directory, true);
                checks.Add(new(item.Id, "PASS", $"Measured {bytes} bytes in {applicationSeconds:F4} s application / {durableSeconds:F4} s including durable tail; lower-device bytes verified. This is collection success, not a speed verdict."));
                Console.WriteLine($"{item.Id}: {checks[^1].Detail}");
                await Task.Yield();
            }
        }
        finally
        {
            Reset(device); device.Control(WriteCacheAction.FlushOrder, value: original);
            device.Control(WriteCacheAction.PerformanceTiming, value: originalTiming);
            if (device.GetDiagnostics().FlushOrder?.Mode != original || device.GetPerformance().TimingEnabled != originalTiming)
                throw new IOException("Ordering cost mode or timing restoration failed.");
        }
        FlushOrderCostPlan.ValidateChecks(plan, checks);
        return checks;
    }
    // Shared by the ordering performance suites: a fresh owned directory on a non-OS NTFS
    // volume with room for the work, Verifier off, 15 minutes after startup, normal priority.
    internal static async Task RequireMeasurementConditions(DiskTarget target, CacheDevice device,
        string work, string evidence, long freeBytes)
    {
        target.ValidateCurrent();
        if (target.IsBoot || target.IsSystem || target.IsPaging ||
            !new DriveInfo(target.Root).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase) ||
            device.GetStatistics().PagingPathCount != 0 || Directory.Exists(work) ||
            !string.Equals(Path.GetDirectoryName(Path.GetFullPath(work)), target.Root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Ordering performance requires a fresh owned non-OS NTFS directory.");
        if (new DriveInfo(target.Root).AvailableFreeSpace < freeBytes)
            throw new IOException($"Ordering performance needs {freeBytes / MiB} MiB free on the lab volume.");
        await RequireRuntimeConditions(evidence);
    }

    internal static async Task RequireRuntimeConditions(string evidence)
    {
        var verifier = await OwnedProcess.RunAsync(Path.Combine(Environment.SystemDirectory, "verifier.exe"),
            ["/query"], evidence + ".verifier", TimeSpan.FromSeconds(30), CancellationToken.None);
        if (!(verifier.Output + verifier.Error).Contains("No drivers are currently verified.", StringComparison.Ordinal))
            throw new IOException("Could not establish that Driver Verifier is off; inspect the retained /query output.");
        if (Environment.TickCount64 < TimeSpan.FromMinutes(15).TotalMilliseconds)
            throw new IOException("Ordering performance requires at least 15 minutes after Windows startup.");
        if (Process.GetCurrentProcess().PriorityClass != ProcessPriorityClass.Normal)
            throw new IOException("Ordering performance requires normal process priority.");
    }
    internal static void Reset(CacheDevice device)
    { device.Control(WriteCacheAction.Disable); device.Control(WriteCacheAction.Release); }
    private static void Stamp(byte[] bytes, int index) => BinaryPrimitives.WriteInt64LittleEndian(bytes, index);
    private static void Check(AlignedFile file, long offset, byte[] expected)
    {
        var actual = new byte[expected.Length]; file.Read(offset, actual);
        if (!actual.AsSpan().SequenceEqual(expected)) throw new IOException($"Ordering cost byte mismatch at {offset}.");
    }
}
