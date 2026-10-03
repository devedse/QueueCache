using System.Runtime.Versioning;
using System.Text.Json;
using QueueCache.Management;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Developer.Verification;

/// <summary>Unique owned native fixtures; all teardown evidence remains in the enclosing maintained run.</summary>
[SupportedOSPlatform("windows")]
internal static class ManagedProviderScenarios
{
    public static async Task<IReadOnlyList<CheckResult>> RunAsync(DiskTarget host, CacheDevice cache, string directory, string evidence)
    {
        var checks = new List<CheckResult>();
        void Pass(string name, string detail) { checks.Add(new(name, "PASS", detail)); Console.WriteLine(name + ": " + detail); }
        Directory.CreateDirectory(directory);
        using var provider = WindowsRamDisk.Connect();
        var before = cache.GetWriteCacheState();
        var id = Guid.NewGuid(); RamDiskSnapshot? ram = null; WindowsDiskStorage? physical = null;
        string? volume = null;
        var snapshots = new List<object>();
        var imagePath = Path.Combine(directory, "provider-" + id.ToString("N") + ".vhdx");
        try
        {
            ram = provider.Create(id, 64UL << 20, 512); snapshots.Add(new { Stage = "Private", Native = ram });
            if ((ram.Flags & RamDiskFlags.Published) != 0 || provider.Enumerate().Single(d => d.ResourceId == id) != ram)
                throw new IOException("Private creation unexpectedly published or lost its native ownership.");
            var reserved = cache.GetWriteCacheState();
            if (reserved.GlobalReservedBytes != before.GlobalReservedBytes + ram.ReservedBytes) throw new IOException("RAM reservation did not use the existing cache budget authority.");
            var storage = provider.Storage(ram);
            var sector = new byte[512];
            await storage.ReadAsync(0, sector, default);
            if (sector.Any(b => b != 0)) throw new IOException("New RAM sectors are not zero.");
            var pattern = Enumerable.Range(0, 1 << 20).Select(n => (byte)(n * 31 + 7)).ToArray();
            await storage.WriteAsync(ram.CapacityBytes - (ulong)pattern.Length, pattern, default);
            var read = new byte[pattern.Length]; await storage.ReadAsync(ram.CapacityBytes - (ulong)pattern.Length, read, default);
            if (!read.SequenceEqual(pattern)) throw new IOException("Private RAM tail-sector transfer differs.");
            Pass("private-owned-ram", "Unpublished zeroed storage, exact tail transfer and shared cache/RAM accounting.");
            // Isolated native VHDX raw access: no filesystem or letter; source attachment is read-only on verification.
            LogicalImageDigest digest;
            using (var image = WindowsVirtualDisk.CreateNew(imagePath, ram.CapacityBytes, 512, ImageAllocation.Dynamic))
            using (var disk = WindowsDiskStorage.Open(image.Attach(false), true))
            {
                disk.SetOffline(true);
                digest = await LogicalImageTransfer.CopyAsync(storage, disk);
            }
            using (var image = WindowsVirtualDisk.Open(imagePath, false, true))
            using (var disk = WindowsDiskStorage.Open(image.Attach(true), true))
            {
                disk.SetOffline(true); await LogicalImageTransfer.VerifyAsync(disk, digest);
            }
            Pass("isolated-vhdx-logical-transfer", "No-letter offline export and read-only offline full logical read-back match SHA-256.");
            ram = provider.Publish(ram); physical = await WindowsDiskStorage.ResolveRamAsync(provider, ram, default);
            physical.InitializeNewGpt(); volume = await physical.WaitVolumeAsync(default);
            await WindowsDiskStorage.FormatNtfsAsync(volume, physical.Number, "QC-Provider", default);
            var letter = Enumerable.Range('D', 'Z' - 'D' + 1).Select(n => (char)n).First(c => !DriveInfo.GetDrives().Any(d => d.Name.StartsWith(c + ":", StringComparison.OrdinalIgnoreCase)));
            WindowsDiskStorage.AssignLetter(volume, letter);
            try
            {
                var file = letter + @":\provider-sentinel.bin";
                using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { stream.Write(pattern); stream.Flush(true); }
                if (!File.ReadAllBytes(file).SequenceEqual(pattern)) throw new IOException("NTFS RAM filesystem bytes differ.");
                using (var open = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    try { using var invalid = WindowsDiskStorage.LockVolume(volume, physical.Number); throw new IOException("Open-file volume lock unexpectedly succeeded."); }
                    catch (System.ComponentModel.Win32Exception) { Pass("open-file-veto", "Windows refused exclusive volume lock while the fixture file was open."); }
                }
                File.Delete(file);
            }
            finally { WindowsDiskStorage.RemoveLetter(volume, letter); }
            using (WindowsDiskStorage.LockVolume(volume, physical.Number))
            {
                var operation = Guid.NewGuid(); ram = provider.Freeze(ram, operation);
                var frozenDigest = await LogicalImageTransfer.HashAsync(provider.Storage(ram, operation));
                snapshots.Add(new { Stage = "Frozen", Native = ram, Digest = frozenDigest });
                ram = provider.Thaw(ram, operation);
            }
            Pass("published-filesystem-freeze", "Owned GPT/NTFS device supports filesystem write/flush/read and stable full-disk freeze/thaw.");
            ram = provider.SetReadOnly(ram, true);
            if ((ram.Flags & RamDiskFlags.ReadOnly) == 0) throw new IOException("Read-only native state was not applied.");
            ram = provider.SetReadOnly(ram, false);
            snapshots.Add(new { Stage = "Ready", Native = provider.Query(ram) });
        }
        finally
        {
            Exception? cleanupFailure = null;
            try
            {
                if (ram is not null)
                {
                    using var locked = volume is not null && physical is not null ? WindowsDiskStorage.LockVolume(volume, physical.Number, true) : null;
                    physical?.Dispose(); physical = null;
                    provider.Remove(ram);
                }
                if (provider.Enumerate().Any(d => d.ResourceId == id)) throw new IOException("Owned RAM fixture survived removal.");
                var after = cache.GetWriteCacheState();
                snapshots.Add(new { Stage = "Removed", Cache = after });
                if (after.GlobalReservedBytes != before.GlobalReservedBytes) throw new IOException("RAM teardown did not release the exact shared reservation.");
                Pass("owned-removal-budget", "Native RAM object removed and complete reservation returned exactly once.");
            }
            catch (Exception ex) { cleanupFailure = ex; }
            finally { physical?.Dispose(); RunStorage.AtomicJson(evidence, new { ResourceId = id, Host = host, Image = imagePath, Snapshots = snapshots, CleanupFailure = cleanupFailure?.ToString() }); }
            if (cleanupFailure is not null) throw new IOException("Provider fixture teardown requires recovery; preserve its native evidence.", cleanupFailure);
        }
        return checks;
    }
}
