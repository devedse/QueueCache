namespace QueueCache.Operations.ManagedDisks;

public sealed record ManagedDiskProgress(ManagedDiskState Stage, string Message, ulong? CompletedBytes = null, ulong? TotalBytes = null);

/// <summary>
/// Native transaction owns unpublished allocation and publication isolation. After successful
/// publication, ownership transfers to the broker; disposing the transaction must retain the live device.
/// </summary>
public interface IManagedDiskCreation : IAsyncDisposable
{
    Guid ResourceId { get; }
    Guid BootEpoch { get; }
    ulong CreationGeneration { get; }
    ulong WriteGeneration { get; }
    ulong? DurableBaselineGeneration => null;
    ILogicalDisk? RamStorage { get; }
    Task InitializeAndFormatAsync(ManagedDiskDefinition definition, CancellationToken token);
    Task SaveInitialImageAsync(ManagedDiskDefinition definition, CancellationToken token);
    Task RecordImportedImageAsync(ImageInspection image, LogicalImageDigest digest, CancellationToken token) => Task.CompletedTask;
    Task<string> PublishAsync(ManagedDiskDefinition definition, CancellationToken token);
    Task AbortAsync(CancellationToken token);
}

public interface IImageReadView : IAsyncDisposable
{
    ILogicalDisk LogicalStorage { get; }
    ImageInspection Identity { get; }
    // Disposal must release and detach the staging view before publication can happen.
}

public interface IManagedDiskBackend
{
    Task<ManagedDiskCapability> CapabilityAsync(ManagedDiskMode mode, CancellationToken token);
    Task<ImageInspection> InspectAsync(string path, CancellationToken token);
    Task<IImageReadView> OpenImportAsync(ImageInspection expected, CancellationToken token);
    Task<IManagedDiskCreation> CreateUnpublishedAsync(ManagedDiskDefinition definition, CancellationToken token);
}

/// <summary>All modes share validation, owned publication and failure cleanup. Only storage operations vary.</summary>
public sealed class ManagedDiskCreationCoordinator(IManagedDiskBackend backend)
{
    public async Task<ManagedDiskRuntime> CreateAsync(ManagedDiskDefinition definition,
        IProgress<ManagedDiskProgress>? progress = null, CancellationToken token = default)
    {
        definition.Validate();
        var capability = await backend.CapabilityAsync(definition.Mode, token);
        if (capability.Mode != definition.Mode)
            throw new InvalidDataException("Backend capability mode mismatch.");
        capability.RequireAvailable();
        ImageInspection? source = null;
        if (definition.Source == ManagedDiskSource.OpenExisting)
        {
            source = await backend.InspectAsync(definition.ImagePath!, token);
            source.ValidateFor(definition);
        }
        progress?.Report(new(ManagedDiskState.Creating, "Creating an owned, unpublished disk."));
        await using var creation = await backend.CreateUnpublishedAsync(definition, token);
        try
        {
            if (creation.ResourceId != definition.ResourceId || creation.BootEpoch == Guid.Empty || creation.CreationGeneration == 0)
                throw new InvalidDataException("Native creation identity is invalid.");
            if (definition.Mode == ManagedDiskMode.ImageInRam && source is not null)
            {
                progress?.Report(new(ManagedDiskState.Loading, "Loading every logical image sector into RAM."));
                await using (var import = await backend.OpenImportAsync(source, token))
                {
                    if (import.Identity != source)
                        throw new IOException("The source image changed before import.");
                    var destination = creation.RamStorage ?? throw new InvalidDataException("No RAM storage for full-image import.");
                    if (destination.CapacityBytes != definition.CapacityBytes || destination.SectorBytes != definition.SectorBytes)
                        throw new InvalidDataException("Native RAM capacity does not match the requested disk.");
                    var digest = await LogicalImageTransfer.CopyAsync(import.LogicalStorage, destination,
                        progress is null ? null : new TransferProgress(progress), token);
                    await LogicalImageTransfer.VerifyAsync(destination, digest, token);
                    await creation.RecordImportedImageAsync(source, digest, token);
                }
                // Native image view is detached before the copied GPT identities are published.
            }
            if (definition.Source == ManagedDiskSource.CreateNew)
            {
                progress?.Report(new(ManagedDiskState.Formatting, "Initializing and formatting the new owned disk."));
                await creation.InitializeAndFormatAsync(definition, token);
                if (definition.Mode == ManagedDiskMode.ImageInRam)
                    await creation.SaveInitialImageAsync(definition, token);
            }
            ulong? baseline = definition.Mode == ManagedDiskMode.ImageInRam ? creation.DurableBaselineGeneration ?? creation.WriteGeneration : null;
            token.ThrowIfCancellationRequested();
            var volume = await creation.PublishAsync(definition, token);
            if (string.IsNullOrWhiteSpace(volume))
                throw new InvalidDataException("Publication did not resolve a managed volume.");
            var result = new ManagedDiskRuntime(definition.ResourceId, creation.BootEpoch, creation.CreationGeneration,
                definition.Mode, ManagedDiskState.Ready, creation.WriteGeneration, baseline, volume);
            return result;
        }
        catch (Exception operationFailure)
        {
            // Cleanup has an independent deadline, even if the caller cancelled.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await creation.AbortAsync(cleanup.Token); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Managed disk creation failed and owned-resource cleanup requires recovery.", operationFailure, cleanupFailure);
            }
            throw;
        }
    }

    private sealed class TransferProgress(IProgress<ManagedDiskProgress> target) : IProgress<ImageTransferProgress>
    {
        public void Report(ImageTransferProgress value) => target.Report(new(ManagedDiskState.Loading,
            "Loading logical image sectors.", value.CompletedBytes, value.TotalBytes));
    }
}
