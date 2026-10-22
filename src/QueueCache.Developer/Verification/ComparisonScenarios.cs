using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using QueueCache.Management;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

public sealed record ComparisonTarget(char Letter, string Root, string? VolumeName, string FileSystem, string Label,
    long ClusterBytes, long TotalBytes, long FreeBytes, string DriveType, int? DiskNumber);

public sealed record MemorySnapshot(DateTimeOffset Utc, long TotalPhysicalBytes, long AvailablePhysicalBytes,
    long CommitBytes, long CommitLimitBytes, long KernelNonpagedBytes, long KernelPagedBytes, long SystemCacheBytes);

public sealed record SettleResult(double Seconds, long? DiskBytesWritten, bool IdleObserved, string Source, long[]? DiskBytesPerSecond = null);
public sealed record TreeCacheSnapshot(WriteCacheState State, CachePerformance Performance, CacheDiagnostics Diagnostics);

public sealed record SequentialPass(long Bytes, double Seconds, double MBPerSecond, double? FlushSeconds, long[]? BytesPerSecond,
    DateTimeOffset? Started = null, DateTimeOffset? Ended = null);

public sealed record TreeResult(int Files, long Bytes, double CreateSeconds, double CopySeconds, double ReadSeconds, double DeleteSeconds,
    SettleResult? AfterCreate, SettleResult? AfterCopy, bool HashVerified,
    MemorySnapshot? StartMemory, MemorySnapshot? AfterCreateWorkMemory, MemorySnapshot? AfterCreateMemory,
    MemorySnapshot? AfterCopyWorkMemory, MemorySnapshot? AfterCopyMemory,
    MemorySnapshot? BeforeReadMemory, MemorySnapshot? AfterReadMemory,
    TreeCacheSnapshot? BeforeCreateCache, TreeCacheSnapshot? AfterCreateCache, TreeCacheSnapshot? AfterCreateSettleCache,
    TreeCacheSnapshot? BeforeCopyCache, TreeCacheSnapshot? AfterCopyCache, TreeCacheSnapshot? AfterCopySettleCache,
    TreeCacheSnapshot? BeforeReadCache, TreeCacheSnapshot? AfterReadCache,
    TreeCacheSnapshot? BeforeDeleteCache, TreeCacheSnapshot? AfterDeleteCache);

public sealed record DurabilityOracle(int SchemaVersion, ulong Nonce, string Directory, int SetMiB, int SmallFiles,
    DateTimeOffset Generation1Utc, DateTimeOffset CompletedUtc, double FlushedSetFlushSeconds, string Product);

public sealed record SectorCounts(long Expected, long New, long Old, long Other, long Missing);

public sealed record DurabilityResult(DateTimeOffset BootUtc, SectorCounts Flushed, SectorCounts Unflushed,
    int SmallFilesExpected, int SmallFilesIntact, int SmallFilesDamaged, int SmallFilesMissing, int ChkdskExitCode);

/// <summary>Churn power-cut oracle (schema 2): when each churn file was created and when files were deleted, in
/// milliseconds after ChurnStartUtc (-1: not deleted). Contents follow from the nonce (ComparisonData.ChurnFile).</summary>
public sealed record ChurnOracle(int SchemaVersion, ulong Nonce, string Directory, int BaseFiles, DateTimeOffset BaseSettledUtc,
    DateTimeOffset ChurnStartUtc, DateTimeOffset CompletedUtc, int[] CreatedMs, int[] BaseDeletedMs, int[] ChurnDeletedMs, string Product);

/// <summary>Files of one group after the reset. For deleted files, Intact means the deletion was lost (the file came back).</summary>
public sealed record ChurnBucket(string Group, int Expected, int Intact, int Damaged, int Missing, IReadOnlyDictionary<string, int> DamagedAs);

public sealed record ChurnResult(DateTimeOffset BootUtc, IReadOnlyList<ChurnBucket> Groups, int ChkdskExitCode, IReadOnlyList<string> Unreadable);

