using System.Runtime.Versioning;
using QueueCache.Management;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Developer.Verification;

/// <summary>Lifecycle and data checks of asynchronous split reads on an owned RAM disk (plan 119):
/// verified overlapped reads in the product default mode, the in-flight cap, and ending Direct access
/// while forced asynchronous reads are in flight. Correctness only; scores are not compared.</summary>
[SupportedOSPlatform("windows")]
internal static class RamReadAsyncChecks
{
    public static async Task<string> RunAsync(RamDiskSnapshot native, string volume, string file, string prefix)
    {
        var fingerprints = OverlappedReadOracle.Fingerprints(RamReadReferencePlan.FileMiB);
        using var provider = WindowsRamDisk.Connect();
        RamAsyncReads Read() => provider.AsyncReads(native) ?? throw new NotSupportedException("The loaded provider does not implement asynchronous RAM reads.");
        static void Balanced(RamAsyncReads state, RamAsyncMode mode, string stage)
        {
            if (state.Mode != mode || state.InFlight != 0 || state.InlineActive != 0 || state.Started == 0 ||
                state.Started != state.Completed + state.Declined || state.MaxInFlight == 0 || state.MaxInFlight > RamAsyncReads.Cap)
                throw new InvalidDataException($"{stage}: asynchronous read accounting is not quiescent and balanced.");
        }
        static Task<OverlappedReadOracle.Result> Reader(string file, long[] fingerprints, int depth, double seconds) =>
            Task.Factory.StartNew(() => OverlappedReadOracle.Run(file, fingerprints, depth, TimeSpan.FromSeconds(seconds)),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var initial = Read();
        RunStorage.AtomicJson(prefix + ".async-initial.json", initial);
        if (initial.Mode != RamAsyncMode.Adaptive || initial.InFlight != 0 || initial.Started != 0)
            throw new InvalidDataException("A new RAM disk must start in adaptive mode with nothing admitted.");
        if (RamDirectChecks.State(volume) is { Full: false } notFull)
            throw new IOException($"Direct access is not fully active before the checks: {notFull.Describe()}.");

        // 1. Product default, one thread at queue depth 8: correct bytes on whichever path the policy
        // picks. The share is recorded, not required: when submission is slower than a copy (for
        // example under Driver Verifier) the policy correctly stays inline. ram-read-async enforces
        // activation where speed is measured.
        provider.SetAsyncReads(native, RamAsyncMode.Adaptive);
        var adaptive = await Reader(file, fingerprints, 8, 2);
        var adaptiveState = Read();
        RunStorage.AtomicJson(prefix + ".async-adaptive.json", new { Load = adaptive, State = adaptiveState });
        Balanced(adaptiveState, RamAsyncMode.Adaptive, "Adaptive queue depth 8");

        // 2. Forced, queue depth 32: the in-flight cap is never exceeded; reads past it are copied inline.
        provider.SetAsyncReads(native, RamAsyncMode.Always);
        var saturated = await Reader(file, fingerprints, 32, 2);
        var saturatedState = Read();
        RunStorage.AtomicJson(prefix + ".async-saturation.json", new { Load = saturated, State = saturatedState });
        Balanced(saturatedState, RamAsyncMode.Always, "Forced queue depth 32");
        if (saturatedState.InlineCap > 0 && saturatedState.MaxInFlight != RamAsyncReads.Cap)
            throw new InvalidDataException("Reads fell back for the cap without the cap having been reached.");

        // 3. Forced reads in flight while Direct access ends: the end waits for them; the standard path serves the rest.
        provider.SetAsyncReads(native, RamAsyncMode.Always);
        var load = Reader(file, fingerprints, 8, 3);
        await Task.Delay(1000);
        if (load.IsCompleted) await load; // Surface an early failure.
        var ended = RamDirectChecks.EndWithUnrecognizedControl(volume);
        var during = await load;
        var endedState = Read();
        Balanced(endedState, RamAsyncMode.Always, "Direct access ended under load");
        var rebound = RamDirectChecks.Rebind(volume);
        provider.SetAsyncReads(native, RamAsyncMode.Adaptive);
        var restored = Read();
        RunStorage.AtomicJson(prefix + ".async-end-under-load.json", new { Load = during, State = endedState, Ended = ended, Rebound = rebound, Restored = restored });
        if (restored.Mode != RamAsyncMode.Adaptive || restored.InFlight != 0)
            throw new IOException("The RAM disk did not return to adaptive asynchronous reads.");
        return $"Verified overlapped reads: adaptive Q8 {adaptive.Blocks} MiB ({adaptiveState.Completed} asynchronous); " +
               $"forced Q32 {saturated.Blocks} MiB (max in flight {saturatedState.MaxInFlight}, {saturatedState.InlineCap} past the cap inline); " +
               $"Direct access ended under forced Q8 load after {endedState.Completed} asynchronous reads and the standard path " +
               $"served the rest ({during.Blocks} MiB verified); rebound and restored to adaptive.";
    }
}
