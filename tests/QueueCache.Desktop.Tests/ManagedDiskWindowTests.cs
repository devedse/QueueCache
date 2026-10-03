using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QueueCache.Desktop;
using QueueCache.Management;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;

internal static class ManagedDiskWindowTests
{
    public static void Run(string output)
    {
        var service = new Fixture();
        var window = new ManagedDiskWindow(service);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        T Control<T>(string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
        void Click(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Select(string name, int index) { Control<ComboBox>(name).SelectedIndex = index; Dispatcher.UIThread.RunJobs(); }
        bool Text(string fragment) => window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text?.Contains(fragment) == true);
        var pure = window.Definition();
        pure.Validate();
        Check(pure.Mode == ManagedDiskMode.EphemeralRam && pure.ImagePath is null && pure.Cache is null, "pure RAM uses full capacity without an image or partial cache");
        Check(Text("fresh empty disk at each Windows startup") && Text("lost when stopped"), "pure RAM explains volatility and fresh startup formatting");
        Check(!Control<TextBox>("DiskImagePath").IsEffectivelyVisible && !Control<NumericUpDown>("DiskCacheMiB").IsEffectivelyVisible, "pure RAM hides backing-only controls");
        using (var frame = window.CaptureRenderedFrame() ?? throw new Exception("No managed disk frame."))
            frame.Save(Path.Combine(output, "managed-disk.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        Select("DiskMode", 1);
        Control<TextBox>("DiskImagePath").Text = @"C:\Images\Backing.vhdx";
        Control<NumericUpDown>("DiskCacheMiB").Value = 64;
        Check(window.Definition().Cache?.BudgetMiB == 64 && window.Definition().CapacityBytes == 1024 * ManagedDiskDefinition.MiB, "VHDX cache size is independent of virtual capacity");
        Check(Control<NumericUpDown>("DiskCacheMiB").IsEffectivelyVisible && !Control<TextBox>("DiskCheckpointDirectory").IsEffectivelyVisible, "backed mode uses cache controls without checkpoint policy");
        Select("DiskWriteBehaviour", 1);
        Click("CreateManagedDisk");
        Check(service.Creates == 0 && Text("volatile"), "Fast creation requires existing cache volatility consent");
        Control<CheckBox>("DiskVolatility").IsChecked = true;
        Check(window.Definition().Cache?.Preset == CachePreset.Fast && window.Definition().AcceptVolatileWrites, "Fast acceptance binds the shared configuration contract");
        Select("DiskMode", 2);
        Control<TextBox>("DiskImagePath").Text = @"C:\Images\Source.vhdx";
        Control<TextBox>("DiskCheckpointDirectory").Text = @"C:\Images\Checkpoints";
        Check(Control<CheckBox>("DiskSaveBeforeStop").IsChecked == true && Control<CheckBox>("DiskShutdownSave").IsChecked != true, "full image defaults to save before stop and optional shutdown save");
        Check(!Control<NumericUpDown>("DiskCacheMiB").IsEffectivelyVisible && Text("every logical sector") && Text("last committed image"), "whole-image mode shows full-load and committed startup semantics");
        Select("DiskSource", 1);
        Check(!Control<Button>("CreateManagedDisk").IsEnabled && !Control<NumericUpDown>("DiskCapacityMiB").IsEffectivelyVisible, "import requires inspection and cannot override image capacity");
        Click("InspectDiskImage");
        Dispatcher.UIThread.RunJobs();
        var imported = window.Definition();
        imported.Validate();
        Check(imported.CapacityBytes == service.VirtualBytes && imported.SectorBytes == 4096 && imported.Cache is null, "import binds native virtual capacity and geometry, not sparse file size");
        Check(Text("no automatic formatting") && Text("4,096 MiB") && !Control<TextBox>("DiskLabel").IsEffectivelyVisible, "existing image is preserved and full RAM payload is visible");
        Control<CheckBox>("DiskInitializeRaw").IsChecked = true;
        var raw = window.Definition(); raw.Validate();
        Check(raw.InitializeBlankImage && raw.ExpectedBlankImage is not null && Control<TextBox>("DiskLabel").IsEffectivelyVisible &&
            Text("every logical sector is blank"), "explicit RAW flow binds inspected identity and exposes formatting intent");
        Control<CheckBox>("DiskReadOnly").IsChecked = true;
        Check(window.Definition().ReadOnly && !window.Definition().SaveBeforeStopping && !window.Definition().SaveDuringShutdown &&
            !window.Definition().InitializeBlankImage, "read-only RAM copy disables initialization and automatic saves");
        Control<TextBox>("DiskImagePath").Text = @"C:\Images\Replacement.vhdx";
        Check(window.Definition().CapacityBytes == 0, "stale image metadata cannot bind a changed path before UI events settle");
        Dispatcher.UIThread.RunJobs();
        Check(!Control<Button>("CreateManagedDisk").IsEnabled && window.Definition().CapacityBytes == 0, "changing source invalidates inspected identity and capacity");
        Click("InspectDiskImage");
        Dispatcher.UIThread.RunJobs();
        Click("CreateManagedDisk");
        Dispatcher.UIThread.RunJobs();
        Check(service.Creates == 1 && service.Created?.ReadOnly == true && service.Created.Source == ManagedDiskSource.OpenExisting, "wizard submits one typed request through shared operations");

        var unavailable = new ManagedDiskWindow(new Fixture { Available = false });
        unavailable.Show(); Dispatcher.UIThread.RunJobs();
        foreach (var mode in Enum.GetValues<ManagedDiskMode>())
        {
            unavailable.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "DiskMode").SelectedIndex = (int)mode;
            Dispatcher.UIThread.RunJobs();
            Check(!unavailable.GetVisualDescendants().OfType<Button>().Single(c => c.Name == "CreateManagedDisk").IsEnabled, "unqualified backend cannot activate " + mode);
        }
        Check(unavailable.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Native provider qualification is required."), "unavailable backend explains its actual capability gate");
        unavailable.Close();
        var failureService = new Fixture { FailCreation = true };
        var failure = new ManagedDiskWindow(failureService);
        failure.Show(); Dispatcher.UIThread.RunJobs();
        failure.GetVisualDescendants().OfType<Button>().Single(c => c.Name == "CreateManagedDisk").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Check(failure.IsVisible && failure.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Native reservation refused."), "native creation failure stays visible without reporting a ready disk");
        failure.Close();
        Console.WriteLine("Managed-disk UI contracts passed; no real disk operations performed.");
    }

    private sealed class Fixture : IManagedDiskService
    {
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
            Creates++; Created = definition;
            return FailCreation ? Task.FromException<ManagedDiskRuntime>(new IOException("Native reservation refused.")) :
                Task.FromResult(new ManagedDiskRuntime(definition.ResourceId, Guid.NewGuid(), 1, definition.Mode, ManagedDiskState.Ready, 1, 1, "R:"));
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); }
}
