using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using QueueCache.Management;

namespace QueueCache.Operations.ManagedDisks;

/// <summary>Privileged broker operations. Storage lifetime belongs to Windows/provider, not callers or UI windows.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsManagedDiskEngine : IManagedDiskService, IManagedDiskBackend, IManagedCheckpointBackend, IDisposable
{
    private readonly ManagedDiskStore store;
    private readonly ConcurrentDictionary<Guid, Entry> entries = new();
    private readonly SemaphoreSlim mutation = new(1, 1);
    private readonly Guid startupSession;
    private readonly Guid mountedEpoch;
    private ulong mountedGeneration;
    private readonly AsyncLocal<Entry?> imageOperation = new();
    private sealed class Entry(ManagedDiskRecord record) : IDisposable
    {
        public volatile ManagedDiskRecord Record = record;
        public WindowsRamDisk? Provider;
        public WindowsVirtualDisk? Image;
        public WindowsDiskStorage? Disk;
        public IDisposable? Paths;
        public bool Published;
        public readonly ManagedImageIo ImageIo = new();
        public void Dispose() { Disk?.Dispose(); Image?.Dispose(); Provider?.Dispose(); Paths?.Dispose(); }
    }
    public WindowsManagedDiskEngine(Guid startupSession)
    {
        if (startupSession == Guid.Empty) throw new ArgumentException("An authoritative Windows startup session is required.");
        this.startupSession = startupSession; mountedEpoch = startupSession;
        ManagedDiskHostProtection.CreateProtectedDirectory(ManagedDiskHostProtection.CatalogDirectory);
        store = new(ManagedDiskHostProtection.CatalogDirectory);
        foreach (var record in store.List())
        {
            entries.TryAdd(record.ResourceId, new(record));
            if (record.Definition.Mode == ManagedDiskMode.CachedVhdx && record.Runtime?.BootEpoch == mountedEpoch)
                mountedGeneration = Math.Max(mountedGeneration, record.Runtime.CreationGeneration);
        }
    }
    public Task<IReadOnlyList<ManagedDiskCapability>> CapabilitiesAsync(CancellationToken token = default) => Task.Run<IReadOnlyList<ManagedDiskCapability>>(() =>
    {
        token.ThrowIfCancellationRequested();
        string? problem = null;
        try { using var provider = WindowsRamDisk.Connect(); _ = provider.Capabilities(); }
        catch (Exception ex) when (ex is IOException or Win32Exception) { problem = ex.Message; }
        return Enum.GetValues<ManagedDiskMode>().Select(m => new ManagedDiskCapability(m, problem is null, problem)).ToArray();
    }, token);
    public async Task<ManagedDiskCapability> CapabilityAsync(ManagedDiskMode mode, CancellationToken token) => (await CapabilitiesAsync(token)).Single(c => c.Mode == mode);
    public Task<ImageInspection> InspectAsync(string path, CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested(); ValidateSourceFile(path); return WindowsVirtualDisk.Inspect(path);
    }, token);
    private static void ValidateSourceFile(string path)
    {
        ManagedDiskPaths.ValidateImagePath(path);
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Compressed | FileAttributes.Encrypted)) != 0)
            throw new NotSupportedException("Reparse, compressed or encrypted image files are unsupported.");
    }
    public Task<IReadOnlyList<ManagedDiskRecord>> ListAsync(CancellationToken token = default) => Task.Run<IReadOnlyList<ManagedDiskRecord>>(() =>
    {
        token.ThrowIfCancellationRequested();
        return entries.Values.Select(entry => Snapshot(entry)).OrderBy(r => r.Definition.PreferredLetter).ToArray();
    }, token);
    private ManagedDiskRecord Snapshot(Entry entry)
    {
        var record = entry.Record;
        if (entry.Provider is not null && record.Native is not null && record.Runtime?.State is ManagedDiskState.Ready or ManagedDiskState.Saving or ManagedDiskState.RecoveryRequired)
        {
            try
            {
                var native = entry.Provider.Query(record.Native); native.RequireSameCreation(record.Native);
                record = record with { Native = native, Runtime = record.Runtime with { WriteGeneration = native.WriteGeneration } };
            }
            catch (Exception ex) when (ex is IOException or Win32Exception)
            { record = record with { Runtime = record.Runtime! with { State = ManagedDiskState.RecoveryRequired, LastError = ex.Message }, LastError = ex.Message }; }
        }
        return record with { ImageIo = entry.ImageIo.Snapshot() };
    }
    public async Task<ManagedDiskRuntime> CreateAsync(ManagedDiskDefinition definition, IProgress<ManagedDiskProgress>? progress = null, CancellationToken token = default)
    {
        definition.Validate(); await mutation.WaitAsync(token);
        try
        {
            if (entries.ContainsKey(definition.ResourceId) || store.ContainsResource(definition.ResourceId)) throw new IOException("This managed resource identity already exists or was retired. Use Start/recover, or create a new resource identity.");
            var record = new ManagedDiskRecord(definition, StartupSession: startupSession);
            store.Save(record); entries.TryAdd(definition.ResourceId, new(record));
            return await StartCoreAsync(entries[definition.ResourceId], progress, token, creating: true);
        }
        finally { mutation.Release(); }
    }
    private async Task<ManagedDiskRuntime> StartCoreAsync(Entry entry, IProgress<ManagedDiskProgress>? progress, CancellationToken token, bool creating = false)
    {
        using var measured = MeasureImages(entry);
        var definition = entry.Record.Definition;
        if (!creating && entry.Record.Runtime?.State != ManagedDiskState.Stopped) throw new IOException("The managed disk is live or requires recovery; it cannot be recreated.");
        if (!creating && definition.Mode != ManagedDiskMode.EphemeralRam)
        {
            var reference = definition.Mode == ManagedDiskMode.ImageInRam ? entry.Record.CommittedImage?.Identity : entry.Record.OriginalSource;
            if (reference is null) throw new IOException("No recorded image identity is available; recover the failed creation before starting.");
            var actual = await InspectAsync(reference.Path, token);
            if (!actual.SameImage(reference)) throw new IOException("The remembered image was replaced or changed; startup is blocked.");
            definition = definition with { Source = ManagedDiskSource.OpenExisting, ImagePath = reference.Path,
                InitializeBlankImage = false, ExpectedBlankImage = null };
        }
        var hostPaths = new List<string>();
        if (definition.Mode != ManagedDiskMode.EphemeralRam)
        {
            var imageParent = Path.GetDirectoryName(definition.ImagePath!)!;
            if (definition.Mode == ManagedDiskMode.CachedVhdx || definition.Source == ManagedDiskSource.CreateNew)
                ManagedDiskHostProtection.CreateProtectedDirectory(imageParent);
            hostPaths.Add(imageParent);
            if (definition.CheckpointDirectory is not null)
            { ManagedDiskHostProtection.CreateProtectedDirectory(definition.CheckpointDirectory); hostPaths.Add(definition.CheckpointDirectory); }
            ManagedDiskHostProtection.Register(definition.ResourceId, hostPaths);
        }
        try
        {
            var runtime = await new ManagedDiskCreationCoordinator(this).CreateAsync(definition, progress, token);
            var latest = entry.Record;
            Update(entry, latest with { Runtime = runtime, StartupSession = startupSession, LastError = null,
                Definition = latest.Definition with { InitializeBlankImage = false, ExpectedBlankImage = null } });
            store.SaveJournal(new(Guid.NewGuid(), definition.ResourceId, ManagedDiskJournalStage.Ready, runtime.BootEpoch, runtime.CreationGeneration));
            return runtime;
        }
        catch (Exception ex)
        {
            var record = entry.Record;
            var runtime = record.Runtime ?? new(definition.ResourceId, startupSession, 1, definition.Mode, ManagedDiskState.Stopped, 0, null);
            var state = entry.Provider is null && entry.Image is null ? ManagedDiskState.Stopped : ManagedDiskState.RecoveryRequired;
            Update(entry, record with { Runtime = runtime with { State = state, LastError = ex.Message }, LastError = ex.Message });
            throw;
        }
    }
    private void Update(Entry entry, ManagedDiskRecord record)
    { record = record with { ImageIo = entry.ImageIo.Snapshot() }; store.Save(record); entry.Record = record; }
    private IDisposable MeasureImages(Entry entry)
    {
        var previous = imageOperation.Value; imageOperation.Value = entry;
        return new ImageMeasurementScope(() => imageOperation.Value = previous);
    }
    private sealed class ImageMeasurementScope(Action restore) : IDisposable { public void Dispose() => restore(); }
    public async Task<IManagedDiskCreation> CreateUnpublishedAsync(ManagedDiskDefinition definition, CancellationToken token)
    {
        var entry = entries[definition.ResourceId];
        var operation = Guid.NewGuid();
        store.SaveJournal(new(operation, definition.ResourceId, ManagedDiskJournalStage.Creating, startupSession, 0));
        try
        {
        if (definition.Mode == ManagedDiskMode.CachedVhdx)
        {
            entry.Paths = ManagedDiskHostProtection.Pin(Path.GetDirectoryName(definition.ImagePath!)!, true);
            entry.Image = definition.Source == ManagedDiskSource.CreateNew
                ? WindowsVirtualDisk.CreateNew(definition.ImagePath!, definition.CapacityBytes, definition.SectorBytes, definition.Allocation)
                : WindowsVirtualDisk.Open(definition.ImagePath!, false, false);
            entry.Disk = WindowsDiskStorage.Open(entry.Image.Attach(false, permanent: true), true);
            var generation = checked(++mountedGeneration);
            var runtime = new ManagedDiskRuntime(definition.ResourceId, mountedEpoch, generation, definition.Mode, ManagedDiskState.Creating, 0, null);
            entry.Record = entry.Record with { Runtime = runtime, PhysicalDiskNumber = entry.Disk.Number,
                OriginalSource = WindowsVirtualDisk.Inspect(definition.ImagePath!, allowAttached: true), StartupSession = startupSession };
            Update(entry, entry.Record with { Runtime = runtime, PhysicalDiskNumber = entry.Disk.Number,
                OriginalSource = WindowsVirtualDisk.Inspect(definition.ImagePath!, allowAttached: true), StartupSession = startupSession });
        }
        else
        {
            var overhead = (definition.CapacityBytes / (4UL << 20) + 1) * 32 + (2UL << 20);
            MemoryBudget.ValidateIncrease(0, checked(definition.CapacityBytes + overhead));
            entry.Provider = WindowsRamDisk.Connect();
            token.ThrowIfCancellationRequested();
            var native = entry.Provider.Create(definition.ResourceId, definition.CapacityBytes, definition.SectorBytes);
            var runtime = new ManagedDiskRuntime(definition.ResourceId, native.BootEpoch, native.CreationGeneration, definition.Mode, ManagedDiskState.Creating, native.WriteGeneration, null);
            entry.Record = entry.Record with { Runtime = runtime, Native = native, StartupSession = startupSession };
            Update(entry, entry.Record);
        }
        return new Creation(this, entry, definition);
        }
        catch (Exception failure)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await AbortCreationAsync(entry, cleanup.Token); }
            catch (Exception cleanupFailure) { throw new AggregateException("Allocation/attachment failed; owned storage is retained where cleanup failed.", failure, cleanupFailure); }
            throw;
        }
    }
    public async Task<IImageReadView> OpenImportAsync(ImageInspection expected, CancellationToken token)
        => await OpenImportCoreAsync(expected, false, token);
    public async Task<IImageReadView> OpenBlankImportAsync(ImageInspection expected, CancellationToken token)
        => await OpenImportCoreAsync(expected, true, token);
    private async Task<IImageReadView> OpenImportCoreAsync(ImageInspection expected, bool requireBlank, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var pins = ManagedDiskHostProtection.Pin(Path.GetDirectoryName(expected.Path)!, false, trustedDirectory: false);
        SafeFileHandle? lease = null; WindowsVirtualDisk? image = null; WindowsDiskStorage? disk = null;
        try
        {
            lease = File.OpenHandle(expected.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actual = WindowsVirtualDisk.Inspect(expected.Path);
            if (actual != expected) throw new IOException("The image identity changed after inspection.");
            image = WindowsVirtualDisk.Open(expected.Path, false, true); disk = WindowsDiskStorage.Open(image.Attach(true), true);
            disk.SetOffline(true);
            if (disk.CapacityBytes != expected.VirtualBytes || disk.SectorBytes != expected.SectorBytes) throw new IOException("The staging disk geometry differs from the source.");
            var measured = (imageOperation.Value ?? throw new InvalidOperationException("Image import requires an owned resource measurement scope.")).ImageIo.Measure(disk);
            if (requireBlank) await ManagedImageLayout.RequireBlankAsync(measured, token);
            else _ = await ManagedImageLayout.InspectAsync(measured, token);
            return new ImportView(pins, lease, image, disk, measured, actual);
        }
        catch { disk?.Dispose(); image?.Dispose(); lease?.Dispose(); pins.Dispose(); throw; }
    }
    private sealed class ImportView(IDisposable pins, SafeFileHandle lease, WindowsVirtualDisk image, WindowsDiskStorage disk, ILogicalDisk measured, ImageInspection identity) : IImageReadView
    {
        public ILogicalDisk LogicalStorage => measured;
        public ImageInspection Identity => identity;
        public ValueTask DisposeAsync() { disk.Dispose(); try { image.Detach(); } finally { image.Dispose(); lease.Dispose(); pins.Dispose(); } return ValueTask.CompletedTask; }
    }
    private sealed class Creation(WindowsManagedDiskEngine engine, Entry entry, ManagedDiskDefinition definition) : IManagedDiskCreation
    {
        private bool transferred;
        private ulong? baseline;
        public Guid ResourceId => definition.ResourceId;
        public Guid BootEpoch => entry.Record.Runtime!.BootEpoch;
        public ulong CreationGeneration => entry.Record.Runtime!.CreationGeneration;
        public ulong WriteGeneration => entry.Provider is null ? 0 : entry.Provider.Query(entry.Record.Native!).WriteGeneration;
        public ulong? DurableBaselineGeneration => baseline;
        public ILogicalDisk? RamStorage => entry.Provider?.Storage(entry.Record.Native!);
        private async Task EnsurePublishedAsync(CancellationToken token)
        {
            if (entry.Provider is not null && !entry.Published)
            {
                var native = entry.Provider.Publish(entry.Record.Native!);
                entry.Disk = await WindowsDiskStorage.ResolveRamAsync(entry.Provider, native, token);
                engine.Update(entry, entry.Record with { Native = native, PhysicalDiskNumber = entry.Disk.Number }); entry.Published = true;
            }
        }
        public async Task InitializeAndFormatAsync(ManagedDiskDefinition value, CancellationToken token)
        {
            if (value.InitializeBlankImage && value.Mode == ManagedDiskMode.CachedVhdx)
            {
                var actual = WindowsVirtualDisk.Inspect(value.ImagePath!, allowAttached: true);
                if (!actual.SameImage(value.ExpectedBlankImage!)) throw new IOException("The selected blank VHDX identity changed before initialization.");
                entry.Disk!.SetOffline(true);
                await ManagedImageLayout.RequireBlankAsync(entry.ImageIo.Measure(entry.Disk), token);
            }
            await EnsurePublishedAsync(token); token.ThrowIfCancellationRequested();
            engine.Update(entry, entry.Record with { Runtime = entry.Record.Runtime! with { State = ManagedDiskState.Formatting } });
            entry.Disk!.InitializeNewGpt(); entry.Disk.SetOffline(false); var volume = await entry.Disk.WaitVolumeAsync(token);
            await WindowsDiskStorage.FormatNtfsAsync(volume, entry.Disk.Number, value.Label, token);
            engine.Update(entry, entry.Record with { VolumePath = volume, GptDiskId = entry.Disk.Layout().DiskId });
        }
        public Task RecordImportedImageAsync(ImageInspection image, LogicalImageDigest digest, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var previous = entry.Record.CommittedImage;
            baseline = WriteGeneration;
            if (previous?.Digest is not null && previous.Digest != digest) throw new IOException("The remembered image's logical contents changed; it was not published.");
            engine.Update(entry, entry.Record with { OriginalSource = entry.Record.OriginalSource ?? image,
                CommittedImage = previous?.Digest is not null ? previous : new(image, digest, baseline), ImageTransferAttempts = entry.Record.ImageTransferAttempts + 1,
                ImageTransferredBytes = checked(entry.Record.ImageTransferredBytes + digest.Bytes) });
            return Task.CompletedTask;
        }
        public async Task SaveInitialImageAsync(ManagedDiskDefinition value, CancellationToken token)
        {
            var path = value.Source == ManagedDiskSource.CreateNew ? value.ImagePath! : Path.Combine(value.CheckpointDirectory!,
                $"{value.ResourceId:N}-initial-{Guid.NewGuid():N}.vhdx");
            var result = await new ManagedImageCheckpoint(engine.store, engine).SaveAsync(entry.Record, path, true, token: token);
            entry.Record = result.Record;
            baseline = result.Record.Runtime!.SavedGeneration;
        }
        public async Task<string> PublishAsync(ManagedDiskDefinition value, CancellationToken token)
        {
            if (value.Source == ManagedDiskSource.OpenExisting)
            {
                var privateLayout = await ManagedImageLayout.InspectAsync(RamStorage ?? entry.Disk!, token);
                WindowsDiskStorage.RefuseOnlineClone(privateLayout, entry.Disk?.Number);
            }
            await EnsurePublishedAsync(token);
            var layout = entry.Disk!.Layout(); entry.Disk.RefuseOnlineClone(layout);
            entry.Disk.SetOffline(false);
            var volume = entry.Record.VolumePath ?? await entry.Disk.WaitVolumeAsync(token);
            if (WindowsDiskStorage.FileSystem(volume) != "NTFS") throw new NotSupportedException("Only unencrypted NTFS images can be activated.");
            if (value.ReadOnly) engine.Update(entry, entry.Record with { Native = entry.Provider!.SetReadOnly(entry.Record.Native!, true) });
            WindowsDiskStorage.AssignLetter(volume, value.PreferredLetter);
            engine.Update(entry, entry.Record with { VolumePath = volume, GptDiskId = layout.DiskId });
            ManagedDiskHostProtection.RegisterVolume(value.ResourceId, volume);
            if (value.Mode == ManagedDiskMode.CachedVhdx)
                await CacheTasks.SaveAsync(value.PreferredLetter + ":", value.Cache!, false, value.AcceptVolatileWrites, token: token);
            transferred = true;
            return value.PreferredLetter + ":";
        }
        public Task AbortAsync(CancellationToken token) => engine.AbortCreationAsync(entry, token);
        public ValueTask DisposeAsync() { _ = transferred; return ValueTask.CompletedTask; }
    }
    private Task AbortCreationAsync(Entry entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var record = entry.Record;
        var locks = new List<IDisposable>();
        try
        {
            if (entry.Disk is not null)
                foreach (var volume in WindowsDiskStorage.Volumes(entry.Disk.Number))
                    locks.Add(WindowsDiskStorage.LockVolume(volume, entry.Disk.Number, true));
            if (record.Definition.Mode == ManagedDiskMode.CachedVhdx && record.VolumePath is not null)
            { using var cache = CacheDevice.OpenVolumeName(record.VolumePath, true); cache.Control(WriteCacheAction.Release); }
            if (record.VolumePath is not null) WindowsDiskStorage.RemoveLetter(record.VolumePath, record.Definition.PreferredLetter);
            entry.Disk?.Dispose(); entry.Disk = null;
            if (entry.Provider is not null && record.Native is not null) entry.Provider.Remove(record.Native);
            entry.Image?.Detach(); entry.Dispose(); entry.Provider = null; entry.Image = null; entry.Paths = null;
            Update(entry, record with { Native = null, PhysicalDiskNumber = null, VolumePath = null,
                Runtime = record.Runtime is null ? null : record.Runtime with { State = ManagedDiskState.Stopped, Volume = null } });
        }
        finally { foreach (var held in locks) held.Dispose(); }
        return Task.CompletedTask;
    }
    public void Dispose() { foreach (var entry in entries.Values) entry.Dispose(); mutation.Dispose(); }
}
