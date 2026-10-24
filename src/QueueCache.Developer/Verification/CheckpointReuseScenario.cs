using System.Runtime.Versioning;
using System.Security.Cryptography;
using QueueCache.Management;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Developer.Verification;

/// <summary>Owned broker fixtures for changed-region saves, full verification and safe fallback.
/// Elapsed times are retained as evidence; a Verifier run is not a performance comparison.</summary>
[SupportedOSPlatform("windows")]
public static class CheckpointReuseScenario
{
    private const int MiB = 1 << 20;
    public static int ValidateImageMiB(int imageMiB) => imageMiB is >= 128 and <= 2048
        ? imageMiB : throw new ArgumentOutOfRangeException(nameof(imageMiB), "Checkpoint fixtures require 128..2048 MiB.");
    public static int ExistingDataMiB(int imageMiB) => ValidateImageMiB(imageMiB) >= 1024 ? imageMiB / 2 : 0;
    public static void ValidateSave(ManagedDiskRecord before, ManagedDiskRecord after, bool reused)
    {
        var size = after.Definition.CapacityBytes;
        var saved = after.LastSave ?? throw new InvalidDataException("Missing completed-save statistics.");
        var a = before.ImageIo ?? throw new InvalidDataException("Missing pre-save image I/O evidence.");
        var b = after.ImageIo ?? throw new InvalidDataException("Missing post-save image I/O evidence.");
        if (saved.PayloadBytes is not { } payload || saved.Bytes != size || saved.ReusedCandidate != reused ||
            reused && (payload == 0 || payload >= size || saved.ContainerHeaderBytes != 8192) || !reused && payload != size ||
            a.ObservationEpoch != b.ObservationEpoch || b.WrittenBytes < a.WrittenBytes || b.ReadBytes < a.ReadBytes ||
            b.WrittenBytes - a.WrittenBytes != payload || b.ReadBytes - a.ReadBytes < size ||
            after.CommittedImage?.Digest?.Bytes != size || after.Runtime?.State != ManagedDiskState.Ready)
            throw new InvalidDataException("Save lacks changed-payload/full-verification evidence or did not use the required candidate strategy.");
    }
    public static async Task<IReadOnlyList<CheckResult>> RunAsync(DiskTarget target, CacheDevice host,
        string work, string evidence, int imageMiB = 128)
    {
        ValidateImageMiB(imageMiB);
        // The worker writes its CheckResult[] to the reply itself after this method
        // returns. Keep transaction/I/O evidence separate from that final reply.
        evidence += ".trace.json";
        target.ValidateCurrent();
        if (target.IsBoot || target.IsSystem || target.IsPaging || Directory.Exists(work) ||
            !Path.GetDirectoryName(Path.GetFullPath(work))!.Equals(target.Root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Checkpoint reuse requires a fresh owned directory on a non-OS lab volume.");
        var service = new ManagedDiskBrokerClient();
        var baseline = host.GetWriteCacheState().GlobalReservedBytes;
        var trace = new List<object>(); var checks = new List<CheckResult>();
        foreach (var (allocation, sector) in new[] { (ImageAllocation.Dynamic, 512u), (ImageAllocation.Fixed, 4096u) })
        {
            var id = Guid.NewGuid(); var directory = Path.Combine(work, id.ToString("N"));
            var existing = await service.ListAsync();
            var letter = Enumerable.Range('F', 'Z' - 'F' + 1).Select(n => (char)n).Reverse().First(c =>
                !DriveInfo.GetDrives().Any(d => d.Name.StartsWith(c + ":", StringComparison.OrdinalIgnoreCase)) &&
                existing.All(r => r.Definition.PreferredLetter != c));
            var definition = ManagedDiskDefinition.New(ManagedDiskMode.ImageInRam) with
            {
                ResourceId = id, CapacityBytes = (ulong)imageMiB * MiB, SectorBytes = sector, PreferredLetter = letter,
                Label = "QC-Delta", Allocation = allocation, ImagePath = Path.Combine(directory, "source.vhdx"),
                CheckpointDirectory = Path.Combine(directory, "Checkpoints"), Access = RamAccess.Direct
            };
            Exception? failure = null;
            try
            {
                await service.CreateAsync(definition, new ConsoleProgress());
                var root = letter + @":\";
                var payloadPath = Path.Combine(root, "payload.bin"); var guardPath = Path.Combine(root, "guard.bin");
                var bytes = ConcurrentCacheOracle.Pattern(229, 1, 4 * MiB);
                var guard = ConcurrentCacheOracle.Pattern(230, 1, MiB);
                Write(payloadPath, bytes); Write(guardPath, guard);
                var existingPath = Path.Combine(root, "existing-data.bin");
                var existingMiB = ExistingDataMiB(imageMiB);
                var existingHash = existingMiB > 0 ? SeedExisting(existingPath, existingMiB) : null;
                if (existingHash is not null)
                {
                    trace.Add(new { Stage = "ExistingData", ResourceId = id, Path = existingPath, Bytes = (ulong)existingMiB * MiB, Sha256 = existingHash });
                    RunStorage.AtomicJson(evidence, trace);
                }
                await Act(ManagedDiskAction.Save); // First generated checkpoint and fingerprints.
                await Act(ManagedDiskAction.Save); // Second; first can now be reused without touching current.
                var before = await Record();
                var protectedPath = before.CommittedImage!.Identity.Path;
                var protectedHash = Hash(protectedPath); var sourceHash = Hash(definition.ImagePath!);
                bytes[0] ^= 0x7f; Write(payloadPath, bytes);
                before = await Record();
                await Act(ManagedDiskAction.Save);
                var reused = await Record(); ValidateSave(before, reused, true);
                if (Hash(protectedPath) != protectedHash || Hash(definition.ImagePath!) != sourceHash ||
                    reused.PreviousImage != before.CommittedImage || reused.CommittedImage!.Identity.Path != before.PreviousImage!.Identity.Path)
                    throw new IOException("Reuse changed the protected current/original image or committed the wrong candidate.");
                Pass("changed-regions", $"{reused.LastSave!.PayloadBytes}/{definition.CapacityBytes} logical payload bytes written, " +
                    $"{reused.LastSave.ContainerHeaderBytes} VHDX header bytes; full logical read-back and unchanged current/source containers verified. Save {reused.LastSave.Duration.TotalSeconds:F3} s.");
                trace.Add(new { Stage = "ReuseEvidence", Before = before, After = reused, ProtectedHash = protectedHash, SourceHash = sourceHash });

                if (allocation == ImageAllocation.Dynamic)
                {
                    // Force growth beyond the small saved payload. The older dynamic
                    // candidate must really contain a sparse BAT block for this case.
                    using (var container = File.OpenRead(reused.PreviousImage!.Identity.Path))
                        if (!VhdxPayloadLayout.Read(container, definition.CapacityBytes, sector).PayloadOffsets.Any(p => p < 0))
                            throw new IOException("Dynamic growth fixture has no sparse block to exercise allocation fallback.");
                    Write(Path.Combine(root, "growth.bin"), ConcurrentCacheOracle.Pattern(231, 1, 80 * MiB));
                    before = await Record();
                    protectedHash = Hash(before.CommittedImage!.Identity.Path);
                    await Act(ManagedDiskAction.Save);
                    var grown = await Record(); ValidateSave(before, grown, false);
                    if (Hash(before.CommittedImage.Identity.Path) != protectedHash || Hash(definition.ImagePath!) != sourceHash)
                        throw new IOException("Sparse-growth fallback changed a protected image.");
                    trace.Add(new { Stage = "SparseGrowthEvidence", Before = before, After = grown });
                    Pass("sparse-growth-full-save", "A changed unallocated block selected a complete new verified candidate before modifying the older image.");
                    reused = grown;
                }

                // A corrupt hint must cause a new complete save, never another attempt to trust it.
                var sidecar = ImageChunkMap.PathFor(reused.PreviousImage!.Identity.Path);
                File.WriteAllBytes(sidecar, [0x51]);
                protectedHash = Hash(reused.CommittedImage!.Identity.Path);
                bytes[MiB] ^= 0x5f; Write(payloadPath, bytes);
                before = await Record();
                await Act(ManagedDiskAction.Save);
                var full = await Record(); ValidateSave(before, full, false);
                if (Hash(reused.CommittedImage!.Identity.Path) != protectedHash || Hash(definition.ImagePath!) != sourceHash)
                    throw new IOException("Full fallback changed a protected checkpoint or source.");
                trace.Add(new { Stage = "FullFallbackEvidence", Before = before, After = full });
                Pass("invalid-map-full-save", $"Corrupt candidate fingerprints caused a new full verified save of {full.LastSave!.PayloadBytes} bytes; prior current/source unchanged. Save {full.LastSave.Duration.TotalSeconds:F3} s. Times are not a performance verdict.");
                await Act(ManagedDiskAction.Stop, discard: true);
                await Act(ManagedDiskAction.Start);
                if (!File.ReadAllBytes(payloadPath).SequenceEqual(bytes) || !File.ReadAllBytes(guardPath).SequenceEqual(guard))
                    throw new IOException("Committed checkpoint reload lost payload or untouched guard bytes.");
                if (existingHash is not null && Hash(existingPath) != existingHash)
                    throw new IOException("Committed checkpoint reload lost existing populated-image data.");
                Pass("committed-reload", "Payload and untouched guard match after discard and committed-image reload." +
                    (existingHash is null ? "" : " Populated-image data also matches its recorded SHA-256."));

                // A user can choose an export name resembling an automatic checkpoint.
                // Even after committing it and rotating it to PreviousImage, never consume it.
                var exportPath = Path.Combine(definition.CheckpointDirectory!, $"{id:N}-user-selected.vhdx");
                await Act(ManagedDiskAction.Export, path: exportPath, commitExport: true);
                var exportHash = Hash(exportPath);
                if ((await Record()).CommittedImage!.GeneratedCheckpoint)
                    throw new IOException("User-selected export acquired automatic checkpoint provenance.");
                await Act(ManagedDiskAction.Save);
                var rotated = await Record();
                if (rotated.PreviousImage!.Identity.Path != exportPath || ManagedCheckpointReuse.Candidate(rotated) is not null)
                    throw new IOException("User-selected export was eligible after pointer rotation.");
                await Act(ManagedDiskAction.Save);
                if (Hash(exportPath) != exportHash)
                    throw new IOException("A later automatic save modified the user-selected export.");
                Pass("export-provenance", "A committed export with an automatic-looking filename remains byte-identical after two later saves.");
            }
            catch (Exception ex) { failure = ex; throw; }
            finally
            {
                Exception? cleanup = null;
                try
                {
                    var current = (await service.ListAsync()).SingleOrDefault(r => r.ResourceId == id);
                    if (current is not null)
                    {
                        if (current.Runtime is { State: not ManagedDiskState.Stopped }) await Act(ManagedDiskAction.Stop, discard: true);
                        await Act(ManagedDiskAction.RemoveDefinition);
                    }
                    if (host.GetWriteCacheState().GlobalReservedBytes != baseline)
                        throw new IOException("Checkpoint fixture did not restore the original shared RAM reservation.");
                }
                catch (Exception ex) { cleanup = ex; }
                trace.Add(new { Stage = "Cleanup", ResourceId = id, PrimaryFailure = failure?.ToString(), Failure = cleanup?.ToString(), Images = directory });
                RunStorage.AtomicJson(evidence, trace);
                if (cleanup is not null) throw new IOException("Checkpoint fixture cleanup failed; preserve owned evidence.",
                    failure is null ? cleanup : new AggregateException(failure, cleanup));
            }
            async Task<ManagedDiskRecord> Record() => (await service.ListAsync()).Single(r => r.ResourceId == id);
            async Task Act(ManagedDiskAction action, bool discard = false, string? path = null, bool commitExport = false)
            {
                var record = await Record();
                var result = await service.ExecuteAsync(new(id, action, ManagedDiskExpected.From(record.Runtime!),
                    discard ? ManagedDiskStopIntent.DiscardThenStop : null, Path: path, CommitExport: commitExport,
                    AcceptDiscard: discard), new ConsoleProgress());
                trace.Add(new { Stage = action.ToString(), Result = result }); RunStorage.AtomicJson(evidence, trace);
            }
            void Pass(string name, string detail)
            {
                var label = $"checkpoint-reuse/{allocation}-{sector}/{name}";
                checks.Add(new(label, "PASS", detail)); Console.WriteLine(label + ": " + detail);
            }
        }
        return checks;
    }
    private static void Write(string path, byte[] bytes)
    { using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough); file.Write(bytes); file.Flush(true); }
    private static string Hash(string path)
    { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static string SeedExisting(string path, int mebibytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, MiB, FileOptions.WriteThrough);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var offset = 0; offset < mebibytes; offset += 8)
        {
            var bytes = ConcurrentCacheOracle.Pattern(232, offset + 1, Math.Min(8, mebibytes - offset) * MiB);
            file.Write(bytes); digest.AppendData(bytes);
        }
        file.Flush(true);
        return Convert.ToHexString(digest.GetHashAndReset());
    }
    private sealed class ConsoleProgress : IProgress<ManagedDiskProgress>
    { public void Report(ManagedDiskProgress value) => Console.WriteLine($"{value.Stage}: {value.Message} {value.CompletedBytes}/{value.TotalBytes}"); }
}
