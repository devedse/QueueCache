using QueueCache.Developer.Verification;

internal static class TelemetryRecorderTests
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var entered = Signal();
        var release = Signal();
        var captured = Signal();
        var values = new List<int>();
        var count = 0;
        var recording = TelemetryRecorder.RecordAsync(_ =>
        {
            var value = ++count;
            if (value == 2) entered.Task.GetAwaiter().GetResult();
            if (value == 5) captured.SetResult();
            return new TelemetryCapture<int>(value, value == 5);
        }, async (value, token) =>
        {
            if (value == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
            values.Add(value);
        }, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(1), capacity: 16);
        await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!recording.IsCompleted && values.Count == 0,
            "Actual observations continue while the first persistence is blocked; completion still waits for persistence.");
        release.SetResult();
        await recording.WaitAsync(TimeSpan.FromSeconds(5));
        Check(values.SequenceEqual([1, 2, 3, 4, 5]), "Every collected point is persisted once in its original order.");

        // A real synchronous filesystem poll must not trap sampling either.
        // Start the poll only after RecordAsync has returned to its caller.
        entered = Signal(); release = Signal(); captured = Signal();
        values = []; count = 0;
        var stop = new TelemetryStopSignal(() =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return true;
        });
        var polling = TelemetryRecorder.RecordAsync(_ =>
        {
            var stopping = stop.Requested;
            var value = ++count;
            if (value == 2) entered.Task.GetAwaiter().GetResult();
            if (value == 5) captured.TrySetResult();
            return new TelemetryCapture<int>(value, stopping);
        }, async (value, _) =>
        {
            await Task.Yield();
            values.Add(value);
            stop.Poll();
        }, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(5));
        try
        {
            await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!stop.Requested && !polling.IsCompleted && count >= 5,
                "A blocked synchronous stop-file poll does not block real sampling or infer stop completion.");
        }
        finally { release.TrySetResult(); }
        await polling.WaitAsync(TimeSpan.FromSeconds(5));
        Check(stop.Requested && stop.LastPollMilliseconds is >= 0 && values.Count >= 6 &&
              values.SequenceEqual(Enumerable.Range(1, count)),
            "Stop polling retains every actual sample and a trailing observation after the stop signal.");

        entered = Signal();
        var never = Signal();
        count = 0;
        var overflow = TelemetryRecorder.RecordAsync(_ =>
        {
            var value = ++count;
            if (value == 2) entered.Task.GetAwaiter().GetResult();
            return new TelemetryCapture<int>(value, false);
        }, async (_, token) =>
        {
            entered.TrySetResult();
            await never.Task.WaitAsync(token);
        }, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(1), capacity: 1);
        try { await overflow.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Buffer exhaustion was accepted."); }
        catch (IOException error)
        {
            Check(error.Message.Contains("bounded sample buffer", StringComparison.Ordinal) && count == 3,
                "Capacity exhaustion fails and cancels the blocked writer, rather than dropping or synthesizing samples.");
        }

        entered = Signal();
        count = 0;
        var captureFailure = new IOException("observer failed");
        var failingCapture = TelemetryRecorder.RecordAsync<int>(_ =>
        {
            if (++count == 2)
            {
                entered.Task.GetAwaiter().GetResult();
                throw captureFailure;
            }
            return new(count, false);
        }, async (_, token) =>
        {
            entered.TrySetResult();
            await never.Task.WaitAsync(token);
        }, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(1));
        try { await failingCapture.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Observer failure was accepted."); }
        catch (IOException error) { Check(ReferenceEquals(error, captureFailure), "Observer failure retains its cause and stops the blocked persister."); }

        var persistenceFailure = new IOException("evidence write failed");
        var failingWriter = TelemetryRecorder.RecordAsync(_ => new TelemetryCapture<int>(1, false),
            (_, _) => Task.FromException(persistenceFailure), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(1));
        try { await failingWriter.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Persistence failure was accepted."); }
        catch (IOException error) { Check(ReferenceEquals(error, persistenceFailure), "Persistence failure stops and joins the sampler while retaining its cause."); }
        Console.WriteLine("Telemetry sampling/persistence/ownership contracts passed (no driver access).");
    }
}
