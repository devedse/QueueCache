using System.Buffers.Binary;
using System.Security.Cryptography;
using QueueCache.Operations.ManagedDisks;

internal static class VhdxPayloadTests
{
    private const int MiB = 1 << 20, Metadata = MiB, Bat = 2 * MiB, Payload = 4 * MiB, Block = 2 * MiB;
    private static void Check(bool value, string message) { if (!value) throw new Exception("VHDX payload: " + message); }
    public static async Task RunAsync()
    {
        Check(VhdxPayloadLayout.Crc32C("123456789"u8) == 0xe3069283, "Castagnoli CRC reference vector");
        var directory = Path.Combine(Path.GetTempPath(), "qcache-vhdx-payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var sector in new[] { 512u, 4096u })
            {
                var original = Fixture(sector);
                var path = Path.Combine(directory, "candidate-" + sector + ".vhdx");
                await File.WriteAllBytesAsync(path, original);
                await using (var view = VhdxPayloadStorage.Open(path, 4UL * MiB, sector))
                {
                    var untouched = new byte[sector];
                    Check(await view.ReadAsync(0, untouched, default) == untouched.Length && untouched.All(b => b == 0x71), "logical read through BAT");
                    Check(view.HeaderBytesWritten == 0, "read does not modify headers");
                }
                Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(original), "read-only use of adapter leaves container bytes unchanged");
                var update = Enumerable.Repeat((byte)0xa6, checked((int)sector * 3)).ToArray();
                var offset = (ulong)Block - sector;
                await using (var view = VhdxPayloadStorage.Open(path, 4UL * MiB, sector))
                {
                    try { using var other = File.OpenRead(path); throw new Exception("Exclusive candidate was shared."); }
                    catch (IOException) { }
                    await view.WriteAsync(offset, update, default);
                    await view.FlushAsync(default);
                    var read = new byte[update.Length];
                    Check(await view.ReadAsync(offset, read, default) == read.Length && read.AsSpan().SequenceEqual(update), "cross-block write and read map exact sectors");
                    Check(view.HeaderBytesWritten == 8192, "two durable headers precede first payload write");
                    await view.WriteAsync(0, new byte[sector], default);
                    Check(view.HeaderBytesWritten == 8192, "later write preserves this open's header identity");
                    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                    try { await view.WriteAsync(0, update, cancelled.Token); throw new Exception("Cancelled payload write succeeded."); }
                    catch (OperationCanceledException) { }
                }
                var final = File.ReadAllBytes(path);
                Check(final.AsSpan(Metadata, 3 * MiB).SequenceEqual(original.AsSpan(Metadata, 3 * MiB)), "metadata, BAT and log remain byte-identical");
                var expected = original.AsSpan(Payload, 4 * MiB).ToArray();
                // BAT intentionally swaps the physical blocks: this oracle checks
                // container offsets independently from the adapter's logical reads.
                update.AsSpan(0, (int)sector).CopyTo(expected.AsSpan(4 * MiB - (int)sector));
                update.AsSpan((int)sector).CopyTo(expected);
                expected.AsSpan(Block, (int)sector).Clear();
                Check(final.AsSpan(Payload, 4 * MiB).SequenceEqual(expected), "only intended payload sectors changed");
                using var stream = File.OpenRead(path);
                var layout = VhdxPayloadLayout.Read(stream, 4UL * MiB, sector);
                Check(layout.Sequence == 12 && !layout.Header.AsSpan(16, 32).SequenceEqual(original.AsSpan(65536 + 16, 32)), "checksummed header sequence and write GUIDs advanced");
            }
            // The first interleaved sector-bitmap entry occurs after 4 GiB for
            // 512-byte sectors and 1 MiB payload blocks. Keep the fixture sparse in memory.
            var large = Fixture(512);
            U32(large, Metadata + 65536, MiB);
            U64(large, Metadata + 65536 + 32, 4097UL * MiB);
            large.AsSpan(Bat, MiB).Clear();
            for (ulong block = 0; block < 4097; block++)
                U64(large, Bat + checked((int)((block + block / 4096) * 8)), (4UL * MiB + block * MiB) | 6);
            using (var sparse = new SparseFixtureStream(large, 4101L * MiB))
            {
                var layout = VhdxPayloadLayout.Read(sparse, 4097UL * MiB, 512);
                Check(layout.PayloadOffsets.Length == 4097 && layout.PayloadOffsets[^1] == 4100L * MiB,
                    "payload indexing crosses the interleaved sector-bitmap gap");
            }
            U64(large, Bat + 4096 * 8, 6);
            try
            {
                using var sparse = new SparseFixtureStream(large, 4101L * MiB);
                VhdxPayloadLayout.Read(sparse, 4097UL * MiB, 512);
                throw new Exception("Allocated non-differencing bitmap accepted.");
            }
            catch (NotSupportedException) { }
            var fixture = Fixture(512);
            var broken = Path.Combine(directory, "unsupported.vhdx");
            foreach (var mutate in new Action<byte[]>[]
            {
                b => b[65536 + 100] ^= 1,
                b => b[192 * 1024 + 100] ^= 1,
                b => { Guid.NewGuid().TryWriteBytes(b.AsSpan(65536 + 48, 16)); HeaderChecksum(b, 65536); },
                b => U32(b, Metadata + 65536 + 4, 2), // HasParent.
                b => U64(b, Bat, 0),
                b => U64(b, Bat, (ulong)Payload | 7),
                b => U64(b, Bat, (ulong)Metadata | 6),
                b => U64(b, Bat + 8, (ulong)(Payload + Block) | 6),
                b => U64(b, Bat, (ulong)b.Length | 6),
                b => U32(b, Metadata + 32 + 3 * 32 + 16, 65536), // Overlap metadata items.
                b => U64(b, 128 * 1024 + 8, 10), // Ambiguous equal sequence, with a valid CRC.
            })
            {
                var bytes = fixture.ToArray(); mutate(bytes);
                // The equal-sequence mutation intentionally preserves valid headers.
                if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(128 * 1024 + 8)) == 10) HeaderChecksum(bytes, 128 * 1024);
                File.WriteAllBytes(broken, bytes);
                var digest = SHA256.HashData(bytes);
                try { await using var view = VhdxPayloadStorage.Open(broken, 4UL * MiB, 512); throw new Exception("Unsafe layout accepted."); }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { }
                Check(SHA256.HashData(File.ReadAllBytes(broken)).AsSpan().SequenceEqual(digest), "refused layouts are not repaired or mutated");
            }
            File.WriteAllBytes(broken, fixture);
            try { await using var view = VhdxPayloadStorage.Open(broken, 8UL * MiB, 512); throw new Exception("Changed capacity accepted."); }
            catch (InvalidDataException) { }
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("VHDX payload layout, header, bounds, exclusive access and byte-oracle contracts passed (no attachment).");
    }

    private static byte[] Fixture(uint sector)
    {
        var bytes = new byte[8 * MiB];
        "vhdxfile"u8.CopyTo(bytes);
        var diskId = Guid.NewGuid(); var fileGuid = Guid.NewGuid(); var dataGuid = Guid.NewGuid();
        foreach (var (offset, sequence) in new[] { (65536, 10UL), (131072, 9UL) })
        {
            "head"u8.CopyTo(bytes.AsSpan(offset)); U64(bytes, offset + 8, sequence);
            fileGuid.TryWriteBytes(bytes.AsSpan(offset + 16, 16)); dataGuid.TryWriteBytes(bytes.AsSpan(offset + 32, 16));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 66), 1);
            U32(bytes, offset + 68, MiB); U64(bytes, offset + 72, 3UL * MiB); HeaderChecksum(bytes, offset);
        }
        var region = 192 * 1024;
        "regi"u8.CopyTo(bytes.AsSpan(region)); U32(bytes, region + 8, 2);
        Region(new("2dc27766-f623-4200-9d64-115e9bfd4a08"), Bat, 0);
        Region(new("8b7ca206-4790-4b9a-b8fe-575f050f886e"), Metadata, 1);
        U32(bytes, region + 4, VhdxPayloadLayout.Crc32C(bytes.AsSpan(region, 65536), 4));
        bytes.AsSpan(region, 65536).CopyTo(bytes.AsSpan(256 * 1024));
        "metadata"u8.CopyTo(bytes.AsSpan(Metadata));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(Metadata + 10), 5);
        Item(new("caa16737-fa36-4d43-b3b6-33f0aa44e76b"), 0, 8);
        Item(new("2fa54224-cd1b-4876-b211-5dbed83bf4b8"), 1, 8);
        Item(new("beca12ab-b2e6-4523-93ef-c309e000c746"), 2, 16);
        Item(new("8141bf1d-a96f-4709-ba47-f233a8faab5f"), 3, 4);
        Item(new("cda348c7-445d-4471-9cc9-e9885251c556"), 4, 4);
        U32(bytes, Metadata + 65536, Block); U64(bytes, Metadata + 65536 + 32, 4UL * MiB);
        diskId.TryWriteBytes(bytes.AsSpan(Metadata + 65536 + 64, 16));
        U32(bytes, Metadata + 65536 + 96, sector); U32(bytes, Metadata + 65536 + 128, 4096);
        U64(bytes, Bat, (ulong)(Payload + Block) | 6); U64(bytes, Bat + 8, (ulong)Payload | 6);
        bytes.AsSpan(Payload).Fill(0x71);
        return bytes;
        void Region(Guid id, int location, int index)
        {
            var entry = region + 16 + index * 32; id.TryWriteBytes(bytes.AsSpan(entry, 16));
            U64(bytes, entry + 16, (ulong)location); U32(bytes, entry + 24, MiB); U32(bytes, entry + 28, 1);
        }
        void Item(Guid id, int index, uint length)
        {
            var entry = Metadata + 32 + index * 32; id.TryWriteBytes(bytes.AsSpan(entry, 16));
            U32(bytes, entry + 16, (uint)(65536 + index * 32)); U32(bytes, entry + 20, length); U32(bytes, entry + 24, 4);
        }
    }
    private sealed class SparseFixtureStream(byte[] bytes, long length) : Stream
    {
        private long position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)); }
        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, Math.Max(0, length - position));
            buffer[..count].Clear();
            if (position < bytes.Length) bytes.AsSpan((int)position, Math.Min(count, bytes.Length - (int)position)).CopyTo(buffer);
            position += count; return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        { SeekOrigin.Begin => offset, SeekOrigin.Current => position + offset, SeekOrigin.End => length + offset, _ => throw new ArgumentException("Invalid origin.") };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private static void HeaderChecksum(byte[] bytes, int offset) => U32(bytes, offset + 4, VhdxPayloadLayout.Crc32C(bytes.AsSpan(offset, 4096), 4));
    private static void U32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void U64(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
}
