using System.Text.Json;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

public sealed partial class VerificationRunner
{
    // The ordering performance suites: the cost screen's worker allows two hours, the
    // sustained runs (five-minute file runs and their write-out) three.
    private async Task<IReadOnlyList<CheckResult>> MeasureFlushOrder(string operation, CancellationToken token)
    {
        var seconds = operation is "flush-order-sustained" or "flush-order-fua-sustained" ? 10800 : 7200;
        var prefix = storage.PathFor(operation);
        var stop = prefix + ".stop"; var ready = prefix + ".ready.json";
        using var children = CancellationTokenSource.CreateLinkedTokenSource(token);
        var telemetry = Worker(Job("telemetry") with
        {
            Reply = prefix + ".telemetry.jsonl", StopFile = stop, ReadyFile = ready, Seconds = seconds + 60
        }, children.Token, seconds + 70);
        try
        {
            await TelemetryCoverage.WaitReadyAsync(ready, telemetry, TimeSpan.FromSeconds(45), token);
            var start = DateTimeOffset.UtcNow;
            var reply = await Worker(Job(operation) with
            {
                Reply = prefix + ".reply.json", WorkDirectory = workDirectory,
                BudgetMiB = options.BudgetMiB, ReferenceRepeats = options.Repeats,
                ProcessEvidence = prefix + ".process.json"
            }, token, seconds);
            var end = DateTimeOffset.UtcNow;
            File.WriteAllText(stop, "stop");
            await telemetry;
            var stamps = new List<DateTimeOffset>();
            foreach (var line in File.ReadLines(prefix + ".telemetry.jsonl"))
            {
                var sample = JsonSerializer.Deserialize<TelemetrySnapshot>(line)
                    ?? throw new InvalidDataException("Missing ordering telemetry sample.");
                stamps.Add(sample.Utc);
                if (sample.State.Errors != original.State.Errors || sample.State.LastError != 0 ||
                    sample.State.Instance != original.State.Instance)
                    throw new IOException("Driver error or instance change during an ordering performance run.");
            }
            TelemetryCoverage.Validate(stamps, start, end);
            RunStorage.AtomicJson(prefix + ".coverage.json", new { Start = start, End = end, MaximumSampleGapSeconds = 2 });
            var checks = JsonSerializer.Deserialize<CheckResult[]>(File.ReadAllText(reply))
                ?? throw new InvalidDataException("Missing ordering performance checks.");
            if (operation is "flush-order-sustained" or "flush-order-fua-sustained")
                FlushOrderSustainedPlan.ValidateChecks(FlushOrderSustainedPlan.Cases(options.Repeats, operation == "flush-order-fua-sustained"), checks);
            else
                FlushOrderCostPlan.ValidateChecks(FlushOrderCostPlan.Cases(options.Repeats, operation == "flush-order-fua-cost"), checks);
            return checks;
        }
        finally
        {
            File.WriteAllText(stop, "stop");
            children.Cancel();
            try { await telemetry; } catch (Exception) { /* Primary failure and raw trace are retained. */ }
        }
    }
}
