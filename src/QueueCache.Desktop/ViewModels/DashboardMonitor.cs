using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using QueueCache.Desktop.Formatting;
using QueueCache.Desktop.Services;
using QueueCache.Management;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Desktop.ViewModels;

/// <summary>
/// Live state of every volume cache and virtual disk, and the actions on them. Discovery and
/// sampling are independent: a stalled volume or a slow inventory never holds up the others,
/// each volume has at most one outstanding driver request, and a sample that arrives for a
/// volume that has since been replaced is dropped.
/// </summary>
public sealed partial class DashboardMonitor : ObservableObject
{
    /// <summary>Volume discovery also runs on its own at this interval (device changes are not observed).</summary>
    internal static readonly TimeSpan InventoryFallback = TimeSpan.FromMinutes(2);
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<ulong> availableRam;
    private readonly Func<string, VolumeSpace?> volumeSpace;
    /// <summary>Volumes of disk images with a RAM cache: sampled here, shown on the virtual disk.</summary>
    private readonly List<VolumeViewModel> diskVolumes = [];
    private bool discovering, samplingDisks, closed;

    public DashboardMonitor(ICacheTaskService caches, IManagedDiskService disks, IDialogService dialogs,
        Func<DateTimeOffset>? clock = null, Func<ulong>? availableRam = null, Func<IReadOnlySet<char>>? usedLetters = null,
        Func<string, VolumeSpace?>? volumeSpace = null)
    {
        this.volumeSpace = volumeSpace ?? (path => OperatingSystem.IsWindows() ? VolumeSpaceReader.Read(path) : null);
        Caches = caches;
        Disks = disks;
        Dialogs = dialogs;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.availableRam = availableRam ?? (() => OperatingSystem.IsWindows() ? MemoryBudget.AvailableForCache() : 0);
        UsedLetters = usedLetters ?? (() => DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet());
    }

    public ICacheTaskService Caches { get; }
    public IManagedDiskService Disks { get; }
    internal IDialogService Dialogs { get; }
    internal Func<IReadOnlySet<char>> UsedLetters { get; }
    public ObservableCollection<DiskGroupViewModel> DiskGroups { get; } = [];
    public ObservableCollection<VolumeViewModel> Volumes { get; } = [];
    public ObservableCollection<SavedVolumeViewModel> Disconnected { get; } = [];
    public ObservableCollection<VirtualDiskViewModel> VirtualDisks { get; } = [];

    /// <summary>Live values update at this interval; values older than three intervals are shown as unavailable.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);
    internal TimeSpan StaleAfter => TimeSpan.FromSeconds(Math.Max(3, Interval.TotalSeconds * 3));
    internal DateTimeOffset NextInventory { get; set; } = DateTimeOffset.MinValue;
    internal bool GlobalBusy { get; private set; }

