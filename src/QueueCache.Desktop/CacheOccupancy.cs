using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace QueueCache.Desktop;

/// <summary>Resident bytes, not throughput: draining moves purple into teal, not into free space.</summary>
internal sealed class CacheOccupancy : Control
{
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
        context.FillRectangle(Brush.Parse("#E8F1F2"), new Rect(Bounds.Size));
        double x = 0;
        foreach (var (fraction, color) in new[] { (read, "#3489DB"), (retained, "#087F8C"), (dirty, "#9A66CC") })
        {
            context.FillRectangle(Brush.Parse(color), new Rect(x, 0, fraction * Bounds.Width, Bounds.Height));
            x += fraction * Bounds.Width;
        }
    }
}
