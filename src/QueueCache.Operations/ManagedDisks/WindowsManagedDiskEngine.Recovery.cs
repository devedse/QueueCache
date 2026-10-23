using System.ComponentModel;
using System.Runtime.Versioning;
using QueueCache.Management;

namespace QueueCache.Operations.ManagedDisks;

[SupportedOSPlatform("windows")]
public sealed partial class WindowsManagedDiskEngine
{
    /// <summary>How long startup keeps retrying what was refused only because RAM was short (#12), and how often.</summary>
    internal static readonly TimeSpan StartupMemoryRetry = TimeSpan.FromMinutes(10), StartupMemoryRetryInterval = TimeSpan.FromSeconds(15);
    private readonly HashSet<string> waitingProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> waitingDisks = [];
    private DateTime retryUntilUtc;

    public async Task InitializeAsync(IProgress<ManagedDiskProgress>? progress = null, CancellationToken token = default)
    {
        await mutation.WaitAsync(token);
        retryUntilUtc = DateTime.UtcNow + StartupMemoryRetry;
        try
        {
            foreach (var entry in entries.Values)
            {
                try
                {
                    var newStartup = entry.Record.StartupSession != startupSession;
                    await ReconcileAsync(entry, token);
                    if (ManagedDiskStartup.ShouldStartAfterReconcile(entry.Record.Definition, newStartup, entry.Record.Runtime))
                        await StartCoreAsync(entry, progress, token);
                }
                catch (InsufficientMemoryForCacheException ex) when (WaitsForMemory(entry))
                {
                    // Refused before anything was allocated: it stays Stopped and RetryStartupAsync tries again.
                    WindowsManagedBrokerService.Log($"Managed disk {entry.Record.ResourceId}: {ex.Message} {MemoryBudget.DescribeAvailable()}. Retrying until {retryUntilUtc:u}.");
                    Update(entry, entry.Record with { LastError = WaitingForMemoryText(ex) });
                    waitingDisks.Add(entry.Record.ResourceId);
                }
                catch (Exception ex)
                {
                    var record = entry.Record;
                    var runtime = record.Runtime ?? new(record.ResourceId, startupSession, 1, record.Definition.Mode, ManagedDiskState.Blocked, 0, null);
                    Update(entry, record with { Runtime = runtime with { State = entry.Disk is null ? ManagedDiskState.Blocked : ManagedDiskState.RecoveryRequired }, LastError = ex.Message });
                }
            }
            using var provider = WindowsRamDisk.Connect();
            var unknown = provider.Enumerate().Where(n => !entries.ContainsKey(n.ResourceId)).ToArray();
            // Retain them untouched, but keep serving cataloged disks and restoring ordinary
            // saved profiles: an unrelated native object must not disable the whole broker.
            if (unknown.Length != 0)
                WindowsManagedBrokerService.Log("Uncataloged owned RAM objects are retained for recovery: " + string.Join(", ", unknown.Select(n => n.ResourceId)));
            // Managed resources own their image-volume profiles. Regular disks retain strict saved identities.
            if (store.ReadStartupSession() != startupSession)
            {
                try { SavedConfigurations.ClearRestoreStatus(); }
                catch (Exception ex) { WindowsManagedBrokerService.Log("Could not clear the previous startup restore results: " + ex.Message); }
                var restored = await SavedConfigurations.RestoreAsync(token: token);
                foreach (var result in restored)
                {
                    if (result.MemoryShort)
                        WindowsManagedBrokerService.Log($"Saved profile {result.Volume}: {result.Detail} {MemoryBudget.DescribeAvailable()}. Retrying until {retryUntilUtc:u}.");
                    else if (!result.Applied)
                        WindowsManagedBrokerService.Log($"Saved profile {result.Volume}: {result.Detail}");
                    RecordRestore(result, result.MemoryShort ? retryUntilUtc : null);
                    if (result.MemoryShort && result.VolumeId is not null)
                        waitingProfiles.Add(result.VolumeId);
                }
                store.SaveStartupSession(startupSession);
            }
        }
        finally { mutation.Release(); }
    }
    private async Task ReconcileAsync(Entry entry, CancellationToken token)
    {
        using var measured = MeasureImages(entry);
        token.ThrowIfCancellationRequested(); var record = store.Read(entry.Record.ResourceId); var journal = store.ReadJournal(record.ResourceId);
        entry.Record = record;
        if (record.Definition.Mode != ManagedDiskMode.CachedVhdx)
        {
            entry.Provider ??= WindowsRamDisk.Connect();
            var native = entry.Provider.Enumerate().SingleOrDefault(n => n.ResourceId == record.ResourceId);
            if (native is null)
            {
                entry.Disk?.Dispose(); entry.Disk = null; entry.Provider.Dispose(); entry.Provider = null; entry.Published = false;
                var runtime = record.Runtime ?? new(record.ResourceId, startupSession, 1, record.Definition.Mode, ManagedDiskState.Stopped, 0, null);
                var precedingCreation = record.Native is not null || record.PhysicalDiskNumber is not null || runtime.State != ManagedDiskState.Stopped;
                Update(entry, record with { Native = null, PhysicalDiskNumber = null, VolumePath = null, StartupSession = startupSession,
                    Runtime = runtime with { State = ManagedDiskState.Stopped, Volume = null },
                    LastError = precedingCreation ? "The preceding RAM creation is no longer present. Volatile contents/changes since the last save may be unavailable; any committed image is retained. No save was inferred from its disappearance." : null });
                return;
            }
            if (record.Native is null || record.Runtime is null) throw new IOException("Live RAM has no durable creation identity. Restore its matching catalog before mutating it.");
            native.RequireSameCreation(record.Native);
            if ((native.Flags & RamDiskFlags.Frozen) != 0)
            {
                if (journal is null || native.FreezeOwner != journal.OperationId || native.BootEpoch != journal.BootEpoch || native.CreationGeneration != journal.CreationGeneration)
                    throw new IOException("A native stable-view owner does not match the durable journal. RAM is retained and mutation is blocked.");
                if (journal.Candidate?.Digest is not null)
                {
                    var candidate = await VerifyCandidateAsync(journal.Candidate.Identity.Path, journal.Candidate.Digest, token);
                    if (!candidate.SameImage(journal.Candidate.Identity)) throw new IOException("The journal candidate identity changed.");
                    // A verified candidate alone never changes the committed startup source.
                    if (record.CommittedImage?.Identity.SameImage(candidate) == true && record.CommittedImage.Digest != journal.Candidate.Digest)
                        throw new IOException("Catalog and candidate digests disagree; pointer recovery is blocked.");
                }
                native = entry.Provider.Thaw(native, journal.OperationId);
            }
            entry.Disk?.Dispose(); entry.Disk = null;
            entry.Published = (native.Flags & RamDiskFlags.Published) != 0;
            if (!entry.Published)
                throw new IOException("An interrupted unpublished creation is retained. Explicitly discard that owned creation before retrying; it cannot be adopted as Ready.");
            entry.Disk = await WindowsDiskStorage.ResolveRamAsync(entry.Provider, native, token);
            if (ManagedDiskStartup.IsIncompleteCreation(record))
            {
                Update(entry, record with { Native = native, PhysicalDiskNumber = entry.Disk.Number,
                    Runtime = record.Runtime with { State = ManagedDiskState.RecoveryRequired },
                    LastError = "The published RAM creation has no completed filesystem binding. Explicitly discard this owned creation; no format or load was performed." });
                return;
            }
            var volume = await entry.Disk.WaitVolumeAsync(token);
            if (record.GptDiskId is null || entry.Disk.Layout().DiskId != record.GptDiskId ||
                record.VolumePath is null || !volume.Equals(record.VolumePath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The live RAM layout/volume differs from its catalog. No format or reload was performed.");
            ManagedDiskHostProtection.RegisterVolume(record.ResourceId, volume);
            if (!ManagedDiskStartup.CanAdoptReady(record, journal))
            {
                Update(entry, record with { Native = native, PhysicalDiskNumber = entry.Disk.Number,
                    Runtime = record.Runtime with { State = ManagedDiskState.RecoveryRequired },
                    LastError = "An interrupted creation/format lacks a completed Ready boundary. The owned disk is retained; explicitly stop it or reformat with fresh erase consent." });
                return;
            }
            Update(entry, record with { Native = native, PhysicalDiskNumber = entry.Disk.Number,
                Runtime = record.Runtime with { State = ManagedDiskState.Ready, WriteGeneration = native.WriteGeneration, Volume = record.Definition.PreferredLetter + ":" }, LastError = null });
            if (record.StartupSession != startupSession)
            {
                // This is a proven new Windows startup, including Fast Startup. Same-session
                // sleep/hibernate and broker restarts retain the original startup classifier.
                await StopCoreAsync(entry, ManagedDiskStopIntent.DiscardThenStop, acceptDiscard: true, null, token);
                Update(entry, entry.Record with { StartupSession = startupSession });
            }
            else
            {
                WindowsDiskStorage.AssignLetter(volume, record.Definition.PreferredLetter);
                RamDirectBinding.Enable(record.Definition, volume);
            }
        }
        else
        {
            if (record.OriginalSource is null)
            {
                var runtime = record.Runtime ?? new(record.ResourceId, startupSession, 1, record.Definition.Mode, ManagedDiskState.Stopped, 0, null);
                Update(entry, record with { Runtime = runtime with { State = ManagedDiskState.Stopped, Volume = null }, StartupSession = startupSession }); return;
            }
            entry.Paths ??= ManagedDiskHostProtection.Pin(Path.GetDirectoryName(record.OriginalSource.Path)!, true);
            entry.Image ??= WindowsVirtualDisk.Open(record.OriginalSource.Path, false, false);
            string physicalPath;
            try { physicalPath = entry.Image.PhysicalPath(); }
            catch (Win32Exception ex) when (ex.NativeErrorCode is 55 or 1167 or 2)
            {
                entry.Dispose(); entry.Image = null; entry.Disk = null; entry.Paths = null;
                Update(entry, record with { PhysicalDiskNumber = null, VolumePath = null, StartupSession = startupSession,
                    Runtime = record.Runtime! with { State = ManagedDiskState.Stopped, Volume = null } }); return;
            }
            if (record.Runtime is null || record.PhysicalDiskNumber is null)
                throw new IOException("An attached image lacks durable ownership evidence. It was not detached or adopted.");
            if (record.StartupSession != startupSession && !ManagedDiskStartup.IsPreviousHybridStartup(record.StartupSession, startupSession, hybridTransitions))
                throw new IOException("The attached image is not from the immediately preceding native hybrid startup. It was not detached or adopted.");
            entry.Image.AdoptAttached(); entry.Disk?.Dispose(); entry.Disk = WindowsDiskStorage.Open(physicalPath, true);
            ManagedDiskStartup.RequireAttachedImageIdentity(record, WindowsVirtualDisk.Inspect(record.OriginalSource.Path, true),
                entry.Disk.Number, entry.Disk.CapacityBytes, entry.Disk.SectorBytes);
            if (ManagedDiskStartup.IsIncompleteCreation(record))
            {
                Update(entry, record with { Runtime = record.Runtime with { State = ManagedDiskState.RecoveryRequired },
                    LastError = "The owned image has an incomplete filesystem binding. Explicitly stop/detach it; image bytes are retained and no format was performed." });
                return;
            }
            if (entry.Disk.Layout().DiskId != record.GptDiskId) throw new IOException("The attached image layout changed.");
            var volume = await entry.Disk.WaitVolumeAsync(token);
            if (!volume.Equals(record.VolumePath, StringComparison.OrdinalIgnoreCase)) throw new IOException("The attached image volume changed.");
            ManagedDiskHostProtection.RegisterVolume(record.ResourceId, volume);
            if (!ManagedDiskStartup.CanAdoptReady(record, journal))
            {
                Update(entry, record with { Runtime = record.Runtime with { State = ManagedDiskState.RecoveryRequired },
                    LastError = "An interrupted creation/format is retained without being declared Ready. Stop the owned image or explicitly reformat it." });
                return;
            }
            if (record.StartupSession != startupSession)
            {
                // Fast Startup may preserve a permanent VHDX attachment. After exact
                // image/physical/GPT/volume/Ready-journal validation, stop/drain that
                // owned attachment before reopening the remembered startup recipe.
                Update(entry, record with { Runtime = record.Runtime with { State = ManagedDiskState.Ready }, LastError = null });
                await StopCoreAsync(entry, ManagedDiskStopIntent.DrainThenDetach, acceptDiscard: false, null, token);
                Update(entry, entry.Record with { StartupSession = startupSession });
            }
            else
            {
                WindowsDiskStorage.AssignLetter(volume, record.Definition.PreferredLetter);
                await CacheTasks.SaveAsync(record.Definition.PreferredLetter + ":", record.Definition.Cache!, false, record.Definition.AcceptVolatileWrites, token: token, managedOwner: record.ResourceId);
                Update(entry, record with { Runtime = record.Runtime with { State = ManagedDiskState.Ready }, LastError = null });
            }
        }
        var current = entry.Record.Runtime!;
        store.SaveJournal(new(Guid.NewGuid(), record.ResourceId, current.State == ManagedDiskState.Stopped ? ManagedDiskJournalStage.Stopped : ManagedDiskJournalStage.Ready,
            current.BootEpoch, current.CreationGeneration));
    }
    /// <summary>A RAM disk refused before allocating anything stays Stopped, so it can simply be started again.</summary>
    private static bool WaitsForMemory(Entry entry) =>
        entry.Provider is null && entry.Image is null && entry.Disk is null && entry.Record.Runtime?.State == ManagedDiskState.Stopped;
    private string WaitingForMemoryText(Exception refusal) =>
        $"Waiting for free RAM to start; QueueCache retries until {retryUntilUtc.ToLocalTime():HH:mm}. {refusal.Message}";
    private static void RecordRestore(RestoreResult result, DateTime? retryUntil)
    {
        if (result.VolumeId is null)
            return;
        var state = result.Applied ? ProfileRestoreState.Restored
            : retryUntil is null ? ProfileRestoreState.NotRestored : ProfileRestoreState.WaitingForMemory;
        try { SavedConfigurations.RecordRestore(new(result.Volume, result.VolumeId, state, result.Detail, DateTime.UtcNow, retryUntil)); }
        catch (Exception ex) { WindowsManagedBrokerService.Log($"Could not record the startup restore result of {result.Volume}: {ex.Message}"); }
    }

    /// <summary>Retries the saved profiles and RAM disks that startup refused only because RAM was short at
    /// that moment (#12): every 15 s for 10 minutes. Anything else that changes them meanwhile (a new
    /// setting, a manual start or stop) ends the retry for that one. Stops with <paramref name="token"/>.</summary>
    public async Task RetryStartupAsync(CancellationToken token)
    {
        var lastDetail = "";
        try
        {
            while (waitingProfiles.Count + waitingDisks.Count != 0 && DateTime.UtcNow < retryUntilUtc)
            {
                await Task.Delay(StartupMemoryRetryInterval, token);
                await mutation.WaitAsync(token);
                try { lastDetail = await RetryStartupOnceAsync(token) ?? lastDetail; }
                finally { mutation.Release(); }
            }
            if (waitingProfiles.Count + waitingDisks.Count != 0)
                await GiveUpAsync($"RAM stayed short for {StartupMemoryRetry.TotalMinutes:0} minutes after startup. {lastDetail}".TrimEnd());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (waitingProfiles.Count + waitingDisks.Count != 0)
                await GiveUpAsync("The QueueCache service stopped while it waited for free RAM.");
        }
        catch (Exception ex)
        {
            WindowsManagedBrokerService.Log("Retrying the startup restore failed: " + ex);
            await GiveUpAsync("Retrying failed: " + ex.Message);
        }
    }
    /// <summary>One retry pass under the mutation lock. Returns the latest RAM refusal, if any.</summary>
    private async Task<string?> RetryStartupOnceAsync(CancellationToken token)
    {
        string? refusal = null;
        if (waitingProfiles.Count != 0)
        {
            waitingProfiles.IntersectWith(SavedConfigurations.List().Select(p => p.VolumeId)); // Removed meanwhile.
            foreach (var result in await SavedConfigurations.RestoreAsync(token: token, include: p => waitingProfiles.Contains(p.VolumeId), onlyIfNoCache: true))
            {
                if (result.MemoryShort) { refusal = $"{result.Detail} {MemoryBudget.DescribeAvailable()}."; continue; }
                waitingProfiles.Remove(result.VolumeId!);
                RecordRestore(result, null);
                WindowsManagedBrokerService.Log($"Saved profile {result.Volume}: " + (result.Applied ? "restored after waiting for free RAM." : result.Detail));
            }
        }
        foreach (var id in waitingDisks.ToArray())
        {
            if (!entries.TryGetValue(id, out var entry) || !WaitsForMemory(entry) || entry.Record.LastError?.StartsWith("Waiting for free RAM", StringComparison.Ordinal) != true)
            {
                waitingDisks.Remove(id); // Started, stopped or removed by its owner meanwhile.
                continue;
            }
            try
            {
                await StartCoreAsync(entry, null, token);
                waitingDisks.Remove(id);
                WindowsManagedBrokerService.Log($"Managed disk {id}: started after waiting for free RAM.");
            }
            catch (InsufficientMemoryForCacheException ex) when (WaitsForMemory(entry))
            {
                refusal = $"{ex.Message} {MemoryBudget.DescribeAvailable()}.";
                Update(entry, entry.Record with { LastError = WaitingForMemoryText(ex) });
            }
            catch (Exception ex)
            {
                // StartCoreAsync recorded the outcome (Stopped or RecoveryRequired) and its error.
                waitingDisks.Remove(id);
                WindowsManagedBrokerService.Log($"Managed disk {id}: starting after waiting for RAM failed: {ex.Message}");
            }
        }
        return refusal;
    }
    private async Task GiveUpAsync(string detail)
    {
        foreach (var volumeId in waitingProfiles)
        {
            var profile = SavedConfigurations.List().FirstOrDefault(p => p.Matches(volumeId));
            if (profile is not null)
                RecordRestore(new(profile.Volume, false, detail, volumeId), null);
            WindowsManagedBrokerService.Log($"Saved profile {profile?.Volume ?? volumeId}: not restored. {detail}");
        }
        waitingProfiles.Clear();
        if (waitingDisks.Count == 0)
            return;
        // Shutdown may hold the lock for its saves; the record then keeps its waiting text until the next start.
        if (!await mutation.WaitAsync(TimeSpan.FromSeconds(10)))
            return;
        try
        {
            foreach (var id in waitingDisks)
                if (entries.TryGetValue(id, out var entry) && WaitsForMemory(entry))
                {
                    Update(entry, entry.Record with { LastError = "Not started: " + detail + " Start it once RAM is free." });
                    WindowsManagedBrokerService.Log($"Managed disk {id}: not started. {detail}");
                }
            waitingDisks.Clear();
        }
        finally { mutation.Release(); }
    }

    public async Task AttemptShutdownSavesAsync(CancellationToken token)
    {
        await mutation.WaitAsync(token);
        try
        {
            foreach (var entry in entries.Values.Where(e => e.Record.Definition.Mode == ManagedDiskMode.ImageInRam && e.Record.Definition.SaveDuringShutdown && e.Record.Runtime?.State == ManagedDiskState.Ready))
            {
                try { await SaveCoreAsync(entry, null, true, null, token); }
                catch (Exception ex) { Update(entry, entry.Record with { LastError = "Shutdown save attempt failed; last committed image retained: " + ex.Message }); }
                if (token.IsCancellationRequested) break;
            }
        }
        finally { mutation.Release(); }
    }
}
