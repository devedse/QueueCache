using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace QueueCache.Desktop;

internal static class AppBranding
{
    public static WindowIcon CreateIcon()
    {
        using var stream = typeof(AppBranding).Assembly.GetManifestResourceStream("QueueCache.Icon")
            ?? throw new InvalidOperationException("Embedded application icon is missing.");
        return new WindowIcon(stream);
    }

    public static Bitmap CreateHeaderImage()
    {
        using var stream = typeof(AppBranding).Assembly.GetManifestResourceStream("QueueCache.Artwork")
            ?? throw new InvalidOperationException("Embedded application icon is missing.");
        return new Bitmap(stream);
    }
}
