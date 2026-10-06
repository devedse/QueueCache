using System.Buffers.Binary;
using System.Text;

namespace QueueCache.Management;

[Flags]
public enum RamDirectAccess : uint { None = 0, Reads = 1, Writes = 2 }

/// <summary>Why reads or writes of a RAM-disk volume take the standard path instead of Direct access.</summary>
public enum RamDirectReason : uint
{
    None = 0, NotRamDisk = 1, NotOffered = 2, CacheActive = 3, Layout = 4, UnknownDriver = 5, BitLocker = 6,
    Snapshot = 7, WriteProtected = 8, Control = 9, Removed = 10, QueryFailed = 11, InUse = 12
}

/// <summary>The volume filter's Direct access state for one volume (native QC_RAM_DIRECT_STATE).</summary>
public sealed record RamDirectState(RamDirectAccess Access, RamDirectReason Reason, uint Detail, Guid ResourceId,
    ulong OffsetBytes, ulong LengthBytes, ulong ReadRequests, ulong WriteRequests, ulong ReadBytes, ulong WriteBytes,
    ulong DeclinedRequests, string Driver)
{
    public const int WireSize = 160, BindWireSize = 16;
    public const uint StateIoctl = (0x8844u << 16) | (0xD18u << 2);
    public const uint BindIoctl = (0x8844u << 16) | (3u << 14) | (0xD19u << 2);
    public const uint BindReadsOnly = 1;

    public bool Full => Access == (RamDirectAccess.Reads | RamDirectAccess.Writes);

    public static byte[] BindRequest(bool readsOnly)
    {
        var data = new byte[BindWireSize];
        BinaryPrimitives.WriteUInt32LittleEndian(data, BindWireSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), readsOnly ? BindReadsOnly : 0);
        return data;
    }

    public static RamDirectState Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length != WireSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != WireSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 1)
            throw new InvalidDataException("Incompatible Direct access state from the volume filter.");
        var access = (RamDirectAccess)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        var reason = (RamDirectReason)BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        if ((access & ~(RamDirectAccess.Reads | RamDirectAccess.Writes)) != 0 || access == RamDirectAccess.Writes || !Enum.IsDefined(reason))
            throw new InvalidDataException("Invalid Direct access state from the volume filter.");
        var name = Encoding.Unicode.GetString(data.Slice(96, 64)).TrimEnd('\0');
        return new(access, reason, BinaryPrimitives.ReadUInt32LittleEndian(data[16..]), new Guid(data.Slice(24, 16)),
            Read64(data, 40), Read64(data, 48), Read64(data, 56), Read64(data, 64), Read64(data, 72), Read64(data, 80), Read64(data, 88), name);
    }

    /// <summary>Plain-language summary for status displays.</summary>
    public string Describe()
    {
        var why = Reason switch
        {
            RamDirectReason.None => "",
            RamDirectReason.NotRamDisk => "the volume is not on a QueueCache RAM disk",
            RamDirectReason.NotOffered => "the RAM disk was not created for Direct access",
            RamDirectReason.CacheActive => "a cache task owns the volume",
            RamDirectReason.Layout => "the volume is not one extent on the RAM disk",
            RamDirectReason.UnknownDriver => $"an unrecognized driver is in the storage stack ({Driver})",
            RamDirectReason.BitLocker => "the volume uses BitLocker",
            RamDirectReason.Snapshot => "a shadow copy exists or was taken",
            RamDirectReason.WriteProtected => "the volume or disk is write-protected",
            RamDirectReason.Control => $"an unrecognized control request was sent to the volume (0x{Detail:X8})",
            RamDirectReason.Removed => "the RAM disk or volume was removed",
            RamDirectReason.QueryFailed => $"an identity query failed (0x{Detail:X8})",
            RamDirectReason.InUse => "another volume already uses this RAM disk directly",
            _ => Reason.ToString()
        };
        return Access switch
        {
            RamDirectAccess.Reads | RamDirectAccess.Writes => "Direct",
            RamDirectAccess.Reads => $"Direct reads; writes standard because {why}",
            _ => why.Length == 0 ? "Standard" : $"Standard because {why}"
        };
    }
    private static ulong Read64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
}