    /// <summary>Settings → Advanced: detailed driver timing for every cache and RAM disk. The drivers
    /// start with it off; the monitor turns it on (or off) wherever it differs.</summary>
    [ObservableProperty] private bool driverTiming;
    /// <summary>Settings → Advanced: serve cache hits on the calling thread (driver default on).</summary>
    [ObservableProperty] private bool callerPath = true;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMessage))] private string? message;
    [ObservableProperty] private NoticeSeverity messageSeverity;
    [ObservableProperty] private string? volumesStatus = "Looking for volumes…";
    [ObservableProperty] private string? virtualDisksStatus = "Looking for virtual disks…";
    [ObservableProperty] private Health overallHealth = Health.Unknown;
    [ObservableProperty] private string healthTitle = "Checking caches and disks…";
    [ObservableProperty] private string healthDetail = "";
    [ObservableProperty] private string healthWord = "Checking";
    [ObservableProperty] private string? activitySummary;
    [ObservableProperty] private double ramBarTotal;
    [ObservableProperty] private ulong cachesReservedBytes;
    [ObservableProperty] private ulong disksReservedBytes;
    [ObservableProperty] private ulong freeBytes;
    [ObservableProperty] private string ramSummary = "";
    [ObservableProperty] private string cachesReservedText = "";
    [ObservableProperty] private string disksReservedText = "";
    [ObservableProperty] private string freeText = "";
    [ObservableProperty] private bool hasRamLimit;

    /// <summary>Whether a page-level message is shown; closing it clears it.</summary>
    public bool HasMessage
    {
        get => Message is not null;
        set { if (!value) Message = null; }
    }

    /// <summary>Raised after the set of volumes or virtual disks was rebuilt.</summary>
    public event EventHandler? Rebuilt;
    /// <summary>Raised after new values were applied (Overview refreshes its list).</summary>
    public event EventHandler? Updated;

    public void Close() => closed = true;

    // ---- Timer ----

    /// <summary>One update interval: mark old values as unavailable, start discovery when due, sample everything.</summary>
    public async Task TickAsync()
    {
        var now = clock();
        foreach (var volume in Volumes.Concat(diskVolumes).Where(v => v.State is not null && !v.IsStale && now - v.Sampled > StaleAfter))
            volume.MarkStale();
        UpdateSummary();
        if (closed)
            return;
        // Discovery never holds up telemetry; each volume has at most one outstanding sample.
        if (now >= NextInventory && !GlobalBusy)
            _ = RefreshAsync();
        await SampleVirtualDisksAsync();
        await SampleAsync();
    }

    /// <summary>Finds volumes and saved settings. Manual refresh is immediate; the fallback rescan is independent of the update interval.</summary>
    public async Task RefreshAsync()
    {
        if (discovering || Volumes.Any(v => v.IsBusy))
            return;
        discovering = true;
        NextInventory = clock() + InventoryFallback;
        try
        {
            await SampleVirtualDisksAsync();
            var listed = await Caches.ListAsync();
            // A virtual disk's volume belongs to the virtual disk, not to the ordinary cache list.
            bool Managed(VolumeDescription v) => VirtualDisks.Any(d => d.Record.VolumePath?.Equals(v.VolumePath, StringComparison.OrdinalIgnoreCase) == true);
            var volumes = listed.Where(v => !Managed(v)).ToArray();
            var cached = listed.Where(v => Managed(v) && VirtualDisks.Any(d => d.IsCachedImage &&
                d.Record.VolumePath!.Equals(v.VolumePath, StringComparison.OrdinalIgnoreCase))).ToArray();
            var saved = await Caches.ListSavedAsync();
            if (closed)
                return;
            if (!Volumes.Any(v => v.IsBusy) && Signature(volumes) != Signature(Volumes.Select(v => v.Volume)))
                Rebuild(volumes);
            if (Signature(cached) != Signature(diskVolumes.Select(v => v.Volume)))
            {
                diskVolumes.Clear();
                diskVolumes.AddRange(cached.Select(v => new VolumeViewModel(this, v)));
            }
            AttachDiskCaches();
            var present = volumes.Select(v => v.VolumeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Disconnected.Clear();
            foreach (var profile in saved.Where(p => !present.Contains(p.VolumeId)))
                Disconnected.Add(new(profile.Volume, profile.VolumeId));
            await SampleAsync();
            VolumesStatus = volumes.Length == 0 ? "No volumes found." : null;
        }
        catch (Exception ex)
        {
            if (!closed)
                VolumesStatus = "Volume discovery failed: " + ex.Message;
        }
        finally
        {
            discovering = false;
        }
    }

    /// <summary>Reads every volume's cache state at once; each volume at most once at a time.</summary>
    public Task SampleAsync() => Task.WhenAll(Volumes.Concat(diskVolumes).ToArray().Select(SampleVolumeAsync));

    /// <summary>Still shown somewhere: a late sample for a replaced volume is dropped.</summary>
    private bool Owns(VolumeViewModel volume) => Volumes.Contains(volume) || diskVolumes.Contains(volume);

    private void AttachDiskCaches()
    {
        foreach (var disk in VirtualDisks)
            disk.Cache = disk.IsCachedImage && disk.Record.VolumePath is { } path
                ? diskVolumes.FirstOrDefault(v => v.Volume.VolumePath.Equals(path, StringComparison.OrdinalIgnoreCase))
                : null;
    }

    internal async Task SampleVolumeAsync(VolumeViewModel volume)
    {
        if (volume.Sampling || closed)
            return;
        volume.Sampling = true;
        try
        {
            var state = await Caches.ReadAsync(volume.Volume);
            if (closed || !Owns(volume))
                return; // A replaced volume: its late sample must not reach the new one.
            var now = clock();
            var rates = volume.State is null ? null : CacheTelemetry.Between(volume.State, state, now > volume.Sampled ? now - volume.Sampled : TimeSpan.FromTicks(1));
            volume.Apply(state, rates, now);
            if (volume.MapRequested && now - volume.MapSampled >= TimeSpan.FromSeconds(2))
            {
                volume.MapSampled = now;
                try { volume.LayoutMap = await Caches.ReadLayoutMapAsync(volume.Volume); }
                catch (Exception) { volume.LayoutMap = null; } // The map is optional; never fail the sample.
            }
            VirtualDisks.FirstOrDefault(d => d.Cache == volume)?.CacheSampled();
            _ = ApplyDeveloperSettingsAsync(volume, state);
        }
        catch (Exception ex)
        {
            if (!closed && Owns(volume))
                volume.MarkUnavailable(ex);
        }
        finally
        {
            volume.Sampling = false;
        }
        UpdateSummary();
    }

    public async Task SampleVirtualDisksAsync()
    {
        if (samplingDisks || closed)
            return;
        samplingDisks = true;
        try
        {
            var records = await Disks.ListAsync();
            if (closed)
                return;
            var spaces = await Task.Run(() => records.ToDictionary(r => r.ResourceId,
                r => r.VolumePath is { } path && r.Runtime?.State == ManagedDiskState.Ready ? volumeSpace(path) : null));
            if (closed)
                return;
            var oldVolumes = VirtualDisks.Select(d => d.Record.VolumePath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newVolumes = records.Select(r => r.VolumePath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!oldVolumes.SetEquals(newVolumes))
                NextInventory = DateTimeOffset.MinValue; // A disk's volume came or went: rediscover volumes now.
            var now = clock();
            var ids = records.Select(r => r.ResourceId).ToHashSet();
            var changed = false;
            foreach (var gone in VirtualDisks.Where(d => !ids.Contains(d.ResourceId)).ToArray())
            {
                VirtualDisks.Remove(gone);
                changed = true;
            }
            foreach (var record in records.OrderBy(r => r.Definition.PreferredLetter))
            {
                var existing = VirtualDisks.FirstOrDefault(d => d.ResourceId == record.ResourceId);
                if (existing is null)
                {
                    VirtualDisks.Add(new(this, record, now, spaces[record.ResourceId]));
                    changed = true;
                }
                else
                    existing.Apply(record, now, spaces[record.ResourceId]);
            }
            AttachDiskCaches();
            foreach (var disk in VirtualDisks)
                _ = ApplyDiskTimingAsync(disk);
            VirtualDisksStatus = records.Count == 0 ? "No virtual disks yet. Create one to get a disk that lives in RAM." : null;
            if (changed)
                Rebuilt?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            VirtualDisksStatus = "Virtual disk state unavailable: " + ex.Message;
            foreach (var disk in VirtualDisks)
                disk.MarkUnavailable();
        }
        finally
        {
            samplingDisks = false;
        }
        UpdateSummary();
    }

    /// <summary>Brings one cache to the Advanced settings. A cache that refuses keeps its state
    /// until it is recreated (a new instance) or the setting changes; it is not asked again each update.</summary>
    private async Task ApplyDeveloperSettingsAsync(VolumeViewModel volume, WriteCacheState state)
    {
        if (state.BudgetBytes == 0 || volume.IsBusy || closed)
            return;
        // Caller path: the driver starts each cache with it on and does not report it.
        if (volume.CallerPathBelief is not { } belief || belief.Instance != state.Instance)
            volume.CallerPathBelief = belief = (state.Instance, true);
        if (belief.Enabled != CallerPath)
        {
            volume.CallerPathBelief = (state.Instance, CallerPath);
            try { await Caches.SetCallerPathAsync(volume.Volume, CallerPath); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
        var timing = state.Performance is { } performance ? performance.TimingEnabled != 0 : (bool?)null;
        if (timing is { } on && on != DriverTiming && volume.TimingAttempt != (state.Instance, DriverTiming))
        {
            volume.TimingAttempt = (state.Instance, DriverTiming);
            try { await Caches.SetTimingAsync(volume.Volume, DriverTiming); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
    }

    private async Task ApplyDiskTimingAsync(VirtualDiskViewModel disk)
    {
        var record = disk.Record;
        if (record.Native is not { } native || record.Statistics is null || record.Runtime is not { State: ManagedDiskState.Ready } runtime ||
            disk.IsBusy || GlobalBusy || closed)
            return;
        var on = (native.Flags & RamDiskFlags.Timing) != 0;
        var attempt = (native.BootEpoch, native.CreationGeneration, DriverTiming);
        if (on == DriverTiming || disk.TimingAttempt == attempt)
            return;
        disk.TimingAttempt = attempt;
        try { await Disks.ExecuteAsync(new(disk.ResourceId, ManagedDiskAction.SetTiming, ManagedDiskExpected.From(runtime), Timing: DriverTiming)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private void Rebuild(IReadOnlyList<VolumeDescription> volumes)
    {
        DiskGroups.Clear();
        Volumes.Clear();
        // Each volume is its own cache; volumes are grouped under the disk that holds them.
        foreach (var disk in volumes.GroupBy(v => (v.DiskNumber, v.Instance)).OrderBy(g => g.Key.DiskNumber))
        {
            var members = disk.Select(v => new VolumeViewModel(this, v)).ToArray();
            foreach (var member in members)
                Volumes.Add(member);
            DiskGroups.Add(new(this, members));
        }
        Rebuilt?.Invoke(this, EventArgs.Empty);
    }

    // Identity, not size or label: a changed GUID, letter or disk rebuilds the list.
    private static string Signature(IEnumerable<VolumeDescription> volumes) =>
        string.Join("|", volumes.Select(v => $"{v.Volume}/{v.VolumePath}/{v.Instance}/{v.DiskNumber}/{v.Bytes}/{v.DiskBytes}/{v.FileSystem}"));

    private void UpdateSummary()
    {
        var fresh = Volumes.Where(v => v.State is not null && !v.IsStale).ToArray();
        CachesReservedBytes = fresh.Aggregate(0UL, (total, v) => total + v.State!.ReservedBytes);
        DisksReservedBytes = VirtualDisks.Aggregate(0UL, (total, d) => total + d.ReservedBytes);
        var limit = fresh.Select(v => v.State!.GlobalLimitBytes).DefaultIfEmpty(0UL).Max();
        // The driver's shared reservation also covers virtual disks; never show less than it reports.
        var reserved = Math.Max(CachesReservedBytes + DisksReservedBytes, fresh.Select(v => v.State!.GlobalReservedBytes).DefaultIfEmpty(0UL).Max());
        HasRamLimit = limit > 0;
        FreeBytes = limit > reserved ? limit - reserved : 0;
        CachesReservedText = CachesReservedBytes == 0 ? "None" : Format.Bytes(CachesReservedBytes);
        DisksReservedText = DisksReservedBytes == 0 ? "None" : Format.Bytes(DisksReservedBytes);
        FreeText = Format.Bytes(FreeBytes);
        RamBarTotal = limit > 0 ? limit : reserved;
        RamSummary = limit > 0 ? $"{Format.Bytes(reserved)} of {Format.Bytes(limit)} allowed" : $"{Format.Bytes(reserved)} in use";

        var errors = Volumes.Where(v => v.Health == Health.Error).Select(v => v.Volume.Volume)
            .Concat(VirtualDisks.Where(d => d.Health == Health.Error).Select(d => d.Title)).ToArray();
        var unknown = Volumes.Count(v => v.Health == Health.Unknown) + VirtualDisks.Count(d => d.Health == Health.Unknown);
        var unsaved = VirtualDisks.Count(d => d.HasUnsavedChanges);
        var inRamOnly = Volumes.Count(v => v.IsVolatile && v.PendingBytes > 0);
        var details = new List<string>();
        if (unsaved > 0)
            details.Add($"{unsaved} {(unsaved == 1 ? "disk has" : "disks have")} unsaved changes");
        if (inRamOnly > 0)
            details.Add($"{inRamOnly} {(inRamOnly == 1 ? "volume holds" : "volumes hold")} writes only in RAM");
        if (errors.Length > 0)
        {
            OverallHealth = Health.Error;
            HealthTitle = errors.Length == 1 ? $"{errors[0]} needs your attention" : $"{errors.Length} items need your attention";
        }
        else if (unknown > 0)
        {
            OverallHealth = Health.Unknown;
            HealthTitle = Volumes.Count + VirtualDisks.Count == unknown ? "Checking caches and disks…" : "Some state is unavailable";
        }
        else
        {
            OverallHealth = unsaved > 0 ? Health.Attention : Health.Ok;
            HealthTitle = "Everything is running";
        }
        var caches = Volumes.Count(v => v.HasCache);
        if (details.Count == 0)
            details.Add($"{caches} {(caches == 1 ? "cache" : "caches")} · {VirtualDisks.Count} {(VirtualDisks.Count == 1 ? "virtual disk" : "virtual disks")}");
        HealthDetail = string.Join(" · ", details);
        // Live throughput of everything, for the notification-area tooltip.
        var latest = Volumes.Select(v => v.Activity.LastOrDefault()).Concat(VirtualDisks.Select(d => d.Activity.LastOrDefault())).ToArray();
        var reading = latest.Sum(s => s.Read);
        var writing = latest.Sum(s => s.Incoming);
        ActivitySummary = reading < 0.05 && writing < 0.05 ? null : $"Reading {Format.Rate(reading)} · writing {Format.Rate(writing)}";
        HealthWord = OverallHealth switch { Health.Ok => "Healthy", Health.Attention => "Attention", Health.Error => "Error", _ => "Unknown" };
        Updated?.Invoke(this, EventArgs.Empty);
    }

    // ---- Volume cache actions ----

    /// <summary>Runs one action on one volume. Its other actions are disabled meanwhile; other volumes stay usable.</summary>
    internal async Task RunAsync(VolumeViewModel volume, string operation, Func<Task<string?>> action)
    {
        if (volume.IsBusy)
            return;
        volume.SetBusy(true, operation);
        volume.Notify(null, NoticeSeverity.Informational);
        try
        {
            var done = await action();
            if (done is not null)
                volume.Notify(done, NoticeSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            volume.Notify("Cancelled. The cache keeps running.", NoticeSeverity.Informational);
        }
        catch (Exception ex)
        {
            volume.Notify(ex.Message, NoticeSeverity.Error);
        }
        finally
        {
            volume.SetBusy(false, "");
            if (!closed)
                await SampleVolumeAsync(volume);
        }
    }

    internal Task RunAsync(VolumeViewModel volume, string operation, Func<Task> action, string? done = null) =>
        RunAsync(volume, operation, async () => { await action(); return done; });

    internal Task EditCacheAsync(VolumeViewModel volume) => RunAsync(volume, volume.HasCache ? "Changing cache settings" : "Adding a cache", async () =>
    {
        var current = volume.State ?? throw new IOException("The driver did not answer for this volume. Wait a moment and try again.");
        var globalFree = current.GlobalLimitBytes - Math.Min(current.GlobalLimitBytes, current.GlobalReservedBytes);
        var available = (long)(Math.Min(globalFree, availableRam()) >> 20) + (long)(current.BudgetBytes >> 20);
        available = Math.Min(MemoryBudget.MaximumMiB, available);
        if (available < 1)
            throw new IOException("No RAM is free for a cache right now. Make another cache smaller or free some memory first.");
        var editor = new CacheSettingsViewModel(volume.Volume, current, volume.Persistent, (int)available);
        var result = await Dialogs.EditCacheAsync(editor);
        if (result is null)
            return null;
        var progress = new Progress<string>(text => volume.OperationText = text);
        await Caches.SaveAsync(volume.Volume, result.Configuration, result.Persistent, progress);
        return current.BudgetBytes == 0 ? "Cache added." : "Cache settings saved.";
    });

    internal async Task EjectDiskAsync(DiskGroupViewModel group)
    {
        if (GlobalBusy)
            return;
        GlobalBusy = true;
        try
        {
            var preview = await Caches.PreviewEjectAsync(group.EjectVolume);
            if (!preview.Ejectable)
            {
                Notify(preview.UnsupportedReason ?? "Windows cannot eject this disk.", NoticeSeverity.Warning);
                return;
            }
            var choice = await Dialogs.ConfirmAsync(new($"Eject disk {preview.DiskNumber} ({preview.Name})?",
                $"Windows removes the whole disk, including {string.Join(", ", preview.Volumes)}. Writes that are not yet on the disk are written first. Windows refuses if files on it are open.",
                [new("eject", "Eject disk", ChoiceKind.Primary)]));
            if (choice is null)
                return;
            var progress = new Progress<string>(text => Notify(text, NoticeSeverity.Informational));
            var result = await Caches.EjectAsync(group.EjectVolume, progress, preview);
            Notify(result.RemovalObserved
                ? $"Windows removed disk {result.Disk.DiskNumber}. You can unplug it."
                : $"Windows accepted the eject of disk {result.Disk.DiskNumber}, but has not removed it yet. Check that it is gone before unplugging it; its caches stay off.",
                result.RemovalObserved ? NoticeSeverity.Success : NoticeSeverity.Warning);
            NextInventory = DateTimeOffset.MinValue;
        }
        catch (Exception ex)
        {
            Notify(ex.Message, NoticeSeverity.Error);
        }
        finally
        {
            GlobalBusy = false;
        }
    }

    internal void Notify(string? text, NoticeSeverity severity)
    {
        MessageSeverity = severity;
        Message = text;
    }

    // ---- Virtual disk actions ----

    /// <summary>Opens New disk; returns the created disk's resource ID.</summary>
    public async Task<Guid?> CreateDiskAsync()
    {
        if (GlobalBusy)
            return null;
        GlobalBusy = true;
        try
        {
            var editor = new CreateDiskViewModel(Disks, UsedLetters());
            var created = await Dialogs.CreateDiskAsync(editor);
            if (created is null)
                return null;
            await SampleVirtualDisksAsync();
            NextInventory = DateTimeOffset.MinValue;
            VirtualDisks.FirstOrDefault(d => d.ResourceId == created.ResourceId)?.Notify($"{created.Volume} is ready to use.", NoticeSeverity.Success);
            return created.ResourceId;
        }
        finally
        {
            GlobalBusy = false;
        }
    }

    internal Task RunDiskAsync(VirtualDiskViewModel disk, string operation, ManagedDiskAction action) =>
        RunDiskOperationAsync(disk, operation, (progress, token) =>
        {
            var expected = action == ManagedDiskAction.Recover || disk.Record.Runtime is null ? null : ManagedDiskExpected.From(disk.Record.Runtime);
            return Disks.ExecuteAsync(new(disk.ResourceId, action, expected), progress, token)!;
        });

    internal async Task RunDiskOperationAsync(VirtualDiskViewModel disk, string operation,
        Func<IProgress<ManagedDiskProgress>, CancellationToken, Task<ManagedDiskOperationResult?>> action)
    {
        if (disk.IsBusy)
            return;
        disk.SetBusy(true, operation);
        disk.Notify(null, NoticeSeverity.Informational);
        try
        {
            var progress = new DiskProgress(text => disk.OperationText = text);
            var result = await action(progress, CancellationToken.None);
            if (result is not null)
                disk.Notify(result.Message, NoticeSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            disk.Notify("Cancelled. Check the disk's state before trying again.", NoticeSeverity.Informational);
        }
        catch (Exception ex)
        {
            disk.Notify(ex.Message, NoticeSeverity.Error);
        }
        finally
        {
            disk.SetBusy(false, "");
            if (!closed)
            {
                await SampleVirtualDisksAsync();
                await RefreshAsync();
            }
        }
    }

    internal async Task StopDiskAsync(VirtualDiskViewModel disk)
    {
        var record = disk.Record;
        var runtime = record.Runtime;
        if (runtime is null)
            return;
        // The generation the user saw: a discard confirmed here never discards newer writes.
        var expected = ManagedDiskExpected.From(runtime);
        var letter = $"{record.Definition.PreferredLetter}:";
        ManagedDiskStopIntent intent;
        var acceptDiscard = false;
        switch (record.Definition.Mode)
        {
            case ManagedDiskMode.CachedVhdx:
                intent = ManagedDiskStopIntent.DrainThenDetach;
                break;
            case ManagedDiskMode.EphemeralRam:
                if (await Dialogs.ConfirmAsync(new($"Stop {disk.Title}?",
                    $"{letter} is a RAM disk. Stopping it permanently erases everything on it. Close programs that use {letter} first; Windows refuses to stop a disk with open files.",
                    [new("discard", "Erase and stop", ChoiceKind.Destructive)])) is null)
                    return;
                intent = ManagedDiskStopIntent.DiscardThenStop;
                acceptDiscard = true;
                break;
            default:
                if (record.Definition.ReadOnly)
                {
                    if (await Dialogs.ConfirmAsync(new($"Stop {disk.Title}?",
                        $"{letter} is read-only, so there is nothing to save. Windows refuses to stop a disk with open files.",
                        [new("stop", "Stop", ChoiceKind.Primary)])) is null)
                        return;
                    intent = ManagedDiskStopIntent.DiscardThenStop;
                    acceptDiscard = true;
                    break;
                }
                var image = record.CommittedImage?.Identity.Path ?? record.Definition.ImagePath;
                var choice = await Dialogs.ConfirmAsync(new($"Stop {disk.Title}?",
                    (runtime.HasUnsavedChanges
                        ? $"Changes since the last save exist only in RAM. Save them to a new checkpoint of {image} before stopping, or discard them permanently."
                        : "There are no unsaved changes.") + " Windows refuses to stop a disk with open files.",
                    [new("save", "Save and stop", ChoiceKind.Primary), new("discard", "Discard changes and stop", ChoiceKind.Destructive)]));
                if (choice is null)
                    return;
                intent = choice == "save" ? ManagedDiskStopIntent.SaveThenStop : ManagedDiskStopIntent.DiscardThenStop;
                acceptDiscard = choice == "discard";
                break;
        }
        await RunDiskOperationAsync(disk, intent == ManagedDiskStopIntent.SaveThenStop ? "Saving and stopping" : "Stopping", (progress, token) =>
            Disks.ExecuteAsync(new(disk.ResourceId, ManagedDiskAction.Stop, expected, intent, AcceptDiscard: acceptDiscard), progress, token)!);
    }

    internal async Task FormatDiskAsync(VirtualDiskViewModel disk)
    {
        var record = disk.Record;
        var expected = record.Runtime is null ? null : ManagedDiskExpected.From(record.Runtime);
        var message = record.Definition.Mode == ManagedDiskMode.ImageInRam
            ? $"Erase everything on {disk.Title} and format it as NTFS. This changes only the RAM copy: the imported image remains unchanged until you save."
            : $"Erase everything on {disk.Title} and format it as NTFS. Windows refuses if files on it are open.";
        if (await Dialogs.ConfirmAsync(new($"Format {disk.Title}?", message, [new("format", "Erase and format", ChoiceKind.Destructive)])) is null)
            return;
        await RunDiskOperationAsync(disk, "Formatting", (progress, token) =>
            Disks.ExecuteAsync(new(disk.ResourceId, ManagedDiskAction.Format, expected, AcceptErase: true), progress, token)!);
    }

    internal async Task ForgetDiskAsync(VirtualDiskViewModel disk)
    {
        var record = disk.Record;
        var expected = record.Runtime is null ? null : ManagedDiskExpected.From(record.Runtime);
        var message = record.Definition.Mode == ManagedDiskMode.EphemeralRam
            ? $"QueueCache removes {disk.Title} and its settings."
            : $"QueueCache stops tracking {disk.Title}. Its image files are kept, so you can open the image again later as a new disk.";
        if (await Dialogs.ConfirmAsync(new($"Forget {disk.Title}?", message, [new("forget", "Forget disk", ChoiceKind.Destructive)])) is null)
            return;
        await RunDiskOperationAsync(disk, "Forgetting", (progress, token) =>
            Disks.ExecuteAsync(new(disk.ResourceId, ManagedDiskAction.RemoveDefinition, expected), progress, token)!);
    }

    internal async Task DiskFormAsync(VirtualDiskViewModel disk, ManagedDiskAction action)
    {
        var editor = new DiskActionViewModel(Disks, disk.Record, action);
        disk.Notify(null, NoticeSeverity.Informational);
        var result = await Dialogs.DiskActionAsync(editor);
        if (result is not null)
            disk.Notify(result.Message, NoticeSeverity.Success);
        if (!closed)
        {
            await SampleVirtualDisksAsync();
            await RefreshAsync();
        }
    }

    internal async Task EditDiskCacheAsync(VirtualDiskViewModel disk)
    {
        var record = disk.Record;
        var definition = record.Definition;
        var expected = ManagedDiskExpected.From(record.Runtime!);
        var listed = await Caches.ListAsync();
        var descriptor = listed.SingleOrDefault(v => record.VolumePath?.Equals(v.VolumePath, StringComparison.OrdinalIgnoreCase) == true)
            ?? new VolumeDescription(definition.PreferredLetter + ":", definition.Label, "NTFS", checked((long)definition.CapacityBytes),
                record.VolumePath ?? @"\\?\Volume{00000000-0000-0000-0000-000000000000}\", record.PhysicalDiskNumber ?? 0, "Disk image", "remembered disk image",
                checked((long)definition.CapacityBytes), false, false, false);
        var editor = new CacheSettingsViewModel(descriptor, definition.Cache!, MemoryBudget.MaximumMiB);
        var result = await Dialogs.EditCacheAsync(editor);
        if (result is null)
            return;
        await RunDiskOperationAsync(disk, "Changing cache settings", (progress, token) =>
            Disks.ExecuteAsync(new(disk.ResourceId, ManagedDiskAction.ChangeCache, expected,
                Cache: result.Configuration, AcceptVolatileWrites: result.Configuration.Preset == CachePreset.Fast), progress, token)!);
    }

    private sealed class DiskProgress(Action<string> update) : IProgress<ManagedDiskProgress>
    {
        public void Report(ManagedDiskProgress value)
        {
            var text = value.TotalBytes is > 0 && value.CompletedBytes is { } done
                ? $"{value.Message} {Format.Percent(100.0 * done / value.TotalBytes.Value)}"
                : value.Message;
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                update(text);
            else
                Avalonia.Threading.Dispatcher.UIThread.Post(() => update(text));
        }
    }
}
