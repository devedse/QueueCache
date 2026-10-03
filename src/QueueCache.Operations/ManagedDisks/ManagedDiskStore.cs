using System.Security.Cryptography;
using System.Text.Json;
using QueueCache.Management;

namespace QueueCache.Operations.ManagedDisks;

public enum ManagedDiskJournalStage { Creating, Loading, Ready, Exporting, CandidateVerified, Committed, Stopping, Stopped, RecoveryRequired }
public sealed record ManagedImageReference(ImageInspection Identity, LogicalImageDigest? Digest, ulong? Generation);
public sealed record ManagedDiskRecord(ManagedDiskDefinition Definition, ManagedDiskRuntime? Runtime = null,
    ManagedImageReference? CommittedImage = null, ManagedImageReference? PreviousImage = null,
    ImageInspection? OriginalSource = null, RamDiskSnapshot? Native = null, int? PhysicalDiskNumber = null,
    string? VolumePath = null, DateTimeOffset? SavedAt = null, string? LastError = null, bool Removed = false,
    Guid? GptDiskId = null, Guid? StartupSession = null, ulong ImageTransferAttempts = 0, ulong ImageTransferredBytes = 0)
{
    public Guid ResourceId => Definition.ResourceId;
    public void Validate()
    {
        if (Definition is null) throw new InvalidDataException("Missing managed disk definition.");
        Definition.Validate();
        if ((Runtime is not null && (Runtime.ResourceId != ResourceId || Runtime.Mode != Definition.Mode)) ||
            (Native is not null && (Native.ResourceId != ResourceId || Definition.Mode == ManagedDiskMode.CachedVhdx)) ||
            (PhysicalDiskNumber is < 0) || (CommittedImage is not null && Definition.Mode != ManagedDiskMode.ImageInRam) ||
            (Removed && (Definition.StartAtBoot || (Runtime is not null && Runtime.State != ManagedDiskState.Stopped))))
            throw new InvalidDataException("Managed disk catalog ownership or mode mismatch.");
        foreach (var reference in new[] { CommittedImage, PreviousImage })
        {
            if (reference is null) continue;
            ManagedDiskPaths.ValidateImagePath(reference.Identity.Path);
            if (reference.Identity.VirtualBytes != Definition.CapacityBytes || reference.Identity.SectorBytes != Definition.SectorBytes ||
                reference.Identity.Differencing || reference.Identity.DiskId == Guid.Empty || string.IsNullOrWhiteSpace(reference.Identity.FileIdentity) ||
                (reference.Digest is not null && (reference.Digest.Bytes != Definition.CapacityBytes ||
                    reference.Digest.Sha256.Length != 64 || !reference.Digest.Sha256.All(Uri.IsHexDigit))))
                throw new InvalidDataException("Invalid committed image identity or logical digest.");
        }
    }
}
public sealed record ManagedDiskJournal(Guid OperationId, Guid ResourceId, ManagedDiskJournalStage Stage,
    Guid BootEpoch, ulong CreationGeneration, ulong? FrozenGeneration = null,
    ManagedImageReference? Candidate = null, ManagedImageReference? Previous = null, string? Failure = null, string? CandidatePath = null)
{
    public void Validate()
    {
        if (CandidatePath is not null) ManagedDiskPaths.ValidateImagePath(CandidatePath);
        if (OperationId == Guid.Empty || ResourceId == Guid.Empty || !Enum.IsDefined(Stage) ||
            (Stage is ManagedDiskJournalStage.CandidateVerified or ManagedDiskJournalStage.Committed &&
                (Candidate?.Digest is null || FrozenGeneration is null)))
            throw new InvalidDataException("Incomplete or unsupported managed disk operation journal.");
    }
}

