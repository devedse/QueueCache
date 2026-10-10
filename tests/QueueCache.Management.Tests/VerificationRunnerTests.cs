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
        Check(QueueCache.Operations.ConfigurationGate.NameFor("disk-a") == QueueCache.Operations.ConfigurationGate.NameFor("DISK-A") &&
            QueueCache.Operations.ConfigurationGate.NameFor("disk-a") != QueueCache.Operations.ConfigurationGate.NameFor("disk-b"),
            "configuration transactions use case-insensitive physical-disk identity");
        await Task.Run(() =>
        {
            using var firstGate = QueueCache.Operations.ConfigurationGate.Enter("fixture-disk-a");
            var otherDisk = Task.Run(() => { using var gate = QueueCache.Operations.ConfigurationGate.Enter("fixture-disk-b"); });
            Check(otherDisk.Wait(TimeSpan.FromSeconds(2)), "another disk can configure during an eject transaction");
        });
        await Task.Run(() =>
        {
            using var started = new ManualResetEventSlim();
            Task sameDisk;
            using (QueueCache.Operations.ConfigurationGate.Enter("fixture-disk-a"))
            {
                sameDisk = Task.Run(() => { started.Set(); using var gate = QueueCache.Operations.ConfigurationGate.Enter("FIXTURE-DISK-A"); });
                Check(started.Wait(TimeSpan.FromSeconds(2)) && !sameDisk.Wait(100), "volumes on the same disk serialize their mutations");
            }
            Check(sameDisk.Wait(TimeSpan.FromSeconds(2)), "same-disk mutation resumes after eject transaction releases ownership");
        });
        var options = new VerificationOptions("Q:", "performance");
        Check(VerificationPlan.Version == 115, "plan 115 declares attribution and priority/affinity diagnostics");
        await RunCampaignContractsAsync();
        Check(VerificationPlan.Suites.Contains("partial-read-accounting") &&
              VerificationPlan.Integrity(options with { Suite = "partial-read-accounting" }).Single().Operation == "partial-read-accounting" &&
              !VerificationPlan.Integrity(options with { Suite = "full" }).Any(c => c.Operation == "partial-read-accounting"),
              "partial-read accounting is an explicit maintained scenario, excluded from full");
        VerificationPlan.Validate(options with { Suite = "partial-read-accounting", DiskSpd = null });
        var references = RamReadReferencePlan.Cases(3);
        Check(references.Count == 36 && references.Select(c => c.Id).Distinct().Count() == 36 &&
              references.First().Access == QueueCache.Operations.ManagedDisks.RamAccess.Direct &&
              references.First(c => c.Repeat == 2).Access == QueueCache.Operations.ManagedDisks.RamAccess.Standard,
              "RAM references alternate access and shape order with immutable unique windows");
        Check(references.Where(c => c.BlockKiB == 1024 && c.QueueDepth * c.Threads == 8).Count() == 12 &&
              references.Count(c => c.BlockKiB == 4) == 18,
              "RAM references preserve aggregate large queue depth and separate small-read controls");
        Check(RamReadReferencePlan.Arguments(references[0], 10).Contains("-W0") &&
              RamReadReferencePlan.Arguments(references[0], 10).Contains("-Rxml") &&
              !RamReadReferencePlan.Arguments(references[0], 10).Any(a => a.StartsWith("-c")),
              "RAM reference scores do not recreate files or hide preparation inside warm-up");
        RamReadReferencePlan.ValidateStandardError(references[0], "");
        RamReadReferencePlan.ValidateStandardError(references[2], RamReadReferencePlan.IndependentSequentialWarning + "\n");
        Reject(() => RamReadReferencePlan.ValidateStandardError(references[0], RamReadReferencePlan.IndependentSequentialWarning));
        Reject(() => RamReadReferencePlan.ValidateStandardError(references[^1], RamReadReferencePlan.IndependentSequentialWarning));
        Reject(() => RamReadReferencePlan.ValidateStandardError(references[2], RamReadReferencePlan.IndependentSequentialWarning + "\nERROR: could not read"));
        Reject(() => RamReadReferencePlan.ValidateStandardError(references[2], "WARNING: unknown warning"));
        Reject(() => VerificationPlan.Validate(options with { Suite = "ram-read-reference", DiskSpd = null, BudgetMiB = 2048 }));
        Reject(() => VerificationPlan.Validate(options with { Suite = "ram-read-reference", BudgetMiB = 1024 }));
        Check(!VerificationPlan.Integrity(options with { Suite = "full" }).Any(c => c.Operation == "ram-read-reference"),
              "RAM read references remain opt-in");
        var queueReferences = RamReadReferencePlan.Cases(3, includeQueue: true);
        Check(queueReferences.Count == 54 && queueReferences.Select(c => c.Id).Distinct().Count() == 54 &&
              queueReferences.Count(c => c.RamReadQueueMode == 2) == 18 &&
              queueReferences.Where(c => c.Access == QueueCache.Operations.ManagedDisks.RamAccess.Standard).All(c => c.RamReadQueueMode == 0),
              "queue references retain synchronous and Standard controls and eighteen adaptive windows");
        Check(VerificationPlan.Integrity(options with { Suite = "ram-read-queue" }).Single().Operation == "ram-read-reference",
              "RAM queue comparison reuses maintained ownership and restoration orchestration");
        Reject(() => VerificationPlan.Validate(options with { Suite = "ram-read-queue", DiskSpd = null, BudgetMiB = 2048 }));
        Reject(() => VerificationPlan.Validate(options with { Suite = "ram-read-queue", BudgetMiB = 1024 }));
        var queueCase = queueReferences.First(c => c.RamReadQueueMode == 2);
        var queueBefore = new QueueCache.Management.CacheRamReadQueue(2, 4, 4, 0, 0, 0, 0);
        var queueAfter = queueBefore with { Queued = 104, Completed = 104 };
        RamReadReferenceEvidence.ValidateQueue(queueCase, [queueBefore, queueBefore with { Queued = 54, Completed = 53 }, queueAfter]);
        Reject(() => RamReadReferenceEvidence.ValidateQueue(queueCase, [null, null]));
        Reject(() => RamReadReferenceEvidence.ValidateQueue(queueCase, [queueBefore, queueAfter with { Mode = 0 }]));
        Reject(() => RamReadReferenceEvidence.ValidateQueue(queueCase, [queueBefore, queueAfter with { Completed = 103 }]));
        Reject(() => RamReadReferenceEvidence.ValidateQueue(queueCase, [queueBefore, queueAfter with { Cancelled = 1 }]));
        Reject(() => RamReadReferenceEvidence.ValidateQueue(queueCase, [queueBefore, queueAfter with { QueueFull = 1 }]));
        Reject(() => RamReadReferenceEvidence.ValidateQueue(queueCase, [queueBefore, queueAfter with { Fallback = 1 }]));
        Reject(() => RamReadReferenceEvidence.ValidateQueue(queueCase with { BlockKiB = 4 }, [queueBefore, queueAfter]));
        var referenceChecks = references.Select(c => new QueueCache.Operations.CheckResult(c.Id, "PASS", "fixture")).ToArray();
        RamReadReferenceEvidence.ValidateChecks(references, referenceChecks);
        Reject(() => RamReadReferenceEvidence.ValidateChecks(references, referenceChecks[..^1]));
        Reject(() => RamReadReferenceEvidence.ValidateChecks(references, [.. referenceChecks[..^1], referenceChecks[0]]));
        Reject(() => RamReadReferenceEvidence.ValidateChecks(references, [.. referenceChecks[..^1], referenceChecks[^1] with { Result = "FAIL" }]));
        var resource = Guid.NewGuid(); var boot = Guid.NewGuid(); var sampleTime = DateTimeOffset.UtcNow;
        var direct = new QueueCache.Management.RamDirectState(QueueCache.Management.RamDirectAccess.Reads | QueueCache.Management.RamDirectAccess.Writes,
            QueueCache.Management.RamDirectReason.None, 0, resource, 0, 2UL << 30, 0, 0, 0, 0, 0, "fixture");
        var referenceBefore = new RamReadReferenceBoundary(sampleTime, resource, boot, 1, 5,
            QueueCache.Operations.ManagedDisks.ManagedDiskState.Ready, false, false, direct, 0, 0, 0, null, 0, 0);
        var referenceAfter = referenceBefore with { Utc = sampleTime.AddSeconds(1), Direct = direct with { ReadRequests = 64, ReadBytes = 64UL << 20 } };
        var referenceScore = new DiskSpdScore(64L << 20, 64, 1, 64, 64, .1, null, .1, .1);
        RamReadReferenceSample NativeSample(RamReadReferenceBoundary boundary) => new(boundary.Utc,
            new QueueCache.Management.RamDiskSnapshot(resource, boot, 1, 2UL << 30, boundary.WriteGeneration, 2UL << 30,
                Guid.Empty, 512, QueueCache.Management.RamDiskFlags.Published | QueueCache.Management.RamDiskFlags.DirectRegistered,
                0, 0, boundary.ProviderWriteBytes ?? 0, 0, 0, 0, 0),
            new QueueCache.Management.RamDiskStatistics(boundary.ProviderReadRequests, boundary.ProviderWriteRequests ?? 0,
                10000000, 0, 0, 0, 0, 0, 0), boundary.CacheEnabled, boundary.Direct);
        void ValidateReference(RamReadReferenceBoundary after, DiskSpdScore score) =>
            RamReadReferenceEvidence.Validate(references[0], [NativeSample(referenceBefore), NativeSample(after)], referenceBefore, after, score,
                sampleTime.AddMilliseconds(100), sampleTime.AddMilliseconds(900));
        ValidateReference(referenceAfter, referenceScore);
        ValidateReference(referenceAfter with { WriteGeneration = 6, Direct = referenceAfter.Direct with { WriteBytes = 4096 } }, referenceScore);
        Reject(() => ValidateReference(referenceAfter with { WriteGeneration = 4 }, referenceScore));
        Reject(() => ValidateReference(referenceAfter with { ProviderWriteBytes = null }, referenceScore));
        Reject(() => ValidateReference(referenceAfter with { ImageReadAttempts = 1 }, referenceScore));
        Reject(() => ValidateReference(referenceAfter with { Direct = direct }, referenceScore));
        Reject(() => ValidateReference(referenceAfter, referenceScore with { Bytes = 0, Operations = 0 }));
        var nativeBefore = NativeSample(referenceBefore); var nativeAfter = NativeSample(referenceAfter);
        void ValidateNative(RamReadReferenceSample changed) => RamReadReferenceEvidence.Validate(references[0],
            [nativeBefore, changed], referenceBefore, referenceAfter, referenceScore,
            sampleTime.AddMilliseconds(100), sampleTime.AddMilliseconds(900));
        Reject(() => ValidateNative(nativeAfter with { Native = nativeAfter.Native with { ResourceId = Guid.NewGuid() } }));
        Reject(() => ValidateNative(nativeAfter with { Native = nativeAfter.Native with { Errors = 1 } }));
        Reject(() => ValidateNative(nativeAfter with { Native = nativeAfter.Native with { CapacityBytes = 1UL << 30 } }));
        Reject(() => ValidateNative(nativeAfter with { Native = nativeAfter.Native with { Flags = QueueCache.Management.RamDiskFlags.Published } }));
        Reject(() => ValidateNative(nativeAfter with { Native = nativeAfter.Native with { Flags = nativeAfter.Native.Flags | QueueCache.Management.RamDiskFlags.Frozen } }));
        Reject(() => ValidateNative(nativeAfter with { Utc = sampleTime.AddSeconds(2.084) }));
        var standardCase = references.First(c => c.Access == QueueCache.Operations.ManagedDisks.RamAccess.Standard && c.BlockKiB == 1024);
        var standardBefore = referenceBefore with { Direct = direct with { Access = QueueCache.Management.RamDirectAccess.None } };
        var standardAfter = standardBefore with { Utc = referenceAfter.Utc, ProviderReadRequests = 64 };
        RamReadReferenceSample StandardSample(RamReadReferenceBoundary boundary) => NativeSample(boundary) with
            { Native = NativeSample(boundary).Native with { Flags = QueueCache.Management.RamDiskFlags.Published } };
        RamReadReferenceEvidence.Validate(standardCase, [StandardSample(standardBefore), StandardSample(standardAfter)],
            standardBefore, standardAfter, referenceScore, sampleTime.AddMilliseconds(100), sampleTime.AddMilliseconds(900));
        var ownedDefinition = QueueCache.Operations.ManagedDisks.ManagedDiskDefinition.New(QueueCache.Operations.ManagedDisks.ManagedDiskMode.EphemeralRam)
            with { CapacityBytes = 2UL << 30, Label = "QC-ReadRef-fixture" };
        RamReadReferenceEvidence.ValidateOwnedDefinition(ownedDefinition, ownedDefinition, []);
        Reject(() => RamReadReferenceEvidence.ValidateOwnedDefinition(ownedDefinition, ownedDefinition with { Label = "another disk" }, []));
        Reject(() => RamReadReferenceEvidence.ValidateOwnedDefinition(ownedDefinition, ownedDefinition, [ownedDefinition.ResourceId]));
        var stagedBefore = new QueueCache.Management.CacheStagedReads(10, 4096, 4, 512);
        var stagedAfter = new QueueCache.Management.CacheStagedReads(12, 12288, 5, 1536);
        Check(stagedAfter.Since(stagedBefore) == new QueueCache.Management.CacheStagedReads(2, 8192, 1, 1024),
              "staged traffic is derived from quiescent window counters");
        Reject(() => stagedBefore.Since(stagedAfter));
        Reject(() => new QueueCache.Management.CacheStagedReads(11, 8192, 8, 512).Since(stagedBefore));
        Reject(() => new QueueCache.Management.CacheStagedReads(11, 8192, 5, 9000).Since(stagedBefore));
        Check(VerificationPlan.ManagedSectorSizes.SequenceEqual(new uint[] { 512, 4096 }), "provider and product suites share the required 512/4Kn fixture contract");
        Check(VerificationPlan.Integrity(options with { Suite = "managed-provider" }).Single().Operation == "managed-provider" &&
            !VerificationPlan.Integrity(options with { Suite = "full" }).Any(c => c.Operation == "managed-provider"), "native provider proof is opt-in, never a broad-suite side effect");
        foreach (var suite in new[] { "ram-disk", "vhdx-backed", "image-in-ram" })
            Check(VerificationPlan.Integrity(options with { Suite = suite }).Single().Operation == suite &&
                !VerificationPlan.Integrity(options with { Suite = "full" }).Any(c => c.Operation == suite), "product managed fixtures are explicit: " + suite);
        Check(VerificationPlan.Integrity(options with { Suite = "managed-cli" }).Single().Operation == "managed-cli" &&
            !VerificationPlan.Integrity(options with { Suite = "full" }).Any(c => c.Operation == "managed-cli"), "actual product CLI qualification is explicit and excluded from full");
        VerificationPlan.Validate(options with { Suite = "managed-cli", DiskSpd = null });
        foreach (var suite in new[] { "managed-broker-restart", "managed-lifecycle-prepare", "managed-lifecycle-verify", "managed-lifecycle-cleanup" })
            Check(VerificationPlan.Integrity(options with { Suite = suite }).Single().Operation == suite &&
                !VerificationPlan.Integrity(options with { Suite = "full" }).Any(c => c.Operation == suite), "retained lifecycle fixtures/transitions are opt-in: " + suite);
        VerificationPlan.Validate(options with { Suite = "managed-lifecycle-prepare", DiskSpd = null });
        VerificationPlan.Validate(options with { Suite = "managed-lifecycle-verify", ManagedOraclePath = @"C:\Results\prior.json", ManagedTransition = ManagedLifecycleTransition.Restart, DiskSpd = null });
        Reject(() => VerificationPlan.Validate(options with { Suite = "managed-lifecycle-verify" }));
        Reject(() => VerificationPlan.Validate(options with { Suite = "managed-lifecycle-cleanup", ManagedOraclePath = @"C:\Results\prior.json", ManagedTransition = ManagedLifecycleTransition.Restart }));
        Reject(() => VerificationPlan.Validate(options with { Suite = "quick", ManagedOraclePath = @"C:\Results\prior.json" }));
        Reject(() => VerificationPlan.Validate(options with { Suite = "managed-lifecycle-prepare", OraclePath = @"C:\Results\prior.json" }));
        var windowsRemovalCases = VerificationPlan.Integrity(options with { Suite = "disk-removal-windows" });
        Check(windowsRemovalCases.Count == 1 && windowsRemovalCases[0].Id == "disk-windows-eject-reconnect" &&
            !VerificationPlan.Integrity(options with { Suite = "full" }).Any(c => c.Id == "disk-windows-eject-reconnect"),
            "native Windows eject remains a distinct opt-in case outside full");
        var removalOptions = new VerificationOptions("W:", "disk-removal", BudgetMiB: 256,
            DisposableInstance: "test-disposable", DisposableBytes: 8L << 30);
        VerificationPlan.Validate(removalOptions);
        Reject(() => VerificationPlan.Validate(removalOptions with { DisposableInstance = null }));
        Reject(() => VerificationPlan.Validate(removalOptions with { DisposableBytes = 0 }));
        Reject(() => VerificationPlan.Validate(removalOptions with { BudgetMiB = 1024 }));
        Reject(() => VerificationPlan.Validate(removalOptions with { Suite = "quick" }));
        Check(VerificationPlan.Integrity(removalOptions).Single().Id == DiskRemovalHandshake.CaseId &&
            !VerificationPlan.Integrity(options with { Suite = "full" }).Any(c => c.Operation == "disk-removal"),
            "removal is explicitly opted in and excluded from full");
        var reconnectTarget = new QueueCache.Operations.DiskTarget('W', 3, 1L << 30, "test-disposable")
            { DiskBytes = 8L << 30, VolumeId = "{00000000-0000-0000-0000-000000000001}" };
        var reconnectAck = new DiskReconnectAcknowledgement("run-one", DiskRemovalHandshake.CaseId,
            reconnectTarget.Instance, reconnectTarget.VolumeId);
        DiskRemovalHandshake.Validate(reconnectAck, "run-one", reconnectTarget);
        foreach (var bad in new[] { reconnectAck with { RunId = "old-run" }, reconnectAck with { CaseId = "other-case" },
            reconnectAck with { Instance = "replacement-disk" }, reconnectAck with { VolumeId = "replacement-volume" } })
        {
            try { DiskRemovalHandshake.Validate(bad, "run-one", reconnectTarget); }
            catch (IOException) { continue; }
            throw new Exception("Reconnect handshake accepted stale or replacement identity.");
        }
        VerificationPlan.Validate(new VerificationOptions("Q:", "volumes"));
        VerificationPlan.Validate(new VerificationOptions("V:", "trim-cache"));
        Check(VerificationPlan.Integrity(options with { Suite = "volumes" }).Select(test => test.Id).SequenceEqual(
            ["volume-registration", "volume-raw-disk-commands", "volume-shared-disk", "volume-resize", "volume-snapshot"]), "volumes runs registration, raw disk commands, a shared disk, a resize and a snapshot as separate cases");
        Check(VerificationPlan.Integrity(options with { Suite = "trim-cache" }).Single() == new IntegrityCase("trim-cache", "trim-cache"), "trim-cache is one case");
        Check(!VerificationPlan.Integrity(options with { Suite = "full" }).Any(test => test.Id.StartsWith("volume-") || test.Id == "trim-cache"),
            "full does not include the volume or TRIM suites (they need a lab disk)");
        Check(VerificationRunner.AllowsSkips("volumes") && VerificationRunner.AllowsSkips("trim-cache") && VerificationRunner.AllowsSkips("trim-file") &&
            !VerificationRunner.AllowsSkips("policies") && !VerificationRunner.AllowsSkips("full"), "only capability-dependent suites may complete with skips");
        Check(QueueCache.Operations.CacheScenarios.VerifySettingsRollback("s", true, "x. The previous settings were restored.",
            true, true, true, true, true, true).Result == "PASS", "settings rollback accepts a verified restore");
        foreach (var bad in new Action[]
        {
            () => QueueCache.Operations.CacheScenarios.VerifySettingsRollback("s", null, null, true, true, true, true, true, true),
            () => QueueCache.Operations.CacheScenarios.VerifySettingsRollback("s", false, "not restored", true, true, true, true, true, true),
            () => QueueCache.Operations.CacheScenarios.VerifySettingsRollback("s", true, "The previous settings were restored.", true, false, true, true, true, true),
            () => QueueCache.Operations.CacheScenarios.VerifySettingsRollback("s", true, "The previous settings were restored.", true, true, true, true, false, true),
            () => QueueCache.Operations.CacheScenarios.VerifySettingsRollback("s", true, "The previous settings were restored.", true, true, true, true, true, false)
        })
        {
            try
            {
                bad();
                Check(false, "settings rollback rejects an unarmed fault, a false restore claim, lost data or a stuck fault");
            }
            catch (IOException) { }
        }
        Check(QueueCache.Operations.CacheScenarios.VerifyReadMissIsolation("r", 16 << 20, 0, 16UL << 20, 4096, 9).Result == "PASS",
            "read-miss isolation accepts kept misses that match the file");
        foreach (var bad in new Action[]
        {
            () => QueueCache.Operations.CacheScenarios.VerifyReadMissIsolation("r", 16 << 20, 1, 16UL << 20, 4096, 9),
            () => QueueCache.Operations.CacheScenarios.VerifyReadMissIsolation("r", 16 << 20, 0, 1UL << 20, 4096, 9),
            () => QueueCache.Operations.CacheScenarios.VerifyReadMissIsolation("r", 16 << 20, 0, 16UL << 20, 10, 9)
        })
        {
            try
            {
                bad();
                Check(false, "read-miss isolation rejects buffer contents, missing hits or missing fills");
            }
            catch (IOException) { }
        }
        Check(QueueCache.Operations.PagingRecognitionScenarios.VerifyImages(3, 1 << 20, [], [0, 0], 5).Detail.Contains("repeated a page: 5"),
            "program-file check accepts matching files and clean exits");
        foreach (var bad in new Action[]
        {
            () => QueueCache.Operations.PagingRecognitionScenarios.VerifyImages(3, 1 << 20, ["coreclr.dll"], [0], null),
            () => QueueCache.Operations.PagingRecognitionScenarios.VerifyImages(3, 1 << 20, [], [0, -1073741819], null)
        })
        {
            try
            {
                bad();
                Check(false, "program-file check rejects a mismatched file or a crashed process");
            }
            catch (IOException) { }
        }
        Check(QueueCache.Operations.CacheScenarios.VerifyParallelCopies("p", 10, 5, 0, 0, 3, 4, 0, true).Result == "PASS",
            "parallel-copies accepts whole versions and offloaded copies");
        Check(QueueCache.Operations.CacheScenarios.VerifyParallelCopies("p", 10, 5, 0, 0, null, null, 0, true).Detail.Contains("unavailable"),
            "parallel-copies reports an older driver instead of zero");
        foreach (var bad in new Action[]
        {
            () => QueueCache.Operations.CacheScenarios.VerifyParallelCopies("p", 10, 5, 1, 0, 3, 4, 0, true),
            () => QueueCache.Operations.CacheScenarios.VerifyParallelCopies("p", 10, 5, 0, 1, 3, 4, 0, true),
            () => QueueCache.Operations.CacheScenarios.VerifyParallelCopies("p", 10, 5, 0, 0, 3, 4, 0, false),
            () => QueueCache.Operations.CacheScenarios.VerifyParallelCopies("p", 10, 5, 0, 0, 0, 4, 0, true),
            () => QueueCache.Operations.CacheScenarios.VerifyParallelCopies("p", 10, 5, 0, 0, 3, 4, unchecked((int)0xC0000185), true)
        })
        {
            try
            {
                bad();
                Check(false, "parallel-copies rejects a mismatch, a fault, a lost version or no offload");
            }
            catch (IOException) { }
        }
        Check(QueueCache.Operations.CacheScenarios.VerifyCallerPath(null, null, 100).Contains("unavailable"),
            "caller-path check reports an older driver instead of zero");
        Check(QueueCache.Operations.CacheScenarios.VerifyCallerPath(new(10, 10, 0), new(60, 55, 1), 100).Contains("95 of 100"),
            "caller-path check accepts probes");
        try
        {
            QueueCache.Operations.CacheScenarios.VerifyCallerPath(new(0, 0, 0), new(40, 40, 20), 100);
            Check(false, "caller-path check rejects worker-only service");
        }
        catch (IOException) { }
        Check(QueueCache.Operations.CacheScenarios.VerifyCallerPath(new(0, 0, 0), new(20, 20, 0), 100, "ReFS").Contains("40 of 100 on ReFS"),
            "caller-path check records the ReFS share instead of applying the 90% expectation");
        try
        {
            QueueCache.Operations.CacheScenarios.VerifyCallerPath(new(0, 0, 0), new(0, 0, 0), 100, "ReFS");
            Check(false, "caller-path check still requires the path to work on ReFS");
        }
        catch (IOException) { }
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
        static byte[] NtfsRecord(long highestVcn)
        {
            var record = new byte[1024];
            BitConverter.GetBytes(0x454C4946u).CopyTo(record, 0);
            BitConverter.GetBytes((ushort)0x30).CopyTo(record, 4);
            BitConverter.GetBytes((ushort)3).CopyTo(record, 6);
            BitConverter.GetBytes((ushort)0x38).CopyTo(record, 0x14);
            // Update sequence 0x0007; the real tail bytes of both sectors live in the array.
            BitConverter.GetBytes((ushort)7).CopyTo(record, 0x30);
            record[0x32] = 0xAB; record[0x33] = 0xCD; record[0x34] = 0x12; record[0x35] = 0x34;
            BitConverter.GetBytes((ushort)7).CopyTo(record, 510);
            BitConverter.GetBytes((ushort)7).CopyTo(record, 1022);
            BitConverter.GetBytes(0x80u).CopyTo(record, 0x38);
            BitConverter.GetBytes(0x50).CopyTo(record, 0x3C);
            record[0x40] = 1;
            BitConverter.GetBytes(highestVcn).CopyTo(record, 0x38 + 0x18);
            BitConverter.GetBytes((ushort)0x40).CopyTo(record, 0x38 + 0x20);
            // 16 clusters at LCN 0x1000, 8 sparse clusters, 4 clusters at LCN 0x1000 - 0x10.
            byte[] pairs = [0x31, 0x10, 0x00, 0x10, 0x00, 0x01, 0x08, 0x21, 0x04, 0xF0, 0xFF, 0x00];
            pairs.CopyTo(record, 0x38 + 0x40);
            BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(record, 0x38 + 0x50);
            return record;
        }
        var ntfsRecord = NtfsRecord(0x1B);
        var dataRuns = QueueCache.Operations.SpecialFileMap.DataRuns(ntfsRecord);
        Check(dataRuns.SequenceEqual([(0x1000L, 0x10L), (0xFF0L, 4L)]) && ntfsRecord[510] == 0xAB && ntfsRecord[1023] == 0x34,
            "in-use paging file layout is decoded from its NTFS record with fixups, sparse and negative runs");
        try
        {
            QueueCache.Operations.SpecialFileMap.DataRuns(NtfsRecord(0x30));
            throw new Exception("Partial NTFS layout accepted.");
        }
        catch (NotSupportedException) { }
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
        Check(QueueCache.Operations.PagingRecognitionScenarios.Evaluate(recognitionBefore,
                recognitionBefore with { PagingFileRequests = recognitionBefore.PagingFileRequests + 9 }, ioBefore, ioBefore, "p", 100, 109)
            .Any(check => check.Name == "system-paging/paging-file-io-bypasses-worker" && check.Result == "PASS"),
            "every recognised paging-file request bypassed the worker");
        try
        {
            QueueCache.Operations.PagingRecognitionScenarios.Evaluate(recognitionBefore,
                recognitionBefore with { PagingFileRequests = recognitionBefore.PagingFileRequests + 9 }, ioBefore, ioBefore, "p", 100, 105);
            throw new Exception("Recognised paging-file I/O that went through the worker was accepted.");
        }
        catch (IOException) { }
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
        Check(QueueCache.Operations.OrderingFaultScenarios.VerifyAllocationRetry(10, 13, true, 0)
            .Contains("3 failed lower IRP build(s) were retried"), "transient IRP allocation failure is retried without a fault");
        foreach (var (before, after, flushOk, error) in new (ulong?, ulong?, bool, int)[]
        {
            (10, 11, true, 0),                              // simulated failures not exercised
            (10, 13, false, 0),                             // flush failed
            (10, 13, true, unchecked((int)0xC000009A)),     // cache faulted
        })
        {
            try
            {
                QueueCache.Operations.OrderingFaultScenarios.VerifyAllocationRetry(before, after, flushOk, error);
                throw new Exception("A failed transient allocation retry was accepted.");
            }
            catch (IOException) { }
        }
        try
        {
            QueueCache.Operations.OrderingFaultScenarios.VerifyAllocationRetry(null, 3, true, 0);
            throw new Exception("Missing V11 diagnostics produced a verdict.");
        }
        catch (NotSupportedException) { }
        Check(QueueCache.Operations.OrderingFaultScenarios.VerifyDirectWriteFailure(true, 0, 4, 5)
            .Contains("stayed healthy"), "a failed direct paging write reaches the application without a cache fault");
        foreach (var (flushFailed, error, after) in new (bool, int, ulong?)[]
        {
            (true, 0, 4),                                   // direct path not exercised
            (false, 0, 5),                                  // false success
            (true, unchecked((int)0xC0000185), 5),          // cache faulted
        })
        {
            try
            {
                QueueCache.Operations.OrderingFaultScenarios.VerifyDirectWriteFailure(flushFailed, error, 4, after);
                throw new Exception("An unsafe direct paging write failure was accepted.");
            }
            catch (IOException) { }
        }
        Check(QueueCache.Operations.OrderingFaultScenarios.VerifyReleaseUnderLoad("r", 12, 5, 900, 0, true).Result == "PASS",
            "cache lifecycle under load with whole versions and final media passes");
        Check(QueueCache.Operations.OrderingFaultScenarios.VerifyReleaseUnderLoad("r", 4, 40, 900, 0, true).Result == "SKIP",
            "too few lifecycle cycles during the load leaves the race unexercised");
        foreach (var (mismatches, final) in new[] { (1, true), (0, false) })
        {
            try
            {
                QueueCache.Operations.OrderingFaultScenarios.VerifyReleaseUnderLoad("r", 12, 5, 900, mismatches, final);
                throw new Exception("A torn or stale block under the lifecycle race was accepted.");
            }
            catch (IOException) { }
        }
        Check(QueueCache.Operations.OrderingFaultScenarios.VerifyPagingMapFallback(true, 0, 1, 2, 7, 8)
            .Contains("fell back to the ordered direct path"), "an unmappable paging write completes on the direct path");
        foreach (var (saved, error, mapAfter, directAfter) in new (bool, int, ulong?, ulong?)[]
        {
            (true, 0, 1, 8),                                // map failure not exercised
            (true, 0, 2, 7),                                // no direct fallback
            (false, 0, 2, 8),                               // save failed
            (true, unchecked((int)0xC000009A), 2, 8),       // cache faulted
        })
        {
            try
            {
                QueueCache.Operations.OrderingFaultScenarios.VerifyPagingMapFallback(saved, error, 1, mapAfter, 7, directAfter);
                throw new Exception("A failed paging map fallback was accepted.");
            }
            catch (IOException) { }
        }
        const int insufficient = unchecked((int)0xC000009A);
        Check(QueueCache.Operations.OrderingFaultScenarios.VerifyAllocationExhaustion(0, 249, true, insufficient, 1 << 20, 1 << 20, 5.1)
            .Contains("STATUS_INSUFFICIENT_RESOURCES"), "exhausted IRP allocation faults with the dirty version retained");
        foreach (var (after, flushFailed, error, dirty) in new (ulong?, bool, int, ulong)[]
        {
            (100, true, insufficient, 1UL << 20),           // bound not reached
            (249, false, insufficient, 1UL << 20),          // false success
            (249, true, unchecked((int)0xC0000185), 1UL << 20), // wrong error
            (249, true, insufficient, 4096UL),              // owned version lost
        })
        {
            try
            {
                QueueCache.Operations.OrderingFaultScenarios.VerifyAllocationExhaustion(0, after, flushFailed, error, dirty, 1 << 20, 5.1);
                throw new Exception("Unsafe allocation exhaustion accepted.");
            }
            catch (IOException) { }
        }
        Check(QueueCache.Operations.PagingCoherenceScenarios.VerifyMappedReadRetention(10, 10, 0, 8 << 20, true, 3).Result == "PASS",
            "a mapped read miss is not kept and a re-read matches");
        Check(QueueCache.Operations.PagingCoherenceScenarios.VerifyMappedReadRetention(null, null, 0, 8 << 20, true).Detail.Contains("unavailable"),
            "older drivers report the fill counters as unavailable");
        foreach (var (after, match) in new (ulong?, bool)[] { (11, true), (2058, true), (10, false) })
        {
            try
            {
                QueueCache.Operations.PagingCoherenceScenarios.VerifyMappedReadRetention(10, after, 0, 8 << 20, match);
                throw new Exception("A kept paging read miss or mismatched bytes were accepted.");
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
        var appSession = preflight with { Suite = "system-app-session", BudgetMiB = 1024 };
        VerificationPlan.Validate(appSession);
        Check(VerificationPlan.Integrity(appSession).SequenceEqual(new IntegrityCase[] { new("system-app-session", "system-app-session") }),
            "operator application session is one guarded case");
        Reject(() => VerificationPlan.Validate(appSession with { BudgetMiB = 512 }));
        Reject(() => VerificationPlan.Validate(appSession with { RecoverableVm = false }));
        Check(VerificationWorker.AllowsSystemUsagePaths("system-app-session"), "application session accepts reconciled usage paths");
        var sessionConfig = VerificationRunner.AppSessionConfiguration(1024);
        Check(sessionConfig.Preset == QueueCache.Operations.CachePreset.Fast &&
            sessionConfig.Options.Drain == QueueCache.Management.DrainAlgorithm.Deferred && sessionConfig.Options.MaxDirtyAgeMs == 600000 &&
            VerificationRunner.AppSessionOracleFile != "oracle.json",
            "application session keeps edits in RAM and never lets restoration compare the edited image with the baseline");
        var session = new QueueCache.Operations.AppSessionObservation(true, 120, TimeSpan.FromMinutes(2), true, false, 0,
            300UL << 20, 400UL << 20, 280UL << 20, 2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var baselineHash = new string('A', 64);
        (string, long) edited = (new string('B', 64), 274000000L);
        Check(QueueCache.Operations.AppSessionScenarios.Evaluate(session, baselineHash, edited, edited).All(check => check.Result == "PASS"),
            "edited image identical before and after drain passes");
        foreach (var (observed, live, released) in new[]
        {
            (session with { OperatorDone = false }, edited, edited),
            (session with { AlwaysEnabled = false }, edited, edited),
            (session with { ErrorsDelta = 1 }, edited, edited),
            (session, (baselineHash, 274000000L), (baselineHash, 274000000L)),
            (session, edited, (new string('C', 64), 274000000L)),
            (session, edited, (edited.Item1, 274000512L))
        })
        {
            try
            {
                QueueCache.Operations.AppSessionScenarios.Evaluate(observed, baselineHash, live, released);
                throw new Exception("Incomplete or mismatched application session accepted.");
            }
            catch (IOException) { }
        }
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
        var imageTarget = new QueueCache.Operations.DiskTarget('C', 0, 100L << 30, "SCSI\\TEST", true, true, true) { DiskBytes = 100L << 30 };
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
        var systemTarget = new QueueCache.Operations.DiskTarget('C', 0, 100L << 30, "SCSI\\TEST", true, true, true) { DiskBytes = 100L << 30 };
        var resultsTarget = new QueueCache.Operations.DiskTarget('Q', 1, 200L << 30, "SCSI\\RESULTS") { DiskBytes = 200L << 30 };
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
        // A 199 GiB volume (what the volume filter reports) on a 200 GiB disk.
        var identity = new QueueCache.Operations.DiskTarget('Q', 1, 199L << 30, "fixture") { DiskBytes = 200L << 30 };
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
        QueueCache.Operations.DiskTarget.ValidateDeviceLength(identity, 199UL << 30);
        try
        {
            QueueCache.Operations.DiskTarget.ValidateMountedIdentity(identity, (1, 1L << 20, 198L << 30), "fixture", "NTFS");
            throw new Exception("Extent length other than the volume size accepted.");
        }
        catch (IOException) { }
        try
        {
            QueueCache.Operations.DiskTarget.ValidateMountedIdentity(identity, (2, 1L << 20, 199L << 30), "fixture", "NTFS");
            throw new Exception("Disk-number mismatch accepted.");
        }
        catch (IOException) { }
        try
        {
            QueueCache.Operations.DiskTarget.ValidateDeviceLength(identity, 200UL << 30);
            throw new Exception("Volume-length mismatch accepted.");
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
        var sequential = VerificationPlan.Performance(options with { Suite = "sequential-resident" });
        Check(sequential.Count == 9 && sequential.Select(c => c.Id).Distinct().Count() == 9 &&
            sequential.All(c => c.WarmResident && c.Resident && !c.Timing && c.QueueDepth == 8 && c.Drain == "Idle"),
            "prewarmed sequential suite has nine isolated read/write cases");
        Check(sequential.Count(c => c.Workload == "sequential-read") == 3 &&
            !VerificationPlan.Performance(options with { Suite = "full" }).Any(c => c.WarmResident),
            "prewarmed read/write suite remains opt-in outside full");
        Check(sequential.Count(c => c.PrecomputedWriteBuffer && c.Workload == "sequential-write" && c.WriteBufferArgument == "-Z1M") == 3 &&
            sequential.Where(c => !c.PrecomputedWriteBuffer).All(c => c.WriteBufferArgument == "-Zr") &&
            writes.All(c => c.WriteBufferArgument == "-Zr"), "buffer comparison preserves existing write matrix payload generation");
        Check(VerificationPlan.Performance(options with { Suite = "sequential-resident", CaseFilter = "precomputed" }).Count == 3,
            "precomputed write selection is a complete three-case subset");
        var layouts = VerificationPlan.Performance(options with { Suite = "cache-layout", BudgetMiB = 2048 });
        Check(layouts.Count == 24 && layouts.Select(c => c.Id).Distinct().Count() == 24 &&
            layouts.All(c => c.WarmResident && !c.Timing && c.Workload == "sequential-read"),
            "cache layout comparison has unique resident read cases without timing");
        foreach (var group in layouts.Chunk(4))
            Check(group.Select(c => c.Layout).SequenceEqual(new[] { CacheLayoutStage.Fresh, CacheLayoutStage.SequentialReuse,
                CacheLayoutStage.RandomReuse, CacheLayoutStage.Recreated }) && group.Select(c => c.QueueDepth).Distinct().Count() == 1,
                "layout group brackets reuse with fresh allocations at the same queue depth");
        Check(layouts[0].QueueDepth == 1 && layouts[8].QueueDepth == 8 && layouts[16].QueueDepth == 1,
            "layout comparison alternates queue-depth order across repetitions");
        Reject(() => VerificationPlan.Validate(options with { Suite = "cache-layout", BudgetMiB = 1024 }));
        Reject(() => VerificationPlan.Validate(options with { Suite = "cache-layout", BudgetMiB = 2048, CaseFilter = "RandomReuse" }));
        Reject(() => VerificationPlan.Validate(options with { Suite = "cache-layout", BudgetMiB = 2048, DiskSpd = null }));
        var resetLayouts = VerificationPlan.Performance(options with { Suite = "cache-layout-reset", BudgetMiB = 2048 });
        Check(resetLayouts.Count == 36 && resetLayouts.Select(c => c.Id).Distinct().Count() == 36 &&
            resetLayouts.All(c => c.WarmResident && !c.Timing && c.Workload == "sequential-read"), "reset comparison has 36 unique resident reads");
        foreach (var group in resetLayouts.Chunk(6))
            Check(group.Select(c => c.Layout).SequenceEqual(new[] { CacheLayoutStage.Fresh, CacheLayoutStage.SequentialReuse,
                CacheLayoutStage.ResetAfterSequential, CacheLayoutStage.RandomReuse, CacheLayoutStage.ResetAfterRandom, CacheLayoutStage.Recreated }) &&
                group.Select(c => c.QueueDepth).Distinct().Count() == 1, "same-buffer resets follow both controlled reuse histories");
        Check(resetLayouts[0].QueueDepth == 1 && resetLayouts[12].QueueDepth == 8 && resetLayouts[24].QueueDepth == 1,
            "reset comparison alternates depth order");
        Check((uint)QueueCache.Management.WriteCacheAction.CallerPath == 14 &&
            (uint)QueueCache.Management.WriteCacheAction.LabResetFreeOrder == 15 &&
            (uint)QueueCache.Management.WriteCacheAction.LabMeasureLayout == 16 &&
            (uint)QueueCache.Management.WriteCacheAction.LabCopyFlags == 17 &&
            (uint)QueueCache.Management.WriteCacheAction.LabReadRecall == 18 &&
            (uint)QueueCache.Management.WriteCacheAction.LabRamReadQueue == 19 &&
            (uint)QueueCache.Management.WriteCacheAction.LabCallerBackoff == 20, "diagnostic actions extend the existing ABI");
        Reject(() => VerificationPlan.Validate(options with { Suite = "cache-layout-patterns", BudgetMiB = 2048, DiskSpd = Environment.ProcessPath }));
        Check(new[] { CacheLayoutStage.ResetAfterSequential, CacheLayoutStage.ResetAfterRandom, CacheLayoutStage.ResetAscending }.All(CacheLayoutEvidence.Resets) &&
            !new[] { CacheLayoutStage.Fresh, CacheLayoutStage.Churned, CacheLayoutStage.ChurnedFull }.Any(CacheLayoutEvidence.Resets),
            "the plan-94 order patterns are retired with the free list; only ascending resets remain");
        var steady = VerificationPlan.Performance(options with { Suite = "cache-layout-steady", BudgetMiB = 2048 });
        Check(steady.Count == 18 && steady.Select(c => c.Id).Distinct().Count() == 18 &&
            steady.Chunk(3).All(g => g.Select(c => c.Layout).SequenceEqual(new[] { CacheLayoutStage.Fresh, CacheLayoutStage.Churned, CacheLayoutStage.ResetAscending })),
            "steady comparison: fresh, never-cleared churn, then ascending reset control");
        var full = VerificationPlan.Performance(options with { Suite = "cache-layout-full", BudgetMiB = 2048 });
        Check(full.Count == 18 && full.Select(c => c.Id).Distinct().Count() == 18 &&
            full.Chunk(3).All(g => g.Select(c => c.Layout).SequenceEqual(new[] { CacheLayoutStage.Fresh, CacheLayoutStage.ChurnedFull, CacheLayoutStage.ResetAscending })),
            "full comparison: fresh, full-cache churn, then ascending reset control");
        var tool = Environment.ProcessPath!; // Any existing file passes the DiskSpd path check.
        foreach (var suite in new[] { "cache-layout-steady", "cache-layout-full" })
        {
            VerificationPlan.Validate(options with { Suite = suite, BudgetMiB = 2048, DiskSpd = tool });
            Reject(() => VerificationPlan.Validate(options with { Suite = suite, BudgetMiB = 1024, DiskSpd = tool }));
            Reject(() => VerificationPlan.Validate(options with { Suite = suite, BudgetMiB = 2048, DiskSpd = tool, CaseFilter = "Fresh" }));
            Reject(() => VerificationPlan.Validate(options with { Suite = suite, BudgetMiB = 2048, DiskSpd = null }));
        }
        VerificationPlan.Validate(options with { Suite = "sequential-resident", BudgetMiB = 2048, DiskSpd = tool });
        Reject(() => VerificationPlan.Validate(options with { Suite = "sequential-resident", BudgetMiB = 1024, DiskSpd = tool }));
        Reject(() => VerificationPlan.Validate(options with { Suite = "cache-layout-reset", BudgetMiB = 1024 }));
        Reject(() => VerificationPlan.Validate(options with { Suite = "cache-layout-reset", BudgetMiB = 2048, CaseFilter = "ResetAfterRandom" }));
        Reject(() => VerificationPlan.Validate(options with { Suite = "cache-layout-reset", BudgetMiB = 2048, DiskSpd = null }));
        var warmState = new QueueCache.Management.WriteCacheState(8161, 0, 50UL << 30, 2UL << 30, 2UL << 30, 0, 0, 2UL << 30, 0, 0, 0, 0, 0, 0, 0, 0)
            { Instance = 4, CleanReadBytes = 1UL << 30, ReadHitBytes = 10UL << 30 };
        var warmed = warmState with { ReadHitBytes = 12UL << 30 };
        WarmResidentEvidence.ValidateFirstPass(1L << 30, 1UL << 30);
        Reject(() => WarmResidentEvidence.ValidateFirstPass(800L << 20, 1UL << 30));
        WarmResidentEvidence.Validate(warmState, warmed, 2L << 30, 1UL << 30);
        Reject(() => WarmResidentEvidence.Validate(warmState, warmed with { ReadMissBytes = 4096 }, 2L << 30, 1UL << 30));
        Reject(() => WarmResidentEvidence.Validate(warmState, warmed with { ReadHitBytes = 11UL << 30 }, 2L << 30, 1UL << 30));
        Reject(() => WarmResidentEvidence.Validate(warmState, warmed with { Instance = 5 }, 2L << 30, 1UL << 30));
        Reject(() => WarmResidentEvidence.Validate(warmState, warmed with { CleanReadBytes = 4096 }, 2L << 30, 1UL << 30));
        Reject(() => WarmResidentEvidence.Validate(warmState, warmed with { CleanReadBytes = 0, DirtyBytes = 600UL << 20, InFlightBytes = 500UL << 20 }, 2L << 30, 1UL << 30));
        Reject(() => WarmResidentEvidence.Validate(warmState, warmed, 0, 1UL << 30));
        var layoutPerformance = JsonSerializer.Deserialize<QueueCache.Management.CachePerformance>("{\"Frequency\":10000000}")!;
        var layoutAttribution = JsonSerializer.Deserialize<QueueCache.Management.CacheAttribution>("{}")!;
        var layoutDiagnostics = new QueueCache.Management.CacheDiagnostics(0, 0, 0, 0, 0, 0, 0, 0, 0)
        { Attribution = layoutAttribution, CallerPath = new(0, 0, 0), CopyOffloadReads = 0 };
        var layoutBefore = new CacheLayoutSnapshot(warmState, layoutPerformance, layoutDiagnostics);
        var layoutAfter = layoutBefore with { State = warmed };
        CacheLayoutEvidence.ValidateTransition(null, layoutBefore, false);
        CacheLayoutEvidence.ValidateTransition(warmState.Generation, layoutBefore, true);
        Reject(() => CacheLayoutEvidence.ValidateTransition(null, layoutBefore, true));
        Reject(() => CacheLayoutEvidence.ValidateTransition(warmState.Generation + 1, layoutBefore, true));
        Reject(() => CacheLayoutEvidence.ValidateTransition(warmState.Generation, layoutBefore, false));
        CacheLayoutEvidence.ValidateScore(layoutBefore, layoutAfter, 2L << 30);
        Reject(() => CacheLayoutEvidence.ValidateScore(layoutBefore, layoutAfter with { Performance = layoutPerformance with { TimingEnabled = 1 } }, 2L << 30));
        Reject(() => CacheLayoutEvidence.ValidateScore(layoutBefore, layoutAfter with { Diagnostics = layoutDiagnostics with { Attribution = null } }, 2L << 30));
        foreach (var attribution in new[] { layoutAttribution with { LowerReadAttempts = 1 },
                     layoutAttribution with { LowerWriteAttempts = 1 }, layoutAttribution with { LowerFlushAttempts = 1 } })
            Reject(() => CacheLayoutEvidence.ValidateScore(layoutBefore,
                layoutAfter with { Diagnostics = layoutDiagnostics with { Attribution = attribution } }, 2L << 30));
        Reject(() => CacheLayoutEvidence.ValidateScore(layoutBefore, layoutAfter with { State = warmed with { DirtyBytes = 4096 } }, 2L << 30));
        Check(true, "layout evidence rejects reallocation during reuse, missing counters, timing and lower I/O");
        var measuredLayout = layoutBefore with { Diagnostics = layoutDiagnostics with { Layout = new(3, 300_000, 290_000, 280_000, 1_000, 0) } };
        CacheLayoutEvidence.ValidateLayout(measuredLayout, 2);
        CacheLayoutEvidence.ValidateLayout(measuredLayout, null);
        Reject(() => CacheLayoutEvidence.ValidateLayout(layoutBefore, null));
        Reject(() => CacheLayoutEvidence.ValidateLayout(measuredLayout, 3));
        foreach (var bad in new QueueCache.Management.CacheLayout[] { new(0, 300_000, 0, 0, 0, 0), new(3, 1_000, 0, 0, 0, 0),
                     new(3, 300_000, 290_000, 290_000, 1, 0), new(3, 300_000, 300_001, 0, 0, 0) })
            Reject(() => CacheLayoutEvidence.ValidateLayout(measuredLayout with { Diagnostics = layoutDiagnostics with { Layout = bad } }, null));
        Check(true, "layout measurement must exist, be fresh, cover the resident file and be internally consistent");
        Check(CacheLayoutEvidence.IsQuiet(layoutBefore, layoutBefore) &&
            !CacheLayoutEvidence.IsQuiet(layoutBefore, layoutBefore with { State = warmState with { DirtyBytes = 12288 } }) &&
            !CacheLayoutEvidence.IsQuiet(layoutBefore, layoutBefore with { State = warmState with { ReadMissBytes = warmState.ReadMissBytes + 8192 } }) &&
            !CacheLayoutEvidence.IsQuiet(layoutBefore, layoutBefore with { Diagnostics = layoutDiagnostics with { Attribution = layoutAttribution with { LowerWriteAttempts = 1 } } }) &&
            !CacheLayoutEvidence.IsQuiet(layoutBefore, layoutBefore with { Diagnostics = layoutDiagnostics with { Attribution = null } }),
            "a quiet cache has nothing pending, written or read from the disk between snapshots");
        var emptyLayout = layoutBefore with { State = warmState with { CleanReadBytes = 0 } };
        CacheLayoutEvidence.ValidateReset(emptyLayout, emptyLayout);
        foreach (var changed in new[] { emptyLayout.State with { Generation = 2 }, emptyLayout.State with { Instance = 9 },
                     emptyLayout.State with { ReservedBytes = 1 }, emptyLayout.State with { PayloadCapacity = 4096 },
                     emptyLayout.State with { OccupiedSlots = 1 }, emptyLayout.State with { CleanReadBytes = 4096 },
                     emptyLayout.State with { CleanWriteBytes = 4096 }, emptyLayout.State with { DirtyBytes = 4096 },
                     emptyLayout.State with { InFlightBytes = 4096 }, emptyLayout.State with { Errors = 1 },
                     emptyLayout.State with { AcceptedBytes = 1 }, emptyLayout.State with { ReadHitBytes = 1 },
                     emptyLayout.State with { ReadMissBytes = 1 }, emptyLayout.State with { Evictions = 1 } })
            Reject(() => CacheLayoutEvidence.ValidateReset(emptyLayout, emptyLayout with { State = changed }));
        Reject(() => CacheLayoutEvidence.ValidateReset(layoutBefore, emptyLayout));
        Reject(() => CacheLayoutEvidence.ValidateReset(emptyLayout, emptyLayout with { Diagnostics = layoutDiagnostics with { Attribution = null } }));
        foreach (var attribution in new[] { layoutAttribution with { LowerReadAttempts = 1 },
                     layoutAttribution with { LowerWriteAttempts = 1 }, layoutAttribution with { LowerFlushAttempts = 1 } })
            Reject(() => CacheLayoutEvidence.ValidateReset(emptyLayout,
                emptyLayout with { Diagnostics = layoutDiagnostics with { Attribution = attribution } }));
        Check(true, "same-allocation reset requires empty stable memory, unchanged counters and zero lower I/O");
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
        var drained = healthy with { Flags = healthy.Flags & ~1u, DirtyBytes = 0, InFlightBytes = 0 };
        Check(VerificationWorker.RestorationMismatches(original, drained, healthy, "[]", 0).Count == 0,
            "matching restoration accepted");
        var enabled = healthy with { Flags = 1 };
        Check(VerificationWorker.RestorationMismatches(original with { State = enabled }, drained, enabled with { DirtyBytes = 8192, InFlightBytes = 4096 }, "[]", 0).Count == 0,
            "a re-enabled restored cache may already hold new writes from Windows");
        foreach (var (name, changed) in new (string, QueueCache.Management.WriteCacheState)[]
        {
            ("DirtyBytes when disabled", drained with { DirtyBytes = 1 }),
            ("InFlightBytes when disabled", drained with { InFlightBytes = 1 }),
            ("Enabled when disabled", enabled)
        })
        {
            var mismatch = VerificationWorker.RestorationMismatches(original, changed, healthy, "[]", 0);
            Check(mismatch.Count == 1 && mismatch[0].StartsWith(name + ": expected "), "restoration proves the drain at the disabled boundary: " + name);
        }
        Check(!healthy.Enabled && VerificationWorker.RestorationMismatches(original, drained, healthy with { DirtyBytes = 1 }, "[]", 0)
            .SequenceEqual(["DirtyBytes: expected 0, actual 1"]), "a cache restored as disabled must stay empty");
        foreach (var (name, changed) in new (string, QueueCache.Management.WriteCacheState)[]
        {
            ("Errors", healthy with { Errors = 1 }),
            ("Instance", healthy with { Instance = 1 }),
            ("BudgetBytes", healthy with { BudgetBytes = 1 }),
            ("Enabled", healthy with { Flags = 1 }),
            ("UnsafeDefer", healthy with { Flags = 32 }),
            ("Options", healthy with { Options = new QueueCache.Management.CacheOptions() })
        })
        {
            var mismatch = VerificationWorker.RestorationMismatches(original, drained, changed, "[]", 0);
            Check(mismatch.Count == 1 && mismatch[0].StartsWith(name + ": expected ") && mismatch[0].Contains(", actual "),
                "restoration preserves and identifies " + name);
        }
        Check(VerificationWorker.RestorationMismatches(original, drained, healthy, "changed", 1).Count == 2,
            "profile and timing mismatches both retained");
        var recordedBackoff = original with { CallerBackoff = 16 };
        Check(VerificationWorker.RestorationMismatches(recordedBackoff, drained, healthy, "[]", 0, 16).Count == 0,
            "runtime caller backoff restored exactly");
        Check(VerificationWorker.RestorationMismatches(recordedBackoff, drained, healthy, "[]", 0, 256)
            .SequenceEqual(["CallerBackoff: expected 16, actual 256"]), "backoff mismatch identifies both settings");
        Check(VerificationWorker.RestorationMismatches(recordedBackoff, drained, healthy, "[]", 0).Count == 1,
            "missing driver backoff cannot pass restoration");
        Check(VerificationWorker.RestorationMismatches(original, drained, healthy, "[]", 0, 256).Count == 0,
            "older snapshots do not invent a backoff requirement");
        var originalOptions = new QueueCache.Management.CacheOptions();
        Check(VerificationWorker.RestorationMismatches(original with { State = healthy with { Options = originalOptions } },
            drained, healthy with { Options = originalOptions with { } }, "[]", 0).Count == 0,
            "restoration compares option values rather than object identity");
        ApplyRollbackChecks(healthy, Check);
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
                await OwnedProcess.RunAsync(executable, [.. prefix, "--runner-child", "hang"], store.PathFor("missing-priority-warmup"),
                    TimeSpan.FromSeconds(20), CancellationToken.None, new(System.Diagnostics.ProcessPriorityClass.Normal, 5));
                throw new Exception("Missing scheduling warmup accepted.");
            }
            catch (ArgumentException) { }
            Check(!File.Exists(store.PathFor("missing-priority-warmup.process.json")), "invalid scheduling contracts never launch a child");
            try
            {
                await OwnedProcess.RunAsync(executable, [.. prefix, "--runner-child", "hang", "-W3"], store.PathFor("priority-cancel"),
                    TimeSpan.FromMilliseconds(500), CancellationToken.None, new(System.Diagnostics.ProcessPriorityClass.BelowNormal, 2));
                throw new Exception("Scheduled child deadline did not fire.");
            }
            catch (TimeoutException) { }
            Check(File.Exists(store.PathFor("priority-cancel.scheduling.json")) &&
                JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(File.ReadAllText(store.PathFor("priority-cancel.exit.json")))!["Exited"]!.GetValue<bool>(),
                "owned priority controls are read back, recorded and stopped on timeout");
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
            using var hostProcess = System.Diagnostics.Process.GetCurrentProcess();
            var originalPriority = hostProcess.PriorityClass;
            try
            {
                hostProcess.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;
                var rejectedParent = store.PathFor("concurrent-bad-priority");
                var guarded = new VerificationRunner(executable, [.. prefix, "--fake-verification", "success"], store.PathFor("leases"));
                try
                {
                    await guarded.RunAsync(new("Q:", Suite: "cache-concurrency", Output: rejectedParent,
                        DiskSpd: executable, BudgetMiB: 2048, Repeats: 1), null, CancellationToken.None);
                    throw new Exception("Below-normal cache exercise accepted.");
                }
                catch (IOException ex)
                {
                    Check(ex.Message.Contains("normal-priority") && !Directory.Exists(rejectedParent),
                        "priority gate rejects exercises before target access or output creation");
                }
                // CI's build task runs at below-normal priority. These fixture cases
                // explicitly satisfy the real runner gate; no driver or DiskSpd runs.
                hostProcess.PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal;
                foreach (var mode in new[] { "concurrent-empty-checks", "concurrent-failed-checks" })
                {
                    var parent = store.PathFor(mode);
                    var runner = new VerificationRunner(executable, [.. prefix, "--fake-verification", mode], store.PathFor("leases"));
                    var exit = await runner.RunAsync(new("Q:", Suite: "cache-concurrency", Output: parent,
                        DiskSpd: executable, BudgetMiB: 2048, Repeats: 1), new InlineProgress(_ => { }), CancellationToken.None);
                    var directory = Directory.GetDirectories(parent).Single();
                    using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "status.json")));
                    using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
                    Check(exit != 0 && state.RootElement.GetProperty("Status").GetString() == "INCOMPLETE" &&
                        state.RootElement.GetProperty("Expected").GetInt32() == 13 && state.RootElement.GetProperty("Collected").GetInt32() == 1,
                        "concurrency never scores after empty/failing byte checks: " + mode);
                    Check(manifest.RootElement.GetProperty("CacheExerciseCases").GetArrayLength() == 12 &&
                        manifest.RootElement.GetProperty("ExpectedCases")[0].GetString() == "concurrent-neighbor-sectors",
                        "concurrency manifest separates its byte oracle from complete immutable performance cases");
                    Check(!Directory.GetFiles(directory, "*-prepare.job.json").Any() && File.Exists(Path.Combine(directory, "restored.json")) &&
                        File.Exists(Path.Combine(directory, "FINISHED.txt")), "concurrency failure stops preparation and restores ownership");
                    OwnedProcess.EnsureStopped(directory);
                }
                foreach (var mode in new[] { "partial-empty-checks", "partial-failed-checks" })
                {
                    var parent = store.PathFor(mode);
                    var runner = new VerificationRunner(executable, [.. prefix, "--fake-verification", mode], store.PathFor("leases"));
                    var exit = await runner.RunAsync(new("Q:", Suite: "partial-read-accounting", Output: parent,
                        BudgetMiB: 256), new InlineProgress(_ => { }), CancellationToken.None);
                    var directory = Directory.GetDirectories(parent).Single();
                    using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "status.json")));
                    Check(exit != 0 && state.RootElement.GetProperty("Status").GetString() == "INCOMPLETE" &&
                        state.RootElement.GetProperty("Expected").GetInt32() == 1 &&
                        state.RootElement.GetProperty("Collected").GetInt32() == 1,
                        "partial accounting rejects empty or failed byte/counter evidence: " + mode);
                    Check(File.Exists(Path.Combine(directory, "restored.json")) && File.Exists(Path.Combine(directory, "FINISHED.txt")),
                        "partial accounting failures retain completion and independent restoration evidence");
                    OwnedProcess.EnsureStopped(directory);
                }
                foreach (var mode in new[] { "ram-reference-empty", "ram-reference-failed" })
                foreach (var suiteName in new[] { "ram-read-reference", "ram-read-queue" })
                {
                    var parent = store.PathFor(mode + "-" + suiteName);
                    var runner = new VerificationRunner(executable, [.. prefix, "--fake-verification", mode], store.PathFor("leases"));
                    var exit = await runner.RunAsync(new("Q:", Suite: suiteName, Output: parent,
                        DiskSpd: executable, BudgetMiB: 2048, Repeats: 1), new InlineProgress(_ => { }), CancellationToken.None);
                    var directory = Directory.GetDirectories(parent).Single();
                    using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "status.json")));
                    Check(exit != 0 && state.RootElement.GetProperty("Status").GetString() == "INCOMPLETE" &&
                        File.Exists(Path.Combine(directory, "ram-read-reference-owned.json.cleanup-requested")) &&
                        File.Exists(Path.Combine(directory, "restored.json")),
                        "RAM reference failure invokes independent owned fixture and cache restoration: " + suiteName + " " + mode);
                    OwnedProcess.EnsureStopped(directory);
                }
            }
            finally { hostProcess.PriorityClass = originalPriority; }
            foreach (var mode in new[] { "removal-success", "removal-unobserved", "removal-veto", "removal-stale", "removal-cancel", "removal-worker-failure", "removal-presence-failure", "removal-missing-preparation", "removal-windows-success", "removal-windows-missing-before" })
            {
                var parent = store.PathFor(mode);
                using var cancellation = new CancellationTokenSource();
                var runner = new VerificationRunner(executable, [.. prefix, "--fake-verification", mode], store.PathFor("leases"));
                var progress = new InlineProgress(message =>
                {
                    if (!message.Contains("Awaiting the same disk and reconnect-ack.json")) return;
                    var directory = Directory.GetDirectories(parent).Single();
                    if (mode == "removal-cancel") { cancellation.Cancel(); return; }
                    using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "removal-ready.json")));
                    var ack = ready.RootElement.GetProperty("Acknowledgement").Deserialize<DiskReconnectAcknowledgement>()!;
                    RunStorage.AtomicJson(Path.Combine(directory, "reconnect-ack.json"), mode == "removal-stale" ? ack with { RunId = "old-run" } : ack);
                });
                var exit = await runner.RunAsync(removalOptions with { Volume = "Q:", Output = parent, Suite = mode.StartsWith("removal-windows-") ? "disk-removal-windows" : "disk-removal" }, progress, cancellation.Token);
                var directory = Directory.GetDirectories(parent).Single();
                using var status = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "status.json")));
                var expected = mode is "removal-success" or "removal-windows-success" ? "COMPLETED" : mode is "removal-unobserved" or "removal-veto" or "removal-missing-preparation" ? "INCOMPLETE" : "RESTORATION_FAILED";
                Check(status.RootElement.GetProperty("Status").GetString() == expected && (exit == 0) == (mode is "removal-success" or "removal-windows-success"), "removal coordinator " + mode);
                Check(Directory.GetFiles(directory, "*-restore.job.json").Any() == (mode is "removal-success" or "removal-windows-success" or "removal-unobserved" or "removal-veto" or "removal-missing-preparation"),
                    "removal restoration requires completed reconnect verification " + mode);
                Check(File.Exists(Path.Combine(directory, "FINISHED.txt")), "removal completion marker " + mode);
                OwnedProcess.EnsureStopped(directory);
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

    private sealed class FakeCampaignHost(string mode) : IVerificationCampaignHost
    {
        public int Runs { get; private set; }
        public bool Cleaned { get; private set; }
        public bool Disposed { get; private set; }
        private readonly List<CampaignTargetEvidence> targets = [];
        public Task<IReadOnlyList<CampaignTargetEvidence>> PreflightAsync(VerificationCampaignOptions options, string directory, IProgress<string> progress, CancellationToken token)
        {
            var drivers = new QueueCache.Operations.LoadedDriverObservation(true, null,
                [new("fixture-filter.sys", "fixture-filter.sys", new string('A', 64), "ignored", null), new("fixture-provider.sys", "fixture-provider.sys", new string('B', 64), "ignored", null)]);
            foreach (var (role, letter) in new[] { (CampaignTargetRole.Performance, 'Q'), (CampaignTargetRole.NtfsLab, 'W') })
            {
                var identity = new QueueCache.Operations.DiskTarget(letter, role == CampaignTargetRole.Performance ? 99990 : 99991, 8L << 30, "fixture-" + letter)
                    { DiskBytes = 24L << 30, VolumeId = role == CampaignTargetRole.Performance ? "{11111111-1111-1111-1111-111111111111}" : "{22222222-2222-2222-2222-222222222222}" };
                var recovery = new RecoverySnapshot(1, identity, new(0, 0, 8UL << 30, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0) { Instance = 4 },
                    false, "fixture unchanged profiles", DateTimeOffset.UtcNow, "fixture-machine", 1, 256);
                targets.Add(new(role, recovery, "NTFS", role == CampaignTargetRole.Performance ? "fixture" : "QC-Lab-1",
                    role == CampaignTargetRole.Performance ? "QEMU HARDDISK" : "Msft Virtual Disk",
                    role == CampaignTargetRole.Performance ? null : @"Q:\QueueCache-Lab\fixture.vhdx", role == CampaignTargetRole.NtfsLab,
                    role == CampaignTargetRole.Performance ? null : "Q:", role == CampaignTargetRole.Performance ? null : false, drivers, true));
            }
            if (mode == "unsafe-target") targets[1] = targets[1] with { LabLayout = false };
            return Task.FromResult<IReadOnlyList<CampaignTargetEvidence>>(targets);
        }

        public async Task<CampaignPhaseResult> RunPhaseAsync(VerificationCampaignPhase phase, CampaignTargetEvidence target,
            IReadOnlyList<CampaignTargetEvidence> allTargets, string directory, string? diskSpdHash, Action<string> created, IProgress<string> progress, CancellationToken token)
        {
            Runs++;
            var storage = new RunStorage(Path.Combine(directory, "phases", phase.Id));
            storage.Write("manifest.json", new { PlanVersion = VerificationPlan.Version, Options = phase.Options, ExpectedCases = phase.ExpectedCases,
                DiskSpdSha256 = diskSpdHash, Provenance = new { LoadedDrivers = target.Drivers } });
            storage.Write("recovery.json", target.Recovery); storage.Write("results.json", Array.Empty<CaseResult>());
            created(storage.DirectoryPath);
            if (mode == "cancel") await Task.Delay(Timeout.Infinite, token);
            var failed = mode is "case-failure" or "restore-failure";
            var status = mode == "restore-failure" ? "RESTORATION_FAILED" : failed ? "INCOMPLETE" : "COMPLETED";
            var rows = phase.ExpectedCases.Select(caseId => new CaseResult(caseId, failed ? "FAIL" : "PASS", "fixture", DateTimeOffset.UtcNow, 0.01)).ToArray();
            storage.Write("results.json", rows);
            storage.Write("status.json", new { Status = status, Expected = phase.ExpectedCases.Count, Collected = rows.Length,
                Failure = failed ? "fixture case failure" : null, RestorationFailure = mode == "restore-failure" ? "fixture restoration failure" : null });
            storage.Write("timing.json", new VerificationTiming(0.05, 0.01, 0.01, 0.01, 0.01, 0.01));
            if (mode != "restore-failure") storage.Write("restored.json", target.Recovery.State);
            File.WriteAllText(storage.PathFor("SUMMARY.md"), "fixture summary"); File.WriteAllText(storage.PathFor("run.log"), "fixture log");
            if (mode != "missing-marker") File.WriteAllText(storage.PathFor("FINISHED.txt"), status + "\n");
            if (mode == "wrong-counts") storage.Write("status.json", new { Status = status, Expected = 999, Collected = rows.Length, Failure = (string?)null, RestorationFailure = (string?)null });
            if (mode == "wrong-driver") storage.Write("manifest.json", new { PlanVersion = VerificationPlan.Version, Options = phase.Options, ExpectedCases = phase.ExpectedCases,
                DiskSpdSha256 = diskSpdHash, Provenance = new { LoadedDrivers = target.Drivers with { Modules = [] } } });
            if (mode == "baseline-drift") storage.Write("recovery.json", target.Recovery with { CallerBackoff = 0 });
            progress.Report("Case fixture: " + status);
            return VerificationCampaignEvidence.ReadPhase(phase, storage.DirectoryPath, failed ? 1 : 0, 0.05, target, diskSpdHash);
        }

        public Task CleanupAsync(string directory, IProgress<string> progress)
        {
            Cleaned = true;
            if (File.Exists(Path.Combine(directory, "completion.json"))) throw new Exception("Terminal event published before cleanup.");
            if (mode == "cleanup-failure") throw new IOException("fixture final restoration failed");
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private static async Task RunCampaignContractsAsync()
    {
        void Check(bool value, string detail) { if (!value) throw new Exception("Campaign contract: " + detail); }
        void Reject(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or JsonException) { return; }
            throw new Exception("Campaign accepted invalid evidence/options.");
        }
        var basic = new VerificationCampaignOptions(new("Q:", BudgetMiB: 2048), "smoke", "W:");
        var smoke = VerificationCampaignPlan.Create(basic);
        Check(smoke.Count == 2 && smoke.All(p => p.Role == CampaignTargetRole.NtfsLab) && smoke.Sum(p => p.ExpectedCases.Count) == 2,
            "smoke composes exactly two maintained NTFS cases without DiskSpd");
        var measured = basic with { Profile = "performance", Verification = basic.Verification with { DiskSpd = Environment.ProcessPath } };
        var full = VerificationCampaignPlan.Create(measured);
        Check(full.Count == 13 && full.Sum(p => p.ExpectedCases.Count) == 118 && full[^1].Options.Suite == "ordering-faults",
            "performance plan uses existing counts and fault injection last");
        Check(full.All(p => p.Options.Suite is not ("ram-read-queue" or "write-performance" or "disk-removal" or "system-files")) &&
            full.Single(p => p.Options.Suite == "cache-sustained").Options.SoakSeconds == 120 &&
            full.Single(p => p.Options.Suite == "cache-sustained").Options.Repeats == 1, "performance excludes retired/invasive/write suites and labels short sustained accounting");
        var release = VerificationCampaignPlan.Create(measured with { Profile = "release-performance", LabRefs = "R:" });
        Check(release.Count == 15 && release.Single(p => p.Options.Suite == "write-performance").ExpectedCases.Count == 72 &&
            release.Single(p => p.Options.Suite == "cache-sustained").Options.SoakSeconds == 1800,
            "release includes explicit complete writes, optional ReFS and thirty-minute sustained contract");
        var focused = VerificationCampaignPlan.Create(measured with { Profile = "focused", FocusSuite = "ram-read-scheduling" });
        Check(focused.Count == 6 && focused.Single(p => p.Options.Suite == "ram-read-scheduling").MeasurementWindows == 30,
            "focused campaign adds only affected measurements and retained checks");
        Reject(() => VerificationCampaignPlan.Create(basic with { Verification = basic.Verification with { Suite = "full" } }));
        Reject(() => VerificationCampaignPlan.Create(basic with { LabNtfs = "q:" }));
        Reject(() => VerificationCampaignPlan.Create(basic with { LabNtfs = "C:" }));
        Reject(() => VerificationCampaignPlan.Create(basic with { FocusSuite = "ram-read-queue" }));
        Reject(() => VerificationCampaignPlan.Create(basic with { Profile = "focused" }));
        Reject(() => VerificationCampaignPlan.Create(basic with { Verification = basic.Verification with { CaseFilter = "one" } }));
        Reject(() => VerificationCampaignPlan.Create(basic with { Verification = basic.Verification with { SoakSeconds = 120 } }));
        Reject(() => VerificationCampaignPlan.Create(basic with { Verification = basic.Verification with { DeadlineMinutes = -1 } }));
        Reject(() => VerificationCampaignPlan.Create(measured with { Verification = measured.Verification with { BudgetMiB = 1024 } }));
        var scheduling = RamReadReferencePlan.SchedulingCases(3);
        var boundary = new RecoverySnapshot(1, new QueueCache.Operations.DiskTarget('Q', 99990, 8L << 30, "fixture"),
            new(0, 0, 8UL << 30, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), false, "unchanged", DateTimeOffset.UtcNow, "fixture");
        var pending = boundary with { State = boundary.State with { DirtyBytes = 4096 } };
        Reject(() => VerificationCampaignEvidence.ValidateConfiguration(boundary, pending));
        var preparationOrder = new List<string>(); var drainedBoundary = false;
        var preparedBoundary = await VerificationWorker.PrepareCampaignAsync(boundary, () => drainedBoundary ? boundary : pending,
            () => preparationOrder.Add("volume"), () => { preparationOrder.Add("cache"); drainedBoundary = true; },
            (stage, _) => preparationOrder.Add(stage), TimeSpan.FromSeconds(1));
        Check(preparedBoundary == boundary && preparationOrder.SequenceEqual(new[] { "before", "volume", "after-volume-flush", "cache", "after-cache-flush", "clean" }),
            "explicit boundary preparation drains after filesystem flush without changing the baseline");
        foreach (var invalid in new[] { pending with { State = pending.State with { LastError = 5 } }, pending with { State = pending.State with { Errors = 1 } } })
        {
            var controls = 0;
            try { await VerificationWorker.PrepareCampaignAsync(boundary, () => invalid, () => controls++, () => controls++, (_, _) => { }, TimeSpan.FromSeconds(1));
                throw new Exception("Preparation accepted a fault."); }
            catch (IOException) { Check(controls == 0, "faults never reach preparation controls or drain retry"); }
        }
        try { await VerificationWorker.PrepareCampaignAsync(boundary, () => pending, () => { }, () => { }, (_, _) => { }, TimeSpan.Zero);
            throw new Exception("Preparation ignored its clean-state deadline."); }
        catch (TimeoutException) { }
        Check(scheduling.Count == 30 && scheduling.Select(c => c.Id).Distinct().Count() == 30 && scheduling.All(c => c.SchedulingControl && c.Access == QueueCache.Operations.ManagedDisks.RamAccess.Direct),
            "RAM scheduling freezes the existing Direct/copy path with unique alternating controls");
        Check(scheduling.Count(c => c.Interleaved) == 6 && scheduling.Where(c => c.Interleaved).All(c => c.Threads == 4 && c.BlockKiB == 1024),
            "nonoverlap control applies only to four large sequential readers");
        var interleaved = scheduling.First(c => c.Interleaved && c.DisableAffinity);
        var args = RamReadReferencePlan.Arguments(interleaved, 10);
        Check(args.Contains("-n") && args.Contains("-s4M") && args.Contains("-T1M") && args.Contains("-W3") && !args.Contains("-si"),
            "nonoverlap stride control does not add interlocked submission coordination");
        const string controlledProfile = "<Results><Profile><TimeSpans><TimeSpan><DisableAffinity>true</DisableAffinity><Duration>10</Duration><Warmup>3</Warmup><Targets><Target><BlockSize>1048576</BlockSize><RequestCount>2</RequestCount><ThreadsPerFile>4</ThreadsPerFile><WriteRatio>0</WriteRatio><IOPriority>3</IOPriority><MaxFileSize>1073741824</MaxFileSize><DisableOSCache>true</DisableOSCache><UseLargePages>false</UseLargePages><StrideSize>4194304</StrideSize><ThreadStride>1048576</ThreadStride><InterlockedSequential>false</InterlockedSequential></Target></Targets></TimeSpan></TimeSpans></Profile></Results>";
        RamReadReferencePlan.ValidateProfile(interleaved, controlledProfile, 10);
        foreach (var replacement in new[] {
            ("<DisableAffinity>true</DisableAffinity>", "<DisableAffinity>false</DisableAffinity>"),
            ("<Warmup>3</Warmup>", "<Warmup>0</Warmup>"),
            ("<ThreadStride>1048576</ThreadStride>", "<ThreadStride>0</ThreadStride>"),
            ("<InterlockedSequential>false</InterlockedSequential>", "<InterlockedSequential>true</InterlockedSequential>"),
            ("<UseLargePages>false</UseLargePages>", "") })
            Reject(() => RamReadReferencePlan.ValidateProfile(interleaved, controlledProfile.Replace(replacement.Item1, replacement.Item2), 10));
        var randomControl = scheduling.First(c => c.Random);
        var randomProfile = controlledProfile.Replace("<DisableAffinity>true</DisableAffinity>", "<DisableAffinity>false</DisableAffinity>")
            .Replace("<BlockSize>1048576</BlockSize>", "<BlockSize>4096</BlockSize>")
            .Replace("<RequestCount>2</RequestCount>", "<RequestCount>1</RequestCount>")
            .Replace("<ThreadsPerFile>4</ThreadsPerFile>", "<ThreadsPerFile>1</ThreadsPerFile>")
            .Replace("<StrideSize>4194304</StrideSize>", "<Random>4096</Random>");
        RamReadReferencePlan.ValidateProfile(randomControl, randomProfile, 10);
        Reject(() => RamReadReferencePlan.ValidateProfile(randomControl, randomProfile.Replace("<Random>4096</Random>", "<Random>0</Random>"), 10));

        var parent = Path.Combine(Path.GetTempPath(), "QueueCache-CampaignContracts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            foreach (var mode in new[] { "success", "case-failure", "restore-failure", "unsafe-target", "missing-marker", "wrong-counts", "wrong-driver", "baseline-drift", "cleanup-failure", "cancel" })
            {
                var host = new FakeCampaignHost(mode);
                var runner = new VerificationCampaignRunner(host);
                using var cancellation = new CancellationTokenSource();
                var messages = new List<string>();
                var progress = new InlineProgress(message => { messages.Add(message); if (mode == "cancel" && message.Contains("Phase 1 of")) cancellation.CancelAfter(100); });
                var exit = await runner.RunAsync(basic with { Verification = basic.Verification with { Output = Path.Combine(parent, mode) } }, progress, cancellation.Token);
                var directory = runner.DirectoryPath!;
                var completion = VerificationCampaignEvidence.ReadCompletion(directory);
                Check(host.Cleaned && host.Disposed && File.Exists(Path.Combine(directory, "restoration.json")), "cleanup and ownership release before event: " + mode);
                Check(completion.Status == (mode == "success" ? "COMPLETED" : mode is "restore-failure" or "cleanup-failure" ? "RESTORATION_FAILED" : mode == "cancel" ? "CANCELLED" : "INCOMPLETE"), "honest terminal status: " + mode);
                Check((exit == 0) == (mode == "success") && (exit == 130) == (mode == "cancel"), "exit contract: " + mode);
                Check(host.Runs == (mode == "unsafe-target" ? 0 : mode is "success" or "cleanup-failure" ? 2 : 1), "no phase after failure: " + mode);
                Check(messages.Last().Contains(completion.Status + ":") && File.ReadAllText(Path.Combine(directory, "run.log")).Contains(messages.Last()), "synchronous final/error progress: " + mode);
                Check(completion == VerificationCampaignEvidence.ReadCompletion(directory), "stable event identity for controller deduplication: " + mode);
                if (mode == "success")
                {
                    Check(completion.CompletedPhases == 2 && completion.CollectedCases == 2 && completion.Restoration == "RESTORED", "complete counts/restoration");
                    var resultPath = Path.Combine(directory, "results.json"); var resultText = File.ReadAllText(resultPath);
                    var recorded = JsonSerializer.Deserialize<CampaignPhaseResult[]>(resultText)!;
                    RunStorage.AtomicJson(resultPath, recorded.Select((r, i) => i == 0 ? r with { Collected = 0 } : r));
                    Reject(() => VerificationCampaignEvidence.ReadCompletion(directory)); File.WriteAllText(resultPath, resultText);
                    var eventPath = Path.Combine(directory, "completion.json"); var eventText = File.ReadAllText(eventPath);
                    var eventNode = System.Text.Json.Nodes.JsonNode.Parse(eventText)!.AsObject(); eventNode.Remove("CollectedCases");
                    File.WriteAllText(eventPath, eventNode.ToJsonString()); Reject(() => VerificationCampaignEvidence.ReadCompletion(directory)); File.WriteAllText(eventPath, eventText);
                    var manifest = Path.Combine(directory, "manifest.json"); var original = File.ReadAllText(manifest);
                    File.AppendAllText(manifest, " "); Reject(() => VerificationCampaignEvidence.ReadCompletion(directory)); File.WriteAllText(manifest, original);
                    File.Delete(Path.Combine(directory, "FINISHED.txt")); Reject(() => VerificationCampaignEvidence.ReadCompletion(directory));
                }
            }
        }
        finally { Directory.Delete(parent, recursive: true); }
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
        if (mode == "removal-worker-failure" && job.Operation == "disk-removal-eject")
        {
            Console.Error.WriteLine("fixture: eject outcome unknown after worker failure");
            return 1;
        }
        if (mode == "check-failure" && job.Operation == "files" || mode == "restore-failure" && job.Operation == "restore" || mode == "capture-failure" && job.Operation == "capture")
        {
            Console.Error.WriteLine("fixture failure detail: Access is denied.");
            return 1;
        }
        object reply = new
        {
            Fake = true
        };
        if (job.Operation == "concurrent-sectors")
            reply = mode == "concurrent-empty-checks" ? Array.Empty<QueueCache.Operations.CheckResult>() :
                new QueueCache.Operations.CheckResult[] { new("fixture-neighbors", "FAIL", "fixture byte mismatch") };
        if (job.Operation == "ram-read-reference")
        {
            RunStorage.AtomicJson(job.OraclePath!, new { FixtureOwnership = true });
            reply = mode == "ram-reference-empty" ? Array.Empty<QueueCache.Operations.CheckResult>() :
                new QueueCache.Operations.CheckResult[] { new("fixture-reference", "FAIL", "fixture accounting mismatch") };
        }
        if (job.Operation == "ram-read-cleanup")
            File.WriteAllText(job.OraclePath! + ".cleanup-requested", "owned cleanup requested");
        if (job.Operation == "partial-read-accounting")
            reply = mode == "partial-empty-checks" ? Array.Empty<QueueCache.Operations.CheckResult>() :
                new QueueCache.Operations.CheckResult[] { new("fixture-partial", "FAIL", "fixture accounting mismatch") };
        if (job.Operation is "disk-removal-eject" or "disk-removal-eject-windows")
            reply = new QueueCache.Operations.DiskEjectResult(new(99999, "fixture-only", "fixture", ["Q:"], true, null),
                mode == "removal-veto" ? 23u : 0, mode == "removal-veto" ? 8u : 0, mode == "removal-veto" ? "fixture-device" : "",
                mode is not ("removal-unobserved" or "removal-presence-failure" or "removal-veto"), mode == "removal-presence-failure" ? 0x13u : 0, Preparation: mode == "removal-missing-preparation" ? null :
                [new("Q:", "", new(1, 0, 50UL << 30, 256UL << 20, 0, 8UL << 20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0) { Instance = 4 },
                    new(0, 0, 50UL << 30, 256UL << 20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0) { Instance = 4 },
                    new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
                    new(0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), true)]);
        if (job.Operation == "disk-removal-eject-windows" && mode != "removal-windows-missing-before")
            RunStorage.AtomicJson(job.Reply + ".before.json", new WindowsEjectPrecondition(job.Expected!,
                new(1, 0, 50UL << 30, 256UL << 20, 0, 8UL << 20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0) { Instance = 4 },
                new(0, 0, 0, 0, 0, 0, 0, 0, 0), DateTimeOffset.UtcNow));
        if (job.Operation == "disk-removal-verify")
            reply = new QueueCache.Operations.CheckResult[] { new("fixture-oracle", "PASS", "host-only fixture") };
        if (job.Operation == "capture")
            reply = new RecoverySnapshot(1, new QueueCache.Operations.DiskTarget('Q', 99999, 50L << 30, "fixture-only") { DiskBytes = 50L << 30 },
                new QueueCache.Management.WriteCacheState(0, 0, 50UL << 30, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
                false, "[]", DateTimeOffset.UtcNow, Environment.MachineName);
        RunStorage.AtomicJson(job.Reply, reply);
        return 0;
    }

    private sealed class FakeCacheControl(QueueCache.Management.WriteCacheState state) : QueueCache.Operations.ICacheControl
    {
        public QueueCache.Management.WriteCacheState State = state;
        public readonly List<string> Calls = [];
        public Func<QueueCache.Management.WriteCacheAction, ulong, bool> Fails = (_, _) => false;
        public QueueCache.Management.WriteCacheState GetWriteCacheState() => State;
        public void SetOptions(QueueCache.Management.CacheOptions options)
        {
            Calls.Add("Options");
            State = State with { Options = options };
        }
        public void Control(QueueCache.Management.WriteCacheAction action, ulong budgetBytes = 0, ulong value = 0)
        {
            Calls.Add(action.ToString());
            if (action == QueueCache.Management.WriteCacheAction.Configure)
                State = State with { BudgetBytes = 0, ReservedBytes = 0, PayloadCapacity = 0 }; // Old memory freed first.
            if (Fails(action, budgetBytes))
                throw new IOException($"{action} failed");
            State = action switch
            {
                QueueCache.Management.WriteCacheAction.Disable => State with { Flags = State.Flags & ~1u },
                QueueCache.Management.WriteCacheAction.Enable when State.BudgetBytes == 0 => throw new IOException("no budget"),
                QueueCache.Management.WriteCacheAction.Enable => State with { Flags = State.Flags | 1u },
                QueueCache.Management.WriteCacheAction.FlushPolicy => State with { Flags = value == 1 ? State.Flags | 32u : State.Flags & ~32u },
                QueueCache.Management.WriteCacheAction.Configure => State with { BudgetBytes = budgetBytes, ReservedBytes = budgetBytes, PayloadCapacity = budgetBytes },
                QueueCache.Management.WriteCacheAction.Release => State with { Flags = State.Flags & ~1u, BudgetBytes = 0, ReservedBytes = 0, PayloadCapacity = 0 },
                _ => State
            };
        }
    }

    private static void ApplyRollbackChecks(QueueCache.Management.WriteCacheState healthy, Action<bool, string> Check)
    {
        const ulong MiB = 1 << 20;
        var active = healthy with
        {
            Flags = 1u | 32u, BudgetBytes = 1024 * MiB, ReservedBytes = 1024 * MiB, PayloadCapacity = 1024 * MiB,
            Options = new QueueCache.Management.CacheOptions()
        };
        var bigger = new QueueCache.Operations.CacheConfiguration(2048, QueueCache.Operations.CachePreset.Strict)
        {
            Options = new QueueCache.Management.CacheOptions(Drain: QueueCache.Management.DrainAlgorithm.Eager)
        };
        var fake = new FakeCacheControl(active);
        var applied = QueueCache.Operations.ConfigurationManager.ApplySteps(fake, active, bigger, settleTimeout: TimeSpan.Zero);
        Check(applied.BudgetBytes == 2048 * MiB && !applied.UnsafeDefer && applied.Enabled && applied.Options == bigger.Options,
            "a settings change applies completely");

        fake = new FakeCacheControl(active) { Fails = (action, budget) => action == QueueCache.Management.WriteCacheAction.Configure && budget == 2048 * MiB };
        try
        {
            QueueCache.Operations.ConfigurationManager.ApplySteps(fake, active, bigger, settleTimeout: TimeSpan.Zero);
            throw new Exception("A failed resize was reported as applied.");
        }
        catch (QueueCache.Operations.ConfigurationNotAppliedException failure)
        {
            Check(failure.PreviousSettingsRestored && failure.Message.Contains("previous settings were restored") &&
                fake.State.Enabled && fake.State.BudgetBytes == 1024 * MiB && fake.State.UnsafeDefer &&
                fake.State.Options == active.Options, "a failed resize restores the previous size, preset, options and state");
        }

        fake = new FakeCacheControl(active) { Fails = (action, _) => action == QueueCache.Management.WriteCacheAction.Configure };
        try
        {
            QueueCache.Operations.ConfigurationManager.ApplySteps(fake, active, bigger, settleTimeout: TimeSpan.Zero);
            throw new Exception("A failed resize with a failed rollback was reported as applied.");
        }
        catch (QueueCache.Operations.ConfigurationNotAppliedException failure)
        {
            Check(!failure.PreviousSettingsRestored && failure.Message.Contains("could not be restored") &&
                failure.Message.Contains("The cache is now: disabled, 0 MiB"), "a failed rollback states exactly what is left");
        }

        fake = new FakeCacheControl(active) { Fails = (action, _) => action == QueueCache.Management.WriteCacheAction.Disable };
        try
        {
            QueueCache.Operations.ConfigurationManager.ApplySteps(fake, active, bigger, settleTimeout: TimeSpan.Zero);
            throw new Exception("A failed drain was reported as applied.");
        }
        catch (QueueCache.Operations.ConfigurationNotAppliedException failure)
        {
            Check(failure.PreviousSettingsRestored && fake.Calls.SequenceEqual(["Disable"]) && fake.State == active,
                "a cache that cannot be emptied is left untouched");
        }

        var unconfigured = healthy with { Options = new QueueCache.Management.CacheOptions() };
        fake = new FakeCacheControl(unconfigured) { Fails = (action, _) => action == QueueCache.Management.WriteCacheAction.Enable };
        try
        {
            QueueCache.Operations.ConfigurationManager.ApplySteps(fake, unconfigured, bigger, settleTimeout: TimeSpan.Zero);
            throw new Exception("A failed enable was reported as applied.");
        }
        catch (QueueCache.Operations.ConfigurationNotAppliedException failure)
        {
            Check(failure.PreviousSettingsRestored && fake.State.BudgetBytes == 0 && !fake.State.Enabled &&
                fake.Calls.Contains("Release"), "rolling back to 'never configured' releases the new memory");
        }
    }
}
