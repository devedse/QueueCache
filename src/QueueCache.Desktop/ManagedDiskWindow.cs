using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using QueueCache.Management;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Desktop;

/// <summary>One creation flow; capability decisions and disk operations belong to the shared service.</summary>
[SupportedOSPlatform("windows")]
public sealed class ManagedDiskWindow : Window
{
    private readonly IManagedDiskService service;
    private readonly Guid resourceId = Guid.NewGuid();
    private readonly ComboBox mode = Choice("DiskMode", ["Pure RAM disk", "VHDX with RAM cache", "Entire VHDX in RAM"]);
    private readonly ComboBox source = Choice("DiskSource", ["Create a new disk", "Open an existing VHDX"]);
    private readonly NumericUpDown capacity = Number("DiskCapacityMiB", 16, 131072, 1024);
    private readonly NumericUpDown cache = Number("DiskCacheMiB", 1, MemoryBudget.MaximumMiB, 256);
    private readonly TextBox image = new() { Name = "DiskImagePath", PlaceholderText = @"C:\Disks\Disk.vhdx" };
    private readonly TextBox checkpoints = new() { Name = "DiskCheckpointDirectory", PlaceholderText = @"C:\Disks\Checkpoints" };
    private readonly TextBox letter = new() { Name = "DiskLetter", Text = "R", MaxLength = 1 };
    private readonly TextBox label = new() { Name = "DiskLabel", Text = "QueueCache" };
    private readonly ComboBox allocation = Choice("DiskAllocation", ["Dynamic VHDX", "Fixed VHDX"]);
    private readonly ComboBox behaviour = Choice("DiskWriteBehaviour", ["Strict", "Fast"]);
    private readonly CheckBox volatility = new() { Name = "DiskVolatility", Content = "Accept loss of pending RAM writes after power loss or unsafe removal" };
    private readonly CheckBox startup = new() { Name = "DiskStartup", Content = "Start with Windows" };
    private readonly CheckBox saveBeforeStop = new() { Name = "DiskSaveBeforeStop", Content = "Save before stopping", IsChecked = true };
    private readonly CheckBox shutdownSave = new() { Name = "DiskShutdownSave", Content = "Attempt a save during Windows shutdown (best effort)" };
    private readonly CheckBox readOnly = new() { Name = "DiskReadOnly", Content = "Open the RAM copy read-only" };
    private readonly CheckBox initializeBlank = new() { Name = "DiskInitializeRaw" };
    private readonly TextBlock persistence = MainWindow.Text("", 13, MainWindow.Muted);
    private readonly TextBlock startupHint = MainWindow.Text("", 13, MainWindow.Muted);
    private readonly TextBlock memoryHint = MainWindow.Text("", 13, MainWindow.Muted);
    private readonly TextBlock inspectionHint = MainWindow.Text("", 13, MainWindow.Muted);
    private readonly TextBlock status = MainWindow.Text("Checking available disk modes…", 13, MainWindow.Muted);
    private readonly Button create = new() { Name = "CreateManagedDisk", Content = "Create disk", Background = MainWindow.Accent, Foreground = Brushes.White, IsEnabled = false };
    private readonly Button inspect = new() { Name = "InspectDiskImage", Content = "Inspect VHDX" };
    private readonly Button cancel = new() { Content = "Cancel" };
    private readonly StackPanel form = new() { Spacing = 12 };
    private readonly Control sourceField, capacityField, imageField, cacheField, checkpointField, allocationField, behaviourField, labelField;
    private IReadOnlyList<ManagedDiskCapability> capabilities = [];
    private ImageInspection? inspected;
    private CancellationTokenSource? operation;
    private bool closed;

