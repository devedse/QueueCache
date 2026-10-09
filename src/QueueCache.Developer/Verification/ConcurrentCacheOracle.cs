using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using QueueCache.Developer.FileTests;

namespace QueueCache.Developer.Verification;

public sealed record CacheOracleFile(string Name, int Seed, int Epoch, string Sha256);
public sealed record CacheOracleResult(int BytesPerFile, CacheOracleFile[] Files, long VerifiedWrites);

/// <summary>Independent files overwritten and checked while mixed DiskSpd I/O churns the cache.
/// Only these deterministic files are byte oracles; DiskSpd's write buffers are not.</summary>
public static class ConcurrentCacheOracle
{
    public static byte[] Pattern(int seed, int epoch, int length)
    {
        var bytes = new byte[length];
        new Random(unchecked(seed * 104729 + epoch)).NextBytes(bytes);
        return bytes;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static async Task<CacheOracleResult> Run(string directory, int round, int seconds, Action<int, object> checkpoint)
    {
        const int bytes = 1 << 20;
        var files = await Task.WhenAll(Enumerable.Range(0, 4).Select(stream => Task.Run(async () =>
        {
            var name = $"oracle-r{round:D2}-s{stream}.dat";
            var path = Path.Combine(directory, name);
            var seed = checked(round * 100 + stream);
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
            {
                file.Write(Pattern(seed, 0, bytes));
                file.Flush(true);
            }
            var timer = Stopwatch.StartNew();
            var epoch = 0;
            byte[] expected;
            do
            {
                expected = Pattern(seed, ++epoch, bytes);
                UnbufferedFileWrite.WritePrefix(path, expected);
                if (!UnbufferedFileWrite.ReadPrefix(path, bytes).AsSpan().SequenceEqual(expected))
                    throw new InvalidDataException($"Concurrent byte oracle mismatch: {name}, epoch {epoch}.");
                if (epoch % 20 == 1) checkpoint(stream, new { Name = name, Epoch = epoch, ElapsedSeconds = timer.Elapsed.TotalSeconds });
                await Task.Delay(250);
            } while (timer.Elapsed.TotalSeconds < seconds);
            return new CacheOracleFile(name, seed, epoch, Convert.ToHexString(SHA256.HashData(expected)));
        })));
        return new(bytes, files, files.Sum(f => (long)f.Epoch));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static object Verify(string directory, string oraclePath)
    {
        var oracle = JsonSerializer.Deserialize<CacheOracleResult>(File.ReadAllText(oraclePath)) ??
            throw new InvalidDataException("Missing concurrent oracle.");
        if (oracle.BytesPerFile != 1 << 20 || oracle.Files.Length != 4 || oracle.VerifiedWrites <= 0 ||
            oracle.Files.Select(f => f.Name).Distinct().Count() != 4)
            throw new InvalidDataException("Invalid concurrent oracle manifest.");
        foreach (var entry in oracle.Files)
        {
            if (Path.GetFileName(entry.Name) != entry.Name || !entry.Name.StartsWith("oracle-r", StringComparison.Ordinal) || entry.Epoch <= 0)
                throw new InvalidDataException("Invalid owned oracle filename or epoch.");
            var expected = Pattern(entry.Seed, entry.Epoch, oracle.BytesPerFile);
            if (Convert.ToHexString(SHA256.HashData(expected)) != entry.Sha256 ||
                !UnbufferedFileWrite.ReadPrefix(Path.Combine(directory, entry.Name), oracle.BytesPerFile).AsSpan().SequenceEqual(expected))
                throw new InvalidDataException("Persisted concurrent oracle mismatch: " + entry.Name);
        }
        return new { Verified = oracle.Files.Length, oracle.VerifiedWrites, Source = oraclePath };
    }
}
