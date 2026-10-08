using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using QueueCache.Management;

namespace QueueCache.Desktop.Controls;

/// <summary>A defragmenter-style map of a cache's RAM: one square per 256 KiB chunk (or per group of
/// chunks on large caches). Colour shows what the chunk mostly holds, fill strength how full it is,
/// and a diagonal mark that its data is out of disk order.</summary>
public sealed class CacheMap : Control
{
    public static readonly StyledProperty<CacheLayoutMap?> MapProperty = AvaloniaProperty.Register<CacheMap, CacheLayoutMap?>(nameof(Map));
    public static readonly StyledProperty<IBrush?> ReadBrushProperty = AvaloniaProperty.Register<CacheMap, IBrush?>(nameof(ReadBrush));
    public static readonly StyledProperty<IBrush?> WrittenBrushProperty = AvaloniaProperty.Register<CacheMap, IBrush?>(nameof(WrittenBrush));
    public static readonly StyledProperty<IBrush?> PendingBrushProperty = AvaloniaProperty.Register<CacheMap, IBrush?>(nameof(PendingBrush));
    public static readonly StyledProperty<IBrush?> FreeBrushProperty = AvaloniaProperty.Register<CacheMap, IBrush?>(nameof(FreeBrush));
    public static readonly StyledProperty<IBrush?> MarkBrushProperty = AvaloniaProperty.Register<CacheMap, IBrush?>(nameof(MarkBrush));
    internal const int MaxCells = 2048;
    private const double Cell = 9, Gap = 2;

    static CacheMap()
    {
        AffectsRender<CacheMap>(MapProperty, ReadBrushProperty, WrittenBrushProperty, PendingBrushProperty, FreeBrushProperty, MarkBrushProperty);
        AffectsMeasure<CacheMap>(MapProperty);
    }

    public CacheLayoutMap? Map { get => GetValue(MapProperty); set => SetValue(MapProperty, value); }
    public IBrush? ReadBrush { get => GetValue(ReadBrushProperty); set => SetValue(ReadBrushProperty, value); }
    public IBrush? WrittenBrush { get => GetValue(WrittenBrushProperty); set => SetValue(WrittenBrushProperty, value); }
    public IBrush? PendingBrush { get => GetValue(PendingBrushProperty); set => SetValue(PendingBrushProperty, value); }
    public IBrush? FreeBrush { get => GetValue(FreeBrushProperty); set => SetValue(FreeBrushProperty, value); }
    public IBrush? MarkBrush { get => GetValue(MarkBrushProperty); set => SetValue(MarkBrushProperty, value); }

    internal enum Kind { Free, Read, Written, Pending }
    internal readonly record struct MapCell(Kind Kind, double Fill, bool OutOfOrder);

    /// <summary>Cells for a map: chunks grouped so there are at most <see cref="MaxCells"/>.
    /// Pending wins (data not yet on disk matters most), then the larger of read and written.
    /// Out of order: at least 8 used slots and fewer than half of the neighbouring pairs in disk order.</summary>
    internal static MapCell[] Cells(CacheLayoutMap map)
    {
        var group = Group(map.Chunks);
        var cells = new MapCell[(map.Chunks + group - 1) / group];
        for (var c = 0; c < cells.Length; c++)
        {
            int used = 0, dirty = 0, read = 0, ordered = 0, pairs = 0, slots = 0;
            for (var i = c * group; i < Math.Min(map.Chunks, (c + 1) * group); i++)
            {
                used += map.Used[i]; dirty += map.Dirty[i]; read += map.Read[i]; ordered += map.Ordered[i];
                pairs += Math.Max(0, map.Used[i] - 1);
                slots += CacheLayoutMap.SlotsPerChunk;
            }
            var kind = used == 0 ? Kind.Free : dirty > 0 ? Kind.Pending : read * 2 >= used ? Kind.Read : Kind.Written;
            cells[c] = new(kind, (double)used / slots, used >= 8 && ordered * 2 < pairs);
        }
        return cells;
    }

    private static int Group(int chunks) => Math.Max(1, (chunks + MaxCells - 1) / MaxCells);
    private static int Columns(double width) => Math.Max(1, (int)((width + Gap) / (Cell + Gap)));

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width;
        if (Map is not { Chunks: > 0 } map)
            return new Size(0, 0);
        var count = (map.Chunks + Group(map.Chunks) - 1) / Group(map.Chunks);
        var rows = (count + Columns(width) - 1) / Columns(width);
        return new Size(width, rows * (Cell + Gap) - Gap);
    }

    public override void Render(DrawingContext context)
    {
        if (Map is not { Chunks: > 0 } map)
            return;
        var columns = Columns(Bounds.Width);
        var mark = MarkBrush is null ? null : new Pen(MarkBrush, 1.2);
        var cells = Cells(map);
        for (var c = 0; c < cells.Length; c++)
        {
            var rect = new Rect(c % columns * (Cell + Gap), c / columns * (Cell + Gap), Cell, Cell);
            var cell = cells[c];
            var brush = cell.Kind switch { Kind.Read => ReadBrush, Kind.Written => WrittenBrush, Kind.Pending => PendingBrush, _ => FreeBrush };
            if (brush is null)
                continue;
            // A barely used chunk stays visible; a full one is fully coloured.
            using (context.PushOpacity(cell.Kind == Kind.Free ? 1 : 0.35 + 0.65 * cell.Fill))
                context.DrawRectangle(brush, null, rect, 2, 2);
            if (cell.OutOfOrder && mark is not null)
                context.DrawLine(mark, rect.BottomLeft + new Vector(2, -2), rect.TopRight + new Vector(-2, 2));
        }
    }
}
