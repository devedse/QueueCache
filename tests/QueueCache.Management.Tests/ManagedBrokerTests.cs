using System.Buffers.Binary;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;

internal static class ManagedBrokerTests
{
    public static async Task RunAsync()
    {
        // SERVICE_CONTROL_PRESHUTDOWN is 15; 16 is TIMECHANGE. Preshutdown must stop the broker and run the
        // shutdown saves, or every Windows shutdown waits for it (event 7043).
        Check(WindowsManagedBrokerService.Stops(WindowsManagedBrokerService.ControlPreshutdown) && WindowsManagedBrokerService.ControlPreshutdown == 15 &&
            WindowsManagedBrokerService.Stops(5) && WindowsManagedBrokerService.Stops(1) && !WindowsManagedBrokerService.Stops(16) && !WindowsManagedBrokerService.Stops(4),
            "stop, shutdown and preshutdown stop the broker; time changes and interrogation do not");
        Check(WindowsManagedBrokerService.SavesBeforeStopping(15) && WindowsManagedBrokerService.SavesBeforeStopping(5) &&
            !WindowsManagedBrokerService.SavesBeforeStopping(1) && !WindowsManagedBrokerService.SavesBeforeStopping(16),
            "shutdown saves run on preshutdown and shutdown, not on an ordinary stop");
        var unprotected = ManagedDiskHostProtection.UnprotectedMessage(@"C:\QCTests", false);
        Check(unprotected.Contains(@"icacls ""C:\QCTests"" /inheritance:r /grant:r ""*S-1-5-32-544:(OI)(CI)F"" ""*S-1-5-18:(OI)(CI)F""") &&
            !ManagedDiskHostProtection.UnprotectedMessage(@"D:\", true).Contains("icacls"),
            "a refused folder's message gives the fix, but never suggests re-permissioning a drive root");
        // #12: what the volume list says about this startup's restore of a saved profile.
        var until = new DateTime(2026, 10, 23, 12, 30, 0, DateTimeKind.Utc);
        var waiting = new ProfileRestoreStatus("Q:", "{00000000-0000-0000-0000-000000000002}", ProfileRestoreState.WaitingForMemory, MemoryBudget.InsufficientMessage, until.AddMinutes(-10), until);
        Check(waiting.Describe() == $"waiting for free RAM, retrying until {until.ToLocalTime():HH:mm}" &&
            (waiting with { State = ProfileRestoreState.NotRestored, Detail = "RAM stayed short.", RetryUntilUtc = null }).Describe() == "not restored at startup: RAM stayed short." &&
            (waiting with { State = ProfileRestoreState.Restored }).Describe() is null,
            "the volume list names a waiting or failed startup restore and says nothing once it is restored");
        // #22: Windows waits at most an hour; the broker stops the saves 2 minutes earlier to record the outcome,
        // and gives a save up after 3 minutes without progress.
        Check(WindowsManagedBrokerService.PreshutdownTimeoutMs == 60U * 60 * 1000 && WindowsManagedBrokerService.StallLimit == TimeSpan.FromMinutes(3) &&
            WindowsManagedBrokerService.ShutdownSaveLimit == TimeSpan.FromMinutes(58),
            "Windows waits at most an hour for shutdown saves; the broker stops them just before and after 3 minutes without progress");
        var watchdogLog = new List<string>();
        using (var watchdog = new WindowsManagedBrokerService.ShutdownSaveWatchdog(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(50), line => { lock (watchdogLog) watchdogLog.Add(line); }))
        {
            for (ulong done = 0; done <= 10; done++)
            {
                watchdog.Report(new(ManagedDiskState.Saving, "Copying the complete frozen RAM disk.", done << 20, 10UL << 20));
                await Task.Delay(100);
            }
            Check(!watchdog.Token.IsCancellationRequested, "a save that keeps reporting progress is never given up, however long it takes");
            await Task.Delay(1500);
            Check(watchdog.Token.IsCancellationRequested && watchdog.Reason?.StartsWith("The save made no progress for", StringComparison.Ordinal) == true,
                "a save that stops reporting progress is given up, and says why");
        }
        using (var limited = new WindowsManagedBrokerService.ShutdownSaveWatchdog(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50), _ => { }, TimeSpan.FromMilliseconds(500)))
        {
            for (ulong done = 0; done <= 10 && !limited.Token.IsCancellationRequested; done++)
            {
                limited.Report(new(ManagedDiskState.Saving, "Copying the complete frozen RAM disk.", done << 20, 10UL << 20));
                await Task.Delay(100);
            }
            Check(limited.Token.IsCancellationRequested && limited.Reason == "The save did not finish within the 1-hour limit at shutdown",
                "shutdown saves that keep progressing are still stopped at the overall limit");
        }
        lock (watchdogLog)
            Check(watchdogLog.Count(l => l.StartsWith("Shutdown save: Copying", StringComparison.Ordinal)) == 11 &&
                watchdogLog.Any(l => l.Contains("no progress", StringComparison.Ordinal)),
                "the shutdown save log names each tenth of the progress and the give-up");
        Check(WindowsManagedDiskEngine.StartupMemoryRetry == TimeSpan.FromMinutes(10) && WindowsManagedDiskEngine.StartupMemoryRetryInterval == TimeSpan.FromSeconds(15),
            "startup retries a RAM refusal every 15 s for 10 minutes");
        var request = new ManagedBrokerRequest(1, Guid.NewGuid(), "create", ManagedDiskDefinition.New(ManagedDiskMode.EphemeralRam));
        using var wire = new MemoryStream(); await ManagedDiskBrokerProtocol.WriteAsync(wire, request);
        var bytes = wire.ToArray();
        using var fragmented = new Fragments(bytes);
        var decoded = await ManagedDiskBrokerProtocol.ReadAsync<ManagedBrokerRequest>(fragmented);
        Check(decoded == request, "fragmented broker frames preserve the complete typed definition");
        Check(await ManagedDiskBrokerProtocol.ReadAsync<ManagedBrokerRequest>(fragmented) is null, "clean frame-boundary EOF is distinguishable from an incomplete frame");
        foreach (var length in new[] { 0, -1, ManagedDiskBrokerProtocol.MaximumFrameBytes + 1 })
        {
            var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, length);
            await RejectAsync<InvalidDataException>(() => ManagedDiskBrokerProtocol.ReadAsync<ManagedBrokerRequest>(new MemoryStream(header)));
        }
        foreach (var end in new[] { 1, 3, bytes.Length - 1 })
            await RejectAsync<EndOfStreamException>(() => ManagedDiskBrokerProtocol.ReadAsync<ManagedBrokerRequest>(new MemoryStream(bytes[..end])));
        Reject(() => ManagedDiskBrokerProtocol.Validate(request with { Version = 2 }));
        Reject(() => ManagedDiskBrokerProtocol.Validate(request with { OperationId = Guid.Empty }));
        Reject(() => ManagedDiskBrokerProtocol.Validate(request with { Operation = "cancel" }));
        Reject(() => ManagedDiskBrokerProtocol.Validate(new(1, Guid.NewGuid(), "action")));
        Reject(() => ManagedDiskBrokerProtocol.Validate(new(1, Guid.NewGuid(), "inspect", Path: @"\\server\image.vhdx")));
        // An old preview cannot authorize mutation of a replacement disk or newer erase generation.
        var runtime = new ManagedDiskRuntime(request.Definition!.ResourceId, Guid.NewGuid(), 2, ManagedDiskMode.EphemeralRam, ManagedDiskState.Ready, 10, null);
        var record = new ManagedDiskRecord(request.Definition, runtime);
        ManagedDiskExpected.From(runtime).Validate(record, true);
        RejectIo(() => ManagedDiskExpected.From(runtime with { CreationGeneration = 1 }).Validate(record));
        RejectIo(() => ManagedDiskExpected.From(runtime with { BootEpoch = Guid.NewGuid() }).Validate(record));
        RejectIo(() => ManagedDiskExpected.From(runtime with { WriteGeneration = 9 }).Validate(record, true));
        ManagedDiskExpected.From(runtime with { WriteGeneration = 9 }).Validate(record, false);
        Console.WriteLine("Managed broker framing and selected-generation contracts passed.");
    }
    private static void Check(bool good, string why) { if (!good) throw new Exception(why); }
    private static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { return; } throw new Exception("Invalid broker request accepted."); }
    private static void RejectIo(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Stale managed identity accepted."); }
    private static async Task RejectAsync<T>(Func<Task<ManagedBrokerRequest?>> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Incomplete/invalid broker frame accepted."); }
    private sealed class Fragments(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], token);
    }
}
