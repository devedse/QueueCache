using QueueCache.Management;

namespace QueueCache.Developer.Verification;

public static class SystemPagingOrderEvidence
{
    public static int Validate(IReadOnlyList<TelemetrySnapshot> rows, TelemetryClock start, TelemetryClock end,
        IReadOnlyList<PagingPressureResult> children)
    {
        TelemetryCoverage.Validate(rows.Select(r => r.Clock).ToArray(), start, end);
        if ((decimal)(end.Timestamp - start.Timestamp) / start.Frequency < 120)
            throw new InvalidDataException("Paging-pressure observation was shorter than two minutes.");
        if (children.Count != 4 || !children.Select(c => c.Role).Order().SequenceEqual([0UL, 1UL, 2UL, 3UL]) ||
            children.Any(c => c.Seconds < 120))
            throw new InvalidDataException("Incomplete, duplicated or shortened paging-pressure workers.");
        var hog = children.Single(c => c.Role == 0); var pager = children.Single(c => c.Role == 1);
        var heartbeat = children.Single(c => c.Role == 2); var writer = children.Single(c => c.Role == 3);
        if (hog.RequestedBytes is null or < (1L << 30) || hog.AllocatedBytes != hog.RequestedBytes ||
            pager.AllocatedBytes != 4L << 30 || pager.Operations <= 0 || writer.Operations < PagingWriteEvidence.Files ||
            heartbeat.Operations < 100 || heartbeat.LongestGapSeconds is null or <= 0 or > 2)
            throw new InvalidDataException("Pressure allocations, writer progress or heartbeat contract was not met.");
        var first = rows[0]; var held = 0;
        foreach (var r in rows)
            if (r.Performance is null || r.StageTiming is null || r.Diagnostics.PagingOrder is not { OverlapFound: 0 } || r.Diagnostics.FlushOrder?.Mode != 1 ||
                r.State.Faulted || r.State.LastError != 0 || r.State.Errors != first.State.Errors)
                throw new InvalidDataException("Missing safe-range evidence, unsafe overlap, changed mode or cache error.");
        for (var i = 1; i < rows.Count; ++i)
        {
            var a = rows[i - 1]; var b = rows[i];
            if (a.Clock!.Timestamp < start.Timestamp || b.Clock!.Timestamp > end.Timestamp) continue;
            var before = a.Diagnostics.PagingOrder!; var after = b.Diagnostics.PagingOrder!;
            if (after.Bypasses < before.Bypasses || after.LearnBarriers < before.LearnBarriers)
                throw new InvalidDataException("Paging-order counters regressed.");
            if (after.Bypasses > before.Bypasses && a.State.DirtyBytes + a.State.InFlightBytes > 0 &&
                b.State.DirtyBytes + b.State.InFlightBytes > 0) ++held;
        }
        if (held == 0) throw new InvalidDataException("No page-file bypass was observed while earlier saves remained pending.");
        return held;
    }
}
