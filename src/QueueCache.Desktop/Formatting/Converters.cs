using Avalonia.Data.Converters;
using FluentAvalonia.UI.Controls;
using QueueCache.Desktop.ViewModels;

namespace QueueCache.Desktop.Formatting;

/// <summary>Value converters the views use. View-models stay free of UI types.</summary>
public static class Converters
{
    public static readonly IValueConverter Severity = new FuncValueConverter<NoticeSeverity, FAInfoBarSeverity>(severity => severity switch
    {
        NoticeSeverity.Success => FAInfoBarSeverity.Success,
        NoticeSeverity.Warning => FAInfoBarSeverity.Warning,
        NoticeSeverity.Error => FAInfoBarSeverity.Error,
        _ => FAInfoBarSeverity.Informational
    });

    public static readonly IValueConverter NotZero = new FuncValueConverter<int, bool>(count => count != 0);

    /// <summary>Byte counts for <see cref="Controls.SegmentBar"/>.</summary>
    public static readonly IValueConverter Amount = new FuncValueConverter<ulong, double>(bytes => bytes);
}