/// <summary>Runs inside the comparison worker; compare-drain adds explicit provider controls (see ComparisonPlan).</summary>
[SupportedOSPlatform("windows")]
internal static class ComparisonScenarios
{
    private const int Block = 1 << 20;
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    public static ComparisonTarget Inspect(string volume, string outputDirectory, string suite)
    {
        if (volume.Length != 2 || volume[1] != ':' || !char.IsAsciiLetter(volume[0]))
            throw new ArgumentException("Comparisons take a drive letter such as T:.");
        var letter = char.ToUpperInvariant(volume[0]);
        var root = letter + @":\";
        var system = char.ToUpperInvariant(Environment.GetFolderPath(Environment.SpecialFolder.Windows)[0]);
        if (letter == system) throw new IOException("Comparisons never run on the Windows volume.");
        if (char.ToUpperInvariant(Path.GetFullPath(outputDirectory)[0]) == letter)
            throw new IOException("Comparison evidence must be written to another volume than the one measured.");
        var drive = new DriveInfo(root);
        if (!drive.IsReady) throw new IOException($"{root} is not ready.");
        if (!GetDiskFreeSpaceW(root, out var sectorsPerCluster, out var bytesPerSector, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var name = new char[64];
        var volumeName = GetVolumeNameForVolumeMountPointW(root, name, (uint)name.Length) ? new string(name).TrimEnd('\0') : null;
        var target = new ComparisonTarget(letter, root, volumeName, drive.DriveFormat, drive.VolumeLabel,
            (long)sectorsPerCluster * bytesPerSector, drive.TotalSize, drive.AvailableFreeSpace, drive.DriveType.ToString(), DiskNumber(letter));
        if (suite == "compare-ram" && target.FreeBytes < ComparisonPlan.RamMinimumFreeBytes)
            throw new IOException($"compare-ram needs {ComparisonPlan.RamMinimumFreeBytes >> 20} MiB free on {root} (4 GiB RAM disk); it has {target.FreeBytes >> 20} MiB.");
        return target;
    }

    public static async Task<IReadOnlyList<CheckResult>> RunAsync(WorkerJob job)
    {
        if (Process.GetCurrentProcess().PriorityClass != ProcessPriorityClass.Normal)
            throw new IOException("Comparisons require a normal-priority process; use task priority 4 when launching through Task Scheduler.");
        var evidenceDirectory = Path.GetDirectoryName(job.Reply)!;
        var target = Inspect(job.Volume, evidenceDirectory, job.Operation);
        var evidence = Path.Combine(evidenceDirectory, "compare");
        var product = job.Product ?? throw new ArgumentException("Comparisons require --product.");
        var queueCache = LoadedDriverInspection.Capture();
        if (!queueCache.Available) throw new IOException("Loaded drivers could not be observed: " + queueCache.Error);
        var isQueueCache = job.Provider is { } provider ? ComparisonDrain.Product(provider) == ComparisonProvider.QueueCache :
            product.StartsWith("QueueCache", StringComparison.OrdinalIgnoreCase);
        if (isQueueCache != queueCache.Modules.Count > 0)
            throw new IOException(isQueueCache ? "The product is QueueCache, but no QueueCache driver is loaded."
                : "A QueueCache driver is loaded; measure other products on a VM state without QueueCache.");
        RunStorage.AtomicJson(evidence + ".environment.json", CaptureEnvironment(target, product, queueCache));
        var plan = ComparisonPlan.CasesFor(job.Operation, job.ReferenceRepeats);
        RunStorage.AtomicJson(evidence + ".plan.json", plan);
        if (ComparisonDrain.IsSuite(job.Operation))
            return await ComparisonDrainScenarios.RunAsync(job, target, plan);
        var work = job.WorkDirectory;
        if (work is not null) Directory.CreateDirectory(work);
        var checks = new List<CheckResult>();
        foreach (var step in plan)
        {
            Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] Comparison step {step.Id} ({checks.Count + 1} of {plan.Count})"); Console.Out.Flush();
            var prefix = evidence + "." + step.Id;
            var started = DateTimeOffset.UtcNow;
            var detail = await Step(job, target, step, work, prefix);
            RunStorage.AtomicJson(prefix + ".interval.json", new { Start = started, End = DateTimeOffset.UtcNow });
            checks.Add(new(step.Id, "PASS", detail));
        }
        return checks;
    }

    private static async Task<string> Step(WorkerJob job, ComparisonTarget target, ComparisonCase step, string? work, string prefix)
    {
        string Path_(string file) => Path.Combine(work ?? throw new InvalidOperationException("Missing comparison folder."), file);
        switch (step.Kind)
        {
            case "memory":
            {
                var snapshot = Memory();
                RunStorage.AtomicJson(prefix + ".memory.json", snapshot);
                return $"Available {snapshot.AvailablePhysicalBytes >> 20} MiB, commit {snapshot.CommitBytes >> 20} MiB, nonpaged {snapshot.KernelNonpagedBytes >> 20} MiB.";
            }
            case "canary":
            {
                var write = MemoryCopyProbe.Run("runtime", true, 1, 3);
                var read = MemoryCopyProbe.Run("runtime", false, 1, 3);
                RunStorage.AtomicJson(prefix + ".memory-copy.json", new { Write = write, Read = read });
                return $"Memory copy canary: into cold memory {write.MiBPerSecond:F0} MiB/s, out of cold memory {read.MiBPerSecond:F0} MiB/s.";
            }
            case "prepare":
            {
                var hash = RamReadFileOracle.Prepare(Path_(step.File), (int)(step.Bytes >> 20));
                RunStorage.AtomicJson(prefix + ".oracle.json", new { File = step.File, Sha256 = hash, step.Bytes });
                return $"Created {step.File} ({step.Bytes >> 20} MiB) with its byte oracle.";
            }
            case "prepare-cache":
                return PrepareCache(target, work!, prefix, onlyCheck: step.Bytes == 1);
            case "diskspd":
            {
                var bytes = ComparisonPlan.FileBytes(job.Operation, step.File);
                var file = Path_(step.File);
                if (new FileInfo(file).Length < bytes) throw new IOException($"{step.File} is smaller than {bytes} bytes.");
                var result = await OwnedProcess.RunAsync(job.DiskSpd!, [.. ComparisonPlan.Arguments(step, bytes), file], prefix,
                    TimeSpan.FromSeconds(step.Seconds + ComparisonPlan.WarmupSeconds + 120), CancellationToken.None);
                if (result.ExitCode != 0) throw new IOException($"DiskSpd exit {result.ExitCode}; inspect {prefix}.stderr.txt.");
                if (result.Error.Trim().Length != 0) throw new InvalidDataException($"Unexpected DiskSpd stderr; inspect {prefix}.stderr.txt.");
                var score = ComparisonPlan.Score(step, result.Output, bytes);
                RunStorage.AtomicJson(prefix + ".score.json", score);
                return $"{score.MBPerSecond:F1} MB/s, {score.Iops:F0} IOPS, CPU {score.CpuPercent:F1}%.";
            }
            case "warm":
            {
                var pass = Sequential(Path_(step.File), new FileInfo(Path_(step.File)).Length, write: false, queue: 8);
                RunStorage.AtomicJson(prefix + ".pass.json", pass);
                return $"Read {step.File} once: {pass.MBPerSecond:F1} MB/s in {pass.Seconds:F2} s.";
            }
            case "settle":
            {
                var settle = Settle(target.DiskNumber, step.SettleSeconds);
                RunStorage.AtomicJson(prefix + ".settle.json", settle);
                return Describe(settle);
            }
            case "fill":
            {
                var pass = Sequential(Path_(step.File), step.Bytes, write: true, queue: 1, create: true);
                RunStorage.AtomicJson(prefix + ".pass.json", pass);
                return $"Wrote {step.Bytes >> 20} MiB: {pass.MBPerSecond:F1} MB/s.";
            }
            case "delete":
                File.Delete(Path_(step.File));
                await Task.Delay(TimeSpan.FromSeconds(5));
                return $"Deleted {step.File}; waited 5 s for the volume to release it.";
            case "absorb" or "sustained" or "flush":
            {
                var pass = Sequential(Path_(step.File), step.Bytes, write: true, queue: 8, flush: step.Kind == "flush",
                    perSecond: step.Kind == "sustained");
                var settle = Settle(target.DiskNumber, step.SettleSeconds);
                RunStorage.AtomicJson(prefix + ".pass.json", new { Pass = pass, Settle = settle });
                return $"Wrote {step.Bytes >> 20} MiB at {pass.MBPerSecond:F1} MB/s" +
                    (pass.FlushSeconds is { } flushed ? $", then the flush took {flushed:F3} s" : "") + ". " + Describe(settle);
            }
            case "tree":
            {
                var tree = Tree(Path_(step.File), target.DiskNumber, step.SettleSeconds);
                RunStorage.AtomicJson(prefix + ".tree.json", tree);
                return $"{tree.Files} files, {tree.Bytes >> 20} MiB: create {tree.CreateSeconds:F2} s, copy (4 threads) {tree.CopySeconds:F2} s, " +
                    $"read and hash {tree.ReadSeconds:F2} s, delete {tree.DeleteSeconds:F2} s.";
            }
            case "integrity":
            {
                var file = Path_(step.File);
                var written = RamReadFileOracle.Rewrite(file, (int)(step.Bytes >> 20));
                var read = RamReadFileOracle.Verify(file, (int)(step.Bytes >> 20), prefix);
                RunStorage.AtomicJson(prefix + ".oracle.json", new { Written = written, Read = read });
                if (written != read) throw new InvalidDataException("The RAM disk returned different bytes than were written.");
                return $"Rewrote and read back {step.Bytes >> 20} MiB; bytes identical.";
            }
            case "durability-write":
                return DurabilityWrite(target, work!, job.OraclePath ?? throw new ArgumentException("Missing durability oracle path."), job.Product!, prefix);
            case "durability-churn":
                return DurabilityChurn(target, work!, job.OraclePath ?? throw new ArgumentException("Missing durability oracle path."), job.Product!, prefix);
            case "durability-verify":
                return await DurabilityVerify(target, job.OraclePath ?? throw new ArgumentException("compare-durability-verify requires --oracle."), prefix);
            default:
                throw new ArgumentException("Unknown comparison step kind: " + step.Kind);
        }
    }

    /// <summary>Flushes every file written on a volume (used before the durability reset).</summary>
    public static void FlushVolume(char letter)
    {
        using var volume = CreateFileW($@"\\.\{char.ToUpperInvariant(letter)}:", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (volume.IsInvalid || !FlushFileBuffers(volume)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not flush the evidence volume.");
    }

    private static string Describe(SettleResult settle) => settle.DiskBytesWritten is { } written
        ? $"Settled {settle.Seconds:F0} s ({(settle.IdleObserved ? "disk idle" : "time limit")}); {written >> 20} MiB written to the disk meanwhile."
        : $"Settled {settle.Seconds:F0} s (fixed; no disk counters for this volume).";

    private static object CaptureEnvironment(ComparisonTarget target, string product, LoadedDriverObservation queueCache)
    {
        string? Value(string key, string name) { using var k = Registry.LocalMachine.OpenSubKey(key); return k?.GetValue(name)?.ToString(); }
        string[]? Multi(string key, string name) { using var k = Registry.LocalMachine.OpenSubKey(key); return k?.GetValue(name) as string[]; }
        const string Volume = @"SYSTEM\CurrentControlSet\Control\Class\{71A27CDD-812A-11D0-BEC7-08002BE2092F}";
        const string Disk = @"SYSTEM\CurrentControlSet\Control\Class\{4D36E967-E325-11CE-BFC1-08002BE10318}";
        const string Version = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        object? cacheState = null; string? cacheError = null;
        if (queueCache.Modules.Count > 0)
            try { using var cache = new CacheDevice($"{target.Letter}:", writable: false); cacheState = cache.GetWriteCacheState(); }
            catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException) { cacheError = ex.Message; }
        return new
        {
            Product = product, Target = target, Captured = DateTimeOffset.UtcNow, Machine = System.Environment.MachineName,
            Windows = new { Build = Value(Version, "CurrentBuild"), Revision = Value(Version, "UBR"), Release = Value(Version, "DisplayVersion") },
            SecureBoot = Value(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled"),
            MemoryIntegrityConfigured = Value(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled"),
            VolumeUpperFilters = Multi(Volume, "UpperFilters"), DiskUpperFilters = Multi(Disk, "UpperFilters"), DiskLowerFilters = Multi(Disk, "LowerFilters"),
            QueueCacheDrivers = queueCache.Modules, QueueCacheState = cacheState, QueueCacheStateError = cacheError,
            ThirdPartyDrivers = LoadedDriverInspection.CaptureThirdParty(),
            Memory = Memory(),
            Note = "Neutral evidence for an external comparison. Backing-disk bytes and flushes are taken from the hypervisor, joined by the step intervals."
        };
    }

    public static unsafe MemorySnapshot Memory()
    {
        var status = new MemoryStatus { Length = (uint)sizeof(MemoryStatus) };
        if (!GlobalMemoryStatusEx(ref status)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var info = new PerformanceInformation { Size = (uint)sizeof(PerformanceInformation) };
        if (!GetPerformanceInfo(ref info, info.Size)) throw new Win32Exception(Marshal.GetLastWin32Error());
        long Pages(nuint value) => checked((long)value * (long)info.PageSize);
        return new(DateTimeOffset.UtcNow, (long)status.TotalPhys, (long)status.AvailPhys, Pages(info.CommitTotal), Pages(info.CommitLimit),
            Pages(info.KernelNonpaged), Pages(info.KernelPaged), Pages(info.SystemCache));
    }

    /// <summary>Waits at least <paramref name="minimum"/> seconds for a write-back product to reach the disk, then until the
    /// disk below the volume has written under 1 MB for 10 consecutive seconds (at most 300 s). Products that sit below the
    /// counters are covered by the minimum and the hypervisor's counters.</summary>
    public static SettleResult Settle(int? diskNumber, int minimum)
    {
        var timer = Stopwatch.StartNew();
        if (diskNumber is not { } number)
        {
            Thread.Sleep(TimeSpan.FromSeconds(minimum));
            return new(timer.Elapsed.TotalSeconds, null, false, "fixed");
        }
        var start = DiskBytesWritten(number);
        var previous = start; var quiet = 0;
        var series = new List<long>();
        while (timer.Elapsed.TotalSeconds < 300)
        {
            Thread.Sleep(1000);
            var now = DiskBytesWritten(number);
            series.Add(now - previous);
            quiet = now - previous < 1_000_000 ? quiet + 1 : 0;
            previous = now;
            if (timer.Elapsed.TotalSeconds >= minimum && quiet >= 10)
                return new(timer.Elapsed.TotalSeconds, now - start, true, "IOCTL_DISK_PERFORMANCE", series.ToArray());
        }
        return new(timer.Elapsed.TotalSeconds, previous - start, false, "IOCTL_DISK_PERFORMANCE", series.ToArray());
    }

    private static unsafe int? DiskNumber(char letter)
    {
        using var volume = CreateFileW($@"\\.\{letter}:", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (volume.IsInvalid) return null;
        var number = new byte[12];
        fixed (byte* buffer = number)
            if (!DeviceIoControl(volume, 0x2D1080, null, 0, buffer, 12, out _, IntPtr.Zero)) return null; // IOCTL_STORAGE_GET_DEVICE_NUMBER
        return BinaryPrimitives.ReadUInt32LittleEndian(number) == 7 ? BinaryPrimitives.ReadInt32LittleEndian(number.AsSpan(4)) : null; // FILE_DEVICE_DISK
    }

    private static long DiskBytesWritten(int number) => DiskCounters(number).BytesWritten;

    public static unsafe ComparisonDiskCounters DiskCounters(int number)
    {
        using var disk = CreateFileW($@"\\.\PhysicalDrive{number}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (disk.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "The disk below the volume could not be opened for its counters.");
        var performance = new byte[88];
        fixed (byte* buffer = performance)
            if (!DeviceIoControl(disk, 0x70020, null, 0, buffer, (uint)performance.Length, out var returned, IntPtr.Zero) || returned < 56) // IOCTL_DISK_PERFORMANCE
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Disk performance counters are unavailable.");
        return new(DateTimeOffset.UtcNow, BinaryPrimitives.ReadInt64LittleEndian(performance.AsSpan(8)),
            BinaryPrimitives.ReadUInt32LittleEndian(performance.AsSpan(44)), BinaryPrimitives.ReadUInt32LittleEndian(performance.AsSpan(48)));
    }

    /// <summary>Whole-file sequential 1 MiB unbuffered pass with <paramref name="queue"/> requests in flight
    /// (one thread per request on an overlapped handle). Write passes use random data.</summary>
    public static unsafe SequentialPass Sequential(string path, long bytes, bool write, int queue, bool create = false, bool flush = false, bool perSecond = false)
    {
        if (bytes <= 0 || bytes % Block != 0) throw new ArgumentException("Passes cover whole MiB.");
        if (!create && new FileInfo(path).Length < bytes) throw new IOException($"{path} is smaller than the pass.");
        using var handle = File.OpenHandle(path, create ? FileMode.CreateNew : FileMode.Open, write ? FileAccess.ReadWrite : FileAccess.Read,
            FileShare.Read, (create ? FileOptions.None : FileOptions.Asynchronous) | NoBuffering);
        long next = 0, done = 0;
        var errors = new List<Exception>();
        var started = DateTimeOffset.UtcNow;
        var series = perSecond ? new List<long>() : null;
        var timer = Stopwatch.StartNew();
        var threads = Enumerable.Range(0, create ? 1 : queue).Select(index => new Thread(() =>
        {
            var buffer = (byte*)NativeMemory.AlignedAlloc(Block, 4096);
            try
            {
                if (write) new Random(1307 + index).NextBytes(new Span<byte>(buffer, Block));
                while (true)
                {
                    var offset = Interlocked.Add(ref next, Block) - Block;
                    if (offset >= bytes) break;
                    var transferred = write ? WriteAt(handle, buffer, offset) : RandomAccess.Read(handle, new Span<byte>(buffer, Block), offset);
                    if (transferred != Block) throw new IOException($"Short transfer at {offset}.");
                    Interlocked.Add(ref done, Block);
                }
            }
            catch (Exception ex) { lock (errors) errors.Add(ex); Interlocked.Exchange(ref next, bytes); }
            finally { NativeMemory.AlignedFree(buffer); }
        }) { IsBackground = true }).ToArray();
        foreach (var thread in threads) thread.Start();
        long last = 0;
        if (series is null)
            foreach (var thread in threads) thread.Join();
        else
            while (threads.Any(t => t.IsAlive))
            {
                Thread.Sleep(1000);
                var now = Interlocked.Read(ref done); series.Add(now - last); last = now;
            }
        var seconds = timer.Elapsed.TotalSeconds;
        var ended = DateTimeOffset.UtcNow;
        if (errors.Count > 0) throw new AggregateException("Sequential pass failed.", errors);
        if (done != bytes) throw new IOException("The pass did not cover the whole range.");
        if (series is not null && done > last) series.Add(done - last);
        double? flushSeconds = null;
        if (flush)
        {
            var flushTimer = Stopwatch.StartNew();
            if (!FlushFileBuffers(handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
            flushSeconds = flushTimer.Elapsed.TotalSeconds;
        }
        return new(bytes, seconds, bytes / seconds / 1_000_000, flushSeconds, series?.ToArray(), started, ended);
    }

    private static unsafe int WriteAt(SafeFileHandle handle, byte* buffer, long offset)
    {
        RandomAccess.Write(handle, new ReadOnlySpan<byte>(buffer, Block), offset);
        return Block;
    }

    private static string PrepareCache(ComparisonTarget target, string work, string prefix, bool onlyCheck)
    {
        var missing = ComparisonPlan.CacheFiles.Where(f => !File.Exists(Path.Combine(work, f.Name)) || new FileInfo(Path.Combine(work, f.Name)).Length != f.Bytes).ToArray();
        if (onlyCheck && missing.Length > 0) throw new IOException("Run compare-cache first; its workload files are missing: " + string.Join(", ", missing.Select(m => m.Name)));
        var needed = missing.Sum(f => f.Bytes) + (4L << 30);
        var free = new DriveInfo(target.Root).AvailableFreeSpace;
        if (free < needed) throw new IOException($"compare-cache needs {needed >> 20} MiB free on {target.Root}; it has {free >> 20} MiB.");
        var created = new List<object>();
        foreach (var (name, bytes) in missing)
        {
            var path = Path.Combine(work, name);
            File.Delete(path);
            created.Add(new { Name = name, Pass = Sequential(path, bytes, write: true, queue: 1, create: true) });
        }
        RunStorage.AtomicJson(prefix + ".files.json", new { Created = created, Kept = ComparisonPlan.CacheFiles.Except(missing).Select(f => f.Name) });
        return missing.Length == 0 ? "All workload files were already present at their sizes; kept them."
            : $"Created {string.Join(", ", missing.Select(m => m.Name))}; kept the others.";
    }

    internal static TreeResult RunTreeDecision(string root, int? diskNumber, int settleSeconds, CacheDevice device) =>
        Tree(root, diskNumber, settleSeconds, device);

    private static TreeResult Tree(string root, int? diskNumber, int settleSeconds, CacheDevice? device = null)
    {
        var startMemory = Memory();
        TreeCacheSnapshot? CacheSnapshot() => device is null ? null :
            new(device.GetWriteCacheState(), device.GetPerformance(), device.GetDiagnostics());
        var spec = ComparisonData.TreeSpec();
        var content = ComparisonData.TreeContent();
        var copy = root + "-copy";
        using (var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            for (var i = 0; i < spec.Count; i++) expected.AppendData(content, ComparisonData.TreeOffset(i, spec[i].Bytes), spec[i].Bytes);
            var expectedHash = expected.GetHashAndReset();
            var directories = spec.Select(s => Path.GetDirectoryName(s.Path)!).Distinct().ToArray();
            var beforeCreateCache = CacheSnapshot();
            var timer = Stopwatch.StartNew();
            foreach (var directory in directories) Directory.CreateDirectory(Path.Combine(root, directory));
            for (var i = 0; i < spec.Count; i++)
            {
                using var file = new FileStream(Path.Combine(root, spec[i].Path), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                file.Write(content, ComparisonData.TreeOffset(i, spec[i].Bytes), spec[i].Bytes);
            }
            var create = timer.Elapsed.TotalSeconds;
            var afterCreateCache = CacheSnapshot();
            var afterCreateWorkMemory = Memory();
            var afterCreate = settleSeconds > 0 ? Settle(diskNumber, settleSeconds) : null;
            var afterCreateSettleCache = CacheSnapshot();
            var afterCreateMemory = Memory();
            var beforeCopyCache = CacheSnapshot();
            timer.Restart();
            foreach (var directory in directories) Directory.CreateDirectory(Path.Combine(copy, directory));
            var copiers = Enumerable.Range(0, 4).Select(t => Task.Factory.StartNew(() =>
            {
                for (var i = t; i < spec.Count; i += 4) File.Copy(Path.Combine(root, spec[i].Path), Path.Combine(copy, spec[i].Path));
            }, TaskCreationOptions.LongRunning)).ToArray();
            Task.WaitAll(copiers);
            var copySeconds = timer.Elapsed.TotalSeconds;
            var afterCopyCache = CacheSnapshot();
            var afterCopyWorkMemory = Memory();
            var afterCopy = settleSeconds > 0 ? Settle(diskNumber, settleSeconds) : null;
            var afterCopySettleCache = CacheSnapshot();
            var afterCopyMemory = Memory();
            var beforeReadCache = CacheSnapshot();
            var beforeReadMemory = Memory();
            timer.Restart();
            using var actual = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            foreach (var (path, bytes) in spec)
            {
                using var file = new FileStream(Path.Combine(copy, path), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                if (file.Length != bytes) throw new InvalidDataException($"Copied file {path} has the wrong size.");
                file.ReadExactly(buffer, 0, bytes);
                actual.AppendData(buffer, 0, bytes);
            }
            var read = timer.Elapsed.TotalSeconds;
            if (!actual.GetHashAndReset().AsSpan().SequenceEqual(expectedHash)) throw new InvalidDataException("The copied file tree differs from the original.");
            var afterReadCache = CacheSnapshot();
            var afterReadMemory = Memory();
            var beforeDeleteCache = CacheSnapshot();
            timer.Restart();
            Directory.Delete(root, recursive: true);
            Directory.Delete(copy, recursive: true);
            var delete = timer.Elapsed.TotalSeconds;
            var afterDeleteCache = CacheSnapshot();
            return new(spec.Count, spec.Sum(s => (long)s.Bytes), create, copySeconds, read, delete,
                afterCreate, afterCopy, true, startMemory, afterCreateWorkMemory, afterCreateMemory,
                afterCopyWorkMemory, afterCopyMemory, beforeReadMemory, afterReadMemory,
                beforeCreateCache, afterCreateCache, afterCreateSettleCache,
                beforeCopyCache, afterCopyCache, afterCopySettleCache,
                beforeReadCache, afterReadCache, beforeDeleteCache, afterDeleteCache);
        }
    }

    private static unsafe void WriteSet(string path, ulong nonce, int generation, int set, int mebibytes, bool unbuffered)
    {
        var sectors = (long)mebibytes << 8;
        if (unbuffered)
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, NoBuffering);
            var buffer = (byte*)NativeMemory.AlignedAlloc(Block, 4096);
            try
            {
                for (long first = 0; first < sectors; first += 256)
                {
                    for (var k = 0; k < 256; k++) ComparisonData.FillSector(new Span<byte>(buffer + k * 4096, 4096), nonce, generation, set, first + k);
                    RandomAccess.Write(handle, new ReadOnlySpan<byte>(buffer, Block), first * 4096);
                }
            }
            finally { NativeMemory.AlignedFree(buffer); }
            return;
        }
        using var file = new FileStream(path, generation == 1 ? FileMode.CreateNew : FileMode.Open, FileAccess.Write, FileShare.None, 1 << 16);
        var chunk = new byte[Block];
        for (long first = 0; first < sectors; first += 256)
        {
            for (var k = 0; k < 256; k++) ComparisonData.FillSector(chunk.AsSpan(k * 4096, 4096), nonce, generation, set, first + k);
            file.Write(chunk);
        }
        file.Flush(flushToDisk: generation == 1 || set == 1);
    }

    private static string DurabilityWrite(ComparisonTarget target, string work, string oraclePath, string product, string prefix)
    {
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        var nonce = BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8));
        var flushed = Path.Combine(work, "flushed.dat"); var unflushed = Path.Combine(work, "unflushed.dat");
        var small = Path.Combine(work, "small-new");
        // Generation 1 is written and flushed, then given time to reach the disk even through a write-back cache.
        WriteSet(flushed, nonce, 1, 1, ComparisonPlan.DurabilitySetMiB, unbuffered: false);
        WriteSet(unflushed, nonce, 1, 2, ComparisonPlan.DurabilitySetMiB, unbuffered: false);
        var generation1 = DateTimeOffset.UtcNow;
        var settle = Settle(target.DiskNumber, 60);
        // Generation 2: an application-style durable save (buffered writes, then FlushFileBuffers), raw writes without a
        // flush, and new small files without a flush. The reset follows right after this run finishes.
        var timer = Stopwatch.StartNew();
        WriteSet(flushed, nonce, 2, 1, ComparisonPlan.DurabilitySetMiB, unbuffered: false);
        var flushSeconds = timer.Elapsed.TotalSeconds;
        WriteSet(unflushed, nonce, 2, 2, ComparisonPlan.DurabilitySetMiB, unbuffered: true);
        Directory.CreateDirectory(small);
        var sector = new byte[4096];
        for (var i = 0; i < ComparisonPlan.DurabilitySmallFiles; i++)
        {
            ComparisonData.FillSector(sector, nonce, 2, 3, i);
            File.WriteAllBytes(Path.Combine(small, $"n{i:D5}.bin"), sector);
        }
        var oracle = new DurabilityOracle(1, nonce, work, ComparisonPlan.DurabilitySetMiB, ComparisonPlan.DurabilitySmallFiles,
            generation1, DateTimeOffset.UtcNow, flushSeconds, product);
        using (var file = new FileStream(oraclePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            file.Write(JsonSerializer.SerializeToUtf8Bytes(oracle, RunStorage.Json));
            file.Flush(flushToDisk: true);
        }
        RunStorage.AtomicJson(prefix + ".settle.json", settle);
        return $"Generation 1 flushed and settled ({Describe(settle)}); generation 2 written: durable save (flush {flushSeconds:F3} s), " +
            $"{ComparisonPlan.DurabilitySetMiB} MiB raw writes and {ComparisonPlan.DurabilitySmallFiles} small files without a flush. Reset the VM now.";
    }

    /// <summary>Base files written and settled, then 40 s of steady file creation (200 per second) with deletes, so a
    /// write-back cache holds part of the work while older parts are on the disk. The VM is reset during the write-out
    /// that follows FINISHED.txt (the host watches the disk counters).</summary>
    private static string DurabilityChurn(ComparisonTarget target, string work, string oraclePath, string product, string prefix)
    {
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        var baseDirectory = Path.Combine(work, "base"); var churnDirectory = Path.Combine(work, "churn");
        Directory.CreateDirectory(baseDirectory); Directory.CreateDirectory(churnDirectory);
        var nonce = BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8));
        for (var i = 0; i < ComparisonData.ChurnBaseFiles; i++)
            File.WriteAllBytes(Path.Combine(baseDirectory, $"b{i:D5}.bin"), ComparisonData.ChurnFile(nonce, ComparisonData.ChurnBaseSet, i));
        var settle = Settle(target.DiskNumber, 60);
        var baseSettled = DateTimeOffset.UtcNow;
        var created = new List<int>(); var churnDeleted = new List<int>();
        var baseDeleted = Enumerable.Repeat(-1, ComparisonData.ChurnBaseFiles).ToArray();
        var start = DateTimeOffset.UtcNow; var clock = Stopwatch.StartNew();
        for (var i = 0; clock.Elapsed.TotalSeconds < ComparisonData.ChurnSeconds; i++)
        {
            while (clock.Elapsed.TotalMilliseconds < i * 1000.0 / ComparisonData.ChurnFilesPerSecond) Thread.Sleep(1);
            File.WriteAllBytes(Path.Combine(churnDirectory, $"c{i:D6}.bin"), ComparisonData.ChurnFile(nonce, ComparisonData.ChurnSet, i));
            created.Add((int)clock.ElapsedMilliseconds); churnDeleted.Add(-1);
            var (deleteBase, deleteChurn) = ComparisonData.ChurnDeletes(i);
            if (deleteBase is { } b && baseDeleted[b] < 0)
            {
                File.Delete(Path.Combine(baseDirectory, $"b{b:D5}.bin"));
                baseDeleted[b] = (int)clock.ElapsedMilliseconds;
            }
            if (deleteChurn is { } c)
            {
                File.Delete(Path.Combine(churnDirectory, $"c{c:D6}.bin"));
                churnDeleted[c] = (int)clock.ElapsedMilliseconds;
            }
        }
        var oracle = new ChurnOracle(2, nonce, work, ComparisonData.ChurnBaseFiles, baseSettled, start, DateTimeOffset.UtcNow,
            [.. created], baseDeleted, [.. churnDeleted], product);
        using (var file = new FileStream(oraclePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            file.Write(JsonSerializer.SerializeToUtf8Bytes(oracle, RunStorage.Json));
            file.Flush(flushToDisk: true);
        }
        RunStorage.AtomicJson(prefix + ".settle.json", settle);
        return $"{ComparisonData.ChurnBaseFiles} base files settled ({Describe(settle)}); churn: {created.Count} files created in " +
            $"{clock.Elapsed.TotalSeconds:F1} s, {baseDeleted.Count(d => d >= 0)} base and {churnDeleted.Count(d => d >= 0)} churn files deleted, " +
            "nothing flushed. Reset the VM during the write-out that follows.";
    }

    private static async Task<string> ChurnVerify(ComparisonTarget target, ChurnOracle oracle, string prefix)
    {
        var boot = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(System.Environment.TickCount64);
        if (boot <= oracle.CompletedUtc) throw new IOException("Windows has not restarted since the write phase; reset the VM first.");
        var groups = new Dictionary<string, int[]>(StringComparer.Ordinal); // expected, intact, damaged, missing
        var damage = new Dictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal);
        var unreadable = new List<string>();
        async Task Count(bool isBase, int index)
        {
            var group = ComparisonData.ChurnGroup(oracle, isBase, index);
            if (!groups.TryGetValue(group, out var counts)) groups[group] = counts = new int[4];
            counts[0]++;
            var set = isBase ? ComparisonData.ChurnBaseSet : ComparisonData.ChurnSet;
            var path = isBase ? Path.Combine(oracle.Directory, "base", $"b{index:D5}.bin") : Path.Combine(oracle.Directory, "churn", $"c{index:D6}.bin");
            if (!File.Exists(path)) { counts[3]++; return; }
            byte[]? bytes = null;
            string kind;
            // NTFS refuses files whose records it finds broken ("corrupted and unreadable"); that is a result, not a stop.
            try { bytes = await File.ReadAllBytesAsync(path); kind = ""; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { kind = "unreadable"; unreadable.Add($"{path}: {e.Message}"); }
            if (bytes is not null && ComparisonData.ChurnIntact(bytes, oracle.Nonce, set, index)) { counts[1]++; return; }
            counts[2]++;
            if (!damage.TryGetValue(group, out var kinds)) damage[group] = kinds = new(StringComparer.Ordinal);
            if (bytes is not null) kind = ComparisonData.ChurnDamage(bytes, set, index);
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
        }
        for (var i = 0; i < oracle.BaseFiles; i++) await Count(isBase: true, i);
        for (var i = 0; i < oracle.CreatedMs.Length; i++) await Count(isBase: false, i);
        var chkdsk = await OwnedProcess.RunAsync(Path.Combine(System.Environment.SystemDirectory, "chkdsk.exe"), [$"{target.Letter}:", "/scan"],
            prefix + ".chkdsk", TimeSpan.FromMinutes(20), CancellationToken.None);
        string[] order = ["base files kept", "base files deleted during the churn", "churn files 30 s or older", "churn files 20-30 s old",
            "churn files 10-20 s old", "churn files under 10 s old", "churn files deleted again"];
        var result = new ChurnResult(boot, [.. order.Where(groups.ContainsKey).Select(g => new ChurnBucket(g, groups[g][0], groups[g][1], groups[g][2], groups[g][3],
            damage.TryGetValue(g, out var kinds) ? kinds : new SortedDictionary<string, int>()))],
            chkdsk.ExitCode, unreadable);
        RunStorage.AtomicJson(prefix + ".churn.json", result);
        return "After the reset: " + string.Join("; ", result.Groups.Select(g => $"{g.Group} {g.Intact} intact, {g.Damaged} damaged, {g.Missing} missing of {g.Expected}")) +
            $"; chkdsk /scan exit {chkdsk.ExitCode}. Measurement only.";
    }

    private static SectorCounts CountSectors(string path, ulong nonce, int set, int mebibytes)
    {
        var expected = (long)mebibytes << 8;
        if (!File.Exists(path)) return new(expected, 0, 0, 0, expected);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var available = Math.Min(expected, file.Length / 4096);
        long fresh = 0, old = 0, other = 0;
        var sector = new byte[4096];
        for (long index = 0; index < available; index++)
        {
            file.ReadExactly(sector);
            switch (ComparisonData.Classify(sector, nonce, set, index)) { case 2: fresh++; break; case 1: old++; break; default: other++; break; }
        }
        return new(expected, fresh, old, other, expected - available);
    }

    private static async Task<string> DurabilityVerify(ComparisonTarget target, string oraclePath, string prefix)
    {
        var writeRun = Path.GetDirectoryName(Path.GetFullPath(oraclePath))!;
        var finished = Path.Combine(writeRun, "FINISHED.txt");
        if (!File.Exists(finished) || !File.ReadLines(finished).First().StartsWith("COMPLETED", StringComparison.Ordinal))
            throw new IOException("The write phase did not finish COMPLETED; its oracle cannot be used.");
        var oracleBytes = await File.ReadAllBytesAsync(oraclePath);
        using (var document = JsonDocument.Parse(oracleBytes))
            if (document.RootElement.GetProperty("SchemaVersion").GetInt32() == 2)
            {
                var churn = JsonSerializer.Deserialize<ChurnOracle>(oracleBytes) ?? throw new InvalidDataException("Missing churn oracle.");
                if (char.ToUpperInvariant(churn.Directory[0]) != target.Letter) throw new IOException("The oracle belongs to another volume.");
                return await ChurnVerify(target, churn, prefix);
            }
        var oracle = JsonSerializer.Deserialize<DurabilityOracle>(oracleBytes) ?? throw new InvalidDataException("Missing durability oracle.");
        if (oracle.SchemaVersion != 1) throw new InvalidDataException("Unknown durability oracle version.");
        if (char.ToUpperInvariant(oracle.Directory[0]) != target.Letter) throw new IOException("The oracle belongs to another volume.");
        var boot = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(System.Environment.TickCount64);
        if (boot <= oracle.CompletedUtc) throw new IOException("Windows has not restarted since the write phase; reset the VM first.");
        var flushed = CountSectors(Path.Combine(oracle.Directory, "flushed.dat"), oracle.Nonce, 1, oracle.SetMiB);
        var unflushed = CountSectors(Path.Combine(oracle.Directory, "unflushed.dat"), oracle.Nonce, 2, oracle.SetMiB);
        int intact = 0, damaged = 0, missing = 0;
        var sector = new byte[4096];
        for (var i = 0; i < oracle.SmallFiles; i++)
        {
            var path = Path.Combine(oracle.Directory, "small-new", $"n{i:D5}.bin");
            if (!File.Exists(path)) { missing++; continue; }
            var bytes = await File.ReadAllBytesAsync(path);
            if (bytes.Length == 4096 && ComparisonData.Classify(bytes, oracle.Nonce, 3, i) == 2) intact++; else damaged++;
        }
        var chkdsk = await OwnedProcess.RunAsync(Path.Combine(System.Environment.SystemDirectory, "chkdsk.exe"), [$"{target.Letter}:", "/scan"],
            prefix + ".chkdsk", TimeSpan.FromMinutes(20), CancellationToken.None);
        var result = new DurabilityResult(boot, flushed, unflushed, oracle.SmallFiles, intact, damaged, missing, chkdsk.ExitCode);
        RunStorage.AtomicJson(prefix + ".result.json", result);
        static string Share(SectorCounts c) => $"{c.New * 100.0 / c.Expected:F1}% new, {c.Old * 100.0 / c.Expected:F1}% old, {(c.Other + c.Missing) * 100.0 / c.Expected:F1}% other or missing";
        return $"After the reset: durable save {Share(flushed)}; raw writes without a flush {Share(unflushed)}; small files {intact} intact, " +
            $"{damaged} damaged, {missing} missing of {oracle.SmallFiles}; chkdsk /scan exit {chkdsk.ExitCode}. Measurement only.";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size; public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool GetPerformanceInfo(ref PerformanceInformation information, uint size);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, [Out] char[] volumeName, uint length);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool DeviceIoControl(SafeFileHandle device, uint code, void* input, uint inputLength, void* output, uint outputLength, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FlushFileBuffers(SafeFileHandle handle);
}
