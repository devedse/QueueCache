using System.Runtime.Versioning;

namespace QueueCache.Operations.ManagedDisks;

public interface IManagedDiskService
{
    Task<IReadOnlyList<ManagedDiskCapability>> CapabilitiesAsync(CancellationToken token = default);
    Task<ImageInspection> InspectAsync(string path, CancellationToken token = default);
    Task<ManagedDiskRuntime> CreateAsync(ManagedDiskDefinition definition,
        IProgress<ManagedDiskProgress>? progress = null, CancellationToken token = default);
    Task<IReadOnlyList<ManagedDiskRecord>> ListAsync(CancellationToken token = default) => throw new NotSupportedException("Managed disk listing is unavailable.");
    Task<ManagedDiskOperationResult> ExecuteAsync(ManagedDiskRequest request,
        IProgress<ManagedDiskProgress>? progress = null, CancellationToken token = default) => throw new NotSupportedException("Managed disk actions are unavailable.");
}

/// <summary>Both product frontends use the privileged service; inspection remains a read-only operation.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsManagedDiskService : IManagedDiskService
{
    private readonly ManagedDiskBrokerClient client = new();
    public async Task<IReadOnlyList<ManagedDiskCapability>> CapabilitiesAsync(CancellationToken token = default)
    {
        try { return await client.CapabilitiesAsync(token); }
        catch (IOException ex) { return Enum.GetValues<ManagedDiskMode>().Select(mode => new ManagedDiskCapability(mode, false, ex.Message)).ToArray(); }
    }
    public Task<ImageInspection> InspectAsync(string path, CancellationToken token = default) =>
        Task.Run(() => WindowsVirtualDisk.Inspect(path), token);
    public Task<ManagedDiskRuntime> CreateAsync(ManagedDiskDefinition definition, IProgress<ManagedDiskProgress>? progress = null, CancellationToken token = default) => client.CreateAsync(definition, progress, token);
    public Task<IReadOnlyList<ManagedDiskRecord>> ListAsync(CancellationToken token = default) => client.ListAsync(token);
    public Task<ManagedDiskOperationResult> ExecuteAsync(ManagedDiskRequest request, IProgress<ManagedDiskProgress>? progress = null, CancellationToken token = default) => client.ExecuteAsync(request, progress, token);
}
