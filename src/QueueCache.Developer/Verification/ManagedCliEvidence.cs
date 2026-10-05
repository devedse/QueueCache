using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Developer.Verification;

public sealed record ManagedCliFixture(ManagedDiskMode Mode, string Label, char Letter, ulong CapacityBytes,
    uint SectorBytes, string? ImagePath, IReadOnlySet<Guid> PreexistingResources);

/// <summary>Require the unique requested recipe before CLI evidence or fallback cleanup can target a resource.</summary>
public static class ManagedCliEvidence
{
    public static void RequireOwned(ManagedDiskRecord record, ManagedCliFixture fixture)
    {
        record.Validate();
        if (fixture.PreexistingResources.Contains(record.ResourceId) || record.Definition.Mode != fixture.Mode ||
            record.Definition.Label != fixture.Label || record.Definition.PreferredLetter != fixture.Letter ||
            record.Definition.CapacityBytes != fixture.CapacityBytes || record.Definition.SectorBytes != fixture.SectorBytes ||
            record.Definition.ImagePath != fixture.ImagePath)
            throw new IOException("CLI fixture recipe/identity does not match the uniquely owned request.");
    }
}
