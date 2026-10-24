using System.ComponentModel;
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
    public Task<IReusedCheckpoint?> OpenReusedAsync(ManagedDiskRecord record, CancellationToken token) => Task.Run<IReusedCheckpoint?>(() =>
    {
        token.ThrowIfCancellationRequested();
        if (ManagedCheckpointReuse.Candidate(record) is not { } path || record.PreviousImage is not { Digest: { } digest } previous)
            return null;
        IDisposable? pins = null;
        VhdxPayloadStorage? storage = null;
        try
        {
            pins = ManagedDiskHostProtection.Pin(Path.GetDirectoryName(path)!, true);
            var mapPath = ImageChunkMap.PathFor(path);
            var mapFile = new FileInfo(mapPath);
            if (!mapFile.Exists || mapFile.Length is <= 0 or > ImageChunkMap.MaximumFileBytes)
            { return null; }
            var map = ImageChunkMap.Parse(File.ReadAllBytes(mapPath));
            if (map is null || map.CapacityBytes != record.Definition.CapacityBytes ||
                !map.Sha256.Equals(digest.Sha256, StringComparison.OrdinalIgnoreCase) ||
                map.FileKeySha256 != ImageChunkMap.FileKeyHash(previous.Identity.FileIdentity))
            { return null; }
            // Keep a conservative reserve even for sparse/COW host files. Reuse never grows
            // the VHDX or allocates BAT entries, but host allocation can still grow on overwrite.
            var reserve = checked(record.Definition.CapacityBytes + record.Definition.CapacityBytes / 64 + (64UL << 20));
            if ((ulong)new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace < reserve)
            { return null; }
            var counters = entries[record.ResourceId].ImageIo;
            storage = VhdxPayloadStorage.Open(path, record.Definition.CapacityBytes, record.Definition.SectorBytes, counters.FlushAttempt);
            WindowsVirtualDisk.RequireFileIdentity(storage.Handle, previous.Identity.FileIdentity);
            var file = new FileInfo(path);
            if (storage.DiskId != previous.Identity.DiskId || storage.FileBytes != map.FileBytes || file.LastWriteTimeUtc.Ticks != map.FileWriteTicks)
            { return null; }
            // A candidate with a consumed map is not reused again after interruption. The
            // coordinator journals this path and removes its PreviousImage pointer before writing.
            File.Delete(mapPath);
            ManagedDiskHostProtection.RegisterAdditionalHost(record.ResourceId, Path.GetDirectoryName(path)!);
            var result = new ReusedImage(pins, storage, counters.Measure(storage), path, map.Hashes);
            storage = null; pins = null;
            return result;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or Win32Exception or UnauthorizedAccessException or NotSupportedException)
        {
            WindowsManagedBrokerService.Log($"Managed disk {record.ResourceId}: checkpoint reuse unavailable ({ex.Message}); saving a complete new image.");
            return null;
        }
        finally
        {
            if (storage is not null) storage.DisposeAsync().AsTask().GetAwaiter().GetResult();
            pins?.Dispose();
        }
    }, token);

    private sealed class ReusedImage(IDisposable pins, VhdxPayloadStorage storage, ILogicalDisk measured,
        string path, byte[] hashes) : IReusedCheckpoint
    {
        public string Path => path;
        public ReadOnlyMemory<byte> Hashes => hashes;
        public ILogicalDisk Storage => measured;
        public ulong HeaderBytesWritten => storage.HeaderBytesWritten;
        public async Task CompleteAndDetachAsync(CancellationToken token)
        { await measured.FlushAsync(token); await storage.DisposeAsync(); }
        public async ValueTask DisposeAsync()
        { try { await storage.DisposeAsync(); } finally { pins.Dispose(); } }
    }

    public void SaveChunkMap(ManagedDiskRecord record, ImageInspection identity, ChunkedImageCopy copy)
    {
        if (!ManagedCheckpointReuse.OwnedCheckpoint(record.Definition, identity.Path)) return;
        try
        {
            using var pins = ManagedDiskHostProtection.Pin(Path.GetDirectoryName(identity.Path)!, true);
            var file = new FileInfo(identity.Path);
            var bytes = new ImageChunkMap(copy.Digest.Bytes, copy.Digest.Sha256, copy.Hashes,
                file.Length, file.LastWriteTimeUtc.Ticks, ImageChunkMap.FileKeyHash(identity.FileIdentity)).Serialize();
            var mapPath = ImageChunkMap.PathFor(identity.Path);
            var staged = mapPath + ".stage-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes); entries[record.ResourceId].ImageIo.FlushAttempt(); stream.Flush(flushToDisk: true);
                }
                File.Move(staged, mapPath, overwrite: true);
            }
            finally { File.Delete(staged); }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or NotSupportedException)
        { WindowsManagedBrokerService.Log($"Managed disk {record.ResourceId}: checkpoint fingerprints not retained ({ex.Message}); future saves can copy the complete image."); }
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
    public async Task<ImageInspection> VerifyCandidateAsync(string path, LogicalImageDigest digest, CancellationToken token, IProgress<ImageTransferProgress>? progress = null)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew(); var phases = new List<string>();
        void Mark(string phase) { phases.Add($"{phase}={timer.ElapsedMilliseconds}ms"); timer.Restart(); }
        try
        {
            var expected = await InspectAsync(path, token); Mark("inspect");
            var view = await OpenImportAsync(expected, token); Mark("open");
            try { await LogicalImageTransfer.VerifyAsync(view.LogicalStorage, digest, token, progress); Mark("read"); }
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
            var result = await new ManagedImageCheckpoint(store, this).SaveAsync(Snapshot(entry), path, commit, progress, token, stopAfterSave, allowReuse: export is null);
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
