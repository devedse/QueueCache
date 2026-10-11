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
        RunStreamingContracts(check, reject);
    }

    /// <summary>Plan 120 candidate: streaming stores for large RAM-disk write copies, compared on one build.</summary>
    private static void RunStreamingContracts(Action<bool, string> check, Action<Action> reject)
    {
        check((uint)QueueCache.Management.RamDiskAction.StreamWrites == 0x105 && (uint)QueueCache.Management.RamDiskAction.SetStreamWrites == 0x106 &&
            (uint)QueueCache.Management.RamDiskAction.SetAsyncReads == 0x104, "write copy modes add actions without recycling IDs");
        var plan = RamReadReferencePlan.CasesFor(RamReadRunKind.WriteStreaming, 3);
        check(RamReadReferencePlan.KindFor("ram-write-streaming") == RamReadRunKind.WriteStreaming && plan.Count == 18 &&
            plan.Select(c => c.Id).Distinct().Count() == 18 && plan.Count(c => c.StreamWrites) == 9 &&
            plan.All(c => c.Write && c.SchedulingControl && c.Probe is null) &&
            plan.GroupBy(c => (c.Repeat, c.QueueDepth, c.Threads)).All(g => g.Count(c => c.StreamWrites) == 1 && g.Count() == 2) &&
            RamWriteStreaming.IsTarget(plan.First(c => c.QueueDepth == 1)), "18 same-build windows: each shape once per mode per repetition, Q1T1 target");
        check(plan[0].Repeat == 1 && !plan[0].StreamWrites && plan.First(c => c.Repeat == 2).StreamWrites, "the first mode alternates between repetitions");
        static byte[] Wire(ulong generation, ulong mode, ulong writes, ulong bytes)
        {
            var data = new byte[QueueCache.Management.RamStreamWrites.WireSize];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)data.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 1);
            foreach (var (value, index) in new[] { generation, mode, writes, bytes }.Select((v, i) => (v, i)))
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8 + index * 8), value);
            return data;
        }
        var on = QueueCache.Management.RamStreamWrites.Decode(Wire(4, 1, 0, 0));
        check(on.Mode == QueueCache.Management.RamStreamMode.Large && on.Generation == 4, "write copy payload decodes");
        foreach (var invalid in new[] { Wire(4, 2, 0, 0), Wire(4, 1, 10, 1 << 16) })
            reject(() => QueueCache.Management.RamStreamWrites.Decode(invalid));
        var ran = on with { Writes = 1000, Bytes = 1000UL << 20 };
        check(Math.Abs(QueueCache.Management.RamStreamWrites.ValidateWindow(on, ran, QueueCache.Management.RamStreamMode.Large, 1001UL << 20) - 1000.0 / 1001) < 1e-9,
            "streamed share is streamed bytes over written bytes");
        foreach (var invalid in new[] { ran with { Generation = 5 }, ran with { Mode = QueueCache.Management.RamStreamMode.Off } })
            reject(() => QueueCache.Management.RamStreamWrites.ValidateWindow(on, invalid, QueueCache.Management.RamStreamMode.Large, 1001UL << 20));
        reject(() => QueueCache.Management.RamStreamWrites.ValidateWindow(on, ran, QueueCache.Management.RamStreamMode.Large, 999UL << 20));
        var off = on with { Mode = QueueCache.Management.RamStreamMode.Off };
        check(QueueCache.Management.RamStreamWrites.ValidateWindow(off, off, QueueCache.Management.RamStreamMode.Off, 1UL << 30) == 0, "ordinary windows stream nothing");
        reject(() => QueueCache.Management.RamStreamWrites.ValidateWindow(off, off with { Writes = 1, Bytes = 1 << 20 }, QueueCache.Management.RamStreamMode.Off, 1UL << 30));

        var resource = Guid.NewGuid(); var boot = Guid.NewGuid();
        RamStreamWindow[] Windows(Func<RamReadReferenceCase, double> score) => plan.Select(c => new RamStreamWindow(c, resource, boot, 1,
            c.StreamWrites ? on : off, c.StreamWrites ? ran : off, c.StreamWrites ? 0.99 : 0,
            new DiskSpdScore(50L << 20, 50, 10, score(c), 5, null, 0.1, null, null))).ToArray();
        var directory = Path.Combine(Path.GetTempPath(), "qcache-stream-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var evidence = Path.Combine(directory, "evidence");
            double Target(RamReadReferenceCase c, double gain) => RamWriteStreaming.IsTarget(c) && c.StreamWrites ? 16_000 * gain + c.Repeat : 16_000 + c.Repeat;
            check(RamWriteStreaming.Report(evidence, plan, Windows(c => Target(c, 1.20))) == "ACCEPT_SCREEN", "a clear Q1 gain with flat controls passes the screen");
            check(RamWriteStreaming.Report(evidence, plan, Windows(c => Target(c, 1.02))) == "REJECT", "a gain under 5% rejects the candidate");
            check(RamWriteStreaming.Report(evidence, plan, Windows(c => c.QueueDepth == 8 && c.StreamWrites ? 15_000 : Target(c, 1.20))) ==
                "CONTROL_LOSS_REVISION_OR_REJECT", "a repeatable control loss blocks acceptance");
            check(RamWriteStreaming.Report(evidence, plan.Take(12).ToArray(), Windows(c => Target(c, 1.20))[..12]) == "INCOMPLETE_FOR_ACCEPTANCE",
                "fewer than three repetitions never pass the screen");
            reject(() => RamWriteStreaming.Report(evidence, plan, Windows(c => 1)[..^1]));
        }
        finally { Directory.Delete(directory, true); }
    }
}
