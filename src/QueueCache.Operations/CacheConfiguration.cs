using System.Diagnostics;
using System.Runtime.Versioning;
using QueueCache.Management;

namespace QueueCache.Operations;

public enum CachePreset
{
    Fast, Strict
}
public sealed class CacheDrainingException() : IOException("Cache is busy draining; wait for draining to finish before changing settings.");
public sealed record CacheConfiguration(int BudgetMiB = 4096, CachePreset Preset = CachePreset.Fast, bool Enabled = true)
{
    public CacheOptions Options { get; init; } = new();
    public static CacheConfiguration FromState(WriteCacheState state) => new((int)(state.BudgetBytes >> 20),
        state.UnsafeDefer ? CachePreset.Fast : CachePreset.Strict, state.Enabled)
    {
        Options = state.Options ?? new()
    };

    /// <summary>
    /// Validates a requested configuration before any driver state changes. RAM
    /// availability and target identity require live system state and are checked by
    /// <see cref="ConfigurationManager.Apply"/> after this local validation succeeds.
    /// </summary>
    public void Validate(bool acceptVolatileFlush)
    {
        if (BudgetMiB is < 1 or > MemoryBudget.MaximumMiB)
            throw new ArgumentException("Budget must be 1..131072 MiB; actual availability is checked at apply time.");
        if (Options is null)
            throw new ArgumentException("Cache policies are required.");
        Options.Validate();
        if (!Enum.IsDefined(Preset))
            throw new ArgumentException("Unknown cache preset.");
        if (Preset == CachePreset.Fast && !acceptVolatileFlush)
            throw new ArgumentException("Fast mode acknowledges writes/flushes in volatile RAM. Explicitly accept volatile flushes first.");
    }
}

internal static class ActivationSafety
{
    internal static void ValidateTarget(int usagePathCount)
    {
        if (usagePathCount < 0)
            throw new InvalidDataException("Driver reported an invalid paging/hibernation/dump path count.");
    }
}

/// <summary>The driver operations Apply needs; a seam so failure and rollback can be tested without a driver.</summary>
internal interface ICacheControl
{
    WriteCacheState GetWriteCacheState();
    void Control(WriteCacheAction action, ulong budgetBytes = 0, ulong value = 0);
    void SetOptions(CacheOptions options);
}

[SupportedOSPlatform("windows")]
internal sealed class CacheDeviceControl(CacheDevice device) : ICacheControl
{
    public WriteCacheState GetWriteCacheState() => device.GetWriteCacheState();
    public void Control(WriteCacheAction action, ulong budgetBytes = 0, ulong value = 0) =>
        device.Control(action, budgetBytes, value);
    public void SetOptions(CacheOptions options) => device.SetOptions(options);
}

/// <summary>A settings change failed; the previous settings were restored (or, if not, why).</summary>
public sealed class ConfigurationNotAppliedException(string message, Exception inner, bool restored)
    : IOException(message, inner)
{
    public bool PreviousSettingsRestored { get; } = restored;
}

/// <summary>Both frontends use this orchestrator. A change applies completely or the previous settings are
/// restored; if even that fails the error says exactly what state the cache was left in.</summary>
[SupportedOSPlatform("windows")]
public static class ConfigurationManager
{
    public static WriteCacheState Apply(DiskTarget target, CacheConfiguration configuration,
        bool acceptVolatileFlush, IProgress<string>? progress = null)
        => ApplyCore(target, configuration, acceptVolatileFlush, progress);

    private static WriteCacheState ApplyCore(DiskTarget target, CacheConfiguration configuration,
        bool acceptVolatileFlush, IProgress<string>? progress)
    {
        using var gate = ConfigurationGate.Enter(target.Instance);
        using var hostProtectionGate = ManagedDisks.ManagedDiskHostProtection.EnterPolicyGate();
        configuration.Validate(acceptVolatileFlush);
        // An inspected volume can be remapped between inventory and Apply (including
        // saved-profile startup). Recheck its physical extent and PnP identity at
        // the last boundary before opening the disk for any state change.
        target.ValidateCurrent();
        ManagedDisks.ManagedDiskHostProtection.ValidateCacheChange(target.VolumeId, configuration);
        using var device = new CacheDevice(target.Device, writable: true);
        var initial = device.GetWriteCacheState();
        var state = WaitForHealthyState(device.GetWriteCacheState, initial, progress);
        if (!state.SupportsReadWrite)
            throw new NotSupportedException("Install the matching read/write-cache driver and restart Windows before applying settings.");
        ActivationSafety.ValidateTarget(device.GetStatistics().PagingPathCount);
        // Reject an unsupported new policy before disabling/draining the existing cache.
        if ((configuration.Options.Drain == DrainAlgorithm.Deferred || configuration.Options.MaxDirtyAgeMs > 300000) && !state.SupportsDeferredDrain)
            throw new NotSupportedException("The loaded driver does not support Deferred draining/one-hour ages. Install the newer driver and restart Windows first.");
        if (state.DeviceBytes != (ulong)target.Bytes)
            throw new IOException("Disk size changed.");
        var budget = (ulong)configuration.BudgetMiB << 20;
        if (state.Enabled == configuration.Enabled && state.BudgetBytes == budget &&
            state.UnsafeDefer == (configuration.Preset == CachePreset.Fast) && state.Options == configuration.Options &&
            (!configuration.Enabled || state.Operational))
            return state;
        MemoryBudget.ValidateIncrease(state.BudgetBytes, budget);
        return ApplySteps(new CacheDeviceControl(device), state, configuration, progress);
    }

