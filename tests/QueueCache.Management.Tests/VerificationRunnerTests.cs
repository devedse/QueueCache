using System.Reflection;
using System.Text.Json;
using QueueCache.Developer.Verification;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class VerificationRunnerTests
{
    public static async Task RunAsync()
    {
        void Check(bool value, string name) { if (!value) throw new Exception("Verification runner: " + name); }
        void Reject(Action action)
        {
            try { action(); } catch (Exception e) when (e is ArgumentException or InvalidDataException or System.Xml.XmlException) { return; }
            throw new Exception("Expected rejection.");
        }
        var options = new VerificationOptions("Q:", "performance");
        var plan = VerificationPlan.Performance(options);
        Check(plan.Count == 204 && plan.Select(c => c.Id).Distinct().Count() == plan.Count, "unique repeated-case identities");
        var focused = VerificationPlan.Performance(options with { Suite = "flush-interference", Repeats = 2 });
        Check(focused.Count == 8 && focused.Count(c => c.ApplicationFlush) == 4, "focused eight-case contract");
        Check(VerificationPlan.Performance(options with { Suite = "full" }).Count == 216, "full performance + focused scope");
        Check(!RunStorage.Complete(["a", "b"], [new("a", "PASS", "", DateTimeOffset.UtcNow, 0)]), "incomplete rejected");
        Check(!RunStorage.Complete(["a"], [new("a", "FAIL", "", DateTimeOffset.UtcNow, 0)]), "failed rejected");
        Check(RunStorage.Complete(["a"], [new("a", "MEASURED", "", DateTimeOffset.UtcNow, 0)]), "measurement not correctness pass");
        Reject(() => VerificationPlan.Validate(options with { Suite = "typo" }));
        Reject(() => VerificationPlan.Validate(options with { Suite = "quick", Repeats = 0 }));
        try { VerificationPlan.Validate(options); throw new Exception("Missing tool was accepted."); }
        catch (ArgumentException ex) { Check(ex.Message.Contains("requires --diskspd") && ex.Message.Contains(VerificationPlan.DiskSpdDownload), "missing option guidance"); }
        var missingTool = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "diskspd.exe");
        try { VerificationPlan.Validate(options with { DiskSpd = missingTool }); throw new Exception("Missing file was accepted."); }
        catch (FileNotFoundException ex) { Check(ex.Message.Contains(missingTool), "missing executable prints resolved path"); }
        try { VerificationPlan.Validate(options with { DiskSpd = Path.GetTempPath() }); throw new Exception("Directory was accepted."); }
        catch (ArgumentException ex) { Check(ex.Message.Contains("directory, not an executable"), "directory distinguished from file"); }
        VerificationPlan.Validate(options with { Suite = "quick" });
        VerificationPlan.Validate(options with { Suite = "policies" });
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
            store.Write("state.json", new { Status = "RUNNING" });
            store.Write("state.json", new { Status = "COMPLETED" });
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
            OwnedProcess.EnsureStopped(store.DirectoryPath);
            foreach (var mode in new[] { "success", "check-failure", "restore-failure", "cancel" })
            {
                var runParent = store.PathFor(mode);
                var runner = new VerificationRunner(executable, [.. prefix, "--fake-verification", mode], store.PathFor("leases"));
                using var cancellation = new CancellationTokenSource();
                var progress = new Progress<string>(message =>
                {
                    if (mode == "cancel" && message == "Starting file-integrity") cancellation.CancelAfter(300);
                });
                var exit = await runner.RunAsync(new("Q:", Output: runParent), progress, cancellation.Token);
                var directory = Directory.GetDirectories(runParent).Single();
                var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "status.json")));
                var expectedStatus = mode switch { "success" => "COMPLETED", "check-failure" => "INCOMPLETE", "restore-failure" => "RESTORATION_FAILED", _ => "CANCELLED" };
                Check(state.RootElement.GetProperty("Status").GetString() == expectedStatus, "coordinator " + mode);
                Check((exit == 0) == (mode == "success") && File.Exists(Path.Combine(directory, "FINISHED.txt")), "exit/marker " + mode);
                Check(File.Exists(Path.Combine(directory, "restored.json")) == (mode != "restore-failure"), "independent restoration " + mode);
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
        if (mode == "cancel" && job.Operation == "files") await Task.Delay(Timeout.Infinite);
        if (mode == "check-failure" && job.Operation == "files" || mode == "restore-failure" && job.Operation == "restore") return 1;
        object reply = new { Fake = true };
        if (job.Operation == "capture")
            reply = new RecoverySnapshot(1, new QueueCache.Operations.DiskTarget('Q', 99999, 50L << 30, "fixture-only"),
                new QueueCache.Management.WriteCacheState(0, 0, 50UL << 30, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
                false, "[]", DateTimeOffset.UtcNow, Environment.MachineName);
        RunStorage.AtomicJson(job.Reply, reply);
        return 0;
    }
}
