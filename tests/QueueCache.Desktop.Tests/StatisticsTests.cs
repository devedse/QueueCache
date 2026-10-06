using QueueCache.Desktop.Services;
using QueueCache.Desktop.ViewModels;
using QueueCache.Management;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;
using static Test;

/// <summary>Live figures of virtual disks, the disk image's cache, and Settings → Advanced.</summary>
internal static class StatisticsTests
{
    private const ulong MiB = 1UL << 20;

    public static void Run()
    {
        RamDiskFigures();
        SaveFigures();
        DiskImageCache();
        AdvancedSettings();
        Console.WriteLine("Statistics contracts passed.");
    }

    private static ManagedDiskRecord Ram(out ManagedDiskDefinition definition, RamAccess access = RamAccess.Direct)
    {
        definition = ManagedDiskDefinition.New(ManagedDiskMode.EphemeralRam) with { PreferredLetter = 'X', Label = "Scratch", CapacityBytes = 2UL << 30, Access = access };
        var native = DiskFixture.Snapshot(definition) with { ReadBytes = 0, WriteBytes = 0, Errors = 0 };
        return new(definition, new(definition.ResourceId, native.BootEpoch, 1, definition.Mode, ManagedDiskState.Ready, 1, null, "X:"),
            Native: native, VolumePath: @"\\?\Volume{00000000-0000-0000-0000-0000000000b1}\",
            Statistics: new(0, 0, 10_000_000, 0, 0, 0, 0, 0, 0),
            Direct: new(RamDirectAccess.Reads | RamDirectAccess.Writes, RamDirectReason.None, 0, definition.ResourceId, 0, 2UL << 30, 0, 0, 0, 0, 0, "NTFS"));
    }

    private static ManagedDiskRecord Advance(ManagedDiskRecord record, ulong scsiRead, ulong scsiWritten, ulong directRead, ulong directWritten, ulong requests)
    {
        var native = record.Native!;
        var direct = record.Direct!;
        return record with
        {
            Native = native with { ReadBytes = native.ReadBytes + scsiRead, WriteBytes = native.WriteBytes + scsiWritten },
            Direct = direct with { ReadBytes = direct.ReadBytes + directRead, WriteBytes = direct.WriteBytes + directWritten,
                ReadRequests = direct.ReadRequests + requests / 2 },
            Statistics = record.Statistics! with { ReadRequests = record.Statistics.ReadRequests + requests - requests / 2 }
        };
    }

