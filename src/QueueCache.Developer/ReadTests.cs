using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.Json;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer;

[SupportedOSPlatform("windows")]
public static class ReadTests
{
    public static async Task<int> RunAsync(int number, long expectedBytes, string instance, bool attached, CancellationToken token)
    {
        if (number < 0 || expectedBytes < (2L << 30) || string.IsNullOrWhiteSpace(instance)) return 2;
        var disk = (await DiskCatalog.ListAsync(token)).Single(d => d.Number == number);
        if (disk.Bytes != expectedBytes || !disk.Instance.Equals(instance, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Disk identity/size mismatch.");
        using var control = new CacheDevice(disk.Device);
        CacheStatistics? Snapshot()
        {
            try
            {
                var state = control.GetStatistics();
                if (!attached) throw new IOException("Filter is still attached.");
                if (state.DeviceBytes != expectedBytes || state.Enabled || state.QueueItems != 0 || state.LastError != 0)
                    throw new IOException("Expected healthy pass-through statistics.");
                return state;
            }
            catch (Win32Exception ex) when (!attached && ex.NativeErrorCode == 1) { return null; }
        }
        var before = Snapshot();
        using (var stream = new FileStream(DevicePath.Normalize(disk.Device), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536))
        {
            foreach (var offset in new[] { 0L, 1L << 20, 1L << 30, expectedBytes - 65536 })
            {
                token.ThrowIfCancellationRequested();
                var first = new byte[65536]; var second = new byte[65536];
                stream.Position = offset; stream.ReadExactly(first);
                stream.Position = offset; stream.ReadExactly(second);
                if (!first.AsSpan().SequenceEqual(second)) throw new IOException($"Repeated reads differ at {offset}; use an idle disk.");
            }
        }
        var after = Snapshot();
        if (before is not null && after is not null && (after.ReadBytes - before.ReadBytes < 524288 || after.WrittenBytes != before.WrittenBytes))
            throw new IOException("Unexpected I/O counters; use an idle disk.");
        Console.WriteLine(JsonSerializer.Serialize(new { result = "PASS", disk, attached, bytesRead = 524288, writesIssued = 0, before, after }));
        return 0;
    }
}
