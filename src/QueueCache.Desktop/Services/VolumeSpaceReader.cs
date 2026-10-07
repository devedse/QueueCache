using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using QueueCache.Desktop.ViewModels;

namespace QueueCache.Desktop.Services;

/// <summary>Size and free space of a mounted volume by its volume path; null when Windows cannot say.</summary>
[SupportedOSPlatform("windows")]
internal static class VolumeSpaceReader
{
    public static VolumeSpace? Read(string volumePath)
    {
        var root = volumePath.EndsWith('\\') ? volumePath : volumePath + "\\";
        return GetDiskFreeSpaceExW(root, out var available, out var total, out _) ? new VolumeSpace(total, available) : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong free);
}
