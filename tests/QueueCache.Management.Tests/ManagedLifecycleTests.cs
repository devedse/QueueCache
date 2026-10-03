#pragma warning disable CA1416 // Pure model/acceptance fixtures only; no Windows API is called.
using QueueCache.Developer.Verification;
using QueueCache.Management;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;

internal static class ManagedLifecycleTests
{
    public static void Run()
    {
        var startup = Guid.NewGuid(); var kernel = Guid.NewGuid();
        var host = new DiskTarget('T', 3, 8L << 30, "fixture-host") { DiskBytes = 8L << 30, VolumeId = "{00000000-0000-0000-0000-000000000100}" };
        var fixtures = ManagedLifecycleEvidence.Roles.Select((role, index) =>
        {
            var mode = role == "backed-auto" ? ManagedDiskMode.CachedVhdx : role == "image-auto" ? ManagedDiskMode.ImageInRam : ManagedDiskMode.EphemeralRam;
            var definition = ManagedDiskDefinition.New(mode) with { CapacityBytes = 64UL << 20, PreferredLetter = (char)('R' + index), StartAtBoot = role != "ram-manual",
                ImagePath = mode == ManagedDiskMode.EphemeralRam ? null : @"T:\Images\" + role + ".vhdx", CheckpointDirectory = mode == ManagedDiskMode.ImageInRam ? @"T:\Images\Checkpoints" : null };
            var stopped = role == "ram-stopped-auto";
            var runtime = new ManagedDiskRuntime(definition.ResourceId, mode == ManagedDiskMode.CachedVhdx ? startup : kernel, (ulong)index + 1, mode,
                stopped ? ManagedDiskState.Stopped : ManagedDiskState.Ready, 100, mode == ManagedDiskMode.ImageInRam ? 90UL : null, stopped ? null : definition.PreferredLetter + ":");
            var native = stopped || mode == ManagedDiskMode.CachedVhdx ? null : new RamDiskSnapshot(definition.ResourceId, kernel, runtime.CreationGeneration,
                definition.CapacityBytes, 100, definition.CapacityBytes + (2UL << 20), Guid.Empty, 512, RamDiskFlags.Published, (uint)index, 0, 0, 0, 0, 0, 0);
            var image = mode == ManagedDiskMode.EphemeralRam ? null : new ImageInspection(definition.ImagePath!, "stable-" + role, Guid.NewGuid(), definition.CapacityBytes, 1UL << 20, 512, false);
            var committed = mode == ManagedDiskMode.ImageInRam ? new ManagedImageReference(image!, new(definition.CapacityBytes, new string('A', 64)), 90) : null;
            var record = new ManagedDiskRecord(definition, runtime, committed, OriginalSource: image, Native: native,
                PhysicalDiskNumber: stopped ? null : index + 8, VolumePath: stopped ? null : @"\\?\Volume{" + Guid.NewGuid() + @"}\", StartupSession: startup);
            return new ManagedLifecycleFixture(role, record, new string('B', 64), mode == ManagedDiskMode.ImageInRam ? new string('C', 64) : null);
        }).ToArray();
        var manifest = new ManagedLifecycleManifest(1, Guid.NewGuid(), "fixture-machine", startup, host, 0, DateTimeOffset.UtcNow, fixtures, new(4, 1234, DateTime.UtcNow));
        ManagedLifecycleEvidence.Validate(manifest);
        foreach (var transition in new[] { ManagedLifecycleTransition.Sleep, ManagedLifecycleTransition.Hibernate, ManagedLifecycleTransition.BrokerRestart, ManagedLifecycleTransition.BrokerCrash })
        {
            ManagedLifecycleEvidence.ValidateTransition(manifest, startup, transition);
            foreach (var fixture in fixtures) ManagedLifecycleEvidence.ValidateRecord(fixture, fixture.Before, startup, transition);
            RejectIo(() => ManagedLifecycleEvidence.ValidateTransition(manifest, Guid.NewGuid(), transition));
        }
        foreach (var transition in new[] { ManagedLifecycleTransition.Restart, ManagedLifecycleTransition.ColdStart, ManagedLifecycleTransition.FastStartup })
        {
            var next = Guid.NewGuid(); ManagedLifecycleEvidence.ValidateTransition(manifest, next, transition);
            RejectIo(() => ManagedLifecycleEvidence.ValidateTransition(manifest, startup, transition));
            var auto = fixtures.Single(f => f.Role == "ram-auto");
            RejectIo(() => ManagedLifecycleEvidence.ValidateRecord(auto, auto.Before with { StartupSession = next }, next, transition));
            var manual = fixtures.Single(f => f.Role == "ram-manual");
            Check(ManagedLifecycleEvidence.ExpectStopped(manual, transition) && !ManagedLifecycleEvidence.ExpectStopped(fixtures.Single(f => f.Role == "ram-stopped-auto"), transition),
                "new-startup manual recipe stays stopped while remembered automatic recipe restarts");
        }
        var imageFixture = fixtures.Single(f => f.Role == "image-auto");
        RejectIo(() => ManagedLifecycleEvidence.ValidateRecord(imageFixture, imageFixture.Before with { CommittedImage = imageFixture.Before.CommittedImage! with { Generation = 91 } }, startup, ManagedLifecycleTransition.BrokerRestart));
        RejectIo(() => ManagedLifecycleEvidence.ValidateRecord(fixtures[0], fixtures[0].Before with { Runtime = fixtures[0].Before.Runtime! with { State = ManagedDiskState.RecoveryRequired } }, startup, ManagedLifecycleTransition.Sleep));
        RejectData(() => ManagedLifecycleEvidence.Validate(manifest with { Fixtures = fixtures[..^1] }));
        RejectData(() => ManagedLifecycleEvidence.Validate(manifest with { Fixtures = [fixtures[0], fixtures[0], fixtures[2], fixtures[3], fixtures[4]] }));
        RejectData(() => ManagedLifecycleEvidence.Validate(manifest with { Fixtures = fixtures.Select(f => f with { SessionSha256 = "missing" }).ToArray() }));
        Console.WriteLine("Managed lifecycle epoch/ownership/oracle contracts passed; no power transition performed.");
    }
    private static void Check(bool good, string why) { if (!good) throw new Exception(why); }
    private static void RejectIo(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Invalid lifecycle transition accepted."); }
    private static void RejectData(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Incomplete lifecycle evidence accepted."); }
}
