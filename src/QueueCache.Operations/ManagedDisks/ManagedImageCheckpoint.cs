namespace QueueCache.Operations.ManagedDisks;

public interface IStableManagedImage : IAsyncDisposable
{
    ulong Generation { get; }
    ILogicalDisk Storage { get; }
    /// <summary>See <see cref="ManagedDiskRecord.WrittenBytes"/>, when frozen.</summary>
    ulong? WrittenBytes => null;
}
public interface ICheckpointImage : IAsyncDisposable
{
    ILogicalDisk Storage { get; }
    Task CompleteAndDetachAsync(CancellationToken token);
}
/// <summary>The retained older checkpoint, opened to be written in place (#22): <see cref="Hashes"/> is its
/// chunk map, so a save writes only the chunks that differ. Its chunk map file is already removed.</summary>
public interface IReusedCheckpoint : ICheckpointImage
{
    string Path { get; }
    ReadOnlyMemory<byte> Hashes { get; }
}
public interface IManagedCheckpointBackend
{
    Task<IStableManagedImage> AcquireAsync(ManagedDiskRecord record, Guid operation, CancellationToken token);
    Task<ICheckpointImage> CreateCandidateAsync(ManagedDiskDefinition definition, string path, CancellationToken token);
    Task<ImageInspection> VerifyCandidateAsync(string path, LogicalImageDigest digest, CancellationToken token, IProgress<ImageTransferProgress>? progress = null);
    Task StopFrozenAsync(ManagedDiskRecord record, IStableManagedImage stable, CancellationToken token) => throw new NotSupportedException("Atomic save and stop is unavailable.");
    /// <summary>The record's retained previous checkpoint opened for an in-place save (#22), or null when it cannot
    /// be: not a checkpoint this disk created, no intact chunk map for its digest and file, or not isolated.</summary>
    Task<IReusedCheckpoint?> OpenReusedAsync(ManagedDiskRecord record, CancellationToken token) => Task.FromResult<IReusedCheckpoint?>(null);
    /// <summary>Reads back the chunks a save wrote in place and returns the image's identity (#22).</summary>
    Task<ImageInspection> VerifyChunksAsync(string path, ChunkedCopy copy, CancellationToken token, IProgress<ImageTransferProgress>? progress = null) =>
        throw new NotSupportedException("In-place saves are unavailable.");
    /// <summary>Keeps the verified image's chunk map beside a checkpoint this disk owns, for a later in-place save;
    /// a failure only makes that save a full one.</summary>
    void SaveChunkMap(ManagedDiskRecord record, string path, ChunkedCopy copy) { }
}
public sealed record ManagedCheckpointResult(ManagedDiskRecord Record, ManagedImageReference Image, bool ChangedStartupSource);

/// <summary>Which image a save may write into in place (#22).</summary>
public static class ManagedCheckpointReuse
{
    /// <summary>A save this disk created in its checkpoint folder, named after the disk. An imported or user-chosen
    /// image is never there under this name, so it is never written, also when the disk was last started from it.</summary>
    public static bool OwnedCheckpoint(ManagedDiskDefinition definition, string path)
    {
        // Windows paths, split by hand so the host tests give the same answer on Linux.
        var split = path.LastIndexOf('\\');
        return definition.CheckpointDirectory is { } directory && split > 0 &&
            path[..split].Equals(directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) &&
            path[(split + 1)..].StartsWith(definition.ResourceId.ToString("N") + "-", StringComparison.OrdinalIgnoreCase);
    }
    /// <summary>The retained previous save, when it is such a checkpoint and not the startup image; otherwise null.</summary>
    public static string? Candidate(ManagedDiskRecord record) =>
        record.PreviousImage is { Digest: not null } previous && OwnedCheckpoint(record.Definition, previous.Identity.Path) &&
        record.CommittedImage?.Identity.SameImage(previous.Identity) != true ? previous.Identity.Path : null;
}

