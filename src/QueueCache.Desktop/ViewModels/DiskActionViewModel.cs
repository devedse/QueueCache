using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Desktop.ViewModels;

/// <summary>Actions on a virtual disk that need input: export a copy, startup behavior, settings of a
/// stopped disk, deleting an image. The request carries the generation that was shown, so the
/// service refuses it if the disk changed meanwhile.</summary>
public sealed partial class DiskActionViewModel : ObservableObject
{
    private readonly IManagedDiskService service;
    private readonly ManagedDiskRecord record;
    private CancellationTokenSource? operation;

    public DiskActionViewModel(IManagedDiskService service, ManagedDiskRecord record, ManagedDiskAction action)
    {
        if (action is not (ManagedDiskAction.Export or ManagedDiskAction.SetStartup or ManagedDiskAction.ConfigureStopped or ManagedDiskAction.DeleteImage))
            throw new ArgumentException("This action has no form.", nameof(action));
        this.service = service;
        this.record = record;
        Action = action;
        var definition = record.Definition;
        var name = $"{definition.PreferredLetter}: {definition.Label}";
        (Title, ApplyText, Explanation) = action switch
        {
            ManagedDiskAction.Export => ($"Export a copy of {name}", "Export",
                "Writes a new .vhdx file with every sector of the disk, then checks it. Existing files and the current startup image are kept."),
            ManagedDiskAction.SetStartup => ($"Startup and shutdown for {name}", "Save",
                definition.StartupDescription + ". Saving during shutdown is an attempt: a forced shutdown or power cut can prevent it."),
            ManagedDiskAction.ConfigureStopped => ($"Settings for {name}", "Save",
                "Applies the next time the disk starts." + (definition.Mode == ManagedDiskMode.EphemeralRam ? " A RAM disk is created at the new size." : " Images are never resized or formatted here.")),
            _ => ($"Delete an image of {name}", "Delete image",
                "Permanently deletes one image file that QueueCache created for this disk and that nothing uses any more. Imported images, the startup image and images in use are refused.")
        };
        IsDestructive = action == ManagedDiskAction.DeleteImage;
        ShowPath = action is ManagedDiskAction.Export or ManagedDiskAction.DeleteImage;
        ShowCommit = action == ManagedDiskAction.Export;
        ShowStartup = action == ManagedDiskAction.SetStartup;
        ShowImageStartup = ShowStartup && definition.Mode == ManagedDiskMode.ImageInRam;
        CanSaveAutomatically = !definition.ReadOnly;
        ShowStoppedSettings = action == ManagedDiskAction.ConfigureStopped;
        ShowCapacity = ShowStoppedSettings && definition.Mode == ManagedDiskMode.EphemeralRam;
        ShowAccess = ShowStoppedSettings && definition.Mode != ManagedDiskMode.CachedVhdx;
        startWithWindows = definition.StartAtBoot;
        saveBeforeStop = definition.SaveBeforeStopping;
        saveDuringShutdown = definition.SaveDuringShutdown;
        label = definition.Label;
        letter = definition.PreferredLetter.ToString();
        capacityGiB = definition.CapacityBytes / (double)(1UL << 30);
        access = (int)definition.Access;
    }

    public ManagedDiskAction Action { get; }
    public string Title { get; }
    public string ApplyText { get; }
    public string Explanation { get; }
    public bool IsDestructive { get; }
    public bool ShowPath { get; }
    public bool ShowCommit { get; }
    public bool ShowStartup { get; }
    public bool ShowImageStartup { get; }
    public bool CanSaveAutomatically { get; }
    public bool ShowStoppedSettings { get; }
    public bool ShowCapacity { get; }
    public bool ShowAccess { get; }
    public IReadOnlyList<string> AccessChoices { get; } = ["Standard (full Windows disk stack)", "Direct (fastest: read and written straight from RAM)"];
    public string PathPlaceholder => Action == ManagedDiskAction.Export ? @"D:\Disks\Copy.vhdx" : @"D:\Disks\Old.vhdx";

    public event EventHandler<ManagedDiskOperationResult>? Completed;

    [ObservableProperty] private string path = "";
    [ObservableProperty] private bool commitExport;
    [ObservableProperty] private bool startWithWindows;
    [ObservableProperty] private bool saveBeforeStop;
    [ObservableProperty] private bool saveDuringShutdown;
    [ObservableProperty] private string label;
    [ObservableProperty] private string letter;
    [ObservableProperty] private double capacityGiB;
    [ObservableProperty] private int access;
    [ObservableProperty] private bool isWorking;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool statusIsError;

    public ManagedDiskRequest Request()
    {
        // A definition that never obtained a live identity can only be forgotten.
        if (record.Runtime is null)
            throw new IOException("QueueCache lost track of this disk's live identity. Recover it first.");
        var selectedPath = ShowPath ? ManagedDiskPaths.ValidateImagePath(Path) : null;
        var request = new ManagedDiskRequest(record.ResourceId, Action, ManagedDiskExpected.From(record.Runtime), null, selectedPath,
            CommitExport: ShowCommit && CommitExport,
            // The Delete image button states the consequence; pressing it is the acknowledgement.
            AcceptErase: Action == ManagedDiskAction.DeleteImage,
            StartAtBoot: ShowStartup ? StartWithWindows : null,
            SaveBeforeStopping: ShowImageStartup ? SaveBeforeStop : null,
            SaveDuringShutdown: ShowImageStartup ? SaveDuringShutdown : null,
            Label: ShowStoppedSettings ? Label : null,
            PreferredLetter: ShowStoppedSettings ? char.ToUpperInvariant((Letter ?? "").SingleOrDefault()) : null,
            CapacityBytes: ShowCapacity ? checked((ulong)Math.Round(CapacityGiB * 1024) * ManagedDiskDefinition.MiB) : null,
            Access: ShowAccess ? (RamAccess)Access : null);
        if (Action == ManagedDiskAction.ConfigureStopped)
            _ = ManagedDiskConfiguration.EditStopped(record, request);
        return request;
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (operation is not null)
            return;
        ManagedDiskRequest request;
        try
        {
            request = Request();
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            StatusIsError = true;
            return;
        }
        operation = new();
        IsWorking = true;
        StatusIsError = false;
        ManagedDiskOperationResult? result = null;
        try
        {
            result = await service.ExecuteAsync(request, new Progress(text => Status = text), operation.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled. Check the disk's state before trying again.";
            StatusIsError = true;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            StatusIsError = true;
        }
        finally
        {
            operation.Dispose();
            operation = null;
            IsWorking = false;
        }
        if (result is not null)
            Completed?.Invoke(this, result);
    }

    public bool CancelOperation()
    {
        if (operation is null)
            return false;
        operation.Cancel();
        return true;
    }

    private sealed class Progress(Action<string> update) : IProgress<ManagedDiskProgress>
    {
        public void Report(ManagedDiskProgress value)
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                update(value.Message);
            else
                Avalonia.Threading.Dispatcher.UIThread.Post(() => update(value.Message));
        }
    }
}
