using System.CommandLine;
using System.Globalization;
using System.Runtime.Versioning;

namespace QueueCache.Cli;

[SupportedOSPlatform("windows")]
internal static class DeveloperCommands
{
    public static Command Create(Func<string[], Task<int>> compatibility)
    {
        var root = new Command("developer", "Advanced integration tests and driver hooks. Ordinary file-only checks: qcache test.");
        var reads = new Command("test", "Read-only pass-through smoke test on an idle disk; validates exact identity and size.");
        var readDisk = new Argument<int>("disk");
        var readBytes = new Argument<long>("expected-bytes");
        var readInstance = new Argument<string>("instance");
        var detached = new Option<bool>("--detached");
        reads.Arguments.Add(readDisk); reads.Arguments.Add(readBytes); reads.Arguments.Add(readInstance); reads.Options.Add(detached);
        reads.SetAction((p, token) => QueueCache.Developer.ReadTests.RunAsync(p.GetValue(readDisk), p.GetValue(readBytes), p.GetValue(readInstance)!, !p.GetValue(detached), token));
        root.Subcommands.Add(reads);

        var writes = new Command("write-tests", "DESTRUCTIVE: fixed 64 MiB region at 1 GiB on an empty RAW non-OS disk. Never formats; may change cache policy/hooks.");
        var disk = new Argument<int>("disk"); var bytes = new Argument<long>("expected-bytes"); var instance = new Argument<string>("instance");
        var mode = new Argument<string>("mode");
        mode.AcceptOnlyFromAmong("write-disposable-region", "write-and-read-disposable-region", "verify-only", "verify-base-prefix", "write-dirty-prefix", "verify-dirty-prefix", "write-through-check", "write-concurrent-check", "write-toggle-check", "write-cancellation-check", "write-performance-check");
        var prefix = new Option<int>("--prefix-bytes"); var seed = new Option<ulong>("--seed");
        writes.Arguments.Add(disk); writes.Arguments.Add(bytes); writes.Arguments.Add(instance); writes.Arguments.Add(mode); writes.Options.Add(prefix); writes.Options.Add(seed);
        writes.SetAction(p =>
        {
            var selected = p.GetValue(mode)!; var count = p.GetValue(prefix); var pattern = p.GetValue(seed);
            var needsPrefix = selected is "verify-base-prefix" or "write-dirty-prefix" or "verify-dirty-prefix";
            if (needsPrefix ? count <= 0 || count > (64 << 20) || count % 65536 != 0 : count != 0) return Task.FromResult(2);
            if (selected is "write-dirty-prefix" or "verify-dirty-prefix" && pattern == 0) return Task.FromResult(2);
            var args = new List<string> { N(p.GetValue(disk)), N(p.GetValue(bytes)), p.GetValue(instance)!, "--" + selected };
            if (needsPrefix) args.Add(N(count));
            if (selected is "write-dirty-prefix" or "verify-dirty-prefix") args.Add(N(pattern));
            else if (pattern != 0) args.AddRange(["--seed", N(pattern)]);
            return QueueCache.Developer.WriteTests.Runner.RunAsync(args.ToArray());
        });
        root.Subcommands.Add(writes);

        var files = new Command("file-tests", "Advanced NTFS scenarios on an exact non-OS disk. New files retained; some modes inject faults or prepare a reboot test.");
        var letter = new Argument<string>("letter"); var fileDisk = new Argument<int>("disk"); var fileBytes = new Argument<long>("expected-bytes"); var fileInstance = new Argument<string>("instance");
        var fileMode = new Argument<string>("mode");
        fileMode.AcceptOnlyFromAmong("write-new-files", "concurrent", "baseline", "dirty-reboot", "dirty-reboot-unsafe", "test-flush-policy", "test-coalescing", "verify-files");
        var size = new Option<int?>("--size-mib"); var run = new Option<string>("--run-id");
        files.Arguments.Add(letter); files.Arguments.Add(fileDisk); files.Arguments.Add(fileBytes); files.Arguments.Add(fileInstance); files.Arguments.Add(fileMode); files.Options.Add(size); files.Options.Add(run);
        files.SetAction(p =>
        {
            var selected = p.GetValue(fileMode)!; var mib = p.GetValue(size); var id = p.GetValue(run);
            if (selected == "verify-files" ? mib is not null || !Guid.TryParseExact(id, "N", out _) : id is not null) return Task.FromResult(2);
            if (mib is not null && (mib < 64 || mib > 8192 || selected is not ("write-new-files" or "concurrent" or "baseline"))) return Task.FromResult(2);
            var args = new List<string> { p.GetValue(letter)!.TrimEnd(':'), N(p.GetValue(fileDisk)), N(p.GetValue(fileBytes)), p.GetValue(fileInstance)!, "--" + selected };
            if (mib is not null) args.Add(N(mib.Value));
            if (id is not null) args.Add(id);
            return QueueCache.Developer.FileTests.Runner.RunAsync(args.ToArray());
        });
        root.Subcommands.Add(files);
        var driver = new Command("driver", "Low-level diagnostics and synthetic hooks; not normal cache configuration.");
        foreach (var name in new[] { "delay", "fault" })
        {
            var command = new Command(name, "Set synthetic hook value; 0 clears it. Requires an instrumented driver.");
            var device = new Argument<string>("device"); var value = new Argument<uint>("value");
            command.Arguments.Add(device); command.Arguments.Add(value);
            command.SetAction(p => compatibility(["lab-" + name, p.GetValue(device)!, N(p.GetValue(value))]));
            driver.Subcommands.Add(command);
        }
        var inspect = new Command("inspect", "Read per-device filter registration by PnP instance; does not modify registration.");
        var target = new Argument<string>("instance"); inspect.Arguments.Add(target);
        inspect.SetAction(p => compatibility(["lab-filter", "inspect", p.GetValue(target)!]));
        driver.Subcommands.Add(inspect); root.Subcommands.Add(driver);
        return root;
    }
    private static string N<T>(T value) where T : IFormattable => value.ToString(null, CultureInfo.InvariantCulture);
}
