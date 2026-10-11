using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using QueueCache.Management;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Developer.Verification;

/// <summary>
/// Plan 120 "busy day" on the lab disk's two NTFS volumes at once: writers of four request sizes and an
/// overlapped writer/reader whose requests are cancelled at random, all checked by versioned sectors,
/// while a chaos actor per volume flushes, resizes, tears down, drops clean data, slows the disk, injects
/// transient allocation failures and refused or rolled-back settings, and owned RAM disks are created and
/// removed against the same memory reservation. Then each drain parallelism is faulted under dirty data
/// and retried. Every byte is finally read back from the disk with both caches released.
/// No raw disk writes, no reboot; the sibling volume and owned RAM disks are left as found.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class StressRaceScenarios
{
    private const int MiB = 1 << 20;
    private static readonly int[] Budgets = [64, 96, 128, 256];

    public static async Task<IReadOnlyList<CheckResult>> RunAsync(DiskTarget target, CacheDevice device, string workDirectory,
        int seconds, string ownership, string evidence)
    {
        if (seconds is < 60 or > 3600) throw new ArgumentOutOfRangeException(nameof(seconds));
        var sibling = (await VolumeCatalog.ListAsync()).FirstOrDefault(volume =>
            volume.DiskNumber == target.Number && string.Equals(volume.Instance, target.Instance, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(volume.Volume, target.Device, StringComparison.OrdinalIgnoreCase) && volume.HasFileSystem &&
            !volume.IsBoot && !volume.IsSystem && !volume.IsPaging)
            ?? throw new IOException("stress-races needs the lab disk's second NTFS volume (qcache developer lab-disk create).");
        var other = await DiskTarget.InspectAsync(sibling.Volume);
        using var otherDevice = new CacheDevice(other.Device, writable: true);
        var otherInitial = otherDevice.GetWriteCacheState();
        if (otherInitial.BudgetBytes != 0 || SavedConfigurations.IsSaved(other.VolumeId))
            throw new IOException($"{other.Device} already has a cache task or saved profile; stress-races does not change it.");
        var initial = device.GetWriteCacheState();
        var baselineReserved = initial.GlobalReservedBytes;
        if (initial.GlobalLimitBytes == 0)
            throw new NotSupportedException("The loaded driver does not report the shared memory limit stress-races needs.");
        if (File.Exists(ownership)) throw new IOException("stress-races ownership journal already exists.");
        var service = new ManagedDiskBrokerClient();
        RunStorage.AtomicJson(ownership, new RamReadReferenceOwnership(baselineReserved,
            (await service.ListAsync()).Select(r => r.ResourceId).ToArray(), []));
        var volumes = new[] { new StressVolume(target, device, workDirectory, 0x51A7_0001), new StressVolume(other, otherDevice,
            Path.Combine(other.Root, Path.GetFileName(workDirectory) + "-stress"), 0x51A7_0002) };
        var checks = new List<CheckResult>();
        Exception? failure = null;
        try
        {
            foreach (var volume in volumes) volume.Prepare();
            checks.Add(await BusyDay(volumes, seconds, ownership, evidence));
            checks.AddRange(FaultRetry(volumes[0], evidence));
            foreach (var volume in volumes) volume.ReleaseAndVerify("final");
            // Push the lab file systems' lazy metadata through to the lab disk now, so the backing volume
            // that holds the lab image is clean again when the campaign checks it.
            foreach (var volume in volumes) FlushVolume(volume.Target);
            var reserved = device.GetWriteCacheState().GlobalReservedBytes;
            RunStorage.AtomicJson(evidence + ".reservation.json", new { Baseline = baselineReserved, Final = reserved });
            if (reserved != baselineReserved)
                throw new IOException($"Shared memory reservation is {reserved} bytes after every cache and owned RAM disk was released; it was {baselineReserved}.");
            checks.Add(new("stress/reservation-exact", "PASS", $"Shared reservation returned exactly to {baselineReserved} bytes; " +
                $"all {volumes.Sum(v => v.Regions.Sum(r => r.Region.Bytes)) >> 20} MiB of versioned blocks read back from disk with both caches released."));
        }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            foreach (var volume in volumes)
                try { volume.Device.Control(WriteCacheAction.LabFault); volume.Device.Control(WriteCacheAction.LabDelay); } catch (Win32Exception) { }
            // The sibling is left as found (no cache task); the runner restores the target's captured state.
            foreach (var volume in volumes)
                try { volume.Device.Control(WriteCacheAction.Disable); volume.Device.Control(WriteCacheAction.Release); FlushVolume(volume.Target); }
                catch (Win32Exception) { }
            try { await RamReadReferenceScenarios.CleanupAsync(device, ownership, evidence + ".ram-cleanup.json", finalizeTrace: false); }
            catch (Exception cleanup) when (failure is not null)
            {
                // Keep the original failure; the runner's restoration repeats the guarded cleanup.
                RunStorage.AtomicJson(evidence + ".ram-cleanup-failure.json", new { Error = cleanup.ToString() });
            }
        }
        return checks;
    }

    private static async Task<CheckResult> BusyDay(StressVolume[] volumes, int seconds, string ownership, string evidence)
    {
        using var stop = new CancellationTokenSource();
        var failures = new List<Exception>();
        void Fail(Exception exception) { lock (failures) failures.Add(exception); stop.Cancel(); }
        foreach (var volume in volumes)
            volume.Apply(new CacheConfiguration(128, CachePreset.Fast) { Options = new CacheOptions(Parallelism: 2) });
        var tasks = new List<Task>();
        foreach (var volume in volumes)
        {
            foreach (var region in volume.Regions.Where(r => !r.Region.Overlapped))
                tasks.Add(Run(() => volume.WriteLoop(region, stop.Token)));
            tasks.Add(Run(() => volume.OverlappedLoop(volume.Regions.Single(r => r.Region.Overlapped), stop.Token)));
            for (var reader = 0; reader < 2; reader++)
            {
                var seed = volume.Seed + (ulong)reader;
                tasks.Add(Run(() => volume.ReadLoop(new Random((int)seed), stop.Token)));
            }
            tasks.Add(Run(() => volume.Chaos(new Random((int)(volume.Seed >> 8)), stop.Token)));
        }
        var churn = new RamChurn(ownership);
        tasks.Add(Run(() => churn.Loop(stop.Token)));
        Task Run(Action action) => Task.Factory.StartNew(() =>
        {
            try { action(); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception exception) { Fail(exception); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < seconds && !stop.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, seconds - timer.Elapsed.TotalSeconds)));
            RunStorage.AtomicJson(evidence + ".progress.json", new
            {
                ElapsedSeconds = timer.Elapsed.TotalSeconds, Volumes = volumes.Select(v => v.Summary()), RamDiskCycles = churn.Cycles
            });
        }
        stop.Cancel();
        if (!Task.WaitAll([.. tasks], TimeSpan.FromMinutes(10)))
            failures.Add(new IOException("A stress worker did not stop within 10 minutes after the window ended."));
        RunStorage.AtomicJson(evidence + ".busy.json", new
        {
            Seconds = timer.Elapsed.TotalSeconds, Volumes = volumes.Select(v => v.Summary()), RamDiskCycles = churn.Cycles,
            RamDiskBytesVerified = churn.VerifiedBytes, Failures = failures.Select(f => f.ToString())
        });
        if (failures.Count > 0)
            throw new IOException("stress/busy-volumes failed: " + failures[0].Message, failures[0]);
        foreach (var volume in volumes)
        {
            var state = volume.Device.GetWriteCacheState();
            if (state.LastError != 0 || state.Faulted)
                throw new IOException($"{volume.Target.Device} faulted during the busy window (0x{state.LastError:X8}).");
            volume.RequireCoverage();
        }
        if (churn.Cycles < 2) throw new IOException($"Only {churn.Cycles} owned RAM disk cycles overlapped the busy window.");
        return new("stress/busy-volumes", "PASS", $"{timer.Elapsed.TotalSeconds:F0} s on {string.Join(" and ", volumes.Select(v => v.Target.Device))}: " +
            string.Join("; ", volumes.Select(v => v.Describe())) + $"; {churn.Cycles} owned 2 GiB RAM disks created, written, verified and removed meanwhile. " +
            "Every read returned whole sectors of an allowed version.");
    }

    /// <summary>For drain parallelism 1, 2 and 4: dirty data from every region, an injected lower write
    /// failure (or short transfer), reads that fail rather than return old data, Retry, and the disk bytes.</summary>
    private static IEnumerable<CheckResult> FaultRetry(StressVolume volume, string evidence)
    {
        foreach (var (parallelism, fault) in new[] { (1, 4UL), (2, 5UL), (4, 4UL) })
        {
            var label = $"stress/fault-retry-p{parallelism}";
            volume.Apply(new CacheConfiguration(256, CachePreset.Fast)
            {
                Options = new CacheOptions(Drain: DrainAlgorithm.Idle, MaxDirtyAgeMs: 3600000, IdleMs: 60000, Parallelism: parallelism, RetainWrites: false)
            });
            volume.WritePass();
            var dirty = volume.Device.GetWriteCacheState();
            if (dirty.DirtyBytes < 16UL * MiB) throw new IOException($"{label}: only {dirty.DirtyBytes} dirty bytes before the injected failure.");
            volume.Device.Control(WriteCacheAction.LabFault, value: fault);
            var failed = false;
            try { volume.Device.Control(WriteCacheAction.Flush); }
            catch (Win32Exception) { failed = true; }
            var faulted = volume.Device.GetWriteCacheState();
            if (!failed || !faulted.Faulted || faulted.DirtyBytes == 0)
                throw new IOException($"{label}: the injected lower {(fault == 4 ? "write failure" : "short transfer")} did not fault the cache with dirty data retained.");
            var readFailed = volume.ReadFailsWhileFaulted();
            volume.Device.Control(WriteCacheAction.LabFault);
            volume.Device.Control(WriteCacheAction.Retry);
            volume.Device.Control(WriteCacheAction.Flush);
            var retried = volume.Device.GetWriteCacheState();
            RunStorage.AtomicJson(evidence + $".{label.Replace('/', '-')}.json", new { Dirty = dirty, Faulted = faulted, Retried = retried, ReadFailed = readFailed });
            if (retried.Faulted || retried.LastError != 0 || retried.DirtyBytes != 0)
                throw new IOException($"{label}: Retry did not drain the retained data (0x{retried.LastError:X8}, {retried.DirtyBytes} dirty bytes).");
            volume.ReleaseAndVerify(label);
            yield return new(label, "PASS", $"Drain parallelism {parallelism}: {dirty.DirtyBytes >> 20} MiB dirty across 4 KiB to 4 MiB blocks; " +
                $"lab fault {fault} faulted the cache with {faulted.DirtyBytes >> 20} MiB retained; a read failed instead of returning data; " +
                "Retry drained everything and the disk held each block's last version.");
        }
    }

    private sealed class RegionState(StressRegion region, string path, ulong seed, int index)
    {
        public StressRegion Region { get; } = region;
        public string Path { get; } = path;
        public ulong Seed { get; } = seed;
        public int Index { get; } = index;
        public StressBlockVersions Versions { get; } = new(region.Blocks);
        public long Writes, Reads, Cancelled;
    }

    private sealed class StressVolume(DiskTarget target, CacheDevice device, string directory, ulong seed)
    {
        public DiskTarget Target { get; } = target;
        public CacheDevice Device { get; } = device;
        public ulong Seed { get; } = seed;
        public RegionState[] Regions { get; } = StressRegion.All.Select((region, index) =>
            new RegionState(region, System.IO.Path.Combine(directory, $"stress-{region.Name}.bin"), seed * 31 + (ulong)index, index)).ToArray();
        private readonly Dictionary<StressAction, int> actions = [];
        private long verifiedReads;
        private int[] versionsAtPass = [];

        public void Prepare()
        {
            Device.Control(WriteCacheAction.Disable);
            Device.Control(WriteCacheAction.Release);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            foreach (var region in Regions)
            {
                using var file = new AlignedFile(region.Path, region.Region.BlockBytes, create: true);
                var buffer = new byte[region.Region.BlockBytes];
                for (var block = 0; block < region.Region.Blocks; block++)
                {
                    StressPattern.Fill(buffer, region.Seed, block, 0);
                    file.Write((long)block * buffer.Length, buffer);
                }
                file.Flush();
            }
        }

        public WriteCacheState Apply(CacheConfiguration configuration)
        {
            var state = ConfigurationManager.Apply(Target, configuration, true);
            if (!state.Operational || state.BudgetBytes != (ulong)configuration.BudgetMiB << 20)
                throw new IOException($"{Target.Device}: applied settings are not operational at {configuration.BudgetMiB} MiB.");
            return state;
        }

        public void WriteLoop(RegionState region, CancellationToken token)
        {
            using var file = new AlignedFile(region.Path, region.Region.BlockBytes, create: false, shareRead: true);
            var random = new Random((int)region.Seed);
            var buffer = new byte[region.Region.BlockBytes];
            var next = new int[region.Region.Blocks];
            while (!token.IsCancellationRequested)
            {
                var block = random.Next(region.Region.Blocks);
                var version = ++next[block];
                StressPattern.Fill(buffer, region.Seed, block, version);
                region.Versions.Begin(block, version);
                file.Write((long)block * buffer.Length, buffer);
                region.Versions.Complete(block, version);
                Interlocked.Increment(ref region.Writes);
            }
        }

        /// <summary>Eight overlapped writes and eight overlapped reads at a time, cancelled after 0-3 ms in
        /// most batches. A cancelled write may or may not have landed; both versions stay allowed until the
        /// block is written again.</summary>
        public void OverlappedLoop(RegionState region, CancellationToken token)
        {
            const int depth = 8;
            var block = region.Region.BlockBytes;
            using var handle = File.OpenHandle(region.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
                FileOptions.Asynchronous | (FileOptions)0x20000000);
            using var memory = new AlignedMemory(2 * depth * block);
            var random = new Random((int)region.Seed);
            var next = new int[region.Region.Blocks];
            while (!token.IsCancellationRequested)
            {
                var chosen = Enumerable.Range(0, region.Region.Blocks).OrderBy(_ => random.Next()).Take(2 * depth).ToArray();
                using var cancel = new CancellationTokenSource();
                var operations = new List<(Task<int> Task, int Block, int Version, int Low, bool Write, int Slot)>();
                for (var i = 0; i < 2 * depth; i++)
                {
                    var b = chosen[i]; var slot = memory.Memory.Slice(i * block, block);
                    if (i < depth)
                    {
                        var version = ++next[b];
                        StressPattern.Fill(slot.Span, region.Seed, b, version);
                        region.Versions.Begin(b, version);
                        operations.Add((RandomAccess.WriteAsync(handle, slot, (long)b * block, cancel.Token).AsTask().ContinueWith(t => { t.GetAwaiter().GetResult(); return block; },
                            TaskContinuationOptions.ExecuteSynchronously), b, version, 0, true, i));
                    }
                    else
                        operations.Add((RandomAccess.ReadAsync(handle, slot, (long)b * block, cancel.Token).AsTask(), b, 0, region.Versions.Done(b), false, i));
                }
                if (random.Next(4) != 0)
                {
                    // Half are cancelled at once (requests still queued), half within 3 ms (in progress).
                    var spin = Stopwatch.StartNew(); var wait = random.Next(2) == 0 ? 0 : random.NextDouble() * 3;
                    while (spin.Elapsed.TotalMilliseconds < wait) Thread.SpinWait(50);
                    cancel.Cancel();
                }
                foreach (var operation in operations)
                {
                    try
                    {
                        var transferred = operation.Task.GetAwaiter().GetResult();
                        if (transferred != block) throw new IOException($"Short overlapped {(operation.Write ? "write" : "read")} on {region.Path}.");
                        if (operation.Write)
                        {
                            if (region.Versions.Done(operation.Block) < operation.Version) region.Versions.Complete(operation.Block, operation.Version);
                            Interlocked.Increment(ref region.Writes);
                        }
                        else
                        {
                            var problem = StressPattern.Check(memory.Memory.Span.Slice(operation.Slot * block, block), region.Seed, operation.Block,
                                operation.Low, region.Versions.Started(operation.Block));
                            if (problem is not null) throw new InvalidDataException($"{Target.Device} overlapped read: {problem}.");
                            Interlocked.Increment(ref region.Reads);
                        }
                    }
                    catch (OperationCanceledException) { Interlocked.Increment(ref region.Cancelled); }
                }
            }
        }

        public void ReadLoop(Random random, CancellationToken token)
        {
            var files = Regions.Select(r => new AlignedFile(r.Path, r.Region.BlockBytes, create: false, sharedReadOnly: true)).ToArray();
            try
            {
                var buffers = Regions.Select(r => new byte[r.Region.BlockBytes]).ToArray();
                while (!token.IsCancellationRequested)
                {
                    var index = random.Next(Regions.Length); var region = Regions[index];
                    var block = random.Next(region.Region.Blocks);
                    var low = region.Versions.Done(block);
                    files[index].Read((long)block * region.Region.BlockBytes, buffers[index]);
                    var problem = StressPattern.Check(buffers[index], region.Seed, block, low, region.Versions.Started(block));
                    if (problem is not null) throw new InvalidDataException($"{Target.Device} read: {problem}.");
                    Interlocked.Increment(ref region.Reads);
                    Interlocked.Increment(ref verifiedReads);
                }
            }
            finally { foreach (var file in files) file.Dispose(); }
        }

        public void Chaos(Random random, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var action = StressPlan.Next(random);
                Perform(action, random);
                lock (actions) actions[action] = actions.GetValueOrDefault(action) + 1;
                token.WaitHandle.WaitOne(random.Next(20, 300));
            }
        }

        private void Perform(StressAction action, Random random)
        {
            var before = Device.GetWriteCacheState();
            switch (action)
            {
                case StressAction.Flush:
                    if (before.Enabled) Device.Control(WriteCacheAction.Flush);
                    break;
                case StressAction.Resize:
                    Apply(StressPlan.RandomConfiguration(random, Budgets));
                    break;
                case StressAction.DropClean:
                    if (before.Enabled) Device.Control(WriteCacheAction.DropClean);
                    break;
                case StressAction.Teardown:
                    Device.Control(WriteCacheAction.Disable);
                    Device.Control(WriteCacheAction.Release);
                    Apply(StressPlan.RandomConfiguration(random, Budgets));
                    break;
                case StressAction.LowerDelay:
                    Device.Control(WriteCacheAction.LabDelay, value: (ulong)new[] { 0, 2, 10 }[random.Next(3)]);
                    break;
                case StressAction.TransientAllocation:
                    Device.Control(WriteCacheAction.LabFault, value: 8);
                    break;
                case StressAction.OversizeRefused:
                    // Beyond the RAM Windows keeps for itself: refused before anything changes.
                    try { ConfigurationManager.Apply(Target, new CacheConfiguration(MemoryBudget.MaximumMiB, CachePreset.Fast), true); }
                    catch (InsufficientMemoryForCacheException) { RequireUnchanged(before, "a refused oversized budget"); break; }
                    throw new IOException($"{Target.Device}: a {MemoryBudget.MaximumMiB} MiB budget was accepted.");
                case StressAction.OversizeKernel:
                    OversizeKernel(before);
                    break;
                case StressAction.AllocationRollback:
                    AllocationRollback(before, random);
                    break;
                case StressAction.Strict:
                    Apply(new CacheConfiguration(Budgets[random.Next(Budgets.Length)], CachePreset.Strict));
                    break;
            }
        }

        /// <summary>More than the whole shared limit, past the managed RAM check: the driver refuses the
        /// reservation after the old cache was freed, and the previous settings must come back. (A request
        /// sized from the free space can fit after all: the RAM churn releases 2 GiB at any moment.)</summary>
        private void OversizeKernel(WriteCacheState before)
        {
            if (!before.Operational) { Apply(StressPlan.RandomConfiguration(new Random(), Budgets)); return; }
            using var gate = ConfigurationGate.Enter(Target.Instance);
            var state = Device.GetWriteCacheState();
            var requested = (int)Math.Min(MemoryBudget.MaximumMiB, (state.GlobalLimitBytes >> 20) + 64);
            try
            {
                ConfigurationManager.ApplySteps(new CacheDeviceControl(Device), state, CacheConfiguration.FromState(state) with { BudgetMiB = requested });
            }
            catch (ConfigurationNotAppliedException refused) when (refused.PreviousSettingsRestored)
            {
                RequireUnchanged(state, "a budget beyond the shared reservation");
                return;
            }
            throw new IOException($"{Target.Device}: a {requested} MiB budget beyond the shared reservation was accepted.");
        }

        /// <summary>Lab faults 6/7 fail the next cache allocation once: the resize fails and rolls back.</summary>
        private void AllocationRollback(WriteCacheState before, Random random)
        {
            if (!before.Operational) { Apply(StressPlan.RandomConfiguration(random, Budgets)); return; }
            var current = (int)(before.BudgetBytes >> 20);
            var budget = Budgets.Where(b => b != current).ElementAt(random.Next(Budgets.Length - 1));
            Device.Control(WriteCacheAction.LabFault, value: random.Next(2) == 0 ? 6UL : 7UL);
            try
            {
                ConfigurationManager.Apply(Target, CacheConfiguration.FromState(before) with { BudgetMiB = budget }, true);
            }
            catch (ConfigurationNotAppliedException failed) when (failed.PreviousSettingsRestored)
            {
                RequireUnchanged(before, "a resize whose allocation failed");
                return;
            }
            finally { Device.Control(WriteCacheAction.LabFault); }
            throw new IOException($"{Target.Device}: a resize with an injected allocation failure did not fail.");
        }

        private void RequireUnchanged(WriteCacheState before, string what)
        {
            var after = Device.GetWriteCacheState();
            if (after.BudgetBytes != before.BudgetBytes || after.Enabled != before.Enabled || after.UnsafeDefer != before.UnsafeDefer ||
                after.Options != before.Options || after.LastError != 0 || after.Faulted)
                throw new IOException($"{Target.Device}: after {what} the cache was not left with its previous settings.");
        }

        /// <summary>One sequential pass over the synchronous regions, single-threaded (fault matrix).</summary>
        public void WritePass()
        {
            foreach (var region in Regions.Where(r => !r.Region.Overlapped))
            {
                using var file = new AlignedFile(region.Path, region.Region.BlockBytes, create: false, shareRead: true);
                var buffer = new byte[region.Region.BlockBytes];
                for (var block = 0; block < region.Region.Blocks; block++)
                {
                    var version = Math.Max(region.Versions.Started(block), region.Versions.Done(block)) + 1;
                    StressPattern.Fill(buffer, region.Seed, block, version);
                    region.Versions.Begin(block, version);
                    file.Write((long)block * buffer.Length, buffer);
                    region.Versions.Complete(block, version);
                }
            }
        }

        public bool ReadFailsWhileFaulted()
        {
            var region = Regions[2];
            using var file = new AlignedFile(region.Path, region.Region.BlockBytes, create: false, sharedReadOnly: true);
            try { file.Read(0, new byte[region.Region.BlockBytes]); }
            catch (Win32Exception) { return true; }
            throw new IOException($"{Target.Device}: a read on the faulted cache returned data instead of failing.");
        }

        /// <summary>Releases the cache (data reaches the disk) and reads every block back unbuffered.</summary>
        public void ReleaseAndVerify(string stage)
        {
            Device.Control(WriteCacheAction.LabDelay);
            Device.Control(WriteCacheAction.Disable);
            Device.Control(WriteCacheAction.Release);
            var state = Device.GetWriteCacheState();
            if (state.BudgetBytes != 0 || state.DirtyBytes != 0 || state.LastError != 0)
                throw new IOException($"{Target.Device} ({stage}): the cache did not release cleanly.");
            foreach (var region in Regions)
            {
                using var file = new AlignedFile(region.Path, region.Region.BlockBytes, create: false, sharedReadOnly: true);
                var buffer = new byte[region.Region.BlockBytes];
                for (var block = 0; block < region.Region.Blocks; block++)
                {
                    file.Read((long)block * buffer.Length, buffer);
                    var problem = StressPattern.Check(buffer, region.Seed, block, region.Versions.Done(block), region.Versions.Started(block));
                    if (problem is not null) throw new InvalidDataException($"{Target.Device} ({stage}) disk: {problem}.");
                }
            }
        }

        public void RequireCoverage()
        {
            lock (actions)
                foreach (var action in Enum.GetValues<StressAction>())
                    if (actions.GetValueOrDefault(action) == 0)
                        throw new IOException($"{Target.Device}: the busy window never performed {action}; extend --soak-seconds.");
            if (Regions.Any(r => Interlocked.Read(ref r.Writes) == 0 || Interlocked.Read(ref r.Reads) == 0))
                throw new IOException($"{Target.Device}: a region was never written or read during the busy window.");
            if (Interlocked.Read(ref Regions.Single(r => r.Region.Overlapped).Cancelled) == 0)
                throw new IOException($"{Target.Device}: no overlapped request was cancelled.");
        }

        public object Summary()
        {
            lock (actions)
                return new
                {
                    Target.Device, Actions = actions.ToDictionary(p => p.Key.ToString(), p => p.Value), VerifiedReads = Interlocked.Read(ref verifiedReads),
                    Regions = Regions.Select(r => new { r.Region.Name, Writes = Interlocked.Read(ref r.Writes), Reads = Interlocked.Read(ref r.Reads),
                        Cancelled = Interlocked.Read(ref r.Cancelled) })
                };
        }

        public string Describe()
        {
            lock (actions)
                return $"{Target.Device} {Regions.Sum(r => Interlocked.Read(ref r.Writes))} writes, {Regions.Sum(r => Interlocked.Read(ref r.Reads))} verified reads, " +
                    $"{Regions.Sum(r => Interlocked.Read(ref r.Cancelled))} cancelled overlapped requests, {actions.Values.Sum()} chaos actions " +
                    $"({string.Join(", ", actions.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value}"))})";
        }
    }

    /// <summary>Owned 2 GiB RAM disks created, written, verified and removed in a loop, reserving from the
    /// same shared memory as the caches. Every definition is journaled before it is created.</summary>
    private sealed class RamChurn(string ownership)
    {
        public int Cycles;
        public long VerifiedBytes;
        public void Loop(CancellationToken token)
        {
            var service = new ManagedDiskBrokerClient();
            while (!token.IsCancellationRequested)
            {
                var definition = ManagedDiskDefinition.New(ManagedDiskMode.EphemeralRam) with
                {
                    CapacityBytes = RamReadReferencePlan.DiskMiB * (1UL << 20), PreferredLetter = FreeLetter(),
                    Label = "QC-Stress-" + Guid.NewGuid().ToString("N")[..8]
                };
                var journal = JsonSerializer.Deserialize<RamReadReferenceOwnership>(File.ReadAllText(ownership))!;
                RunStorage.AtomicJson(ownership, journal with { Definitions = [.. journal.Definitions, definition] });
                service.CreateAsync(definition, null).GetAwaiter().GetResult();
                var record = service.ListAsync().GetAwaiter().GetResult().Single(r => r.ResourceId == definition.ResourceId);
                if (record.Runtime?.State != ManagedDiskState.Ready) throw new IOException("Owned stress RAM disk did not become Ready.");
                var file = definition.PreferredLetter + @":\stress.dat";
                var expected = RamReadFileOracle.Prepare(file, 64);
                if (RamReadFileOracle.Verify(file, 64) != expected) throw new InvalidDataException("Owned stress RAM disk lost bytes.");
                Interlocked.Add(ref VerifiedBytes, 64L << 20);
                // The writes advanced the disk's generation: stop exactly the current one.
                record = service.ListAsync().GetAwaiter().GetResult().Single(r => r.ResourceId == definition.ResourceId);
                if (record.Runtime?.State != ManagedDiskState.Ready) throw new IOException("Owned stress RAM disk left Ready before its stop.");
                service.ExecuteAsync(new(record.ResourceId, ManagedDiskAction.Stop, ManagedDiskExpected.From(record.Runtime),
                    ManagedDiskStopIntent.DiscardThenStop, AcceptDiscard: true), null).GetAwaiter().GetResult();
                record = service.ListAsync().GetAwaiter().GetResult().Single(r => r.ResourceId == definition.ResourceId);
                service.ExecuteAsync(new(record.ResourceId, ManagedDiskAction.RemoveDefinition,
                    record.Runtime is null ? null : ManagedDiskExpected.From(record.Runtime)), null).GetAwaiter().GetResult();
                Interlocked.Increment(ref Cycles);
            }
        }
        private static char FreeLetter() => Enumerable.Range('G', 'Z' - 'G' + 1).Select(n => (char)n)
            .FirstOrDefault(c => !Directory.Exists(c + @":\") && DriveInfo.GetDrives().All(d => char.ToUpperInvariant(d.Name[0]) != c)) is var letter && letter != '\0'
            ? letter : throw new IOException("No free drive letter for an owned stress RAM disk.");
    }

    private static void FlushVolume(DiskTarget target)
    {
        using var handle = File.OpenHandle(@"\\.\" + target.Device, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        if (!FlushFileBuffers(handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle handle);

    /// <summary>Page-aligned native memory for unbuffered overlapped I/O.</summary>
    private sealed unsafe class AlignedMemory : System.Buffers.MemoryManager<byte>
    {
        private readonly byte* pointer; private readonly int length;
        public AlignedMemory(int length) { this.length = length; pointer = (byte*)NativeMemory.AlignedAlloc((nuint)length, 4096); }
        public override Span<byte> GetSpan() => new(pointer, length);
        public override System.Buffers.MemoryHandle Pin(int elementIndex = 0) => new(pointer + elementIndex);
        public override void Unpin() { }
        protected override void Dispose(bool disposing) => NativeMemory.AlignedFree(pointer);
    }
}
