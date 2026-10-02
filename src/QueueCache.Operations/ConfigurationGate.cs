using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace QueueCache.Operations;

/// <summary>Cross-process, thread-affine management transaction. Do not await while held.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ConfigurationGate : IDisposable
{
    private readonly Mutex mutex;
    internal static string NameFor(string instance) => @"Global\QueueCache.Configuration.Disk." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instance.ToUpperInvariant())));
    private ConfigurationGate(string instance)
    {
        if (string.IsNullOrWhiteSpace(instance)) throw new ArgumentException("A stable disk identity is required.");
        mutex = new(false, NameFor(instance));
    }
    public static ConfigurationGate Enter(string instance)
    {
        var gate = new ConfigurationGate(instance);
        try
        {
            try
            {
                if (!gate.mutex.WaitOne(TimeSpan.FromSeconds(30)))
                    throw new IOException("Another interface is changing cache settings. Try again when it finishes.");
            }
            catch (AbandonedMutexException) { /* Ownership acquired; caller must inspect live driver state. */ }
            return gate;
        }
        catch { gate.mutex.Dispose(); throw; }
    }
    public void Dispose()
    {
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
