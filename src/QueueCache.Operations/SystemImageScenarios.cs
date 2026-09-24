using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QueueCache.Operations;

public sealed record SystemImageOracle(int SchemaVersion, DiskTarget Target, string FilePath,
    long Bytes, int Width, int Height, int Seed, string ExpectedSha256, DateTimeOffset Created);

/// <summary>Deterministic large BMP used to reproduce the reported Paint/Photos workflow.</summary>
[SupportedOSPlatform("windows")]
public static class SystemImageScenarios
{
    public const int FileMiB = 349;
    public const long FileBytes = (long)FileMiB << 20;
    public const int Width = 9216;
    public const int Height = 9927;
    private const int BlockBytes = 1 << 20;
    private const int HeaderBytes = 4096;
    private const int BitsPerPixel = 32;
    private const int Blocks = FileMiB;

    public static void ValidateOwnedPath(DiskTarget target, string directory, string file)
    {
        if (target.Letter != 'C' || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), target.Root,
                StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(Path.GetFileName(directory),
                "^QueueCache-System-[0-9a-f]{32}(?:-system-active-image-(?:fast|strict))?$",
                RegexOptions.CultureInvariant) ||
            !string.Equals(Path.GetFullPath(file), Path.Combine(Path.GetFullPath(directory), "large-image.bmp"),
                StringComparison.OrdinalIgnoreCase))
            throw new IOException("System-image workload must use only its unique owned C: directory and large-image.bmp.");
    }

    private static void FillBlock(byte[] block, int seed, int index)
    {
        new Random(unchecked(seed ^ (index * 0x1f123bb5))).NextBytes(block);
        if (index != 0)
            return;
        block.AsSpan(0, HeaderBytes).Clear();
        block[0] = (byte)'B';
        block[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(2), FileMiB << 20);
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(10), HeaderBytes);
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(18), Width);
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(22), Height);
        BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(28), BitsPerPixel);
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(34), (FileMiB << 20) - HeaderBytes);
    }

    public static string ExpectedHash(int seed)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var block = new byte[BlockBytes];
        for (var index = 0; index < Blocks; ++index)
        {
            FillBlock(block, seed, index);
            hash.AppendData(block);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static SystemImageOracle ReadOracle(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Image oracle cannot be a reparse point.");
        var oracle = JsonSerializer.Deserialize<SystemImageOracle>(File.ReadAllText(path)) ??
            throw new InvalidDataException("Missing system-image oracle.");
        if (oracle.SchemaVersion != 1 || oracle.Target is null || oracle.FilePath is null ||
            oracle.ExpectedSha256 is null || oracle.Bytes != FileBytes ||
            oracle.Width != Width || oracle.Height != Height || oracle.ExpectedSha256.Length != 64 ||
            !oracle.ExpectedSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid system-image oracle schema or bounds.");
        ValidateOwnedPath(oracle.Target, Path.GetDirectoryName(oracle.FilePath)!, oracle.FilePath);
        return oracle;
    }

    public static IReadOnlyList<CheckResult> Create(DiskTarget target, string directory, string oraclePath)
    {
        target.ValidateCurrent();
        var file = Path.Combine(directory, "large-image.bmp");
        ValidateOwnedPath(target, directory, file);
        if (Directory.Exists(directory) || File.Exists(oraclePath))
            throw new IOException("System-image directory and oracle must both be new.");
        if (new DriveInfo(target.Root).AvailableFreeSpace < (2L << 30) + ((long)FileMiB << 20))
            throw new IOException("System volume has insufficient free space and 2 GiB headroom.");

        var seed = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var oracle = new SystemImageOracle(1, target, file, FileBytes, Width, Height,
            seed, ExpectedHash(seed), DateTimeOffset.UtcNow);
        using (var output = new FileStream(oraclePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(output, oracle);
            output.Flush(true);
        }
        target.ValidateCurrent();
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("System-image directory is a reparse point.");
        var block = new byte[BlockBytes];
        using (var data = new AlignedFile(file, BlockBytes, create: true))
        {
            for (var index = 0; index < Blocks; ++index)
            {
                FillBlock(block, seed, index);
                data.Write((long)index * BlockBytes, block);
            }
            data.Flush();
        }
        return [new("system-image/write-flushed", "PASS",
            "The deterministic 349 MiB 32-bit BMP was written and its application file flush completed; byte verification is a separate boundary.")];
    }

    public static IReadOnlyList<CheckResult> Verify(DiskTarget target, SystemImageOracle oracle)
    {
        target.ValidateCurrent();
        if (target != oracle.Target)
            throw new IOException("System-image target identity changed since the oracle was written.");
        var directory = Path.GetDirectoryName(oracle.FilePath)!;
        ValidateOwnedPath(target, directory, oracle.FilePath);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(oracle.FilePath) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(oracle.FilePath).Length != oracle.Bytes)
            throw new IOException("Owned system-image path or length changed.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var expected = new byte[BlockBytes];
        var actual = new byte[BlockBytes];
        using (var data = new AlignedFile(oracle.FilePath, BlockBytes, create: false))
            for (var index = 0; index < Blocks; ++index)
            {
                FillBlock(expected, oracle.Seed, index);
                data.Read((long)index * BlockBytes, actual);
                if (!expected.AsSpan().SequenceEqual(actual))
                    throw new IOException($"Owned system-image byte mismatch in block {index}.");
                hash.AppendData(actual);
            }
        if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), oracle.ExpectedSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new IOException("Owned system-image SHA-256 disagrees with the off-target oracle.");
        return [new("system-image/full-bytes", "PASS",
            "Every BMP byte matched the off-target oracle through unbuffered reads.")];
    }
}
