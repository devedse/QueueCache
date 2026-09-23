using System.CommandLine;
using System.Runtime.Versioning;
using System.Security.Principal;
using QueueCache.Developer.Verification;

namespace QueueCache.Cli;

[SupportedOSPlatform("windows")]
internal static class VerificationCommands
{
    private sealed class ConsoleProgress : IProgress<string>
    {
        // Progress<T> posts asynchronously; short failed runs can exit before it prints.
        public void Report(string message) => Console.WriteLine(message);
    }

    private static void RequireAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Administrator access required. Open Windows Terminal, Command Prompt or PowerShell with 'Run as administrator', then run this command again. Verification accesses physical disks and changes runtime cache settings. No tests were started. verify-status and --help do not require elevation.");
    }
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
              system-preflight  Read-only C: identity/state check; no workload or cache changes.
                                 Requires explicit VM acknowledgement, disk identity and off-disk output.
              system-files      Bounded 64 MiB owned-file write/overwrite check on C:.
                                 Writes the independent byte oracle off-target before the file.
              system-post-restart Read-only owned-file check using a prior --oracle on another disk.
                                 Never replays system-files writes or reboots automatically.
              system-image-baseline Guarded uncached 349 MiB BMP byte workload on C:.
                                 Leaves C: disabled/released and records a matching pre-active baseline.
              system-active-image Guarded Fast and Strict 349 MiB BMP byte workloads using normal C: activation.
                                 Accepts reconciled system usage paths and restores C: disabled/released.
              policies           Sector regressions, sustained foreground/drain proof, six policies and disk-byte verification.
                                 No DiskSpd needed.
              pressure           Deferred-age, Idle, watermark and capacity/backpressure byte checks.
                                 Separate from full; no DiskSpd needed.
              drain-decision     Seeded drain versus no-drain controls for fitting writes and cold reads.
                                 Parallelism 1/2/4; 24 cases at three repeats. Requires DiskSpd.
              trim-diagnostic    File-integrity/TRIM probes with cache enabled, then disabled.
                                 Unsupported TRIM stays SKIP; filter remains attached. No DiskSpd needed.
              trim-file          Driver-independent TRIM on a new 3 MiB file; guards and reuse checks.
                                 No cache controls or telemetry. Non-OS/non-paging NTFS only; SKIP is not PASS.
              flush-interference Focused hot-reader/blocked-writer test, with/without application flush.
                                 Use --repeats 2 for eight cases. Requires DiskSpd.
              performance        Repeated allocation/drain/queue-depth, delay and off/on workload matrix.
                                 Requires DiskSpd; 204 cases at defaults.
              full               Integrity + performance + flush matrix; 218 cases at defaults.
              write-performance  Fitting-file random 4K Q1/32 and sequential 1M Q1/8 writes.
                                 Off/Eager/Idle, timing off/on; 72 cases. Requires DiskSpd.
                                 Use --budget-mib 2048 for a 1 GiB workload file.

            DiskSpd: download standard Microsoft DiskSpd from https://github.com/microsoft/diskspd/releases
            Extract DiskSpd.ZIP; use amd64\diskspd.exe on x64 Windows (also for Intel CPUs).
            Also supported: CrystalDiskMark's CdmResource\DiskSpd\DiskSpd64.exe on x64 Windows.
            Tested: Microsoft 2.3 and DiskSpd 2.2 bundled with CrystalDiskMark 9.0.3.
            Both use XML results (-Rxml -L). CDM's recognized text trailer is ignored, not its XML measurements.
            Keep the same executable/version when comparing benchmark runs.

            Examples:
              qcache developer verify Q: --suite quick
              qcache developer verify Q: --suite full --diskspd "C:\Tools\DiskSpd\amd64\diskspd.exe" --output .\results
              qcache developer verify Q: --suite full --diskspd "C:\Tools\CrystalDiskMark9_0_3\CdmResource\DiskSpd\DiskSpd64.exe" --output .\results

            Output: unique QueueCache-Verify-* subfolder of --output (default: current directory).
            Live timestamped progress is also saved to run.log, including errors and waiting messages.
            Read FINISHED.txt and SUMMARY.md there. MEASURED is not a performance acceptance verdict.
            Run ordinary suites elevated on a clean, non-OS test disk, with no competing workloads or armed fault/delay hooks.
            System suites require an existing output directory on a different physical disk and explicit VM/disk identity.
            system-files reports live unbuffered bytes; it does not alone prove persistence under an active Fast cache.
            """);
        var volume = new Argument<string>("volume");
        var suite = new Option<string>("--suite") { DefaultValueFactory = _ => "quick", Description = "Which batch to run; see suite descriptions above. System suites require explicit guarded options." };
        suite.AcceptOnlyFromAmong(VerificationPlan.Suites);
        var output = new Option<string>("--output") { DefaultValueFactory = _ => ".", Description = "Parent directory for a unique run folder; defaults to current directory." };
        var disk = new Option<string?>("--diskspd") { Description = "Executable path: Microsoft amd64\\diskspd.exe or CrystalDiskMark CdmResource\\DiskSpd\\DiskSpd64.exe. Quote paths with spaces." };
        var budget = new Option<int>("--budget-mib") { DefaultValueFactory = _ => 1024, Description = "Performance cache budget, 256..8192 MiB. Original configuration is restored." };
        var repeats = new Option<int>("--repeats") { DefaultValueFactory = _ => 3, Description = "Repetitions per performance case, 1..10; each retains separate evidence." };
        var duration = new Option<int>("--duration-seconds") { DefaultValueFactory = _ => 10, Description = "Measured workload duration, 5..60 seconds; preparation/warmup/draining add time." };
        var deadline = new Option<int>("--deadline-minutes") { DefaultValueFactory = _ => 0, Description = "Optional overall limit: 0 = unlimited (default), or 1..1440 minutes. Per-operation and restoration timeouts still apply." };
        var preparationFlush = new Option<int>("--preparation-flush-seconds") { DefaultValueFactory = _ => 180, Description = "Explicit pre-workload flush deadline, 180..3600 seconds. Recorded in manifest; score windows and restoration deadline unchanged." };
        var caseFilter = new Option<string?>("--case-filter") { Description = "write-performance or drain-decision: case-sensitive ID substring. A selected run is not the complete matrix." };
        var systemInstance = new Option<string?>("--system-instance") { Description = "System suites: exact expected C: physical-disk PnP instance ID." };
        var systemBytes = new Option<long?>("--system-bytes") { Description = "System suites: exact expected C: physical-disk byte size." };
        var recoverableVm = new Option<bool>("--recoverable-vm") { Description = "System suites: acknowledge a restorable disposable VM with external console access." };
        var oracle = new Option<string?>("--oracle") { Description = "system-post-restart only: prior system-files oracle.json on a separate physical disk." };
        command.Arguments.Add(volume);
        foreach (var option in new Option[] { suite, output, disk, budget, repeats, duration, deadline, preparationFlush, caseFilter,
            systemInstance, systemBytes, recoverableVm, oracle })
            command.Options.Add(option);
        command.SetAction((p, token) =>
        {
            RequireAdministrator();
            return Runner().RunAsync(new(p.GetValue(volume)!, p.GetValue(suite)!, p.GetValue(output)!,
            p.GetValue(disk), p.GetValue(budget), p.GetValue(repeats), p.GetValue(duration), p.GetValue(deadline), p.GetValue(preparationFlush), p.GetValue(caseFilter),
            p.GetValue(systemInstance), p.GetValue(systemBytes), p.GetValue(recoverableVm), p.GetValue(oracle)),
            new ConsoleProgress(), token);
        });
        return command;
    }
    public static Command CreateRecovery()
    {
        var recover = new Command("verify-recover", "Requires Run as administrator. Retry restoration after ensuring owned processes have stopped. Progress and errors are saved to run.log in the new recovery subfolder.");
        var directory = new Argument<string>("run-directory");
        recover.Arguments.Add(directory);
        recover.SetAction((p, token) => { RequireAdministrator(); return Runner().RecoverAsync(p.GetValue(directory)!, token, new ConsoleProgress()); });
        return recover;
    }
    public static Command CreateStatus()
    {
        var command = new Command("verify-status", "Print the status of a run without querying or changing the driver.");
        var directory = new Argument<string>("run-directory");
        command.Arguments.Add(directory);
        command.SetAction(p => { Console.WriteLine(File.ReadAllText(Path.Combine(Path.GetFullPath(p.GetValue(directory)!), "status.json"))); return 0; });
        return command;
    }
}
