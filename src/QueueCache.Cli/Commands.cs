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
        var disks = new Command("disk", "Disk inventory and filter registration (separate from cache policy).");
        var diskList = new Command("list", "Show volumes, physical drive, GiB and boot/system status.");
        diskList.SetAction(async (_, token) =>
        {
            foreach (var disk in await DiskCatalog.ListAsync(token)) Console.WriteLine(disk.Display);
            return 0;
        });
        disks.Subcommands.Add(diskList);
        foreach (var attach in new[] { true, false })
        {
            var command = new Command(attach ? "attach" : "detach", "Select/remove this disk's filter. Reboot required; does not enable caching.");
            var drive = new Argument<string>("volume"); ValidateVolume(drive); command.Arguments.Add(drive);
            command.SetAction(async (p, token) =>
            {
                Console.WriteLine(await DriverRegistration.ChangeAsync(p.GetValue(drive)!, attach, token));
                return 0;
            });
            disks.Subcommands.Add(command);
        }
        root.Subcommands.Add(disks);
        var apply = new Command("apply", "Create or update a cache task. Fast finishes writes/application flushes in RAM; Strict waits for disk flushes.");
        var volume = new Argument<string>("volume") { Description = "NTFS volume, e.g. Q:" };
        ValidateVolume(volume);
        var budget = new Option<int>("--budget-mib") { DefaultValueFactory = _ => 4096 };
        var preset = new Option<CachePreset>("--preset") { DefaultValueFactory = _ => CachePreset.Fast };
        var accept = new Option<bool>("--accept-volatile-flush");
        var disabled = new Option<bool>("--disabled");
        var save = new Option<bool>("--save") { Description = "Save to administrator-only machine settings after successful Apply; installer startup task restores saved profiles." };
        apply.Arguments.Add(volume); apply.Options.Add(budget); apply.Options.Add(preset); apply.Options.Add(accept); apply.Options.Add(disabled);
        apply.Options.Add(save);
        apply.SetAction(async (p, token) =>
        {
            var configuration = new CacheConfiguration(p.GetValue(budget), p.GetValue(preset), !p.GetValue(disabled));
            configuration.Validate(true); // Validate before opening a disk. The selected preset defines semantics.
            var state = await CacheTasks.SaveAsync(p.GetValue(volume)!, configuration, p.GetValue(save), new ConsoleProgress(), token);
            Console.WriteLine(JsonSerializer.Serialize(state, JsonOptions));
            return 0;
        });
        policy.Subcommands.Add(apply);
        foreach (var name in new[] { "pause", "resume", "remove" })
        {
            var command = new Command(name, name == "remove" ? "Drain and free cache memory, then remove the saved task. Files are untouched." : "Pause/drain or resume a task, preserving its startup setting.");
            var drive = new Argument<string>("volume"); ValidateVolume(drive); command.Arguments.Add(drive);
            command.SetAction(async (p, token) =>
            {
                var selected = p.GetValue(drive)!;
                if (name == "remove") await CacheTasks.RemoveAsync(selected, token);
                else
                {
                    var target = await DiskTarget.InspectAsync(selected, token);
                    var persistent = SavedConfigurations.List().Any(profile => profile.Instance.Equals(target.Instance, StringComparison.OrdinalIgnoreCase));
                    await CacheTasks.SetEnabledAsync(selected, name == "resume", persistent, token);
                }
                Console.WriteLine($"Cache task {name} completed.");
                return 0;
            });
            policy.Subcommands.Add(command);
        }
        var profiles = new Command("profiles", "Show saved machine configurations without applying them.");
        profiles.SetAction(_ => { Console.WriteLine(JsonSerializer.Serialize(SavedConfigurations.List(), JsonOptions)); return 0; });
        policy.Subcommands.Add(profiles);
        var restore = new Command("restore", "Apply saved profiles only when volume, PnP identity and size still match. Used by the installer startup task.");
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
            command.Arguments.Add(drive); command.Options.Add(reportPath);
            if (benchmark) { command.Options.Add(size); command.Options.Add(passes); }
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
                if (output is not null) { JsonSerializer.Serialize(output, report, JsonOptions); output.Flush(true); }
                return report.Passed ? 0 : 1;
            });
            root.Subcommands.Add(command);
        }
        // Preserve existing installer and operator scripts while moving their presentation incrementally.
        Add("list", [], []);
        foreach (var name in new[] { "status", "cache-status" }) Add(name, ["device"], ["--json"]);
        foreach (var name in new[] { "watch", "diagnostics", "enable", "flush", "disable", "retry" }) Add(name, ["device"], []);
        foreach (var name in new[] { "configure", "start", "lab-delay", "lab-fault" }) Add(name, ["device", "value"], []);
        foreach (var name in new[] { "enable", "disable", "flush", "retry", "watch", "diagnostics" })
            Add(name, ["device"], [], policy);
        Add("status", ["device"], ["--json"], policy, "cache-status");
        Add("configure", ["device", "value"], [], policy);
        Add("set", ["device", "preset"], ["--accept-volatile-flush"], policy, "policy");
        var filter = new Command("lab-filter", "Internal guarded installer integration.");
        foreach (var action in new[] { "inspect", "add", "remove" })
        {
            var command = new Command(action);
            var instance = new Argument<string>("instance"); command.Arguments.Add(instance);
            var service = new Argument<string>("service");
            var marker = new Option<bool>("--lab-installer");
            if (action != "inspect") { command.Arguments.Add(service); command.Options.Add(marker); }
            command.SetAction(p => compatibility(action == "inspect"
                ? ["lab-filter", action, p.GetValue(instance)!]
                : p.GetValue(marker) ? ["lab-filter", action, p.GetValue(instance)!, p.GetValue(service)!, "--lab-installer"]
                : ["lab-filter", action, p.GetValue(instance)!, p.GetValue(service)!]));
            filter.Subcommands.Add(command);
        }
        root.Subcommands.Add(filter);
        return root;

        void Add(string name, string[] argumentNames, string[] optionNames, Command? parent = null, string? legacyName = null)
        {
            var command = new Command(name, name.StartsWith("lab-") ? "Advanced lab-only diagnostic hook. Not part of qcache test." : $"Cache {name}.");
            var arguments = argumentNames.Select(n => new Argument<string>(n)).ToArray();
            var options = optionNames.Select(n => new Option<bool>(n)).ToArray();
            foreach (var a in arguments) command.Arguments.Add(a);
            foreach (var o in options) command.Options.Add(o);
            command.SetAction(p => compatibility(new[] { legacyName ?? name }.Concat(arguments.Select(a => p.GetValue(a)!))
                .Concat(options.Where(o => p.GetValue(o)).Select(o => o.Name)).ToArray()));
            (parent ?? root).Subcommands.Add(command);
        }
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static void ValidateVolume(Argument<string> argument) => argument.Validators.Add(result =>
    {
        var value = result.GetValueOrDefault<string>();
        if (value is null || value.Length != 2 || !char.IsAsciiLetter(value[0]) || value[1] != ':')
            result.AddError("Expected an explicit volume such as Q:.");
    });
    private sealed class ConsoleProgress : IProgress<string> { public void Report(string value) => Console.Error.WriteLine(value); }
}
