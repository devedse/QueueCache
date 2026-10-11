using System.Buffers.Binary;

namespace QueueCache.Management;

/// <summary>Native QC_RAM_ASYNC_MODE: adaptive is the product default for a new RAM disk; always is
/// for lab correctness checks (skips only the queue-depth policy).</summary>
public enum RamAsyncMode : ulong { Off = 0, Adaptive = 1, Always = 2 }

/// <summary>Per-resource state (native QC_RAM_ASYNC_DATA) of asynchronous large Direct reads whose
/// split chunks are copied and completed by the provider's helpers. Every admission ends as
/// Completed or Declined (copied inline after all). Probes include forced (Always) admissions.</summary>
public sealed record RamAsyncReads(ulong Generation, RamAsyncMode Mode, ulong InFlight, ulong InlineActive,
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
        if (v[1] > 2 || v.Skip(2).Any(n => n > long.MaxValue))
            throw new InvalidDataException("Invalid RAM asynchronous read mode or counter.");
        return new(v[0], (RamAsyncMode)v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11]);
    }
    /// <summary>Quiescent window boundaries. Returns the share of large Direct reads that completed asynchronously.</summary>
    public static double ValidateWindow(RamAsyncReads before, RamAsyncReads after, RamAsyncMode mode, ulong directReads)
    {
        if (before.Mode != mode || after.Mode != mode || before.Generation != after.Generation ||
            before.InFlight != 0 || after.InFlight != 0 || before.InlineActive != 0 || after.InlineActive != 0)
            throw new InvalidDataException("RAM asynchronous read mode/generation changed or requests remain in flight.");
        if (mode == RamAsyncMode.Off)
        {
            if (before != after) throw new InvalidDataException("Disabled RAM asynchronous read counters changed.");
            return 0;
        }
        if (before.Started != 0 || before.Completed != 0 || before.Declined != 0 || before.Probes != 0 ||
            after.Started == 0 || after.Started != after.Completed + after.Declined || after.Started > directReads ||
            after.MaxInFlight == 0 || after.MaxInFlight > Cap || after.Probes > after.Started)
            throw new InvalidDataException("Quiescent RAM asynchronous read accounting is incomplete/inconsistent.");
        return directReads == 0 ? 0 : (double)after.Completed / directReads;
    }
}
