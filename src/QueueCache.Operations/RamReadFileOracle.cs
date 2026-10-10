using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace QueueCache.Operations;

/// <summary>A whole-file byte oracle with a distinct deterministic pattern for every MiB.</summary>
[SupportedOSPlatform("windows")]
public static class RamReadFileOracle
{
    public static string Prepare(string path, int mebibytes) => Pass(path, mebibytes, true);
    public static string Verify(string path, int mebibytes) => Pass(path, mebibytes, false);
    private static string Pass(string path, int mebibytes, bool create)
    {
        if (mebibytes is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(mebibytes));
        const int block = 1 << 20;
        using var file = new AlignedFile(path, block, create);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var expected = new byte[block]; var actual = new byte[block];
        for (var index = 0; index < mebibytes; index++)
        {
            new Random(11091 ^ index).NextBytes(expected);
            BinaryPrimitives.WriteInt64LittleEndian(expected, (long)index * block);
            if (create) file.Write((long)index * block, expected);
            else
            {
                file.Read((long)index * block, actual);
                if (!expected.AsSpan().SequenceEqual(actual))
                    throw new InvalidDataException($"RAM reference byte mismatch at MiB {index}.");
            }
            hash.AppendData(expected);
        }
        if (create) file.Flush();
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
