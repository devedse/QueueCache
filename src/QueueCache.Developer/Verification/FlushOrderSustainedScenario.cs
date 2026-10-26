using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

/// <summary>One per-second observation: application progress (files, or -1 while a tool runs) and cache state.</summary>
public sealed record FlushOrderSustainedSample(double Seconds, long Files, ulong DirtyBytes, ulong InFlightBytes,
    ulong AcceptedBytes, ulong DrainedBytes, ulong ThrottleWaits, ulong Seals, ulong GroupFlushes);

public sealed record FlushOrderSustainedMeasurement(FlushOrderSustainedCase Case, DateTimeOffset Start,
    DateTimeOffset ScoreEnd, DateTimeOffset DurableEnd, long Files, long Bytes, double ApplicationSeconds,
    double DurableSeconds, int? ToolExitCode, long VerifiedFiles, WriteCacheState Before, WriteCacheState After,
    CacheDiagnostics DiagnosticsBefore, CacheDiagnostics DiagnosticsAfter, CachePerformance PerformanceBefore,
    CachePerformance PerformanceApplication, CachePerformance PerformanceAfter,
    IReadOnlyList<FlushOrderSustainedSample> Samples);

/// <summary>Sustained ordering use on the lab volume: time into the cache, cache fill and time to disk
/// per arm. Not a release verdict; the file run is time-bounded and its byte check is sampled.</summary>
[SupportedOSPlatform("windows")]
public static class FlushOrderSustainedScenario
{
    private const int KiB = 1024, MiB = 1 << 20;
    private static readonly byte[] TreeBase = ConcurrentCacheOracle.Pattern(220, 1, FlushOrderSustainedPlan.TreeFileKiB.Max() * KiB);

