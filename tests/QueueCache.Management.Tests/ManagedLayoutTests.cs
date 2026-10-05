using System.Buffers.Binary;
using QueueCache.Operations.ManagedDisks;

internal static class ManagedLayoutTests
{
    public static async Task RunAsync()
    {
        Check(ManagedImageLayout.Crc32("123456789"u8) == 0xCBF43926, "GPT CRC matches the standard vector");
        foreach (var sector in new[] { 512U, 4096U })
        {
            var disk = Fixture(sector);
            var layout = await ManagedImageLayout.InspectAsync(disk);
            Check(layout.Partitions.Count == 1 && layout.Partitions[0].Offset == 1UL << 20, "decoded GPT/NTFS geometry survives aligned short reads");
            disk.Bytes[466] = 7; await RejectAsync(() => ManagedImageLayout.InspectAsync(disk));
            disk = Fixture(sector); disk.Bytes[checked((int)sector + 56)] ^= 1; await RejectAsync(() => ManagedImageLayout.InspectAsync(disk));
            disk = Fixture(sector); disk.Bytes[checked(disk.Bytes.Length - (int)sector - 16384)] ^= 1; await RejectAsync(() => ManagedImageLayout.InspectAsync(disk));
            disk = Fixture(sector); "-FVE-FS-"u8.CopyTo(disk.Bytes.AsSpan((1 << 20) + 3)); await RejectAsync(() => ManagedImageLayout.InspectAsync(disk));
            disk = Fixture(sector); disk.Bytes[(1 << 20) + 13] = 3; await RejectAsync(() => ManagedImageLayout.InspectAsync(disk));
            disk = Fixture(sector); var header = disk.Bytes.AsSpan((int)sector, (int)sector);
            BinaryPrimitives.WriteUInt32LittleEndian(header[80..], uint.MaxValue); Seal(header);
            await RejectAsync(() => ManagedImageLayout.InspectAsync(disk));
            Check(disk.ReadBytes <= 3UL * sector, "hostile GPT entry count is rejected before any array transfer/allocation");
        }
        Console.WriteLine("Private GPT/NTFS activation contracts passed.");
    }
    private static SectorDisk Fixture(uint sector)
    {
        var disk = new SectorDisk(sector); var sectors = (ulong)disk.Bytes.Length / sector;
        disk.Bytes[510] = 0x55; disk.Bytes[511] = 0xAA; disk.Bytes[450] = 0xEE;
        BinaryPrimitives.WriteUInt32LittleEndian(disk.Bytes.AsSpan(454), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(disk.Bytes.AsSpan(458), checked((uint)sectors - 1));
        var arrayBytes = 128 * 128; var arraySectors = (ulong)arrayBytes / sector;
        var entries = disk.Bytes.AsSpan(checked((int)(2 * sector)), arrayBytes);
        ManagedDiskLayout.BasicData.TryWriteBytes(entries); Guid.NewGuid().TryWriteBytes(entries[16..]);
        var first = (1UL << 20) / sector; var last = sectors - (1UL << 20) / sector - 1;
        BinaryPrimitives.WriteUInt64LittleEndian(entries[32..], first); BinaryPrimitives.WriteUInt64LittleEndian(entries[40..], last);
        entries.CopyTo(disk.Bytes.AsSpan(checked((int)((sectors - 1 - arraySectors) * sector))));
        var id = Guid.NewGuid(); var crc = ManagedImageLayout.Crc32(entries);
        Header(disk.Bytes.AsSpan((int)sector, (int)sector), id, 1, sectors - 1, 2, sectors, arraySectors, crc);
        Header(disk.Bytes.AsSpan(disk.Bytes.Length - (int)sector, (int)sector), id, sectors - 1, 1, sectors - 1 - arraySectors, sectors, arraySectors, crc);
        var ntfs = disk.Bytes.AsSpan(1 << 20, (int)sector); "NTFS    "u8.CopyTo(ntfs[3..]);
        BinaryPrimitives.WriteUInt16LittleEndian(ntfs[11..], (ushort)sector); ntfs[13] = 8; ntfs[510] = 0x55; ntfs[511] = 0xAA;
        BinaryPrimitives.WriteUInt64LittleEndian(ntfs[40..], last - first + 1);
        return disk;
    }
    private static void Header(Span<byte> bytes, Guid id, ulong current, ulong alternate, ulong entries, ulong sectors, ulong arraySectors, uint crc)
    {
        "EFI PART"u8.CopyTo(bytes); BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], 0x10000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], 92); BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], current);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], alternate); BinaryPrimitives.WriteUInt64LittleEndian(bytes[40..], 2 + arraySectors);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[48..], sectors - 2 - arraySectors); id.TryWriteBytes(bytes[56..]);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[72..], entries); BinaryPrimitives.WriteUInt32LittleEndian(bytes[80..], 128);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[84..], 128); BinaryPrimitives.WriteUInt32LittleEndian(bytes[88..], crc); Seal(bytes);
    }
    private static void Seal(Span<byte> header) { header.Slice(16, 4).Clear(); BinaryPrimitives.WriteUInt32LittleEndian(header[16..], ManagedImageLayout.Crc32(header[..92])); }
    private static async Task RejectAsync(Func<Task<ManagedDiskLayout>> action)
    { try { await action(); } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { return; } throw new Exception("Corrupt/encrypted image was accepted for publication."); }
    private static void Check(bool good, string why) { if (!good) throw new Exception(why); }
    private sealed class SectorDisk(uint sector) : ILogicalDisk
    {
        public readonly byte[] Bytes = new byte[16 << 20]; public ulong ReadBytes;
        public ulong CapacityBytes => (ulong)Bytes.Length; public uint SectorBytes => sector;
        public ValueTask<int> ReadAsync(ulong offset, Memory<byte> buffer, CancellationToken token)
        { token.ThrowIfCancellationRequested(); var count = Math.Min(buffer.Length, (int)sector); Bytes.AsMemory((int)offset, count).CopyTo(buffer); ReadBytes += (ulong)count; return ValueTask.FromResult(count); }
        public ValueTask WriteAsync(ulong offset, ReadOnlyMemory<byte> buffer, CancellationToken token) => throw new NotSupportedException();
        public ValueTask FlushAsync(CancellationToken token) => ValueTask.CompletedTask;
    }
}
