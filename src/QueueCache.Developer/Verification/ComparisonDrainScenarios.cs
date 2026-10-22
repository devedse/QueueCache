using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using QueueCache.Management;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Developer.Verification;

public sealed record ComparisonDrainRecovery([property: JsonRequired] string Volume,
    [property: JsonRequired] ComparisonProvider Provider, [property: JsonRequired] string? PrimoCli,
    [property: JsonRequired] ComparisonTarget Target, [property: JsonRequired] bool ResumeRequired,
    [property: JsonRequired] WriteCacheState? OriginalState, Guid? OriginalPrimoTaskId = null,
    ulong? OriginalGatherPolicy = null, bool GatherRestoreRequired = false);

[SupportedOSPlatform("windows")]
internal static class ComparisonDrainScenarios
{
    internal const string RecoveryFile = "compare-drain-recovery.json";

    public static async Task<IReadOnlyList<CheckResult>> RunAsync(WorkerJob job, ComparisonTarget target, IReadOnlyList<ComparisonCase> plan)
    {
        var freeGiB = job.Operation == ComparisonDrainRead.Suite ? 20 : job.Operation == ComparisonDrain.SustainedSuite ? 12 : 7;
        if (target.DiskNumber is null || target.VolumeName is null || target.FreeBytes < (long)freeGiB << 30)
            throw new IOException($"{job.Operation} requires an observable physical backing disk and at least {freeGiB} GiB free.");
        var directory = Path.GetDirectoryName(job.Reply)!;
        var provider = new Provider(job, directory);
        await provider.PreflightAsync(target);
        var work = job.WorkDirectory ?? throw new InvalidDataException("Missing compare-drain work directory.");
        Directory.CreateDirectory(work);
        if (job.Operation == ComparisonDrainRead.Suite)
            return await RunReadAsync(job, target, plan, provider, directory, work);
        Progress("Preparing unique full-GiB payload and every SHA-256 oracle outside timing.");
        using var files = new ComparisonDrainFiles();
        RunStorage.AtomicJson(Path.Combine(directory, "compare-drain-oracle.json"), new
        {
            Algorithm = "SplitMix64 little-endian", ComparisonDrain.Seed, ComparisonDrain.PayloadBytes,
            ComparisonDrain.LargeBytes, ComparisonDrain.SmallBytes, SmallFileCount = ComparisonDrain.SmallFiles,
            LargeSha256 = files.LargeHash, SmallFiles = files.SmallHashes,
            SustainedBytes = job.Operation == ComparisonDrain.SustainedSuite ? files.SustainedBytes : (long?)null,
            SustainedSha256 = job.Operation == ComparisonDrain.SustainedSuite ? files.SustainedHash : null,
            ShuffledOrder = ComparisonDrainFiles.ShuffledOrder(ComparisonDrain.SmallFiles)
        });
        var checks = new List<CheckResult>();
        foreach (var test in plan)
        {
            var prefix = Path.Combine(directory, "compare." + test.Id);
            var expectedBytes = ComparisonDrain.ExpectedBytes(test.Kind);
            var path = Path.Combine(work, test.File);
            var started = DateTimeOffset.UtcNow;
            Progress($"{test.Id}: preparation ({checks.Count + 1}/{plan.Count}).");
            try
            {
                if (test.GatherPolicy is { } policy)
                    await provider.SelectGatherAsync(policy, prefix + ".policy");
                // Explicit preparation is not part of the score. Every scored file is CreateNew.
                var prepared = await ComparisonDrain.PrepareCleanAsync(provider.Kind, async attempt =>
                {
                    ComparisonScenarios.FlushVolume(target.Letter);
                    await provider.DrainAsync(prefix + $".prepare-{attempt:D4}-drain");
                    await provider.ClearAsync(prefix + $".prepare-{attempt:D4}-clear");
                    return await provider.PendingAsync(prefix + $".prepare-{attempt:D4}-pending");
                });
                var before = prepared.Pending;
                RunStorage.AtomicJson(prefix + ".before.pending.json", before);
                RunStorage.AtomicJson(prefix + ".preparation.json", new { prepared.Attempt, Pending = before });
                if (provider.Product == ComparisonProvider.QueueCache)
                    File.Copy(prefix + $".prepare-{prepared.Attempt:D4}-pending.cache.json", prefix + ".before.cache.json");
                var diskBefore = ComparisonScenarios.DiskCounters(target.DiskNumber.Value);
                if (provider.Kind == ComparisonProvider.Direct || provider.Paused)
                    diskBefore = await ComparisonDrain.WaitForDirectDiskIdleAsync(diskBefore,
                        () => ComparisonScenarios.DiskCounters(target.DiskNumber.Value),
                        (attempt, value) => RunStorage.AtomicJson(prefix + $".direct-prepare-disk-{attempt:D4}.json", value));
                if (diskBefore.QueueDepth != 0) throw new IOException("Backing disk is busy before compare-drain.");
                using var stop = new CancellationTokenSource();
                var samples = new List<ComparisonDiskCounters>();
                var ready = prefix + ".ready.json";
                var observer = Task.Factory.StartNew(() => ObserveDisk(target.DiskNumber.Value, prefix, ready, samples, stop.Token),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                ComparisonDrainScore score;
                try
                {
                    await TelemetryCoverage.WaitReadyAsync(ready, observer, TimeSpan.FromSeconds(45), CancellationToken.None);
                    var readyUtc = samples[0].Utc; // published before the ready file
                    Progress(test.Id + ": writing new files, then flushing volume metadata and explicitly draining the product.");
                    var release = prefix + ".start.json";
                    RunStorage.AtomicJson(release, new { At = DateTimeOffset.UtcNow, Ready = readyUtc });
                    var scoreClock = new Stopwatch();
                    var pass = files.Write(path, test.Kind, at =>
                        scoreClock.Start());
                    ComparisonScenarios.FlushVolume(target.Letter);
                    var requested = DateTimeOffset.UtcNow;
                    var drainClock = Stopwatch.StartNew();
                    await provider.DrainAsync(prefix + ".drain");
                    var explicitSeconds = drainClock.Elapsed.TotalSeconds;
                    var returned = DateTimeOffset.UtcNow;
                    // Pending and disk-queue confirmation is part of the endpoint. No fixed quiet hold.
                    var endpoint = Stopwatch.StartNew();
                    ComparisonPending after;
                    ComparisonDiskCounters diskAfter;
                    var poll = 0;
                    do
                    {
                        after = await provider.PendingAsync(prefix + $".after-{++poll:D4}");
                        diskAfter = ComparisonScenarios.DiskCounters(target.DiskNumber.Value);
                        if (after.DirtyBytes is not > 0 && after.InFlightBytes is not > 0 && after.DeferredBlocks is not > 0 && diskAfter.QueueDepth == 0) break;
                        if (endpoint.Elapsed > TimeSpan.FromSeconds(180)) throw new TimeoutException("Explicit drain did not reach zero pending counters and disk queue zero.");
                        await Task.Delay(100);
                    } while (true);
                    var total = scoreClock.Elapsed.TotalSeconds;
                    var confirmationSeconds = endpoint.Elapsed.TotalSeconds;
                    var ended = DateTimeOffset.UtcNow;
                    stop.Cancel(); await observer;
                    score = new(test.Id, provider.Kind, expectedBytes, ComparisonDrain.Files(test.Kind),
                        readyUtc, pass.Started, pass.Ended, requested, returned, ended,
                        pass.Seconds, total - pass.Seconds, explicitSeconds, confirmationSeconds, total, expectedBytes / total / 1_000_000.0,
                        before, after, diskBefore, diskAfter, samples.ToArray());
                    // Preserve the completed measurement even when coverage, bypass, a hash or restoration fails.
                    RunStorage.AtomicJson(prefix + ".score.json", score);
                    ComparisonDrain.ValidateScore(test, score, provider.Kind);
                    if (provider.Product == ComparisonProvider.QueueCache)
                        File.Copy(prefix + $".after-{poll:D4}.cache.json", prefix + ".after.cache.json");
                    if (test.GatherPolicy is { } measuredPolicy)
                    {
                        var initialState = JsonSerializer.Deserialize<WriteCacheState>(File.ReadAllText(prefix + ".before.cache.json"))!;
                        var finalState = JsonSerializer.Deserialize<WriteCacheState>(File.ReadAllText(prefix + ".after.cache.json"))!;
                        DrainBatching.ValidateProfile(initialState, measuredPolicy);
                        DrainBatching.ValidateProfile(finalState, measuredPolicy);
                        RunStorage.AtomicJson(prefix + ".batching.json", DrainBatching.Measure(test,
                            initialState.Performance!, finalState.Performance!));
                    }
                }
                finally { stop.Cancel(); await observer; }

                Progress(test.Id + ": measurement saved; bypassing product cache and verifying every file (untimed).");
                var hashes = await ComparisonDrain.VerifyBypassedAsync(test.Id,
                    () => provider.PauseAsync(prefix + ".pause"), () => files.Verify(path, test.Kind),
                    () => provider.RestoreAsync(prefix + ".resume"), verify => RunStorage.AtomicJson(prefix + ".verify.json", verify),
                    () => provider.ConfirmBypassAsync(prefix));
                checks.Add(new(test.Id, "PASS", $"{score.MBPerSecond:F2} MB/s through explicit drain; app {score.ApplicationSeconds:F3}s, drain/confirmation {score.DrainSeconds:F3}s; all {hashes.Length} file hashes verified with cache bypassed."));
                RunStorage.AtomicJson(Path.Combine(directory, "compare-drain-checks.json"), checks);
                // Only an owned, successfully verified directory is eligible. The next preparation flush
                // drains its deletion metadata before taking the next case's baseline.
                Directory.Delete(path, recursive: true);
                RunStorage.AtomicJson(prefix + ".cleanup.json", new { Directory = path, Removed = true, At = DateTimeOffset.UtcNow });
                Progress(test.Id + ": PASS; byte verification and original active state restored.");
            }
            catch (Exception ex)
            {
                RunStorage.AtomicJson(prefix + ".failure.json", new { Error = ex.ToString(), At = DateTimeOffset.UtcNow });
                Console.Error.WriteLine($"FAIL: {test.Id}: {ex.Message}"); Console.Error.Flush();
                throw;
            }
            finally { RunStorage.AtomicJson(prefix + ".interval.json", new { Start = started, End = DateTimeOffset.UtcNow }); }
        }
        return checks;
    }

    /// <summary>Plan 161: the same reader alone and while the writer keeps the product draining (ComparisonDrainRead).</summary>
    private static async Task<IReadOnlyList<CheckResult>> RunReadAsync(WorkerJob job, ComparisonTarget target,
        IReadOnlyList<ComparisonCase> plan, Provider provider, string directory, string work)
    {
        var diskSpd = job.DiskSpd ?? throw new ArgumentException("compare-drain-read requires --diskspd.");
        var disk = target.DiskNumber!.Value;
        var readerPath = Path.Combine(work, ComparisonDrainRead.ReaderFile);
        var writerPath = Path.Combine(work, ComparisonDrainRead.WriterFile);
        Progress("Creating the 8 GiB reader and writer files outside timing.");
        foreach (var (path, bytes) in new[] { (readerPath, ComparisonDrainRead.ReaderBytes), (writerPath, ComparisonDrainRead.WriterBytes) })
            RunStorage.AtomicJson(Path.Combine(directory, "compare-read-prepare-" + Path.GetFileNameWithoutExtension(path) + ".json"),
                ComparisonScenarios.Sequential(path, bytes, write: true, queue: 8, create: true));
        async Task<ProcessResult> Disk(ComparisonCase window, string path, long bytes, string prefix) =>
            await OwnedProcess.RunAsync(diskSpd, [.. ComparisonPlan.Arguments(window, bytes), path], prefix,
                TimeSpan.FromSeconds(window.Seconds + ComparisonPlan.WarmupSeconds + 120), CancellationToken.None);
        ComparisonScore Score(ComparisonCase window, ProcessResult result, long bytes, string prefix)
        {
            if (result.ExitCode != 0) throw new IOException($"DiskSpd exit {result.ExitCode}; inspect {prefix}.stderr.txt.");
            if (result.Error.Trim().Length != 0) throw new InvalidDataException($"Unexpected DiskSpd stderr; inspect {prefix}.stderr.txt.");
            var score = ComparisonPlan.Score(window, result.Output, bytes);
            RunStorage.AtomicJson(prefix + ".score.json", score);
            return score;
        }
        var checks = new List<CheckResult>();
        foreach (var test in plan)
        {
            var prefix = Path.Combine(directory, "compare." + test.Id);
            var loaded = test.Kind == "read-loaded";
            var started = DateTimeOffset.UtcNow;
            Progress($"{test.Id}: preparation ({checks.Count + 1}/{plan.Count}).");
            try
            {
                // Drain, then drop clean data, so the reader file is uncached in every product at the start.
                var prepared = await ComparisonDrain.PrepareCleanAsync(provider.Kind, async attempt =>
                {
                    ComparisonScenarios.FlushVolume(target.Letter);
                    await provider.DrainAsync(prefix + $".prepare-{attempt:D4}-drain");
                    await provider.ClearAsync(prefix + $".prepare-{attempt:D4}-clear");
                    return await provider.PendingAsync(prefix + $".prepare-{attempt:D4}-pending");
                });
                RunStorage.AtomicJson(prefix + ".preparation.json", new { prepared.Attempt, Pending = prepared.Pending });
                var diskBefore = await ComparisonDrain.WaitForDirectDiskIdleAsync(ComparisonScenarios.DiskCounters(disk),
                    () => ComparisonScenarios.DiskCounters(disk),
                    (attempt, value) => RunStorage.AtomicJson(prefix + $".prepare-disk-{attempt:D4}.json", value));
                using var stop = new CancellationTokenSource();
                var samples = new List<ComparisonDiskCounters>();
                var ready = prefix + ".ready.json";
                var observer = Task.Factory.StartNew(() => ObserveDisk(disk, prefix, ready, samples, stop.Token),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                ComparisonReadScore score;
                try
                {
                    await TelemetryCoverage.WaitReadyAsync(ready, observer, TimeSpan.FromSeconds(45), CancellationToken.None);
                    var readyUtc = samples[0].Utc;
                    var writerWindow = ComparisonDrainRead.WriterWindow(test);
                    Task<ProcessResult>? writer = null;
                    DateTimeOffset? writerStarted = null, writerEnded = null;
                    ComparisonPending? atStart = null, atEnd = null;
                    if (loaded)
                    {
                        Progress(test.Id + ": writer filling the cache; the reader starts once the product is draining.");
                        writerStarted = DateTimeOffset.UtcNow;
                        writer = Disk(writerWindow, writerPath, ComparisonDrainRead.WriterBytes, prefix + ".writer");
                        await Task.Delay(TimeSpan.FromSeconds(ComparisonDrainRead.WriterLeadSeconds));
                        if (writer.IsCompleted) throw new IOException("The writer ended before the reader window.");
                        atStart = await provider.PendingAsync(prefix + ".reader-start");
                    }
                    var readerWindow = ComparisonDrainRead.ReaderWindow(test);
                    var readerStarted = DateTimeOffset.UtcNow;
                    var readerResult = await Disk(readerWindow, readerPath, ComparisonDrainRead.ReaderBytes, prefix + ".reader");
                    var readerEnded = DateTimeOffset.UtcNow;
                    ComparisonScore? writerScore = null;
                    if (writer is not null)
                    {
                        if (writer.IsCompleted) throw new IOException("The writer ended before the reader window completed.");
                        atEnd = await provider.PendingAsync(prefix + ".reader-end");
                        var writerResult = await writer;
                        writerEnded = DateTimeOffset.UtcNow;
                        writerScore = Score(writerWindow, writerResult, ComparisonDrainRead.WriterBytes, prefix + ".writer");
                    }
                    var readerScore = Score(readerWindow, readerResult, ComparisonDrainRead.ReaderBytes, prefix + ".reader");
                    // Unscored endpoint: drain what the writer left, then confirm zero pending and an idle disk.
                    ComparisonScenarios.FlushVolume(target.Letter);
                    await provider.DrainAsync(prefix + ".drain");
                    var endpoint = Stopwatch.StartNew();
                    ComparisonPending after;
                    ComparisonDiskCounters diskAfter;
                    var poll = 0;
                    do
                    {
                        after = await provider.PendingAsync(prefix + $".after-{++poll:D4}");
                        diskAfter = ComparisonScenarios.DiskCounters(disk);
                        if (after.DirtyBytes is not > 0 && after.InFlightBytes is not > 0 && after.DeferredBlocks is not > 0 && diskAfter.QueueDepth == 0) break;
                        if (endpoint.Elapsed > TimeSpan.FromSeconds(180)) throw new TimeoutException("Drain after the window did not reach zero pending counters and disk queue zero.");
                        await Task.Delay(100);
                    } while (true);
                    var ended = DateTimeOffset.UtcNow;
                    stop.Cancel(); await observer;
                    score = new(test.Id, provider.Kind, loaded, readyUtc, writerStarted, readerStarted, readerEnded, writerEnded, ended,
                        readerScore, writerScore, prepared.Pending, atStart, atEnd, after, diskBefore, diskAfter, samples.ToArray(),
                        ComparisonDrainRead.WindowBytes(samples, readerStarted, readerEnded));
                    RunStorage.AtomicJson(prefix + ".score.json", score);
                    ComparisonDrainRead.ValidateScore(test, score, provider.Kind);
                }
                finally { stop.Cancel(); await observer; }
                checks.Add(new(test.Id, "PASS", $"reader {score.Reader.Iops:F0} IOPS, p99 {score.Reader.ReadP99Ms:F3} ms; " +
                    $"backing disk wrote {score.ReaderWindowDiskBytesWritten / 1_000_000.0 / score.Reader.Seconds:F1} MB/s during the reader window" +
                    (score.Writer is { } w ? $"; writer {w.MBPerSecond:F1} MB/s over its run." : ".")));
                RunStorage.AtomicJson(Path.Combine(directory, "compare-drain-checks.json"), checks);
                Progress(test.Id + ": PASS.");
            }
            catch (Exception ex)
            {
                RunStorage.AtomicJson(prefix + ".failure.json", new { Error = ex.ToString(), At = DateTimeOffset.UtcNow });
                Console.Error.WriteLine($"FAIL: {test.Id}: {ex.Message}"); Console.Error.Flush();
                throw;
            }
            finally { RunStorage.AtomicJson(prefix + ".interval.json", new { Start = started, End = DateTimeOffset.UtcNow }); }
        }
        return checks;
    }

    private static void Progress(string text) { Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] {text}"); Console.Out.Flush(); }

    private static void ObserveDisk(int disk, string prefix, string ready, List<ComparisonDiskCounters> samples, CancellationToken stop)
    {
        using var stream = new StreamWriter(prefix + ".disk.jsonl") { AutoFlush = true };
        void Sample() { var value = ComparisonScenarios.DiskCounters(disk); samples.Add(value); stream.WriteLine(JsonSerializer.Serialize(value)); }
        Sample(); RunStorage.AtomicJson(ready, new { At = samples[0].Utc, Source = "IOCTL_DISK_PERFORMANCE", IntervalMs = 100 });
        while (!stop.WaitHandle.WaitOne(100)) Sample();
        Sample(); // endpoint covers even a sub-sampling-interval explicit flush
    }

    public static async Task RestoreAsync(WorkerJob job)
    {
        var path = job.Recovery ?? Path.Combine(Path.GetDirectoryName(job.Reply)!, RecoveryFile);
        if (!File.Exists(path)) return;
        var recovery = JsonSerializer.Deserialize<ComparisonDrainRecovery>(File.ReadAllText(path)) ?? throw new InvalidDataException("Missing compare-drain recovery journal.");
        if (!string.Equals(recovery.Volume, job.Volume, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Comparison recovery volume mismatch.");
        var current = ComparisonScenarios.Inspect(job.Volume, Path.GetDirectoryName(job.Reply)!, "compare-drain");
        ComparisonDrain.ValidateRecoveryTarget(recovery, current);
        var provider = new Provider(job with { Provider = recovery.Provider, PrimoCli = recovery.PrimoCli }, Path.GetDirectoryName(path)!, recovery);
        await provider.RestoreAsync(Path.Combine(Path.GetDirectoryName(job.Reply)!, "compare-drain-restore"), final: true);
    }

    private sealed class Provider(WorkerJob job, string directory, ComparisonDrainRecovery? recorded = null)
    {
        public ComparisonProvider Kind => job.Provider ?? throw new ArgumentException("Missing comparison provider.");
        public ComparisonProvider Product => ComparisonDrain.Product(Kind);
        public bool Paused => ComparisonDrain.IsPaused(Kind);
        private ComparisonDrainRecovery? recovery = recorded;
        private WriteCacheState? bypassBefore;
        private string Journal => Path.Combine(directory, RecoveryFile);
        private string Cli => job.PrimoCli ?? ComparisonDrain.DefaultPrimoCli;

        private async Task<string> Primo(string prefix, params string[] args)
        {
            var timer = Stopwatch.StartNew();
            var result = await OwnedProcess.RunAsync(Cli, args, prefix, TimeSpan.FromSeconds(180), CancellationToken.None);
            RunStorage.AtomicJson(prefix + ".rpc.json", new { Seconds = timer.Elapsed.TotalSeconds, Command = args });
            if (result.ExitCode != 0 || !string.IsNullOrWhiteSpace(result.Error))
                throw new IOException($"PrimoCache CLI failed ({result.ExitCode}); inspect {prefix}.*. Close the PrimoCache GUI.");
            return result.Output;
        }

        public async Task PreflightAsync(ComparisonTarget target)
        {
            if (Product != ComparisonProvider.PrimoCache && LoadedDriverInspection.CaptureThirdParty().Any(d => d.Name.Equals("rxfcv.sys", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("A PrimoCache driver is loaded; Direct and QueueCache require a VM state without PrimoCache.");
            WriteCacheState? original = null;
            Guid? originalPrimoTaskId = null;
            ulong? originalGatherPolicy = null;
            if (Product == ComparisonProvider.QueueCache)
            {
                ManagedBrokerObservation? broker;
                try { broker = WindowsManagedBrokerService.Observe(); }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1060) { broker = null; }
                RunStorage.AtomicJson(Path.Combine(directory, "compare-broker-startup.json"), new { Installed = broker is not null, Broker = broker });
                ComparisonDrain.ValidateBrokerStartup(broker);
                var problems = DriverRegistration.Inspect().Problems();
                if (problems.Count != 0) throw new IOException("Unsupported QueueCache registration: " + string.Join(" ", problems));
                using var cache = new CacheDevice(job.Volume);
                original = cache.GetWriteCacheState();
                ValidateQueueCache(cache, original, active: true);
                var performance = cache.GetPerformance();
                if (performance.DrainGatherPolicy is > 0)
                    throw new IOException("Comparison must start with the current default-off gather policy.");
                if (job.Operation == DrainBatching.Suite)
                {
                    originalGatherPolicy = performance.DrainGatherPolicy ?? throw new IOException("Batching requires native V5 evidence.");
                    DrainBatching.ValidateProfile(original with { Performance = performance }, DrainGatherPolicy.Current);
                }
            }
            else if (Product == ComparisonProvider.PrimoCache)
            {
                await Primo(Path.Combine(directory, "primo-perf-help"), "?", "perf");
                await Primo(Path.Combine(directory, "primo-pause-help"), "?", "pause");
                originalPrimoTaskId = await ValidatePrimoAsync(Path.Combine(directory, "primo-original"));
            }
            recovery = new(job.Volume, Kind, Product == ComparisonProvider.PrimoCache ? Cli : null, target, false, original, originalPrimoTaskId,
                originalGatherPolicy);
            RunStorage.AtomicJson(Journal, recovery);
            ComparisonScenarios.FlushVolume(target.Letter);
            await ProductDrainAsync(Path.Combine(directory, "compare-drain-initial-drain"));
            ComparisonDrain.ValidatePending(await PendingAsync(Path.Combine(directory, "compare-drain-original"), active: true), Kind, clean: true);
            // A paused provider stays paused until the run's restoration; its verification bypass is this pause.
            if (Paused) await PauseProductAsync(Path.Combine(directory, "compare-drain-run-pause"));
        }

        private static void ValidateQueueCache(CacheDevice cache, WriteCacheState state, bool active)
            => ComparisonDrain.ValidateQueueCache(state with { Performance = cache.GetPerformance() }, active);

        public async Task SelectGatherAsync(DrainGatherPolicy policy, string prefix)
        {
            if (Kind != ComparisonProvider.QueueCache || recovery?.OriginalGatherPolicy is not (0 or 1) || !Enum.IsDefined(policy))
                throw new InvalidDataException("Gather mutation requires an observed original policy and QueueCache provider.");
            using var cache = new CacheDevice(job.Volume, writable: true);
            var timer = Stopwatch.StartNew();
            for (var attempt = 1; ; ++attempt)
            {
                var state = cache.GetWriteCacheState() with { Performance = cache.GetPerformance() };
                ValidateQueueCache(cache, state, active: true);
                RunStorage.AtomicJson(prefix + $".state-{attempt:D4}.json", state);
                if (state.Performance?.DrainGatherPolicy == (ulong)policy) return;
                if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Gather switch did not reach a clean boundary.");
                if (state.DirtyBytes != 0 || state.InFlightBytes != 0)
                {
                    cache.Control(WriteCacheAction.Flush);
                    continue;
                }
                recovery = recovery with { GatherRestoreRequired = true };
                RunStorage.AtomicJson(Journal, recovery); // durable before the first policy mutation
                try { cache.Control(WriteCacheAction.LabDrainGather, value: (ulong)policy); }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 170)
                {
                    // A newly dirty block raced the clean observation. Never retry faults or invalid evidence.
                    await Task.Delay(25);
                    continue;
                }
                var applied = cache.GetWriteCacheState() with { Performance = cache.GetPerformance() };
                DrainBatching.ValidateProfile(applied, policy);
                RunStorage.AtomicJson(prefix + ".applied.json", applied);
                return;
            }
        }

        private async Task<ComparisonPrimoObservation> CapturePrimoAsync(string prefix)
        {
            var status = await Primo(prefix, "status", "-a");
            var volumes = await Primo(prefix + ".volumes", "ls", "-v=1");
            return new(status, volumes);
        }

        private async Task<Guid> ValidatePrimoAsync(string prefix)
        {
            var observation = await CapturePrimoAsync(prefix);
            ComparisonDrain.ValidatePrimoConfiguration(observation.Status);
            return ComparisonDrain.PrimoTaskId(observation, job.Volume, paused: false);
        }

        public async Task<ComparisonPending> PendingAsync(string prefix, bool? active = null)
        {
            ComparisonPending pending;
            if (Product == ComparisonProvider.QueueCache)
            {
                using var cache = new CacheDevice(job.Volume);
                var state = cache.GetWriteCacheState() with { Performance = cache.GetPerformance() };
                ValidateQueueCache(cache, state, active: active ?? !Paused);
                if (recovery?.OriginalState is { } original && (state.Instance != original.Instance || state.Errors != original.Errors || state.Options != original.Options))
                    throw new InvalidDataException("QueueCache identity, error count or options changed during comparison.");
                RunStorage.AtomicJson(prefix + ".cache.json", state);
                pending = new(DateTimeOffset.UtcNow, Kind, state.DirtyBytes, state.InFlightBytes, null);
            }
            else if (Product == ComparisonProvider.PrimoCache)
            {
                var output = await Primo(prefix + ".perf", "perf", "-v", job.Volume[..1]);
                pending = new(DateTimeOffset.UtcNow, Kind, null, null, ComparisonDrain.ParseDeferredBlocks(output));
            }
            else pending = new(DateTimeOffset.UtcNow, Kind, null, null, null);
            ComparisonDrain.ValidatePending(pending, Kind, clean: false);
            RunStorage.AtomicJson(prefix + ".pending.json", pending);
            return pending;
        }

        public async Task DrainAsync(string prefix)
        {
            if (!Paused) await ProductDrainAsync(prefix); // a paused product has nothing pending
        }

        private async Task ProductDrainAsync(string prefix)
        {
            if (Product == ComparisonProvider.QueueCache)
            {
                using var cache = new CacheDevice(job.Volume, writable: true);
                if (cache.GetWriteCacheState().Faulted) throw new IOException("Faulted cache; explicit drain refused.");
                cache.Control(WriteCacheAction.Flush);
            }
            else if (Product == ComparisonProvider.PrimoCache) await Primo(prefix, "dwflush", "-v", job.Volume[..1], "-s");
            // Direct's explicit volume flush was issued by the caller.
        }

        public async Task ClearAsync(string prefix)
        {
            if (Paused) return;
            if (Product == ComparisonProvider.QueueCache) { using var cache = new CacheDevice(job.Volume, writable: true); cache.Control(WriteCacheAction.DropClean); }
            else if (Product == ComparisonProvider.PrimoCache) await Primo(prefix, "clear", "-v", job.Volume[..1], "-s");
        }

        /// <summary>Verification bypass. A paused provider is already bypassed for the whole run.</summary>
        public Task PauseAsync(string prefix) => Kind == ComparisonProvider.Direct || Paused ? Task.CompletedTask : PauseProductAsync(prefix);

        private async Task PauseProductAsync(string prefix)
        {
            recovery = (recovery ?? throw new InvalidOperationException("No original state.")) with { ResumeRequired = true };
            RunStorage.AtomicJson(Journal, recovery); // before the mutation, including a partly failed pause
            if (Product == ComparisonProvider.QueueCache)
            {
                using var cache = new CacheDevice(job.Volume, writable: true);
                WriteCacheState Capture()
                {
                    var state = cache.GetWriteCacheState();
                    try { return state with { Performance = cache.GetPerformance() }; }
                    catch
                    {
                        RunStorage.AtomicJson(prefix + ".performance-failure.cache.json", state);
                        throw;
                    }
                }
                bypassBefore = await ComparisonDrain.PauseQueueCacheAsync(recovery.OriginalState ?? throw new InvalidDataException("Missing original QueueCache state."),
                    Capture, () => cache.Control(WriteCacheAction.Disable),
                    (phase, state) => RunStorage.AtomicJson(prefix + "." + phase + ".cache.json", state));
            }
            else
            {
                await Primo(prefix, "pause", "-v", job.Volume[..1], "-s");
                var status = await Primo(prefix + ".status", "status", "-a");
                var volumes = await Primo(prefix + ".volumes", "ls", "-v=1");
                ComparisonDrain.ValidatePrimoVolumeState(status, volumes, job.Volume, paused: true);
            }
        }

        /// <summary>A paused QueueCache compares against its run-start pause, so every scored write is covered too.</summary>
        public async Task ConfirmBypassAsync(string prefix)
        {
            if (Product == ComparisonProvider.QueueCache)
            {
                using var cache = new CacheDevice(job.Volume);
                var after = cache.GetWriteCacheState() with { Performance = cache.GetPerformance() };
                RunStorage.AtomicJson(prefix + ".bypass-before.cache.json", bypassBefore ?? throw new InvalidDataException("Missing pre-oracle bypass state."));
                RunStorage.AtomicJson(prefix + ".bypass-after.cache.json", after);
                ComparisonDrain.ValidateBypass(bypassBefore!, after);
            }
            else if (Product == ComparisonProvider.PrimoCache)
            {
                var observation = await CapturePrimoAsync(prefix + ".bypass-after");
                RunStorage.AtomicJson(prefix + ".bypass-primo.json", observation);
                if (ComparisonDrain.PrimoTaskId(observation, job.Volume, paused: true) != recovery?.OriginalPrimoTaskId)
                    throw new InvalidDataException("Primo bypass task changed during file verification.");
            }
        }

        /// <summary>After each verification, or with final at the run's restoration. A paused provider resumes only then.</summary>
        public async Task RestoreAsync(string prefix, bool final = false)
        {
            if (recovery is null || Paused && !final) return;
            if (recovery.ResumeRequired && Product == ComparisonProvider.QueueCache)
            {
                using var cache = new CacheDevice(job.Volume, writable: true);
                var state = ComparisonDrain.RestoreQueueCache(recovery.OriginalState ?? throw new InvalidDataException("Missing original QueueCache state."),
                    cache.GetWriteCacheState, () => cache.Control(WriteCacheAction.Enable));
                ValidateQueueCache(cache, state, active: true);
            }
            else if (recovery.ResumeRequired && Product == ComparisonProvider.PrimoCache)
            {
                var captures = 0;
                await ComparisonDrain.RestorePrimoAsync(recovery.OriginalPrimoTaskId, job.Volume,
                    () => CapturePrimoAsync(prefix + $".state-{++captures:D2}"),
                    () => Primo(prefix, "resume", "-v", job.Volume[..1], "-s"));
            }
            recovery = recovery with { ResumeRequired = false }; RunStorage.AtomicJson(Journal, recovery);
            if (recovery.GatherRestoreRequired)
            {
                await SelectGatherAsync((DrainGatherPolicy)(recovery.OriginalGatherPolicy ??
                    throw new InvalidDataException("Missing original gather policy during restoration.")), prefix + ".gather");
                recovery = recovery with { GatherRestoreRequired = false }; RunStorage.AtomicJson(Journal, recovery);
            }
        }
    }
}
