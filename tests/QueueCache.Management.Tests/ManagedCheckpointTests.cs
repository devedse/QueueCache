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
        var cancelled = new Fixture("cancel-before");
        try { await cancelled.Save(); throw new Exception("Cancelled precommit save reported success."); } catch (IOException) { }
        Check(cancelled.Store.Record.CommittedImage == cancelled.Original && !cancelled.Frozen && !cancelled.Removed, "precommit cancellation retains old image and live writable RAM");
        var late = new Fixture("cancel-after"); var saved = await late.Save();
        Check(saved.ChangedStartupSource && late.Cancel.IsCancellationRequested && saved.Record.Runtime!.SavedGeneration == 10 && !late.Frozen,
            "late cancellation cannot claim rollback after pointer commit starts");
        Console.WriteLine("Managed checkpoint failure/commit/stop contracts passed.");
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
        public Fixture(string? fault)
        {
            this.fault = fault; Source.Bytes[0] = 0xA5; Source.Bytes[^1] = 0x5A;
            var definition = ManagedDiskDefinition.New(ManagedDiskMode.ImageInRam) with
            { CapacityBytes = 16UL << 20, ImagePath = @"C:\Images\source.vhdx", CheckpointDirectory = @"C:\Images" };
            Original = new(new(definition.ImagePath!, "source-id", Guid.NewGuid(), definition.CapacityBytes, 1UL << 20, 512, false), new(definition.CapacityBytes, new string('A', 64)), 8);
            Store = new(new(definition, new(definition.ResourceId, Guid.NewGuid(), 1, definition.Mode, ManagedDiskState.Ready, 10, 8), Original), Original, fault, Cancel);
            if (fault == "copy") Source.ReadFailureAt = 8UL << 20;
        }
        public Task<ManagedCheckpointResult> Save(bool commit = true, bool stop = false) => new ManagedImageCheckpoint(Store, this)
            .SaveAsync(Store.Record, @"C:\Images\candidate.vhdx", commit, token: Cancel.Token, stopAfterSave: stop);
        public Task<IStableManagedImage> AcquireAsync(ManagedDiskRecord record, Guid operation, CancellationToken token)
        { if (fault == "acquire") throw new IOException("Open-file veto."); Frozen = true; return Task.FromResult<IStableManagedImage>(new Stable(this)); }
        public Task<ICheckpointImage> CreateCandidateAsync(ManagedDiskDefinition definition, string path, CancellationToken token) => Task.FromResult<ICheckpointImage>(new Candidate(this));
        public async Task<ImageInspection> VerifyCandidateAsync(string path, LogicalImageDigest digest, CancellationToken token)
        {
            Check(destination.Flushed, "verification happens only after destination flush/detach");
            if (fault == "verify" || fault?.StartsWith("reporting-", StringComparison.Ordinal) == true)
            { destination.Bytes[^1] ^= 1; Store.Reporting = true; }
            await LogicalImageTransfer.VerifyAsync(destination, digest, token);
            if (fault == "cancel-before") Cancel.Cancel();
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
            { if (owner.fault is "release" or "release-reporting") { owner.Store.Reporting = true; throw new IOException("Unfreeze failed."); } owner.Frozen = false; return ValueTask.CompletedTask; }
        }
        private sealed class Candidate(Fixture owner) : ICheckpointImage
        {
            public ILogicalDisk Storage => owner.destination;
            public Task CompleteAndDetachAsync(CancellationToken token)
            { if (owner.fault == "flush") throw new IOException("Host flush failed."); return Task.CompletedTask; }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
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
