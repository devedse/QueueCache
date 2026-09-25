using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using QueueCache.Management;

namespace QueueCache.Operations;

/// <summary>
/// T085 recognition cross-check on the guarded system disk. It never enables caching or writes files. It sends
/// the current paging-file extents as an observe-only reference set, then applies bounded memory pressure so
/// Windows pages to and from the pagefile. The driver counts paging requests it recognised as paging-file I/O, and
/// "reference misses": requests inside a paging-file extent that recognition would have admitted as application
/// traffic. Any miss fails the check.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PagingRecognitionScenarios
{
    private const long MiB = 1 << 20;

    internal static IReadOnlyList<CheckResult> Evaluate(CachePagingAdmission before, CachePagingAdmission after,
        CachePagingIo ioBefore, CachePagingIo ioAfter, string pressure)
    {
        if (after.ReferenceMisses < before.ReferenceMisses || after.PagingFileRequests < before.PagingFileRequests)
            throw new IOException("Paging recognition counters regressed.");
        var misses = after.ReferenceMisses - before.ReferenceMisses;
        var recognised = after.PagingFileRequests - before.PagingFileRequests;
        var noFile = after.NoFileObject - before.NoFileObject;
        var highIrql = after.HighIrql - before.HighIrql;
        var reads = ioAfter.ReadRequests - ioBefore.ReadRequests;
        var writes = ioAfter.WriteRequests - ioBefore.WriteRequests;
        var evidence = FormattableString.Invariant(
            $"{pressure} Paging requests read/write {reads}/{writes}; recognised as paging-file I/O {recognised}; ") +
            FormattableString.Invariant($"without file object {noFile}; above APC_LEVEL {highIrql}; reference misses {misses}.");
        if (misses != 0)
            throw new IOException("Paging-file requests were not recognised and would have been cached. " + evidence);
        return
        [
            new("system-paging/no-unrecognised-paging-file-requests", "PASS",
                evidence + " No request inside a paging-file extent was classified as application traffic."),
            new("system-paging/paging-file-requests-recognised", recognised > 0 ? "PASS" : "SKIP",
                evidence + (recognised > 0 ? " Paging-file I/O was observed and recognised."
                    : " No paging-file I/O occurred, so recognition itself was not exercised."))
        ];
    }

    public static IReadOnlyList<CheckResult> Run(DiskTarget target, CacheDevice device)
    {
        target.ValidateCurrent();
        if (device.GetDiagnostics().PagingAdmission is null)
            throw new NotSupportedException("Paging recognition requires Diagnostics V10.");
        var map = SpecialFileMap.PagingFiles(target);
        if (map.Files.Count == 0)
            return [new("system-paging/no-unrecognised-paging-file-requests", "SKIP", "No paging file exists on this disk.")];
        device.SetSpecialRanges(map.Ranges, SpecialRangeKind.Reference);
        try
        {
            var diagnosticsBefore = device.GetDiagnostics();
            var pressure = ApplyBoundedPressure();
            var diagnosticsAfter = device.GetDiagnostics();
            return Evaluate(diagnosticsBefore.PagingAdmission!, diagnosticsAfter.PagingAdmission!,
                diagnosticsBefore.PagingIo!, diagnosticsAfter.PagingIo!,
                $"Reference: {map.Ranges.Count} range(s) of {string.Join(", ", map.Files)}. {pressure}");
        }
        finally
        {
            device.SetSpecialRanges([], SpecialRangeKind.Reference);
        }
    }

    // Commit private memory until available physical memory is nearly exhausted (bounded by the commit headroom
    // left for the rest of the system), then touch it all again so trimmed pages are read back from the pagefile.
    private static string ApplyBoundedPressure()
    {
        var status = MemoryStatus();
        var target = (long)status.AvailPhys + 256 * MiB;
        var commitHeadroom = (long)status.AvailPageFile - 1024 * MiB;
        target = Math.Min(target, commitHeadroom);
        if (target < 256 * MiB)
            return "Insufficient commit headroom for bounded pressure; observation only.";
        const long chunk = 64 * MiB;
        var chunks = new List<IntPtr>();
        var timer = Stopwatch.StartNew();
        try
        {
            for (long committed = 0; committed < target && timer.Elapsed < TimeSpan.FromSeconds(60); committed += chunk)
            {
                var memory = VirtualAlloc(IntPtr.Zero, (nuint)chunk, 0x3000 /* MEM_COMMIT | MEM_RESERVE */, 4 /* PAGE_READWRITE */);
                if (memory == IntPtr.Zero)
                    break;
                chunks.Add(memory);
                Touch(memory, chunk, (byte)chunks.Count);
            }
            // Second pass: pages trimmed to the pagefile are read back.
            foreach (var memory in chunks)
                Touch(memory, chunk, 0x5A);
            return FormattableString.Invariant(
                $"Pressure committed {chunks.Count * chunk / MiB} MiB (available was {status.AvailPhys / (ulong)MiB} MiB) in {timer.Elapsed.TotalSeconds:F0} s.");
        }
        finally
        {
            foreach (var memory in chunks)
                VirtualFree(memory, 0, 0x8000 /* MEM_RELEASE */);
        }
    }

    private static void Touch(IntPtr memory, long bytes, byte value)
    {
        for (long offset = 0; offset < bytes; offset += 4096)
            Marshal.WriteByte(memory, (int)offset, value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    private static MemoryStatusEx MemoryStatus()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return status;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, nuint size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(IntPtr address, nuint size, uint type);
}
