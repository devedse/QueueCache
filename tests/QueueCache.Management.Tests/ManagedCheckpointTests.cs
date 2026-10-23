using System.Security.Cryptography;
using QueueCache.Operations.ManagedDisks;

internal static class ManagedCheckpointTests
{
    public static async Task RunAsync()
    {
        foreach (var stage in new[] { "acquire", "copy", "flush", "verify", "verified-journal", "pointer-before", "pointer-after", "committed-journal", "release" })
        {
            var fixture = new Fixture(stage);
            try { await fixture.Save(); throw new Exception("Save failure was reported as success: " + stage); }
            catch (IOException) { }
            var pointerChanged = stage is "pointer-after" or "committed-journal" or "release";
            Check((fixture.Store.Record.CommittedImage != fixture.Original) == pointerChanged, "only the commit boundary can change the startup pointer: " + stage);
            Check(fixture.Source.Bytes[0] == 0xA5 && fixture.Source.Bytes[^1] == 0x5A && !fixture.Removed, "source, old checkpoint and live RAM retained on failure: " + stage);
            Check(fixture.Frozen == (stage == "release"), "independent stable-view cleanup restores writable RAM unless it actually fails: " + stage);
            if (stage is "pointer-before" or "pointer-after" or "release")
                Check(fixture.Store.Record.Runtime!.State == ManagedDiskState.RecoveryRequired, "uncertain pointer/cleanup requires recovery: " + stage);
            else Check(fixture.Store.Record.Runtime!.State == ManagedDiskState.Ready, "precommit failure/committed journal error retains truthful mounted state: " + stage);
            Check(fixture.Store.Journal?.CandidatePath == @"C:\Images\candidate.vhdx", "candidate path remains in the recovery journal even before verification");
        }
        var export = new Fixture(null); var exported = await export.Save(commit: false);
        foreach (var stage in new[] { "reporting-read", "reporting-save", "reporting-journal", "release-reporting" })
        {
            var brokenCatalog = new Fixture(stage);
            try { await brokenCatalog.Save(); throw new Exception("Reporting failure was hidden: " + stage); }
            catch (IOException failure)
            {
                var messages = failure.ToString();
                Check(messages.Contains("reporting") && messages.Contains(stage == "release-reporting" ? "Unfreeze failed" : "logical sectors"),
                    "catalog/reporting failure preserves the original operation or cleanup failure: " + stage);
            }
            Check(!brokenCatalog.Removed && brokenCatalog.Frozen == (stage == "release-reporting"),
                "catalog failure cannot skip independent thaw or silently remove RAM: " + stage);
        }
        Check(!exported.ChangedStartupSource && export.Store.Record.CommittedImage == export.Original && export.Store.Record.Runtime!.HasUnsavedChanges,
            "Save As preserves the startup source, saved generation and dirty state");
        var stop = new Fixture(null); var stopped = await stop.Save(stop: true);
        Check(stop.Removed && !stop.Frozen && stopped.Record.Runtime!.State == ManagedDiskState.Stopped && stopped.Record.Runtime.SavedGeneration == 10,
            "save and stop removes RAM while the exact committed generation is still frozen");
        foreach (var stage in new[] { "cancel-copy", "cancel-before" })
        {
            var cancelled = new Fixture(stage);
            try { await cancelled.Save(); throw new Exception("Cancelled precommit save reported success."); }
            catch (OperationCanceledException ex) { Check(ex.CancellationToken == cancelled.Cancel.Token, "save cancellation preserves the request token"); }
            Check(cancelled.Store.Record.CommittedImage == cancelled.Original && !cancelled.Frozen && !cancelled.Removed &&
                cancelled.Store.Record.Runtime!.State == ManagedDiskState.Ready,
                "precommit cancellation reports cancellation and retains old image and live writable RAM: " + stage);
        }
        var failedCancellationCleanup = new Fixture("cancel-release");
        try { await failedCancellationCleanup.Save(); throw new Exception("Cancellation cleanup failure was hidden."); }
        catch (IOException ex) { Check(ex.ToString().Contains("Unfreeze failed"), "cancellation cannot mask a teardown failure"); }
        Check(failedCancellationCleanup.Frozen && failedCancellationCleanup.Store.Record.Runtime!.State == ManagedDiskState.RecoveryRequired &&
            failedCancellationCleanup.Store.Record.CommittedImage == failedCancellationCleanup.Original,
            "failed cancellation cleanup reports recovery and preserves the preceding checkpoint");
        var late = new Fixture("cancel-after"); var saved = await late.Save();
        Check(saved.ChangedStartupSource && late.Cancel.IsCancellationRequested && saved.Record.Runtime!.SavedGeneration == 10 && !late.Frozen,
            "late cancellation cannot claim rollback after pointer commit starts");
        await InPlaceSaves();
        Console.WriteLine("Managed checkpoint failure/commit/stop contracts passed.");
    }
    /// <summary>#22: a save writes into the older retained image only the 1 MiB chunks that changed.</summary>
    private static async Task InPlaceSaves()
    {
        const int capacity = (16 << 20) + 4096; // 17 chunks; the last one is partial.
        var ram = new ManagedDiskTests.MemoryDisk(capacity) { MaxRead = 3 << 20 };
        new Random(22).NextBytes(ram.Bytes);
        var image = new ManagedDiskTests.MemoryDisk(capacity);
        var full = await LogicalImageTransfer.CopyChangedAsync(ram, image, null);
        Check(image.Bytes.SequenceEqual(ram.Bytes) && image.Flushed && full.Written.Count == 17 && full.WrittenBytes == capacity &&
            full.Digest == await LogicalImageTransfer.HashAsync(ram) && full.Hashes.Length == 17 * 32 &&
            full.Hashes.AsSpan(16 * 32, 32).SequenceEqual(SHA256.HashData(ram.Bytes.AsSpan(16 << 20))),
            "a full save writes every chunk and records the logical digest and each chunk's hash");
        ram.Bytes[3 << 20] ^= 1; ram.Bytes[(4 << 20) + 7] ^= 1; ram.Bytes[^1] ^= 1;
        var changed = await LogicalImageTransfer.CopyChangedAsync(ram, image, full.Hashes);
        Check(changed.Written.SequenceEqual([3, 4, 16]) && changed.WrittenBytes == (2UL << 20) + 4096 && image.Bytes.SequenceEqual(ram.Bytes) &&
            changed.Digest == await LogicalImageTransfer.HashAsync(ram), "a save into a known image writes only the changed chunks, joined, and the result equals RAM");
        await LogicalImageTransfer.VerifyChunksAsync(image, changed.Hashes, changed.Written);
        image.Bytes[(4 << 20) + 9] ^= 1;
        await RejectAsync<InvalidDataException>(() => LogicalImageTransfer.VerifyChunksAsync(image, changed.Hashes, changed.Written), "a rewritten chunk that reads back wrong fails verification");
        await RejectAsync<InvalidDataException>(() => LogicalImageTransfer.CopyChangedAsync(ram, image, full.Hashes.AsMemory(32)), "a chunk map for another capacity is refused");
        var map = new ImageChunkMap(capacity, changed.Digest.Sha256, changed.Hashes, 123456, 638000000000000000);
        var bytes = map.Serialize();
        var parsed = ImageChunkMap.Parse(bytes);
        Check(parsed is not null && parsed.Sha256 == map.Sha256 && parsed.Hashes.SequenceEqual(map.Hashes) && parsed.FileBytes == 123456 &&
            parsed.FileWriteTicks == map.FileWriteTicks && parsed.CapacityBytes == capacity, "a chunk map round-trips");
        bytes[100] ^= 1;
        Check(ImageChunkMap.Parse(bytes) is null && ImageChunkMap.Parse(bytes.AsSpan(0, 90)) is null, "a damaged or short chunk map is not used");

        var reuse = new Fixture(null, reuse: true); var saved = await reuse.Save();
        Check(reuse.Older.Bytes.SequenceEqual(reuse.Source.Bytes) && reuse.PreviousReleasedBeforeWrite && reuse.Map?.Path == Fixture.OlderPath &&
            saved.Record.CommittedImage!.Identity.Path == Fixture.OlderPath && saved.Record.PreviousImage == reuse.Original &&
            saved.Record.LastSave!.Bytes == 2UL << 20 && reuse.Store.Journal!.CandidatePath == Fixture.OlderPath,
            "a save rewrites the two changed chunks of the older image, which becomes the startup image; the previous one is retained");
        var failed = new Fixture("copy", reuse: true);
        try { await failed.Save(); throw new Exception("A failed in-place save reported success."); }
        catch (IOException) { }
        Check(failed.Store.Record.CommittedImage == failed.Original && failed.Store.Record.PreviousImage is null &&
            failed.Store.Record.Runtime!.State == ManagedDiskState.Ready && !failed.Frozen,
            "a failed in-place save keeps the startup image, and the partly rewritten older image is no longer retained");
        var export = new Fixture(null, reuse: true); await export.Save(commit: false);
        Check(export.Store.Record.PreviousImage is not null && export.Older.Bytes.All(b => b == 0) && export.Map is null,
            "Save As never writes into the older image");
    }
    private static async Task RejectAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("FAIL: " + message);
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); }
    private sealed class Fixture : IManagedCheckpointBackend
    {
        public readonly ManagedDiskTests.MemoryDisk Source = new(16 << 20);
        private readonly ManagedDiskTests.MemoryDisk destination = new(16 << 20);
        public readonly CancellationTokenSource Cancel = new();
        public readonly FakeStore Store;
        public readonly ManagedImageReference Original;
        public bool Frozen, Removed;
        private readonly string? fault;
        // #22: the older retained image (an earlier generation, all zero) and its chunk map.
        public const string OlderPath = @"C:\Images\older.vhdx";
        public readonly ManagedDiskTests.MemoryDisk Older = new(16 << 20);
        private readonly byte[] olderHashes = new byte[16 * 32];
        private readonly Guid olderDisk = Guid.NewGuid();
        private readonly bool reuse;
        public (string Path, ChunkedCopy Copy)? Map;
        public bool PreviousReleasedBeforeWrite = true;
        public Fixture(string? fault, bool reuse = false)
        {
            this.fault = fault; this.reuse = reuse; Source.Bytes[0] = 0xA5; Source.Bytes[^1] = 0x5A;
            var definition = ManagedDiskDefinition.New(ManagedDiskMode.ImageInRam) with
            { CapacityBytes = 16UL << 20, ImagePath = @"C:\Images\source.vhdx", CheckpointDirectory = @"C:\Images" };
            Original = new(new(definition.ImagePath!, "source-id", Guid.NewGuid(), definition.CapacityBytes, 1UL << 20, 512, false), new(definition.CapacityBytes, new string('A', 64)), 8);
            var older = reuse ? new ManagedImageReference(new(OlderPath, "older-id", olderDisk, definition.CapacityBytes, 1UL << 20, 512, false), new(definition.CapacityBytes, new string('B', 64)), 6) : null;
            Store = new(new(definition, new(definition.ResourceId, Guid.NewGuid(), 1, definition.Mode, ManagedDiskState.Ready, 10, 8), Original, older), Original, fault, Cancel);
            if (fault == "copy") Source.ReadFailureAt = 8UL << 20;
            for (var i = 0; i < 16; i++) SHA256.HashData(Older.Bytes.AsSpan(i << 20, 1 << 20), olderHashes.AsSpan(i * 32, 32));
        }
        public Task<IReusedCheckpoint?> OpenReusedAsync(ManagedDiskRecord record, CancellationToken token) =>
            Task.FromResult<IReusedCheckpoint?>(reuse && record.PreviousImage is not null ? new Reused(this) : null);
        public async Task<ImageInspection> VerifyChunksAsync(string path, ChunkedCopy copy, CancellationToken token, IProgress<ImageTransferProgress>? progress = null)
        {
            Check(Older.Flushed && path == OlderPath, "in-place verification reads the older image after its flush/detach");
            await LogicalImageTransfer.VerifyChunksAsync(Older, copy.Hashes, copy.Written, token);
            return new(path, "older-id", olderDisk, copy.Digest.Bytes, copy.Digest.Bytes, 512, false);
        }
        public void SaveChunkMap(ManagedDiskRecord record, string path, ChunkedCopy copy) => Map = (path, copy);
        private sealed class Reused(Fixture owner) : IReusedCheckpoint, ILogicalDisk
        {
            public string Path => OlderPath;
            public ReadOnlyMemory<byte> Hashes => owner.olderHashes;
            public ILogicalDisk Storage => this;
            public ulong CapacityBytes => owner.Older.CapacityBytes;
            public uint SectorBytes => owner.Older.SectorBytes;
            public ValueTask<int> ReadAsync(ulong offset, Memory<byte> buffer, CancellationToken token) => owner.Older.ReadAsync(offset, buffer, token);
            public ValueTask WriteAsync(ulong offset, ReadOnlyMemory<byte> buffer, CancellationToken token)
            {
                if (owner.Store.Record.PreviousImage is not null) owner.PreviousReleasedBeforeWrite = false;
                return owner.Older.WriteAsync(offset, buffer, token);
            }
            public ValueTask FlushAsync(CancellationToken token) => owner.Older.FlushAsync(token);
            public Task CompleteAndDetachAsync(CancellationToken token) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
        public Task<ManagedCheckpointResult> Save(bool commit = true, bool stop = false) => new ManagedImageCheckpoint(Store, this)
            .SaveAsync(Store.Record, @"C:\Images\candidate.vhdx", commit,
                progress: new InlineProgress(value => { if (fault == "cancel-copy" && value.CompletedBytes > 0) Cancel.Cancel(); }),
                token: Cancel.Token, stopAfterSave: stop);
        public Task<IStableManagedImage> AcquireAsync(ManagedDiskRecord record, Guid operation, CancellationToken token)
        { if (fault == "acquire") throw new IOException("Open-file veto."); Frozen = true; return Task.FromResult<IStableManagedImage>(new Stable(this)); }
        public Task<ICheckpointImage> CreateCandidateAsync(ManagedDiskDefinition definition, string path, CancellationToken token) => Task.FromResult<ICheckpointImage>(new Candidate(this));
        public async Task<ImageInspection> VerifyCandidateAsync(string path, LogicalImageDigest digest, CancellationToken token, IProgress<ImageTransferProgress>? progress = null)
        {
            Check(destination.Flushed, "verification happens only after destination flush/detach");
            if (fault == "verify" || fault?.StartsWith("reporting-", StringComparison.Ordinal) == true)
            { destination.Bytes[^1] ^= 1; Store.Reporting = true; }
            await LogicalImageTransfer.VerifyAsync(destination, digest, token);
            if (fault is "cancel-before" or "cancel-release") Cancel.Cancel();
            return new(path, "candidate-id", Guid.NewGuid(), digest.Bytes, digest.Bytes, 512, false);
        }
        public Task StopFrozenAsync(ManagedDiskRecord record, IStableManagedImage stable, CancellationToken token)
        {
            Check(Frozen && stable.Generation == Store.Record.Runtime!.SavedGeneration && Store.Record.CommittedImage != Original, "stop follows durable commit without thawing first");
            Removed = true; Store.Save(record with { Runtime = record.Runtime! with { State = ManagedDiskState.Stopped } }); return Task.CompletedTask;
        }
        private sealed class Stable(Fixture owner) : IStableManagedImage
        {
            public ulong Generation => 10;
            public ILogicalDisk Storage => owner.Source;
            public ValueTask DisposeAsync()
            { if (owner.fault is "release" or "release-reporting" or "cancel-release") { owner.Store.Reporting = true; throw new IOException("Unfreeze failed."); } owner.Frozen = false; return ValueTask.CompletedTask; }
        }
        private sealed class Candidate(Fixture owner) : ICheckpointImage
        {
            public ILogicalDisk Storage => owner.destination;
            public Task CompleteAndDetachAsync(CancellationToken token)
            { if (owner.fault == "flush") throw new IOException("Host flush failed."); return Task.CompletedTask; }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class InlineProgress(Action<ManagedDiskProgress> action) : IProgress<ManagedDiskProgress>
    { public void Report(ManagedDiskProgress value) => action(value); }
    private sealed class FakeStore(ManagedDiskRecord initial, ManagedImageReference original, string? fault, CancellationTokenSource cancel) : IManagedDiskRecordStore
    {
        public ManagedDiskRecord Record = initial;
        public ManagedDiskJournal? Journal;
        private bool injected;
        public bool Reporting;
        public ManagedDiskRecord Read(Guid id) => Reporting && fault is "reporting-read" or "release-reporting" ? throw new IOException("Injected catalog reporting read failure.") : Record;
        public ManagedDiskJournal? ReadJournal(Guid id) => Journal;
        public void Save(ManagedDiskRecord record)
        {
            if (Reporting && fault == "reporting-save") throw new IOException("Injected catalog reporting save failure.");
            var pointer = record.CommittedImage != original;
            if (!injected && pointer && fault == "pointer-before") { injected = true; throw new IOException("Pointer barrier failed before replace."); }
            Record = record;
            if (!injected && pointer && fault == "pointer-after") { injected = true; throw new IOException("Pointer barrier failed after replace."); }
            if (pointer && fault == "cancel-after") cancel.Cancel();
        }
        public void SaveJournal(ManagedDiskJournal journal)
        {
            if (Reporting && fault == "reporting-journal") throw new IOException("Injected catalog reporting journal failure.");
            if (!injected && ((journal.Stage == ManagedDiskJournalStage.CandidateVerified && fault == "verified-journal") ||
                (journal.Stage == ManagedDiskJournalStage.Committed && fault == "committed-journal")))
            { injected = true; throw new IOException("Journal barrier failed."); }
            Journal = journal;
        }
    }
}
