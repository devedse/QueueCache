using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using QueueCache.Management;

namespace QueueCache.Operations;

public sealed record DiskEjectPreview(int DiskNumber, string Instance, string Name, IReadOnlyList<string> Volumes,
    bool Ejectable, string? UnsupportedReason);
public sealed record DiskEjectResult(DiskEjectPreview Disk, uint ConfigurationManagerResult, uint VetoType, string VetoName,
    bool RemovalObserved);

/// <summary>One physical-disk eject, including every lettered volume on the disk.</summary>
public static class DiskEjection
{
    private const uint EjectSupported = 0x2, Removable = 0x4;
    private const uint Capabilities = 0x10; // CM_DRP_CAPABILITIES

    [SupportedOSPlatform("windows")]
    public static async Task<DiskEjectPreview> PreviewAsync(string volume, CancellationToken token = default)
    {
        var inventory = await DiskCatalog.ListAsync(token);
        var found = inventory.Where(d => d.Volumes.Contains(volume, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (found.Length != 1)
            throw new IOException($"{volume} must identify exactly one present physical disk.");
        return Preview(found[0]);
    }

    [SupportedOSPlatform("windows")]
    internal static DiskEjectPreview Preview(DiskDescription disk)
    {
        var reason = SafetyReason(disk);
        if (reason is null)
        {
            var code = CM_Locate_DevNodeW(out var node, disk.Instance, 0);
            if (code != 0)
                reason = $"Windows cannot locate this disk's PnP device (Configuration Manager {code}).";
            else
            {
                uint length = sizeof(uint);
                code = CM_Get_DevNode_Registry_PropertyW(node, Capabilities, out _, out var flags, ref length, 0);
                if (code != 0 || length != sizeof(uint))
                    reason = $"Windows cannot read this disk's removal capability (Configuration Manager {code}).";
                else if ((flags & (EjectSupported | Removable)) == 0)
                    reason = "Windows does not identify this disk as removable or ejectable.";
            }
        }
        return new(disk.Number, disk.Instance, disk.Name, disk.Volumes, reason is null, reason);
    }

    internal static string? SafetyReason(DiskDescription disk)
    {
        if (disk.IsBoot || disk.IsSystem || disk.IsPaging)
            return "This disk holds Windows or a paging file.";
        if (disk.IsOffline || disk.IsReadOnly)
            return "This disk is offline or read-only.";
        if (disk.PartitionCount == 0 || disk.Volumes.Length != disk.PartitionCount || disk.Volumes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != disk.PartitionCount)
            return "Every partition must have one drive letter; an unlettered or ambiguous volume needs separate review.";
        if (string.IsNullOrWhiteSpace(disk.Instance))
            return "Windows did not provide a PnP identity for this disk.";
        return null;
    }

    [SupportedOSPlatform("windows")]
    public static async Task<DiskEjectResult> EjectAsync(string volume, IProgress<string>? progress = null, CancellationToken token = default)
    {
        var preview = await PreviewAsync(volume, token);
        if (!preview.Ejectable)
            throw new NotSupportedException(preview.UnsupportedReason);
        var original = await VolumeCatalog.ListAsync(token);
        var affected = original.Where(v => v.DiskNumber == preview.DiskNumber).ToArray();
        if (affected.Length != preview.Volumes.Count || affected.Any(v => !string.Equals(v.Instance, preview.Instance, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The disk's volume inventory changed before eject.");
        return await Task.Run(() => EjectPrepared(volume, preview, affected, progress, token), token);
    }

    [SupportedOSPlatform("windows")]
    private static DiskEjectResult EjectPrepared(string volume, DiskEjectPreview preview,
        VolumeDescription[] affected, IProgress<string>? progress, CancellationToken token)
    {
        // ConfigurationGate is a thread-affine mutex: no await while it is held.
        // QUERY_REMOVE closes admission for native eject; explicit Disable also
        // covers the interval before Windows begins its PnP request.
        using var gate = ConfigurationGate.Enter();
        var beforePreparation = PreviewAsync(volume, token).GetAwaiter().GetResult();
        if (!beforePreparation.Ejectable || beforePreparation.DiskNumber != preview.DiskNumber ||
            !string.Equals(beforePreparation.Instance, preview.Instance, StringComparison.OrdinalIgnoreCase) ||
            !beforePreparation.Volumes.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(preview.Volumes))
            throw new IOException("The disk changed before eject preparation.");
        var disabled = new List<(VolumeDescription Volume, bool WasEnabled)>();
        try
        {
            foreach (var item in affected)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report($"Draining and disabling cache on {item.Volume}");
                using var cache = new CacheDevice(item.Volume, writable: true);
                if (cache.GetStatistics().PagingPathCount != 0)
                    throw new NotSupportedException($"{item.Volume} hosts a paging, hibernation or crash-dump path. Eject was not requested.");
                var before = cache.GetWriteCacheState();
                if (before.BudgetBytes > 0)
                {
                    cache.Control(WriteCacheAction.Disable);
                    disabled.Add((item, before.Enabled));
                }
            }
            foreach (var item in affected)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report($"Flushing filesystem on {item.Volume}");
                // After Disable all later traffic goes to the lower device. A
                // Fast-mode filesystem flush alone is not a durability boundary.
                using var handle = CreateFileW(@"\\.\" + item.Volume, 0xC0000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (handle.IsInvalid || !FlushFileBuffers(handle))
                    throw new IOException($"Windows could not flush {item.Volume} (Win32 {Marshal.GetLastWin32Error()}). Eject was not requested.");
            }
            // Repeat identity resolution after preparation. Never apply the previous
            // disk number to an unrelated disk that has acquired the same letter.
            var current = PreviewAsync(volume, token).GetAwaiter().GetResult();
            if (!current.Ejectable || !string.Equals(current.Instance, preview.Instance, StringComparison.OrdinalIgnoreCase) ||
                current.DiskNumber != preview.DiskNumber || !current.Volumes.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(preview.Volumes))
                throw new IOException("The disk changed during eject preparation. Eject was not requested.");
            token.ThrowIfCancellationRequested();
            progress?.Report("Requesting safe removal from Windows");
            var locate = CM_Locate_DevNodeW(out var node, preview.Instance, 0);
            if (locate != 0)
                throw new IOException($"The disk disappeared before eject (Configuration Manager {locate}).");
            var veto = new StringBuilder(260);
            var result = CM_Request_Device_EjectW(node, out var vetoType, veto, (uint)veto.Capacity, 0);
            if (result != 0)
                throw new IOException($"Windows refused safe removal (Configuration Manager {result}, veto {vetoType}: {veto}).");
            // Configuration Manager can accept a request before device removal
            // becomes visible. Report that distinction rather than claiming a
            // vanished disk from its return code alone.
            var removed = false;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (CM_Locate_DevNodeW(out _, preview.Instance, 0) != 0)
                {
                    removed = true;
                    break;
                }
                Thread.Sleep(250);
            }
            return new(preview, result, vetoType, veto.ToString(), removed);
        }
        catch
        {
            // A veto is not a removal. Restore only a still-present matching
            // volume; never enable a different disk that inherited its letter.
            foreach (var (item, wasEnabled) in disabled.Where(d => d.WasEnabled))
            {
                try
                {
                    var live = VolumeCatalog.ListAsync(CancellationToken.None).GetAwaiter().GetResult();
                    if (!live.Any(v => v.Volume.Equals(item.Volume, StringComparison.OrdinalIgnoreCase) &&
                        v.VolumeId.Equals(item.VolumeId, StringComparison.OrdinalIgnoreCase) &&
                        v.Instance.Equals(item.Instance, StringComparison.OrdinalIgnoreCase) &&
                        v.DiskNumber == item.DiskNumber && v.Bytes == item.Bytes))
                        continue;
                    using var cache = new CacheDevice(item.Volume, writable: true);
                    cache.Control(WriteCacheAction.Enable);
                }
                catch (Exception ex)
                {
                    progress?.Report($"Could not resume {item.Volume} after refused eject: {ex.Message}");
                }
            }
            throw;
        }
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Locate_DevNodeW(out uint device, string instance, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_Registry_PropertyW(uint device, uint property, out uint type, out uint value, ref uint length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Request_Device_EjectW(uint device, out uint vetoType, StringBuilder vetoName, uint nameLength, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle handle);
}
