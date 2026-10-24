using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace QueueCache.Operations.ManagedDisks;

/// <summary>Exclusive file access to the payload of a validated, fully allocated VHDX candidate.
/// No Windows attachment, BAT allocation, log replay or metadata-region update occurs here.
/// The caller owns candidate provenance, parent pinning, journal removal of old pointers and final verification.</summary>
internal sealed class VhdxPayloadStorage : ILogicalDisk, IAsyncDisposable
{
    private readonly FileStream file;
    private readonly VhdxPayloadLayout layout;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool headersUpdated;
    private readonly Action? headerFlushAttempt;
    public ulong CapacityBytes => layout.CapacityBytes;
    public uint SectorBytes => layout.SectorBytes;
    public Guid DiskId => layout.DiskId;
    public SafeFileHandle Handle => file.SafeFileHandle;
    public long FileBytes => file.Length;
    public ulong HeaderBytesWritten { get; private set; }

    private VhdxPayloadStorage(FileStream file, VhdxPayloadLayout layout, Action? headerFlushAttempt)
    { this.file = file; this.layout = layout; this.headerFlushAttempt = headerFlushAttempt; }
    public static VhdxPayloadStorage Open(string path, ulong expectedCapacity, uint expectedSector, Action? headerFlushAttempt = null)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        try { return new(file, VhdxPayloadLayout.Read(file, expectedCapacity, expectedSector), headerFlushAttempt); }
        catch { file.Dispose(); throw; }
    }
    private void Validate(ulong offset, int bytes)
    {
        if (offset > CapacityBytes || (ulong)bytes > CapacityBytes - offset || offset % SectorBytes != 0 || bytes % SectorBytes != 0)
            throw new ArgumentException("Logical image transfer is unaligned or outside the candidate.");
    }
    private (long Physical, int Bytes) Part(ulong offset, int remaining)
    {
        var block = checked((int)(offset / layout.BlockBytes));
        var within = offset % layout.BlockBytes;
        return (checked(layout.PayloadOffsets[block] + (long)within),
            (int)Math.Min((ulong)remaining, layout.BlockBytes - within));
    }
    public async ValueTask<int> ReadAsync(ulong offset, Memory<byte> buffer, CancellationToken token)
    {
        Validate(offset, buffer.Length);
        await gate.WaitAsync(token);
        try
        {
            var completed = 0;
            while (completed < buffer.Length)
            {
                var part = Part(offset + (ulong)completed, buffer.Length - completed);
                var read = 0;
                while (read < part.Bytes)
                {
                    var count = await RandomAccess.ReadAsync(file.SafeFileHandle,
                        buffer.Slice(completed + read, part.Bytes - read), part.Physical + read, token);
                    if (count == 0) throw new EndOfStreamException("Short VHDX payload read.");
                    read += count;
                }
                completed += part.Bytes;
            }
            return completed;
        }
        finally { gate.Release(); }
    }
    public async ValueTask WriteAsync(ulong offset, ReadOnlyMemory<byte> buffer, CancellationToken token)
    {
        Validate(offset, buffer.Length);
        await gate.WaitAsync(token);
        try
        {
            if (buffer.Length == 0) return;
            await UpdateHeadersAsync(token);
            var completed = 0;
            while (completed < buffer.Length)
            {
                var part = Part(offset + (ulong)completed, buffer.Length - completed);
                await RandomAccess.WriteAsync(file.SafeFileHandle, buffer.Slice(completed, part.Bytes), part.Physical, token);
                completed += part.Bytes;
            }
        }
        finally { gate.Release(); }
    }
    private async Task UpdateHeadersAsync(CancellationToken token)
    {
        if (headersUpdated) return;
        var header = layout.Header.ToArray();
        Guid.NewGuid().TryWriteBytes(header.AsSpan(16, 16)); // FileWriteGuid, before any file modification.
        Guid.NewGuid().TryWriteBytes(header.AsSpan(32, 16)); // DataWriteGuid, before any payload modification.
        // Alternate the inactive/current headers, preserving one valid root through each write.
        // Payload is not VHDX metadata and MUST NOT be written through the VHDX log.
        foreach (var (offset, sequence) in new[]
        {
            (layout.CurrentHeaderOffset == 65536 ? 131072L : 65536L, layout.Sequence + 1),
            (layout.CurrentHeaderOffset, layout.Sequence + 2)
        })
        {
            token.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), sequence);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), VhdxPayloadLayout.Crc32C(header, 4));
            await RandomAccess.WriteAsync(file.SafeFileHandle, header, offset, token);
            headerFlushAttempt?.Invoke();
            file.Flush(flushToDisk: true);
            HeaderBytesWritten += (ulong)header.Length;
        }
        headersUpdated = true;
    }
    public async ValueTask FlushAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { token.ThrowIfCancellationRequested(); file.Flush(flushToDisk: true); }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { await file.DisposeAsync(); }
        finally { gate.Release(); }
    }
}
