using QueueCache.Operations;
using QueueCache.Management;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Developer.Verification;

public enum ManagedLifecycleTransition { Restart, ColdStart, FastStartup, Sleep, Hibernate, BrokerRestart, BrokerCrash }
public sealed record ManagedLifecycleFixture(string Role, ManagedDiskRecord Before, string SessionSha256, string? CommittedSha256 = null);
public sealed record ManagedLifecycleManifest(int Version, Guid ProofId, string Machine, Guid StartupSession,
    DiskTarget Host, ulong BaselineReservedBytes, DateTimeOffset PreparedAt, ManagedLifecycleFixture[] Fixtures,
    ManagedBrokerObservation Broker);

/// <summary>Pure lifecycle acceptance rules; neither uptime nor a service PID substitutes for the native startup epoch.</summary>
public static class ManagedLifecycleEvidence
{
    public static readonly string[] Roles = ["ram-auto", "ram-manual", "ram-stopped-auto", "backed-auto", "image-auto"];
    public static bool IsNewStartup(ManagedLifecycleTransition transition) => transition is
        ManagedLifecycleTransition.Restart or ManagedLifecycleTransition.ColdStart or ManagedLifecycleTransition.FastStartup;
    public static void Validate(ManagedLifecycleManifest manifest)
    {
        if (manifest.Version != 1 || manifest.ProofId == Guid.Empty || string.IsNullOrWhiteSpace(manifest.Machine) ||
            manifest.StartupSession == Guid.Empty || manifest.Host is null || manifest.PreparedAt == default ||
            manifest.Broker?.IsRunning != true ||
            manifest.Fixtures is null || manifest.Fixtures.Length != Roles.Length ||
            manifest.Fixtures.Any(f => f is null || f.Before is null) ||
            !manifest.Fixtures.Select(f => f.Role).Order().SequenceEqual(Roles.Order()) ||
            manifest.Fixtures.Select(f => f.Before.ResourceId).Distinct().Count() != Roles.Length)
            throw new InvalidDataException("Incomplete or unsupported managed lifecycle manifest.");
        foreach (var fixture in manifest.Fixtures)
        {
            fixture.Before.Validate(); var record = fixture.Before; var runtime = record.Runtime;
            var mode = fixture.Role == "backed-auto" ? ManagedDiskMode.CachedVhdx : fixture.Role == "image-auto" ? ManagedDiskMode.ImageInRam : ManagedDiskMode.EphemeralRam;
            if (record.Definition.Mode != mode || record.Definition.StartAtBoot != (fixture.Role != "ram-manual") ||
                record.StartupSession != manifest.StartupSession || runtime is null || runtime.BootEpoch == Guid.Empty || runtime.CreationGeneration == 0 ||
                runtime.State != (fixture.Role == "ram-stopped-auto" ? ManagedDiskState.Stopped : ManagedDiskState.Ready) ||
                !Hash(fixture.SessionSha256) || (mode == ManagedDiskMode.ImageInRam && (!Hash(fixture.CommittedSha256) || record.CommittedImage?.Digest is null)) ||
                (mode != ManagedDiskMode.ImageInRam && fixture.CommittedSha256 is not null))
                throw new InvalidDataException("Invalid managed lifecycle fixture ownership or byte oracle.");
            if (runtime.State == ManagedDiskState.Ready && (record.VolumePath is null || record.PhysicalDiskNumber is null ||
                (mode != ManagedDiskMode.CachedVhdx && record.Native is null)))
                throw new InvalidDataException("A lifecycle fixture lacks its actual live native/volume identity.");
        }
    }
    public static void ValidateTransition(ManagedLifecycleManifest manifest, Guid currentStartup, ManagedLifecycleTransition transition)
    {
        Validate(manifest);
        if (!Enum.IsDefined(transition) || currentStartup == Guid.Empty ||
            (manifest.StartupSession != currentStartup) != IsNewStartup(transition))
            throw new IOException("The native Windows startup epoch contradicts the requested transition. No lifecycle acceptance or cleanup is permitted.");
    }
    public static bool ExpectStopped(ManagedLifecycleFixture fixture, ManagedLifecycleTransition transition) =>
        IsNewStartup(transition) ? fixture.Role == "ram-manual" : fixture.Role == "ram-stopped-auto";
    public static void ValidateRecord(ManagedLifecycleFixture fixture, ManagedDiskRecord current, Guid startup, ManagedLifecycleTransition transition)
    {
        current.Validate();
        if (current.Definition != fixture.Before.Definition || current.StartupSession != startup || current.Runtime is null)
            throw new IOException("A lifecycle resource definition/startup identity changed or is missing.");
        var stopped = ExpectStopped(fixture, transition);
        if (current.Runtime.State != (stopped ? ManagedDiskState.Stopped : ManagedDiskState.Ready) ||
            stopped && (current.Native is not null || current.PhysicalDiskNumber is not null || current.VolumePath is not null) ||
            !stopped && (current.VolumePath is null || current.PhysicalDiskNumber is null ||
                current.Definition.Mode != ManagedDiskMode.CachedVhdx && (current.Native is null || (current.Native.Flags & RamDiskFlags.Published) == 0)))
            throw new IOException("The fixture is not in its expected live/stopped state; errors are not retried as transient drains.");
        if (!IsNewStartup(transition))
        {
            current.Runtime.CheckExpected(fixture.Before.Runtime!.BootEpoch, fixture.Before.Runtime.CreationGeneration);
            if (fixture.Before.Native is not null)
                (current.Native ?? throw new IOException("Same-session native RAM disappeared.")).RequireSameCreation(fixture.Before.Native);
        }
        else if (!stopped && current.Definition.Mode != ManagedDiskMode.CachedVhdx && fixture.Before.Native is not null &&
            current.Native?.BootEpoch == fixture.Before.Native.BootEpoch && current.Native.CreationGeneration == fixture.Before.Native.CreationGeneration)
            throw new IOException("A new Windows startup reused the preceding volatile RAM creation.");
        if (current.Definition.Mode == ManagedDiskMode.ImageInRam && current.CommittedImage != fixture.Before.CommittedImage)
            throw new IOException("Lifecycle handling changed the committed checkpoint pointer.");
        if (current.Definition.Mode == ManagedDiskMode.CachedVhdx &&
            (current.OriginalSource is null || fixture.Before.OriginalSource is null || !current.OriginalSource.SameImage(fixture.Before.OriginalSource)))
            throw new IOException("The backing image identity changed across the lifecycle transition.");
    }
    private static bool Hash(string? hash) => hash?.Length == 64 && hash.All(Uri.IsHexDigit);
}
