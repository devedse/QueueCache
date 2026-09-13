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
        var command = new Command("verify", "Foreground current-boot verification. New files only; runtime policies restored. Never formats or reboots.");
        var volume = new Argument<string>("volume");
        var suite = new Option<string>("--suite") { DefaultValueFactory = _ => "quick" };
        suite.AcceptOnlyFromAmong(VerificationPlan.Suites);
        var output = new Option<string>("--output") { DefaultValueFactory = _ => ".", Description = "Parent directory for a unique run folder; defaults to current directory." };
        var disk = new Option<string?>("--diskspd") { Description = "Standard DiskSpd executable with -Rxml support (not the CrystalDiskMark fork)." };
        var budget = new Option<int>("--budget-mib") { DefaultValueFactory = _ => 1024 };
        var repeats = new Option<int>("--repeats") { DefaultValueFactory = _ => 3 };
        var duration = new Option<int>("--duration-seconds") { DefaultValueFactory = _ => 10 };
        var deadline = new Option<int>("--deadline-minutes") { DefaultValueFactory = _ => 90 };
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
