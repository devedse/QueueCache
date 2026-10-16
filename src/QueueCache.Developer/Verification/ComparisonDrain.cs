using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

[JsonConverter(typeof(JsonStringEnumConverter<ComparisonProvider>))]
public enum ComparisonProvider { Direct, QueueCache, PrimoCache }

public sealed record ComparisonDiskCounters(
    [property: JsonRequired] DateTimeOffset Utc, [property: JsonRequired] long BytesWritten,
    [property: JsonRequired] uint WriteOperations, [property: JsonRequired] uint QueueDepth);

/// <summary>Null means inapplicable, never an unobserved zero. Primo's Deferred Blocks includes pending writes;
/// QueueCache supplies its coherent dirty/in-flight byte counters. Direct has no product cache.</summary>
public sealed record ComparisonPending(
    [property: JsonRequired] DateTimeOffset Utc, [property: JsonRequired] ComparisonProvider Provider,
    [property: JsonRequired] ulong? DirtyBytes, [property: JsonRequired] ulong? InFlightBytes,
    [property: JsonRequired] ulong? DeferredBlocks);

public sealed record ComparisonDrainScore(
    [property: JsonRequired] string Id, [property: JsonRequired] ComparisonProvider Provider,
    [property: JsonRequired] long Bytes, [property: JsonRequired] int Files,
    [property: JsonRequired] DateTimeOffset ReadyUtc, [property: JsonRequired] DateTimeOffset StartedUtc,
    [property: JsonRequired] DateTimeOffset ApplicationEndedUtc, [property: JsonRequired] DateTimeOffset DrainRequestedUtc,
    [property: JsonRequired] DateTimeOffset DrainReturnedUtc, [property: JsonRequired] DateTimeOffset EndedUtc,
    [property: JsonRequired] double ApplicationSeconds, [property: JsonRequired] double DrainSeconds,
    [property: JsonRequired] double ExplicitDrainSeconds, [property: JsonRequired] double ConfirmationSeconds,
    [property: JsonRequired] double TotalSeconds, [property: JsonRequired] double MBPerSecond,
    [property: JsonRequired] ComparisonPending Before, [property: JsonRequired] ComparisonPending After,
    [property: JsonRequired] ComparisonDiskCounters DiskBefore, [property: JsonRequired] ComparisonDiskCounters DiskAfter,
    [property: JsonRequired] ComparisonDiskCounters[] DiskSamples);

public sealed record ComparisonFileHash(string File, long Bytes, string Sha256);
public sealed record ComparisonPrimoObservation(string Status, string Volumes);
public sealed record ComparisonDrainVerification(
    [property: JsonRequired] string Id, [property: JsonRequired] bool Bypassed,
    [property: JsonRequired] bool Restored, [property: JsonRequired] bool Verified,
    [property: JsonRequired] ComparisonFileHash[] Files);

/// <summary>Plan 148 fixed-work write-through-drain comparison: 1 GiB large file, 256 MiB small files.
/// Each provider uses the same workload and shared payload prefix. No scored overwrites.</summary>
public static class ComparisonDrain
{
    public const int PayloadBytes = 1 << 30, LargeBytes = PayloadBytes, SmallFiles = 4096, SmallFileBytes = 64 << 10,
        SmallBytes = SmallFiles * SmallFileBytes,
        LargeBlockBytes = 1 << 20, SmallBlockBytes = 4096, Workers = 4, Seed = 147_16384;
    public static string DefaultPrimoCli => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PrimoCache", "rxpcc.exe");

    public static int ExpectedBytes(string kind) => kind switch
    {
        "drain-large" => LargeBytes,
        "drain-small" => SmallBytes,
        _ => throw new InvalidDataException("Unknown compare-drain workload: " + kind)
    };

    public static IReadOnlyList<ComparisonCase> Cases(int repeats)
    {
        if (repeats != 3) throw new ArgumentException("compare-drain requires exactly three repeats.");
        var result = new List<ComparisonCase>();
        for (var r = 1; r <= repeats; r++)
        {
            ComparisonCase large = new($"drain-large-r{r}", "drain-large", r, $"large-r{r}", 'W', 1024, 4, 1, Bytes: LargeBytes);
            ComparisonCase small = new($"drain-small-r{r}", "drain-small", r, $"small-r{r}", 'W', 4, 1, 4, Bytes: SmallBytes);
            result.AddRange(r % 2 == 1 ? [large, small] : new[] { small, large });
        }
        return result;
    }

