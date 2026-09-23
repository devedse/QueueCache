using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace QueueCache.Operations;

public static class MemoryBudget
{
    public const int MaximumMiB = 131072;
    internal const ulong MinimumSystemHeadroom = 2UL << 30;
    internal static ulong RequiredSystemHeadroom(ulong totalPhysical) =>
        Math.Max(MinimumSystemHeadroom, totalPhysical / 4);

    [SupportedOSPlatform("windows")]
    public static ulong AvailableForCache()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        // Available physical memory is a point-in-time estimate, not a reservation.
        // Keep meaningful application/OS headroom: a multi-GiB nonpaged cache plus
        // a large decoded image can otherwise leave a small VM with only 1 GiB.
        // Kernel allocation and the shared physical-RAM cap remain authoritative.
        var headroom = RequiredSystemHeadroom(status.TotalPhysical);
        return status.AvailablePhysical > headroom ? status.AvailablePhysical - headroom : 0;
    }
    [SupportedOSPlatform("windows")]
    public static void ValidateIncrease(ulong current, ulong requested)
    {
        if (requested > current && requested - current > AvailableForCache())
            throw new IOException("Not enough currently available RAM for this increase while preserving at least 2 GiB or 25% of physical RAM for Windows and applications. Choose a smaller budget.");
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
