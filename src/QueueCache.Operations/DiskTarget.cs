using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace QueueCache.Operations;

/// <summary>Validated single-volume target. No guessed disk numbers or raw test writes.</summary>
[SupportedOSPlatform("windows")]
public sealed record DiskTarget(char Letter, int Number, long Bytes, string Instance)
{
    public string Root => $"{Letter}:\\";
    public string Device => $"PhysicalDrive{Number}";

    public static async Task<DiskTarget> InspectAsync(string volume, CancellationToken cancellationToken = default)
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
        info.ArgumentList.Add($"$ErrorActionPreference='Stop'; $p=@(Get-Partition -DriveLetter {letter}); if($p.Count -ne 1){{throw 'Ambiguous volume'}}; $d=Get-Disk -Number $p[0].DiskNumber; $c=Get-CimInstance Win32_DiskDrive -Filter ('Index='+$d.Number); [pscustomobject]@{{Letter='{letter}';Number=[int]$d.Number;Bytes=[long]$d.Size;Instance=$c.PNPDeviceID}} | ConvertTo-Json -Compress");
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
        var target = JsonSerializer.Deserialize<DiskTarget>(await stdout) ?? throw new IOException("Missing disk identity.");
        if (target.Number < 0 || target.Bytes <= 0 || string.IsNullOrWhiteSpace(target.Instance) || target.Letter != letter)
            throw new IOException("Invalid disk identity.");
        if (!string.Equals(new DriveInfo(target.Root).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Only NTFS volumes are currently supported.");
        target.CheckExtents();
        return target;
    }

    public void CheckExtents() => ValidateExtent(this, ReadExtent(Letter));

    public void ValidateCurrent(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extent = ReadExtent(Letter);
        // Reject a remapped volume before opening even the previously recorded disk.
        ValidateExtent(this, extent);
        ValidateMountedIdentity(this, extent, ReadDiskInstance(Number, Instance, cancellationToken),
            new DriveInfo(Root).DriveFormat);
    }

    private static (int Number, long Start, long Length) ReadExtent(char letter)
    {
        using var handle = CreateFileW($"\\\\.\\{letter}:", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
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
        var diskInterface = new Guid("53f56307-b6bf-11d0-94f2-00a0c91efb8b");
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
                    using var handle = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open the recorded disk interface.");
                    var device = new byte[12];
                    if (!DeviceIoControl(handle, 0x2d1080, IntPtr.Zero, 0, device, 12, out var returned, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (returned != 12 || BinaryPrimitives.ReadInt32LittleEndian(device) != 7 ||
                        BinaryPrimitives.ReadInt32LittleEndian(device.AsSpan(4)) != number)
                        throw new IOException("Recorded disk interface has an unexpected device number/type.");
                    return instance.ToString();
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }
        throw new IOException($"Could not resolve the PnP identity of PhysicalDrive{number}.");
    }

    internal static void ValidateExtent(DiskTarget target, (int Number, long Start, long Length) extent)
    {
        if (extent.Number != target.Number || extent.Start < 0 || extent.Length <= 0 || extent.Start > target.Bytes - extent.Length)
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
        if (!string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Only NTFS volumes are currently supported.");
    }

    internal static void ValidateDeviceLength(DiskTarget target, ulong bytes)
    {
        if (bytes != (ulong)target.Bytes)
            throw new IOException("Disk length changed; refusing operation.");
    }

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
