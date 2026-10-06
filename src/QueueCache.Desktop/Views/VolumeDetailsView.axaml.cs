using Avalonia.Controls;

namespace QueueCache.Desktop.Views;

public sealed partial class VolumeDetailsView : UserControl
{
    public VolumeDetailsView()
    {
        InitializeComponent();
        // Two columns of figures when the details pane is narrow.
        SizeChanged += (_, e) => Tiles.Columns = Legend.Columns = e.NewSize.Width < 620 ? 2 : 4;
    }
}
