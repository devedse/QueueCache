using System.CommandLine;
using System.Runtime.Versioning;
using QueueCache.Developer.Verification;

namespace QueueCache.Cli;

[SupportedOSPlatform("windows")]
internal static class VerificationCommands
{
    private static VerificationRunner Runner()
    {
        var executable = Environment.ProcessPath ?? throw new IOException("Missing executable path.");
        // Also supports dotnet qcache.dll during development.
        return new(executable, Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [typeof(VerificationCommands).Assembly.Location] : []);
    }
    public static Command Create()
    {
        var command = new Command("verify", """
            Foreground current-boot verification. New files only; runtime policies restored. Never formats or reboots.

            Suites:
              quick              File-integrity checks; default. No DiskSpd needed.
              policies           Six cache configurations and disk-byte verification. No DiskSpd needed.
              flush-interference Focused hot-reader/blocked-writer test, with/without application flush.
                                 Use --repeats 2 for eight cases. Requires DiskSpd.
              performance        Repeated allocation/drain/queue-depth, delay and off/on workload matrix.
                                 Requires DiskSpd; 204 cases at defaults.
              full               All suites above; 218 cases at defaults. Requires DiskSpd.

            DiskSpd: download standard Microsoft DiskSpd from https://github.com/microsoft/diskspd/releases
            Extract DiskSpd.ZIP; use amd64\diskspd.exe on x64 Windows (also for Intel CPUs).
            CrystalDiskMark bundles a modified DiskSpd; that fork is not supported by this runner.
            The runner needs XML results (-Rxml -L), not CrystalDiskMark score-exit behavior.

            Examples:
              qcache developer verify Q: --suite quick
              qcache developer verify Q: --suite full --diskspd "C:\Tools\DiskSpd\amd64\diskspd.exe" --output .\results

            Output: unique QueueCache-Verify-* subfolder of --output (default: current directory).
            Read FINISHED.txt and SUMMARY.md there. MEASURED is not a performance acceptance verdict.
            Run elevated on a clean, non-OS test disk, with no competing workloads or armed fault/delay hooks.
            """);
        var volume = new Argument<string>("volume");
        var suite = new Option<string>("--suite") { DefaultValueFactory = _ => "quick", Description = "Which batch to run; see suite descriptions above. quick/policies do not require DiskSpd." };
        suite.AcceptOnlyFromAmong(VerificationPlan.Suites);
        var output = new Option<string>("--output") { DefaultValueFactory = _ => ".", Description = "Parent directory for a unique run folder; defaults to current directory." };
        var disk = new Option<string?>("--diskspd") { Description = "Path to Microsoft's extracted amd64\\diskspd.exe, not a folder or CDM executable. Quote paths with spaces." };
        var budget = new Option<int>("--budget-mib") { DefaultValueFactory = _ => 1024, Description = "Performance cache budget, 256..8192 MiB. Original configuration is restored." };
        var repeats = new Option<int>("--repeats") { DefaultValueFactory = _ => 3, Description = "Repetitions per performance case, 1..10; each retains separate evidence." };
        var duration = new Option<int>("--duration-seconds") { DefaultValueFactory = _ => 10, Description = "Measured workload duration, 5..60 seconds; preparation/warmup/draining add time." };
        var deadline = new Option<int>("--deadline-minutes") { DefaultValueFactory = _ => 90, Description = "Overall measurement limit, 1..1440 minutes; restoration has a separate deadline." };
        command.Arguments.Add(volume);
        foreach (var option in new Option[] { suite, output, disk, budget, repeats, duration, deadline }) command.Options.Add(option);
        command.SetAction((p, token) => Runner().RunAsync(new(p.GetValue(volume)!, p.GetValue(suite)!, p.GetValue(output)!,
            p.GetValue(disk), p.GetValue(budget), p.GetValue(repeats), p.GetValue(duration), p.GetValue(deadline)),
            new Progress<string>(Console.WriteLine), token));
        return command;
    }
    public static Command CreateRecovery()
    {
        var recover = new Command("verify-recover", "Retry restoration from an interrupted run after ensuring its owned processes have stopped.");
        var directory = new Argument<string>("run-directory"); recover.Arguments.Add(directory);
        recover.SetAction((p, token) => Runner().RecoverAsync(p.GetValue(directory)!, token));
        return recover;
    }
    public static Command CreateStatus()
    {
        var command = new Command("verify-status", "Print the status of a run without querying or changing the driver.");
        var directory = new Argument<string>("run-directory"); command.Arguments.Add(directory);
        command.SetAction(p => { Console.WriteLine(File.ReadAllText(Path.Combine(Path.GetFullPath(p.GetValue(directory)!), "status.json"))); return 0; });
        return command;
    }
}
