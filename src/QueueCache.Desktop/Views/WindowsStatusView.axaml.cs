using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using QueueCache.Desktop.ViewModels;

namespace QueueCache.Desktop.Views;

/// <summary>Poll only while visible; also reread when the containing window is activated.</summary>
public sealed partial class WindowsStatusView : UserControl
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Window? window;

    public WindowsStatusView()
    {
        InitializeComponent();
        timer.Tick += (_, _) => RefreshIfVisible();
        DataContextChanged += (_, _) => RefreshIfVisible();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        window = TopLevel.GetTopLevel(this) as Window;
        if (window is not null) window.Activated += OnActivated;
        RefreshIfVisible();
        timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        timer.Stop();
        if (window is not null) window.Activated -= OnActivated;
        window = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnActivated(object? sender, EventArgs e) => RefreshIfVisible();

    internal void RefreshIfVisible()
    {
        if (IsEffectivelyVisible && window?.WindowState != WindowState.Minimized && DataContext is WindowsStatusViewModel model)
            model.Refresh();
    }
}
