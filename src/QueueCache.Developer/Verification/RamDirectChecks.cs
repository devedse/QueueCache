using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using QueueCache.Management;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Developer.Verification;

/// <summary>Direct access checks for a Ready RAM-backed product disk (RamAccess.Direct). Each returns
/// its evidence text or throws.</summary>
[SupportedOSPlatform("windows")]
internal static class RamDirectChecks
{
    private const int Block = 64 * 1024;
    // FILE_DEVICE_UNKNOWN function 0xAAA: no layer recognizes it, so it changes nothing, but it is not on
    // the filter's harmless list and must end Direct access before it is forwarded.
    private const uint UnrecognizedControl = (0x22u << 16) | (0xAAAu << 2);

    public static RamDirectState State(string volume)
    {
        using var device = CacheDevice.OpenVolumeName(volume);
        return device.GetRamDirectState();
    }

    public static RamDirectState RequireActive(ManagedDiskRecord record)
    {
        var state = State(record.VolumePath ?? throw new IOException("The disk has no volume."));
        if (!state.Full || state.Reason != RamDirectReason.None || state.ResourceId != record.ResourceId || state.LengthBytes == 0)
            throw new IOException($"Direct access is not fully active: {state.Describe()} (resource {state.ResourceId}, length {state.LengthBytes}).");
        return state;
    }

