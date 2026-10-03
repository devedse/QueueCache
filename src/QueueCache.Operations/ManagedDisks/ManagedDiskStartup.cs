namespace QueueCache.Operations.ManagedDisks;

public enum ManagedDiskStartupAction { LeaveStopped, AdoptLive, CreateAndFormatEmpty, MountImageAndCache, LoadCommittedImage }

/// <summary>Receives a proven Windows startup epoch; UI/service PID or uptime is not a boot classifier.</summary>
public static class ManagedDiskStartup
{
    public static ManagedDiskStartupAction Decide(ManagedDiskDefinition definition, Guid startupEpoch, ManagedDiskRuntime? live)
    {
        definition.Validate();
        if (startupEpoch == Guid.Empty)
            throw new ArgumentException("An authoritative startup epoch is required.");
        if (live is not null)
        {
            if (live.ResourceId != definition.ResourceId || live.Mode != definition.Mode || live.CreationGeneration == 0)
                throw new InvalidDataException("Live managed disk identity does not match its definition.");
            if (live.BootEpoch == startupEpoch && live.State != ManagedDiskState.Stopped)
                return ManagedDiskStartupAction.AdoptLive; // Also retain dirty, frozen and faulted devices for recovery.
            if (live.State != ManagedDiskState.Stopped)
                throw new IOException("A device from another startup is still present; reconcile it before creating another disk.");
        }
        if (!definition.StartAtBoot)
            return ManagedDiskStartupAction.LeaveStopped;
        return definition.Mode switch
        {
            ManagedDiskMode.EphemeralRam => ManagedDiskStartupAction.CreateAndFormatEmpty,
            ManagedDiskMode.CachedVhdx => ManagedDiskStartupAction.MountImageAndCache,
            ManagedDiskMode.ImageInRam => ManagedDiskStartupAction.LoadCommittedImage,
            _ => throw new ArgumentOutOfRangeException(nameof(definition.Mode))
        };
    }
}
