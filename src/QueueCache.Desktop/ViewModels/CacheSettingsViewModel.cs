using CommunityToolkit.Mvvm.ComponentModel;
using QueueCache.Desktop.Formatting;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Desktop.ViewModels;

public sealed record CacheSettingsResult(CacheConfiguration Configuration, bool Persistent);

/// <summary>Add or change a volume's cache. The few choices most people make come first (RAM, Fast
/// or Strict, startup); the tuning that experts change sits in a collapsed Advanced section.</summary>
public sealed partial class CacheSettingsViewModel : ObservableObject
{
    private static readonly int[] Sizes = [256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536];
    private static readonly int[] Batches = [4, 64, 256, 512, 1024];
    private readonly int[] sizes;
    private readonly bool newCache;
    private readonly bool enabled;

    public CacheSettingsViewModel(VolumeDescription volume, WriteCacheState state, bool persistent, int availableMiB = 4096, bool showStartup = true)
    {
        if (availableMiB is < 1 or > MemoryBudget.MaximumMiB)
            throw new ArgumentOutOfRangeException(nameof(availableMiB));
        var options = state.Options ?? new();
        newCache = state.BudgetBytes == 0;
        enabled = newCache || state.Enabled;
        Title = newCache ? $"Add a cache to {volume.Name}" : $"Cache for {volume.Name}";
        Subtitle = VolumeViewModel.Describe(volume);
        ApplyText = newCache ? "Add cache" : "Save";
        AvailableMiB = availableMiB;
        sizes = Sizes.Where(n => n <= availableMiB).ToArray();
        MemoryChoices = sizes.Select(n => Format.Bytes((ulong)n << 20)).Append("Custom size").ToArray();
        var initial = newCache ? Math.Min(4096, availableMiB) : Math.Min(availableMiB, (int)(state.BudgetBytes >> 20));
        selectedMemory = Array.IndexOf(sizes, initial) is var index and >= 0 ? index : sizes.Length;
        customMiB = initial;
        AvailableText = $"Up to {Format.Bytes((ulong)availableMiB << 20)} available";
        // Fast is the default for a new cache.
        isFast = newCache || state.UnsafeDefer;
        startWithWindows = persistent;
        ShowStartup = showStartup;
        allocation = (int)options.Allocation;
        writePercent = options.WritePercent;
        retainWrites = options.RetainWrites;
        promoteOnRead = options.PromoteOnRead;
        DrainChoices = state.SupportsDeferredDrain ? ["Eager", "Balanced", "Idle", "Deferred"] : ["Eager", "Balanced", "Idle"];
        drain = (int)options.Drain;
        lowPercent = options.LowPercent;
        highPercent = options.HighPercent;
        MaximumAgeMs = state.SupportsDeferredDrain ? 3600000 : 300000;
        maxDirtyAgeMs = options.MaxDirtyAgeMs;
        idleMs = options.IdleMs;
        BatchChoices = Batches.Select(n => Format.Bytes((ulong)n << 10)).Append("Custom size").ToArray();
        selectedBatch = Array.IndexOf(Batches, options.BatchKiB) is var batch and >= 0 ? batch : Batches.Length; // Keeps CLI batch sizes not in the list.
        customBatchKiB = options.BatchKiB;
        parallelism = options.Parallelism;
    }

    /// <summary>A remembered configuration (a disk image's cache); it has no live counters and no startup switch.</summary>
    public CacheSettingsViewModel(VolumeDescription volume, CacheConfiguration configuration, int availableMiB = 4096)
        : this(volume, EditorInput(configuration, (ulong)volume.Bytes), false, availableMiB, showStartup: false) { }

    private static WriteCacheState EditorInput(CacheConfiguration configuration, ulong bytes) =>
        new((configuration.Enabled ? 1U : 0U) | (configuration.Preset == CachePreset.Fast ? 32U : 0U) | 4096U,
            0, bytes, checked((ulong)configuration.BudgetMiB << 20), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0) { Options = configuration.Options };

