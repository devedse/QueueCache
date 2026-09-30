using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace QueueCache.Operations;

/// <summary>Validated single-volume target. No guessed disk numbers or raw test writes.
/// The cache filters the volume: <see cref="Device"/> is the volume and <see cref="Bytes"/> its length,
/// which the driver reports as its device length. <see cref="Number"/>, <see cref="Instance"/> and
/// <see cref="DiskBytes"/> identify the physical disk that holds it.</summary>
[SupportedOSPlatform("windows")]
public sealed record DiskTarget(char Letter, int Number, long Bytes, string Instance,
    bool IsBoot = false, bool IsSystem = false, bool IsPaging = false)
{
    public string Root => $"{Letter}:\\";
    public string Device => $"{Letter}:";
    /// <summary>Size of the physical disk holding the volume.</summary>
    public long DiskBytes { get; init; }
    /// <summary>Volume GUID, e.g. {fa32f514-...}: stable across drive-letter changes; identifies saved profiles.</summary>
    public string VolumeId { get; init; } = "";

    // Pagefile configuration can change IsPaging across a restart. The other
    // fields identify the volume, physical disk and required system role.
    public static void ValidateRecordedSystemTarget(DiskTarget recorded, DiskTarget current)
    {
        if (recorded.Letter != current.Letter || recorded.Number != current.Number ||
            recorded.Bytes != current.Bytes || recorded.DiskBytes != current.DiskBytes ||
            (recorded.VolumeId.Length != 0 && !string.Equals(recorded.VolumeId, current.VolumeId, StringComparison.OrdinalIgnoreCase)) ||
            !string.Equals(recorded.Instance, current.Instance, StringComparison.OrdinalIgnoreCase) ||
            recorded.IsBoot != current.IsBoot || recorded.IsSystem != current.IsSystem)
            throw new IOException("System oracle target identity changed.");
    }

    /// <param name="requireFileSystem">False only for removing a cache task, which is safe on any volume (for
    /// example one a raw test left on an unformatted volume); creating or changing a task needs a mounted file
    /// system (NTFS, ReFS, FAT32, exFAT, ...).</param>
    public static async Task<DiskTarget> InspectAsync(string volume, CancellationToken cancellationToken = default, bool requireFileSystem = true)
    {
        if (volume.Length != 2 || !char.IsAsciiLetter(volume[0]) || volume[1] != ':')
            throw new ArgumentException("Select an explicit volume such as Q:, not a directory or raw disk.");
        var letter = char.ToUpperInvariant(volume[0]);
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-Command");
        // Only an already validated ASCII letter enters this script. Native volume extents are checked as well.
        info.ArgumentList.Add($"$ErrorActionPreference='Stop'; $p=@(Get-Partition -DriveLetter {letter}); if($p.Count -ne 1){{throw 'Ambiguous volume'}}; $d=Get-Disk -Number $p[0].DiskNumber; $c=Get-CimInstance Win32_DiskDrive -Filter ('Index='+$d.Number); $paging=@(Get-CimInstance Win32_PageFileUsage | ForEach-Object {{ (Get-Partition -DriveLetter $_.Name.Substring(0,1)).DiskNumber }}); [pscustomobject]@{{Letter='{letter}';Number=[int]$d.Number;Bytes=[long]$d.Size;Instance=$c.PNPDeviceID;IsBoot=[bool]$d.IsBoot;IsSystem=[bool]$d.IsSystem;IsPaging=[bool]($paging -contains $d.Number)}} | ConvertTo-Json -Compress");
        using var process = Process.Start(info) ?? throw new IOException("Cannot inspect disk.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"Disk inventory for {volume} exceeded 30 seconds (Get-Partition/Get-Disk/CIM).", ex);
        }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        if (process.ExitCode != 0)
            throw new IOException(await stderr);
        // The inventory reports the disk size as Bytes; the target's Bytes is the volume.
        var disk = JsonSerializer.Deserialize<DiskTarget>(await stdout) ?? throw new IOException("Missing disk identity.");
        if (disk.Number < 0 || disk.Bytes <= 0 || string.IsNullOrWhiteSpace(disk.Instance) || disk.Letter != letter)
            throw new IOException("Invalid disk identity.");
        var extent = ReadExtent(letter);
        var target = disk with { Bytes = extent.Length, DiskBytes = disk.Bytes, VolumeId = ReadVolumeId(letter) };
        if (requireFileSystem)
        {
            string format;
            try
            {
                format = new DriveInfo(target.Root).DriveFormat;
            }
            catch (IOException ex)
            {
                throw new IOException($"{letter}: has no file system Windows can read (it may be unformatted). Format it first.", ex);
            }
            if (!FileSystems.IsMounted(format))
                throw new IOException($"{letter}: has no file system Windows can read (it may be unformatted). Format it first.");
        }
        target.CheckExtents();
        return target;
    }

    public void CheckExtents() => ValidateExtent(this, ReadExtent(Letter));

    public void ValidateCurrent(CancellationToken cancellationToken = default, bool requireFileSystem = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (VolumeId.Length != 0 && !string.Equals(ReadVolumeId(Letter), VolumeId, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"{Letter}: is now a different volume; refusing operation.");
        var extent = ReadExtent(Letter);
        // Reject a remapped volume before opening even the previously recorded disk.
        ValidateExtent(this, extent);
        var instance = ReadDiskInstance(Number, Instance, cancellationToken);
        if (!MatchesInstance(Instance, instance))
            throw new IOException("Disk identity changed; refusing operation.");
        if (requireFileSystem)
            ValidateMountedIdentity(this, extent, instance, new DriveInfo(Root).DriveFormat);
    }

    /// <summary>The volume GUID mounted at this letter ({...}), from the mount manager.</summary>
    public static string ReadVolumeId(char letter)
    {
        var name = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPointW($"{letter}:\\", name, name.Capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return ParseVolumeId(name.ToString());
    }

    public static string ParseVolumeId(string volumeName) => VolumeIds.Parse(volumeName);

    private static (int Number, long Start, long Length) ReadExtent(char letter) => ReadExtentPath($"\\\\.\\{letter}:");

    private static (int Number, long Start, long Length) ReadExtentPath(string path)
    {
        using var handle = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = new byte[32];
        if (!DeviceIoControl(handle, 0x560000, IntPtr.Zero, 0, buffer, 32, out var returned, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (returned != 32 || BinaryPrimitives.ReadUInt32LittleEndian(buffer) != 1)
            throw new IOException("The selected volume must have exactly one disk extent.");
        return (BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(8)),
            BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(16)),
            BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(24)));
    }

    private static string ReadDiskInstance(int number, string expectedInstance, CancellationToken cancellationToken)
    {
        var path = InterfacePath(new Guid("53f56307-b6bf-11d0-94f2-00a0c91efb8b"), expectedInstance, cancellationToken);
        using var handle = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var device = new byte[12];
        if (!DeviceIoControl(handle, 0x2d1080, IntPtr.Zero, 0, device, 12, out var returned, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (returned != 12 || BinaryPrimitives.ReadInt32LittleEndian(device) != 7 ||
            BinaryPrimitives.ReadInt32LittleEndian(device.AsSpan(4)) != number)
            throw new IOException("Recorded disk interface has an unexpected device number/type.");
        return expectedInstance;
    }

    internal static (int Number, long Start, long Length) ReadVolumeInstanceExtent(string instance)
    {
        // ntddstor.h: hidden/unrecognized partitions have their own interface class.
        var path = FindInterfacePath(new Guid("53f5630d-b6bf-11d0-94f2-00a0c91efb8b"), instance, CancellationToken.None)
            ?? FindInterfacePath(new Guid("7f108a28-9833-4b3b-b780-2c6b5fa5c062"), instance, CancellationToken.None)
            ?? throw new IOException($"Could not resolve a normal or hidden volume interface for {instance}.");
        return ReadExtentPath(path);
    }

    private static string InterfacePath(Guid diskInterface, string expectedInstance, CancellationToken cancellationToken) =>
        FindInterfacePath(diskInterface, expectedInstance, cancellationToken)
        ?? throw new IOException($"Could not resolve the interface for {expectedInstance}.");

    private static string? FindInterfacePath(Guid diskInterface, string expectedInstance, CancellationToken cancellationToken)
    {
        var devices = SetupDiGetClassDevsW(ref diskInterface, null, IntPtr.Zero, 0x12);
        if (devices == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for (uint index = 0; ; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = new SpDeviceInterfaceData { Size = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref diskInterface, index, ref entry))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259)
                        break;
                    throw new Win32Exception(error);
                }
                var info = new SpDevInfoData { Size = Marshal.SizeOf<SpDevInfoData>() };
                // Ask Windows for the required size rather than assuming every interface path fits 4 KiB.
                var sized = SetupDiGetDeviceInterfaceDetailW(devices, ref entry, IntPtr.Zero, 0, out var required, ref info);
                var sizeError = Marshal.GetLastWin32Error();
                if (!sized && sizeError != 122)
                    throw new Win32Exception(sizeError);
                if (required < (IntPtr.Size == 8 ? 8 : 6))
                    throw new IOException("Invalid disk interface detail size.");
                var detail = Marshal.AllocHGlobal(required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(devices, ref entry, detail, required, out _, ref info))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    var instance = new StringBuilder(1024);
                    if (!SetupDiGetDeviceInstanceIdW(devices, ref info, instance, instance.Capacity, out _))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    // SetupAPI metadata identifies the candidate before any disk handle or IOCTL is used.
                    if (!MatchesInstance(expectedInstance, instance.ToString()))
                        continue;
                    var path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4)) ?? throw new IOException("Missing disk interface path.");
                    return path;
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }
        return null;
    }

    internal static void ValidateExtent(DiskTarget target, (int Number, long Start, long Length) extent)
    {
        if (extent.Number != target.Number || extent.Start < 0 || extent.Length <= 0 ||
            extent.Length != target.Bytes || extent.Start > target.DiskBytes - extent.Length)
            throw new IOException("Volume extents do not match the selected disk.");
    }

    internal static bool MatchesInstance(string expected, string actual) =>
        !string.IsNullOrWhiteSpace(expected) && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    internal static void ValidateMountedIdentity(DiskTarget target, (int Number, long Start, long Length) extent,
        string instance, string format)
    {
        ValidateExtent(target, extent);
        if (!string.Equals(instance, target.Instance, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Disk identity changed; refusing operation.");
        if (!FileSystems.IsMounted(format))
            throw new IOException("The volume no longer has a mounted file system.");
    }

    internal static void ValidateDeviceLength(DiskTarget target, ulong bytes)
    {
        if (bytes != (ulong)target.Bytes)
            throw new IOException("Volume length changed; refusing operation.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputBytes,
        [Out] byte[] output, uint outputBytes, out uint returned, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int Size; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public int Size; public Guid ClassGuid; public int DevInst; public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devices, IntPtr info, ref Guid classGuid, uint index,
        ref SpDeviceInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr devices, ref SpDeviceInterfaceData data,
        IntPtr detail, int detailBytes, out int requiredBytes, ref SpDevInfoData info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr devices, ref SpDevInfoData info, StringBuilder instance,
        int instanceChars, out int requiredChars);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
}
