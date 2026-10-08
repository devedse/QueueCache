using System.Buffers.Binary;

namespace QueueCache.Management;

public enum RamDiskAction : uint
{
    Capabilities = 1, Enumerate, Create, Query, Read, Write, Publish, Freeze, Thaw, Remove, SetReadOnly, StartupSession,
    /// <summary>Reply followed by <see cref="RamDiskStatistics"/>; offered when Capabilities reports StatisticsSupported.</summary>
    Statistics, SetTiming,
    /// <summary>Reply followed by <see cref="RamPhysicalMap"/>; offset = estimated physical page span. Offered with PhysicalMapSupported.</summary>
    PhysicalMap,
    DeveloperCreateAllocationFailure = 0x100
}
[Flags]
public enum RamDiskFlags : uint
{
    None = 0, Published = 1, ReadOnly = 2, Frozen = 4,
    /// <summary>Create: offer the disk to the volume filter for Direct access.</summary>
    Direct = 8,
    /// <summary>Reply: the volume filter accepted the disk for Direct access.</summary>
    DirectRegistered = 16,
    /// <summary>Every transfer is timed (SetTiming). Off when a disk is created.</summary>
    Timing = 32,
    /// <summary>Capabilities reply: the provider offers Statistics and SetTiming.</summary>
    StatisticsSupported = 64,
    /// <summary>Capabilities reply: the provider offers PhysicalMap.</summary>
    PhysicalMapSupported = 128
}

/// <summary>Where a RAM disk's pages sit in physical memory (native QC_RAM_PHYSICAL_MAP): Counts[i] pages fall in
/// bin i of [0, SpanPages) pages; Runs counts physically contiguous runs in disk order. The pages are locked.</summary>
public sealed record RamPhysicalMap(ulong SpanPages, ulong Pages, ulong Runs, uint[] Counts)
{
    public const int Bins = 1024;
    public const int WireSize = 32 + 4 * Bins;
    public static RamPhysicalMap Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < WireSize || BinaryPrimitives.ReadUInt32LittleEndian(data[24..]) != Bins)
            throw new InvalidDataException("Invalid RAM disk physical map.");
        var counts = new uint[Bins];
        ulong total = 0;
        for (var i = 0; i < Bins; i++)
            total += counts[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(32 + 4 * i)..]);
        var map = new RamPhysicalMap(BinaryPrimitives.ReadUInt64LittleEndian(data), BinaryPrimitives.ReadUInt64LittleEndian(data[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(data[16..]), counts);
        if (map.SpanPages == 0 || total != map.Pages || map.Runs > map.Pages || map.Pages > 0 && map.Runs == 0)
            throw new InvalidDataException("Inconsistent RAM disk physical map.");
        return map;
    }
}

/// <summary>Request counts of the provider's SCSI path, and transfer timing of the SCSI and Direct
/// paths while <see cref="RamDiskFlags.Timing"/> is on (native QC_RAM_STATISTICS).</summary>
public sealed record RamDiskStatistics(ulong ReadRequests, ulong WriteRequests, ulong Frequency,
    ulong TimedReads, ulong TimedWrites, ulong ReadTicks, ulong WriteTicks, ulong MaxReadTicks, ulong MaxWriteTicks)
{
    public const int WireSize = 72;
    public static RamDiskStatistics Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < WireSize)
            throw new InvalidDataException("Truncated RAM disk statistics.");
        var v = new ulong[9];
        for (var i = 0; i < v.Length; ++i)
            v[i] = BinaryPrimitives.ReadUInt64LittleEndian(data[(i * 8)..]);
        var result = new RamDiskStatistics(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8]);
        if (result.Frequency == 0)
            throw new InvalidDataException("RAM disk statistics without a timer frequency.");
        return result;
    }
    [System.Text.Json.Serialization.JsonIgnore] public double AverageReadMicroseconds => TimedReads == 0 ? 0 : 1e6 * ReadTicks / TimedReads / Frequency;
    [System.Text.Json.Serialization.JsonIgnore] public double AverageWriteMicroseconds => TimedWrites == 0 ? 0 : 1e6 * WriteTicks / TimedWrites / Frequency;
    [System.Text.Json.Serialization.JsonIgnore] public double MaxReadMicroseconds => 1e6 * MaxReadTicks / Frequency;
    [System.Text.Json.Serialization.JsonIgnore] public double MaxWriteMicroseconds => 1e6 * MaxWriteTicks / Frequency;
}

