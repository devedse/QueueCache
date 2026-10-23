using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace QueueCache.Operations;

/// <summary>A budget increase was refused because too little RAM is available right now.</summary>
public sealed class InsufficientMemoryForCacheException(string message) : IOException(message);

public static class MemoryBudget
{
    public const int MaximumMiB = 131072;
    internal const ulong MinimumSystemHeadroom = 2UL << 30;
    internal static ulong RequiredSystemHeadroom(ulong totalPhysical) =>
        Math.Max(MinimumSystemHeadroom, totalPhysical / 4);

    [SupportedOSPlatform("windows")]
    public static ulong AvailableForCache() => AvailableForReservation();

    /// <summary>Shared cache/RAM-disk headroom estimate; the kernel budget and allocation remain authoritative.</summary>
    [SupportedOSPlatform("windows")]
    public static ulong AvailableForReservation()
    {
        var status = ReadStatus();
        // Available physical memory is a point-in-time estimate, not a reservation.
        // Keep meaningful application/OS headroom: a multi-GiB nonpaged cache plus
        // a large decoded image can otherwise leave a small VM with only 1 GiB.
        // Kernel allocation and the shared physical-RAM cap remain authoritative.
        var headroom = RequiredSystemHeadroom(status.TotalPhysical);
        return status.AvailablePhysical > headroom ? status.AvailablePhysical - headroom : 0;
    }
    /// <summary>The measured amounts behind a refusal, for logs: available and total RAM and the kept headroom.</summary>
    [SupportedOSPlatform("windows")]
    public static string DescribeAvailable()
    {
        static string GiB(ulong bytes) => $"{bytes / 1073741824.0:0.0} GiB";
        try
        {
            var status = ReadStatus();
            return $"{GiB(status.AvailablePhysical)} of {GiB(status.TotalPhysical)} RAM available; {GiB(RequiredSystemHeadroom(status.TotalPhysical))} is kept for Windows and applications";
        }
        catch (Win32Exception ex) { return "Available RAM is unknown: " + ex.Message; }
    }
    [SupportedOSPlatform("windows")]
    private static MemoryStatus ReadStatus()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return status;
    }
    public const string InsufficientMessage = "Not enough currently available RAM for this increase while preserving at least 2 GiB or 25% of physical RAM for Windows and applications. Choose a smaller budget.";
    [SupportedOSPlatform("windows")]
    public static void ValidateIncrease(ulong current, ulong requested)
    {
        if (requested > current && requested - current > AvailableForReservation())
            throw new InsufficientMemoryForCacheException(InsufficientMessage);
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
