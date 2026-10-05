using System.Reflection;
using System.Runtime.InteropServices;
using QueueCache.Management;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;

internal static class ManagedDiskTests
{
    private const ulong MiB = ManagedDiskDefinition.MiB;
    public static async Task RunAsync()
    {
        DefinitionsAndStartup();
        ProviderRepair();
        ReservationHeadroom();
        ProviderTrimEvidence();
        ProviderAllocationEvidence();
        CheckpointRetentionEvidence();
        ProductCliOwnership();
        AttachedStartupOwnership();
        NativeImageAbi();
        NativeRamAbi();
        RamDirectContracts();
        DurableCatalog();
        await LogicalTransfers();
        await CreationTransactions();
        await ManagedCheckpointTests.RunAsync();
        await ManagedBrokerTests.RunAsync();
        await ManagedLayoutTests.RunAsync();
        Console.WriteLine("Managed-disk contracts passed (no driver or real disk access).");
    }

    private static void RamDirectContracts()
    {
        // Access is a RAM-backed setting; VHDX-backed disks use their cache.
        (Definition(ManagedDiskMode.EphemeralRam) with { Access = RamAccess.Direct }).Validate();
        (Definition(ManagedDiskMode.ImageInRam) with { Access = RamAccess.Direct }).Validate();
        Throws<ArgumentException>(() => (Definition(ManagedDiskMode.CachedVhdx) with { Access = RamAccess.Direct }).Validate());
        Throws<ArgumentException>(() => (Definition(ManagedDiskMode.EphemeralRam) with { Access = (RamAccess)7 }).Validate());

        var bind = RamDirectState.BindRequest(readsOnly: true);
        Check(bind.Length == RamDirectState.BindWireSize && BitConverter.ToUInt32(bind, 0) == 16 && BitConverter.ToUInt32(bind, 4) == 1 &&
            BitConverter.ToUInt32(bind, 8) == RamDirectState.BindReadsOnly, "bind request matches QC_RAM_DIRECT_BIND");
        Check(RamDirectState.StateIoctl == 0x88443460 && RamDirectState.BindIoctl == 0x8844F464, "Direct controls match the driver's CTL_CODEs");

        var wire = new byte[RamDirectState.WireSize];
        BitConverter.TryWriteBytes(wire.AsSpan(0), 160u); BitConverter.TryWriteBytes(wire.AsSpan(4), 1u);
        BitConverter.TryWriteBytes(wire.AsSpan(8), 1u); BitConverter.TryWriteBytes(wire.AsSpan(12), (uint)RamDirectReason.Snapshot);
        var resource = Guid.NewGuid(); resource.TryWriteBytes(wire.AsSpan(24));
        BitConverter.TryWriteBytes(wire.AsSpan(40), 1UL << 20); BitConverter.TryWriteBytes(wire.AsSpan(48), 63UL << 20);
        BitConverter.TryWriteBytes(wire.AsSpan(56), 5UL); BitConverter.TryWriteBytes(wire.AsSpan(88), 2UL);
        System.Text.Encoding.Unicode.GetBytes(@"\Driver\snapman").CopyTo(wire, 96);
        var state = RamDirectState.Decode(wire);
        Check(state.Access == RamDirectAccess.Reads && state.Reason == RamDirectReason.Snapshot && state.ResourceId == resource &&
            state.OffsetBytes == 1UL << 20 && state.LengthBytes == 63UL << 20 && state.ReadRequests == 5 && state.DeclinedRequests == 2 &&
            state.Driver == @"\Driver\snapman" && !state.Full && state.Describe().Contains("shadow copy"), "Direct state decodes at the native offsets");
        var writesOnly = (byte[])wire.Clone(); writesOnly[8] = 2;
        Throws<InvalidDataException>(() => RamDirectState.Decode(writesOnly)); // Writes never without reads.
        var unknownReason = (byte[])wire.Clone(); unknownReason[12] = 99;
        Throws<InvalidDataException>(() => RamDirectState.Decode(unknownReason));
        Throws<InvalidDataException>(() => RamDirectState.Decode(wire.AsSpan(0, 159)));
    }

    private static void ReservationHeadroom()
    {
        foreach (var capacity in new[] { 16UL << 20, 17UL << 20, 64UL << 20, 128UL << 30 })
        {
            var estimate = RamDiskSnapshot.EstimateReservationBytes(capacity);
            Check(estimate > capacity + capacity / 512 + (1UL << 20),
                "reservation headroom includes a PFN for every page, slab descriptors and control overhead");
        }
        foreach (var invalid in new[] { 0UL, (16UL << 20) - 1, (128UL << 30) + (1UL << 20), ulong.MaxValue })
            Throws<ArgumentOutOfRangeException>(() => RamDiskSnapshot.EstimateReservationBytes(invalid));
    }

    private static void ProviderRepair()
    {
        var unbound = new RamProviderRegistration(null, 0, false, true, false);
        Check(unbound.CanRepairUnbound, "proven unbound inactive provider can be repaired");
        Check((unbound with { DevNodeStatus = null, Disconnected = true }).CanRepairUnbound, "disconnected unbound node can be repaired with module-absence proof");
        foreach (var blocked in new[] { unbound with { Service = "qcramdisk" }, unbound with { DevNodeStatus = 8 },
            unbound with { ProviderModuleLoaded = true }, unbound with { ModulesObserved = false }, unbound with { DevNodeStatus = null } })
            Check(!blocked.CanRepairUnbound, "bound, started, loaded or unobserved provider cannot bypass live-RAM preflight");
    }

