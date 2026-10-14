using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

public sealed record TreeDecisionEvidence(TreeDecisionCase Case, CacheLayoutSnapshot Before,
    TreeResult Tree, CacheLayoutSnapshot After)
{
    public TreeDecisionAttemptDeltas LowerIoAttempts => TreeDecisionEvidenceContract.GetAttemptDeltas(this);
}

public sealed record TreeBuildDecisionEvidence(TreeBuildDecisionCase Case,
    LoadedDriverObservation DriversBefore, TreeDecisionEvidence Tree, LoadedDriverObservation DriversAfter);

public static class TreeBuildDecisionEvidenceContract
{
    public static void ValidateCapabilities(CacheLayoutSnapshot snapshot)
    {
        if (snapshot.Diagnostics.Attribution is null)
            throw new InvalidDataException("This loaded driver does not expose the lower-I/O counters required by tree-build-decision; refusing to mutate cache settings.");
    }

    public static void ValidateStart(TreeBuildDecisionCase test, LoadedDriverObservation observation)
    {
        ValidateObservation(test, observation);
    }

    public static void Validate(TreeBuildDecisionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Tree.Case.Id != evidence.Case.Id || evidence.Tree.Case.CopyFlags != 7 ||
            evidence.Tree.Case.BatchKiB != 256 || evidence.Tree.Case.Parallelism != 2 || !evidence.Tree.Case.CacheEnabled)
            throw new InvalidDataException("Tree build evidence does not match its fixed flags-7, 256 KiB × 2 workload case.");
        TreeDecisionEvidenceContract.Validate(evidence.Tree);
        ValidateObservation(evidence.Case, evidence.DriversBefore);
        ValidateObservation(evidence.Case, evidence.DriversAfter);
        if (!Identity(evidence.DriversBefore).SequenceEqual(Identity(evidence.DriversAfter), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The loaded QueueCache driver paths or file hashes changed during the tree workload.");
    }

    private static void ValidateObservation(TreeBuildDecisionCase test, LoadedDriverObservation observation)
    {
        if (!observation.Available || observation.Modules is null || observation.Modules.Count != 2)
            throw new InvalidDataException("Loaded QueueCache driver identity is unavailable or contains an unexpected module set.");
        var names = observation.Modules.Select(module => FileName(module.FilePath)).ToArray();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2 ||
            observation.Modules.Count(module => IsFilter(FileName(module.FilePath))) != 1 ||
            observation.Modules.Count(module => FileName(module.FilePath).Equals("qcramdisk.sys", StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidDataException("Expected exactly one loaded QueueCache filter module and one qcramdisk.sys module.");
        var filter = observation.Modules.Single(module => IsFilter(FileName(module.FilePath)));
        var provider = observation.Modules.Single(module => FileName(module.FilePath).Equals("qcramdisk.sys", StringComparison.OrdinalIgnoreCase));
        if (filter.FileError is not null || provider.FileError is not null ||
            !ValidHash(filter.FileSha256) || !ValidHash(provider.FileSha256))
            throw new InvalidDataException("A loaded QueueCache driver file could not be hashed.");
        if (!filter.FileSha256!.Equals(test.ExpectedFilterSha256, StringComparison.OrdinalIgnoreCase) ||
            !provider.FileSha256!.Equals(test.ExpectedProviderSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Loaded driver file hashes do not match planned build {test.Build} for case {test.Id}.");
    }

    private static string[] Identity(LoadedDriverObservation observation) => observation.Modules
        .OrderBy(module => FileName(module.FilePath), StringComparer.OrdinalIgnoreCase)
        .Select(module => $"{FileName(module.FilePath)}|{module.FilePath.Replace('\\', '/')}|{module.FileSha256}")
        .ToArray();

    private static string FileName(string path) => path.Replace('\\', '/').Split('/').Last();
    private static bool IsFilter(string name) => name.Equals("qcachelab.sys", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("QueueCache-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".sys", StringComparison.OrdinalIgnoreCase);
    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
public sealed record TreePhaseIoAttempts(ulong Read, ulong Write, ulong Flush);
public sealed record TreeDecisionAttemptDeltas(TreePhaseIoAttempts Create, TreePhaseIoAttempts CreateSettle,
    TreePhaseIoAttempts Copy, TreePhaseIoAttempts CopySettle, TreePhaseIoAttempts Read, TreePhaseIoAttempts Delete);

/// <summary>Contract for the same-build file-tree experiment. It validates coverage and identity; it does not assign a speed verdict.</summary>
public static class TreeDecisionEvidenceContract
{
    public static void Validate(TreeDecisionEvidence evidence)
    {
        var test = evidence.Case;
        var before = evidence.Before;
        var after = evidence.After;
        var tree = evidence.Tree;
        var initial = before.State;
        var final = after.State;
        ValidateStart(test, before);
        if (after.Diagnostics.CopyFlags != before.Diagnostics.CopyFlags || final.Instance != initial.Instance ||
            final.Generation != initial.Generation || final.Errors != initial.Errors || final.LastError != 0 ||
            final.Faulted || final.Suspended || final.Removed || after.Performance.TimingEnabled != 0)
            throw new InvalidDataException("Tree-decision cache identity, health, copy flags or timing changed during the measurement.");
        if (before.Diagnostics.Attribution is null || after.Diagnostics.Attribution is null)
            throw new InvalidDataException("Tree-decision lower-I/O attempt counters are unavailable; missing counters are not zero.");
        ValidatePhase(tree.BeforeCreateCache, tree.AfterCreateCache, initial.Instance);
        ValidatePhase(tree.AfterCreateCache, tree.AfterCreateSettleCache, initial.Instance);
        ValidatePhase(tree.BeforeCopyCache, tree.AfterCopyCache, initial.Instance);
        ValidatePhase(tree.AfterCopyCache, tree.AfterCopySettleCache, initial.Instance);
        ValidatePhase(tree.BeforeReadCache, tree.AfterReadCache, initial.Instance);
        ValidatePhase(tree.BeforeDeleteCache, tree.AfterDeleteCache, initial.Instance);

        var spec = ComparisonData.TreeSpec();
        var expectedFiles = spec.Count;
        var expectedBytes = spec.Sum(file => (long)file.Bytes);
        if (!tree.HashVerified || tree.Files != expectedFiles || tree.Bytes != expectedBytes ||
            !PositiveFinite(tree.CreateSeconds) || !PositiveFinite(tree.CopySeconds) ||
            !PositiveFinite(tree.ReadSeconds) || !PositiveFinite(tree.DeleteSeconds))
            throw new InvalidDataException("Tree-decision did not complete the exact seeded file-count, byte-count and hash oracle.");
        ValidateSettle(tree.AfterCreate, 120);
        ValidateSettle(tree.AfterCopy, 120);
        ValidateMemory(tree.StartMemory, tree.AfterCreateWorkMemory, tree.AfterCreateMemory,
            tree.AfterCopyWorkMemory, tree.AfterCopyMemory, tree.BeforeReadMemory, tree.AfterReadMemory);
        _ = GetAttemptDeltas(evidence);
    }

    public static void ValidateStart(TreeDecisionCase test, CacheLayoutSnapshot before)
    {
        var initial = before.State;
        var options = initial.Options;
        if (string.IsNullOrWhiteSpace(test.Id) || test.Repeat is < 1 or > 3 || test.Position is < 1 or > 5 ||
            initial.Enabled != test.CacheEnabled || initial.Faulted || initial.Suspended || initial.Removed ||
            initial.LastError != 0 || initial.Instance == 0 || initial.DirtyBytes != 0 || initial.InFlightBytes != 0 ||
            options is null || options.Drain != DrainAlgorithm.Idle || !options.RetainWrites || !options.PromoteOnRead ||
            options.BatchKiB != test.BatchKiB || options.Parallelism != test.Parallelism || before.Performance.TimingEnabled != 0)
            throw new InvalidDataException("Tree-decision case did not start from its declared clean, timing-off cache configuration.");
        if (test.CopyFlags is { } expectedFlags && before.Diagnostics.CopyFlags != expectedFlags)
            throw new InvalidDataException("Tree-decision copy flags do not match the declared case.");
        if (before.Diagnostics.Attribution is null)
            throw new InvalidDataException("Tree-decision lower-I/O attempt counters are unavailable; missing counters are not zero.");
    }

    private static bool PositiveFinite(double value) => value > 0 && double.IsFinite(value);

    public static void ValidateSettle(SettleResult? settle, int minimumSeconds = 0)
    {
        if (settle is not { IdleObserved: true, DiskBytesWritten: not null, Source: "IOCTL_DISK_PERFORMANCE" } ||
            !PositiveFinite(settle.Seconds) || settle.Seconds < minimumSeconds)
            throw new InvalidDataException("Tree-decision requires a recorded disk-counter settle with observed idle after create and copy.");
    }

    private static void ValidatePhase(TreeCacheSnapshot? before, TreeCacheSnapshot? after, ulong instance)
    {
        if (before is null || after is null)
            throw new InvalidDataException("Tree-decision is missing a phase-specific cache snapshot.");
        var start = before.State;
        var end = after.State;
        var lowerBefore = before.Diagnostics.Attribution;
        var lowerAfter = after.Diagnostics.Attribution;
        if (start.Instance != instance || end.Instance != instance || start.Generation != end.Generation ||
            start.Errors != end.Errors || end.LastError != 0 || end.Faulted || end.Suspended || end.Removed ||
            before.Performance.TimingEnabled != 0 || after.Performance.TimingEnabled != 0 ||
            before.Diagnostics.CopyFlags != after.Diagnostics.CopyFlags ||
            lowerBefore is null || lowerAfter is null ||
            lowerAfter.LowerReadAttempts < lowerBefore.LowerReadAttempts ||
            lowerAfter.LowerWriteAttempts < lowerBefore.LowerWriteAttempts ||
            lowerAfter.LowerFlushAttempts < lowerBefore.LowerFlushAttempts)
            throw new InvalidDataException("Tree-decision phase cache identity, health, timing or lower-I/O counters are incomplete or inconsistent.");
    }

    public static TreeDecisionAttemptDeltas GetAttemptDeltas(TreeDecisionEvidence evidence) => new(
        Delta(evidence.Tree.BeforeCreateCache, evidence.Tree.AfterCreateCache),
        Delta(evidence.Tree.AfterCreateCache, evidence.Tree.AfterCreateSettleCache),
        Delta(evidence.Tree.BeforeCopyCache, evidence.Tree.AfterCopyCache),
        Delta(evidence.Tree.AfterCopyCache, evidence.Tree.AfterCopySettleCache),
        Delta(evidence.Tree.BeforeReadCache, evidence.Tree.AfterReadCache),
        Delta(evidence.Tree.BeforeDeleteCache, evidence.Tree.AfterDeleteCache));

    private static TreePhaseIoAttempts Delta(TreeCacheSnapshot? before, TreeCacheSnapshot? after)
    {
        var first = before?.Diagnostics.Attribution ?? throw new InvalidDataException("Tree-decision lower-I/O start counters are missing.");
        var last = after?.Diagnostics.Attribution ?? throw new InvalidDataException("Tree-decision lower-I/O end counters are missing.");
        if (last.LowerReadAttempts < first.LowerReadAttempts || last.LowerWriteAttempts < first.LowerWriteAttempts ||
            last.LowerFlushAttempts < first.LowerFlushAttempts)
            throw new InvalidDataException("Tree-decision lower-I/O attempt counters moved backwards.");
        return new(last.LowerReadAttempts - first.LowerReadAttempts, last.LowerWriteAttempts - first.LowerWriteAttempts,
            last.LowerFlushAttempts - first.LowerFlushAttempts);
    }

    private static void ValidateMemory(params MemorySnapshot?[] samples)
    {
        if (samples.Any(sample => sample is null) || samples.Select(sample => sample!.Utc).Zip(samples.Skip(1).Select(sample => sample!.Utc),
                (left, right) => right >= left).Any(ordered => !ordered))
            throw new InvalidDataException("Tree-decision requires ordered memory and system-cache samples for every tree phase.");
    }
}
