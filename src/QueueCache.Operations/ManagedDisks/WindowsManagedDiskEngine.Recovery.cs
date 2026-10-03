using System.ComponentModel;
using System.Runtime.Versioning;
using QueueCache.Management;

namespace QueueCache.Operations.ManagedDisks;

[SupportedOSPlatform("windows")]
public sealed partial class WindowsManagedDiskEngine
{
    public async Task InitializeAsync(IProgress<ManagedDiskProgress>? progress = null, CancellationToken token = default)
    {
        await mutation.WaitAsync(token);
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
                catch (Exception ex)
                {
                    var record = entry.Record;
                    var runtime = record.Runtime ?? new(record.ResourceId, startupSession, 1, record.Definition.Mode, ManagedDiskState.Blocked, 0, null);
                    Update(entry, record with { Runtime = runtime with { State = entry.Disk is null ? ManagedDiskState.Blocked : ManagedDiskState.RecoveryRequired }, LastError = ex.Message });
                }
            }
            using var provider = WindowsRamDisk.Connect();
            var unknown = provider.Enumerate().Where(n => !entries.ContainsKey(n.ResourceId)).ToArray();
            if (unknown.Length != 0)
                throw new IOException("Uncataloged owned RAM objects are retained for recovery: " + string.Join(", ", unknown.Select(n => n.ResourceId)));
            // Managed resources own their image-volume profiles. Regular disks retain strict saved identities.
            if (store.ReadStartupSession() != startupSession)
            {
                var restored = await SavedConfigurations.RestoreAsync(token: token);
                foreach (var failure in restored.Where(r => !r.Applied)) WindowsManagedBrokerService.Log($"Saved profile {failure.Volume}: {failure.Detail}");
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
            else WindowsDiskStorage.AssignLetter(volume, record.Definition.PreferredLetter);
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
            if (record.StartupSession != startupSession || record.Runtime is null || record.GptDiskId is null || record.PhysicalDiskNumber is null)
                throw new IOException("An attached image lacks same-startup ownership evidence. It was not detached or adopted.");
            entry.Image.AdoptAttached(); entry.Disk?.Dispose(); entry.Disk = WindowsDiskStorage.Open(physicalPath, true);
            if (!WindowsVirtualDisk.Inspect(record.OriginalSource.Path, true).SameImage(record.OriginalSource) || entry.Disk.Layout().DiskId != record.GptDiskId ||
                entry.Disk.Number != record.PhysicalDiskNumber)
                throw new IOException("The attached VHDX does not match its remembered image and physical binding.");
            var volume = await entry.Disk.WaitVolumeAsync(token);
            if (!volume.Equals(record.VolumePath, StringComparison.OrdinalIgnoreCase)) throw new IOException("The attached image volume changed.");
            ManagedDiskHostProtection.RegisterVolume(record.ResourceId, volume);
            if (!ManagedDiskStartup.CanAdoptReady(record, journal))
            {
                Update(entry, record with { Runtime = record.Runtime with { State = ManagedDiskState.RecoveryRequired },
                    LastError = "An interrupted creation/format is retained without being declared Ready. Stop the owned image or explicitly reformat it." });
                return;
            }
            WindowsDiskStorage.AssignLetter(volume, record.Definition.PreferredLetter);
            await CacheTasks.SaveAsync(record.Definition.PreferredLetter + ":", record.Definition.Cache!, false, record.Definition.AcceptVolatileWrites, token: token, managedOwner: record.ResourceId);
            Update(entry, record with { Runtime = record.Runtime with { State = ManagedDiskState.Ready }, LastError = null });
        }
        var current = entry.Record.Runtime!;
        store.SaveJournal(new(Guid.NewGuid(), record.ResourceId, ManagedDiskJournalStage.Ready, current.BootEpoch, current.CreationGeneration));
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
