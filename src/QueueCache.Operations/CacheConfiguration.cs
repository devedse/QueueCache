using QueueCache.Management;
using System.Runtime.Versioning;
using System.Diagnostics;

namespace QueueCache.Operations;

public enum CachePreset { Fast, Strict }
public sealed class CacheDrainingException() : IOException("Cache is busy draining; wait for draining to finish before changing settings.");
public sealed record CacheConfiguration(int BudgetMiB = 4096, CachePreset Preset = CachePreset.Fast, bool Enabled = true)
{
    public CacheOptions Options { get; init; } = new();
    public static CacheConfiguration FromState(WriteCacheState state) => new((int)(state.BudgetBytes >> 20),
        state.UnsafeDefer ? CachePreset.Fast : CachePreset.Strict, state.Enabled) { Options = state.Options ?? new() };
    public void Validate(bool acceptVolatileFlush)
    {
        if (BudgetMiB is < 1 or > MemoryBudget.MaximumMiB) throw new ArgumentException("Budget must be 1..131072 MiB; actual availability is checked at apply time.");
        if (Options is null) throw new ArgumentException("Cache policies are required.");
        Options.Validate();
        if (!Enum.IsDefined(Preset)) throw new ArgumentException("Unknown cache preset.");
        if (Preset == CachePreset.Fast && !acceptVolatileFlush)
            throw new ArgumentException("Fast mode acknowledges writes/flushes in volatile RAM. Explicitly accept volatile flushes first.");
    }
}

/// <summary>Both frontends use this orchestrator. Failed changes stop immediately; no fictitious rollback.</summary>
[SupportedOSPlatform("windows")]
public static class ConfigurationManager
{
    public static WriteCacheState Apply(DiskTarget target, CacheConfiguration configuration,
        bool acceptVolatileFlush, IProgress<string>? progress = null)
    {
        using var gate = ConfigurationGate.Enter();
        configuration.Validate(acceptVolatileFlush);
        target.CheckExtents();
        using var device = new CacheDevice(target.Device, writable: true);
        var initial = device.GetWriteCacheState();
        var state = WaitForHealthyState(device.GetWriteCacheState, initial, progress);
        if (!state.SupportsReadWrite) throw new NotSupportedException("Install the matching read/write-cache driver and restart Windows before applying settings.");
        if (state.DeviceBytes != (ulong)target.Bytes) throw new IOException("Disk size changed.");
        var budget = (ulong)configuration.BudgetMiB << 20;
        if (state.Enabled == configuration.Enabled && state.BudgetBytes == budget &&
            state.UnsafeDefer == (configuration.Preset == CachePreset.Fast) && state.Options == configuration.Options &&
            (!configuration.Enabled || state.Operational)) return state;
        MemoryBudget.ValidateIncrease(state.BudgetBytes, budget);
        progress?.Report("Draining and disabling before applying configuration. This may take time.");
        device.Control(WriteCacheAction.Disable);
        progress?.Report("Applying preset and RAM budget.");
        device.Control(WriteCacheAction.FlushPolicy, value: configuration.Preset == CachePreset.Fast ? 1UL : 0UL);
        if (state.BudgetBytes != budget) device.Control(WriteCacheAction.Configure, budget);
        device.SetOptions(configuration.Options);
        if (configuration.Enabled) device.Control(WriteCacheAction.Enable);
        // A background barrier can start immediately after Enable completes.
        // Wait on snapshots, not by replaying Disable/Configure/Enable.
        var result = WaitForHealthyState(device.GetWriteCacheState, state, progress);
        if (result.Enabled != configuration.Enabled || result.BudgetBytes != budget ||
            result.UnsafeDefer != (configuration.Preset == CachePreset.Fast) || result.Options != configuration.Options ||
            result.Instance != state.Instance || (configuration.Enabled && !result.Operational))
            throw new IOException("Driver state does not match the requested configuration. No success has been assumed.");
        progress?.Report("Configuration applied and verified.");
        return result;
    }

    internal static WriteCacheState WaitForHealthyState(Func<WriteCacheState> read,
        WriteCacheState baseline, IProgress<string>? progress = null, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        if (limit < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        // Reject faults already present in the initial snapshot, even if later cleared.
        try { EnsureHealthy(baseline); }
        catch (CacheDrainingException) { /* Only this transient condition may settle. */ }
        var timer = Stopwatch.StartNew();
        var reported = false;
        while (true)
        {
            var state = read();
            if (state.Instance != baseline.Instance || state.DeviceBytes != baseline.DeviceBytes || state.Errors != baseline.Errors)
                throw new IOException("Driver identity, disk size or error count changed while waiting for configuration state.");
            try { EnsureHealthy(state); return state; }
            catch (CacheDrainingException)
            {
                if (timer.Elapsed >= limit)
                    throw new TimeoutException($"Cache remained busy draining for {limit.TotalSeconds:F0} seconds while verifying configuration.");
                if (!reported) { progress?.Report($"Waiting for transient draining before verifying configuration (up to {limit.TotalSeconds:F0} seconds)."); reported = true; }
                Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(200, Math.Max(1, (limit - timer.Elapsed).TotalMilliseconds))));
            }
        }
    }

    public static void EnsureHealthy(WriteCacheState state)
    {
        if (state.Faulted || state.LastError != 0 || state.Removed || state.Suspended)
            throw new IOException("Cache faulted, removed or suspended. Inspect status; no automatic recovery performed.");
        if (state.Draining) throw new CacheDrainingException();
    }
}
