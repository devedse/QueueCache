using QueueCache.Management;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Developer.Verification;

/// <summary>Acceptance oracles for the owned native RAM fixture; no Windows or driver access.</summary>
public static class ManagedProviderEvidence
{
    /// <summary>
    /// The provider answers DATA PROTECT / WRITE PROTECTED (7/27h). Windows classpnp maps that
    /// ASC to STATUS_IO_DEVICE_ERROR for raw writes, so the class-level write-protect state is
    /// proven through IOCTL_DISK_IS_WRITABLE, and the rejection by exactly one provider error.
    /// </summary>
    public static void ValidateReadOnlyWrite(bool writableBefore, bool writableWhileReadOnly, int? writeError,
        ulong errorsBefore, ulong errorsAfter, ulong generationBefore, ulong generationAfter, bool bytesUnchanged)
    {
        if (!writableBefore || writableWhileReadOnly)
            throw new IOException("Windows did not report the native read-only state through IOCTL_DISK_IS_WRITABLE.");
        if (writeError is not (19 or 1117))
            throw new IOException($"Native read-only disk did not reject a physical sector write as write-protected (error {writeError?.ToString() ?? "none"}).");
        if (errorsAfter != errorsBefore + 1 || generationAfter != generationBefore || !bytesUnchanged)
            throw new IOException("Rejected read-only write was not exactly one provider rejection with unchanged sectors and generation.");
    }

    public static void ValidateReservation(RamDiskSnapshot ram, ulong globalBefore, ulong globalAfter)
    {
        if (ram.CapacityBytes == 0 || ram.CapacityBytes % (1UL << 20) != 0)
            throw new InvalidDataException("Reservation evidence requires the observed aligned native capacity.");
        var slabs = (ram.CapacityBytes + RamDiskSnapshot.AllocationSlabBytes - 1) / RamDiskSnapshot.AllocationSlabBytes;
        // x64 MDL/PFN and provider slab descriptors, plus the bounded transfer workspace.
        var minimum = checked(ram.CapacityBytes + ram.CapacityBytes / 4096 * 8 + slabs * (48 + 24) +
            RamDiskSnapshot.MaximumTransferBytes + 4096UL);
        if (ram.ReservedBytes < minimum || globalAfter != checked(globalBefore + ram.ReservedBytes))
            throw new IOException("Shared RAM reservation omitted native page metadata or did not charge the existing authority exactly.");
    }

    public static void ValidateAllocationFailure(RamDiskAllocationFailureProof before, RamDiskAllocationFailureProof after,
        Guid resource, uint afterSlabs, ulong reservationBefore, ulong reservationAfter)
    {
        if (before.BootEpoch == Guid.Empty || resource == Guid.Empty || afterSlabs == 0)
            throw new InvalidDataException("Allocation-failure proof requires observed boot identity and a bounded owned request.");
        if (after.BootEpoch != before.BootEpoch || after.ResourceId != resource ||
            before.CompletedInjections == ulong.MaxValue || after.CompletedInjections != before.CompletedInjections + 1 ||
            after.AllocatedSlabs != afterSlabs || reservationAfter != reservationBefore)
            throw new IOException("Native failure did not reach the requested allocation boundary and restore the exact reservation.");
    }

    public static void ValidateTrim(RamDiskSnapshot before, RamDiskSnapshot after,
        ReadOnlySpan<byte> original, ReadOnlySpan<byte> observed, int offset, int length)
    {
        after.RequireSameCreation(before);
        if (before.SectorBytes is not (512 or 4096) || offset <= 0 || length <= 0 ||
            offset % before.SectorBytes != 0 || length % before.SectorBytes != 0 ||
            original.Length != observed.Length || offset >= original.Length || length >= original.Length - offset)
            throw new InvalidDataException("TRIM evidence requires aligned interior ranges and complete adjacent guards.");
        if (after.Trims <= before.Trims || after.WriteGeneration <= before.WriteGeneration || after.Errors != before.Errors)
            throw new IOException("Native TRIM completion/generation evidence is missing or reports an error.");
        if (!original[..offset].SequenceEqual(observed[..offset]) ||
            !original[(offset + length)..].SequenceEqual(observed[(offset + length)..]) ||
            observed.Slice(offset, length).ContainsAnyExcept((byte)0))
            throw new IOException("RAM TRIM changed an adjacent guard or failed to zero the discarded range.");
    }
}
