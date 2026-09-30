using System.Runtime.Versioning;
using System.Security.Cryptography;
using QueueCache.Management;

namespace QueueCache.Operations;

public sealed record DiskRemovalOracle(DiskTarget Target, string File, int Length, string Sha256,
    WriteCacheState BeforeRemoval, DateTimeOffset Prepared);

public static class DiskRemovalEvidence
{
    public static void ValidatePreparation(string volume, string volumeId, long volumeBytes, DiskEjectPreparation[]? preparation)
    {
        if (preparation is not { Length: 1 })
            throw new IOException("Missing exact single-volume removal preparation evidence.");
        var p = preparation[0];
        if (!p.Volume.Equals(volume, StringComparison.OrdinalIgnoreCase) ||
            !p.VolumeId.Equals(volumeId, StringComparison.OrdinalIgnoreCase) ||
            p.Before.DeviceBytes != (ulong)volumeBytes || p.Disabled.DeviceBytes != (ulong)volumeBytes ||
            !p.Before.Enabled || p.Before.DirtyBytes < (8UL << 20) || p.Before.BudgetBytes == 0 ||
            p.Before.Instance == 0 || p.Disabled.Instance != p.Before.Instance ||
            p.Disabled.BudgetBytes != p.Before.BudgetBytes || p.Disabled.Errors != p.Before.Errors || p.Before.LastError != 0 || p.Disabled.LastError != 0 ||
            p.Disabled.Enabled || p.Disabled.DirtyBytes != 0 || p.Disabled.InFlightBytes != 0 || !p.FileSystemFlushed ||
            p.BeforeLower is null || p.DisabledLower is null ||
            p.DisabledLower.LowerWriteAttempts <= p.BeforeLower.LowerWriteAttempts ||
            p.DisabledLower.LowerFlushAttempts <= p.BeforeLower.LowerFlushAttempts)
            throw new IOException("Orderly removal lacks matching pending-write, completed drain and lower-flush preparation evidence.");
    }

}

/// <summary>The first removal qualification: one disposable volume, Fast pending writes,
/// orderly Windows eject, then an operator reconnect on the same bus and letter.</summary>
[SupportedOSPlatform("windows")]
public static class DiskRemovalScenarios
{
    public static DiskRemovalOracle Prepare(DiskTarget target, CacheDevice device, string workDirectory, int budgetMiB)
    {
        target.ValidateCurrent();
        if (device.GetWriteCacheState().BudgetBytes != 0 || SavedConfigurations.IsSaved(target.VolumeId))
            throw new IOException("Removal verification requires an unconfigured disposable volume without a saved profile.");
        ConfigurationManager.Apply(target, VolumeScenarios.Pending(budgetMiB), true);
        Directory.CreateDirectory(workDirectory);
        var path = Path.Combine(workDirectory, "orderly-eject.bin");
        var bytes = new byte[8 << 20];
        RandomNumberGenerator.Fill(bytes); // Unique oracle bytes for every removal case.
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.Write(bytes);
            file.Flush(flushToDisk: true); // Fast application flush is deliberately volatile.
        }
        var pending = device.GetWriteCacheState();
        if (!pending.Enabled || pending.DirtyBytes < (ulong)bytes.Length || pending.LastError != 0)
            throw new IOException("The full pending-write precondition was not observed; removal case is unexercised.");
        return new(target, path, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), pending, DateTimeOffset.UtcNow);
    }

    public static IReadOnlyList<CheckResult> Verify(DiskRemovalOracle oracle, CacheDevice device)
    {
        oracle.Target.ValidateCurrent();
        var state = device.GetWriteCacheState();
        if (state.Instance == oracle.BeforeRemoval.Instance || state.BudgetBytes != 0 || state.DirtyBytes != 0 ||
            state.InFlightBytes != 0 || state.LastError != 0)
            throw new IOException("Reconnect did not establish a fresh empty cache lifetime.");
        using var file = File.OpenRead(oracle.File);
        var hash = Convert.ToHexString(SHA256.HashData(file));
        if (file.Length != oracle.Length || hash != oracle.Sha256)
            throw new IOException("The acknowledged orderly-eject file does not match its off-disk oracle.");
        return [new("orderly-eject/reconnect-oracle", "PASS", $"{file.Length} bytes match SHA-256 {hash}; fresh empty cache instance {state.Instance}.")];
    }
}
