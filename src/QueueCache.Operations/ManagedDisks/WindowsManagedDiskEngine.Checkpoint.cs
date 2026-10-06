using System.Runtime.Versioning;
using QueueCache.Management;

namespace QueueCache.Operations.ManagedDisks;

[SupportedOSPlatform("windows")]
public sealed partial class WindowsManagedDiskEngine
{
    private void ValidateOwnedAttachment(Entry entry)
    {
        var record = Snapshot(entry);
        if (entry.Disk is null || record.Runtime is null || record.PhysicalDiskNumber != entry.Disk.Number ||
            entry.Disk.CapacityBytes != record.Definition.CapacityBytes || entry.Disk.SectorBytes != record.Definition.SectorBytes)
            throw new IOException("The managed physical attachment/geometry changed; mutation is blocked.");
        if (entry.Provider is not null)
        {
            entry.Provider.Query(record.Native!).RequireSameCreation(record.Native!);
            if (entry.Disk.Serial() != record.Native!.DeviceSerial) throw new IOException("The resolved RAM disk serial changed.");
        }
        else if (entry.Image is null || entry.Image.PhysicalPath() != entry.Disk.Path || record.OriginalSource is null ||
            !WindowsVirtualDisk.Inspect(record.OriginalSource.Path, true).SameImage(record.OriginalSource))
            throw new IOException("The owned VHDX attachment identity changed.");
    }
    private void ValidateLive(Entry entry)
    {
        ValidateOwnedAttachment(entry); var record = Snapshot(entry);
        if (record.VolumePath is null || WindowsDiskStorage.VolumeDisk(record.VolumePath) != entry.Disk!.Number ||
            entry.Disk.Layout().DiskId != record.GptDiskId)
            throw new IOException("The managed disk/volume binding changed; mutation is blocked.");
    }
    public Task<IStableManagedImage> AcquireAsync(ManagedDiskRecord record, Guid operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var entry = entries[record.ResourceId]; ValidateLive(entry);
        var restoreLetter = WindowsDiskStorage.HasLetter(record.VolumePath!, record.Definition.PreferredLetter);
        var volumeLock = WindowsDiskStorage.LockVolume(record.VolumePath!, entry.Disk!.Number, dismount: true);
        try
        {
            var native = entry.Provider!.Freeze(entry.Record.Native!, operation);
            entry.Record = entry.Record with { Native = native, Runtime = entry.Record.Runtime! with { WriteGeneration = native.WriteGeneration } };
            return Task.FromResult<IStableManagedImage>(new StableView(entry, volumeLock, operation, native, restoreLetter));
        }
        catch { volumeLock.Dispose(); throw; }
    }
    private sealed class StableView(Entry entry, IDisposable volumeLock, Guid operation, RamDiskSnapshot native, bool restoreLetter) : IStableManagedImage
    {
        public bool Removed;
        public RamDiskSnapshot Native => native;
        public ulong Generation => native.WriteGeneration;
        public ulong? WrittenBytes { get; } = native.WriteBytes +
            (RamDirectBinding.Query(entry.Record.Definition, entry.Record.VolumePath)?.WriteBytes ?? 0);
        public ILogicalDisk Storage => entry.Provider!.Storage(native, operation);
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!Removed)
                {
                    var thawed = entry.Provider!.Thaw(native, operation);
                    entry.Record = entry.Record with { Native = thawed };
                    if (entry.Disk is null)
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        entry.Disk = await WindowsDiskStorage.ResolveRamAsync(entry.Provider, thawed, cleanup.Token);
                    }
                    if (restoreLetter && entry.Record.VolumePath is not null) WindowsDiskStorage.AssignLetter(entry.Record.VolumePath, entry.Record.Definition.PreferredLetter);
                }
            }
            finally { volumeLock.Dispose(); }
            // Lock and dismount may have ended Direct access; resume it (a no-op while still bound).
            if (!Removed && entry.Record.VolumePath is not null) RamDirectBinding.Enable(entry.Record.Definition, entry.Record.VolumePath);
        }
    }
    public Task StopFrozenAsync(ManagedDiskRecord record, IStableManagedImage stable, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (stable is not StableView view || view.Generation != record.Runtime!.SavedGeneration)
            throw new IOException("The committed image is not the current frozen generation.");
        var entry = entries[record.ResourceId];
        WindowsDiskStorage.RemoveLetter(record.VolumePath!, record.Definition.PreferredLetter);
        entry.Disk!.Dispose(); entry.Disk = null;
        entry.Provider!.Remove(view.Native); view.Removed = true;
        entry.Provider.Dispose(); entry.Provider = null; entry.Published = false;
        entry.Record = record with { Native = null, PhysicalDiskNumber = null, VolumePath = null,
            Runtime = record.Runtime with { State = ManagedDiskState.Stopped, Volume = null } };
        Update(entry, entry.Record);
        return Task.CompletedTask;
    }
    public Task<ICheckpointImage> CreateCandidateAsync(ManagedDiskDefinition definition, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var parent = Path.GetDirectoryName(path)!;
        ManagedDiskHostProtection.CreateProtectedDirectory(parent);
        var pins = ManagedDiskHostProtection.Pin(parent, true);
        WindowsVirtualDisk? image = null; WindowsDiskStorage? disk = null;
        try
        {
            ManagedDiskHostProtection.RegisterAdditionalHost(definition.ResourceId, parent);
            var drive = new DriveInfo(Path.GetPathRoot(parent)!);
            var required = checked(definition.CapacityBytes + definition.CapacityBytes / 64 + (64UL << 20));
            if ((ulong)drive.AvailableFreeSpace < required) throw new IOException("The destination lacks worst-case space for a complete VHDX candidate while retaining existing checkpoints.");
            image = WindowsVirtualDisk.CreateNew(path, definition.CapacityBytes, definition.SectorBytes, definition.Allocation);
            disk = WindowsDiskStorage.Open(image.Attach(false), true); disk.SetOffline(true);
            var counters = entries[definition.ResourceId].ImageIo;
            return Task.FromResult<ICheckpointImage>(new CandidateImage(pins, image, disk, counters.Measure(disk), counters, path));
        }
        catch { disk?.Dispose(); image?.Dispose(); pins.Dispose(); throw; }
    }
    private sealed class CandidateImage(IDisposable pins, WindowsVirtualDisk image, WindowsDiskStorage disk, ILogicalDisk measured, ManagedImageIo counters, string path) : ICheckpointImage
    {
        private bool detached;
        public ILogicalDisk Storage => measured;
        public async Task CompleteAndDetachAsync(CancellationToken token)
        {
            await measured.FlushAsync(token); disk.Dispose(); image.Detach(); detached = true;
            // The virtual-disk handle keeps write access to the container until it is closed.
            image.Dispose();
            using var host = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
            counters.FlushAttempt();
            host.Flush(flushToDisk: true);
        }
        public ValueTask DisposeAsync()
        { disk.Dispose(); try { if (!detached) image.Detach(); } finally { image.Dispose(); pins.Dispose(); } return ValueTask.CompletedTask; }
    }
    public async Task<ImageInspection> VerifyCandidateAsync(string path, LogicalImageDigest digest, CancellationToken token)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew(); var phases = new List<string>();
        void Mark(string phase) { phases.Add($"{phase}={timer.ElapsedMilliseconds}ms"); timer.Restart(); }
        try
        {
            var expected = await InspectAsync(path, token); Mark("inspect");
            var view = await OpenImportAsync(expected, token); Mark("open");
            try { await LogicalImageTransfer.VerifyAsync(view.LogicalStorage, digest, token); Mark("read"); }
            finally { await view.DisposeAsync(); Mark("close"); }
            return expected;
        }
        finally { WindowsManagedBrokerService.Log("Checkpoint verification " + string.Join(' ', phases)); }
    }
    private async Task<ManagedCheckpointResult> SaveCoreAsync(Entry entry, string? export, bool commit, IProgress<ManagedDiskProgress>? progress, CancellationToken token, bool stopAfterSave = false)
    {
        using var measured = MeasureImages(entry);
        ValidateLive(entry);
        var path = export ?? Path.Combine(entry.Record.Definition.CheckpointDirectory!,
            $"{entry.Record.ResourceId:N}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.vhdx");
        try
        {
            var result = await new ManagedImageCheckpoint(store, this).SaveAsync(Snapshot(entry), path, commit, progress, token, stopAfterSave);
            entry.Record = result.Record with { Native = entry.Provider?.Query(entry.Record.Native!),
                ImageTransferAttempts = checked(entry.Record.ImageTransferAttempts + 1),
                ImageTransferredBytes = checked(entry.Record.ImageTransferredBytes + result.Image.Digest!.Bytes) };
            Update(entry, entry.Record); return result with { Record = entry.Record };
        }
        catch (Exception failure)
        {
            ManagedDiskRecord actual;
            try { actual = store.Read(entry.Record.ResourceId); }
            catch (Exception catalog)
            {
                entry.Record = entry.Record with { Runtime = entry.Record.Runtime! with { State = ManagedDiskState.RecoveryRequired },
                    LastError = "Save outcome requires catalog reconciliation. " + failure.Message + " Catalog: " + catalog.Message };
                throw new AggregateException("Save and catalog recovery failed; owned storage is retained where present.", failure, catalog);
            }
            if (entry.Provider is null && !entry.Published)
                actual = actual with { Native = null, PhysicalDiskNumber = null, VolumePath = null,
                    Runtime = actual.Runtime! with { State = ManagedDiskState.Stopped, Volume = null } };
            else if (entry.Disk is null)
                actual = actual with { Runtime = actual.Runtime! with { State = ManagedDiskState.RecoveryRequired },
                    LastError = "Saved-image outcome is recorded; the live RAM binding needs reconciliation before another operation." };
            entry.Record = actual;
            try { Update(entry, actual); }
            catch (Exception catalog) { throw new AggregateException("Save failed and its reconciled state could not be recorded.", failure, catalog); }
            throw;
        }
    }
}
