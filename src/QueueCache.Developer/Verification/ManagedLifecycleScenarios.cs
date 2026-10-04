using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using QueueCache.Management;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Developer.Verification;

/// <summary>Prepare/verify phases persist independent byte evidence; never reboot, hibernate or kill a process.</summary>
[SupportedOSPlatform("windows")]
internal static class ManagedLifecycleScenarios
{
    public static async Task<IReadOnlyList<CheckResult>> RunAsync(WorkerJob job, DiskTarget host, CacheDevice device)
    {
        var service = new ManagedDiskBrokerClient(); var trace = new List<object>();
        var evidence = job.Reply + ".managed-lifecycle.json";
        var preparing = job.Operation is "managed-lifecycle-prepare" or "managed-broker-restart";
        var manifestPath = job.ManagedOraclePath ?? throw new ArgumentException("A managed lifecycle evidence path is required.");
        ManagedDiskPaths.ValidateLocalDirectory(Path.GetDirectoryName(manifestPath));
        ManagedDiskHostProtection.ValidateWritableDirectory(Path.GetDirectoryName(manifestPath)!);
        ManagedLifecycleManifest manifest;
        try { manifest = preparing ? await PrepareAsync() : Read(manifestPath); }
        catch (Exception ex)
        {
            trace.Add(new { Stage = "PreparationOrManifestReadFailed", Error = ex.ToString(), Manifest = manifestPath, PartialRecipes = manifestPath + ".preparing.json", ResourcesRetained = true });
            RunStorage.AtomicJson(evidence, trace); throw;
        }
        try
        {
            if (manifest.Machine != Environment.MachineName || !string.Equals(manifest.Host.Instance, host.Instance, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(manifest.Host.VolumeId, host.VolumeId, StringComparison.OrdinalIgnoreCase) ||
                manifest.Host.Bytes != host.Bytes || manifest.Host.DiskBytes != host.DiskBytes)
                throw new IOException("Lifecycle manifest machine/host disk identity differs from this run.");
            if (job.Operation == "managed-lifecycle-prepare")
                return [new("managed-lifecycle-prepared", "PASS", "Five owned fixtures and byte oracles retained. Preparation is complete; no transition was performed or qualified. Manifest: " + manifestPath)];
            if (job.Operation == "managed-lifecycle-cleanup")
            { await CleanupAsync(); return [new("managed-lifecycle-cleanup", "PASS", "Only manifest-owned resources stopped/forgotten. Source/checkpoint images and evidence retained.")]; }
            var transition = job.Operation == "managed-broker-restart" ? ManagedLifecycleTransition.BrokerRestart :
                job.ManagedTransition ?? throw new ArgumentException("Declare the externally observed lifecycle transition.");
            if (job.Operation == "managed-broker-restart")
            {
                var inventory = await service.ListAsync(); RequireOwnedInventory(inventory);
                trace.Add(new { Stage = "ServiceBeforeStop", Broker = WindowsManagedBrokerService.Observe() }); RunStorage.AtomicJson(evidence, trace);
                await WindowsManagedBrokerService.StopAsync();
                trace.Add(new { Stage = "ServiceStopped", At = DateTimeOffset.UtcNow }); RunStorage.AtomicJson(evidence, trace);
                WindowsManagedBrokerService.Start();
            }
            var records = await WaitInventoryAsync();
            var broker = WindowsManagedBrokerService.Observe();
            trace.Add(new { Stage = "ServiceAfterTransition", Broker = broker, Transition = transition.ToString() }); RunStorage.AtomicJson(evidence, trace);
            if (!broker.IsRunning || transition is ManagedLifecycleTransition.BrokerRestart or ManagedLifecycleTransition.BrokerCrash &&
                broker.ProcessId == manifest.Broker.ProcessId && broker.StartedUtc == manifest.Broker.StartedUtc)
                throw new IOException("The requested broker transition lacks a new running service process identity.");
            using var provider = WindowsRamDisk.Connect();
            var session = provider.StartupSession().BootEpoch;
            ManagedLifecycleEvidence.ValidateTransition(manifest, session, transition);
            foreach (var fixture in manifest.Fixtures)
            {
                var current = records.SingleOrDefault(r => r.ResourceId == fixture.Before.ResourceId) ?? throw new IOException("A remembered lifecycle resource disappeared.");
                ManagedLifecycleEvidence.ValidateRecord(fixture, current, session, transition);
                if (!ManagedLifecycleEvidence.ExpectStopped(fixture, transition))
                {
                    var sessionFile = FilePath(current, "session.bin");
                    if (ManagedLifecycleEvidence.IsNewStartup(transition) && current.Definition.Mode != ManagedDiskMode.CachedVhdx)
                    {
                        if (Directory.EnumerateFiles(current.VolumePath!).Any(path => Path.GetFileName(path).Equals("session.bin", StringComparison.OrdinalIgnoreCase)))
                            throw new IOException("New-startup RAM retained a preceding unsaved session file.");
                    }
                    else CheckBytes(sessionFile, fixture.SessionSha256);
                    if (current.Definition.Mode == ManagedDiskMode.ImageInRam)
                        CheckBytes(FilePath(current, "committed.bin"), fixture.CommittedSha256!);
                }
                trace.Add(new { Stage = "Verified", Transition = transition.ToString(), Fixture = fixture.Role, Record = current });
                RunStorage.AtomicJson(evidence, trace);
            }
            await CleanupAsync();
            return [new("managed-lifecycle-" + transition, "PASS", "Native startup epoch, exact resource creations, stopped recipe behavior and independent byte oracles matched. External power-transition provenance must be retained separately.")];
        }
        catch (Exception ex)
        {
            trace.Add(new { Stage = "Failed", Error = ex.ToString(), Manifest = manifestPath, ResourcesRetained = true });
            RunStorage.AtomicJson(evidence, trace); throw;
        }

        async Task<ManagedLifecycleManifest> PrepareAsync()
        {
            if ((await service.ListAsync()).Count != 0) throw new IOException("Lifecycle preparation requires an empty managed catalog so broker transitions cannot affect another owner's resources.");
            var hostState = device.GetWriteCacheState();
            if (hostState.Enabled && hostState.UnsafeDefer)
                throw new IOException("Lifecycle image host must be Strict or disabled before any fixture is created.");
            using var provider = WindowsRamDisk.Connect();
            var session = provider.StartupSession().BootEpoch; var baseline = device.GetWriteCacheState().GlobalReservedBytes;
            var definitions = new List<ManagedDiskDefinition>(); var fixtures = new List<ManagedLifecycleFixture>();
            var proof = Guid.NewGuid();
            foreach (var role in ManagedLifecycleEvidence.Roles)
            {
                var mode = role == "backed-auto" ? ManagedDiskMode.CachedVhdx : role == "image-auto" ? ManagedDiskMode.ImageInRam : ManagedDiskMode.EphemeralRam;
                var directory = Path.Combine(job.WorkDirectory!, proof.ToString("N"), role);
                var definition = ManagedDiskDefinition.New(mode) with { CapacityBytes = 64UL << 20, PreferredLetter = FreeLetter(definitions.Select(d => d.PreferredLetter)), Label = "QC-Lifecycle",
                    StartAtBoot = role != "ram-manual", Cache = mode == ManagedDiskMode.CachedVhdx ? new(64, CachePreset.Strict) : null,
                    ImagePath = mode == ManagedDiskMode.EphemeralRam ? null : Path.Combine(directory, "source.vhdx"),
                    CheckpointDirectory = mode == ManagedDiskMode.ImageInRam ? Path.Combine(directory, "Checkpoints") : null };
                definitions.Add(definition);
                // Preserve the owned recipe before the first allocation, even if creation fails.
                DurableWrite(manifestPath + ".preparing.json", new { Version = 1, Proof = proof, Session = session, Definitions = definitions, Fixtures = fixtures });
                await service.CreateAsync(definition, new ProgressLog());
                var record = await FindAsync(definition.ResourceId); string? committed = null;
                if (mode == ManagedDiskMode.ImageInRam)
                {
                    committed = Write(FilePath(record, "committed.bin"));
                    await ActAsync(record, ManagedDiskAction.Save);
                    record = await FindAsync(definition.ResourceId);
                }
                var hash = Write(FilePath(record, "session.bin"));
                await ActAsync(await FindAsync(definition.ResourceId), ManagedDiskAction.Flush);
                if (role == "ram-stopped-auto") await ActAsync(await FindAsync(definition.ResourceId), ManagedDiskAction.Stop, discard: true);
                fixtures.Add(new(role, await FindAsync(definition.ResourceId), hash, committed));
                trace.Add(new { Stage = "Prepared", Role = role, Resource = definition.ResourceId }); RunStorage.AtomicJson(evidence, trace);
            }
            if (provider.StartupSession().BootEpoch != session) throw new IOException("Windows startup changed during preparation; fixtures/evidence retained.");
            var prepared = new ManagedLifecycleManifest(1, proof, Environment.MachineName, session, host, baseline, DateTimeOffset.UtcNow, fixtures.ToArray(), WindowsManagedBrokerService.Observe());
            ManagedLifecycleEvidence.Validate(prepared); DurableWrite(manifestPath, prepared);
            return prepared;
        }
        async Task CleanupAsync()
        {
            foreach (var fixture in manifest.Fixtures)
            {
                var current = (await service.ListAsync()).SingleOrDefault(r => r.ResourceId == fixture.Before.ResourceId);
                if (current is null) continue;
                if (current.Definition != fixture.Before.Definition || current.Runtime is null)
                    throw new IOException("Cleanup refuses a changed resource recipe or absent runtime identity.");
                if (current.Runtime.State != ManagedDiskState.Stopped)
                    await ActAsync(current, ManagedDiskAction.Stop, discard: current.Definition.Mode != ManagedDiskMode.CachedVhdx);
                await ActAsync(await FindAsync(current.ResourceId), ManagedDiskAction.RemoveDefinition);
                trace.Add(new { Stage = "Cleaned", Resource = current.ResourceId }); RunStorage.AtomicJson(evidence, trace);
            }
            if (device.GetWriteCacheState().GlobalReservedBytes != manifest.BaselineReservedBytes)
                throw new IOException("Lifecycle teardown did not restore the recorded shared reservation. Inspect current independent cache profiles; no automatic defaults change was attempted.");
        }
        async Task<IReadOnlyList<ManagedDiskRecord>> WaitInventoryAsync()
        {
            var until = DateTimeOffset.UtcNow.AddMinutes(2);
            while (true)
            {
                try { return await service.ListAsync(); }
                catch (IOException) when (job.Operation == "managed-broker-restart" && DateTimeOffset.UtcNow < until)
                { await Task.Delay(250); }
            }
        }
        void RequireOwnedInventory(IReadOnlyList<ManagedDiskRecord> inventory)
        {
            if (!inventory.Select(r => r.ResourceId).ToHashSet().SetEquals(manifest.Fixtures.Select(f => f.Before.ResourceId)))
                throw new IOException("Broker restart refuses foreign or missing managed resources.");
        }
        async Task<ManagedDiskRecord> FindAsync(Guid id) => (await service.ListAsync()).Single(r => r.ResourceId == id);
        async Task ActAsync(ManagedDiskRecord record, ManagedDiskAction action, bool discard = false) =>
            _ = await service.ExecuteAsync(new(record.ResourceId, action, record.Runtime is null ? null : ManagedDiskExpected.From(record.Runtime),
                StopIntent: discard ? ManagedDiskStopIntent.DiscardThenStop : null, AcceptDiscard: discard), new ProgressLog());
    }
    private static ManagedLifecycleManifest Read(string path)
    {
        using var pins = ManagedDiskHostProtection.Pin(Path.GetDirectoryName(path)!, true);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Lifecycle manifest cannot be a reparse point.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 1 << 20) throw new InvalidDataException("Invalid lifecycle manifest length.");
        var manifest = JsonSerializer.Deserialize<ManagedLifecycleManifest>(file, ManagedDiskBrokerProtocol.Json) ?? throw new InvalidDataException("Missing lifecycle manifest.");
        ManagedLifecycleEvidence.Validate(manifest); return manifest;
    }
    private static void DurableWrite(string path, object value)
    {
        var stage = path + ".stage-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(file, value, ManagedDiskBrokerProtocol.Json); file.Flush(true); }
            if (File.Exists(path)) File.Replace(stage, path, path + ".previous", false); else File.Move(stage, path);
            using var committed = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
            committed.Flush(true);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
    private static string FilePath(ManagedDiskRecord record, string name)
    {
        if (record.Runtime?.State != ManagedDiskState.Ready || record.VolumePath is null || record.PhysicalDiskNumber is null ||
            WindowsDiskStorage.VolumeDisk(record.VolumePath) != record.PhysicalDiskNumber ||
            !WindowsDiskStorage.HasLetter(record.VolumePath, record.Definition.PreferredLetter))
            throw new IOException("The lifecycle byte oracle's owned volume binding changed.");
        return record.VolumePath + name;
    }
    private static string Write(string path)
    {
        var bytes = RandomNumberGenerator.GetBytes(16 << 10);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        file.Write(bytes); file.Flush(true); return Convert.ToHexString(SHA256.HashData(bytes));
    }
    private static void CheckBytes(string path, string expected)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != 16 << 10 || Convert.ToHexString(SHA256.HashData(file)) != expected) throw new IOException("Lifecycle file bytes differ from the independent manifest oracle.");
    }
    private static char FreeLetter(IEnumerable<char> remembered) => Enumerable.Range('D', 'Z' - 'D' + 1).Select(i => (char)i)
        .First(c => !remembered.Contains(c) && !DriveInfo.GetDrives().Any(d => d.Name.StartsWith(c + ":", StringComparison.OrdinalIgnoreCase)));
    private sealed class ProgressLog : IProgress<ManagedDiskProgress>
    { public void Report(ManagedDiskProgress value) => Console.WriteLine($"{value.Stage}: {value.Message} {value.CompletedBytes}/{value.TotalBytes}"); }
}
