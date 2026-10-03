using QueueCache.Management;

namespace QueueCache.Operations.ManagedDisks;

public enum ManagedDiskMode { EphemeralRam, CachedVhdx, ImageInRam }
public enum ManagedDiskSource { CreateNew, OpenExisting }
public enum ImageAllocation { Dynamic, Fixed }
public enum ManagedDiskState { Stopped, Creating, Loading, Formatting, Ready, Saving, Stopping, Blocked, Faulted, RecoveryRequired }
public enum ManagedDiskStopIntent { DiscardThenStop, DrainThenDetach, SaveThenStop }

/// <summary>A remembered recipe is separate from startup enablement and live device identity.</summary>
public sealed record ManagedDiskDefinition(
    Guid ResourceId,
    ManagedDiskMode Mode,
    ManagedDiskSource Source,
    ulong CapacityBytes,
    char PreferredLetter,
    string Label,
    string? ImagePath = null,
    string? CheckpointDirectory = null,
    CacheConfiguration? Cache = null,
    bool AcceptVolatileWrites = false,
    bool StartAtBoot = false,
    bool SaveBeforeStopping = false,
    bool SaveDuringShutdown = false,
    bool ReadOnly = false,
    ImageAllocation Allocation = ImageAllocation.Dynamic,
    uint SectorBytes = 512,
    int SchemaVersion = 1)
{
    public const ulong MiB = 1UL << 20;
    public static ManagedDiskDefinition New(ManagedDiskMode mode) => new(Guid.NewGuid(), mode,
        ManagedDiskSource.CreateNew, 1024 * MiB, 'R', "QueueCache",
        Cache: mode == ManagedDiskMode.CachedVhdx ? new CacheConfiguration(256, CachePreset.Strict) : null,
        SaveBeforeStopping: mode == ManagedDiskMode.ImageInRam);

    public void Validate()
    {
        if (SchemaVersion != 1 || ResourceId == Guid.Empty || !Enum.IsDefined(Mode) || !Enum.IsDefined(Source) ||
            !Enum.IsDefined(Allocation))
            throw new ArgumentException("Invalid managed disk definition or schema version.");
        if (SectorBytes is not (512 or 4096) || CapacityBytes < 16 * MiB || CapacityBytes > long.MaxValue || CapacityBytes % MiB != 0)
            throw new ArgumentException("Capacity must be an aligned whole MiB, at least 16 MiB, with a supported sector size.");
        if (PreferredLetter is < 'D' or > 'Z')
            throw new ArgumentException("Choose a drive letter from D through Z.");
        if (string.IsNullOrWhiteSpace(Label) || Label.Length > 32 || Label.Any(c => c < ' ' || "\\/:*?\"<>|".Contains(c)))
            throw new ArgumentException("Use a valid NTFS volume label of at most 32 characters.");
        if (ReadOnly && Source == ManagedDiskSource.CreateNew)
            throw new ArgumentException("Create and format a writable disk before opening it read-only.");
        if (Mode == ManagedDiskMode.EphemeralRam)
        {
            if (Source != ManagedDiskSource.CreateNew || ImagePath is not null || CheckpointDirectory is not null ||
                Cache is not null || SaveBeforeStopping || SaveDuringShutdown || AcceptVolatileWrites)
                throw new ArgumentException("Pure RAM disks have no image, cache budget or image-save policy.");
        }
        else
        {
            ManagedDiskPaths.ValidateImagePath(ImagePath);
            if (Mode == ManagedDiskMode.CachedVhdx)
            {
                if (CheckpointDirectory is not null || SaveBeforeStopping || SaveDuringShutdown || Cache is null)
                    throw new ArgumentException("Backed disks require an independent cache budget and do not use RAM-image checkpoints.");
                Cache.Validate(AcceptVolatileWrites);
                if (ReadOnly)
                    throw new NotSupportedException("Read-only VHDX read-cache activation requires separate qualification.");
            }
            else
            {
                if (Cache is not null || AcceptVolatileWrites)
                    throw new ArgumentException("A complete RAM image has no partial cache budget or Strict/Fast switch.");
                ManagedDiskPaths.ValidateLocalDirectory(CheckpointDirectory);
                if (ReadOnly && (SaveBeforeStopping || SaveDuringShutdown))
                    throw new ArgumentException("Read-only RAM devices do not save runtime changes automatically.");
            }
        }
    }

    public string StartupDescription => Mode switch
    {
        ManagedDiskMode.EphemeralRam => "Create and format a fresh empty disk at each Windows startup",
        ManagedDiskMode.CachedVhdx => "Mount VHDX and restore its cache at Windows startup",
        ManagedDiskMode.ImageInRam => "Load the last committed image fully into RAM at Windows startup",
        _ => throw new ArgumentOutOfRangeException(nameof(Mode))
    };
}

