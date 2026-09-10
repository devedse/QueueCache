using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace QueueCache.Desktop;

/// <summary>Bounded in-memory history, one point per confirmed driver sample.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class RateHistory : Control
{
    private readonly Queue<(double Incoming, double Draining, double Read)> samples = new();
    public RateHistory() { Height = 55; ClipToBounds = true; }
    public void Add(double incoming, double draining, double read, bool reset)
    {
        if (reset) samples.Clear();
        samples.Enqueue((incoming, draining, read));
        while (samples.Count > 60) samples.Dequeue();
        InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var points = samples.ToArray(); if (points.Length < 2) return;
        var scale = Math.Max(1, points.Max(p => Math.Max(p.Read, Math.Max(p.Incoming, p.Draining))));
        var reading = new Pen(MainWindow.ReadFill, 2);
        var incoming = new Pen(MainWindow.RetainedFill, 2);
        var draining = new Pen(MainWindow.PendingFill, 2);
        for (int i = 1; i < points.Length; i++)
        {
            double x1 = Bounds.Width * (i - 1) / 59, x2 = Bounds.Width * i / 59;
            context.DrawLine(reading, new Point(x1, Bounds.Height * (1 - points[i - 1].Read / scale)), new Point(x2, Bounds.Height * (1 - points[i].Read / scale)));
            context.DrawLine(incoming, new Point(x1, Bounds.Height * (1 - points[i - 1].Incoming / scale)), new Point(x2, Bounds.Height * (1 - points[i].Incoming / scale)));
            context.DrawLine(draining, new Point(x1, Bounds.Height * (1 - points[i - 1].Draining / scale)), new Point(x2, Bounds.Height * (1 - points[i].Draining / scale)));
        }
    }
}