    public string Title { get; }
    public string Subtitle { get; }
    public string ApplyText { get; }
    public int AvailableMiB { get; }
    public string AvailableText { get; }
    public IReadOnlyList<string> MemoryChoices { get; }
    public IReadOnlyList<string> DrainChoices { get; }
    public IReadOnlyList<string> BatchChoices { get; }
    public IReadOnlyList<string> AllocationChoices { get; } = ["Shared automatically", "Fixed split between reads and writes"];
    public bool ShowStartup { get; }
    public int MaximumAgeMs { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsCustomMemory))] private int selectedMemory;
    [ObservableProperty] private int customMiB;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsStrict))] private bool isFast;
    [ObservableProperty] private bool startWithWindows;
    [ObservableProperty] private bool isAdvancedOpen;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsFixedAllocation), nameof(AllocationText))] private int allocation;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AllocationText))] private int writePercent;
    [ObservableProperty] private bool retainWrites;
    [ObservableProperty] private bool promoteOnRead;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DrainText), nameof(ShowWatermarks), nameof(ShowAge), nameof(ShowIdle))] private int drain;
    [ObservableProperty] private int lowPercent;
    [ObservableProperty] private int highPercent;
    [ObservableProperty] private int maxDirtyAgeMs;
    [ObservableProperty] private int idleMs;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsCustomBatch))] private int selectedBatch;
    [ObservableProperty] private int customBatchKiB;
    [ObservableProperty] private int parallelism;
    [ObservableProperty] private string? error;

    public bool IsCustomMemory => SelectedMemory == sizes.Length;
    public bool IsStrict
    {
        get => !IsFast;
        set => IsFast = !value;
    }
    public bool IsFixedAllocation => Allocation == 1;
    public bool IsCustomBatch => SelectedBatch == Batches.Length;
    public string AllocationText => Allocation == 0
        ? "Reads and writes share the cache. Data that is already on disk makes room for new writes."
        : $"{100 - WritePercent}% for reads, {WritePercent}% for writes. 0% writes caches only reads; 100% caches only writes.";
    // Which settings each algorithm uses (QcShouldDrain in driver/qcache/cachepolicy.h): Eager none of
    // them; Balanced the watermarks and maximum age; Idle also the idle interval; Deferred only the age.
    public bool ShowWatermarks => Drain is 1 or 2;
    public bool ShowAge => Drain != 0;
    public bool ShowIdle => Drain == 2;
    public string DrainText => Drain switch
    {
        0 => "Eager: every write starts going to the disk as soon as it arrives. The least data waits in RAM and the disk is busiest; repeated overwrites are still merged.",
        1 => "Balanced: writes wait in RAM until the cache reaches the start level or the oldest write reaches the maximum age, then go to the disk down to the stop level. Absorbs bursts and overwrites, with more data waiting in RAM.",
        2 => "Idle: like Balanced, and also writes to the disk whenever no new write has arrived for the idle interval. The disk stays quiet during a burst and catches up between bursts.",
        _ => "Deferred: writes go to the disk only when the oldest one reaches its maximum age. No idle or fill-level trigger. Flushes, a full cache and shutdown still write everything."
    };

    /// <summary>The configuration to apply, or null with <see cref="Error"/> set.</summary>
    public CacheSettingsResult? Result()
    {
        try
        {
            var config = new CacheConfiguration(IsCustomMemory ? CustomMiB : sizes[SelectedMemory],
                IsFast ? CachePreset.Fast : CachePreset.Strict, enabled)
            {
                Options = new((CacheAllocation)Allocation, WritePercent, RetainWrites, PromoteOnRead, (DrainAlgorithm)Drain,
                    LowPercent, HighPercent, MaxDirtyAgeMs, IdleMs, IsCustomBatch ? CustomBatchKiB : Batches[SelectedBatch], Parallelism)
            };
            config.Validate(true);
            Error = null;
            return new(config, StartWithWindows);
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
            return null;
        }
    }
}
