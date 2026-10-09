using Avalonia.Controls;
using Avalonia.Interactivity;
using QueueCache.Desktop.ViewModels;

namespace QueueCache.Desktop.Views;

public sealed partial class VolumeDetailsView : UserControl
{
    public VolumeDetailsView() => InitializeComponent();

    private void PopoutMap(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VolumeViewModel volume && TopLevel.GetTopLevel(this) is Window owner)
            CacheMapWindow.Open(owner, volume);
    }
}
