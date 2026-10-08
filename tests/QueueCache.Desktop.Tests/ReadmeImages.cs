using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QueueCache.Desktop.Services;
using QueueCache.Desktop.ViewModels;
using QueueCache.Desktop.Views;
using QueueCache.Management;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;
using static Test;

/// <summary>The README screenshots: a healthy example setup in dark mode. Run with
/// <c>dotnet run --project tests/QueueCache.Desktop.Tests -c Release -- --readme docs/images</c>.</summary>
internal static class ReadmeImages
{
    private const ulong MiB = 1UL << 20, GiB = 1UL << 30;

    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        var now = new DateTimeOffset(2026, 10, 8, 14, 30, 0, TimeSpan.Zero);
        var caches = new DemoCaches();
        var disks = new DemoDisks(caches, now);
        var shell = new ShellViewModel(caches, disks, new FakeDialogs(), new MemorySettingsStore(), () => 24 * GiB, () => new HashSet<char>("CDEIRV"),
            path => disks.Space(path), () => now, new MemorySignInTask(true));
        var window = new MainWindow { DataContext = shell, Width = 1280, Height = 860 };
        window.Show();
        Settle(shell.Monitor.RefreshAsync());
        // A minute of activity so the charts have lines, ending at a busy moment.
        for (var second = 0; second < 85; second++)
        {
            now += TimeSpan.FromSeconds(1);
            caches.Advance(second);
            disks.Advance(second);
            Settle(shell.Monitor.SampleVirtualDisksAsync());
            Settle(shell.Monitor.SampleAsync());
        }
        Check(shell.Monitor.OverallHealth == Health.Ok, "readme: the example setup is healthy");

        Show(window, shell, AppPage.Overview);
        Save(window, output, "overview");

        Show(window, shell, AppPage.Caches);
        var gamesCache = shell.Monitor.Volumes.Single(v => v.Volume.Volume == "D:");
        shell.Caches.Select(gamesCache);
        now += TimeSpan.FromSeconds(1);
        caches.Advance(85);
        disks.Advance(85);
        Settle(shell.Monitor.SampleAsync()); // The selected cache reads its memory map.
        Dispatcher.UIThread.RunJobs();
        Check(gamesCache.HasLayoutMap && gamesCache.LayoutOrderText.EndsWith("in disk order"), "readme: the selected cache shows its memory map");
        Save(window, output, "cache");
        // The memory map card, scrolled into view.
        window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MapCard").BringIntoView();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Save(window, output, "cache-map");

        // Taller, so the RAM disk's details fit without scrolling.
        window.Height = 960;
        Show(window, shell, AppPage.VirtualDisks);
        shell.VirtualDisks.Select(shell.Monitor.VirtualDisks.Single(d => d.IsRamDisk));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Save(window, output, "ram-disk");
        var ramDisk = shell.Monitor.VirtualDisks.Single(d => d.IsRamDisk);
        Check(ramDisk.HasPhysicalMap && ramDisk.PhysicalText.StartsWith("8 GiB in "), "readme: the RAM disk shows its place in physical memory");
        window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PhysicalCard").BringIntoView();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Save(window, output, "ram-disk-physical");
        shell.Stop();
        window.Close();

        var games = caches.Volumes.Single(v => v.Volume == "D:");
        var settings = new CacheSettingsWindow { DataContext = new CacheSettingsViewModel(games, caches.State(games), true, 24 * 1024) };
        settings.Show();
        Save(settings, output, "cache-settings");
        settings.Close();

