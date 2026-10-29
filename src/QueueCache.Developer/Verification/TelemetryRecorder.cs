using System.Diagnostics;
using System.Threading.Channels;

namespace QueueCache.Developer.Verification;

public sealed record TelemetryCapture<T>(T Sample, bool Stop);

/// <summary>A persisted-sample callback polls the filesystem; the sampling thread
/// reads only the published flag and still captures a final trailing observation.</summary>
public sealed class TelemetryStopSignal(Func<bool> poll)
{
    private int requested;
    public bool Requested => Volatile.Read(ref requested) != 0;
    public double? LastPollMilliseconds { get; private set; }

    public void Poll()
    {
        var timer = Stopwatch.StartNew();
        var stop = poll();
        LastPollMilliseconds = timer.Elapsed.TotalMilliseconds;
        if (stop) Volatile.Write(ref requested, 1);
    }
}

/// <summary>Observe on an owned sampling thread while serialization and disk writes
/// run separately. Every actual sample must be persisted; capacity exhaustion fails.</summary>
public static class TelemetryRecorder
{
    public const int BufferCapacity = 256;

    public static async Task RecordAsync<T>(Func<double?, TelemetryCapture<T>> capture,
        Func<T, CancellationToken, Task> persist, TimeSpan duration, TimeSpan cadence,
        CancellationToken token = default, int capacity = BufferCapacity)
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));
        if (cadence <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(cadence));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
        var queue = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        var sampler = Task.Factory.StartNew(() =>
        {
            try
            {
                var timer = Stopwatch.StartNew();
                double? delayMilliseconds = null;
                while (timer.Elapsed < duration)
                {
                    stopping.Token.ThrowIfCancellationRequested();
                    var point = capture(delayMilliseconds);
                    if (!queue.Writer.TryWrite(point.Sample))
                        throw new IOException("Telemetry persistence exhausted the bounded sample buffer; no samples were dropped or inferred.");
                    if (point.Stop) break;
                    var delay = Stopwatch.StartNew();
                    if (stopping.Token.WaitHandle.WaitOne(cadence))
                        stopping.Token.ThrowIfCancellationRequested();
                    delayMilliseconds = delay.Elapsed.TotalMilliseconds;
                }
                queue.Writer.TryComplete();
            }
            catch (Exception error)
            {
                queue.Writer.TryComplete(error);
                throw;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        async Task Write()
        {
            await foreach (var sample in queue.Reader.ReadAllAsync(stopping.Token))
                await persist(sample, stopping.Token);
        }
        var writer = Write();
        try
        {
            var first = await Task.WhenAny(sampler, writer);
            await first;
            await Task.WhenAll(sampler, writer);
        }
        catch
        {
            stopping.Cancel();
            try { await Task.WhenAll(sampler, writer); } catch { /* Preserve the first failure. */ }
            throw;
        }
    }
}
