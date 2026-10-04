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
        var checks = new List<CheckResult>(); var files = new List<string>();
        var completed = false;
        try
        {
            foreach (var sector in VerificationPlan.ManagedSectorSizes)
            {
                var raw = evidence + ".sector-" + sector + ".json"; files.Add(raw);
                checks.AddRange(await RunGeometryAsync(host, cache, Path.Combine(directory, "sector-" + sector), raw, sector));
            }
            completed = true; return checks;
        }
        finally { RunStorage.AtomicJson(evidence, new { Completed = completed, GeometryEvidence = files, Checks = checks }); }
    }

    private static async Task<IReadOnlyList<CheckResult>> RunGeometryAsync(DiskTarget host, CacheDevice cache, string directory, string evidence, uint sectorBytes)
    {
        var checks = new List<CheckResult>();
        void Pass(string name, string detail) { name += "-" + sectorBytes; checks.Add(new(name, "PASS", detail)); Console.WriteLine(name + ": " + detail); }
        Directory.CreateDirectory(directory);
        using var provider = WindowsRamDisk.Connect();
        var before = cache.GetWriteCacheState();
        var id = Guid.NewGuid(); RamDiskSnapshot? ram = null; WindowsDiskStorage? physical = null;
        string? volume = null;
        var snapshots = new List<object>();
        Exception? primaryFailure = null;
        var imagePath = Path.Combine(directory, "provider-" + id.ToString("N") + ".vhdx");
        try
        {
            var originalDisks = provider.Enumerate();
            foreach (var afterSlabs in new uint[] { 1, 8, 16 })
            {
                var failedResource = Guid.NewGuid(); var proofBefore = provider.AllocationFailureProof(); var failed = false;
                try { provider.CreateWithAllocationFailure(failedResource, 64UL << 20, sectorBytes, afterSlabs); }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1450) { failed = true; }
                var proofAfter = provider.AllocationFailureProof(); var budgetAfter = cache.GetWriteCacheState();
                snapshots.Add(new { Stage = "AllocationFailure", Resource = failedResource, RequestedSlabs = afterSlabs,
                    Before = proofBefore, After = proofAfter, Cache = budgetAfter });
                ManagedProviderEvidence.ValidateAllocationFailure(proofBefore, proofAfter, failedResource, afterSlabs,
                    before.GlobalReservedBytes, budgetAfter.GlobalReservedBytes);
                if (!failed || !provider.Enumerate().Select(Identity).SequenceEqual(originalDisks.Select(Identity)))
                    throw new IOException("Native allocation failure did not reach its requested boundary, remove all private state and restore the exact shared reservation.");
            }
            Pass("allocation-failure-rollback", "Real allocations fail after 1/8/16 slabs, with native boundary proof, no surviving object and exact reservation restoration.");
            ram = provider.Create(id, 64UL << 20, sectorBytes); snapshots.Add(new { Stage = "Private", Native = ram });
            if ((ram.Flags & RamDiskFlags.Published) != 0 || provider.Enumerate().Single(d => d.ResourceId == id) != ram)
                throw new IOException("Private creation unexpectedly published or lost its native ownership.");
            var reserved = cache.GetWriteCacheState();
            ManagedProviderEvidence.ValidateReservation(ram, before.GlobalReservedBytes, reserved.GlobalReservedBytes);
            var storage = provider.Storage(ram);
            var sector = new byte[sectorBytes];
            await storage.ReadAsync(0, sector, default);
            if (sector.Any(b => b != 0)) throw new IOException("New RAM sectors are not zero.");
            var pattern = Enumerable.Range(0, 1 << 20).Select(n => (byte)(n * 31 + 7)).ToArray();
            await storage.WriteAsync(ram.CapacityBytes - (ulong)pattern.Length, pattern, default);
            var read = new byte[pattern.Length]; await storage.ReadAsync(ram.CapacityBytes - (ulong)pattern.Length, read, default);
            if (!read.SequenceEqual(pattern)) throw new IOException("Private RAM tail-sector transfer differs.");
            Pass("private-owned-ram", "Unpublished zeroed storage, exact tail transfer and shared cache/RAM accounting.");
            var generation = provider.Query(ram).WriteGeneration;
            await RejectRangeAsync(() => storage.ReadAsync(1, sector, default).AsTask());
            await RejectRangeAsync(() => storage.ReadAsync(ram.CapacityBytes, sector, default).AsTask());
            await RejectRangeAsync(() => storage.WriteAsync(0, new byte[sectorBytes - 1], default).AsTask());
            if (provider.Query(ram).WriteGeneration != generation) throw new IOException("Rejected native range changed the RAM generation.");
            Pass("native-range-rejection", "Unaligned offset, end-of-disk read and partial-sector write fail with ERROR_INVALID_PARAMETER and preserve generation.");
            // Isolated native VHDX raw access: no filesystem or letter; source attachment is read-only on verification.
            LogicalImageDigest digest;
            using (var image = WindowsVirtualDisk.CreateNew(imagePath, ram.CapacityBytes, sectorBytes, ImageAllocation.Dynamic))
            using (var disk = WindowsDiskStorage.Open(image.Attach(false), true))
            {
                disk.SetOffline(true);
                digest = await LogicalImageTransfer.CopyAsync(storage, disk);
            }
            using (var image = WindowsVirtualDisk.Open(imagePath, false, true))
            using (var disk = WindowsDiskStorage.Open(image.Attach(true), true))
            {
                await LogicalImageTransfer.VerifyAsync(disk, digest);
            }
            Pass("isolated-vhdx-logical-transfer", "No-letter offline export and read-only letterless full logical read-back match SHA-256.");
            ram = provider.Publish(ram); physical = await WindowsDiskStorage.ResolveRamAsync(provider, ram, default);
            physical.InitializeNewGpt(); volume = await physical.WaitVolumeAsync(default);
            await WindowsDiskStorage.FormatNtfsAsync(volume, physical.Number, "QC-Provider", default);
            var letter = Enumerable.Range('D', 'Z' - 'D' + 1).Select(n => (char)n).First(c => !DriveInfo.GetDrives().Any(d => d.Name.StartsWith(c + ":", StringComparison.OrdinalIgnoreCase)));
            WindowsDiskStorage.AssignLetter(volume, letter);
            try
            {
                using (var ramCache = new CacheDevice(letter + ":", writable: true))
                {
                    try
                    {
                        ramCache.Control(WriteCacheAction.Configure, 16UL << 20);
                        ramCache.Control(WriteCacheAction.Release);
                        throw new IOException("Native filter permitted a redundant cache on an owned RAM disk.");
                    }
                    catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 50) { }
                    if (ramCache.GetWriteCacheState().BudgetBytes != 0 || cache.GetWriteCacheState().GlobalReservedBytes != reserved.GlobalReservedBytes)
                        throw new IOException("Rejected redundant cache changed shared reservations.");
                }
                Pass("redundant-cache-refusal", "Native cache configuration fails with ERROR_NOT_SUPPORTED before reserving more shared RAM.");
                var file = letter + @":\provider-sentinel.bin";
                using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { stream.Write(pattern); stream.Flush(true); }
                if (!File.ReadAllBytes(file).SequenceEqual(pattern)) throw new IOException("NTFS RAM filesystem bytes differ.");
                var trimPath = letter + @":\provider-trim.bin";
                var trimBytes = new byte[3 << 20]; new Random(718).NextBytes(trimBytes);
                using (var trimmed = new AlignedFile(trimPath, trimBytes.Length, create: true))
                {
                    trimmed.Write(0, trimBytes); trimmed.Flush();
                    var observed = new byte[trimBytes.Length]; trimmed.Read(0, observed);
                    if (!observed.SequenceEqual(trimBytes)) throw new IOException("Provider pre-TRIM bytes differ.");
                    var trimBefore = provider.Query(ram);
                    trimmed.Trim(1 << 20, 1 << 20); trimmed.Read(0, observed);
                    var trimAfter = provider.Query(ram);
                    snapshots.Add(new { Stage = "Trim", Before = trimBefore, After = trimAfter });
                    ManagedProviderEvidence.ValidateTrim(trimBefore, trimAfter, trimBytes, observed, 1 << 20, 1 << 20);
                    new Random(719).NextBytes(trimBytes); trimmed.Write(0, trimBytes); trimmed.Flush(); trimmed.Read(0, observed);
                    if (!observed.SequenceEqual(trimBytes)) throw new IOException("Provider post-TRIM reuse differs.");
                }
                File.Delete(trimPath);
                Pass("native-trim-zero-guards-reuse", "File-relative TRIM reaches the provider, advances generation, zeroes the middle range, preserves guards and permits exact rewrite.");
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
            using (WindowsDiskStorage.LockVolume(volume, physical.Number))
            {
                var offset = ram.CapacityBytes - (1UL << 20); var beforeBytes = new byte[sectorBytes];
                await physical.ReadAsync(offset, beforeBytes, default);
                var writableBefore = physical.MediaWritable();
                ram = provider.SetReadOnly(ram, true);
                try
                {
                    if ((ram.Flags & RamDiskFlags.ReadOnly) == 0) throw new IOException("Read-only native state was not applied.");
                    var writableWhileReadOnly = physical.MediaWritable();
                    var protectedState = provider.Query(ram); int? writeError = null;
                    try { await physical.WriteAsync(offset, new byte[sectorBytes], default); }
                    catch (IOException ex) { writeError = ex.HResult & 0xFFFF; }
                    var afterBytes = new byte[sectorBytes]; await physical.ReadAsync(offset, afterBytes, default);
                    var rejectedState = provider.Query(ram);
                    snapshots.Add(new { Stage = "ReadOnlyWrite", WritableBefore = writableBefore, WritableWhileReadOnly = writableWhileReadOnly,
                        WriteError = writeError, Before = protectedState, After = rejectedState });
                    ManagedProviderEvidence.ValidateReadOnlyWrite(writableBefore, writableWhileReadOnly, writeError, protectedState.Errors,
                        rejectedState.Errors, protectedState.WriteGeneration, rejectedState.WriteGeneration, afterBytes.SequenceEqual(beforeBytes));
                    Pass("native-readonly-write-veto", $"Windows reports the media write-protected; a physical sector write fails (Win32 {writeError}) as exactly one provider rejection with unchanged sectors and generation.");
                }
                finally { ram = provider.SetReadOnly(ram, false); }
            }
            snapshots.Add(new { Stage = "Ready", Native = provider.Query(ram) });
        }
        catch (Exception ex) { primaryFailure = ex; throw; }
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
            finally { physical?.Dispose(); RunStorage.AtomicJson(evidence, new { ResourceId = id, Host = host, Image = imagePath, Snapshots = snapshots, PrimaryFailure = primaryFailure?.ToString(), CleanupFailure = cleanupFailure?.ToString() }); }
            if (cleanupFailure is not null) throw new IOException("Provider fixture teardown requires recovery; preserve its native evidence.",
                primaryFailure is null ? cleanupFailure : new AggregateException(primaryFailure, cleanupFailure));
        }
        return checks;

        static (Guid Resource, Guid Epoch, ulong Creation, ulong Capacity, uint Sector, uint Slot, ulong Reservation) Identity(RamDiskSnapshot disk)
            => (disk.ResourceId, disk.BootEpoch, disk.CreationGeneration, disk.CapacityBytes, disk.SectorBytes, disk.Slot, disk.ReservedBytes);

        static async Task RejectRangeAsync(Func<Task> operation)
        {
            try { await operation(); }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 87) { return; }
            throw new IOException("Invalid native RAM range was not rejected with ERROR_INVALID_PARAMETER.");
        }
    }
}
