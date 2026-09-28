using System.ComponentModel;
using System.Runtime.Versioning;
using QueueCache.Management;

namespace QueueCache.Operations;

/// <summary>
/// File-level TRIM (FSCTL_FILE_LEVEL_TRIM; NTFS sends the volume a data-set TRIM) while the cache holds data for
/// the trimmed range: pending writes are dropped rather than written, clean copies are dropped, an already
/// issued write completes first, and untrimmed neighbours and later rewrites are exact. Needs a disk that accepts
/// TRIM (for example a VHDX); otherwise the case is SKIP. New files only; trimmed bytes are never compared.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TrimScenarios
{
    private const int MiB = 1 << 20;

    public static IReadOnlyList<CheckResult> Run(DiskTarget target, CacheDevice device, string workDirectory)
    {
        var initial = device.GetWriteCacheState();
        if (!initial.SupportsDeferredDrain || !initial.ExtendedCountersAvailable)
            throw new NotSupportedException("TRIM verification requires Deferred draining and the extended TRIM/discard counters.");
        ConfigurationManager.Apply(target, VolumeScenarios.Pending(256), true);
        var directory = VolumeScenarios.NewDirectory(target, workDirectory, "trim-cache");
        // Probe on a file of its own so an unsupported disk leaves no cached data behind.
        var probe = Path.Combine(directory, "probe.bin");
        VolumeScenarios.WriteNew(probe, VolumeScenarios.Pattern(MiB, 0x7121));
        try
        {
            using var file = new AlignedFile(probe, MiB, create: false);
            file.Trim(0, MiB);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 1 or 50 or 326)
        {
            device.Control(WriteCacheAction.Flush);
            return [new("trim-cache", "SKIP", $"The disk under {target.Device} does not accept TRIM: Win32 {ex.NativeErrorCode} ({ex.Message}). " +
                "Run this suite on a VHDX volume (build/New-VolumeLabDisk.ps1).")];
        }
        return [Pending(device, directory), Clean(device, directory), InFlight(target, device, directory)];
    }

    // Accepted, undrained writes in the trimmed range are dropped, not drained to the disk.
    private static CheckResult Pending(CacheDevice device, string directory)
    {
        var path = Path.Combine(directory, "pending.bin");
        var first = VolumeScenarios.Pattern(4 * MiB, 0x7122);
        VolumeScenarios.WriteNew(path, first);
        var before = device.GetWriteCacheState();
        if (before.DirtyBytes < 4UL * MiB)
            throw new IOException("Could not establish 4 MiB of pending writes before TRIM.");
        Trim(path, MiB, MiB);
        var after = device.GetWriteCacheState();
        var guards = Guards(path, first, MiB, MiB);
        var dropped = after.DiscardedBytes - before.DiscardedBytes;
        var second = VolumeScenarios.Pattern(4 * MiB, 0x7123);
        VolumeScenarios.WriteNew(path, second);
        device.Control(WriteCacheAction.Flush);
        device.Control(WriteCacheAction.DropClean);
        var rewrite = VolumeScenarios.ReadAll(path).AsSpan().SequenceEqual(second);
        var pass = after.TrimRequests > before.TrimRequests && dropped >= (ulong)MiB && after.DrainedBytes == before.DrainedBytes &&
            Healthy(after) && guards && rewrite;
        return new("trim-cache/pending-writes-dropped", pass ? "PASS" : "FAIL",
            $"TRIM of the middle 1 MiB of 4 MiB pending: {after.TrimRequests - before.TrimRequests} TRIM request(s), {dropped} pending bytes dropped, " +
            $"{after.DrainedBytes - before.DrainedBytes} bytes drained; untrimmed guards {(guards ? "exact" : "DIFFER")} from RAM; " +
            $"full rewrite after flush and dropping clean data {(rewrite ? "exact" : "DIFFERS")} from the disk.");
    }

    // Drained, retained copies in the trimmed range are released; nothing is written.
    private static CheckResult Clean(CacheDevice device, string directory)
    {
        var path = Path.Combine(directory, "clean.bin");
        var data = VolumeScenarios.Pattern(4 * MiB, 0x7124);
        VolumeScenarios.WriteNew(path, data);
        device.Control(WriteCacheAction.Flush);
        var before = device.GetWriteCacheState();
        Trim(path, MiB, MiB);
        var after = device.GetWriteCacheState();
        var released = before.CleanReadBytes + before.CleanWriteBytes - Math.Min(before.CleanReadBytes + before.CleanWriteBytes,
            after.CleanReadBytes + after.CleanWriteBytes);
        device.Control(WriteCacheAction.DropClean);
        var guards = Guards(path, data, MiB, MiB);
        var pass = after.TrimRequests > before.TrimRequests && released >= (ulong)MiB && after.DrainedBytes == before.DrainedBytes &&
            Healthy(after) && guards;
        return new("trim-cache/clean-copies-released", pass ? "PASS" : "FAIL",
            $"TRIM of the middle 1 MiB of 4 MiB drained and retained: {released} clean bytes released, {after.DrainedBytes - before.DrainedBytes} bytes drained; " +
            $"untrimmed guards {(guards ? "exact" : "DIFFER")} from the disk after dropping clean data.");
    }

    // TRIM arriving while a drain of the same file is in progress: it waits for the issued write only,
    // and the remaining drain and the guards stay exact.
    private static CheckResult InFlight(DiskTarget target, CacheDevice device, string directory)
    {
        var path = Path.Combine(directory, "in-flight.bin");
        var data = VolumeScenarios.Pattern(8 * MiB, 0x7125);
        VolumeScenarios.WriteNew(path, data);
        var start = device.GetWriteCacheState();
        device.Control(WriteCacheAction.LabDelay, value: 300);
        Task flush;
        WriteCacheState before, after;
        try
        {
            flush = Task.Run(() =>
            {
                using var control = new CacheDevice(target.Device, writable: true);
                control.Control(WriteCacheAction.Flush);
            });
            if (!SpinWait.SpinUntil(() => device.GetWriteCacheState().InFlightBytes != 0 || flush.IsCompleted, 5000) || flush.IsCompleted)
                throw new IOException("Could not observe a drain in flight before TRIM.");
            before = device.GetWriteCacheState();
            Trim(path, 3 * MiB, 2 * MiB);
            after = device.GetWriteCacheState();
        }
        finally
        {
            device.Control(WriteCacheAction.LabDelay, value: 0);
        }
        flush.GetAwaiter().GetResult();
        var drained = device.GetWriteCacheState();
        // Every pending byte of the file either reached the disk or was dropped by the TRIM, and the TRIM dropped
        // no more than its own range. New file-system metadata may arrive after the flush (Fast), so the guards
        // are compared after a second flush, from the disk.
        var written = drained.DrainedBytes - start.DrainedBytes;
        var dropped = drained.DiscardedBytes - start.DiscardedBytes;
        device.Control(WriteCacheAction.Flush);
        device.Control(WriteCacheAction.DropClean);
        var guards = Guards(path, data, 3 * MiB, 2 * MiB);
        var pass = after.TrimRequests > before.TrimRequests && Healthy(drained) && written + dropped >= 8UL * MiB &&
            dropped <= 2UL * MiB && guards;
        return new("trim-cache/during-drain", pass ? "PASS" : "FAIL",
            $"TRIM of 2 MiB inside 8 MiB pending while {before.InFlightBytes} bytes were in flight (300 ms lab delay per drain batch): " +
            $"{after.TrimRequests - before.TrimRequests} TRIM request(s); the flush wrote {written} bytes and the TRIM dropped {dropped} " +
            $"(at most its 2 MiB); untrimmed guards {(guards ? "exact" : "DIFFER")} from the disk.");
    }

    private static void Trim(string path, long offset, long length)
    {
        using var file = new AlignedFile(path, MiB, create: false);
        file.Trim(offset, length);
    }

    private static bool Healthy(WriteCacheState state) => !state.Faulted && state.LastError == 0 && state.Enabled;

    // Everything outside [offset, offset + length) matches; the trimmed range itself is undefined.
    private static bool Guards(string path, byte[] expected, int offset, int length)
    {
        var actual = VolumeScenarios.ReadAll(path);
        return actual.Length == expected.Length && actual.AsSpan(0, offset).SequenceEqual(expected.AsSpan(0, offset)) &&
            actual.AsSpan(offset + length).SequenceEqual(expected.AsSpan(offset + length));
    }
}
