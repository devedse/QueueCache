using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using QueueCache.Desktop.ViewModels;

namespace QueueCache.Desktop.Views;

/// <summary>The shell window. View concerns only: navigation selection, the Mica backdrop, and
/// hiding to the notification area instead of closing.</summary>
public sealed partial class MainWindow : Window
{
    private bool exiting;

    public MainWindow()
    {
        InitializeComponent();
        Navigation.SelectionChanged += (_, e) =>
        {
            if (Shell is { } shell && e.SelectedItem is FANavigationViewItem { Tag: string tag } && Enum.TryParse<AppPage>(tag, out var page))
                shell.Page = page;
        };
        Opened += (_, _) =>
        {
            this.FitToScreen();
            SelectNavigation();
        };
        Closing += OnClosing;
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Shell is { } shell)
        {
            shell.PropertyChanged += OnShellChanged;
            shell.SetWindowVisible(IsVisible);
        }
        SelectNavigation();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
            Shell?.SetWindowVisible(IsVisible);
        // Mica shows through only when Windows provides it; otherwise keep the solid base color.
        if (change.Property == ActualTransparencyLevelProperty)
        {
            if (ActualTransparencyLevel == WindowTransparencyLevel.Mica)
                Background = Brushes.Transparent;
            else
                Bind(BackgroundProperty, this.GetResourceObservable("SolidBackgroundFillColorBaseBrush"));
        }
    }

    /// <summary>Exit from the notification area: close for real, still refusing while an operation runs.</summary>
    public void Exit()
    {
        exiting = true;
        Show();
        Close();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Page))
            SelectNavigation();
    }

    private void SelectNavigation()
    {
        if (Shell is not { } shell)
            return;
        var tag = shell.Page.ToString();
        var item = Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<FANavigationViewItem>().FirstOrDefault(i => Equals(i.Tag, tag));
        if (item is not null && !ReferenceEquals(Navigation.SelectedItem, item))
            Navigation.SelectedItem = item;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (Shell is not { } shell)
            return;
        if (!exiting && shell.Settings.KeepRunningInTray && !e.IsProgrammatic)
        {
            // Closing the window keeps QueueCache in the notification area (Settings can turn this off).
            e.Cancel = true;
            Hide();
            return;
        }
        if (shell.HasRunningOperation)
        {
            e.Cancel = true;
            exiting = false;
            shell.Monitor.Notify("QueueCache closes when the current operation finishes.", NoticeSeverity.Informational);
            return;
        }
        shell.Stop();
    }
}
