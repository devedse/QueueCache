using System.Globalization;
using System.Xml.Linq;

namespace QueueCache.Developer.Verification;

/// <summary>Strict measured settings for the priority/affinity factorial, separate from legacy priority-cost.</summary>
public static class CachePriorityProfile
{
    public static void Validate(string output, IReadOnlyList<string> arguments, int files)
    {
        var profile = DiskSpdParser.ParseXml(output).Element("Profile") ?? throw new InvalidDataException("Missing profile.");
        var span = profile.Element("TimeSpans")?.Elements("TimeSpan").SingleOrDefault()
            ?? throw new InvalidDataException("Missing single timespan.");
        var targets = span.Element("Targets")?.Elements("Target").ToArray() ?? [];
        if (targets.Length != files) throw new InvalidDataException("Priority/affinity target count changed.");
        string Arg(string prefix) => arguments.Single(a => a.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];
        static string Bytes(string value) => (long.Parse(value[..^1], CultureInfo.InvariantCulture) *
            (value[^1] switch { 'K' => 1024L, 'M' => 1L << 20, _ => throw new InvalidDataException("Unsupported size.") }))
            .ToString(CultureInfo.InvariantCulture);
        static void Expect(XElement element, string field, string value)
        {
            if (element.Element(field)?.Value != value) throw new InvalidDataException("Priority/affinity profile mismatch: " + field);
        }
        Expect(span, "DisableAffinity", arguments.Contains("-n") ? "true" : "false");
        Expect(span, "Duration", Arg("-d")); Expect(span, "Warmup", "3");
        foreach (var target in targets)
        {
            Expect(target, "BlockSize", Bytes(Arg("-b")));
            Expect(target, "RequestCount", Arg("-o")); Expect(target, "ThreadsPerFile", "1");
            Expect(target, "WriteRatio", "0"); Expect(target, "MaxFileSize", Bytes(Arg("-f")));
            Expect(target, "IOPriority", "3"); Expect(target, "DisableOSCache", "true");
            Expect(target, "UseLargePages", "false");
            if (arguments.Any(a => a.StartsWith("-r", StringComparison.Ordinal))) Expect(target, "Random", Bytes(Arg("-r")));
            else
            {
                Expect(target, "StrideSize", Bytes(Arg("-b"))); Expect(target, "ThreadStride", "0");
                Expect(target, "InterlockedSequential", "false");
            }
        }
    }
}
