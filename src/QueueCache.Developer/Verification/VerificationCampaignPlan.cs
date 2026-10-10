namespace QueueCache.Developer.Verification;

public enum CampaignTargetRole { Performance, NtfsLab, RefsLab }

public sealed record VerificationCampaignOptions(VerificationOptions Verification, string Profile,
    string LabNtfs, string? LabRefs = null, string? FocusSuite = null, bool PauseBackingCache = false);

public sealed record VerificationCampaignPhase(string Id, CampaignTargetRole Role,
    VerificationOptions Options, IReadOnlyList<string> ExpectedCases, int MeasurementWindows);

/// <summary>Campaigns compose existing suite contracts, with no scenario logic in the CLI.</summary>
public static class VerificationCampaignPlan
{
    public static readonly string[] Profiles = ["smoke", "focused", "performance", "release-performance"];
    public static readonly string[] FocusSuites =
    ["partial-read-accounting", "paging-coherence", "policies", "pressure", "ordering-faults",
     "sequential-resident", "cache-layout", "ram-read-reference", "ram-read-scheduling", "ram-read-attribution",
     "caller-backoff", "priority-cost", "priority-affinity", "cache-concurrency", "cache-map-cost", "cache-recall",
     "cache-sustained", "write-performance"];

    public static IReadOnlyList<string> ExpectedCases(VerificationOptions options)
    {
        var performance = options.Suite is "performance" or "full" or "flush-interference" or "write-performance" or "sequential-resident" ||
            VerificationPlan.IsLayoutSuite(options.Suite) ? VerificationPlan.Performance(options) : [];
        return [.. VerificationPlan.Integrity(options).Select(c => c.Id), .. performance.Select(c => c.Id),
            .. VerificationPlan.DrainDecision(options).Select(c => c.Id), .. CacheExercisePlan.Cases(options).Select(c => c.Id)];
    }

