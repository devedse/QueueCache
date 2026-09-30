using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using QueueCache.Management;

namespace QueueCache.Operations;

public sealed record DiskEjectPreview(int DiskNumber, string Instance, string Name, IReadOnlyList<string> Volumes,
    bool Ejectable, string? UnsupportedReason, long DiskBytes = 0,
    IReadOnlyDictionary<string, string>? VolumeIds = null, string? RemovalInstance = null, string[]? RemovalMembers = null,
    RemovalRelationQuery[]? RemovalRelations = null, RemovalVolumeExtent[]? RemovalVolumes = null);
public sealed record DiskEjectPreparation(string Volume, string VolumeId, WriteCacheState Before, WriteCacheState Disabled,
    CacheAttribution? BeforeLower, CacheAttribution? DisabledLower, bool FileSystemFlushed);
public sealed record DiskEjectResult(DiskEjectPreview Disk, uint ConfigurationManagerResult, uint VetoType, string VetoName,
    bool RemovalObserved, uint PresenceResult = 0, string[]? RollbackErrors = null, DiskEjectPreparation[]? Preparation = null);
public sealed class DiskEjectVetoException(DiskEjectResult result) : IOException(
    $"Windows refused safe removal (Configuration Manager {result.ConfigurationManagerResult}, veto {result.VetoType}: {result.VetoName}).")
{
    public DiskEjectResult Result { get; internal set; } = result;
}

/// <summary>One physical-disk eject, including every lettered volume on the disk.</summary>
public static class DiskEjection
{
    private const uint Capabilities = 0x10; // CM_DRP_CAPABILITIES

