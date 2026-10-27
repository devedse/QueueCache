using Avalonia.Controls;
using Avalonia.Interactivity;
using QueueCache.Desktop.ViewModels;

namespace QueueCache.Desktop.Views;

/// <summary>Closes with a <see cref="CacheSettingsResult"/> when the settings are valid, or null.</summary>
public sealed partial class CacheSettingsWindow : Window
{
    public CacheSettingsWindow()
    {
        InitializeComponent();
        Opened += (_, _) => this.FitToScreen();
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CacheSettingsViewModel editor && editor.Result() is { } result)
            Close(result);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnOrderingHelp(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control)
            ToolTip.SetIsOpen(control, true);
    }

    private void OnOrderingHelpLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control)
            ToolTip.SetIsOpen(control, false);
    }
}
