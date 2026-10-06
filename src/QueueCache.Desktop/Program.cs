using Avalonia;
using QueueCache.Desktop.Services;

namespace QueueCache.Desktop;

internal static class Program
{
    /// <summary>Set while this process owns the session's QueueCache window (Windows only).</summary>
    internal static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            Instance = SingleInstance.Acquire();
            if (Instance is null)
                return;
        }
        try
        {
            AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Instance?.Dispose();
        }
    }
}
