using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

public sealed record WindowsEjectPrecondition(DiskTarget Target, QueueCache.Management.WriteCacheState State,
    QueueCache.Management.CacheDiagnostics Diagnostics, DateTimeOffset Requested);

/// <summary>Verification only: request native Windows eject without product cache preparation.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsDiskEjection
{
    internal static DiskEjectResult Request(DiskEjectPreview preview)
    {
        if (!preview.Ejectable || string.IsNullOrWhiteSpace(preview.RemovalInstance))
            throw new NotSupportedException(preview.UnsupportedReason ?? "Missing verified removal node.");
        Check(CM_Locate_DevNodeW(out var node, preview.RemovalInstance, 0));
        var veto = new StringBuilder(260);
        var result = CM_Request_Device_EjectW(node, out var vetoType, veto, (uint)veto.Capacity, 0);
        uint presence = 0;
        var removed = false;
        for (var attempt = 0; attempt < (result == 0 ? 20 : 1); attempt++)
        {
            presence = CM_Locate_DevNodeW(out _, preview.Instance, 0);
            if (presence == 0x0D) { removed = true; break; }
            if (presence != 0 || result != 0) break;
            Thread.Sleep(250);
        }
        return new(preview, result, vetoType, veto.ToString(), removed, presence);
    }
    private static void Check(uint result)
    {
        if (result != 0) throw new IOException($"Windows cannot locate the verified removal node ({result}).");
    }
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Request_Device_EjectW(uint node, out uint vetoType, StringBuilder vetoName, uint length, uint flags);
}