        var create = new CreateDiskViewModel(disks, new HashSet<char>("CDEIRV"));
        Settle(create.LoadAsync());
        create.Letter = "S:";
        create.Label = "Scratch";
        create.CapacityGiB = 8;
        var creator = new CreateDiskWindow { DataContext = create, Height = 640 };
        creator.Show();
        Save(creator, output, "new-disk");
        creator.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        Console.WriteLine("README images written to " + Path.GetFullPath(output));
    }

    private static void Show(Window window, ShellViewModel shell, AppPage page)
    {
        shell.Page = page;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Save(Window window, string output, string name)
    {
        for (var i = 0; i < 60; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        using var frame = window.CaptureRenderedFrame() ?? throw new Exception("No rendered frame for " + name);
        frame.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
        Console.WriteLine("  " + name + ".png");
    }

    /// <summary>A smooth, believable load curve: busy, calm, busy again.</summary>
    internal static double Wave(int second, double low, double high, double phase = 0) =>
        low + (high - low) * Math.Pow(Math.Max(0, Math.Sin((second + phase) / 6.0)), 2);

    /// <summary>C: (Windows, no cache), D: Games on a hard disk with a Fast cache, E: Projects with a
    /// Strict cache, and V: (a disk image's volume, cached through its virtual disk).</summary>
    private sealed class DemoCaches : ICacheTaskService
    {
        public readonly VolumeDescription[] Volumes =
        [
            new("C:", "Windows", "NTFS", 930L << 30, @"\\?\Volume{0000000c-0000-0000-0000-000000000001}\", 0, "Samsung SSD 990 PRO 1TB", "demo-nvme", 931L << 30, true, true, true),
            new("D:", "Games", "NTFS", 3725L << 30, @"\\?\Volume{0000000d-0000-0000-0000-000000000002}\", 1, "WD Red Plus 4TB", "demo-hdd", 3726L << 30, false, false, false),
            new("E:", "Projects", "NTFS", 1863L << 30, @"\\?\Volume{0000000e-0000-0000-0000-000000000003}\", 2, "Crucial MX500 2TB", "demo-sata", 1863L << 30, false, false, false),
            new("V:", "VMs", "NTFS", 256L << 30, @"\\?\Volume{0000000f-0000-0000-0000-000000000004}\", 3, "Msft Virtual Disk", "demo-vhdx", 256L << 30, false, false, false)
        ];
        private readonly Dictionary<string, (ulong Read, ulong Accepted, ulong Drained, ulong Hits)> counters = new()
        {
            ["D:"] = (412 * GiB, 38 * GiB, 37 * GiB, 380 * GiB), ["E:"] = (96 * GiB, 21 * GiB, 21 * GiB, 81 * GiB), ["V:"] = (55 * GiB, 30 * GiB, 30 * GiB, 47 * GiB)
        };

        public void Advance(int second)
        {
            Add("D:", Wave(second, 20, 720), Wave(second, 4, 160, 2), Wave(second, 0, 140, -3));
            Add("E:", Wave(second, 2, 90, 9), Wave(second, 1, 45, 11), Wave(second, 1, 45, 11));
            Add("V:", Wave(second, 5, 60, 4), Wave(second, 3, 30, 5), Wave(second, 3, 30, 5));
        }

        private void Add(string volume, double readMiB, double writeMiB, double drainMiB)
        {
            var (read, accepted, drained, hits) = counters[volume];
            var r = (ulong)(readMiB * MiB);
            counters[volume] = (read + r, accepted + (ulong)(writeMiB * MiB), drained + (ulong)(drainMiB * MiB), hits + r * 9 / 10);
        }

        public WriteCacheState State(VolumeDescription volume)
        {
            if (volume.Volume == "C:")
                return new(256 | 1024 | 4096, 0, (ulong)volume.Bytes, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
                    { Instance = 1, Generation = 1, GlobalLimitBytes = 48 * GiB, GlobalReservedBytes = 40 * GiB, ExtendedCountersAvailable = true };
            var (budget, fast, readCache, onDisk, dirty) = volume.Volume switch
            {
                "D:" => (8 * GiB, true, 5600 * MiB, 1180 * MiB, 610 * MiB),
                "E:" => (4 * GiB, false, 2650 * MiB, 840 * MiB, 0UL),
                _ => (4 * GiB, false, 1900 * MiB, 1300 * MiB, 0UL)
            };
            var (read, accepted, drained, hits) = counters[volume.Volume];
            var flags = 1u | 128 | 256 | 512 | 1024 | 4096 | (fast ? 32u : 0u);
            var capacity = budget - budget / 64;
            return new(flags, 0, (ulong)volume.Bytes, budget, budget, dirty, 0, capacity, 1, accepted, drained, 0, read, 0, 0, dirty)
            {
                Options = new(Drain: DrainAlgorithm.Idle),
                CleanReadBytes = readCache, CleanWriteBytes = onDisk, ReadHitBytes = hits, ReadMissBytes = read - hits,
                ExtendedCountersAvailable = true, Instance = 1, Generation = 1, OldestDirtyMs = dirty == 0 ? 0UL : 1800UL,
                GlobalLimitBytes = 48 * GiB, GlobalReservedBytes = 40 * GiB
            };
        }

        public Task<IReadOnlyList<VolumeDescription>> ListAsync() => Task.FromResult<IReadOnlyList<VolumeDescription>>(Volumes);
        public Task<WriteCacheState> ReadAsync(VolumeDescription volume) => Task.FromResult(State(volume));
        public Task<CacheLayoutMap?> ReadLayoutMapAsync(VolumeDescription volume) =>
            Task.FromResult(State(volume).PayloadCapacity is > 0 and var bytes ? DemoMap((int)(bytes / (256 * 1024))) : (CacheLayoutMap?)null);
        public bool IsPersistent(VolumeDescription volume) => true;
        public Task SaveAsync(VolumeDescription volume, CacheConfiguration configuration, bool persistent, IProgress<string> progress) => Task.CompletedTask;
        public Task SetEnabledAsync(VolumeDescription volume, bool enabled, bool persistent) => Task.CompletedTask;
        public Task FlushAsync(VolumeDescription volume) => Task.CompletedTask;
        public Task DropCleanAsync(VolumeDescription volume) => Task.CompletedTask;
        public Task RemoveAsync(VolumeDescription volume) => Task.CompletedTask;
        public Task<WorkloadReport> TestAsync(string volume, bool benchmark, IProgress<string> progress, CancellationToken token) =>
            throw new NotSupportedException();
    }

    /// <summary>R: a RAM disk, I: an image in RAM, V: a disk image with a RAM cache.</summary>
    private sealed class DemoDisks : IManagedDiskService
    {
        private readonly List<ManagedDiskRecord> records;
        private readonly string vmsVolume;

        public DemoDisks(DemoCaches caches, DateTimeOffset now)
        {
            vmsVolume = caches.Volumes.Single(v => v.Volume == "V:").VolumePath;
            var ram = ManagedDiskDefinition.New(ManagedDiskMode.EphemeralRam) with { PreferredLetter = 'R', Label = "Scratch", CapacityBytes = 8 * GiB };
            var image = ManagedDiskDefinition.New(ManagedDiskMode.ImageInRam) with
            {
                PreferredLetter = 'I', Label = "Builds", CapacityBytes = 16 * GiB, ImagePath = @"E:\Disks\Builds.vhdx",
                CheckpointDirectory = @"E:\Disks\Checkpoints", StartAtBoot = true
            };
            var vms = ManagedDiskDefinition.New(ManagedDiskMode.CachedVhdx) with
            {
                PreferredLetter = 'V', Label = "VMs", CapacityBytes = 256 * GiB, ImagePath = @"D:\Disks\VMs.vhdx", Cache = new CacheConfiguration(4096, CachePreset.Strict)
            };
            records =
            [
                Live(ram, 1, @"\\?\Volume{000000a1-0000-0000-0000-000000000001}\", 64 * GiB, 41 * GiB) with { },
                Live(image, 2, @"\\?\Volume{000000a2-0000-0000-0000-000000000002}\", 120 * GiB, 18 * GiB) with
                {
                    SavedAt = now.AddMinutes(-12),
                    LastSave = null
                },
                new(vms, new(vms.ResourceId, Guid.NewGuid(), 1, vms.Mode, ManagedDiskState.Ready, 1, null, "V:"), VolumePath: vmsVolume, PhysicalDiskNumber: 3)
            ];
            var saved = records[1];
            records[1] = saved with
            {
                Runtime = saved.Runtime! with { SavedGeneration = saved.Runtime.WriteGeneration },
                LastSave = new(TimeSpan.FromSeconds(11), 16 * GiB, saved.WrittenBytes, saved.Native!.BootEpoch, saved.Native.CreationGeneration)
            };
        }

        private static ManagedDiskRecord Live(ManagedDiskDefinition definition, ulong creation, string volume, ulong read, ulong written)
        {
            var boot = Guid.NewGuid();
            var native = new RamDiskSnapshot(definition.ResourceId, boot, creation, definition.CapacityBytes, 100,
                RamDiskSnapshot.EstimateReservationBytes(definition.CapacityBytes), Guid.Empty, 512, RamDiskFlags.Published | RamDiskFlags.DirectRegistered,
                (uint)creation, read / 20, written / 20, 120, 4, 0, 0);
            return new(definition, new(definition.ResourceId, boot, creation, definition.Mode, ManagedDiskState.Ready, 100, null, definition.PreferredLetter + ":"),
                Native: native, VolumePath: volume, Statistics: new(read / 20 / 65536, written / 20 / 65536, 10_000_000, 0, 0, 0, 0, 0, 0),
                Direct: new(RamDirectAccess.Reads | RamDirectAccess.Writes, RamDirectReason.None, 0, definition.ResourceId, 0, definition.CapacityBytes,
                    read / 32768, written / 32768, read - read / 20, written - written / 20, 0, "NTFS"));
        }

        public void Advance(int second)
        {
            records[0] = Grow(records[0], Wave(second, 150, 2400, 1), Wave(second, 90, 1300, 3));
            records[1] = Grow(records[1], Wave(second, 10, 220, 7), Wave(second, 0, 40, 8));
        }

        private static ManagedDiskRecord Grow(ManagedDiskRecord record, double readMiB, double writeMiB)
        {
            ulong r = (ulong)(readMiB * MiB), w = (ulong)(writeMiB * MiB);
            var native = record.Native!; var direct = record.Direct!; var stats = record.Statistics!;
            return record with
            {
                Native = native with { ReadBytes = native.ReadBytes + r / 20, WriteBytes = native.WriteBytes + w / 20 },
                Direct = direct with { ReadBytes = direct.ReadBytes + r - r / 20, WriteBytes = direct.WriteBytes + w - w / 20,
                    ReadRequests = direct.ReadRequests + r / 32768, WriteRequests = direct.WriteRequests + w / 32768 },
                Statistics = stats with { ReadRequests = stats.ReadRequests + r / 20 / 65536, WriteRequests = stats.WriteRequests + w / 20 / 65536 },
                Physical = record.Physical ?? DemoPhysical(native.CapacityBytes)
            };
        }

        public VolumeSpace? Space(string path) =>
            path.Contains("a1") ? new VolumeSpace(8 * GiB - 40 * MiB, 3100 * MiB) : path.Contains("a2") ? new VolumeSpace(16 * GiB - 64 * MiB, 6900 * MiB) : null;

        public Task<IReadOnlyList<ManagedDiskCapability>> CapabilitiesAsync(CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<ManagedDiskCapability>>(Enum.GetValues<ManagedDiskMode>().Select(m => new ManagedDiskCapability(m, true, null)).ToArray());
        public Task<ImageInspection> InspectAsync(string path, CancellationToken token = default) => throw new NotSupportedException();
        public Task<ManagedDiskRuntime> CreateAsync(ManagedDiskDefinition definition, IProgress<ManagedDiskProgress>? progress = null, CancellationToken token = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedDiskRecord>> ListAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<ManagedDiskRecord>>(records.ToArray());
    }
}
