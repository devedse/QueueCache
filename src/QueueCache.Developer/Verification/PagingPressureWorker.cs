using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

public sealed record PagingPressureResult(ulong Role, long? RequestedBytes, long? AllocatedBytes,
    long Operations, double? LongestGapSeconds, double Seconds);

/// <summary>Bounded fixtures in owned qcache workers. Native allocations always leave with their process.</summary>
[SupportedOSPlatform("windows")]
internal static class PagingPressureWorker
{
    private const long MiB = 1L << 20, Chunk = 64 * MiB;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAlloc(nint address, nuint bytes, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFree(nint address, nuint bytes, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualLock(nint address, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessWorkingSetSizeEx(nint process, nuint minimum, nuint maximum, uint flags);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Size, Load;
        public ulong Total, Available, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, Extended;
    }
    private static void Start(WorkerJob job)
    {
        RunStorage.AtomicJson(job.ReadyFile!, new { Ready = true, Role = job.Value });
        var wait = Stopwatch.StartNew();
        while (!File.Exists(job.StartFile))
        {
            if (File.Exists(job.StopFile) || wait.Elapsed.TotalSeconds > 90)
                throw new IOException("Pressure start handshake was cancelled or timed out.");
            Thread.Sleep(10);
        }
    }
    public static PagingPressureResult Run(WorkerJob job)
    {
        if (job.ReadyFile is null || job.StartFile is null || job.StopFile is null || job.Seconds != 120 || job.Value > 3)
            throw new IOException("Invalid guarded paging fixture contract.");
        if (job.Value == 2)
        {
            Start(job); var clock = Stopwatch.StartNew(); double last = 0, longest = 0; long samples = 0;
            while (clock.Elapsed.TotalSeconds < job.Seconds && !File.Exists(job.StopFile))
            {
                Thread.Sleep(100); var now = clock.Elapsed.TotalSeconds;
                longest = Math.Max(longest, now - last); last = now; ++samples;
            }
            return new(2, null, null, samples, longest, clock.Elapsed.TotalSeconds);
        }
        if (job.Value == 3)
        {
            if (job.WorkDirectory is null) throw new IOException("No owned writer directory.");
            var files = Enumerable.Range(0, SystemPagingOrdering.Files).Select(i => new FileStream(
                SystemPagingOrdering.FileName(job.WorkDirectory, i), FileMode.Open, FileAccess.Write, FileShare.ReadWrite)).ToArray();
            try
            {
                var patterns = Enumerable.Range(0, files.Length).Select(SystemPagingOrdering.Pattern).ToArray();
                Start(job); var clock = Stopwatch.StartNew(); long writes = 0;
                while (clock.Elapsed.TotalSeconds < job.Seconds && !File.Exists(job.StopFile))
                {
                    var i = (int)(writes % files.Length); files[i].Position = 0;
                    files[i].Write(patterns[i]); files[i].Flush(true); ++writes;
                }
                return new(3, null, null, writes, null, clock.Elapsed.TotalSeconds);
            }
            finally { foreach (var file in files) file.Dispose(); }
        }
        var status = new MemoryStatus { Size = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) throw new Win32Exception();
        var wanted = job.Value == 0 ? Math.Max(0, (long)status.Available - (1L << 30)) / Chunk * Chunk : 4L << 30;
        if (wanted < 1L << 30) throw new IOException("Insufficient RAM for the declared pressure fixture.");
        var blocks = new List<nint>();
        try
        {
            if (job.Value == 0 && !SetProcessWorkingSetSizeEx(GetCurrentProcess(),
                (nuint)(wanted + 256 * MiB), (nuint)(wanted + 512 * MiB), 0)) throw new Win32Exception();
            while (blocks.Count * Chunk < wanted)
            {
                var memory = VirtualAlloc(0, (nuint)Chunk, 0x3000, 4);
                if (memory == 0) throw new Win32Exception();
                blocks.Add(memory);
                if (job.Value == 0 && !VirtualLock(memory, (nuint)Chunk)) throw new Win32Exception();
                Touch(memory);
            }
            Start(job); var clock = Stopwatch.StartNew(); long operations = 0;
            while (clock.Elapsed.TotalSeconds < job.Seconds && !File.Exists(job.StopFile))
            {
                if (job.Value == 0) Thread.Sleep(100);
                else foreach (var block in blocks)
                {
                    Touch(block); ++operations;
                    if (clock.Elapsed.TotalSeconds >= job.Seconds || File.Exists(job.StopFile)) break;
                }
            }
            return new(job.Value, wanted, blocks.Count * Chunk, operations, null, clock.Elapsed.TotalSeconds);
        }
        finally { foreach (var block in blocks) VirtualFree(block, 0, 0x8000); }
    }
    private static void Touch(nint memory)
    {
        for (var offset = 0; offset < Chunk; offset += 4096) Marshal.WriteByte(memory, offset, 0x5A);
    }
}
