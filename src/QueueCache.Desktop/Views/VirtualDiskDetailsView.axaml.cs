using Avalonia.Controls;

namespace QueueCache.Desktop.Views;

public sealed partial class VirtualDiskDetailsView : UserControl
{
    public VirtualDiskDetailsView()
    {
        InitializeComponent();
        // Two columns of figures when the details pane is narrow.
        Figures.SizeChanged += (_, e) => Tiles.Columns = e.NewSize.Width < 620 ? 2 : 4;
    }
}
