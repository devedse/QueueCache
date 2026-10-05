using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QueueCache.Operations.ManagedDisks;

public sealed record ManagedBrokerRequest(int Version, Guid OperationId, string Operation,
    ManagedDiskDefinition? Definition = null, ManagedDiskRequest? Action = null, string? Path = null);
public sealed record ManagedBrokerReply(int Version, Guid OperationId, string Kind, JsonElement? Result = null,
    ManagedDiskProgress? Progress = null, string? Error = null, bool Cancelled = false);
public sealed record ManagedBrokerListEnd(int Count);

public static class ManagedDiskBrokerProtocol
{
    public const int Version = 1, MaximumFrameBytes = 1 << 20;
    public const string PipeName = "QueueCache.ManagedDisks.v1";
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter() }
    };
    public static void Validate(ManagedBrokerRequest request)
    {
        if (request.Version != Version || request.OperationId == Guid.Empty || request.Operation is not ("capabilities" or "inspect" or "list" or "create" or "action" or "cancel") ||
            (request.Operation == "create" && request.Definition is null) || (request.Operation == "action" && request.Action is null) ||
            (request.Operation == "inspect" && request.Path is null))
            throw new InvalidDataException("Unsupported or incomplete managed broker request.");
        if (request.Operation == "create") request.Definition!.Validate();
        if (request.Operation == "inspect") ManagedDiskPaths.ValidateImagePath(request.Path);
        if ((request.Definition is not null && request.Operation != "create") ||
            (request.Action is not null && request.Operation != "action") || (request.Path is not null && request.Operation != "inspect"))
            throw new InvalidDataException("Unexpected fields in managed broker request.");
    }
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (payload.Length is 0 or > MaximumFrameBytes) throw new InvalidDataException("Managed broker payload exceeds its bounded frame size.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(payload, token); await stream.FlushAsync(token);
    }
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken token = default)
    {
        var header = new byte[4]; var read = await stream.ReadAsync(header.AsMemory(0, 1), token);
        if (read == 0) return default;
        await stream.ReadExactlyAsync(header.AsMemory(1), token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumFrameBytes) throw new InvalidDataException("Invalid managed broker frame length.");
        var payload = new byte[length]; await stream.ReadExactlyAsync(payload, token);
        return JsonSerializer.Deserialize<T>(payload, Json) ?? throw new InvalidDataException("Empty managed broker payload.");
    }
}