    public ManagedDiskWindow(IManagedDiskService service)
    {
        this.service = service;
        Title = "Create disk";
        Icon = AppBranding.CreateIcon();
        Width = 660;
        Height = 800;
        MinWidth = 560;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        var body = new StackPanel { Margin = new Thickness(28), Spacing = 16 };
        body.Children.Add(MainWindow.Text("Create disk", 25, MainWindow.Ink, FontWeight.SemiBold));
        body.Children.Add(Field("Storage", mode));
        body.Children.Add(persistence);
        sourceField = Field("Source", source);
        capacityField = Field("Virtual disk capacity (MiB)", capacity);
        var imageControls = new StackPanel { Spacing = 8 };
        imageControls.Children.Add(image);
        imageControls.Children.Add(inspect);
        imageControls.Children.Add(inspectionHint);
        imageField = Field("VHDX path", imageControls);
        cacheField = Field("Independent RAM cache budget (MiB)", cache);
        checkpointField = Field("Checkpoint directory", checkpoints);
        allocationField = Field("New image allocation", allocation);
        behaviourField = Field("Cached write behaviour", behaviour);
        labelField = Field("New NTFS volume label", label);
        foreach (var control in new Control[] { sourceField, imageField, capacityField, memoryHint, cacheField,
            allocationField, checkpointField, behaviourField, volatility, Field("Preferred drive letter (D–Z)", letter),
            labelField, initializeBlank, readOnly, saveBeforeStop, shutdownSave, startup, startupHint })
            form.Children.Add(control);
        body.Children.Add(form);
        body.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        buttons.Children.Add(cancel);
        buttons.Children.Add(create);
        body.Children.Add(buttons);
        Content = new ScrollViewer { Content = body };
        mode.SelectionChanged += (_, _) => { inspected = null; source.SelectedIndex = 0; Update(); };
        source.SelectionChanged += (_, _) => { inspected = null; readOnly.IsChecked = false; initializeBlank.IsChecked = false; Update(); };
        image.TextChanged += (_, _) => { inspected = null; inspectionHint.Text = ""; Update(); };
        behaviour.SelectionChanged += (_, _) => Update();
        capacity.ValueChanged += (_, _) => Update();
        readOnly.IsCheckedChanged += (_, _) =>
        {
            if (readOnly.IsChecked == true) { saveBeforeStop.IsChecked = false; shutdownSave.IsChecked = false; }
            if (readOnly.IsChecked == true) initializeBlank.IsChecked = false;
            Update();
        };
        initializeBlank.IsCheckedChanged += (_, _) => Update();
        inspect.Click += async (_, _) => await InspectAsync();
        create.Click += async (_, _) => await CreateAsync();
        cancel.Click += (_, _) => { if (operation is not null) operation.Cancel(); else Close(null); };
        Closing += (_, e) => { if (operation is not null) { e.Cancel = true; operation.Cancel(); } };
        Closed += (_, _) => closed = true;
        Opened += async (_, _) =>
        {
            try { capabilities = await service.CapabilitiesAsync(); if (!closed) Update(); }
            catch (Exception ex) { if (!closed) status.Text = ex.Message; }
        };
        Update();
    }

    private ManagedDiskMode Mode => (ManagedDiskMode)mode.SelectedIndex;
    private bool Existing => Mode != ManagedDiskMode.EphemeralRam && source.SelectedIndex == 1;
    private ImageInspection? CurrentInspection => inspected is not null &&
        string.Equals(inspected.Path, image.Text, StringComparison.OrdinalIgnoreCase) ? inspected : null;

    public ManagedDiskDefinition Definition()
    {
        var backed = Mode == ManagedDiskMode.CachedVhdx;
        var fullImage = Mode == ManagedDiskMode.ImageInRam;
        var fast = backed && behaviour.SelectedIndex == 1;
        var current = CurrentInspection;
        return new(resourceId, Mode, Existing ? ManagedDiskSource.OpenExisting : ManagedDiskSource.CreateNew,
            Existing ? current?.VirtualBytes ?? 0 : checked((ulong)(capacity.Value ?? 0) * ManagedDiskDefinition.MiB),
            char.ToUpperInvariant((letter.Text ?? "").SingleOrDefault()), label.Text ?? "",
            Mode == ManagedDiskMode.EphemeralRam ? null : image.Text,
            fullImage ? checkpoints.Text : null, backed ? new CacheConfiguration((int)(cache.Value ?? 0),
                fast ? CachePreset.Fast : CachePreset.Strict) : null, fast && volatility.IsChecked == true,
            startup.IsChecked == true, fullImage && saveBeforeStop.IsChecked == true,
            fullImage && shutdownSave.IsChecked == true, fullImage && Existing && readOnly.IsChecked == true,
            (ImageAllocation)allocation.SelectedIndex, Existing ? current?.SectorBytes ?? 512 : 512,
            InitializeBlankImage: Existing && initializeBlank.IsChecked == true,
            ExpectedBlankImage: Existing && initializeBlank.IsChecked == true ? current : null);
    }

