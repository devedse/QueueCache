using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace QueueCache.Operations;

public static class MemoryBudget
{
    public const int MaximumMiB = 131072;
    [SupportedOSPlatform("windows")]
    public static ulong AvailableForCache()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) throw new Win32Exception(Marshal.GetLastWin32Error());
        // Available physical memory is a point-in-time estimate, not a reservation.
        // Kernel allocation and the shared physical-RAM cap remain authoritative.
        return status.AvailablePhysical > (1UL << 30) ? status.AvailablePhysical - (1UL << 30) : 0;
    }
    [SupportedOSPlatform("windows")]
    public static void ValidateIncrease(ulong current, ulong requested)
    {
        if (requested > current && requested - current > AvailableForCache())
            throw new IOException("Not enough currently available RAM for this increase while leaving 1 GiB for Windows. Choose a smaller budget.");
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtended;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
