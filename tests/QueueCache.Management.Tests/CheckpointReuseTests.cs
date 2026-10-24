using System.Security.Cryptography;
using QueueCache.Operations.ManagedDisks;
using QueueCache.Developer.Verification;

internal static class CheckpointReuseTests
{
    private const int MiB = 1 << 20, Capacity = 16 * MiB;
    private static void Check(bool value, string message) { if (!value) throw new Exception("Checkpoint reuse: " + message); }
    public static async Task RunAsync()
    {
        Check(CheckpointReuseScenario.ValidateImageMiB(128) == 128 && CheckpointReuseScenario.ValidateImageMiB(1024) == 1024 &&
            CheckpointReuseScenario.ValidateImageMiB(2048) == 2048, "bounded checkpoint fixture sizes retain small checks and a larger same-build observation");
        try { CheckpointReuseScenario.ValidateImageMiB(4096); throw new Exception("Unbounded image fixture accepted."); }
        catch (ArgumentOutOfRangeException) { }
        var success = new Fixture();
        var result = await success.Save();
        Check(result.Image.Identity.Path == success.OlderPath && result.Record.PreviousImage == success.Current,
            "older candidate becomes current only after verification; old current is retained");
        Check(result.Record.LastSave is { Bytes: Capacity, PayloadBytes: MiB, ReusedCandidate: true, ContainerHeaderBytes: 8192 },
            "statistics distinguish checked logical size, changed payload and container headers");
        Check(success.Candidate.ReadBytes == Capacity && success.MapSaved && !success.Frozen && success.CurrentBytes.All(b => b == 0x17),
            "all candidate sectors were read back; current image was never written; source thawed");
        var unchanged = new Fixture(change: false);
        var unchangedResult = await unchanged.Save();
        Check(unchangedResult.Record.LastSave is { PayloadBytes: 0, ContainerHeaderBytes: 0 } && unchanged.Candidate.Writes == 0 &&
            unchanged.Candidate.ReadBytes == Capacity, "no changed regions still get complete verification");
        var full = new Fixture(); var export = await full.Save(allowReuse: false);
        Check(full.OpenReuseCalls == 0 && export.Image.Identity.Path == full.RequestedPath &&
            export.Record.LastSave is { PayloadBytes: Capacity, ReusedCandidate: false }, "explicit destination is honored even for a committed export");
        Check(!export.Image.GeneratedCheckpoint && ManagedCheckpointReuse.Candidate(full.Store.Record with
            { PreviousImage = export.Image, CommittedImage = full.Current }) is null,
            "committed export in the generated filename namespace cannot become a future reuse candidate");
        var sparse = new Fixture("sparse-unchanged");
        var sparseResult = await sparse.Save();
        Check(sparseResult.Record.LastSave is { ReusedCandidate: true, PayloadBytes: MiB },
            "unchanged sparse source regions can be skipped without allocating BAT entries");
        var growth = new Fixture("sparse-growth");
        var growthResult = await growth.Save();
        Check(growthResult.Record.LastSave is { ReusedCandidate: false, PayloadBytes: Capacity } && growth.Candidate.Writes == 0 &&
            growthResult.Image.Identity.Path == growth.RequestedPath && growthResult.Record.PreviousImage == growth.Current,
            "changed unallocated region selects full save before any reuse mutation");
        foreach (var reason in new[] { "partial-read", "stale-map", "reporting-read" })
        {
            var broken = new Fixture(reason);
            try { await broken.Save(); throw new Exception("Interrupted/corrupt reuse succeeded."); }
            catch (IOException) { }
            Check(broken.Store.Record.CommittedImage == broken.Current && broken.Store.Record.PreviousImage is null &&
                broken.CurrentBytes.All(b => b == 0x17) && !broken.Frozen && !broken.MapSaved,
                "failed candidate is not a retained fallback and cannot change the current image: " + reason);
            if (reason == "stale-map") Check(broken.Candidate.ReadBytes == Capacity,
                "corruption in a skipped region is caught by full verification, not only changed-region checks");
        }
        var ownership = new Fixture();
        var record = ownership.Store.Record;
        Check(ManagedCheckpointReuse.Candidate(record) == ownership.OlderPath, "catalog-owned older candidate eligible");
        Check(ManagedCheckpointReuse.Candidate(record with { PreviousImage = record.PreviousImage! with { GeneratedCheckpoint = false } }) is null,
            "legacy or unrecorded filename provenance never grants automatic reuse");
        Check(ManagedCheckpointReuse.Candidate(record with { OriginalSource = record.PreviousImage!.Identity with { Path = @"C:\Imports\alias.vhdx" } }) is null,
            "an imported image's file identity cannot be reused through another path");
        Check(ManagedCheckpointReuse.Candidate(record with { PreviousImage = record.PreviousImage! with
            { Identity = record.PreviousImage.Identity with { FileIdentity = record.CommittedImage!.Identity.FileIdentity, DiskId = Guid.NewGuid() } } }) is null,
            "latest file identity is protected even when metadata IDs differ");
        Check(ManagedCheckpointReuse.Candidate(record with { Definition = record.Definition with { ImagePath = ownership.OlderPath } }) is null,
            "user-selected original path is protected");
        var interrupted = new ManagedDiskJournal(Guid.NewGuid(), record.ResourceId, ManagedDiskJournalStage.Exporting,
            record.Runtime!.BootEpoch, 1, CandidatePath: ownership.OlderPath);
        Check(ManagedCheckpointReuse.ExcludeInterruptedCandidate(record, interrupted).PreviousImage is null &&
            ManagedCheckpointReuse.ExcludeInterruptedCandidate(record, interrupted).CommittedImage == record.CommittedImage,
            "reconciliation drops an interrupted older pointer without promoting its candidate");
        var epoch = Guid.NewGuid();
        var observedBefore = success.Store.Initial with { ImageIo = new(epoch, 1, 2, 3, 0, 0) };
        var observedAfter = result.Record with { ImageIo = new(epoch, 2, 3, 4, Capacity, MiB) };
        CheckpointReuseScenario.ValidateSave(observedBefore, observedAfter, true);
        Reject(() => CheckpointReuseScenario.ValidateSave(observedBefore, observedAfter with
            { LastSave = observedAfter.LastSave! with { PayloadBytes = null } }, true));
        Reject(() => CheckpointReuseScenario.ValidateSave(observedBefore, observedAfter with
            { ImageIo = observedAfter.ImageIo! with { ReadBytes = Capacity - 512 } }, true));
        Reject(() => CheckpointReuseScenario.ValidateSave(observedBefore, observedAfter with
            { ImageIo = observedAfter.ImageIo! with { ObservationEpoch = Guid.NewGuid() } }, true));
        var campaign = VerificationCampaignPlan.Create(new(new("Q:", "quick", Repeats: 1), "experiment", "W:", FocusSuite: "checkpoint-reuse", PauseBackingCache: true));
        Check(campaign.Count == 1 && campaign[0].Role == CampaignTargetRole.NtfsLab && campaign[0].ExpectedCases.SequenceEqual(["checkpoint-reuse"]),
            "only checkpoint checks run on the owned lab role with backing-cache restoration");
        MapContracts();
        Console.WriteLine("Checkpoint changed-region, full verification, interruption, export and identity contracts passed.");
    }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Invalid checkpoint evidence accepted.");
    }
    private static void MapContracts()
    {
        var hashes = new byte[16 * ImageChunkMap.HashBytes];
        var map = new ImageChunkMap(Capacity, new string('A', 64), hashes, 20 * MiB, DateTime.UtcNow.Ticks,
            ImageChunkMap.FileKeyHash(@"C:\Images\one|001122"));
        var bytes = map.Serialize(); var parsed = ImageChunkMap.Parse(bytes);
        Check(parsed is not null && parsed.CapacityBytes == map.CapacityBytes && parsed.Hashes.AsSpan().SequenceEqual(hashes) &&
            parsed.FileKeySha256 == map.FileKeySha256, "checksummed map round trip");
        Check(ImageChunkMap.FileKeyHash(@"C:\Images\one|AABBCC") == ImageChunkMap.FileKeyHash(@"C:\Images\two|aabbcc"),
            "file-key binding ignores a hardlink's path and hex letter case");
        bytes[100] ^= 1; Check(ImageChunkMap.Parse(bytes) is null, "corrupt map rejected");
        bytes = map.Serialize(); System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), ulong.MaxValue);
        SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)).CopyTo(bytes, bytes.Length - 32);
        Check(ImageChunkMap.Parse(bytes) is null, "hostile capacity rejected without integer overflow or allocation");
        Check(ImageChunkMap.Parse(map.Serialize().AsSpan(0, 127)) is null, "truncated map rejected");
    }

    private sealed class Fixture : IManagedCheckpointBackend
    {
        private readonly string? fault;
        public readonly ManagedDiskTests.MemoryDisk Source = new(Capacity);
        public readonly byte[] CurrentBytes = Enumerable.Repeat((byte)0x17, Capacity).ToArray();
        public readonly TrackedDisk Candidate = new();
        private readonly TrackedDisk full = new();
        public readonly Store Store;
        public readonly ManagedImageReference Current;
        public readonly string OlderPath, RequestedPath;
        private readonly byte[] hashes;
        public bool Frozen, MapSaved;
        public int OpenReuseCalls;
        public Fixture(string? fault = null, bool change = true)
        {
            this.fault = fault;
            Source.Bytes.AsSpan().Fill(0x17); Candidate.Data.Bytes.AsSpan().Fill(0x17);
            hashes = new byte[16 * 32];
            for (var i = 0; i < 16; i++) SHA256.HashData(Candidate.Data.Bytes.AsSpan(i * MiB, MiB), hashes.AsSpan(i * 32, 32));
            if (change) Source.Bytes.AsSpan(MiB, MiB).Fill(0x33);
            if (fault is "partial-read" or "reporting-read") Source.ReadFailureAt = 8UL * MiB;
            if (fault == "stale-map") Candidate.Data.Bytes[0] = 0x99;
            var definition = ManagedDiskDefinition.New(ManagedDiskMode.ImageInRam) with
            { CapacityBytes = Capacity, ImagePath = @"C:\Images\source.vhdx", CheckpointDirectory = @"C:\Images" };
            OlderPath = $@"C:\Images\{definition.ResourceId:N}-older.vhdx";
            RequestedPath = $@"C:\Images\{definition.ResourceId:N}-new.vhdx";
            var digest = new LogicalImageDigest(Capacity, Convert.ToHexString(SHA256.HashData(CurrentBytes)));
            Current = new(new($@"C:\Images\{definition.ResourceId:N}-current.vhdx", "current-key", Guid.NewGuid(), Capacity, Capacity, 512, false), digest, 1);
            var older = new ManagedImageReference(new(OlderPath, "older-key", Guid.NewGuid(), Capacity, Capacity, 512, false), digest, 0)
            { GeneratedCheckpoint = true };
            Store = new(new(definition, new(definition.ResourceId, Guid.NewGuid(), 1, definition.Mode, ManagedDiskState.Ready, 2, 1), Current, older));
            Candidate.BeforeWrite = () =>
            {
                Check(Store.Record.PreviousImage is null && Store.Journal?.CandidatePath == OlderPath,
                    "catalog and journal stop exposing the consumed predecessor before mutation");
                if (fault == "reporting-read") Store.FailRead = true;
            };
        }
        public Task<ManagedCheckpointResult> Save(bool allowReuse = true) => new ManagedImageCheckpoint(Store, this)
            .SaveAsync(Store.Record, RequestedPath, true, allowReuse: allowReuse);
        public Task<IStableManagedImage> AcquireAsync(ManagedDiskRecord record, Guid operation, CancellationToken token)
        { Frozen = true; return Task.FromResult<IStableManagedImage>(new Stable(this)); }
        public Task<IReusedCheckpoint?> OpenReusedAsync(ManagedDiskRecord record, CancellationToken token)
        {
            OpenReuseCalls++;
            return Task.FromResult<IReusedCheckpoint?>(ManagedCheckpointReuse.Candidate(record) is null ? null : new Reused(this));
        }
        public Task<ICheckpointImage> CreateCandidateAsync(ManagedDiskDefinition definition, string path, CancellationToken token) =>
            Task.FromResult<ICheckpointImage>(new Full(full));
        public async Task<ImageInspection> VerifyCandidateAsync(string path, LogicalImageDigest digest, CancellationToken token, IProgress<ImageTransferProgress>? progress = null)
        {
            var disk = path == OlderPath ? Candidate : full;
            Check(disk.Data.Flushed, "verification starts after candidate flush");
            await LogicalImageTransfer.VerifyAsync(disk, digest, token);
            return path == OlderPath ? Store.Initial.PreviousImage!.Identity : new(path, "new-key", Guid.NewGuid(), Capacity, Capacity, 512, false);
        }
        public void SaveChunkMap(ManagedDiskRecord record, ImageInspection identity, ChunkedImageCopy copy) => MapSaved = true;
        private sealed class Stable(Fixture owner) : IStableManagedImage
        {
            public ulong Generation => 2;
            public ILogicalDisk Storage => owner.Source;
            public ValueTask DisposeAsync() { owner.Frozen = false; return ValueTask.CompletedTask; }
        }
        private sealed class Reused(Fixture owner) : IReusedCheckpoint
        {
            public string Path => owner.OlderPath;
            public ReadOnlyMemory<byte> Hashes => owner.hashes;
            public ulong HeaderBytesWritten => owner.Candidate.Writes == 0 ? 0UL : 8192UL;
            public bool CanWrite(ulong offset, int bytes) => owner.fault switch
            {
                "sparse-unchanged" => offset != 0,
                "sparse-growth" => offset != MiB,
                _ => true
            };
            public ILogicalDisk Storage => owner.Candidate;
            public Task CompleteAndDetachAsync(CancellationToken token) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
        private sealed class Full(TrackedDisk disk) : ICheckpointImage
        {
            public ILogicalDisk Storage => disk;
            public Task CompleteAndDetachAsync(CancellationToken token) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class Store(ManagedDiskRecord initial) : IManagedDiskRecordStore
    {
        public ManagedDiskRecord Initial => initial;
        public ManagedDiskRecord Record = initial;
        public ManagedDiskJournal? Journal;
        public bool FailRead;
        public ManagedDiskRecord Read(Guid id) => FailRead ? throw new IOException("Reporting read failed.") : Record;
        public ManagedDiskJournal? ReadJournal(Guid id) => Journal;
        public void Save(ManagedDiskRecord record) => Record = record;
        public void SaveJournal(ManagedDiskJournal journal) => Journal = journal;
    }
    private sealed class TrackedDisk : ILogicalDisk
    {
        public readonly ManagedDiskTests.MemoryDisk Data = new(Capacity);
        public Action? BeforeWrite;
        public ulong Writes, ReadBytes;
        public ulong CapacityBytes => Capacity;
        public uint SectorBytes => 512;
        public async ValueTask<int> ReadAsync(ulong offset, Memory<byte> buffer, CancellationToken token)
        { var read = await Data.ReadAsync(offset, buffer, token); ReadBytes += (ulong)read; return read; }
        public async ValueTask WriteAsync(ulong offset, ReadOnlyMemory<byte> bytes, CancellationToken token)
        { BeforeWrite?.Invoke(); await Data.WriteAsync(offset, bytes, token); Writes++; }
        public ValueTask FlushAsync(CancellationToken token) => Data.FlushAsync(token);
    }
}
