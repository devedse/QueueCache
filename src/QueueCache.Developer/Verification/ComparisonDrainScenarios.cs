using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

public sealed record ComparisonDrainRecovery([property: JsonRequired] string Volume,
    [property: JsonRequired] ComparisonProvider Provider, [property: JsonRequired] string? PrimoCli,
    [property: JsonRequired] ComparisonTarget Target, [property: JsonRequired] bool ResumeRequired,
    [property: JsonRequired] WriteCacheState? OriginalState, Guid? OriginalPrimoTaskId = null);

[SupportedOSPlatform("windows")]
internal static class ComparisonDrainScenarios
{
    internal const string RecoveryFile = "compare-drain-recovery.json";

    public static async Task<IReadOnlyList<CheckResult>> RunAsync(WorkerJob job, ComparisonTarget target, IReadOnlyList<ComparisonCase> plan)
    {
        if (target.DiskNumber is null || target.VolumeName is null || target.FreeBytes < 7L << 30)
            throw new IOException("compare-drain requires an observable physical backing disk and at least 7 GiB free.");
        var directory = Path.GetDirectoryName(job.Reply)!;
        var provider = new Provider(job, directory);
        await provider.PreflightAsync(target);
        var work = job.WorkDirectory ?? throw new InvalidDataException("Missing compare-drain work directory.");
        Directory.CreateDirectory(work);
        Progress("Preparing unique full-GiB payload and every SHA-256 oracle outside timing.");
        using var files = new ComparisonDrainFiles();
        RunStorage.AtomicJson(Path.Combine(directory, "compare-drain-oracle.json"), new
        {
            Algorithm = "SplitMix64 little-endian", ComparisonDrain.Seed, ComparisonDrain.PayloadBytes,
            ComparisonDrain.LargeBytes, ComparisonDrain.SmallBytes, SmallFileCount = ComparisonDrain.SmallFiles,
            LargeSha256 = files.LargeHash, SmallFiles = files.SmallHashes,
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
                if (provider.Kind == ComparisonProvider.QueueCache)
                    File.Copy(prefix + $".prepare-{prepared.Attempt:D4}-pending.cache.json", prefix + ".before.cache.json");
                var diskBefore = ComparisonScenarios.DiskCounters(target.DiskNumber.Value);
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
                    var pass = files.Write(path, test.Kind == "drain-small", at =>
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
                    score = new(test.Id, provider.Kind, expectedBytes, test.Kind == "drain-small" ? ComparisonDrain.SmallFiles : 1,
                        readyUtc, pass.Started, pass.Ended, requested, returned, ended,
                        pass.Seconds, total - pass.Seconds, explicitSeconds, confirmationSeconds, total, expectedBytes / total / 1_000_000.0,
                        before, after, diskBefore, diskAfter, samples.ToArray());
                    // Preserve the completed measurement even when coverage, bypass, a hash or restoration fails.
                    RunStorage.AtomicJson(prefix + ".score.json", score);
                    ComparisonDrain.ValidateScore(test, score, provider.Kind);
                }
                finally { stop.Cancel(); await observer; }

                Progress(test.Id + ": measurement saved; bypassing product cache and verifying every file (untimed).");
                var hashes = await ComparisonDrain.VerifyBypassedAsync(test.Id,
                    () => provider.PauseAsync(prefix + ".pause"), () => files.Verify(path, test.Kind == "drain-small"),
                    () => provider.RestoreAsync(prefix + ".resume"), verify => RunStorage.AtomicJson(prefix + ".verify.json", verify));
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
        await provider.RestoreAsync(Path.Combine(Path.GetDirectoryName(job.Reply)!, "compare-drain-restore"));
    }

    private sealed class Provider(WorkerJob job, string directory, ComparisonDrainRecovery? recorded = null)
    {
        public ComparisonProvider Kind => job.Provider ?? throw new ArgumentException("Missing comparison provider.");
        private ComparisonDrainRecovery? recovery = recorded;
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
            if (Kind != ComparisonProvider.PrimoCache && LoadedDriverInspection.CaptureThirdParty().Any(d => d.Name.Equals("rxfcv.sys", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("A PrimoCache driver is loaded; Direct and QueueCache require a VM state without PrimoCache.");
            WriteCacheState? original = null;
            Guid? originalPrimoTaskId = null;
            if (Kind == ComparisonProvider.QueueCache)
            {
                var problems = DriverRegistration.Inspect().Problems();
                if (problems.Count != 0) throw new IOException("Unsupported QueueCache registration: " + string.Join(" ", problems));
                using var cache = new CacheDevice(job.Volume);
                original = cache.GetWriteCacheState();
                ValidateQueueCache(cache, original, active: true);
            }
            else if (Kind == ComparisonProvider.PrimoCache)
            {
                await Primo(Path.Combine(directory, "primo-perf-help"), "?", "perf");
                await Primo(Path.Combine(directory, "primo-pause-help"), "?", "pause");
                originalPrimoTaskId = await ValidatePrimoAsync(Path.Combine(directory, "primo-original"));
            }
            recovery = new(job.Volume, Kind, Kind == ComparisonProvider.PrimoCache ? Cli : null, target, false, original, originalPrimoTaskId);
            RunStorage.AtomicJson(Journal, recovery);
            ComparisonScenarios.FlushVolume(target.Letter);
            await DrainAsync(Path.Combine(directory, "compare-drain-initial-drain"));
            ComparisonDrain.ValidatePending(await PendingAsync(Path.Combine(directory, "compare-drain-original")), Kind, clean: true);
        }

        private static void ValidateQueueCache(CacheDevice cache, WriteCacheState state, bool active)
            => ComparisonDrain.ValidateQueueCache(state with { Performance = cache.GetPerformance() }, active);

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

        public async Task<ComparisonPending> PendingAsync(string prefix)
        {
            ComparisonPending pending;
            if (Kind == ComparisonProvider.QueueCache)
            {
                using var cache = new CacheDevice(job.Volume);
                var state = cache.GetWriteCacheState() with { Performance = cache.GetPerformance() };
                ValidateQueueCache(cache, state, active: true);
                if (recovery?.OriginalState is { } original && (state.Instance != original.Instance || state.Errors != original.Errors || state.Options != original.Options))
                    throw new InvalidDataException("QueueCache identity, error count or options changed during comparison.");
                RunStorage.AtomicJson(prefix + ".cache.json", state);
                pending = new(DateTimeOffset.UtcNow, Kind, state.DirtyBytes, state.InFlightBytes, null);
            }
            else if (Kind == ComparisonProvider.PrimoCache)
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
            if (Kind == ComparisonProvider.QueueCache)
            {
                using var cache = new CacheDevice(job.Volume, writable: true);
                if (cache.GetWriteCacheState().Faulted) throw new IOException("Faulted cache; explicit drain refused.");
                cache.Control(WriteCacheAction.Flush);
            }
            else if (Kind == ComparisonProvider.PrimoCache) await Primo(prefix, "dwflush", "-v", job.Volume[..1], "-s");
            // Direct's explicit volume flush was issued by the caller.
        }

        public async Task ClearAsync(string prefix)
        {
            if (Kind == ComparisonProvider.QueueCache) { using var cache = new CacheDevice(job.Volume, writable: true); cache.Control(WriteCacheAction.DropClean); }
            else if (Kind == ComparisonProvider.PrimoCache) await Primo(prefix, "clear", "-v", job.Volume[..1], "-s");
        }

        public async Task PauseAsync(string prefix)
        {
            if (Kind == ComparisonProvider.Direct) return;
            recovery = (recovery ?? throw new InvalidOperationException("No original state.")) with { ResumeRequired = true };
            RunStorage.AtomicJson(Journal, recovery); // before the mutation, including a partly failed pause
            if (Kind == ComparisonProvider.QueueCache)
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
                await ComparisonDrain.PauseQueueCacheAsync(recovery.OriginalState ?? throw new InvalidDataException("Missing original QueueCache state."),
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

        public async Task RestoreAsync(string prefix)
        {
            if (recovery?.ResumeRequired != true) return;
            if (Kind == ComparisonProvider.QueueCache)
            {
                using var cache = new CacheDevice(job.Volume, writable: true);
                var state = ComparisonDrain.RestoreQueueCache(recovery.OriginalState ?? throw new InvalidDataException("Missing original QueueCache state."),
                    cache.GetWriteCacheState, () => cache.Control(WriteCacheAction.Enable));
                ValidateQueueCache(cache, state, active: true);
            }
            else if (Kind == ComparisonProvider.PrimoCache)
            {
                var captures = 0;
                await ComparisonDrain.RestorePrimoAsync(recovery.OriginalPrimoTaskId, job.Volume,
                    () => CapturePrimoAsync(prefix + $".state-{++captures:D2}"),
                    () => Primo(prefix, "resume", "-v", job.Volume[..1], "-s"));
            }
            recovery = recovery with { ResumeRequired = false }; RunStorage.AtomicJson(Journal, recovery);
        }
    }
}
