using System.Diagnostics;
using System.Runtime.Versioning;
using QueueCache.Management;

namespace QueueCache.Operations;

/// <summary>Explicit, current-boot policy scenarios. Only fresh files are written;
/// runtime settings are restored, saved profiles are never modified.</summary>
[SupportedOSPlatform("windows")]
public static class CacheScenarios
{
    public static Task<IReadOnlyList<CheckResult>> RunAsync(DiskTarget target, IProgress<string>? progress = null,
        CancellationToken token = default) => Task.Run<IReadOnlyList<CheckResult>>(() =>
    {
        using var gate = ConfigurationGate.Enter();
        using var device = new CacheDevice(target.Device, writable: true);
        var original = device.GetWriteCacheState();
        ConfigurationManager.EnsureHealthy(original);
        if (!original.SupportsReadWrite)
            throw new NotSupportedException("Read/write driver required.");
        var directory = Path.Combine(target.Root, "QueueCache-Scenarios-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var results = new List<CheckResult>();
        CacheOptions[] cases = [new(), new(CacheAllocation.Fixed, 50, Drain: DrainAlgorithm.Balanced, Parallelism: 2),
            new(CacheAllocation.Fixed, 50, PromoteOnRead: false, Drain: DrainAlgorithm.Idle, Parallelism: 4),
            new(CacheAllocation.Fixed, 100, RetainWrites: false), new(CacheAllocation.Fixed, 0), new(Parallelism: 4, BatchKiB: 1024)];
        try
        {
            SectorScenarios.Run(target, device, directory, results, progress, token);
            RunSustainedForeground(target, device, directory, results, progress, token);
            for (int scenario = 0; scenario < cases.Length; scenario++)
            {
                token.ThrowIfCancellationRequested();
                var options = cases[scenario];
                var label = $"{scenario + 1}: {options.Allocation}/{options.WritePercent}%/{options.Drain}/{options.Parallelism} drains";
                progress?.Report(label);
                ConfigurationManager.Apply(target, new CacheConfiguration(64, scenario == 4 ? CachePreset.Strict : CachePreset.Fast) { Options = options }, true);
                var file = Path.Combine(directory, $"scenario-{scenario + 1}.bin");
                var expected = new byte[8 << 20];
                new Random(173 + scenario).NextBytes(expected);
                var actual = new byte[expected.Length];
                using (var data = new AlignedFile(file, expected.Length, create: true))
                {
                    data.Write(0, expected);
                    data.Read(0, actual);
                    if (!expected.AsSpan().SequenceEqual(actual))
                        throw new IOException(label + ": live read mismatch");
                    // Rapid replacements exercise dirty/in-flight version ownership.
                    for (int overwrite = 0; overwrite < 8; overwrite++)
                    {
                        token.ThrowIfCancellationRequested();
                        expected[overwrite] ^= 0x5A;
                        data.Write(0, expected);
                    }
                    device.Control(WriteCacheAction.Flush);
                    data.Read(0, actual);
                    var first = device.GetWriteCacheState();
                    data.Read(0, actual);
                    var second = device.GetWriteCacheState();
                    if (!expected.AsSpan().SequenceEqual(actual))
                        throw new IOException(label + ": warm read mismatch");
                    bool cacheReads = options.Allocation == CacheAllocation.Automatic || options.WritePercent < 100 || options.RetainWrites;
                    if (cacheReads && second.ReadHitBytes - first.ReadHitBytes < (ulong)expected.Length)
                        throw new IOException(label + ": warm read did not hit RAM");
                    results.Add(new(label + "/warm-read", "PASS", $"Hit delta {second.ReadHitBytes - first.ReadHitBytes} bytes; contents verified."));
                    if (cacheReads)
                    {
                        // A separate tiny file and inventory queries must not evict this hot file.
                        // The retained payload is much smaller than either configured pool.
                        using (var metadata = new FileStream(Path.Combine(directory, $"tiny-{scenario}.bin"), FileMode.CreateNew, FileAccess.Write))
                        {
                            metadata.Write(new byte[512]);
                            metadata.Flush(true);
                        }
                        var afterMetadata = device.GetWriteCacheState();
                        var metadataBarrier = device.GetDiagnostics().LastBarrierCode;
                        _ = DiskCatalog.ListAsync().GetAwaiter().GetResult();
                        var before = device.GetWriteCacheState();
                        data.Read(0, actual);
                        var after = device.GetWriteCacheState();
                        if (!expected.AsSpan().SequenceEqual(actual))
                            throw new IOException(label + ": retained read mismatch");
                        if (after.ReadHitBytes - before.ReadHitBytes < (ulong)expected.Length)
                            throw new IOException(label + $": unrelated metadata/discovery evicted hot read data; clean after metadata={afterMetadata.CleanReadBytes + afterMetadata.CleanWriteBytes}, barrier=0x{metadataBarrier:X}; after discovery={before.CleanReadBytes + before.CleanWriteBytes}, barrier=0x{device.GetDiagnostics().LastBarrierCode:X}; hit={after.ReadHitBytes - before.ReadHitBytes}");
                        results.Add(new(label + "/retention", "PASS", "Hot data survives unrelated small-file writes and disk discovery."));
                    }
                    device.Control(WriteCacheAction.Disable);
                    // Disabled routing + unbuffered read is an independent lower-disk oracle.
                    data.Read(0, actual);
                    if (!expected.AsSpan().SequenceEqual(actual))
                        throw new IOException(label + ": disabled-cache disk read mismatch");
                    var state = device.GetWriteCacheState();
                    if (state.Enabled || state.DirtyBytes != 0 || state.InFlightBytes != 0 || state.LastError != 0 || state.Errors != original.Errors)
                        throw new IOException(label + ": drain/state mismatch");
                    results.Add(new(label + "/disk-oracle", "PASS", "All bytes matched with caching disabled, no pending writes/errors."));
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { results.Add(new("scenario", "FAIL", ex.Message)); }
        finally
        {
            device.Control(WriteCacheAction.LabDelay, value: 0);
            progress?.Report("Restoring original runtime configuration; retained files: " + directory);
            // Report restoration failure rather than pretending the previous cache is active.
            if (original.BudgetBytes == 0)
                device.Control(WriteCacheAction.Release);
            else
                ConfigurationManager.Apply(target, CacheConfiguration.FromState(original), true);
        }
        return results;
    }, token);

    private static void RunSustainedForeground(DiskTarget target, CacheDevice device, string directory,
        List<CheckResult> results, IProgress<string>? progress, CancellationToken token)
    {
        const int budgetMiB = 64;
        const int hotSetBytes = 8 << 20;
        const int transferBytes = 64 << 10;
        const int durationSeconds = 60;
        var label = "foreground-background";
        progress?.Report($"{label}: {durationSeconds}s fitting writes and cached reads while Idle drains");

        var blocks = Enumerable.Range(0, hotSetBytes / transferBytes).Select(index =>
        {
            var block = new byte[transferBytes];
            new Random(8100 + index).NextBytes(block);
            return block;
        }).ToArray();
        var actual = new byte[transferBytes];
        using var file = new AlignedFile(Path.Combine(directory, "foreground-background.bin"), transferBytes, true);
        for (var index = 0; index < blocks.Length; index++)
            file.Write((long)index * transferBytes, blocks[index]);
        device.Control(WriteCacheAction.Flush);
        device.Control(WriteCacheAction.DropClean);

        var options = new CacheOptions(Drain: DrainAlgorithm.Idle, MaxDirtyAgeMs: 5000, IdleMs: 250,
            LowPercent: 40, HighPercent: 80, BatchKiB: 256, Parallelism: 1);
        ConfigurationManager.Apply(target, new CacheConfiguration(budgetMiB, CachePreset.Fast) { Options = options }, true);
        var before = device.GetWriteCacheState();
        var diagnosticsBefore = device.GetDiagnostics();
        var attemptsBefore = diagnosticsBefore.Attribution ??
            throw new NotSupportedException("Sustained admission proof requires diagnostics V2.");
        var writeMilliseconds = new List<double>();
        var readMilliseconds = new List<double>();
        ulong readBytes = 0;
        ulong peakDirty = before.DirtyBytes;
        var sequence = 0;

        void WriteAndRead()
        {
            var index = sequence % blocks.Length;
            var block = blocks[index];
            block[sequence % block.Length] ^= (byte)(0x5A + sequence % 31);
            var started = Stopwatch.GetTimestamp();
            file.Write((long)index * transferBytes, block);
            writeMilliseconds.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            started = Stopwatch.GetTimestamp();
            file.Read((long)index * transferBytes, actual);
            readMilliseconds.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (!block.AsSpan().SequenceEqual(actual))
                throw new IOException(label + ": cached read mismatch");
            readBytes += transferBytes;
            sequence++;
        }

        // At a clean boundary, one fitting write and its cached read must not issue lower I/O.
        WriteAndRead();
        var diagnosticsAfterAdmission = device.GetDiagnostics();
        var attemptsAfterAdmission = diagnosticsAfterAdmission.Attribution;
        var admissionEvidence = SectorScenarios.VerifyAdmissionAttempts(diagnosticsBefore, diagnosticsAfterAdmission);

        var timer = Stopwatch.StartNew();
        var nextSample = TimeSpan.Zero;
        while (timer.Elapsed < TimeSpan.FromSeconds(durationSeconds))
        {
            token.ThrowIfCancellationRequested();
            WriteAndRead();
            if (timer.Elapsed >= nextSample)
            {
                var sample = device.GetWriteCacheState();
                peakDirty = Math.Max(peakDirty, sample.DirtyBytes);
                nextSample = timer.Elapsed + TimeSpan.FromSeconds(1);
            }
        }

        var after = device.GetWriteCacheState();
        var attemptsAfter = device.GetDiagnostics().Attribution!;
        if (after.AcceptedBytes <= before.AcceptedBytes || after.DrainedBytes <= before.DrainedBytes ||
            attemptsAfter.LowerWriteAttempts <= attemptsAfterAdmission!.LowerWriteAttempts)
            throw new IOException(label + ": foreground or background made no measurable progress");
        if (after.ReadHitBytes - before.ReadHitBytes < readBytes || attemptsAfter.LowerReadAttempts != attemptsBefore.LowerReadAttempts)
            throw new IOException(label + ": a known-current cached read missed RAM");
        if (after.ThrottleWaits != before.ThrottleWaits)
            throw new IOException(label + ": fitting foreground writes waited for capacity");
        if (peakDirty >= before.PayloadCapacity || after.LastError != 0 || after.Errors != before.Errors)
            throw new IOException(label + ": capacity or driver error invariant failed");

        device.Control(WriteCacheAction.Disable);
        for (var index = 0; index < blocks.Length; index++)
        {
            file.Read((long)index * transferBytes, actual);
            if (!blocks[index].AsSpan().SequenceEqual(actual))
                throw new IOException(label + ": persisted disk oracle mismatch");
        }
        var final = device.GetWriteCacheState();
        if (final.DirtyBytes != 0 || final.InFlightBytes != 0 || final.LastError != 0 || final.Errors != before.Errors)
            throw new IOException(label + ": final drain/state mismatch");

        static double Percentile99(List<double> samples)
        {
            samples.Sort();
            return samples[Math.Min(samples.Count - 1, (int)Math.Ceiling(samples.Count * 0.99) - 1)];
        }
        results.Add(new(label, "PASS",
            $"{sequence} serialized 64 KiB write/read pairs over {durationSeconds}s; write p99/max {Percentile99(writeMilliseconds):F3}/{writeMilliseconds.Max():F3} ms; " +
            $"read p99/max {Percentile99(readMilliseconds):F3}/{readMilliseconds.Max():F3} ms; accepted/drained deltas " +
            $"{after.AcceptedBytes - before.AcceptedBytes}/{after.DrainedBytes - before.DrainedBytes} bytes; read-hit delta " +
            $"{after.ReadHitBytes - before.ReadHitBytes} bytes; capacity-wait delta {after.ThrottleWaits - before.ThrottleWaits}; " +
            $"peak dirty {peakDirty}/{before.PayloadCapacity} bytes; lower-write attempts delta " +
            $"{attemptsAfter.LowerWriteAttempts - attemptsAfterAdmission.LowerWriteAttempts}. Persisted bytes verified after Disable."));
        results.Add(SectorScenarios.AdmissionCheck(label + "/first-fitting-write-zero-lower-io", admissionEvidence));
    }
}
