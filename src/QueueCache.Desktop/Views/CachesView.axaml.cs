using Avalonia.Controls;
using QueueCache.Desktop.ViewModels;

namespace QueueCache.Desktop.Views;

public sealed partial class CachesView : UserControl
{
    public CachesView()
    {
        InitializeComponent();
        // Disk rows are headers: not selectable, not a keyboard stop.
        VolumeList.ContainerPrepared += (_, e) =>
        {
            var header = e.Container.DataContext is DiskGroupViewModel;
            e.Container.Classes.Set("header", header);
            e.Container.IsEnabled = !header;
            e.Container.Focusable = !header;
        };
    }
}
