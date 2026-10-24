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
        string work, string evidence)
    {
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
                ResourceId = id, CapacityBytes = 128UL * MiB, SectorBytes = sector, PreferredLetter = letter,
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

                // A corrupt hint must cause a new complete save, never another attempt to trust it.
                var sidecar = ImageChunkMap.PathFor(reused.PreviousImage!.Identity.Path);
                File.WriteAllBytes(sidecar, [0x51]);
                protectedHash = Hash(reused.CommittedImage.Identity.Path);
                bytes[MiB] ^= 0x5f; Write(payloadPath, bytes);
                before = await Record();
                await Act(ManagedDiskAction.Save);
                var full = await Record(); ValidateSave(before, full, false);
                if (Hash(reused.CommittedImage.Identity.Path) != protectedHash || Hash(definition.ImagePath!) != sourceHash)
                    throw new IOException("Full fallback changed a protected checkpoint or source.");
                trace.Add(new { Stage = "FullFallbackEvidence", Before = before, After = full });
                Pass("invalid-map-full-save", $"Corrupt candidate fingerprints caused a new full verified save of {full.LastSave!.PayloadBytes} bytes; prior current/source unchanged. Save {full.LastSave.Duration.TotalSeconds:F3} s. Times are not a performance verdict.");
                await Act(ManagedDiskAction.Stop, discard: true);
                await Act(ManagedDiskAction.Start);
                if (!File.ReadAllBytes(payloadPath).SequenceEqual(bytes) || !File.ReadAllBytes(guardPath).SequenceEqual(guard))
                    throw new IOException("Committed checkpoint reload lost payload or untouched guard bytes.");
                Pass("committed-reload", "Payload and untouched guard match after discard and committed-image reload.");
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
            async Task Act(ManagedDiskAction action, bool discard = false)
            {
                var record = await Record();
                var result = await service.ExecuteAsync(new(id, action, ManagedDiskExpected.From(record.Runtime!),
                    discard ? ManagedDiskStopIntent.DiscardThenStop : null, AcceptDiscard: discard), new ConsoleProgress());
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
    private sealed class ConsoleProgress : IProgress<ManagedDiskProgress>
    { public void Report(ManagedDiskProgress value) => Console.WriteLine($"{value.Stage}: {value.Message} {value.CompletedBytes}/{value.TotalBytes}"); }
}
