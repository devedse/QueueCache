using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Desktop;

/// <summary>Explicit intent and selected generation are captured together; the broker rechecks ownership.</summary>
[SupportedOSPlatform("windows")]
public sealed class ManagedDiskActionWindow : Window
{
    private readonly IManagedDiskService service;
    private readonly ManagedDiskRecord record;
    private readonly ManagedDiskAction action;
    private readonly TextBox path = new() { Name = "ManagedActionPath", PlaceholderText = @"C:\Images\Export.vhdx" };
    private readonly TextBox label = new() { Name = "ManagedFormatLabel" };
    private readonly CheckBox acknowledge = new() { Name = "ManagedEraseAcknowledgement" };
    private readonly CheckBox commit = new() { Name = "ManagedExportCommit", Content = "Use the verified export as the future startup image" };
    private readonly ComboBox stop = new() { Name = "ManagedStopIntent" };
    private readonly CheckBox startup = new() { Name = "ManagedStartAtBoot", Content = "Start with Windows" };
    private readonly CheckBox saveStop = new() { Name = "ManagedSaveBeforeStop", Content = "Save before stopping" };
    private readonly CheckBox shutdown = new() { Name = "ManagedSaveDuringShutdown", Content = "Attempt save during orderly shutdown (best effort)" };
    private readonly TextBlock status = MainWindow.Text("", 13, MainWindow.Muted);
    private readonly StackPanel form = new() { Spacing = 12 };
    private readonly Button apply = new() { Name = "ApplyManagedAction", Background = MainWindow.Accent, Foreground = Brushes.White };
    private readonly Button cancel = new() { Content = "Cancel" };
    private CancellationTokenSource? operation;

