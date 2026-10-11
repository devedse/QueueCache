using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace QueueCache.Operations;

/// <summary>One thread keeps a fixed number of overlapped unbuffered 1 MiB reads in flight, like a
/// single-thread DiskSpd queue, over a <see cref="RamReadFileOracle"/> file. Every 32 KiB of each block
/// is checked against the file's pattern; those words are poisoned before each read, so a chunk that
/// was never copied, or was copied to the wrong place, cannot pass. Any read error fails.</summary>
[SupportedOSPlatform("windows")]
public static class OverlappedReadOracle
{
    public sealed record Result(long Blocks, int Depth, double Seconds);
    private const int Block = 1 << 20, Stride = 32 * 1024, Samples = Block / Stride;
    private const long Poison = unchecked((long)0xBADC0FFEE0DDF00DUL);

    /// <summary>The checked words of every block, computed once from the oracle's pattern.</summary>
    public static long[] Fingerprints(int mebibytes)
    {
        var words = new long[mebibytes * Samples];
        var buffer = new byte[Block];
        for (var index = 0; index < mebibytes; index++)
        {
            new Random(11091 ^ index).NextBytes(buffer);
            BinaryPrimitives.WriteInt64LittleEndian(buffer, (long)index * Block);
            for (var s = 0; s < Samples; s++)
                words[index * Samples + s] = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(s * Stride));
        }
        return words;
    }

    public static Result Run(string path, long[] fingerprints, int depth, TimeSpan duration)
    {
        if (depth is < 1 or > 64 || fingerprints.Length == 0 || fingerprints.Length % Samples != 0)
            throw new ArgumentOutOfRangeException(nameof(depth));
        var mebibytes = fingerprints.Length / Samples;
        using var handle = CreateFileW(path, 0x80000000, 7, IntPtr.Zero, 3, 0x20000000 | 0x40000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var memory = VirtualAlloc(IntPtr.Zero, (nuint)depth * Block, 0x3000, 4);
        if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var overlapped = new IntPtr[depth]; var events = new IntPtr[depth];
        var block = new int[depth]; var active = new bool[depth];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long blocks = 0; var next = 0;
        try
        {
            for (var i = 0; i < depth; i++)
            {
                overlapped[i] = Marshal.AllocHGlobal(32);
                events[i] = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
                if (events[i] == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            void Issue(int slot)
            {
                var index = next; next = (next + 1) % mebibytes;
                var buffer = memory + slot * Block;
                for (var s = 0; s < Samples; s++) Marshal.WriteInt64(buffer, s * Stride, Poison);
                var offset = (long)index * Block;
                var ov = overlapped[slot];
                Marshal.WriteInt64(ov, 0, 0); Marshal.WriteInt64(ov, 8, 0);
                Marshal.WriteInt32(ov, 16, (int)(offset & 0xFFFFFFFF)); Marshal.WriteInt32(ov, 20, (int)(offset >> 32));
                Marshal.WriteIntPtr(ov, 24, events[slot]);
                if (!ResetEvent(events[slot])) throw new Win32Exception(Marshal.GetLastWin32Error());
                block[slot] = index;
                if (!ReadFile(handle, buffer, Block, IntPtr.Zero, ov) && Marshal.GetLastWin32Error() is var error && error != 997)
                    throw new Win32Exception(error);
                active[slot] = true;
            }
            for (var i = 0; i < depth; i++) Issue(i);
            while (active.Any(a => a))
            {
                var waiting = Enumerable.Range(0, depth).Where(i => active[i]).ToArray();
                var signalled = WaitForMultipleObjects((uint)waiting.Length, waiting.Select(i => events[i]).ToArray(), false, 30_000);
                if (signalled >= waiting.Length) throw new IOException("Overlapped read did not complete within 30 s.");
                var slot = waiting[signalled];
                active[slot] = false;
                if (!GetOverlappedResult(handle, overlapped[slot], out var count, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (count != Block) throw new IOException($"Short overlapped read at MiB {block[slot]}: {count} bytes.");
                var buffer = memory + slot * Block;
                for (var s = 0; s < Samples; s++)
                    if (Marshal.ReadInt64(buffer, s * Stride) != fingerprints[block[slot] * Samples + s])
                        throw new InvalidDataException($"Overlapped read returned wrong bytes at MiB {block[slot]}, offset {s * Stride}.");
                blocks++;
                if (clock.Elapsed < duration) Issue(slot);
            }
            return new(blocks, depth, clock.Elapsed.TotalSeconds);
        }
        finally
        {
            // Never free a buffer or OVERLAPPED that a read still owns.
            if (active.Any(a => a))
            {
                CancelIoEx(handle, IntPtr.Zero);
                for (var i = 0; i < depth; i++) if (active[i]) GetOverlappedResult(handle, overlapped[i], out _, true);
            }
            foreach (var e in events) if (e != IntPtr.Zero) CloseHandle(e);
            foreach (var o in overlapped) if (o != IntPtr.Zero) Marshal.FreeHGlobal(o);
            VirtualFree(memory, 0, 0x8000);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(SafeFileHandle handle, IntPtr data, uint length, IntPtr count, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr overlapped, out uint count, [MarshalAs(UnmanagedType.Bool)] bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateEventW(IntPtr security, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, IntPtr name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ResetEvent(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, nuint bytes, uint allocation, uint protection);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(IntPtr address, nuint size, uint type);
}
