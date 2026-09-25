using System.Buffers.Binary;

namespace QueueCache.Management;

/// <summary>
/// T085 driver range sets in disk byte offsets, rounded outward to the 4 KiB cache block, merged and bounded.
/// Paging files are recognised per request by the driver; these sets only force the direct path (verification)
/// or provide reference paging-file extents for the recognition cross-check.
/// </summary>
public static class SpecialRangeMap
{
    public const int MaxRanges = 256;
    private const long Block = 4096;

    /// <summary>Round outward to whole cache blocks, sort and merge overlapping or adjacent ranges.</summary>
    public static IReadOnlyList<DiskRange> Normalize(IEnumerable<DiskRange> ranges)
    {
        var rounded = new List<(long Start, long End)>();
        foreach (var range in ranges)
        {
            if (range.Start < 0 || range.Length <= 0 || range.Start > long.MaxValue - range.Length - Block)
                throw new ArgumentOutOfRangeException(nameof(ranges), "Invalid special-file range.");
            var start = range.Start / Block * Block;
            var end = (range.Start + range.Length + Block - 1) / Block * Block;
            rounded.Add((start, end));
        }
        rounded.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<DiskRange>();
        long currentStart = 0, currentEnd = -1;
        foreach (var (start, end) in rounded)
        {
            if (currentEnd >= start)
            {
                currentEnd = Math.Max(currentEnd, end);
                continue;
            }
            if (currentEnd > currentStart)
                merged.Add(new(currentStart, currentEnd - currentStart));
            (currentStart, currentEnd) = (start, end);
        }
        if (currentEnd > currentStart)
            merged.Add(new(currentStart, currentEnd - currentStart));
        if (merged.Count > MaxRanges)
            throw new InvalidDataException($"Special files use {merged.Count} disk ranges; at most {MaxRanges} are supported.");
        return merged;
    }

    /// <summary>Wire format: QC_SPECIAL_RANGES_HEADER (24 bytes) then 16-byte {Start, Length} entries.
    /// An empty list clears the selected set.</summary>
    public static byte[] Encode(IReadOnlyList<DiskRange> ranges, SpecialRangeKind kind, ulong generation = 0)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (ranges.Count > MaxRanges)
            throw new ArgumentOutOfRangeException(nameof(ranges));
        var data = new byte[24 + ranges.Count * 16];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), (uint)ranges.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), (uint)kind);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16), generation);
        for (var index = 0; index < ranges.Count; index++)
        {
            if (ranges[index].Start < 0 || ranges[index].Length <= 0)
                throw new ArgumentOutOfRangeException(nameof(ranges));
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(24 + index * 16), ranges[index].Start);
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(32 + index * 16), ranges[index].Length);
        }
        return data;
    }

    /// <summary>
    /// Paging-file registry entries ("C:\pagefile.sys 512 512"). A file is fixed-size only with two equal,
    /// positive sizes; "?:\" (automatic) or missing/zero sizes mean Windows may grow it into unknown ranges.
    /// </summary>
    public static bool IsFixedPagingFileEntry(string entry, char volume, out bool matchesVolume)
    {
        var parts = entry.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        matchesVolume = false;
        if (parts.Length == 0 || parts[0].Length < 2)
            return false;
        if (parts[0][0] == '?')
        {
            matchesVolume = true; // Automatic placement may use any volume.
            return false;
        }
        matchesVolume = char.ToUpperInvariant(parts[0][0]) == char.ToUpperInvariant(volume);
        return parts.Length >= 3 && long.TryParse(parts[1], out var initial) && long.TryParse(parts[2], out var maximum) &&
            initial > 0 && initial == maximum;
    }
}
