using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using QueueCache.Desktop.Formatting;
using QueueCache.Desktop.ViewModels;

namespace QueueCache.Desktop.Controls;

/// <summary>Reads, incoming writes and writes to disk over the last samples, on one axis in MiB/s.
/// Faint grid lines, the axis top labelled, the newest point emphasized; hovering shows the
/// values at that moment.</summary>
public sealed class ActivityChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<RateSample>?> SamplesProperty = AvaloniaProperty.Register<ActivityChart, IReadOnlyList<RateSample>?>(nameof(Samples));
    public static readonly StyledProperty<int> CapacityProperty = AvaloniaProperty.Register<ActivityChart, int>(nameof(Capacity), VolumeViewModel.HistoryLength);
    public static readonly StyledProperty<double> SecondsPerSampleProperty = AvaloniaProperty.Register<ActivityChart, double>(nameof(SecondsPerSample), 1);
    public static readonly StyledProperty<IBrush?> ReadBrushProperty = AvaloniaProperty.Register<ActivityChart, IBrush?>(nameof(ReadBrush));
    public static readonly StyledProperty<IBrush?> IncomingBrushProperty = AvaloniaProperty.Register<ActivityChart, IBrush?>(nameof(IncomingBrush));
    public static readonly StyledProperty<IBrush?> DrainedBrushProperty = AvaloniaProperty.Register<ActivityChart, IBrush?>(nameof(DrainedBrush));
    /// <summary>False for a RAM disk: nothing is written on to another disk, so there is no third line.</summary>
    public static readonly StyledProperty<bool> ShowDrainedProperty = AvaloniaProperty.Register<ActivityChart, bool>(nameof(ShowDrained), true);
    public static readonly StyledProperty<string> IncomingLabelProperty = AvaloniaProperty.Register<ActivityChart, string>(nameof(IncomingLabel), "Writes in");
    public static readonly StyledProperty<IBrush?> GridBrushProperty = AvaloniaProperty.Register<ActivityChart, IBrush?>(nameof(GridBrush));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<ActivityChart, IBrush?>(nameof(Foreground));
    private int? hover;

    static ActivityChart()
    {
        AffectsRender<ActivityChart>(SamplesProperty, CapacityProperty, ShowDrainedProperty, ReadBrushProperty, IncomingBrushProperty, DrainedBrushProperty, GridBrushProperty, ForegroundProperty);
        HeightProperty.OverrideDefaultValue<ActivityChart>(140);
    }

    public ActivityChart()
    {
        ToolTip.SetShowDelay(this, 0);
        ToolTip.SetPlacement(this, PlacementMode.Pointer);
    }

    public IReadOnlyList<RateSample>? Samples { get => GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public int Capacity { get => GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }
    public double SecondsPerSample { get => GetValue(SecondsPerSampleProperty); set => SetValue(SecondsPerSampleProperty, value); }
    public IBrush? ReadBrush { get => GetValue(ReadBrushProperty); set => SetValue(ReadBrushProperty, value); }
    public IBrush? IncomingBrush { get => GetValue(IncomingBrushProperty); set => SetValue(IncomingBrushProperty, value); }
    public IBrush? DrainedBrush { get => GetValue(DrainedBrushProperty); set => SetValue(DrainedBrushProperty, value); }
    public bool ShowDrained { get => GetValue(ShowDrainedProperty); set => SetValue(ShowDrainedProperty, value); }
    public string IncomingLabel { get => GetValue(IncomingLabelProperty); set => SetValue(IncomingLabelProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private const double Top = 18, Bottom = 4;

    /// <summary>The axis top: the largest value rounded up to 1, 2 or 5 times a power of ten (at least 1 MiB/s).</summary>
    /// <summary>A round axis top in the unit the label uses: 1/2/5 × 10ⁿ MiB/s, or GiB/s above 1,000 MiB/s
    /// (so the label reads "5 GiB/s", not "4.88 GiB/s").</summary>
    internal static double AxisTop(IEnumerable<RateSample> samples)
    {
        var max = samples.Select(s => Math.Max(s.Read, Math.Max(s.Incoming, s.Drained))).DefaultIfEmpty(0).Max();
        return max > 1000 ? Round(max / 1024) * 1024 : Round(max);
    }

    private static double Round(double max)
    {
        if (max <= 1)
            return 1;
        var power = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (var step in new[] { 1, 2, 5, 10 })
            if (max <= step * power)
                return step * power;
        return 10 * power;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= Top + Bottom)
            return;
        var samples = Samples ?? [];
        var typeface = new Typeface(FontFamily.Default);
        var plotHeight = height - Top - Bottom;
        var grid = new Pen(GridBrush, 1);
        foreach (var fraction in new[] { 0.0, 0.5, 1.0 })
        {
            var y = Math.Round(Top + plotHeight * (1 - fraction)) + 0.5;
            context.DrawLine(grid, new Point(0, y), new Point(width, y));
        }
        if (samples.Count < 2)
        {
            Text(context, "Collecting activity…", typeface, new Point(0, Top + plotHeight / 2 - 8));
            return;
        }
        var top = AxisTop(samples);
        Text(context, Format.Rate(top) == "Idle" ? "1 MiB/s" : Format.Rate(top), typeface, new Point(0, 0));
        var capacity = Math.Max(2, Capacity);
        // The newest sample sits at the right edge; older ones extend left.
        double X(int index) => width * (capacity - samples.Count + index) / (capacity - 1);
        double Y(double value) => Top + plotHeight * (1 - Math.Clamp(value / top, 0, 1));
        foreach (var (brush, pick) in new (IBrush?, Func<RateSample, double>)[] { (DrainedBrush, s => s.Drained), (IncomingBrush, s => s.Incoming), (ReadBrush, s => s.Read) }.Skip(ShowDrained ? 0 : 1))
        {
            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(new Point(X(0), Y(pick(samples[0]))), false);
                for (var i = 1; i < samples.Count; i++)
                    g.LineTo(new Point(X(i), Y(pick(samples[i]))));
                g.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(brush, 2, lineJoin: PenLineJoin.Round), line);
            var last = new Point(X(samples.Count - 1), Y(pick(samples[^1])));
            context.DrawEllipse(brush, null, last, 3, 3);
        }
        if (hover is { } index && index < samples.Count)
        {
            var x = Math.Round(X(index)) + 0.5;
            context.DrawLine(new Pen(Foreground, 1, dashStyle: DashStyle.Dash), new Point(x, Top), new Point(x, Top + plotHeight));
        }
    }

    private void Text(DrawingContext context, string text, Typeface typeface, Point at) =>
        context.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, Foreground), at);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var samples = Samples ?? [];
        if (samples.Count < 2 || Bounds.Width <= 0)
            return;
        var capacity = Math.Max(2, Capacity);
        var position = e.GetPosition(this).X / Bounds.Width * (capacity - 1);
        var index = (int)Math.Round(position) - (capacity - samples.Count);
        if (index < 0 || index >= samples.Count)
        {
            Clear();
            return;
        }
        hover = index;
        var sample = samples[index];
        var age = (samples.Count - 1 - index) * SecondsPerSample;
        ToolTip.SetTip(this, $"{(age < 0.5 ? "Now" : $"{age:0.#} s ago")}\nReads {Format.Rate(sample.Read)}\n{IncomingLabel} {Format.Rate(sample.Incoming)}" +
            (ShowDrained ? $"\nWritten to disk {Format.Rate(sample.Drained)}" : ""));
        ToolTip.SetIsOpen(this, true);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Clear();
    }

    private void Clear()
    {
        hover = null;
        ToolTip.SetIsOpen(this, false);
        InvalidateVisual();
    }
}
