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
        Check(VerificationPlan.Version == 120, "plan 120 reference suites have a new verification plan version on every host");
        RunPlan120Contracts(Check, Reject);
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

    /// <summary>Plan 120: RAM write reference with memory-copy probes, and the cached read attribution suite.</summary>
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

    /// <summary>Plan 120 cached read candidate: copy flags 3 against 7 on one build.</summary>
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
            CacheReadBookkeeping.ProductFlags == CacheReadBookkeeping.CandidateFlags, "the candidate adds only flag 4 and is the product default; Q8T1 is the target");
        var phase = VerificationCampaignPlan.Create(new(new("Q:", BudgetMiB: 2048, Repeats: 3, DiskSpd: Environment.ProcessPath), "experiment", "W:", FocusSuite: "cache-read-bookkeeping"));
        check(phase.Count == 1 && phase[0].Role == CampaignTargetRole.Performance && phase[0].MeasurementWindows == 18, "one-phase cached read comparison");
        var before = JsonSerializer.SerializeToElement(new { Diagnostics = new { CopyFlags = 7UL } });
        var score = new DiskSpdScore(1000L << 20, 1000, 10, 100, 100, null, null, null, null);
        var candidate = cases.First(c => c.CopyFlags == 7);
        check(CacheReadBookkeeping.Window(candidate, before, score).CopyFlags == 7, "the window records the reported copy flags");
        reject(() => CacheReadBookkeeping.Window(cases.First(c => c.CopyFlags == 3), before, score));
        reject(() => CacheReadBookkeeping.Window(candidate, JsonSerializer.SerializeToElement(new { Diagnostics = new { } }), score));
        CacheReadBookkeeping.ComparisonWindow[] Windows(Func<PerformanceCase, double> rate) =>
            cases.Select(c => new CacheReadBookkeeping.ComparisonWindow(c, c.CopyFlags!.Value, rate(c))).ToArray();
        double Target(PerformanceCase c, double gain) => CacheReadBookkeeping.IsTarget(c) && c.CopyFlags == 7 ? 36_000 * gain + c.Repeat : 36_000 + c.Repeat;
        check(CacheReadBookkeeping.Evaluate(Windows(c => Target(c, 1.10))).Verdict == "ACCEPT_SCREEN", "a clear Q8 gain passes the screen");
        check(CacheReadBookkeeping.Evaluate(Windows(c => Target(c, 1.01))).Verdict == "REJECT", "a 1% gain rejects");
        check(CacheReadBookkeeping.Evaluate(Windows(c => c.QueueDepth == 1 && c.CopyFlags == 7 ? 34_000 : Target(c, 1.10))).Verdict ==
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
    }
}
