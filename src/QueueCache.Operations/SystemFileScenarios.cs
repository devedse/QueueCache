using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QueueCache.Operations;

public sealed record SystemFileOracle(int SchemaVersion, DiskTarget Target, string FilePath,
    long Bytes, int Seed, string ExpectedSha256, DateTimeOffset Created);

/// <summary>Bounded, owned-file OS-volume oracle. No raw disk writes or cache controls.</summary>
[SupportedOSPlatform("windows")]
public static class SystemFileScenarios
{
    public const int FileMiB = 64;
    private const int BlockBytes = 1 << 20;
    private const int Blocks = FileMiB;
    private const int OverwriteBlock = 8;

    public static void ValidateOwnedPath(DiskTarget target, string directory, string file)
    {
        if (target.Letter != 'C' || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), target.Root,
                StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(Path.GetFileName(directory), "^QueueCache-System-[0-9a-f]{32}$", RegexOptions.CultureInvariant) ||
            !string.Equals(Path.GetFullPath(file), Path.Combine(Path.GetFullPath(directory), "payload.bin"),
                StringComparison.OrdinalIgnoreCase))
            throw new IOException("System-file workload must use only its unique owned C: directory and payload.bin.");
    }

    private static void Fill(byte[] block, int seed, int index, int generation)
    {
        new Random(unchecked(seed ^ (index * 0x1f123bb5) ^ (generation * 0x2a316d41))).NextBytes(block);
    }

    public static string ExpectedHash(int seed)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var block = new byte[BlockBytes];
        for (var index = 0; index < Blocks; index++)
        {
            Fill(block, seed, index, index == OverwriteBlock ? 2 : 0);
            hash.AppendData(block);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static SystemFileOracle ReadOracle(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Oracle cannot be a reparse point.");
        var oracle = JsonSerializer.Deserialize<SystemFileOracle>(File.ReadAllText(path)) ??
            throw new InvalidDataException("Missing system-file oracle.");
        if (oracle.SchemaVersion != 1 || oracle.Target is null || oracle.FilePath is null ||
            oracle.ExpectedSha256 is null || oracle.Bytes != (long)FileMiB << 20 ||
            oracle.ExpectedSha256.Length != 64 || !oracle.ExpectedSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid system-file oracle schema or bounds.");
        ValidateOwnedPath(oracle.Target, Path.GetDirectoryName(oracle.FilePath)!, oracle.FilePath);
        return oracle;
    }

    public static IReadOnlyList<CheckResult> Create(DiskTarget target, string directory, string oraclePath)
    {
        target.ValidateCurrent();
        var file = Path.Combine(directory, "payload.bin");
        ValidateOwnedPath(target, directory, file);
        if (Directory.Exists(directory) || File.Exists(oraclePath))
            throw new IOException("System-file directory and oracle must both be new.");
        if (new DriveInfo(target.Root).AvailableFreeSpace < (1L << 30) + ((long)FileMiB << 20))
            throw new IOException("System volume has insufficient free space and 1 GiB headroom.");

        var seed = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var oracle = new SystemFileOracle(1, target, file, (long)FileMiB << 20,
            seed, ExpectedHash(seed), DateTimeOffset.UtcNow);
        // Expected bytes are committed off-target before any C: workload write.
        using (var output = new FileStream(oraclePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(output, oracle);
            output.Flush(true);
        }
        target.ValidateCurrent();
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("System-file directory is a reparse point.");
        var block = new byte[BlockBytes];
        var read = new byte[BlockBytes];
        using (var data = new AlignedFile(file, BlockBytes, create: true))
        {
            for (var index = 0; index < Blocks; index++)
            {
                Fill(block, seed, index, 0);
                data.Write((long)index * BlockBytes, block);
            }
            for (var generation = 1; generation <= 2; generation++)
            {
                Fill(block, seed, OverwriteBlock, generation);
                data.Write((long)OverwriteBlock * BlockBytes, block);
                data.Read((long)OverwriteBlock * BlockBytes, read);
                if (!block.AsSpan().SequenceEqual(read))
                    throw new IOException($"Immediate overwrite mismatch at generation {generation}.");
            }
            data.Flush();
        }
        Verify(target, oracle);
        return [new("system-file/create/live-bytes", "PASS",
            "64 MiB owned file and two same-range 1 MiB overwrites matched independently computed bytes; file buffers flushed. This is a current-volume check, not independent persistence proof.")];
    }

    public static IReadOnlyList<CheckResult> Verify(DiskTarget target, SystemFileOracle oracle)
    {
        target.ValidateCurrent();
        DiskTarget.ValidateRecordedSystemTarget(oracle.Target, target);
        var directory = Path.GetDirectoryName(oracle.FilePath)!;
        ValidateOwnedPath(target, directory, oracle.FilePath);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(oracle.FilePath) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(oracle.FilePath).Length != oracle.Bytes)
            throw new IOException("Owned system-file path or length changed.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var expected = new byte[BlockBytes];
        var actual = new byte[BlockBytes];
        using (var data = new AlignedFile(oracle.FilePath, BlockBytes, create: false))
        {
            for (var index = 0; index < Blocks; index++)
            {
                Fill(expected, oracle.Seed, index, index == OverwriteBlock ? 2 : 0);
                data.Read((long)index * BlockBytes, actual);
                if (!expected.AsSpan().SequenceEqual(actual))
                    throw new IOException($"Owned system-file byte mismatch in block {index}.");
                hash.AppendData(actual);
            }
        }
        if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), oracle.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Owned system-file SHA-256 disagrees with off-target oracle.");
        return [new("system-file/read-only-bytes", "PASS",
            "Every owned byte matched the off-target oracle through unbuffered file reads. No workload write or cache control was issued.")];
    }
}
