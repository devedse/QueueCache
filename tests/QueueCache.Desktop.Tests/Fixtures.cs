using Avalonia.Threading;
using QueueCache.Desktop.Services;
using QueueCache.Desktop.ViewModels;
using QueueCache.Management;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;

internal static class Test
{
    /// <summary>A RAM disk's pages over a machine's physical memory: mostly a few long stretches with gaps.</summary>
    public static RamPhysicalMap DemoPhysical(ulong diskBytes, ulong ramBytes = 24UL << 30, int seed = 3)
    {
        var random = new Random(seed);
        ulong span = ramBytes / 4096, pages = diskBytes / 4096, perBin = span / RamPhysicalMap.Bins, left = pages, runs = 0;
        var counts = new uint[RamPhysicalMap.Bins];
        for (var bin = (int)(RamPhysicalMap.Bins * 0.3); left > 0 && bin < RamPhysicalMap.Bins; bin++)
        {
            if (random.Next(10) == 0)
                continue; // Memory Windows already uses.
            var take = Math.Min(left, random.Next(4) == 0 ? perBin / 2 : perBin);
            counts[bin] = (uint)take;
            left -= take;
            runs += take == perBin ? 1UL : 3UL;
        }
        return new(span, pages - left, runs, counts);
    }

    /// <summary>A plausible cache in use: in-order read cache, written data kept for reads, a little
    /// not yet on disk, a band of scattered partly used chunks, and free space at the end.</summary>
    public static CacheLayoutMap DemoMap(int chunks, int seed = 7)
    {
        var random = new Random(seed);
        byte[] used = new byte[chunks], dirty = new byte[chunks], read = new byte[chunks], ordered = new byte[chunks];
        for (var i = 0; i < chunks; i++)
        {
            var at = (double)i / chunks;
            byte u = at < 0.83 ? (byte)64 : at < 0.91 ? (byte)random.Next(12, 50) : random.Next(12) == 0 ? (byte)random.Next(1, 30) : (byte)0;
            used[i] = u;
            if (u == 0)
                continue;
            var scattered = at >= 0.83 && at < 0.91;
            ordered[i] = (byte)(scattered ? random.Next(0, u / 4) : u - 1);
            if (at >= 0.75 && at < 0.83)
                dirty[i] = u;
            else if (at < 0.60 || scattered || at >= 0.91)
                read[i] = u;
        }
        return new(256 * 1024, 1, chunks, used, dirty, read, ordered);
    }