    private void Update()
    {
        if (closed || operation is not null) return;
        var pure = Mode == ManagedDiskMode.EphemeralRam;
        var backed = Mode == ManagedDiskMode.CachedVhdx;
        sourceField.IsVisible = imageField.IsVisible = !pure;
        capacityField.IsVisible = !Existing;
        cacheField.IsVisible = behaviourField.IsVisible = backed;
        checkpointField.IsVisible = saveBeforeStop.IsVisible = shutdownSave.IsVisible = Mode == ManagedDiskMode.ImageInRam;
        allocationField.IsVisible = !pure && !Existing;
        labelField.IsVisible = !Existing || initializeBlank.IsChecked == true;
        initializeBlank.IsVisible = Existing;
        initializeBlank.IsEnabled = readOnly.IsChecked != true;
        initializeBlank.Content = backed ? "Initialize a completely blank image as GPT/NTFS (changes this VHDX)" :
            "Initialize a completely blank RAM copy as GPT/NTFS (preserves source; saves a new checkpoint)";
        volatility.IsVisible = backed && behaviour.SelectedIndex == 1;
        readOnly.IsVisible = Mode == ManagedDiskMode.ImageInRam && Existing;
        saveBeforeStop.IsEnabled = shutdownSave.IsEnabled = readOnly.IsChecked != true;
        inspect.IsVisible = Existing;
        startupHint.Text = ManagedDiskDefinition.New(Mode).StartupDescription + ".";
        persistence.Text = Mode switch
        {
            ManagedDiskMode.EphemeralRam => "Data lives only in RAM and is lost when stopped, restarted or powered off. Each new creation is partitioned and formatted as NTFS.",
            ManagedDiskMode.CachedVhdx => "The VHDX holds the disk; its RAM cache can be smaller than the disk. Existing images keep their filesystem and data.",
            _ => "Load every logical sector into RAM before use. Flushes stay in RAM; Save image creates a consistent VHDX checkpoint. The imported file is preserved."
        };
        var current = CurrentInspection;
        var bytes = Existing ? current?.VirtualBytes : checked((ulong)(capacity.Value ?? 0) * ManagedDiskDefinition.MiB);
        memoryHint.Text = backed ? "RAM is the independent cache budget, including cache metadata and staging buffers." :
            bytes is null ? "Inspect the VHDX to determine its full virtual RAM requirement; file size is not disk capacity." :
            $"Full RAM payload: {bytes / ManagedDiskDefinition.MiB:N0} MiB, plus native metadata and bounded transfer workspace. Reservation must fit the shared cache/RAM-disk budget.";
        if (Existing && inspected is not null)
            inspectionHint.Text = $"Virtual capacity: {inspected.VirtualBytes / ManagedDiskDefinition.MiB:N0} MiB; allocated file: {inspected.AllocatedBytes / ManagedDiskDefinition.MiB:N0} MiB. " +
                (initializeBlank.IsChecked == true ? "Explicit initialization checks every logical sector is blank before formatting. Nonempty or damaged images are refused." :
                    "Preserve the existing layout; no automatic formatting.");
        var capability = capabilities.SingleOrDefault(c => c.Mode == Mode);
        create.IsEnabled = capability?.CanStart == true && (!Existing || current is not null);
        status.Text = capability is null ? "Checking available disk modes…" : !capability.CanStart ? capability.UnavailableReason :
            Existing && current is null ? "Inspect the existing image before creating the disk." : "Ready to validate and reserve memory before creation.";
    }

    private async Task InspectAsync()
    {
        if (operation is not null) return;
        Begin();
        try
        {
            var path = ManagedDiskPaths.ValidateImagePath(image.Text);
            status.Text = "Inspecting image identity and virtual capacity…";
            inspected = await service.InspectAsync(path, operation!.Token);
            inspected.ValidateFor(Definition());
        }
        catch (Exception ex) { inspected = null; inspectionHint.Text = ex.Message; }
        finally { End(); }
    }

    private async Task CreateAsync()
    {
        if (operation is not null) return;
        Begin();
        string? error = null;
        ManagedDiskRuntime? result = null;
        try
        {
            var definition = Definition();
            definition.Validate();
            if (Existing) (inspected ?? throw new InvalidOperationException("Inspect the source image first.")).ValidateFor(definition);
            result = await service.CreateAsync(definition, new UiProgress(text => status.Text = text), operation!.Token);
            if (result.ResourceId != resourceId || result.Mode != Mode || result.State != ManagedDiskState.Ready ||
                result.BootEpoch == Guid.Empty || result.CreationGeneration == 0 || string.IsNullOrWhiteSpace(result.Volume))
                throw new InvalidDataException("The service did not return a ready disk with a valid native identity.");
        }
        catch (OperationCanceledException) { error = "Creation cancelled. The service returned without a ready disk."; }
        catch (Exception ex) { error = ex.Message; }
        finally { End(); }
        if (error is not null) status.Text = error;
        else Close(result);
    }

    private void Begin()
    {
        operation = new();
        mode.IsEnabled = form.IsEnabled = create.IsEnabled = false;
        cancel.Content = "Cancel operation";
    }
    private void End()
    {
        operation!.Dispose(); operation = null;
        mode.IsEnabled = form.IsEnabled = true;
        cancel.Content = "Cancel";
        Update();
    }
    private static ComboBox Choice(string name, string[] items) => new() { Name = name, ItemsSource = items, SelectedIndex = 0 };
    private static NumericUpDown Number(string name, int min, int max, int value) => new() { Name = name, Minimum = min, Maximum = max, Value = value, FormatString = "0", Increment = 1 };
    private static Control Field(string label, Control input)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(MainWindow.Text(label, 13, MainWindow.Ink, FontWeight.SemiBold));
        panel.Children.Add(input);
        return panel;
    }
    private sealed class UiProgress(Action<string> update) : IProgress<ManagedDiskProgress>
    {
        public void Report(ManagedDiskProgress value)
        {
            var text = value.TotalBytes is > 0 ? $"{value.Message} {value.CompletedBytes:N0} / {value.TotalBytes:N0} bytes" : value.Message;
            if (Dispatcher.UIThread.CheckAccess()) update(text);
            else Dispatcher.UIThread.Post(() => update(text));
        }
    }
}
