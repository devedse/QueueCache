namespace QueueCache.Operations.ManagedDisks;

public enum ManagedDiskAction { Start, Stop, Flush, Save, Export, Format, ChangeCache, SetStartup, RemoveDefinition, DeleteImage, Recover }
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
    bool AcceptVolatileWrites = false, bool? StartAtBoot = null, bool? SaveBeforeStopping = null, bool? SaveDuringShutdown = null, string? Label = null);
public sealed record ManagedDiskOperationResult(ManagedDiskRecord Record, string Message, string? ImagePath = null);
