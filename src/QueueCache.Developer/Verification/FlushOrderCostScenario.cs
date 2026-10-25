using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.Versioning;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

public sealed record FlushOrderCostMeasurement(FlushOrderCostCase Case, DateTimeOffset Start,
    DateTimeOffset ScoreEnd, DateTimeOffset DurableEnd, long Bytes, int Requests,
    double ApplicationSeconds, double DurableSeconds, WriteCacheState Before, WriteCacheState After,
    CacheDiagnostics DiagnosticsBefore, CacheDiagnostics DiagnosticsAfter);

/// <summary>A small live-driver cost screen: RAM acknowledgement and complete durable tail are separate.
/// This does not replace DiskSpd or the release-performance matrix.</summary>
[SupportedOSPlatform("windows")]
public static class FlushOrderCostScenario
{
    private const int MiB = 1 << 20;
    public static async Task<IReadOnlyList<CheckResult>> RunAsync(DiskTarget target, CacheDevice device,
        string work, string evidence, int repeats, int budgetMiB)
    {
        var plan = FlushOrderCostPlan.Cases(repeats);
        target.ValidateCurrent();
        if (budgetMiB != FlushOrderCostPlan.BudgetMiB || target.IsBoot || target.IsSystem || target.IsPaging ||
            !new DriveInfo(target.Root).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase) ||
            device.GetStatistics().PagingPathCount != 0 || Directory.Exists(work) ||
            !string.Equals(Path.GetDirectoryName(Path.GetFullPath(work)), target.Root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Ordering cost requires a fresh owned non-OS NTFS directory and --budget-mib 256.");
        var verifier = await OwnedProcess.RunAsync(Path.Combine(Environment.SystemDirectory, "verifier.exe"),
            ["/query"], evidence + ".verifier", TimeSpan.FromSeconds(30), CancellationToken.None);
        if (!(verifier.Output + verifier.Error).Contains("No drivers are currently verified.", StringComparison.Ordinal))
            throw new IOException("Could not establish that Driver Verifier is off; inspect the retained /query output.");
        if (Environment.TickCount64 < TimeSpan.FromMinutes(15).TotalMilliseconds)
            throw new IOException("Ordering performance requires at least 15 minutes after Windows startup.");
        if (Process.GetCurrentProcess().PriorityClass != ProcessPriorityClass.Normal)
            throw new IOException("Ordering performance requires normal process priority.");
        var original = device.GetDiagnostics().FlushOrder?.Mode ?? throw new NotSupportedException("Diagnostics V26 required.");
        Directory.CreateDirectory(work);
        RunStorage.AtomicJson(evidence + ".plan.json", plan);
        var checks = new List<CheckResult>();
        try
        {
            foreach (var item in plan)
            {
                Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] {item.Id}: preparing equal work");
                Reset(device);
                var directory = Path.Combine(work, item.Id); Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "data.bin");
                var data = ConcurrentCacheOracle.Pattern(215, 1, MiB);
                var isRead = item.Workload == "resident-read";
                if (item.Workload != "flush-heavy-files")
                {
                    var sizeMiB = isRead ? FlushOrderCostPlan.ResidentMiB : FlushOrderCostPlan.SequentialMiB;
                    using var prepare = new AlignedFile(path, MiB, create: true);
                    prepare.Reserve((long)sizeMiB * MiB);
                    var zeros = new byte[MiB];
                    for (var i = 0; i < sizeMiB; i++)
                    {
                        Stamp(data, i);
                        prepare.Write((long)i * MiB, isRead ? data : zeros);
                    }
                    prepare.Flush();
                }
                device.Control(WriteCacheAction.LabFlushOrder, value: item.Mode);
                ConfigurationManager.Apply(target, new CacheConfiguration(budgetMiB, CachePreset.Fast)
                {
                    Options = new(Drain: DrainAlgorithm.Eager, BatchKiB: 1024, Parallelism: 4,
                        RetainWrites: true, PromoteOnRead: true)
                }, true);
                using var file = item.Workload == "flush-heavy-files" ? null : new AlignedFile(path, MiB, create: false, shareAll: true);
                if (isRead)
                {
                    for (var i = 0; i < FlushOrderCostPlan.ResidentMiB; i++) file!.Read((long)i * MiB, data);
                    device.Control(WriteCacheAction.Flush);
                }
                var before = device.GetWriteCacheState(); var diagBefore = device.GetDiagnostics();
                var started = DateTimeOffset.UtcNow; var timer = Stopwatch.StartNew();
                long bytes = 0; var requests = 0;
                if (item.Workload == "sequential-write")
                {
                    for (var i = 0; i < FlushOrderCostPlan.SequentialMiB; i++)
                    { Stamp(data, i); file!.Write((long)i * MiB, data); bytes += MiB; requests++; }
                }
                else if (item.Workload == "flush-heavy-files")
                {
                    for (var i = 0; i < FlushOrderCostPlan.SmallFiles; i++)
                    {
                        using var small = new FileStream(Path.Combine(directory, $"file-{i:D4}.bin"), FileMode.CreateNew,
                            FileAccess.Write, FileShare.None, 4096);
                        Stamp(data, i); small.Write(data, 0, 4096); small.Flush(true); bytes += 4096; requests++;
                    }
                }
                else
                {
                    for (var pass = 0; pass < FlushOrderCostPlan.ResidentPasses; pass++)
                        for (var i = 0; i < FlushOrderCostPlan.ResidentMiB; i++)
                        { file!.Read((long)i * MiB, data); bytes += MiB; requests++; }
                }
                var applicationSeconds = timer.Elapsed.TotalSeconds; var scoreEnd = DateTimeOffset.UtcNow;
                // Include filesystem metadata that has not reached our volume
                // cache yet; flushing only QueueCache would undercount the tail.
                var checkpoint = DurableCheckpoint.Complete(target.Letter, device, evidence + "." + item.Id + ".checkpoint.json");
                var durableSeconds = timer.Elapsed.TotalSeconds; var durableEnd = DateTimeOffset.UtcNow;
                var after = checkpoint.Final.After; var diagAfter = checkpoint.Final.DiagnosticsAfter;
                RunStorage.AtomicJson(evidence + "." + item.Id + ".checkpoint.json", checkpoint);
                if (after.LastError != 0 || after.Errors != before.Errors || after.DirtyBytes != 0 || after.InFlightBytes != 0 ||
                    diagAfter.FlushOrder?.Mode != item.Mode || diagBefore.FlushOrder?.Mode != item.Mode)
                    throw new IOException("Ordering cost ended unhealthy, dirty or in the wrong mode.");
                if (isRead && (diagBefore.Attribution is not { } a || diagAfter.Attribution is not { } b ||
                    b.LowerReadAttempts != a.LowerReadAttempts || after.CacheReadBytes - before.CacheReadBytes != (ulong)bytes))
                    throw new IOException("Resident read control was not entirely served from RAM.");
                RunStorage.AtomicJson(evidence + "." + item.Id + ".json", new FlushOrderCostMeasurement(item,
                    started, scoreEnd, durableEnd, bytes, requests, applicationSeconds, durableSeconds, before, after, diagBefore, diagAfter));
                Reset(device);
                // Check the lower device with unbuffered reads after cache release.
                if (item.Workload == "flush-heavy-files")
                {
                    for (var i = 0; i < FlushOrderCostPlan.SmallFiles; i++)
                    {
                        using var small = new AlignedFile(Path.Combine(directory, $"file-{i:D4}.bin"), 4096, create: false);
                        Stamp(data, i); Check(small, 0, data.AsSpan(0, 4096).ToArray());
                    }
                }
                else
                    for (var i = 0; i < (isRead ? FlushOrderCostPlan.ResidentMiB : FlushOrderCostPlan.SequentialMiB); i++)
                    { Stamp(data, i); Check(file!, (long)i * MiB, data); }
                checks.Add(new(item.Id, "PASS", $"Measured {bytes} bytes in {applicationSeconds:F4} s application / {durableSeconds:F4} s including durable tail; lower-device bytes verified. This is collection success, not a speed verdict."));
                Console.WriteLine($"{item.Id}: {checks[^1].Detail}");
                await Task.Yield();
            }
        }
        finally
        {
            Reset(device); device.Control(WriteCacheAction.LabFlushOrder, value: original);
            if (device.GetDiagnostics().FlushOrder?.Mode != original) throw new IOException("Ordering cost mode restoration failed.");
        }
        FlushOrderCostPlan.ValidateChecks(plan, checks);
        return checks;
    }
    private static void Reset(CacheDevice device)
    { device.Control(WriteCacheAction.Disable); device.Control(WriteCacheAction.Release); }
    private static void Stamp(byte[] bytes, int index) => BinaryPrimitives.WriteInt64LittleEndian(bytes, index);
    private static void Check(AlignedFile file, long offset, byte[] expected)
    {
        var actual = new byte[expected.Length]; file.Read(offset, actual);
        if (!actual.AsSpan().SequenceEqual(expected)) throw new IOException($"Ordering cost byte mismatch at {offset}.");
    }
}
