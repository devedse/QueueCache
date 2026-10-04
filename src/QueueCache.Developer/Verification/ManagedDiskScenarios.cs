using System.Runtime.Versioning;
using QueueCache.Management;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Developer.Verification;

/// <summary>Product broker fixtures, owned by a maintained worker with retained transaction/cleanup evidence.</summary>
[SupportedOSPlatform("windows")]
internal static class ManagedDiskScenarios
{
    public static async Task<IReadOnlyList<CheckResult>> RunAsync(string operation, CacheDevice hostCache, string work, string evidence)
    {
        var service = new ManagedDiskBrokerClient(); var checks = new List<CheckResult>(); var trace = new List<object>();
        var mode = operation switch { "ram-disk" => ManagedDiskMode.EphemeralRam, "vhdx-backed" => ManagedDiskMode.CachedVhdx, "image-in-ram" => ManagedDiskMode.ImageInRam, _ => throw new ArgumentException("Unknown managed product scenario.") };
        var originalBudget = hostCache.GetWriteCacheState().GlobalReservedBytes;
        var variants = mode == ManagedDiskMode.CachedVhdx ? new[] { (ImageAllocation.Dynamic, false), (ImageAllocation.Fixed, false), (ImageAllocation.Dynamic, true) } :
            mode == ManagedDiskMode.ImageInRam ? [(ImageAllocation.Dynamic, false), (ImageAllocation.Dynamic, true)] : [(ImageAllocation.Dynamic, false)];
        foreach (var sectorBytes in VerificationPlan.ManagedSectorSizes)
        foreach (var (allocation, initializeRaw) in variants)
        {
            var id = Guid.NewGuid(); var directory = Path.Combine(work, id.ToString("N"));
            var definition = ManagedDiskDefinition.New(mode) with { ResourceId = id, CapacityBytes = 64UL << 20,
                SectorBytes = sectorBytes,
                PreferredLetter = FreeLetter(), Label = "QC-Managed", Allocation = allocation,
                ImagePath = mode == ManagedDiskMode.EphemeralRam ? null : Path.Combine(directory, "source.vhdx"),
                CheckpointDirectory = mode == ManagedDiskMode.ImageInRam ? Path.Combine(directory, "Checkpoints") : null,
                Cache = mode == ManagedDiskMode.CachedVhdx ? new CacheConfiguration(64, CachePreset.Strict) : null };
            var created = false; string? movedSource = null; Exception? primaryFailure = null;
            try
            {
                if (initializeRaw)
                {
                    ManagedDiskHostProtection.CreateProtectedDirectory(directory);
                    using (WindowsVirtualDisk.CreateNew(definition.ImagePath!, definition.CapacityBytes, definition.SectorBytes, allocation)) { }
                    var identity = await service.InspectAsync(definition.ImagePath!);
                    definition = definition with { Source = ManagedDiskSource.OpenExisting, InitializeBlankImage = true, ExpectedBlankImage = identity };
                }
                await service.CreateAsync(definition, new ProgressLog()); created = true;
                var ready = await RecordAsync(); trace.Add(new { Stage = "Created", Record = ready });
                if (ready.Runtime?.State != ManagedDiskState.Ready || ready.PhysicalDiskNumber is null || ready.VolumePath is null)
                    throw new IOException("Product creation returned without a ready owned disk/volume.");
                var file = definition.PreferredLetter + @":\managed-sentinel.bin";
                var bytes = Enumerable.Range(0, 4 << 20).Select(n => (byte)(n * 17 + 11)).ToArray();
                Write(file, bytes);
                using (var open = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var preview = await RecordAsync();
                    try
                    {
                        await service.ExecuteAsync(new(id, ManagedDiskAction.Stop, ManagedDiskExpected.From(preview.Runtime!),
                            mode == ManagedDiskMode.CachedVhdx ? ManagedDiskStopIntent.DrainThenDetach : ManagedDiskStopIntent.DiscardThenStop, AcceptDiscard: mode != ManagedDiskMode.CachedVhdx));
                        throw new InvalidDataException("Product Stop bypassed a Windows open-file veto.");
                    }
                    catch (IOException ex)
                    {
                        var retained = await RecordAsync(); trace.Add(new { Stage = "StopVeto", Error = ex.Message, Record = retained });
                        if (retained.Runtime?.State != ManagedDiskState.Ready || !File.ReadAllBytes(file).SequenceEqual(bytes))
                            throw new IOException("Stop veto did not retain the live contents/binding.");
                    }
                }
                Pass("managed-open-file-veto-" + allocation + (initializeRaw ? "-raw" : ""), "Product Stop honours Windows locks and preserves the owned live disk.");
                await ActAsync(ManagedDiskAction.Flush);
                if (mode == ManagedDiskMode.ImageInRam)
                {
                    var before = await RecordAsync();
                    if (!before.Runtime!.HasUnsavedChanges) throw new IOException("RAM writes/flush were incorrectly marked saved.");
                    var export = await ActAsync(ManagedDiskAction.Export, Path.Combine(directory, "standalone-export.vhdx"));
                    if (export.Record.CommittedImage != before.CommittedImage || export.Record.Runtime!.SavedGeneration != before.Runtime.SavedGeneration)
                        throw new IOException("Export unexpectedly changed startup source/saved generation.");
                    var saved = await ActAsync(ManagedDiskAction.Save);
                    if (saved.Record.CommittedImage?.Digest is null || saved.Record.PreviousImage != before.CommittedImage)
                        throw new IOException("Save did not verify the full image and retain the preceding checkpoint.");
                    var runtimeStart = await RecordAsync();
                    movedSource = definition.ImagePath + ".runtime-unavailable"; File.Move(definition.ImagePath!, movedSource);
                    bytes[0] ^= 0xFF; Write(file, bytes); await ActAsync(ManagedDiskAction.Flush);
                    if (!File.ReadAllBytes(file).SequenceEqual(bytes)) throw new IOException("Whole-image RAM runtime depends on the unavailable source file.");
                    var runtimeEnd = await RecordAsync();
                    if (runtimeStart.ImageIo is null || runtimeEnd.ImageIo is null || runtimeStart.ImageIo.ObservationEpoch != runtimeEnd.ImageIo.ObservationEpoch ||
                        runtimeStart.ImageIo != runtimeEnd.ImageIo)
                        throw new IOException("RAM runtime image-I/O attempt evidence is missing, has changed epoch, or shows image access.");
                    if (!runtimeEnd.Runtime!.HasUnsavedChanges || runtimeEnd.ImageTransferAttempts != runtimeStart.ImageTransferAttempts ||
                        runtimeEnd.ImageTransferredBytes != runtimeStart.ImageTransferredBytes)
                        throw new IOException("Runtime I/O triggered an image transfer or lost its changed generation.");
                    trace.Add(new { Stage = "SourceUnavailableRuntime", Start = runtimeStart, End = runtimeEnd });
                    File.Move(movedSource, definition.ImagePath!); movedSource = null;
                    await ActAsync(ManagedDiskAction.Stop, discard: true);
                    await ActAsync(ManagedDiskAction.Start);
                    bytes[0] ^= 0xFF;
                    if (!File.ReadAllBytes(file).SequenceEqual(bytes)) throw new IOException("Restarted image did not load the committed checkpoint, excluding discarded RAM changes.");
                    Pass("whole-image-save-export-runtime" + (initializeRaw ? "-raw" : ""), "Full verified save, unchanged-source export, RAM-only runtime with unavailable source and unchanged image-I/O attempts, dirty generation and committed reload match bytes.");
                }
                else if (mode == ManagedDiskMode.CachedVhdx)
                {
                    await ActAsync(ManagedDiskAction.Stop); await ActAsync(ManagedDiskAction.Start);
                    if (!File.ReadAllBytes(file).SequenceEqual(bytes)) throw new IOException("Backed VHDX detach/reopen lost the Strict byte oracle.");
                    var current = await RecordAsync();
                    await service.ExecuteAsync(new(id, ManagedDiskAction.ChangeCache, ManagedDiskExpected.From(current.Runtime!),
                        Cache: new(64, CachePreset.Fast), AcceptVolatileWrites: true));
                    bytes[0] ^= 0xFF; Write(file, bytes); await ActAsync(ManagedDiskAction.Flush);
                    await ActAsync(ManagedDiskAction.Stop); await ActAsync(ManagedDiskAction.Start);
                    if (!File.ReadAllBytes(file).SequenceEqual(bytes)) throw new IOException("Fast backed VHDX explicit flush/detach/reopen differs.");
                    Pass("backed-vhdx-persistence-" + allocation + (initializeRaw ? "-raw" : ""), "Independent shared cache, Strict and explicit Fast flush/detach/reopen preserve the owned byte oracle.");
                }
                else
                {
                    await ActAsync(ManagedDiskAction.Stop, discard: true); await ActAsync(ManagedDiskAction.Start);
                    if (File.Exists(file)) throw new IOException("A new pure RAM creation reused the preceding filesystem contents.");
                    Pass("pure-ram-fresh-creation", "Product format/write/flush/explicit discard/recreate produces a fresh NTFS disk.");
                }
            }
            catch (Exception ex) { primaryFailure = ex; throw; }
            finally
            {
                Exception? cleanup = null;
                try
                {
                    if (movedSource is not null) File.Move(movedSource, definition.ImagePath!);
                    var record = (await service.ListAsync()).SingleOrDefault(r => r.ResourceId == id);
                    if (record is not null)
                    {
                        if (record.Runtime?.State != ManagedDiskState.Stopped) await ActAsync(ManagedDiskAction.Stop, discard: mode != ManagedDiskMode.CachedVhdx);
                        await ActAsync(ManagedDiskAction.RemoveDefinition);
                    }
                    if (hostCache.GetWriteCacheState().GlobalReservedBytes != originalBudget) throw new IOException("Managed teardown did not restore the exact shared RAM reservation.");
                }
                catch (Exception ex) { cleanup = ex; }
                trace.Add(new { Stage = "Cleanup", ResourceId = id, Created = created, PrimaryFailure = primaryFailure?.ToString(), Error = cleanup?.ToString(), FilesRetained = directory });
                RunStorage.AtomicJson(evidence, trace);
                if (cleanup is not null) throw new IOException("Managed product fixture cleanup failed; preserve the recorded owned resource and images.",
                    primaryFailure is null ? cleanup : new AggregateException(primaryFailure, cleanup));
            }
            async Task<ManagedDiskRecord> RecordAsync() => (await service.ListAsync()).Single(r => r.ResourceId == id);
            void Pass(string name, string detail) { name += "-" + sectorBytes; checks.Add(new(name, "PASS", detail)); Console.WriteLine(name + ": " + detail); }
            async Task<ManagedDiskOperationResult> ActAsync(ManagedDiskAction action, string? path = null, bool discard = false)
            {
                var record = await RecordAsync();
                var result = await service.ExecuteAsync(new(id, action, ManagedDiskExpected.From(record.Runtime!),
                    discard ? ManagedDiskStopIntent.DiscardThenStop : null, Path: path, AcceptDiscard: discard), new ProgressLog());
                trace.Add(new { Stage = action.ToString(), Result = result }); RunStorage.AtomicJson(evidence, trace); return result;
            }
        }
        return checks;
    }
    private static char FreeLetter() => Enumerable.Range('D', 'Z' - 'D' + 1).Select(i => (char)i)
        .First(c => !DriveInfo.GetDrives().Any(d => d.Name.StartsWith(c + ":", StringComparison.OrdinalIgnoreCase)));
    private static void Write(string file, byte[] bytes)
    { using var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough); stream.Write(bytes); stream.Flush(true); }
    private sealed class ProgressLog : IProgress<ManagedDiskProgress>
    { public void Report(ManagedDiskProgress value) => Console.WriteLine($"{value.Stage}: {value.Message} {value.CompletedBytes}/{value.TotalBytes}"); }
}
