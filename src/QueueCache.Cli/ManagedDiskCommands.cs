using System.CommandLine;
using System.CommandLine.Parsing;
using System.Runtime.Versioning;
using System.Text.Json;
using QueueCache.Management;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Cli;

/// <summary>Product argument binding only; the service owns storage transactions and recovery.</summary>
[SupportedOSPlatform("windows")]
internal static class ManagedDiskCommands
{
    private static readonly JsonSerializerOptions json = new(ManagedDiskBrokerProtocol.Json) { WriteIndented = true };
    public static void AddTo(Command parent)
    {
        var service = new WindowsManagedDiskService();
        var list = new Command("list", "List remembered managed disks, live generations, checkpoint/unsaved state and errors.");
        var listJson = JsonOption(list);
        list.SetAction(async (p, token) => { Print(await service.ListAsync(token), p.GetValue(listJson)); return 0; });
        parent.Subcommands.Add(list);
        var status = new Command("status", "Show one managed resource without flushing or copying images.");
        var statusId = ResourceArgument(status); var statusJson = JsonOption(status);
        status.SetAction(async (p, token) => { Print(await FindAsync(service, p.GetValue(statusId), token), p.GetValue(statusJson)); return 0; });
        parent.Subcommands.Add(status);
        var capabilities = new Command("capabilities", "Show which managed modes the installed broker/provider can start.");
        capabilities.SetAction(async (_, token) => { Console.WriteLine(JsonSerializer.Serialize(await service.CapabilitiesAsync(token), json)); return 0; });
        parent.Subcommands.Add(capabilities);
        var inspect = new Command("inspect", "Read a detached local VHDX's stable identity, virtual capacity and sector size.");
        var inspectPath = new Argument<string>("image"); inspect.Arguments.Add(inspectPath);
        inspect.SetAction(async (p, token) =>
        { ManagedDiskPaths.ValidateImagePath(p.GetValue(inspectPath)); Console.WriteLine(JsonSerializer.Serialize(await service.InspectAsync(p.GetValue(inspectPath)!, token), json)); return 0; });
        parent.Subcommands.Add(inspect);

        var create = new Command("create", "Create a pure RAM disk, a VHDX with independent cache, or a complete VHDX image in RAM. New disks are GPT/NTFS; loading preserves existing contents.");
        var mode = new Option<string>("--mode") { Required = true, Description = "ram, cached-vhdx, or image-in-ram." };
        mode.Validators.Add(r => { if (r.GetValueOrDefault<string>() is not ("ram" or "cached-vhdx" or "image-in-ram")) r.AddError("--mode must be ram, cached-vhdx, or image-in-ram."); });
        var size = new Option<ulong?>("--size-mib") { Description = "New disk capacity (at least 16 MiB). Existing images use their inspected virtual capacity." };
        var letter = new Option<string>("--letter") { DefaultValueFactory = _ => "R", Description = "An unused drive letter D through Z." };
        letter.Validators.Add(r => { if (!ValidLetter(r.GetValueOrDefault<string>())) r.AddError("--letter must be a single drive letter D through Z."); });
        var label = new Option<string>("--label") { DefaultValueFactory = _ => "QueueCache" };
        var newImage = new Option<string>("--new-image") { Description = "Create a NEW standalone .vhdx; existing files are never overwritten." };
        var load = new Option<string>("--load") { Description = "Open an existing detached standalone .vhdx; do not format it." };
        var checkpoints = new Option<string>("--checkpoint-directory") { Description = "Image-in-RAM: local NTFS directory for unique verified checkpoints." };
        var fixedImage = new Option<bool>("--fixed-image") { Description = "Create a fixed rather than dynamic VHDX." };
        var sector = new Option<uint>("--sector-bytes") { DefaultValueFactory = _ => 512 };
        var readOnly = new Option<bool>("--read-only") { Description = "Publish the loaded RAM device read-only; the input image is always preserved." };
        var startup = new Option<bool>("--startup") { Description = "Remember automatic Windows startup using this mode's recipe." };
        var discardOnStop = new Option<bool>("--discard-on-stop") { Description = "Image-in-RAM: disable default save-before-stop. Each actual discard still requires --discard." };
        var shutdownSave = new Option<bool>("--save-on-shutdown") { Description = "Image-in-RAM: attempt a bounded checkpoint during orderly shutdown; completion is not guaranteed." };
        foreach (Option option in new Option[] { mode, size, letter, label, newImage, load, checkpoints, fixedImage, sector, readOnly, startup, discardOnStop, shutdownSave }) create.Options.Add(option);
        var createCache = new CacheBinding(create);
        var createJson = JsonOption(create);
        create.SetAction(async (p, token) =>
        {
            var selected = p.GetValue(mode) switch { "ram" => ManagedDiskMode.EphemeralRam, "cached-vhdx" => ManagedDiskMode.CachedVhdx, _ => ManagedDiskMode.ImageInRam };
            var destination = p.GetValue(newImage); var source = p.GetValue(load);
            if (destination is not null && source is not null) throw new ArgumentException("Choose either --new-image or --load.");
            if (selected != ManagedDiskMode.EphemeralRam && destination is null && source is null) throw new ArgumentException("An image mode requires --new-image or --load.");
            var requested = p.GetValue(size);
            if (requested is < 16 || requested > (ulong)long.MaxValue / ManagedDiskDefinition.MiB) throw new ArgumentException("Invalid --size-mib capacity.");
            if (source is null && requested is null) throw new ArgumentException("A new disk requires --size-mib.");
            if (source is not null) ManagedDiskPaths.ValidateImagePath(source);
            if (destination is not null) ManagedDiskPaths.ValidateImagePath(destination);
            if (selected != ManagedDiskMode.CachedVhdx && createCache.WasSpecified(p)) throw new ArgumentException("Cache options apply only to --mode cached-vhdx.");
            if (selected != ManagedDiskMode.ImageInRam && (p.GetValue(discardOnStop) || p.GetValue(shutdownSave))) throw new ArgumentException("Image-save policies apply only to --mode image-in-ram.");
            if (p.GetValue(fixedImage) && (selected == ManagedDiskMode.EphemeralRam || source is not null)) throw new ArgumentException("--fixed-image applies only when creating a new VHDX.");
            // Validate the whole recipe before even read-only image inspection or broker connection.
            var definition = new ManagedDiskDefinition(Guid.NewGuid(), selected,
                source is null ? ManagedDiskSource.CreateNew : ManagedDiskSource.OpenExisting,
                checked((requested ?? 16) * ManagedDiskDefinition.MiB), char.ToUpperInvariant(p.GetValue(letter)![0]), p.GetValue(label)!,
                source ?? destination, p.GetValue(checkpoints), selected == ManagedDiskMode.CachedVhdx ? createCache.Read(p) : null,
                selected == ManagedDiskMode.CachedVhdx && p.GetValue(createCache.Accept), p.GetValue(startup),
                selected == ManagedDiskMode.ImageInRam && !p.GetValue(discardOnStop) && !p.GetValue(readOnly), p.GetValue(shutdownSave),
                p.GetValue(readOnly), p.GetValue(fixedImage) ? ImageAllocation.Fixed : ImageAllocation.Dynamic, p.GetValue(sector));
            definition.Validate();
            if (source is not null)
            {
                var image = await service.InspectAsync(source, token);
                if (requested is not null && definition.CapacityBytes != image.VirtualBytes) throw new ArgumentException("--size-mib must equal the existing image's virtual capacity.");
                if (p.GetResult(sector) is { Implicit: false } && image.SectorBytes != definition.SectorBytes) throw new ArgumentException("--sector-bytes does not match the existing image.");
                definition = definition with { CapacityBytes = image.VirtualBytes, SectorBytes = image.SectorBytes };
                definition.Validate(); image.ValidateFor(definition);
            }
            var runtime = await service.CreateAsync(definition, new ConsoleProgress(), token);
            Print(await FindAsync(service, runtime.ResourceId, token), p.GetValue(createJson)); return 0;
        });
        parent.Subcommands.Add(create);

        foreach (var pair in new[] { ("start", ManagedDiskAction.Start), ("stop", ManagedDiskAction.Stop), ("flush", ManagedDiskAction.Flush),
            ("save", ManagedDiskAction.Save), ("export", ManagedDiskAction.Export), ("format", ManagedDiskAction.Format),
            ("cache", ManagedDiskAction.ChangeCache), ("startup", ManagedDiskAction.SetStartup), ("remove", ManagedDiskAction.RemoveDefinition),
            ("delete-image", ManagedDiskAction.DeleteImage), ("recover", ManagedDiskAction.Recover) })
        {
            var action = pair.Item2;
            var command = new Command(pair.Item1, Describe(action)); var resource = ResourceArgument(command); var outputJson = JsonOption(command);
            var discard = new Option<bool>("--discard") { Description = "Explicitly acknowledge loss of this RAM creation's contents/unsaved generation. Images are retained." };
            var saveStop = new Option<bool>("--save") { Description = "Verify/commit a full checkpoint before stopping the image-in-RAM disk." };
            var erase = new Option<bool>("--accept-erase") { Description = "Explicitly acknowledge erasing the selected owned volume/image." };
            var path = new Option<string>("--path") { Required = action is ManagedDiskAction.Export or ManagedDiskAction.DeleteImage };
            var commit = new Option<bool>("--commit") { Description = "Use this verified export as the future startup source." };
            var formatLabel = new Option<string>("--label");
            var enabled = ExplicitBool("--enabled"); var saveBefore = ExplicitBool("--save-before-stop"); var saveShutdown = ExplicitBool("--save-on-shutdown");
            var expectedBoot = new Option<Guid?>("--expected-boot"); var expectedCreation = new Option<ulong?>("--expected-creation"); var expectedWrite = new Option<ulong?>("--expected-write");
            foreach (var option in new Option[] { expectedBoot, expectedCreation, expectedWrite }) command.Options.Add(option);
            if (action == ManagedDiskAction.Stop) { command.Options.Add(discard); command.Options.Add(saveStop); }
            if (action is ManagedDiskAction.Format or ManagedDiskAction.DeleteImage) command.Options.Add(erase);
            if (action is ManagedDiskAction.Export or ManagedDiskAction.DeleteImage) command.Options.Add(path);
            if (action == ManagedDiskAction.Export) command.Options.Add(commit);
            if (action == ManagedDiskAction.Format) command.Options.Add(formatLabel);
            if (action == ManagedDiskAction.SetStartup) { command.Options.Add(enabled); command.Options.Add(saveBefore); command.Options.Add(saveShutdown); }
            CacheBinding? cache = action == ManagedDiskAction.ChangeCache ? new(command) : null;
            command.SetAction(async (p, token) =>
            {
                var wantsDiscard = action == ManagedDiskAction.Stop && p.GetValue(discard);
                var wantsSave = action == ManagedDiskAction.Stop && p.GetValue(saveStop);
                if (wantsDiscard && wantsSave) throw new ArgumentException("--save and --discard cannot be combined.");
                if (action is ManagedDiskAction.Format or ManagedDiskAction.DeleteImage && !p.GetValue(erase)) throw new ArgumentException("This action requires --accept-erase.");
                var selectedPath = action is ManagedDiskAction.Export or ManagedDiskAction.DeleteImage ? p.GetValue(path) : null;
                if (selectedPath is not null) ManagedDiskPaths.ValidateImagePath(selectedPath);
                var policy = cache?.Read(p); policy?.Validate(p.GetValue(cache!.Accept));
                var boot = p.GetValue(expectedBoot); var creation = p.GetValue(expectedCreation); var write = p.GetValue(expectedWrite);
                if ((boot is not null || creation is not null || write is not null) && (boot is null || creation is null || write is null))
                    throw new ArgumentException("Supply --expected-boot, --expected-creation and --expected-write together.");
                if (action == ManagedDiskAction.SetStartup && p.GetValue(enabled) is null && p.GetValue(saveBefore) is null && p.GetValue(saveShutdown) is null)
                    throw new ArgumentException("Choose at least one startup/save policy setting.");
                var record = await FindAsync(service, p.GetValue(resource), token);
                var expected = action == ManagedDiskAction.Recover ? null : boot is not null
                    ? new ManagedDiskExpected(record.ResourceId, boot.Value, creation!.Value, write!.Value)
                    : ManagedDiskExpected.From(record.Runtime ?? throw new IOException("No runtime identity is recorded. Run disk recover first."));
                var request = new ManagedDiskRequest(record.ResourceId, action, expected,
                    wantsDiscard ? ManagedDiskStopIntent.DiscardThenStop : wantsSave ? ManagedDiskStopIntent.SaveThenStop : null,
                    selectedPath, action == ManagedDiskAction.Export && p.GetValue(commit), wantsDiscard,
                    action is ManagedDiskAction.Format or ManagedDiskAction.DeleteImage && p.GetValue(erase), policy,
                    cache is not null && p.GetValue(cache.Accept), action == ManagedDiskAction.SetStartup ? p.GetValue(enabled) : null,
                    action == ManagedDiskAction.SetStartup ? p.GetValue(saveBefore) : null, action == ManagedDiskAction.SetStartup ? p.GetValue(saveShutdown) : null,
                    action == ManagedDiskAction.Format ? p.GetValue(formatLabel) : null);
                var result = await service.ExecuteAsync(request, new ConsoleProgress(), token);
                if (p.GetValue(outputJson)) Console.WriteLine(JsonSerializer.Serialize(result, json));
                else { Console.WriteLine(result.Message); Print(result.Record, false); }
                return 0;
            });
            parent.Subcommands.Add(command);
        }
    }
    private static Option<bool?> ExplicitBool(string name) => new(name) { Arity = ArgumentArity.ExactlyOne };
    private static bool ValidLetter(string? value) => value?.Length == 1 && char.ToUpperInvariant(value[0]) is >= 'D' and <= 'Z';
    private static Argument<Guid> ResourceArgument(Command command)
    {
        var argument = new Argument<Guid>("resource") { Description = "Stable managed resource GUID from disk list (never a drive letter or physical disk number)." };
        argument.Validators.Add(r => { if (r.Tokens.Count != 1 || !Guid.TryParse(r.Tokens[0].Value, out var id) || id == Guid.Empty) r.AddError("A nonempty managed resource GUID is required."); });
        command.Arguments.Add(argument); return argument;
    }
    private static Option<bool> JsonOption(Command command) { var option = new Option<bool>("--json"); command.Options.Add(option); return option; }
    private static async Task<ManagedDiskRecord> FindAsync(IManagedDiskService service, Guid resource, CancellationToken token) =>
        (await service.ListAsync(token)).SingleOrDefault(r => r.ResourceId == resource) ?? throw new IOException("The selected managed resource does not exist.");
    private static void Print(object value, bool asJson)
    {
        if (asJson) { Console.WriteLine(JsonSerializer.Serialize(value, json)); return; }
        foreach (var record in value is ManagedDiskRecord item ? new[] { item } : (IReadOnlyList<ManagedDiskRecord>)value)
        {
            var runtime = record.Runtime;
            Console.WriteLine($"{record.ResourceId} | {record.Definition.Mode} | {record.Definition.PreferredLetter}: | {record.Definition.CapacityBytes / ManagedDiskDefinition.MiB} MiB | {runtime?.State.ToString() ?? "No runtime identity"} | startup={record.Definition.StartAtBoot}");
            if (runtime is not null) Console.WriteLine($"  boot={runtime.BootEpoch} creation={runtime.CreationGeneration} write={runtime.WriteGeneration} saved={runtime.SavedGeneration?.ToString() ?? "unavailable"}{(runtime.HasUnsavedChanges ? " | Unsaved RAM changes" : "")}");
            if (record.CommittedImage is not null) Console.WriteLine($"  startup image: {record.CommittedImage.Identity.Path}; saved at {record.SavedAt?.ToString("O") ?? "not yet checkpointed"}");
            if (record.LastError is not null) Console.WriteLine("  " + record.LastError);
        }
    }
    private static string Describe(ManagedDiskAction action) => action switch
    {
        ManagedDiskAction.Stop => "Lock/quiesce and stop this owned disk. Backed images drain/detach; RAM discard is explicit; image-in-RAM defaults to its save-before-stop policy.",
        ManagedDiskAction.Flush => "Flush RAM/device writes. Only backed VHDX flush is persistence; this never saves a whole RAM image.",
        ManagedDiskAction.Save => "Freeze, copy and verify all logical sectors, then commit a unique checkpoint; retain source and previous image.",
        ManagedDiskAction.Export => "Export a verified full frozen generation to a NEW .vhdx. Startup image changes only with --commit.",
        ManagedDiskAction.Format => "Erase and format the exact owned volume as NTFS; requires --accept-erase. For image-in-RAM the source remains unchanged until Save.",
        ManagedDiskAction.RemoveDefinition => "Forget a stopped resource's definition. Image files are retained.",
        ManagedDiskAction.DeleteImage => "Delete an unreferenced owned image with --path and --accept-erase. Imported/referenced/mounted images are refused.",
        ManagedDiskAction.Recover => "Reconcile exact native ownership and the durable journal. Keep surviving RAM and prior committed image; never reformat/reload live RAM.",
        ManagedDiskAction.ChangeCache => "Apply and remember a backed VHDX's independent cache using the shared cache policy transaction.",
        ManagedDiskAction.SetStartup => "Remember automatic startup and image-in-RAM stop/shutdown save policies. Boolean options require true or false.",
        _ => "Start a stopped remembered disk using its stored recipe and strict image identity."
    };
    private sealed class ConsoleProgress : IProgress<ManagedDiskProgress>
    {
        public void Report(ManagedDiskProgress value) => Console.Error.WriteLine($"{value.Stage}: {value.Message}" +
            (value.CompletedBytes is not null ? $" ({value.CompletedBytes}/{value.TotalBytes} bytes)" : ""));
    }
    private sealed class CacheBinding
    {
        public readonly Option<int> Budget = new("--budget-mib") { DefaultValueFactory = _ => 256 };
        public readonly Option<CachePreset> Preset = new("--preset") { DefaultValueFactory = _ => CachePreset.Strict };
        public readonly Option<bool> Accept = new("--accept-volatile-flush"), Disabled = new("--disabled"), Discard = new("--discard-drained"), NoPromotion = new("--no-promotion");
        public readonly Option<CacheAllocation> Allocation = new("--allocation") { DefaultValueFactory = _ => CacheAllocation.Automatic };
        public readonly Option<int> WritePercent = new("--write-percent") { DefaultValueFactory = _ => 50 };
        public readonly Option<DrainAlgorithm> Drain = new("--drain") { DefaultValueFactory = _ => DrainAlgorithm.Idle };
        public readonly Option<int> Low = new("--low-percent") { DefaultValueFactory = _ => 40 }, High = new("--high-percent") { DefaultValueFactory = _ => 80 },
            Age = new("--max-dirty-age-ms") { DefaultValueFactory = _ => 5000 }, Idle = new("--idle-ms") { DefaultValueFactory = _ => 250 },
            Batch = new("--batch-kib") { DefaultValueFactory = _ => 256 }, Parallel = new("--drain-parallelism") { DefaultValueFactory = _ => 2 };
        private readonly Option[] options;
        public CacheBinding(Command command)
        {
            options = [Budget, Preset, Accept, Disabled, Allocation, WritePercent, Drain, Discard, NoPromotion, Low, High, Age, Idle, Batch, Parallel];
            foreach (var option in options) command.Options.Add(option);
        }
        public bool WasSpecified(ParseResult p) => options.Any(o => p.GetResult(o) is { Implicit: false });
        public CacheConfiguration Read(ParseResult p) => new(p.GetValue(Budget), p.GetValue(Preset), !p.GetValue(Disabled))
        { Options = new(p.GetValue(Allocation), p.GetValue(WritePercent), !p.GetValue(Discard), !p.GetValue(NoPromotion), p.GetValue(Drain), p.GetValue(Low), p.GetValue(High), p.GetValue(Age), p.GetValue(Idle), p.GetValue(Batch), p.GetValue(Parallel)) };
    }
}
