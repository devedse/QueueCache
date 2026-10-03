namespace QueueCache.Operations.ManagedDisks;

public interface IStableManagedImage : IAsyncDisposable
{
    ulong Generation { get; }
    ILogicalDisk Storage { get; }
}
public interface ICheckpointImage : IAsyncDisposable
{
    ILogicalDisk Storage { get; }
    Task CompleteAndDetachAsync(CancellationToken token);
}
public interface IManagedCheckpointBackend
{
    Task<IStableManagedImage> AcquireAsync(ManagedDiskRecord record, Guid operation, CancellationToken token);
    Task<ICheckpointImage> CreateCandidateAsync(ManagedDiskDefinition definition, string path, CancellationToken token);
    Task<ImageInspection> VerifyCandidateAsync(string path, LogicalImageDigest digest, CancellationToken token);
    Task StopFrozenAsync(ManagedDiskRecord record, IStableManagedImage stable, CancellationToken token) => throw new NotSupportedException("Atomic save and stop is unavailable.");
}
public sealed record ManagedCheckpointResult(ManagedDiskRecord Record, ManagedImageReference Image, bool ChangedStartupSource);

/// <summary>One full-sector save/export transaction; pointer commits begin only after detached read-back verification.</summary>
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
            LogicalImageDigest digest;
            await using (var destination = await backend.CreateCandidateAsync(record.Definition, path, token))
            {
                digest = await LogicalImageTransfer.CopyAsync(stable.Storage, destination.Storage,
                    progress is null ? null : new TransferProgress(progress), token);
                await destination.CompleteAndDetachAsync(token);
            }
            progress?.Report(new(ManagedDiskState.Saving, "Verifying every saved logical sector before committing the image."));
            var identity = await backend.VerifyCandidateAsync(path, digest, token);
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
                    Runtime = next.Runtime!.RecordSaved(stable.Generation), SavedAt = DateTimeOffset.UtcNow, LastError = null };
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
        if (failure is not null) throw new IOException(committed ? "Image committed; final cleanup, stop or catalog reconciliation failed. Inspect the actual disk state before retrying." : "Save failed. The live RAM disk and preceding committed image are retained.", failure);
        return new(record, candidate ?? throw new InvalidDataException("Missing committed checkpoint."), commit);
        void TryRecord(Action action)
        {
            try { action(); }
            catch (Exception reporting) { failure = failure is null ? reporting : new AggregateException(failure, reporting); }
        }
    }
    private sealed class TransferProgress(IProgress<ManagedDiskProgress> progress) : IProgress<ImageTransferProgress>
    { public void Report(ImageTransferProgress value) => progress.Report(new(ManagedDiskState.Saving, "Copying the complete frozen RAM disk.", value.CompletedBytes, value.TotalBytes)); }
}
