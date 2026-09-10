using System.Buffers.Binary;

namespace QueueCache.Management;

public enum CacheAllocation { Automatic, Fixed }

/// <summary>Background drain trigger. Settings each algorithm actually uses (see QcShouldDrain in driver/qcache/cachepolicy.h):</summary>
public enum DrainAlgorithm
{
    /// <summary>Drain a pending write as soon as it is accepted. Ignores <see cref="CacheOptions.LowPercent"/>,
    /// <see cref="CacheOptions.HighPercent"/>, <see cref="CacheOptions.MaxDirtyAgeMs"/> and <see cref="CacheOptions.IdleMs"/>.</summary>
    Eager,
    /// <summary>Drain at the high watermark or when the oldest pending block exceeds its maximum age, down to the low
    /// watermark. Uses <see cref="CacheOptions.LowPercent"/>, <see cref="CacheOptions.HighPercent"/> and
    /// <see cref="CacheOptions.MaxDirtyAgeMs"/>; ignores <see cref="CacheOptions.IdleMs"/>.</summary>
    Balanced,
    /// <summary>Balanced triggers plus a write-idle trigger. Uses every scheduling setting, including
    /// <see cref="CacheOptions.IdleMs"/>.</summary>
    Idle,
}

/// <summary>Independent allocation, retention and background scheduling policies. Matches QC_OPTIONS.
/// <see cref="BatchKiB"/> and <see cref="Parallelism"/> describe how a drain is issued and apply to every algorithm;
/// the watermark/age/idle settings are algorithm-specific as documented on <see cref="DrainAlgorithm"/>.</summary>
public sealed record CacheOptions(CacheAllocation Allocation = CacheAllocation.Automatic, int WritePercent = 50,
    bool RetainWrites = true, bool PromoteOnRead = true, DrainAlgorithm Drain = DrainAlgorithm.Eager,
    int LowPercent = 40, int HighPercent = 80, int MaxDirtyAgeMs = 5000, int IdleMs = 250, int BatchKiB = 256, int Parallelism = 1)
{
    public const int WireSize = 48;
    public void Validate()
    {
        if (!Enum.IsDefined(Allocation) || !Enum.IsDefined(Drain)) throw new ArgumentException("Unknown cache algorithm.");
        if (WritePercent is < 0 or > 100) throw new ArgumentException("Write share must be 0..100 percent.");
        if (LowPercent < 0 || LowPercent >= HighPercent || HighPercent > 100) throw new ArgumentException("Watermarks require 0 <= low < high <= 100.");
        if (MaxDirtyAgeMs is < 10 or > 300000 || IdleMs is < 10 or > 60000) throw new ArgumentException("Dirty age must be 10..300000 ms; idle interval 10..60000 ms.");
        if (BatchKiB is < 4 or > 1024 || BatchKiB % 4 != 0) throw new ArgumentException("Drain batch must be 4..1024 KiB, in multiples of 4.");
        if (Parallelism is < 1 or > 4) throw new ArgumentException("Drain parallelism must be 1..4.");
    }
    public byte[] Encode()
    {
        Validate();
        uint[] values = [1, WireSize, (uint)Allocation, (uint)WritePercent,
            (RetainWrites ? 1u : 0) | (PromoteOnRead ? 2u : 0), (uint)Drain,
            (uint)LowPercent, (uint)HighPercent, (uint)MaxDirtyAgeMs, (uint)IdleMs, (uint)BatchKiB, (uint)Parallelism];
        var result = new byte[WireSize];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i * 4), values[i]);
        return result;
    }
    public static CacheOptions Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length != WireSize) throw new InvalidDataException("Invalid policy length.");
        var v = new uint[12];
        for (int i = 0; i < v.Length; i++) v[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);
        if (v[0] != 1 || v[1] != WireSize || (v[4] & ~3u) != 0) throw new InvalidDataException("Unsupported cache policy contract.");
        var result = new CacheOptions((CacheAllocation)v[2], checked((int)v[3]), (v[4] & 1) != 0, (v[4] & 2) != 0,
            (DrainAlgorithm)v[5], checked((int)v[6]), checked((int)v[7]), checked((int)v[8]), checked((int)v[9]), checked((int)v[10]), checked((int)v[11]));
        result.Validate(); return result;
    }
}
