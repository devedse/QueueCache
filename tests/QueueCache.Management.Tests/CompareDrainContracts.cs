using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using QueueCache.Developer.Verification;
using QueueCache.Management;

internal static class CompareDrainContracts
{
    public static void Run(Action<bool, string> check, Action<Action> reject)
    {
        var plan = ComparisonPlan.CasesFor("compare-drain", 3);
        check(plan.Count == 6 && plan.Select(c => c.Id).Distinct().Count() == 6 &&
            plan.Select(c => c.Kind).SequenceEqual(["drain-large", "drain-small", "drain-small", "drain-large", "drain-large", "drain-small"]) &&
            plan.All(c => c.Bytes == ComparisonDrain.ExpectedBytes(c.Kind) && c.SettleSeconds == 0) &&
            plan.Where(c => c.Kind == "drain-large").All(c => c.BlockKiB == 1024 && c.Queue == 4 && c.Bytes == 1L << 30) &&
            plan.Where(c => c.Kind == "drain-small").All(c => c.BlockKiB == 4 && c.Threads == 4 && c.Bytes == 256L << 20) &&
            ComparisonDrain.SmallFiles == 4096 && ComparisonDrain.SmallFiles * (long)ComparisonDrain.SmallFileBytes == ComparisonDrain.SmallBytes &&
            ComparisonDrain.PayloadBytes == 1 << 30 && ComparisonDrain.Seed == 147_16384 &&
            !ComparisonPlan.UsesDiskSpd("compare-drain") && VerificationPlan.Suites.Contains("compare-drain"),
            "plan 148 compare-drain: 1 GiB large and 256 MiB small-file workloads, unchanged payload seed, three alternating repetitions");
        check(ComparisonPlan.WorkDirectory("compare-drain", "root", "run-a") == Path.Combine("root", "run-a") &&
            ComparisonPlan.WorkDirectory("compare-drain", "root", "run-b") != ComparisonPlan.WorkDirectory("compare-drain", "root", "run-a"),
            "compare-drain owns a fresh root per run, including after another provider");
        var paired = DrainBatching.Cases(3);
        check(paired.Count == 12 && paired.Select(c => c.Id).Distinct().Count() == 12 &&
            paired.All(c => c.GatherPolicy is not null && c.Bytes == ComparisonDrain.ExpectedBytes(c.Kind)) &&
            paired.GroupBy(c => (c.Kind, c.Repeat)).All(g => g.Count() == 2 && g.Select(c => c.GatherPolicy).Distinct().Count() == 2) &&
            paired.Take(2).Select(c => c.GatherPolicy).SequenceEqual(paired.Skip(4).Take(2).Reverse().Select(c => c.GatherPolicy)) &&
            !ComparisonPlan.UsesDiskSpd(DrainBatching.Suite) && ComparisonPlan.IsComparison(DrainBatching.Suite) && VerificationPlan.Suites.Contains(DrainBatching.Suite),
            "batching is a complete twelve-case same-build paired matrix with reversed order and unchanged payloads");
        var batchingOptions = new VerificationOptions("Q:", DrainBatching.Suite, BudgetMiB: 2048, Product: "QueueCache batching", Provider: ComparisonProvider.QueueCache);
        VerificationPlan.Validate(batchingOptions);
        foreach (var bad in new[] { batchingOptions with { Provider = ComparisonProvider.Direct }, batchingOptions with { Repeats = 2 }, batchingOptions with { CaseFilter = "small" } })
        {
            try { VerificationPlan.Validate(bad); throw new Exception("Accepted incomplete or wrong-provider batching matrix."); }
            catch (ArgumentException) { }
        }
        var options = new VerificationOptions("Q:", "compare-drain", BudgetMiB: 2048, Product: "arbitrary display label", Provider: ComparisonProvider.Direct);
        VerificationPlan.Validate(options);
        VerificationPlan.Validate(options with { Provider = ComparisonProvider.QueueCache });
        VerificationPlan.Validate(options with { Provider = ComparisonProvider.PrimoCache, PrimoCli = Environment.ProcessPath });
        foreach (var invalid in new[] { options with { Provider = null }, options with { Provider = (ComparisonProvider)99 }, options with { Repeats = 2 },
            options with { BudgetMiB = 1024 }, options with { PrimoCli = "unexpected" }, options with { Suite = "compare-snapshot" },
            options with { DiskSpd = Environment.ProcessPath }, options with { CaseFilter = "large" }, options with { KeepWorkloads = true } })
        {
            try { VerificationPlan.Validate(invalid); throw new Exception("Accepted invalid compare-drain options."); }
            catch (ArgumentException) { }
        }
        var preparations = 0;
        var settled = ComparisonDrain.PrepareCleanAsync(ComparisonProvider.QueueCache, attempt =>
        {
            preparations++;
            return Task.FromResult(new ComparisonPending(DateTimeOffset.UtcNow, ComparisonProvider.QueueCache,
                attempt == 1 ? 4096UL : 0UL, 0, null));
        }).GetAwaiter().GetResult();
        check(settled.Attempt == 2 && preparations == 2 && settled.Pending.DirtyBytes == 0,
            "preparation drains new filesystem writes before the scored ready handshake");
        var invalidAttempts = 0;
        reject(() => ComparisonDrain.PrepareCleanAsync(ComparisonProvider.QueueCache, attempt =>
        {
            invalidAttempts++;
            return Task.FromResult(new ComparisonPending(DateTimeOffset.UtcNow, ComparisonProvider.QueueCache, null, null, null));
        }).GetAwaiter().GetResult());
        check(invalidAttempts == 1, "missing pending evidence is never retried as drainage");
        try
        {
            ComparisonDrain.PrepareCleanAsync(ComparisonProvider.QueueCache, attempt =>
                Task.FromResult(new ComparisonPending(DateTimeOffset.UtcNow, ComparisonProvider.QueueCache, 4096, 0, null)),
                TimeSpan.Zero).GetAwaiter().GetResult();
            throw new Exception("Accepted permanently dirty preparation.");
        }
        catch (TimeoutException) { }
        var busyDisk = new ComparisonDiskCounters(DateTimeOffset.UtcNow, 4096, 1, 1);
        var diskObservations = new List<ComparisonDiskCounters>();
        var idleDisk = ComparisonDrain.WaitForDirectDiskIdleAsync(busyDisk,
            () => busyDisk with { Utc = DateTimeOffset.UtcNow, QueueDepth = 0 },
            (_, value) => diskObservations.Add(value)).GetAwaiter().GetResult();
        check(idleDisk.QueueDepth == 0 && diskObservations.Count == 2 && diskObservations[0].QueueDepth == 1,
            "Direct preparation records the busy observation and waits unscored for the same zero-queue precondition");
        reject(() => ComparisonDrain.WaitForDirectDiskIdleAsync(busyDisk,
            () => busyDisk with { BytesWritten = 0 }, (_, _) => { }).GetAwaiter().GetResult());
        try
        {
            ComparisonDrain.WaitForDirectDiskIdleAsync(busyDisk, () => busyDisk, (_, _) => { }, TimeSpan.Zero).GetAwaiter().GetResult();
            throw new Exception("Accepted permanently busy Direct preparation.");
        }
        catch (TimeoutException) { }
        RunDeferredBlocksContracts(check, reject);
        var config = "Status: Active\r\nLevel-1 Cache: 2048,00MB\r\nLevel-2 Cache: 0,00MB\r\nR/W Ratio: Shared\r\nBlock Size: 4KB\r\nDefer-Write: Enabled\r\nLatency: 10s\r\nMode: Native\r\nPrefetch: Disabled\r\n";
        ComparisonDrain.ValidatePrimoConfiguration(config);
        var volumes = "Vol #6     QueueCacheLab (Q:)     NTFS     86,03GB/199,98GB      4KB      1\r\n";
        ComparisonDrain.ValidatePrimoVolumeState(config + "Volume #6: Cache (Active)\r\n", volumes, "Q:", paused: false);
        ComparisonDrain.ValidatePrimoVolumeState(config + "Volume #6: Cache (Paused)\r\n", volumes, "Q:", paused: true);
        reject(() => ComparisonDrain.ValidatePrimoVolumeState(config + "Volume #6: Cache (Active)\r\n", volumes, "Q:", paused: true));
        reject(() => ComparisonDrain.ValidatePrimoVolumeState(config + "Volume #7: Cache (Paused)\r\n", volumes, "Q:", paused: true));
        reject(() => ComparisonDrain.ValidatePrimoConfiguration(config.Replace("2048,00", "1024,00")));
        reject(() => ComparisonDrain.ValidatePrimoConfiguration(config.Replace("Status: Active", "Status: Paused")));
        var primoTaskId = Guid.Parse("7BE4E302-2EB8-4599-BAE3-C1693EF2F398");
        var taskHeader = $"Cache Task #1 {primoTaskId:B}\r\n";
        var primoActive = new ComparisonPrimoObservation(taskHeader + config + "Volume #6: Cache (Active)\r\n", volumes);
        var primoPaused = primoActive with { Status = primoActive.Status.Replace("Cache (Active)", "Cache (Paused)") };
        check(ComparisonDrain.PrimoTaskId(primoActive, "Q:", paused: false) == primoTaskId, "original Primo task GUID is tied to the target-volume owner column");
        foreach (var beforeResume in new[] { primoPaused, primoActive, primoPaused with { Status = primoPaused.Status.Replace("Status: Active", "Status: Paused") } })
        {
            var resumed = false;
            ComparisonDrain.RestorePrimoAsync(primoTaskId, "Q:", () => Task.FromResult(resumed ? primoActive : beforeResume),
                () => { resumed = true; return Task.CompletedTask; }).GetAwaiter().GetResult();
            check(resumed, "Primo recovery accepts the original paused or already-active task and confirms it after resume");
        }
        foreach (var changed in new[]
        {
            primoPaused with { Status = primoPaused.Status.Replace(primoTaskId.ToString("B"), Guid.NewGuid().ToString("B")) },
            primoPaused with { Status = primoPaused.Status.Replace(primoTaskId.ToString("B"), "") },
            primoPaused with { Status = primoPaused.Status + $"Cache Task #2 {Guid.NewGuid():B}\r\n" },
            primoPaused with { Status = primoPaused.Status + taskHeader },
            primoPaused with { Volumes = volumes.Replace("4KB      1", "4KB      2") },
            primoPaused with { Volumes = volumes + volumes },
            primoPaused with { Volumes = volumes.Replace("(Q:)", "(R:)") }
        })
        {
            var resumed = false;
            reject(() => ComparisonDrain.RestorePrimoAsync(primoTaskId, "Q:", () => Task.FromResult(changed),
                () => { resumed = true; return Task.CompletedTask; }).GetAwaiter().GetResult());
            check(!resumed, "missing, ambiguous or changed Primo task ownership never invokes resume");
        }
        foreach (var missing in new Guid?[] { null, Guid.Empty })
        {
            var resumed = false;
            reject(() => ComparisonDrain.RestorePrimoAsync(missing, "Q:", () => Task.FromResult(primoPaused),
                () => { resumed = true; return Task.CompletedTask; }).GetAwaiter().GetResult());
            check(!resumed, "a journal without a valid original Primo task GUID cannot resume a task");
        }
        foreach (var beforeResume in new[] { primoPaused, primoActive, primoPaused with { Status = primoPaused.Status.Replace("Status: Active", "Status: Paused") } })
        {
            var changedConfig = beforeResume with { Status = beforeResume.Status.Replace("2048,00", "1024,00") };
            var resumed = false;
            reject(() => ComparisonDrain.RestorePrimoAsync(primoTaskId, "Q:", () => Task.FromResult(changedConfig),
                () => { resumed = true; return Task.CompletedTask; }).GetAwaiter().GetResult());
            check(!resumed, "changed configuration on the original active or paused Primo task is rejected before resume");
        }
        foreach (var afterResume in new[] { primoActive with { Status = primoActive.Status.Replace(primoTaskId.ToString("B"), Guid.NewGuid().ToString("B")) },
            primoPaused, primoActive with { Status = primoActive.Status.Replace("2048,00", "1024,00") },
            primoActive with { Status = primoActive.Status.Replace("Status: Active", "Status: Paused") } })
        {
            var resumed = false;
            reject(() => ComparisonDrain.RestorePrimoAsync(primoTaskId, "Q:", () => Task.FromResult(resumed ? afterResume : primoPaused),
                () => { resumed = true; return Task.CompletedTask; }).GetAwaiter().GetResult());
            check(resumed, "Primo recovery rechecks GUID, active state and configuration after resume");
        }
        var target = new ComparisonTarget('Q', @"Q:\", "volume-guid", "NTFS", "test", 4096, 20L << 30, 10L << 30, "Fixed", 3);
        var recovery = new ComparisonDrainRecovery("Q:", ComparisonProvider.PrimoCache, "rxpcc.exe", target, true, null, primoTaskId);
        ComparisonDrain.ValidateRecoveryTarget(recovery, target);
        check(JsonSerializer.Deserialize<ComparisonDrainRecovery>(JsonSerializer.Serialize(recovery))!.OriginalPrimoTaskId == primoTaskId,
            "recovery journal preserves the original validated Primo task GUID");
        var legacyDirect = JsonNode.Parse(JsonSerializer.Serialize(recovery with { Provider = ComparisonProvider.Direct, PrimoCli = null }))!.AsObject();
        legacyDirect.Remove("OriginalPrimoTaskId");
        var legacyRecovery = JsonSerializer.Deserialize<ComparisonDrainRecovery>(legacyDirect.ToJsonString())!;
        ComparisonDrain.ValidateRecoveryTarget(legacyRecovery, target);
        check(legacyRecovery.OriginalPrimoTaskId is null, "earlier Direct recovery journals remain readable without a Primo GUID");
        foreach (var changed in new[] { target with { VolumeName = "replacement" }, target with { VolumeName = null },
            target with { DiskNumber = 4 }, target with { TotalBytes = 21L << 30 }, target with { ClusterBytes = 8192 }, target with { Letter = 'R' } })
            reject(() => ComparisonDrain.ValidateRecoveryTarget(recovery, changed));
        var recoveryJson = JsonNode.Parse(JsonSerializer.Serialize(recovery))!.AsObject(); recoveryJson.Remove("ResumeRequired");
        try { JsonSerializer.Deserialize<ComparisonDrainRecovery>(recoveryJson.ToJsonString()); throw new Exception("Missing recovery state was treated as restored."); }
        catch (JsonException) { }

        var original = new WriteCacheState(1 | 32 | 256 | 512, 0, 50UL << 30, 2UL << 30, 2UL << 30, 0, 0, 2UL << 30,
            0, 0, 0, 0, 0, 0, 0, 0) { Instance = 42, Options = new CacheOptions() };
        var paused = original with { Flags = original.Flags & ~1u };
        foreach (var changed in new[] { paused with { Instance = 43 }, paused with { Options = original.Options with { Parallelism = 2 } },
            paused with { BudgetBytes = 1UL << 30 }, paused with { Flags = paused.Flags & ~32u }, paused with { Errors = 1 },
            paused with { LastError = 5 }, paused with { Flags = paused.Flags | 2u }, paused with { Flags = paused.Flags | 16u } })
        {
            var enabled = false;
            reject(() => ComparisonDrain.RestoreQueueCache(original, () => changed, () => enabled = true));
            check(!enabled, "recovery rejects changed identity/settings/errors before invoking Enable");
        }
        foreach (var beforeResume in new[] { paused, original })
        {
            var enabled = false;
            var restored = ComparisonDrain.RestoreQueueCache(original, () => enabled ? original : beforeResume, () => enabled = true);
            check(enabled && restored == original, "recovery restores the original instance whether pause completed or partly failed");
        }
        var enabledBeforePostcheck = false;
        reject(() => ComparisonDrain.RestoreQueueCache(original, () => enabledBeforePostcheck ? original with { Instance = 43 } : paused,
            () => enabledBeforePostcheck = true));
        check(enabledBeforePostcheck, "recovery also rechecks identity after Enable");
        RunQueueCachePauseContracts(original, check, reject);

        using var files = new ComparisonDrainFiles(4 << 20);
        using var same = new ComparisonDrainFiles(4 << 20);
        check(files.LargeHash == same.LargeHash && files.SmallHashes.SequenceEqual(same.SmallHashes) &&
            files.SmallHashes.Select(f => f.Sha256).Distinct().Count() == 64 &&
            ComparisonDrainFiles.ShuffledOrder(ComparisonDrain.SmallFiles).Distinct().Count() == ComparisonDrain.SmallFiles &&
            ComparisonDrainFiles.ShuffledOrder(64).SequenceEqual(ComparisonDrainFiles.ShuffledOrder(64)),
            "payload is deterministic and unique across file offsets; fixed shuffled lanes cover every new file once");
        var payload = new byte[4 << 20]; ComparisonDrainFiles.Fill(payload);
        using (var compressed = new MemoryStream())
        {
            using (var zip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) zip.Write(payload);
            check(compressed.Length > payload.Length * 0.99, "the payload is incompressible across MiB and file boundaries");
        }
        var directory = Directory.CreateTempSubdirectory("qc-drain-contract-").FullName;
        try
        {
            foreach (var small in new[] { false, true })
            {
                var path = Path.Combine(directory, small ? "small" : "large");
                var clockStarted = false;
                var pass = files.Write(path, small, _ => clockStarted = true);
                check(clockStarted && pass.Seconds > 0 && pass.Ended >= pass.Started && files.Verify(path, small).Length == (small ? 64 : 1),
                    "portable file helper uses fresh CreateNew files, Q4 or four shuffled workers, and verifies every hash");
                try { files.Write(path, small, _ => { }); throw new Exception("Scored overwrite accepted."); } catch (IOException) { }
            }
            var largeContent = File.ReadAllBytes(Path.Combine(directory, "large", "large.bin"));
            foreach (var index in new[] { 0, 17, 63 })
                check(largeContent.AsSpan(index * ComparisonDrain.SmallFileBytes, ComparisonDrain.SmallFileBytes)
                    .SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "small", ComparisonDrainFiles.SmallName(index)))),
                    "small files use unique slices of the shared large payload prefix");
            using (var damaged = File.OpenWrite(Path.Combine(directory, "small", ComparisonDrainFiles.SmallName(17)))) damaged.WriteByte(0);
            reject(() => files.Verify(Path.Combine(directory, "small"), true));
            RunReportContracts(directory, plan, check, reject);
            RunReportContracts(directory, paired, check, reject, batching: true);
        }
        finally { Directory.Delete(directory, recursive: true); }

        var restores = 0;
        ComparisonDrainVerification? result = null;
        reject(() => ComparisonDrain.VerifyBypassedAsync("failed-hash", () => Task.CompletedTask,
            () => throw new InvalidDataException("bad hash"), () => { restores++; return Task.CompletedTask; }, value => result = value).GetAwaiter().GetResult());
        check(restores == 1 && result is { Bypassed: true, Verified: false, Restored: true }, "hash failure still restores original active state and preserves failed verification evidence");
        reject(() => ComparisonDrain.VerifyBypassedAsync("failed-pause", () => throw new InvalidDataException("partly failed pause"),
            () => [], () => { restores++; return Task.CompletedTask; }, value => result = value).GetAwaiter().GetResult());
        check(restores == 2 && result is { Bypassed: false, Verified: false, Restored: true }, "a partly failed pause is also restored");
        reject(() => ComparisonDrain.VerifyBypassedAsync("failed-resume", () => Task.CompletedTask,
            () => [], () => throw new InvalidDataException("resume failed"), value => result = value).GetAwaiter().GetResult());
        check(result is { Verified: true, Restored: false }, "restoration failure is persisted and cannot pass verification");
    }

    private static void RunQueueCachePauseContracts(WriteCacheState original, Action<bool, string> check, Action<Action> reject)
    {
        var performance = new CachePerformance(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var active = original with { Performance = performance };
        foreach (var (batch, parallelism) in new[] { (1024, 4), (1024, 8), (2048, 4), (2048, 8) })
            ComparisonDrain.ValidateQueueCache(active with { Options = active.Options! with { BatchKiB = batch, Parallelism = parallelism } }, active: true);
        var paused = active with { Flags = active.Flags & ~1u, Generation = active.Generation + 1 };
        var samples = new Queue<WriteCacheState>([active with { Flags = active.Flags | 8u }, active,
            paused with { Flags = paused.Flags | 8u, DirtyBytes = 4096, InFlightBytes = 4096 }, paused]);
        var observed = new Dictionary<string, WriteCacheState>();
        var disables = 0; var verified = false; var restored = false;
        ComparisonDrainVerification? verification = null;
        ComparisonDrain.VerifyBypassedAsync("pause-transient",
            () => ComparisonDrain.PauseQueueCacheAsync(original, () => samples.Dequeue(), () => disables++,
                (phase, state) => observed.Add(phase, state), TimeSpan.FromSeconds(2)),
            () => { check(samples.Count == 0 && disables == 1 && observed.Last().Value == paused, "bypassed hashes wait for confirmed clean disabled state"); verified = true; return []; },
            () => { restored = true; return Task.CompletedTask; }, value => verification = value).GetAwaiter().GetResult();
        check(verified && restored && verification is { Bypassed: true, Verified: true, Restored: true } &&
            observed.Count == 4 && observed["before-disable-0001"].Draining && observed["after-disable-0001"].Draining &&
            observed.Values.All(s => s.Performance == performance), "only transient barriers settle; every full state is recorded and Disable is never replayed");

        var invalid = new[]
        {
            active with { Instance = original.Instance + 1 }, active with { DeviceBytes = original.DeviceBytes + 4096 },
            active with { Options = null }, active with { Options = original.Options! with { RetainWrites = false } },
            active with { Options = original.Options! with { BatchKiB = 256 } }, active with { Options = original.Options! with { Parallelism = 2 } },
            active with { Options = original.Options! with { BatchKiB = 2048 } }, active with { Options = original.Options! with { Parallelism = 8 } },
            active with { BudgetBytes = 1UL << 30 }, active with { ReservedBytes = 0 }, active with { PayloadCapacity = 0 },
            active with { Flags = active.Flags & ~32u }, active with { Errors = original.Errors + 1 }, active with { LastError = 5 },
            active with { Flags = active.Flags | 2u }, active with { Flags = active.Flags | 4u }, active with { Flags = active.Flags | 16u },
            active with { Performance = null }, active with { Performance = performance with { TimingEnabled = 1 } }
        };
        foreach (var changed in invalid.Append(paused).Append(active with { Flags = active.Flags & ~512u }))
        {
            var reads = 0; disables = 0; observed.Clear();
            reject(() => ComparisonDrain.PauseQueueCacheAsync(original, () => { reads++; return changed with { Flags = changed.Flags | 8u }; },
                () => disables++, (phase, state) => observed.Add(phase, state), TimeSpan.Zero).GetAwaiter().GetResult());
            check(reads == 1 && disables == 0 && observed.Count == 1 && observed.Single().Value.Draining,
                "identity/settings/errors/lifecycle/timing or unexpected disabled state fail before mutation even with Draining set");
        }
        foreach (var changed in invalid.Select(s => s with { Flags = (s.Flags & ~1u) | 8u }).Append(active with { Flags = active.Flags | 8u })
            .Append(paused with { DirtyBytes = 4096 }).Append(paused with { InFlightBytes = 4096 }))
        {
            var reads = 0; disables = 0; observed.Clear(); verified = false; restored = false;
            reject(() => ComparisonDrain.VerifyBypassedAsync("pause-invalid",
                () => ComparisonDrain.PauseQueueCacheAsync(original, () => ++reads == 1 ? active : changed, () => disables++,
                    (phase, state) => observed.Add(phase, state), TimeSpan.Zero),
                () => { verified = true; return []; }, () => { restored = true; return Task.CompletedTask; }, value => verification = value).GetAwaiter().GetResult());
            check(reads == 2 && disables == 1 && observed.Last().Value == changed && !verified && restored &&
                verification is { Bypassed: false, Verified: false, Restored: true },
                "post-Disable mismatch/fault/re-enablement/pending data is recorded and rejected immediately; hashes stay blocked and restoration runs");
        }
        samples = new Queue<WriteCacheState>([active, paused with { Flags = paused.Flags | 8u },
            paused with { Flags = paused.Flags | 8u | 2u, LastError = 5 }, paused]);
        disables = 0; observed.Clear();
        reject(() => ComparisonDrain.PauseQueueCacheAsync(original, () => samples.Dequeue(), () => disables++,
            (phase, state) => observed.Add(phase, state)).GetAwaiter().GetResult());
        check(samples.Count == 1 && disables == 1 && observed.Count == 3 && observed.Last().Value.LastError == 5,
            "a fault arising during a transient barrier is persisted and fails immediately without waiting for a later healthy state");
        foreach (var beforeDisable in new[] { true, false })
        {
            var reads = 0; disables = 0; observed.Clear();
            try
            {
                ComparisonDrain.PauseQueueCacheAsync(original, () => ++reads == 1 && !beforeDisable ? active :
                    beforeDisable ? active with { Flags = active.Flags | 8u } : paused with { Flags = paused.Flags | 8u },
                    () => disables++, (phase, state) => observed.Add(phase, state), TimeSpan.Zero).GetAwaiter().GetResult();
                throw new Exception("Permanent draining was accepted.");
            }
            catch (TimeoutException ex)
            {
                check(reads == (beforeDisable ? 1 : 2) && disables == (beforeDisable ? 0 : 1) && observed.Last().Value.Draining &&
                    ex.Message.Contains("State:") && ex.Message.Contains(beforeDisable ? "before-disable" : "after-disable"),
                    "permanent Draining has a bounded deadline, retained full-state evidence and no control replay");
            }
        }
        var failure = new IOException("Disable failed");
        var failedState = paused with { Flags = paused.Flags | 2u, LastError = 5, Errors = 1 };
        var failureReads = 0; disables = 0; observed.Clear();
        try
        {
            ComparisonDrain.PauseQueueCacheAsync(original, () => ++failureReads == 1 ? active : failedState,
                () => { disables++; throw failure; }, (phase, state) => observed.Add(phase, state)).GetAwaiter().GetResult();
            throw new Exception("Disable failure was hidden.");
        }
        catch (IOException ex)
        {
            check(ReferenceEquals(ex, failure) && disables == 1 && failureReads == 2 && observed["disable-failed"] == failedState,
                "failed Disable retains its exact exception and full failure state without retry");
        }
        Console.WriteLine("QueueCache pause identity, strict failure, transient barrier, deadline and evidence contracts passed (no driver access).");
    }

    private static void RunDeferredBlocksContracts(Action<bool, string> check, Action<Action> reject)
    {
        const string fixture = "Volume #6:\r\n  Total Read            : 0\r\n  Cached Read           : 0 (0,0%)\r\n" +
            "  Deferred Blocks       : 0 (0,0%)\r\n  Trimmed Blocks        : 0\r\n";
        const string row = "  Deferred Blocks       : 0 (0,0%)\r\n";
        check(ComparisonDrain.ParseDeferredBlocks(fixture) == 0, "actual Primo 4.4.1 CRLF perf output accepts its comma-decimal percentage");
        foreach (var (value, expected) in new (string, ulong)[]
        {
            ("0", 0), ("1", 1), ("12345", 12345), ("12,345", 12345), ("12.345", 12345),
            ("12 345", 12345), ("12'345", 12345), ("12\u00a0345", 12345), ("1,234,567", 1234567),
            ("18446744073709551615", ulong.MaxValue), ("18,446,744,073,709,551,615", ulong.MaxValue)
        })
        foreach (var suffix in new[] { "", " (0,0%)", " (12,5%)", " (12.5%)", " (99,99%)", " (99.99%)", " (0%)", " (100%)", " (100,00%)", " (100.00%)" })
            check(ComparisonDrain.ParseDeferredBlocks(fixture.Replace(row, "  Deferred Blocks       : " + value + suffix + "\r\n")) == expected,
                "pending blocks come from the integer, independent of optional display percentage: " + value + suffix);
        check(ComparisonDrain.ParseDeferredBlocks("\tDeferred Blocks\t:\t12,345 (12.5%)\t\n") == 12345,
            "pending block parsing accepts horizontal whitespace and LF as well as CRLF");
        foreach (var value in new[]
        {
            "", "-1", "+1", "0,5", "0.5", "1,23", "12,3456", "1234,567", "1,234.567", "1 234'567",
            "18446744073709551616", "18,446,744,073,709,551,616", "0 KB", "0 blocks", "0 (0,0%) KB"
        })
            reject(() => ComparisonDrain.ParseDeferredBlocks(fixture.Replace(row, "  Deferred Blocks       : " + value + "\r\n")));
        foreach (var suffix in new[]
        {
            "()", "(%)", "(0,0)", "0,0%", "(0,0%%)", "(0,0%) trailing", "(0,0%) (0,0%)", "(0,,0%)", "(0.0.0%)", "(0,0.0%)",
            "(,5%)", "(.5%)", "(1,%)", "(1.%)", "(-1%)", "(+1%)", "(100,1%)", "(100.01%)", "(101%)", "(NaN%)", "(Infinity%)", "(1e1%)",
            "(0,0 %)", "(0,\r\n0%)"
        })
            reject(() => ComparisonDrain.ParseDeferredBlocks(fixture.Replace(row, "  Deferred Blocks       : 1 " + suffix + "\r\n")));
        foreach (var missing in new[] { "", fixture.Replace(row, ""), fixture.Replace("Deferred Blocks", "Deferred Write"),
            fixture.Replace("Deferred Blocks", "Accumulated Deferred Blocks"), fixture.Replace("Deferred Blocks", "Deferred Blocks Total"),
            fixture.Replace(row, "  Deferred Blocks       :\r\n0 (0,0%)\r\n") })
            reject(() => ComparisonDrain.ParseDeferredBlocks(missing));
        foreach (var duplicate in new[] { row, "Deferred Blocks: 1\n", "Deferred Blocks: 1 (50.0%)\r\n",
            "Deferred Blocks: invalid\r\n", "Deferred Blocks:\r\n", "Deferred Blocks 1 (50%)\r\n" })
        {
            reject(() => ComparisonDrain.ParseDeferredBlocks(fixture + duplicate));
            reject(() => ComparisonDrain.ParseDeferredBlocks(duplicate + fixture));
        }
    }

    private static void RunReportContracts(string directory, IReadOnlyList<ComparisonCase> plan, Action<bool, string> check, Action<Action> reject, bool batching = false)
    {
        IReadOnlyList<ComparisonReport.DrainRow> Rows(IReadOnlyList<ComparisonCase> cases, ComparisonProvider product)
            => ComparisonReport.Drains(directory, cases, product, batching);
        var hash = new string('A', 64);
        var small = Enumerable.Range(0, ComparisonDrain.SmallFiles).Select(i => new ComparisonFileHash(ComparisonDrainFiles.SmallName(i), ComparisonDrain.SmallFileBytes, hash)).ToArray();
        var oraclePath = Path.Combine(directory, "compare-drain-oracle.json");
        RunStorage.AtomicJson(oraclePath, new { ComparisonDrain.PayloadBytes, ComparisonDrain.LargeBytes, ComparisonDrain.SmallBytes,
            SmallFileCount = ComparisonDrain.SmallFiles, ComparisonDrain.Seed, LargeSha256 = hash, SmallFiles = small });
        var utc = DateTimeOffset.UtcNow;
        var provider = ComparisonProvider.QueueCache;
        ComparisonDrainScore Score(ComparisonCase test)
        {
            var seconds = 8.0 + test.Repeat * 2;
            var expectedBytes = ComparisonDrain.ExpectedBytes(test.Kind);
            var operations = (uint)(expectedBytes / (1 << 20));
            var before = new ComparisonPending(utc.AddSeconds(-1), provider, 0, 0, null);
            var after = before with { Utc = utc.AddSeconds(seconds - 0.2) };
            var diskBefore = new ComparisonDiskCounters(utc.AddSeconds(-1), 0, 0, 0);
            var diskAfter = new ComparisonDiskCounters(utc.AddSeconds(seconds - 0.1), expectedBytes, operations, 0);
            var samples = Enumerable.Range(0, (int)seconds + 3).Select(i => new ComparisonDiskCounters(utc.AddSeconds(i - 1),
                Math.Min(i, (int)seconds) * (long)expectedBytes / (int)seconds, (uint)(Math.Min(i, (int)seconds) * operations / (int)seconds), 0)).ToArray();
            return new(test.Id, provider, expectedBytes, test.Kind == "drain-small" ? ComparisonDrain.SmallFiles : 1,
                utc.AddSeconds(-1), utc, utc.AddSeconds(1), utc.AddSeconds(1.1), utc.AddSeconds(seconds - 0.5), utc.AddSeconds(seconds),
                1, seconds - 1, seconds - 1.6, 0.5, seconds, expectedBytes / seconds / 1_000_000.0,
                before, after, diskBefore, diskAfter, samples);
        }
        foreach (var test in plan)
        {
            var score = Score(test);
            ComparisonDrain.ValidateScore(test, score, provider);
            if (batching)
            {
                var counters = new CachePerformance(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
                {
                    DrainGatherPolicy = (ulong)test.GatherPolicy!.Value,
                    DrainEndLimit = 0, DrainEndGap = 0, DrainEndInFlight = 0, DrainEndFilling = 0, DrainEndPartial = 0, DrainEndFence = 0,
                    DrainPartialAnchor = 0, DrainSizeSmall = 0, DrainSizeMedium = 0, DrainSizeLarge = 0,
                    DrainGapCleanFull = 0, DrainGapCleanPartial = 0, DrainGapAbsent = 0, DrainGapFilling = 0,
                    DrainBackLimit = 0, DrainBackGap = 0, DrainBackInFlight = 0, DrainBackFilling = 0,
                    DrainBackPartial = 0, DrainBackFence = 0, DrainBackBoundary = 0, DrainBackAligned = 0
                };
                var state = new WriteCacheState(1 | 32 | 256 | 512, 0, 50UL << 30, 2UL << 30, 2UL << 30, 0, 0, 2UL << 30,
                    0, 0, 0, 0, 0, 0, 0, 0) { Instance = 42, Options = new CacheOptions { BatchKiB = 2048, Parallelism = 4 }, Performance = counters };
                var finished = counters with { DrainBatches = 2, DrainBytes = (ulong)test.Bytes, DrainEndGap = 2,
                    DrainSizeSmall = 2, DrainGapAbsent = 2, DrainBackGap = 2 };
                var evidence = DrainBatching.Measure(test, counters, finished);
                check(evidence.Gaps["absent"] == 2, "missing-neighbor attribution reconciles with forward and backward batch counts");
                reject(() => DrainBatching.Measure(test, counters, finished with { DrainGapAbsent = null }));
                reject(() => DrainBatching.Measure(test, counters, finished with { DrainGapAbsent = 3 }));
                reject(() => DrainBatching.Measure(test, counters, finished with { DrainBackGap = 1 }));
                reject(() => DrainBatching.Measure(test, counters, finished with { DrainGatherPolicy = null }));
                reject(() => DrainBatching.Measure(test, counters, finished with { TimingEnabled = 1 }));
                RunStorage.AtomicJson(Path.Combine(directory, $"compare.{test.Id}.before.cache.json"), state);
                RunStorage.AtomicJson(Path.Combine(directory, $"compare.{test.Id}.after.cache.json"), state with { Performance = finished });
                RunStorage.AtomicJson(Path.Combine(directory, $"compare.{test.Id}.batching.json"), evidence);
            }
            RunStorage.AtomicJson(Path.Combine(directory, $"compare.{test.Id}.score.json"), score);
            RunStorage.AtomicJson(Path.Combine(directory, $"compare.{test.Id}.verify.json"), new ComparisonDrainVerification(test.Id, true, true, true,
                test.Kind == "drain-small" ? small : [new("large.bin", ComparisonDrain.LargeBytes, hash)]));
        }
        var rows = Rows(plan, provider);
        check(rows.Count == (batching ? 4 : 2) && rows.All(r => r.Samples == 3 && r.MBPerSecond == (r.Shape.EndsWith("small", StringComparison.Ordinal) ? ComparisonDrain.SmallBytes : ComparisonDrain.LargeBytes) / 12.0 / 1_000_000 &&
            r.MinMBPerSecond < r.MBPerSecond && r.MaxMBPerSecond > r.MBPerSecond && r.AverageWriteBytes == 1 << 20),
            "two headline medians use each workload's own byte count, complete three-sample ranges and backing write sizes");
        reject(() => Rows(plan.Take(5).ToArray(), provider));
        reject(() => Rows(plan, ComparisonProvider.PrimoCache));
        reject(() => Rows(plan.Select(c => c.Kind == "drain-small" ? c with { Bytes = 1L << 30 } : c).ToArray(), provider));
        if (batching) reject(() => ComparisonReport.Drains(directory, ComparisonDrain.Cases(3), provider, batching: true));
        var originalOracle = File.ReadAllText(oraclePath);
        var oldOracle = JsonNode.Parse(originalOracle)!.AsObject(); oldOracle["SmallBytes"] = 1L << 30; oldOracle["SmallFileCount"] = 16_384;
        File.WriteAllText(oraclePath, oldOracle.ToJsonString());
        reject(() => Rows(plan, provider));
        File.WriteAllText(oraclePath, originalOracle);
        var smallCase = plan.First(c => c.Kind == "drain-small");
        var smallScore = Score(smallCase);
        reject(() => ComparisonDrain.ValidateScore(smallCase, smallScore with { Bytes = ComparisonDrain.LargeBytes,
            MBPerSecond = ComparisonDrain.LargeBytes / smallScore.TotalSeconds / 1_000_000.0 }, provider));
        reject(() => ComparisonDrain.ValidateScore(smallCase, smallScore with { Files = 16_384 }, provider));
        reject(() => ComparisonDrain.ValidateScore(smallCase, smallScore with { DiskAfter = smallScore.DiskAfter with { BytesWritten = ComparisonDrain.SmallBytes - 4096 } }, provider));
        var first = plan[0]; var good = Score(first);
        var contradictory = new[]
        {
            good with { DiskSamples = good.DiskSamples.Select(s => s with { BytesWritten = 0, WriteOperations = 0 }).ToArray() },
            good with { DiskSamples = good.DiskSamples.Select(s => s with { WriteOperations = 0 }).ToArray() },
            good with { DiskAfter = good.DiskAfter with { BytesWritten = good.DiskAfter.BytesWritten + 4096 } },
            good with { DiskAfter = good.DiskAfter with { WriteOperations = good.DiskAfter.WriteOperations + 1 } }
        };
        foreach (var bad in contradictory) reject(() => ComparisonDrain.ValidateScore(first, bad, provider));
        var shifted = good with { DiskBefore = good.DiskBefore with { BytesWritten = 4096, WriteOperations = 17 },
            DiskAfter = good.DiskAfter with { BytesWritten = good.DiskAfter.BytesWritten + 4096, WriteOperations = good.DiskAfter.WriteOperations + 17 },
            DiskSamples = good.DiskSamples.Select(s => s with { BytesWritten = s.BytesWritten + 4096, WriteOperations = s.WriteOperations + 17 }).ToArray() };
        ComparisonDrain.ValidateScore(first, shifted, provider);
        // Baseline/series contradiction with an otherwise whole-GiB boundary delta.
        var badBaseline = good with { DiskBefore = good.DiskBefore with { Utc = utc.AddMilliseconds(-1), BytesWritten = ComparisonDrain.LargeBytes },
            DiskAfter = good.DiskAfter with { BytesWritten = 2L * ComparisonDrain.LargeBytes } };
        reject(() => ComparisonDrain.ValidateScore(first, badBaseline, provider));
        foreach (var bad in new[] { good with { After = good.After with { DirtyBytes = 4096 } }, good with { After = good.After with { InFlightBytes = null } },
            good with { DiskAfter = good.DiskAfter with { QueueDepth = 1 } }, good with { DiskAfter = good.DiskAfter with { WriteOperations = 0 } },
            good with { DiskAfter = good.DiskAfter with { BytesWritten = 0 } }, good with { DiskSamples = good.DiskSamples.Take(2).ToArray() },
            good with { DiskSamples = good.DiskSamples.Where((_, i) => i is < 2 or > 5).ToArray() }, good with { MBPerSecond = double.NaN },
            good with { Bytes = 4096 }, good with { Id = "another-case" } })
            reject(() => ComparisonDrain.ValidateScore(first, bad, provider));
        var scorePath = Path.Combine(directory, $"compare.{first.Id}.score.json");
        foreach (var bad in contradictory.Append(badBaseline))
        {
            RunStorage.AtomicJson(scorePath, bad);
            reject(() => Rows(plan, provider));
        }
        RunStorage.AtomicJson(scorePath, good);
        var json = JsonNode.Parse(File.ReadAllText(scorePath))!.AsObject(); json.Remove("TotalSeconds");
        File.WriteAllText(scorePath, json.ToJsonString());
        try { Rows(plan, provider); throw new Exception("A missing score field became zero."); } catch (JsonException) { }
        RunStorage.AtomicJson(scorePath, good);
        var verifyPath = Path.Combine(directory, $"compare.{first.Id}.verify.json");
        RunStorage.AtomicJson(verifyPath, new ComparisonDrainVerification(first.Id, true, false, true, [new("large.bin", ComparisonDrain.LargeBytes, hash)]));
        reject(() => Rows(plan, provider));
        File.Delete(verifyPath);
        try { Rows(plan, provider); throw new Exception("A missing verification was published."); } catch (IOException) { }
        check(File.Exists(scorePath), "completed measurement is preserved independently of missing or failed byte verification");
    }
}
