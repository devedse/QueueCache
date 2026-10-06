using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace QueueCache.Desktop.Views;

internal static class WindowPlacement
{
    /// <summary>Keeps a window, title bar included, inside the working area of its screen. A default
    /// size that suits a large screen would otherwise put the title bar or the buttons off a small one.</summary>
    public static void FitToScreen(this Window window)
    {
        if (window.Screens.ScreenFromWindow(window) is not { } screen)
            return;
        var area = screen.WorkingArea;
        var scaling = screen.Scaling;
        var frame = window.FrameSize is { } outer ? outer - window.ClientSize : new Size(16, 40);
        window.MaxWidth = Math.Min(window.MaxWidth, Math.Max(window.MinWidth, area.Width / scaling - frame.Width));
        window.MaxHeight = Math.Min(window.MaxHeight, Math.Max(window.MinHeight, area.Height / scaling - frame.Height));
        if (!double.IsNaN(window.Width))
            window.Width = Math.Min(window.Width, window.MaxWidth);
        if (!double.IsNaN(window.Height))
            window.Height = Math.Min(window.Height, window.MaxHeight);
        // Once the new size is applied, move the window back inside the working area.
        Dispatcher.UIThread.Post(() =>
        {
            var size = window.FrameSize ?? window.ClientSize + frame;
            var width = (int)Math.Ceiling(size.Width * scaling);
            var height = (int)Math.Ceiling(size.Height * scaling);
            var x = Math.Clamp(window.Position.X, area.X, Math.Max(area.X, area.Right - width));
            var y = Math.Clamp(window.Position.Y, area.Y, Math.Max(area.Y, area.Bottom - height));
            if (x != window.Position.X || y != window.Position.Y)
                window.Position = new PixelPoint(x, y);
        }, DispatcherPriority.Background);
    }
}
