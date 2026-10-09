using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QueueCache.Desktop.Formatting;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Desktop.ViewModels;

public enum NoticeSeverity { Informational, Success, Warning, Error }

/// <summary>One sample of the activity chart, in MiB/s.</summary>
public readonly record struct RateSample(double Read, double Incoming, double Drained);

/// <summary>One volume and its cache (if any). Live values come from the driver through
/// <see cref="DashboardMonitor"/>; this class turns them into what the views show.</summary>
public sealed partial class VolumeViewModel : ObservableObject
{
    public const int HistoryLength = 60;
    private readonly DashboardMonitor monitor;
    private readonly List<RateSample> history = [];

    internal VolumeViewModel(DashboardMonitor monitor, VolumeDescription volume)
    {
        this.monitor = monitor;
        Volume = volume;
    }

    public VolumeDescription Volume { get; }
    /// <summary>The physical disk this volume is on; ejecting removes the whole disk.</summary>
    public DiskGroupViewModel? Group { get; internal set; }
    public string Letter => Volume.Volume.TrimEnd(':');
    public string Title => string.IsNullOrWhiteSpace(Volume.Label) ? Volume.Volume : $"{Volume.Volume} {Volume.Label}";
    public string Subtitle => Describe(Volume);

    // ---- Driver sample state (owned by DashboardMonitor) ----
    internal WriteCacheState? State { get; private set; }
    internal DateTimeOffset Sampled { get; private set; }
    internal bool Sampling { get; set; }
    internal bool IsStale { get; private set; }
    // What the monitor last asked of this cache instance (Settings → Advanced).
    internal (ulong Instance, bool Enabled)? CallerPathBelief { get; set; }
    internal (ulong Instance, bool Enabled)? TimingAttempt { get; set; }

