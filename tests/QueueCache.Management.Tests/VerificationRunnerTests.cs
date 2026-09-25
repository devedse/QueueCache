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
        Check(VerificationPlan.Version == 44, "paging overlap tolerates admitted metadata pages in flight");
        Check(VerificationPlan.Integrity(new VerificationOptions("Q:", "paging-coherence"))
            .SequenceEqual([new IntegrityCase("paging-coherence", "paging-coherence")]),
            "mixed paging/file check is one maintained non-OS case");
        var gateOrder = new QueueCache.Management.CacheLabGate(3, 1, 10, 11, 13, 12, 14, 15);
        Check(QueueCache.Operations.PagingCoherenceScenarios.VerifyGateOrder(gateOrder).Contains("< old retirement 13 <"),
            "gated direct paging write waited for the submitted old drain's retirement");
        foreach (var bad in new[]
        {
            gateOrder with { Hits = 0 }, gateOrder with { DirectWaitSeq = 0 },
            gateOrder with { DirectSubmitSeq = 13 }, // submitted without waiting for the old drain's retirement
            gateOrder with { DirectWaitSeq = 10 },   // started waiting before the old write reached the device
            gateOrder with { OldLowerDoneSeq = 9 }
        })
        {
            try
            {
                QueueCache.Operations.PagingCoherenceScenarios.VerifyGateOrder(bad);
                throw new Exception("Out-of-order or incomplete gate evidence accepted.");
            }
            catch (IOException) { }
        }
        Check(VerificationPlan.Integrity(new VerificationOptions("Q:", "ordering-faults"))
            .SequenceEqual([new IntegrityCase("ordering-faults", "ordering-faults")]), "fault ordering is one maintained non-OS case");
        Check(VerificationPlan.Integrity(new VerificationOptions("Q:", "app-write-profile"))
            .SequenceEqual([new IntegrityCase("app-write-profile", "app-write-profile")]), "application write profile is one non-OS case");
        var profileBefore = new QueueCache.Operations.AppWriteProfileScenarios.Counters(0, 0, 0, 0, 0);
        var profileText = QueueCache.Operations.AppWriteProfileScenarios.Describe("buffered-close", 500, 1200,
            profileBefore, profileBefore with { PagingWriteBytes = 256UL << 20, PagingForwardedWrites = 64 });
        Check(profileText.Contains("RAM-admitted 0.0 MiB") && profileText.Contains("paging-marked writes 256.0 MiB") &&
            profileText.Contains("512 MiB/s"), "application write profile reports admitted versus forwarded bytes");
        var admittedPaging = profileBefore with { Accepted = 128UL << 20, PagingWriteBytes = 128UL << 20,
            PagingAdmittedBytes = 128UL << 20 };
        Check(QueueCache.Operations.AppWriteProfileScenarios.Arrived(profileBefore, admittedPaging) == 128UL << 20,
            "admitted paging writes are not counted twice while waiting for a file to arrive");
        Check(VerificationPlan.Integrity(new VerificationOptions("C:", "system-paging-recognition"))
            .SequenceEqual([new IntegrityCase("system-paging-recognition", "system-paging-recognition")]),
            "paging recognition is one guarded system case");
        var admittedAll = new Dictionary<string, ulong> { ["buffered-close"] = 256UL << 20, ["mapped-flush"] = 250UL << 20 };
        Check(QueueCache.Operations.AppWriteProfileScenarios.VerifyAdmission(true, admittedAll).Result == "PASS",
            "application writes admitted to RAM pass");
        Check(QueueCache.Operations.AppWriteProfileScenarios.VerifyAdmission(false, admittedAll).Result == "SKIP",
            "older drivers cannot claim application admission");
        try
        {
            QueueCache.Operations.AppWriteProfileScenarios.VerifyAdmission(true,
                new Dictionary<string, ulong> { ["buffered-flush"] = 0 });
            throw new Exception("Unadmitted application writes accepted.");
        }
        catch (IOException) { }
        Check(QueueCache.Operations.AppWriteProfileScenarios.VerifyPoolIndependent(true, 100UL << 20, 110UL << 20, 1024UL << 20)
            .Result == "PASS", "a small nonpaged delta proves page-backed cache memory");
        try
        {
            QueueCache.Operations.AppWriteProfileScenarios.VerifyPoolIndependent(true, 100UL << 20, 1124UL << 20, 1024UL << 20);
            throw new Exception("Pool-backed cache payload accepted.");
        }
        catch (IOException) { }
        var recognitionBefore = new QueueCache.Management.CachePagingAdmission(0, 0, 0, 10, 1, 0, 0, 0, 3);
        var ioBefore = new QueueCache.Management.CachePagingIo(5, 0, 5, 0, 0, 0, 0, 0, 0);
        var recognised = QueueCache.Operations.PagingRecognitionScenarios.Evaluate(recognitionBefore,
            recognitionBefore with { PagingFileRequests = 40 }, ioBefore, ioBefore with { ReadRequests = 25, WriteRequests = 20 }, "test");
        Check(recognised.All(check => check.Result == "PASS"), "recognised paging-file I/O without misses passes");
        Check(QueueCache.Operations.PagingRecognitionScenarios.Evaluate(recognitionBefore, recognitionBefore, ioBefore, ioBefore, "idle")
            .Any(check => check.Result == "SKIP"), "no paging-file I/O leaves recognition unexercised");
        try
        {
            QueueCache.Operations.PagingRecognitionScenarios.Evaluate(recognitionBefore,
                recognitionBefore with { PagingFileRequests = 40, ReferenceMisses = 1 }, ioBefore, ioBefore, "miss");
            throw new Exception("An unrecognised paging-file request was accepted.");
        }
        catch (IOException) { }
        var failedGate = new QueueCache.Management.CacheLabGate(3, 1, 1, 2, 4, 3, 0, 0);
        Check(QueueCache.Operations.OrderingFaultScenarios.VerifyFailedOldDrain(failedGate, true, true, 4096)
            .Contains("never submitted"), "failed old drain stops the waiting paging write before submission");
        foreach (var (gate, failed, faulted, dirty) in new[]
        {
            (failedGate with { DirectSubmitSeq = 5 }, true, true, 4096UL),  // submitted despite the failure
            (failedGate, false, true, 4096UL),                               // false success
            (failedGate, true, false, 4096UL),                               // no fault
            (failedGate, true, true, 0UL),                                   // dirty version lost
            (failedGate with { DirectWaitSeq = 0 }, true, true, 4096UL)      // never waited
        })
        {
            try
            {
                QueueCache.Operations.OrderingFaultScenarios.VerifyFailedOldDrain(gate, failed, faulted, dirty);
                throw new Exception("Unsafe failed-drain ordering accepted.");
            }
            catch (IOException) { }
        }
        var shortGate = new QueueCache.Management.CacheLabGate(3, 1, 1, 2, 3, 0, 0, 0);
        Check(QueueCache.Operations.OrderingFaultScenarios.VerifyShortSparse(shortGate, true, true, 6144, 2048)
            .Contains("stayed dirty"), "short sparse completion keeps the whole version dirty");
        foreach (var (flushFailed, faulted, dirty) in new[] { (false, true, 2048UL), (true, false, 2048UL), (true, true, 1024UL) })
        {
            try
            {
                QueueCache.Operations.OrderingFaultScenarios.VerifyShortSparse(shortGate, flushFailed, faulted, dirty, 2048);
                throw new Exception("Unsafe short sparse completion accepted.");
            }
            catch (IOException) { }
        }
        var offloadBefore = new QueueCache.Management.CachePagingOffload(5, 5, 0, 0, 0, 1);
        var offloadAfter = offloadBefore with { OffloadedReads = 8, Completions = 8 };
        Check(QueueCache.Operations.PagingCoherenceScenarios.VerifyBlockedPageIn(offloadBefore, offloadAfter, true, 4, 3.0)
            .Result == "PASS", "page-ins completed on the paging thread while the writer was blocked");
        Check(QueueCache.Operations.PagingCoherenceScenarios.VerifyBlockedPageIn(offloadBefore, offloadAfter, false, 4, 3.0)
            .Result == "SKIP", "a writer that finished first leaves the dependency unproven");
        Check(QueueCache.Operations.PagingCoherenceScenarios.VerifyBlockedPageIn(offloadBefore, offloadBefore, true, 4, 3.0)
            .Result == "SKIP", "page-ins served without the paging thread leave the dependency unproven");
        try
        {
            QueueCache.Operations.PagingCoherenceScenarios.VerifyBlockedPageIn(offloadBefore,
                offloadAfter with { Failures = 1 }, true, 4, 3.0);
            throw new Exception("Failed offloaded page-in accepted.");
        }
        catch (IOException) { }
        var overlapBefore = new QueueCache.Management.CachePagingRoute(0, 0, 0, 0, 0, 0, 0);
        var overlapAfter = overlapBefore with { WriteRequests = 1, WriteCompletions = 1, OverlapWaits = 1 };
        Check(QueueCache.Operations.PagingCoherenceScenarios.VerifyObservedOverlap(overlapBefore, overlapAfter, 1536)
            .Contains("isolated 1536-byte older write"), "mixed-I/O case requires observed sparse in-flight payload");
        Check(QueueCache.Operations.PagingCoherenceScenarios.VerifyObservedOverlap(overlapBefore, overlapAfter, 1536 + 8192)
            .Contains("9728 bytes in flight"), "admitted 4 KiB metadata pages may share the sparse in-flight interval");
        foreach (var (observed, route) in new[]
        {
            (0UL, overlapAfter), (4096UL, overlapAfter), (2048UL, overlapAfter), (1536UL, overlapBefore),
            (1536UL, overlapAfter with { WriteCompletions = 0 }),
            (1536UL, overlapAfter with { WriteFailures = 1 }),
            (1536UL, overlapAfter with { OverlapWaits = 0 })
        })
        {
            try
            {
                QueueCache.Operations.PagingCoherenceScenarios.VerifyObservedOverlap(overlapBefore, route, observed);
                throw new Exception("Unobserved paging/drainer overlap accepted.");
            }
            catch (IOException) { }
        }
        var usageActivity = new QueueCache.Management.CacheUsageActivities(
            new(2, 0, 2, 0, 0, 0, 556), new(0, 0, 0, 0, 0, 0, 0), new(0, 0, 0, 0, 0, 0, 0));
        var usageDiagnostics = new QueueCache.Management.CacheDiagnostics(0, 0, 0, 0, 0, 0, 0, 0, 0)
        {
            UsagePaths = new(2, 0, 0), UsageActivity = usageActivity
        };
        var usageStatistics = new QueueCache.Management.CacheStatistics(false, 0, 100L << 30, 0, 0, 0, 0, 0,
            2, 0, 0, 0);
        VerificationWorker.ValidateSystemUsageDiagnostics(usageStatistics, usageDiagnostics);
        foreach (var invalid in new[]
        {
            usageDiagnostics with { UsageActivity = null },
            usageDiagnostics with { UsagePaths = new(1, 0, 0) },
            usageDiagnostics with { UsageActivity = usageActivity with { Paging = usageActivity.Paging with { InSuccesses = 1 } } },
            usageDiagnostics with { UsageActivity = usageActivity with { Paging = usageActivity.Paging with { OutRequests = 1 } } }
        })
        {
            try
            {
                VerificationWorker.ValidateSystemUsageDiagnostics(usageStatistics, invalid);
                throw new Exception("Incoherent system usage lifecycle accepted.");
            }
            catch (Exception e) when (e is IOException or NotSupportedException) { }
        }
        var preflight = new VerificationOptions("C:", "system-preflight", "Q:\\results",
            SystemInstance: "SCSI\\TEST", SystemBytes: 100L << 30, RecoverableVm: true);
        VerificationPlan.Validate(preflight);
        Check(VerificationPlan.Integrity(preflight).SequenceEqual(new IntegrityCase[] { new("system-preflight", "system-preflight") }),
            "system preflight is separate from mutating suites");
        Reject(() => VerificationPlan.Validate(preflight with { RecoverableVm = false }));
        Reject(() => VerificationPlan.Validate(preflight with { SystemInstance = null }));
        Reject(() => VerificationPlan.Validate(preflight with { SystemBytes = null }));
        Reject(() => VerificationPlan.Validate(preflight with { Volume = "Q:" }));
        Reject(() => VerificationPlan.Validate(preflight with { Suite = "quick" }));
        var systemFiles = preflight with { Suite = "system-files" };
        var postRestart = preflight with { Suite = "system-post-restart", OraclePath = "Q:\\prior\\oracle.json" };
        VerificationPlan.Validate(systemFiles);
        VerificationPlan.Validate(postRestart);
        Check(VerificationPlan.Integrity(systemFiles).SequenceEqual(new IntegrityCase[] { new("system-file-create", "system-file-create") }),
            "system-file creation is one bounded case");
        Check(VerificationPlan.Integrity(postRestart).SequenceEqual(new IntegrityCase[] { new("system-file-verify", "system-file-verify") }),
            "post-restart verification never repeats creation");
        Reject(() => VerificationPlan.Validate(postRestart with { OraclePath = null }));
        Reject(() => VerificationPlan.Validate(systemFiles with { OraclePath = "Q:\\prior\\oracle.json" }));
        Reject(() => VerificationPlan.Validate(postRestart with { RecoverableVm = false }));
        var activeImage = preflight with { Suite = "system-active-image", BudgetMiB = 512 };
        var imageBaseline = preflight with { Suite = "system-image-baseline" };
        VerificationPlan.Validate(activeImage);
        VerificationPlan.Validate(imageBaseline);
        Check(VerificationPlan.Integrity(imageBaseline).SequenceEqual(new IntegrityCase[] { new("system-image-baseline", "system-image-baseline") }),
            "uncached system-image baseline is a separate bounded case");
        Check(VerificationWorker.AllowsSystemUsagePaths("system-capture") &&
            VerificationWorker.AllowsSystemUsagePaths("system-active-image") &&
            VerificationWorker.AllowsSystemUsagePaths("system-restore") &&
            VerificationWorker.AllowsSystemUsagePaths("system-file-create") &&
            VerificationWorker.AllowsSystemUsagePaths("system-file-verify") &&
            !VerificationWorker.AllowsSystemUsagePaths("system-image-baseline"),
            "active capture/workload/restoration accept reconciled system usage paths");
        var pagingBefore = usageDiagnostics with
        {
            PagingIo = new QueueCache.Management.CachePagingIo(10, 1000, 20, 2000, 3, 2, 3000, 4096, 4)
        };
        var pagingAfter = usageDiagnostics with
        {
            PagingIo = new QueueCache.Management.CachePagingIo(13, 1120, 22, 2256, 4, 2, 4000, 8192, 8)
        };
        Check(VerificationWorker.PagingIoDelta(pagingBefore, pagingAfter) ==
            new QueueCache.Management.CachePagingIo(3, 120, 2, 256, 4, 2, 4000, 8192, 8),
            "paging I/O window uses lifetime-counter deltas and preserves last-request breadcrumbs");
        try
        {
            VerificationWorker.PagingIoDelta(pagingBefore,
                pagingAfter with { PagingIo = pagingAfter.PagingIo! with { ReadRequests = 9 } });
            throw new Exception("Backward paging I/O counters accepted.");
        }
        catch (IOException) { }
        try
        {
            VerificationWorker.PagingIoDelta(pagingBefore with { PagingIo = null }, pagingAfter);
            throw new Exception("Missing paging I/O diagnostics accepted.");
        }
        catch (NotSupportedException) { }
        var guardedPaging = pagingAfter with
        {
            PagingProgress = new QueueCache.Management.CachePagingProgress(5, 7, 9, 0, 1UL << 20, 2UL << 20),
            PagingRoute = new QueueCache.Management.CachePagingRoute(10, 10, 0, 20, 20, 0, 1)
        };
        VerificationWorker.ValidateActiveSystemPaths(usageStatistics, guardedPaging);
        var allSystemActivity = new QueueCache.Management.CacheUsageActivities(
            usageActivity.Paging, new(1, 0, 1, 0, 0, 0, 600), new(1, 0, 1, 0, 0, 0, 601));
        VerificationWorker.ValidateActiveSystemPaths(
            usageStatistics with { PagingPathCount = 4 },
            guardedPaging with
            {
                UsagePaths = new(2, 1, 1),
                UsageActivity = allSystemActivity
            });
        var progressAfter = guardedPaging;
        Check(VerificationWorker.ValidatePagingProgressWindow(guardedPaging, progressAfter) ==
            new QueueCache.Management.CachePagingProgress(0, 0, 0, 0, 1UL << 20, 2UL << 20),
            "active paging window has no mapping or capacity failure");
        Check(VerificationWorker.ValidatePagingProgressWindow(guardedPaging,
            guardedPaging with { PagingProgress = guardedPaging.PagingProgress! with { ServicedReadMisses = 10 } })
            .ServicedReadMisses == 1, "cooperative paging read may complete");
        Check(VerificationWorker.ValidatePagingRouteWindow(guardedPaging, guardedPaging) ==
            new QueueCache.Management.CachePagingRoute(0, 0, 0, 0, 0, 0, 0),
            "routed paging completion window");
        var routedAfter = guardedPaging with
        {
            PagingRoute = guardedPaging.PagingRoute! with
            {
                ReadRequests = 12, ReadCompletions = 12,
                WriteRequests = 21, WriteCompletions = 21, OverlapWaits = 2
            }
        };
        Check(VerificationWorker.ValidatePagingRouteWindow(guardedPaging, routedAfter) ==
            new QueueCache.Management.CachePagingRoute(2, 2, 0, 1, 1, 0, 1),
            "routed paging actual completion deltas");
        try
        {
            VerificationWorker.ValidatePagingRouteWindow(guardedPaging,
                routedAfter with { PagingRoute = routedAfter.PagingRoute! with { ReadFailures = 1 } });
            throw new Exception("Failed routed paging request accepted.");
        }
        catch (IOException) { }
        foreach (var invalid in new[]
        {
            progressAfter with { PagingProgress = progressAfter.PagingProgress! with { MapFailures = 6 } },
            progressAfter with { PagingProgress = progressAfter.PagingProgress! with { CapacityWaits = 8 } },
            progressAfter with { PagingProgress = progressAfter.PagingProgress! with { ReservedBytes = 1 } }
        })
        {
            try
            {
                VerificationWorker.ValidatePagingProgressWindow(guardedPaging, invalid);
                throw new Exception("Unsafe paging progress window accepted.");
            }
            catch (IOException) { }
        }
        Check(VerificationPlan.Integrity(activeImage).SequenceEqual(new IntegrityCase[]
        {
            new("system-active-image-fast", "system-active-image"),
            new("system-active-image-strict", "system-active-image")
        }), "active system-image covers normal Fast and Strict product paths");
        var imageRoot = "C:\\QueueCache-System-0123456789abcdef0123456789abcdef";
        var imageTarget = new QueueCache.Operations.DiskTarget('C', 0, 100L << 30, "SCSI\\TEST", true, true, true);
        var fastArtifacts = VerificationRunner.SystemImageArtifacts(imageRoot, "system-active-image-fast");
        var strictArtifacts = VerificationRunner.SystemImageArtifacts(imageRoot, "system-active-image-strict");
        Check(fastArtifacts.WorkDirectory != strictArtifacts.WorkDirectory &&
              fastArtifacts.OracleFile != strictArtifacts.OracleFile &&
              Path.GetFileName(fastArtifacts.OracleFile) == fastArtifacts.OracleFile &&
              Path.GetFileName(strictArtifacts.OracleFile) == strictArtifacts.OracleFile,
            "active Fast and Strict cases retain separate workload and oracle evidence");
        QueueCache.Operations.SystemImageScenarios.ValidateOwnedPath(imageTarget,
            fastArtifacts.WorkDirectory, Path.Combine(fastArtifacts.WorkDirectory, "large-image.bmp"));
        QueueCache.Operations.SystemImageScenarios.ValidateOwnedPath(imageTarget,
            strictArtifacts.WorkDirectory, Path.Combine(strictArtifacts.WorkDirectory, "large-image.bmp"));
        try
        {
            var invalidDirectory = imageRoot + "-system-active-image-other";
            QueueCache.Operations.SystemImageScenarios.ValidateOwnedPath(imageTarget,
                invalidDirectory, Path.Combine(invalidDirectory, "large-image.bmp"));
            throw new Exception("Unknown active-image directory suffix accepted.");
        }
        catch (IOException) { }
        Reject(() => VerificationRunner.SystemImageArtifacts("C:\\QueueCache-System-run", "../bad"));
        Reject(() => VerificationPlan.Validate(activeImage with { BudgetMiB = 1024 }));
        QueueCache.Management.WriteCacheState ActiveState(ulong accepted, uint flags = 1 | 256 | 512, ulong instance = 7, ulong errors = 0) =>
            new(flags, 0, 100UL << 30, 512UL << 20, 512UL << 20, 0, 0, 500UL << 20, 0,
                accepted, 0, 0, 0, errors, 0, 0) { Instance = instance };
        var enabledImageState = ActiveState(1000);
        VerificationWorker.ValidateActiveImageRouting(enabledImageState,
            ActiveState(1000 + (ulong)QueueCache.Operations.SystemImageScenarios.FileBytes),
            (ulong)QueueCache.Operations.SystemImageScenarios.FileBytes);
        foreach (var invalid in new[]
        {
            ActiveState(1000 + (ulong)QueueCache.Operations.SystemImageScenarios.FileBytes, flags: 0),
            ActiveState(1000 + (ulong)QueueCache.Operations.SystemImageScenarios.FileBytes - 1),
            ActiveState(1000 + (ulong)QueueCache.Operations.SystemImageScenarios.FileBytes, instance: 8),
            ActiveState(1000 + (ulong)QueueCache.Operations.SystemImageScenarios.FileBytes, errors: 1)
        })
        {
            try
            {
                VerificationWorker.ValidateActiveImageRouting(enabledImageState, invalid,
                    (ulong)QueueCache.Operations.SystemImageScenarios.FileBytes);
                throw new Exception("Invalid active image routing evidence accepted.");
            }
            catch (IOException) { }
        }
        var passedImage = new CaseResult("system-active-image-fast", "PASS", "", DateTimeOffset.UtcNow, 1);
        Check(VerificationWorker.RequiresSystemImageEvidence([passedImage]),
            "passed active image requires post-release byte evidence");
        Check(!VerificationWorker.RequiresSystemImageEvidence([passedImage with { Status = "FAIL" }]),
            "failed active image permits restoration without nonexistent image evidence");
        var systemTarget = new QueueCache.Operations.DiskTarget('C', 0, 100L << 30, "SCSI\\TEST", true, true, true);
        var resultsTarget = new QueueCache.Operations.DiskTarget('Q', 1, 200L << 30, "SCSI\\RESULTS");
        Check(VerificationRunner.IsSystemRecoveryTarget(systemTarget),
            "C boot/system recovery selects guarded system restoration");
        Check(!VerificationRunner.IsSystemRecoveryTarget(resultsTarget),
            "ordinary disk recovery retains generic restoration");
        SystemPreflightGuard.ValidateTargets(systemTarget, resultsTarget, "SCSI\\TEST", 100L << 30);
        foreach (var invalid in new[] { resultsTarget with { Number = 0 }, resultsTarget with { Instance = "SCSI\\TEST" } })
        {
            try { SystemPreflightGuard.ValidateTargets(systemTarget, invalid, "SCSI\\TEST", 100L << 30); throw new Exception("Same-disk results accepted."); }
            catch (IOException) { }
        }
        try { SystemPreflightGuard.ValidateTargets(systemTarget with { IsBoot = false }, resultsTarget, "SCSI\\TEST", 100L << 30); throw new Exception("Non-boot target accepted."); }
        catch (IOException) { }
        try { SystemPreflightGuard.ValidateTargets(systemTarget, resultsTarget, "WRONG", 100L << 30); throw new Exception("Wrong identity accepted."); }
        catch (IOException) { }
        SystemPreflightGuard.ValidateRecordedTarget(systemTarget with { IsPaging = false }, systemTarget with { IsPaging = true });
        QueueCache.Operations.DiskTarget.ValidateRecordedSystemTarget(
            systemTarget with { IsPaging = true, Instance = "scsi\\test" },
            systemTarget with { IsPaging = false });
        QueueCache.Operations.DiskTarget.ValidateRecordedSystemTarget(
            systemTarget with { IsPaging = false }, systemTarget with { IsPaging = true });
        foreach (var changed in new[]
        {
            systemTarget with { Letter = 'D' },
            systemTarget with { Number = 2 },
            systemTarget with { Bytes = 99L << 30 },
            systemTarget with { Instance = "SCSI\\OTHER" },
            systemTarget with { IsBoot = false },
            systemTarget with { IsSystem = false }
        })
        {
            try { SystemPreflightGuard.ValidateRecordedTarget(systemTarget, changed); throw new Exception("Changed post-restart disk identity accepted."); }
            catch (IOException) { }
        }
        foreach (var changed in new[] { systemTarget with { Number = 2 }, systemTarget with { IsBoot = false } })
        {
            try { QueueCache.Operations.DiskTarget.ValidateRecordedSystemTarget(systemTarget, changed); throw new Exception("Worker identity accepted a different system disk."); }
            catch (IOException) { }
        }
        var imageCases = new[]
        {
            new CaseResult("system-active-image-fast", "PASS", "", DateTimeOffset.UtcNow, 1),
            new CaseResult("system-active-image-strict", "PASS", "", DateTimeOffset.UtcNow, 1)
        };
        var imageOracles = VerificationRunner.PassedSystemImageOracles("Q:\\verification", imageCases);
        Check(imageOracles.Length == 2 && imageOracles[0].EndsWith("system-active-image-fast.oracle.json") &&
              imageOracles[1].EndsWith("system-active-image-strict.oracle.json"),
            "both successful image cases retain independent post-release oracles");
        var ownedDirectory = "C:\\QueueCache-System-0123456789abcdef0123456789abcdef";
        QueueCache.Operations.SystemFileScenarios.ValidateOwnedPath(systemTarget, ownedDirectory, ownedDirectory + "\\payload.bin");
        foreach (var badPath in new[] { "C:\\Windows\\payload.bin", ownedDirectory + "\\other.bin", "Q:\\QueueCache-System-0123456789abcdef0123456789abcdef\\payload.bin" })
        {
            try { QueueCache.Operations.SystemFileScenarios.ValidateOwnedPath(systemTarget, Path.GetDirectoryName(badPath)!, badPath); throw new Exception("Unowned file path accepted."); }
            catch (IOException) { }
        }
        var expectedHash = QueueCache.Operations.SystemFileScenarios.ExpectedHash(104729);
        Check(expectedHash.Length == 64 && expectedHash.All(Uri.IsHexDigit) &&
            expectedHash != QueueCache.Operations.SystemFileScenarios.ExpectedHash(104759),
            "expected SHA is deterministic input-derived, not copied from file reads");
        var expectedImageHash = QueueCache.Operations.SystemImageScenarios.ExpectedHash(104729);
        Check(expectedImageHash.Length == 64 && expectedImageHash.All(Uri.IsHexDigit) &&
            expectedImageHash != QueueCache.Operations.SystemImageScenarios.ExpectedHash(104759),
            "large BMP SHA is deterministic input-derived, not copied from file reads");
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
        static QueueCache.Management.CacheDiagnostics AdmissionDiagnostics(QueueCache.Management.CacheAttribution? attribution,
            QueueCache.Management.CacheLowerSources? sources = null) =>
            new(0, 0, 0, 0, 0, 0, 0, 0, 0) { Attribution = attribution, LowerSources = sources };
        var noSources = AdmissionDiagnostics(admissionAttempts);
        var zeroIoAdmission = QueueCache.Operations.SectorScenarios.VerifyAdmissionAttempts(noSources, noSources);
        Check(zeroIoAdmission.Status == "PASS" && zeroIoAdmission.Detail.Contains("before=1/2/3, after=1/2/3"),
            "zero lower attempts support the admission assertion");
        foreach (var changed in new[] { admissionAttempts with { LowerReadAttempts = 2 }, admissionAttempts with { LowerWriteAttempts = 3 },
            admissionAttempts with { LowerFlushAttempts = 4 } })
        {
            var unattributed = QueueCache.Operations.SectorScenarios.VerifyAdmissionAttempts(noSources, AdmissionDiagnostics(changed));
            Check(unattributed.Status == "SKIP" && unattributed.Detail.Contains("UNPROVEN") &&
                unattributed.Detail.Contains("not causal attribution"),
                "without V8 source attribution any lower attempt keeps admission unproven");
        }
        var sources = new QueueCache.Management.CacheLowerSources(5, 6, 7, 8, 9);
        var withSources = AdmissionDiagnostics(admissionAttempts, sources);
        var pagingOnly = QueueCache.Operations.SectorScenarios.VerifyAdmissionAttempts(withSources,
            AdmissionDiagnostics(admissionAttempts with { LowerReadAttempts = 2, LowerWriteAttempts = 4 },
                sources with { PagingForwardedWrites = 9, PagingForwardedReads = 9 }));
        Check(pagingOnly.Status == "PASS" && pagingOnly.Detail.Contains("forwarded paging writes 2"),
            "forwarded paging-marked requests cannot be the owned unbuffered admission");
        foreach (var (changed, source) in new[]
        {
            (admissionAttempts with { LowerWriteAttempts = 3 }, sources with { ForwardedWrites = 7 }),
            (admissionAttempts with { LowerReadAttempts = 2 }, sources with { OtherReads = 10 })
        })
        {
            Check(QueueCache.Operations.SectorScenarios.VerifyAdmissionAttempts(withSources,
                AdmissionDiagnostics(changed, source)).Status == "SKIP",
                "a forwarded non-paging request could be the owned request: unproven, not pass");
        }
        foreach (var (changed, source) in new[]
        {
            (admissionAttempts with { LowerWriteAttempts = 3 }, sources with { GeneratedWrites = 6 }),
            (admissionAttempts with { LowerFlushAttempts = 4 }, sources)
        })
        {
            try
            {
                QueueCache.Operations.SectorScenarios.VerifyAdmissionAttempts(withSources, AdmissionDiagnostics(changed, source));
                throw new Exception("QueueCache-generated lower I/O accepted during admission.");
            }
            catch (IOException) { }
        }
        Check(QueueCache.Operations.SectorScenarios.DrainWriteAttempts(withSources) == 5 &&
            QueueCache.Operations.SectorScenarios.DrainWriteAttempts(noSources) == 2,
            "drain triggers use generated writes when attributed, else every lower write");
        try
        {
            QueueCache.Operations.SectorScenarios.VerifyAdmissionAttempts(noSources,
                AdmissionDiagnostics(admissionAttempts with { LowerReadAttempts = 0 }));
            throw new Exception("Reversed lower attempt counter accepted.");
        }
        catch (IOException) { }
        foreach (var pair in new[] { (Before: (QueueCache.Management.CacheAttribution?)null, After: (QueueCache.Management.CacheAttribution?)admissionAttempts),
            (Before: (QueueCache.Management.CacheAttribution?)admissionAttempts, After: (QueueCache.Management.CacheAttribution?)null) })
        {
            try
            {
                QueueCache.Operations.SectorScenarios.VerifyAdmissionAttempts(AdmissionDiagnostics(pair.Before),
                    AdmissionDiagnostics(pair.After));
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
        var drainDecision = VerificationPlan.DrainDecision(options with { Suite = "drain-decision" });
        Check(drainDecision.Count == 24 && drainDecision.Select(c => c.Id).Distinct().Count() == 24,
            "drain decision has unique controls and p1/p2/p4 repetitions");
        Check(drainDecision.Count(c => !c.PendingDrain) == 6 &&
            drainDecision.Where(c => c.PendingDrain).Select(c => c.Parallelism).Distinct().Order().SequenceEqual(new[] { 1, 2, 4 }),
            "drain decision retains matched controls and supported parallelism");
        Check(drainDecision[8].Workload == "cold-read" && drainDecision[8].Repeat == 2,
            "drain decision alternates workload order between repetitions");
        Check(VerificationPlan.DrainDecision(options with { Suite = "quick" }).Count == 0,
            "other suites do not acquire drain-decision cases");
        var selectedDrain = VerificationPlan.DrainDecision(options with
        {
            Suite = "drain-decision",
            CaseFilter = "fitting-write-p1"
        });
        Check(selectedDrain.Count == 3 && selectedDrain.All(c => c.Workload == "fitting-write" && c.Parallelism == 1),
            "drain decision filter retains stable matched repetition IDs");
        Reject(() => VerificationPlan.Validate(options with { Suite = "drain-decision", BudgetMiB = 4097 }));
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
