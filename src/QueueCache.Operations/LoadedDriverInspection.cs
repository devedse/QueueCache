using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace QueueCache.Operations;

public sealed record LoadedDriverFile(string ModulePath, string FilePath, string? FileSha256, string? FileVersion, string? FileError);
public sealed record LoadedDriverObservation(bool Available, string? Error, IReadOnlyList<LoadedDriverFile> Modules);

/// <summary>Loaded module paths from PSAPI, never inferred from SCM registration.
/// File hashes describe the current on-disk file, not a hash of kernel memory.</summary>
[SupportedOSPlatform("windows")]
public static class LoadedDriverInspection
{
    private static readonly object gate = new();
    public static LoadedDriverObservation Capture()
    {
        lock (gate)
        {
            try
            {
                using var privilege = new DebugPrivilege();
                var modules = new List<LoadedDriverFile>();
                foreach (var address in Addresses())
                {
                    var path = new StringBuilder(32768);
                    var length = GetDeviceDriverFileNameW(address, path, (uint)path.Capacity);
                    if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "A loaded module path could not be read.");
                    if (length >= path.Capacity - 1) throw new IOException("A loaded module path was truncated.");
                    var module = path.ToString();
                    var name = Path.GetFileName(module);
                    if (!name.Equals("qcachelab.sys", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("qcramdisk.sys", StringComparison.OrdinalIgnoreCase) &&
                        !(name.StartsWith("QueueCache-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".sys", StringComparison.OrdinalIgnoreCase))) continue;
                    var file = NormalizePath(module);
                    string? hash = null, version = null, error = null;
                    try
                    {
                        using var stream = File.OpenRead(file);
                        hash = Convert.ToHexString(SHA256.HashData(stream));
                        version = FileVersionInfo.GetVersionInfo(file).FileVersion;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = ex.Message; }
                    modules.Add(new(module, file, hash, version, error));
                }
                return new(true, null, modules);
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
            { return new(false, ex.Message, []); }
        }
    }
    public static string NormalizePath(string value)
    {
        var path = value.Trim('"');
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) return Path.Combine(windows, path[12..]);
        if (path.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase)) return Path.Combine(windows, path);
        return path;
    }
    private static IntPtr[] Addresses()
    {
        var buffer = new IntPtr[1024];
        for (var attempt = 0; attempt < 4; ++attempt)
        {
            if (!EnumDeviceDrivers(buffer, checked((uint)(buffer.Length * IntPtr.Size)), out var needed))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (needed % IntPtr.Size != 0 || needed > 1024 * 1024) throw new IOException("Invalid loaded-module enumeration size.");
            var count = checked((int)(needed / IntPtr.Size));
            if (count > buffer.Length) { buffer = new IntPtr[count + 128]; continue; }
            // Windows 11 24H2 reports success with null addresses without SeDebugPrivilege.
            if (count == 0 || buffer.Take(count).Any(address => address == IntPtr.Zero))
                throw new IOException("Loaded-module enumeration returned no usable addresses; absence cannot be established.");
            return buffer[..count];
        }
        throw new IOException("Loaded modules changed repeatedly during enumeration; refresh the observation.");
    }
    private sealed class DebugPrivilege : IDisposable
    {
        private readonly SafeAccessTokenHandle token;
        private readonly Privileges previous;
        public DebugPrivilege()
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x28, out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!LookupPrivilegeValueW(null, "SeDebugPrivilege", out var luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var desired = new Privileges { Count = 1, Luid = luid, Attributes = 2 };
                if (!AdjustTokenPrivileges(token, false, ref desired, (uint)Marshal.SizeOf<Privileges>(), out previous, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var error = Marshal.GetLastWin32Error();
                if (error != 0) throw new Win32Exception(error, "SeDebugPrivilege is required to observe loaded drivers.");
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
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not restore the process debug privilege.");
            }
            finally { token.Dispose(); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct Privileges { public uint Count; public Luid Luid; public uint Attributes; }
    [DllImport("psapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDeviceDrivers([Out] IntPtr[] addresses, uint size, out uint needed);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetDeviceDriverFileNameW(IntPtr address, StringBuilder path, uint size);
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
