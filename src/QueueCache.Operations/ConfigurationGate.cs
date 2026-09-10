using System.Runtime.Versioning;

namespace QueueCache.Operations;

/// <summary>Cross-process, thread-affine management transaction. Do not await while held.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ConfigurationGate : IDisposable
{
    private readonly Mutex mutex = new(false, @"Global\QueueCache.Configuration");
    private ConfigurationGate() { }
    public static ConfigurationGate Enter()
    {
        var gate = new ConfigurationGate();
        try
        {
            try { if (!gate.mutex.WaitOne(TimeSpan.FromSeconds(30))) throw new IOException("Another interface is changing cache settings. Try again when it finishes."); }
            catch (AbandonedMutexException) { /* Ownership acquired; caller must inspect live driver state. */ }
            return gate;
        }
        catch { gate.mutex.Dispose(); throw; }
    }
    public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
}
