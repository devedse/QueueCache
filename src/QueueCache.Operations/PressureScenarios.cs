using System.Diagnostics;
using System.Runtime.Versioning;
using QueueCache.Management;

namespace QueueCache.Operations;

/// <summary>
/// Bounded policy-trigger and capacity regressions on new files. The caller owns
/// target validation and process orchestration; this type restores runtime state.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PressureScenarios
{
    private const int BudgetMiB = 64;
    private const int TransferBytes = 64 << 10;

    public static Task<IReadOnlyList<CheckResult>> RunAsync(DiskTarget target,
        IProgress<string>? progress = null, CancellationToken token = default) =>
        Task.Run<IReadOnlyList<CheckResult>>(() =>
        {
            using var gate = ConfigurationGate.Enter();
            using var device = new CacheDevice(target.Device, writable: true);
            var original = device.GetWriteCacheState();
            ConfigurationManager.EnsureHealthy(original);
            if (!original.SupportsReadWrite || !original.SupportsDeferredDrain)
                throw new NotSupportedException("Pressure verification requires the current read/write and deferred-drain protocol.");

            var directory = Path.Combine(target.Root, "QueueCache-Pressure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var results = new List<CheckResult>();
            try
            {
                RunTimedTrigger(target, device, directory, results, progress, token,
                    "trigger/deferred-age", new CacheOptions(RetainWrites: false, Drain: DrainAlgorithm.Deferred,
                        MaxDirtyAgeMs: 1000, IdleMs: 100), TimeSpan.FromMilliseconds(750), TimeSpan.FromSeconds(3));
                RunTimedTrigger(target, device, directory, results, progress, token,
                    "trigger/idle", new CacheOptions(RetainWrites: false, Drain: DrainAlgorithm.Idle,
                        LowPercent: 40, HighPercent: 80, MaxDirtyAgeMs: 5000, IdleMs: 500),
                    TimeSpan.FromMilliseconds(350), TimeSpan.FromSeconds(2));
                RunWatermarkTrigger(target, device, directory, results, progress, token);

                foreach (var test in new[]
                {
                    ("Automatic", new CacheOptions(RetainWrites: false, Drain: DrainAlgorithm.Deferred,
                        MaxDirtyAgeMs: 3600000)),
                    ("Fixed50", new CacheOptions(CacheAllocation.Fixed, 50, RetainWrites: false,
                        Drain: DrainAlgorithm.Deferred, MaxDirtyAgeMs: 3600000)),
                    ("Fixed100", new CacheOptions(CacheAllocation.Fixed, 100, RetainWrites: false,
                        Drain: DrainAlgorithm.Deferred, MaxDirtyAgeMs: 3600000)),
                    ("Fixed0", new CacheOptions(CacheAllocation.Fixed, 0, RetainWrites: false,
                        Drain: DrainAlgorithm.Deferred, MaxDirtyAgeMs: 3600000))
                })
                    RunCapacityCase(target, device, directory, results, progress, token,
                        "capacity/" + test.Item1, test.Item2);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { results.Add(new("pressure", "FAIL", ex.Message)); }
            finally
            {
                device.Control(WriteCacheAction.LabDelay, value: 0);
                progress?.Report("Restoring original runtime configuration; retained files: " + directory);
                if (original.BudgetBytes == 0)
                    device.Control(WriteCacheAction.Release);
                else
                    ConfigurationManager.Apply(target, CacheConfiguration.FromState(original), true);
            }
            return results;
        }, token);

    private static void RunTimedTrigger(DiskTarget target, CacheDevice device, string directory,
        List<CheckResult> results, IProgress<string>? progress, CancellationToken token,
        string label, CacheOptions options, TimeSpan quietWindow, TimeSpan timeout)
    {
        progress?.Report(label);
        var path = Path.Combine(directory, label.Replace('/', '-') + ".bin");
        PrepareFile(path, TransferBytes);
        ConfigurationManager.Apply(target, new CacheConfiguration(BudgetMiB, CachePreset.Fast)
        {
            Options = options
        }, true);
        device.Control(WriteCacheAction.LabDelay, value: 25);
        using var file = new AlignedFile(path, TransferBytes, create: false);
        var expected = Pattern(9100 + results.Count, 0);
        var actual = new byte[TransferBytes];
        var before = device.GetWriteCacheState();
        var attemptsBefore = RequiredAttribution(device, label);
        var timer = Stopwatch.StartNew();
        file.Write(0, expected);
        file.Read(0, actual);
        if (!expected.AsSpan().SequenceEqual(actual))
            throw new IOException(label + ": immediate cached bytes did not match");
        var admission = SectorScenarios.VerifyAdmissionAttempts(attemptsBefore, RequiredAttribution(device, label));

        WaitUntil(quietWindow, token);
        var early = device.GetWriteCacheState();
        if (early.DrainedBytes != before.DrainedBytes || early.DirtyBytes == 0)
            throw new IOException(label + ": background drain started before the configured trigger window");

        var triggered = WaitForState(device, state => state.DrainedBytes > before.DrainedBytes,
            timeout - timer.Elapsed, token, label + " did not start draining after its trigger");
        var triggerMilliseconds = timer.Elapsed.TotalMilliseconds;
        device.Control(WriteCacheAction.LabDelay, value: 0);
        device.Control(WriteCacheAction.Disable);
        file.Read(0, actual);
        var final = device.GetWriteCacheState();
        if (!expected.AsSpan().SequenceEqual(actual) || final.DirtyBytes != 0 || final.InFlightBytes != 0 ||
            final.LastError != 0 || final.Errors != before.Errors)
            throw new IOException(label + ": final disk bytes/state mismatch");
        results.Add(new(label, "PASS",
            $"No drain during {quietWindow.TotalMilliseconds:F0} ms quiet window; first observed drain at " +
            $"{triggerMilliseconds:F1} ms with dirty/in-flight {triggered.DirtyBytes}/{triggered.InFlightBytes}. " +
            admission + " Persisted bytes verified after Disable."));
    }

    private static void RunWatermarkTrigger(DiskTarget target, CacheDevice device, string directory,
        List<CheckResult> results, IProgress<string>? progress, CancellationToken token)
    {
        const string label = "trigger/high-watermark";
        const int length = 20 << 20;
        progress?.Report(label);
        var path = Path.Combine(directory, "trigger-high-watermark.bin");
        PrepareFile(path, length);
        var options = new CacheOptions(RetainWrites: false, Drain: DrainAlgorithm.Balanced,
            LowPercent: 10, HighPercent: 20, MaxDirtyAgeMs: 5000, BatchKiB: 256, Parallelism: 1);
        ConfigurationManager.Apply(target, new CacheConfiguration(BudgetMiB, CachePreset.Fast) { Options = options }, true);
        device.Control(WriteCacheAction.LabDelay, value: 25);
        using var file = new AlignedFile(path, TransferBytes, create: false);
        var before = device.GetWriteCacheState();
        var attemptsBefore = RequiredAttribution(device, label);
        ulong peakDirty = 0;
        var highBytes = (before.PayloadCapacity * (ulong)options.HighPercent + 99) / 100;
        var crossedHighWatermark = false;
        var actual = new byte[TransferBytes];
        for (var offset = 0; offset < length; offset += TransferBytes)
        {
            token.ThrowIfCancellationRequested();
            var expected = Pattern(9200, offset / TransferBytes);
            file.Write(offset, expected);
            if (offset == 0)
            {
                file.Read(0, actual);
                if (!expected.AsSpan().SequenceEqual(actual))
                    throw new IOException(label + ": immediate cached bytes did not match");
                _ = SectorScenarios.VerifyAdmissionAttempts(attemptsBefore, RequiredAttribution(device, label));
            }
            var sample = device.GetWriteCacheState();
            peakDirty = Math.Max(peakDirty, sample.DirtyBytes);
            if (!crossedHighWatermark && sample.DirtyBytes < highBytes && sample.DrainedBytes != before.DrainedBytes)
                throw new IOException(label + ": background drain started below the configured high watermark");
            crossedHighWatermark |= sample.DirtyBytes >= highBytes;
        }
        if (!crossedHighWatermark)
            throw new IOException(label + ": workload did not cross the configured high watermark");
        var triggered = WaitForState(device, state => state.DrainedBytes > before.DrainedBytes,
            TimeSpan.FromSeconds(3), token, label + " did not start draining above the high watermark");
        device.Control(WriteCacheAction.LabDelay, value: 0);
        device.Control(WriteCacheAction.Disable);
        VerifyFile(file, length, 9200, actual, token, label);
        var final = device.GetWriteCacheState();
        if (final.DirtyBytes != 0 || final.InFlightBytes != 0 || final.LastError != 0 || final.Errors != before.Errors)
            throw new IOException(label + ": final state mismatch");
        results.Add(new(label, "PASS",
            $"No drain below the {highBytes}-byte high watermark; Balanced 20% high/10% low trigger made background progress; " +
            $"peak dirty {peakDirty}/{before.PayloadCapacity} bytes; " +
            $"observed drained delta {triggered.DrainedBytes - before.DrainedBytes}. Persisted bytes verified after Disable."));
    }

    private static void RunCapacityCase(DiskTarget target, CacheDevice device, string directory,
        List<CheckResult> results, IProgress<string>? progress, CancellationToken token,
        string label, CacheOptions options)
    {
        const int length = 80 << 20;
        progress?.Report(label + ": writing beyond the configured write pool");
        var path = Path.Combine(directory, label.Replace('/', '-') + ".bin");
        PrepareFile(path, length);
        ConfigurationManager.Apply(target, new CacheConfiguration(BudgetMiB, CachePreset.Fast) { Options = options }, true);
        device.Control(WriteCacheAction.LabDelay, value: 25);
        using var file = new AlignedFile(path, TransferBytes, create: false);
        var before = device.GetWriteCacheState();
        var diagnosticsBefore = RequiredAttribution(device, label);
        var actual = new byte[TransferBytes];
        ulong peakDirty = 0;
        string? admission = null;
        for (var offset = 0; offset < length; offset += TransferBytes)
        {
            token.ThrowIfCancellationRequested();
            var expected = Pattern(9300 + options.WritePercent + (int)options.Allocation * 1000, offset / TransferBytes);
            file.Write(offset, expected);
            if (offset == 0)
            {
                file.Read(0, actual);
                if (!expected.AsSpan().SequenceEqual(actual))
                    throw new IOException(label + ": immediate bytes did not match");
                if (options.WritePercent != 0 || options.Allocation == CacheAllocation.Automatic)
                    admission = SectorScenarios.VerifyAdmissionAttempts(diagnosticsBefore, RequiredAttribution(device, label));
            }
            if ((offset / TransferBytes & 7) == 0)
                peakDirty = Math.Max(peakDirty, device.GetWriteCacheState().DirtyBytes);
        }
        var after = device.GetWriteCacheState();
        var diagnosticsAfter = RequiredAttribution(device, label);
        var cached = options.Allocation == CacheAllocation.Automatic || options.WritePercent > 0;
        if (cached && after.ThrottleWaits <= before.ThrottleWaits)
            throw new IOException(label + ": writes beyond the pool never observed capacity backpressure");
        if (!cached && (after.ThrottleWaits != before.ThrottleWaits ||
            diagnosticsAfter.QuotaWriteBarriers <= diagnosticsBefore.QuotaWriteBarriers))
            throw new IOException(label + ": Fixed 0% did not use its explicit quota fallback");
        var writeLimit = options.Allocation == CacheAllocation.Automatic ? before.PayloadCapacity :
            before.PayloadCapacity * (ulong)options.WritePercent / 100;
        if (cached && peakDirty > writeLimit + TransferBytes)
            throw new IOException(label + ": observed dirty bytes exceeded the configured write pool");
        if (after.LastError != 0 || after.Errors != before.Errors || after.DirtyBytes > after.PayloadCapacity)
            throw new IOException(label + ": pressure state/error invariant failed");

        device.Control(WriteCacheAction.LabDelay, value: 0);
        device.Control(WriteCacheAction.Disable);
        VerifyFile(file, length, 9300 + options.WritePercent + (int)options.Allocation * 1000, actual, token, label);
        var final = device.GetWriteCacheState();
        if (final.DirtyBytes != 0 || final.InFlightBytes != 0 || final.LastError != 0 || final.Errors != before.Errors)
            throw new IOException(label + ": final disk bytes/state mismatch");
        results.Add(new(label, "PASS",
            $"Wrote and verified {length} bytes with payload/write limit {before.PayloadCapacity}/{writeLimit}; " +
            $"capacity-wait delta {after.ThrottleWaits - before.ThrottleWaits}; peak sampled dirty {peakDirty}; " +
            $"lower-write-attempt delta {diagnosticsAfter.LowerWriteAttempts - diagnosticsBefore.LowerWriteAttempts}; " +
            $"quota-barrier delta {diagnosticsAfter.QuotaWriteBarriers - diagnosticsBefore.QuotaWriteBarriers}. " +
            (admission ?? "Fixed 0% intentionally uses ordered lower I/O.") + " Persisted bytes verified after Disable."));
    }

    private static CacheAttribution RequiredAttribution(CacheDevice device, string label) =>
        device.GetDiagnostics().Attribution ??
        throw new NotSupportedException(label + " requires diagnostics V2 lower-I/O attempt counters.");

    private static void PrepareFile(string path, int length)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
        file.SetLength(length);
        file.Flush(true);
    }

    private static byte[] Pattern(int seed, int block)
    {
        var bytes = new byte[TransferBytes];
        new Random(unchecked(seed * 397 ^ block)).NextBytes(bytes);
        return bytes;
    }

    private static void VerifyFile(AlignedFile file, int length, int seed, byte[] actual,
        CancellationToken token, string label)
    {
        for (var offset = 0; offset < length; offset += TransferBytes)
        {
            token.ThrowIfCancellationRequested();
            var expected = Pattern(seed, offset / TransferBytes);
            file.Read(offset, actual);
            if (!expected.AsSpan().SequenceEqual(actual))
                throw new IOException(label + $": persisted disk mismatch at offset {offset}");
        }
    }

    private static WriteCacheState WaitForState(CacheDevice device, Func<WriteCacheState, bool> predicate,
        TimeSpan timeout, CancellationToken token, string failure)
    {
        if (timeout <= TimeSpan.Zero)
            throw new TimeoutException(failure);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            token.ThrowIfCancellationRequested();
            var state = device.GetWriteCacheState();
            if (predicate(state))
                return state;
            Thread.Sleep(20);
        }
        throw new TimeoutException(failure);
    }

    private static void WaitUntil(TimeSpan duration, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < duration)
        {
            token.ThrowIfCancellationRequested();
            Thread.Sleep(20);
        }
    }
}
