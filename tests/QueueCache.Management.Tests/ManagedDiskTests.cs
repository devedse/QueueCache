using System.Reflection;
using System.Runtime.InteropServices;
using QueueCache.Management;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;

internal static class ManagedDiskTests
{
    private const ulong MiB = ManagedDiskDefinition.MiB;
    public static async Task RunAsync()
    {
        DefinitionsAndStartup();
        NativeImageAbi();
        NativeRamAbi();
        await LogicalTransfers();
        await CreationTransactions();
        Console.WriteLine("Managed-disk contracts passed (no driver or real disk access).");
    }

    private static ManagedDiskDefinition Definition(ManagedDiskMode mode) => ManagedDiskDefinition.New(mode) with
    {
        CapacityBytes = 16 * MiB,
        ImagePath = mode == ManagedDiskMode.EphemeralRam ? null : @"C:\Images\Source.vhdx",
        CheckpointDirectory = mode == ManagedDiskMode.ImageInRam ? @"C:\Images\Checkpoints" : null
    };

    private static void NativeRamAbi()
    {
        var expected = new RamDiskSnapshot(Guid.NewGuid(), Guid.NewGuid(), 7, 16 * MiB, 19,
            18 * MiB, Guid.Empty, 512, RamDiskFlags.Published, 5, 1024, 2048, 3, 4, 0, 6);
        var wire = RamDiskSnapshot.Request(RamDiskAction.Query, expected);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(wire.AsSpan(80), expected.ReservedBytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(108), (uint)expected.Flags);
        var decoded = RamDiskSnapshot.Decode(wire);
        decoded.RequireSameCreation(expected);
        Check(decoded.WriteGeneration == expected.WriteGeneration && decoded.CapacityBytes == expected.CapacityBytes &&
            decoded.ReservedBytes == expected.ReservedBytes, "RAM ABI retains exact geometry, generations and reservation");
        Throws<IOException>(() => decoded.RequireSameCreation(expected with { BootEpoch = Guid.NewGuid() }));
        Throws<IOException>(() => decoded.RequireSameCreation(expected with { CreationGeneration = 8 }));
        Throws<IOException>(() => decoded.RequireSameCreation(expected with { Slot = 4 }));
        Throws<InvalidDataException>(() => RamDiskSnapshot.Decode(wire[..^1]));
        var badVersion = (byte[])wire.Clone(); badVersion[4] = 2;
        Throws<InvalidDataException>(() => RamDiskSnapshot.Decode(badVersion));
        var badFrozen = (byte[])wire.Clone(); badFrozen[108] |= (byte)RamDiskFlags.Frozen;
        Throws<InvalidDataException>(() => RamDiskSnapshot.Decode(badFrozen));
        var insufficient = (byte[])wire.Clone(); insufficient.AsSpan(80, 8).Clear();
        Throws<InvalidDataException>(() => RamDiskSnapshot.Decode(insufficient));
        Throws<ArgumentException>(() => RamDiskSnapshot.Request(RamDiskAction.Write, expected, transferBytes: RamDiskSnapshot.MaximumTransferBytes + 1));
        Throws<ArgumentException>(() => RamDiskSnapshot.Request(RamDiskAction.Create, resource: Guid.NewGuid(), capacity: 16 * MiB + 512));
        Throws<ArgumentException>(() => RamDiskSnapshot.Request(RamDiskAction.Remove));
        Throws<ArgumentException>(() => RamDiskSnapshot.Request(RamDiskAction.Freeze, expected));
        Check(RamDiskSnapshot.Request(RamDiskAction.Read, expected, transferBytes: 4096).Length == RamDiskSnapshot.WireSize + 4096,
            "bounded RAM transfers carry bytes, never user pointers");
    }

