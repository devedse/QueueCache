using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using QueueCache.Management;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Developer.Verification;

public sealed record RamReadReferenceOwnership(ulong OriginalReservedBytes, Guid[] OriginalResources,
    ManagedDiskDefinition[] Definitions);

/// <summary>Owns disposable broker fixtures. Every intended definition is journaled before Create;
/// the coordinator repeats guarded cleanup under its independent restoration deadline.</summary>
[SupportedOSPlatform("windows")]
internal static class RamReadReferenceScenarios
{
    public static async Task<IReadOnlyList<CheckResult>> RunAsync(CacheDevice host, string diskspd, int repeats,
        int seconds, string ownership, string evidence, bool includeQueue = false)
    {
        if (Process.GetCurrentProcess().PriorityClass != ProcessPriorityClass.Normal)
            throw new IOException("RAM read references require normal CPU priority and a normal I/O/memory-priority launch environment.");
        var service = new ManagedDiskBrokerClient();
        var journal = new RamReadReferenceOwnership(host.GetWriteCacheState().GlobalReservedBytes,
            (await service.ListAsync()).Select(r => r.ResourceId).ToArray(), []);
        if (File.Exists(ownership)) throw new IOException("RAM reference ownership journal already exists.");
        RunStorage.AtomicJson(ownership, journal);
        var plan = RamReadReferencePlan.Cases(repeats, includeQueue);
        RunStorage.AtomicJson(evidence + ".plan.json", new { Cases = plan, Seconds = seconds, Measurement = "Read reference; no speed acceptance verdict" });
        var checks = new List<CheckResult>();
        foreach (var group in plan.GroupBy(s => new { s.Repeat, s.Access, s.RamReadQueueMode }))
        {
            var definition = ManagedDiskDefinition.New(ManagedDiskMode.EphemeralRam) with
            {
                CapacityBytes = RamReadReferencePlan.DiskMiB * (1UL << 20), Access = group.Key.Access,
                PreferredLetter = FreeLetter(), Label = "QC-ReadRef-" + Guid.NewGuid().ToString("N")[..8]
            };
            journal = journal with { Definitions = [.. journal.Definitions, definition] };
            RunStorage.AtomicJson(ownership, journal);
            try
            {
                await service.CreateAsync(definition, new ProgressLog());
                var current = (await service.ListAsync()).Single(r => r.ResourceId == definition.ResourceId);
                if (current.Runtime?.State != ManagedDiskState.Ready || current.VolumePath is null || current.PhysicalDiskNumber is null)
                    throw new IOException("Owned RAM reference did not become Ready.");
                var expectedNative = current.Native ?? throw new IOException("Owned RAM reference has no native identity.");
                if (includeQueue)
                {
                    using var fixture = CacheDevice.OpenVolumeName(current.VolumePath);
                    if (fixture.GetDiagnostics().RamReadQueue is null)
                        throw new IOException("RAM read queue comparison requires V22 scheduling diagnostics.");
                    fixture.Control(WriteCacheAction.LabRamReadQueue, value: (ulong)group.Key.RamReadQueueMode);
                }
                var file = definition.PreferredLetter + @":\ram-read-reference.dat";
                var expectedHash = RamReadFileOracle.Prepare(file, RamReadReferencePlan.FileMiB);
                foreach (var scenario in group)
                {
                    var prefix = evidence + "." + scenario.Id;
                    Console.WriteLine($"Reference {scenario.Id} ({checks.Count + 1} of {plan.Count})"); Console.Out.Flush();
                    var beforeHash = RamReadFileOracle.Verify(file, RamReadReferencePlan.FileMiB, prefix + ".before");
                    if (beforeHash != expectedHash) throw new InvalidDataException("RAM reference oracle hash changed.");
                    var before = await Snapshot();
                    var samples = new List<RamReadReferenceSample>();
                    using var provider = WindowsRamDisk.Connect();
                    using var sampledDevice = CacheDevice.OpenVolumeName(current.VolumePath);
                    using var stop = new CancellationTokenSource();
                    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    // A dedicated sleeping thread avoids broker inventory requests and
                    // thread-pool continuations competing with saturated copy workers.
                    var observer = Task.Factory.StartNew(Observe, CancellationToken.None,
                        TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    DiskSpdScore? score = null; DateTimeOffset start = default, end = default;
                    using var child = new CancellationTokenSource();
                    Task<ProcessResult>? workload = null;
                    Exception? failure = null;
                    try
                    {
                        // Forbid writes to the payload while allowing the read-only
                        // DiskSpd handle. NTFS may still write unrelated volume metadata.
                        using var payloadGuard = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                        await Task.WhenAny(ready.Task, observer).WaitAsync(TimeSpan.FromSeconds(45));
                        if (observer.IsCompleted) { await observer; throw new IOException("RAM telemetry ended before readiness."); }
                        await ready.Task;
                        RunStorage.AtomicJson(prefix + ".ready.json", new { Ready = true, At = samples[0].Utc });
                        start = DateTimeOffset.UtcNow;
                        workload = OwnedProcess.RunAsync(diskspd, [.. RamReadReferencePlan.Arguments(scenario, seconds), file],
                            prefix, TimeSpan.FromSeconds(seconds + 120), child.Token);
                        if (await Task.WhenAny(workload, observer) == observer) { await observer; throw new IOException("RAM telemetry ended during the workload."); }
                        var result = await workload;
                        if (result.ExitCode != 0) throw new IOException($"RAM reference DiskSpd exit {result.ExitCode}; inspect raw stderr/stdout.");
                        score = DiskSpdParser.Parse(result.Output);
                        end = DateTimeOffset.UtcNow;
                    }
                    catch (Exception ex) { failure = ex; throw; }
                    finally
                    {
                        child.Cancel(); stop.Cancel();
                        if (workload is not null)
                            try { await workload; } catch (Exception) when (failure is not null) { }
                        try { await observer; } catch (Exception) when (failure is not null) { }
                    }
                    var after = await Snapshot();
                    RunStorage.AtomicJson(prefix + ".boundaries.json", new { Before = before, After = after });
                    RunStorage.AtomicJson(prefix + "-interval.json", new { Start = start, End = end, MaximumSampleGapSeconds = 2 });
                    RamReadReferenceEvidence.Validate(scenario, samples, before, after, score!, start, end);
                    var afterHash = RamReadFileOracle.Verify(file, RamReadReferencePlan.FileMiB, prefix + ".after");
                    RunStorage.AtomicJson(prefix + ".oracle.json", new { Expected = expectedHash, Before = beforeHash, After = afterHash, Bytes = 1L << 30 });
                    if (afterHash != expectedHash) throw new InvalidDataException("RAM reference byte oracle changed after scoring.");
                    RunStorage.AtomicJson(prefix + ".score.json", score!);
                    checks.Add(new(scenario.Id, "PASS", $"Reference {score!.MiBPerSecond:F2} MiB/s; whole-file bytes/access/lifecycle and telemetry checked. No speed acceptance verdict."));
                    async Task<RamReadReferenceBoundary> Snapshot()
                    {
                        var record = (await service.ListAsync()).Single(r => r.ResourceId == definition.ResourceId);
                        if (record.Definition != definition || record.Runtime is not { } runtime || record.Native is not { } native ||
                            record.Statistics is not { } statistics || record.ImageIo is not { } io || record.VolumePath != current.VolumePath)
                            throw new IOException("RAM reference identity/statistics are missing or changed.");
                        native.RequireSameCreation(expectedNative);
                        if (native.Errors != 0) throw new IOException("RAM reference provider recorded an error.");
                        using var device = CacheDevice.OpenVolumeName(record.VolumePath!);
                        return new(DateTimeOffset.UtcNow, record.ResourceId, runtime.BootEpoch, runtime.CreationGeneration,
                            runtime.WriteGeneration, runtime.State, device.GetWriteCacheState().Enabled,
                            (native.Flags & RamDiskFlags.Timing) != 0, device.GetRamDirectState(), statistics.ReadRequests,
                            io.ReadAttempts, io.WriteAttempts, record.LastError ?? runtime.LastError, statistics.WriteRequests, native.WriteBytes,
                            device.GetDiagnostics().RamReadQueue);
                    }
                    void Observe()
                    {
                        using var log = new StreamWriter(prefix + ".telemetry.jsonl");
                        for (;;)
                        {
                            var native = provider.Query(expectedNative);
                            native.RequireSameCreation(expectedNative);
                            var statistics = provider.Statistics(expectedNative)
                                ?? throw new IOException("RAM reference provider statistics are unavailable.");
                            var enabled = sampledDevice.GetWriteCacheState().Enabled;
                            var direct = sampledDevice.GetRamDirectState();
                            var queue = sampledDevice.GetDiagnostics().RamReadQueue;
                            var sample = new RamReadReferenceSample(DateTimeOffset.UtcNow, native, statistics, enabled, direct, queue);
                            samples.Add(sample);
                            log.WriteLine(JsonSerializer.Serialize(sample)); log.Flush();
                            ready.TrySetResult();
                            if (stop.IsCancellationRequested) break;
                            stop.Token.WaitHandle.WaitOne(1000);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RunStorage.AtomicJson(evidence + $".r{group.Key.Repeat}-{group.Key.Access}-queue{group.Key.RamReadQueueMode}.failure.json", new { Error = ex.ToString() });
                throw;
            }
            finally { await CleanupAsync(host, ownership, evidence + $".r{group.Key.Repeat}-{group.Key.Access}-queue{group.Key.RamReadQueueMode}.cleanup.json"); }
        }
        return checks;
    }

    public static async Task CleanupAsync(CacheDevice host, string ownership, string evidence)
    {
        var journal = JsonSerializer.Deserialize<RamReadReferenceOwnership>(await File.ReadAllTextAsync(ownership))
            ?? throw new InvalidDataException("Missing RAM reference ownership.");
        if (journal.Definitions.Select(d => d.ResourceId).Distinct().Count() != journal.Definitions.Length)
            throw new InvalidDataException("Duplicate RAM reference ownership IDs.");
        var service = new ManagedDiskBrokerClient(); var removed = new List<Guid>();
        RunStorage.AtomicJson(evidence + ".before.json", new { Records = await service.ListAsync(), Ownership = journal });
        foreach (var definition in journal.Definitions)
        {
            var record = (await service.ListAsync()).SingleOrDefault(r => r.ResourceId == definition.ResourceId);
            if (record is null) continue;
            RamReadReferenceEvidence.ValidateOwnedDefinition(definition, record.Definition, journal.OriginalResources);
            if (record.Runtime is { State: not ManagedDiskState.Stopped } runtime)
            {
                if (runtime.State != ManagedDiskState.Ready) throw new IOException("RAM reference is not Ready/Stopped; preserve the fixture and inspect recorded state.");
                await service.ExecuteAsync(new(record.ResourceId, ManagedDiskAction.Stop, ManagedDiskExpected.From(runtime),
                    ManagedDiskStopIntent.DiscardThenStop, AcceptDiscard: true), new ProgressLog());
            }
            record = (await service.ListAsync()).Single(r => r.ResourceId == definition.ResourceId);
            RamReadReferenceEvidence.ValidateOwnedDefinition(definition, record.Definition, journal.OriginalResources);
            await service.ExecuteAsync(new(record.ResourceId, ManagedDiskAction.RemoveDefinition,
                record.Runtime is null ? null : ManagedDiskExpected.From(record.Runtime)), new ProgressLog());
            removed.Add(record.ResourceId);
        }
        var resources = (await service.ListAsync()).Select(r => r.ResourceId).Order().ToArray();
        var reserved = host.GetWriteCacheState().GlobalReservedBytes;
        RunStorage.AtomicJson(evidence, new { Removed = removed, Resources = resources, OriginalResources = journal.OriginalResources,
            OriginalReservedBytes = journal.OriginalReservedBytes, ReservedBytes = reserved });
        if (!resources.SequenceEqual(journal.OriginalResources.Order()) || reserved != journal.OriginalReservedBytes)
            throw new IOException("RAM reference cleanup did not restore original resources and shared reservation.");
    }
    private static char FreeLetter() => Enumerable.Range('G', 'Z' - 'G' + 1).Select(n => (char)n)
        .FirstOrDefault(c => !Directory.Exists(c + @":\") && DriveInfo.GetDrives().All(d => char.ToUpperInvariant(d.Name[0]) != c)) is var letter && letter != '\0'
        ? letter : throw new IOException("No free drive letter for an owned RAM reference.");
    private sealed class ProgressLog : IProgress<ManagedDiskProgress>
    {
        public void Report(ManagedDiskProgress value) { Console.WriteLine($"{value.Stage}: {value.Message}"); Console.Out.Flush(); }
    }
}
