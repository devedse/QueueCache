using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QueueCache.Desktop.Formatting;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Desktop.ViewModels;

/// <summary>New disk. The service decides what is possible (<see cref="ManagedDiskCapability"/>)
/// and performs the creation; this only gathers one typed definition.</summary>
public sealed partial class CreateDiskViewModel : ObservableObject
{
    private readonly IManagedDiskService service;
    private readonly Guid resourceId = Guid.NewGuid();
    private IReadOnlyList<ManagedDiskCapability> capabilities = [];
    private ImageInspection? inspected;
    private CancellationTokenSource? operation;

    public CreateDiskViewModel(IManagedDiskService service, IReadOnlySet<char> usedLetters)
    {
        this.service = service;
        Letters = Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).Where(c => !usedLetters.Contains(c)).Select(c => $"{c}:").ToArray();
        letter = Letters.Contains("R:") ? "R:" : Letters.FirstOrDefault() ?? "R:";
        access = (int)ManagedDiskDefinition.DefaultRamAccess;
        Update();
    }

    public IReadOnlyList<string> Letters { get; }
    public IReadOnlyList<string> AccessChoices { get; } = ["Standard (full Windows disk stack)", "Direct (fastest: read and written straight from RAM)"];
    public IReadOnlyList<string> AllocationChoices { get; } = ["Dynamic (grows as it fills)", "Fixed (full size on disk now)"];

    /// <summary>Raised when the disk was created; the view closes with it.</summary>
    public event EventHandler<ManagedDiskRuntime>? Created;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsRamDisk), nameof(IsCachedImage), nameof(IsImage))] private ManagedDiskMode mode;
    [ObservableProperty] private bool useExisting;
    [ObservableProperty] private double capacityGiB = 1;
    [ObservableProperty] private int cacheMiB = 256;
    [ObservableProperty] private string imagePath = "";
    [ObservableProperty] private string checkpointDirectory = "";
    [ObservableProperty] private string letter;
    [ObservableProperty] private string label = "QueueCache";
    [ObservableProperty] private int allocation;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsStrict))] private bool isFast;
    [ObservableProperty] private int access;
    [ObservableProperty] private bool startWithWindows;
    [ObservableProperty] private bool saveBeforeStop = true;
    [ObservableProperty] private bool saveDuringShutdown;
    [ObservableProperty] private bool readOnly;
    [ObservableProperty] private bool initializeBlank;
    [ObservableProperty] private bool isAdvancedOpen;
    [ObservableProperty] private bool isWorking;

    // Derived view state (Update)
    [ObservableProperty] private bool showSource;
    [ObservableProperty] private bool showImage;
    [ObservableProperty] private bool showCapacity;
    [ObservableProperty] private bool showCache;
    [ObservableProperty] private bool showCheckpoint;
    [ObservableProperty] private bool showAllocation;
    [ObservableProperty] private bool showLabel;
    [ObservableProperty] private bool showInitializeBlank;
    [ObservableProperty] private bool showReadOnly;
    [ObservableProperty] private bool showAccess;
    [ObservableProperty] private bool canCreate;
    [ObservableProperty] private string memoryText = "";
    [ObservableProperty] private string inspectionText = "";
    [ObservableProperty] private string startupText = "";
    [ObservableProperty] private string initializeText = "";
    [ObservableProperty] private string status = "Checking which disk types are available…";
    [ObservableProperty] private bool statusIsError;

    public bool IsRamDisk
    {
        get => Mode == ManagedDiskMode.EphemeralRam;
        set { if (value) Mode = ManagedDiskMode.EphemeralRam; }
    }
    public bool IsImage
    {
        get => Mode == ManagedDiskMode.ImageInRam;
        set { if (value) Mode = ManagedDiskMode.ImageInRam; }
    }
    public bool IsCachedImage
    {
        get => Mode == ManagedDiskMode.CachedVhdx;
        set { if (value) Mode = ManagedDiskMode.CachedVhdx; }
    }

    public bool IsStrict
    {
        get => !IsFast;
        set => IsFast = !value;
    }

    private bool Existing => Mode != ManagedDiskMode.EphemeralRam && UseExisting;
    private ImageInspection? CurrentInspection => inspected is not null && string.Equals(inspected.Path, ImagePath, StringComparison.OrdinalIgnoreCase) ? inspected : null;
    private ulong CapacityBytes => checked((ulong)Math.Round(Math.Max(0, CapacityGiB) * 1024) * ManagedDiskDefinition.MiB);

    public async Task LoadAsync()
    {
        try
        {
            capabilities = await service.CapabilitiesAsync();
            Update();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, error: true);
        }
    }

    partial void OnModeChanged(ManagedDiskMode value) { inspected = null; UseExisting = false; Update(); }
    partial void OnUseExistingChanged(bool value) { inspected = null; ReadOnly = false; InitializeBlank = false; Update(); }
    // Changing the path invalidates the inspected identity and capacity at once, before any other event.
    partial void OnImagePathChanged(string value) { inspected = null; InspectionText = ""; Update(); }
    partial void OnCapacityGiBChanged(double value) => Update();
    partial void OnIsFastChanged(bool value) => Update();
    partial void OnInitializeBlankChanged(bool value) => Update();
    partial void OnReadOnlyChanged(bool value)
    {
        if (value)
        {
            SaveBeforeStop = false;
            SaveDuringShutdown = false;
            InitializeBlank = false;
        }
        Update();
    }

    public ManagedDiskDefinition Definition()
    {
        var backed = Mode == ManagedDiskMode.CachedVhdx;
        var fullImage = Mode == ManagedDiskMode.ImageInRam;
        var current = CurrentInspection;
        var letterChar = Letter.Length > 0 ? char.ToUpperInvariant(Letter[0]) : 'R';
        // Choosing Fast, whose consequence the option itself states, is the volatility acknowledgement.
        return new(resourceId, Mode, Existing ? ManagedDiskSource.OpenExisting : ManagedDiskSource.CreateNew,
            Existing ? current?.VirtualBytes ?? 0 : CapacityBytes, letterChar, Label ?? "",
            Mode == ManagedDiskMode.EphemeralRam ? null : ImagePath, fullImage ? CheckpointDirectory : null,
            backed ? new CacheConfiguration(CacheMiB, IsFast ? CachePreset.Fast : CachePreset.Strict) : null, backed && IsFast,
            StartWithWindows, fullImage && SaveBeforeStop, fullImage && SaveDuringShutdown, fullImage && Existing && ReadOnly,
            (ImageAllocation)Allocation, Existing ? current?.SectorBytes ?? 512 : 512,
            InitializeBlankImage: Existing && InitializeBlank,
            ExpectedBlankImage: Existing && InitializeBlank ? current : null,
            Access: backed ? RamAccess.Standard : (RamAccess)Access);
    }

    private void Update()
    {
        if (IsWorking)
            return;
        var pure = Mode == ManagedDiskMode.EphemeralRam;
        var backed = Mode == ManagedDiskMode.CachedVhdx;
        ShowSource = ShowImage = !pure;
        ShowCapacity = !Existing;
        ShowCache = backed;
        ShowAccess = !backed;
        ShowCheckpoint = Mode == ManagedDiskMode.ImageInRam;
        ShowAllocation = !pure && !Existing;
        ShowLabel = !Existing || InitializeBlank;
        ShowInitializeBlank = Existing;
        ShowReadOnly = Mode == ManagedDiskMode.ImageInRam && Existing;
        InitializeText = backed ? "Format a completely blank image as NTFS (this changes the .vhdx file)" :
            "Format a completely blank image as NTFS in RAM (the file stays unchanged until you save)";
        StartupText = ManagedDiskDefinition.New(Mode).StartupDescription + ".";
        var current = CurrentInspection;
        var bytes = Existing ? current?.VirtualBytes : CapacityBytes;
        MemoryText = backed ? $"Uses {Format.Bytes((ulong)CacheMiB << 20)} of RAM for its cache, plus a little for bookkeeping." :
            bytes is null ? "Inspect the image to see how much RAM it needs; its file size is not its capacity." :
            $"Uses {Format.Bytes(bytes.Value)} of RAM while running, plus a little for bookkeeping. It must fit in the RAM QueueCache may use.";
        if (Existing && inspected is not null)
            InspectionText = $"Capacity {Format.Bytes(inspected.VirtualBytes)} ({inspected.VirtualBytes / ManagedDiskDefinition.MiB:N0} MiB); file size {Format.Bytes(inspected.AllocatedBytes)}. " +
                (InitializeBlank ? "Before formatting, every logical sector is checked to be blank; an image with data or damage is refused."
                    : "The existing layout and data are kept; no automatic formatting.");
        var capability = capabilities.SingleOrDefault(c => c.Mode == Mode);
        CanCreate = capability?.CanStart == true && (!Existing || current is not null);
        if (capability is null)
            SetStatus("Checking which disk types are available…");
        else if (!capability.CanStart)
            SetStatus(capability.UnavailableReason ?? "This disk type is not available.", error: true);
        else if (Existing && current is null)
            SetStatus("Inspect the image first.");
        else
            SetStatus("");
    }

    private void SetStatus(string text, bool error = false)
    {
        Status = text;
        StatusIsError = error;
    }

    [RelayCommand]
    private async Task InspectAsync()
    {
        if (operation is not null)
            return;
        Begin();
        try
        {
            var path = ManagedDiskPaths.ValidateImagePath(ImagePath);
            SetStatus("Reading the image's identity and capacity…");
            inspected = await service.InspectAsync(path, operation!.Token);
            inspected.ValidateFor(Definition());
        }
        catch (Exception ex)
        {
            inspected = null;
            InspectionText = ex.Message;
        }
        finally
        {
            End();
        }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (operation is not null)
            return;
        Begin();
        string? error = null;
        ManagedDiskRuntime? result = null;
        try
        {
            var definition = Definition();
            definition.Validate();
            if (Existing)
                (inspected ?? throw new InvalidOperationException("Inspect the image first.")).ValidateFor(definition);
            result = await service.CreateAsync(definition, new CreationProgress(text => Status = text), operation!.Token);
            if (result.ResourceId != resourceId || result.Mode != Mode || result.State != ManagedDiskState.Ready ||
                result.BootEpoch == Guid.Empty || result.CreationGeneration == 0 || string.IsNullOrWhiteSpace(result.Volume))
                throw new InvalidDataException("The service did not return a ready disk with a valid identity.");
        }
        catch (OperationCanceledException)
        {
            error = "Creation cancelled. No disk was created.";
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            End();
        }
        if (error is not null)
            SetStatus(error, error: true);
        else
            Created?.Invoke(this, result!);
    }

    /// <summary>Cancels a running inspection or creation. Returns false when nothing was running (the dialog may close).</summary>
    public bool CancelOperation()
    {
        if (operation is null)
            return false;
        operation.Cancel();
        return true;
    }

    private void Begin()
    {
        operation = new();
        IsWorking = true;
        CanCreate = false;
    }

    private void End()
    {
        operation!.Dispose();
        operation = null;
        IsWorking = false;
        Update();
    }

    private sealed class CreationProgress(Action<string> update) : IProgress<ManagedDiskProgress>
    {
        public void Report(ManagedDiskProgress value)
        {
            var text = value.TotalBytes is > 0 && value.CompletedBytes is { } done ? $"{value.Message} {Format.Percent(100.0 * done / value.TotalBytes.Value)}" : value.Message;
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                update(text);
            else
                Avalonia.Threading.Dispatcher.UIThread.Post(() => update(text));
        }
    }
}