    private static void DefinitionsAndStartup()
    {
        var epoch = Guid.NewGuid();
        foreach (var mode in Enum.GetValues<ManagedDiskMode>())
        {
            var definition = Definition(mode);
            definition.Validate();
            Check(ManagedDiskStartup.Decide(definition, epoch, null) == ManagedDiskStartupAction.LeaveStopped, "startup is independent of recipe creation");
            var enabled = definition with { StartAtBoot = true };
            var expected = mode switch
            {
                ManagedDiskMode.EphemeralRam => ManagedDiskStartupAction.CreateAndFormatEmpty,
                ManagedDiskMode.CachedVhdx => ManagedDiskStartupAction.MountImageAndCache,
                _ => ManagedDiskStartupAction.LoadCommittedImage
            };
            Check(ManagedDiskStartup.Decide(enabled, epoch, null) == expected, "only pure RAM is recreated and formatted at startup");
            foreach (var state in new[] { ManagedDiskState.Ready, ManagedDiskState.Saving, ManagedDiskState.Faulted, ManagedDiskState.RecoveryRequired })
            {
                var live = new ManagedDiskRuntime(definition.ResourceId, epoch, 7, mode, state, 9, 1);
                Check(ManagedDiskStartup.Decide(enabled, epoch, live) == ManagedDiskStartupAction.AdoptLive, "service restart adopts live and dirty devices without formatting");
                Throws<IOException>(() => ManagedDiskStartup.Decide(enabled, Guid.NewGuid(), live));
                Throws<IOException>(() => live.CheckExpected(epoch, 8));
            }
        }
        var ram = Definition(ManagedDiskMode.EphemeralRam);
        Throws<ArgumentException>(() => (ram with { ImagePath = @"C:\Disk.vhdx" }).Validate());
        Throws<ArgumentException>(() => (ram with { Source = ManagedDiskSource.OpenExisting }).Validate());
        Throws<ArgumentException>(() => (ram with { Cache = new CacheConfiguration(4) }).Validate());
        Throws<ArgumentException>(() => (ram with { SaveBeforeStopping = true }).Validate());
        Throws<ArgumentException>(() => (ram with { CapacityBytes = 16 * MiB + 512 }).Validate());
        Throws<ArgumentException>(() => (ram with { PreferredLetter = 'C' }).Validate());
        Throws<ArgumentException>(() => (ram with { SchemaVersion = 2 }).Validate());
        var backed = Definition(ManagedDiskMode.CachedVhdx) with { Cache = new CacheConfiguration(1, CachePreset.Strict) };
        backed.Validate(); // Reuse the normal cache contract; memory need not equal disk capacity.
        Throws<ArgumentException>(() => (backed with { Cache = backed.Cache! with { Preset = CachePreset.Fast } }).Validate());
        (backed with { Cache = backed.Cache! with { Preset = CachePreset.Fast }, AcceptVolatileWrites = true }).Validate();
        var image = Definition(ManagedDiskMode.ImageInRam) with { Source = ManagedDiskSource.OpenExisting, CapacityBytes = 64UL << 30 };
        new ImageInspection(image.ImagePath!, "source-id", Guid.NewGuid(), image.CapacityBytes, 3UL << 30, 512, false).ValidateFor(image);
        Throws<InvalidDataException>(() => new RamReservationQuote(3UL << 30, MiB, MiB).Validate(image, 80UL << 30));
        Throws<InvalidDataException>(() => new RamReservationQuote(image.CapacityBytes, MiB, MiB).Validate(image, image.CapacityBytes));
        new RamReservationQuote(image.CapacityBytes, MiB, MiB).Validate(image, 80UL << 30);
        Throws<OverflowException>(() => _ = new RamReservationQuote(ulong.MaxValue, MiB, MiB).TotalBytes);
        Throws<InvalidDataException>(() => new ImageInspection(image.ImagePath!, "id", Guid.NewGuid(), image.CapacityBytes, 3UL << 30, 512, true).ValidateFor(image));
        foreach (var path in new[] { @"\\server\share\disk.vhdx", @"C:\Images\..\disk.vhdx", @"C:\disk.vhdx:stream", "disk.vhdx" })
            Throws<ArgumentException>(() => ManagedDiskPaths.ValidateImagePath(path));
        ManagedDiskPaths.ValidateImagePath(@"C:\Images\A's $disk.vhdx");
        var runtime = new ManagedDiskRuntime(image.ResourceId, epoch, 1, image.Mode, ManagedDiskState.Ready, 12, 8);
        Check(runtime.RecordSaved(10).HasUnsavedChanges, "writes after freeze remain dirty after saving the frozen generation");
        Check(!runtime.RecordSaved(12).HasUnsavedChanges, "matching checkpoint generation is clean");
        Throws<InvalidDataException>(() => runtime.RecordSaved(13));
    }

