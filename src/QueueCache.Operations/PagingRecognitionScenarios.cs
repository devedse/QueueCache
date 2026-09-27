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
/// traffic. Any miss fails the check. It also checks that program files read back correctly through the cache
/// after that pressure (see <see cref="VerifyImages"/>); the only cache change is dropping clean data first.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PagingRecognitionScenarios
{
    private const long MiB = 1 << 20;

    internal static IReadOnlyList<CheckResult> Evaluate(CachePagingAdmission before, CachePagingAdmission after,
        CachePagingIo ioBefore, CachePagingIo ioAfter, string pressure, ulong? bypassBefore = null, ulong? bypassAfter = null)
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
        // V12: every recognised paging-file request must have gone straight to the disk from dispatch.
        // Older drivers do not report bypasses; the check is then omitted, not claimed.
        CheckResult? bypass = null;
        if (bypassBefore is null || bypassAfter is null)
        {
        }
        else if (bypassAfter.Value - bypassBefore.Value != recognised)
            throw new IOException(FormattableString.Invariant(
                $"{bypassAfter.Value - bypassBefore.Value} paging-file requests bypassed the worker, expected {recognised}. ") + evidence);
        else
            bypass = new("system-paging/paging-file-io-bypasses-worker", recognised > 0 ? "PASS" : "SKIP",
                FormattableString.Invariant($"All {recognised} recognised paging-file requests were forwarded straight to the disk."));
        CheckResult[] checks =
        [
            new("system-paging/no-unrecognised-paging-file-requests", "PASS",
                evidence + " No request inside a paging-file extent was classified as application traffic."),
            new("system-paging/paging-file-requests-recognised", recognised > 0 ? "PASS" : "SKIP",
                evidence + (recognised > 0 ? " Paging-file I/O was observed and recognised."
                    : " No paging-file I/O occurred, so recognition itself was not exercised."))
        ];
        return bypass is null ? checks : [bypass, .. checks];
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
            // Program-file oracle: with clean cached data dropped, unbuffered reads of these unmodified files come
            // from the disk. After the pressure, and after new processes loaded them, every file must still hash
            // the same through the normal (cached, paging) read path.
            var images = Directory.GetFiles(AppContext.BaseDirectory, "*.dll");
            device.Control(WriteCacheAction.DropClean);
            var reference = images.ToDictionary(path => path, UnbufferedHash);
            var diagnosticsBefore = device.GetDiagnostics();
            var pressure = ApplyBoundedPressure();
            var launches = LaunchSelf(10);
            var diagnosticsAfter = device.GetDiagnostics();
            var mismatched = images.Where(path => !BufferedHash(path).AsSpan().SequenceEqual(reference[path]))
                .Select(path => Path.GetFileName(path)).ToArray();
            var checks = Evaluate(diagnosticsBefore.PagingAdmission!, diagnosticsAfter.PagingAdmission!,
                diagnosticsBefore.PagingIo!, diagnosticsAfter.PagingIo!,
                $"Reference: {map.Ranges.Count} range(s) of {string.Join(", ", map.Files)}. {pressure}",
                diagnosticsBefore.PagingFileBypasses, diagnosticsAfter.PagingFileBypasses);
            return [.. checks, VerifyImages(images.Length, images.Sum(path => new FileInfo(path).Length), mismatched, launches,
                diagnosticsBefore.PagingReadsRepeatedPages is null || diagnosticsAfter.PagingReadsRepeatedPages is null
                    ? null : diagnosticsAfter.PagingReadsRepeatedPages - diagnosticsBefore.PagingReadsRepeatedPages)];
        }
        finally
        {
            device.SetSpecialRanges([], SpecialRangeKind.Reference);
        }
    }

    /// <summary>0.4.148.1-0.4.162.1 kept paging read misses; a clustered page-in's buffer can repeat Windows'
    /// shared dummy page, so program pages were cached with another block's data and new processes crashed.
    /// Every file must hash as it did on disk and every started process must exit normally.</summary>
    internal static CheckResult VerifyImages(int files, long bytes, IReadOnlyList<string> mismatched,
        IReadOnlyList<int> exitCodes, ulong? repeatedPageReads)
    {
        const string label = "system-paging/program-files-match-disk";
        var failedLaunches = exitCodes.Count(code => code != 0);
        var evidence = FormattableString.Invariant(
            $"{files} program files ({bytes / MiB} MiB) hashed from disk before the pressure; {exitCodes.Count} processes started afterwards") +
            (repeatedPageReads is { } repeated
                ? FormattableString.Invariant($"; paging reads whose buffer repeated a page: {repeated}") : "");
        if (mismatched.Count != 0 || failedLaunches != 0)
            throw new IOException(FormattableString.Invariant(
                $"{label}: {mismatched.Count} program files read differently through the cache ({string.Join(", ", mismatched.Take(8))}) and {failedLaunches} processes failed. ") + evidence);
        return new(label, "PASS", evidence + ". Every file matched and every process exited normally.");
    }

    private static byte[] UnbufferedHash(string path)
    {
        using var file = new AlignedFile(path, (int)MiB, create: false, sharedReadOnly: true);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[MiB];
        var length = new FileInfo(path).Length;
        for (long offset = 0; offset < length; offset += MiB)
        {
            var read = file.ReadUpTo(offset, buffer);
            hash.AppendData(buffer, 0, (int)Math.Min(read, length - offset));
        }
        return hash.GetHashAndReset();
    }

    private static byte[] BufferedHash(string path)
    {
        using var stream = File.OpenRead(path);
        return System.Security.Cryptography.SHA256.HashData(stream);
    }

    // Short-lived copies of this program load its runtime and libraries through paging reads.
    private static IReadOnlyList<int> LaunchSelf(int count)
    {
        var codes = new List<int>();
        for (var index = 0; index < count; index++)
        {
            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(60_000))
            {
                process.Kill();
                codes.Add(-1);
                continue;
            }
            codes.Add(process.ExitCode);
        }
        return codes;
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