    [SupportedOSPlatform("windows")]
    public static async Task<DiskEjectPreview> PreviewAsync(string volume, CancellationToken token = default)
    {
        var inventory = await DiskCatalog.ListAsync(token);
        var found = inventory.Where(d => d.Volumes.Contains(volume, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (found.Length != 1)
            throw new IOException($"{volume} must identify exactly one present physical disk.");
        var preview = Preview(found[0]);
        var mounted = (await VolumeCatalog.ListAsync(token)).Where(v => v.DiskNumber == preview.DiskNumber).ToArray();
        if (mounted.Any(v => !v.Instance.Equals(preview.Instance, StringComparison.OrdinalIgnoreCase)) ||
            !mounted.Select(v => v.Volume).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(preview.Volumes))
            throw new IOException("The disk's mounted-volume identities changed during preview.");
        return preview with { DiskBytes = found[0].Bytes, VolumeIds = mounted.ToDictionary(v => v.Volume, v => v.VolumeId, StringComparer.OrdinalIgnoreCase) };
    }

    [SupportedOSPlatform("windows")]
    internal static DiskEjectPreview Preview(DiskDescription disk)
    {
        var reason = SafetyReason(disk);
        DeviceRemovalScope.Scope? scope = null;
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
                else
                {
                    try { scope = DeviceRemovalScope.Resolve(node, disk.Instance, flags, disk.Number, disk.Bytes); }
                    catch (Exception ex) when (ex is IOException or NotSupportedException or System.ComponentModel.Win32Exception) { reason = ex.Message; }
                }
            }
        }
        return new(disk.Number, disk.Instance, disk.Name, disk.Volumes, reason is null, reason,
            RemovalInstance: scope?.Instance, RemovalMembers: scope?.Members, RemovalRelations: scope?.Relations, RemovalVolumes: scope?.Volumes);
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

    internal static bool SameMountedVolume(VolumeDescription expected, VolumeDescription current) =>
        current.Volume.Equals(expected.Volume, StringComparison.OrdinalIgnoreCase) &&
        current.VolumeId.Equals(expected.VolumeId, StringComparison.OrdinalIgnoreCase) &&
        current.Instance.Equals(expected.Instance, StringComparison.OrdinalIgnoreCase) &&
        current.DiskNumber == expected.DiskNumber && current.Bytes == expected.Bytes;

    internal static void ValidatePreview(DiskEjectPreview expected, DiskEjectPreview current)
    {
        if (expected.DiskNumber != current.DiskNumber || expected.DiskBytes != current.DiskBytes ||
            string.IsNullOrWhiteSpace(expected.RemovalInstance) || expected.RemovalMembers is null || current.RemovalMembers is null ||
            !string.Equals(expected.RemovalInstance, current.RemovalInstance, StringComparison.OrdinalIgnoreCase) ||
            !(expected.RemovalMembers ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(current.RemovalMembers ?? []) ||
            !expected.Instance.Equals(current.Instance, StringComparison.OrdinalIgnoreCase) ||
            !expected.Volumes.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(current.Volumes) ||
            expected.VolumeIds is null || current.VolumeIds is null ||
            expected.VolumeIds.Count != expected.Volumes.Count || current.VolumeIds.Count != current.Volumes.Count ||
            expected.VolumeIds.Any(pair => !current.VolumeIds.Any(live =>
                live.Key.Equals(pair.Key, StringComparison.OrdinalIgnoreCase) &&
                live.Value.Equals(pair.Value, StringComparison.OrdinalIgnoreCase))))
            throw new IOException("The disk or volumes changed after eject preview. Refresh and confirm the current disk.");
    }

    [SupportedOSPlatform("windows")]
    public static async Task<DiskEjectResult> EjectAsync(string volume, IProgress<string>? progress = null, CancellationToken token = default,
        DiskEjectPreview? expected = null, bool capturePreparation = false)
    {
        var preview = await PreviewAsync(volume, token);
        if (expected is not null)
            ValidatePreview(expected, preview);
        if (!preview.Ejectable)
            throw new NotSupportedException(preview.UnsupportedReason);
        var original = await VolumeCatalog.ListAsync(token);
        var affected = original.Where(v => v.DiskNumber == preview.DiskNumber).ToArray();
        if (affected.Length != preview.Volumes.Count || affected.Any(v => !string.Equals(v.Instance, preview.Instance, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The disk's volume inventory changed before eject.");
        var targets = new List<DiskTarget>(affected.Length);
        foreach (var item in affected)
        {
            var target = await DiskTarget.InspectAsync(item.Volume, token);
            if (target.Number != preview.DiskNumber ||
                !target.Instance.Equals(preview.Instance, StringComparison.OrdinalIgnoreCase) ||
                !target.VolumeId.Equals(item.VolumeId, StringComparison.OrdinalIgnoreCase) || target.Bytes != item.Bytes)
                throw new IOException($"{item.Volume} has an ambiguous or changed disk extent. Eject was not requested.");
            targets.Add(target);
        }
        return await Task.Run(() => EjectPrepared(volume, preview, affected, targets, progress, token, capturePreparation), token);
    }

    [SupportedOSPlatform("windows")]
    private static DiskEjectResult EjectPrepared(string volume, DiskEjectPreview preview,
        VolumeDescription[] affected, IReadOnlyList<DiskTarget> targets, IProgress<string>? progress, CancellationToken token, bool capturePreparation)
    {
        // ConfigurationGate is a thread-affine mutex: no await while it is held.
        // QUERY_REMOVE closes admission for native eject; explicit Disable also
        // covers the interval before Windows begins its PnP request.
        using var gate = ConfigurationGate.Enter(preview.Instance);
        foreach (var target in targets)
            target.ValidateCurrent(token);
        var beforePreparation = PreviewAsync(volume, token).GetAwaiter().GetResult();
        ValidatePreview(preview, beforePreparation);
        if (!beforePreparation.Ejectable || beforePreparation.DiskNumber != preview.DiskNumber ||
            !string.Equals(beforePreparation.Instance, preview.Instance, StringComparison.OrdinalIgnoreCase) ||
            !beforePreparation.Volumes.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(preview.Volumes))
            throw new IOException("The disk changed before eject preparation.");
        var disabled = new List<(VolumeDescription Volume, bool WasEnabled)>();
        var preparation = new List<DiskEjectPreparation>();
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
                var beforeLower = capturePreparation ? cache.GetDiagnostics().Attribution
                    ?? throw new NotSupportedException("Removal verification requires lower I/O attempt diagnostics.") : null;
                if (before.BudgetBytes > 0)
                {
                    cache.Control(WriteCacheAction.Disable);
                    disabled.Add((item, before.Enabled));
                }
                if (capturePreparation)
                    preparation.Add(new(item.Volume, item.VolumeId, before, cache.GetWriteCacheState(),
                        beforeLower, cache.GetDiagnostics().Attribution, false));
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
                if (capturePreparation)
                {
                    var index = preparation.FindIndex(p => p.Volume == item.Volume);
                    preparation[index] = preparation[index] with { FileSystemFlushed = true };
                }
            }
            // Repeat identity resolution after preparation. Never apply the previous
            // disk number to an unrelated disk that has acquired the same letter.
            var current = PreviewAsync(volume, token).GetAwaiter().GetResult();
            ValidatePreview(preview, current);
            if (!current.Ejectable || !string.Equals(current.Instance, preview.Instance, StringComparison.OrdinalIgnoreCase) ||
                current.DiskNumber != preview.DiskNumber || !current.Volumes.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(preview.Volumes))
                throw new IOException("The disk changed during eject preparation. Eject was not requested.");
            foreach (var target in targets)
                target.ValidateCurrent(token);
            token.ThrowIfCancellationRequested();
            progress?.Report("Requesting safe removal from Windows");
            var locate = CM_Locate_DevNodeW(out var node, preview.RemovalInstance ?? preview.Instance, 0);
            if (locate != 0)
                throw new IOException($"The disk disappeared before eject (Configuration Manager {locate}).");
            var veto = new StringBuilder(260);
            var result = CM_Request_Device_EjectW(node, out var vetoType, veto, (uint)veto.Capacity, 0);
            if (result != 0)
            {
                var presenceAfterVeto = CM_Locate_DevNodeW(out _, preview.Instance, 0);
                throw new DiskEjectVetoException(new(preview, result, vetoType, veto.ToString(), false, presenceAfterVeto, Preparation: capturePreparation ? preparation.ToArray() : null));
            }
            // Configuration Manager can accept a request before device removal
            // becomes visible. Report that distinction rather than claiming a
            // vanished disk from its return code alone.
            var removed = false;
            uint presence = 0;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                presence = CM_Locate_DevNodeW(out _, preview.Instance, 0);
                if (presence == 0x0D) // CR_NO_SUCH_DEVNODE; other API errors do not prove absence.
                {
                    removed = true;
                    break;
                }
                if (presence != 0)
                {
                    progress?.Report($"Windows accepted eject, but device-presence verification failed (Configuration Manager {presence}).");
                    break;
                }
                Thread.Sleep(250);
            }
            return new(preview, result, vetoType, veto.ToString(), removed, presence, Preparation: capturePreparation ? preparation.ToArray() : null);
        }
        catch (Exception failure)
        {
            // A veto is not a removal. Restore only a still-present matching
            // volume; never enable a different disk that inherited its letter.
            var rollbackErrors = new List<string>();
            foreach (var (item, wasEnabled) in disabled.Where(d => d.WasEnabled))
            {
                try
                {
                    var live = VolumeCatalog.ListAsync(CancellationToken.None).GetAwaiter().GetResult();
                    if (!live.Any(v => SameMountedVolume(item, v)))
                        continue;
                    using var cache = new CacheDevice(item.Volume, writable: true);
                    cache.Control(WriteCacheAction.Enable);
                }
                catch (Exception ex)
                {
                    var detail = $"Could not resume {item.Volume} after refused eject: {ex.Message}";
                    rollbackErrors.Add(detail);
                    progress?.Report(detail);
                }
            }
            if (failure is DiskEjectVetoException vetoFailure)
                vetoFailure.Result = vetoFailure.Result with { RollbackErrors = rollbackErrors.ToArray() };
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
