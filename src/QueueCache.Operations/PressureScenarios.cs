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
                        MaxDirtyAgeMs: 1000, IdleMs: 100), [350, 700],
                    TimeSpan.FromMilliseconds(850), TimeSpan.FromMilliseconds(1450));
                RunTimedTrigger(target, device, directory, results, progress, token,
                    "trigger/idle", new CacheOptions(RetainWrites: false, Drain: DrainAlgorithm.Idle,
                        LowPercent: 40, HighPercent: 80, MaxDirtyAgeMs: 5000, IdleMs: 500),
                    [250], TimeSpan.FromMilliseconds(650), TimeSpan.FromMilliseconds(1500));
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
        string label, CacheOptions options, int[] overwriteAtMilliseconds,
        TimeSpan noEarlierThan, TimeSpan noLaterThan)
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
        var seed = 9100 + results.Count;
        var expected = Pattern(seed, 0);
        var actual = new byte[TransferBytes];
        var before = device.GetWriteCacheState();
        var attemptsBefore = RequiredDiagnostics(device, label);
        var timer = Stopwatch.StartNew();
        file.Write(0, expected);
        file.Read(0, actual);
        if (!expected.AsSpan().SequenceEqual(actual))
            throw new IOException(label + ": immediate cached bytes did not match");
        var admission = SectorScenarios.VerifyAdmissionAttempts(attemptsBefore, RequiredDiagnostics(device, label));

        for (var revision = 0; revision < overwriteAtMilliseconds.Length; ++revision)
        {
            WaitUntil(timer, TimeSpan.FromMilliseconds(overwriteAtMilliseconds[revision]), token);
            expected = Pattern(seed, revision + 1);
            file.Write(0, expected);
        }
        WaitUntil(timer, noEarlierThan, token);
        var early = device.GetWriteCacheState();
        var attemptsAtBoundary = RequiredDiagnostics(device, label);
        if (SectorScenarios.DrainWriteAttempts(attemptsAtBoundary) != SectorScenarios.DrainWriteAttempts(attemptsBefore) ||
            early.DrainedBytes != before.DrainedBytes || early.DirtyBytes == 0)
            throw new IOException(label + ": background drain started before the configured trigger window");

        var attempt = WaitForWriteAttempt(device, SectorScenarios.DrainWriteAttempts(attemptsBefore),
            noLaterThan - timer.Elapsed, token, label + " did not issue lower I/O in its trigger window");
        var triggerMilliseconds = timer.Elapsed.TotalMilliseconds;
        ValidateTriggerWindow(triggerMilliseconds, noEarlierThan.TotalMilliseconds, noLaterThan.TotalMilliseconds);
        var triggered = WaitForState(device, state => state.DrainedBytes > before.DrainedBytes,
            TimeSpan.FromSeconds(3), token, label + " issued lower I/O but did not complete draining");
        device.Control(WriteCacheAction.LabDelay, value: 0);
        device.Control(WriteCacheAction.Disable);
        file.Read(0, actual);
        var final = device.GetWriteCacheState();
        if (!expected.AsSpan().SequenceEqual(actual) || final.DirtyBytes != 0 || final.InFlightBytes != 0 ||
            final.LastError != 0 || final.Errors != before.Errors)
            throw new IOException(label + ": final disk bytes/state mismatch");
        results.Add(new(label, "PASS",
            $"No lower write attempt before {noEarlierThan.TotalMilliseconds:F0} ms; first attempt at " +
            $"{triggerMilliseconds:F1} ms (drain-write attempt delta {SectorScenarios.DrainWriteAttempts(attempt) - SectorScenarios.DrainWriteAttempts(attemptsBefore)}); " +
            $"completion observed with dirty/in-flight {triggered.DirtyBytes}/{triggered.InFlightBytes}. " +
            "Persisted bytes verified after Disable."));
        results.Add(SectorScenarios.AdmissionCheck(label + "/first-write-zero-lower-io", admission));
    }

    private static void RunWatermarkTrigger(DiskTarget target, CacheDevice device, string directory,
        List<CheckResult> results, IProgress<string>? progress, CancellationToken token)
    {
        const string label = "trigger/high-watermark";
        progress?.Report(label);
        var path = Path.Combine(directory, "trigger-high-watermark.bin");
        PrepareFile(path, 20 << 20);
        var options = new CacheOptions(RetainWrites: false, Drain: DrainAlgorithm.Balanced,
            LowPercent: 10, HighPercent: 20, MaxDirtyAgeMs: 3600000, BatchKiB: 256, Parallelism: 1);
        ConfigurationManager.Apply(target, new CacheConfiguration(BudgetMiB, CachePreset.Fast) { Options = options }, true);
        device.Control(WriteCacheAction.LabDelay, value: 25);
        var before = device.GetWriteCacheState();
        var highBytes = (before.PayloadCapacity * (ulong)options.HighPercent + 99) / 100;
        var belowBlocks = checked((int)((highBytes - 1) / TransferBytes));
        var length = checked((belowBlocks + 1) * TransferBytes);
        if (length > 20 << 20)
            throw new IOException(label + ": bounded workload is too small for the configured high watermark");
        using var file = new AlignedFile(path, TransferBytes, create: false);
        var attemptsBefore = RequiredDiagnostics(device, label);
        var actual = new byte[TransferBytes];
        SectorScenarios.AdmissionEvidence? admission = null;
        for (var block = 0; block < belowBlocks; ++block)
        {
            token.ThrowIfCancellationRequested();
            var offset = block * TransferBytes;
            var expected = Pattern(9200, block);
            file.Write(offset, expected);
            if (offset == 0)
            {
                file.Read(0, actual);
                if (!expected.AsSpan().SequenceEqual(actual))
                    throw new IOException(label + ": immediate cached bytes did not match");
                admission = SectorScenarios.VerifyAdmissionAttempts(attemptsBefore, RequiredDiagnostics(device, label));
            }
        }
        var below = device.GetWriteCacheState();
        var attemptsBelow = RequiredDiagnostics(device, label);
        if (below.DirtyBytes >= highBytes || below.DrainedBytes != before.DrainedBytes ||
            SectorScenarios.DrainWriteAttempts(attemptsBelow) != SectorScenarios.DrainWriteAttempts(attemptsBefore))
            throw new IOException(label + ": lower I/O started below the configured high watermark");

        file.Write(belowBlocks * TransferBytes, Pattern(9200, belowBlocks));
        var attempt = WaitForWriteAttempt(device, SectorScenarios.DrainWriteAttempts(attemptsBefore), TimeSpan.FromSeconds(2), token,
            label + " did not issue lower I/O after crossing the high watermark");
        var triggered = WaitForState(device, state => state.DrainedBytes > before.DrainedBytes,
            TimeSpan.FromSeconds(3), token, label + " issued lower I/O but did not complete draining");
        device.Control(WriteCacheAction.LabDelay, value: 0);
        device.Control(WriteCacheAction.Disable);
        VerifyFile(file, length, 9200, actual, token, label);
        var final = device.GetWriteCacheState();
        if (final.DirtyBytes != 0 || final.InFlightBytes != 0 || final.LastError != 0 || final.Errors != before.Errors)
            throw new IOException(label + ": final state mismatch");
        results.Add(new(label, "PASS",
            $"No lower write attempt at {below.DirtyBytes} bytes below the {highBytes}-byte high watermark; " +
            $"crossing it produced drain-write attempt delta {SectorScenarios.DrainWriteAttempts(attempt) - SectorScenarios.DrainWriteAttempts(attemptsBefore)} and " +
            $"drained delta {triggered.DrainedBytes - before.DrainedBytes}. " +
            "Persisted bytes verified after Disable."));
        if (admission is not null)
            results.Add(SectorScenarios.AdmissionCheck(label + "/first-write-zero-lower-io", admission));
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
        var diagnosticsBefore = RequiredDiagnostics(device, label);
        var actual = new byte[TransferBytes];
        ulong peakDirty = 0, peakWriteOwned = 0, peakOccupiedSlots = 0, peakInFlight = 0;
        SectorScenarios.AdmissionEvidence? admission = null;
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
                    admission = SectorScenarios.VerifyAdmissionAttempts(diagnosticsBefore, RequiredDiagnostics(device, label));
            }
            var sample = device.GetWriteCacheState();
            peakDirty = Math.Max(peakDirty, sample.DirtyBytes);
            peakWriteOwned = Math.Max(peakWriteOwned, sample.DirtyBytes + sample.CleanWriteBytes);
            peakOccupiedSlots = Math.Max(peakOccupiedSlots, sample.OccupiedSlots);
            peakInFlight = Math.Max(peakInFlight, sample.InFlightBytes);
        }
        var after = device.GetWriteCacheState();
        var diagnosticsAfter = RequiredDiagnostics(device, label);
        var cached = options.Allocation == CacheAllocation.Automatic || options.WritePercent > 0;
        if (cached && after.ThrottleWaits <= before.ThrottleWaits)
            throw new IOException(label + ": writes beyond the pool never observed capacity backpressure");
        if (!cached && (after.ThrottleWaits != before.ThrottleWaits ||
            diagnosticsAfter.Attribution!.QuotaWriteBarriers <= diagnosticsBefore.Attribution!.QuotaWriteBarriers))
            throw new IOException(label + ": Fixed 0% did not use its explicit quota fallback");
        var writeLimit = options.Allocation == CacheAllocation.Automatic ? before.PayloadCapacity :
            before.PayloadCapacity * (ulong)options.WritePercent / 100;
        ValidateCapacityBounds(cached, before.PayloadCapacity, writeLimit, peakDirty, peakInFlight,
            peakWriteOwned, peakOccupiedSlots);
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
            $"capacity-wait delta {after.ThrottleWaits - before.ThrottleWaits}; peak dirty/in-flight/write-owned/occupied slots " +
            $"{peakDirty}/{peakInFlight}/{peakWriteOwned}/{peakOccupiedSlots}; " +
            $"lower-write-attempt delta {diagnosticsAfter.Attribution!.LowerWriteAttempts - diagnosticsBefore.Attribution!.LowerWriteAttempts}; " +
            $"quota-barrier delta {diagnosticsAfter.Attribution!.QuotaWriteBarriers - diagnosticsBefore.Attribution!.QuotaWriteBarriers}. " +
            "Persisted bytes verified after Disable."));
        if (admission is not null)
            results.Add(SectorScenarios.AdmissionCheck(label + "/first-fitting-write-zero-lower-io", admission));
    }

    private static CacheDiagnostics RequiredDiagnostics(CacheDevice device, string label) =>
        device.GetDiagnostics() is { Attribution: not null } diagnostics ? diagnostics :
        throw new NotSupportedException(label + " requires diagnostics V2 lower-I/O attempt counters.");

    internal static void ValidateTriggerWindow(double observedMilliseconds, double noEarlierMilliseconds,
        double noLaterMilliseconds)
    {
        if (observedMilliseconds < noEarlierMilliseconds || observedMilliseconds > noLaterMilliseconds)
            throw new IOException($"Lower-I/O trigger at {observedMilliseconds:F1} ms was outside the " +
                $"{noEarlierMilliseconds:F0}..{noLaterMilliseconds:F0} ms acceptance window.");
    }

    internal static void ValidateCapacityBounds(bool cached, ulong payloadCapacity, ulong writeLimit,
        ulong peakDirty, ulong peakInFlight, ulong peakWriteOwned, ulong peakOccupiedSlots)
    {
        var occupiedBytes = checked(peakOccupiedSlots * 4096);
        if (peakDirty > payloadCapacity || peakInFlight > peakDirty || occupiedBytes > payloadCapacity)
            throw new IOException("Observed cache reservations exceeded payload/state bounds.");
        if (cached && peakWriteOwned > writeLimit)
            throw new IOException("Observed dirty/retained-write ownership exceeded the configured write pool.");
        if (!cached && (peakDirty != 0 || peakInFlight != 0 || peakWriteOwned != 0))
            throw new IOException("Fixed 0% unexpectedly reserved write-cache payload.");
    }

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

    private static CacheDiagnostics WaitForWriteAttempt(CacheDevice device, ulong attemptsBefore,
        TimeSpan timeout, CancellationToken token, string failure)
    {
        if (timeout <= TimeSpan.Zero)
            throw new TimeoutException(failure);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            token.ThrowIfCancellationRequested();
            var attempts = RequiredDiagnostics(device, failure);
            if (SectorScenarios.DrainWriteAttempts(attempts) > attemptsBefore)
                return attempts;
            Thread.Sleep(20);
        }
        throw new TimeoutException(failure);
    }

    private static void WaitUntil(Stopwatch timer, TimeSpan elapsed, CancellationToken token)
    {
        while (timer.Elapsed < elapsed)
        {
            token.ThrowIfCancellationRequested();
            Thread.Sleep(20);
        }
    }
}
