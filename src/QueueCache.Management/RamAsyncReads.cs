using System.Buffers.Binary;

namespace QueueCache.Management;

/// <summary>Default-off per-resource experiment (native QC_RAM_ASYNC_DATA): asynchronous large
/// Direct reads whose split chunks are copied and completed by the provider's helpers.
/// Every admission ends as Completed or Declined (copied inline after all).</summary>
public sealed record RamAsyncReads(ulong Generation, bool Enabled, ulong InFlight, ulong InlineActive,
    ulong Started, ulong Completed, ulong Declined, ulong MaxInFlight, ulong Probes,
    ulong InlineStreak, ulong InlineBusy, ulong InlineCap)
{
    public const int WireSize = 104;
    public const ulong Cap = 16;
    public static RamAsyncReads Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length != WireSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != WireSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 1)
            throw new InvalidDataException("Incompatible RAM asynchronous read payload.");
        var v = new ulong[12];
        for (var i = 0; i < v.Length; ++i) v[i] = BinaryPrimitives.ReadUInt64LittleEndian(data[(8 + i * 8)..]);
        if (v[1] > 1 || v.Skip(2).Any(n => n > long.MaxValue))
            throw new InvalidDataException("Invalid RAM asynchronous read mode or counter.");
        return new(v[0], v[1] == 1, v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11]);
    }
    /// <summary>Quiescent window boundaries. Returns the share of large Direct reads that completed asynchronously.</summary>
    public static double ValidateWindow(RamAsyncReads before, RamAsyncReads after, bool enabled, ulong directReads)
    {
        if (before.Enabled != enabled || after.Enabled != enabled || before.Generation != after.Generation ||
            before.InFlight != 0 || after.InFlight != 0 || before.InlineActive != 0 || after.InlineActive != 0)
            throw new InvalidDataException("RAM asynchronous read mode/generation changed or requests remain in flight.");
        if (!enabled)
        {
            if (before != after) throw new InvalidDataException("Disabled RAM asynchronous read counters changed.");
            return 0;
        }
        if (before.Started != 0 || before.Completed != 0 || before.Declined != 0 || before.Probes != 0 ||
            after.Started == 0 || after.Started != after.Completed + after.Declined || after.Completed > directReads ||
            after.MaxInFlight == 0 || after.MaxInFlight > Cap || after.Probes > after.Started)
            throw new InvalidDataException("Quiescent RAM asynchronous read accounting is incomplete/inconsistent.");
        return directReads == 0 ? 0 : (double)after.Completed / directReads;
    }
}