    private static void AttachedStartupOwnership()
    {
        var cold = new Guid("11223344-5566-7788-0000-000000000000");
        var hybrid1 = new Guid("11223344-5566-7788-0100-000000000000");
        var hybrid2 = new Guid("11223344-5566-7788-0200-000000000000");
        Check(ManagedDiskStartup.IsPreviousHybridStartup(cold, hybrid1, 1) && ManagedDiskStartup.IsPreviousHybridStartup(hybrid1, hybrid2, 2),
            "native hybrid transition proof binds retained images to the immediately preceding kernel startup");
        Check(!ManagedDiskStartup.IsPreviousHybridStartup(cold, hybrid1, 0) &&
            !ManagedDiskStartup.IsPreviousHybridStartup(cold, hybrid2, 2) &&
            !ManagedDiskStartup.IsPreviousHybridStartup(Guid.NewGuid(), hybrid1, 1) &&
            !ManagedDiskStartup.IsPreviousHybridStartup(null, hybrid1, 1),
            "cold, skipped, foreign and unavailable startup identities cannot authorize retained-image detach");
        var definition = ManagedDiskDefinition.New(ManagedDiskMode.CachedVhdx) with
        { CapacityBytes = 64 * MiB, ImagePath = @"T:\Owned\startup.vhdx", StartAtBoot = true };
        var image = new ImageInspection(definition.ImagePath!, "host|exact-file", Guid.NewGuid(), definition.CapacityBytes, MiB, 512, false);
        var runtime = new ManagedDiskRuntime(definition.ResourceId, Guid.NewGuid(), 1, definition.Mode, ManagedDiskState.Ready, 0, null);
        var record = new ManagedDiskRecord(definition, runtime, OriginalSource: image, PhysicalDiskNumber: 7);
        ManagedDiskStartup.RequireAttachedImageIdentity(record, image, 7, definition.CapacityBytes, 512);
        Throws<IOException>(() => ManagedDiskStartup.RequireAttachedImageIdentity(record, image with { FileIdentity = "host|foreign-file" }, 7, definition.CapacityBytes, 512));
        Throws<IOException>(() => ManagedDiskStartup.RequireAttachedImageIdentity(record, image with { DiskId = Guid.NewGuid() }, 7, definition.CapacityBytes, 512));
        Throws<IOException>(() => ManagedDiskStartup.RequireAttachedImageIdentity(record, image, 8, definition.CapacityBytes, 512));
        Throws<IOException>(() => ManagedDiskStartup.RequireAttachedImageIdentity(record, image, 7, definition.CapacityBytes + MiB, 512));
        Throws<IOException>(() => ManagedDiskStartup.RequireAttachedImageIdentity(record, image, 7, definition.CapacityBytes, 4096));
        Throws<IOException>(() => ManagedDiskStartup.RequireAttachedImageIdentity(record with { PhysicalDiskNumber = null }, image, 7, definition.CapacityBytes, 512));
        var stopped = runtime with { State = ManagedDiskState.Stopped };
        Check(ManagedDiskStartup.ShouldStartAfterReconcile(definition, true, stopped) &&
            !ManagedDiskStartup.ShouldStartAfterReconcile(definition, false, stopped) &&
            !ManagedDiskStartup.ShouldStartAfterReconcile(definition with { StartAtBoot = false }, true, stopped),
            "only automatic recipes reopen after a proven new startup, once the owned old attachment is stopped");
    }

    #pragma warning disable CA1416 // Pure acceptance oracle in the Windows-targeted runner assembly; no native calls.
    private static void ProviderTrimEvidence()
    {
        foreach (var sector in new uint[] { 512, 4096 })
        {
            var original = Enumerable.Repeat((byte)17, checked((int)sector * 3)).ToArray();
            var observed = original.ToArray(); observed.AsSpan((int)sector, (int)sector).Clear();
            var before = new RamDiskSnapshot(Guid.NewGuid(), Guid.NewGuid(), 1, 64UL << 20, 10,
                65UL << 20, Guid.Empty, sector, RamDiskFlags.Published, 0, 0, 0, 0, 2, 0, 0);
            var after = before with { Trims = 3, WriteGeneration = 11 };
            QueueCache.Developer.Verification.ManagedProviderEvidence.ValidateTrim(before, after, original, observed, (int)sector, (int)sector);
            foreach (var bad in new[] { after with { Trims = 2 }, after with { WriteGeneration = 10 },
                after with { Errors = 1 }, after with { BootEpoch = Guid.NewGuid() }, after with { CreationGeneration = 2 } })
                Throws<IOException>(() => QueueCache.Developer.Verification.ManagedProviderEvidence.ValidateTrim(before, bad, original, observed, (int)sector, (int)sector));
            foreach (var corrupt in new[] { 0, (int)sector, original.Length - 1 })
            {
                var bad = observed.ToArray(); bad[corrupt] ^= 1;
                Throws<IOException>(() => QueueCache.Developer.Verification.ManagedProviderEvidence.ValidateTrim(before, after, original, bad, (int)sector, (int)sector));
            }
            Throws<InvalidDataException>(() => QueueCache.Developer.Verification.ManagedProviderEvidence.ValidateTrim(before, after, original, observed[..^1], (int)sector, (int)sector));
        }
    }
    #pragma warning restore CA1416

    #pragma warning disable CA1416 // Pure failure acceptance; no native calls.
    private static void CheckpointRetentionEvidence()
    {
        var definition = Definition(ManagedDiskMode.ImageInRam); var epoch = Guid.NewGuid();
        var native = new RamDiskSnapshot(definition.ResourceId, epoch, 1, definition.CapacityBytes, 10,
            definition.CapacityBytes + MiB, Guid.Empty, 512, RamDiskFlags.Published, 0, 0, 0, 0, 0, 0, 0);
        var image = new ManagedImageReference(new(definition.ImagePath!, "owned-file", Guid.NewGuid(), definition.CapacityBytes, MiB, 512, false),
            new(definition.CapacityBytes, new string('A', 64)), 8);
        var before = new ManagedDiskRecord(definition, new(definition.ResourceId, epoch, 1, definition.Mode, ManagedDiskState.Ready, 10, 8, "R:"),
            image, Native: native, PhysicalDiskNumber: 7, VolumePath: "owned-volume", SavedAt: DateTimeOffset.UtcNow, GptDiskId: Guid.NewGuid());
        var after = before with { LastError = "Expected save veto", Native = native with { Flushes = 3, WriteGeneration = 11 },
            Runtime = before.Runtime! with { WriteGeneration = 11 } };
        QueueCache.Developer.Verification.ManagedCheckpointEvidence.RequireRetained(before, after);
        foreach (var bad in new[] { after with { Native = null }, after with { Native = after.Native! with { Flags = RamDiskFlags.Published | RamDiskFlags.Frozen } },
            after with { Native = after.Native! with { Flags = RamDiskFlags.Published | RamDiskFlags.ReadOnly } },
            after with { CommittedImage = image with { Generation = 10 } }, after with { PhysicalDiskNumber = 8 },
            after with { VolumePath = null }, after with { GptDiskId = Guid.NewGuid() }, after with { Runtime = after.Runtime! with { State = ManagedDiskState.RecoveryRequired } },
            after with { Runtime = after.Runtime! with { SavedGeneration = 11 } }, after with { Definition = definition with { Label = "Foreign" } } })
            Throws<IOException>(() => QueueCache.Developer.Verification.ManagedCheckpointEvidence.RequireRetained(before, bad));
    }
    #pragma warning restore CA1416

