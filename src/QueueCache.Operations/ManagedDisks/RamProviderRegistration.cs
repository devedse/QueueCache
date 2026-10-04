namespace QueueCache.Operations.ManagedDisks;

/// <summary>Repair is permitted only for a proven unbound, inactive root node.
/// An unavailable observation is never evidence that private RAM is absent.</summary>
public sealed record RamProviderRegistration(string? Service, uint? DevNodeStatus, bool Disconnected,
    bool ModulesObserved, bool ProviderModuleLoaded)
{
    public bool CanRepairUnbound => Service is null && ModulesObserved && !ProviderModuleLoaded &&
        (DevNodeStatus is { } status ? (status & 8) == 0 : Disconnected); // DN_STARTED
}
