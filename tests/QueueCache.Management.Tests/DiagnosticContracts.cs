using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using QueueCache.Developer.Verification;
using QueueCache.Management;
using QueueCache.Operations;

internal static class DiagnosticContracts
{
    public static void Run()
    {
        static void Check(bool value, string name) { if (!value) throw new Exception(name); }
        static void Reject(Action action)
        {
            try { action(); } catch (InvalidDataException) { return; }
            throw new Exception("Expected diagnostic evidence rejection.");
        }
        Check(VerificationPlan.Version == 156, "plan 156 adds the paired gather-policy experiment and V5 gap attribution");
        Check(VerificationPlan.Integrity(new("Q:", "cache-allocation")).Single().Id == "cache-allocation-bounds",
            "small-cache allocation runs through the maintained verification runner");
        var drainTiming = DrainDecisionTiming.ForScore(10);
        Check(drainTiming.ScoreSeconds == 10 && drainTiming.ProcessDeadlineSeconds > 140 &&
            drainTiming.FlushDeadlineSeconds == drainTiming.ProcessDeadlineSeconds &&
            drainTiming.TelemetrySeconds > 45 + drainTiming.ProcessDeadlineSeconds &&
            drainTiming.TelemetryDeadlineSeconds > drainTiming.TelemetrySeconds,
            "the recorded 140-second pending-drain completion fits without ending its observer first, including readiness delay");
        var drainStart = DateTimeOffset.UtcNow;
        var completeDrain = Enumerable.Range(0, 146).Select(i => drainStart.AddSeconds(i)).ToArray();
        TelemetryCoverage.Validate(completeDrain, drainStart.AddMilliseconds(250), drainStart.AddSeconds(140));
        Reject(() => TelemetryCoverage.Validate(completeDrain.Take(131).ToArray(),
            drainStart.AddMilliseconds(250), drainStart.AddSeconds(140)));
        Reject(() => TelemetryCoverage.Validate(completeDrain.Where((_, i) => i is < 60 or > 63).ToArray(),
            drainStart.AddMilliseconds(250), drainStart.AddSeconds(140)));
        RunPlan120Contracts(Check, Reject);
        ReadMissContracts(Check, Reject);
        RunComparisonContracts(Check, Reject);
        CompareDrainContracts.Run(Check, Reject);
        DrainLockContracts.Run(Check, Reject);
        RunTreeDecisionContracts(Check, Reject);
        RunReadTailDecisionContracts(Check, Reject);
        RunDrainFlushContracts(Check, Reject);
        VerificationRunnerTests.RunCoordinationContracts(Check, Reject);
        VerificationRunnerTests.RunAsyncReadContracts(Check, Reject);
        var asyncExperiment = VerificationCampaignPlan.Create(new(new("Q:", BudgetMiB: 2048, Repeats: 3,
            DiskSpd: Environment.ProcessPath), "experiment", "W:", FocusSuite: "ram-read-async"));
        Check(asyncExperiment.Count == 1 && asyncExperiment[0].MeasurementWindows == 18 && asyncExperiment[0].ExpectedCases.Count == 1,
            "asynchronous read experiment is one phase of 18 windows on every host");
        foreach (var profile in new[] { "performance", "release-performance" })
        {
            var regression = VerificationCampaignPlan.Create(new(new("Q:", BudgetMiB: 2048, Repeats: 3, DiskSpd: Environment.ProcessPath), profile, "W:"));
            Check(regression.Single(p => p.Options.Suite == "ram-read-async-lifecycle").Options.Repeats == 1 &&
                regression.Sum(p => p.ExpectedCases.Count) == (profile == "performance" ? 34 : 106),
                "regression campaigns compose on every host, with lifecycle checks run once");
        }
        var coordination = RamReadReferencePlan.CasesFor(RamReadRunKind.Coordination, 1);
        Check(coordination.Count == 9 && coordination.Count(c => c.CoordinationEnabled) == 3 &&
            coordination.All(c => c.SchedulingControl && !c.DisableAffinity), "nine off/on/off coordination windows on every host");
        var experiment = VerificationCampaignPlan.Create(new(new("Q:", BudgetMiB: 2048, Repeats: 1,
            DiskSpd: Environment.ProcessPath), "experiment", "W:", FocusSuite: "ram-read-coordination"));
        Check(experiment.Count == 1 && experiment[0].MeasurementWindows == 9 && experiment[0].ExpectedCases.Count == 1,
            "one-phase experiment excludes retained correctness matrix on every host");
        var cases = RamReadReferencePlan.CasesFor(RamReadRunKind.Attribution, 3);
        Check(cases.Count == 9 && cases.Select(c => c.Id).Distinct().Count() == 9 &&
            cases.GroupBy(c => (c.QueueDepth, c.Threads)).All(g => g.Count() == 3) &&
            cases.All(c => c.SchedulingControl && !c.DisableAffinity), "three immutable traced shapes with balanced repetitions");
        Check(cases[2].Interleaved && RamReadReferencePlan.Arguments(cases[2], 10).Contains("-s4M") &&
            RamReadReferencePlan.Arguments(cases[2], 10).Contains("-T1M") && !RamReadReferencePlan.Arguments(cases[2], 10).Contains("-si"), "separate source lanes");
        Check(RamReadReferencePlan.CasesFor(RamReadRunKind.Reference, 3).Count == 36 &&
            RamReadReferencePlan.CasesFor(RamReadRunKind.Scheduling, 3).Count == 30, "legacy run kinds retain their meanings");
        var forwarded = JsonSerializer.Deserialize<WorkerJob>(JsonSerializer.Serialize(new WorkerJob("ram-read-reference", "Q:", "reply", ReferenceKind: RamReadRunKind.Attribution)))!;
        Check(forwarded.ReferenceKind == RamReadRunKind.Attribution, "typed attribution crosses the worker boundary");
        var span = new XElement("TimeSpan", new XElement("DisableAffinity", "false"), new XElement("Duration", 10), new XElement("Warmup", 3),
            new XElement("Targets", new XElement("Target", new XElement("BlockSize", 1048576), new XElement("RequestCount", 1),
                new XElement("ThreadsPerFile", 1), new XElement("WriteRatio", 0), new XElement("MaxFileSize", 1073741824),
                new XElement("IOPriority", 3), new XElement("DisableOSCache", "true"), new XElement("UseLargePages", "false"),
                new XElement("StrideSize", 1048576), new XElement("ThreadStride", 0), new XElement("InterlockedSequential", "false"))));
        string Xml() => new XElement("Results", new XElement("Profile", new XElement("TimeSpans", span))).ToString();
        var arguments = new[] { "-b1M", "-o1", "-t1", "-w0", "-f1024M", "-d10", "-W3", "-I3" };
        CachePriorityProfile.Validate(Xml(), arguments, 1);
        RamReadReferencePlan.ValidateProfile(cases[0], Xml(), 10);
        foreach (var element in span.Descendants().Where(e => !e.HasElements).ToArray())
        {
            var old = element.Value; element.Value = "unexpected";
            Reject(() => CachePriorityProfile.Validate(Xml(), arguments, 1));
            Reject(() => RamReadReferencePlan.ValidateProfile(cases[0], Xml(), 10));
            element.Value = old;
        }
        Reject(() => CachePriorityProfile.Validate(Xml(), [.. arguments, "-n"], 1));
        Reject(() => CachePriorityProfile.Validate(Xml(), arguments, 2));
        var identity = new ProcessIdentity(17, DateTime.UtcNow);
        RamReadAttribution.ValidateWindow(identity, DateTimeOffset.UtcNow.AddSeconds(10), 100, 40, 39);
        Reject(() => RamReadAttribution.ValidateWindow(identity, DateTimeOffset.UtcNow.AddSeconds(10), 100, 40, 37));
        Reject(() => RamReadAttribution.ValidateWindow(identity, DateTimeOffset.UtcNow.AddSeconds(10), 0, 0, 0));
        Reject(() => RamReadAttribution.ValidateWindow(identity, DateTimeOffset.UtcNow.AddSeconds(-10), 100, 40, 39));
        var pdb = new byte[512 * 4];
        "Microsoft C/C++ MSF 7.00\r\n\u001aDS\0\0\0"u8.CopyTo(pdb);
        void Number(int offset, int value) => System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(pdb.AsSpan(offset, 4), value);
        Number(32, 512); Number(44, 16); Number(52, 1); Number(512, 2);
        Number(1024, 2); Number(1028, 0); Number(1032, 28); Number(1036, 3);
        var signature = Guid.NewGuid(); Number(1536 + 8, 2); signature.TryWriteBytes(pdb.AsSpan(1536 + 12, 16));
        Check(NativeTraceSymbols.ReadPdbIdentity(pdb) == (signature, 2), "native PDB signature and age come from the information stream");
        Number(1036, 1000); Reject(() => NativeTraceSymbols.ReadPdbIdentity(pdb));
        Reject(() => NativeTraceSymbols.ReadPdbIdentity([1, 2, 3]));
        var target = new QueueCache.Operations.DiskTarget('Q', 99990, 8L << 30, "fixture");
        var recovery = new RecoverySnapshot(1, target, new(1, 0, 8UL << 30, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            false, "unchanged profiles", DateTimeOffset.UtcNow, "fixture");
        var copyFlagsState = recovery.State with { Instance = 7, Generation = 12 };
        var copyFlagsRecovery = recovery with { State = copyFlagsState, CopyFlags = 11 };
        var legacyJson = JsonNode.Parse(JsonSerializer.Serialize(copyFlagsRecovery))!.AsObject();
        legacyJson.Remove(nameof(RecoverySnapshot.CopyFlags));
        Check(JsonSerializer.Deserialize<RecoverySnapshot>(legacyJson.ToJsonString())?.CopyFlags is null &&
              JsonSerializer.Deserialize<RecoverySnapshot>(JsonSerializer.Serialize(copyFlagsRecovery))?.CopyFlags == 11,
            "recovery snapshots preserve nondefault copy flags and distinguish legacy missing evidence");
        Check(VerificationWorker.CopyFlagsMismatches(11, 11).Count == 0 &&
              VerificationWorker.CopyFlagsMismatches(0, 0).Count == 0 &&
              VerificationWorker.CopyFlagsMismatches(11, 7).SequenceEqual(["CopyFlags: expected 11, actual 7"]) &&
              VerificationWorker.CopyFlagsMismatches(11, null).SequenceEqual(["CopyFlags: expected 11, actual unavailable"]),
            "copy-flags restoration requires exact numeric readback and accepts zero");
        var legacyCopyEvidence = new CacheLayoutSnapshot(copyFlagsState,
            JsonSerializer.Deserialize<CachePerformance>("{\"Frequency\":10000000}")!,
            new CacheDiagnostics(0, 0, 0, 0, 0, 0, 0, 0, 0) { CopyFlags = 0 });
        Check(VerificationWorker.ValidateLegacyCopyFlags(copyFlagsRecovery with { CopyFlags = null }, legacyCopyEvidence) == 0,
            "legacy read-miss flags can be recovered from matching pre-mutation cache identity evidence");
        try
        {
            VerificationWorker.ValidateLegacyCopyFlags(copyFlagsRecovery with { CopyFlags = null },
                legacyCopyEvidence with { State = copyFlagsState with { Generation = 13 } });
            throw new Exception("Legacy copy-flags evidence from another generation was accepted.");
        }
        catch (IOException) { }
        Reject(() => VerificationWorker.ValidateLegacyCopyFlags(copyFlagsRecovery with { CopyFlags = null },
            legacyCopyEvidence with { Diagnostics = legacyCopyEvidence.Diagnostics with { CopyFlags = null } }));
        Reject(() => VerificationWorker.ValidateLegacyCopyFlags(copyFlagsRecovery with { CopyFlags = null }, null));
        Reject(() => VerificationWorker.ValidateLegacyCopyFlags(copyFlagsRecovery with { CopyFlags = null },
            legacyCopyEvidence with { State = null! }));
        try
        {
            VerificationCampaignEvidence.ValidateConfiguration(copyFlagsRecovery,
                copyFlagsRecovery with { CopyFlags = 7 });
            throw new Exception("Campaign baseline accepted changed copy flags.");
        }
        catch (IOException) { }
        var finalTarget = new CampaignTargetEvidence(CampaignTargetRole.Performance, recovery, "NTFS", "fixture", "fixture", null,
            false, null, null, new(true, null, []));
        var finalOrder = new List<string>(); var finalClean = false;
        VerificationCampaignHost.FinalizeTargetsAsync([finalTarget],
            _ => { finalOrder.Add("prepare"); finalClean = true; return Task.CompletedTask; },
            t => { finalOrder.Add("inspect"); return Task.FromResult(finalClean ? t : t with { Recovery = t.Recovery with { State = t.Recovery.State with { DirtyBytes = 4096 } } }); })
            .GetAwaiter().GetResult();
        Check(finalOrder.SequenceEqual(["prepare", "inspect"]), "final backing restoration prepares before strict clean capture");
        finalOrder.Clear(); var lateWrites = 1;
        VerificationCampaignHost.FinalizeTargetsAsync([finalTarget],
            _ => { finalOrder.Add("prepare"); return Task.CompletedTask; },
            t => { finalOrder.Add("inspect"); return lateWrites-- > 0 ? Task.FromException<CampaignTargetEvidence>(new IOException("worker: " + VerificationWorker.NotCleanMessage)) : Task.FromResult(t); })
            .GetAwaiter().GetResult();
        Check(finalOrder.SequenceEqual(["prepare", "inspect", "prepare", "inspect"]), "late lab-image writes are drained again before the strict capture");
        var attempts = 0;
        try
        {
            VerificationCampaignHost.FinalizeTargetsAsync([finalTarget], _ => Task.CompletedTask,
                t => { attempts++; return Task.FromException<CampaignTargetEvidence>(new IOException(VerificationWorker.NotCleanMessage)); }).GetAwaiter().GetResult();
            throw new Exception("Final restoration accepted a cache that never stayed clean.");
        }
        catch (IOException) { Check(attempts == VerificationCampaignHost.FinalCleanAttempts, "a cache that never stays clean fails after the bounded attempts"); }
        attempts = 0;
        try
        {
            VerificationCampaignHost.FinalizeTargetsAsync([finalTarget], _ => Task.CompletedTask,
                t => { attempts++; return Task.FromException<CampaignTargetEvidence>(new IOException("identity changed")); }).GetAwaiter().GetResult();
            throw new Exception("Final restoration retried a non-drain failure.");
        }
        catch (IOException) { Check(attempts == 1, "other capture failures are never retried"); }
        finalOrder.Clear();
        var disabledTarget = finalTarget with { Recovery = recovery with { State = recovery.State with { Flags = 0 } } };
        VerificationCampaignHost.FinalizeTargetsAsync([disabledTarget],
            _ => { finalOrder.Add("unexpected drain"); return Task.CompletedTask; }, t => Task.FromResult(t)).GetAwaiter().GetResult();
        Check(finalOrder.Count == 0, "disabled baseline observations do not cause a drain");
        var inspectedFault = false;
        try
        {
            VerificationCampaignHost.FinalizeTargetsAsync([finalTarget], _ => Task.FromException(new IOException("fixture restoration fault")),
                t => { inspectedFault = true; return Task.FromResult(t); }).GetAwaiter().GetResult();
            throw new Exception("Final restoration accepted a fault.");
        }
        catch (IOException) { Check(!inspectedFault, "final preparation failure stops without observation/retry/success"); }
        var root = Directory.CreateTempSubdirectory("qc-trace-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "qcachelab.pdb"), "plan-only fixture");
            File.WriteAllText(Path.Combine(root, "qcramdisk.pdb"), "plan-only fixture");
            var attribution = VerificationCampaignPlan.Create(new(new("Q:", DiskSpd: Environment.ProcessPath,
                BudgetMiB: 2048, Repeats: 1, TraceSymbols: root), "focused", "W:", FocusSuite: "ram-read-attribution"));
            Check(attribution.Count == 6 && attribution.Single(p => p.Options.Suite == "ram-read-attribution").MeasurementWindows == 3 &&
                attribution.Count(p => p.Options.TraceSymbols is not null) == 1, "attribution symbols reach only the selected phase and do not invalidate campaign base/correctness options");
            var journalPath = Path.Combine(root, "owned.trace.json");
            var journal = new TraceOwnership("QCTrace-" + Guid.NewGuid().ToString("N"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wpr.exe"), journalPath + ".etl", root,
                "RECORDING", DateTimeOffset.UtcNow, Environment.MachineName);
            VerificationTraceSession.ValidateOwnership(journal, journalPath);
            Reject(() => VerificationTraceSession.ValidateOwnership(journal with { Instance = "OtherSession" }, journalPath));
            Reject(() => VerificationTraceSession.ValidateOwnership(journal with { Machine = "OtherMachine" }, journalPath));
            Reject(() => VerificationTraceSession.ValidateOwnership(journal with { Etl = Path.Combine(root, "other.etl") }, journalPath));
            RunStorage.AtomicJson(journalPath, journal); // simulate worker death before finally
            var commands = new List<string[]>();
            Task<ProcessResult> Execute(string executable, string[] args, string prefix, bool allowMissing)
            {
                Check(executable == journal.Executable && args[^2] == "-instancename" && args[^1] == journal.Instance, "only the owned named trace can be stopped");
                commands.Add(args);
                if (args[0] == "-stop") File.WriteAllText(journal.Etl, "owned fixture");
                return Task.FromResult(new ProcessResult(0, "WPR recording is in progress", ""));
            }
            VerificationTraceSession.CleanupAsync(journalPath, Execute).GetAwaiter().GetResult();
            Check(commands.Count == 2 && JsonSerializer.Deserialize<TraceOwnership>(File.ReadAllText(journalPath))!.State == "FINISHED", "parent finalizes a recording after abrupt worker exit");
            VerificationTraceSession.CleanupAsync(journalPath, Execute).GetAwaiter().GetResult();
            Check(commands.Count == 2, "finalized cleanup is idempotent");
            var shortPath = Path.Combine(Path.GetTempPath(), journal.Instance);
            var stagedJournal = journal with { WorkDirectory = shortPath, State = "FINALIZING" };
            Reject(() => VerificationTraceSession.ValidateOwnership(stagedJournal with { WorkDirectory = root }, journalPath));
            Directory.CreateDirectory(shortPath);
            try
            {
                File.Delete(journal.Etl);
                File.WriteAllText(Path.Combine(shortPath, "trace.etl"), "trace stopped before worker death");
                RunStorage.AtomicJson(journalPath, stagedJournal);
                VerificationTraceSession.CleanupAsync(journalPath, (_, args, _, _) =>
                {
                    Check(args[0] == "-status", "already stopped staging trace must not be stopped again");
                    return Task.FromResult(new ProcessResult(0, "WPR is not recording", ""));
                }).GetAwaiter().GetResult();
                Check(File.ReadAllText(journal.Etl) == "trace stopped before worker death" &&
                    !Directory.Exists(shortPath) && File.Exists(journalPath + ".hash.json"),
                    "parent preserves short-path ETL and digest after worker death without recursive deletion");
            }
            finally { if (Directory.Exists(shortPath)) Directory.Delete(shortPath, true); }
            var deep = Directory.CreateDirectory(Path.Combine(root, new string('a', 100), new string('b', 100), new string('c', 80))).FullName;
            var deepJournal = Path.Combine(deep, "owned.trace.json");
            var started = false;
            Task<ProcessResult> NativePaths(string _, string[] args, string __, bool ___)
            {
                switch (args[0])
                {
                    case "-profiles": return Task.FromResult(new ProcessResult(0, "CPU CPU usage", ""));
                    case "-exportprofile":
                        Check(args[2].Length < 260 && !args[2].StartsWith(deep), "WPR export uses a short path outside deep evidence");
                        File.WriteAllText(args[2], "<WindowsPerformanceRecorder><SystemCollector><BufferSize Value='1024'/><Buffers Value='20'/></SystemCollector><Keyword Value='SampledProfile'/><Keyword Value='CSwitch'/><Keyword Value='ReadyThread'/></WindowsPerformanceRecorder>");
                        break;
                    case "-start":
                        Check(args[4].Length < 260 && File.Exists(deepJournal), "WPR start uses short staging with ownership persisted");
                        Check(args[1].EndsWith("profile.wprp!CPU.Verbose") && File.ReadAllText(args[1].Split('!')[0]).Contains("Value=\"128\""), "recording uses the actual bounded derived profile");
                        started = true; break;
                    case "-stop":
                        Check(args[1].Length < 260, "WPR stop uses short staging");
                        File.WriteAllText(args[1], "deep trace fixture"); break;
                    case "-status": return Task.FromResult(new ProcessResult(0, started ? "WPR recording is in progress\nBuffer Size (KB): 1024\nNumber of Buffers: 128\nEvents Lost: 0\n" : "WPR is not recording", ""));
                }
                return Task.FromResult(new ProcessResult(0, "", ""));
            }
            VerificationTraceSession.StartAsync(deepJournal, NativePaths).GetAwaiter().GetResult();
            VerificationTraceSession.CleanupAsync(deepJournal, NativePaths).GetAwaiter().GetResult();
            Check(File.ReadAllText(deepJournal + ".etl") == "deep trace fixture" && File.Exists(deepJournal + ".wprp"),
                "short native files are retained in original deep evidence directory");
            var oversizedProfile = System.Xml.Linq.XDocument.Parse("<Profiles><SystemCollector/><EventCollector/><EventCollector/></Profiles>");
            Reject(() => VerificationTraceSession.ConfigureDiagnosticProfile(oversizedProfile));
            var collectorStatus = "Buffer Size (KB): 1024\nNumber of Buffers: 128\nEvents Lost: 0\n";
            VerificationTraceSession.ValidateCollectorStatus(collectorStatus + collectorStatus, 2);
            Reject(() => VerificationTraceSession.ValidateCollectorStatus(collectorStatus, 2));
            Reject(() => VerificationTraceSession.ValidateCollectorStatus(collectorStatus.Replace("128", "20"), 1));
            Reject(() => VerificationTraceSession.ValidateCollectorStatus(collectorStatus.Replace("Lost: 0", "Lost: 1"), 1));
            var fixtureHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(deepJournal + ".etl")));
            string borrowed;
            using (var input = VerificationTraceInput.Open(deepJournal + ".etl", fixtureHash))
            {
                borrowed = input.FilePath;
                Check(borrowed.Length < 260 && File.ReadAllText(borrowed) == "deep trace fixture", "native analyzer receives a short verified clone");
            }
            Check(!File.Exists(borrowed) && File.Exists(deepJournal + ".etl"), "analysis disposes only its clone and preserves raw ETL");
            Reject(() => VerificationTraceInput.Open(deepJournal + ".etl", new string('0', 64)));
            var analysisJobPath = Path.Combine(deep, "worker-00002-ram-read-reference.job.json");
            var analysisJob = new WorkerJob("ram-read-reference", "Q:", Path.Combine(deep, "reply.json"),
                OraclePath: Path.Combine(deep, "owned.json"), Seconds: 10, ReferenceRepeats: 1, ReferenceKind: RamReadRunKind.Attribution);
            RunStorage.AtomicJson(analysisJobPath, analysisJob);
            RunStorage.AtomicJson(analysisJob.Reply + ".plan.json", new { Cases = RamReadReferencePlan.CasesFor(RamReadRunKind.Attribution, 1), Seconds = 10 });
            Reject(() => RamReadAttribution.ReadInputs(deep));
            File.WriteAllText(Path.Combine(deep, "FINISHED.txt"), "INCOMPLETE original verdict remains");
            Check(RamReadAttribution.ReadInputs(deep).Cases.Length == 3, "finalized failed collection can be analyzed without rerunning its benchmark");
            RunStorage.AtomicJson(analysisJobPath, analysisJob with { OraclePath = Path.Combine(root, "outside.json") });
            Reject(() => RamReadAttribution.ReadInputs(deep));
            RunStorage.AtomicJson(analysisJobPath, analysisJob);
            RunStorage.AtomicJson(analysisJob.Reply + ".plan.json", new { Cases = RamReadReferencePlan.CasesFor(RamReadRunKind.Attribution, 1), Seconds = 11 });
            Reject(() => RamReadAttribution.ReadInputs(deep));
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("Attribution and priority/affinity contracts passed (no driver access).");
        TraceToolSmoke.RunOptional();
    }

    private static void RunTreeDecisionContracts(Action<bool, string> check, Action<Action> reject)
    {
        static void RejectOptions(Action action)
        {
            try { action(); } catch (ArgumentException) { return; }
            throw new Exception("Expected tree-decision option rejection.");
        }
        var options = new VerificationOptions("Q:", "tree-decision", BudgetMiB: 2048, Repeats: 3);
        VerificationPlan.Validate(options);
        var cases = VerificationPlan.TreeDecision(options);
        check(cases.Count == 15 && cases.Select(test => test.Id).Distinct().Count() == 15 &&
            cases.GroupBy(test => test.Repeat).All(group => group.Count() == 5 && group.Select(test => test.Position).Order().SequenceEqual([1, 2, 3, 4, 5])) &&
            cases.GroupBy(test => test.Repeat).Select(group => string.Join(",", group.OrderBy(test => test.Position).Select(test => test.Arm))).Distinct().Count() == 3,
            "tree-decision creates five fixed arms in three rotated repetitions with immutable unique IDs");
        check(cases.Select(test => test.Arm).Distinct().Count() == 5 &&
            cases.Count(test => !test.CacheEnabled) == 3 &&
            cases.Where(test => test.CacheEnabled).Select(test => (test.CopyFlags, test.BatchKiB, test.Parallelism)).Distinct().Count() == 4,
            "tree-decision compares a cache-off control with flags 7/15 at 256x2 and 1024x4");
        var selected = VerificationPlan.TreeDecision(options with { CaseFilter = "r02-p01" });
        check(selected.Count == 1 && selected[0].Repeat == 2 && selected[0].Position == 1,
            "tree-decision case filter records a focused subset without changing case identity");
        RejectOptions(() => VerificationPlan.Validate(options with { Repeats = 2 }));
        RejectOptions(() => VerificationPlan.Validate(options with { BudgetMiB = 1024 }));
        RejectOptions(() => VerificationPlan.Validate(options with { CaseFilter = "no-such-case" }));

        var cached = cases.First(test => test.Arm == "flags7-b256-p2");
        var cacheOptions = new CacheOptions(Drain: DrainAlgorithm.Idle, RetainWrites: true, PromoteOnRead: true,
            BatchKiB: cached.BatchKiB, Parallelism: cached.Parallelism);
        var state = new WriteCacheState(1, 0, 1UL << 30, 2048UL << 20, 1UL << 30, 0, 0, 1UL << 30,
            0, 0, 0, 0, 0, 0, 0, 0) { Options = cacheOptions, Instance = 77, Generation = 9 };
        var perf = new CachePerformance(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var attribution = new CacheAttribution(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var diagnostics = new CacheDiagnostics(0, 0, 0, 0, 0, 0, 0, 0, 0) { Attribution = attribution, CopyFlags = 7 };
        var snapshot = new CacheLayoutSnapshot(state, perf, diagnostics);
        TreeDecisionEvidenceContract.ValidateStart(cached, snapshot);
        reject(() => TreeDecisionEvidenceContract.ValidateStart(cached,
            snapshot with { Diagnostics = diagnostics with { CopyFlags = 15 } }));
        reject(() => TreeDecisionEvidenceContract.ValidateStart(cached,
            snapshot with { Performance = perf with { TimingEnabled = 1 } }));
        reject(() => TreeDecisionEvidenceContract.ValidateStart(cached,
            snapshot with { Diagnostics = diagnostics with { Attribution = null } }));

        var now = DateTimeOffset.UtcNow;
        MemorySnapshot Memory(int second) => new(now.AddSeconds(second), 16L << 30, 8L << 30, 0, 0, 0, 0, 2L << 30);
        var treeSpec = ComparisonData.TreeSpec();
        var settle = new SettleResult(120, 0, true, "IOCTL_DISK_PERFORMANCE");
        TreeCacheSnapshot Cache(ulong reads, ulong writes, ulong flushes) => new(state, perf,
            diagnostics with { Attribution = new(reads, writes, flushes, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0) });
        var beforeCreate = Cache(100, 200, 30);
        var afterCreate = Cache(101, 202, 30);
        var afterCreateSettle = Cache(103, 202, 30);
        var afterCopy = Cache(105, 205, 31);
        var afterCopySettle = Cache(107, 205, 31);
        var afterRead = Cache(110, 205, 31);
        var afterDelete = Cache(111, 206, 31);
        var tree = new TreeResult(treeSpec.Count, treeSpec.Sum(file => (long)file.Bytes), 1, 1, 1, 1, settle, settle, true,
            Memory(0), Memory(1), Memory(2), Memory(3), Memory(4), Memory(5), Memory(6),
            beforeCreate, afterCreate, afterCreateSettle, afterCreateSettle, afterCopy, afterCopySettle,
            afterCopySettle, afterRead, afterRead, afterDelete);
        var completeEvidence = new TreeDecisionEvidence(cached, snapshot, tree, snapshot);
        TreeDecisionEvidenceContract.Validate(completeEvidence);
        check(completeEvidence.LowerIoAttempts.Create == new TreePhaseIoAttempts(1, 2, 0) &&
            completeEvidence.LowerIoAttempts.CreateSettle == new TreePhaseIoAttempts(2, 0, 0) &&
            completeEvidence.LowerIoAttempts.Copy == new TreePhaseIoAttempts(2, 3, 1) &&
            completeEvidence.LowerIoAttempts.CopySettle == new TreePhaseIoAttempts(2, 0, 0) &&
            completeEvidence.LowerIoAttempts.Read == new TreePhaseIoAttempts(3, 0, 0) &&
            completeEvidence.LowerIoAttempts.Delete == new TreePhaseIoAttempts(1, 1, 0),
            "tree-decision reports separate create/copy/read/delete and settle lower-I/O attempt deltas");
        var roundTrip = JsonSerializer.Deserialize<TreeDecisionEvidence>(JsonSerializer.Serialize(completeEvidence))!;
        TreeDecisionEvidenceContract.Validate(roundTrip);
        check(roundTrip.LowerIoAttempts.Read == new TreePhaseIoAttempts(3, 0, 0),
            "tree-decision case evidence round-trips all phase snapshots and derived lower-I/O deltas");
        reject(() => TreeDecisionEvidenceContract.Validate(new(cached, snapshot,
            tree with { AfterCopy = settle with { IdleObserved = false } }, snapshot)));
        reject(() => TreeDecisionEvidenceContract.Validate(new(cached, snapshot,
            tree with { AfterReadCache = Cache(106, 205, 31) }, snapshot)));
        check(CacheLayoutEvidence.IsQuiet(snapshot, snapshot) &&
            !CacheLayoutEvidence.IsQuiet(snapshot, snapshot with { State = state with { DirtyBytes = 4096 } }) &&
            !CacheLayoutEvidence.IsQuiet(snapshot, snapshot with
            {
                Diagnostics = diagnostics with
                { Attribution = attribution with { LowerWriteAttempts = attribution.LowerWriteAttempts + 1 } }
            }), "quiet-cache oracle requires zero dirty bytes and unchanged lower-I/O counters across the wait");
        Console.WriteLine("Tree-decision plan, start-state, counter coverage and hash/settle contracts passed (no driver access).");
    }

    private static void RunReadTailDecisionContracts(Action<bool, string> check, Action<Action> reject)
    {
        static void RejectOptions(Action action)
        {
            try { action(); } catch (ArgumentException) { return; }
            throw new Exception("Expected read-tail option rejection.");
        }

        var options = new VerificationOptions("Q:", "read-tail-decision", BudgetMiB: 2048, Repeats: 3);
        VerificationPlan.Validate(options);
        var cases = ReadTailDecision.Cases(options);
        check(cases.Count == 24 && cases.Select(test => test.Id).Distinct().Count() == 24 &&
            cases.GroupBy(test => test.Repeat).All(group => group.Count() == 8 &&
                group.Select(test => test.Position).SequenceEqual([1, 1, 2, 2, 3, 3, 4, 4]) &&
                group.Select(test => (test.Workload, test.CopyFlags)).Distinct().Count() == 8),
            "read-tail plan records 24 unique cases with paired flags and rotated workload order");
        check(cases.Select(test => test.Workload).Distinct().Count() == 4 &&
            cases.GroupBy(test => test.Workload).All(group => group.Count() == 6 &&
                group.Select(test => test.CopyFlags).Order().SequenceEqual([7UL, 7UL, 7UL, 15UL, 15UL, 15UL])) &&
            cases.All(test => test.OperationCount == 100_000 && !test.Writes || test.FileBytes / test.BlockBytes >= test.OperationCount),
            "read-tail plan fixes cold/resident/write shapes, 100,000 operations and fitting unique write space");
        foreach (var test in cases) ReadTailDecision.ValidateCase(test);
        var write = cases.First(test => test.Workload == "foreground-write");
        var writeOffsets = ReadTailDecision.AddressOffsets(write);
        check(writeOffsets.Distinct().Count() == write.OperationCount &&
            writeOffsets.All(offset => offset >= 0 && offset % write.BlockBytes == 0 && offset + write.BlockBytes <= write.FileBytes),
            "foreground-write offsets are aligned, in-bounds and unique for the durable oracle");
        check(ReadTailDecision.AddressSequenceSha256(write) == ReadTailDecision.AddressSequenceSha256(write),
            "read-tail address sequence has a stable recorded SHA-256");
        RejectOptions(() => VerificationPlan.Validate(options with { Repeats = 2 }));
        RejectOptions(() => VerificationPlan.Validate(options with { BudgetMiB = 1024 }));
        RejectOptions(() => VerificationPlan.Validate(options with { DiskSpd = "fixture" }));
        RejectOptions(() => VerificationPlan.Validate(options with { CaseFilter = "tail-r01" }));

        var ticks = Enumerable.Repeat(Math.Max(1, System.Diagnostics.Stopwatch.Frequency / 100), write.OperationCount).ToArray();
        ticks[^1] = Math.Max(1, System.Diagnostics.Stopwatch.Frequency);
        ticks[^2] = ticks[^3] = Math.Max(1, System.Diagnostics.Stopwatch.Frequency * 3 / 10);
        var result = ReadTailDecision.Summarize(write, ticks, 12.5);
        ReadTailDecision.ValidateResult(write, result);
        check(result.CompletedOperations == 100_000 && result.Bytes == 409_600_000 &&
            result.Over50Milliseconds == 3 && result.Over100Milliseconds == 3 &&
            result.Over250Milliseconds == 3 && result.Over500Milliseconds == 1 &&
            result.LatencyTicks.Length == 100_000,
            "read-tail percentile and exact latency-threshold counts derive from every per-operation sample");
        reject(() => ReadTailDecision.ValidateResult(write, result with { Over100Milliseconds = 0 }));
        reject(() => ReadTailDecision.ValidateResult(write, result with { LatencyTicks = ticks[..^1] }));
        var workerJob = new WorkerJob("read-tail", "Q:", "reply", TailDecision: write);
        check(JsonSerializer.Deserialize<WorkerJob>(JsonSerializer.Serialize(workerJob))!.TailDecision == write,
            "immutable read-tail case crosses the owned-worker JSON boundary");

        var cacheOptions = new CacheOptions(Drain: DrainAlgorithm.Idle, RetainWrites: true,
            PromoteOnRead: true, BatchKiB: 256, Parallelism: 2);
        var state = new WriteCacheState(1, 0, 16UL << 30, 2UL << 30, 2UL << 30,
            0, 0, 2UL << 30, 0, 0, 0, 0, 0, 0, 0, 0)
        { Options = cacheOptions, Instance = 77, Generation = 9 };
        var performance = new CachePerformance(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        CacheLayoutSnapshot Snapshot(ulong hits, ulong misses, ulong accepted, ulong reads, ulong writes, ulong flushes, ulong flags) =>
            new(state with { ReadHitBytes = hits, ReadMissBytes = misses, AcceptedBytes = accepted }, performance,
                new CacheDiagnostics(0, 0, 0, 0, 0, 0, 0, 0, 0)
                {
                    Attribution = new(reads, writes, flushes, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
                    CopyFlags = flags
                });
        var cold = cases.First(test => test.Workload == "cold-random");
        ReadTailDecision.ValidateEvidence(new(cold, Snapshot(0, 0, 0, 100, 100, 20, cold.CopyFlags), result with { CaseId = cold.Id,
            Bytes = 409_600_000, AddressSequenceSha256 = ReadTailDecision.AddressSequenceSha256(cold) },
            Snapshot(0, 4096, 0, 101, 100, 20, cold.CopyFlags)));
        var resident = cases.First(test => test.Workload == "resident-random");
        var residentResult = ReadTailDecision.Summarize(resident, ticks, 12.5);
        ReadTailDecision.ValidateEvidence(new(resident, Snapshot(0, 0, 0, 100, 100, 20, resident.CopyFlags), residentResult,
            Snapshot((ulong)residentResult.Bytes, 0, 0, 100, 100, 20, resident.CopyFlags)));
        var accepted = cases.First(test => test.Workload == "foreground-write");
        var writeResult = ReadTailDecision.Summarize(accepted, ticks, 12.5);
        ReadTailDecision.ValidateEvidence(new(accepted, Snapshot(0, 0, 0, 100, 100, 20, accepted.CopyFlags), writeResult,
            Snapshot(0, 0, (ulong)writeResult.Bytes, 100, 100, 20, accepted.CopyFlags)));
        var throttled = Snapshot(0, 0, (ulong)writeResult.Bytes, 100, 100, 20, accepted.CopyFlags);
        reject(() => ReadTailDecision.ValidateEvidence(new(accepted, Snapshot(0, 0, 0, 100, 100, 20, accepted.CopyFlags),
            writeResult, throttled with { State = throttled.State with { ThrottleWaits = 1 } })));
        reject(() => ReadTailDecision.ValidateEvidence(new(resident, Snapshot(0, 0, 0, 100, 100, 20, resident.CopyFlags), residentResult,
            Snapshot((ulong)residentResult.Bytes, 1, 0, 101, 100, 20, resident.CopyFlags))));
        ReadTailEvidence MakeEvidence(ReadTailCase test)
        {
            var measured = ReadTailDecision.Summarize(test, ticks, 12.5);
            var before = Snapshot(0, 0, 0, 100, 100, 20, test.CopyFlags);
            var after = test.Workload switch
            {
                "cold-random" => Snapshot(0, 4096, 0, 101, 100, 20, test.CopyFlags),
                "resident-random" or "resident-sequential" => Snapshot((ulong)measured.Bytes, 0, 0, 100, 100, 20, test.CopyFlags),
                _ => Snapshot(0, 0, (ulong)measured.Bytes, 100, 100, 20, test.CopyFlags)
            };
            return new(test, before, measured, after);
        }
        var aggregate = ReadTailDecision.AggregateEvidence(cases.Select(MakeEvidence));
        var rendered = ReadTailDecision.RenderSummary(aggregate);
        check(aggregate.CaseCount == 24 && aggregate.Cases.Count == 24 && aggregate.Pairs.Count == 12 &&
            rendered.Contains("Flags 15 minus flags 7", StringComparison.Ordinal) &&
            rendered.Contains(">50/>100/>250/>500 ms", StringComparison.Ordinal),
            "read-tail final report requires the complete paired matrix and retains exact threshold counts");
        reject(() => ReadTailDecision.AggregateEvidence(cases.Take(23).Select(MakeEvidence)));
        Console.WriteLine("Read-tail plan, operation samples, address and cache-accounting contracts passed (no driver access).");
    }

    private static void RunDrainFlushContracts(Action<bool, string> check, Action<Action> reject)
    {
        var options = new VerificationOptions("Q:", "drain-flush-decision", BudgetMiB: 2048, Repeats: 3);
        DrainFlushDecision.ValidateOptions(options);
        VerificationPlan.Validate(options);
        var cases = DrainFlushDecision.Cases(options);
        check(cases.Count == 6 && cases.Select(test => test.Id).Distinct().Count() == 6 &&
            cases.GroupBy(test => test.Repeat).All(group => group.Count() == 2 &&
                group.Select(test => test.Position).SequenceEqual([1, 2]) &&
                group.Select(test => (test.BatchKiB, test.Parallelism)).Distinct().Count() == 2),
            "explicit-drain plan alternates both batch settings across three complete pairs");
        const ulong instance = 78, errors = 0, seedBytes = 512UL << 20, baselineDrained = 100;
        var flags = 1U | 32U | 256U | 512U;
        CacheOptions Policy(DrainFlushCase test) => new(WritePercent: 100, RetainWrites: true, PromoteOnRead: true,
            Drain: DrainAlgorithm.Deferred, LowPercent: 40, HighPercent: 80, MaxDirtyAgeMs: 3_600_000,
            IdleMs: 250, BatchKiB: test.BatchKiB, Parallelism: test.Parallelism);
        WriteCacheState State(DrainFlushCase test, ulong accepted, ulong drained, ulong dirty, ulong lowerWrites) =>
            new(flags, 0, 16UL << 30, 2UL << 30, 2UL << 30, dirty, 0, 2UL << 30,
                dirty / 4096, accepted, drained, 0, 0, errors, 0, dirty)
            {
                Options = Policy(test), Instance = instance, Generation = 9, LowerWrites = lowerWrites
            };
        CachePerformance Performance(ulong timing = 0, ulong batches = 0, ulong drainBytes = 0) =>
            new(1_000_000, timing, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                batches, drainBytes, 0, 0);
        CacheAttribution Attribution(ulong writes, ulong flushes, ulong reads = 0) => new(reads, writes, flushes,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        CacheDiagnostics Diagnostics(ulong writes, ulong flushes) => new(0, 0, 0, 0, 0, 0, 0, 0, 0)
            { Attribution = Attribution(writes, flushes) };

        var epoch = new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
        var verified = new List<DrainFlushEvidence>();
        foreach (var test in cases)
        {
            var before = State(test, 0, baselineDrained, 0, 0);
            var seeded = State(test, seedBytes, baselineDrained, seedBytes, 0);
            var attribution = Attribution(10, 2);
            var seed = new DrainSeedResult(108000 + test.Repeat, checked((int)seedBytes), seedBytes,
                before, seeded, Performance(), Performance(), attribution, attribution);
            var controlBefore = State(test, seedBytes, baselineDrained, seedBytes, 0);
            var controlAfter = State(test, seedBytes, baselineDrained + seedBytes, 0, 1);
            var control = new DrainFlushControlResult(epoch.AddSeconds(1.2), epoch.AddSeconds(1.5), 300, 1000,
                controlBefore, controlAfter, Performance(), Performance(0, 1, seedBytes),
                Diagnostics(10, 2), Diagnostics(11, 3));
            var process = new ProcessIdentity(45 + test.Repeat, DateTime.SpecifyKind(
                epoch.AddSeconds(0.8).UtcDateTime, DateTimeKind.Utc));
            var seedCompleted = epoch.AddSeconds(0.5);
            var ready = epoch.AddSeconds(1);
            var release = epoch.AddSeconds(1.1);
            var metrics = DrainFlushDecision.ValidateEvidence(test, seed, process,
                seedCompleted, ready, release, control, instance, errors);
            check(Math.Abs(metrics.DurationMilliseconds - 300) < 0.01 && metrics.DrainBatches == 1 &&
                metrics.DriverDrainBytes == seedBytes && metrics.LowerWriteAttempts == 1 &&
                metrics.LowerFlushAttempts == 1 && metrics.PendingBytesAfter == 0,
                $"explicit-drain evidence times the actual flush call and verifies writes, flushes, batches and a clean seed: {metrics}");
            verified.Add(new(test, seed, true, process, seedCompleted, ready, release, control,
                "worker.process.json", "seed-verification.json", metrics));
        }
        var first = verified[0];
        reject(() => DrainFlushDecision.ValidateEvidence(first.Case, first.Seed, first.WorkerProcess,
            first.SeedCompletedUtc, first.WorkerReadyUtc, first.ReleaseUtc,
            first.Control with { After = first.Control.After with
                { DirtyBytes = 8192, AcceptedBytes = seedBytes + 8192 } }, instance, errors));
        reject(() => DrainFlushDecision.ValidateEvidence(first.Case, first.Seed, first.WorkerProcess,
            first.SeedCompletedUtc, first.WorkerReadyUtc, first.ReleaseUtc,
            first.Control with { PerformanceAfter = Performance(1, 1, seedBytes) }, instance, errors));
        reject(() => DrainFlushDecision.ValidateEvidence(first.Case, first.Seed, first.WorkerProcess,
            first.SeedCompletedUtc, first.WorkerReadyUtc, first.ReleaseUtc,
            first.Control with { DiagnosticsAfter = Diagnostics(11, 2) }, instance, errors));
        var readAttemptSeed = first.Seed with
        {
            AttributionAfter = Attribution(writes: 10, flushes: 2, reads: 1)
        };
        reject(() => DrainFlushDecision.ValidateEvidence(first.Case, readAttemptSeed, first.WorkerProcess,
            first.SeedCompletedUtc, first.WorkerReadyUtc, first.ReleaseUtc, first.Control, instance, errors));
        reject(() => DrainFlushDecision.ValidateEvidence(first.Case, first.Seed, first.WorkerProcess,
            first.SeedCompletedUtc, first.WorkerReadyUtc, first.ReleaseUtc,
            first.Control with { Before = first.Control.Before with
                { DirtyBytes = seedBytes + 4096, AcceptedBytes = seedBytes + 4096 } }, instance, errors));
        var clockAdjustedControl = first.Control with
        {
            CompletedUtc = first.Control.EnteredUtc.AddMilliseconds(-100)
        };
        var monotonicMetrics = DrainFlushDecision.ValidateEvidence(first.Case, first.Seed,
            first.WorkerProcess, first.SeedCompletedUtc, first.WorkerReadyUtc, first.ReleaseUtc,
            clockAdjustedControl, instance, errors);
        check(Math.Abs(monotonicMetrics.DurationMilliseconds - 300) < 0.01,
            "explicit Flush duration uses monotonic ticks when wall-clock timestamps move backwards");
        var summary = DrainFlushDecision.BuildSummary(verified);
        check(summary.CaseCount == 6 && summary.Rows.Count == 2 &&
            DrainFlushDecision.RenderSummary(summary).Contains("monotonic timer", StringComparison.Ordinal),
            "explicit-drain report requires both complete three-case arms and the timestamp contract");
        reject(() => DrainFlushDecision.BuildSummary(verified[..^1]));
        reject(() => DrainFlushDecision.BuildSummary(verified.Select((row, index) => index == 0
            ? row with { SeedVerified = false } : row).ToArray()));
        Console.WriteLine("Explicit-drain plan, ready/start handshake timestamps, timing-off counters and full-matrix contracts passed (no driver access).");
    }

    /// <summary>Plan 120: RAM write reference with memory-copy probes, and the cached read attribution suite.</summary>
    private static void ReadMissContracts(Action<bool, string> check, Action<Action> reject)
    {
        var options = new VerificationOptions("Q:", "cache-read-misses", BudgetMiB: 2048, Repeats: 3, DiskSpd: Environment.ProcessPath);
        VerificationPlan.Validate(options);
        var cases = VerificationPlan.Performance(options);
        check(cases.Count == 24 && cases.Select(c => c.Id).Distinct().Count() == 24 &&
            cases.All(c => !c.Timing && c.CopyFlags is 7 or 15) &&
            cases.GroupBy(c => (c.Repeat, c.Workload, c.WarmResident)).All(g => g.Count() == 2 &&
                g.First().CopyFlags == (g.Key.Repeat % 2 == 1 ? 7UL : 15UL)),
            "read-miss matrix alternates same-build modes with cold targets and cached controls");
        var campaign = VerificationCampaignPlan.Create(new(options with { Suite = "quick" }, "experiment", "W:", FocusSuite: "cache-read-misses"));
        check(campaign.Count == 1 && campaign[0].ExpectedCases.Count == 24, "read-miss experiment composes without extra phases");
        CacheReadMisses.ComparisonWindow[] Windows(Func<PerformanceCase, double> speed) => cases.Select(c =>
            new CacheReadMisses.ComparisonWindow(c, speed(c), 1, 10, 4096, c.WarmResident ? 0UL : 4096UL, c.WarmResident ? 0UL : 1UL)).ToArray();
        double Win(PerformanceCase c) => c.WarmResident ? 1000 : c.CopyFlags == 15 ? 200 : 100;
        check(CacheReadMisses.Evaluate(Windows(Win)).Verdict == "ACCEPT_SCREEN", "both read targets must win");
        check(CacheReadMisses.Evaluate(Windows(c => c.WarmResident && c.CopyFlags == 15 ? 900 : Win(c))).Verdict ==
            "CONTROL_LOSS_REVISION_OR_REJECT", "cached control loss prevents read-miss acceptance");
        check(CacheReadMisses.Evaluate(Windows(c => !c.WarmResident && c.Workload == "random-read" ? 100 : Win(c))).Verdict == "REJECT",
            "one winning shape cannot hide a losing read target");
        reject(() => CacheReadMisses.Evaluate(Windows(Win).Skip(1).ToArray()));
        reject(() => CacheReadMisses.Evaluate([.. Windows(Win), Windows(Win)[0]]));
        check(CacheReadMisses.Evaluate(Windows(Win).Where(w => w.Case.Repeat < 3).ToArray()).Verdict == "INCOMPLETE_FOR_ACCEPTANCE",
            "two complete read-miss repetitions are not acceptance");
        JsonElement Snapshot(ulong flags, ulong hit, ulong miss, ulong reads) => JsonSerializer.SerializeToElement(new
        {
            State = new { Operational = true, Instance = 1, Generation = 2, PayloadCapacity = 1024, Errors = 0, LastError = 0,
                ReadHitBytes = hit, ReadMissBytes = miss },
            Performance = new { TimingEnabled = 0 },
            Diagnostics = new { CopyFlags = flags, Attribution = new { LowerReadAttempts = reads } }
        });
        var cold = cases.First(c => !c.WarmResident && c.Workload == "random-read");
        var score = new DiskSpdScore(4096, 1, 1, 1, 1, 1, null, 2, 3);
        CacheReadMisses.Window(cold, Snapshot(7, 0, 0, 0), Snapshot(7, 0, 4096, 1), score);
        reject(() => CacheReadMisses.Window(cold, Snapshot(7, 0, 0, 0), Snapshot(15, 0, 4096, 1), score));
        reject(() => CacheReadMisses.Window(cold, Snapshot(7, 0, 0, 0), Snapshot(7, 0, 2048, 1), score));
        reject(() => CacheReadMisses.Window(cold, Snapshot(7, 0, 0, 0), Snapshot(7, 4096, 0, 0), score));
        reject(() => CacheReadMisses.Window(cold, Snapshot(7, 0, 0, 0), Snapshot(7, 0, 4096, 1), score with { ReadMaxMilliseconds = null }));
        var warm = cases.First(c => c.WarmResident && c.Workload == "random-read");
        reject(() => CacheReadMisses.Window(warm, Snapshot(7, 0, 0, 0), Snapshot(7, 4096, 512, 1), score));
    }

    private static void RunPlan120Contracts(Action<bool, string> check, Action<Action> reject)
    {
        var writes = RamReadReferencePlan.CasesFor(RamReadRunKind.WriteReference, 3);
        check(RamReadReferencePlan.KindFor("ram-write-reference") == RamReadRunKind.WriteReference && VerificationPlan.IsRamReadSuite("ram-write-reference"),
            "the write reference runs on owned RAM disks");
        check(writes.Count == 36 && writes.Select(c => c.Id).Distinct().Count() == 36 && writes.All(c => c.Write || c.Probe == "runtime"),
            "36 immutable write-reference windows; the only read is the runtime copy out of cold memory");
        var probes = writes.Where(c => c.Probe is not null).ToArray();
        var disk = writes.Where(c => c.Probe is null).ToArray();
        check(probes.Length == 24 && probes.GroupBy(c => (c.Write, c.Probe, c.Threads)).All(g => g.Count() == 3) &&
            probes.All(c => c.Threads is 1 or 4 && (c.Write || c.Probe != "streaming")), "eight balanced memory-copy probes per repetition");
        check(disk.Length == 12 && disk.GroupBy(c => (c.QueueDepth, c.Threads)).All(g => g.Count() == 3) &&
            disk.All(c => c.Write && c.SchedulingControl && c.Access == QueueCache.Operations.ManagedDisks.RamAccess.Direct && c.Interleaved == (c.Threads == 4)),
            "one- and four-submitter Direct-disk write shapes, each repeated three times");
        check(writes.GroupBy(c => c.Repeat).All(g => g.TakeWhile(c => c.Probe is not null).Count() == 8),
            "each repetition copies memory before its owned disk exists");
        var arguments = RamReadReferencePlan.Arguments(disk.First(c => c.Threads == 4), 10);
        check(arguments.Contains("-w100") && arguments.Contains("-Z1M") && arguments.Contains("-s4M") && arguments.Contains("-T1M") &&
            !arguments.Contains("-w0") && !RamReadReferencePlan.Arguments(RamReadReferencePlan.CasesFor(RamReadRunKind.Reference, 1)[0], 10).Contains("-Z1M"),
            "write windows use a hot fixed source buffer; read windows are unchanged");
        var span = new XElement("TimeSpan", new XElement("DisableAffinity", "false"), new XElement("Duration", 10), new XElement("Warmup", 3),
            new XElement("Targets", new XElement("Target", new XElement("BlockSize", 1048576), new XElement("RequestCount", 8),
                new XElement("ThreadsPerFile", 1), new XElement("WriteRatio", 100), new XElement("MaxFileSize", 1073741824),
                new XElement("IOPriority", 3), new XElement("DisableOSCache", "true"), new XElement("UseLargePages", "false"),
                new XElement("StrideSize", 1048576), new XElement("ThreadStride", 0), new XElement("InterlockedSequential", "false"))));
        var xml = new XElement("Results", new XElement("Profile", new XElement("TimeSpans", span))).ToString();
        var q8 = disk.First(c => c.QueueDepth == 8 && c.Threads == 1);
        RamReadReferencePlan.ValidateProfile(q8, xml, 10);
        reject(() => RamReadReferencePlan.ValidateProfile(q8 with { Write = false }, xml, 10));
        MemoryCopyProbe.Validate(new("streaming", true, 4, 100L << 20, 5.01, 19.96));
        foreach (var invalid in new MemoryCopyResult[] { new("streaming", true, 4, 0, 5, 0), new("temporal", true, 1, (1L << 20) + 1, 5, 1),
            new("runtime", false, 1, 1L << 20, 3, 1), new("unknown", true, 1, 1L << 20, 5, 1) })
            reject(() => MemoryCopyProbe.Validate(invalid));

        var options = new VerificationOptions("Q:", "cache-read-reference", BudgetMiB: 2048, Repeats: 3, DiskSpd: Environment.ProcessPath);
        var reads = VerificationPlan.Performance(options);
        check(reads.Count == 18 && reads.Select(c => c.Id).Distinct().Count() == 18 &&
            reads.GroupBy(c => (c.QueueDepth, c.Threads, c.Timing)).All(g => g.Count() == 3) &&
            reads.All(c => c.Workload == "sequential-read" && c.Resident && c.WarmResident && c.Layout == CacheLayoutStage.None),
            "cached read reference: three resident shapes, timing off and on, three repetitions");
        foreach (var suite in new[] { "ram-write-reference", "cache-read-reference" })
        {
            var phase = VerificationCampaignPlan.Create(new(new("Q:", BudgetMiB: 2048, Repeats: 3, DiskSpd: Environment.ProcessPath), "experiment", "W:", FocusSuite: suite));
            check(phase.Count == 1 && phase[0].Role == CampaignTargetRole.Performance &&
                phase[0].MeasurementWindows == (suite == "ram-write-reference" ? 36 : 18), "plan 120 suites run as one-phase experiments");
            VerificationPlan.Validate(options with { Suite = suite, Repeats = 1 });
            foreach (var invalid in new[] { options with { Suite = suite, Repeats = 4 }, options with { Suite = suite, BudgetMiB = 1024 } })
                try { VerificationPlan.Validate(invalid); throw new Exception("plan 120 suite accepted invalid options: " + suite); }
                catch (ArgumentException) { }
        }

        static JsonElement Snapshot(ulong timing, ulong acquires, ulong wait, ulong hold, ulong queue, ulong queued, ulong caller, ulong offload) =>
            JsonSerializer.SerializeToElement(new
            {
                Performance = new { Frequency = 10_000_000UL, TimingEnabled = timing, LockAcquires = acquires, LockWaitTicks = wait,
                    LockHoldTicks = hold, QueueWaitTicks = queue, QueuedRequests = queued },
                Diagnostics = new { CallerPath = new { Reads = caller }, CopyOffloadReads = (ulong?)offload }
            });
        var timed = reads.First(c => c.Timing && c.QueueDepth == 8);
        var score = new DiskSpdScore(1000L << 20, 1000, 10, 100, 100, null, null, null, null);
        var row = CacheReadReference.Row(timed, Snapshot(1, 0, 0, 0, 0, 0, 5, 7), Snapshot(1, 3000, 20_000, 10_000, 50_000, 900, 5, 907), score);
        check(Math.Abs(row.LockAcquiresPerRead!.Value - 3) < 1e-9 && Math.Abs(row.LockWaitMicrosecondsPerRead!.Value - 2) < 1e-9 &&
            Math.Abs(row.LockHoldMicrosecondsPerRead!.Value - 1) < 1e-9 && Math.Abs(row.QueueWaitMicrosecondsPerRead!.Value - 5) < 1e-9 &&
            Math.Abs(row.CopyOffloadShare - 0.9) < 1e-9 && row.CallerPathShare == 0, "attribution divides counter deltas by completed reads");
        var untimed = reads.First(c => !c.Timing && c.QueueDepth == 1);
        check(CacheReadReference.Row(untimed, Snapshot(0, 0, 0, 0, 0, 0, 0, 0), Snapshot(0, 0, 0, 0, 0, 0, 990, 0), score) is
            { LockWaitMicrosecondsPerRead: null, CallerPathShare: 0.99 }, "timing-off windows carry no timing values");
        reject(() => CacheReadReference.Row(timed, Snapshot(0, 0, 0, 0, 0, 0, 0, 0), Snapshot(0, 0, 0, 0, 0, 0, 0, 0), score));
        reject(() => CacheReadReference.Row(timed, Snapshot(1, 5, 0, 0, 0, 0, 0, 0), Snapshot(1, 4, 0, 0, 0, 0, 0, 0), score));
        reject(() => CacheReadReference.Row(timed, Snapshot(1, 0, 0, 0, 0, 0, 0, 0), JsonSerializer.SerializeToElement(new
        {
            Performance = new { Frequency = 10_000_000UL, TimingEnabled = 1UL, LockAcquires = 1UL, LockWaitTicks = 1UL, LockHoldTicks = 1UL, QueueWaitTicks = 1UL, QueuedRequests = 1UL },
            Diagnostics = new { CallerPath = new { Reads = 1UL }, CopyOffloadReads = (ulong?)null }
        }), score));
        reject(() => CacheReadReference.Row(timed, Snapshot(1, 0, 0, 0, 0, 0, 0, 0), Snapshot(1, 0, 0, 0, 0, 0, 0, 0), score with { Bytes = 999 }));
        RunBookkeepingContracts(check, reject);
        RunStressContracts(check, reject);
    }

    /// <summary>Plan 121 driver-independent comparisons with other RAM disks and RAM caches.</summary>
    private static void RunComparisonContracts(Action<bool, string> check, Action<Action> reject)
    {
        var ram = ComparisonPlan.CasesFor("compare-ram", 3);
        var ramWindows = ram.Where(c => c.Kind == "diskspd").ToArray();
        check(ram.Select(c => c.Id).Distinct().Count() == ram.Count && ramWindows.Length == 36 &&
            ramWindows.GroupBy(c => ComparisonPlan.Shape(c.Id)).Count() == 12 && ramWindows.GroupBy(c => ComparisonPlan.Shape(c.Id)).All(g => g.Count() == 3),
            "compare-ram: twelve read and write shapes, each with three immutable repetitions");
        check(ram.Take(3).Select(c => c.Kind).SequenceEqual(["canary", "memory", "prepare"]) && ram[^2].Kind == "integrity" && ram[^1].Kind == "memory" &&
            ram.Count(c => c.Kind == "tree") == 3 && ram.Count(c => c.Kind == "memory") == 5,
            "compare-ram starts with the canary and the test file, ends with the byte check and records memory around the fill");
        check(ramWindows.All(c => c.Seconds == 10 && c.Buffering == ComparisonPlan.Unbuffered && c.File == "test-1g.dat"),
            "RAM-disk windows are ten-second unbuffered windows on the 1 GiB test file");
        var cache = ComparisonPlan.CasesFor("compare-cache", 3);
        var cacheWindows = cache.Where(c => c.Kind == "diskspd").ToArray();
        check(cache.Select(c => c.Id).Distinct().Count() == cache.Count && cacheWindows.Length == 47 &&
            cacheWindows.Count(c => c.Buffering == ComparisonPlan.Buffered) == 12 && cacheWindows.Count(c => c.Buffering == ComparisonPlan.WriteThrough) == 6 &&
            cache.Count(c => c.Kind == "absorb") == 3 && cache.Count(c => c.Kind == "flush") == 3 && cache.Count(c => c.Kind == "sustained") == 2 &&
            cache.Count(c => c.Kind == "tree") == 2 && cacheWindows.Count(c => c.Access == 'M') == 1 &&
            cache.Count(c => c.Id.StartsWith("hot-r1-warm", StringComparison.Ordinal)) == 3,
            "compare-cache: hot, buffered, write-through, larger-than-cache and mixed windows plus absorb, flush, sustained and tree steps");
        check(cache.Where(c => c.Kind is "absorb" or "flush" or "sustained" or "settle" or "tree").All(c => c.SettleSeconds >= 45) &&
            cacheWindows.All(c => ComparisonPlan.CacheFiles.Any(f => f.Name == c.File)), "every cache write waits for write-back; windows use the kept files");
        var cold = ComparisonPlan.CasesFor("compare-cache-cold", 3);
        check(cold.Count(c => c.Kind == "warm") == 2 && cold.Count(c => c.Kind == "diskspd") == 2 && cold.All(c => c.Repeat == 1),
            "the cold run happens once: first and second read, then two random-read windows");
        check(ComparisonPlan.CasesFor("compare-durability-write", 1).Single().Kind == "durability-write" &&
            ComparisonPlan.WorkDirectory("compare-ram", @"T:\", "Run") == Path.Combine(@"T:\", "Run") &&
            ComparisonPlan.WorkDirectory("compare-cache", @"Q:\", "Run") == Path.Combine(@"Q:\", "QCBench") &&
            ComparisonPlan.WorkDirectory("compare-snapshot", @"Q:\", "Run") is null, "comparison folders: run-owned for RAM disks, kept for caches");
        check(ComparisonPlan.CasesFor("compare-durability-churn", 1).Single().Kind == "durability-churn" &&
            ComparisonPlan.IsDurabilityWrite("compare-durability-churn") && ComparisonPlan.IsDurabilityWrite("compare-durability-write") &&
            !ComparisonPlan.IsDurabilityWrite("compare-durability-verify") && !ComparisonPlan.UsesDiskSpd("compare-durability-churn") &&
            ComparisonPlan.WorkDirectory("compare-durability-churn", @"Q:\", "Run") == Path.Combine(@"Q:\", "QCBench-churn"),
            "the churn power-cut test writes an oracle and a durable FINISHED marker, without DiskSpd, in its own folder");
        reject(() => { try { ComparisonPlan.CasesFor("compare-ram", 4); } catch (ArgumentOutOfRangeException) { throw new InvalidDataException(); } });

        string[] Args(ComparisonCase c) => ComparisonPlan.Arguments(c, 1L << 30);
        var q8t4 = ramWindows.First(c => !c.Random && c.Threads == 4 && c.Access == 'W');
        var buffered = cacheWindows.First(c => c.Buffering == ComparisonPlan.Buffered && c.Access == 'R');
        var through = cacheWindows.First(c => c.Buffering == ComparisonPlan.WriteThrough);
        var random = ramWindows.First(c => c.Random && c.Access == 'R');
        check(Args(q8t4).Contains("-si") && Args(q8t4).Contains("-Su") && Args(q8t4).Contains("-Z1M") && Args(q8t4).Contains("-w100") &&
            Args(q8t4).Contains("-f1024M") && Args(q8t4).Contains("-W2") && Args(q8t4).Contains("-d10"),
            "multi-threaded sequential writes share one cursor, unbuffered, with a fixed random source buffer");
        check(Args(buffered).Contains("-Sb") && !Args(buffered).Contains("-Z1M") && Args(through).Contains("-Sh") &&
            Args(random).Contains("-r4K") && Args(random).Contains("-z7") && !Args(random).Contains("-si"),
            "buffered, write-through and random windows ask DiskSpd for exactly that");
        reject(() => { try { ComparisonPlan.Arguments(ram[0], 1L << 30); } catch (ArgumentException) { throw new InvalidDataException(); } });

        XElement Profile(ComparisonCase c, bool? disableOsCache, bool writeThrough, string? random = null) =>
            new("Profile", new XElement("TimeSpans", new XElement("TimeSpan", new XElement("Duration", c.Seconds), new XElement("Warmup", 2),
                new XElement("Targets", new XElement("Target", new XElement("BlockSize", c.BlockKiB * 1024L), new XElement("RequestCount", c.Queue),
                    new XElement("ThreadsPerFile", c.Threads), new XElement("WriteRatio", c.WritePercent), new XElement("MaxFileSize", 1L << 30),
                    disableOsCache is { } d ? new XElement("DisableOSCache", d ? "true" : "false") : null,
                    writeThrough ? new XElement("WriteThrough", "true") : null,
                    random is not null ? new XElement("Random", random) : new XElement("InterlockedSequential", c.Threads > 1 ? "true" : "false"))))));
        ComparisonPlan.ValidateProfile(q8t4, new XElement("Results", Profile(q8t4, true, false)), 1L << 30);
        ComparisonPlan.ValidateProfile(through, new XElement("Results", Profile(through, true, true)), 1L << 30);
        ComparisonPlan.ValidateProfile(buffered, new XElement("Results", Profile(buffered, null, false)), 1L << 30);
        ComparisonPlan.ValidateProfile(random, new XElement("Results", Profile(random, true, false, "4096")), 1L << 30);
        reject(() => ComparisonPlan.ValidateProfile(through, new XElement("Results", Profile(through, true, false)), 1L << 30));
        reject(() => ComparisonPlan.ValidateProfile(buffered, new XElement("Results", Profile(buffered, true, false)), 1L << 30));
        reject(() => ComparisonPlan.ValidateProfile(q8t4, new XElement("Results", Profile(q8t4 with { Threads = 1 }, true, false)), 1L << 30));
        reject(() => ComparisonPlan.ValidateProfile(q8t4, new XElement("Results", Profile(q8t4, true, false)), 2L << 30));

        string Results(ComparisonCase c, long reads, long writes, bool cpu = true, bool p50 = true)
        {
            var block = c.BlockKiB * 1024L;
            var threads = Enumerable.Range(0, c.Threads).Select(_ => new XElement("Thread", new XElement("Target",
                new XElement("ReadBytes", reads / c.Threads * block), new XElement("ReadCount", reads / c.Threads),
                new XElement("WriteBytes", writes / c.Threads * block), new XElement("WriteCount", writes / c.Threads),
                new XElement("FileSize", 1L << 30))));
            XElement Bucket(double percentile, double ms) => new("Bucket", new XElement("Percentile", percentile),
                reads > 0 ? new XElement("ReadMilliseconds", ms) : null, writes > 0 ? new XElement("WriteMilliseconds", ms * 2) : null);
            return new XElement("Results", Profile(c, c.Buffering == ComparisonPlan.Buffered ? null : true, c.Buffering == ComparisonPlan.WriteThrough,
                    c.Random ? (c.BlockKiB * 1024L).ToString() : null),
                new XElement("TimeSpan", new XElement("TestTimeSeconds", 10),
                    cpu ? new XElement("CpuUtilization", new XElement("Average", new XElement("UsagePercent", 25.5))) : null,
                    threads, new XElement("Latency", p50 ? Bucket(50, 0.1) : null, Bucket(99, 0.5), Bucket(99.9, 0.9), Bucket(100, 2)))).ToString();
        }
        var scored = ComparisonPlan.Score(q8t4, Results(q8t4, 0, 4000), 1L << 30);
        check(Math.Abs(scored.MBPerSecond - 4000 * 1048576.0 / 10 / 1_000_000) < 1e-6 && scored.WriteP50Ms == 0.2 && scored.WriteP999Ms == 1.8 &&
            scored.ReadP50Ms is null && scored.CpuPercent == 25.5 && scored.Iops == 400, "comparison scores carry decimal MB/s, p50/p99/p99.9 and CPU");
        reject(() => ComparisonPlan.Score(q8t4, Results(q8t4, 0, 4000, cpu: false), 1L << 30));
        reject(() => ComparisonPlan.Score(q8t4, Results(q8t4, 0, 4000, p50: false), 1L << 30));
        reject(() => ComparisonPlan.Score(q8t4, Results(q8t4, 400, 4000), 1L << 30));
        check(ComparisonPlan.Score(q8t4, Results(q8t4, 0, 0), 1L << 30) is { Operations: 0, MBPerSecond: 0, WriteP99Ms: null, ReadP99Ms: null },
            "a window that completed nothing is a stall (0 MB/s, latency N/A), not a collection failure");
        var mixed = cacheWindows.Single(c => c.Access == 'M');
        check(ComparisonPlan.Score(mixed, Results(mixed, 700, 300), 4L << 30 >> 2).ReadP99Ms == 0.5, "a mixed window reports both directions");
        reject(() => ComparisonPlan.Score(mixed, Results(mixed, 700, 0), 1L << 30));

        check(ComparisonPlan.Shape("ram-r2-seq1m-q8t1-read") == "ram-seq1m-q8t1-read" && ComparisonPlan.Shape("absorb-r3") == "absorb" &&
            ComparisonPlan.Shape("hot-r1-warm") == "hot-warm" && ComparisonPlan.Spread([3.0, 1, 2]).Median == 2 && ComparisonPlan.Spread([1.0, 2, 3, 4]) == (2.5, 1, 4),
            "repetitions group by shape; medians of odd and even counts");
        var two = ram.Take(2).ToArray();
        ComparisonPlan.ValidateChecks(two, [new(two[0].Id, "PASS", ""), new(two[1].Id, "PASS", "")]);
        reject(() => ComparisonPlan.ValidateChecks(two, [new(two[1].Id, "PASS", ""), new(two[0].Id, "PASS", "")]));
        reject(() => ComparisonPlan.ValidateChecks(two, [new(two[0].Id, "PASS", "")]));
        reject(() => ComparisonPlan.ValidateChecks(two, [new(two[0].Id, "PASS", ""), new(two[1].Id, "FAIL", "")]));

        var options = new VerificationOptions("T:", "compare-ram", DiskSpd: Environment.ProcessPath, Product: "Example RAM Disk 1.0 static");
        VerificationPlan.Validate(options);
        VerificationPlan.Validate(options with { Suite = "compare-durability-verify", DiskSpd = null, OraclePath = "oracle.json" });
        VerificationPlan.Validate(options with { Suite = "compare-snapshot", DiskSpd = null });
        VerificationPlan.Validate(options with { Suite = "compare-durability-churn", DiskSpd = null });
        foreach (var invalid in new[] { options with { Product = null }, options with { DiskSpd = null }, options with { Repeats = 4 },
            options with { Suite = "compare-durability-write" }, options with { Suite = "compare-durability-verify", DiskSpd = null },
            options with { Suite = "compare-durability-write", DiskSpd = null, OraclePath = "oracle.json" }, options with { Suite = "quick", DiskSpd = null },
            options with { Suite = "compare-durability-churn" }, options with { Suite = "compare-durability-churn", DiskSpd = null, OraclePath = "oracle.json" },
            options with { KeepWorkloads = true } })
            try { VerificationPlan.Validate(invalid); throw new Exception("comparison options accepted: " + invalid); }
            catch (ArgumentException) { }

        var spec = ComparisonData.TreeSpec();
        var bytes = spec.Sum(f => (long)f.Bytes);
        check(spec.Count == ComparisonPlan.TreeFiles && spec.SequenceEqual(ComparisonData.TreeSpec()) && spec.Select(f => f.Path).Distinct().Count() == spec.Count &&
            bytes is > 1L << 30 and < 3L << 29 && spec.All(f => f.Bytes is >= 1024 and <= 1 << 20) &&
            spec.Select((f, i) => ComparisonData.TreeOffset(i, f.Bytes) + f.Bytes).Max() <= 4 << 20,
            "the file tree is seeded, unique, 1.0-1.5 GiB and every file fits the shared content");
        var sector = new byte[4096];
        ComparisonData.FillSector(sector, 42, 2, 1, 7);
        var older = new byte[4096];
        ComparisonData.FillSector(older, 42, 1, 1, 7);
        check(ComparisonData.Classify(sector, 42, 1, 7) == 2 && ComparisonData.Classify(older, 42, 1, 7) == 1 &&
            ComparisonData.Classify(sector, 43, 1, 7) == 0 && ComparisonData.Classify(sector, 42, 1, 8) == 0 && !sector.AsSpan(32).SequenceEqual(older.AsSpan(32)),
            "durability sectors identify their generation and nothing else");
        sector[100] ^= 1;
        check(ComparisonData.Classify(sector, 42, 1, 7) == 0, "a changed byte makes a sector 'other'");

        // Churn power-cut test: deterministic file contents and schedule, and age groups from the oracle's timeline.
        var churnFile = ComparisonData.ChurnFile(42, ComparisonData.ChurnSet, 5);
        var changed = (byte[])churnFile.Clone(); changed[^1] ^= 1;
        check(churnFile.Length == ComparisonData.ChurnSectors(ComparisonData.ChurnSet, 5) * 4096 && churnFile.Length is >= 4096 and <= 32768 &&
            ComparisonData.ChurnIntact(churnFile, 42, ComparisonData.ChurnSet, 5) && !ComparisonData.ChurnIntact(changed, 42, ComparisonData.ChurnSet, 5) &&
            !ComparisonData.ChurnIntact(churnFile.AsSpan(0, churnFile.Length - 4096), 42, ComparisonData.ChurnSet, 5) &&
            !ComparisonData.ChurnIntact(churnFile, 42, ComparisonData.ChurnBaseSet, 5) && !ComparisonData.ChurnIntact(churnFile, 43, ComparisonData.ChurnSet, 5) &&
            Enumerable.Range(0, 64).Select(i => ComparisonData.ChurnSectors(ComparisonData.ChurnSet, i)).Distinct().Count() == 8,
            "churn files have fixed 4-32 KiB contents; a changed byte, a missing sector, another set or nonce is not intact");
        var full = ComparisonData.ChurnFile(42, ComparisonData.ChurnSet, 13);
        var foreign = (byte[])full.Clone();
        ComparisonData.ChurnFile(42, ComparisonData.ChurnBaseSet, 2).AsSpan(0, 4096).CopyTo(foreign.AsSpan(full.Length - 4096));
        check(ComparisonData.ChurnDamage([], ComparisonData.ChurnSet, 13) == "empty" &&
            ComparisonData.ChurnDamage(full.AsSpan(0, full.Length - 4096), ComparisonData.ChurnSet, 13) == (full.Length > 4096 ? "short" : "empty") &&
            ComparisonData.ChurnDamage(new byte[full.Length], ComparisonData.ChurnSet, 13) == "zeros" &&
            ComparisonData.ChurnDamage(foreign, ComparisonData.ChurnSet, 13) == "another file" &&
            ComparisonData.ChurnDamage(changed, ComparisonData.ChurnSet, 5) == "other",
            "damaged churn files are classified as empty, short, zeros, another test file's data, or other");
        var churnSteps = ComparisonData.ChurnSeconds * ComparisonData.ChurnFilesPerSecond;
        var deletes = Enumerable.Range(0, churnSteps).Select(i => ComparisonData.ChurnDeletes(i)).ToArray();
        var baseDeletes = deletes.Where(d => d.Base is not null).Select(d => d.Base!.Value).ToArray();
        var churnDeletes = deletes.Where(d => d.Churn is not null).Select((d, n) => (d.Churn!.Value, n)).ToArray();
        check(baseDeletes.Distinct().Count() == ComparisonData.ChurnBaseFiles / 2 && baseDeletes.Max() < ComparisonData.ChurnBaseFiles / 2 &&
            churnDeletes.Length > 0 && Enumerable.Range(0, churnSteps).All(i => ComparisonData.ChurnDeletes(i).Churn is not { } c || c == i - 1000) &&
            Enumerable.Range(0, 1000).All(i => ComparisonData.ChurnDeletes(i).Churn is null),
            "a full churn deletes the first half of the base files once each and only churn files 1,000 steps old");
        var t0 = DateTimeOffset.UnixEpoch;
        var churnOracle = new ChurnOracle(2, 42, @"Q:\QCBench-churn", 2, t0, t0, t0.AddSeconds(40), [0, 5_000, 15_000, 25_000, 35_000, 39_000],
            [-1, 100], [-1, -1, -1, -1, -1, 39_500], "Example");
        check(ComparisonData.ChurnGroup(churnOracle, true, 0) == "base files kept" &&
            ComparisonData.ChurnGroup(churnOracle, true, 1) == "base files deleted during the churn" &&
            ComparisonData.ChurnGroup(churnOracle, false, 0) == "churn files 30 s or older" && ComparisonData.ChurnGroup(churnOracle, false, 1) == "churn files 30 s or older" &&
            ComparisonData.ChurnGroup(churnOracle, false, 2) == "churn files 20-30 s old" && ComparisonData.ChurnGroup(churnOracle, false, 3) == "churn files 10-20 s old" &&
            ComparisonData.ChurnGroup(churnOracle, false, 4) == "churn files under 10 s old" && ComparisonData.ChurnGroup(churnOracle, false, 5) == "churn files deleted again",
            "churn files are grouped by their age when the churn ended; deleted files form their own groups");

        var directory = Directory.CreateTempSubdirectory("qc-compare-").FullName;
        try
        {
            var windows = ramWindows.Where(c => ComparisonPlan.Shape(c.Id) == "ram-seq1m-q8t1-read").ToArray();
            var mbs = new[] { 30_000.0, 31_000, 29_000 };
            for (var i = 0; i < 3; i++)
                File.WriteAllText(Path.Combine(directory, $"compare.{windows[i].Id}.score.json"), JsonSerializer.Serialize(
                    new ComparisonScore(windows[i].Id, 1, 1, 10, mbs[i], 100 + i, 0.1 * (i + 1), 1, 2, null, null, null, 10 + i)));
            var absorb = cache.Where(c => c.Kind == "absorb").ToArray();
            for (var i = 0; i < 3; i++)
                File.WriteAllText(Path.Combine(directory, $"compare.{absorb[i].Id}.pass.json"), JsonSerializer.Serialize(new
                {
                    Pass = new SequentialPass(1L << 30, 1, 1000 + i, null, null), Settle = new SettleResult(60, 1L << 30, true, "test")
                }));
            var shapes = ComparisonReport.Shapes(directory, windows);
            var steps = ComparisonReport.Steps(directory, absorb);
            check(shapes.Single() is { Samples: 3, MBPerSecond: 30_000, MinMBPerSecond: 29_000, MaxMBPerSecond: 31_000, ReadP50Ms: 0.2, WriteP99Ms: null, CpuPercent: 11 } &&
                steps.Single() is { Shape: "absorb", Measure: "MB/s", Median: 1001, Samples: 3 }, "the report takes medians and ranges from each step's own evidence");
            File.Delete(Path.Combine(directory, $"compare.{windows[2].Id}.score.json"));
            try { ComparisonReport.Shapes(directory, windows); throw new Exception("a missing score was summarized"); }
            catch (IOException) { }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>Cached bookkeeping candidate: flags 11/15 on one build, with miss offload in both arms.</summary>
    private static void RunBookkeepingContracts(Action<bool, string> check, Action<Action> reject)
    {
        var options = new VerificationOptions("Q:", "cache-read-bookkeeping", BudgetMiB: 2048, Repeats: 3, DiskSpd: Environment.ProcessPath);
        var cases = VerificationPlan.Performance(options);
        check(cases.Count == 18 && cases.Select(c => c.Id).Distinct().Count() == 18 &&
            cases.Count(c => c.CopyFlags == CacheReadBookkeeping.CandidateFlags) == 9 && cases.Count(c => c.CopyFlags == CacheReadBookkeeping.OriginalFlags) == 9 &&
            cases.All(c => !c.Timing && c.WarmResident && c.Workload == "sequential-read") &&
            cases.GroupBy(c => (c.Repeat, c.QueueDepth, c.Threads)).All(g => g.Count() == 2 && g.Select(c => c.CopyFlags).Distinct().Count() == 2),
            "cached read comparison: each shape once per mode per repetition, timing off");
        check(CacheReadBookkeeping.IsTarget(cases[0]) && CacheReadBookkeeping.CandidateFlags == (CacheReadBookkeeping.OriginalFlags | 4) &&
            CacheReadBookkeeping.ProductFlags == CacheReadBookkeeping.CandidateFlags && (CacheReadBookkeeping.ProductFlags & 8) != 0,
            "the candidate adds only flag 4 and is the product default (with miss offload, flag 8); Q8T1 is the target");
        var phase = VerificationCampaignPlan.Create(new(new("Q:", BudgetMiB: 2048, Repeats: 3, DiskSpd: Environment.ProcessPath), "experiment", "W:", FocusSuite: "cache-read-bookkeeping"));
        check(phase.Count == 1 && phase[0].Role == CampaignTargetRole.Performance && phase[0].MeasurementWindows == 18, "one-phase cached read comparison");
        var before = JsonSerializer.SerializeToElement(new { Diagnostics = new { CopyFlags = 15UL } });
        var score = new DiskSpdScore(1000L << 20, 1000, 10, 100, 100, null, null, null, null);
        var candidate = cases.First(c => c.CopyFlags == 15);
        check(CacheReadBookkeeping.Window(candidate, before, score).CopyFlags == 15, "the window records the reported copy flags");
        reject(() => CacheReadBookkeeping.Window(cases.First(c => c.CopyFlags == 11), before, score));
        reject(() => CacheReadBookkeeping.Window(candidate, JsonSerializer.SerializeToElement(new { Diagnostics = new { } }), score));
        CacheReadBookkeeping.ComparisonWindow[] Windows(Func<PerformanceCase, double> rate) =>
            cases.Select(c => new CacheReadBookkeeping.ComparisonWindow(c, c.CopyFlags!.Value, rate(c))).ToArray();
        double Target(PerformanceCase c, double gain) => CacheReadBookkeeping.IsTarget(c) && c.CopyFlags == 15 ? 36_000 * gain + c.Repeat : 36_000 + c.Repeat;
        check(CacheReadBookkeeping.Evaluate(Windows(c => Target(c, 1.10))).Verdict == "ACCEPT_SCREEN", "a clear Q8 gain passes the screen");
        check(CacheReadBookkeeping.Evaluate(Windows(c => Target(c, 1.01))).Verdict == "REJECT", "a 1% gain rejects");
        check(CacheReadBookkeeping.Evaluate(Windows(c => c.QueueDepth == 1 && c.CopyFlags == 15 ? 34_000 : Target(c, 1.10))).Verdict ==
            "CONTROL_LOSS_REVISION_OR_REJECT", "a repeatable control loss blocks acceptance");
        check(CacheReadBookkeeping.Evaluate(Windows(c => Target(c, 1.10)).Where(w => w.Case.Repeat < 3).ToArray()).Verdict == "INCOMPLETE_FOR_ACCEPTANCE",
            "two repetitions never pass");
    }

    /// <summary>Plan 120 busy-day suite: versioned sectors, version windows and the action schedule.</summary>
    private static void RunStressContracts(Action<bool, string> check, Action<Action> reject)
    {
        var buffer = new byte[64 << 10];
        StressPattern.Fill(buffer, 99, 7, 3);
        check(StressPattern.Check(buffer, 99, 7, 3, 3) is null && StressPattern.Check(buffer, 99, 7, 1, 5) is null, "a whole version inside its window passes");
        check(StressPattern.Check(buffer, 99, 7, 4, 6) is not null, "a stale version is reported");
        check(StressPattern.Check(buffer, 99, 8, 3, 3) is not null && StressPattern.Check(buffer, 98, 7, 3, 3) is not null, "a misplaced or foreign block is reported");
        var torn = (byte[])buffer.Clone();
        var newer = new byte[64 << 10]; StressPattern.Fill(newer, 99, 7, 4);
        Array.Copy(newer, StressPattern.Sector, torn, StressPattern.Sector, StressPattern.Sector);
        check(StressPattern.Check(torn, 99, 7, 3, 4) is null && StressPattern.Check(torn, 99, 7, 3, 3) is not null,
            "sectors of two allowed versions pass; a newer sector outside the window fails");
        torn[StressPattern.Sector * 2 + 100] ^= 1;
        check(StressPattern.Check(torn, 99, 7, 0, 10)?.Contains("not a whole written sector") == true, "a torn sector is detected");
        var versions = new StressBlockVersions(4);
        versions.Begin(2, 5); check(versions.Started(2) == 5 && versions.Done(2) == 0, "a started write widens the window");
        versions.Complete(2, 5); check(versions.Done(2) == 5, "a completed write moves the lower bound");
        var random = new Random(120);
        var first = StressPlan.Round(random); var second = StressPlan.Round(random);
        check(first.ToHashSet().SetEquals(Enum.GetValues<StressAction>()) && first.Length == Enum.GetValues<StressAction>().Length + 2 &&
            first.Count(a => a == StressAction.Resize) == 2 && first.Count(a => a == StressAction.Flush) == 2 && !first.SequenceEqual(second),
            "every round performs every chaos action, in a different order each round");
        for (var i = 0; i < 50; i++)
        {
            var configuration = StressPlan.RandomConfiguration(random, [64, 96, 128, 256]);
            configuration.Validate(true);
            check(configuration.Options.Parallelism is 1 or 2 or 4 && configuration.BudgetMiB is 64 or 96 or 128 or 256, "random settings stay in the supported set");
        }
        check(StressRegion.All.Select(r => r.BlockBytes).Distinct().Count() == 4 && StressRegion.All.Count(r => r.Overlapped) == 1 &&
            StressRegion.All.Max(r => r.BlockBytes) == 4 << 20 && StressRegion.All.All(r => r.BlockBytes % StressPattern.Sector == 0),
            "regions cover 4 KiB to 4 MiB requests and one overlapped region");
        check(VerificationPlan.Integrity(new VerificationOptions("W:", "stress-races")) is [{ Id: "stress-races", Operation: "stress-races" }], "one integrity case");
        check(VerificationPlan.InjectsLabFaults("stress-races") && VerificationPlan.InjectsLabFaults("ordering-faults") &&
            !VerificationPlan.InjectsLabFaults("policies") && !VerificationPlan.InjectsLabFaults("pressure"),
            "only the two fault-injecting suites may restore with a raised error count");
        VerificationPlan.Validate(new VerificationOptions("W:", "stress-races", Repeats: 1, SoakSeconds: 600));
        foreach (var invalid in new[] { new VerificationOptions("W:", "stress-races", Repeats: 1, SoakSeconds: 30), new VerificationOptions("W:", "stress-races", Repeats: 2),
            new VerificationOptions("W:", "policies", SoakSeconds: 600) })
            try { VerificationPlan.Validate(invalid); throw new Exception("stress-races accepted invalid options"); }
            catch (ArgumentException) { }
        var stress = VerificationCampaignPlan.Create(new(new("Q:", BudgetMiB: 2048, SoakSeconds: 600), "experiment", "W:", FocusSuite: "stress-races"));
        check(stress.Count == 1 && stress[0].Role == CampaignTargetRole.NtfsLab && stress[0].Options.Volume == "W:" &&
            stress[0].Options.SoakSeconds == 600 && stress[0].Options.Repeats == 1, "stress-races runs on the NTFS lab volume with its soak length");
        var focused = VerificationCampaignPlan.Create(new(new("Q:", BudgetMiB: 2048, SoakSeconds: 120), "focused", "W:", FocusSuite: "stress-races"));
        check(focused.Single(p => p.Options.Suite == "stress-races").Role == CampaignTargetRole.NtfsLab && focused[^1].Options.Suite == "ordering-faults",
            "a focused stress campaign keeps fault injection last");
        var faulted = new WriteCacheState(8163, -1, 1UL << 30, 64UL << 20, 64UL << 20,
            1UL << 20, 0, 60UL << 20, 256, 2UL << 20, 1UL << 20, 0, 0, 1, 1, 1UL << 20)
            { Instance = 7, Generation = 3 };
        var retried = faulted with { Flags = 8161, LastError = 0, DirtyBytes = 0, DrainedBytes = 2UL << 20 };
        StressRetryEvidence.Validate(faulted, retried);
        reject(() => StressRetryEvidence.Validate(faulted, retried with { DrainedBytes = retried.DrainedBytes - 512 }));
        reject(() => StressRetryEvidence.Validate(faulted, retried with
            { AcceptedBytes = retried.AcceptedBytes + 16384, DrainedBytes = retried.DrainedBytes + 8192, DirtyBytes = 8192 }));
        reject(() => StressRetryEvidence.Validate(faulted, retried with { InFlightBytes = 512 }));
        reject(() => StressRetryEvidence.Validate(faulted, retried with { Instance = 8 }));
        reject(() => StressRetryEvidence.Validate(faulted, retried with { Errors = 2 }));
        reject(() => StressRetryEvidence.Validate(faulted, retried with { DiscardedBytes = 512 }));
    }
}