    #pragma warning disable CA1416 // Pure allocation-boundary acceptance; no native calls.
    private static void ProviderAllocationEvidence()
    {
        var resource = Guid.NewGuid(); var before = new RamDiskAllocationFailureProof(Guid.NewGuid(), Guid.Empty, 0, 0);
        var after = before with { ResourceId = resource, CompletedInjections = 1, AllocatedSlabs = 8 };
        QueueCache.Developer.Verification.ManagedProviderEvidence.ValidateAllocationFailure(before, after, resource, 8, 1024, 1024);
        foreach (var bad in new[] { after with { CompletedInjections = 0 }, after with { CompletedInjections = 2 },
            after with { ResourceId = Guid.NewGuid() }, after with { BootEpoch = Guid.NewGuid() }, after with { AllocatedSlabs = 7 } })
            Throws<IOException>(() => QueueCache.Developer.Verification.ManagedProviderEvidence.ValidateAllocationFailure(before, bad, resource, 8, 1024, 1024));
        Throws<IOException>(() => QueueCache.Developer.Verification.ManagedProviderEvidence.ValidateAllocationFailure(before, after, resource, 8, 1024, 1025));
        var ram = new RamDiskSnapshot(resource, before.BootEpoch, 1, 64UL << 20, 0, 66UL << 20,
            Guid.Empty, 512, RamDiskFlags.None, 0, 0, 0, 0, 0, 0, 0);
        QueueCache.Developer.Verification.ManagedProviderEvidence.ValidateReservation(ram, 1024, 1024 + ram.ReservedBytes);
        var omittedMetadata = ram with { ReservedBytes = 65UL << 20 };
        Throws<IOException>(() => QueueCache.Developer.Verification.ManagedProviderEvidence.ValidateReservation(omittedMetadata, 1024, 1024 + omittedMetadata.ReservedBytes));
        Throws<IOException>(() => QueueCache.Developer.Verification.ManagedProviderEvidence.ValidateReservation(ram, 1024, 1024 + ram.ReservedBytes - 1));
        foreach (var error in new[] { 19, 1117 })
            QueueCache.Developer.Verification.ManagedProviderEvidence.ValidateReadOnlyWrite(true, false, error, 4, 5, 9, 9, true);
        foreach (var bad in new (bool Before, bool During, int? Error, ulong ErrorsAfter, ulong Generation, bool Same)[]
        {
            (false, false, 19, 5, 9, true), (true, true, 19, 5, 9, true), (true, false, null, 5, 9, true), (true, false, 5, 5, 9, true),
            (true, false, 1117, 4, 9, true), (true, false, 1117, 6, 9, true), (true, false, 19, 5, 10, true), (true, false, 19, 5, 9, false)
        })
            Throws<IOException>(() => QueueCache.Developer.Verification.ManagedProviderEvidence.ValidateReadOnlyWrite(
                bad.Before, bad.During, bad.Error, 4, bad.ErrorsAfter, 9, bad.Generation, bad.Same));
    }
    #pragma warning restore CA1416

    #pragma warning disable CA1416 // Pure ownership oracle in the Windows-targeted runner assembly.
    private static void ProductCliOwnership()
    {
        var definition = ManagedDiskDefinition.New(ManagedDiskMode.CachedVhdx) with
        { CapacityBytes = 64UL << 20, Label = "QC-CLI-owned", PreferredLetter = 'R', ImagePath = @"T:\Owned\source.vhdx" };
        var record = new ManagedDiskRecord(definition);
        var fixture = new QueueCache.Developer.Verification.ManagedCliFixture(definition.Mode, definition.Label,
            definition.PreferredLetter, definition.CapacityBytes, definition.SectorBytes, definition.ImagePath, new HashSet<Guid>());
        QueueCache.Developer.Verification.ManagedCliEvidence.RequireOwned(record, fixture);
        foreach (var foreign in new[] { definition with { Label = "Other" }, definition with { PreferredLetter = 'S' },
            definition with { CapacityBytes = 128UL << 20 }, definition with { SectorBytes = 4096 },
            definition with { ImagePath = @"T:\Other\source.vhdx" } })
            Throws<IOException>(() => QueueCache.Developer.Verification.ManagedCliEvidence.RequireOwned(new(foreign), fixture));
        Throws<IOException>(() => QueueCache.Developer.Verification.ManagedCliEvidence.RequireOwned(record,
            fixture with { PreexistingResources = new HashSet<Guid> { record.ResourceId } }));
    }
    #pragma warning restore CA1416