    internal static WriteCacheState ApplySteps(ICacheControl device, WriteCacheState state,
        CacheConfiguration configuration, IProgress<string>? progress = null, TimeSpan? settleTimeout = null)
    {
        progress?.Report("Draining and disabling before applying configuration. This may take time.");
        try
        {
            device.Control(WriteCacheAction.Disable);
        }
        catch (Exception exception)
        {
            // Nothing has been changed yet: the cache could not be emptied (for example a disk error).
            throw new ConfigurationNotAppliedException("The new settings were not applied: the cache could not be " +
                $"emptied first ({exception.Message}). Its existing settings are unchanged.", exception, true);
        }
        var previous = CacheConfiguration.FromState(state);
        try
        {
            progress?.Report("Applying preset and RAM budget.");
            return SetAndVerify(device, state, configuration, progress, settleTimeout);
        }
        catch (Exception failure)
        {
            progress?.Report("The new settings failed; restoring the previous settings.");
            try
            {
                device.Control(WriteCacheAction.Disable);
                SetAndVerify(device, state, previous, progress, settleTimeout);
            }
            catch (Exception rollback)
            {
                throw new ConfigurationNotAppliedException($"The new settings were not applied ({failure.Message}) " +
                    $"and the previous settings could not be restored ({rollback.Message}). " +
                    $"The cache is now: {Describe(device)}.", failure, false);
            }
            throw new ConfigurationNotAppliedException($"The new settings were not applied ({failure.Message}). " +
                "The previous settings were restored.", failure, true);
        }
    }

    // Driver order: preset and options only while disabled; Configure frees the old memory before
    // allocating the new size, so it is the step most likely to fail and runs last before Enable.
    private static WriteCacheState SetAndVerify(ICacheControl device, WriteCacheState baseline,
        CacheConfiguration target, IProgress<string>? progress, TimeSpan? settleTimeout)
    {
        var budget = (ulong)target.BudgetMiB << 20;
        device.Control(WriteCacheAction.FlushPolicy, value: target.Preset == CachePreset.Fast ? 1UL : 0UL);
        device.SetOptions(target.Options);
        var current = device.GetWriteCacheState().BudgetBytes;
        if (current != budget)
            device.Control(budget == 0 ? WriteCacheAction.Release : WriteCacheAction.Configure, budget);
        if (target.Enabled)
            device.Control(WriteCacheAction.Enable);
        // A background barrier can start immediately after Enable completes.
        // Wait on snapshots, not by replaying Disable/Configure/Enable.
        var result = WaitForHealthyState(device.GetWriteCacheState, baseline, progress, settleTimeout);
        if (result.Enabled != target.Enabled || result.BudgetBytes != budget ||
            result.UnsafeDefer != (target.Preset == CachePreset.Fast) || result.Options != target.Options ||
            result.Instance != baseline.Instance || (target.Enabled && !result.Operational))
            throw new IOException("Driver state does not match the requested configuration.");
        progress?.Report("Configuration applied and verified.");
        return result;
    }

    private static string Describe(ICacheControl device)
    {
        try
        {
            var state = device.GetWriteCacheState();
            return $"{(state.Enabled ? "enabled" : "disabled")}, {state.BudgetBytes >> 20} MiB, " +
                $"{(state.UnsafeDefer ? "Fast" : "Strict")}, error 0x{state.LastError:X8}";
        }
        catch (Exception exception)
        {
            return $"unknown ({exception.Message})";
        }
    }

    internal static WriteCacheState WaitForHealthyState(Func<WriteCacheState> read,
        WriteCacheState baseline, IProgress<string>? progress = null, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        if (limit < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        // Reject faults already present in the initial snapshot, even if later cleared.
        try
        {
            EnsureHealthy(baseline);
        }
        catch (CacheDrainingException) { /* Only this transient condition may settle. */ }
        var timer = Stopwatch.StartNew();
        var reported = false;
        while (true)
        {
            var state = read();
            if (state.Instance != baseline.Instance || state.DeviceBytes != baseline.DeviceBytes || state.Errors != baseline.Errors)
                throw new IOException("Driver identity, disk size or error count changed while waiting for configuration state.");
            try
            {
                EnsureHealthy(state);
                return state;
            }
            catch (CacheDrainingException)
            {
                if (timer.Elapsed >= limit)
                    throw new TimeoutException($"Cache remained busy draining for {limit.TotalSeconds:F0} seconds while verifying configuration.");
                if (!reported)
                {
                    progress?.Report($"Waiting for transient draining before verifying configuration (up to {limit.TotalSeconds:F0} seconds).");
                    reported = true;
                }
                Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(200, Math.Max(1, (limit - timer.Elapsed).TotalMilliseconds))));
            }
        }
    }

    public static void EnsureHealthy(WriteCacheState state)
    {
        if (state.Faulted || state.LastError != 0 || state.Removed || state.Suspended)
            throw new IOException("Cache faulted, removed or suspended. Inspect status; no automatic recovery performed.");
        if (state.Draining)
            throw new CacheDrainingException();
    }
}
