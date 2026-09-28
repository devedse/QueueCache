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
            RunParallelCopies(target, device, directory, results, progress, token);
            RunReadMissIsolation(target, device, directory, results, progress, token);
            RunSettingsRollback(target, device, directory, results, progress, token);
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

    private static byte[] ParallelPattern(int writer, int pass, int block)
    {
        var data = new byte[ParallelBlockBytes];
        new Random(unchecked(200_003 + writer * 1_000_003 + pass * 10_007 + block)).NextBytes(data);
        return data;
    }

    private const int ParallelBlockBytes = 1 << 20, ParallelBlocks = 8, ParallelWriters = 4, ParallelReaders = 2;

    /// <summary>Large (1 MiB) reads and writes from several threads, so the worker hands their copies to the
    /// offloaded-request threads while write-back runs. Every read-back after a write and every concurrent read
    /// must return one whole written version of its block; the disk must hold each block's last version.</summary>
    private static void RunParallelCopies(DiskTarget target, CacheDevice device, string directory,
        List<CheckResult> results, IProgress<string>? progress, CancellationToken token)
    {
        const string label = "parallel-copies";
        const int durationSeconds = 20;
        progress?.Report($"{label}: {durationSeconds}s of 1 MiB writes/read-backs on {ParallelWriters} threads with {ParallelReaders} concurrent readers");
        ConfigurationManager.Apply(target, new CacheConfiguration(256, CachePreset.Fast)
        {
            Options = new CacheOptions(Drain: DrainAlgorithm.Eager, Parallelism: 2)
        }, true);
        var paths = Enumerable.Range(0, ParallelWriters).Select(writer => Path.Combine(directory, $"parallel-{writer}.bin")).ToArray();
        for (var writer = 0; writer < ParallelWriters; writer++)
            using (var file = new AlignedFile(paths[writer], ParallelBlockBytes, create: true))
                for (var block = 0; block < ParallelBlocks; block++)
                    file.Write((long)block * ParallelBlockBytes, ParallelPattern(writer, 0, block));
        var passes = new int[ParallelWriters, ParallelBlocks];
        var before = device.GetDiagnostics();
        long writes = 0, reads = 0, readBackMismatches = 0, concurrentMismatches = 0;
        Exception? loadError = null;
        var deadline = Stopwatch.StartNew();
        var handles = paths.Select(path => new AlignedFile(path, ParallelBlockBytes, create: false, shareRead: true)).ToArray();
        try
        {
            var writers = Enumerable.Range(0, ParallelWriters).Select(writer => Task.Run(() =>
            {
                try
                {
                    var actual = new byte[ParallelBlockBytes];
                    for (var pass = 1; deadline.Elapsed < TimeSpan.FromSeconds(durationSeconds) && !token.IsCancellationRequested; pass++)
                        for (var block = 0; block < ParallelBlocks; block++)
                        {
                            var data = ParallelPattern(writer, pass, block);
                            handles[writer].Write((long)block * ParallelBlockBytes, data);
                            Volatile.Write(ref passes[writer, block], pass);
                            Interlocked.Increment(ref writes);
                            handles[writer].Read((long)block * ParallelBlockBytes, actual);
                            if (!actual.AsSpan().SequenceEqual(data))
                                Interlocked.Increment(ref readBackMismatches);
                        }
                }
                catch (Exception exception) { loadError ??= exception; }
            })).ToArray();
            var readers = Enumerable.Range(0, ParallelReaders).Select(reader => Task.Run(() =>
            {
                try
                {
                    var files = paths.Select(path => new AlignedFile(path, ParallelBlockBytes, create: false, sharedReadOnly: true)).ToArray();
                    try
                    {
                        var random = new Random(200_017 + reader);
                        var actual = new byte[ParallelBlockBytes];
                        while (writers.Any(task => !task.IsCompleted))
                        {
                            var writer = random.Next(ParallelWriters);
                            var block = random.Next(ParallelBlocks);
                            var first = Volatile.Read(ref passes[writer, block]);
                            files[writer].Read((long)block * ParallelBlockBytes, actual);
                            var last = Volatile.Read(ref passes[writer, block]);
                            var whole = false;
                            for (var candidate = Math.Max(0, first - 1); candidate <= last + 1 && !whole; candidate++)
                                whole = actual.AsSpan().SequenceEqual(ParallelPattern(writer, candidate, block));
                            if (!whole)
                                Interlocked.Increment(ref concurrentMismatches);
                            Interlocked.Increment(ref reads);
                        }
                    }
                    finally
                    {
                        foreach (var file in files)
                            file.Dispose();
                    }
                }
                catch (Exception exception) { loadError ??= exception; }
            })).ToArray();
            if (!Task.WaitAll([.. writers, .. readers], TimeSpan.FromMinutes(5)))
                throw new IOException($"{label}: the concurrent load did not finish within 5 minutes.");
        }
        finally
        {
            foreach (var handle in handles)
                handle.Dispose();
        }
        if (loadError is not null)
            throw new IOException($"{label}: the concurrent load failed.", loadError);
        var after = device.GetDiagnostics();
        var state = device.GetWriteCacheState();
        device.Control(WriteCacheAction.Disable);
        var persisted = true;
        var readBack = new byte[ParallelBlockBytes];
        for (var writer = 0; writer < ParallelWriters && persisted; writer++)
            using (var file = new AlignedFile(paths[writer], ParallelBlockBytes, create: false, sharedReadOnly: true))
                for (var block = 0; block < ParallelBlocks && persisted; block++)
                {
                    file.Read((long)block * ParallelBlockBytes, readBack);
                    persisted = readBack.AsSpan().SequenceEqual(ParallelPattern(writer, passes[writer, block], block));
                }
        results.Add(VerifyParallelCopies(label, writes, reads, readBackMismatches, concurrentMismatches,
            before.CopyOffloadReads is null || after.CopyOffloadReads is null ? null : after.CopyOffloadReads - before.CopyOffloadReads,
            before.CopyOffloadWrites is null || after.CopyOffloadWrites is null ? null : after.CopyOffloadWrites - before.CopyOffloadWrites,
            state.LastError, persisted));
    }

    /// <summary>A kept read miss must be the disk's data, not the application's buffer: another thread overwrites
    /// the read buffer while each unbuffered read misses, then a separate handle re-reads the block from RAM.</summary>
    private static void RunReadMissIsolation(DiskTarget target, CacheDevice device, string directory,
        List<CheckResult> results, IProgress<string>? progress, CancellationToken token)
    {
        const string label = "read-miss-isolation";
        const int fileBytes = 16 << 20, chunk = 1 << 20;
        progress?.Report($"{label}: read misses while another thread overwrites the read buffer");
        ConfigurationManager.Apply(target, new CacheConfiguration(256, CachePreset.Fast)
        {
            Options = new CacheOptions(Drain: DrainAlgorithm.Eager)
        }, true);
        var path = Path.Combine(directory, "read-miss-isolation.bin");
        var expected = new byte[fileBytes];
        new Random(300_007).NextBytes(expected);
        using (var file = new AlignedFile(path, chunk, create: true))
            for (var offset = 0; offset < fileBytes; offset += chunk)
                file.Write(offset, expected.AsSpan(offset, chunk).ToArray());
        device.Control(WriteCacheAction.Flush);
        device.Control(WriteCacheAction.DropClean);
        var before = device.GetWriteCacheState();
        var diagnosticsBefore = device.GetDiagnostics();
        long scribbles = 0;
        var mismatched = 0;
        using (var victim = new AlignedFile(path, chunk, create: false, sharedReadOnly: true))
        using (var check = new AlignedFile(path, chunk, create: false, sharedReadOnly: true))
        {
            var stop = 0;
            var scribbler = Task.Run(() =>
            {
                var noise = new byte[chunk];
                Array.Fill(noise, (byte)0xEE);
                while (Volatile.Read(ref stop) == 0)
                {
                    System.Runtime.InteropServices.Marshal.Copy(noise, 0, victim.TransferBuffer, chunk);
                    Interlocked.Increment(ref scribbles);
                }
            });
            var ignored = new byte[chunk];
            var actual = new byte[chunk];
            try
            {
                for (var offset = 0; offset < fileBytes; offset += chunk)
                {
                    token.ThrowIfCancellationRequested();
                    victim.Read(offset, ignored); // Its buffer is overwritten concurrently; contents undefined.
                    check.Read(offset, actual);
                    if (!actual.AsSpan().SequenceEqual(expected.AsSpan(offset, chunk)))
                        mismatched++;
                }
            }
            finally
            {
                Volatile.Write(ref stop, 1);
                scribbler.Wait();
            }
        }
        var after = device.GetWriteCacheState();
        var diagnosticsAfter = device.GetDiagnostics();
        results.Add(VerifyReadMissIsolation(label, fileBytes, mismatched, after.ReadHitBytes - before.ReadHitBytes,
            diagnosticsBefore.ReadFills is null || diagnosticsAfter.ReadFills is null ? null : diagnosticsAfter.ReadFills - diagnosticsBefore.ReadFills,
            Interlocked.Read(ref scribbles)));
        device.Control(WriteCacheAction.Disable);
    }

    /// <summary>N1 on the real driver: lab faults 6 and 7 fail the next cache allocation once, so applying a
    /// different size fails after the old cache was freed. The previous settings must be back, enabled and healthy,
    /// with cached data intact; once the fault is spent the same change must apply.</summary>
    private static void RunSettingsRollback(DiskTarget target, CacheDevice device, string directory,
        List<CheckResult> results, IProgress<string>? progress, CancellationToken token)
    {
        var previous = new CacheConfiguration(64, CachePreset.Fast)
        {
            Options = new CacheOptions(Drain: DrainAlgorithm.Idle, Parallelism: 2)
        };
        var requested = new CacheConfiguration(128, CachePreset.Strict)
        {
            Options = new CacheOptions(CacheAllocation.Fixed, 50, Drain: DrainAlgorithm.Balanced, Parallelism: 1)
        };
        foreach (var fault in new[] { 6UL, 7UL })
        {
            token.ThrowIfCancellationRequested();
            var label = $"settings-rollback/lab-fault-{fault}";
            progress?.Report($"{label}: a failed resize must restore the previous settings");
            ConfigurationManager.Apply(target, previous, true);
            var path = Path.Combine(directory, $"settings-rollback-{fault}.bin");
            var expected = new byte[1 << 20];
            new Random(400_009 + (int)fault).NextBytes(expected);
            var actual = new byte[expected.Length];
            using var file = new AlignedFile(path, expected.Length, create: true);
            file.Write(0, expected);
            device.Control(WriteCacheAction.LabFault, value: fault);
            ConfigurationNotAppliedException? failure = null;
            try
            {
                ConfigurationManager.Apply(target, requested, true);
            }
            catch (ConfigurationNotAppliedException exception)
            {
                failure = exception;
            }
            finally
            {
                device.Control(WriteCacheAction.LabFault, value: 0);
            }
            var restored = device.GetWriteCacheState();
            file.Read(0, actual);
            var dataMatches = actual.AsSpan().SequenceEqual(expected);
            WriteCacheState? applied = null;
            if (failure is not null)
                applied = ConfigurationManager.Apply(target, requested, true);
            results.Add(VerifySettingsRollback(label, failure?.PreviousSettingsRestored, failure?.Message,
                restored.Enabled && restored.Operational && restored.LastError == 0,
                restored.BudgetBytes == 64UL << 20, restored.UnsafeDefer, restored.Options == previous.Options,
                dataMatches, applied is { } state && state.BudgetBytes == 128UL << 20 && !state.UnsafeDefer));
        }
        ConfigurationManager.Apply(target, previous, true);
    }

    internal static CheckResult VerifySettingsRollback(string label, bool? restored, string? message, bool healthy,
        bool previousSize, bool previousFast, bool previousOptions, bool dataMatches, bool appliedAfterwards)
    {
        if (restored is null)
            throw new IOException($"{label}: the settings change succeeded although the allocation fault was armed");
        if (restored != true || message?.Contains("previous settings were restored", StringComparison.Ordinal) != true)
            throw new IOException($"{label}: the failure did not report restored settings: {message}");
        if (!healthy || !previousSize || !previousFast || !previousOptions)
            throw new IOException(FormattableString.Invariant(
                $"{label}: reported restored, but the driver is not: healthy {healthy}, 64 MiB {previousSize}, Fast {previousFast}, options {previousOptions}"));
        if (!dataMatches)
            throw new IOException($"{label}: a file written before the failed change read back differently");
        if (!appliedAfterwards)
            throw new IOException($"{label}: the same change did not apply once the fault was spent");
        return new(label, "PASS", "The failed resize reported \"previous settings were restored\"; the cache was back at " +
            "64 MiB Fast with its options, enabled and healthy; a file written before read back intact; the same " +
            "change (128 MiB Strict) applied once the fault was spent.");
    }

    internal static CheckResult VerifyReadMissIsolation(string label, int fileBytes, int mismatchedChunks,
        ulong hitBytes, ulong? fills, long scribbles)
    {
        if (mismatchedChunks != 0)
            throw new IOException(FormattableString.Invariant(
                $"{label}: {mismatchedChunks} MiB re-read from RAM held another program's buffer contents instead of the file"));
        if (fills is null)
            return new(label, "PASS", FormattableString.Invariant(
                $"Re-reads matched the file; read fill counter unavailable (driver older than Diagnostics V13); {scribbles} buffer overwrites."));
        if (fills < (ulong)(fileBytes / 4096) || hitBytes < (ulong)fileBytes)
            throw new IOException(FormattableString.Invariant(
                $"{label}: the misses were not kept (fills {fills}, re-read hit {hitBytes} of {fileBytes} bytes), so isolation was not exercised"));
        return new(label, "PASS", FormattableString.Invariant(
            $"{fills} blocks kept from misses while their read buffer was overwritten {scribbles} times; every re-read from RAM ({hitBytes} bytes) matched the file."));
    }

    internal static CheckResult VerifyParallelCopies(string label, long writes, long reads, long readBackMismatches,
        long concurrentMismatches, ulong? offloadedReads, ulong? offloadedWrites, int lastError, bool persisted)
    {
        if (readBackMismatches != 0 || concurrentMismatches != 0)
            throw new IOException(FormattableString.Invariant(
                $"{label}: {readBackMismatches} read-backs and {concurrentMismatches} concurrent reads did not return one whole written version ({writes} writes, {reads} reads)"));
        if (lastError != 0)
            throw new IOException(FormattableString.Invariant($"{label}: the cache faulted (0x{lastError:X8})"));
        if (!persisted)
            throw new IOException($"{label}: after Disable the disk did not hold every block's last written version");
        if (writes == 0 || reads == 0)
            throw new IOException($"{label}: the load made no progress");
        var offloads = offloadedReads is null || offloadedWrites is null
            ? "offloaded-copy counters unavailable (driver older than diagnostics V16)"
            : offloadedReads == 0 || offloadedWrites == 0
                ? throw new IOException(FormattableString.Invariant(
                    $"{label}: no large copies were offloaded (reads {offloadedReads}, writes {offloadedWrites})"))
                : FormattableString.Invariant($"offloaded copies: {offloadedReads} reads, {offloadedWrites} writes");
        return new(label, "PASS", FormattableString.Invariant(
            $"{writes} 1 MiB writes with read-back and {reads} concurrent 1 MiB reads each returned one whole version; {offloads}; last versions persisted after Disable."));
    }

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
        var diagnosticsAfterWindow = device.GetDiagnostics();
        var attemptsAfter = diagnosticsAfterWindow.Attribution!;
        var callerEvidence = VerifyCallerPath(diagnosticsBefore.CallerPath, diagnosticsAfterWindow.CallerPath, (ulong)sequence * 2);
        if (after.AcceptedBytes <= before.AcceptedBytes || after.DrainedBytes <= before.DrainedBytes ||
            attemptsAfter.LowerWriteAttempts <= attemptsAfterAdmission!.LowerWriteAttempts)
            throw new IOException(label + ": foreground or background made no measurable progress");
        // Lower-read counters are device-wide: other activity on the disk (indexing, scanning)
        // can read it during the window. The owned blocks are dirty or retained and dirty data
        // is never evicted, so with zero evictions an owned read cannot have missed.
        var otherLowerReads = attemptsAfter.LowerReadAttempts - attemptsBefore.LowerReadAttempts;
        if (after.ReadHitBytes - before.ReadHitBytes < readBytes || (otherLowerReads != 0 && after.Evictions != before.Evictions))
        {
            var diagnosticsAfter = device.GetDiagnostics();
            throw new IOException(label + ": a known-current cached read missed RAM " + FormattableString.Invariant(
                $"(hit {after.ReadHitBytes - before.ReadHitBytes} of {readBytes} bytes; lower reads +{attemptsAfter.LowerReadAttempts - attemptsBefore.LowerReadAttempts}") +
                FormattableString.Invariant($", paging-forwarded +{(diagnosticsAfter.LowerSources?.PagingForwardedReads ?? 0) - (diagnosticsBefore.LowerSources?.PagingForwardedReads ?? 0)}") +
                FormattableString.Invariant($", other +{(diagnosticsAfter.LowerSources?.OtherReads ?? 0) - (diagnosticsBefore.LowerSources?.OtherReads ?? 0)}") +
                FormattableString.Invariant($"; fills +{(diagnosticsAfter.ReadFills ?? 0) - (diagnosticsBefore.ReadFills ?? 0)}/+{(diagnosticsAfter.PagingReadFills ?? 0) - (diagnosticsBefore.PagingReadFills ?? 0)}") +
                FormattableString.Invariant($"; evictions +{after.Evictions - before.Evictions})."));
        }
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
            $"{attemptsAfter.LowerWriteAttempts - attemptsAfterAdmission.LowerWriteAttempts}; device-wide lower reads from other " +
            $"activity {otherLowerReads} with zero evictions; {callerEvidence}. Persisted bytes verified after Disable."));
        results.Add(SectorScenarios.AdmissionCheck(label + "/first-fitting-write-zero-lower-io", admissionEvidence));
    }
    /// <summary>Diagnostics V14: serialized fitting writes and RAM read hits on an otherwise idle
    /// disk are served on the caller's thread. Every 1024th candidate probes the request worker and
    /// other disk activity can add requests, so at least 90% of the owned requests must be counted.</summary>
    internal static string VerifyCallerPath(CacheCallerPath? before, CacheCallerPath? after, ulong ownedRequests)
    {
        if (before is null || after is null)
            return "caller-path counters unavailable (driver older than diagnostics V14)";
        var served = after.Reads - before.Reads + (after.Writes - before.Writes);
        var declined = after.Declined - before.Declined;
        if (served * 10 < ownedRequests * 9)
            throw new IOException(FormattableString.Invariant(
                $"foreground-background: only {served} of {ownedRequests} idle-disk requests were served on the caller's thread ({declined} declined)"));
        return FormattableString.Invariant($"caller-thread requests {served} of {ownedRequests} ({declined} declined)");
    }

}