    public static void ValidateOptions(VerificationOptions options)
    {
        if (options.Suite != "compare-drain")
        {
            if (options.Provider is not null || options.PrimoCli is not null)
                throw new ArgumentException("--provider and --primo-cli apply only to compare-drain.");
            return;
        }
        if (options.Provider is not { } provider || !Enum.IsDefined(provider) || options.Repeats != 3 || options.BudgetMiB != 2048)
            throw new ArgumentException("compare-drain requires --provider Direct|QueueCache|PrimoCache, --budget-mib 2048 and --repeats 3.");
        if (provider != ComparisonProvider.PrimoCache && options.PrimoCli is not null)
            throw new ArgumentException("--primo-cli requires --provider PrimoCache.");
        if (provider == ComparisonProvider.PrimoCache && !File.Exists(options.PrimoCli ?? DefaultPrimoCli))
            throw new FileNotFoundException("PrimoCache CLI is required (close its GUI); use --primo-cli if installed elsewhere.", options.PrimoCli ?? DefaultPrimoCli);
    }

    public static void ValidatePending(ComparisonPending value, ComparisonProvider provider, bool clean)
    {
        if (value.Provider != provider || value.Utc == default ||
            provider == ComparisonProvider.QueueCache && (value.DirtyBytes is null || value.InFlightBytes is null || value.InFlightBytes > value.DirtyBytes) ||
            provider == ComparisonProvider.PrimoCache && value.DeferredBlocks is null ||
            clean && (value.DirtyBytes is > 0 || value.InFlightBytes is > 0 || value.DeferredBlocks is > 0))
            throw new InvalidDataException("Missing, inconsistent or nonzero product pending counters.");
    }

    public static void ValidateScore(ComparisonCase test, ComparisonDrainScore score, ComparisonProvider provider)
    {
        ValidatePending(score.Before, provider, true); ValidatePending(score.After, provider, true);
        var expectedBytes = ExpectedBytes(test.Kind);
        if (score.Id != test.Id || score.Provider != provider || score.Bytes != expectedBytes || score.Bytes != test.Bytes ||
            score.Files != (test.Kind == "drain-large" ? 1 : SmallFiles) ||
            score.ReadyUtc > score.StartedUtc || score.Before.Utc > score.StartedUtc ||
            score.StartedUtc >= score.ApplicationEndedUtc || score.ApplicationEndedUtc > score.DrainRequestedUtc ||
            score.DrainRequestedUtc > score.DrainReturnedUtc || score.DrainReturnedUtc > score.After.Utc ||
            score.After.Utc > score.EndedUtc || score.DiskAfter.Utc < score.After.Utc || score.DiskAfter.Utc > score.EndedUtc ||
            score.DiskBefore.Utc > score.StartedUtc || score.DiskBefore.QueueDepth != 0 || score.DiskAfter.QueueDepth != 0 ||
            !double.IsFinite(score.TotalSeconds) || !double.IsFinite(score.ApplicationSeconds) || !double.IsFinite(score.DrainSeconds) ||
            !double.IsFinite(score.ExplicitDrainSeconds) || score.ExplicitDrainSeconds < 0 ||
            !double.IsFinite(score.ConfirmationSeconds) || score.ConfirmationSeconds < 0 ||
            !double.IsFinite(score.MBPerSecond) || score.TotalSeconds <= 0 || score.ApplicationSeconds <= 0 || score.DrainSeconds < 0 ||
            Math.Abs(score.ApplicationSeconds + score.DrainSeconds - score.TotalSeconds) > 0.001 ||
            score.ExplicitDrainSeconds + score.ConfirmationSeconds > score.DrainSeconds + 0.01 ||
            Math.Abs(score.MBPerSecond - expectedBytes / score.TotalSeconds / 1_000_000.0) > 0.000001)
            throw new InvalidDataException("Invalid or incomplete compare-drain score: " + test.Id);
        ValidateDiskDelta(score.DiskBefore, score.DiskAfter);
        if (score.DiskAfter.BytesWritten - score.DiskBefore.BytesWritten < expectedBytes)
            throw new InvalidDataException("Backing-disk counters do not cover the scored payload.");
        TelemetryCoverage.Validate(score.DiskSamples.Select(s => s.Utc).ToArray(), score.StartedUtc, score.EndedUtc);
        // Reconcile both scored boundaries with the same cumulative observation timeline.
        // Independent monotonic sequences could otherwise claim writes absent from the covering samples.
        var timeline = score.DiskSamples.Append(score.DiskBefore).Append(score.DiskAfter).OrderBy(s => s.Utc).ToArray();
        for (var i = 1; i < timeline.Length; i++) ValidateDiskDelta(timeline[i - 1], timeline[i], allowIdle: true);
    }

