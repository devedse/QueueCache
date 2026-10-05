using QueueCache.Management;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Developer.Verification;

/// <summary>Failed precommit saves must retain the same writable RAM and startup image.</summary>
public static class ManagedCheckpointEvidence
{
    public static void RequireRetained(ManagedDiskRecord before, ManagedDiskRecord after)
    {
        before.Validate(); after.Validate();
        if (before.Definition.Mode != ManagedDiskMode.ImageInRam || before.Runtime?.State != ManagedDiskState.Ready ||
            before.Native is null || before.CommittedImage is null || !before.Runtime.HasUnsavedChanges ||
            before.PhysicalDiskNumber is null || before.VolumePath is null || before.GptDiskId is null)
            throw new InvalidDataException("Save-failure proof requires an observed dirty RAM image and preceding committed checkpoint.");
        if (after.Definition != before.Definition || after.Runtime?.State != ManagedDiskState.Ready || after.Native is null ||
            after.Runtime.BootEpoch != before.Runtime.BootEpoch || after.Runtime.CreationGeneration != before.Runtime.CreationGeneration ||
            after.CommittedImage != before.CommittedImage || after.PreviousImage != before.PreviousImage || after.SavedAt != before.SavedAt ||
            after.Runtime.SavedGeneration != before.Runtime.SavedGeneration || !after.Runtime.HasUnsavedChanges ||
            after.PhysicalDiskNumber != before.PhysicalDiskNumber || after.VolumePath != before.VolumePath ||
            after.Runtime.Volume != before.Runtime.Volume || after.GptDiskId != before.GptDiskId ||
            after.OriginalSource != before.OriginalSource || after.StartupSession != before.StartupSession ||
            (after.Native.Flags & (RamDiskFlags.Frozen | RamDiskFlags.ReadOnly)) != 0 ||
            (after.Native.Flags & RamDiskFlags.Published) == 0)
            throw new IOException("Failed save changed the startup image, lost the live RAM binding or left it frozen/unavailable.");
        after.Native.RequireSameCreation(before.Native);
    }
}
