using System.CommandLine;
using System.Runtime.Versioning;
using System.Text.Json;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Cli;

[SupportedOSPlatform("windows")]
internal static class Commands
{
    public static RootCommand Create(Func<string[], Task<int>> compatibility)
    {
        var root = new RootCommand("QueueCache disk cache. Fast mode is volatile; this build uses a test-signed driver.");
        var policy = new Command("policy", "Cache configuration, state and persistence.");
        root.Subcommands.Add(policy);
        var volumes = new Command("volume", "Volumes QueueCache can cache (one cache per volume), and the filter registration.");
        var volumeList = new Command("list", "Show each lettered volume, its disk, whether the filter is loaded, and its cache task.");
        volumeList.SetAction(async (_, token) =>
        {
            var problems = DriverRegistration.Inspect().Problems();
            Console.WriteLine(problems.Count == 0 ? "Filter registration: every volume (topmost volume filter)." : "Filter registration: " + string.Join(" ", problems));
            var saved = SavedConfigurations.List();
            foreach (var item in await VolumeCatalog.ListAsync(token))
                Console.WriteLine($"{item.Display} | {VolumeStatus(item, saved)}");
            return problems.Count == 0 ? 0 : 1;
        });
        volumes.Subcommands.Add(volumeList);
        root.Subcommands.Add(volumes);
        var disks = new Command("disk", "Managed RAM/VHDX disks and physical Windows disk discovery/ejection.");
        var diskList = new Command("physical-list", "Show each physical disk with its lettered volumes, GiB and Windows status.");
        diskList.SetAction(async (_, token) =>
        {
            foreach (var disk in await DiskCatalog.ListAsync(token))
                Console.WriteLine(disk.Display);
            return 0;
        });
        disks.Subcommands.Add(diskList);
        var eject = new Command("eject", "Safely eject the entire physical disk containing this volume. Every volume on the disk is flushed first.");
        var ejectVolume = new Argument<string>("volume") { Description = "A lettered volume on the disk to eject, e.g. R:." };
        ValidateVolume(ejectVolume);
        var previewEject = new Option<bool>("--preview") { Description = "Show the disk, affected volumes and Windows eject capability without changing anything." };
        eject.Arguments.Add(ejectVolume);
        eject.Options.Add(previewEject);
        eject.SetAction(async (p, token) =>
        {
            var selected = p.GetValue(ejectVolume)!;
            if (p.GetValue(previewEject))
            {
                var found = await DiskEjection.PreviewAsync(selected, token);
                Console.WriteLine(JsonSerializer.Serialize(found, JsonOptions));
                return found.Ejectable ? 0 : 1;
            }
            var result = await DiskEjection.EjectAsync(selected, new ConsoleProgress(), token);
            Console.WriteLine(result.RemovalObserved
                ? $"Windows removed disk {result.Disk.DiskNumber} ({string.Join(", ", result.Disk.Volumes)})."
                : $"Windows accepted eject of disk {result.Disk.DiskNumber}, but removal was not observed within five seconds. Check device state before disconnecting it; affected caches remain disabled.");
            return result.RemovalObserved ? 0 : 1;
        });
        disks.Subcommands.Add(eject);
        ManagedDiskCommands.AddTo(disks);
        root.Subcommands.Add(disks);
        var apply = new Command("apply", "Create or update a cache task. Fast finishes writes/application flushes in RAM; Strict waits for disk flushes.");
        var volume = new Argument<string>("volume") { Description = "Lettered volume with a file system (NTFS, ReFS, FAT32, exFAT), e.g. Q:. Each volume has its own cache, also when several share one disk." };
        ValidateVolume(volume);
        var budget = new Option<int>("--budget-mib") { DefaultValueFactory = _ => 4096 };
        var preset = new Option<CachePreset>("--preset") { DefaultValueFactory = _ => CachePreset.Fast };
        var accept = new Option<bool>("--accept-volatile-flush");
        var disabled = new Option<bool>("--disabled");
        var save = new Option<bool>("--save") { Description = "Save to administrator-only machine settings after successful Apply; installer startup task restores saved profiles." };
        apply.Arguments.Add(volume);
        apply.Options.Add(budget);
        apply.Options.Add(preset);
        apply.Options.Add(accept);
        apply.Options.Add(disabled);
        apply.Options.Add(save);
        var runtimeOnly = new Option<bool>("--runtime-only") { Description = "Change this boot only; leave the saved startup profile untouched. Cannot combine with --save." };
        apply.Options.Add(runtimeOnly);
        var allocation = new Option<CacheAllocation>("--allocation") { DefaultValueFactory = _ => CacheAllocation.Automatic };
        var writePercent = new Option<int>("--write-percent") { DefaultValueFactory = _ => 50, Description = "Fixed allocation: 0 = read-only, 100 = write-only." };
        // Drain scheduling. Eager ignores the watermark/age/idle settings; Balanced adds watermarks and
        // maximum age; Idle adds the write-idle interval. Batch size and parallelism apply to all three.
        var drain = new Option<DrainAlgorithm>("--drain") { DefaultValueFactory = _ => DrainAlgorithm.Idle, Description = "Eager: drain as writes arrive. Balanced: watermarks/age. Idle (default): Balanced plus write-idle. Deferred: age only, no early watermark/idle trigger; explicit flush/capacity boundaries still apply." };
        var discard = new Option<bool>("--discard-drained") { Description = "Release written blocks after draining instead of retaining them for reads." };
        var noPromotion = new Option<bool>("--no-promotion") { Description = "Keep retained writes in the write quota when read." };
        var low = new Option<int>("--low-percent") { DefaultValueFactory = _ => 40, Description = "Balanced/Idle: stop pressure draining at this share of the write pool." };
        var high = new Option<int>("--high-percent") { DefaultValueFactory = _ => 80, Description = "Balanced/Idle: start pressure draining at this share of the write pool." };
        var age = new Option<int>("--max-dirty-age-ms") { DefaultValueFactory = _ => 5000, Description = "Balanced/Idle/Deferred: first-dirty age trigger, 10..3600000 ms (one hour). Not a durability deadline. Deferred ignores watermarks and idle." };
        var idle = new Option<int>("--idle-ms") { DefaultValueFactory = _ => 250, Description = "Idle only: drain after this long without a newly cached write." };
        var batch = new Option<int>("--batch-kib") { DefaultValueFactory = _ => 256, Description = "All algorithms: maximum adjacent-write gather size per lower write." };
        var parallel = new Option<int>("--drain-parallelism") { DefaultValueFactory = _ => 2, Description = "All algorithms: maximum simultaneous lower writes." };
        foreach (Option option in new Option[] { allocation, writePercent, drain, discard, noPromotion, low, high, age, idle, batch, parallel })
            apply.Options.Add(option);
        apply.SetAction(async (p, token) =>
        {
            var configuration = new CacheConfiguration(p.GetValue(budget), p.GetValue(preset), !p.GetValue(disabled))
            {
                Options = new(p.GetValue(allocation), p.GetValue(writePercent), !p.GetValue(discard), !p.GetValue(noPromotion),
                    p.GetValue(drain), p.GetValue(low), p.GetValue(high), p.GetValue(age), p.GetValue(idle), p.GetValue(batch), p.GetValue(parallel))
            };
            var accepted = p.GetValue(accept);
            configuration.Validate(accepted); // Validate before opening a disk. Fast requires explicit CLI acknowledgement.
            if (p.GetValue(save) && p.GetValue(runtimeOnly))
                throw new ArgumentException("--save and --runtime-only cannot be combined.");
            var selectedVolume = p.GetValue(volume)!;
            try
            {
                var fileSystem = new DriveInfo(selectedVolume + "\\").DriveFormat;
                if (FileSystems.IsMounted(fileSystem) && !FileSystems.IsJournaled(fileSystem) && configuration.Preset == CachePreset.Fast)
                    Console.Error.WriteLine($"Warning: {selectedVolume} ({fileSystem}) {FileSystems.NoJournalWarning}");
            }
            catch (IOException) { } // Unformatted: Apply below reports it.
            var state = await CacheTasks.SaveAsync(selectedVolume, configuration, p.GetValue(save), accepted,
                new ConsoleProgress(), token, p.GetValue(runtimeOnly));
            Console.WriteLine(JsonSerializer.Serialize(state, JsonOptions));
            return 0;
        });
        policy.Subcommands.Add(apply);
        foreach (var name in new[] { "pause", "resume", "remove" })
        {
            var command = new Command(name, name == "remove" ? "Drain and free cache memory, then remove the saved task. Files are untouched." : "Pause/drain or resume a task, preserving its startup setting.");
            var drive = new Argument<string>("volume");
            ValidateVolume(drive);
            command.Arguments.Add(drive);
            var transient = new Option<bool>("--runtime-only") { Description = "Leave the saved startup profile untouched." };
            command.Options.Add(transient);
            command.SetAction(async (p, token) =>
            {
                var selected = p.GetValue(drive)!;
                if (name == "remove")
                    await CacheTasks.RemoveAsync(selected, token, p.GetValue(transient));
                else
                {
                    var target = await DiskTarget.InspectAsync(selected, token);
                    var persistent = SavedConfigurations.IsSaved(target.VolumeId);
                    await CacheTasks.SetEnabledAsync(selected, name == "resume", persistent, token, p.GetValue(transient));
                }
                Console.WriteLine($"Cache task {name} completed.");
                return 0;
            });
            policy.Subcommands.Add(command);
        }
        var profiles = new Command("profiles", "Show saved machine configurations without applying them.");
        profiles.SetAction(_ => { Console.WriteLine(JsonSerializer.Serialize(SavedConfigurations.List(), JsonOptions)); return 0; });
        policy.Subcommands.Add(profiles);
        var restore = new Command("restore", "Apply saved profiles only when the volume (GUID), its disk's PnP identity and its size still match. Used by the installer startup task.");
        restore.SetAction(async (_, token) =>
        {
            var results = await SavedConfigurations.RestoreAsync(new ConsoleProgress(), token);
            Console.WriteLine(JsonSerializer.Serialize(results, JsonOptions));
            return results.All(r => r.Applied) ? 0 : 1;
        });
        policy.Subcommands.Add(restore);
        foreach (var benchmark in new[] { false, true })
        {
            var command = new Command(benchmark ? "benchmark" : "test", "File-only current-boot workload. No reboot, format, fault injection or policy changes. Files are retained.");
            var drive = new Argument<string>("volume");
            ValidateVolume(drive);
            var size = new Option<int>("--size-mib") { DefaultValueFactory = _ => 256 };
            var passes = new Option<int>("--passes") { DefaultValueFactory = _ => 4 };
            var reportPath = new Option<string>("--report") { Description = "Create a NEW JSON report (existing files are never overwritten)." };
            command.Arguments.Add(drive);
            command.Options.Add(reportPath);
            if (benchmark)
            {
                command.Options.Add(size);
                command.Options.Add(passes);
            }
            command.SetAction(async (p, token) =>
            {
                // Reserve output first so an invalid/existing report path fails before disk activity.
                using var output = p.GetValue(reportPath) is { } path ? new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read) : null;
                var target = await DiskTarget.InspectAsync(p.GetValue(drive)!, token);
                var progress = new ConsoleProgress();
                var report = benchmark
                    ? await DiskWorkloads.BenchmarkAsync(target, p.GetValue(size), p.GetValue(passes), progress, token)
                    : await DiskWorkloads.TestAsync(target, progress, token);
                Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
                if (output is not null)
                {
                    JsonSerializer.Serialize(output, report, JsonOptions);
                    output.Flush(true);
                }
                return report.Passed ? 0 : 1;
            });
            root.Subcommands.Add(command);
        }
        // Preserve existing installer and operator scripts while moving their presentation incrementally.
        Add("list", [], []);
        foreach (var name in new[] { "status", "cache-status" })
            Add(name, ["device"], ["--json"]);
        foreach (var name in new[] { "watch", "diagnostics", "enable", "flush", "disable", "retry", "drop-clean" })
            Add(name, ["device"], []);
        foreach (var name in new[] { "configure", "start", "lab-delay", "lab-fault", "lab-copy-flags", "lab-read-recall" })
            Add(name, ["device", "value"], []);
        foreach (var name in new[] { "enable", "disable", "flush", "retry", "watch", "diagnostics", "drop-clean" })
            Add(name, ["device"], [], policy);
        Add("status", ["device"], ["--json"], policy, "cache-status");
        Add("configure", ["device", "value"], [], policy);
        Add("set", ["device", "preset"], ["--accept-volatile-flush"], policy, "policy");
        root.Subcommands.Add(DeveloperCommands.Create(compatibility));
        return root;

