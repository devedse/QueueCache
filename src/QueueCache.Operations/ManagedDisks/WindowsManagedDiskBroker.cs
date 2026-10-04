using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace QueueCache.Operations.ManagedDisks;

/// <summary>Authenticated local service endpoint; client disconnect cancels an operation, never device lifetime.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsManagedDiskBroker(IManagedDiskService service)
{
    private readonly ConcurrentDictionary<int, Task> clients = new();
    private readonly SemaphoreSlim capacity = new(15, 15);
    private int sequence;
    public async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await capacity.WaitAsync(token);
                foreach (var done in clients.Where(c => c.Value.IsCompleted).ToArray())
                { await done.Value; clients.TryRemove(done.Key, out _); }
                var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
                // Each further server instance needs FILE_CREATE_PIPE_INSTANCE under the
                // existing instance's DACL; only the LocalSystem broker may create them.
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    PipeAccessRights.ReadWrite, AccessControlType.Allow));
                var pipe = NamedPipeServerStreamAcl.Create(ManagedDiskBrokerProtocol.PipeName, PipeDirection.InOut, 16,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, security);
                try { await pipe.WaitForConnectionAsync(token); }
                catch { pipe.Dispose(); capacity.Release(); throw; }
                var id = Interlocked.Increment(ref sequence);
                clients[id] = ServeOwnedAsync(pipe, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { await Task.WhenAll(clients.Values); }
    }
    private async Task ServeOwnedAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        try { await ServeAsync(pipe, token); }
        catch (Exception ex) { WindowsManagedBrokerService.Log("Broker connection failed: " + ex.Message); }
        finally { capacity.Release(); }
    }
    private static void Authorize(NamedPipeServerStream pipe)
    {
        var machine = new StringBuilder(256);
        if (!GetNamedPipeClientComputerNameW(pipe.SafePipeHandle, machine, (uint)machine.Capacity) ||
            !machine.ToString().TrimStart('\\').Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Managed disk control accepts local clients only.");
        var permitted = false;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(ifImpersonating: true);
            permitted = identity is not null && (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true ||
                new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
        });
        if (!permitted) throw new UnauthorizedAccessException("Run QueueCache as an elevated administrator to manage disks.");
    }
    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken serviceToken)
    {
        using (pipe)
        using (var operation = CancellationTokenSource.CreateLinkedTokenSource(serviceToken))
        {
            Guid id = Guid.Empty;
            var outbound = Channel.CreateBounded<ManagedBrokerReply>(new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
            var sending = SendAsync();
            Task? cancelReader = null;
            try
            {
                Authorize(pipe);
                using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(serviceToken); requestDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                var request = await ManagedDiskBrokerProtocol.ReadAsync<ManagedBrokerRequest>(pipe, requestDeadline.Token)
                    ?? throw new EndOfStreamException("The client sent no broker request.");
                id = request.OperationId; ManagedDiskBrokerProtocol.Validate(request);
                if (request.Operation == "cancel") throw new InvalidDataException("Cancellation must belong to a running operation.");
                cancelReader = ReadCancellationAsync(request.OperationId);
                var progress = new BrokerProgress(value => outbound.Writer.TryWrite(new(1, id, "progress", Progress: value)));
                if (request.Operation == "list")
                {
                    var records = await service.ListAsync(operation.Token);
                    foreach (var record in records)
                        await outbound.Writer.WriteAsync(new(1, id, "item", JsonSerializer.SerializeToElement(record, ManagedDiskBrokerProtocol.Json)), operation.Token);
                    await outbound.Writer.WriteAsync(new(1, id, "result", JsonSerializer.SerializeToElement(new ManagedBrokerListEnd(records.Count), ManagedDiskBrokerProtocol.Json)), operation.Token);
                    return;
                }
                object result = request.Operation switch
                {
                    "capabilities" => await service.CapabilitiesAsync(operation.Token),
                    "inspect" => await service.InspectAsync(request.Path!, operation.Token),
                    "create" => await service.CreateAsync(request.Definition!, progress, operation.Token),
                    "action" => await service.ExecuteAsync(request.Action!, progress, operation.Token),
                    _ => throw new InvalidDataException("Unsupported broker operation.")
                };
                await outbound.Writer.WriteAsync(new(1, id, "result", JsonSerializer.SerializeToElement(result, ManagedDiskBrokerProtocol.Json)), CancellationToken.None);
            }
            catch (Exception ex)
            {
                await outbound.Writer.WriteAsync(new(1, id, "error", Error: ex.Message, Cancelled: ex is OperationCanceledException), CancellationToken.None);
            }
            finally
            {
                outbound.Writer.TryComplete();
                try { await sending; } catch (IOException) { }
                operation.Cancel();
                if (cancelReader is not null) try { await cancelReader; } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            }
            async Task SendAsync()
            {
                try
                {
                    await foreach (var frame in outbound.Reader.ReadAllAsync())
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        await ManagedDiskBrokerProtocol.WriteAsync(pipe, frame, deadline.Token);
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                { operation.Cancel(); outbound.Writer.TryComplete(ex); }
            }
            async Task ReadCancellationAsync(Guid operationId)
            {
                try
                {
                    var frame = await ManagedDiskBrokerProtocol.ReadAsync<ManagedBrokerRequest>(pipe, operation.Token);
                    if (frame is null) { operation.Cancel(); return; }
                    ManagedDiskBrokerProtocol.Validate(frame);
                    if (frame.Operation != "cancel" || frame.OperationId != operationId) throw new InvalidDataException("Cancellation identity does not match the running request.");
                    operation.Cancel();
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { operation.Cancel(); }
                catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
            }
        }
    }
    private sealed class BrokerProgress(Action<ManagedDiskProgress> report) : IProgress<ManagedDiskProgress> { public void Report(ManagedDiskProgress value) => report(value); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientComputerNameW(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, StringBuilder name, uint length);
}
