namespace QueueCache.Developer.Verification;

/// <summary>Deleted durable markers may reappear after a cut, but must never contain paging data.</summary>
public static class GrowthMarkerEvidence
{
    public const int Count = 2000;
    public static byte[] Pattern(ulong nonce, int index)
    {
        var bytes = new byte[4096]; ComparisonData.FillSector(bytes, nonce, 1, 4, index); return bytes;
    }
    public static bool Intact(byte[] bytes, ulong nonce, int index) => bytes.AsSpan().SequenceEqual(Pattern(nonce, index));
}
public sealed record GrowthMarkerResult(int Expected, int Absent, int Intact, IReadOnlyList<string> Damaged);
