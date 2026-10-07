using Avalonia;
using QueueCache.Desktop.Services;

namespace QueueCache.Desktop;

internal static class Program
{
    /// <summary>Set while this process owns the session's QueueCache window (Windows only).</summary>
    internal static SingleInstance? Instance { get; private set; }
    /// <summary>Started by the sign-in task: stay in the notification area.</summary>
    internal static bool InTray { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        InTray = args.Contains("--tray");
        if (OperatingSystem.IsWindows())
        {
            // A second start at sign-in must not pop up the window of one already running.
            Instance = SingleInstance.Acquire(showExisting: !InTray);
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
