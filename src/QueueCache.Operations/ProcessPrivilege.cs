using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace QueueCache.Operations;

/// <summary>Enables one privilege of an elevated process for a scope, then restores the previous state.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ProcessPrivilege : IDisposable
{
    private readonly SafeAccessTokenHandle token;
    private readonly Privileges previous;
    private readonly string name;
    public ProcessPrivilege(string name, string requiredFor)
    {
        this.name = name;
        if (!OpenProcessToken(GetCurrentProcess(), 0x28, out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!LookupPrivilegeValueW(null, name, out var luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var desired = new Privileges { Count = 1, Luid = luid, Attributes = 2 };
            if (!AdjustTokenPrivileges(token, false, ref desired, (uint)Marshal.SizeOf<Privileges>(), out previous, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var error = Marshal.GetLastWin32Error();
            if (error != 0) throw new Win32Exception(error, $"{name} is required {requiredFor}.");
        }
        catch { token.Dispose(); throw; }
    }
    public void Dispose()
    {
        try
        {
            if (previous.Count == 0) return; // Already enabled; no token change to undo.
            var restore = previous;
            if (!AdjustTokenPrivileges(token, false, ref restore, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastWin32Error() != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not restore the process's {name}.");
        }
        finally { token.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct Privileges { public uint Count; public Luid Luid; public uint Attributes; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref Privileges state, uint length, out Privileges previous, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref Privileges state, uint length, IntPtr previous, IntPtr needed);
}

/// <summary>Windows' own copies of file data in RAM. Purging them makes the next read of a file reach the
/// volume (and so QueueCache) instead of Windows' file cache, as after a restart.</summary>
[SupportedOSPlatform("windows")]
internal static class StandbyMemory
{
    private const int SystemMemoryListInformation = 80;
    private const int MemoryEmptyWorkingSets = 2, MemoryPurgeStandbyList = 4;

    /// <summary>Empties every working set (the file cache's pages become standby) and purges the standby list.
    /// Requires an elevated process (SeProfileSingleProcessPrivilege).</summary>
    public static void Purge()
    {
        using var privilege = new ProcessPrivilege("SeProfileSingleProcessPrivilege", "to purge Windows' standby memory");
        foreach (var command in new[] { MemoryEmptyWorkingSets, MemoryPurgeStandbyList })
        {
            var value = command;
            var status = NtSetSystemInformation(SystemMemoryListInformation, ref value, sizeof(int));
            if (status != 0)
                throw new Win32Exception(RtlNtStatusToDosError(status), $"Purging Windows' memory lists failed (NTSTATUS 0x{status:X8}).");
        }
    }
    [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int informationClass, ref int information, int length);
    [DllImport("ntdll.dll")] private static extern int RtlNtStatusToDosError(int status);
}
