using System.Runtime.Versioning;

namespace QueueCache.Operations;

/// <summary>A physical disk and its lettered volumes. The cache works per volume (<see cref="VolumeCatalog"/>);
/// disks are listed for identity and safety checks.</summary>
public sealed record DiskDescription(int Number, string Name, long Bytes, string Instance, string[] Volumes, bool IsBoot, bool IsSystem, bool IsPaging = false,
    // Excludes the GPT Microsoft Reserved partition, which has no file system to flush.
    int PartitionCount = 0, bool IsReadOnly = false, bool IsOffline = false)
{
    public string Device => $"PhysicalDrive{Number}";
    public double SizeGiB => Bytes / 1073741824.0;
    public string Display => $"{string.Join(", ", Volumes)} | {Device} | {SizeGiB:0.##} GiB | {Name}" +
        (IsBoot || IsSystem ? " [boot/system]" : "") + (IsPaging ? " [paging]" : "");
}

[SupportedOSPlatform("windows")]
public static class DiskCatalog
{
    public static async Task<IReadOnlyList<DiskDescription>> ListAsync(CancellationToken token = default) =>
        (await Task.Run(() => StorageInventory.Read(token), token).WaitAsync(TimeSpan.FromSeconds(30), token)).DiskList();
}
