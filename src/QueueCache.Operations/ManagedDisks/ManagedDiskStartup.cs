namespace QueueCache.Operations.ManagedDisks;

public enum ManagedDiskStartupAction { LeaveStopped, AdoptLive, CreateAndFormatEmpty, MountImageAndCache, LoadCommittedImage }

/// <summary>Receives a proven Windows startup epoch; UI/service PID or uptime is not a boot classifier.</summary>
public static class ManagedDiskStartup
{
    /// <summary>Matches the native v1 startup epoch codec, never a PID/uptime inference.</summary>
    public static bool IsPreviousHybridStartup(Guid? previous, Guid current, ulong transitions)
    {
        if (previous is null || previous == Guid.Empty || current == Guid.Empty || transitions == 0) return false;
        var bytes = current.ToByteArray();
        // QC_STARTUP_EPOCH encodes the authoritative hybrid counter into GUID.Data4.
        // Reverse only the immediately preceding transition of this same kernel epoch.
        for (var i = 0; i < 8; i++) bytes[8 + i] ^= (byte)((transitions ^ (transitions - 1)) >> (i * 8));
        return new Guid(bytes) == previous;
    }

    public static void RequireAttachedImageIdentity(ManagedDiskRecord record, ImageInspection actual,
        int physicalNumber, ulong capacityBytes, uint sectorBytes)
    {
        if (record.Definition.Mode != ManagedDiskMode.CachedVhdx || record.OriginalSource is null || record.Runtime is null ||
            record.PhysicalDiskNumber is null || physicalNumber != record.PhysicalDiskNumber ||
            capacityBytes != record.Definition.CapacityBytes || sectorBytes != record.Definition.SectorBytes ||
            !actual.SameImage(record.OriginalSource))
            throw new IOException("The attached VHDX does not match its remembered image and physical binding.");
    }

    public static bool IsIncompleteCreation(ManagedDiskRecord record) =>
        record.Runtime?.State is ManagedDiskState.Creating or ManagedDiskState.Loading or ManagedDiskState.Formatting or ManagedDiskState.Blocked or ManagedDiskState.Faulted or ManagedDiskState.RecoveryRequired &&
        (record.GptDiskId is null || record.VolumePath is null);
    public static bool ShouldStartAfterReconcile(ManagedDiskDefinition definition, bool newWindowsStartup, ManagedDiskRuntime? runtime) =>
        newWindowsStartup && definition.StartAtBoot && runtime?.State == ManagedDiskState.Stopped;
    public static bool CanAdoptReady(ManagedDiskRecord record, ManagedDiskJournal? journal) =>
        record.Runtime?.State is ManagedDiskState.Ready or ManagedDiskState.Saving or ManagedDiskState.RecoveryRequired &&
        !string.IsNullOrWhiteSpace(record.Runtime.Volume) && journal is not null &&
        (journal.Stage == ManagedDiskJournalStage.Ready || journal.CandidatePath is not null &&
            journal.Stage is ManagedDiskJournalStage.Exporting or ManagedDiskJournalStage.CandidateVerified or ManagedDiskJournalStage.Committed or ManagedDiskJournalStage.RecoveryRequired);
    public static ManagedDiskStartupAction Decide(ManagedDiskDefinition definition, Guid startupEpoch, ManagedDiskRuntime? live)
    {
        definition.Validate();
        if (startupEpoch == Guid.Empty)
            throw new ArgumentException("An authoritative startup epoch is required.");
        if (live is not null)
        {
            if (live.ResourceId != definition.ResourceId || live.Mode != definition.Mode || live.CreationGeneration == 0)
                throw new InvalidDataException("Live managed disk identity does not match its definition.");
            if (live.BootEpoch == startupEpoch && live.State != ManagedDiskState.Stopped)
                return ManagedDiskStartupAction.AdoptLive; // Also retain dirty, frozen and faulted devices for recovery.
            if (live.State != ManagedDiskState.Stopped)
                throw new IOException("A device from another startup is still present; reconcile it before creating another disk.");
        }
        if (!definition.StartAtBoot)
            return ManagedDiskStartupAction.LeaveStopped;
        return definition.Mode switch
        {
            ManagedDiskMode.EphemeralRam => ManagedDiskStartupAction.CreateAndFormatEmpty,
            ManagedDiskMode.CachedVhdx => ManagedDiskStartupAction.MountImageAndCache,
            ManagedDiskMode.ImageInRam => ManagedDiskStartupAction.LoadCommittedImage,
            _ => throw new ArgumentOutOfRangeException(nameof(definition.Mode))
        };
    }
}
