using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using QueueCache.Desktop.ViewModels;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Desktop.Views;

/// <summary>Closes with the created disk's <see cref="ManagedDiskRuntime"/>, or null. Cancel first
/// cancels a running inspection or creation; the window closes only when nothing runs.</summary>
public sealed partial class CreateDiskWindow : Window
{
    private static readonly FilePickerFileType Vhdx = new("Virtual hard disk (.vhdx)") { Patterns = ["*.vhdx"] };

    public CreateDiskWindow()
    {
        InitializeComponent();
        Opened += (_, _) => this.FitToScreen();
        Closing += (_, e) =>
        {
            if (Editor is { IsWorking: true } editor)
            {
                e.Cancel = true;
                editor.CancelOperation();
            }
        };
    }

    private CreateDiskViewModel? Editor => DataContext as CreateDiskViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Editor is { } editor)
            editor.Created += (_, runtime) => Close(runtime);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (Editor?.CancelOperation() != true)
            Close(null);
    }

    private async void OnBrowseImage(object? sender, RoutedEventArgs e)
    {
        if (Editor is not { } editor)
            return;
        IStorageFile? file;
        if (editor.UseExisting)
            file = (await StorageProvider.OpenFilePickerAsync(new() { Title = "Choose an image file", FileTypeFilter = [Vhdx] })).FirstOrDefault();
        else
            file = await StorageProvider.SaveFilePickerAsync(new() { Title = "Create the image file", FileTypeChoices = [Vhdx], DefaultExtension = "vhdx", SuggestedFileName = editor.Label });
        if (file?.TryGetLocalPath() is { } path)
            editor.ImagePath = path;
    }

    private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        if (Editor is not { } editor)
            return;
        var folder = (await StorageProvider.OpenFolderPickerAsync(new() { Title = "Choose the folder for saved copies" })).FirstOrDefault();
        if (folder?.TryGetLocalPath() is { } path)
            editor.CheckpointDirectory = path;
    }
}