        void Add(string name, string[] argumentNames, string[] optionNames, Command? parent = null, string? legacyName = null)
        {
            var command = new Command(name, name.StartsWith("lab-") ? "Advanced lab-only diagnostic hook. Not part of qcache test." : $"Cache {name}.");
            var arguments = argumentNames.Select(n => new Argument<string>(n)).ToArray();
            var options = optionNames.Select(n => new Option<bool>(n)).ToArray();
            foreach (var a in arguments)
                command.Arguments.Add(a);
            foreach (var o in options)
                command.Options.Add(o);
            command.SetAction(p => compatibility(new[] { legacyName ?? name }.Concat(arguments.Select(a => p.GetValue(a)!))
                .Concat(options.Where(o => p.GetValue(o)).Select(o => o.Name)).ToArray()));
            (parent ?? root).Subcommands.Add(command);
        }
    }
    private static string VolumeStatus(VolumeDescription volume, IReadOnlyList<SavedConfiguration> saved)
    {
        var profile = saved.Any(p => p.Matches(volume.VolumeId)) ? " | saved for startup" : "";
        try
        {
            using var device = new CacheDevice(volume.Volume);
            var state = device.GetWriteCacheState();
            return state.BudgetBytes == 0 ? "no cache" + profile
                : $"{state.BudgetBytes >> 20} MiB {(state.UnsafeDefer ? "Fast" : "Strict")} cache, {state.RuntimeStatus}{profile}";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 1 or 50)
        {
            return "filter not loaded (restart Windows after installing)" + profile;
        }
        catch (Exception ex) { return "state unavailable: " + ex.Message + profile; }
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static void ValidateVolume(Argument<string> argument) => argument.Validators.Add(result =>
    {
        var value = result.GetValueOrDefault<string>();
        if (value is null || value.Length != 2 || !char.IsAsciiLetter(value[0]) || value[1] != ':')
            result.AddError("Expected an explicit volume such as Q:.");
    });
    private sealed class ConsoleProgress : IProgress<string>
    {
        public void Report(string value) => Console.Error.WriteLine(value);
    }
}
