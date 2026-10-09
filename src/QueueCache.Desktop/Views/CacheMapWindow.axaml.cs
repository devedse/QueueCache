using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using QueueCache.Desktop.ViewModels;

namespace QueueCache.Desktop.Views;

/// <summary>A live map shares the monitor's existing sample and survives page changes.
/// Closing, hiding or minimizing releases its request; replacing the volume closes the window.</summary>
public sealed partial class CacheMapWindow : Window
{
    private VolumeViewModel? volume;
    private bool requesting;
    private WindowState previousState = WindowState.Maximized;

    public CacheMapWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            SetRequest(false);
            if (volume is { } current)
                current.Monitor.Rebuilt -= InventoryChanged;
        };
    }

    internal static CacheMapWindow Open(Window owner, VolumeViewModel volume)
    {
        var existing = owner.OwnedWindows.OfType<CacheMapWindow>().FirstOrDefault(w => w.volume == volume);
        if (existing is not null)
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Maximized;
            existing.Activate();
            return existing;
        }
        var window = new CacheMapWindow { volume = volume, DataContext = volume };
        volume.Monitor.Rebuilt += window.InventoryChanged;
        window.Show(owner);
        return window;
    }

    private void InventoryChanged(object? sender, EventArgs e)
    {
        if (volume is { } current && !current.Monitor.Owns(current))
            Close();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty || change.Property == WindowStateProperty)
            SetRequest(IsVisible && WindowState != WindowState.Minimized);
        if (change.Property == WindowStateProperty && FullScreenButton is not null)
            FullScreenButton.Content = WindowState == WindowState.FullScreen ? "Exit full screen" : "Full screen";
    }

    private void SetRequest(bool value)
    {
        if (volume is null || requesting == value)
            return;
        requesting = value;
        if (value)
            volume.AddMapWindow();
        else
            volume.RemoveMapWindow();
    }

    private void ToggleFullScreen(object? sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.FullScreen)
            WindowState = previousState;
        else
        {
            previousState = WindowState;
            WindowState = WindowState.FullScreen;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullScreen(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && WindowState == WindowState.FullScreen)
        {
            WindowState = previousState;
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private void CloseMap(object? sender, RoutedEventArgs e) => Close();
}
