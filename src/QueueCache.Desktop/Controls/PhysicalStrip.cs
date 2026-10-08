using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using QueueCache.Management;

namespace QueueCache.Desktop.Controls;

/// <summary>Physical memory as one strip from the lowest address (left) to the highest (right), with the
/// RAM disk's pages drawn where they sit: the darker a slice, the more of it the disk uses.</summary>
public sealed class PhysicalStrip : Control
{
    public static readonly StyledProperty<RamPhysicalMap?> MapProperty = AvaloniaProperty.Register<PhysicalStrip, RamPhysicalMap?>(nameof(Map));
    public static readonly StyledProperty<IBrush?> UsedBrushProperty = AvaloniaProperty.Register<PhysicalStrip, IBrush?>(nameof(UsedBrush));
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<PhysicalStrip, IBrush?>(nameof(TrackBrush));

    static PhysicalStrip() => AffectsRender<PhysicalStrip>(MapProperty, UsedBrushProperty, TrackBrushProperty);

    public RamPhysicalMap? Map { get => GetValue(MapProperty); set => SetValue(MapProperty, value); }
    public IBrush? UsedBrush { get => GetValue(UsedBrushProperty); set => SetValue(UsedBrushProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    /// <summary>Share of each bin's pages the disk uses (0..1).</summary>
    internal static double[] Shares(RamPhysicalMap map)
    {
        var perBin = Math.Max(1.0, (double)map.SpanPages / RamPhysicalMap.Bins);
        return map.Counts.Select(c => Math.Min(1, c / perBin)).ToArray();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width, 28);

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (TrackBrush is not null)
            context.DrawRectangle(TrackBrush, null, bounds, 4, 4);
        if (Map is not { Pages: > 0 } map || UsedBrush is null)
            return;
        var shares = Shares(map);
        var width = bounds.Width / shares.Length;
        using (context.PushClip(new RoundedRect(bounds, 4)))
            for (var i = 0; i < shares.Length; i++)
                if (shares[i] > 0)
                    using (context.PushOpacity(0.25 + 0.75 * shares[i]))
                        // Slightly wider than a bin so neighbouring used bins join into one band.
                        context.DrawRectangle(UsedBrush, null, new Rect(i * width, 0, Math.Max(1, width + 0.5), bounds.Height));
    }
}
