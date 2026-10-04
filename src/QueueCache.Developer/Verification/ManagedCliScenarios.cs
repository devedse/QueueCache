using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using QueueCache.Management;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Developer.Verification;

/// <summary>Exercises the shipped product commands, retaining every owned child and its exact JSON/error output.</summary>
[SupportedOSPlatform("windows")]
internal static class ManagedCliScenarios
{
    public static async Task<IReadOnlyList<CheckResult>> RunAsync(WorkerJob job, CacheDevice host)
    {
        if (string.IsNullOrWhiteSpace(job.ProductExecutable) || job.ProductPrefix is null)
            throw new InvalidDataException("The coordinator must supply its exact product command invocation.");
        var service = new ManagedDiskBrokerClient(); var checks = new List<CheckResult>();
        var trace = new List<object>(); var sequence = 0; var baseline = host.GetWriteCacheState().GlobalReservedBytes;
        var previousIds = (await service.ListAsync()).Select(r => r.ResourceId).ToHashSet();
        foreach (var sector in VerificationPlan.ManagedSectorSizes)
        foreach (var mode in Enum.GetValues<ManagedDiskMode>())
        {
            var nonce = Guid.NewGuid().ToString("N"); var label = "QC-CLI-" + nonce[..16];
            var directory = Path.Combine(job.WorkDirectory!, nonce); ManagedDiskHostProtection.CreateProtectedDirectory(directory);
            var source = Path.Combine(directory, "source.vhdx"); var checkpoints = Path.Combine(directory, "Checkpoints");
            var activeSource = source;
            var letter = Enumerable.Range('D', 'Z' - 'D' + 1).Select(n => (char)n)
                .First(c => !DriveInfo.GetDrives().Any(d => d.Name.StartsWith(c + ":", StringComparison.OrdinalIgnoreCase)));
            Guid? resource = null; Exception? primary = null;
            var fixture = new ManagedCliFixture(mode, label, letter, 64UL << 20, sector,
                mode == ManagedDiskMode.EphemeralRam ? null : source, previousIds);
            var bytes = new byte[1 << 20]; new Random(1729).NextBytes(bytes);
            try
            {
                var args = new List<string> { "create", "--mode", mode switch { ManagedDiskMode.EphemeralRam => "ram", ManagedDiskMode.CachedVhdx => "cached-vhdx", _ => "image-in-ram" },
                    "--size-mib", "64", "--sector-bytes", sector.ToString(CultureInfo.InvariantCulture), "--letter", letter.ToString(), "--label", label, "--json" };
                if (mode != ManagedDiskMode.EphemeralRam) args.AddRange(["--new-image", source]);
                if (mode == ManagedDiskMode.ImageInRam) args.AddRange(["--checkpoint-directory", checkpoints]);
                if (mode == ManagedDiskMode.CachedVhdx) args.AddRange(["--budget-mib", "32", "--preset", "Strict"]);
                var created = Read<ManagedDiskRecord>(await CommandAsync(args)); resource = created.ResourceId;
                RequireOwned(created); RequireReady(created);
                var listed = Read<ManagedDiskRecord[]>(await CommandAsync(["list", "--json"]));
                if (listed.Count(r => r.ResourceId == resource) != 1) throw new IOException("CLI list omitted or duplicated its created resource.");
                var ready = await StatusAsync(); ready.Runtime!.CheckExpected(created.Runtime!.BootEpoch, created.Runtime.CreationGeneration);
                await ActionAsync("startup", ["--enabled", "true"]);
                var startup = await ActionAsync("startup", ["--enabled", "false"]);
                if (startup.Record.Definition.StartAtBoot) throw new IOException("CLI startup did not remember false.");
                await ActionAsync("recover");
                var recovered = await StatusAsync(); recovered.Runtime!.CheckExpected(ready.Runtime.BootEpoch, ready.Runtime.CreationGeneration);

                var file = letter + @":\cli-sentinel.bin";
                Write(file, bytes); await ActionAsync("flush"); RequireBytes();
                if (mode == ManagedDiskMode.ImageInRam)
                {
                    var before = await StatusAsync(); var export = Path.Combine(checkpoints, resource.Value.ToString("N") + "-cli-export.vhdx");
                    var exported = await ActionAsync("export", ["--path", export]);
                    if (exported.Record.CommittedImage != before.CommittedImage || exported.Record.Runtime!.SavedGeneration != before.Runtime!.SavedGeneration)
                        throw new IOException("CLI export changed the checkpoint pointer/generation without --commit.");
                    var inspected = Read<ImageInspection>(await CommandAsync(["inspect", export]));
                    if (inspected.VirtualBytes != created.Definition.CapacityBytes || inspected.SectorBytes != sector)
                        throw new IOException("CLI inspect returned the wrong exported image geometry.");
                    await ActionAsync("delete-image", ["--path", export, "--accept-erase"]);
                    if (File.Exists(export)) throw new IOException("CLI delete-image left its unreferenced owned export present.");
                    var saved = await ActionAsync("save");
                    if (saved.Record.CommittedImage?.Digest is null) throw new IOException("CLI save did not commit a verified image.");
                }
                if (mode == ManagedDiskMode.CachedVhdx)
                {
                    var cache = await ActionAsync("cache", ["--budget-mib", "32", "--preset", "Fast", "--accept-volatile-flush"]);
                    if (cache.Record.Definition.Cache?.Preset != CachePreset.Fast || !cache.Record.Definition.AcceptVolatileWrites)
                        throw new IOException("CLI cache did not remember explicit Fast configuration.");
                }

                using (var open = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    await CommandAsync(["stop", resource.Value.ToString(), "--json", .. StopFlags()], expectedExit: 1);
                RequireReady(await StatusAsync()); RequireBytes();
                var preview = await StatusAsync();
                await CommandAsync(["format", resource.Value.ToString(), "--accept-erase", "--expected-boot", Guid.NewGuid().ToString(),
                    "--expected-creation", preview.Runtime!.CreationGeneration.ToString(CultureInfo.InvariantCulture),
                    "--expected-write", preview.Runtime.WriteGeneration.ToString(CultureInfo.InvariantCulture), "--json"], expectedExit: 1);
                RequireBytes();
                await ActionAsync("format", ["--accept-erase", "--label", label]);
                if (File.Exists(file)) throw new IOException("CLI format retained the erased fixture file.");
                Write(file, bytes); await ActionAsync("flush");
                await ActionAsync("stop", StopFlags());
                if ((await StatusAsync()).Runtime?.State != ManagedDiskState.Stopped) throw new IOException("CLI stop did not retain a stopped recipe.");
                await ActionAsync("configure", ["--label", label]);
                await ActionAsync("start"); RequireReady(await StatusAsync());
                if (mode == ManagedDiskMode.EphemeralRam)
                { if (File.Exists(file)) throw new IOException("CLI RAM restart retained discarded contents."); }
                else RequireBytes();
                var stopped = await ActionAsync("stop", mode == ManagedDiskMode.CachedVhdx ? [] : ["--discard"]);
                var reloadSource = stopped.Record.CommittedImage?.Identity.Path ?? source;
                await ActionAsync("remove");
                if (Read<ManagedDiskRecord[]>(await CommandAsync(["list", "--json"])).Any(r => r.ResourceId == resource))
                    throw new IOException("CLI remove retained its stopped definition.");
                if (mode != ManagedDiskMode.EphemeralRam && !File.Exists(source)) throw new IOException("CLI resource removal deleted its source image.");
                if (mode != ManagedDiskMode.EphemeralRam)
                {
                    resource = null; activeSource = reloadSource; fixture = fixture with { ImagePath = activeSource };
                    var loadArgs = new List<string> { "create", "--mode", mode == ManagedDiskMode.CachedVhdx ? "cached-vhdx" : "image-in-ram",
                        "--load", activeSource, "--letter", letter.ToString(), "--label", label, "--json" };
                    if (mode == ManagedDiskMode.CachedVhdx) loadArgs.AddRange(["--budget-mib", "32", "--preset", "Strict"]);
                    else loadArgs.AddRange(["--checkpoint-directory", checkpoints, "--read-only"]);
                    var loaded = Read<ManagedDiskRecord>(await CommandAsync(loadArgs)); resource = loaded.ResourceId;
                    RequireReady(loaded); RequireBytes();
                    if (loaded.Definition.Source != ManagedDiskSource.OpenExisting || loaded.Definition.ReadOnly != (mode == ManagedDiskMode.ImageInRam))
                        throw new IOException("CLI load did not remember existing-source/read-only semantics.");
                    await ActionAsync("flush");
                    await ActionAsync("stop", mode == ManagedDiskMode.CachedVhdx ? [] : ["--discard"]);
                    await ActionAsync("remove");
                    if (!File.Exists(activeSource)) throw new IOException("CLI loaded-source removal deleted the imported image.");
                }
                checks.Add(new($"product-cli-{mode}-{sector}", "PASS", "Actual product commands and strict JSON, Windows stop veto, stale erase refusal, format/restart bytes, existing-image load and source retention verified."));
            }
            catch (Exception ex) { primary = ex; throw; }
            finally
            {
                Exception? cleanup = null;
                try
                {
                    // Independent broker access reconciles successful creation even if CLI output/parsing failed.
                    var owned = (await service.ListAsync()).Where(r => resource is { } id ? r.ResourceId == id :
                        !previousIds.Contains(r.ResourceId) && r.Definition.Label == label && r.Definition.Mode == mode &&
                        r.Definition.PreferredLetter == letter && r.Definition.ImagePath == (mode == ManagedDiskMode.EphemeralRam ? null : activeSource)).ToArray();
                    if (owned.Length > 1) throw new IOException("CLI fixture ownership is ambiguous; preserve records for recovery.");
                    foreach (var record in owned)
                    {
                        RequireOwned(record);
                        var current = record;
                        if (current.Runtime?.State != ManagedDiskState.Stopped)
                            current = (await service.ExecuteAsync(new(record.ResourceId, ManagedDiskAction.Stop, ManagedDiskExpected.From(record.Runtime!),
                                mode == ManagedDiskMode.CachedVhdx ? ManagedDiskStopIntent.DrainThenDetach : ManagedDiskStopIntent.DiscardThenStop,
                                AcceptDiscard: mode != ManagedDiskMode.CachedVhdx))).Record;
                        await service.ExecuteAsync(new(current.ResourceId, ManagedDiskAction.RemoveDefinition, ManagedDiskExpected.From(current.Runtime!)));
                    }
                    if (host.GetWriteCacheState().GlobalReservedBytes != baseline) throw new IOException("CLI fixture teardown changed the shared reservation.");
                }
                catch (Exception ex) { cleanup = ex; }
                trace.Add(new { Stage = "Cleanup", Resource = resource, Mode = mode, Sector = sector, FilesRetained = directory, PrimaryFailure = primary?.ToString(), CleanupFailure = cleanup?.ToString() });
                RunStorage.AtomicJson(job.Reply + ".cli.json", trace);
                if (cleanup is not null) throw new IOException("CLI fixture cleanup requires recovery; preserve its records/images.", primary is null ? cleanup : new AggregateException(primary, cleanup));
            }

            void RequireOwned(ManagedDiskRecord record)
                => ManagedCliEvidence.RequireOwned(record, fixture);
            void RequireReady(ManagedDiskRecord record)
            { RequireOwned(record); if (record.Runtime?.State != ManagedDiskState.Ready || record.VolumePath is null || record.PhysicalDiskNumber is null) throw new IOException("CLI fixture is not bound to a ready owned volume."); }
            void RequireBytes() { if (!File.ReadAllBytes(letter + @":\cli-sentinel.bin").SequenceEqual(bytes)) throw new IOException("CLI fixture byte oracle differs."); }
            string[] StopFlags() => mode switch { ManagedDiskMode.EphemeralRam => ["--discard"], ManagedDiskMode.ImageInRam => ["--save"], _ => [] };
            async Task<ManagedDiskRecord> StatusAsync()
            { var result = Read<ManagedDiskRecord>(await CommandAsync(["status", resource!.Value.ToString(), "--json"])); RequireOwned(result); return result; }
            async Task<ManagedDiskOperationResult> ActionAsync(string action, string[]? flags = null)
            {
                var result = Read<ManagedDiskOperationResult>(await CommandAsync([action, resource!.Value.ToString(), .. flags ?? [], "--json"]));
                if (result.Record.ResourceId != resource) throw new IOException("CLI action returned a different managed resource.");
                result.Record.Validate(); return result;
            }
        }
        return checks;

        async Task<string> CommandAsync(IReadOnlyList<string> args, int expectedExit = 0)
        {
            var prefix = job.Reply + $".cli-{++sequence:D4}";
            var command = job.ProductPrefix.Concat(["disk"]).Concat(args).ToArray();
            Console.WriteLine($"Product CLI {sequence}: disk {args[0]} (raw evidence: {prefix})");
            var result = await OwnedProcess.RunAsync(job.ProductExecutable, command, prefix, TimeSpan.FromSeconds(180), CancellationToken.None);
            trace.Add(new { Stage = "Command", Prefix = prefix, ExpectedExit = expectedExit, result.ExitCode });
            RunStorage.AtomicJson(job.Reply + ".cli.json", trace);
            if (result.ExitCode != expectedExit) throw new IOException($"Product CLI disk {args[0]} returned {result.ExitCode}, expected {expectedExit}; inspect {prefix}.stderr.txt.");
            return result.Output;
        }
    }
    private static T Read<T>(string output) => JsonSerializer.Deserialize<T>(output, ManagedDiskBrokerProtocol.Json) ?? throw new InvalidDataException("Missing product CLI JSON.");
    private static void Write(string path, byte[] bytes)
    { using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough); file.Write(bytes); file.Flush(true); }
}
