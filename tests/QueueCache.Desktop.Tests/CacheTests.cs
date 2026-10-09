using QueueCache.Desktop.Services;
using QueueCache.Desktop.ViewModels;
using static Test;

/// <summary>Volume caches as the monitor and the Caches page see them. No real volume is opened.</summary>
internal static class CacheTests
{
    private static void MapLifecycle()
    {
        var volumes = new VolumeFixture();
        var now = DateTimeOffset.UtcNow;
        var monitor = new DashboardMonitor(volumes, new DiskFixture(), new FakeDialogs(), clock: () => now,
            availableRam: () => 8UL << 30, usedLetters: () => new HashSet<char>());
        Settle(monitor.RefreshAsync());
        var page = new CachesViewModel(monitor);
        var q = monitor.Volumes.Single(v => v.Volume.Volume == "Q:");
        var s = monitor.Volumes.Single(v => v.Volume.Volume == "S:");
        volumes.Map = DemoMap(4) with { Generation = volumes.State.Generation };
        page.Select(q);
        Settle(monitor.SampleVolumeAsync(q));
        Check(q.HasLayoutMap && volumes.MapReads == 1, "the selected cache gets a complete map from its current allocation");
        now += TimeSpan.FromMilliseconds(500);
        Settle(monitor.SampleVolumeAsync(q));
        Check(volumes.MapReads == 1, "map polling is limited to one request every two seconds");
        now += TimeSpan.FromSeconds(2);
        volumes.PendingMap = new();
        var pending = monitor.SampleVolumeAsync(q);
        page.Select(s);
        volumes.PendingMap.SetResult(volumes.Map);
        Settle(pending);
        Check(!q.HasLayoutMap, "a map arriving after selection changed cannot republish the old cache");
        volumes.PendingMap = null;
        page.Select(q);
        volumes.Map = volumes.Map! with { Generation = volumes.State.Generation + 1 };
        Settle(monitor.SampleVolumeAsync(q));
        Check(!q.HasLayoutMap, "a map from another cache generation is rejected");
        now += TimeSpan.FromSeconds(2);
        volumes.Map = DemoMap(4) with { Generation = volumes.State.Generation, TotalChunks = 5 };
        Settle(monitor.SampleVolumeAsync(q));
        Check(!q.HasLayoutMap, "a truncated map does not advertise totals for the complete cache");
        now += TimeSpan.FromSeconds(2);
        volumes.Map = DemoMap(4) with { Generation = volumes.State.Generation };
        Settle(monitor.SampleVolumeAsync(q));
        q.Apply(volumes.State with { Generation = volumes.State.Generation + 1 }, null, now);
        Check(!q.HasLayoutMap, "a cache resize clears the prior allocation map immediately");
        q.Apply(volumes.State, null, now);
        Settle(monitor.SampleVolumeAsync(q));
        Check(q.HasLayoutMap, "a fresh map returns after a cache allocation changes");
        q.MarkStale();
        Check(!q.HasLayoutMap, "stale cache state hides its memory map");
        q.Apply(volumes.NoCache, null, now);
        Check(!q.HasLayoutMap, "removing a cache clears the memory map");
    }