    public ManagedDiskActionWindow(IManagedDiskService service, ManagedDiskRecord record, ManagedDiskAction action)
    {
        this.service = service; this.record = record; this.action = action;
        Title = action switch { ManagedDiskAction.Stop => "Stop disk", ManagedDiskAction.Format => "Format disk", ManagedDiskAction.Export => "Export image",
            ManagedDiskAction.SetStartup => "Startup settings", ManagedDiskAction.RemoveDefinition => "Forget disk", ManagedDiskAction.DeleteImage => "Delete owned image", _ => throw new ArgumentException("This operation needs no intent dialog.") };
        Icon = AppBranding.CreateIcon(); Width = 650; Height = 550; MinWidth = 560; MinHeight = 430;
        Background = Brushes.White; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new StackPanel { Margin = new Thickness(28), Spacing = 16 };
        body.Children.Add(MainWindow.Text(Title, 25, MainWindow.Ink, FontWeight.SemiBold));
        body.Children.Add(MainWindow.Text($"{record.Definition.PreferredLetter}: {record.Definition.Label}\nResource {record.ResourceId}\nCreation {record.Runtime?.CreationGeneration}; RAM generation {record.Runtime?.WriteGeneration}", 13, MainWindow.Muted));
        var hint = action switch
        {
            ManagedDiskAction.Stop when record.Definition.Mode == ManagedDiskMode.CachedVhdx => "Windows must lock the owned volume. Pending cache writes drain to the VHDX before detach; open files can veto the operation.",
            ManagedDiskAction.Stop when record.Definition.Mode == ManagedDiskMode.EphemeralRam => "All contents of this RAM creation will be lost. Windows must lock the volume first; open files can veto stopping.",
            ManagedDiskAction.Stop => "Save and stop copies and verifies the complete frozen disk before committing a checkpoint. Discard loses unsaved RAM changes and keeps the previous image. Open files can veto either operation.",
            ManagedDiskAction.Format when record.Definition.Mode == ManagedDiskMode.ImageInRam => "Erase and format this RAM copy as NTFS. The imported image remains unchanged; save a checkpoint to retain the formatted copy.",
            ManagedDiskAction.Format => "Erase and format only the exact owned disk volume as NTFS. Open files can veto formatting.",
            ManagedDiskAction.Export => "Write a NEW standalone VHDX containing every logical sector of the frozen RAM disk, then verify it. Existing files and the previous startup image are preserved.",
            ManagedDiskAction.SetStartup => record.Definition.StartupDescription + ". Shutdown saving is an attempt and cannot guarantee persistence during forced shutdown or power loss.",
            ManagedDiskAction.RemoveDefinition => "Forget this stopped disk's recipe. Image files and recovery evidence are retained.",
            _ => "Delete only an unreferenced image created for this managed resource. Imported, retained, mounted and in-flight images are refused."
        };
        body.Children.Add(MainWindow.Text(hint, 14, MainWindow.Ink));
        path.IsVisible = action is ManagedDiskAction.Export or ManagedDiskAction.DeleteImage;
        label.IsVisible = action == ManagedDiskAction.Format; label.Text = record.Definition.Label;
        commit.IsVisible = action == ManagedDiskAction.Export;
        var ramStop = action == ManagedDiskAction.Stop && record.Definition.Mode != ManagedDiskMode.CachedVhdx;
        stop.IsVisible = ramStop && record.Definition.Mode == ManagedDiskMode.ImageInRam;
        stop.ItemsSource = new[] { "Save verified image, then stop", "Discard unsaved RAM changes, then stop" };
        stop.SelectedIndex = record.Definition.SaveBeforeStopping ? 0 : 1;
        acknowledge.Content = action == ManagedDiskAction.Format ? "Erase the selected volume's contents" : action == ManagedDiskAction.DeleteImage
            ? "Delete the selected unreferenced owned image" : "Discard this RAM creation's contents/unsaved changes";
        acknowledge.IsVisible = action is ManagedDiskAction.Format or ManagedDiskAction.DeleteImage || ramStop && (record.Definition.Mode == ManagedDiskMode.EphemeralRam || stop.SelectedIndex == 1);
        stop.SelectionChanged += (_, _) => { acknowledge.IsChecked = false; acknowledge.IsVisible = stop.SelectedIndex == 1; };
        startup.IsVisible = action == ManagedDiskAction.SetStartup; startup.IsChecked = record.Definition.StartAtBoot;
        saveStop.IsVisible = shutdown.IsVisible = action == ManagedDiskAction.SetStartup && record.Definition.Mode == ManagedDiskMode.ImageInRam;
        saveStop.IsChecked = record.Definition.SaveBeforeStopping; shutdown.IsChecked = record.Definition.SaveDuringShutdown;
        saveStop.IsEnabled = shutdown.IsEnabled = !record.Definition.ReadOnly;
        foreach (var control in new Control[] { path, label, commit, stop, acknowledge, startup, saveStop, shutdown }) form.Children.Add(control);
        body.Children.Add(form); body.Children.Add(status);
        apply.Content = Title;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        buttons.Children.Add(cancel); buttons.Children.Add(apply); body.Children.Add(buttons);
        Content = new ScrollViewer { Content = body };
        apply.Click += async (_, _) => await ApplyAsync();
        cancel.Click += (_, _) => { if (operation is not null) operation.Cancel(); else Close(null); };
        Closing += (_, e) => { if (operation is not null) { e.Cancel = true; operation.Cancel(); } };
    }
    public ManagedDiskRequest Request()
    {
        if (acknowledge.IsVisible && acknowledge.IsChecked != true) throw new ArgumentException("Acknowledge the selected erase/discard before continuing.");
        if (record.Runtime is null) throw new IOException("Refresh/recover this managed disk's runtime identity first.");
        var intent = action != ManagedDiskAction.Stop ? (ManagedDiskStopIntent?)null : record.Definition.Mode switch
        {
            ManagedDiskMode.CachedVhdx => ManagedDiskStopIntent.DrainThenDetach,
            ManagedDiskMode.EphemeralRam => ManagedDiskStopIntent.DiscardThenStop,
            _ => stop.SelectedIndex == 0 ? ManagedDiskStopIntent.SaveThenStop : ManagedDiskStopIntent.DiscardThenStop
        };
        var selectedPath = path.IsVisible ? ManagedDiskPaths.ValidateImagePath(path.Text) : null;
        return new(record.ResourceId, action, ManagedDiskExpected.From(record.Runtime), intent, selectedPath,
            CommitExport: commit.IsVisible && commit.IsChecked == true, AcceptDiscard: intent == ManagedDiskStopIntent.DiscardThenStop && acknowledge.IsChecked == true,
            AcceptErase: action is ManagedDiskAction.Format or ManagedDiskAction.DeleteImage && acknowledge.IsChecked == true,
            StartAtBoot: startup.IsVisible ? startup.IsChecked == true : null, SaveBeforeStopping: saveStop.IsVisible ? saveStop.IsChecked == true : null,
            SaveDuringShutdown: shutdown.IsVisible ? shutdown.IsChecked == true : null, Label: label.IsVisible ? label.Text : null);
    }
    private async Task ApplyAsync()
    {
        if (operation is not null) return;
        ManagedDiskRequest request;
        try { request = Request(); } catch (Exception ex) { status.Text = ex.Message; return; }
        operation = new(); form.IsEnabled = apply.IsEnabled = false; cancel.Content = "Cancel operation";
        ManagedDiskOperationResult? result = null; string? error = null;
        try { result = await service.ExecuteAsync(request, new UiProgress(text => status.Text = text), operation.Token); }
        catch (OperationCanceledException) { error = "Cancellation completed. Refresh the disk to see its actual state before retrying."; }
        catch (Exception ex) { error = ex.Message; }
        finally { operation.Dispose(); operation = null; form.IsEnabled = apply.IsEnabled = true; cancel.Content = "Cancel"; }
        if (error is not null) status.Text = error; else Close(result);
    }
    private sealed class UiProgress(Action<string> update) : IProgress<ManagedDiskProgress>
    {
        public void Report(ManagedDiskProgress value)
        { if (Dispatcher.UIThread.CheckAccess()) update(value.Message); else Dispatcher.UIThread.Post(() => update(value.Message)); }
    }
}
