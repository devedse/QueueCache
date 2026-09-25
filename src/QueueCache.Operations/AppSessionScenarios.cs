using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using QueueCache.Management;

namespace QueueCache.Operations;

/// <summary>What was observed while an operator edited the owned image with real applications.</summary>
public sealed record AppSessionObservation(bool OperatorDone, int Samples, TimeSpan Duration, bool AlwaysEnabled,
    bool Faulted, ulong ErrorsDelta, ulong AcceptedDelta, ulong MaxDirtyBytes, ulong DirtyAtDone, int FileChanges,
    DateTimeOffset? FirstChange, DateTimeOffset? LastChange);

/// <summary>
/// T080/T086: an operator-driven application session on the guarded system disk. The runner creates the
/// deterministic baseline image while C: is uncached, enables a runtime Fast cache, then this class samples cache
/// state and the file's metadata once per second while the operator edits, saves and reopens the image in real
/// applications (Paint, Photos). After the operator signals completion the saved bytes are hashed while the cache
/// is still active, the cache is drained and released, and the bytes read back from disk must match.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AppSessionScenarios
{
    public static string Instructions(string imagePath, string doneFile) =>
        $"""
        QueueCache application session (T080). The C: cache is active now.
        1. Open {imagePath} in Paint.
        2. Make a visible edit and save it over the same file as a BMP (File > Save).
        3. Close Paint, open the same file in Photos, check the edit is shown, then close Photos.
        4. Signal completion by creating this file (any content): {doneFile}
        The run waits up to the configured time; without the completion file the case is INCOMPLETE.
        """;

    /// <summary>Samples until the completion file exists or the time limit passes. Metadata only: the image is
    /// never opened here, so the applications are not disturbed.</summary>
    public static AppSessionObservation Observe(string imagePath, string doneFile, TimeSpan limit,
        Func<WriteCacheState> state, string samplesPath)
    {
        var first = state();
        var timer = Stopwatch.StartNew();
        var info = new FileInfo(imagePath);
        var lastWrite = info.LastWriteTimeUtc;
        var lastLength = info.Length;
        int samples = 0, changes = 0;
        bool alwaysEnabled = true, faulted = false, done = false;
        ulong maxDirty = 0, errors = 0, accepted = 0, dirty = 0;
        DateTimeOffset? firstChange = null, lastChange = null;
        using var output = new StreamWriter(samplesPath, append: false);
        while (true)
        {
            done = File.Exists(doneFile);
            var current = state();
            info.Refresh();
            var exists = info.Exists;
            var write = exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            var length = exists ? info.Length : -1;
            if (write != lastWrite || length != lastLength)
            {
                changes++;
                firstChange ??= DateTimeOffset.UtcNow;
                lastChange = DateTimeOffset.UtcNow;
                lastWrite = write;
                lastLength = length;
            }
            samples++;
            alwaysEnabled &= current.Enabled && current.Instance == first.Instance;
            faulted |= current.Faulted || current.LastError != 0;
            errors = current.Errors - first.Errors;
            accepted = current.AcceptedBytes - first.AcceptedBytes;
            dirty = current.DirtyBytes;
            maxDirty = Math.Max(maxDirty, dirty);
            output.WriteLine(JsonSerializer.Serialize(new
            {
                At = DateTimeOffset.UtcNow, current.Enabled, current.Faulted, current.LastError, current.DirtyBytes,
                current.InFlightBytes, current.AcceptedBytes, current.DrainedBytes, current.Errors,
                FileExists = exists, FileBytes = length, FileWriteUtc = write, OperatorDone = done
            }));
            output.Flush();
            if (done || timer.Elapsed >= limit)
                break;
            Thread.Sleep(1000);
        }
        return new(done, samples, timer.Elapsed, alwaysEnabled, faulted, errors, accepted, maxDirty, dirty, changes,
            firstChange, lastChange);
    }

    /// <summary>SHA-256 of the file's bytes through unbuffered reads (served by the cache while it is active).</summary>
    public static (string Sha256, long Bytes) UnbufferedHash(string path)
    {
        const int chunk = 1 << 20;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[chunk];
        long total = 0;
        using (var file = new AlignedFile(path, chunk, create: false, sharedReadOnly: true))
            while (true)
            {
                var count = file.ReadUpTo(total, buffer);
                hash.AppendData(buffer, 0, count);
                total += count;
                if (count < chunk)
                    break;
            }
        if (total != new FileInfo(path).Length)
            throw new IOException("Unbuffered read length disagrees with the file length.");
        return (Convert.ToHexString(hash.GetHashAndReset()), total);
    }

    /// <summary>Acceptance: the operator finished, an edit was saved, the cache stayed healthy, and the bytes on
    /// disk after drain and release equal the bytes seen while the cache was active.</summary>
    public static IReadOnlyList<CheckResult> Evaluate(AppSessionObservation session, string baselineSha256,
        (string Sha256, long Bytes) live, (string Sha256, long Bytes) released)
    {
        var window = FormattableString.Invariant(
            $"{session.Samples} samples over {session.Duration.TotalSeconds:F0} s; {session.FileChanges} file change(s) ") +
            $"(first {session.FirstChange:O}, last {session.LastChange:O}); " +
            FormattableString.Invariant($"C: accepted {session.AcceptedDelta} bytes, maximum dirty {session.MaxDirtyBytes}, ") +
            FormattableString.Invariant($"dirty when the operator finished {session.DirtyAtDone}.");
        if (!session.OperatorDone)
            throw new IOException("The operator did not signal completion within the time limit; the session is incomplete. " + window);
        if (!session.AlwaysEnabled || session.Faulted || session.ErrorsDelta != 0)
            throw new IOException("The C: cache did not stay enabled and error-free during the application session. " + window);
        if (string.Equals(live.Sha256, baselineSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The image still equals the baseline: no edit was saved, so the session proves nothing. " + window);
        if (!string.Equals(live.Sha256, released.Sha256, StringComparison.OrdinalIgnoreCase) || live.Bytes != released.Bytes)
            throw new IOException($"The saved image changed across drain and release (active {live.Sha256}/{live.Bytes}, " +
                $"released {released.Sha256}/{released.Bytes}). " + window);
        return
        [
            new("system-app/session-completed", "PASS", "The operator edited, saved and reopened the image and signalled completion. " + window),
            new("system-app/edit-saved", "PASS", $"The saved image ({live.Bytes} bytes, SHA-256 {live.Sha256}) differs from the baseline."),
            new("system-app/pending-at-finish", "PASS", session.DirtyAtDone > 0
                ? FormattableString.Invariant($"{session.DirtyAtDone} C: bytes were still only in RAM when the operator finished (device-wide; not attributed to the image).")
                : "No C: bytes were dirty when the operator finished; the edit had already drained, so this run did not hold it in RAM across the check."),
            new("system-app/post-drain-bytes", "PASS", "After drain, disable and release the image read back from disk byte-identical to the bytes seen while the cache was active.")
        ];
    }
}
