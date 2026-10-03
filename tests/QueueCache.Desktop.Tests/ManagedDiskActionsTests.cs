using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QueueCache.Desktop;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;

internal sealed class EmptyManagedFixture : IManagedDiskService
{
    public Task<IReadOnlyList<ManagedDiskCapability>> CapabilitiesAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<ManagedDiskCapability>>([]);
    public Task<IReadOnlyList<ManagedDiskRecord>> ListAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<ManagedDiskRecord>>([]);
    public Task<ImageInspection> InspectAsync(string path, CancellationToken token = default) => throw new NotSupportedException();
    public Task<ManagedDiskRuntime> CreateAsync(ManagedDiskDefinition definition, IProgress<ManagedDiskProgress>? progress = null, CancellationToken token = default) => throw new NotSupportedException();
}
internal static class ManagedDiskActionsTests
{
    public static void Run(string output)
    {
        var definition = ManagedDiskDefinition.New(ManagedDiskMode.ImageInRam) with { ImagePath = @"C:\Images\source.vhdx", CheckpointDirectory = @"C:\Images\Checkpoints" };
        var runtime = new ManagedDiskRuntime(definition.ResourceId, Guid.NewGuid(), 7, definition.Mode, ManagedDiskState.Ready, 12, 9, "R:");
        var record = new ManagedDiskRecord(definition, runtime, VolumePath: @"\\?\Volume{00000000-0000-0000-0000-000000000003}\");
        var fixture = new ManagedFixture(record);
        var stop = new ManagedDiskActionWindow(fixture, record, ManagedDiskAction.Stop); stop.Show(); Dispatcher.UIThread.RunJobs();
        Check(stop.Request().StopIntent == ManagedDiskStopIntent.SaveThenStop && !stop.Request().AcceptDiscard, "image stop defaults to save, without implicit discard consent");
        var choice = Find<ComboBox>(stop, "ManagedStopIntent"); choice.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
        Reject(() => stop.Request());
        Find<CheckBox>(stop, "ManagedEraseAcknowledgement").IsChecked = true;
        Check(stop.Request().AcceptDiscard && stop.Request().Expected?.WriteGeneration == 12, "discard intent carries the displayed exact RAM generation");
        Find<Button>(stop, "ApplyManagedAction").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        Check(fixture.Request?.StopIntent == ManagedDiskStopIntent.DiscardThenStop && fixture.Request.AcceptDiscard, "stop dialog uses shared broker request");
        var format = new ManagedDiskActionWindow(fixture, record, ManagedDiskAction.Format); format.Show(); Dispatcher.UIThread.RunJobs();
        Reject(() => format.Request()); Find<CheckBox>(format, "ManagedEraseAcknowledgement").IsChecked = true;
        Check(format.Request().AcceptErase && format.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("RAM copy") == true && t.Text.Contains("imported image remains unchanged")), "format acknowledgement explains image-in-RAM source preservation");
        format.Close();
        var export = new ManagedDiskActionWindow(fixture, record, ManagedDiskAction.Export); export.Show(); Dispatcher.UIThread.RunJobs();
        Find<TextBox>(export, "ManagedActionPath").Text = @"C:\Images\export.vhdx";
        Check(!export.Request().CommitExport, "export preserves startup source by default"); Find<CheckBox>(export, "ManagedExportCommit").IsChecked = true;
        Check(export.Request().CommitExport, "export changes startup source only by explicit choice"); export.Close();
        var pure = ManagedDiskDefinition.New(ManagedDiskMode.EphemeralRam);
        var stopped = new ManagedDiskRecord(pure, new(pure.ResourceId, Guid.NewGuid(), 1, pure.Mode, ManagedDiskState.Stopped, 0, null));
        var settings = new ManagedDiskActionWindow(fixture, stopped, ManagedDiskAction.ConfigureStopped); settings.Show(); Dispatcher.UIThread.RunJobs();
        Find<TextBox>(settings, "ManagedPreferredLetter").Text = "S";
        Find<NumericUpDown>(settings, "ManagedStoppedCapacityMiB").Value = 2048;
        Check(settings.Request().PreferredLetter == 'S' && settings.Request().CapacityBytes == 2048 * ManagedDiskDefinition.MiB,
            "stopped settings submit the next pure-RAM recipe without erase or live resize"); settings.Close();
        var dashboard = new MainWindow(new VolumeFixture(record.VolumePath!), fixture); dashboard.Show(); Dispatcher.UIThread.RunJobs();
        var labels = dashboard.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
        Check(labels.Any(t => t?.Contains("Unsaved RAM changes") == true) && labels.Any(t => t?.Contains("Entire VHDX in RAM") == true), "managed dashboard shows typed mode and unsaved generation");
        Check(!dashboard.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Content, "Add cache")), "managed RAM volume does not get a duplicate ordinary-cache card");
        Check(dashboard.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Content, "Save image") && b.IsVisible && b.IsEnabled), "ready image exposes save independently of flush");
        using (var frame = dashboard.CaptureRenderedFrame() ?? throw new Exception("No managed dashboard frame.")) frame.Save(Path.Combine(output, "managed-dashboard.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        fixture.Record = record with { Runtime = runtime with { State = ManagedDiskState.RecoveryRequired }, LastError = "Frozen checkpoint needs reconciliation." };
        ((Task)typeof(MainWindow).GetMethod("SampleManagedDisks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(dashboard, null)!).GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
        Check(dashboard.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Content, "Recover disk") && b.IsEnabled) &&
              dashboard.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Content, "Save image") && !b.IsEnabled), "recovery-required disk offers reconciliation and blocks another save");
        dashboard.Close();
        Console.WriteLine("Managed disk action/dashboard contracts passed.");
    }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static void Check(bool good, string why) { if (!good) throw new Exception(why); }
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Destructive action accepted without acknowledgement."); }
    private sealed class ManagedFixture(ManagedDiskRecord record) : IManagedDiskService
    {
        public ManagedDiskRecord Record = record; public ManagedDiskRequest? Request;
        public Task<IReadOnlyList<ManagedDiskRecord>> ListAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<ManagedDiskRecord>>([Record]);
        public Task<IReadOnlyList<ManagedDiskCapability>> CapabilitiesAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<ManagedDiskCapability>>([]);
        public Task<ImageInspection> InspectAsync(string path, CancellationToken token = default) => throw new NotSupportedException();
        public Task<ManagedDiskRuntime> CreateAsync(ManagedDiskDefinition definition, IProgress<ManagedDiskProgress>? progress = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task<ManagedDiskOperationResult> ExecuteAsync(ManagedDiskRequest request, IProgress<ManagedDiskProgress>? progress = null, CancellationToken token = default)
        { Request = request; return Task.FromResult(new ManagedDiskOperationResult(Record, "Intent recorded by fake broker.")); }
    }
    private sealed class VolumeFixture(string path) : ICacheTaskService
    {
        public Task<IReadOnlyList<VolumeDescription>> ListAsync() => Task.FromResult<IReadOnlyList<VolumeDescription>>([new("R:", "QueueCache", "NTFS", 1L << 30, path, 8, "RAM", "managed-ram", 1L << 30, false, false, false)]);
        public Task<QueueCache.Management.WriteCacheState> ReadAsync(VolumeDescription volume) => throw new Exception("Owned RAM must not be sampled as an ordinary cache.");
        public bool IsPersistent(VolumeDescription volume) => false;
        public Task SaveAsync(VolumeDescription volume, CacheConfiguration configuration, bool persistent, IProgress<string> progress) => throw new NotSupportedException();
        public Task SetEnabledAsync(VolumeDescription volume, bool enabled, bool persistent) => throw new NotSupportedException();
        public Task FlushAsync(VolumeDescription volume) => throw new NotSupportedException();
        public Task DropCleanAsync(VolumeDescription volume) => throw new NotSupportedException();
        public Task RemoveAsync(VolumeDescription volume) => throw new NotSupportedException();
        public Task<WorkloadReport> TestAsync(string volume, bool benchmark, IProgress<string> progress, CancellationToken token) => throw new NotSupportedException();
    }
}
