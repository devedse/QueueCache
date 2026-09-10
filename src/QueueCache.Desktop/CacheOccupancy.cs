using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace QueueCache.Desktop;

/// <summary>Resident bytes, not throughput: draining moves purple into teal, not into free space.</summary>
internal sealed class CacheOccupancy : Control
{
    // One source of truth for this bar, the history chart and their legends.
    internal static readonly IBrush ReadFill = Brush.Parse("#3489DB"), RetainedFill = Brush.Parse("#087F8C"),
        PendingFill = Brush.Parse("#9A66CC"), FreeFill = Brush.Parse("#E8F1F2");
    private double read, retained, dirty;
    public CacheOccupancy() { Height = 14; ClipToBounds = true; }
    public void Update(ulong readBytes, ulong retainedBytes, ulong dirtyBytes, ulong capacity)
    {
        read = capacity == 0 ? 0 : Math.Clamp((double)readBytes / capacity, 0, 1);
        retained = capacity == 0 ? 0 : Math.Clamp((double)retainedBytes / capacity, 0, 1 - read);
        dirty = capacity == 0 ? 0 : Math.Clamp((double)dirtyBytes / capacity, 0, 1 - read - retained);
        InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(FreeFill, new Rect(Bounds.Size));
        double x = 0;
        foreach (var (fraction, fill) in new[] { (read, ReadFill), (retained, RetainedFill), (dirty, PendingFill) })
        {
            context.FillRectangle(fill, new Rect(x, 0, fraction * Bounds.Width, Bounds.Height));
            x += fraction * Bounds.Width;
        }
    }
}
