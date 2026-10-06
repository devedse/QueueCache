using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QueueCache.Desktop.Formatting;
using QueueCache.Desktop.Services;
using QueueCache.Management;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Desktop.ViewModels;

/// <summary>A RAM disk, an image in RAM or a disk image with a RAM cache, as the managed-disk
/// service reports it. Which actions apply follows the disk's state; the service rechecks.</summary>
public sealed partial class VirtualDiskViewModel : ObservableObject
{
    private readonly DashboardMonitor monitor;

    private readonly List<RateSample> history = [];
    private Counters? last;
    private DateTimeOffset lastAt;

    /// <summary>Everything read and written since the disk started, both access paths together.</summary>
    private readonly record struct Counters(Guid Boot, ulong Creation, ulong Read, ulong Written, ulong? Requests);

    internal VirtualDiskViewModel(DashboardMonitor monitor, ManagedDiskRecord record, DateTimeOffset now, VolumeSpace? space = null)
    {
        this.monitor = monitor;
        Apply(record, now, space);
    }

    public ManagedDiskRecord Record { get; private set; } = null!;
    public Guid ResourceId => Record.ResourceId;

    [ObservableProperty] private string title = "";
    [ObservableProperty] private string kind = "";
    [ObservableProperty] private string subtitle = "";
    [ObservableProperty] private Health health;
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private bool isVolatile;
    [ObservableProperty] private bool hasUnsavedChanges;
    [ObservableProperty] private string? saveStatus;
    [ObservableProperty] private string? problem;
    [ObservableProperty] private string headline = "";
    [ObservableProperty] private IReadOnlyList<Fact> facts = [];
    [ObservableProperty] private string supportDetails = "";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string operationText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNotice))] private string? noticeText;
    [ObservableProperty] private NoticeSeverity noticeSeverity;
    [ObservableProperty] private bool stateUnavailable;

    // Actions: one main action for the state, a few common ones, the rest in More.
    [ObservableProperty] private bool canStart;
    [ObservableProperty] private bool canStop;
    [ObservableProperty] private bool canFlush;
    [ObservableProperty] private bool canSave;
    [ObservableProperty] private bool canExport;
    [ObservableProperty] private bool canFormat;
    [ObservableProperty] private bool canEditCache;
    [ObservableProperty] private bool canEditStartup;
    [ObservableProperty] private bool canEditStopped;
    [ObservableProperty] private bool canForget;
    [ObservableProperty] private bool canDeleteImage;
    [ObservableProperty] private bool canRecover;
    [ObservableProperty] private bool showSave;
    [ObservableProperty] private bool showExport;
    [ObservableProperty] private bool showEditCache;
    [ObservableProperty] private bool showDeleteImage;
    [ObservableProperty] private string stopText = "Stop";
    [ObservableProperty] private string letter = "";
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private bool isStoppedState;
    [ObservableProperty] private bool isReadyState;
    [ObservableProperty] private bool needsRecovery;

    // Live figures of a running RAM disk or image in RAM.
    [ObservableProperty] private bool hasActivity;
    [ObservableProperty] private IReadOnlyList<RateSample> activity = [];
    [ObservableProperty] private double secondsPerSample = 1;
    [ObservableProperty] private string activityTitle = "Activity";
    [ObservableProperty] private string readingText = "Idle";
    [ObservableProperty] private string writingText = "Idle";
    [ObservableProperty] private string requestsText = "";
    [ObservableProperty] private string readTotalText = "";
    [ObservableProperty] private string writtenTotalText = "";
    [ObservableProperty] private string directShareText = "";
    [ObservableProperty] private string errorsText = "";
    [ObservableProperty] private bool hasSpace;
    [ObservableProperty] private ulong usedBytes;
    [ObservableProperty] private ulong spaceBytes;
    [ObservableProperty] private string spaceText = "";
    [ObservableProperty] private string usedText = "";
    [ObservableProperty] private string freeText = "";
    [ObservableProperty] private bool hasSaveFigures;
    [ObservableProperty] private string writtenSinceSaveText = "";
    [ObservableProperty] private string lastSaveText = "";
    [ObservableProperty] private bool timingOn;
    [ObservableProperty] private string timingText = "";
    /// <summary>The cache of a disk image with a RAM cache (its volume), sampled like any cache.</summary>
    [ObservableProperty] private VolumeViewModel? cache;

    /// <summary>Whether the result of the last action is shown; closing it clears it.</summary>
    public bool HasNotice
    {
        get => NoticeText is not null;
        set { if (!value) NoticeText = null; }
    }

    public bool IsImage => Record.Definition.Mode == ManagedDiskMode.ImageInRam;
    public bool IsRamDisk => Record.Definition.Mode == ManagedDiskMode.EphemeralRam;
    public bool IsCachedImage => Record.Definition.Mode == ManagedDiskMode.CachedVhdx;
    internal ulong ReservedBytes => Record.Native?.ReservedBytes ?? Cache?.State?.ReservedBytes ?? 0;
    internal (Guid Boot, ulong Creation, bool Enabled)? TimingAttempt { get; set; }

    [RelayCommand] private Task Start() => monitor.RunDiskAsync(this, "Starting", ManagedDiskAction.Start);
    [RelayCommand] private Task Flush() => monitor.RunDiskAsync(this, "Flushing", ManagedDiskAction.Flush);
    [RelayCommand] private Task Save() => monitor.RunDiskAsync(this, "Saving", ManagedDiskAction.Save);
    [RelayCommand] private Task Recover() => monitor.RunDiskAsync(this, "Recovering", ManagedDiskAction.Recover);
    [RelayCommand] private Task Stop() => monitor.StopDiskAsync(this);
    [RelayCommand] private Task Format() => monitor.FormatDiskAsync(this);
    [RelayCommand] private Task Forget() => monitor.ForgetDiskAsync(this);
    [RelayCommand] private Task EditCache() => monitor.EditDiskCacheAsync(this);
    [RelayCommand] private Task Export() => monitor.DiskFormAsync(this, ManagedDiskAction.Export);
    [RelayCommand] private Task EditStartup() => monitor.DiskFormAsync(this, ManagedDiskAction.SetStartup);
    [RelayCommand] private Task EditStopped() => monitor.DiskFormAsync(this, ManagedDiskAction.ConfigureStopped);
    [RelayCommand] private Task DeleteImage() => monitor.DiskFormAsync(this, ManagedDiskAction.DeleteImage);
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

    internal void MarkUnavailable()
    {
        StateUnavailable = true;
        Health = Health.Unknown;
        StatusText = "State unavailable";
        Headline = "State unavailable";
        UpdateActions();
    }

    internal void Apply(ManagedDiskRecord record, DateTimeOffset now, VolumeSpace? space = null)
    {
        Record = record;
        StateUnavailable = false;
        var definition = record.Definition;
        var runtime = record.Runtime;
        var ready = runtime?.State == ManagedDiskState.Ready;
        Title = $"{definition.PreferredLetter}: {definition.Label}";
        Letter = definition.PreferredLetter.ToString();
        Kind = definition.Mode switch
        {
            ManagedDiskMode.EphemeralRam => "RAM disk",
            ManagedDiskMode.CachedVhdx => "Disk image with RAM cache",
            _ => "Image in RAM"
        };
        Subtitle = $"{Kind} · {Formatting.Format.Bytes(definition.CapacityBytes)}" +
            (definition.ImagePath is null ? "" : definition.Mode == ManagedDiskMode.ImageInRam ? $" · loaded from {definition.ImagePath}" : $" · {definition.ImagePath}");
        IsVolatile = definition.Mode != ManagedDiskMode.CachedVhdx || definition.Cache?.Preset == Operations.CachePreset.Fast;
        HasUnsavedChanges = definition.Mode == ManagedDiskMode.ImageInRam && ready && runtime!.HasUnsavedChanges;
        (Health, StatusText) = runtime is null ? (Health.Error, "Needs recovery") : runtime.State switch
        {
            ManagedDiskState.Ready => (Health.Ok, "Running"),
            ManagedDiskState.Stopped => (Health.Idle, "Stopped"),
            ManagedDiskState.Blocked => (Health.Error, "Blocked"),
            ManagedDiskState.Faulted => (Health.Error, "Error"),
            ManagedDiskState.RecoveryRequired => (Health.Error, "Needs recovery"),
            var transitional => (Health.Idle, transitional.ToString())
        };
        Problem = runtime is null ? "QueueCache lost track of this disk's live identity. Recover it to continue." :
            record.LastError ?? (runtime.State == ManagedDiskState.RecoveryRequired ? "This disk needs to be reconciled after an interrupted operation. Recover it to continue." : null);
        var saved = record.SavedAt is { } at ? $"Saved {Formatting.Format.When(at, now)}" : "Not saved yet";
        SaveStatus = definition.Mode != ManagedDiskMode.ImageInRam ? null :
            HasUnsavedChanges ? $"{saved} · changes since then are only in RAM" : ready ? $"{saved} · no unsaved changes" : null;
        Headline = runtime is null ? "Needs recovery" :
            HasUnsavedChanges ? $"{saved} · unsaved changes" :
            definition.Mode switch
            {
                ManagedDiskMode.EphemeralRam => ready ? "Erased when stopped" : "Starts empty",
                ManagedDiskMode.ImageInRam => saved,
                _ => definition.ImagePath ?? ""
            };

        var list = new List<Fact>
        {
            new("At Windows startup", definition.StartAtBoot ? definition.StartupDescription : "Does not start automatically"),
            new("When stopped", definition.Mode switch
            {
                ManagedDiskMode.EphemeralRam => "Everything on it is erased",
                ManagedDiskMode.CachedVhdx => "Pending data is written to the image file, then it is detached",
                _ => definition.ReadOnly ? "Read-only: nothing is saved" : definition.SaveBeforeStopping ? "Saves first, then stops" : "Asks whether to save or discard changes"
            })
        };
        if (definition.Mode == ManagedDiskMode.ImageInRam)
            list.Add(new("Last saved", record.SavedAt is { } savedAt ? Formatting.Format.When(savedAt, now) : "Not saved yet (started from the imported image)"));
        if (definition.Cache is { } cache)
            list.Add(new("Cache", $"{Formatting.Format.Bytes((ulong)cache.BudgetMiB << 20)} · {cache.Preset}"));
        if (record.Native is { } native)
            list.Add(new("RAM reserved", Formatting.Format.Bytes(native.ReservedBytes)));
        if (definition.Mode != ManagedDiskMode.CachedVhdx)
            list.Add(new("Access path", definition.Access == RamAccess.Standard ? "Standard (full Windows disk stack)" :
                record.Direct?.Describe() ?? (ready ? "Direct requested; state unavailable" : "Direct when running")));
        if (record.CommittedImage is { } image)
            list.Add(new("Startup image", image.Identity.Path));
        if (definition.ReadOnly)
            list.Add(new("Read-only", "Changes are never saved to the image"));
        Facts = list;
        ApplyFigures(record, now, ready ? space : null);
        if (ready && definition.Mode != ManagedDiskMode.CachedVhdx && !HasUnsavedChanges)
        {
            if (Busy(history.LastOrDefault()))
                Headline = $"Reading {ReadingText} · writing {WritingText}";
            else if (definition.Mode == ManagedDiskMode.EphemeralRam && HasSpace)
                Headline = $"{UsedText} · erased when stopped";
        }
        else
            CacheSampled();
        SupportDetails = $"Resource {record.ResourceId}" +
            (runtime is null ? "" : $"\nBoot epoch {runtime.BootEpoch} · creation {runtime.CreationGeneration} · RAM generation {runtime.WriteGeneration} · saved generation {runtime.SavedGeneration?.ToString() ?? "none"}") +
            (record.PhysicalDiskNumber is { } disk ? $"\nPhysical disk {disk}" : "") + (record.VolumePath is { } volume ? $"\nVolume {volume}" : "");
        UpdateActions();
    }

    /// <summary>A running disk image's overview line is its cache's state.</summary>
    internal void CacheSampled()
    {
        if (IsCachedImage && Record.Runtime?.State == ManagedDiskState.Ready && Cache is { HasCache: true, Headline: { Length: > 0 } cacheHeadline })
            Headline = cacheHeadline;
    }

    private static bool Busy(RateSample sample) => sample.Read >= 0.05 || sample.Incoming >= 0.05;

    /// <summary>Rates, totals, space, save and timing figures. Counters start again when the disk
    /// is started again, so a new creation (or a counter going back) starts a new chart.</summary>
    private void ApplyFigures(ManagedDiskRecord record, DateTimeOffset now, VolumeSpace? space)
    {
        var native = record.Native;
        var direct = record.Direct;
        HasActivity = native is not null && record.Runtime?.State == ManagedDiskState.Ready;
        if (!HasActivity || native is null)
        {
            last = null;
            history.Clear();
            Activity = [];
            HasSpace = false;
            TimingOn = false;
        }
        else
        {
            var stats = record.Statistics;
            var current = new Counters(native.BootEpoch, native.CreationGeneration, native.ReadBytes + (direct?.ReadBytes ?? 0),
                native.WriteBytes + (direct?.WriteBytes ?? 0),
                stats is null ? null : stats.ReadRequests + stats.WriteRequests + (direct?.ReadRequests ?? 0) + (direct?.WriteRequests ?? 0));
            var seconds = (now - lastAt).TotalSeconds;
            var continues = last is { } previous && previous.Boot == current.Boot && previous.Creation == current.Creation &&
                current.Read >= previous.Read && current.Written >= previous.Written && seconds > 0;
            if (!continues)
                history.Clear();
            double MiB(ulong before, ulong after) => (after - before) / 1048576.0 / seconds;
            var sample = continues ? new RateSample(MiB(last!.Value.Read, current.Read), MiB(last.Value.Written, current.Written), 0) : default;
            history.Add(sample);
            while (history.Count > VolumeViewModel.HistoryLength)
                history.RemoveAt(0);
            Activity = history.ToArray();
            SecondsPerSample = monitor.Interval.TotalSeconds;
            ActivityTitle = $"Activity · last {Formatting.Format.Duration(TimeSpan.FromSeconds(VolumeViewModel.HistoryLength * SecondsPerSample))}";
            ReadingText = Formatting.Format.Rate(sample.Read);
            WritingText = Formatting.Format.Rate(sample.Incoming);
            RequestsText = current.Requests is not { } requests ? "Unknown with this driver" :
                continues && last!.Value.Requests is { } before && requests >= before ? $"{(requests - before) / seconds:N0} per second" : "0 per second";
            last = current;
            lastAt = now;

            ReadTotalText = Formatting.Format.Bytes(current.Read);
            WrittenTotalText = Formatting.Format.Bytes(current.Written);
            ErrorsText = native.Errors.ToString("N0");
            var directBytes = (direct?.ReadBytes ?? 0) + (direct?.WriteBytes ?? 0);
            DirectShareText = record.Definition.Access == RamAccess.Standard ? "Not used (standard access)" :
                current.Read + current.Written == 0 ? "No reads or writes yet" :
                Formatting.Format.Percent(100.0 * directBytes / (current.Read + current.Written));

            HasSpace = space is { TotalBytes: > 0 };
            if (space is { TotalBytes: > 0 } volume)
            {
                SpaceBytes = volume.TotalBytes;
                UsedBytes = volume.TotalBytes - Math.Min(volume.TotalBytes, volume.FreeBytes);
                UsedText = $"{Formatting.Format.Bytes(UsedBytes)} of {Formatting.Format.Bytes(SpaceBytes)} used";
                FreeText = Formatting.Format.Bytes(volume.FreeBytes);
            }

            TimingOn = (native.Flags & RamDiskFlags.Timing) != 0 && stats is not null;
            TimingText = !TimingOn ? "" :
                $"Reads: average {Time(stats!.TimedReads, stats.AverageReadMicroseconds)}, slowest {Time(stats.TimedReads, stats.MaxReadMicroseconds)} · {stats.TimedReads:N0} timed\n" +
                $"Writes: average {Time(stats.TimedWrites, stats.AverageWriteMicroseconds)}, slowest {Time(stats.TimedWrites, stats.MaxWriteMicroseconds)} · {stats.TimedWrites:N0} timed";
        }

        HasSaveFigures = record.Definition.Mode == ManagedDiskMode.ImageInRam;
        var save = record.LastSave;
        LastSaveText = save is null ? record.SavedAt is null ? "Not saved yet" : "Unknown until the next save" :
            $"{Formatting.Format.Duration(save.Duration)} for {Formatting.Format.Bytes(save.Bytes)} · {Formatting.Format.Rate(save.Bytes / 1048576.0 / Math.Max(save.Duration.TotalSeconds, 0.001))}";
        WrittenSinceSaveText = record.SavedAt is null ? "Not saved yet" :
            save is { WrittenBytes: { } atSave } && record.Native is { } now2 && now2.BootEpoch == save.BootEpoch &&
            now2.CreationGeneration == save.CreationGeneration && record.WrittenBytes is { } written && written >= atSave
                ? (written == atSave ? "Nothing" : "At most " + Formatting.Format.Bytes(written - atSave))
                : "Unknown since the disk was restarted";
    }

    private static string Time(ulong count, double microseconds) => count == 0 ? "none yet" : Formatting.Format.Micro(microseconds);

    private void UpdateActions()
    {
        var record = Record;
        var definition = record.Definition;
        var runtime = record.Runtime;
        var stopped = runtime?.State == ManagedDiskState.Stopped;
        var ready = runtime?.State == ManagedDiskState.Ready;
        var idle = !IsBusy && !monitor.GlobalBusy && !StateUnavailable;
        IsStoppedState = stopped;
        IsReadyState = ready;
        IsRunning = runtime is not null && !stopped;
        NeedsRecovery = runtime is null || runtime.State is ManagedDiskState.Blocked or ManagedDiskState.RecoveryRequired or ManagedDiskState.Faulted;
        ShowSave = ShowExport = definition.Mode == ManagedDiskMode.ImageInRam;
        ShowEditCache = definition.Mode == ManagedDiskMode.CachedVhdx;
        ShowDeleteImage = definition.Mode != ManagedDiskMode.EphemeralRam;
        CanStart = idle && stopped;
        CanStop = idle && runtime is not null && !stopped;
        CanFlush = idle && ready;
        CanSave = idle && ready && ShowSave;
        CanExport = idle && ready && ShowExport;
        CanFormat = idle && runtime is not null && runtime.State is ManagedDiskState.Ready or ManagedDiskState.RecoveryRequired &&
            record.VolumePath is not null && !definition.ReadOnly;
        CanEditCache = idle && ShowEditCache && (ready || stopped);
        CanEditStartup = idle && runtime is not null;
        CanEditStopped = idle && stopped;
        CanForget = idle && (stopped || runtime is null);
        CanDeleteImage = idle && ShowDeleteImage && runtime is not null;
        CanRecover = idle && NeedsRecovery;
        StopText = definition.Mode == ManagedDiskMode.CachedVhdx ? "Stop" : "Stop…";
    }

    internal void Refresh() => UpdateActions();
}

/// <summary>Size and free space of a volume, as Windows reports them.</summary>
public readonly record struct VolumeSpace(ulong TotalBytes, ulong FreeBytes);

/// <summary>One labelled value in a details list.</summary>
public sealed record Fact(string Label, string Value);
