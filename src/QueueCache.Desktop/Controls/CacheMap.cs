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
    public static readonly StyledProperty<int> CellLimitProperty = AvaloniaProperty.Register<CacheMap, int>(nameof(CellLimit), MaxCells);
    public static readonly StyledProperty<bool> FillViewportProperty = AvaloniaProperty.Register<CacheMap, bool>(nameof(FillViewport));
    internal const int MaxCells = 2048;
    private const double Cell = 9, Gap = 2;

    static CacheMap()
    {
        AffectsRender<CacheMap>(MapProperty, ReadBrushProperty, WrittenBrushProperty, PendingBrushProperty, FreeBrushProperty, MarkBrushProperty, CellLimitProperty, FillViewportProperty);
        AffectsMeasure<CacheMap>(MapProperty, CellLimitProperty, FillViewportProperty);
    }

    public CacheLayoutMap? Map { get => GetValue(MapProperty); set => SetValue(MapProperty, value); }
    public IBrush? ReadBrush { get => GetValue(ReadBrushProperty); set => SetValue(ReadBrushProperty, value); }
    public IBrush? WrittenBrush { get => GetValue(WrittenBrushProperty); set => SetValue(WrittenBrushProperty, value); }
    public IBrush? PendingBrush { get => GetValue(PendingBrushProperty); set => SetValue(PendingBrushProperty, value); }
    public IBrush? FreeBrush { get => GetValue(FreeBrushProperty); set => SetValue(FreeBrushProperty, value); }
    public IBrush? MarkBrush { get => GetValue(MarkBrushProperty); set => SetValue(MarkBrushProperty, value); }
    public int CellLimit { get => GetValue(CellLimitProperty); set => SetValue(CellLimitProperty, value); }
    public bool FillViewport { get => GetValue(FillViewportProperty); set => SetValue(FillViewportProperty, value); }

    internal enum Kind { Free, Read, Written, Pending }
    internal readonly record struct MapCell(Kind Kind, double Fill, bool OutOfOrder);

    /// <summary>Cells for a map: chunks grouped to the configured limit (2,048 by default).
    /// Pending wins (data not yet on disk matters most), then the larger of read and written.
    /// Out of order: at least 8 used slots and fewer than half of the neighbouring pairs in disk order.</summary>
    internal static MapCell[] Cells(CacheLayoutMap map, int cellLimit = MaxCells)
    {
        var group = Group(map.Chunks, cellLimit);
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

    private static int Group(int chunks, int limit) => Math.Max(1, (chunks + Math.Max(1, limit) - 1) / Math.Max(1, limit));
    private static int Columns(double width) => Math.Max(1, (int)((width + Gap) / (Cell + Gap)));

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width;
        if (Map is not { Chunks: > 0 } map)
            return new Size(0, 0);
        if (FillViewport)
            return new Size(width, double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height);
        var group = Group(map.Chunks, CellLimit);
        var count = (map.Chunks + group - 1) / group;
        var rows = (count + Columns(width) - 1) / Columns(width);
        return new Size(width, rows * (Cell + Gap) - Gap);
    }

    public override void Render(DrawingContext context)
    {
        if (Map is not { Chunks: > 0 } map)
            return;
        var cells = Cells(map, CellLimit);
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || cells.Length == 0)
            return;
        var columns = FillViewport
            ? Math.Clamp((int)Math.Ceiling(Math.Sqrt(cells.Length * Bounds.Width / Bounds.Height)), 1, cells.Length)
            : Columns(Bounds.Width);
        var rows = (cells.Length + columns - 1) / columns;
        var step = FillViewport ? Math.Min(Bounds.Width / columns, Bounds.Height / rows) : Cell + Gap;
        var size = step * Cell / (Cell + Gap);
        var mark = MarkBrush is null ? null : new Pen(MarkBrush, 1.2);
        for (var c = 0; c < cells.Length; c++)
        {
            var rect = new Rect(c % columns * step, c / columns * step, size, size);
            var cell = cells[c];
            var brush = cell.Kind switch { Kind.Read => ReadBrush, Kind.Written => WrittenBrush, Kind.Pending => PendingBrush, _ => FreeBrush };
            if (brush is null)
                continue;
            // A barely used chunk stays visible; a full one is fully coloured.
            using (context.PushOpacity(cell.Kind == Kind.Free ? 1 : 0.35 + 0.65 * cell.Fill))
                context.DrawRectangle(brush, null, rect, 2, 2);
            if (cell.OutOfOrder && mark is not null)
                context.DrawLine(mark, rect.BottomLeft + new Vector(size * 2 / 9, -size * 2 / 9), rect.TopRight + new Vector(-size * 2 / 9, size * 2 / 9));
        }
    }
}
