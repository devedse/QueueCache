using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.Json;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer;

[SupportedOSPlatform("windows")]
public static class ReadTests
{
    /// <summary>Read-only pass-through check on one volume, named by letter, exact size and volume GUID.
    /// Reads through the volume handle, the same stack position file-system reads use.</summary>
    public static async Task<int> RunAsync(string volume, long expectedBytes, string volumeId, bool attached, CancellationToken token)
    {
        if (volume is not { Length: 2 } || !char.IsAsciiLetter(volume[0]) || volume[1] != ':' || expectedBytes < (2L << 30) ||
            !Guid.TryParseExact(volumeId, "B", out _))
            return 2;
        var disk = (await VolumeCatalog.ListAsync(token)).SingleOrDefault(v => v.Volume.Equals(volume, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException($"{volume} is not a lettered fixed volume on one disk.");
        if (disk.Bytes != expectedBytes || !disk.VolumeId.Equals(volumeId, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Volume identity/size mismatch.");
        using var control = new CacheDevice(disk.Volume);
        CacheStatistics? Snapshot()
        {
            try
            {
                var state = control.GetStatistics();
                if (!attached)
                    throw new IOException("Filter is still attached.");
                if (state.DeviceBytes != expectedBytes || state.Enabled || state.QueueItems != 0 || state.LastError != 0)
                    throw new IOException("Expected healthy pass-through statistics.");
                return state;
            }
            catch (Win32Exception ex) when (!attached && ex.NativeErrorCode == 1) { return null; }
        }
        var before = Snapshot();
        using (var stream = new FileStream(DevicePath.NormalizeVolume(disk.Volume), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536))
        {
            foreach (var offset in new[] { 0L, 1L << 20, 1L << 30, expectedBytes - 65536 })
            {
                token.ThrowIfCancellationRequested();
                var first = new byte[65536];
                var second = new byte[65536];
                stream.Position = offset;
                stream.ReadExactly(first);
                stream.Position = offset;
                stream.ReadExactly(second);
                if (!first.AsSpan().SequenceEqual(second))
                    throw new IOException($"Repeated reads differ at {offset}; use an idle volume.");
            }
        }
        var after = Snapshot();
        if (before is not null && after is not null && (after.ReadBytes - before.ReadBytes < 524288 || after.WrittenBytes != before.WrittenBytes))
            throw new IOException("Unexpected I/O counters; use an idle volume.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result = "PASS",
            volume = disk,
            attached,
            bytesRead = 524288,
            writesIssued = 0,
            before,
            after
        }));
        return 0;
    }
}
