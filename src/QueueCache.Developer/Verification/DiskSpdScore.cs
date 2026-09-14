using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace QueueCache.Developer.Verification;

public sealed record DiskSpdScore(long Bytes, long Operations, double Seconds, double MiBPerSecond,
    double Iops, double? ReadP99Milliseconds, double? WriteP99Milliseconds,
    double? ReadP999Milliseconds, double? ReadMaxMilliseconds);

/// <summary>Structured DiskSpd results only. Missing/malformed results are collection failures, not zero scores.</summary>
public static class DiskSpdParser
{
    public static DiskSpdScore Parse(string xml)
    {
        // CDM 9.0.3's DiskSpd 2.2 appends these two text lines even in XML mode.
        // Strip only this complete, recognized trailer. Never scrape a Results element
        // out of arbitrary error output or use the trailer's zero scores as metrics.
        var end = xml.LastIndexOf("</Results>", StringComparison.Ordinal);
        if (end >= 0)
        {
            end += "</Results>".Length;
            var trailer = xml[end..].Trim();
            if (Regex.IsMatch(trailer, @"\AScore:[ \t]*[0-9]+(?:\.[0-9]+)?\r?\naverageLatency:[ \t]*[0-9]+(?:\.[0-9]+)?\z",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                xml = xml[..end];
        }
        var root = XDocument.Parse(xml).Root ?? throw new InvalidDataException("Missing DiskSpd XML.");
        if (root.Name.LocalName != "Results")
            throw new InvalidDataException("Expected DiskSpd Results XML (use -Rxml).");
        var spans = root.Elements("TimeSpan").ToArray();
        if (spans.Length != 1)
            throw new InvalidDataException("Expected exactly one measured time span.");
        var span = spans[0];
        double Number(XElement parent, string name) => double.Parse(parent.Element(name)?.Value ??
            throw new InvalidDataException("Missing DiskSpd " + name), CultureInfo.InvariantCulture);
        var seconds = Number(span, "TestTimeSeconds");
        var targets = span.Elements("Thread").SelectMany(t => t.Elements("Target")).ToArray();
        if (!double.IsFinite(seconds) || seconds <= 0 || targets.Length == 0)
            throw new InvalidDataException("Invalid measured span.");
        long Sum(string field) => targets.Sum(t =>
        {
            var value = long.Parse(t.Element(field)?.Value ?? throw new InvalidDataException("Missing DiskSpd " + field), CultureInfo.InvariantCulture);
            return value >= 0 ? value : throw new InvalidDataException("Negative DiskSpd counter.");
        });
        var bytes = checked(Sum("ReadBytes") + Sum("WriteBytes"));
        var reads = Sum("ReadCount");
        var writes = Sum("WriteCount");
        var count = checked(reads + writes);
        if (bytes < 0 || count < 0)
            throw new InvalidDataException("Negative DiskSpd counters.");
        double? Latency(string name, double percentile, long operations)
        {
            if (operations == 0)
                return null;
            var bucket = span.Element("Latency")?.Elements("Bucket").SingleOrDefault(b =>
                double.TryParse(b.Element("Percentile")?.Value, CultureInfo.InvariantCulture, out var p) && p == percentile);
            return double.TryParse(bucket?.Element(name)?.Value, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n >= 0 ? n :
                throw new InvalidDataException("Missing/invalid latency; enable DiskSpd -L.");
        }
        return new(bytes, count, seconds, bytes / seconds / (1 << 20), count / seconds,
            Latency("ReadMilliseconds", 99, reads), Latency("WriteMilliseconds", 99, writes),
            Latency("ReadMilliseconds", 99.9, reads), Latency("ReadMilliseconds", 100, reads));
    }
}
