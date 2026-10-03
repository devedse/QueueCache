using System.Buffers.Binary;
using QueueCache.Operations.ManagedDisks;

internal static class ManagedBrokerTests
{
    public static async Task RunAsync()
    {
        var request = new ManagedBrokerRequest(1, Guid.NewGuid(), "create", ManagedDiskDefinition.New(ManagedDiskMode.EphemeralRam));
        using var wire = new MemoryStream(); await ManagedDiskBrokerProtocol.WriteAsync(wire, request);
        var bytes = wire.ToArray();
        using var fragmented = new Fragments(bytes);
        var decoded = await ManagedDiskBrokerProtocol.ReadAsync<ManagedBrokerRequest>(fragmented);
        Check(decoded == request, "fragmented broker frames preserve the complete typed definition");
        Check(await ManagedDiskBrokerProtocol.ReadAsync<ManagedBrokerRequest>(fragmented) is null, "clean frame-boundary EOF is distinguishable from an incomplete frame");
        foreach (var length in new[] { 0, -1, ManagedDiskBrokerProtocol.MaximumFrameBytes + 1 })
        {
            var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, length);
            await RejectAsync<InvalidDataException>(() => ManagedDiskBrokerProtocol.ReadAsync<ManagedBrokerRequest>(new MemoryStream(header)));
        }
        foreach (var end in new[] { 1, 3, bytes.Length - 1 })
            await RejectAsync<EndOfStreamException>(() => ManagedDiskBrokerProtocol.ReadAsync<ManagedBrokerRequest>(new MemoryStream(bytes[..end])));
        Reject(() => ManagedDiskBrokerProtocol.Validate(request with { Version = 2 }));
        Reject(() => ManagedDiskBrokerProtocol.Validate(request with { OperationId = Guid.Empty }));
        Reject(() => ManagedDiskBrokerProtocol.Validate(request with { Operation = "cancel" }));
        Reject(() => ManagedDiskBrokerProtocol.Validate(new(1, Guid.NewGuid(), "action")));
        Reject(() => ManagedDiskBrokerProtocol.Validate(new(1, Guid.NewGuid(), "inspect", Path: @"\\server\image.vhdx")));
        // An old preview cannot authorize mutation of a replacement disk or newer erase generation.
        var runtime = new ManagedDiskRuntime(request.Definition!.ResourceId, Guid.NewGuid(), 2, ManagedDiskMode.EphemeralRam, ManagedDiskState.Ready, 10, null);
        var record = new ManagedDiskRecord(request.Definition, runtime);
        ManagedDiskExpected.From(runtime).Validate(record, true);
        RejectIo(() => ManagedDiskExpected.From(runtime with { CreationGeneration = 1 }).Validate(record));
        RejectIo(() => ManagedDiskExpected.From(runtime with { BootEpoch = Guid.NewGuid() }).Validate(record));
        RejectIo(() => ManagedDiskExpected.From(runtime with { WriteGeneration = 9 }).Validate(record, true));
        ManagedDiskExpected.From(runtime with { WriteGeneration = 9 }).Validate(record, false);
        Console.WriteLine("Managed broker framing and selected-generation contracts passed.");
    }
    private static void Check(bool good, string why) { if (!good) throw new Exception(why); }
    private static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { return; } throw new Exception("Invalid broker request accepted."); }
    private static void RejectIo(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Stale managed identity accepted."); }
    private static async Task RejectAsync<T>(Func<Task<ManagedBrokerRequest?>> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Incomplete/invalid broker frame accepted."); }
    private sealed class Fragments(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], token);
    }
}