    private static void NativeImageAbi()
    {
        // Layout only; never invokes Windows functions on the host.
#pragma warning disable CA1416
        var native = typeof(WindowsVirtualDisk);
#pragma warning restore CA1416
        Type Nested(string name) => native.GetNestedType(name, BindingFlags.NonPublic)!;
        Check(Marshal.SizeOf(Nested("VirtualStorage")) == 20, "VIRTUAL_STORAGE_TYPE ABI");
        Check(Marshal.SizeOf(Nested("OpenParameters")) == 28, "OPEN_VIRTUAL_DISK_PARAMETERS v2 ABI");
        if (IntPtr.Size == 8)
        {
            var create = Nested("CreateParameters");
            Check(Marshal.SizeOf(create) == 128 && Marshal.OffsetOf(create, "Id").ToInt32() == 8 &&
                Marshal.OffsetOf(create, "MaximumSize").ToInt32() == 24 && Marshal.OffsetOf(create, "Parent").ToInt32() == 48 &&
                Marshal.OffsetOf(create, "Resiliency").ToInt32() == 108, "CREATE_VIRTUAL_DISK_PARAMETERS x64 v2 ABI");
        }
    }

    private static async Task LogicalTransfers()
    {
        var source = new MemoryDisk(32 * 512) { MaxRead = 512 };
        // Includes sparse zero sectors, an initial layout header and a backup header at the very end.
        source.Bytes[0] = 0xA5; source.Bytes[7000] = 42; source.Bytes[^1] = 0x5A;
        var destination = new MemoryDisk(source.Bytes.Length);
        Array.Fill(destination.Bytes, (byte)0xFF);
        var progress = new CaptureProgress<ImageTransferProgress>();
        var digest = await LogicalImageTransfer.CopyAsync(source, destination, progress, chunkBytes: 4096);
        Check(source.Bytes.SequenceEqual(destination.Bytes) && destination.Flushed, "all logical sectors, including zero regions and tail, are copied and flushed");
        Check(progress.Items.Last() == new ImageTransferProgress(source.CapacityBytes, source.CapacityBytes), "transfer reports complete virtual capacity");
        await LogicalImageTransfer.VerifyAsync(destination, digest);
        destination.Bytes[^1] ^= 1;
        await ThrowsAsync<InvalidDataException>(() => LogicalImageTransfer.VerifyAsync(destination, digest));
        await ThrowsAsync<InvalidDataException>(() => LogicalImageTransfer.CopyAsync(source, new MemoryDisk(512)));
        source.ReadFailureAt = 8192;
        destination.Flushed = false;
        await ThrowsAsync<IOException>(() => LogicalImageTransfer.CopyAsync(source, destination, chunkBytes: 4096));
        Check(!destination.Flushed, "read failures cannot report a complete image");
        source.ReadFailureAt = null; source.MaxRead = 0;
        await ThrowsAsync<InvalidDataException>(() => LogicalImageTransfer.CopyAsync(source, destination));
        source.MaxRead = 511;
        await ThrowsAsync<InvalidDataException>(() => LogicalImageTransfer.CopyAsync(source, destination));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => LogicalImageTransfer.CopyAsync(new MemoryDisk(512), new MemoryDisk(512), token: cancel.Token));
    }

    private static async Task CreationTransactions()
    {
        foreach (var mode in Enum.GetValues<ManagedDiskMode>())
        {
            var definition = Definition(mode);
            var backend = new FakeBackend(definition);
            var result = await new ManagedDiskCreationCoordinator(backend).CreateAsync(definition);
            Check(result.State == ManagedDiskState.Ready && backend.Formatted && backend.Published && backend.Disposed && !backend.Aborted, "common new-disk creation and ownership transfer for " + mode);
            Check(backend.InitialSaved == (mode == ManagedDiskMode.ImageInRam), "initial image save only for full-image mode");
        }
        var existing = Definition(ManagedDiskMode.ImageInRam) with { Source = ManagedDiskSource.OpenExisting };
        var import = new FakeBackend(existing);
        var runtime = await new ManagedDiskCreationCoordinator(import).CreateAsync(existing);
        Check(!import.Formatted && !import.InitialSaved && import.ImportDisposed && import.CopyCompleteAtPublication, "existing image is completely loaded and detached before publication, never formatted");
        Check(runtime.SavedGeneration == 4 && runtime.WriteGeneration == 5 && runtime.HasUnsavedChanges, "post-publication writes are not included in the loaded image baseline");
        var cached = Definition(ManagedDiskMode.CachedVhdx) with { Source = ManagedDiskSource.OpenExisting };
        var backing = new FakeBackend(cached);
        await new ManagedDiskCreationCoordinator(backing).CreateAsync(cached);
        Check(!backing.Formatted && !backing.OpenedImport, "mounted image cache preserves existing content without full-image transfer");
        var unavailable = new FakeBackend(existing) { Available = false };
        await ThrowsAsync<NotSupportedException>(() => new ManagedDiskCreationCoordinator(unavailable).CreateAsync(existing));
        Check(!unavailable.Allocated, "capability refusal happens before memory allocation");
        var changed = new FakeBackend(existing) { ChangeSource = true };
        await ThrowsAsync<IOException>(() => new ManagedDiskCreationCoordinator(changed).CreateAsync(existing));
        Check(changed.Aborted && changed.ImportDisposed && !changed.Published, "changed source is refused with owned cleanup");
        var failed = new FakeBackend(existing) { FailImport = true };
        await ThrowsAsync<IOException>(() => new ManagedDiskCreationCoordinator(failed).CreateAsync(existing));
        Check(failed.Aborted && !failed.Published && failed.ImportDisposed, "incomplete import is never published");
        using var cancel = new CancellationTokenSource();
        var cancelled = new FakeBackend(existing) { CancelOnImport = cancel };
        await ThrowsAsync<OperationCanceledException>(() => new ManagedDiskCreationCoordinator(cancelled).CreateAsync(existing, token: cancel.Token));
        Check(cancelled.Aborted && cancelled.CleanupTokenWasLive && !cancelled.Published, "cancellation uses a separate live cleanup deadline");
        var recovery = new FakeBackend(existing) { FailImport = true, FailCleanup = true };
        var aggregate = await ThrowsAsync<AggregateException>(() => new ManagedDiskCreationCoordinator(recovery).CreateAsync(existing));
        Check(aggregate.InnerExceptions.Count == 2 && !recovery.Published, "cleanup failure preserves both errors and requires recovery");
    }

    internal sealed class MemoryDisk(int bytes) : ILogicalDisk
    {
        public byte[] Bytes { get; } = new byte[bytes];
        public ulong CapacityBytes => (ulong)Bytes.Length;
        public uint SectorBytes => 512;
        public int MaxRead = int.MaxValue;
        public ulong? ReadFailureAt;
        public bool Flushed;
        public ValueTask<int> ReadAsync(ulong offset, Memory<byte> buffer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (ReadFailureAt is not null && offset >= ReadFailureAt) throw new IOException("Injected owned image read failure.");
            var count = Math.Min(buffer.Length, MaxRead);
            Bytes.AsMemory((int)offset, count).CopyTo(buffer);
            return ValueTask.FromResult(count);
        }
        public ValueTask WriteAsync(ulong offset, ReadOnlyMemory<byte> buffer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); buffer.CopyTo(Bytes.AsMemory((int)offset)); return ValueTask.CompletedTask;
        }
        public ValueTask FlushAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Flushed = true; return ValueTask.CompletedTask; }
    }

    private sealed class FakeBackend(ManagedDiskDefinition definition) : IManagedDiskBackend, IManagedDiskCreation
    {
        public bool Available = true, Allocated, Formatted, Published, Aborted, Disposed, ImportDisposed, InitialSaved,
            OpenedImport, CopyCompleteAtPublication, ChangeSource, FailImport, FailCleanup, CleanupTokenWasLive;
        public CancellationTokenSource? CancelOnImport;
        private readonly MemoryDisk source = new((int)definition.CapacityBytes);
        private readonly MemoryDisk ram = new((int)definition.CapacityBytes);
        private readonly Guid imageId = Guid.NewGuid();
        public Guid ResourceId => definition.ResourceId;
        public Guid BootEpoch { get; } = Guid.NewGuid();
        public ulong CreationGeneration => 1;
        public ulong WriteGeneration => Published ? 5UL : 4UL;
        public ILogicalDisk? RamStorage => ram;
        private ImageInspection Inspection => new(definition.ImagePath!, "stable-id", imageId, definition.CapacityBytes, MiB, 512, false);
        public Task<ManagedDiskCapability> CapabilityAsync(ManagedDiskMode mode, CancellationToken token) => Task.FromResult(new ManagedDiskCapability(mode, Available, "Not qualified."));
        public Task<ImageInspection> InspectAsync(string path, CancellationToken token) => Task.FromResult(Inspection);
        public Task<IImageReadView> OpenImportAsync(ImageInspection expected, CancellationToken token)
        {
            OpenedImport = true; source.Bytes[^1] = 7;
            if (FailImport) source.ReadFailureAt = 8 * MiB;
            CancelOnImport?.Cancel();
            return Task.FromResult<IImageReadView>(new ReadView(this, ChangeSource ? Inspection with { FileIdentity = "replacement-id" } : Inspection));
        }
        public Task<IManagedDiskCreation> CreateUnpublishedAsync(ManagedDiskDefinition value, CancellationToken token) { Allocated = true; return Task.FromResult<IManagedDiskCreation>(this); }
        public Task InitializeAndFormatAsync(ManagedDiskDefinition value, CancellationToken token) { Formatted = true; return Task.CompletedTask; }
        public Task SaveInitialImageAsync(ManagedDiskDefinition value, CancellationToken token) { InitialSaved = true; return Task.CompletedTask; }
        public Task<string> PublishAsync(ManagedDiskDefinition value, CancellationToken token)
        {
            if (OpenedImport && !ImportDisposed) throw new Exception("Source staging view remained attached at publication.");
            CopyCompleteAtPublication = source.Bytes.SequenceEqual(ram.Bytes) && ram.Flushed;
            Published = true; return Task.FromResult("R:");
        }
        public Task AbortAsync(CancellationToken token)
        {
            Aborted = true; CleanupTokenWasLive = !token.IsCancellationRequested;
            return FailCleanup ? Task.FromException(new IOException("Cleanup failed.")) : Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        private sealed class ReadView(FakeBackend owner, ImageInspection inspection) : IImageReadView
        {
            public ILogicalDisk LogicalStorage => owner.source;
            public ImageInspection Identity => inspection;
            public ValueTask DisposeAsync() { owner.ImportDisposed = true; return ValueTask.CompletedTask; }
        }
    }
    private sealed class CaptureProgress<T> : IProgress<T>
    {
        public List<T> Items { get; } = [];
        public void Report(T value) => Items.Add(value);
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T exception) { return exception; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