/// <summary>One save/export transaction; pointer commits begin only after detached read-back verification. A save
/// writes into the retained older checkpoint only the chunks that changed, when its chunk map allows (#22);
/// otherwise it writes a complete new image.</summary>
public sealed class ManagedImageCheckpoint(IManagedDiskRecordStore store, IManagedCheckpointBackend backend)
{
    public async Task<ManagedCheckpointResult> SaveAsync(ManagedDiskRecord record, string path, bool commit,
        IProgress<ManagedDiskProgress>? progress = null, CancellationToken token = default, bool stopAfterSave = false)
    {
        if (record.Definition.Mode != ManagedDiskMode.ImageInRam || record.Runtime is null ||
            record.Runtime.State is not (ManagedDiskState.Ready or ManagedDiskState.Creating or ManagedDiskState.Formatting))
            throw new IOException("Only a ready owned RAM image can be checkpointed.");
        ManagedDiskPaths.ValidateImagePath(path);
        var runtime = record.Runtime;
        var operation = Guid.NewGuid();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var journal = new ManagedDiskJournal(operation, record.ResourceId, ManagedDiskJournalStage.Exporting,
            runtime.BootEpoch, runtime.CreationGeneration, Previous: record.CommittedImage, CandidatePath: path);
        store.SaveJournal(journal);
        progress?.Report(new(ManagedDiskState.Saving, "Locking filesystem and pausing RAM writes for a complete checkpoint."));
        IStableManagedImage? stable = null;
        ManagedImageReference? candidate = null;
        var committed = false;
        var commitStarted = false;
        Exception? failure = null;
        try
        {
            stable = await backend.AcquireAsync(record, operation, token);
            journal = journal with { FrozenGeneration = stable.Generation };
            store.SaveJournal(journal);
            var saving = record with { Runtime = runtime with { State = ManagedDiskState.Saving, WriteGeneration = stable.Generation } };
            store.Save(saving);
            ChunkedCopy copy;
            var reused = commit ? await backend.OpenReusedAsync(record, token) : null;
            await using (var destination = reused ?? await backend.CreateCandidateAsync(record.Definition, path, token))
            {
                if (reused is not null)
                {
                    // The older image stops being a retained checkpoint before its first byte changes.
                    path = reused.Path;
                    journal = journal with { CandidatePath = path };
                    store.SaveJournal(journal);
                    store.Save(saving = saving with { PreviousImage = null });
                }
                copy = await LogicalImageTransfer.CopyChangedAsync(stable.Storage, destination.Storage, reused?.Hashes,
                    progress is null ? null : new TransferProgress(progress, reused is not null), token);
                await destination.CompleteAndDetachAsync(token);
            }
            var digest = copy.Digest;
            progress?.Report(new(ManagedDiskState.Saving, reused is null ? VerifyProgress.Full : VerifyProgress.Changed));
            var verify = progress is null ? null : new VerifyProgress(progress, reused is not null);
            var identity = reused is null ? await backend.VerifyCandidateAsync(path, digest, token, verify)
                : await backend.VerifyChunksAsync(path, copy, token, verify);
            if (commit) backend.SaveChunkMap(record, path, copy);
            progress?.Report(new(ManagedDiskState.Saving, "Saved image verified; committing it."));
            if (identity.VirtualBytes != record.Definition.CapacityBytes || identity.SectorBytes != record.Definition.SectorBytes || identity.Differencing)
                throw new InvalidDataException("The verified checkpoint image geometry changed.");
            candidate = new(identity, digest, stable.Generation);
            journal = journal with { Stage = ManagedDiskJournalStage.CandidateVerified, Candidate = candidate };
            store.SaveJournal(journal);
            token.ThrowIfCancellationRequested();
            // Cancellation cannot roll back a pointer switch once this boundary begins.
            var next = record with { Runtime = runtime with { State = ManagedDiskState.Ready, WriteGeneration = stable.Generation } };
            if (commit)
                next = next with { CommittedImage = candidate, PreviousImage = record.CommittedImage,
                    Runtime = next.Runtime!.RecordSaved(stable.Generation), SavedAt = DateTimeOffset.UtcNow, LastError = null,
                    LastSave = new(started.Elapsed, copy.WrittenBytes, stable.WrittenBytes, runtime.BootEpoch, runtime.CreationGeneration) };
            commitStarted = true;
            store.Save(next);
            committed = true;
            store.SaveJournal(journal with { Stage = ManagedDiskJournalStage.Committed });
            record = next;
            if (stopAfterSave)
            {
                if (!commit) throw new InvalidOperationException("Save and stop must commit the startup image.");
                await backend.StopFrozenAsync(record, stable, CancellationToken.None);
                record = store.Read(record.ResourceId);
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            // Preserve the candidate and original images. A failed pointer barrier is
            // recovery-required, even when a new primary record happens to be readable.
            ManagedDiskRecord actual;
            try { actual = store.Read(record.ResourceId); }
            catch (Exception recovery)
            {
                failure = new AggregateException(failure!, recovery);
                actual = record with { Runtime = record.Runtime! with { State = ManagedDiskState.RecoveryRequired } };
            }
            record = actual with { Runtime = actual.Runtime! with { State = commitStarted && !committed ? ManagedDiskState.RecoveryRequired : actual.Runtime.State == ManagedDiskState.Stopped ? ManagedDiskState.Stopped : ManagedDiskState.Ready },
                LastError = (committed ? "Checkpoint committed; final cleanup or stop failed. " : "Checkpoint did not complete; RAM and the preceding committed image are retained. ") + ex.Message };
            if (failure is AggregateException) record = record with { Runtime = record.Runtime with { State = ManagedDiskState.RecoveryRequired } };
            TryRecord(() => store.Save(record));
            TryRecord(() => store.SaveJournal(journal with { Stage = ManagedDiskJournalStage.RecoveryRequired, Failure = failure!.ToString() }));
        }
        finally
        {
            if (stable is not null)
            {
                try { await stable.DisposeAsync(); }
                catch (Exception cleanup)
                {
                    failure = failure is null ? cleanup : new AggregateException(failure, cleanup);
                    TryRecord(() => record = store.Read(record.ResourceId));
                    TryRecord(() => store.Save(record with { Runtime = record.Runtime! with { State = ManagedDiskState.RecoveryRequired }, LastError = "Stable RAM view could not be released: " + cleanup.Message }));
                }
            }
        }
        if (!commitStarted && failure is OperationCanceledException)
            throw new OperationCanceledException("Save cancelled before commit. The live RAM disk and preceding committed image are retained.", failure, token);
        if (failure is not null) throw new IOException(committed ? "Image committed; final cleanup, stop or catalog reconciliation failed. Inspect the actual disk state before retrying." : "Save failed. The live RAM disk and preceding committed image are retained.", failure);
        return new(record, candidate ?? throw new InvalidDataException("Missing committed checkpoint."), commit);
        void TryRecord(Action action)
        {
            try { action(); }
            catch (Exception reporting) { failure = failure is null ? reporting : new AggregateException(failure, reporting); }
        }
    }
    private sealed class TransferProgress(IProgress<ManagedDiskProgress> progress, bool changed) : IProgress<ImageTransferProgress>
    {
        public void Report(ImageTransferProgress value) => progress.Report(new(ManagedDiskState.Saving,
            changed ? "Writing the parts changed since the older saved image." : "Copying the complete frozen RAM disk.", value.CompletedBytes, value.TotalBytes));
    }
    private sealed class VerifyProgress(IProgress<ManagedDiskProgress> progress, bool changed) : IProgress<ImageTransferProgress>
    {
        public const string Full = "Verifying every saved logical sector before committing the image.", Changed = "Verifying the rewritten parts of the saved image.";
        public void Report(ImageTransferProgress value) => progress.Report(new(ManagedDiskState.Saving, changed ? Changed : Full, value.CompletedBytes, value.TotalBytes));
    }
}
