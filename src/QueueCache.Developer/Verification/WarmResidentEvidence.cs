using QueueCache.Management;

namespace QueueCache.Developer.Verification;

public static class WarmResidentEvidence
{
    public static void ValidateFirstPass(long readBytes, ulong fileBytes)
    {
        if (fileBytes == 0 || readBytes <= 0 || (ulong)readBytes < fileBytes)
            throw new InvalidDataException("The first sequential warm pass did not read the entire fitting file.");
    }

    public static void Validate(WriteCacheState before, WriteCacheState after, long readBytes, ulong fileBytes)
    {
        if (fileBytes == 0 || readBytes <= 0 || (ulong)readBytes < fileBytes || !before.Operational || !after.Operational ||
            before.Instance == 0 || before.Instance != after.Instance || before.Generation != after.Generation ||
            before.Errors != after.Errors || after.LastError != 0 ||
            before.ReadMissBytes != after.ReadMissBytes || after.ReadHitBytes < before.ReadHitBytes ||
            after.ReadHitBytes - before.ReadHitBytes < (ulong)readBytes ||
            after.CleanReadBytes + after.CleanWriteBytes + after.DirtyBytes < fileBytes)
            throw new InvalidDataException("The second sequential warm pass did not prove a stable RAM-resident fitting file.");
    }
}
