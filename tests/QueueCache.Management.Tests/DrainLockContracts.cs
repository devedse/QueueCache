using System.Text.Json;
using QueueCache.Developer.Verification;
using QueueCache.Management;

internal static class DrainLockContracts
{
    public static void Run(Action<bool, string> check, Action<Action> reject)
    {
        var options = new VerificationOptions("Q:", DrainLockDiagnostic.Suite, DiskSpd: Environment.ProcessPath,
            BudgetMiB: 2048, Repeats: 3, DurationSeconds: 6);
        VerificationPlan.Validate(options);
        foreach (var invalid in new[] { options with { BudgetMiB = 1024 }, options with { Repeats = 1 },
            options with { DurationSeconds = 10 }, options with { CaseFilter = "alone" } })
        {
            try { VerificationPlan.Validate(invalid); throw new Exception("Accepted invalid lock diagnostic options."); }
            catch (ArgumentException) { }
        }
        var cases = VerificationPlan.Performance(options);
        check(cases.Count == 48 && cases.Select(c => c.Id).Distinct().Count() == 48 &&
            cases.GroupBy(c => (c.Resident, c.Parallelism, c.Writer, c.Timing)).All(g => g.Count() == 3) &&
            cases.All(c => c.BatchKiB == 2048 && c.Parallelism is 4 or 8 && !c.ApplicationFlush && c.DelayMs == 0),
            "lock diagnostic has complete balanced immutable short windows");
        var state = new WriteCacheState(1 | 32 | 256 | 512, 0, 200UL << 30, 2UL << 30, 2UL << 30, 0, 0,
            2UL << 30, 0, 0, 0, 0, 0, 0, 0, 0) { Instance = 1, Generation = 2 };
        var perf = new CachePerformance(1_000_000, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var diag = new CacheDiagnostics(0, 0, 0, 0, 0, 0, 0, 0, 0) { CopyFlags = 15,
            Attribution = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0) };
        DrainLockWindow Window(PerformanceCase c)
        {
            var a = new CacheLayoutSnapshot(state with { Options = DrainLockDiagnostic.Options(c) },
                perf with { TimingEnabled = c.Timing ? 1UL : 0UL }, diag);
            long bytes = c.Resident ? 100L << 20 : 100L * 4096;
            var b = a with
            {
                State = a.State with { ReadHitBytes = c.Resident ? (ulong)bytes : 0, ReadMissBytes = c.Resident ? 0 : (ulong)bytes,
                    DrainedBytes = c.Writer ? 64UL << 20 : 0 },
                Performance = a.Performance with { LockAcquires = c.Timing ? 100UL : 0,
                    LockWaitTicks = c.Timing ? 250000UL : 0, LockHoldTicks = c.Timing ? 120000UL : 0 },
                Diagnostics = diag with { Attribution = diag.Attribution! with
                    { LowerReadAttempts = c.Resident ? 0UL : 100, LowerWriteAttempts = c.Writer ? 32UL : 0 } }
            };
            var at = DateTimeOffset.UnixEpoch;
            var reader = new DiskSpdScore(bytes, 100, 6, bytes / 6.0 / (1 << 20), 100 / 6.0, 1, null, 2, 3);
            return new(c, a, b, at, at.AddSeconds(6), reader, c.Writer ? reader : null);
        }
        var windows = cases.Select(Window).ToArray();
        DrainLockDiagnostic.RequireComplete(cases, windows);
        reject(() => DrainLockDiagnostic.RequireComplete(cases, windows[..^1]));
        reject(() => DrainLockDiagnostic.RequireComplete(cases, [.. windows[..^1], windows[0]]));
        var timed = windows.First(w => w.Case.Timing && w.Case.Resident && w.Case.Writer);
        using var metrics = JsonDocument.Parse(JsonSerializer.Serialize(DrainLockDiagnostic.Metrics(timed)));
        check(metrics.RootElement.GetProperty("GlobalLockBusyPercent").GetDouble() == 2 &&
            metrics.RootElement.GetProperty("GlobalLockWaitMilliseconds").GetDouble() == 250,
            "lock occupancy uses global held time over the declared envelope; waiting is summed, not reader-only latency");
        using var untimed = JsonDocument.Parse(JsonSerializer.Serialize(DrainLockDiagnostic.Metrics(windows.First(w => !w.Case.Timing))));
        check(untimed.RootElement.GetProperty("GlobalLockWaitMilliseconds").ValueKind == JsonValueKind.Null,
            "timing-off lock measurements are unavailable, never zero");
        reject(() => DrainLockDiagnostic.Validate(timed with { After = timed.After with { State = timed.After.State with { ReadMissBytes = 4096 } } }));
        reject(() => DrainLockDiagnostic.Validate(timed with { After = timed.After with { State = timed.After.State with { DrainedBytes = 0 } } }));
        reject(() => DrainLockDiagnostic.Validate(timed with { After = timed.After with { Performance = timed.After.Performance with { TimingEnabled = 0 } } }));
        reject(() => DrainLockDiagnostic.Validate(timed with { After = timed.After with { Performance = timed.After.Performance with { LockAcquires = 0 } } }));
        reject(() => DrainLockDiagnostic.Validate(timed with { Reader = timed.Reader with { Operations = 0, Bytes = 0 } }));
        reject(() => DrainLockDiagnostic.Validate(timed with { After = timed.After with { Diagnostics = diag with { Attribution = null } } }));
        var cold = windows.First(w => !w.Case.Resident);
        check(cold.Before.State.Options is { Allocation: CacheAllocation.Fixed, WritePercent: 100 },
            "cold-read diagnostic disables miss admission with a zero read partition, not merely disabled promotion");
        reject(() => DrainLockDiagnostic.Validate(cold with { Before = cold.Before with
            { State = cold.Before.State with { Options = cold.Before.State.Options! with { Allocation = CacheAllocation.Automatic, WritePercent = 50 } } } }));
        reject(() => DrainLockDiagnostic.Validate(cold with { After = cold.After with { State = cold.After.State with { ReadHitBytes = 4096 } } }));
        // Plan 159: background writes in a standalone window are a distinct, retryable failure, never a score.
        Exception? Thrown(Action action) { try { action(); return null; } catch (Exception ex) { return ex; } }
        var alone = windows.First(w => !w.Case.Writer && !w.Case.Resident);
        var noisy = alone with { After = alone.After with { State = alone.After.State with { DrainedBytes = 4096 },
            Diagnostics = alone.After.Diagnostics with { Attribution = alone.After.Diagnostics.Attribution! with { LowerWriteAttempts = 1 } } } };
        check(Thrown(() => DrainLockDiagnostic.Validate(noisy)) is StandaloneNoiseException { LowerWrites: 1, DrainedBytes: 4096 } &&
            DrainLockDiagnostic.StandaloneAttempts == 3,
            "a standalone window with one background 4 KiB write is a recorded, retryable noise failure (three attempts)");
        check(Thrown(() => DrainLockDiagnostic.Validate(timed with { After = timed.After with { State = timed.After.State with { DrainedBytes = 0 } } }))
                is InvalidDataException &&
            Thrown(() => DrainLockDiagnostic.Validate(alone with { Writer = alone.Reader })) is InvalidDataException,
            "missing writer drain and a standalone writer score stay hard failures, never retried as noise");
        reject(() => DrainLockDiagnostic.RequireComplete(cases, windows.Select(w => w == alone ? noisy : w).ToArray()));
        Console.WriteLine("Drain lock diagnostic matrix, counter scope, path purity, missing evidence and timing contracts passed (no driver access).");
    }
}