    public static void Check(bool result, string description)
    {
        if (!result)
            throw new Exception("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }

    /// <summary>Runs dispatcher work until a view-model task finishes; fixtures complete synchronously unless a test holds them.</summary>
    public static void Settle(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("A view-model task did not finish.");
            Thread.Sleep(1);
        }
        Dispatcher.UIThread.RunJobs();
        task.GetAwaiter().GetResult();
    }

    public static T Settle<T>(Task<T> task)
    {
        Settle((Task)task);
        return task.Result;
    }
}

/// <summary>C: alone on disk 0 (Windows); Q: (NTFS, Fast cache), S: (FAT32) and R: (unformatted) share disk 1.</summary>
internal sealed class VolumeFixture : ICacheTaskService
{
    public VolumeDescription[] Volumes =
    [
        new("C:", "", "NTFS", 99L << 30, @"\\?\Volume{00000000-0000-0000-0000-000000000001}\", 0, "System SSD", "fixture-system", 100L << 30, true, false, true),
        new("Q:", "Games", "NTFS", 150L << 30, @"\\?\Volume{00000000-0000-0000-0000-000000000002}\", 1, "Game library SSD", "fixture-data", 200L << 30, false, false, false),
        new("S:", "Stick", "FAT32", 16L << 30, @"\\?\Volume{00000000-0000-0000-0000-000000000004}\", 1, "Game library SSD", "fixture-data", 200L << 30, false, false, false),
        new("R:", "", "", 49L << 30, @"\\?\Volume{00000000-0000-0000-0000-000000000003}\", 1, "Game library SSD", "fixture-data", 200L << 30, false, false, false)
    ];
    // Enabled, Fast, release, read/write with confirmed routing, drop-clean, deferred drain.
    public WriteCacheState State = new(1 | 32 | 128 | 256 | 512 | 1024 | 4096, 0, 150UL << 30, 4UL << 30, 4UL << 30, 1536UL << 20, 256UL << 10, 4000UL << 20, 1, 2UL << 30, 512UL << 20, 0, 0, 0, 0, 1536UL << 20)
    {
        Options = new(Drain: DrainAlgorithm.Idle),
        CleanReadBytes = 1024UL << 20,
        CleanWriteBytes = 256UL << 20,
        ReadHitBytes = 820UL << 20,
        ReadMissBytes = 180UL << 20,
        ExtendedCountersAvailable = true,
        Instance = 1,
        Generation = 2,
        OldestDirtyMs = 2100,
        GlobalLimitBytes = 20UL << 30,
        GlobalReservedBytes = 14UL << 30
    };
    public int Pauses, Removes, CleanDrops, Flushes, Saves, Retries, Ejects, DataReads, BlockedReads, InventoryReads;
    public List<(string Volume, bool Enabled)> TimingSets = [], CallerPathSets = [];
    public bool RawVolumeHasCache, FaultStick;
    public TaskCompletionSource<IReadOnlyList<VolumeDescription>>? PendingInventory;
    public TaskCompletionSource<WriteCacheState>? PendingSystem;
    public TaskCompletionSource? PendingFlush;
    public TaskCompletionSource<CacheLayoutMap?>? PendingMap;
    public int MapReads;
    public CacheLayoutMap? Map;
    public CacheConfiguration? Saved;

    public Task<IReadOnlyList<VolumeDescription>> ListAsync()
    {
        InventoryReads++;
        return PendingInventory?.Task ?? Task.FromResult<IReadOnlyList<VolumeDescription>>(Volumes.ToArray());
    }
    public Task<IReadOnlyList<SavedConfiguration>> ListSavedAsync() => Task.FromResult<IReadOnlyList<SavedConfiguration>>(
        [new SavedConfiguration(2, "T:", "fixture-removed", 8L << 30, new CacheConfiguration(256, CachePreset.Strict), false, "{00000000-0000-0000-0000-000000000005}")]);
    public Task<CacheLayoutMap?> ReadLayoutMapAsync(VolumeDescription volume)
    {
        MapReads++;
        return PendingMap?.Task ?? Task.FromResult(Map);
    }
    public Task<WriteCacheState> ReadAsync(VolumeDescription volume)
    {
        if (volume.Volume == "C:" && PendingSystem is not null)
        {
            BlockedReads++;
            return PendingSystem.Task;
        }
        if (volume.Volume == "Q:")
            DataReads++;
        if (volume.Volume == "S:" && FaultStick)
            return Task.FromResult(State with
            {
                Flags = (State.Flags | 2) & ~32U, LastError = unchecked((int)0x80070015), BudgetBytes = 512UL << 20, ReservedBytes = 512UL << 20,
                PayloadCapacity = 480UL << 20, DirtyBytes = 120UL << 20, CleanReadBytes = 200UL << 20, CleanWriteBytes = 0
            });
        return Task.FromResult(volume.Volume is "Q:" or "V:" || volume.Volume == "R:" && RawVolumeHasCache ? State : NoCache);
    }
    public WriteCacheState NoCache => State with { Flags = 256 | 1024 | 4096, BudgetBytes = 0, ReservedBytes = 0, DirtyBytes = 0, PayloadCapacity = 0, CleanReadBytes = 0, CleanWriteBytes = 0 };
    public bool IsPersistent(VolumeDescription volume) => true;
    public Task SetEnabledAsync(VolumeDescription volume, bool enabled, bool persistent)
    {
        if (!enabled)
            Pauses++;
        return Task.CompletedTask;
    }
    public Task RemoveAsync(VolumeDescription volume)
    {
        Removes++;
        return Task.CompletedTask;
    }
    public Task FlushAsync(VolumeDescription volume)
    {
        Flushes++;
        return PendingFlush?.Task ?? Task.CompletedTask;
    }
    public Task DropCleanAsync(VolumeDescription volume)
    {
        CleanDrops++;
        return Task.CompletedTask;
    }
    public Task RetryAsync(VolumeDescription volume)
    {
        Retries++;
        return Task.CompletedTask;
    }
    public Task SaveAsync(VolumeDescription volume, CacheConfiguration configuration, bool persistent, IProgress<string> progress)
    {
        Saves++;
        Saved = configuration;
        return Task.CompletedTask;
    }
    public Task SetTimingAsync(VolumeDescription volume, bool enabled)
    {
        TimingSets.Add((volume.Volume, enabled));
        return Task.CompletedTask;
    }
    public Task SetCallerPathAsync(VolumeDescription volume, bool enabled)
    {
        CallerPathSets.Add((volume.Volume, enabled));
        return Task.CompletedTask;
    }
    public Task<DiskEjectPreview> PreviewEjectAsync(string volume) =>
        Task.FromResult(new DiskEjectPreview(1, "fixture-data", "Game library SSD", ["Q:", "S:", "R:"], true, null));
    public Task<DiskEjectResult> EjectAsync(string volume, IProgress<string> progress, DiskEjectPreview? expected = null)
    {
        Ejects++;
        return Task.FromResult(new DiskEjectResult(expected!, 0, 0, "", true));
    }
    public Task<WorkloadReport> TestAsync(string volume, bool benchmark, IProgress<string> progress, CancellationToken token) =>
        throw new NotSupportedException("Fixture never opens disks.");
}

internal sealed class DiskFixture : IManagedDiskService
{
    public List<ManagedDiskRecord> Records = [];
    public List<ManagedDiskRequest> Requests = [];
    public bool Available = true, FailCreation;
    public int Creates;
    public ulong VirtualBytes = 4UL << 30;
    public ManagedDiskDefinition? Created;

    public Task<IReadOnlyList<ManagedDiskCapability>> CapabilitiesAsync(CancellationToken token = default) =>
        Task.FromResult<IReadOnlyList<ManagedDiskCapability>>(Enum.GetValues<ManagedDiskMode>().Select(mode =>
            new ManagedDiskCapability(mode, Available, Available ? null : "Native provider qualification is required.")).ToArray());
    public Task<ImageInspection> InspectAsync(string path, CancellationToken token = default) =>
        Task.FromResult(new ImageInspection(path, "stable-id", Guid.NewGuid(), VirtualBytes, 2 * ManagedDiskDefinition.MiB, 4096, false));
    public Task<ManagedDiskRuntime> CreateAsync(ManagedDiskDefinition definition, IProgress<ManagedDiskProgress>? progress = null, CancellationToken token = default)
    {
        Creates++;
        Created = definition;
        return FailCreation ? Task.FromException<ManagedDiskRuntime>(new IOException("Native reservation refused.")) :
            Task.FromResult(new ManagedDiskRuntime(definition.ResourceId, Guid.NewGuid(), 1, definition.Mode, ManagedDiskState.Ready, 1, 1, "R:"));
    }
    public Task<IReadOnlyList<ManagedDiskRecord>> ListAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<ManagedDiskRecord>>(Records.ToArray());
    public Task<ManagedDiskOperationResult> ExecuteAsync(ManagedDiskRequest request, IProgress<ManagedDiskProgress>? progress = null, CancellationToken token = default)
    {
        Requests.Add(request);
        return Task.FromResult(new ManagedDiskOperationResult(Records.First(r => r.ResourceId == request.ResourceId), "Done by the fake broker."));
    }

    internal static RamDiskSnapshot Snapshot(ManagedDiskDefinition definition) =>
        new(definition.ResourceId, Guid.NewGuid(), 1, definition.CapacityBytes, 1, RamDiskSnapshot.EstimateReservationBytes(definition.CapacityBytes),
            Guid.Empty, 512, 0, 0, 3UL << 30, 1UL << 30, 10, 0, 0, 1000);

    /// <summary>An image in RAM with unsaved changes (RAM generation 12, saved 9), a RAM disk and a stopped disk image with a cache.</summary>
    public static DiskFixture Sample()
    {
        var image = ManagedDiskDefinition.New(ManagedDiskMode.ImageInRam) with
        {
            PreferredLetter = 'I', Label = "Builds", CapacityBytes = 8UL << 30, ImagePath = @"D:\Disks\Builds.vhdx", CheckpointDirectory = @"D:\Disks\Checkpoints"
        };
        var ram = ManagedDiskDefinition.New(ManagedDiskMode.EphemeralRam) with { PreferredLetter = 'X', Label = "Scratch", CapacityBytes = 2UL << 30 };
        var cached = ManagedDiskDefinition.New(ManagedDiskMode.CachedVhdx) with
        {
            PreferredLetter = 'V', Label = "VMs", CapacityBytes = 64UL << 30, ImagePath = @"D:\Disks\VMs.vhdx", Cache = new CacheConfiguration(2048, CachePreset.Strict)
        };
        return new DiskFixture
        {
            Records =
            [
                new(image, new(image.ResourceId, Guid.NewGuid(), 7, image.Mode, ManagedDiskState.Ready, 12, 9, "I:"), Native: Snapshot(image),
                    VolumePath: @"\\?\Volume{00000000-0000-0000-0000-0000000000a1}\", SavedAt: DateTimeOffset.Now.AddMinutes(-20)),
                new(ram, new(ram.ResourceId, Guid.NewGuid(), 3, ram.Mode, ManagedDiskState.Ready, 1, null, "X:"), Native: Snapshot(ram),
                    VolumePath: @"\\?\Volume{00000000-0000-0000-0000-0000000000a2}\", Statistics: new(52_000, 21_000, 10_000_000, 0, 0, 0, 0, 0, 0),
                    Direct: new(RamDirectAccess.Reads | RamDirectAccess.Writes, RamDirectReason.None, 0, ram.ResourceId, 0, 2UL << 30,
                        410_000, 160_000, 9UL << 30, 3UL << 30, 12, "NTFS")),
                new(cached, new(cached.ResourceId, Guid.NewGuid(), 2, cached.Mode, ManagedDiskState.Stopped, 0, null))
            ]
        };
    }
}

/// <summary>Answers dialogs the way a test says, and records what was asked.</summary>
internal sealed class FakeDialogs : IDialogService
{
    public readonly Queue<string?> Answers = new();
    public readonly List<Confirmation> Asked = [];
    public Action<Confirmation>? WhileAsking;
    public Func<CacheSettingsViewModel, CacheSettingsResult?> EditCache = _ => null;
    public Func<CreateDiskViewModel, ManagedDiskRuntime?> CreateDisk = _ => null;
    public Func<DiskActionViewModel, ManagedDiskOperationResult?> DiskAction = _ => null;
    public CacheSettingsViewModel? LastCacheEditor;

    public Task<string?> ConfirmAsync(Confirmation confirmation)
    {
        Asked.Add(confirmation);
        WhileAsking?.Invoke(confirmation);
        return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : null);
    }
    public Task<CacheSettingsResult?> EditCacheAsync(CacheSettingsViewModel editor)
    {
        LastCacheEditor = editor;
        return Task.FromResult(EditCache(editor));
    }
    public Task<ManagedDiskRuntime?> CreateDiskAsync(CreateDiskViewModel editor) => Task.FromResult(CreateDisk(editor));
    public Task<ManagedDiskOperationResult?> DiskActionAsync(DiskActionViewModel editor) => Task.FromResult(DiskAction(editor));
}
