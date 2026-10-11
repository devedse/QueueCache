using System.Buffers.Binary;

namespace QueueCache.Management;

/// <summary>Default-off resource-scoped counters. Durations are sampled QPC ticks,
/// not score-window time; independent live counters are not transactional.</summary>
public sealed record RamCoordination(ulong Generation, ulong Frequency, bool Enabled,
    ulong Started, ulong Completed, ulong Active, ulong PeakActive, ulong DirectBytes,
    ulong DirectSamples, ulong DirectTicks, ulong Splits, ulong SplitBytes,
    ulong CallerBytes, ulong HelperBytes, ulong Posted, ulong Taken, ulong Withdrawn,
    ulong SampledSplits, ulong PostTicks, ulong WithdrawTicks, ulong WaitTicks,
    ulong TakeSamples, ulong TakeTicks, ulong HelperCpuMask)
{
    public const int WireSize = 200;
    public static RamCoordination Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length != WireSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != WireSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 1)
            throw new InvalidDataException("Incompatible RAM coordination payload.");
        var v = new ulong[24];
        for (var i = 0; i < v.Length; ++i) v[i] = BinaryPrimitives.ReadUInt64LittleEndian(data[(8 + i * 8)..]);
        if (v[1] == 0 || v[2] > 1 || v.Skip(3).Any(n => n > long.MaxValue))
            throw new InvalidDataException("Invalid RAM coordination frequency, mode or counter.");
        return new(v[0], v[1], v[2] == 1, v[3], v[4], v[5], v[6], v[7], v[8], v[9],
            v[10], v[11], v[12], v[13], v[14], v[15], v[16], v[17], v[18], v[19],
            v[20], v[21], v[22], v[23]);
    }
    public static void ValidateWindow(RamCoordination before, RamCoordination after, bool enabled,
        ulong directBytes, ulong scoredBytes)
    {
        if (before.Enabled != enabled || after.Enabled != enabled || before.Generation != after.Generation ||
            before.Frequency != after.Frequency || before.Active != 0 || after.Active != 0)
            throw new InvalidDataException("RAM coordination mode/generation changed or requests remain active.");
        if (!enabled)
        {
            if (before != after) throw new InvalidDataException("Disabled RAM coordination counters changed.");
            return;
        }
        if (before.Started != 0 || before.Completed != 0 || before.Splits != 0 || before.DirectBytes != 0 ||
            after.Started == 0 || after.Started != after.Completed || after.PeakActive == 0 ||
            after.PeakActive > after.Started || after.DirectBytes != directBytes || after.DirectBytes < scoredBytes ||
            after.Splits == 0 || after.SplitBytes > after.DirectBytes ||
            after.CallerBytes > after.SplitBytes || after.HelperBytes != after.SplitBytes - after.CallerBytes ||
            after.Taken > after.Posted || after.Withdrawn != after.Posted - after.Taken ||
            after.DirectSamples == 0 || after.DirectSamples > after.Started || after.DirectTicks == 0 ||
            after.SampledSplits == 0 || after.SampledSplits > after.Splits || after.TakeSamples > after.Taken)
            throw new InvalidDataException("Quiescent RAM coordination accounting is incomplete/inconsistent.");
    }
}
