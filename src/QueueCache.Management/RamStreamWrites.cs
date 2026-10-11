using System.Buffers.Binary;

namespace QueueCache.Management;

/// <summary>Native QC_RAM_STREAM_MODE: off copies every write with ordinary stores; large uses streaming
/// (non-temporal) stores for writes of at least <see cref="RamStreamWrites.MinimumBytes"/>.</summary>
public enum RamStreamMode : ulong { Off = 0, Large = 1 }

/// <summary>Per-resource write copy mode (native QC_RAM_STREAM_DATA). Writes and Bytes count the large
/// write copies that used streaming stores since the mode was last set.</summary>
public sealed record RamStreamWrites(ulong Generation, RamStreamMode Mode, ulong Writes, ulong Bytes)
{
    public const int WireSize = 40;
    public const ulong MinimumBytes = 64 * 1024;
    public static RamStreamWrites Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length != WireSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != WireSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 1)
            throw new InvalidDataException("Incompatible RAM write copy payload.");
        var v = new ulong[4];
        for (var i = 0; i < v.Length; ++i) v[i] = BinaryPrimitives.ReadUInt64LittleEndian(data[(8 + i * 8)..]);
        if (v[1] > 1 || v[2] > long.MaxValue || v[3] > long.MaxValue || v[3] < v[2] * MinimumBytes)
            throw new InvalidDataException("Invalid RAM write copy mode or counter.");
        return new(v[0], (RamStreamMode)v[1], v[2], v[3]);
    }
    /// <summary>Quiescent window boundaries. Returns the share of the window's written bytes that streamed.</summary>
    public static double ValidateWindow(RamStreamWrites before, RamStreamWrites after, RamStreamMode mode, ulong writtenBytes)
    {
        if (before.Mode != mode || after.Mode != mode || before.Generation != after.Generation ||
            after.Writes < before.Writes || after.Bytes < before.Bytes)
            throw new InvalidDataException("RAM write copy mode/generation changed or its counters decreased.");
        if (mode == RamStreamMode.Off)
        {
            if (before != after) throw new InvalidDataException("Disabled RAM write copy counters changed.");
            return 0;
        }
        var streamed = after.Bytes - before.Bytes;
        if (streamed > writtenBytes) throw new InvalidDataException("More bytes streamed than the window wrote.");
        return writtenBytes == 0 ? 0 : (double)streamed / writtenBytes;
    }
}
