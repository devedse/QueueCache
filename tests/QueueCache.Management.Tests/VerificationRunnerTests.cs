using System.Reflection;
using System.Text.Json;
using QueueCache.Developer.Verification;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class VerificationRunnerTests
{
    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string message) => report(message);
    }
    public static async Task RunAsync()
    {
        void Check(bool value, string name)
        {
            if (!value)
                throw new Exception("Verification runner: " + name);
        }
        void Reject(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e) when (e is ArgumentException or InvalidDataException or System.Xml.XmlException) { return; }
            throw new Exception("Expected rejection.");
        }
        var options = new VerificationOptions("Q:", "performance");
        Check(VerificationPlan.Version == 14, "pressure and trigger verification contract version");
        QueueCache.Operations.PressureScenarios.ValidateTriggerWindow(1000, 850, 1450);
        foreach (var observed in new[] { 849d, 1451d })
        {
            try
            {
                QueueCache.Operations.PressureScenarios.ValidateTriggerWindow(observed, 850, 1450);
                throw new Exception("Out-of-window pressure trigger accepted.");
            }
            catch (IOException) { }
        }
        QueueCache.Operations.PressureScenarios.ValidateCapacityBounds(true, 8192, 4096, 4096, 2048, 4096, 2);
        QueueCache.Operations.PressureScenarios.ValidateCapacityBounds(false, 8192, 0, 0, 0, 0, 0);
        QueueCache.Operations.PressureScenarios.ValidateCapacityBounds(false, 8192, 0, 0, 0, 0, 2);
        foreach (var bounds in new[]
        {
            (Cached: true, Payload: 8192UL, Limit: 4096UL, Dirty: 4096UL, InFlight: 0UL, WriteOwned: 4097UL, Slots: 1UL),
            (Cached: true, Payload: 8192UL, Limit: 8192UL, Dirty: 4096UL, InFlight: 4097UL, WriteOwned: 4096UL, Slots: 1UL),
            (Cached: false, Payload: 4096UL, Limit: 0UL, Dirty: 0UL, InFlight: 0UL, WriteOwned: 1UL, Slots: 1UL)
        })
        {
            try
            {
                QueueCache.Operations.PressureScenarios.ValidateCapacityBounds(bounds.Cached, bounds.Payload,
                    bounds.Limit, bounds.Dirty, bounds.InFlight, bounds.WriteOwned, bounds.Slots);
                throw new Exception("Invalid pressure reservation bounds accepted.");
            }
            catch (IOException) { }
        }
        var admissionAttempts = new QueueCache.Management.CacheAttribution(1, 2, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Check(QueueCache.Operations.SectorScenarios.VerifyAdmissionAttempts(admissionAttempts, admissionAttempts).Contains("before=1/2/3, after=1/2/3"),
            "admission retains exact attempt evidence");
        foreach (var changed in new[] { admissionAttempts with { LowerReadAttempts = 2 }, admissionAttempts with { LowerWriteAttempts = 3 },
            admissionAttempts with { LowerFlushAttempts = 4 }, admissionAttempts with { LowerReadAttempts = 0 } })
        {
            try
            {
                QueueCache.Operations.SectorScenarios.VerifyAdmissionAttempts(admissionAttempts, changed);
                throw new Exception("Lower attempt difference accepted.");
            }
            catch (IOException) { }
        }
        foreach (var pair in new[] { (Before: (QueueCache.Management.CacheAttribution?)null, After: (QueueCache.Management.CacheAttribution?)admissionAttempts),
            (Before: (QueueCache.Management.CacheAttribution?)admissionAttempts, After: (QueueCache.Management.CacheAttribution?)null) })
        {
            try
            {
                QueueCache.Operations.SectorScenarios.VerifyAdmissionAttempts(pair.Before, pair.After);
                throw new Exception("Missing attempt counters accepted.");
            }
            catch (NotSupportedException) { }
        }
        Check(options.PreparationFlushSeconds == 180, "original flush deadline remains default");
        VerificationPlan.Validate(options with { Suite = "quick", PreparationFlushSeconds = 600 });
        Reject(() => VerificationPlan.Validate(options with { Suite = "quick", PreparationFlushSeconds = 179 }));
        Reject(() => VerificationPlan.Validate(options with { Suite = "quick", PreparationFlushSeconds = 3601 }));
        VerificationPlan.Validate(new VerificationOptions("Q:", "trim-file"));
        Check(VerificationPlan.Integrity(options with { Suite = "trim-file" }).SequenceEqual(
            new IntegrityCase[] { new("trim-file", "trim-file") }), "file-only TRIM requires no cache routing mutation");
        VerificationPlan.Validate(new VerificationOptions("Q:", "policies"));
        VerificationPlan.Validate(new VerificationOptions("Q:", "pressure"));
        Check(VerificationPlan.Integrity(options with { Suite = "pressure" }).SequenceEqual(
            new IntegrityCase[] { new("pressure-integrity", "pressure") }),
            "pressure remains a focused opt-in integrity suite");
        VerificationPlan.Validate(new VerificationOptions("Q:", "trim-diagnostic"));
        Check(VerificationPlan.Integrity(options with { Suite = "trim-diagnostic" }).SequenceEqual(
            new IntegrityCase[] { new("trim-cache-enabled", "files", true), new("trim-cache-disabled", "files", false) }),
            "TRIM diagnostic runs enabled/disabled routing with distinct case IDs");
        Check(VerificationPlan.Integrity(options with { Suite = "quick" }).SequenceEqual(
            new IntegrityCase[] { new("file-integrity", "files") }), "quick keeps its original routing");
        Check(VerificationPlan.Integrity(options with { Suite = "full" }).SequenceEqual(
            new IntegrityCase[] { new("file-integrity", "files"), new("policy-integrity", "policies") }),
            "full integrity scope unchanged; TRIM diagnostic is opt-in");
        Check(VerificationPlan.Integrity(options).Count == 0, "performance has no integrity cases");
        using (var errors = new StringWriter())
        {
            VerificationWorker.ReportFailures([new("warm-read", "PASS", "okay"), new("retention", "FAIL", "hot data evicted; barrier=0x74804")], errors);
            Check(errors.ToString().Contains("FAIL: retention: hot data evicted; barrier=0x74804") && !errors.ToString().Contains("warm-read"),
                "structured check failures reach worker stderr with exact details");
        }
        Check(options.DeadlineMinutes == 0, "overall deadline disabled by default");
        var identity = new QueueCache.Operations.DiskTarget('Q', 1, 200L << 30, "fixture");
        var dataDisk = new QueueCache.Operations.DiskDescription(1, "fixture", 200L << 30, "fixture", ["Q:"], false, false);
        VerificationWorker.ValidateFileTarget(identity, dataDisk);
        foreach (var excluded in new[] { dataDisk with { IsBoot = true }, dataDisk with { IsSystem = true }, dataDisk with { IsPaging = true }, dataDisk with { Bytes = 1 }, dataDisk with { Instance = "other" } })
        {
            try
            {
                VerificationWorker.ValidateFileTarget(identity, excluded);
                throw new Exception("File-only unsafe target accepted.");
            }
            catch (IOException) { }
        }
        CaseResult[] skippedTrim = [new("trim-file", "SKIP", "Win32 326", DateTimeOffset.UtcNow, 1)];
        Check(!RunStorage.Complete(["trim-file"], skippedTrim), "SKIP is not normal suite success");
        Check(RunStorage.Complete(["trim-file"], skippedTrim, allowSkipped: true), "diagnostic can finish with explicit SKIP");
        Check(!RunStorage.Complete(["trim-file", "missing"], skippedTrim, allowSkipped: true), "diagnostic cannot accept missing cases");
        QueueCache.Operations.DiskTarget.ValidateMountedIdentity(identity, (1, 1L << 20, 199L << 30), "FIXTURE", "ntfs");
        QueueCache.Operations.DiskTarget.ValidateDeviceLength(identity, 200UL << 30);
        try
        {
            QueueCache.Operations.DiskTarget.ValidateMountedIdentity(identity, (2, 1L << 20, 199L << 30), "fixture", "NTFS");
            throw new Exception("Disk-number mismatch accepted.");
        }
        catch (IOException) { }
        try
        {
            QueueCache.Operations.DiskTarget.ValidateDeviceLength(identity, 201UL << 30);
            throw new Exception("Disk-length mismatch accepted.");
        }
        catch (IOException) { }
        try
        {
            QueueCache.Operations.DiskTarget.ValidateMountedIdentity(identity, (1, 1L << 20, 199L << 30), "replacement", "NTFS");
            throw new Exception("PnP identity mismatch accepted.");
        }
        catch (IOException) { }
        try
        {
            QueueCache.Operations.DiskTarget.ValidateMountedIdentity(identity, (1, 1L << 20, 201L << 30), "fixture", "NTFS");
            throw new Exception("Out-of-bounds extent accepted.");
        }
        catch (IOException) { }
        if (Environment.GetEnvironmentVariable("QCACHE_TEST_DISK_TARGET") is { Length: > 0 } nativeVolume)
        {
            var nativeTarget = await QueueCache.Operations.DiskTarget.InspectAsync(nativeVolume);
            nativeTarget.ValidateCurrent();
            // Exercise the real native enumeration/marshalling path without cache controls or disk writes.
            try
            {
                (nativeTarget with
                {
                    Instance = "QueueCache-nonexistent-test-device"
                }).ValidateCurrent();
                throw new Exception("Native PnP mismatch accepted.");
            }
            catch (IOException) { }
            try
            {
                (nativeTarget with
                {
                    Number = int.MaxValue
                }).ValidateCurrent();
                throw new Exception("Native disk-number mismatch accepted.");
            }
            catch (IOException) { }
            using var cancelledIdentity = new CancellationTokenSource();
            cancelledIdentity.Cancel();
            try
            {
                nativeTarget.ValidateCurrent(cancelledIdentity.Token);
                throw new Exception("Native identity cancellation ignored.");
            }
            catch (OperationCanceledException) { }
            Console.WriteLine($"Native disk identity validated: {nativeTarget.Device} / {nativeTarget.Instance}");
        }
        VerificationPlan.Validate(options with
        {
            Suite = "quick",
            DeadlineMinutes = 0
        });
        VerificationPlan.Validate(options with
        {
            Suite = "quick",
            DeadlineMinutes = 1440
        });
        Reject(() => VerificationPlan.Validate(options with { Suite = "quick", DeadlineMinutes = -1 }));
        Reject(() => VerificationPlan.Validate(options with { Suite = "quick", DeadlineMinutes = 1441 }));
        var plan = VerificationPlan.Performance(options);
        var writes = VerificationPlan.Performance(options with { Suite = "write-performance" });
        Check(writes.Count == 72 && writes.Select(c => c.Id).Distinct().Count() == 72, "write matrix unique repetitions");
        Check(writes.All(c => c.Resident && !c.Writer && c.DelayMs == 0), "write matrix isolated fitting file, no injected delay");
        Check(writes.Count(c => c.Timing) == 36 && writes.Count(c => c.Drain == "Idle") == 24, "write matrix timing and policy controls");
        Check(writes.Where(c => c.Workload == "random-write").All(c => c.QueueDepth is 1 or 32), "CDM random write queue depths");
        var selection = options with { Suite = "write-performance", CaseFilter = "random-write-q1-Idle-timingFalse" };
        var selectedWrites = VerificationPlan.Performance(selection);
        Check(selectedWrites.Count == 3 && selectedWrites.SequenceEqual(writes.Where(test => test.Id.Contains(selection.CaseFilter))),
            "selected repetitions retain complete matrix IDs and workloads");
        Check(!RunStorage.Complete(writes.Select(test => test.Id).ToArray(), selectedWrites.Select(test =>
            new CaseResult(test.Id, "MEASURED", "", DateTimeOffset.UtcNow, 0)).ToArray()), "selected matrix is not full completion");
        Reject(() => VerificationPlan.Validate(selection with { CaseFilter = " " }));
        Reject(() => VerificationPlan.Validate(selection with { CaseFilter = "does-not-exist" }));
        Reject(() => VerificationPlan.Validate(selection with { CaseFilter = "RANDOM-WRITE" }));
        Reject(() => VerificationPlan.Validate(selection with { Suite = "quick" }));
        Check(plan.Count == 204 && plan.Select(c => c.Id).Distinct().Count() == plan.Count, "unique repeated-case identities");
        Check(plan[75].Id == "0076-r2-Automatic-Idle-d0-q32-loaded",
            "stable scenario ordering and readable case identity");
        var focused = VerificationPlan.Performance(options with
        {
            Suite = "flush-interference",
            Repeats = 2
        });
        Check(focused.Count == 8 && focused.Count(c => c.ApplicationFlush) == 4, "focused eight-case contract");
        Check(VerificationPlan.Performance(options with
        {
            Suite = "full"
        }).Count == 216, "full performance + focused scope");
        Check(!RunStorage.Complete(["a", "b"], [new("a", "PASS", "", DateTimeOffset.UtcNow, 0)]), "incomplete rejected");
        Check(!RunStorage.Complete(["a"], [new("a", "FAIL", "", DateTimeOffset.UtcNow, 0)]), "failed rejected");
        Check(RunStorage.Complete(["a"], [new("a", "MEASURED", "", DateTimeOffset.UtcNow, 0)]), "measurement not correctness pass");
        Reject(() => VerificationPlan.Validate(options with { Suite = "typo" }));
        Reject(() => VerificationPlan.Validate(options with { Suite = "quick", Repeats = 0 }));
        try
        {
            VerificationPlan.Validate(options);
            throw new Exception("Missing tool was accepted.");
        }
        catch (ArgumentException ex) { Check(ex.Message.Contains("requires --diskspd") && ex.Message.Contains(VerificationPlan.DiskSpdDownload), "missing option guidance"); }
        var missingTool = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "diskspd.exe");
        try
        {
            VerificationPlan.Validate(options with
            {
                DiskSpd = missingTool
            });
            throw new Exception("Missing file was accepted.");
        }
        catch (FileNotFoundException ex) { Check(ex.Message.Contains(missingTool), "missing executable prints resolved path"); }
        try
        {
            VerificationPlan.Validate(options with
            {
                DiskSpd = Path.GetTempPath()
            });
            throw new Exception("Directory was accepted.");
        }
        catch (ArgumentException ex) { Check(ex.Message.Contains("directory, not an executable"), "directory distinguished from file"); }
        VerificationPlan.Validate(options with
        {
            Suite = "quick"
        });
        VerificationPlan.Validate(options with
        {
            Suite = "policies"
        });
        var epoch = DateTimeOffset.UtcNow;
        TelemetryCoverage.Validate([epoch, epoch.AddSeconds(1), epoch.AddSeconds(2)], epoch, epoch.AddSeconds(2));
        Reject(() => TelemetryCoverage.Validate([epoch.AddSeconds(1), epoch.AddSeconds(2)], epoch, epoch.AddSeconds(2)));
        Reject(() => TelemetryCoverage.Validate([epoch, epoch.AddSeconds(1)], epoch, epoch.AddSeconds(2)));
        Reject(() => TelemetryCoverage.Validate([epoch, epoch.AddSeconds(3)], epoch, epoch.AddSeconds(3)));
        Reject(() => TelemetryCoverage.Validate([epoch, epoch, epoch.AddSeconds(1)], epoch, epoch.AddSeconds(1)));
        var draining = new QueueCache.Management.WriteCacheState(8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        try
        {
            QueueCache.Operations.ConfigurationManager.EnsureHealthy(draining);
            throw new Exception("Drain was accepted.");
        }
        catch (QueueCache.Operations.CacheDrainingException) { }
        try
        {
            QueueCache.Operations.ConfigurationManager.EnsureHealthy(draining with
            {
                LastError = 1
            });
            throw new Exception("Fault was accepted.");
        }
        catch (IOException ex) { Check(ex is not QueueCache.Operations.CacheDrainingException, "faults must never be retried as transient drains"); }
        var healthy = draining with
        {
            Flags = 0
        };
        var original = new RecoverySnapshot(1, identity, healthy, false, "[]", epoch, "fixture");
        var flushOrder = new List<string>();
        var pending = healthy with { DirtyBytes = 10 };
        VerificationWorker.FlushForRestoration(
            () => { flushOrder.Add("volume"); pending = pending with { DirtyBytes = 20 }; },
            () => { flushOrder.Add("cache"); pending = pending with { DirtyBytes = 4 }; },
            () => { flushOrder.Add("disable"); pending = pending with { DirtyBytes = 0, Flags = 0 }; },
            () => pending,
            (phase, snapshot) => flushOrder.Add($"{phase}:{snapshot.DirtyBytes}"));
        Check(flushOrder.SequenceEqual(new[] { "before-volume-flush:10", "volume", "after-volume-flush:20", "cache", "after-cache-flush:4", "disable", "after-cache-disable:0" }),
            "restoration flushes filesystem and cache before disabling/draining late admissions and records each boundary");
        foreach (var failedStage in new[] { "volume", "cache", "disable" })
        {
            flushOrder.Clear();
            var failure = new IOException("flush failure");
            try
            {
                VerificationWorker.FlushForRestoration(
                    () => { flushOrder.Add("volume"); if (failedStage == "volume") throw failure; },
                    () => { flushOrder.Add("cache"); if (failedStage == "cache") throw failure; },
                    () => { flushOrder.Add("disable"); throw failure; },
                    () => healthy,
                    (phase, _) => flushOrder.Add(phase));
                throw new Exception("Failed flush accepted.");
            }
            catch (IOException ex) { Check(ReferenceEquals(ex, failure), "original flush error preserved"); }
            Check(flushOrder.SequenceEqual(failedStage switch
            {
                "volume" => new[] { "before-volume-flush", "volume" },
                "cache" => new[] { "before-volume-flush", "volume", "after-volume-flush", "cache" },
                _ => new[] { "before-volume-flush", "volume", "after-volume-flush", "cache", "after-cache-flush", "disable" }
            }),
                "failed flush stops restoration without recording a successful boundary");
        }
        Check(VerificationWorker.RestorationMismatches(original, healthy, "[]", 0).Count == 0,
            "matching restoration accepted");
        foreach (var (name, changed) in new (string, QueueCache.Management.WriteCacheState)[]
        {
            ("DirtyBytes", healthy with { DirtyBytes = 1 }),
            ("InFlightBytes", healthy with { InFlightBytes = 1 }),
            ("Errors", healthy with { Errors = 1 }),
            ("Instance", healthy with { Instance = 1 }),
            ("BudgetBytes", healthy with { BudgetBytes = 1 }),
            ("Enabled", healthy with { Flags = 1 }),
            ("UnsafeDefer", healthy with { Flags = 32 }),
            ("Options", healthy with { Options = new QueueCache.Management.CacheOptions() })
        })
        {
            var mismatch = VerificationWorker.RestorationMismatches(original, changed, "[]", 0);
            Check(mismatch.Count == 1 && mismatch[0].StartsWith(name + ": expected ") && mismatch[0].Contains(", actual "),
                "restoration preserves and identifies " + name);
        }
        Check(VerificationWorker.RestorationMismatches(original, healthy, "changed", 1).Count == 2,
            "profile and timing mismatches both retained");
        var originalOptions = new QueueCache.Management.CacheOptions();
        Check(VerificationWorker.RestorationMismatches(original with { State = healthy with { Options = originalOptions } },
            healthy with { Options = originalOptions with { } }, "[]", 0).Count == 0,
            "restoration compares option values rather than object identity");
        var reads = 0;
        var settled = QueueCache.Operations.ConfigurationManager.WaitForHealthyState(
            () => ++reads < 3 ? draining : healthy, healthy);
        Check(settled == healthy && reads == 3, "post-apply transient draining is polled without replaying mutations");
        Check(QueueCache.Operations.ConfigurationManager.WaitForHealthyState(() => healthy, draining) == healthy,
            "initial draining may settle too, including disabled cache");
        try
        {
            QueueCache.Operations.ConfigurationManager.WaitForHealthyState(() => draining, healthy, timeout: TimeSpan.Zero);
            throw new Exception("Permanent draining accepted.");
        }
        catch (TimeoutException) { }
        foreach (var invalid in new[] { healthy with { Flags = 2 }, healthy with { Flags = 4 }, healthy with { Flags = 16 },
            healthy with { LastError = 1 }, healthy with { Errors = 1 }, healthy with { Instance = 1 }, healthy with { DeviceBytes = 1 } })
        {
            var attempts = 0;
            try
            {
                QueueCache.Operations.ConfigurationManager.WaitForHealthyState(() => { attempts++; return invalid; }, healthy);
                throw new Exception("Fault or identity change accepted.");
            }
            catch (IOException) { Check(attempts == 1, "non-transient state fails immediately"); }
        }
        try
        {
            QueueCache.Operations.ConfigurationManager.WaitForHealthyState(() => healthy, draining with
            {
                LastError = 1
            });
            throw new Exception("Initial fault hidden.");
        }
        catch (IOException) { }
        // Minimal independent fixture matching Microsoft's XmlResultParser structure, not text columns.
        const string xml = """
            <Results><TimeSpan><TestTimeSeconds>10.00</TestTimeSeconds>
            <Latency><Bucket><Percentile>99</Percentile><ReadMilliseconds>0.123</ReadMilliseconds></Bucket>
            <Bucket><Percentile>99.9</Percentile><ReadMilliseconds>0.456</ReadMilliseconds></Bucket>
            <Bucket><Percentile>100</Percentile><ReadMilliseconds>1.234</ReadMilliseconds></Bucket></Latency>
            <Thread><Target><ReadBytes>40960</ReadBytes><WriteBytes>0</WriteBytes><ReadCount>10</ReadCount><WriteCount>0</WriteCount></Target></Thread>
            <Thread><Target><ReadBytes>40960</ReadBytes><WriteBytes>0</WriteBytes><ReadCount>10</ReadCount><WriteCount>0</WriteCount></Target></Thread>
            </TimeSpan></Results>
            """;
        var score = DiskSpdParser.Parse(xml);
        foreach (var newline in new[] { "\r\n", "\n" })
            Check(DiskSpdParser.Parse(xml + newline + "Score: 0" + newline + "averageLatency: 0.000000" + newline) == score,
                "CDM XML trailer does not replace real measurements");
        Reject(() => DiskSpdParser.Parse(xml + "\nScore: 0"));
        Reject(() => DiskSpdParser.Parse(xml + "\nScore: 0\naverageLatency: 0.000000\nERROR: failed"));
        Reject(() => DiskSpdParser.Parse("ERROR: failed\n" + xml + "\nScore: 0\naverageLatency: 0.000000"));
        Reject(() => DiskSpdParser.Parse(xml + "\nScore: NaN\naverageLatency: 0"));
        Reject(() => DiskSpdParser.Parse(xml.Replace("ReadBytes", "MissingBytes") + "\nScore: 0\naverageLatency: 0.000000"));
        if (Environment.GetEnvironmentVariable("QCACHE_TEST_COMPATIBILITY_RESULTS") is { Length: > 0 } evidence)
        {
            foreach (var variant in new[] { "cdm", "microsoft" })
                foreach (var workload in new[] { "read", "write", "mixed" })
                {
                    var observed = DiskSpdParser.Parse(File.ReadAllText(Path.Combine(evidence, $"{variant}-{workload}-xml-stdout.txt")));
                    Check(observed.Operations > 0, "VM XML fixture: " + variant + " " + workload);
                    Check(workload == "write" ? observed.WriteP99Milliseconds is not null : observed.ReadP99Milliseconds is not null,
                        "VM latency fixture: " + variant + " " + workload);
                }
            Console.WriteLine("All six VM DiskSpd compatibility outputs parsed successfully.");
        }
        Check(score.Operations == 20 && score.Iops == 2 && score.ReadP99Milliseconds == .123 && score.WriteP99Milliseconds is null, "XML totals and latency units");
        var zero = DiskSpdParser.Parse(xml.Replace("<ReadCount>10", "<ReadCount>0").Replace("<ReadBytes>40960", "<ReadBytes>0"));
        Check(zero.Operations == 0 && zero.ReadP99Milliseconds is null, "zero IO means N/A latency");
        Reject(() => DiskSpdParser.Parse(xml.Replace("ReadBytes", "Wrong")));
        Reject(() => DiskSpdParser.Parse(xml.Replace("<Percentile>99</Percentile>", "<Percentile>98</Percentile>")));
        Reject(() => DiskSpdParser.Parse(xml.Replace("10.00", "0")));
        Reject(() => DiskSpdParser.Parse("DiskSpd text output"));
        var store = new RunStorage(Path.GetTempPath());
        try
        {
            var readyPath = store.PathFor("readiness.json");
            var alive = new TaskCompletionSource();
            try
            {
                await TelemetryCoverage.WaitReadyAsync(readyPath, Task.CompletedTask, TimeSpan.FromSeconds(1), CancellationToken.None);
                throw new Exception("Early observer exit accepted.");
            }
            catch (IOException) { }
            try
            {
                await TelemetryCoverage.WaitReadyAsync(readyPath, alive.Task, TimeSpan.Zero, CancellationToken.None);
                throw new Exception("Readiness timeout ignored.");
            }
            catch (TimeoutException) { }
            using (var cancelledReady = new CancellationTokenSource())
            {
                cancelledReady.Cancel();
                try
                {
                    await TelemetryCoverage.WaitReadyAsync(readyPath, alive.Task, TimeSpan.FromSeconds(1), cancelledReady.Token);
                    throw new Exception("Readiness cancellation ignored.");
                }
                catch (OperationCanceledException) { }
            }
            RunStorage.AtomicJson(readyPath, new
            {
                Ready = true
            });
            await TelemetryCoverage.WaitReadyAsync(readyPath, alive.Task, TimeSpan.FromSeconds(1), CancellationToken.None);
            store.Write("state.json", new
            {
                Status = "RUNNING"
            });
            store.Write("state.json", new
            {
                Status = "COMPLETED"
            });
            Check(File.ReadAllText(store.PathFor("state.json")).Contains("COMPLETED"), "atomic replace");
            Reject(() => store.PathFor("../escape"));
            store.Add(new("one", "PASS", "", DateTimeOffset.UtcNow, 1));
            Reject(() => store.Add(new("one", "PASS", "", DateTimeOffset.UtcNow, 1)));
            var executable = Environment.ProcessPath!;
            string[] prefix = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? [Assembly.GetEntryAssembly()!.Location] : [];
            var process = await OwnedProcess.RunAsync(executable, [.. prefix, "--runner-child", "two words", "--argument", "Q:\\a b.dat"], store.PathFor("child"), TimeSpan.FromSeconds(20), CancellationToken.None);
            Check(process.ExitCode == 0 && process.Error.Contains("captured stderr"), "stdout/stderr persisted");
            Check(JsonSerializer.Deserialize<string[]>(process.Output)!.SequenceEqual(["two words", "--argument", "Q:\\a b.dat"]), "exact argument vector");
            try
            {
                await OwnedProcess.RunAsync(executable, [.. prefix, "--runner-child", "hang"], store.PathFor("timeout"), TimeSpan.FromMilliseconds(300), CancellationToken.None);
                throw new Exception("Deadline did not fire.");
            }
            catch (TimeoutException) { }
            using var cancel = new CancellationTokenSource(300);
            try
            {
                await OwnedProcess.RunAsync(executable, [.. prefix, "--runner-child", "hang"], store.PathFor("cancel"), TimeSpan.FromSeconds(20), cancel.Token);
                throw new Exception("Cancellation did not fire.");
            }
            catch (OperationCanceledException) { }
            var completeOutput = typeof(OwnedProcess).GetMethod("CompleteOutputAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            var pendingOutput = new TaskCompletionSource();
            foreach (var primary in new Exception[] { new TimeoutException("fixture deadline"), new IOException("fixture PID did not exit"), new OperationCanceledException() })
            {
                var pipeEvidence = store.PathFor("pipe-" + primary.GetType().Name);
                await (Task)completeOutput.Invoke(null, [pendingOutput.Task, Task.CompletedTask, pipeEvidence, primary, TimeSpan.Zero])!;
                Check(primary.Data["OutputPipeFailure"] is string && File.ReadAllText(pipeEvidence + ".pipe-failure.json").Contains(primary.GetType().Name),
                    "pipe timeout preserves primary termination failure and evidence");
            }
            try
            {
                await (Task)completeOutput.Invoke(null, [pendingOutput.Task, Task.CompletedTask, store.PathFor("pipe-only"), null, TimeSpan.Zero])!;
                throw new Exception("Open pipe without primary failure accepted.");
            }
            catch (IOException ex) { Check(ex.Message.Contains("Output pipes did not close"), "standalone pipe failure remains fatal"); }
            pendingOutput.SetResult();
            OwnedProcess.EnsureStopped(store.DirectoryPath);
            foreach (var mode in new[] { "success", "capture-failure", "check-failure", "restore-failure", "identity-failure", "observer-failure", "cancel" })
            {
                var runParent = store.PathFor(mode);
                var runner = new VerificationRunner(executable, [.. prefix, "--fake-verification", mode], store.PathFor("leases"));
                using var cancellation = new CancellationTokenSource();
                var messages = new List<string>();
                var progress = new InlineProgress(message =>
                {
                    messages.Add(message);
                    if (mode == "cancel" && message.EndsWith("Starting file-integrity"))
                        cancellation.CancelAfter(300);
                });
                var exit = await runner.RunAsync(new("Q:", Output: runParent), progress, cancellation.Token);
                var directory = Directory.GetDirectories(runParent).Single();
                var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "status.json")));
                var expectedStatus = mode switch
                {
                    "success" => "COMPLETED",
                    "capture-failure" or "check-failure" or "identity-failure" => "INCOMPLETE",
                    "restore-failure" or "observer-failure" => "RESTORATION_FAILED",
                    _ => "CANCELLED"
                };
                Check(state.RootElement.GetProperty("Status").GetString() == expectedStatus, "coordinator " + mode);
                Check((exit == 0) == (mode == "success") && File.Exists(Path.Combine(directory, "FINISHED.txt")), "exit/marker " + mode);
                Check(File.Exists(Path.Combine(directory, "restored.json")) == (mode is not ("restore-failure" or "capture-failure" or "observer-failure")), "independent restoration " + mode);
                OwnedProcess.EnsureStopped(directory);
                if (mode == "observer-failure")
                    Check(!Directory.GetFiles(directory, "*-restore.job.json").Any(), "restore must not bypass observer readiness");
                var log = File.ReadAllText(Path.Combine(directory, "run.log"));
                Check(log.Contains(expectedStatus + ":") && messages.Last().Contains(expectedStatus + ":"), "final status logged and delivered before return " + mode);
                Check(log.Contains("Starting worker-") && log.Contains("Finished worker-"), "worker progress persisted " + mode);
                Check(log.Contains("overall limit: unlimited") && log.Contains("[Preflight | 0/1 completed]"), "unlimited run and total shown " + mode);
                if (mode != "capture-failure")
                    Check(messages.Any(m => m.Contains("[Test 1 of 1] Starting worker-")) && messages.Any(m => m.Contains("[Restoring |")), "case progress on child logs and restoration " + mode);
                if (mode == "identity-failure")
                    Check(log.Contains("Worker volume disagrees with the recorded target"), "real worker rejects inconsistent target before native access and recovery still runs");
                else if (mode.EndsWith("failure"))
                    Check(log.Contains("fixture failure detail") && messages.Any(m => m.Contains("fixture failure detail")), "actual child error visible " + mode);
            }
            if (Environment.GetEnvironmentVariable("QCACHE_TEST_DISKSPD") is { Length: > 0 } diskspd)
            {
                var actual = await OwnedProcess.RunAsync(diskspd, ["-c16M", "-b4K", "-o1", "-t1", "-w0", "-d1", "-W0", "-S", "-L", "-Rxml", store.PathFor("fixture.dat")],
                    store.PathFor("real-diskspd"), TimeSpan.FromSeconds(30), CancellationToken.None);
                Check(actual.ExitCode == 0 && DiskSpdParser.Parse(actual.Output).Operations > 0, "real standard DiskSpd XML smoke check");
            }
        }
        finally
        {
            // Only this test's freshly created unique temp directory is owned here.
            Directory.Delete(store.DirectoryPath, recursive: true);
        }
        Console.WriteLine("Verification runner contracts passed (no driver or workload-disk access).");
    }

    public static async Task<int> FakeWorkerAsync(string mode, string path)
    {
        var job = JsonSerializer.Deserialize<WorkerJob>(await File.ReadAllTextAsync(path))!;
        if (mode == "identity-failure" && job.Operation == "files")
        {
            RunStorage.AtomicJson(path, job with
            {
                Volume = "R:"
            });
            try
            {
                return await VerificationWorker.ExecuteAsync(path);
            }
            catch (IOException ex) { Console.Error.WriteLine(ex); return 1; }
        }
        if (job.Operation == "telemetry")
        {
            if (mode == "observer-failure")
            {
                Console.Error.WriteLine("fixture failure detail: target discovery timed out before telemetry readiness.");
                return 1;
            }
            RunStorage.AtomicJson(job.Reply, new
            {
                Fake = true
            });
            if (job.ReadyFile is not null)
                RunStorage.AtomicJson(job.ReadyFile, new
                {
                    Ready = true
                });
            while (!File.Exists(job.StopFile))
                await Task.Delay(20);
            return 0;
        }
        if (mode == "cancel" && job.Operation == "files")
            await Task.Delay(Timeout.Infinite);
        if (mode == "check-failure" && job.Operation == "files" || mode == "restore-failure" && job.Operation == "restore" || mode == "capture-failure" && job.Operation == "capture")
        {
            Console.Error.WriteLine("fixture failure detail: Access is denied.");
            return 1;
        }
        object reply = new
        {
            Fake = true
        };
        if (job.Operation == "capture")
            reply = new RecoverySnapshot(1, new QueueCache.Operations.DiskTarget('Q', 99999, 50L << 30, "fixture-only"),
                new QueueCache.Management.WriteCacheState(0, 0, 50UL << 30, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
                false, "[]", DateTimeOffset.UtcNow, Environment.MachineName);
        RunStorage.AtomicJson(job.Reply, reply);
        return 0;
    }
}
