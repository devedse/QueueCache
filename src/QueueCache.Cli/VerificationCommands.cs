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
    private static (string Executable, IReadOnlyList<string> Prefix) Invocation()
    {
        var executable = Environment.ProcessPath ?? throw new IOException("Missing executable path.");
        // Also supports dotnet qcache.dll during development.
        return (executable, Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [typeof(VerificationCommands).Assembly.Location] : []);
    }
    private static VerificationRunner Runner() { var invocation = Invocation(); return new(invocation.Executable, invocation.Prefix); }
    public static Command Create()
    {
        var command = new Command("verify", """
            Foreground current-boot verification. Owned fixtures only; runtime policies restored. Managed suites format only their new owned devices. No automatic reboot.

            Suites:
              quick              File-integrity checks; default. No DiskSpd needed.
              managed-provider   Native private-sector/shared-budget/VHDX/NTFS/freeze/remove proof. Opt-in, no DiskSpd.
              ram-disk           Product pure-RAM create/format/lock veto/discard/recreate; unique owned fixture only.
              vhdx-backed        Product fixed/dynamic VHDX with independent Strict/Fast cache; flush/detach/reopen byte oracle.
              image-in-ram       Full import/checkpoint/export/dirty/reload; runtime bytes with the owned source unavailable. Opt-in.
              managed-broker-restart Five owned fixtures; restart only QueueCache's broker and prove unchanged creations/bytes and stopped recipes. Empty managed catalog required.
              managed-lifecycle-prepare Prepare five fixtures and a durable off-host manifest; retain live RAM. No reboot or power transition.
              managed-lifecycle-verify Verify a prior --managed-oracle after the externally observed --managed-transition; cleanup only after all checks pass.
              managed-lifecycle-cleanup Explicitly stop/forget only prior manifest-owned fixtures, retaining images/evidence.
              disk-removal-windows Native Windows eject while cache remains dirty/enabled, then live reconnect; no product preparation. Same disposable identity requirements.
              disk-removal       One disposable volume: Fast pending-write oracle, Windows eject, operator reconnect. Explicit disk identity and budget 256..512 MiB required; excludes surprise removal.
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
              stress-races       Lab NTFS volume and its sibling at once: versioned 4K..4M writers, cancelled overlapped
                                 I/O, flush/resize/teardown/drop/delay/allocation-fault/refused-settings chaos and owned
                                 RAM disk churn; then fault + Retry at drain parallelism 1/2/4 and every byte read back.
                                 --repeats 1, --soak-seconds 60..3600 (default 240). Lab disk volumes only.
              pressure           Deferred-age, Idle, watermark and capacity/backpressure byte checks.
                                 Separate from full; no DiskSpd needed.
              drain-decision     Seeded drain versus no-drain controls for fitting writes and cold reads; drain batch
                                 256/1024 KiB x parallelism 1/2/4, three alternated repetitions (42 cases).
                                 Parallelism 1/2/4; 24 cases at three repeats. Requires DiskSpd.
              trim-diagnostic    File-integrity/TRIM probes with cache enabled, then disabled.
                                 Unsupported TRIM stays SKIP; filter remains attached. No DiskSpd needed.
              trim-file          Driver-independent TRIM on a new 3 MiB file; guards and reuse checks.
                                 No cache controls or telemetry. Non-OS/non-paging volumes only; SKIP is not PASS.
              flush-interference Focused hot-reader/blocked-writer test, with/without application flush.
                                 Use --repeats 2 for eight cases. Requires DiskSpd.
              performance        Repeated allocation/drain/queue-depth, delay and off/on workload matrix.
                                 Requires DiskSpd; 204 cases at defaults.
              full               Integrity + performance + flush matrix; 218 cases at defaults.
              sequential-resident Prewarmed fitting-file sequential 1M Q8 read/write, timing off.
                                  Requires DiskSpd and --budget-mib 2048; 9 cases at defaults.
                                  Compares fresh per-I/O and precomputed random write buffers.
              cache-layout       Fitting sequential reads Q1/Q8: fresh allocation, sequential reuse,
                                 random reuse + drop-clean, then reallocated control. Timing off.
                                 Normal priority, DiskSpd, --budget-mib 2048; 24 cases at defaults.
              cache-layout-reset Adds same-allocation free-slot resets after sequential/random reuse.
                                 36 ordered cases; requires the matching diagnostic driver and DiskSpd.
              cache-layout-steady Never-cleared cache: 60 s random-read churn over a file twice the
                                 cache, re-read, then ascending reset control. 18 cases; measures layout.
              cache-layout-full  Full cache: sequential fill past capacity, 120 s random 16K reads,
                                 re-read, then ascending reset control. 18 cases; measures layout.
              cache-read-reference Resident 1M reads Q1T1/Q8T1/Q2T4, timing off (scores) and on (lock and queue
                                 time per read); 18 cases at --repeats 3; --budget-mib 2048. Attribution only.
              cache-read-misses  Same-build flags 7/15: cold 4K Q32 and 1M Q8 reads, cached controls,
                                 three alternated repetitions; byte/mode checks and worst-read latency.
              cache-read-bookkeeping Same-build copy flags 11/15 (one lookup per block, run-based recency moves):
                                 1M Q8T1 target, Q1T1 and Q2T4 controls; 18 cases at --repeats 3.
              cache-concurrency  Separate fitting files; 1/2/4 streams at total Q8, Q1 control, RAM-only and background-drain writes.
              cache-sustained    Six mixed read/write episodes with concurrent byte oracles and idle rereads, never clearing.
                                 --repeats 1, --soak-seconds 1800 (default); minimum 120 is a smoke test.
              cache-map-cost     Resident 4K reads with map polling off, every 2 s, and every 250 ms.
              cache-recall       Read recall off/on: a fitting file read again in a cache full of stale data,
                                 and a hot set across a one-off and a repeated scan larger than the cache.
              caller-backoff     256/0 caller cooldown, fitting 64K mixed Q1/Q8 and 4K/1M read controls.
                                 V23 diagnostics, normal priority; byte oracles run after each score.
              priority-affinity  Cached reads: Normal/BelowNormal CPU crossed with default/unbound affinity.
              priority-cost      Fitting RAM reads: independent Normal/CPU BelowNormal/I/O Low/memory Low.
                                 1M Q1/Q8 and 4K Q1 controls; owned child settings, three-second warmup.
              partial-read-accounting Patterned partial sectors and accounting, plus 16 overlapping cold/partial readers.
                                 Requires diagnostics V21; NTFS 512-byte sectors. No DiskSpd needed.
              ram-read-reference Direct/Standard RAM-disk reads: 1M Q1T1/Q8T1/Q2T4 and 4K controls.
              ram-read-attribution Three traced Direct RAM read shapes; diagnostic, not speed acceptance.
              ram-read-coordination Nine Direct off/on/off request/helper diagnostic windows.
              ram-read-async     Same-build original/candidate asynchronous split reads: 1M Q8T1 target,
                                 Q1T1 and Q2T4 controls; 18 windows at --repeats 3; --budget-mib 2048.
              ram-read-async-lifecycle Verified overlapped reads, in-flight cap and Direct end under load
                                 for asynchronous split reads; correctness only; --repeats 1.
              ram-write-reference Owned RAM-disk 1M writes Q1T1/Q8T1/Q2T4/Q8T4 and user-mode copies into and out
                                 of cold memory (ordinary, explicit, non-temporal stores); 36 windows at --repeats 3.
                                 The disk is rewritten and read back after its write windows.
              ram-read-scheduling Direct RAM-disk reads: default/unbound affinity and overlapping/interleaved cursors.
                                 Existing driver/copy path unchanged; 30 windows at three repeats; --budget-mib 2048.
                                 All ram-read-* suites: owned 2 GiB disks, whole-file byte checks, --budget-mib 2048,
                                 normal priority and DiskSpd; reference measurements, not speed acceptance.
              write-performance  Fitting-file random 4K Q1/32 and sequential 1M Q1/8 writes.
                                 Off/Eager/Idle, timing off/on; 72 cases. Requires DiskSpd.
                                 Use --budget-mib 2048 for a 1 GiB workload file.

            Comparisons with other products (driver-independent; no QueueCache configuration, hooks or telemetry;
            --product "<name> <version> <mode>" required; QueueCache must be loaded exactly when the product is QueueCache):
              compare-snapshot   Memory, loaded third-party drivers and storage filters only (e.g. before creating a disk).
              compare-ram        A 4 GiB RAM disk: 1M Q1/Q8/Q8T4 and 4K Q1/Q32/Q32T4 reads and writes with latency and CPU,
                                 memory before/after a 2 GiB fill, a 40,000-file tree (create, copy, read, delete), byte check.
                                 DiskSpd; --repeats 1..3 (3 for reports).
              compare-cache      A cached volume (files kept in <volume>\QCBench): hot CrystalDiskMark shapes, buffered and
                                 write-through writes, an 8 GiB file larger than the cache, 1 GiB absorb, 512 MiB + flush,
                                 8 GiB sustained writes, 5-minute 70/30 mix, file tree; waits for write-back after writes.
              compare-cache-cold Needs a cache emptied beforehand (product command or restart): first and second read of a
                                 1 GiB file, 60 s random reads of the 8 GiB file twice. Run compare-cache first.
              compare-durability-write  Generation 1 flushed and settled, then a durable save, raw writes without a flush and
                                 10,000 new small files; the evidence volume is flushed before FINISHED.txt. Reset the VM then.
              compare-durability-churn  2,000 base files settled, then 40 s of file creation (200/s) with deletes and no flush;
                                 FINISHED.txt as above. Reset the VM while the cache writes the churn out to the disk.
              compare-durability-verify After the reset, with --oracle <write run>\durability-oracle.json: what survived (for the
                                 churn test by file age) and chkdsk /scan. Measurement only.

            DiskSpd: download standard Microsoft DiskSpd from https://github.com/microsoft/diskspd/releases
            Extract DiskSpd.ZIP; use amd64\diskspd.exe on x64 Windows (also for Intel CPUs).
            Also supported: CrystalDiskMark's CdmResource\DiskSpd\DiskSpd64.exe on x64 Windows.
            Tested: Microsoft 2.3 and DiskSpd 2.2 bundled with CrystalDiskMark 9.0.3.
            Both use XML results (-Rxml -L). CDM's recognized text trailer is ignored, not its XML measurements.
            Keep the same executable/version when comparing benchmark runs.

            Examples:
              qcache developer verify Q: --suite quick
              qcache developer verify Q: --campaign performance --lab-ntfs W: --diskspd "C:\Tools\DiskSpd\diskspd.exe" --pause-backing-cache
              qcache developer verify Q: --campaign experiment --focus-suite ram-read-async --lab-ntfs W: --budget-mib 2048 --repeats 3 --diskspd "C:\Tools\DiskSpd\diskspd.exe" --pause-backing-cache
              qcache developer verify Q: --suite full --diskspd "C:\Tools\DiskSpd\amd64\diskspd.exe" --output .\results
              qcache developer verify Q: --suite full --diskspd "C:\Tools\CrystalDiskMark9_0_3\CdmResource\DiskSpd\DiskSpd64.exe" --output .\results

            Output: unique QueueCache-Verify-* subfolder of --output (default: current directory).
            Live timestamped progress is also saved to run.log, including errors and waiting messages.
            Read FINISHED.txt and SUMMARY.md there. MEASURED is not a performance acceptance verdict.
            Campaigns compose suites sequentially in one QueueCache-Campaign-* folder with child evidence and completion.json.
            Profiles: smoke (two short NTFS checks), experiment (one narrow RAM suite), focused (one suite plus retained
            correctness checks), performance (product-default regression), release-performance (adds writes/30-minute soak).
            Campaigns require an attached developer NTFS lab; ReFS only for focused caller-backoff. --campaign and --suite are exclusive.
            A controller can await process exit and read verify-completion once. Guide: docs/TESTING.md.
            Run ordinary suites elevated on a clean, non-OS test disk, with no competing workloads or armed fault/delay hooks.
            System suites require an existing output directory on a different physical disk and explicit VM/disk identity.
            system-files reports live unbuffered bytes; it does not alone prove persistence under an active Fast cache.
            """);
        var volume = new Argument<string>("volume");
        var suite = new Option<string?>("--suite") { Description = "Single batch; defaults to quick. Mutually exclusive with --campaign." };
        suite.AcceptOnlyFromAmong(VerificationPlan.Suites);
        var campaign = new Option<string?>("--campaign") { Description = "Run maintained suites sequentially: smoke, focused, experiment, performance or release-performance." };
        campaign.AcceptOnlyFromAmong(VerificationCampaignPlan.Profiles);
        var labNtfs = new Option<string?>("--lab-ntfs") { Description = "Campaign only: explicit NTFS volume on an attached qcache developer lab-disk VHDX." };
        var labRefs = new Option<string?>("--lab-refs") { Description = "Campaign only: optional ReFS lab for caller-backoff comparisons." };
        var focusSuite = new Option<string?>("--focus-suite") { Description = "Focused: affected suite plus retained checks; experiment: one implemented narrow RAM suite." };
        focusSuite.AcceptOnlyFromAmong(VerificationCampaignPlan.FocusSuites);
        var pauseBacking = new Option<bool>("--pause-backing-cache") { Description = "Campaign only: allow pausing the explicit performance volume's cache during lab phases and independently restoring it." };
        var output = new Option<string>("--output") { DefaultValueFactory = _ => ".", Description = "Parent directory for a unique run folder; defaults to current directory." };
        var disk = new Option<string?>("--diskspd") { Description = "Executable path: Microsoft amd64\\diskspd.exe or CrystalDiskMark CdmResource\\DiskSpd\\DiskSpd64.exe. Quote paths with spaces." };
        var budget = new Option<int?>("--budget-mib") { Description = "Performance cache budget, 256..8192 MiB. Default 1024 for suites, 2048 for campaigns; original configuration is restored." };
        var repeats = new Option<int>("--repeats") { DefaultValueFactory = _ => 3, Description = "Repetitions per performance case, 1..10; each retains separate evidence." };
        var duration = new Option<int>("--duration-seconds") { DefaultValueFactory = _ => 10, Description = "Measured workload duration, 5..60 seconds; preparation/warmup/draining add time." };
        var soak = new Option<int?>("--soak-seconds") { Description = "Sustained suite/campaign phase: 120..3600 seconds divisible by six. Suite/release default 1800; performance campaign default 120 (smoke)." };
        var deadline = new Option<int>("--deadline-minutes") { DefaultValueFactory = _ => 0, Description = "Optional overall limit: 0 = unlimited (default), or 1..1440 minutes. Per-operation and restoration timeouts still apply." };
        var preparationFlush = new Option<int>("--preparation-flush-seconds") { DefaultValueFactory = _ => 180, Description = "Explicit pre-workload flush deadline, 180..3600 seconds. Recorded in manifest; score windows and restoration deadline unchanged." };
        var caseFilter = new Option<string?>("--case-filter") { Description = "Focused performance suite: case-sensitive ID substring. A selected run is not the complete matrix." };
        var systemInstance = new Option<string?>("--system-instance") { Description = "System suites: exact expected C: physical-disk PnP instance ID." };
        var systemBytes = new Option<long?>("--system-bytes") { Description = "System suites: exact expected C: physical-disk byte size." };
        var recoverableVm = new Option<bool>("--recoverable-vm") { Description = "System suites: acknowledge a restorable disposable VM with external console access." };
        var oracle = new Option<string?>("--oracle") { Description = "system-post-restart: prior system-files oracle.json on a separate physical disk; compare-durability-verify: the write run's durability-oracle.json." };
        var disposableInstance = new Option<string?>("--disposable-instance") { Description = "disk-removal suites only: exact PnP instance of the disposable physical disk to eject." };
        var disposableBytes = new Option<long?>("--disposable-bytes") { Description = "disk-removal suites only: exact physical disk byte size. Results must be on another disk." };
        var managedOracle = new Option<string?>("--managed-oracle") { Description = "managed-lifecycle-verify/cleanup only: prior managed-lifecycle-manifest.json on another physical disk." };
        var managedTransition = new Option<ManagedLifecycleTransition?>("--managed-transition") { Description = "managed-lifecycle-verify only: externally observed Restart, ColdStart, FastStartup, Sleep, Hibernate, BrokerRestart or BrokerCrash. Never inferred from uptime." };
        var traceSymbols = new Option<string?>("--trace-symbols") { Description = "Attribution only: local directory containing matching native PDBs; recorded in the manifest." };
        var product = new Option<string?>("--product") { Description = "Comparison suites only: the measured product, version and mode, e.g. \"SoftPerfect RAM Disk 26.10 static\"." };
        var keepWorkloads = new Option<bool>("--keep-workloads") { Description = "Keep the run's workload files on the tested volume after a completed run (failed runs always keep them)." };
        command.Arguments.Add(volume);
        foreach (var option in new Option[] { suite, campaign, labNtfs, labRefs, focusSuite, pauseBacking, output, disk, budget, repeats, duration, deadline, preparationFlush, caseFilter, soak,
            systemInstance, systemBytes, recoverableVm, oracle, disposableInstance, disposableBytes, managedOracle, managedTransition, keepWorkloads, traceSymbols, product })
            command.Options.Add(option);
        command.SetAction((p, token) =>
        {
            RequireAdministrator();
            var selectedCampaign = p.GetValue(campaign);
            if (selectedCampaign is not null && p.GetValue(suite) is not null)
                throw new ArgumentException("--campaign and --suite are mutually exclusive, including --suite quick.");
            if (selectedCampaign is null && (p.GetValue(labNtfs) is not null || p.GetValue(labRefs) is not null || p.GetValue(focusSuite) is not null || p.GetValue(pauseBacking)))
                throw new ArgumentException("Lab/focus/pause options require --campaign.");
            var selected = new VerificationOptions(p.GetValue(volume)!, p.GetValue(suite) ?? "quick", p.GetValue(output)!,
            p.GetValue(disk), p.GetValue(budget) ?? (selectedCampaign is null ? 1024 : 2048), p.GetValue(repeats), p.GetValue(duration), p.GetValue(deadline), p.GetValue(preparationFlush), p.GetValue(caseFilter),
            p.GetValue(systemInstance), p.GetValue(systemBytes), p.GetValue(recoverableVm), p.GetValue(oracle),
            p.GetValue(disposableInstance), p.GetValue(disposableBytes), p.GetValue(managedOracle), p.GetValue(managedTransition), p.GetValue(soak),
            p.GetValue(keepWorkloads), p.GetValue(traceSymbols), p.GetValue(product));
            if (selectedCampaign is null) return Runner().RunAsync(selected, new ConsoleProgress(), token);
            var invocation = Invocation();
            return new VerificationCampaignRunner(new VerificationCampaignHost(invocation.Executable, invocation.Prefix))
                .RunAsync(new(selected, selectedCampaign, p.GetValue(labNtfs) ?? throw new ArgumentException("Campaign requires --lab-ntfs <volume>."),
                    p.GetValue(labRefs), p.GetValue(focusSuite), p.GetValue(pauseBacking)), new ConsoleProgress(), token);
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
    public static Command CreateCompletion()
    {
        var command = new Command("verify-completion", "Read and validate a finalized campaign's durable completion event. Read-only; no elevation or driver access.");
        var directory = new Argument<string>("campaign-directory"); command.Arguments.Add(directory);
        var parent = new Option<bool>("--output-parent") { Description = "Treat the argument as an explicit unique job output parent; require exactly one campaign and validate it on this machine." };
        command.Options.Add(parent);
        command.SetAction(p => { Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
            p.GetValue(parent) ? VerificationCampaignEvidence.ReadCompletionFromParent(p.GetValue(directory)!) :
                VerificationCampaignEvidence.ReadCompletion(p.GetValue(directory)!), RunStorage.Json)); return 0; });
        return command;
    }
    public static Command CreateAttributionAnalysis()
    {
        var command = new Command("verify-attribution", "Analyze a finalized ram-read-attribution run without repeating workloads or changing driver state. Writes a new analysis folder; preserves the original collection verdict.");
        var directory = new Argument<string>("run-directory"); command.Arguments.Add(directory);
        var symbols = new Option<string>("--trace-symbols") { Required = true, Description = "Native PDB directory matching the original captured GUID/age and hashes." };
        command.Options.Add(symbols);
        command.SetAction(async p => { Console.WriteLine(await RamReadAttribution.ReanalyzeAsync(p.GetValue(directory)!, p.GetValue(symbols)!)); return 0; });
        return command;
    }
}
