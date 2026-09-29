using System.CommandLine;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using QueueCache.Management;

namespace QueueCache.Cli;

[SupportedOSPlatform("windows")]
internal static class DeveloperCommands
{
    public static Command Create(Func<string[], Task<int>> compatibility)
    {
        var root = new Command("developer", "Advanced integration tests and driver hooks. Ordinary file-only checks: qcache test.");
        root.Subcommands.Add(VerificationCommands.Create());
        root.Subcommands.Add(VerificationCommands.CreateRecovery());
        root.Subcommands.Add(VerificationCommands.CreateStatus());
        var performance = new Command("performance", "Read queue/phase and lifetime performance counters as JSON. Detailed timing is opt-in.");
        var perfDevice = new Argument<string>("device");
        var timing = new Option<bool?>("--timing") { Description = "Enable/disable detailed driver timing; omitted leaves it unchanged." };
        var callerPath = new Option<bool?>("--caller-path") { Description = "Serve RAM hits and fitting writes on the calling thread when the disk is otherwise idle (default on); omitted leaves it unchanged. Not persisted." };
        performance.Arguments.Add(perfDevice);
        performance.Options.Add(timing);
        performance.Options.Add(callerPath);
        performance.SetAction(p =>
        {
            var enabled = p.GetValue(timing);
            var caller = p.GetValue(callerPath);
            using var device = new CacheDevice(p.GetValue(perfDevice)!, enabled is not null || caller is not null);
            if (!device.GetWriteCacheState().SupportsPerformance)
                throw new NotSupportedException("Install the performance-telemetry driver first.");
            if (enabled is not null)
                device.Control(WriteCacheAction.PerformanceTiming, value: enabled.Value ? 1UL : 0UL);
            if (caller is not null)
                device.Control(WriteCacheAction.CallerPath, value: caller.Value ? 1UL : 0UL);
            Console.WriteLine(JsonSerializer.Serialize(device.GetPerformance(), new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        });
        root.Subcommands.Add(performance);
        var scenarios = new Command("cache-scenarios", "Current-boot RAM-cache policy scenarios on new files. Temporarily changes runtime policies, drains, verifies disk bytes and restores settings; no reboot or format.");
        var scenarioVolume = new Argument<string>("volume");
        scenarios.Arguments.Add(scenarioVolume);
        scenarios.SetAction(async (p, token) =>
        {
            var target = await QueueCache.Operations.DiskTarget.InspectAsync(p.GetValue(scenarioVolume)!, token);
            var results = await QueueCache.Operations.CacheScenarios.RunAsync(target, new Progress<string>(Console.Error.WriteLine), token);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return results.All(r => r.Result != "FAIL") ? 0 : 1;
        });
        root.Subcommands.Add(scenarios);
        var reads = new Command("test", "Read-only pass-through smoke test through an idle volume's handle; validates the exact volume GUID and size (see qcache volume list / Get-Volume).");
        var readDisk = new Argument<string>("volume");
        var readBytes = new Argument<long>("expected-bytes");
        var readInstance = new Argument<string>("volume-id") { Description = "Volume GUID with braces, e.g. {fa32f514-...}." };
        var detached = new Option<bool>("--detached");
        reads.Arguments.Add(readDisk);
        reads.Arguments.Add(readBytes);
        reads.Arguments.Add(readInstance);
        reads.Options.Add(detached);
        reads.SetAction((p, token) => QueueCache.Developer.ReadTests.RunAsync(p.GetValue(readDisk)!, p.GetValue(readBytes), p.GetValue(readInstance)!, !p.GetValue(detached), token));
        root.Subcommands.Add(reads);

        var writes = new Command("write-tests", "DESTRUCTIVE: fixed 64 MiB region at 1 GiB of an unformatted (RAW) lettered volume on a non-OS disk, written through the volume handle. Never formats; may change cache policy/hooks.");
        var disk = new Argument<string>("volume");
        var bytes = new Argument<long>("expected-bytes") { Description = "Exact volume (partition) size in bytes." };
        var instance = new Argument<string>("volume-id") { Description = "Volume GUID with braces, e.g. {fa32f514-...}." };
        var mode = new Argument<string>("mode");
        mode.AcceptOnlyFromAmong("write-disposable-region", "write-and-read-disposable-region", "verify-only", "verify-base-prefix", "write-dirty-prefix", "verify-dirty-prefix", "write-through-check", "write-concurrent-check", "write-toggle-check", "write-cancellation-check", "write-performance-check");
        var prefix = new Option<int>("--prefix-bytes");
        var seed = new Option<ulong>("--seed");
        writes.Arguments.Add(disk);
        writes.Arguments.Add(bytes);
        writes.Arguments.Add(instance);
        writes.Arguments.Add(mode);
        writes.Options.Add(prefix);
        writes.Options.Add(seed);
        writes.SetAction(p =>
        {
            var selected = p.GetValue(mode)!;
            var count = p.GetValue(prefix);
            var pattern = p.GetValue(seed);
            var needsPrefix = selected is "verify-base-prefix" or "write-dirty-prefix" or "verify-dirty-prefix";
            if (needsPrefix ? count <= 0 || count > (64 << 20) || count % 65536 != 0 : count != 0)
                return Task.FromResult(2);
            if (selected is "write-dirty-prefix" or "verify-dirty-prefix" && pattern == 0)
                return Task.FromResult(2);
            var args = new List<string> { p.GetValue(disk)!, N(p.GetValue(bytes)), p.GetValue(instance)!, "--" + selected };
            if (needsPrefix)
                args.Add(N(count));
            if (selected is "write-dirty-prefix" or "verify-dirty-prefix")
                args.Add(N(pattern));
            else if (pattern != 0)
                args.AddRange(["--seed", N(pattern)]);
            return QueueCache.Developer.WriteTests.Runner.RunAsync(args.ToArray());
        });
        root.Subcommands.Add(writes);

        var files = new Command("file-tests", "Advanced file-system scenarios on an exact non-OS disk. New files retained; some modes inject faults or prepare a reboot test.");
        var letter = new Argument<string>("letter");
        var fileDisk = new Argument<int>("disk");
        var fileBytes = new Argument<long>("expected-bytes");
        var fileInstance = new Argument<string>("instance");
        var fileMode = new Argument<string>("mode");
        fileMode.AcceptOnlyFromAmong("write-new-files", "concurrent", "baseline", "dirty-reboot", "dirty-reboot-unsafe", "test-flush-policy", "test-coalescing", "verify-files");
        var size = new Option<int?>("--size-mib");
        var run = new Option<string>("--run-id");
        files.Arguments.Add(letter);
        files.Arguments.Add(fileDisk);
        files.Arguments.Add(fileBytes);
        files.Arguments.Add(fileInstance);
        files.Arguments.Add(fileMode);
        files.Options.Add(size);
        files.Options.Add(run);
        files.SetAction(p =>
        {
            var selected = p.GetValue(fileMode)!;
            var mib = p.GetValue(size);
            var id = p.GetValue(run);
            if (selected == "verify-files" ? mib is not null || !Guid.TryParseExact(id, "N", out _) : id is not null)
                return Task.FromResult(2);
            if (mib is not null && (mib < 64 || mib > 8192 || selected is not ("write-new-files" or "concurrent" or "baseline")))
                return Task.FromResult(2);
            var args = new List<string> { p.GetValue(letter)!.TrimEnd(':'), N(p.GetValue(fileDisk)), N(p.GetValue(fileBytes)), p.GetValue(fileInstance)!, "--" + selected };
            if (mib is not null)
                args.Add(N(mib.Value));
            if (id is not null)
                args.Add(id);
            return QueueCache.Developer.FileTests.Runner.RunAsync(args.ToArray());
        });
        root.Subcommands.Add(files);
        var driver = new Command("driver", "Low-level diagnostics and synthetic hooks; not normal cache configuration.");
        foreach (var name in new[] { "delay", "fault" })
        {
            var command = new Command(name, "Set synthetic hook value; 0 clears it. Requires an instrumented driver.");
            var device = new Argument<string>("device");
            var value = new Argument<uint>("value");
            command.Arguments.Add(device);
            command.Arguments.Add(value);
            command.SetAction(p => compatibility(["lab-" + name, p.GetValue(device)!, N(p.GetValue(value))]));
            driver.Subcommands.Add(command);
        }
        var registration = new Command("registration", "Show the Volume/disk class filter lists, lab switches and any problem as JSON; changes nothing.");
        registration.SetAction(_ =>
        {
            var current = QueueCache.Operations.DriverRegistration.Inspect();
            var problems = current.Problems();
            Console.WriteLine(JsonSerializer.Serialize(new { Registration = current, Problems = problems }, new JsonSerializerOptions { WriteIndented = true }));
            return problems.Count == 0 ? 0 : 1;
        });
        driver.Subcommands.Add(registration);
        root.Subcommands.Add(driver);
        var lab = new Command("lab-disk", "Volume-filter lab disk: an expandable VHDX with two formatted volumes (NTFS by default) and one unformatted volume, for the volumes and trim-cache suites and write-tests.");
        var labPath = new Argument<string>("vhdx") { Description = @"Local .vhdx path, e.g. C:\QueueCache-Lab\VolumeLab.vhdx." };
        var create = new Command("create", "Create and attach a NEW VHDX and partition only that disk (expandable: uses only the space written). Refuses an existing file or a letter in use.");
        var letters = new Option<string>("--letters") { DefaultValueFactory = _ => "V,W,X", Description = "Two formatted volumes, then the unformatted volume." };
        var labSize = new Option<int>("--size-gib") { DefaultValueFactory = _ => QueueCache.Developer.LabDisk.DefaultSizeGiB };
        var labFileSystem = new Option<string>("--file-system") { DefaultValueFactory = _ => "NTFS", Description = "File system of the two formatted volumes: NTFS, ReFS (a Dev Drive, 50 GiB each), FAT32 or exFAT." };
        create.Arguments.Add(labPath);
        create.Options.Add(letters);
        create.Options.Add(labSize);
        create.Options.Add(labFileSystem);
        create.SetAction((p, token) =>
        {
            var fileSystem = QueueCache.Developer.LabDisk.ParseFileSystem(p.GetValue(labFileSystem)!);
            // Default size: large enough for the chosen file system.
            var size = p.GetResult(labSize) is null ? Math.Max(QueueCache.Developer.LabDisk.DefaultSizeGiB, QueueCache.Developer.LabDisk.MinimumSizeGiB(fileSystem) + 3)
                : p.GetValue(labSize);
            return QueueCache.Developer.LabDisk.CreateAsync(p.GetValue(labPath)!,
                QueueCache.Developer.LabDisk.ParseLetters(p.GetValue(letters)!), size, fileSystem, token);
        });
        lab.Subcommands.Add(create);
        var attach = new Command("attach", "Attach an existing lab VHDX (Windows does not reattach it after a restart) and show its volumes.");
        attach.Arguments.Add(labPath);
        attach.SetAction((p, token) => QueueCache.Developer.LabDisk.AttachAsync(p.GetValue(labPath)!, token));
        lab.Subcommands.Add(attach);
        var detach = new Command("detach", "Detach the lab VHDX. Refuses while any of its volumes has a cache task.");
        detach.Arguments.Add(labPath);
        detach.SetAction((p, token) => QueueCache.Developer.LabDisk.DetachAsync(p.GetValue(labPath)!, token));
        lab.Subcommands.Add(detach);
        root.Subcommands.Add(lab);
        return root;
    }
    private static string N<T>(T value) where T : IFormattable => value.ToString(null, CultureInfo.InvariantCulture);
}
