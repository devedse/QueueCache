using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;

namespace QueueCache.Operations.ManagedDisks;

[SupportedOSPlatform("windows")]
public sealed class ManagedDiskBrokerClient : IManagedDiskService
{
    public Task<IReadOnlyList<ManagedDiskCapability>> CapabilitiesAsync(CancellationToken token = default) =>
        CallAsync<IReadOnlyList<ManagedDiskCapability>>(new(1, Guid.NewGuid(), "capabilities"), null, token);
    public Task<ImageInspection> InspectAsync(string path, CancellationToken token = default) =>
        CallAsync<ImageInspection>(new(1, Guid.NewGuid(), "inspect", Path: path), null, token);
    public Task<IReadOnlyList<ManagedDiskRecord>> ListAsync(CancellationToken token = default) =>
        CallAsync<IReadOnlyList<ManagedDiskRecord>>(new(1, Guid.NewGuid(), "list"), null, token);
    public Task<ManagedDiskRuntime> CreateAsync(ManagedDiskDefinition definition, IProgress<ManagedDiskProgress>? progress = null, CancellationToken token = default) =>
        CallAsync<ManagedDiskRuntime>(new(1, Guid.NewGuid(), "create", Definition: definition), progress, token);
    public Task<ManagedDiskOperationResult> ExecuteAsync(ManagedDiskRequest request, IProgress<ManagedDiskProgress>? progress = null, CancellationToken token = default) =>
        CallAsync<ManagedDiskOperationResult>(new(1, Guid.NewGuid(), "action", Action: request), progress, token);
    private static async Task<T> CallAsync<T>(ManagedBrokerRequest request, IProgress<ManagedDiskProgress>? progress, CancellationToken token)
    {
        ManagedDiskBrokerProtocol.Validate(request);
        using var pipe = new NamedPipeClientStream(".", ManagedDiskBrokerProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(token); connectDeadline.CancelAfter(TimeSpan.FromSeconds(5));
        try { await pipe.ConnectAsync(connectDeadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("The managed-disk service is unavailable. Install the matching QueueCache package and start its Windows service."); }
        WindowsManagedBrokerService.AuthenticateServer(pipe);
        await ManagedDiskBrokerProtocol.WriteAsync(pipe, request, token);
        var readOnly = request.Operation is "capabilities" or "inspect" or "list";
        using var observationDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (readOnly) observationDeadline.CancelAfter(TimeSpan.FromSeconds(request.Operation == "inspect" ? 30 : 5));
        using var finished = new CancellationTokenSource();
        var cancellation = SendCancellationAsync();
        var records = new List<ManagedDiskRecord>();
        try
        {
            while (true)
            {
                // Once sent, cancellation is a request. Await the actual terminal
                // outcome: pointer commit/Windows formatting cannot be rolled back by disconnecting.
                var reply = await ManagedDiskBrokerProtocol.ReadAsync<ManagedBrokerReply>(pipe, readOnly ? observationDeadline.Token : CancellationToken.None)
                    ?? throw new EndOfStreamException("Managed broker disconnected before the terminal result. Refresh/recover the resource before retrying.");
                if (reply.Version != 1 || reply.OperationId != request.OperationId) throw new InvalidDataException("Broker response identity/version mismatch.");
                if (reply.Kind == "progress") { if (reply.Progress is null) throw new InvalidDataException("Missing broker progress."); progress?.Report(reply.Progress); continue; }
                if (reply.Kind == "item")
                {
                    if (request.Operation != "list" || reply.Result is null || records.Count >= 1024) throw new InvalidDataException("Unexpected managed list item.");
                    var record = reply.Result.Value.Deserialize<ManagedDiskRecord>(ManagedDiskBrokerProtocol.Json) ?? throw new InvalidDataException("Incomplete managed record.");
                    record.Validate();
                    if (records.Any(r => r.ResourceId == record.ResourceId)) throw new InvalidDataException("Duplicate managed list resource.");
                    records.Add(record); continue;
                }
                if (reply.Kind == "error")
                {
                    if (reply.Cancelled) throw new OperationCanceledException(reply.Error, token);
                    throw new IOException(reply.Error ?? "Managed operation failed without an error detail.");
                }
                if (reply.Kind != "result" || reply.Result is null) throw new InvalidDataException("Invalid broker terminal response.");
                if (request.Operation == "list")
                {
                    var end = reply.Result.Value.Deserialize<ManagedBrokerListEnd>(ManagedDiskBrokerProtocol.Json);
                    if (end is null || end.Count != records.Count) throw new InvalidDataException("Incomplete managed disk list.");
                    return (T)(object)(IReadOnlyList<ManagedDiskRecord>)records;
                }
                return reply.Result.Value.Deserialize<T>(ManagedDiskBrokerProtocol.Json) ?? throw new InvalidDataException("Incomplete managed broker result.");
            }
        }
        catch (OperationCanceledException) when (readOnly && !token.IsCancellationRequested)
        { throw new IOException("Managed disk state did not respond before the observation deadline. No mutation was requested; live counters are unavailable."); }
        finally { finished.Cancel(); await cancellation; }
        async Task SendCancellationAsync()
        {
            using var signal = CancellationTokenSource.CreateLinkedTokenSource(token, finished.Token);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, signal.Token); }
            catch (OperationCanceledException) { }
            if (token.IsCancellationRequested && !finished.IsCancellationRequested)
            {
                using var sendDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await ManagedDiskBrokerProtocol.WriteAsync(pipe, new ManagedBrokerRequest(1, request.OperationId, "cancel"), sendDeadline.Token); }
                catch (IOException) { }
                catch (OperationCanceledException) { }
            }
        }
    }
}
