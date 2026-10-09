using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace QueueCache.Developer.FileTests;

/// <summary>Writes only a newly created test file's allocated prefix, bypassing Windows' file cache.
/// Optional write-through/flush requests exercise the selected driver policy.</summary>
internal static class UnbufferedFileWrite
{
    public static byte[] ReadPrefix(string path, int length)
    {
        if (length <= 0 || length % 4096 != 0)
            throw new ArgumentException("Requires aligned payload.");
        using var file = CreateFileW(path, 0x80000000, 3, IntPtr.Zero, 3, 0x20000000, IntPtr.Zero);
        if (file.IsInvalid)
            throw new Win32Exception();
        var memory = VirtualAlloc(IntPtr.Zero, (nuint)length, 0x3000, 4);
        if (memory == IntPtr.Zero)
            throw new Win32Exception();
        try
        {
            if (!ReadFile(file, memory, (uint)length, out var read, IntPtr.Zero))
                throw new Win32Exception();
            if (read != length)
                throw new IOException("Short file read.");
            var result = new byte[length];
            Marshal.Copy(memory, result, 0, length);
            return result;
        }
        finally { VirtualFree(memory, 0, 0x8000); }
    }
    /// <summary>One complete sequential pass over whole files with unbuffered 1 MiB reads, one at a time.
    /// Unlike a timed DiskSpd window, every byte is read exactly once however slow the disk is.</summary>
    public static (long Bytes, long Requests, double Seconds) ReadPass(IReadOnlyList<string> paths)
    {
        const int block = 1 << 20;
        var memory = VirtualAlloc(IntPtr.Zero, block, 0x3000, 4);
        if (memory == IntPtr.Zero)
            throw new Win32Exception();
        try
        {
            long bytes = 0, requests = 0;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            foreach (var path in paths)
            {
                // FILE_FLAG_NO_BUFFERING | FILE_FLAG_SEQUENTIAL_SCAN: every read reaches the volume.
                using var file = CreateFileW(path, 0x80000000, 3, IntPtr.Zero, 3, 0x20000000 | 0x08000000, IntPtr.Zero);
                if (file.IsInvalid)
                    throw new Win32Exception();
                var length = new FileInfo(path).Length;
                if (length <= 0 || length % block != 0)
                    throw new IOException("Read passes require whole-MiB files: " + path);
                for (long offset = 0; offset < length; offset += block)
                {
                    if (!ReadFile(file, memory, block, out var read, IntPtr.Zero))
                        throw new Win32Exception();
                    if (read != block)
                        throw new IOException("Short read in a whole-file pass: " + path);
                    bytes += read;
                    requests++;
                }
            }
            return (bytes, requests, timer.Elapsed.TotalSeconds);
        }
        finally { VirtualFree(memory, 0, 0x8000); }
    }
    public static void WritePrefix(string path, byte[] bytes, bool writeThrough = false, bool flush = false)
    {
        if (bytes.Length == 0 || bytes.Length % 4096 != 0)
            throw new ArgumentException("Requires aligned payload.");
        WriteRange(path, 0, bytes, writeThrough, flush);
    }
    /// <summary>Sector-aligned writes to an existing owned file; callers validate target sector size.</summary>
    public static void WriteRange(string path, long offset, byte[] bytes, bool writeThrough = false, bool flush = false)
    {
        if (bytes.Length == 0 || bytes.Length % 512 != 0 || offset < 0 || offset % 512 != 0)
            throw new ArgumentException("Requires sector-aligned offset and payload.");
        using var file = CreateFileW(path, 0x40000000, 3, IntPtr.Zero, 3, 0x20000000u | (writeThrough ? 0x80000000u : 0), IntPtr.Zero);
        if (file.IsInvalid)
            throw new Win32Exception();
        var memory = VirtualAlloc(IntPtr.Zero, (nuint)bytes.Length, 0x3000, 4);
        if (memory == IntPtr.Zero)
            throw new Win32Exception();
        try
        {
            if (!SetFilePointerEx(file, offset, out _, 0))
                throw new Win32Exception();
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            if (!WriteFile(file, memory, (uint)bytes.Length, out var written, IntPtr.Zero))
                throw new Win32Exception();
            if (written != bytes.Length)
                throw new IOException("Short file write.");
            if (flush && !FlushFileBuffers(file))
                throw new Win32Exception();
        }
        finally { VirtualFree(memory, 0, 0x8000); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFilePointerEx(SafeFileHandle handle, long distance, out long position, uint method);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(SafeFileHandle handle, IntPtr data, uint length, out uint written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(SafeFileHandle handle, IntPtr data, uint length, out uint read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, nuint size, uint allocation, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(IntPtr address, nuint size, uint freeType);
}