    private static void RamDiskFigures()
    {
        var now = DateTimeOffset.UtcNow;
        var disks = new DiskFixture { Records = [Ram(out _)] };
        var monitor = new DashboardMonitor(new VolumeFixture(), disks, new FakeDialogs(), clock: () => now, availableRam: () => 8UL << 30,
            usedLetters: () => new HashSet<char>(), volumeSpace: _ => new VolumeSpace(2UL << 30, 1536 * MiB));
        Settle(monitor.SampleVirtualDisksAsync());
        var ram = monitor.VirtualDisks.Single();
        Check(ram.HasActivity && ram.Activity.Count == 1 && ram.ReadingText == "Idle" && ram.RequestsText == "0 per second",
            "a RAM disk's first sample has no rates yet");
        Check(ram.HasSpace && ram.UsedText == "512 MiB of 2 GiB used" && ram.FreeText == "1.5 GiB" && ram.Headline == "512 MiB of 2 GiB used · erased when stopped",
            "a RAM disk shows the space Windows reports as used and free");
        Check(ram.DirectShareText == "No reads or writes yet" && ram.ErrorsText == "0", "a RAM disk without I/O says so instead of 0%");

        now += TimeSpan.FromSeconds(1);
        disks.Records[0] = Advance(disks.Records[0], 20 * MiB, 10 * MiB, 80 * MiB, 40 * MiB, 3000);
        Settle(monitor.SampleVirtualDisksAsync());
        Check(ram.Activity.Count == 2 && ram.ReadingText == "100 MiB/s" && ram.WritingText == "50 MiB/s" && ram.RequestsText == "3,000 per second",
            "rates count both access paths and the requests of both");
        Check(ram.ReadTotalText == "100 MiB" && ram.WrittenTotalText == "50 MiB" && ram.DirectShareText == "80%",
            "totals since start, and the share that went through Direct access");
        Check(ram.Headline == "Reading 100 MiB/s · writing 50 MiB/s", "a busy RAM disk shows its activity in the overview");
        Check(monitor.ActivitySummary == "Reading 100 MiB/s · writing 50 MiB/s", "the notification area tooltip gets the total activity");
        Check(!ram.TimingOn && ram.TimingText == "", "timing figures stay hidden while timing is off");

        // Stopped and started again: a new creation whose counters start from zero.
        now += TimeSpan.FromSeconds(1);
        var restarted = Ram(out _) with { };
        disks.Records[0] = disks.Records[0] with { Native = restarted.Native! with { ResourceId = disks.Records[0].ResourceId, CreationGeneration = 2 },
            Direct = restarted.Direct, Statistics = restarted.Statistics };
        Settle(monitor.SampleVirtualDisksAsync());
        Check(ram.Activity.Count == 1 && ram.ReadingText == "Idle", "a restarted disk starts a new chart instead of showing a negative rate");

        var timed = disks.Records[0];
        disks.Records[0] = timed with
        {
            Native = timed.Native! with { Flags = timed.Native.Flags | RamDiskFlags.Timing },
            Statistics = timed.Statistics! with { TimedReads = 4, ReadTicks = 100, MaxReadTicks = 50, TimedWrites = 0 }
        };
        Settle(monitor.SampleVirtualDisksAsync());
        Check(ram.TimingOn && ram.TimingText.StartsWith("Reads: average 2.5 µs, slowest 5 µs · 4 timed") && ram.TimingText.Contains("Writes: average none yet"),
            "with timing on, a RAM disk shows average and slowest request times");

        var standard = new DiskFixture { Records = [Ram(out _, RamAccess.Standard)] };
        var other = new DashboardMonitor(new VolumeFixture(), standard, new FakeDialogs(), clock: () => now, usedLetters: () => new HashSet<char>());
        Settle(other.SampleVirtualDisksAsync());
        Check(other.VirtualDisks.Single().DirectShareText == "Not used (standard access)" && !other.VirtualDisks.Single().HasSpace,
            "standard access says Direct is not used; without a space answer no space bar is shown");
    }

    private static void SaveFigures()
    {
        var disks = DiskFixture.Sample();
        var image = disks.Records[0];
        var native = image.Native!;
        disks.Records[0] = image with
        {
            Native = native with { WriteBytes = 300 * MiB },
            LastSave = new(TimeSpan.FromSeconds(16), 8UL << 30, 100 * MiB, native.BootEpoch, native.CreationGeneration)
        };
        var monitor = new DashboardMonitor(new VolumeFixture(), disks, new FakeDialogs(), usedLetters: () => new HashSet<char>());
        Settle(monitor.SampleVirtualDisksAsync());
        var disk = monitor.VirtualDisks.Single(d => d.IsImage);
        Check(disk.HasSaveFigures && disk.LastSaveText == "16 s for 8 GiB · 512 MiB/s" && disk.WrittenSinceSaveText == "At most 200 MiB",
            "an image in RAM shows how long its last save took and how much was written since");
        disks.Records[0] = disks.Records[0] with { Native = native with { WriteBytes = 300 * MiB, CreationGeneration = native.CreationGeneration + 1 } };
        Settle(monitor.SampleVirtualDisksAsync());
        Check(disk.WrittenSinceSaveText == "Unknown since the disk was restarted", "after a restart the written amount is not compared with the old save");
        Check(!monitor.VirtualDisks.Single(d => d.IsRamDisk).HasSaveFigures, "a RAM disk has no save figures");
    }

