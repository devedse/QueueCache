using System.Diagnostics;
using Microsoft.Windows.EventTracing;
using QueueCache.Developer.Verification;

internal static class TraceToolSmoke
{
    // Opt-in installed-tool smoke: no driver access, fixture or benchmark. Retain exact evidence.
    public static void RunOptional()
    {
        if (Environment.GetEnvironmentVariable("QCACHE_TEST_WPR") != "1") return;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WPR smoke requires elevated Windows.");
        var parent = Environment.GetEnvironmentVariable("QCACHE_TEST_WPR_OUTPUT")
            ?? throw new ArgumentException("Set QCACHE_TEST_WPR_OUTPUT to an existing evidence directory.");
        if (!Path.IsPathFullyQualified(parent) || !Directory.Exists(parent)) throw new ArgumentException("WPR smoke output parent must exist and be absolute.");
        var root = Directory.CreateDirectory(Path.Combine(parent, "Wpr-Smoke-" + Guid.NewGuid().ToString("N"))).FullName;
        var deep = Directory.CreateDirectory(Path.Combine(root, new string('a', 90), new string('b', 90), new string('c', 90))).FullName;
        var journal = Path.Combine(deep, "owned.trace.json");
        try
        {
            try
            {
                VerificationTraceSession.StartAsync(journal).GetAwaiter().GetResult();
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed < TimeSpan.FromMilliseconds(100)) Thread.SpinWait(1000);
                Thread.Sleep(2000);
            }
            finally { VerificationTraceSession.CleanupAsync(journal).GetAwaiter().GetResult(); }
            using (var trace = TraceProcessor.Create(journal + ".etl", new TraceProcessorSettings { AllowLostEvents = false }))
            {
                var cpu = trace.UseCpuSamplingData(); var scheduling = trace.UseCpuSchedulingData();
                trace.Process();
                var own = cpu.Result.Samples.Count(s => s.Process?.Id == Environment.ProcessId);
                var switches = scheduling.Result.ThreadActivity.Count(a => a.Thread?.Process?.Id == Environment.ProcessId);
                if (own == 0 || switches == 0) throw new InvalidDataException("Installed trace tools lack owned-process sampling/scheduler data.");
                RunStorage.AtomicJson(Path.Combine(root, "tool-smoke.json"), new { CpuSamples = own, Activities = switches,
                    Scope = "Owned test process, not a driver performance measurement", Evidence = deep });
            }
            File.WriteAllText(Path.Combine(root, "FINISHED.txt"), "PASS installed trace toolchain; no benchmark or driver access.");
            Console.WriteLine("Installed WPR/TraceProcessor deep-path smoke passed: " + root);
        }
        catch (Exception ex) { RunStorage.AtomicJson(Path.Combine(root, "failure.json"), new { Error = ex.ToString() }); throw; }
    }
}
