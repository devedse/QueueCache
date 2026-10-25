using System.Text.Json;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

public sealed partial class VerificationRunner
{
    private async Task<IReadOnlyList<CheckResult>> MeasureFlushOrderCost(CancellationToken token)
    {
        var prefix = storage.PathFor("flush-order-cost");
        var stop = prefix + ".stop"; var ready = prefix + ".ready.json";
        using var children = CancellationTokenSource.CreateLinkedTokenSource(token);
        var telemetry = Worker(Job("telemetry") with
        {
            Reply = prefix + ".telemetry.jsonl", StopFile = stop, ReadyFile = ready, Seconds = 960
        }, children.Token, 970);
        try
        {
            await TelemetryCoverage.WaitReadyAsync(ready, telemetry, TimeSpan.FromSeconds(45), token);
            var start = DateTimeOffset.UtcNow;
            var reply = await Worker(Job("flush-order-cost") with
            {
                Reply = prefix + ".reply.json", WorkDirectory = workDirectory,
                BudgetMiB = options.BudgetMiB, ReferenceRepeats = options.Repeats,
                ProcessEvidence = prefix + ".process.json"
            }, token, 900);
            var end = DateTimeOffset.UtcNow;
            File.WriteAllText(stop, "stop");
            await telemetry;
            var stamps = new List<DateTimeOffset>();
            foreach (var line in File.ReadLines(prefix + ".telemetry.jsonl"))
            {
                var sample = JsonSerializer.Deserialize<TelemetrySnapshot>(line)
                    ?? throw new InvalidDataException("Missing ordering cost telemetry sample.");
                stamps.Add(sample.Utc);
                if (sample.State.Errors != original.State.Errors || sample.State.LastError != 0 ||
                    sample.State.Instance != original.State.Instance)
                    throw new IOException("Driver error or instance change during ordering cost screen.");
            }
            TelemetryCoverage.Validate(stamps, start, end);
            RunStorage.AtomicJson(prefix + ".coverage.json", new { Start = start, End = end, MaximumSampleGapSeconds = 2 });
            var checks = JsonSerializer.Deserialize<CheckResult[]>(File.ReadAllText(reply))
                ?? throw new InvalidDataException("Missing ordering cost checks.");
            FlushOrderCostPlan.ValidateChecks(FlushOrderCostPlan.Cases(options.Repeats), checks);
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
