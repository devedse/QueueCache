using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace QueueCache.Operations.ManagedDisks;

/// <summary>Root adapter setup within the existing installer; no devcon dependency.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsProviderInstallation
{
    private const string HardwareId = @"ROOT\QueueCacheRamDisk";
    private static readonly Guid AdapterClass = new("4d36e97b-e325-11ce-bfc1-08002be10318");
    private static readonly IntPtr Invalid = new(-1);

    public static bool IsInstalled() { using var set = new DeviceSet(); return set.Find() is not null; }
    public static void RequireNoLiveDisks()
    {
        if (!IsInstalled()) return;
        using var provider = WindowsRamDisk.Connect();
        if (provider.Enumerate().Count != 0)
            throw new IOException("Stop or recover every managed RAM disk before updating or uninstalling QueueCache. No RAM contents were discarded.");
    }

    public static bool Install(string infPath)
    {
        infPath = Path.GetFullPath(infPath);
        if (!File.Exists(infPath) || !File.Exists(Path.Combine(Path.GetDirectoryName(infPath)!, "qcramdisk.sys")) ||
            !File.Exists(Path.Combine(Path.GetDirectoryName(infPath)!, "qcramdisk.cat")))
            throw new FileNotFoundException("The signed RAM provider package is incomplete.");
        using var set = new DeviceSet();
        if (set.Find() is null)
        {
            var info = DeviceInfo.New();
            Check(SetupDiCreateDeviceInfoW(set.Handle, "QueueCacheRamDisk", ref set.Class, "QueueCache RAM disk adapter", IntPtr.Zero, 1, ref info));
            var identifiers = Encoding.Unicode.GetBytes(HardwareId + "\0\0");
            Check(SetupDiSetDeviceRegistryPropertyW(set.Handle, ref info, 1, identifiers, (uint)identifiers.Length));
            Check(SetupDiCallClassInstaller(0x19, set.Handle, ref info)); // DIF_REGISTERDEVICE
        }
        Check(UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, HardwareId, infPath, 1, out var reboot));
        return reboot;
    }
    public static void Remove()
    {
        using var set = new DeviceSet();
        var found = set.Find();
        if (found is null) return;
        // Low-level uninstall also refuses private allocations, not just mounted letters.
        RequireNoLiveDisks();
        var info = found.Value;
        Check(SetupDiCallClassInstaller(5, set.Handle, ref info)); // DIF_REMOVE, whole owned root adapter.
    }
    private sealed class DeviceSet : IDisposable
    {
        public Guid Class = AdapterClass;
        public IntPtr Handle { get; }
        public DeviceSet()
        {
            Handle = SetupDiGetClassDevsW(ref Class, null, IntPtr.Zero, 2); // Present adapters only.
            if (Handle == Invalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public DeviceInfo? Find()
        {
            DeviceInfo? found = null;
            for (uint index = 0; ; ++index)
            {
                var info = DeviceInfo.New();
                if (!SetupDiEnumDeviceInfo(Handle, index, ref info))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new Win32Exception(error);
                }
                var bytes = new byte[4096];
                if (!SetupDiGetDeviceRegistryPropertyW(Handle, ref info, 1, out var type, bytes, (uint)bytes.Length, out var needed))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 13) continue;
                    throw new Win32Exception(error);
                }
                if (type != 7 || needed > bytes.Length || needed % 2 != 0) throw new InvalidDataException("Invalid adapter hardware identity.");
                var identities = Encoding.Unicode.GetString(bytes, 0, (int)needed).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (!identities.Contains(HardwareId, StringComparer.OrdinalIgnoreCase)) continue;
                if (found is not null) throw new IOException("Multiple QueueCache RAM adapters exist; reconcile them before installation.");
                found = info;
            }
            return found;
        }
        public void Dispose() { if (!SetupDiDestroyDeviceInfoList(Handle)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo
    {
        public uint Size;
        public Guid Class;
        public uint Instance;
        public IntPtr Reserved;
        public static DeviceInfo New() => new() { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
    }
    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid @class, string? enumerator, IntPtr window, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiCreateDeviceInfoW(IntPtr set, string name, ref Guid @class, string description, IntPtr window, uint flags, ref DeviceInfo info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref DeviceInfo info, uint property, byte[] data, uint length);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref DeviceInfo info, uint property, out uint type, [Out] byte[] data, uint length, out uint required);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref DeviceInfo info);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr window, string hardware, string inf, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool reboot);
}
