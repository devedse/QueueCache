namespace QueueCache.Operations.ManagedDisks;

/// <summary>Resource-scoped logical-image and explicit host-flush attempts for one broker observation epoch.</summary>
public sealed record ManagedImageIoSnapshot(Guid ObservationEpoch, ulong ReadAttempts, ulong WriteAttempts,
    ulong FlushAttempts, ulong ReadBytes, ulong WrittenBytes);

public sealed class ManagedImageIo
{
    private readonly object gate = new();
    private ManagedImageIoSnapshot state = new(Guid.NewGuid(), 0, 0, 0, 0, 0);
    public ManagedImageIoSnapshot Snapshot() { lock (gate) return state; }
    public void FlushAttempt() { lock (gate) state = state with { FlushAttempts = checked(state.FlushAttempts + 1) }; }
    public ILogicalDisk Measure(ILogicalDisk storage) => new MeasuredDisk(this, storage);
    private sealed class MeasuredDisk(ManagedImageIo owner, ILogicalDisk storage) : ILogicalDisk
    {
        public ulong CapacityBytes => storage.CapacityBytes;
        public uint SectorBytes => storage.SectorBytes;
        public async ValueTask<int> ReadAsync(ulong offset, Memory<byte> bytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (owner.gate) owner.state = owner.state with { ReadAttempts = checked(owner.state.ReadAttempts + 1) };
            var completed = await storage.ReadAsync(offset, bytes, token);
            if (completed < 0 || completed > bytes.Length) throw new InvalidDataException("Invalid measured image read completion.");
            lock (owner.gate) owner.state = owner.state with { ReadBytes = checked(owner.state.ReadBytes + (ulong)completed) };
            return completed;
        }
        public async ValueTask WriteAsync(ulong offset, ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (owner.gate) owner.state = owner.state with { WriteAttempts = checked(owner.state.WriteAttempts + 1) };
            await storage.WriteAsync(offset, bytes, token);
            lock (owner.gate) owner.state = owner.state with { WrittenBytes = checked(owner.state.WrittenBytes + (ulong)bytes.Length) };
        }
        public async ValueTask FlushAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); owner.FlushAttempt(); await storage.FlushAsync(token);
        }
    }
}