    public static async Task<IReadOnlyList<CheckResult>> RunAsync(DiskTarget target, CacheDevice device,
        string work, string evidence, int repeats, int budgetMiB, bool writeThroughArm = false)
    {
        var plan = FlushOrderSustainedPlan.Cases(repeats, writeThroughArm);
        if (budgetMiB != FlushOrderSustainedPlan.BudgetMiB)
            throw new IOException($"Sustained ordering requires --budget-mib {FlushOrderSustainedPlan.BudgetMiB}.");
        var tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
        var robocopy = Path.Combine(Environment.SystemDirectory, "robocopy.exe");
        if (!File.Exists(tar) || !File.Exists(robocopy))
            throw new FileNotFoundException("Sustained ordering runs Windows' own tar.exe and robocopy.exe.");
        // The nonstop file run can create about a million 4 KiB files before the case deletes them.
        await FlushOrderCostScenario.RequireMeasurementConditions(target, device, work, evidence, 6L << 30);
        var original = device.GetDiagnostics().FlushOrder?.Mode ?? throw new NotSupportedException("Diagnostics V26 required.");
        if (writeThroughArm)
        {
            FlushOrderCostScenario.Reset(device);
            var available = false;
            try
            {
                try { device.Control(WriteCacheAction.FlushOrder, value: 2); }
                catch (Win32Exception) { }
                available = device.GetDiagnostics().WriteThroughGroups?.Available == 1;
            }
            finally { device.Control(WriteCacheAction.FlushOrder, value: original); }
            if (!available)
                throw new NotSupportedException("The sustained write-through arm needs a lab volume whose disk reports FUA support.");
        }
        var originalTiming = device.GetPerformance().TimingEnabled;
        Directory.CreateDirectory(work);
        RunStorage.AtomicJson(evidence + ".plan.json", plan);
        var checks = new List<CheckResult>();
        try
        {
            device.Control(WriteCacheAction.PerformanceTiming, value: 1);
            foreach (var item in plan)
            {
                Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] {item.Id}: preparing");
                FlushOrderCostScenario.Reset(device);
                var directory = Path.Combine(work, item.Id); Directory.CreateDirectory(directory);
                var source = Path.Combine(directory, "source");
                var archive = Path.Combine(directory, "tree.tar");
                var destination = Path.Combine(directory, "copy");
                var tool = item.Workload != "files-5min";
                if (tool)
                {
                    WriteTree(source);
                    if (item.Workload == "tar-extract")
                    {
                        await RunTool(tar, ["-cf", archive, "-C", source, "."], evidence + "." + item.Id + ".prepare", 0);
                        Directory.Delete(source, true);
                    }
                    Directory.CreateDirectory(destination);
                }
                // Preparation reaches the disk before the cache is enabled for the measured phase.
                ComparisonScenarios.FlushVolume(target.Letter);
                device.Control(WriteCacheAction.FlushOrder, value: item.Mode);
                ConfigurationManager.Apply(target, new CacheConfiguration(budgetMiB, CachePreset.Fast)
                {
                    Options = new(Drain: DrainAlgorithm.Eager, BatchKiB: 1024, Parallelism: 4,
                        RetainWrites: true, PromoteOnRead: true)
                }, true);
                var before = device.GetWriteCacheState(); var diagBefore = device.GetDiagnostics(); var perfBefore = device.GetPerformance();
                if (perfBefore.TimingEnabled != 1) throw new IOException("Sustained ordering timing was not enabled.");
                var samples = new List<FlushOrderSustainedSample>();
                var progress = new StrongBox<long>(tool ? -1 : 0);
                int? exitCode = null;
                double applicationSeconds, durableSeconds;
                DateTimeOffset started, scoreEnd, durableEnd;
                CachePerformance perfApplication;
                DurableCheckpointResult checkpoint;
                var timer = Stopwatch.StartNew();
                using (var sampling = new CancellationTokenSource())
                {
                    var sampler = Task.Run(() => Sample(device, timer, progress, samples, sampling.Token));
                    try
                    {
                        started = DateTimeOffset.UtcNow;
                        if (item.Workload == "files-5min")
                            WriteFlushedFiles(directory, timer, progress);
                        else if (item.Workload == "tar-extract")
                            exitCode = await RunTool(tar, ["-xf", archive, "-C", destination], evidence + "." + item.Id + ".tool", 0);
                        else
                            exitCode = await RunTool(robocopy, [source, destination, "/E", "/R:0", "/W:0", "/NP", "/NFL", "/NDL", "/NJH", "/NJS"],
                                evidence + "." + item.Id + ".tool", 1, mustEqual: true);
                        applicationSeconds = timer.Elapsed.TotalSeconds; scoreEnd = DateTimeOffset.UtcNow;
                        perfApplication = device.GetPerformance();
                        // Filesystem metadata that has not reached the cache yet belongs to the tail.
                        checkpoint = DurableCheckpoint.Complete(target.Letter, device, evidence + "." + item.Id + ".checkpoint.json");
                        durableSeconds = timer.Elapsed.TotalSeconds; durableEnd = DateTimeOffset.UtcNow;
                    }
                    finally
                    {
                        sampling.Cancel();
                        await sampler;
                    }
                }
                var after = checkpoint.Final.After; var diagAfter = checkpoint.Final.DiagnosticsAfter; var perfAfter = device.GetPerformance();
                RunStorage.AtomicJson(evidence + "." + item.Id + ".checkpoint.json", checkpoint);
                if (after.LastError != 0 || after.Errors != before.Errors || after.DirtyBytes != 0 || after.InFlightBytes != 0 ||
                    diagAfter.FlushOrder?.Mode != item.Mode || diagBefore.FlushOrder?.Mode != item.Mode)
                    throw new IOException("Sustained ordering ended unhealthy, dirty or in the wrong mode.");
                var files = tool ? FlushOrderSustainedPlan.TreeFiles : progress.Value;
                var bytes = tool ? FlushOrderSustainedPlan.TreeBytes : files * 4 * KiB;
                FlushOrderCostScenario.Reset(device);
                // Unbuffered checks after cache release: every tree file; for the file run, every
                // 64th file and the last thousand, which were the most recently acknowledged.
                var verified = tool ? VerifyTree(destination) : VerifyFlushedFiles(directory, files);
                RunStorage.AtomicJson(evidence + "." + item.Id + ".json", new FlushOrderSustainedMeasurement(item,
                    started, scoreEnd, durableEnd, files, bytes, applicationSeconds, durableSeconds, exitCode, verified,
                    before, after, diagBefore, diagAfter, perfBefore, perfApplication, perfAfter, samples));
                Directory.Delete(directory, true);
                var peak = samples.Max(s => s.DirtyBytes);
                checks.Add(new(item.Id, "PASS", $"{files} files ({bytes / MiB} MiB) in {applicationSeconds:F2} s application / " +
                    $"{durableSeconds:F2} s until on disk; {after.ThrottleWaits - before.ThrottleWaits} waits for cache space, " +
                    $"peak {peak / MiB} MiB unwritten, {diagAfter.FlushOrder!.Seals - diagBefore.FlushOrder!.Seals} bookmarks; " +
                    $"{verified} files verified after release. Collection success, not a speed verdict."));
                Console.WriteLine($"{item.Id}: {checks[^1].Detail}");
            }
        }
        finally
        {
            FlushOrderCostScenario.Reset(device); device.Control(WriteCacheAction.FlushOrder, value: original);
            device.Control(WriteCacheAction.PerformanceTiming, value: originalTiming);
            if (device.GetDiagnostics().FlushOrder?.Mode != original || device.GetPerformance().TimingEnabled != originalTiming)
                throw new IOException("Sustained ordering mode or timing restoration failed.");
        }
        FlushOrderSustainedPlan.ValidateChecks(plan, checks);
        return checks;
    }

    private static async Task Sample(CacheDevice device, Stopwatch timer, StrongBox<long> progress,
        List<FlushOrderSustainedSample> samples, CancellationToken token)
    {
        using var tick = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                var state = device.GetWriteCacheState();
                var order = device.GetDiagnostics().FlushOrder ?? throw new InvalidDataException("Missing ordering diagnostics.");
                lock (samples)
                    samples.Add(new(timer.Elapsed.TotalSeconds, Interlocked.Read(ref progress.Value), state.DirtyBytes,
                        state.InFlightBytes, state.AcceptedBytes, state.DrainedBytes, state.ThrottleWaits, order.Seals, order.Flushes));
            }
            while (await tick.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static void WriteFlushedFiles(string directory, Stopwatch timer, StrongBox<long> progress)
    {
        var data = ConcurrentCacheOracle.Pattern(219, 1, 4 * KiB);
        for (long i = 0; i < FlushOrderSustainedPlan.MaxFiles && timer.Elapsed.TotalSeconds < FlushOrderSustainedPlan.FileSeconds; i++)
        {
            var path = FlushedFile(directory, i);
            if (i % FlushOrderSustainedPlan.FilesPerDirectory == 0) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Stamp(data, i, 0);
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4 * KiB))
            { file.Write(data); file.Flush(true); }
            Interlocked.Increment(ref progress.Value);
        }
    }

    private static long VerifyFlushedFiles(string directory, long files)
    {
        if (files == 0 || File.Exists(FlushedFile(directory, files)))
            throw new IOException("The flushed-file count does not match the files on disk.");
        var expected = ConcurrentCacheOracle.Pattern(219, 1, 4 * KiB); long verified = 0;
        for (long i = 0; i < files; i++)
        {
            if (i % 64 != 0 && i < files - 1000) continue;
            Stamp(expected, i, 0);
            using var file = new AlignedFile(FlushedFile(directory, i), 4 * KiB, create: false, sharedReadOnly: true);
            Check(file, expected);
            verified++;
        }
        return verified;
    }

    private static string FlushedFile(string directory, long i) =>
        Path.Combine(directory, "files", $"d{i / FlushOrderSustainedPlan.FilesPerDirectory:D4}", $"f{i % FlushOrderSustainedPlan.FilesPerDirectory:D3}.bin");
    private static string TreeFile(string root, int i) => Path.Combine(root, $"d{i / 1000:D2}", $"f{i % 1000:D3}.bin");

    private static void WriteTree(string root)
    {
        var buffer = new byte[FlushOrderSustainedPlan.TreeFileKiB.Max() * KiB];
        for (var i = 0; i < FlushOrderSustainedPlan.TreeFiles; i++)
        {
            var path = TreeFile(root, i);
            if (i % 1000 == 0) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, TreeContent(buffer, i).ToArray());
        }
    }

    private static long VerifyTree(string root)
    {
        if (Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count() != FlushOrderSustainedPlan.TreeFiles)
            throw new IOException("The copied tree does not contain exactly the source files.");
        var buffer = new byte[FlushOrderSustainedPlan.TreeFileKiB.Max() * KiB];
        for (var i = 0; i < FlushOrderSustainedPlan.TreeFiles; i++)
        {
            var expected = TreeContent(buffer, i).ToArray();
            using var file = new AlignedFile(TreeFile(root, i), expected.Length, create: false, sharedReadOnly: true);
            Check(file, expected);
        }
        return FlushOrderSustainedPlan.TreeFiles;
    }

    // Shared random bytes, with each file's index and block number at the start of every 4 KiB block.
    private static ReadOnlySpan<byte> TreeContent(byte[] buffer, int i)
    {
        var length = FlushOrderSustainedPlan.TreeFileKiB[i % FlushOrderSustainedPlan.TreeFileKiB.Length] * KiB;
        TreeBase.AsSpan(0, length).CopyTo(buffer);
        for (var block = 0; block < length / (4 * KiB); block++)
            Stamp(buffer.AsSpan(block * 4 * KiB), i, block);
        return buffer.AsSpan(0, length);
    }

    private static void Stamp(Span<byte> bytes, long index, int block)
    {
        BinaryPrimitives.WriteInt64LittleEndian(bytes, index);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], block);
    }

    private static void Check(AlignedFile file, byte[] expected)
    {
        var actual = new byte[expected.Length]; file.Read(0, actual);
        if (!actual.AsSpan().SequenceEqual(expected)) throw new IOException("Sustained ordering byte mismatch.");
    }

    private static async Task<int> RunTool(string executable, string[] arguments, string prefix, int success, bool mustEqual = false)
    {
        var result = await OwnedProcess.RunAsync(executable, arguments, prefix, TimeSpan.FromMinutes(30), CancellationToken.None);
        // robocopy reports 1 when it copied files without mismatches or failures; tar reports 0.
        if (mustEqual ? result.ExitCode != success : result.ExitCode < 0 || result.ExitCode > success)
            throw new IOException($"{Path.GetFileName(executable)} exited with {result.ExitCode}; see {prefix}.stderr.txt.");
        return result.ExitCode;
    }
}
