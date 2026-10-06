using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using QueueCache.Desktop.ViewModels;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Desktop.Views;

/// <summary>Closes with the <see cref="ManagedDiskOperationResult"/>, or null. Cancel first cancels a
/// running operation; the window closes only when nothing runs.</summary>
public sealed partial class DiskActionWindow : Window
{
    private static readonly FilePickerFileType Vhdx = new("Virtual hard disk (.vhdx)") { Patterns = ["*.vhdx"] };

    public DiskActionWindow()
    {
        InitializeComponent();
        Closing += (_, e) =>
        {
            if (Editor is { IsWorking: true } editor)
            {
                e.Cancel = true;
                editor.CancelOperation();
            }
        };
    }

    private DiskActionViewModel? Editor => DataContext as DiskActionViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Editor is { } editor)
            editor.Completed += (_, result) => Close(result);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (Editor?.CancelOperation() != true)
            Close(null);
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (Editor is not { } editor)
            return;
        IStorageFile? file = editor.Action == ManagedDiskAction.Export
            ? await StorageProvider.SaveFilePickerAsync(new() { Title = "Export a copy", FileTypeChoices = [Vhdx], DefaultExtension = "vhdx" })
            : (await StorageProvider.OpenFilePickerAsync(new() { Title = "Choose the image to delete", FileTypeFilter = [Vhdx] })).FirstOrDefault();
        if (file?.TryGetLocalPath() is { } path)
            editor.Path = path;
    }
}
