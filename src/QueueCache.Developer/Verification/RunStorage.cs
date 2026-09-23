using System.Text.Json;

namespace QueueCache.Developer.Verification;

public sealed record VerificationOptions(string Volume, string Suite = "quick", string Output = ".",
    string? DiskSpd = null, int BudgetMiB = 1024, int Repeats = 3, int DurationSeconds = 10,
    int DeadlineMinutes = 0, int PreparationFlushSeconds = 180, string? CaseFilter = null,
    string? SystemInstance = null, long? SystemBytes = null, bool RecoverableVm = false,
    string? OraclePath = null);
public sealed record CaseResult(string Id, string Status, string Detail, DateTimeOffset Started,
    double Seconds, DiskSpdScore? Score = null);

/// <summary>One immutable namespace per run. All report replacement is atomic on the output volume.</summary>
public sealed class RunStorage
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string DirectoryPath
    {
        get;
    }
    public List<CaseResult> Results { get; } = [];
    public RunStorage(string parent)
    {
        DirectoryPath = Path.Combine(Path.GetFullPath(parent), $"QueueCache-Verify-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(DirectoryPath);
    }
    public string PathFor(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name is "." or "..")
            throw new ArgumentException("Expected a single output filename.");
        return Path.Combine(DirectoryPath, name);
    }
    public static void AtomicJson(string path, object value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
        File.Move(temporary, path, overwrite: true);
    }
    public void Write(string name, object value) => AtomicJson(PathFor(name), value);
    public void Add(CaseResult result)
    {
        if (Results.Any(r => r.Id == result.Id))
            throw new InvalidDataException("Duplicate case ID: " + result.Id);
        Results.Add(result);
        Write("results.json", Results);
    }
    public static bool Complete(IReadOnlyList<string> expected, IReadOnlyList<CaseResult> results, bool allowSkipped = false) =>
        expected.Count > 0 && expected.Distinct().Count() == expected.Count && results.Count == expected.Count &&
        results.Select(r => r.Id).Distinct().Count() == results.Count &&
        expected.All(id => results.Any(r => r.Id == id && (r.Status is "PASS" or "MEASURED" || allowSkipped && r.Status == "SKIP")));
}
