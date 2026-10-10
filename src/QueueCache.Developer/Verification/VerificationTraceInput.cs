using System.Security.Cryptography;

namespace QueueCache.Developer.Verification;

/// <summary>Short verified input for native trace readers; original evidence is never moved/deleted.</summary>
public sealed class VerificationTraceInput : IDisposable
{
    public string FilePath { get; }
    private readonly string? ownedDirectory;
    private VerificationTraceInput(string file, string? directory) { FilePath = file; ownedDirectory = directory; }

    public static VerificationTraceInput Open(string source, string expectedSha256)
    {
        source = Path.GetFullPath(source);
        VerificationTraceInput input;
        if (source.Length < 220) input = new(source, null);
        else
        {
            var directory = Directory.CreateTempSubdirectory("QCTraceAnalysis-").FullName;
            input = new(Path.Combine(directory, "trace.etl"), directory);
            try { File.Copy(source, input.FilePath, overwrite: false); }
            catch { input.Dispose(); throw; }
        }
        try
        {
            using var file = File.OpenRead(input.FilePath);
            if (Convert.ToHexString(SHA256.HashData(file)) != expectedSha256)
                throw new InvalidDataException("Native trace analysis input digest mismatch.");
            return input;
        }
        catch { input.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (ownedDirectory is null) return;
        File.Delete(FilePath);
        if (Directory.Exists(ownedDirectory) && !Directory.EnumerateFileSystemEntries(ownedDirectory).Any()) Directory.Delete(ownedDirectory);
    }
}
