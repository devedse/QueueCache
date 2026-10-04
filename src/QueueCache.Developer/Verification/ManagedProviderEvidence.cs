using QueueCache.Management;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Developer.Verification;

/// <summary>Acceptance oracles for the owned native RAM fixture; no Windows or driver access.</summary>
public static class ManagedProviderEvidence
{
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