    public static void Run()
    {
        MapLifecycle();
        MapVisibility();
        var volumes = new VolumeFixture();
        var dialogs = new FakeDialogs();
        var now = DateTimeOffset.UtcNow;
        var monitor = new DashboardMonitor(volumes, new DiskFixture(), dialogs, clock: () => now, availableRam: () => 8UL << 30,
            usedLetters: () => new HashSet<char> { 'C', 'Q', 'S', 'R' }) { Interval = TimeSpan.FromSeconds(0.5) };
        Settle(monitor.RefreshAsync());
        VolumeViewModel Volume(string letter) => monitor.Volumes.Single(v => v.Volume.Volume == letter);

        Check(monitor.DiskGroups.Count == 2 && monitor.DiskGroups[1].Header == "Disk 1 · Game library SSD · 200 GiB" &&
              monitor.DiskGroups[1].Volumes.Count == 3, "volumes are grouped under the disk that holds them");
        Check(monitor.DiskGroups[1].Hint == "Each volume has its own cache" && monitor.DiskGroups[0].Hint == "", "a shared disk says each volume has its own cache");
        Check(!monitor.DiskGroups[0].CanEject && monitor.DiskGroups[1].CanEject && Volume("Q:").Group == monitor.DiskGroups[1] &&
              monitor.DiskGroups[1].EjectText == "Safely eject disk 1…", "one eject action per disk, never on the Windows or paging-file disk");
        Check(!Volume("R:").CanEdit && Volume("R:").Description.Contains("not formatted") && Volume("R:").KindText == "Not formatted",
            "an unformatted volume explains why it cannot get a cache");
        Check(Volume("S:").CanEdit && Volume("S:").Warning?.StartsWith("FAT32") == true && Volume("S:").Warning!.Contains("journal"),
            "a FAT32 volume can get a cache and warns that it has no journal");
        Check(monitor.Disconnected.Count == 1 && monitor.Disconnected[0].Text.Contains("T:") && monitor.Disconnected[0].Text.Contains("Not connected"),
            "a disconnected saved volume is listed without live values");

        var q = Volume("Q:");
        Check(q.HasCache && q.Health == Health.Ok && q.StatusText == "Active" && q.IsVolatile && q.PresetText == "Fast",
            "an operational Fast cache is Active and marked volatile");
        Check(q.PendingText == "1.5 GiB" && q.UsedText == "2.75 GiB of 3.91 GiB used" && q.ReadHitText == "82%" && q.Headline == "1.5 GiB not yet on disk",
            "cache figures use binary units and three significant digits");
        Check(q.ReadingText == "Idle" && q.Activity.Count == 1, "no activity reads Idle, not 0.0 MB/s");
        Check(!Volume("C:").ShowStatus && q.ShowStatus && Volume("C:").KindText == "No cache", "a volume without a cache needs no status word");
        Check(monitor.HealthDetail.Contains("1 volume holds writes only in RAM") && monitor.OverallHealth == Health.Ok,
            "the summary says when writes are only in RAM");
        Check(monitor.RamSummary == "14 GiB of 20 GiB allowed" && monitor.FreeText == "6 GiB", "RAM summary shows the driver's whole reservation against the allowed limit");

        // Manual refresh is immediate; the inventory fallback is independent of the update interval.
        var reads = volumes.InventoryReads;
        Settle(monitor.RefreshAsync());
        Check(volumes.InventoryReads == reads + 1 && monitor.NextInventory == now + DashboardMonitor.InventoryFallback,
            "manual refresh is immediate and the fallback rescan is two minutes away regardless of the interval");
        Settle(monitor.TickAsync());
        Check(volumes.InventoryReads == reads + 1, "an update tick does not rescan volumes before the fallback is due");

        // Actions go through the service.
        Settle(q.TogglePauseCommand.ExecuteAsync(null));
        Settle(q.ClearReadCacheCommand.ExecuteAsync(null));
        Settle(q.RemoveCommand.ExecuteAsync(null));
        Check(volumes.Pauses == 1 && volumes.CleanDrops == 1 && volumes.Removes == 1, "Pause, Clear read cache and Remove call the cache service");
        Check(q.NoticeText == "Cache removed. Its RAM is free again." && q.NoticeSeverity == NoticeSeverity.Success && q.HasNotice,
            "the result appears on the volume the action ran on");
        q.HasNotice = false;
        Check(q.NoticeText is null, "closing the notice clears it");

        // A pending flush disables only that volume's actions; telemetry and other volumes continue.
        volumes.PendingFlush = new();
        var flush = q.FlushCommand.ExecuteAsync(null);
        Check(q.IsBusy && !q.CanPause && !q.CanFlush && q.OperationText == "Flushing…", "same-volume actions are disabled while it flushes");
        Check(Volume("S:").CanEdit, "another volume stays configurable during the flush");
        var dataReads = volumes.DataReads;
        Settle(monitor.SampleAsync());
        Check(volumes.DataReads > dataReads, "telemetry continues during a pending flush");
        volumes.PendingFlush.SetResult();
        volumes.PendingFlush = null;
        Settle(flush);
        Check(!q.IsBusy && q.CanPause && q.NoticeText == "All pending writes are on the disk.", "actions return after the flush");

        // A stalled volume or inventory does not hold up the others; one outstanding read per volume.
        volumes.PendingInventory = new();
        var discovery = monitor.RefreshAsync();
        volumes.PendingSystem = new();
        var blocked = monitor.SampleAsync();
        dataReads = volumes.DataReads;
        Settle(monitor.SampleAsync());
        Check(volumes.DataReads > dataReads, "a healthy volume keeps sampling while discovery and another volume are stalled");
        Check(volumes.BlockedReads == 1, "only one outstanding request per stalled volume");
        // A stalled volume shows Unknown, not old numbers as live.
        now += TimeSpan.FromSeconds(5);
        Settle(monitor.TickAsync());
        var c = Volume("C:");
        Check(c.Health == Health.Unknown && c.StatusText == "State unavailable" && monitor.HealthTitle == "Some state is unavailable",
            "a volume without a fresh answer shows State unavailable");
        volumes.PendingSystem.SetResult(volumes.NoCache);
        volumes.PendingSystem = null;
        volumes.PendingInventory.SetResult(volumes.Volumes);
        volumes.PendingInventory = null;
        Settle(blocked);
        Settle(discovery);
        Check(c.Health == Health.Idle, "the stalled volume recovers on its next answer");

        // A cache left on an unformatted volume can be flushed and removed, not reconfigured.
        volumes.RawVolumeHasCache = true;
        Settle(monitor.SampleAsync());
        var r = Volume("R:");
        Check(!r.CanEdit && !r.CanPause && r.CanFlush && r.CanRemove && r.Description.Contains("no file system"),
            "a cached unformatted volume can be flushed and removed, and says why settings are unavailable");
        volumes.RawVolumeHasCache = false;

        // A failing cache shows an error with Retry, and comes first in the overview.
        volumes.FaultStick = true;
        Settle(monitor.SampleAsync());
        var s = Volume("S:");
        Check(s.Health == Health.Error && s.Problem?.Contains("0x80070015") == true && s.CanRetry && monitor.OverallHealth == Health.Error &&
              monitor.HealthTitle == "S: needs your attention", "a failing cache is an error that says what to do");
        Settle(s.RetryCommand.ExecuteAsync(null));
        Check(volumes.Retries == 1, "Retry calls the cache service");
        volumes.FaultStick = false;
        Settle(monitor.SampleAsync());

        // Editing: the editor gets the RAM that is really available, and the result is saved.
        dialogs.EditCache = editor => { editor.IsStrict = true; return editor.Result(); };
        Settle(q.EditCacheCommand.ExecuteAsync(null));
        Check(dialogs.LastCacheEditor?.AvailableMiB == 10240 && volumes.Saves == 1 && volumes.Saved?.Preset == QueueCache.Operations.CachePreset.Strict &&
              q.NoticeText == "Cache settings saved.", "the editor offers free RAM plus this cache's own, and saves the choice");

        // Eject asks first, then ejects the whole disk.
        Settle(monitor.DiskGroups[1].EjectCommand.ExecuteAsync(null));
        Check(volumes.Ejects == 0 && dialogs.Asked[^1].Title.StartsWith("Eject disk 1"), "eject asks before removing the disk");
        dialogs.Answers.Enqueue("eject");
        Settle(monitor.DiskGroups[1].EjectCommand.ExecuteAsync(null));
        Check(volumes.Ejects == 1 && monitor.Message?.Contains("You can unplug it") == true, "a confirmed eject removes the disk and says it can be unplugged");

        // Re-enumeration can keep a volume GUID but change its disk number; a late read must not reach the replacement.
        var retired = new TaskCompletionSource<QueueCache.Management.WriteCacheState>();
        volumes.PendingSystem = retired;
        var retiredSample = monitor.SampleVolumeAsync(Volume("C:"));
        volumes.PendingSystem = null;
        volumes.Volumes[0] = volumes.Volumes[0] with { DiskNumber = 7 };
        Settle(monitor.RefreshAsync());
        Check(monitor.DiskGroups.Any(g => g.Header.StartsWith("Disk 7 ")), "a changed disk number rebuilds the list even with the same GUID");
        retired.SetResult(volumes.State);
        Settle(retiredSample);
        Check(!Volume("C:").HasCache, "a late sample from a replaced volume does not publish onto its replacement");

        // A virtual disk's volume belongs to the virtual disk, not the ordinary cache list.
        var disks = DiskFixture.Sample();
        var managed = new VolumeFixture();
        managed.Volumes = [.. managed.Volumes, new("I:", "Builds", "NTFS", 8L << 30, disks.Records[0].VolumePath!, 8, "RAM", "managed-ram", 8L << 30, false, false, false)];
        var withDisks = new DashboardMonitor(managed, disks, dialogs, availableRam: () => 8UL << 30, usedLetters: () => new HashSet<char>());
        Settle(withDisks.RefreshAsync());
        Check(withDisks.VirtualDisks.Count == 3 && !withDisks.Volumes.Any(v => v.Volume.Volume == "I:"), "a virtual disk's volume gets no duplicate cache entry");

        // Shell: navigation, add cache from the overview, and the update interval setting.
        var store = new MemorySettingsStore();
        var shell = new ShellViewModel(new VolumeFixture(), DiskFixture.Sample(), dialogs, store, () => 8UL << 30, () => new HashSet<char>());
        Settle(shell.Monitor.RefreshAsync());
        var items = shell.Overview.Items;
        Check(items.Count == 7 && items[0] is VirtualDiskViewModel { HasUnsavedChanges: true } && items[^1] is VolumeViewModel { HasCache: false },
            "the overview lists what needs attention first and volumes without a cache last");
        var stick = shell.Monitor.Volumes.Single(v => v.Volume.Volume == "S:");
        dialogs.EditCache = _ => null;
        shell.Overview.AddCacheCommand.Execute(stick);
        Check(shell.Page == AppPage.Caches && shell.Caches.Selected == stick && shell.CurrentPage == shell.Caches, "Add cache in the overview opens that volume on the Caches page");
        Check(shell.Caches.Rows.Count == 6 && shell.Caches.Rows[0] is DiskGroupViewModel && shell.Caches.Rows[2] is DiskGroupViewModel,
            "the Caches list shows each disk followed by its volumes");
        shell.Settings.UpdateChoice = Array.IndexOf(DesktopSettings.UpdateChoices, 5d);
        Check(shell.Monitor.Interval == TimeSpan.FromSeconds(5) && store.Load().UpdateSeconds == 5, "the update interval setting applies at once and is saved");
        shell.Settings.UpdateChoice = Array.IndexOf(DesktopSettings.UpdateChoices, 0.1);
        Check(shell.Monitor.Interval == TimeSpan.FromMilliseconds(100) && shell.Monitor.MapInterval == TimeSpan.FromMilliseconds(100) &&
              store.Load().UpdateSeconds == 0.1 && shell.Settings.UpdateChoices[shell.Settings.UpdateChoice] == "Every 0.1 seconds",
            "the 0.1-second preference updates live values and visible maps and is saved with its own label");
        shell.Settings.Theme = (int)AppTheme.Dark;
        Check(store.Load().Theme == AppTheme.Dark, "the theme setting is saved");
        shell.Stop();
        Console.WriteLine("Cache contracts passed.");
    }