/// <summary>Checksummed records with durable staging and one retained committed predecessor.</summary>
public interface IManagedDiskRecordStore
{
    ManagedDiskRecord Read(Guid id);
    void Save(ManagedDiskRecord record);
    ManagedDiskJournal? ReadJournal(Guid id);
    void SaveJournal(ManagedDiskJournal journal);
}
public sealed class ManagedDiskStore : IManagedDiskRecordStore
{
    private const int MaximumRecordBytes = 1 << 20;
    private readonly string directory;
    private sealed record Envelope(int Version, string Payload, string Sha256);
    public ManagedDiskStore(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(this.directory);
    }
    public IReadOnlyList<ManagedDiskRecord> List()
    {
        var records = new List<ManagedDiskRecord>();
        // Enumeration is bounded to catalog records, never candidate image names.
        foreach (var path in Directory.EnumerateFiles(directory, "*.resource.json").Order())
        {
            if (records.Count >= 1024) throw new InvalidDataException("Managed disk catalog exceeds its supported limit.");
            var name = Path.GetFileName(path).Replace(".resource.json", "", StringComparison.Ordinal);
            if (!Guid.TryParseExact(name, "N", out var id)) throw new InvalidDataException("Invalid managed resource filename.");
            var record = Read(id); if (!record.Removed) records.Add(record);
        }
        return records;
    }
    public bool ContainsResource(Guid id) => File.Exists(PathFor(id, "resource")) || File.Exists(PathFor(id, "resource") + ".previous");
    public ManagedDiskRecord Read(Guid id)
    {
        var record = ReadWithPrevious<ManagedDiskRecord>(PathFor(id, "resource"), out var recovered);
        record.Validate();
        if (record.ResourceId != id) throw new InvalidDataException("Managed disk record does not match its resource filename.");
        return recovered ? record with { LastError = "Catalog recovery used the previous committed record. Reconcile this resource before mutation." } : record;
    }
    public void Save(ManagedDiskRecord record)
    {
        record.Validate(); Write(PathFor(record.ResourceId, "resource"), record);
    }
    public ManagedDiskJournal? ReadJournal(Guid id)
    {
        var path = PathFor(id, "journal");
        if (!File.Exists(path) && !File.Exists(path + ".previous")) return null;
        var journal = ReadWithPrevious<ManagedDiskJournal>(path, out var recovered); journal.Validate();
        if (journal.ResourceId != id) throw new InvalidDataException("Journal resource identity mismatch.");
        return recovered ? journal with { Failure = "Operation journal recovered from its previous record; explicit reconciliation is required." } : journal;
    }
    public void SaveJournal(ManagedDiskJournal journal)
    {
        journal.Validate(); Write(PathFor(journal.ResourceId, "journal"), journal);
    }
    public void RemoveStopped(Guid id)
    {
        var record = Read(id);
        if (record.Runtime is not null && record.Runtime.State != ManagedDiskState.Stopped)
            throw new IOException("Stop the managed disk before removing its configuration.");
        // A durable tombstone prevents a crash between deletions from resurrecting
        // an auto-start recipe. Forgetting never removes images or recovery evidence.
        Save(record with { Removed = true, Definition = record.Definition with { StartAtBoot = false } });
    }
    private string PathFor(Guid id, string kind)
    {
        if (id == Guid.Empty) throw new ArgumentException("A managed resource ID is required.");
        return Path.Combine(directory, $"{id:N}.{kind}.json");
    }
    private static T ReadWithPrevious<T>(string path, out bool recovered)
    {
        recovered = false;
        try { return ReadChecked<T>(path); }
        catch (Exception primary) when (primary is IOException or InvalidDataException or JsonException or FormatException)
        {
            try { recovered = true; return ReadChecked<T>(path + ".previous"); }
            catch (Exception previous) when (previous is IOException or InvalidDataException or JsonException or FormatException)
            {
                throw new AggregateException("Neither managed catalog generation is valid; recovery is required.", primary, previous);
            }
        }
    }
    private static T ReadChecked<T>(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is 0 or > MaximumRecordBytes) throw new InvalidDataException("Invalid managed record length.");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        var envelope = JsonSerializer.Deserialize<Envelope>(bytes) ?? throw new InvalidDataException("Missing managed record envelope.");
        if (envelope.Version != 1 || envelope.Payload is null || envelope.Sha256 is null)
            throw new InvalidDataException("Unsupported managed record envelope.");
        var payload = Convert.FromBase64String(envelope.Payload);
        if (Convert.ToHexString(SHA256.HashData(payload)) != envelope.Sha256)
            throw new InvalidDataException("Managed record checksum mismatch.");
        return JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException("Missing managed record payload.");
    }
    private static void Write<T>(string path, T record)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(record);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, Convert.ToBase64String(payload), Convert.ToHexString(SHA256.HashData(payload))));
        if (bytes.Length > MaximumRecordBytes) throw new InvalidDataException("Managed record exceeds the supported size.");
        var staged = path + ".stage-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes); stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(staged, path, path + ".previous", ignoreMetadataErrors: false);
            else File.Move(staged, path);
            // Require the committed pointer itself to survive a durability barrier and read-back.
            using (var committed = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough))
                committed.Flush(flushToDisk: true);
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                throw new IOException("Managed record commit read-back failed.");
        }
        finally { File.Delete(staged); }
    }
}
