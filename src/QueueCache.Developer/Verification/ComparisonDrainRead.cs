using System.Text.Json.Serialization;

namespace QueueCache.Developer.Verification;

/// <summary>One plan-161 window. Loaded windows run the reader while the writer keeps the product's cache full;
/// alone windows have no writer. Pending snapshots bracket the reader window of a loaded case.</summary>
public sealed record ComparisonReadScore(
    [property: JsonRequired] string Id, [property: JsonRequired] ComparisonProvider Provider, [property: JsonRequired] bool Loaded,
    [property: JsonRequired] DateTimeOffset ReadyUtc, DateTimeOffset? WriterStartedUtc,
    [property: JsonRequired] DateTimeOffset ReaderStartedUtc, [property: JsonRequired] DateTimeOffset ReaderEndedUtc,
    DateTimeOffset? WriterEndedUtc, [property: JsonRequired] DateTimeOffset EndedUtc,
    [property: JsonRequired] ComparisonScore Reader, ComparisonScore? Writer,
    [property: JsonRequired] ComparisonPending Before, ComparisonPending? AtReaderStart, ComparisonPending? AtReaderEnd,
    [property: JsonRequired] ComparisonPending After, [property: JsonRequired] ComparisonDiskCounters DiskBefore,
    [property: JsonRequired] ComparisonDiskCounters DiskAfter, [property: JsonRequired] ComparisonDiskCounters[] DiskSamples,
    [property: JsonRequired] long ReaderWindowDiskBytesWritten);

/// <summary>Plan 161 read-during-drain comparison: a 4 KiB random Q1 reader on an uncached 8 GiB file, alone and
/// while a sequential 1 MiB Q8 writer keeps the product's 2 GiB cache full, so the product drains at its own full
/// rate. Every provider runs the same DiskSpd windows; only the product drain/clear/pending controls differ.</summary>
public static class ComparisonDrainRead
{
    public const string Suite = "compare-drain-read";
    public const long ReaderBytes = 8L << 30, WriterBytes = 8L << 30;
    public const int ReaderSeconds = 10, WriterSeconds = 30, WriterLeadSeconds = 12;
    public const string ReaderFile = "reader-8g.dat", WriterFile = "writer-8g.dat";
    /// <summary>Background file-system writes tolerated in an alone window; a loaded window drains far more.</summary>
    public const long AloneMaxDiskBytes = 16L << 20, LoadedMinDiskBytes = 64L << 20;

    public static IReadOnlyList<ComparisonCase> Cases(int repeats)
    {
        if (repeats != 3) throw new ArgumentException("compare-drain-read requires exactly three repeats.");
        var result = new List<ComparisonCase>();
        for (var r = 1; r <= repeats; r++)
        {
            ComparisonCase alone = new($"read-alone-r{r}", "read-alone", r, ReaderFile, 'R', 4, 1, 1, Random: true, Seconds: ReaderSeconds);
            ComparisonCase loaded = new($"read-loaded-r{r}", "read-loaded", r, ReaderFile, 'R', 4, 1, 1, Random: true, Seconds: ReaderSeconds);
            result.AddRange(r % 2 == 1 ? [alone, loaded] : new[] { loaded, alone });
        }
        return result;
    }

    public static ComparisonCase ReaderWindow(ComparisonCase test) =>
        new(test.Id + "-reader", "diskspd", test.Repeat, ReaderFile, 'R', 4, 1, 1, Random: true, Seconds: ReaderSeconds);

    public static ComparisonCase WriterWindow(ComparisonCase test) =>
        new(test.Id + "-writer", "diskspd", test.Repeat, WriterFile, 'W', 1024, 8, 1, Random: false, Seconds: WriterSeconds, WritePercent: 100);

    /// <summary>Disk bytes written between the last sample at or before start and the first at or after end.</summary>
    public static long WindowBytes(IReadOnlyList<ComparisonDiskCounters> samples, DateTimeOffset start, DateTimeOffset end)
    {
        var before = samples.LastOrDefault(s => s.Utc <= start) ?? throw new InvalidDataException("No disk sample before the reader window.");
        var after = samples.FirstOrDefault(s => s.Utc >= end) ?? throw new InvalidDataException("No disk sample after the reader window.");
        ComparisonDrain.ValidateDiskDelta(before, after, allowIdle: true);
        return after.BytesWritten - before.BytesWritten;
    }

