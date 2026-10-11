using System.Text.Json;
using System.Xml.Linq;
using QueueCache.Developer.Verification;

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
        Check(VerificationPlan.Version == 117, "coordination contract has a new verification plan version on every host");
        VerificationRunnerTests.RunCoordinationContracts(Check, Reject);
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
            cases.All(c => c.SchedulingControl && !c.DisableAffinity && c.RamReadQueueMode == 0), "three immutable traced shapes with balanced repetitions");
        Check(cases[2].Interleaved && RamReadReferencePlan.Arguments(cases[2], 10).Contains("-s4M") &&
            RamReadReferencePlan.Arguments(cases[2], 10).Contains("-T1M") && !RamReadReferencePlan.Arguments(cases[2], 10).Contains("-si"), "separate source lanes");
        Check(RamReadReferencePlan.CasesFor(RamReadRunKind.Reference, 3).Count == 36 &&
            RamReadReferencePlan.CasesFor(RamReadRunKind.ArchivedQueue, 3).Count == 54 &&
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
        var finalTarget = new CampaignTargetEvidence(CampaignTargetRole.Performance, recovery, "NTFS", "fixture", "fixture", null,
            false, null, null, new(true, null, []));
        var finalOrder = new List<string>(); var finalClean = false;
        VerificationCampaignHost.FinalizeTargetsAsync([finalTarget],
            _ => { finalOrder.Add("prepare"); finalClean = true; return Task.CompletedTask; },
            t => { finalOrder.Add("inspect"); return Task.FromResult(finalClean ? t : t with { Recovery = t.Recovery with { State = t.Recovery.State with { DirtyBytes = 4096 } } }); })
            .GetAwaiter().GetResult();
        Check(finalOrder.SequenceEqual(["prepare", "inspect"]), "final backing restoration prepares before strict clean capture");
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
}