    private static void DurableCatalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "QueueCache-Catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ManagedDiskStore(root);
            Check(store.ReadStartupSession() is null, "first startup has no fabricated coordinator marker");
            var startup = Guid.NewGuid(); store.SaveStartupSession(startup);
            Check(store.ReadStartupSession() == startup, "same-session broker restart recognizes completed startup ownership");
            var startupDefinition = Definition(ManagedDiskMode.EphemeralRam) with { StartAtBoot = true };
            var stopped = new ManagedDiskRuntime(startupDefinition.ResourceId, startup, 1, startupDefinition.Mode, ManagedDiskState.Stopped, 0, null);
            Check(!ManagedDiskStartup.ShouldStartAfterReconcile(startupDefinition, false, stopped), "broker restart cannot resurrect an intentionally stopped automatic-start disk");
            Check(ManagedDiskStartup.ShouldStartAfterReconcile(startupDefinition, true, stopped), "a proven new startup runs the remembered stopped recipe");
            Check(!ManagedDiskStartup.ShouldStartAfterReconcile(startupDefinition, true, stopped with { State = ManagedDiskState.RecoveryRequired }), "startup does not recreate a surviving resource requiring recovery");
            var readyRecord = new ManagedDiskRecord(startupDefinition, stopped with { State = ManagedDiskState.Ready, Volume = "R:" });
            var readyJournal = new ManagedDiskJournal(Guid.NewGuid(), startupDefinition.ResourceId, ManagedDiskJournalStage.Ready, startup, 1);
            Check(ManagedDiskStartup.CanAdoptReady(readyRecord, readyJournal), "completed Ready boundary can be adopted without formatting");
            Check(!ManagedDiskStartup.CanAdoptReady(readyRecord, readyJournal with { Stage = ManagedDiskJournalStage.Formatting }), "interrupted formatting is not relabelled Ready by recovery");
            Check(!ManagedDiskStartup.CanAdoptReady(readyRecord with { Runtime = readyRecord.Runtime! with { Volume = null } }, readyJournal), "initial checkpoint cannot impersonate completed creation publication");
            Check(!ManagedDiskStartup.CanAdoptReady(readyRecord, null), "missing durable lifecycle boundary is unavailable, not Ready");
            var definition = Definition(ManagedDiskMode.ImageInRam);
            var epoch = Guid.NewGuid();
            var runtime = new ManagedDiskRuntime(definition.ResourceId, epoch, 1, definition.Mode, ManagedDiskState.Stopped, 10, 8);
            var previous = new ManagedImageReference(new(@"C:\Images\old.vhdx", "old-id", Guid.NewGuid(), definition.CapacityBytes, MiB, 512, false), new(definition.CapacityBytes, new string('A', 64)), 8);
            var current = previous with { Identity = previous.Identity with { Path = @"C:\Images\new.vhdx", FileIdentity = "new-id" }, Generation = 10 };
            var record = new ManagedDiskRecord(definition, runtime, previous);
            store.Save(record);
            var journal = new ManagedDiskJournal(Guid.NewGuid(), definition.ResourceId, ManagedDiskJournalStage.Exporting, epoch, 1, 10, Previous: previous);
            store.SaveJournal(journal);
            Check(store.Read(definition.ResourceId).CommittedImage == previous, "candidate creation does not change the committed startup source");
            Throws<InvalidDataException>(() => store.SaveJournal(journal with { Stage = ManagedDiskJournalStage.CandidateVerified }));
            store.SaveJournal(journal with { Stage = ManagedDiskJournalStage.CandidateVerified, Candidate = current });
            Check(store.Read(definition.ResourceId).CommittedImage == previous, "verified uncommitted candidate does not silently become startup source");
            store.Save(record with { CommittedImage = current, PreviousImage = previous, Runtime = runtime.RecordSaved(10) });
            Check(store.Read(definition.ResourceId).CommittedImage == current && store.Read(definition.ResourceId).PreviousImage == previous, "pointer commit retains previous verified image");
            var path = Path.Combine(root, definition.ResourceId.ToString("N") + ".resource.json");
            File.WriteAllText(path, "corrupt");
            var recovered = store.Read(definition.ResourceId);
            Check(recovered.CommittedImage == previous && recovered.LastError is not null, "corrupt current catalog recovers predecessor with explicit reconciliation state");
            store.Save(record);
            store.RemoveStopped(definition.ResourceId);
            Check(store.ContainsResource(definition.ResourceId), "retired resource identity cannot be silently reused by a new creation");
            Check(store.List().Count == 0 && store.Read(definition.ResourceId).Removed && !store.Read(definition.ResourceId).Definition.StartAtBoot, "durable removal tombstone prevents recipe resurrection and retains image references");
            Throws<InvalidDataException>(() => store.Save(record with { Native = new(Guid.NewGuid(), epoch, 1, definition.CapacityBytes, 0, 18 * MiB, Guid.Empty, 512, 0, 0, 0, 0, 0, 0, 0, 0) }));
            File.WriteAllBytes(path, new byte[(1 << 20) + 1]); File.WriteAllText(path + ".previous", "also corrupt");
            Throws<AggregateException>(() => store.Read(definition.ResourceId));
        }
        finally { Directory.Delete(root, true); }
    }

    private static ManagedDiskDefinition Definition(ManagedDiskMode mode) => ManagedDiskDefinition.New(mode) with
    {
        CapacityBytes = 16 * MiB,
        ImagePath = mode == ManagedDiskMode.EphemeralRam ? null : @"C:\Images\Source.vhdx",
        CheckpointDirectory = mode == ManagedDiskMode.ImageInRam ? @"C:\Images\Checkpoints" : null
    };

    private static void NativeRamAbi()
    {
        var expected = new RamDiskSnapshot(Guid.NewGuid(), Guid.NewGuid(), 7, 16 * MiB, 19,
            18 * MiB, Guid.Empty, 512, RamDiskFlags.Published, 5, 1024, 2048, 3, 4, 0, 6);
        var wire = RamDiskSnapshot.Request(RamDiskAction.Query, expected);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(wire.AsSpan(80), expected.ReservedBytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(108), (uint)expected.Flags);
        var decoded = RamDiskSnapshot.Decode(wire);
        decoded.RequireSameCreation(expected);
        Check(decoded.WriteGeneration == expected.WriteGeneration && decoded.CapacityBytes == expected.CapacityBytes &&
            decoded.ReservedBytes == expected.ReservedBytes, "RAM ABI retains exact geometry, generations and reservation");
        Throws<IOException>(() => decoded.RequireSameCreation(expected with { BootEpoch = Guid.NewGuid() }));
        Throws<IOException>(() => decoded.RequireSameCreation(expected with { CreationGeneration = 8 }));
        Throws<IOException>(() => decoded.RequireSameCreation(expected with { Slot = 4 }));
        Throws<InvalidDataException>(() => RamDiskSnapshot.Decode(wire[..^1]));
        var badVersion = (byte[])wire.Clone(); badVersion[4] = 1;
        Throws<InvalidDataException>(() => RamDiskSnapshot.Decode(badVersion));
        var badFrozen = (byte[])wire.Clone(); badFrozen[108] |= (byte)RamDiskFlags.Frozen;
        Throws<InvalidDataException>(() => RamDiskSnapshot.Decode(badFrozen));
        var insufficient = (byte[])wire.Clone(); insufficient.AsSpan(80, 8).Clear();
        Throws<InvalidDataException>(() => RamDiskSnapshot.Decode(insufficient));
        Throws<ArgumentException>(() => RamDiskSnapshot.Request(RamDiskAction.Write, expected, transferBytes: RamDiskSnapshot.MaximumTransferBytes + 1));
        Throws<ArgumentException>(() => RamDiskSnapshot.Request(RamDiskAction.Create, resource: Guid.NewGuid(), capacity: 16 * MiB + 512));
        Throws<ArgumentException>(() => RamDiskSnapshot.Request(RamDiskAction.Remove));
        // Direct access is requested only at creation; the reply reports registration.
        var direct = RamDiskSnapshot.Request(RamDiskAction.Create, resource: Guid.NewGuid(), capacity: 16 * MiB, flags: RamDiskFlags.Direct);
        Check(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(direct.AsSpan(108)) == (uint)RamDiskFlags.Direct &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(direct.AsSpan(4)) == RamDiskSnapshot.Version, "Create carries the Direct request in protocol version 2");
        Throws<ArgumentException>(() => RamDiskSnapshot.Request(RamDiskAction.Create, resource: Guid.NewGuid(), capacity: 16 * MiB, flags: RamDiskFlags.ReadOnly));
        Throws<ArgumentException>(() => RamDiskSnapshot.Request(RamDiskAction.SetReadOnly, expected, flags: RamDiskFlags.Direct));
        var registered = (byte[])wire.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(registered.AsSpan(108), (uint)(RamDiskFlags.Published | RamDiskFlags.DirectRegistered));
        Check(RamDiskSnapshot.Decode(registered).Flags.HasFlag(RamDiskFlags.DirectRegistered), "replies report Direct registration");
        var unknownFlag = (byte[])wire.Clone(); unknownFlag[108] |= 0x40;
        Throws<InvalidDataException>(() => RamDiskSnapshot.Decode(unknownFlag));
        Throws<ArgumentException>(() => RamDiskSnapshot.Request(RamDiskAction.Freeze, expected));
        Check(RamDiskSnapshot.Request(RamDiskAction.Read, expected, transferBytes: 4096).Length == RamDiskSnapshot.WireSize + 4096,
            "bounded RAM transfers carry bytes, never user pointers");
        foreach (var boundary in new ulong[] { 1, 8, 16 })
            Check(RamDiskSnapshot.Request(RamDiskAction.DeveloperCreateAllocationFailure, resource: Guid.NewGuid(), capacity: 64 * MiB,
                offset: boundary).Length == RamDiskSnapshot.WireSize, "allocation failure is one bounded creation request without transfer buffers");
        foreach (var invalid in new ulong[] { 0, 17, ulong.MaxValue })
            Throws<ArgumentException>(() => RamDiskSnapshot.Request(RamDiskAction.DeveloperCreateAllocationFailure, resource: Guid.NewGuid(), capacity: 64 * MiB, offset: invalid));
        Throws<ArgumentException>(() => RamDiskSnapshot.Request(RamDiskAction.DeveloperCreateAllocationFailure, expected, capacity: 64 * MiB, offset: 1));
    }

    private static void DefinitionsAndStartup()
    {
        var epoch = Guid.NewGuid();
        foreach (var mode in Enum.GetValues<ManagedDiskMode>())
        {
            var definition = Definition(mode);
            definition.Validate();
            Check(ManagedDiskStartup.Decide(definition, epoch, null) == ManagedDiskStartupAction.LeaveStopped, "startup is independent of recipe creation");
            var enabled = definition with { StartAtBoot = true };
            var expected = mode switch
            {
                ManagedDiskMode.EphemeralRam => ManagedDiskStartupAction.CreateAndFormatEmpty,
                ManagedDiskMode.CachedVhdx => ManagedDiskStartupAction.MountImageAndCache,
                _ => ManagedDiskStartupAction.LoadCommittedImage
            };
            Check(ManagedDiskStartup.Decide(enabled, epoch, null) == expected, "only pure RAM is recreated and formatted at startup");
            foreach (var state in new[] { ManagedDiskState.Ready, ManagedDiskState.Saving, ManagedDiskState.Faulted, ManagedDiskState.RecoveryRequired })
            {
                var live = new ManagedDiskRuntime(definition.ResourceId, epoch, 7, mode, state, 9, 1);
                Check(ManagedDiskStartup.Decide(enabled, epoch, live) == ManagedDiskStartupAction.AdoptLive, "service restart adopts live and dirty devices without formatting");
                Throws<IOException>(() => ManagedDiskStartup.Decide(enabled, Guid.NewGuid(), live));
                Throws<IOException>(() => live.CheckExpected(epoch, 8));
            }
        }
        var ram = Definition(ManagedDiskMode.EphemeralRam);
        ManagedDiskConfiguration.RequireCacheOwner(null, null);
        ManagedDiskConfiguration.RequireCacheOwner(ram.ResourceId, ram.ResourceId);
        Throws<IOException>(() => ManagedDiskConfiguration.RequireCacheOwner(ram.ResourceId, null));
        Throws<IOException>(() => ManagedDiskConfiguration.RequireCacheOwner(ram.ResourceId, Guid.NewGuid()));
        var stoppedRam = new ManagedDiskRecord(ram, new(ram.ResourceId, epoch, 1, ram.Mode, ManagedDiskState.Stopped, 0, null));
        var interrupted = stoppedRam with { Runtime = stoppedRam.Runtime! with { State = ManagedDiskState.RecoveryRequired } };
        Check(ManagedDiskStartup.IsIncompleteCreation(interrupted), "an interrupted creation can use exact attachment cleanup without a nonexistent filesystem binding");
        Check(ManagedDiskStartup.IsIncompleteCreation(interrupted with { Runtime = interrupted.Runtime! with { State = ManagedDiskState.Blocked } }), "failed reconciliation does not make an incomplete owned creation impossible to stop");
        Check(!ManagedDiskStartup.IsIncompleteCreation(interrupted with { GptDiskId = Guid.NewGuid(), VolumePath = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\" }), "complete bindings retain normal Windows volume checks");
        Check(!ManagedDiskStartup.IsIncompleteCreation(stoppedRam), "stopped recipes are not mistaken for surviving incomplete creations");
        var edit = new ManagedDiskRequest(ram.ResourceId, ManagedDiskAction.ConfigureStopped, PreferredLetter: 'S', Label: "Next", CapacityBytes: 32 * MiB);
        var edited = ManagedDiskConfiguration.EditStopped(stoppedRam, edit);
        Check(edited.PreferredLetter == 'S' && edited.Label == "Next" && edited.CapacityBytes == 32 * MiB && edited.ResourceId == ram.ResourceId,
            "stopped pure RAM edits update only the next recipe without creating or formatting storage");
        Throws<IOException>(() => ManagedDiskConfiguration.EditStopped(stoppedRam with { Runtime = stoppedRam.Runtime! with { State = ManagedDiskState.Ready } }, edit));
        Throws<IOException>(() => ManagedDiskConfiguration.EditStopped(stoppedRam with { PhysicalDiskNumber = 4 }, edit));
        Throws<ArgumentException>(() => ManagedDiskConfiguration.EditStopped(stoppedRam, edit with { PreferredLetter = 'C' }));
        var stoppedImage = new ManagedDiskRecord(Definition(ManagedDiskMode.ImageInRam), new(ram.ResourceId, epoch, 1, ManagedDiskMode.ImageInRam, ManagedDiskState.Stopped, 0, null));
        Throws<NotSupportedException>(() => ManagedDiskConfiguration.EditStopped(stoppedImage, edit));
        Throws<ArgumentException>(() => (ram with { ImagePath = @"C:\Disk.vhdx" }).Validate());
        Throws<ArgumentException>(() => (ram with { Source = ManagedDiskSource.OpenExisting }).Validate());
        Throws<ArgumentException>(() => (ram with { Cache = new CacheConfiguration(4) }).Validate());
        Throws<ArgumentException>(() => (ram with { SaveBeforeStopping = true }).Validate());
        Throws<ArgumentException>(() => (ram with { CapacityBytes = 16 * MiB + 512 }).Validate());
        Throws<ArgumentException>(() => (ram with { PreferredLetter = 'C' }).Validate());
        Throws<ArgumentException>(() => (ram with { SchemaVersion = 2 }).Validate());
        var backed = Definition(ManagedDiskMode.CachedVhdx) with { Cache = new CacheConfiguration(1, CachePreset.Strict) };
        backed.Validate(); // Reuse the normal cache contract; memory need not equal disk capacity.
        Throws<ArgumentException>(() => (backed with { Cache = backed.Cache! with { Preset = CachePreset.Fast } }).Validate());
        (backed with { Cache = backed.Cache! with { Preset = CachePreset.Fast }, AcceptVolatileWrites = true }).Validate();
        var image = Definition(ManagedDiskMode.ImageInRam) with { Source = ManagedDiskSource.OpenExisting, CapacityBytes = 64UL << 30 };
        new ImageInspection(image.ImagePath!, "source-id", Guid.NewGuid(), image.CapacityBytes, 3UL << 30, 512, false).ValidateFor(image);
        Throws<InvalidDataException>(() => new RamReservationQuote(3UL << 30, MiB, MiB).Validate(image, 80UL << 30));
        Throws<InvalidDataException>(() => new RamReservationQuote(image.CapacityBytes, MiB, MiB).Validate(image, image.CapacityBytes));
        new RamReservationQuote(image.CapacityBytes, MiB, MiB).Validate(image, 80UL << 30);
        Throws<OverflowException>(() => _ = new RamReservationQuote(ulong.MaxValue, MiB, MiB).TotalBytes);
        Throws<InvalidDataException>(() => new ImageInspection(image.ImagePath!, "id", Guid.NewGuid(), image.CapacityBytes, 3UL << 30, 512, true).ValidateFor(image));
        foreach (var path in new[] { @"\\server\share\disk.vhdx", @"C:\Images\..\disk.vhdx", @"C:\disk.vhdx:stream", "disk.vhdx" })
            Throws<ArgumentException>(() => ManagedDiskPaths.ValidateImagePath(path));
        ManagedDiskPaths.ValidateImagePath(@"C:\Images\A's $disk.vhdx");
        var runtime = new ManagedDiskRuntime(image.ResourceId, epoch, 1, image.Mode, ManagedDiskState.Ready, 12, 8);
        Check(runtime.RecordSaved(10).HasUnsavedChanges, "writes after freeze remain dirty after saving the frozen generation");
        Check(!runtime.RecordSaved(12).HasUnsavedChanges, "matching checkpoint generation is clean");
        Throws<InvalidDataException>(() => runtime.RecordSaved(13));
    }

    private static void NativeImageAbi()
    {
        // Layout only; never invokes Windows functions on the host.
#pragma warning disable CA1416
        var native = typeof(WindowsVirtualDisk);
#pragma warning restore CA1416
        Type Nested(string name) => native.GetNestedType(name, BindingFlags.NonPublic)!;
        Check(Marshal.SizeOf(Nested("VirtualStorage")) == 20, "VIRTUAL_STORAGE_TYPE ABI");
        Check(Marshal.SizeOf(Nested("OpenParameters")) == 28, "OPEN_VIRTUAL_DISK_PARAMETERS v2 ABI");
        if (IntPtr.Size == 8)
        {
            var create = Nested("CreateParameters");
            Check(Marshal.SizeOf(create) == 128 && Marshal.OffsetOf(create, "Id").ToInt32() == 8 &&
                Marshal.OffsetOf(create, "MaximumSize").ToInt32() == 24 && Marshal.OffsetOf(create, "Parent").ToInt32() == 48 &&
                Marshal.OffsetOf(create, "Resiliency").ToInt32() == 108, "CREATE_VIRTUAL_DISK_PARAMETERS x64 v2 ABI");
        }
    }

    private static async Task LogicalTransfers()
    {
        var source = new MemoryDisk(32 * 512) { MaxRead = 512 };
        // Includes sparse zero sectors, an initial layout header and a backup header at the very end.
        source.Bytes[0] = 0xA5; source.Bytes[7000] = 42; source.Bytes[^1] = 0x5A;
        var destination = new MemoryDisk(source.Bytes.Length);
        Array.Fill(destination.Bytes, (byte)0xFF);
        var progress = new CaptureProgress<ImageTransferProgress>();
        var digest = await LogicalImageTransfer.CopyAsync(source, destination, progress, chunkBytes: 4096);
        Check(source.Bytes.SequenceEqual(destination.Bytes) && destination.Flushed, "all logical sectors, including zero regions and tail, are copied and flushed");
        Check(progress.Items.Last() == new ImageTransferProgress(source.CapacityBytes, source.CapacityBytes), "transfer reports complete virtual capacity");
        await LogicalImageTransfer.VerifyAsync(destination, digest);
        destination.Bytes[^1] ^= 1;
        await ThrowsAsync<InvalidDataException>(() => LogicalImageTransfer.VerifyAsync(destination, digest));
        await ThrowsAsync<InvalidDataException>(() => LogicalImageTransfer.CopyAsync(source, new MemoryDisk(512)));
        source.ReadFailureAt = 8192;
        destination.Flushed = false;
        await ThrowsAsync<IOException>(() => LogicalImageTransfer.CopyAsync(source, destination, chunkBytes: 4096));
        Check(!destination.Flushed, "read failures cannot report a complete image");
        source.ReadFailureAt = null; source.MaxRead = 0;
        await ThrowsAsync<InvalidDataException>(() => LogicalImageTransfer.CopyAsync(source, destination));
        source.MaxRead = 511;
        await ThrowsAsync<InvalidDataException>(() => LogicalImageTransfer.CopyAsync(source, destination));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => LogicalImageTransfer.CopyAsync(new MemoryDisk(512), new MemoryDisk(512), token: cancel.Token));
    }

    private static async Task CreationTransactions()
    {
        var countedDisk = new MemoryDisk(16 << 20) { MaxRead = 512 };
        var measurements = new ManagedImageIo(); var measuredDisk = measurements.Measure(countedDisk);
        await measuredDisk.ReadAsync(0, new byte[4096], default);
        await measuredDisk.WriteAsync(0, new byte[512], default); await measuredDisk.FlushAsync(default);
        var counted = measurements.Snapshot();
        Check(counted.ObservationEpoch != Guid.Empty && counted.ReadAttempts == 1 && counted.ReadBytes == 512 &&
            counted.WriteAttempts == 1 && counted.WrittenBytes == 512 && counted.FlushAttempts == 1,
            "image counters record actual short completion bytes separately from attempts");
        countedDisk.ReadFailureAt = 0;
        await ThrowsAsync<IOException>(async () => { await measuredDisk.ReadAsync(0, new byte[512], default); });
        Check(measurements.Snapshot().ReadAttempts == 2 && measurements.Snapshot().ReadBytes == 512,
            "failed image attempts are counted without inventing completed bytes");
        using (var cancelledRead = new CancellationTokenSource())
        {
            cancelledRead.Cancel();
            await ThrowsAsync<OperationCanceledException>(async () => { await measuredDisk.ReadAsync(0, new byte[512], cancelledRead.Token); });
            Check(measurements.Snapshot().ReadAttempts == 2, "pre-cancelled image operation makes no lower attempt");
        }
        Check(new ManagedImageIo().Snapshot().ObservationEpoch != counted.ObservationEpoch,
            "a new observation epoch cannot silently combine counters across broker restarts");
        var blank = new MemoryDisk(16 << 20) { MaxRead = 4096 };
        await ManagedImageLayout.RequireBlankAsync(blank);
        blank.Bytes[^1] = 1;
        await ThrowsAsync<NotSupportedException>(() => ManagedImageLayout.RequireBlankAsync(blank));
        blank.Bytes[^1] = 0; blank.Bytes[512] = 1;
        await ThrowsAsync<NotSupportedException>(() => ManagedImageLayout.RequireBlankAsync(blank));
        blank.Bytes[512] = 0; blank.ReadFailureAt = 8 * MiB;
        await ThrowsAsync<IOException>(() => ManagedImageLayout.RequireBlankAsync(blank));
        foreach (var mode in Enum.GetValues<ManagedDiskMode>())
        {
            var definition = Definition(mode);
            var backend = new FakeBackend(definition);
            var result = await new ManagedDiskCreationCoordinator(backend).CreateAsync(definition);
            Check(result.State == ManagedDiskState.Ready && backend.Formatted && backend.Published && backend.Disposed && !backend.Aborted, "common new-disk creation and ownership transfer for " + mode);
            Check(backend.InitialSaved == (mode == ManagedDiskMode.ImageInRam), "initial image save only for full-image mode");
        }
        var existing = Definition(ManagedDiskMode.ImageInRam) with { Source = ManagedDiskSource.OpenExisting };
        var import = new FakeBackend(existing);
        var runtime = await new ManagedDiskCreationCoordinator(import).CreateAsync(existing);
        Check(!import.Formatted && !import.InitialSaved && import.ImportDisposed && import.CopyCompleteAtPublication, "existing image is completely loaded and detached before publication, never formatted");
        Check(runtime.SavedGeneration == 4 && runtime.WriteGeneration == 5 && runtime.HasUnsavedChanges, "post-publication writes are not included in the loaded image baseline");
        var cached = Definition(ManagedDiskMode.CachedVhdx) with { Source = ManagedDiskSource.OpenExisting };
        var backing = new FakeBackend(cached);
        await new ManagedDiskCreationCoordinator(backing).CreateAsync(cached);
        Check(!backing.Formatted && !backing.OpenedImport, "mounted image cache preserves existing content without full-image transfer");
        foreach (var candidate in new[] { existing, cached })
        {
            var rawBackend = new FakeBackend(candidate);
            var rawDefinition = candidate with { InitializeBlankImage = true, ExpectedBlankImage = rawBackend.Inspection };
            await new ManagedDiskCreationCoordinator(rawBackend).CreateAsync(rawDefinition);
            Check(rawBackend.Formatted && rawBackend.Published && rawBackend.InitialSaved == (candidate.Mode == ManagedDiskMode.ImageInRam),
                "explicit blank-image initialization formats and commits only the selected storage mode");
            var stale = rawDefinition with { ExpectedBlankImage = rawBackend.Inspection with { FileIdentity = "replaced" } };
            var staleBackend = new FakeBackend(candidate);
            await ThrowsAsync<IOException>(() => new ManagedDiskCreationCoordinator(staleBackend).CreateAsync(stale));
            Check(!staleBackend.Allocated, "stale initialization consent is refused before attachment or allocation");
            Throws<ArgumentException>(() => (rawDefinition with { ExpectedBlankImage = null }).Validate());
            Throws<ArgumentException>(() => (rawDefinition with { ReadOnly = true }).Validate());
        }
        var unavailable = new FakeBackend(existing) { Available = false };
        await ThrowsAsync<NotSupportedException>(() => new ManagedDiskCreationCoordinator(unavailable).CreateAsync(existing));
        Check(!unavailable.Allocated, "capability refusal happens before memory allocation");
        var changed = new FakeBackend(existing) { ChangeSource = true };
        await ThrowsAsync<IOException>(() => new ManagedDiskCreationCoordinator(changed).CreateAsync(existing));
        Check(changed.Aborted && changed.ImportDisposed && !changed.Published, "changed source is refused with owned cleanup");
        var failed = new FakeBackend(existing) { FailImport = true };
        await ThrowsAsync<IOException>(() => new ManagedDiskCreationCoordinator(failed).CreateAsync(existing));
        Check(failed.Aborted && !failed.Published && failed.ImportDisposed, "incomplete import is never published");
        using var cancel = new CancellationTokenSource();
        var cancelled = new FakeBackend(existing) { CancelOnImport = cancel };
        await ThrowsAsync<OperationCanceledException>(() => new ManagedDiskCreationCoordinator(cancelled).CreateAsync(existing, token: cancel.Token));
        Check(cancelled.Aborted && cancelled.CleanupTokenWasLive && !cancelled.Published, "cancellation uses a separate live cleanup deadline");
        var recovery = new FakeBackend(existing) { FailImport = true, FailCleanup = true };
        var aggregate = await ThrowsAsync<AggregateException>(() => new ManagedDiskCreationCoordinator(recovery).CreateAsync(existing));
        Check(aggregate.InnerExceptions.Count == 2 && !recovery.Published, "cleanup failure preserves both errors and requires recovery");
    }

    internal sealed class MemoryDisk(int bytes) : ILogicalDisk
    {
        public byte[] Bytes { get; } = new byte[bytes];
        public ulong CapacityBytes => (ulong)Bytes.Length;
        public uint SectorBytes => 512;
        public int MaxRead = int.MaxValue;
        public ulong? ReadFailureAt;
        public bool Flushed;
        public ValueTask<int> ReadAsync(ulong offset, Memory<byte> buffer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (ReadFailureAt is not null && offset >= ReadFailureAt) throw new IOException("Injected owned image read failure.");
            var count = Math.Min(buffer.Length, MaxRead);
            Bytes.AsMemory((int)offset, count).CopyTo(buffer);
            return ValueTask.FromResult(count);
        }
        public ValueTask WriteAsync(ulong offset, ReadOnlyMemory<byte> buffer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); buffer.CopyTo(Bytes.AsMemory((int)offset)); return ValueTask.CompletedTask;
        }
        public ValueTask FlushAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Flushed = true; return ValueTask.CompletedTask; }
    }

    private sealed class FakeBackend(ManagedDiskDefinition definition) : IManagedDiskBackend, IManagedDiskCreation
    {
        public bool Available = true, Allocated, Formatted, Published, Aborted, Disposed, ImportDisposed, InitialSaved,
            OpenedImport, CopyCompleteAtPublication, ChangeSource, FailImport, FailCleanup, CleanupTokenWasLive;
        public CancellationTokenSource? CancelOnImport;
        private readonly MemoryDisk source = new((int)definition.CapacityBytes);
        private readonly MemoryDisk ram = new((int)definition.CapacityBytes);
        private readonly Guid imageId = Guid.NewGuid();
        public Guid ResourceId => definition.ResourceId;
        public Guid BootEpoch { get; } = Guid.NewGuid();
        public ulong CreationGeneration => 1;
        public ulong WriteGeneration => Published ? 5UL : 4UL;
        public ILogicalDisk? RamStorage => ram;
        public ImageInspection Inspection => new(definition.ImagePath!, "stable-id", imageId, definition.CapacityBytes, MiB, 512, false);
        public Task<ManagedDiskCapability> CapabilityAsync(ManagedDiskMode mode, CancellationToken token) => Task.FromResult(new ManagedDiskCapability(mode, Available, "Not qualified."));
        public Task<ImageInspection> InspectAsync(string path, CancellationToken token) => Task.FromResult(Inspection);
        public Task<IImageReadView> OpenImportAsync(ImageInspection expected, CancellationToken token)
        {
            OpenedImport = true; source.Bytes[^1] = 7;
            if (FailImport) source.ReadFailureAt = 8 * MiB;
            CancelOnImport?.Cancel();
            return Task.FromResult<IImageReadView>(new ReadView(this, ChangeSource ? Inspection with { FileIdentity = "replacement-id" } : Inspection));
        }
        public Task<IImageReadView> OpenBlankImportAsync(ImageInspection expected, CancellationToken token)
        { OpenedImport = true; return Task.FromResult<IImageReadView>(new ReadView(this, Inspection)); }
        public Task<IManagedDiskCreation> CreateUnpublishedAsync(ManagedDiskDefinition value, CancellationToken token) { Allocated = true; return Task.FromResult<IManagedDiskCreation>(this); }
        public Task InitializeAndFormatAsync(ManagedDiskDefinition value, CancellationToken token) { Formatted = true; return Task.CompletedTask; }
        public Task SaveInitialImageAsync(ManagedDiskDefinition value, CancellationToken token) { InitialSaved = true; return Task.CompletedTask; }
        public Task<string> PublishAsync(ManagedDiskDefinition value, CancellationToken token)
        {
            if (OpenedImport && !ImportDisposed) throw new Exception("Source staging view remained attached at publication.");
            CopyCompleteAtPublication = source.Bytes.SequenceEqual(ram.Bytes) && ram.Flushed;
            Published = true; return Task.FromResult("R:");
        }
        public Task AbortAsync(CancellationToken token)
        {
            Aborted = true; CleanupTokenWasLive = !token.IsCancellationRequested;
            return FailCleanup ? Task.FromException(new IOException("Cleanup failed.")) : Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        private sealed class ReadView(FakeBackend owner, ImageInspection inspection) : IImageReadView
        {
            public ILogicalDisk LogicalStorage => owner.source;
            public ImageInspection Identity => inspection;
            public ValueTask DisposeAsync() { owner.ImportDisposed = true; return ValueTask.CompletedTask; }
        }
    }
    private sealed class CaptureProgress<T> : IProgress<T>
    {
        public List<T> Items { get; } = [];
        public void Report(T value) => Items.Add(value);
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T exception) { return exception; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