/// <summary>Versioned Storport control ABI. All offsets are independent of CLR packing.</summary>
public sealed record RamDiskSnapshot(Guid ResourceId, Guid BootEpoch, ulong CreationGeneration,
    ulong CapacityBytes, ulong WriteGeneration, ulong ReservedBytes, Guid FreezeOwner,
    uint SectorBytes, RamDiskFlags Flags, uint Slot, ulong ReadBytes, ulong WriteBytes,
    ulong Flushes, ulong Trims, ulong Errors, ulong Transfers)
{
    public const int WireSize = 168, MaximumTransferBytes = 1 << 20, MaximumDisks = 32;
    public const uint AllocationSlabBytes = 4 << 20;
    // Keep 1 and extend compatibly: the update preflight (new CLI) must enumerate disks through the
    // still-installed provider; a version change blocks every update (see ramdiskprotocol.h).
    public const uint Magic = 0x52444351, ServiceIoctl = 0x0004D038, Version = 1;

    /// <summary>Conservative x64 headroom estimate, including PFN/MDL/slab metadata. Native reservation is authoritative.</summary>
    public static ulong EstimateReservationBytes(ulong capacity)
    {
        if (capacity < 16UL << 20 || capacity > 128UL << 30 || capacity % (1UL << 20) != 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        var slabs = (capacity + AllocationSlabBytes - 1) / AllocationSlabBytes;
        return checked(capacity + capacity / 4096 * 8 + slabs * (64 + 32) + (2UL << 20));
    }

    public static byte[] Request(RamDiskAction action, RamDiskSnapshot? expected = null,
        Guid resource = default, ulong capacity = 0, uint sector = 512, uint slot = 0,
        ulong offset = 0, int transferBytes = 0, Guid freezeOwner = default, RamDiskFlags flags = RamDiskFlags.None)
    {
        if (!Enum.IsDefined(action) || transferBytes < 0 || transferBytes > MaximumTransferBytes ||
            (flags & ~(action switch { RamDiskAction.Create => RamDiskFlags.Direct, RamDiskAction.SetReadOnly => RamDiskFlags.ReadOnly,
                RamDiskAction.SetTiming => RamDiskFlags.Timing, _ => RamDiskFlags.None })) != 0 ||
            slot >= MaximumDisks || expected?.Slot >= MaximumDisks ||
            (action is not (RamDiskAction.Capabilities or RamDiskAction.Enumerate or RamDiskAction.Create or RamDiskAction.StartupSession or RamDiskAction.DeveloperCreateAllocationFailure) && expected is null) ||
            (action is RamDiskAction.Create or RamDiskAction.DeveloperCreateAllocationFailure && (resource == Guid.Empty || capacity < 16UL << 20 || capacity > 128UL << 30 ||
                sector is not (512 or 4096) || capacity % (1UL << 20) != 0)) ||
            (action == RamDiskAction.DeveloperCreateAllocationFailure && (expected is not null || transferBytes != 0 ||
                offset == 0 || offset > (capacity + AllocationSlabBytes - 1) / AllocationSlabBytes)) ||
            (action is RamDiskAction.Freeze or RamDiskAction.Thaw && freezeOwner == Guid.Empty))
            throw new ArgumentException("Invalid bounded RAM disk control request.");
        var data = new byte[WireSize + transferBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(data, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), WireSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), (uint)action);
        (expected?.ResourceId ?? resource).TryWriteBytes(data.AsSpan(16, 16));
        (expected?.BootEpoch ?? Guid.Empty).TryWriteBytes(data.AsSpan(32, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(48), expected?.CreationGeneration ?? 0);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(56), expected?.CapacityBytes ?? capacity);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(64), offset);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(72), expected?.WriteGeneration ?? 0);
        freezeOwner.TryWriteBytes(data.AsSpan(88, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(104), expected?.SectorBytes ?? sector);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(108), (uint)flags);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(112), (uint)transferBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(116), expected?.Slot ?? slot);
        return data;
    }

    public static RamDiskSnapshot Decode(ReadOnlySpan<byte> data, bool capabilities = false)
    {
        if (data.Length < WireSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != Version || BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) != WireSize)
            throw new InvalidDataException("Incompatible or truncated RAM disk provider reply.");
        var result = new RamDiskSnapshot(new(data.Slice(16, 16)), new(data.Slice(32, 16)),
            Read64(data, 48), Read64(data, 56), Read64(data, 72), Read64(data, 80), new(data.Slice(88, 16)),
            BinaryPrimitives.ReadUInt32LittleEndian(data[104..]), (RamDiskFlags)BinaryPrimitives.ReadUInt32LittleEndian(data[108..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[116..]), Read64(data, 120), Read64(data, 128),
            Read64(data, 136), Read64(data, 144), Read64(data, 152), Read64(data, 160));
        if (result.BootEpoch == Guid.Empty || result.CapacityBytes == 0 || result.CapacityBytes > long.MaxValue ||
            (result.Flags & ~(capabilities ? RamDiskFlags.StatisticsSupported :
                RamDiskFlags.Published | RamDiskFlags.ReadOnly | RamDiskFlags.Frozen | RamDiskFlags.DirectRegistered | RamDiskFlags.Timing)) != 0 ||
            (!capabilities && (result.ResourceId == Guid.Empty || result.CreationGeneration == 0 || result.Slot >= MaximumDisks ||
                result.SectorBytes is not (512 or 4096) || result.CapacityBytes % result.SectorBytes != 0 ||
                result.ReservedBytes < result.CapacityBytes ||
                result.Flags.HasFlag(RamDiskFlags.Frozen) != (result.FreezeOwner != Guid.Empty))))
            throw new InvalidDataException("Invalid native RAM disk identity, geometry or lifetime state.");
        if (capabilities && (result.Slot != MaximumDisks || BinaryPrimitives.ReadUInt32LittleEndian(data[112..]) != MaximumTransferBytes))
            throw new InvalidDataException("Incompatible RAM disk provider limits.");
        return result;
    }
    public void RequireSameCreation(RamDiskSnapshot expected)
    {
        if (ResourceId != expected.ResourceId || BootEpoch != expected.BootEpoch || CreationGeneration != expected.CreationGeneration ||
            Slot != expected.Slot || CapacityBytes != expected.CapacityBytes || SectorBytes != expected.SectorBytes)
            throw new IOException("The RAM disk was replaced; refresh before modifying it.");
    }
    public string DeviceSerial => "QC" + Convert.ToHexString(ResourceId.ToByteArray());
    private static ulong Read64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
}
