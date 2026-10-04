using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using QueueCache.Management;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Desktop;

public sealed partial class MainWindow
{
    private readonly StackPanel managedCards = new() { Spacing = 16 };
    private readonly TextBlock managedStatus = Text("Discovering managed disks…", 13, Muted);
    private readonly Dictionary<Guid, ManagedView> managedViews = new();
    private bool samplingManaged;
    private sealed class ManagedView(ManagedDiskRecord record)
    {
        public ManagedDiskRecord Record = record;
        public readonly TextBlock Name = Text("", 22, Ink, Avalonia.Media.FontWeight.SemiBold), Status = Text("", 14, Ink), Detail = Text("", 13, Muted);
        public readonly Dictionary<ManagedDiskAction, Button> Actions = new();
    }
    private async Task SampleManagedDisks()
    {
        if (samplingManaged || closed) return;
        samplingManaged = true;
        try
        {
            var records = await managedDisks.ListAsync();
            if (closed) return;
            var ids = records.Select(r => r.ResourceId).ToHashSet();
            var oldVolumes = managedViews.Values.Select(v => v.Record.VolumePath).Where(v => v is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newVolumes = records.Select(v => v.VolumePath).Where(v => v is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!oldVolumes.SetEquals(newVolumes)) nextInventory = DateTimeOffset.MinValue;
            if (!ids.SetEquals(managedViews.Keys))
            {
                managedCards.Children.Clear(); managedViews.Clear();
                foreach (var record in records)
                {
                    var view = new ManagedView(record); managedViews.Add(record.ResourceId, view);
                    managedCards.Children.Add(BuildManagedCard(view));
                }
            }
            foreach (var record in records) { managedViews[record.ResourceId].Record = record; UpdateManagedCard(managedViews[record.ResourceId]); }
            managedStatus.Text = records.Count == 0 ? "No managed disks. Choose Create disk to add one." : "";
        }
        catch (Exception ex)
        {
            managedStatus.Text = "Managed disk state unavailable: " + ex.Message;
            foreach (var view in managedViews.Values)
            { view.Status.Text = "State unavailable"; foreach (var action in view.Actions.Values) action.IsEnabled = false; }
        }
        finally { samplingManaged = false; }
    }
    private Control BuildManagedCard(ManagedView view)
    {
        var body = new StackPanel { Spacing = 12, Margin = new Thickness(22) };
        body.Children.Add(view.Name); body.Children.Add(view.Status); body.Children.Add(view.Detail);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var pair in new[] { (ManagedDiskAction.Start, "Start disk"), (ManagedDiskAction.Stop, "Stop disk"), (ManagedDiskAction.Flush, "Flush disk"),
            (ManagedDiskAction.Save, "Save image"), (ManagedDiskAction.Export, "Export image"), (ManagedDiskAction.Format, "Format disk"),
            (ManagedDiskAction.ChangeCache, "Cache settings"), (ManagedDiskAction.SetStartup, "Startup settings"),
            (ManagedDiskAction.ConfigureStopped, "Stopped disk settings"), (ManagedDiskAction.RemoveDefinition, "Forget disk"), (ManagedDiskAction.DeleteImage, "Delete owned image"), (ManagedDiskAction.Recover, "Recover disk") })
        {
            var action = pair.Item1;
            var button = Action(pair.Item2, async () =>
            {
                if (action == ManagedDiskAction.ChangeCache) await EditManagedCache(view.Record);
                else if (action is ManagedDiskAction.Start or ManagedDiskAction.Flush or ManagedDiskAction.Save or ManagedDiskAction.Recover)
                {
                    var expected = action == ManagedDiskAction.Recover ? null : ManagedDiskExpected.From(view.Record.Runtime!);
                    var result = await managedDisks.ExecuteAsync(new(view.Record.ResourceId, action, expected), new ManagedUiProgress(text => message.Text = text));
                    message.Text = result.Message;
                }
                else
                {
                    var result = await new ManagedDiskActionWindow(managedDisks, view.Record, action).ShowDialog<ManagedDiskOperationResult?>(this);
                    if (result is not null) message.Text = result.Message;
                }
                await SampleManagedDisks(); await Refresh();
            });
            button.Name = $"Managed{action}-{view.Record.ResourceId:N}"; button.Margin = new Thickness(0, 0, 8, 8);
            view.Actions.Add(action, button); actions.Children.Add(button);
        }
        body.Children.Add(actions);
        return new Border { Background = Brushes.White, CornerRadius = new CornerRadius(12), Child = body };
    }
    private void UpdateManagedCard(ManagedView view)
    {
        var record = view.Record; var definition = record.Definition; var runtime = record.Runtime;
        var mode = definition.Mode switch { ManagedDiskMode.EphemeralRam => "Pure RAM disk", ManagedDiskMode.CachedVhdx => "VHDX with RAM cache", _ => "Entire VHDX in RAM" };
        view.Name.Text = $"{definition.PreferredLetter}: {definition.Label} · {mode}";
        view.Status.Text = runtime is null ? "Runtime identity unavailable · Recover required" :
            runtime.State + (runtime.Mode != ManagedDiskMode.ImageInRam ? "" : runtime.State == ManagedDiskState.Stopped ?
                record.CommittedImage is null ? " · No committed image" : " · Only the committed image is retained" :
                runtime.State is not (ManagedDiskState.Ready or ManagedDiskState.Saving) ? " · RAM state needs reconciliation" :
                runtime.HasUnsavedChanges ? " · Unsaved RAM changes" : " · Saved generation");
        view.Detail.Text = $"{definition.CapacityBytes / ManagedDiskDefinition.MiB:N0} MiB disk" +
            (definition.Cache is not null ? $" · {definition.Cache.BudgetMiB:N0} MiB cache · {definition.Cache.Preset}" : " · full capacity reserved in RAM") +
            (record.Native is null ? "" : $"\nActual reserved RAM: {record.Native.ReservedBytes / ManagedDiskDefinition.MiB:N0} MiB · read {record.Native.ReadBytes:N0} bytes · written {record.Native.WriteBytes:N0} bytes · flushes {record.Native.Flushes:N0} · errors {record.Native.Errors:N0}") +
            $"\n{(definition.StartAtBoot ? definition.StartupDescription : "Automatic startup off; definition remembered")}." +
            (definition.Mode == ManagedDiskMode.EphemeralRam ? "\nContents are temporary; stopping or a new Windows startup loses them." : "") +
            (record.CommittedImage is null ? "" : $"\nStartup image: {record.CommittedImage.Identity.Path}\nLast committed save: {record.SavedAt?.ToString("u") ?? "imported source; no checkpoint yet"}. Flush stays in RAM; Save image commits a full checkpoint.") +
            (record.LastError is null ? "" : "\n" + record.LastError) + $"\nResource: {record.ResourceId}";
        var ready = runtime?.State == ManagedDiskState.Ready; var stopped = runtime?.State == ManagedDiskState.Stopped;
        foreach (var pair in view.Actions)
        {
            pair.Value.IsVisible = pair.Key switch
            {
                ManagedDiskAction.Save or ManagedDiskAction.Export => definition.Mode == ManagedDiskMode.ImageInRam,
                ManagedDiskAction.ChangeCache => definition.Mode == ManagedDiskMode.CachedVhdx,
                ManagedDiskAction.DeleteImage => definition.Mode != ManagedDiskMode.EphemeralRam,
                _ => true
            };
            pair.Value.IsEnabled = !busy && pair.Key switch
            {
                ManagedDiskAction.Start => stopped,
                ManagedDiskAction.Stop => runtime is not null && !stopped,
                ManagedDiskAction.Flush or ManagedDiskAction.Save or ManagedDiskAction.Export => ready,
                ManagedDiskAction.Format => runtime is not null && runtime.State is ManagedDiskState.Ready or ManagedDiskState.RecoveryRequired &&
                    record.VolumePath is not null && !definition.ReadOnly,
                ManagedDiskAction.RemoveDefinition => stopped || runtime is null,
                ManagedDiskAction.ConfigureStopped => stopped,
                ManagedDiskAction.ChangeCache => ready || stopped,
                ManagedDiskAction.SetStartup or ManagedDiskAction.DeleteImage => runtime is not null,
                ManagedDiskAction.Recover => runtime is null || runtime.State is ManagedDiskState.Blocked or ManagedDiskState.RecoveryRequired or ManagedDiskState.Faulted,
                _ => false
            };
        }
    }
    private async Task EditManagedCache(ManagedDiskRecord record)
    {
        var definition = record.Definition;
        var descriptor = (await service.ListAsync()).SingleOrDefault(v => record.VolumePath?.Equals(v.VolumePath, StringComparison.OrdinalIgnoreCase) == true)
            ?? new VolumeDescription(definition.PreferredLetter + ":", definition.Label, "NTFS", checked((long)definition.CapacityBytes),
                record.VolumePath ?? @"\\?\Volume{00000000-0000-0000-0000-000000000000}\", record.PhysicalDiskNumber ?? 0, "Managed VHDX", "remembered managed image", checked((long)definition.CapacityBytes), false, false, false);
        var result = await new CacheSettingsWindow(descriptor, definition.Cache!, MemoryBudget.MaximumMiB).ShowDialog<CacheSettingsResult?>(this);
        if (result is null) return;
        var changed = await managedDisks.ExecuteAsync(new(record.ResourceId, ManagedDiskAction.ChangeCache, ManagedDiskExpected.From(record.Runtime!),
            Cache: result.Configuration, AcceptVolatileWrites: result.Configuration.Preset == CachePreset.Fast), new ManagedUiProgress(text => message.Text = text));
        message.Text = changed.Message;
    }
    private sealed class ManagedUiProgress(Action<string> update) : IProgress<ManagedDiskProgress>
    {
        public void Report(ManagedDiskProgress progress)
        {
            var text = progress.Message + (progress.TotalBytes is > 0 ? $" ({progress.CompletedBytes:N0}/{progress.TotalBytes:N0} bytes)" : "");
            if (Dispatcher.UIThread.CheckAccess()) update(text); else Dispatcher.UIThread.Post(() => update(text));
        }
    }
}
