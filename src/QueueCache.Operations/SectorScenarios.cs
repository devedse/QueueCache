using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using QueueCache.Management;

namespace QueueCache.Operations;

/// <summary>Sector ownership regressions. Caller owns runtime restoration, including failure.</summary>
[SupportedOSPlatform("windows")]
internal static class SectorScenarios
{
    internal static string VerifyAdmissionAttempts(CacheAttribution? before, CacheAttribution? after,
        CachePagingRoute? routeBefore = null, CachePagingRoute? routeAfter = null)
    {
        if (before is null || after is null)
            throw new NotSupportedException("Admission proof requires diagnostics V2 lower-I/O attempt counters.");
        var evidence = $"Lower attempts read/write/flush before={before.LowerReadAttempts}/{before.LowerWriteAttempts}/{before.LowerFlushAttempts}, " +
            $"after={after.LowerReadAttempts}/{after.LowerWriteAttempts}/{after.LowerFlushAttempts}.";
        var lowerWrites = after.LowerWriteAttempts >= before.LowerWriteAttempts
            ? after.LowerWriteAttempts - before.LowerWriteAttempts : ulong.MaxValue;
        var routedWrites = routeBefore is not null && routeAfter is not null &&
            routeAfter.WriteRequests >= routeBefore.WriteRequests &&
            routeAfter.WriteCompletions >= routeBefore.WriteCompletions &&
            routeAfter.WriteFailures == routeBefore.WriteFailures
            ? routeAfter.WriteRequests - routeBefore.WriteRequests : ulong.MaxValue;
        var completedWrites = routeBefore is not null && routeAfter is not null &&
            routeAfter.WriteCompletions >= routeBefore.WriteCompletions
            ? routeAfter.WriteCompletions - routeBefore.WriteCompletions : ulong.MaxValue;
        if (before.LowerReadAttempts != after.LowerReadAttempts ||
            before.LowerFlushAttempts != after.LowerFlushAttempts ||
            (lowerWrites != 0 && (lowerWrites != routedWrites || routedWrites != completedWrites)) ||
            (routeBefore is not null && routeAfter is not null &&
                (routedWrites != lowerWrites || routedWrites != completedWrites)))
            throw new IOException("Deferred admission issued unexplained lower I/O. " + evidence +
                $" Routed paging writes={routedWrites}, completed={completedWrites}.");
        return evidence + (lowerWrites == 0 ? " No lower I/O." :
            $" Exactly {lowerWrites} successful routed paging write(s) coincided with the lower writes; " +
            "this process-wide counter match does not identify their file range or prove causation.");
    }

