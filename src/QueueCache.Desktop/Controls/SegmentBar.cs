using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace QueueCache.Desktop.Controls;

/// <summary>Up to three amounts of one whole, left to right, with the rest shown as free. Used for
/// what is in a cache's RAM and for the RAM split between caches and virtual disks.
/// Segments are separated by a small gap so neighbors stay distinct without relying on color.</summary>
public sealed class SegmentBar : Control
{
    public static readonly StyledProperty<double> FirstProperty = AvaloniaProperty.Register<SegmentBar, double>(nameof(First));
    public static readonly StyledProperty<double> SecondProperty = AvaloniaProperty.Register<SegmentBar, double>(nameof(Second));
    public static readonly StyledProperty<double> ThirdProperty = AvaloniaProperty.Register<SegmentBar, double>(nameof(Third));
    public static readonly StyledProperty<double> TotalProperty = AvaloniaProperty.Register<SegmentBar, double>(nameof(Total));
    public static readonly StyledProperty<IBrush?> FirstBrushProperty = AvaloniaProperty.Register<SegmentBar, IBrush?>(nameof(FirstBrush));
    public static readonly StyledProperty<IBrush?> SecondBrushProperty = AvaloniaProperty.Register<SegmentBar, IBrush?>(nameof(SecondBrush));
    public static readonly StyledProperty<IBrush?> ThirdBrushProperty = AvaloniaProperty.Register<SegmentBar, IBrush?>(nameof(ThirdBrush));
    public static readonly StyledProperty<IBrush?> RestBrushProperty = AvaloniaProperty.Register<SegmentBar, IBrush?>(nameof(RestBrush));

    static SegmentBar()
    {
        AffectsRender<SegmentBar>(FirstProperty, SecondProperty, ThirdProperty, TotalProperty, FirstBrushProperty, SecondBrushProperty, ThirdBrushProperty, RestBrushProperty);
        HeightProperty.OverrideDefaultValue<SegmentBar>(12);
    }

    public double First { get => GetValue(FirstProperty); set => SetValue(FirstProperty, value); }
    public double Second { get => GetValue(SecondProperty); set => SetValue(SecondProperty, value); }
    public double Third { get => GetValue(ThirdProperty); set => SetValue(ThirdProperty, value); }
    public double Total { get => GetValue(TotalProperty); set => SetValue(TotalProperty, value); }
    public IBrush? FirstBrush { get => GetValue(FirstBrushProperty); set => SetValue(FirstBrushProperty, value); }
    public IBrush? SecondBrush { get => GetValue(SecondBrushProperty); set => SetValue(SecondBrushProperty, value); }
    public IBrush? ThirdBrush { get => GetValue(ThirdBrushProperty); set => SetValue(ThirdBrushProperty, value); }
    public IBrush? RestBrush { get => GetValue(RestBrushProperty); set => SetValue(RestBrushProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;
        const double gap = 2;
        var radius = Math.Min(4, bounds.Height / 2);
        var total = Math.Max(Total, First + Second + Third);
        var parts = new[] { (First, FirstBrush), (Second, SecondBrush), (Third, ThirdBrush), (Math.Max(0, total - First - Second - Third), RestBrush) }
            .Where(p => p.Item1 > 0 && total > 0).ToArray();
        if (parts.Length == 0)
        {
            context.DrawRectangle(RestBrush, null, bounds, radius, radius);
            return;
        }
        var usable = bounds.Width - gap * (parts.Length - 1);
        using (context.PushClip(new RoundedRect(bounds, radius)))
        {
            var x = 0.0;
            foreach (var (amount, brush) in parts)
            {
                // Every non-zero amount stays visible, however small.
                var width = Math.Max(2, usable * amount / total);
                context.FillRectangle(brush ?? Brushes.Transparent, new Rect(x, 0, Math.Min(width, bounds.Width - x), bounds.Height));
                x += width + gap;
                if (x >= bounds.Width)
                    break;
            }
        }
    }
}
