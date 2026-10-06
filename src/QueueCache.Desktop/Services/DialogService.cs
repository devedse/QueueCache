using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using QueueCache.Desktop.ViewModels;
using QueueCache.Desktop.Views;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Desktop.Services;

/// <summary>Shows confirmations as Windows content dialogs and the editors as modal windows over the main window.</summary>
public sealed class DialogService(Func<Window?> owner) : IDialogService
{
    public async Task<string?> ConfirmAsync(Confirmation confirmation)
    {
        if (confirmation.Choices.Count is < 1 or > 2)
            throw new ArgumentException("A confirmation offers one or two choices besides Cancel.", nameof(confirmation));
        var destructive = confirmation.Choices.Any(c => c.Kind == ChoiceKind.Destructive);
        var dialog = new FAContentDialog
        {
            Title = confirmation.Title,
            Content = new TextBlock { Text = confirmation.Message, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 },
            PrimaryButtonText = confirmation.Choices[0].Text,
            SecondaryButtonText = confirmation.Choices.Count > 1 ? confirmation.Choices[1].Text : null,
            CloseButtonText = confirmation.CancelText,
            // Enter never confirms a choice that loses data.
            DefaultButton = destructive ? FAContentDialogButton.Close : FAContentDialogButton.Primary
        };
        dialog.TemplateApplied += (_, e) =>
        {
            string[] parts = ["PrimaryButton", "SecondaryButton"];
            for (var i = 0; i < confirmation.Choices.Count; i++)
                if (confirmation.Choices[i].Kind == ChoiceKind.Destructive && e.NameScope.Find<Button>(parts[i]) is { } button)
                    button.Classes.Add("danger");
        };
        var result = await dialog.ShowAsync(owner());
        return result switch
        {
            FAContentDialogResult.Primary => confirmation.Choices[0].Id,
            FAContentDialogResult.Secondary => confirmation.Choices[1].Id,
            _ => null
        };
    }

    public async Task<CacheSettingsResult?> EditCacheAsync(CacheSettingsViewModel editor)
    {
        var window = new CacheSettingsWindow { DataContext = editor };
        return await ShowAsync<CacheSettingsResult?>(window);
    }

    public async Task<ManagedDiskRuntime?> CreateDiskAsync(CreateDiskViewModel editor)
    {
        var window = new CreateDiskWindow { DataContext = editor };
        _ = editor.LoadAsync();
        return await ShowAsync<ManagedDiskRuntime?>(window);
    }

    public async Task<ManagedDiskOperationResult?> DiskActionAsync(DiskActionViewModel editor)
    {
        var window = new DiskActionWindow { DataContext = editor };
        return await ShowAsync<ManagedDiskOperationResult?>(window);
    }

    private async Task<T> ShowAsync<T>(Window window)
    {
        var parent = owner();
        if (parent is null)
            return default!;
        return await window.ShowDialog<T>(parent);
    }
}