    /// <summary>Bytes written through Direct access are the bytes the standard SCSI path reads, and the reverse,
    /// including through a lock and dismount.</summary>
    public static async Task<string> Coherence(ManagedDiskRecord record, string root)
    {
        var volume = record.VolumePath!; var number = record.PhysicalDiskNumber ?? throw new IOException("No physical disk.");
        var before = RequireActive(record);
        var path = Path.Combine(root, "direct-coherence.bin");
        var marker = RandomNumberGenerator.GetBytes(Block);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { stream.Write(marker); stream.Flush(true); }
        using var disk = WindowsDiskStorage.Open(@"\\.\PhysicalDrive" + number, true);
        var at = await FindAsync(disk, marker);
        var replacement = RandomNumberGenerator.GetBytes(Block);
        var volumeOffset = checked((long)(at - before.OffsetBytes));
        using (var locked = WindowsDiskStorage.LockVolume(volume, number, dismount: true))
        {
            await disk.WriteAsync(at, replacement, CancellationToken.None);
            await disk.FlushAsync(CancellationToken.None);
            var back = new byte[Block];
            if (locked.Read(volumeOffset, back) != Block || !back.AsSpan().SequenceEqual(replacement))
                throw new IOException("A raw-disk (standard path) write was not what the locked volume read back through Direct access.");
        }
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(replacement))
            throw new IOException("After the dismount, NTFS did not read the bytes written through the standard path.");
        File.Delete(path);
        var after = RequireActive(record);
        if (after.WriteRequests <= before.WriteRequests || after.ReadRequests <= before.ReadRequests)
            throw new IOException("Direct access did not serve the coherence reads and writes.");
        return $"64 KiB written through Direct access was found by a raw standard-path disk read at {at}; a raw-disk overwrite under " +
               $"lock/dismount read back through the locked volume (Direct) and through NTFS afterwards; Direct stayed active " +
               $"(reads {before.ReadRequests}->{after.ReadRequests}, writes {before.WriteRequests}->{after.WriteRequests}).";
    }

    /// <summary>An unrecognized control request ends Direct access before it is forwarded; the standard path keeps
    /// serving the same data, and a new bind restores Direct access.</summary>
    public static string UnrecognizedControlFallback(ManagedDiskRecord record, string root, string sentinel, byte[] sentinelBytes)
    {
        var volume = record.VolumePath!;
        RequireActive(record);
        using (var handle = Open(volume))
            DeviceIoControl(handle, UnrecognizedControl, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero); // Its own result is irrelevant.
        var ended = State(volume);
        if (ended.Access != RamDirectAccess.None || ended.Reason != RamDirectReason.Control || ended.Detail != UnrecognizedControl)
            throw new IOException($"An unrecognized control did not end Direct access: {ended.Describe()}.");
        if (!File.ReadAllBytes(sentinel).AsSpan().SequenceEqual(sentinelBytes))
            throw new IOException("The standard path returned different bytes after Direct access ended.");
        var probe = Path.Combine(root, "direct-standard-probe.bin"); var data = RandomNumberGenerator.GetBytes(Block);
        File.WriteAllBytes(probe, data);
        if (!File.ReadAllBytes(probe).AsSpan().SequenceEqual(data)) throw new IOException("Standard-path writes failed after Direct access ended.");
        File.Delete(probe);
        RamDirectState rebound;
        using (var device = CacheDevice.OpenVolumeName(volume, writable: true)) rebound = device.BindRamDirect(readsOnly: false);
        if (!rebound.Full) throw new IOException($"A new bind did not restore Direct access: {rebound.Describe()}.");
        return $"Control 0x{UnrecognizedControl:X8} ended Direct access ({ended.Describe()}); data stayed identical on the standard path; a new bind restored it.";
    }

    /// <summary>A shadow copy turns Direct writes off (volsnap's copy-on-write is below the filter): the snapshot
    /// keeps the pre-snapshot bytes while the live volume takes new ones; reads stay Direct.</summary>
    public static string SnapshotFallback(ManagedDiskRecord record, string root)
    {
        var volume = record.VolumePath!;
        RequireActive(record);
        var path = Path.Combine(root, "direct-snapshot.bin");
        var original = RandomNumberGenerator.GetBytes(4 * Block);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { stream.Write(original); stream.Flush(true); }
        var copy = ShadowCopies.Create(root, out var failure) ?? throw new IOException($"Windows could not create a shadow copy (Win32_ShadowCopy.Create {failure}).");
        try
        {
            var state = State(volume);
            if (state.Access != RamDirectAccess.Reads || state.Reason != RamDirectReason.Snapshot)
                throw new IOException($"A shadow copy did not turn Direct writes off: {state.Describe()}.");
            var changed = RandomNumberGenerator.GetBytes(4 * Block);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(changed); stream.Flush(true); }
            if (!File.ReadAllBytes(ShadowCopies.PathIn(copy, path)).AsSpan().SequenceEqual(original))
                throw new IOException("The shadow copy lost the pre-snapshot bytes after a live write.");
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(changed))
                throw new IOException("The live volume did not keep the post-snapshot write.");
            var reads = State(volume);
            if (reads.ReadRequests <= state.ReadRequests || reads.Access != RamDirectAccess.Reads)
                throw new IOException("Reads did not stay Direct after the snapshot.");
            return $"Shadow copy {copy.Id}: {state.Describe()}; the snapshot kept the original 256 KiB while the live file took new bytes; reads stayed Direct.";
        }
        finally { ShadowCopies.Delete(copy.Id); File.Delete(path); }
    }

    private static async Task<ulong> FindAsync(WindowsDiskStorage disk, byte[] marker)
    {
        var chunk = new byte[(4 << 20) + Block];
        for (ulong offset = 0; offset < disk.CapacityBytes; offset += 4UL << 20)
        {
            var length = (int)Math.Min((ulong)chunk.Length, disk.CapacityBytes - offset);
            length -= length % (int)disk.SectorBytes;
            var read = await disk.ReadAsync(offset, chunk.AsMemory(0, length), CancellationToken.None);
            var index = chunk.AsSpan(0, read).IndexOf(marker);
            if (index >= 0) return offset + (ulong)index;
        }
        throw new IOException("Bytes written through Direct access were not found by a raw standard-path read of the disk.");
    }
    private static SafeFileHandle Open(string volume)
    {
        var handle = CreateFileW(volume.TrimEnd('\\'), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "Cannot open the volume."); }
        return handle;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, uint inputLength, IntPtr output, uint outputLength, out uint returned, IntPtr overlapped);
}