    public static void Run(DiskTarget target, CacheDevice device, string directory,
        List<CheckResult> results, IProgress<string>? progress, CancellationToken token)
    {
        if (!GetDiskFreeSpaceW(target.Root, out _, out var sectorBytes, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        // A native 4Kn volume cannot issue sub-cache-block unbuffered requests.
        // Record this explicitly rather than claiming its sector cases passed.
        if (sectorBytes != 512)
            throw new NotSupportedException("Partial-sector verification requires a 512-byte logical sector volume.");
        var originalErrors = device.GetWriteCacheState().Errors;
        foreach (var parallelism in new[] { 1, 2, 4 })
        foreach (var retain in new[] { false, true })
        {
            token.ThrowIfCancellationRequested();
            var label = $"sectors/p{parallelism}/retain{retain}";
            progress?.Report(label);
            var options = new CacheOptions(Drain: DrainAlgorithm.Deferred,
                MaxDirtyAgeMs: 3600000, Parallelism: parallelism, RetainWrites: retain);
            ConfigurationManager.Apply(target, new CacheConfiguration(64, CachePreset.Fast) { Options = options }, true);
            var expected = new byte[65536];
            new Random(718).NextBytes(expected);
            var actual = new byte[expected.Length];
            using var file = new AlignedFile(Path.Combine(directory, $"sectors-{parallelism}-{retain}.bin"),
                expected.Length, true, alignment: 512);
            file.Write(0, expected);
            device.Control(WriteCacheAction.Flush);
            device.Control(WriteCacheAction.DropClean);
            var before = device.GetWriteCacheState();
            if (before.DirtyBytes != 0 || before.InFlightBytes != 0)
                throw new IOException(label + ": admission requires a clean, idle lower-data boundary");
            var diagnosticsBefore = device.GetDiagnostics();
            var attemptsBefore = diagnosticsBefore.Attribution;
            if (attemptsBefore is null)
                throw new NotSupportedException("Sector admission requires diagnostics V2; install the attribution driver.");
            void Write(int offset, int length, byte value)
            {
                token.ThrowIfCancellationRequested();
                var patch = new byte[length];
                Array.Fill(patch, value);
                file.Write(offset, patch);
                patch.CopyTo(expected, offset);
                var immediate = new byte[length];
                file.Read(offset, immediate);
                if (!patch.AsSpan().SequenceEqual(immediate))
                    throw new IOException(label + ": partial RAM read mismatch");
            }
            // Every position in a 4 KiB slot, then disjoint and crossing masks.
            for (var i = 0; i < 8; i++)
                Write(i * 4096 + i * 512, 512, (byte)(31 + i));
            Write(3584, 1536, 169);
            Write(40960, 4096, 211);
            var admitted = device.GetWriteCacheState();
            var diagnosticsAfter = device.GetDiagnostics();
            var attemptsAfter = diagnosticsAfter.Attribution;
            var admissionEvidence = VerifyAdmissionAttempts(attemptsBefore, attemptsAfter,
                diagnosticsBefore.PagingRoute, diagnosticsAfter.PagingRoute);
            if (admitted.LowerWrites != before.LowerWrites || admitted.Flushes != before.Flushes)
                throw new IOException(label + ": unexpected lower write/flush during deferred partial admission");
            file.Read(0, actual);
            if (!expected.AsSpan().SequenceEqual(actual))
                throw new IOException(label + ": cold neighbour/partial overlay mismatch");
            results.Add(new(label + "/admission", "PASS", "All sector positions, crossing and full writes plus cached reads matched. " + admissionEvidence));
            device.Control(WriteCacheAction.Disable);
            var beforeDiskRead = device.GetDiagnostics().Attribution!;
            file.Read(0, actual);
            var afterDiskRead = device.GetDiagnostics().Attribution!;
            if (beforeDiskRead.LowerWriteAttempts <= attemptsAfter!.LowerWriteAttempts ||
                beforeDiskRead.LowerFlushAttempts <= attemptsAfter.LowerFlushAttempts ||
                afterDiskRead.LowerReadAttempts <= beforeDiskRead.LowerReadAttempts)
                throw new IOException(label + ": lower attempt counters did not observe explicit drain/flush/disk read");
            if (!expected.AsSpan().SequenceEqual(actual))
                throw new IOException(label + ": sparse-drain disk mismatch");
            device.SetOptions(options with { Drain = DrainAlgorithm.Eager });
            device.Control(WriteCacheAction.LabDelay, value: 25);
            device.Control(WriteCacheAction.Enable);
            var observedInFlight = false;
            for (var i = 0; i < 128; i++)
            {
                Write(i % 3 == 0 ? 0 : i % 8 * 512, i % 3 == 0 ? 4096 : 512, (byte)i);
                observedInFlight |= device.GetWriteCacheState().InFlightBytes != 0;
                file.Read(0, actual);
                if (!expected.AsSpan().SequenceEqual(actual))
                    throw new IOException(label + ": partial/full overwrite mismatch");
            }
            device.Control(WriteCacheAction.LabDelay, value: 0);
            device.Control(WriteCacheAction.Disable);
            file.Read(0, actual);
            var final = device.GetWriteCacheState();
            if (!expected.AsSpan().SequenceEqual(actual) || final.DirtyBytes != 0 ||
                final.InFlightBytes != 0 || final.Errors != originalErrors || final.LastError != 0)
                throw new IOException(label + ": final disk oracle/state mismatch");
            results.Add(new(label + "/disk-oracle", "PASS",
                $"Exact disk bytes after sparse drains and 128 full/partial overwrites. In-flight state observed: {observedInFlight}."));
        }
        RunObservedReplacement(target, device, directory, originalErrors, results, token);
    }

    private static void RunObservedReplacement(DiskTarget target, CacheDevice device, string directory,
        ulong originalErrors, List<CheckResult> results, CancellationToken token)
    {
        const string label = "sectors/observed-inflight-replacement";
        token.ThrowIfCancellationRequested();
        ConfigurationManager.Apply(target, new CacheConfiguration(64, CachePreset.Fast)
        {
            Options = new CacheOptions(Drain: DrainAlgorithm.Eager, Parallelism: 1, RetainWrites: false)
        }, true);
        using var file = new AlignedFile(Path.Combine(directory, "inflight-replacement.bin"), 4096, true, alignment: 512);
        var baseline = new byte[4096];
        new Random(719).NextBytes(baseline);
        file.Write(0, baseline);
        device.Control(WriteCacheAction.Flush);
        device.Control(WriteCacheAction.DropClean);
        device.Control(WriteCacheAction.LabDelay, value: 2000);
        var old = new byte[512];
        var newest = new byte[512];
        Array.Fill(old, (byte)0x4D);
        Array.Fill(newest, (byte)0xB6);
        file.Write(512, old);
        // A 4 KiB NTFS metadata write can also be in flight here. Only the exact
        // 512-byte payload interval is useful evidence for this replacement.
        if (!SpinWait.SpinUntil(() => device.GetWriteCacheState().InFlightBytes == 512, 8000))
            throw new IOException(label + ": the 512-byte payload did not enter an isolated lower-write interval");
        var inFlightBefore = device.GetWriteCacheState();
        file.Write(512, newest);
        var inFlightAfter = device.GetWriteCacheState();
        if (inFlightBefore.InFlightBytes != 512 || inFlightAfter.InFlightBytes != 512)
            throw new IOException(label + ": isolated payload overlap was not observed across replacement");
        var actual = new byte[512];
        file.Read(512, actual);
        if (!newest.AsSpan().SequenceEqual(actual))
            throw new IOException(label + ": newest acknowledged bytes were not visible in RAM");
        device.Control(WriteCacheAction.LabDelay, value: 0);
        device.Control(WriteCacheAction.Disable);
        file.Read(512, actual);
        var final = device.GetWriteCacheState();
        if (!newest.AsSpan().SequenceEqual(actual) || final.DirtyBytes != 0 || final.InFlightBytes != 0 ||
            final.LastError != 0 || final.Errors != originalErrors)
            throw new IOException(label + ": newest bytes were not persisted after Disable");
        results.Add(new(label, "PASS", $"Observed in-flight bytes before/after newer admission " +
            $"{inFlightBefore.InFlightBytes}/{inFlightAfter.InFlightBytes}; newest RAM and disk bytes matched. " +
            "This does not prove a fully controlled old/new completion interleaving."));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster,
        out uint bytesPerSector, out uint freeClusters, out uint totalClusters);
}