    [ObservableProperty] private Health health = Health.Unknown;
    [ObservableProperty] private string statusText = "Connecting";
    [ObservableProperty] private string description = "";
    [ObservableProperty] private string? warning;
    [ObservableProperty] private string? problem;
    [ObservableProperty] private bool hasCache;
    [ObservableProperty] private bool isVolatile;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string operationText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNotice))] private string? noticeText;
    [ObservableProperty] private NoticeSeverity noticeSeverity;
    [ObservableProperty] private string supportDetails = "";

    // The details pane and pop-out windows share one map sample for this volume.
    private bool selectedMapRequested;
    private int mapWindows;
    internal bool MapRequested { get => selectedMapRequested || mapWindows > 0; set => selectedMapRequested = value; }
    internal DashboardMonitor Monitor => monitor;
    internal void AddMapWindow()
    {
        ++mapWindows;
        MapSampled = default;
    }
    internal void RemoveMapWindow()
    {
        --mapWindows;
        if (!MapRequested)
            LayoutMap = null;
    }
    internal DateTimeOffset MapSampled { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasLayoutMap), nameof(LayoutOrderText), nameof(LayoutFreeText))]
    private CacheLayoutMap? layoutMap;
    public bool HasLayoutMap => LayoutMap is { Chunks: > 0, IsComplete: true };
    public string LayoutOrderText => LayoutMap?.InOrder is { } order ? $"{order * 100:0}% in disk order" : "Nothing cached yet";
    public string LayoutFreeText => LayoutMap is { } map ? $"{map.FreeChunks:N0} of {map.TotalChunks:N0} free" : "";

    // Settings summary
    [ObservableProperty] private string budgetText = "";
    [ObservableProperty] private string presetText = "";
    [ObservableProperty] private string drainText = "";

    // What is in RAM
    [ObservableProperty] private ulong readBytes;
    [ObservableProperty] private ulong onDiskBytes;
    [ObservableProperty] private ulong pendingBytes;
    [ObservableProperty] private ulong capacityBytes;
    [ObservableProperty] private string readText = "";
    [ObservableProperty] private string onDiskText = "";
    [ObservableProperty] private string pendingText = "";
    [ObservableProperty] private string freeText = "";
    [ObservableProperty] private string usedText = "";

    // Figures
    [ObservableProperty] private string readHitText = "";
    [ObservableProperty] private string oldestUnwrittenText = "";
    [ObservableProperty] private string overwritesText = "";
    [ObservableProperty] private string errorsText = "";
    [ObservableProperty] private string readingText = "Idle";
    [ObservableProperty] private string incomingText = "Idle";
    [ObservableProperty] private string drainingText = "Idle";
    [ObservableProperty] private IReadOnlyList<RateSample> activity = [];
    [ObservableProperty] private double secondsPerSample = 1;
    [ObservableProperty] private string activityTitle = "Activity";
    [ObservableProperty] private string driverText = "";
    [ObservableProperty] private bool timingOn;
    [ObservableProperty] private string timingText = "";

    /// <summary>The one line the Overview shows for this volume, and what kind of item it is.</summary>
    [ObservableProperty] private string headline = "";
    [ObservableProperty] private string kindText = "";

    // Actions
    [ObservableProperty] private string editText = "Add cache";
    [ObservableProperty] private string pauseText = "Pause";
    [ObservableProperty] private bool canEdit;
    [ObservableProperty] private bool canPause;
    [ObservableProperty] private bool canFlush;
    [ObservableProperty] private bool canRemove;
    [ObservableProperty] private bool canClearReadCache;
    [ObservableProperty] private bool showClearReadCache;
    [ObservableProperty] private bool canRetry;
    [ObservableProperty] private bool showStatus = true;
    [ObservableProperty] private bool hasMoreActions;

    /// <summary>Whether the result of the last action is shown; closing it clears it.</summary>
    public bool HasNotice
    {
        get => NoticeText is not null;
        set { if (!value) NoticeText = null; }
    }

    internal bool Persistent => State?.BudgetBytes is null or 0 || monitor.Caches.IsPersistent(Volume);

    [RelayCommand] private Task EditCache() => monitor.EditCacheAsync(this);
    [RelayCommand] private Task TogglePause() => monitor.RunAsync(this, PauseText, () =>
        monitor.Caches.SetEnabledAsync(Volume, !(State?.Enabled ?? false), Persistent));
    [RelayCommand] private Task Flush() => monitor.RunAsync(this, "Flushing", () => monitor.Caches.FlushAsync(Volume), "All pending writes are on the disk.");
    [RelayCommand] private Task ClearReadCache() => monitor.RunAsync(this, "Clearing the read cache", () => monitor.Caches.DropCleanAsync(Volume),
        "Read cache cleared. Writes that are not yet on disk were kept.");
    [RelayCommand] private Task Remove() => monitor.RunAsync(this, "Writing pending data to disk and removing the cache", () => monitor.Caches.RemoveAsync(Volume),
        "Cache removed. Its RAM is free again.");
    [RelayCommand] private Task Retry() => monitor.RunAsync(this, "Retrying disk writes", () => monitor.Caches.RetryAsync(Volume));
    [RelayCommand] private void DismissNotice() => NoticeText = null;

    internal void SetBusy(bool busy, string operation)
    {
        IsBusy = busy;
        OperationText = busy ? operation + "…" : "";
        UpdateActions();
    }

    internal void Notify(string? text, NoticeSeverity severity)
    {
        NoticeSeverity = severity;
        NoticeText = text;
    }

    /// <summary>A fresh driver sample. <paramref name="rates"/> is null for the first sample.</summary>
    internal void Apply(WriteCacheState state, CacheTelemetry? rates, DateTimeOffset now)
    {
        if (State is null || State.Instance != state.Instance || State.Generation != state.Generation || state.PayloadCapacity == 0)
        {
            LayoutMap = null;
            MapSampled = default;
        }
        State = state;
        Sampled = now;
        IsStale = false;
        var exists = state.BudgetBytes > 0;
        HasCache = exists;
        IsVolatile = exists && state.UnsafeDefer;
        (Health, StatusText) = Classify(state);
        Problem = state.Faulted || state.LastError != 0
            ? $"Writes to {Volume.Volume} are failing (error 0x{state.LastError:X8}). The writes not yet on disk are kept in RAM. Check the disk, then retry."
            : null;
        Description = exists ? "" : Volume.HasFileSystem ? "Ready for a cache. Choose how much RAM it may use." : "This volume is not formatted. Format it to add a cache.";
        if (exists && !Volume.HasFileSystem)
            Description = "This volume has no file system: only Flush, Clear read cache and Remove cache are available.";
        Warning = Volume.HasFileSystem && !Volume.Journaled ? $"{Volume.FileSystem} {FileSystems.NoJournalWarning}" : null;

        var options = state.Options;
        BudgetText = exists ? $"{Format.Bytes(state.BudgetBytes)} RAM" : "";
        PresetText = exists ? state.UnsafeDefer ? "Fast" : "Strict" : "";
        DrainText = !exists ? "" : (options?.Drain ?? DrainAlgorithm.Eager) switch
        {
            DrainAlgorithm.Eager => "Writes to disk at once",
            DrainAlgorithm.Balanced => "Writes to disk in batches",
            DrainAlgorithm.Idle => "Writes to disk when idle",
            _ => "Writes to disk after a delay"
        };

        ReadBytes = state.CleanReadBytes;
        OnDiskBytes = state.CleanWriteBytes;
        PendingBytes = state.DirtyBytes;
        CapacityBytes = state.PayloadCapacity;
        ReadText = Format.Bytes(state.CleanReadBytes);
        OnDiskText = Format.Bytes(state.CleanWriteBytes);
        PendingText = Format.Bytes(state.DirtyBytes);
        FreeText = Format.Bytes(state.FreeBytes);
        UsedText = $"{Format.Bytes(state.CleanReadBytes + state.CleanWriteBytes + state.DirtyBytes)} of {Format.Bytes(state.PayloadCapacity)} used";

        ReadHitText = state.ReadHitBytes + state.ReadMissBytes == 0 ? "No reads yet" : Format.Percent(state.ReadHitPercent);
        OldestUnwrittenText = state.DirtyBytes == 0 ? "None" : Format.Duration(TimeSpan.FromMilliseconds(state.OldestDirtyMs));
        OverwritesText = state.ExtendedCountersAvailable ? Format.Bytes(state.CoalescedBytes) : "Unknown";
        ErrorsText = state.Errors.ToString("N0");
        KindText = exists ? $"Cache · {Format.Bytes(state.BudgetBytes)} · {PresetText}" : Volume.HasFileSystem ? "No cache" : "Not formatted";
        Headline = !exists ? "" :
            state.DirtyBytes == 0 ? "All writes on disk" : $"{Format.Bytes(state.DirtyBytes)} not yet on disk";

        if (rates is null || rates.CountersReset)
            history.Clear();
        history.Add(new(rates?.ReadMiBPerSecond ?? 0, rates?.AcceptedMiBPerSecond ?? 0, rates?.DrainedMiBPerSecond ?? 0));
        while (history.Count > HistoryLength)
            history.RemoveAt(0);
        Activity = history.ToArray();
        SecondsPerSample = monitor.Interval.TotalSeconds;
        ActivityTitle = $"Activity · last {Format.Duration(TimeSpan.FromSeconds(HistoryLength * SecondsPerSample))}";
        ReadingText = Format.Rate(rates?.ReadMiBPerSecond ?? 0);
        IncomingText = Format.Rate(rates?.AcceptedMiBPerSecond ?? 0);
        DrainingText = Format.Rate(rates?.DrainedMiBPerSecond ?? 0);

        var performance = state.Performance;
        TimingOn = exists && performance is { TimingEnabled: not 0 };
        TimingText = TimingOn ? Timing(performance!) : "";
        DriverText = performance is null ? "Driver timing is unavailable with this driver." :
            $"Phase {performance.PhaseName} · {performance.QueueDepth} queued · current request {performance.Milliseconds(performance.ActiveAgeTicks):0} ms · reads that bypassed RAM {performance.BypassReads:N0}";
        SupportDetails = $"Volume {Volume.VolumePath}\nDisk {Volume.DiskNumber} ({Volume.DiskName}) · {Volume.Instance}\nCache instance {state.Instance} · generation {state.Generation} · flags 0x{state.Flags:X}";
        UpdateActions();
    }

    /// <summary>No fresh answer within the stale window: say so instead of showing old numbers as live.</summary>
    internal void MarkStale()
    {
        IsStale = true;
        LayoutMap = null;
        MapSampled = default;
        Health = Health.Unknown;
        StatusText = "State unavailable";
        Description = "Waiting for a fresh answer from the driver. The last known state is not shown as live.";
        Headline = "State unavailable";
        UpdateActions();
    }

    internal void MarkUnavailable(Exception error)
    {
        State = null;
        LayoutMap = null;
        MapSampled = default;
        IsStale = false;
        HasCache = false;
        IsVolatile = false;
        Health = Health.Unknown;
        StatusText = "Not connected";
        Description = "This volume is unavailable or its QueueCache filter is not responding. It comes back by itself once the volume is reconnected.";
        Headline = "Not connected";
        KindText = "Volume";
        Problem = null;
        SupportDetails = error.Message;
        UpdateActions();
    }

    private void UpdateActions()
    {
        var state = State;
        var live = state is not null && !IsStale;
        var exists = live && state!.BudgetBytes > 0;
        EditText = exists ? "Cache settings" : "Add cache";
        PauseText = state?.Enabled == true ? "Pause" : "Resume";
        CanEdit = live && !IsBusy && state!.SupportsReadWrite && Volume.HasFileSystem;
        CanPause = exists && !IsBusy && Volume.HasFileSystem;
        CanFlush = exists && !IsBusy;
        CanRemove = exists && !IsBusy && state!.SupportsRelease;
        ShowClearReadCache = exists && state!.SupportsDropClean;
        CanClearReadCache = ShowClearReadCache && !IsBusy && state!.CleanReadBytes + state.CleanWriteBytes > 0;
        CanRetry = live && !IsBusy && (state!.Faulted || state.LastError != 0);
        // An idle volume without a cache needs no status word: "No cache" already says it.
        ShowStatus = HasCache || Health != Health.Idle;
        HasMoreActions = exists || Group?.CanEject == true;
    }

    /// <summary>The timed parts of the cache driver, since timing was turned on or the cache started.</summary>
    private static string Timing(CachePerformance p)
    {
        string Average(ulong ticks, ulong count) => count == 0 ? "none yet" : Format.Micro(1e6 * ticks / count / p.Frequency);
        string Max(ulong ticks) => Format.Micro(1e6 * ticks / p.Frequency);
        return $"Cache lock: wait {Average(p.LockWaitTicks, p.LockAcquires)} on average, at most {Max(p.MaxLockWaitTicks)} · " +
               $"held {Average(p.LockHoldTicks, p.LockAcquires)}, at most {Max(p.MaxLockHoldTicks)} · {p.LockAcquires:N0} times\n" +
               $"Waiting in the queue: {Average(p.QueueWaitTicks, p.QueuedRequests)} on average, at most {Max(p.MaxQueueWaitTicks)} · {p.QueuedRequests:N0} requests\n" +
               $"Waiting for free RAM: {p.CapacityWaits:N0} times, {Format.Micro(1e6 * p.CapacityWaitTicks / p.Frequency)} in total\n" +
               $"Writing to disk: {Average(p.LowerIoTicks, p.DrainBatches)} per batch · {p.DrainBatches:N0} batches, {Format.Bytes(p.DrainBytes)}";
    }

    private static (Health, string) Classify(WriteCacheState state) =>
        state.Removed ? (Health.Idle, "Removed") :
        state.Faulted || state.LastError != 0 ? (Health.Error, "Error") :
        state.Suspended ? (Health.Attention, "Suspended") :
        state.Draining ? (Health.Attention, "Writing to disk") :
        state.Operational ? (Health.Ok, state.SupportsReadWrite ? "Active" : "Active (legacy driver)") :
        state.Enabled ? (Health.Attention, "State mismatch") :
        state.BudgetBytes > 0 ? (Health.Idle, "Paused") : (Health.Idle, "No cache");

    internal static string Describe(VolumeDescription volume) =>
        $"{(string.IsNullOrEmpty(volume.FileSystem) ? "Unformatted" : volume.FileSystem)} · {Format.Bytes(volume.Bytes)} · disk {volume.DiskNumber} ({volume.DiskName})" +
        (volume.IsBoot || volume.IsSystem ? " · Windows" : "") + (volume.IsPaging ? " · paging file" : "");
}