    public static void ValidateQueueCache(WriteCacheState state, bool active)
    {
        // Check every nontransient predicate before classifying a barrier as Draining.
        if (state.Faulted || state.LastError != 0 || state.Enabled != active || state.Suspended || state.Removed ||
            !state.UnsafeDefer || state.BudgetBytes != 2UL << 30 ||
            state.Options is not { BatchKiB: 1024, Parallelism: 4 } || state.Performance is not { TimingEnabled: 0 } ||
            active && (state.ReservedBytes == 0 || state.PayloadCapacity == 0 || state.SupportsReadWrite && !state.RoutingConfirmed))
            throw new InvalidDataException($"compare-drain requires a healthy {(active ? "active" : "paused")} 2 GiB QueueCache Fast profile, 1024 KiB x 4, timing diagnostics OFF. State: {JsonSerializer.Serialize(state)}");
        if (state.Draining) throw new CacheDrainingException();
    }

    public static async Task<WriteCacheState> PauseQueueCacheAsync(WriteCacheState original,
        Func<WriteCacheState> capture, Action disable, Action<string, WriteCacheState> persist, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        if (limit < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        WriteCacheState Observe(string phase)
        {
            var state = capture();
            persist(phase, state); // Evidence precedes validation, including every rejected/transient snapshot.
            return state;
        }
        async Task<WriteCacheState> Confirm(bool active, string phase)
        {
            var timer = Stopwatch.StartNew();
            for (var poll = 1; ; poll++)
            {
                var state = Observe($"{phase}-{poll:D4}");
                if (original.Instance == 0 || original.Options is null || state.Instance != original.Instance ||
                    state.DeviceBytes != original.DeviceBytes || state.Options != original.Options || state.BudgetBytes != original.BudgetBytes ||
                    state.ReservedBytes != original.ReservedBytes || state.PayloadCapacity != original.PayloadCapacity ||
                    state.UnsafeDefer != original.UnsafeDefer || state.Errors != original.Errors)
                    throw new InvalidDataException("QueueCache identity, allocation, options, mode or errors changed at " + phase + ". State: " + JsonSerializer.Serialize(state));
                try
                {
                    ValidateQueueCache(state, active);
                    if (!active && (state.DirtyBytes != 0 || state.InFlightBytes != 0))
                        throw new InvalidDataException("QueueCache pause left pending data. State: " + JsonSerializer.Serialize(state));
                    return state;
                }
                catch (CacheDrainingException)
                {
                    // Disable itself is synchronous; another OS barrier may begin before the snapshot.
                    // Wait on observations only, never replay Disable or treat a fault as a drain.
                    if (timer.Elapsed >= limit)
                        throw new TimeoutException($"QueueCache remained Draining at {phase} for {limit.TotalSeconds:F0} seconds. State: {JsonSerializer.Serialize(state)}");
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(200, Math.Max(1, (limit - timer.Elapsed).TotalMilliseconds))));
                }
            }
        }
        await Confirm(active: true, "before-disable"); // Refuse a replaced/reconfigured instance before any mutation.
        try { disable(); }
        catch (Exception failure)
        {
            try { Observe("disable-failed"); }
            catch (Exception observation) { throw new AggregateException("QueueCache Disable and failure-state capture failed.", failure, observation); }
            throw;
        }
        return await Confirm(active: false, "after-disable");
    }

    public static WriteCacheState RestoreQueueCache(WriteCacheState original, Func<WriteCacheState> capture, Action enable)
    {
        void ValidateIdentity(WriteCacheState current)
        {
            if (current.Instance != original.Instance || original.Options is null || current.Options != original.Options ||
                current.BudgetBytes != original.BudgetBytes || current.UnsafeDefer != original.UnsafeDefer || current.Errors != original.Errors ||
                current.Faulted || current.LastError != 0 || current.Removed || current.Suspended || current.Draining)
                throw new InvalidDataException("QueueCache recovery instance, options, budget, mode, errors or lifecycle state changed; restoration refused.");
        }
        ValidateIdentity(capture()); // A replacement cache must remain untouched, even on the same volume.
        enable();
        var restored = capture();
        ValidateIdentity(restored);
        if (!restored.Operational) throw new InvalidDataException("QueueCache recovery did not restore the original active state.");
        return restored;
    }

    public static async Task RestorePrimoAsync(Guid? originalTaskId, string volume,
        Func<Task<ComparisonPrimoObservation>> capture, Func<Task> resume)
    {
        if (originalTaskId is null || originalTaskId == Guid.Empty)
            throw new InvalidDataException("Missing original PrimoCache task GUID; resume refused.");
        var current = await capture();
        if (PrimoTaskId(current, volume) != originalTaskId)
            throw new InvalidDataException("PrimoCache task ownership changed; resume refused.");
        ValidatePrimoConfiguration(current.Status, allowPaused: true);
        await resume();
        var restored = await capture();
        if (PrimoTaskId(restored, volume, paused: false) != originalTaskId)
            throw new InvalidDataException("Original PrimoCache task ownership was not restored.");
        ValidatePrimoConfiguration(restored.Status);
    }

    public static void ValidateRecoveryTarget(ComparisonDrainRecovery recovery, ComparisonTarget current)
    {
        var original = recovery.Target;
        if (!Enum.IsDefined(recovery.Provider) || recovery.Provider == ComparisonProvider.QueueCache && recovery.OriginalState is null ||
            original.VolumeName is null || current.VolumeName is null || original.DiskNumber is null ||
            !string.Equals(current.VolumeName, original.VolumeName, StringComparison.OrdinalIgnoreCase) ||
            current.Letter != original.Letter || !string.Equals(recovery.Volume, $"{current.Letter}:", StringComparison.OrdinalIgnoreCase) ||
            current.DiskNumber != original.DiskNumber || current.TotalBytes != original.TotalBytes ||
            current.FileSystem != original.FileSystem || current.ClusterBytes != original.ClusterBytes)
            throw new InvalidDataException("Comparison recovery target identity/provider changed; no provider control was issued.");
    }

    public static void ValidateDiskDelta(ComparisonDiskCounters before, ComparisonDiskCounters after, bool allowIdle = false)
    {
        if (before.BytesWritten < 0 || after.BytesWritten < before.BytesWritten || after.WriteOperations < before.WriteOperations ||
            !allowIdle && (after.BytesWritten == before.BytesWritten || after.WriteOperations == before.WriteOperations))
            throw new InvalidDataException("Missing or regressing backing-disk write bytes/operations (counter wrap is not accepted).");
    }

    public static async Task<ComparisonFileHash[]> VerifyBypassedAsync(string id, Func<Task> pause,
        Func<ComparisonFileHash[]> verify, Func<Task> restore, Action<ComparisonDrainVerification> persist)
    {
        ComparisonFileHash[] hashes = [];
        var bypassed = false; var verified = false; var restored = false;
        try
        {
            await pause(); bypassed = true;
            hashes = verify(); verified = true;
            return hashes;
        }
        finally
        {
            try { await restore(); restored = true; }
            finally { persist(new(id, bypassed, restored, verified, hashes)); }
        }
    }

    /// <summary>Only the current Deferred Blocks label is accepted; accumulated deferred bytes are not pending data.
    /// Integer grouping and an optional 0..100 percentage (Primo 4.4.1) are accepted; decimal block counts/units are rejected.</summary>
    public static ulong ParseDeferredBlocks(string text)
    {
        // Count labeled rows before validating their values, so a malformed duplicate cannot be ignored.
        var rows = Regex.Matches(text, @"(?im)^[ \t]*Deferred Blocks\b[^\r\n]*");
        if (rows.Count != 1) throw new InvalidDataException("Expected exactly one current 'Deferred Blocks' counter in rxpcc perf output.");
        var match = Regex.Match(rows[0].Value,
            @"\A[ \t]*Deferred Blocks[ \t]*:[ \t]*(?<blocks>[0-9]+|[0-9]{1,3}(?<separator>[ ,.'\u00a0])[0-9]{3}(?:\k<separator>[0-9]{3})*)[ \t]*" +
            @"(?:\((?:[0-9]{1,2}(?:[.,][0-9]+)?|100(?:[.,]0+)?)%\))?[ \t]*\z", RegexOptions.IgnoreCase);
        var number = Regex.Replace(match.Groups["blocks"].Value, @"[ ,.'\u00a0]", "");
        if (!match.Success || !ulong.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var blocks))
            throw new InvalidDataException("Invalid 'Deferred Blocks' count or percentage in rxpcc perf output.");
        return blocks;
    }

    public static void ValidatePrimoConfiguration(string text, bool allowPaused = false)
    {
        foreach (var pattern in new[] { allowPaused ? @"^\s*Status:\s*(?:Active|Paused)\s*$" : @"^\s*Status:\s*Active\s*$", @"^\s*Level-1 Cache:\s*2048(?:[.,]0+)?\s*MB\s*$",
            @"^\s*Level-2 Cache:\s*0(?:[.,]0+)?\s*MB\s*$", @"^\s*R/W Ratio:\s*Shared\s*$", @"^\s*Block Size:\s*4\s*KB\s*$",
            @"^\s*Defer-Write:\s*Enabled\s*$", @"^\s*Latency:\s*10s\s*$", @"^\s*Mode:\s*Native\s*$", @"^\s*Prefetch:\s*Disabled\s*$" })
            if (!Regex.IsMatch(text, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase))
                throw new InvalidDataException("PrimoCache must be " + (allowPaused ? "active or paused" : "active") + ", shared 2048 MB L1, no L2, 4 KB, Native defer-write 10 s, prefetch disabled: " + pattern);
    }

    public static void ValidatePrimoVolumeState(string status, string volumes, string volume, bool paused)
    {
        var rows = Regex.Matches(volumes, @"(?im)^Vol #([0-9]+)[^\r\n]*\(" + Regex.Escape(volume) + @"\)[^\r\n]*\r?$");
        if (rows.Count != 1) throw new InvalidDataException("Expected exactly one PrimoCache target-volume mapping.");
        var id = rows[0].Groups[1].Value;
        var states = Regex.Matches(status, @"(?im)^Volume #" + id + @":\s*Cache\s*\((Active|Paused)\)\s*$");
        if (states.Count != 1 || states[0].Groups[1].Value != (paused ? "Paused" : "Active"))
            throw new InvalidDataException("PrimoCache did not confirm the target volume's " + (paused ? "paused" : "active") + " state.");
    }

    public static Guid PrimoTaskId(ComparisonPrimoObservation observation, string volume, bool? paused = null)
    {
        var tasks = Regex.Matches(observation.Status, @"(?im)^Cache Task #([0-9]+)[ \t]+(\{[0-9a-f-]+\})[ \t]*\r?$");
        if (Regex.Matches(observation.Status, @"(?im)^Cache Task #").Count != 1 || tasks.Count != 1 ||
            !Guid.TryParseExact(tasks[0].Groups[2].Value, "B", out var taskId) || taskId == Guid.Empty)
            throw new InvalidDataException("Expected exactly one valid PrimoCache task GUID.");
        var rows = Regex.Matches(observation.Volumes, @"(?im)^Vol #([0-9]+)[^\r\n]*\(" + Regex.Escape(volume) + @"\)[^\r\n]*\r?$");
        var owner = rows.Count == 1 ? Regex.Match(rows[0].Value, @"[ \t]+([0-9]+)[ \t]*\r?$") : null;
        if (owner is null || !owner.Success || owner.Groups[1].Value != tasks[0].Groups[1].Value)
            throw new InvalidDataException("The recorded PrimoCache task does not unambiguously own the target volume.");
        if (Regex.Matches(observation.Status, @"(?im)^[ \t]*Status:[ \t]*(Active|Paused)[ \t]*\r?$").Count != 1)
            throw new InvalidDataException("PrimoCache task must be active or paused before recovery.");
        var states = Regex.Matches(observation.Status, @"(?im)^Volume #" + rows[0].Groups[1].Value + @":\s*Cache\s*\((Active|Paused)\)\s*$");
        if (states.Count != 1 || paused is { } expected && !states[0].Groups[1].Value.Equals(expected ? "Paused" : "Active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("PrimoCache target volume must be " + (paused is null ? "active or paused." : paused.Value ? "paused." : "active."));
        return taskId;
    }
}