    public static IReadOnlyList<VerificationCampaignPhase> Create(VerificationCampaignOptions options)
    {
        if (!Profiles.Contains(options.Profile)) throw new ArgumentException("Unknown verification campaign.");
        if (options.Verification.Suite != "quick" || options.Verification.CaseFilter is not null ||
            options.Verification.SystemInstance is not null || options.Verification.SystemBytes is not null ||
            options.Verification.RecoverableVm || options.Verification.OraclePath is not null ||
            options.Verification.DisposableInstance is not null || options.Verification.DisposableBytes is not null ||
            options.Verification.ManagedOraclePath is not null || options.Verification.ManagedTransition is not null)
            throw new ArgumentException("Campaign selection cannot be combined with a suite, case filter or lifecycle/system options.");
        if (options.Verification.TraceSymbols is not null && options.FocusSuite != "ram-read-attribution")
            throw new ArgumentException("--trace-symbols requires focused ram-read-attribution.");
        if ((options.Profile == "focused") != (options.FocusSuite is not null) ||
            options.FocusSuite is { } focus && !FocusSuites.Contains(focus))
            throw new ArgumentException("--focus-suite is required only for the focused campaign and must name a supported performance/correctness suite.");
        if (options.Verification.SoakSeconds is not null && options.Profile is not ("performance" or "release-performance") && options.FocusSuite != "cache-sustained")
            throw new ArgumentException("--soak-seconds requires a campaign with a sustained phase.");
        static void Volume(string value)
        {
            if (value.Length != 2 || !char.IsAsciiLetter(value[0]) || value[1] != ':' || char.ToUpperInvariant(value[0]) is 'A' or 'B' or 'C')
                throw new ArgumentException("Campaign targets must be explicit non-OS volumes, e.g. Q:, W: and R:.");
        }
        Volume(options.Verification.Volume); Volume(options.LabNtfs);
        if (options.LabRefs is not null) Volume(options.LabRefs);
        var letters = new[] { options.Verification.Volume, options.LabNtfs, options.LabRefs }.Where(v => v is not null).ToArray();
        if (letters.Distinct(StringComparer.OrdinalIgnoreCase).Count() != letters.Length)
            throw new ArgumentException("Performance, NTFS lab and optional ReFS lab targets must be different volumes.");
        if (options.LabRefs is not null && options.Profile is not ("performance" or "release-performance") && options.FocusSuite != "caller-backoff")
            throw new ArgumentException("--lab-refs applies to performance campaigns or a focused caller-backoff campaign.");
        VerificationPlan.Validate(options.Verification with { DiskSpd = null, SoakSeconds = null, TraceSymbols = null });

        var phases = new List<VerificationCampaignPhase>();
        void Add(string suite, CampaignTargetRole role, string? filter = null)
        {
            var volume = role switch { CampaignTargetRole.Performance => options.Verification.Volume,
                CampaignTargetRole.NtfsLab => options.LabNtfs, _ => options.LabRefs! };
            var selected = options.Verification with
            {
                Volume = volume.ToUpperInvariant(), Suite = suite, CaseFilter = filter,
                TraceSymbols = suite == "ram-read-attribution" ? options.Verification.TraceSymbols : null,
                SoakSeconds = suite == "cache-sustained" ? options.Verification.SoakSeconds ??
                    (options.Profile == "release-performance" ? 1800 : 120) : null,
                Repeats = suite == "cache-sustained" ? 1 : options.Verification.Repeats,
                // The optional overall deadline belongs to the campaign, not a fresh timer per phase.
                DeadlineMinutes = 0
            };
            VerificationPlan.Validate(selected);
            var expected = ExpectedCases(selected);
            if (expected.Count == 0 || expected.Distinct().Count() != expected.Count)
                throw new InvalidDataException("Campaign phase has missing or duplicate cases.");
            var windows = VerificationPlan.IsRamReadSuite(suite) ? RamReadReferencePlan.CasesFor(selected).Count :
                expected.Count - VerificationPlan.Integrity(selected).Count;
            phases.Add(new($"{phases.Count + 1:D2}-{suite}-{role}", role, selected, expected, windows));
        }

        if (options.Profile == "smoke")
        {
            Add("quick", CampaignTargetRole.NtfsLab);
            Add("partial-read-accounting", CampaignTargetRole.NtfsLab);
        }
        else
        {
            foreach (var suite in new[] { "partial-read-accounting", "paging-coherence", "policies", "pressure" })
                Add(suite, CampaignTargetRole.NtfsLab);
            if (options.Profile == "focused")
            {
                if (!phases.Any(p => p.Options.Suite == options.FocusSuite) && options.FocusSuite != "ordering-faults")
                    Add(options.FocusSuite!, options.FocusSuite is "caller-backoff" or "priority-cost" or "priority-affinity" ? CampaignTargetRole.NtfsLab : CampaignTargetRole.Performance);
                if (options.FocusSuite == "caller-backoff" && options.LabRefs is not null)
                    Add("caller-backoff", CampaignTargetRole.RefsLab);
            }
            else
            {
                Add("cache-layout", CampaignTargetRole.Performance);
                Add("ram-read-reference", CampaignTargetRole.Performance);
                Add("ram-read-scheduling", CampaignTargetRole.Performance);
                Add("caller-backoff", CampaignTargetRole.NtfsLab);
                if (options.LabRefs is not null) Add("caller-backoff", CampaignTargetRole.RefsLab);
                Add("priority-cost", CampaignTargetRole.NtfsLab);
                Add("cache-recall", CampaignTargetRole.Performance);
                Add("cache-sustained", CampaignTargetRole.Performance);
                Add("cache-map-cost", CampaignTargetRole.Performance);
                if (options.Profile == "release-performance") Add("write-performance", CampaignTargetRole.Performance);
            }
            // Fault injection is never followed by a benchmark phase.
            Add("ordering-faults", CampaignTargetRole.NtfsLab);
        }
        return phases;
    }
}
