using System.Runtime.Versioning;

namespace QueueCache.Operations.ManagedDisks;

public interface IManagedDiskService
{
    Task<IReadOnlyList<ManagedDiskCapability>> CapabilitiesAsync(CancellationToken token = default);
    Task<ImageInspection> InspectAsync(string path, CancellationToken token = default);
    Task<ManagedDiskRuntime> CreateAsync(ManagedDiskDefinition definition,
        IProgress<ManagedDiskProgress>? progress = null, CancellationToken token = default);
}

/// <summary>Inspection is available now. Mutation waits for qualified native ownership and broker lifetime.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsManagedDiskService : IManagedDiskService
{
    public Task<IReadOnlyList<ManagedDiskCapability>> CapabilitiesAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ManagedDiskCapability>>([
            new(ManagedDiskMode.EphemeralRam, false, "RAM disk creation is not available in this build yet."),
            new(ManagedDiskMode.CachedVhdx, false, "Creating or mounting managed VHDX disks is not available in this build yet."),
            new(ManagedDiskMode.ImageInRam, false, "Loading and saving a complete VHDX in RAM is not available in this build yet.")
        ]);
    }
    public Task<ImageInspection> InspectAsync(string path, CancellationToken token = default) =>
        Task.Run(() => WindowsVirtualDisk.Inspect(path), token);
    public async Task<ManagedDiskRuntime> CreateAsync(ManagedDiskDefinition definition,
        IProgress<ManagedDiskProgress>? progress = null, CancellationToken token = default)
    {
        definition.Validate();
        (await CapabilitiesAsync(token)).Single(c => c.Mode == definition.Mode).RequireAvailable();
        throw new NotSupportedException("The installed managed-disk backend is unavailable.");
    }
}