    private static bool HasPending(ComparisonPending pending) =>
        pending.DirtyBytes is > 0 || pending.DeferredBlocks is > 0;

    public static void ValidateScore(ComparisonCase test, ComparisonReadScore score, ComparisonProvider provider)
    {
        var loaded = test.Kind == "read-loaded";
        if (test.Kind is not ("read-alone" or "read-loaded") || score.Id != test.Id || score.Provider != provider || score.Loaded != loaded)
            throw new InvalidDataException("Read-during-drain score does not match its case: " + test.Id);
        ComparisonDrain.ValidatePending(score.Before, provider, clean: true);
        ComparisonDrain.ValidatePending(score.After, provider, clean: true);
        var reader = score.Reader;
        if (reader.Id != test.Id + "-reader" || reader.Operations <= 0 || reader.ReadP99Ms is null || reader.WriteP99Ms is not null ||
            reader.Bytes != reader.Operations * 4096L || reader.Seconds is < ReaderSeconds - 0.5 or > ReaderSeconds + 0.5 ||
            score.ReadyUtc > score.ReaderStartedUtc || score.Before.Utc > score.ReaderStartedUtc ||
            score.ReaderStartedUtc >= score.ReaderEndedUtc || score.ReaderEndedUtc > score.After.Utc || score.After.Utc > score.EndedUtc ||
            score.DiskBefore.Utc > score.ReaderStartedUtc || score.DiskAfter.Utc > score.EndedUtc ||
            score.DiskBefore.QueueDepth != 0 || score.DiskAfter.QueueDepth != 0)
            throw new InvalidDataException("Invalid or incomplete read-during-drain reader score: " + test.Id);
        if (loaded)
        {
            // The writer led the reader, outlived it, and the product held data to drain at both reader boundaries.
            if (score.Writer is not { } writer || writer.Id != test.Id + "-writer" || writer.Operations <= 0 || writer.ReadP99Ms is not null ||
                score.WriterStartedUtc is not { } writerStart || score.WriterEndedUtc is not { } writerEnd ||
                score.AtReaderStart is not { } atStart || score.AtReaderEnd is not { } atEnd ||
                writerStart < score.ReadyUtc || score.ReaderStartedUtc - writerStart < TimeSpan.FromSeconds(WriterLeadSeconds) ||
                writerEnd < score.ReaderEndedUtc || writerEnd > score.After.Utc ||
                atStart.Utc > score.ReaderStartedUtc || atEnd.Utc < score.ReaderEndedUtc || atEnd.Utc > writerEnd)
                throw new InvalidDataException("Loaded read window lacks a covering writer or pending evidence: " + test.Id);
            ComparisonDrain.ValidatePending(atStart, provider, clean: false);
            ComparisonDrain.ValidatePending(atEnd, provider, clean: false);
            if (!ComparisonDrain.IsPaused(provider) && (!HasPending(atStart) || !HasPending(atEnd)))
                throw new InvalidDataException("The product had nothing pending at a reader boundary; it was not draining: " + test.Id);
        }
        else if (score.Writer is not null || score.WriterStartedUtc is not null || score.WriterEndedUtc is not null ||
                 score.AtReaderStart is not null || score.AtReaderEnd is not null)
            throw new InvalidDataException("An alone read window has writer evidence: " + test.Id);
        TelemetryCoverage.Validate(score.DiskSamples.Select(s => s.Utc).ToArray(), score.WriterStartedUtc ?? score.ReaderStartedUtc, score.EndedUtc);
        var timeline = score.DiskSamples.Append(score.DiskBefore).Append(score.DiskAfter).OrderBy(s => s.Utc).ToArray();
        for (var i = 1; i < timeline.Length; i++) ComparisonDrain.ValidateDiskDelta(timeline[i - 1], timeline[i], allowIdle: true);
        var window = WindowBytes(score.DiskSamples, score.ReaderStartedUtc, score.ReaderEndedUtc);
        if (window != score.ReaderWindowDiskBytesWritten)
            throw new InvalidDataException("Reader-window disk bytes contradict the disk samples: " + test.Id);
        if (loaded ? window < LoadedMinDiskBytes : window > AloneMaxDiskBytes)
            throw new InvalidDataException(loaded
                ? "The backing disk was not draining during the loaded reader window: " + test.Id
                : "Background writes reached the disk during an alone reader window: " + test.Id);
    }
}
