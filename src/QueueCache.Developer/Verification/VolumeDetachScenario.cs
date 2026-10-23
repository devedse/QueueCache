using System.Runtime.Versioning;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

/// <summary>
/// #21: detaching a virtual disk whose volume holds a Fast cache's pending writes keeps them. Windows removes
/// the volume without a query-remove; the file system's release of the volume is where the cache writes them
/// out. Uses a NEW 1 GiB VHDX of its own in the run folder, never the lab disk, and deletes it afterwards.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class VolumeDetachScenario
{
    private const int MiB = 1 << 20;

    public static async Task<IReadOnlyList<CheckResult>> RunAsync(DiskTarget target, string workDirectory, CancellationToken token)
    {
        var image = Path.Combine(VolumeScenarios.NewDirectory(target, workDirectory, "detach"), "detach.vhdx");
        var since = DateTime.Now;
        var attached = false;
        try
        {
            var letter = await LabDisk.CreateScratchAsync(image, 1024, "QC-Detach", token);
            attached = true;
            var scratch = await DiskTarget.InspectAsync($"{letter}:", token);
            ConfigurationManager.Apply(scratch, VolumeScenarios.Pending(256), true);
            var data = VolumeScenarios.Pattern(64 * MiB, 0x5EED0201);
            VolumeScenarios.WriteNew(Path.Combine(scratch.Root, "pending.bin"), data);
            ulong pending;
            using (var device = new CacheDevice(scratch.Device))
                pending = device.GetWriteCacheState().DirtyBytes;
            await LabDisk.DetachScratchAsync(image, token);
            attached = false;
            await Task.Delay(TimeSpan.FromSeconds(3), token); // The removal entry is written after the volume is gone.
            var events = await LabDisk.DriverEventIdsAsync(since, token);
            await LabDisk.AttachScratchAsync(image, token);
            attached = true;
            await Task.Delay(TimeSpan.FromSeconds(5), token);
            var volumes = await LabDisk.ScratchVolumesAsync(image, token);
            var file = volumes.Length == 1 ? Path.Combine(volumes[0] + "\\", "pending.bin") : null;
            var kept = file is not null && File.Exists(file) && VolumeScenarios.ReadAll(file).AsSpan().SequenceEqual(data);
            return
            [
                new("detach/pending-writes-kept", pending > 0 && kept ? "PASS" : "FAIL",
                    $"{letter}: (new 1 GiB VHDX, 256 MiB Fast cache, Deferred) held {pending / (double)MiB:0.0} MiB pending when it was detached; " +
                    $"after attaching it again the 64 MiB file was {(file is null ? $"not checked ({volumes.Length} lettered volumes)" : !File.Exists(file) ? "missing" : kept ? "intact" : "DIFFERENT")}."),
                new("detach/removal-event", events.Contains(2) && !events.Contains(1) ? "PASS" : "FAIL",
                    $"System log qcachelab entries since the start: [{string.Join(", ", events)}]; expected 2 (removed, nothing pending) and no 1 (writes lost).")
            ];
        }
        finally
        {
            if (attached)
                try { await LabDisk.DetachScratchAsync(image, CancellationToken.None); } catch (IOException) { }
            try { File.Delete(image); } catch (IOException) { }
        }
    }
}
