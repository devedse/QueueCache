using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace QueueCache.Operations.ManagedDisks;

/// <summary>Typed Windows VHDX primitives; lifecycle/format transactions belong to the coordinator.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVirtualDisk : IDisposable
{
    private static readonly Guid MicrosoftVendor = new("EC984AEC-A0F9-47E9-901F-71415A66345B");
    private readonly SafeFileHandle handle;
    private bool attached;
    private WindowsVirtualDisk(SafeFileHandle handle) => this.handle = handle;

    public static ImageInspection Inspect(string path, bool allowAttached = false)
    {
        ManagedDiskPaths.ValidateImagePath(path);
        using var identityHandle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, allowAttached ? FileShare.ReadWrite : FileShare.Read);
        var fileId = new byte[24]; // FILE_ID_INFO: volume serial and 128-bit file ID.
        if (!GetFileInformationByHandleEx(identityHandle, 18, fileId, (uint)fileId.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var finalPath = new StringBuilder(32768);
        var pathLength = GetFinalPathNameByHandle(identityHandle, finalPath, (uint)finalPath.Capacity, 0);
        if (pathLength == 0 || pathLength >= finalPath.Capacity)
            throw new IOException("Could not resolve the source image's final handle path.");
        using var disk = Open(path, informationOnly: true, readOnly: true);
        var size = disk.Information(1);
        // GET_VIRTUAL_DISK_INFO_VIRTUAL_DISK_ID. IDENTIFIER (2) changes once a VHDX has been
        // opened for writing (seen on the VM), so it cannot identify a used image.
        var id = disk.Information(14);
        var subtype = disk.Information(7);
        return new(path, finalPath + "|" + Convert.ToHexString(fileId), new Guid(id.AsSpan(8, 16)),
            BitConverter.ToUInt64(size, 8), BitConverter.ToUInt64(size, 16),
            BitConverter.ToUInt32(size, 28), BitConverter.ToUInt32(subtype, 8) == 4);
    }

    public static void DeleteDetachedOwnedImage(ImageInspection expected)
    {
        ManagedDiskPaths.ValidateImagePath(expected.Path);
        // DELETE access with no write/delete sharing excludes attached/writable views.
        // Disposition applies to this exact opened file, never a subsequently substituted path.
        using var target = CreateFileW(expected.Path, 0x80010000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (target.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "The image is attached, in use, or unavailable for owned deletion.");
        var identity = new byte[24];
        if (!GetFileInformationByHandleEx(target, 18, identity, (uint)identity.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!Convert.ToHexString(identity).Equals(expected.FileIdentity[(expected.FileIdentity.LastIndexOf('|') + 1)..], StringComparison.OrdinalIgnoreCase))
            throw new IOException("The deletion target file identity changed.");
        var disposition = 1;
        if (!SetFileInformationByHandle(target, 4, ref disposition, 4)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows refused deleting the owned detached image.");
    }

    public static WindowsVirtualDisk Open(string path, bool informationOnly, bool readOnly)
    {
        ManagedDiskPaths.ValidateImagePath(path);
        var storage = new VirtualStorage { DeviceId = 3, Vendor = MicrosoftVendor };
        var parameters = new OpenParameters { Version = 2, InformationOnly = informationOnly ? 1 : 0, ReadOnly = readOnly ? 1 : 0 };
        Check(OpenVirtualDisk(ref storage, path, 0, 0, ref parameters, out var image));
        return new(image);
    }

    public static WindowsVirtualDisk CreateNew(string path, ulong capacityBytes, uint sectorBytes, ImageAllocation allocation)
    {
        ManagedDiskPaths.ValidateImagePath(path);
        if (capacityBytes < 16UL << 20 || capacityBytes > long.MaxValue || sectorBytes is not (512 or 4096) ||
            capacityBytes % (1UL << 20) != 0 || !Enum.IsDefined(allocation))
            throw new ArgumentException("Invalid new image geometry.");
        var storage = new VirtualStorage { DeviceId = 3, Vendor = MicrosoftVendor };
        var parameters = new CreateParameters { Version = 2, Id = Guid.NewGuid(), MaximumSize = capacityBytes,
            SectorBytes = sectorBytes, PhysicalSectorBytes = 4096 };
        // Native creation never overwrites an existing file. Version 2 requires access mask NONE.
        Check(CreateVirtualDisk(ref storage, path, 0, IntPtr.Zero, allocation == ImageAllocation.Fixed ? 1U : 0U,
            0, ref parameters, IntPtr.Zero, out var image));
        return new(image);
    }

    /// <summary>No-letter alone does not prove isolation; caller must qualify offline staging before raw transfer.</summary>
    public string Attach(bool readOnly, bool permanent = false)
    {
        if (attached)
            throw new InvalidOperationException("This owned image view is already attached.");
        var parameters = new AttachParameters { Version = 1 };
        Check(AttachVirtualDisk(handle, IntPtr.Zero, (readOnly ? 3U : 2U) | (permanent ? 4U : 0U), 0, ref parameters, IntPtr.Zero));
        attached = true;
        return PhysicalPath();
    }

    public string PhysicalPath()
    {
        uint size = 32768 * 2;
        var physical = new StringBuilder((int)size / 2);
        Check(GetVirtualDiskPhysicalPath(handle, ref size, physical));
        return physical.ToString();
    }

    public void AdoptAttached() { _ = PhysicalPath(); attached = true; }

    public void Detach()
    {
        if (!attached)
            return;
        Check(DetachVirtualDisk(handle, 0, 0));
        attached = false;
    }

    private byte[] Information(uint version)
    {
        var bytes = new byte[80];
        BitConverter.GetBytes(version).CopyTo(bytes, 0);
        var size = (uint)bytes.Length;
        Check(GetVirtualDiskInformation(handle, ref size, bytes, out _));
        return bytes;
    }

    public void Dispose()
    {
        // Non-permanent attach lifetime remains scoped to this handle until the broker owns it.
        handle.Dispose();
    }

    private static void Check(uint status) { if (status != 0) throw new Win32Exception(unchecked((int)status)); }
    [StructLayout(LayoutKind.Sequential)]
    private struct VirtualStorage { public uint DeviceId; public Guid Vendor; }
    [StructLayout(LayoutKind.Sequential)]
    private struct OpenParameters { public uint Version; public int InformationOnly, ReadOnly; public Guid Resiliency; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AttachParameters { public uint Version, Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CreateParameters
    {
        public uint Version, UnionPadding;
        public Guid Id;
        public ulong MaximumSize;
        public uint BlockBytes, SectorBytes, PhysicalSectorBytes;
        public IntPtr Parent, Source;
        public uint OpenFlags;
        public VirtualStorage ParentType, SourceType;
        public Guid Resiliency;
    }
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint OpenVirtualDisk(ref VirtualStorage storage, string path, uint access, uint flags, ref OpenParameters parameters, out SafeFileHandle handle);
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint CreateVirtualDisk(ref VirtualStorage storage, string path, uint access, IntPtr security,
        uint flags, uint providerFlags, ref CreateParameters parameters, IntPtr overlapped, out SafeFileHandle handle);
    [DllImport("virtdisk.dll")]
    private static extern uint GetVirtualDiskInformation(SafeFileHandle handle, ref uint size, [In, Out] byte[] information, out uint used);
    [DllImport("virtdisk.dll")]
    private static extern uint AttachVirtualDisk(SafeFileHandle handle, IntPtr security, uint flags, uint providerFlags, ref AttachParameters parameters, IntPtr overlapped);
    [DllImport("virtdisk.dll")]
    private static extern uint DetachVirtualDisk(SafeFileHandle handle, uint flags, uint providerFlags);
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetVirtualDiskPhysicalPath(SafeFileHandle handle, ref uint size, StringBuilder path);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, uint informationClass, [Out] byte[] information, uint size);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, uint informationClass, ref int data, uint bytes);
}