    private static void MapVisibility()
    {
        var volumes = new VolumeFixture();
        var now = DateTimeOffset.UtcNow;
        volumes.Map = DemoMap(4) with { Generation = volumes.State.Generation };
        var shell = new ShellViewModel(volumes, new DiskFixture(), new FakeDialogs(), new MemorySettingsStore(),
            () => 8UL << 30, () => new HashSet<char>(), clock: () => now);
        var window = new QueueCache.Desktop.Views.MainWindow { DataContext = shell };
        window.Show();
        Settle(shell.Monitor.RefreshAsync());
        Check(volumes.MapReads == 0, "Overview does not request a hidden cache map");
        var q = shell.Monitor.Volumes.Single(v => v.Volume.Volume == "Q:");
        shell.Caches.Select(q);
        shell.Page = AppPage.Caches;
        Settle(shell.Monitor.SampleVolumeAsync(q));
        Check(volumes.MapReads == 1 && q.HasLayoutMap, "a visible Caches page requests its selected map");
        shell.Page = AppPage.Settings;
        now += TimeSpan.FromSeconds(3);
        Settle(shell.Monitor.SampleVolumeAsync(q));
        Check(volumes.MapReads == 1 && !q.HasLayoutMap, "another page stops map polling and clears the hidden map");
        shell.Page = AppPage.Caches;
        window.Hide();
        now += TimeSpan.FromSeconds(3);
        Settle(shell.Monitor.SampleVolumeAsync(q));
        Check(volumes.MapReads == 1, "a window hidden in the notification area does not poll cache maps");
        window.Show();
        Settle(shell.Monitor.SampleVolumeAsync(q));
        Check(volumes.MapReads == 2 && q.HasLayoutMap, "showing the window resumes with a fresh map");
        var popout = QueueCache.Desktop.Views.CacheMapWindow.Open(window, q);
        Check(ReferenceEquals(popout, QueueCache.Desktop.Views.CacheMapWindow.Open(window, q)), "reopening a cache map activates the existing window");
        shell.Page = AppPage.Settings;
        now += TimeSpan.FromSeconds(3);
        Settle(shell.Monitor.SampleVolumeAsync(q));
        Check(volumes.MapReads == 3 && q.HasLayoutMap && q.MapRequested, "a visible pop-out keeps its map live on another page");
        popout.WindowState = Avalonia.Controls.WindowState.Minimized;
        now += TimeSpan.FromSeconds(3);
        Settle(shell.Monitor.SampleVolumeAsync(q));
        Check(volumes.MapReads == 3 && !q.MapRequested && !q.HasLayoutMap, "minimizing the only visible map stops requests and clears its snapshot");
        popout.WindowState = Avalonia.Controls.WindowState.Maximized;
        shell.Settings.UpdateChoice = Array.IndexOf(DesktopSettings.UpdateChoices, 0.1);
        Settle(shell.Monitor.SampleVolumeAsync(q));
        now += TimeSpan.FromMilliseconds(100);
        volumes.PendingMap = new();
        var fastSample = shell.Monitor.SampleVolumeAsync(q);
        Settle(shell.Monitor.SampleVolumeAsync(q));
        Check(volumes.MapReads == 5 && !fastSample.IsCompleted, "100 ms sampling keeps one outstanding map request per volume");
        popout.Close();
        volumes.PendingMap.SetResult(volumes.Map);
        Settle(fastSample);
        volumes.PendingMap = null;
        Check(!q.MapRequested && !q.HasLayoutMap, "a map arriving after its pop-out closes cannot republish a hidden snapshot");
        popout = QueueCache.Desktop.Views.CacheMapWindow.Open(window, q);
        volumes.Volumes = [.. volumes.Volumes, volumes.Volumes[0] with { Volume = "Z:", VolumePath = @"\\?\Volume{00000000-0000-0000-0000-000000000099}\", Instance = "unrelated-disk" }];
        Settle(shell.Monitor.RefreshAsync());
        Check(popout.IsVisible && ReferenceEquals(q, shell.Monitor.Volumes.Single(v => v.Volume.Volume == "Q:")),
            "an unrelated volume arriving preserves the open cache map and its sample owner");
        volumes.Volumes = volumes.Volumes.Where(v => v.Volume != "Q:").ToArray();
        Settle(shell.Monitor.RefreshAsync());
        Check(!popout.IsVisible && !q.MapRequested, "replacing or removing the volume closes its pop-out and releases map polling");
        window.Close();
    }
}