public static class ManagedDiskPaths
{
    // Validate Windows paths consistently even in host-safe tests on Linux. Native code resolves final handle paths too.
    public static string ValidateImagePath(string? path)
    {
        var result = ValidateLocalDirectory(path);
        if (!result.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a local .vhdx file.");
        return result;
    }

    public static string ValidateLocalDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' ||
            path[2] is not ('\\' or '/') || path.Any(c => c < ' ' || "\"<>|*?".Contains(c)) ||
            path[2..].Contains(':') || path[3..].Split(['\\', '/']).Any(p => p is "." or ".."))
            throw new ArgumentException("Choose an absolute local Windows path without traversal or alternate data streams.");
        return path;
    }
}

public sealed record ManagedDiskCapability(ManagedDiskMode Mode, bool CanStart, string? UnavailableReason)
{
    public void RequireAvailable()
    {
        if (!CanStart)
            throw new NotSupportedException(UnavailableReason ?? "This managed disk mode is unavailable.");
    }
}

public sealed record ImageInspection(string Path, string FileIdentity, Guid DiskId,
    ulong VirtualBytes, ulong AllocatedBytes, uint SectorBytes, bool Differencing)
{
    public bool SameImage(ImageInspection other) =>
        string.Equals(FileIdentity[(FileIdentity.LastIndexOf('|') + 1)..], other.FileIdentity[(other.FileIdentity.LastIndexOf('|') + 1)..], StringComparison.OrdinalIgnoreCase) &&
        DiskId == other.DiskId && VirtualBytes == other.VirtualBytes && SectorBytes == other.SectorBytes && Differencing == other.Differencing;
    public void ValidateFor(ManagedDiskDefinition definition)
    {
        if (definition.Source != ManagedDiskSource.OpenExisting ||
            !string.Equals(Path, definition.ImagePath, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(FileIdentity) || DiskId == Guid.Empty || Differencing ||
            VirtualBytes != definition.CapacityBytes || SectorBytes != definition.SectorBytes)
            throw new InvalidDataException("The inspected image identity, capacity or sector size changed, or its image chain is unsupported.");
    }
}

public sealed record RamReservationQuote(ulong CapacityBytes, ulong MetadataBytes, ulong TransferWorkspaceBytes)
{
    public ulong TotalBytes => checked(CapacityBytes + MetadataBytes + TransferWorkspaceBytes);
    public void Validate(ManagedDiskDefinition definition, ulong availableBudgetBytes)
    {
        if (definition.Mode == ManagedDiskMode.CachedVhdx || CapacityBytes != definition.CapacityBytes || MetadataBytes == 0 ||
            TotalBytes > availableBudgetBytes)
            throw new InvalidDataException("The native RAM reservation quote does not cover the complete disk within the shared budget.");
    }
}

public sealed record ManagedDiskRuntime(Guid ResourceId, Guid BootEpoch, ulong CreationGeneration,
    ManagedDiskMode Mode, ManagedDiskState State, ulong WriteGeneration, ulong? SavedGeneration,
    string? Volume = null, string? LastError = null)
{
    public bool HasUnsavedChanges => Mode == ManagedDiskMode.ImageInRam && (SavedGeneration is null || WriteGeneration != SavedGeneration);
    public ManagedDiskRuntime RecordSaved(ulong frozenGeneration)
    {
        if (Mode != ManagedDiskMode.ImageInRam || frozenGeneration > WriteGeneration)
            throw new InvalidDataException("The checkpoint generation does not belong to this RAM image.");
        return this with { SavedGeneration = frozenGeneration };
    }
    public void CheckExpected(Guid bootEpoch, ulong creationGeneration)
    {
        if (BootEpoch == Guid.Empty || BootEpoch != bootEpoch || CreationGeneration != creationGeneration || CreationGeneration == 0)
            throw new IOException("The selected managed disk has been recreated; refresh before changing it.");
    }
}