    private static void DiskImageCache()
    {
        var volumes = new VolumeFixture();
        var managed = new VolumeDescription("V:", "VMs", "NTFS", 64L << 30, @"\\?\Volume{00000000-0000-0000-0000-0000000000c1}\", 2, "QueueCache VHDX", "fixture-vhdx", 64L << 30, false, false, false);
        volumes.Volumes = [.. volumes.Volumes, managed];
        var definition = ManagedDiskDefinition.New(ManagedDiskMode.CachedVhdx) with
        {
            PreferredLetter = 'V', Label = "VMs", CapacityBytes = 64UL << 30, ImagePath = @"D:\Disks\VMs.vhdx", Cache = new CacheConfiguration(2048, CachePreset.Strict)
        };
        var disks = new DiskFixture
        {
            Records = [new(definition, new(definition.ResourceId, Guid.NewGuid(), 2, definition.Mode, ManagedDiskState.Ready, 0, null, "V:"), VolumePath: managed.VolumePath)]
        };
        var monitor = new DashboardMonitor(volumes, disks, new FakeDialogs(), availableRam: () => 8UL << 30, usedLetters: () => new HashSet<char>());
        Settle(monitor.RefreshAsync());
        var disk = monitor.VirtualDisks.Single();
        Check(monitor.Volumes.All(v => v.Volume.Volume != "V:") && disk.Cache?.Volume.Volume == "V:" && disk.Cache.HasCache,
            "a disk image's cache is shown on the virtual disk, not in the Caches list");
        Check(disk.Headline == "1.5 GiB not yet on disk" && !disk.HasActivity, "the overview row of a disk image shows its cache's state");
        Check(monitor.DisksReservedBytes == volumes.State.ReservedBytes, "the disk image's cache RAM counts as virtual disk RAM");
    }

    private static void AdvancedSettings()
    {
        var volumes = new VolumeFixture();
        volumes.State = volumes.State with { Performance = new(10_000_000, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0) };
        var disks = new DiskFixture { Records = [Ram(out _)] };
        var monitor = new DashboardMonitor(volumes, disks, new FakeDialogs(), usedLetters: () => new HashSet<char>());
        Settle(monitor.RefreshAsync());
        Check(volumes.TimingSets.Count == 0 && volumes.CallerPathSets.Count == 0 && disks.Requests.Count == 0,
            "with the default settings nothing is changed in any driver");

        monitor.DriverTiming = true;
        monitor.CallerPath = false;
        Settle(monitor.SampleVirtualDisksAsync());
        Settle(monitor.SampleAsync());
        var cached = monitor.Volumes.Where(v => v.HasCache).Select(v => v.Volume.Volume).ToHashSet();
        Check(volumes.TimingSets.Count == cached.Count && volumes.TimingSets.All(s => s.Enabled && cached.Contains(s.Volume)),
            "timing is turned on for every cache");
        Check(volumes.CallerPathSets.Count == cached.Count && volumes.CallerPathSets.All(s => !s.Enabled), "the caller path is turned off for every cache");
        Check(disks.Requests.Count == 1 && disks.Requests[0] is { Action: ManagedDiskAction.SetTiming, Timing: true, Expected: not null },
            "timing is turned on for a running RAM disk, for the disk identity it was seen with");

        // The fixtures do not change state, so the drivers still report timing off: not asked again.
        Settle(monitor.SampleVirtualDisksAsync());
        Settle(monitor.SampleAsync());
        Check(volumes.TimingSets.Count == cached.Count && volumes.CallerPathSets.Count == cached.Count && disks.Requests.Count == 1,
            "a driver that keeps its state is not asked again every update");

        // A cache started again is a new instance: the setting is applied again.
        volumes.State = volumes.State with { Instance = 2 };
        Settle(monitor.SampleAsync());
        Check(volumes.TimingSets.Count == 2 * cached.Count && volumes.CallerPathSets.Count == 2 * cached.Count, "a new cache instance gets the settings again");

        monitor.CallerPath = true;
        Settle(monitor.SampleAsync());
        Check(volumes.CallerPathSets.Count == 3 * cached.Count && volumes.CallerPathSets.TakeLast(cached.Count).All(s => s.Enabled),
            "turning the caller path back on restores it on every cache");

        var store = new MemorySettingsStore();
        var settings = new SettingsViewModel(store) { DriverTiming = true, CallerPath = false };
        Check(store.Load() is { DriverTiming: true, CallerPath: false } && settings.DriverTimingDescription.Length > 0, "the Advanced settings are saved");
    }
}
