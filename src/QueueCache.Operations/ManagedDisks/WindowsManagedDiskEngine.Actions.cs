using System.Runtime.Versioning;
using QueueCache.Management;

namespace QueueCache.Operations.ManagedDisks;

[SupportedOSPlatform("windows")]
public sealed partial class WindowsManagedDiskEngine
{
    public async Task<ManagedDiskOperationResult> ExecuteAsync(ManagedDiskRequest request, IProgress<ManagedDiskProgress>? progress = null, CancellationToken token = default)
    {
        if (request.ResourceId == Guid.Empty || !Enum.IsDefined(request.Action)) throw new ArgumentException("A valid managed resource/action is required.");
        await mutation.WaitAsync(token);
        try
        {
            if (!entries.TryGetValue(request.ResourceId, out var entry)) throw new IOException("The managed disk does not exist.");
            var record = Snapshot(entry);
            // A definition that never obtained a runtime has no identity to refresh; it can only be forgotten.
            var identityless = request.Action == ManagedDiskAction.RemoveDefinition && record.Runtime is null;
            if (request.Action != ManagedDiskAction.Recover && !identityless)
                (request.Expected ?? throw new IOException("A fresh expected managed disk identity is required.")).Validate(record,
                    request.Action == ManagedDiskAction.Format || (request.Action == ManagedDiskAction.Stop &&
                        (request.StopIntent == ManagedDiskStopIntent.DiscardThenStop || request.StopIntent is null &&
                            (record.Definition.Mode == ManagedDiskMode.EphemeralRam || record.Definition.Mode == ManagedDiskMode.ImageInRam && !record.Definition.SaveBeforeStopping))));
            string message; string? imagePath = null;
            switch (request.Action)
            {
                case ManagedDiskAction.Start:
                    await StartCoreAsync(entry, progress, token); message = "Managed disk started."; break;
                case ManagedDiskAction.Stop:
                    await StopCoreAsync(entry, request.StopIntent, request.AcceptDiscard, progress, token);
                    message = "Managed disk stopped. Its configuration and images are retained."; break;
                case ManagedDiskAction.Flush:
                    ValidateLive(entry);
                    if (record.Definition.Mode == ManagedDiskMode.CachedVhdx)
                    { using var cache = CacheDevice.OpenVolumeName(record.VolumePath!, true); cache.Control(WriteCacheAction.Flush); }
                    await entry.Disk!.FlushAsync(token);
                    message = record.Definition.Mode == ManagedDiskMode.CachedVhdx ? "Backing-image flush completed." : "RAM writes flushed. This does not save an image or make RAM persistent."; break;
                case ManagedDiskAction.Save:
                case ManagedDiskAction.Export:
                    if (record.Definition.Mode != ManagedDiskMode.ImageInRam) throw new IOException("Only whole-image RAM disks save/export checkpoints.");
                    if (request.Action == ManagedDiskAction.Export && request.Path is null) throw new ArgumentException("An export destination is required.");
                    var saved = await SaveCoreAsync(entry, request.Action == ManagedDiskAction.Export ? request.Path : null,
                        request.Action == ManagedDiskAction.Save || request.CommitExport, progress, token);
                    imagePath = saved.Image.Identity.Path;
                    message = saved.ChangedStartupSource ? "Verified checkpoint committed. Subsequent writes mark RAM changed again." : "Verified standalone export completed; startup source is unchanged."; break;
                case ManagedDiskAction.Format:
                    if (!request.AcceptErase) throw new IOException("Formatting requires explicit acknowledgement that existing volume contents will be erased.");
                    await FormatCoreAsync(entry, request.Label, progress, token);
                    message = record.Definition.Mode == ManagedDiskMode.ImageInRam ? "RAM copy formatted. Save a checkpoint to retain this change; the source image is preserved." : "Owned NTFS volume formatted."; break;
                case ManagedDiskAction.ChangeCache:
                    if (record.Definition.Mode != ManagedDiskMode.CachedVhdx || request.Cache is null) throw new IOException("Only a backed VHDX has an independent cache configuration.");
                    request.Cache.Validate(request.AcceptVolatileWrites);
                    var changed = record.Definition with { Cache = request.Cache, AcceptVolatileWrites = request.AcceptVolatileWrites };
                    changed.Validate();
                    if (record.Runtime!.State != ManagedDiskState.Stopped)
                    { ValidateLive(entry); await CacheTasks.SaveAsync(changed.PreferredLetter + ":", request.Cache, false, request.AcceptVolatileWrites, token: token, managedOwner: record.ResourceId); }
                    Update(entry, entry.Record with { Definition = changed }); message = "Backing-volume cache settings applied and remembered."; break;
                case ManagedDiskAction.SetStartup:
                    var startup = record.Definition with { StartAtBoot = request.StartAtBoot ?? record.Definition.StartAtBoot,
                        SaveBeforeStopping = request.SaveBeforeStopping ?? record.Definition.SaveBeforeStopping,
                        SaveDuringShutdown = request.SaveDuringShutdown ?? record.Definition.SaveDuringShutdown };
                    startup.Validate(); Update(entry, record with { Definition = startup });
                    message = startup.StartAtBoot ? startup.StartupDescription + "." : "Automatic startup disabled; configuration retained."; break;
                case ManagedDiskAction.ConfigureStopped:
                    if (entry.Provider is not null || entry.Image is not null || entry.Disk is not null)
                        throw new IOException("Surviving owned storage must be reconciled before editing its creation settings.");
                    Update(entry, entry.Record with { Definition = ManagedDiskConfiguration.EditStopped(record, request) });
                    message = "Stopped creation settings remembered. Start explicitly or wait for a new Windows startup."; break;
                case ManagedDiskAction.RemoveDefinition:
                    if (record.Runtime is { State: not ManagedDiskState.Stopped }) throw new IOException("Stop the managed disk explicitly before removing its definition.");
                    store.RemoveStopped(record.ResourceId); ManagedDiskHostProtection.Unregister(record.ResourceId);
                    entries.TryRemove(record.ResourceId, out _); entry.Dispose(); message = "Managed disk definition removed; no image files were deleted."; break;
                case ManagedDiskAction.DeleteImage:
                    if (!request.AcceptErase || request.Path is null) throw new IOException("Image deletion requires a path and explicit erase acknowledgement.");
                    DeleteImageCore(entry, request.Path); message = "Unreferenced owned image deleted."; break;
                case ManagedDiskAction.Recover:
                    await ReconcileAsync(entry, token); message = "Managed disk ownership and journal reconciled; prior committed source retained unless its catalog already committed a verified candidate."; break;
                default: throw new ArgumentOutOfRangeException(nameof(request));
            }
            return new(Snapshot(entry), message, imagePath);
        }
        finally { mutation.Release(); }
    }
    private async Task StopCoreAsync(Entry entry, ManagedDiskStopIntent? intent, bool acceptDiscard, IProgress<ManagedDiskProgress>? progress, CancellationToken token)
    {
        var record = Snapshot(entry);
        if (record.Runtime?.State == ManagedDiskState.Stopped) return;
        var mode = record.Definition.Mode;
        intent ??= mode switch
        {
            ManagedDiskMode.EphemeralRam => ManagedDiskStopIntent.DiscardThenStop,
            ManagedDiskMode.CachedVhdx => ManagedDiskStopIntent.DrainThenDetach,
            _ => record.Definition.SaveBeforeStopping ? ManagedDiskStopIntent.SaveThenStop : ManagedDiskStopIntent.DiscardThenStop
        };
        if ((mode == ManagedDiskMode.CachedVhdx && intent != ManagedDiskStopIntent.DrainThenDetach) ||
            (mode == ManagedDiskMode.EphemeralRam && intent != ManagedDiskStopIntent.DiscardThenStop) ||
            (mode == ManagedDiskMode.ImageInRam && intent == ManagedDiskStopIntent.DrainThenDetach))
            throw new IOException("The stop intent does not match this disk's storage contract.");
        if (entry.Disk is null && entry.Provider is not null && record.Native is not null &&
            (record.Native.Flags & RamDiskFlags.Published) == 0)
        {
            if (intent != ManagedDiskStopIntent.DiscardThenStop || !acceptDiscard)
                throw new IOException("The interrupted unpublished RAM creation can only be stopped with explicit discard intent.");
            var owned = entry.Provider.Query(record.Native); owned.RequireSameCreation(record.Native);
            if ((owned.Flags & (RamDiskFlags.Published | RamDiskFlags.Frozen)) != 0)
                throw new IOException("The RAM object's publication/freeze changed. Reconcile it before discarding.");
            entry.Provider.Remove(owned); entry.Provider.Dispose(); entry.Provider = null;
            Update(entry, record with { Native = null, PhysicalDiskNumber = null, VolumePath = null,
                Runtime = record.Runtime! with { State = ManagedDiskState.Stopped, Volume = null } });
            store.SaveJournal(new(Guid.NewGuid(), record.ResourceId, ManagedDiskJournalStage.Stopped, record.Runtime.BootEpoch, record.Runtime.CreationGeneration));
            return;
        }
        if (ManagedDiskStartup.IsIncompleteCreation(record))
        {
            if (mode != ManagedDiskMode.CachedVhdx && (intent != ManagedDiskStopIntent.DiscardThenStop || !acceptDiscard))
                throw new IOException("An incomplete RAM creation requires explicit discard; it cannot be saved without an owned filesystem binding.");
            ValidateOwnedAttachment(entry);
            progress?.Report(new(ManagedDiskState.Stopping, "Locking any enumerated volumes before stopping the exact incomplete creation."));
            await AbortCreationAsync(entry, token);
            store.SaveJournal(new(Guid.NewGuid(), record.ResourceId, ManagedDiskJournalStage.Stopped, record.Runtime!.BootEpoch, record.Runtime.CreationGeneration));
            return;
        }
        ValidateLive(entry);
        if (intent == ManagedDiskStopIntent.SaveThenStop)
        {
            await SaveCoreAsync(entry, null, true, progress, token, stopAfterSave: true);
        }
        else
        {
            if (intent == ManagedDiskStopIntent.DiscardThenStop && !acceptDiscard)
                throw new IOException("Stopping this RAM disk requires explicit discard acknowledgement. Cancellation is not discard permission.");
            progress?.Report(new(ManagedDiskState.Stopping, mode == ManagedDiskMode.CachedVhdx ? "Draining cache and detaching the owned image." : "Locking the owned RAM volume before discarding it."));
            var removed = false;
            try
            {
                // Only the locking handle can reach a locked volume: release through it, then dismount.
                using var locked = WindowsDiskStorage.LockVolume(record.VolumePath!, entry.Disk!.Number);
                if (mode == ManagedDiskMode.CachedVhdx)
                { using var cache = locked.Cache(); cache.Control(WriteCacheAction.Release); }
                locked.Dismount();
                WindowsDiskStorage.RemoveLetter(record.VolumePath!, record.Definition.PreferredLetter);
                entry.Disk.Dispose(); entry.Disk = null;
                if (entry.Provider is not null) entry.Provider.Remove(record.Native!);
                else entry.Image!.Detach();
                removed = true;
                entry.Dispose(); entry.Provider = null; entry.Image = null; entry.Paths = null; entry.Published = false;
                var stoppedRecord = record with { Native = null, PhysicalDiskNumber = null, VolumePath = null,
                    Runtime = record.Runtime! with { State = ManagedDiskState.Stopped, Volume = null } };
                entry.Record = stoppedRecord; Update(entry, stoppedRecord);
            }
            catch (Exception failure)
            {
                if (removed) throw new IOException("Storage stopped, but its catalog requires reconciliation. Images are retained.", failure);
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    entry.Disk ??= entry.Provider is not null
                        ? await WindowsDiskStorage.ResolveRamAsync(entry.Provider, record.Native!, cleanup.Token)
                        : WindowsDiskStorage.Open(entry.Image!.PhysicalPath(), true);
                    ValidateLive(entry);
                    WindowsDiskStorage.AssignLetter(record.VolumePath!, record.Definition.PreferredLetter);
                    if (mode == ManagedDiskMode.CachedVhdx)
                        await CacheTasks.SaveAsync(record.Definition.PreferredLetter + ":", record.Definition.Cache!, false, record.Definition.AcceptVolatileWrites, token: cleanup.Token, managedOwner: record.ResourceId);
                    // Only a refused exclusive lock is a Windows veto; anything later is a stop failure.
                    Update(entry, record with { LastError = (failure is WindowsVetoException ? "Windows vetoed stopping; the owned disk remains mounted: " : "Stopping failed; the owned disk remains mounted: ") + failure.Message });
                }
                catch (Exception recovery)
                {
                    Update(entry, record with { Runtime = record.Runtime! with { State = ManagedDiskState.RecoveryRequired },
                        LastError = "Stop failed; owned storage is retained where present. Restore failed: " + recovery.Message });
                    throw new AggregateException("Stopping and restoring the managed disk failed. Reconcile before another operation.", failure, recovery);
                }
                throw;
            }
        }
        var stopped = entry.Record.Runtime!;
        store.SaveJournal(new(Guid.NewGuid(), record.ResourceId, ManagedDiskJournalStage.Stopped, stopped.BootEpoch, stopped.CreationGeneration));
    }
    private async Task FormatCoreAsync(Entry entry, string? label, IProgress<ManagedDiskProgress>? progress, CancellationToken token)
    {
        ValidateLive(entry); var record = entry.Record;
        if (record.Definition.ReadOnly) throw new IOException("A read-only RAM device cannot be formatted.");
        var definition = record.Definition with { Label = label ?? record.Definition.Label }; definition.Validate();
        var started = false;
        try
        {
            // Prove exclusivity before recording an erase boundary or changing cache/mounts.
            using (var locked = WindowsDiskStorage.LockVolume(record.VolumePath!, entry.Disk!.Number))
            {
                token.ThrowIfCancellationRequested();
                store.SaveJournal(new(Guid.NewGuid(), record.ResourceId, ManagedDiskJournalStage.Formatting, record.Runtime!.BootEpoch, record.Runtime.CreationGeneration));
                started = true;
                Update(entry, record with { Runtime = record.Runtime with { State = ManagedDiskState.Formatting } });
                if (definition.Mode == ManagedDiskMode.CachedVhdx)
                { using var cache = locked.Cache(); cache.Control(WriteCacheAction.Release); }
                locked.Dismount();
                WindowsDiskStorage.RemoveLetter(record.VolumePath!, definition.PreferredLetter);
            }
            progress?.Report(new(ManagedDiskState.Formatting, "Formatting the exact owned NTFS volume."));
            ValidateLive(entry); await WindowsDiskStorage.FormatNtfsAsync(record.VolumePath!, entry.Disk!.Number, definition.Label, token);
            WindowsDiskStorage.AssignLetter(record.VolumePath!, definition.PreferredLetter);
            if (definition.Mode == ManagedDiskMode.CachedVhdx)
                await CacheTasks.SaveAsync(definition.PreferredLetter + ":", definition.Cache!, false, definition.AcceptVolatileWrites, token: token, managedOwner: record.ResourceId);
            var generation = entry.Provider?.Query(record.Native!).WriteGeneration ?? record.Runtime!.WriteGeneration;
            Update(entry, entry.Record with { Definition = definition, Runtime = entry.Record.Runtime! with { State = ManagedDiskState.Ready, WriteGeneration = generation }, LastError = null });
            store.SaveJournal(new(Guid.NewGuid(), record.ResourceId, ManagedDiskJournalStage.Ready, record.Runtime!.BootEpoch, record.Runtime.CreationGeneration));
        }
        catch (Exception ex)
        {
            if (!started) throw;
            entry.Record = entry.Record with { Runtime = entry.Record.Runtime! with { State = ManagedDiskState.RecoveryRequired }, LastError = "Format failed; the disk is retained and requires recovery: " + ex.Message };
            try { Update(entry, entry.Record); }
            catch (Exception catalog) { throw new AggregateException("Format and recording its recovery state failed.", ex, catalog); }
            throw;
        }
    }
    private void DeleteImageCore(Entry entry, string path)
    {
        ManagedDiskPaths.ValidateImagePath(path);
        var definition = entry.Record.Definition;
        var directory = Path.GetDirectoryName(path)!;
        var owned = definition.Source == ManagedDiskSource.CreateNew && path.Equals(definition.ImagePath, StringComparison.OrdinalIgnoreCase) ||
            definition.CheckpointDirectory is not null && directory.Equals(definition.CheckpointDirectory, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(path).StartsWith(definition.ResourceId.ToString("N") + "-", StringComparison.OrdinalIgnoreCase);
        if (!owned) throw new IOException("Only unreferenced images created for this managed resource can be deleted here; imported sources are preserved.");
        using var pins = ManagedDiskHostProtection.Pin(directory, true);
        var candidate = WindowsVirtualDisk.Inspect(path);
        foreach (var item in store.List())
        {
            if (new[] { item.CommittedImage?.Identity, item.PreviousImage?.Identity, item.OriginalSource }.Any(i => i is not null && i.SameImage(candidate)))
                throw new IOException("This image is referenced by a managed resource or retained checkpoint.");
            var journal = store.ReadJournal(item.ResourceId);
            if (journal is not null && journal.Stage is not (ManagedDiskJournalStage.Ready or ManagedDiskJournalStage.Stopped or ManagedDiskJournalStage.Committed) &&
                (journal.Candidate?.Identity.SameImage(candidate) == true || journal.CandidatePath?.Equals(path, StringComparison.OrdinalIgnoreCase) == true))
                throw new IOException("This image is part of an unreconciled operation.");
        }
        // Windows denies opening an attached or writable image for exclusive deletion.
        WindowsVirtualDisk.DeleteDetachedOwnedImage(candidate);
    }
}
