namespace QueueCache.Operations.ManagedDisks;

public enum ManagedDiskAction { Start, Stop, Flush, Save, Export, Format, ChangeCache, SetStartup, RemoveDefinition, DeleteImage, Recover, ConfigureStopped }
public sealed record ManagedDiskExpected(Guid ResourceId, Guid BootEpoch, ulong CreationGeneration, ulong WriteGeneration)
{
    public static ManagedDiskExpected From(ManagedDiskRuntime runtime) => new(runtime.ResourceId, runtime.BootEpoch, runtime.CreationGeneration, runtime.WriteGeneration);
    public void Validate(ManagedDiskRecord record, bool acknowledgeGeneration = false)
    {
        if (ResourceId != record.ResourceId || record.Runtime is null) throw new IOException("Refresh the managed disk identity before changing it.");
        record.Runtime.CheckExpected(BootEpoch, CreationGeneration);
        if (acknowledgeGeneration && record.Runtime.WriteGeneration != WriteGeneration)
            throw new IOException("RAM contents changed after the erase/discard preview. Refresh and acknowledge the current generation.");
    }
}
public sealed record ManagedDiskRequest(Guid ResourceId, ManagedDiskAction Action, ManagedDiskExpected? Expected = null,
    ManagedDiskStopIntent? StopIntent = null, string? Path = null, bool CommitExport = false,
    bool AcceptDiscard = false, bool AcceptErase = false, CacheConfiguration? Cache = null,
    bool AcceptVolatileWrites = false, bool? StartAtBoot = null, bool? SaveBeforeStopping = null, bool? SaveDuringShutdown = null, string? Label = null,
    char? PreferredLetter = null, ulong? CapacityBytes = null);
public sealed record ManagedDiskOperationResult(ManagedDiskRecord Record, string Message, string? ImagePath = null);

public static class ManagedDiskConfiguration
{
    public static ManagedDiskDefinition EditStopped(ManagedDiskRecord record, ManagedDiskRequest request)
    {
        if (record.Runtime?.State != ManagedDiskState.Stopped || record.Native is not null || record.PhysicalDiskNumber is not null || record.VolumePath is not null)
            throw new IOException("Stop and reconcile the managed disk before editing its creation settings.");
        if (request.PreferredLetter is null && request.Label is null && request.CapacityBytes is null)
            throw new ArgumentException("Choose a preferred letter, label or pure-RAM capacity to change.");
        if (request.CapacityBytes is not null && record.Definition.Mode != ManagedDiskMode.EphemeralRam)
            throw new NotSupportedException("Existing images cannot be resized through creation settings.");
        var definition = record.Definition with { PreferredLetter = request.PreferredLetter ?? record.Definition.PreferredLetter,
            Label = request.Label ?? record.Definition.Label, CapacityBytes = request.CapacityBytes ?? record.Definition.CapacityBytes };
        definition.Validate(); return definition;
    }
}
