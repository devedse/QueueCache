using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace QueueCache.Desktop.Services;

/// <summary>One QueueCache window per Windows session. The app keeps running in the notification
/// area, so starting it again asks the running instance to show its window instead.</summary>
internal sealed class SingleInstance : IDisposable
{
    private const string Name = @"Local\QueueCache.Desktop";
    private readonly Mutex mutex;
    private readonly EventWaitHandle show;
    private readonly RegisteredWaitHandle wait;

    private SingleInstance(Mutex mutex, EventWaitHandle show)
    {
        this.mutex = mutex;
        this.show = show;
        wait = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => ShowRequested?.Invoke(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Raised on a thread-pool thread when another start asked for the window.</summary>
    public event Action? ShowRequested;

    /// <summary>The instance, or null after asking the running one to show itself.</summary>
    [SupportedOSPlatform("windows")]
    public static SingleInstance? Acquire(bool showExisting = true)
    {
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, Name + ".Show");
        var mutex = new Mutex(true, Name, out var created);
        if (created)
            return new SingleInstance(mutex, show);
        if (showExisting)
        {
            // The running instance may bring its window to the front.
            AllowSetForegroundWindow(-1);
            show.Set();
        }
        show.Dispose();
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        wait.Unregister(null);
        show.Dispose();
        mutex.ReleaseMutex();
        mutex.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
