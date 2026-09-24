using System.Buffers.Binary;
using QueueCache.Developer.Verification;
using QueueCache.Management;
using QueueCache.Operations;

if (OperatingSystem.IsWindows() && args.Length == 4 && args[0] == "--fake-verification" && args[2] == "--verification-worker")
{
    Environment.ExitCode = await VerificationRunnerTests.FakeWorkerAsync(args[1], args[3]);
    return;
}

// Isolated fake child for runner contract tests; never opens a disk/driver.
if (args.Length > 0 && args[0] == "--runner-child")
{
    if (args[1] == "hang")
        await Task.Delay(Timeout.Infinite);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(args.Skip(1)));
    Console.Error.WriteLine("captured stderr");
    return;
}
if (OperatingSystem.IsWindows()) await VerificationRunnerTests.RunAsync();

// Dependency-free protocol regression checks. No driver or disk writes required.
var perfWire = new byte[CachePerformance.WireSize];
BinaryPrimitives.WriteUInt32LittleEndian(perfWire, 3);
BinaryPrimitives.WriteUInt32LittleEndian(perfWire.AsSpan(4), CachePerformance.WireSize);
BinaryPrimitives.WriteUInt64LittleEndian(perfWire.AsSpan(8), 10_000_000);
BinaryPrimitives.WriteUInt64LittleEndian(perfWire.AsSpan(8 + 8 * 8), 2);
BinaryPrimitives.WriteUInt64LittleEndian(perfWire.AsSpan(8 + 8 * 50), 101);
BinaryPrimitives.WriteUInt64LittleEndian(perfWire.AsSpan(8 + 8 * 51), 102);
BinaryPrimitives.WriteUInt64LittleEndian(perfWire.AsSpan(8 + 8 * 52), 103);
var perf = CachePerformance.Decode(perfWire);
Check(perf.PhaseName == "Waiting for write capacity" && perf.Milliseconds(10_000) == 1, "performance wire phase and QPC conversion");
Check(perf.DrainSelectionTicks == 101 && perf.DrainCopyTicks == 102 && perf.DrainRetirementTicks == 103, "performance v3 drain phase timings");
var v2PerfWire = perfWire[..CachePerformance.V2WireSize];
BinaryPrimitives.WriteUInt32LittleEndian(v2PerfWire, 2);
BinaryPrimitives.WriteUInt32LittleEndian(v2PerfWire.AsSpan(4), CachePerformance.V2WireSize);
Check(CachePerformance.Decode(v2PerfWire).DrainSelectionTicks == 0, "performance v2 compatibility defaults drain phase timings");
var legacyPerfWire = perfWire[..CachePerformance.LegacyWireSize];
BinaryPrimitives.WriteUInt32LittleEndian(legacyPerfWire, 1);
BinaryPrimitives.WriteUInt32LittleEndian(legacyPerfWire.AsSpan(4), CachePerformance.LegacyWireSize);
Check(CachePerformance.Decode(legacyPerfWire).ServiceReadCalls == 0, "performance v1 compatibility defaults appended counters");
Reject(() => CachePerformance.Decode(perfWire[..^1]), "reject truncated performance response");
BinaryPrimitives.WriteUInt64LittleEndian(perfWire.AsSpan(8), 0);
Reject(() => CachePerformance.Decode(perfWire), "reject zero performance frequency");
var data = new byte[184];
BinaryPrimitives.WriteUInt32LittleEndian(data, 184);
data[4] = 1;
BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), unchecked((int)0xC000009A));
BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), 200L << 30);
BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(104), 9L << 30);
BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(136), 3L << 30);
BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(152), 2);
BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(176), 4L << 30);
var s = CacheStatistics.Decode(data);
var alphaDefault = new CacheConfiguration();
alphaDefault.Validate(true);
Check(alphaDefault.Preset == CachePreset.Fast && alphaDefault.Enabled && alphaDefault.Options == new CacheOptions(
    Drain: DrainAlgorithm.Idle, LowPercent: 40, HighPercent: 80, MaxDirtyAgeMs: 5000,
    IdleMs: 250, BatchKiB: 256, Parallelism: 1), "new task uses explicit Fast/Idle alpha defaults");
foreach (var allocation in Enum.GetValues<CacheAllocation>())
    foreach (var algorithm in Enum.GetValues<DrainAlgorithm>())
        foreach (var share in new[] { 0, 50, 100 })
        {
            var options = new CacheOptions(allocation, share, Drain: algorithm, Parallelism: 4);
            Check(CacheOptions.Decode(options.Encode()) == options, "policy wire roundtrip");
        }
Reject(() => new CacheOptions(WritePercent: 101).Validate(), "invalid share");
Reject(() => new CacheOptions(LowPercent: 90, HighPercent: 80).Validate(), "inverted watermarks");
Reject(() => new CacheOptions(BatchKiB: 7).Validate(), "unaligned batch");
Reject(() => new CacheOptions(Parallelism: 5).Validate(), "unbounded parallelism");
Reject(() => new CacheOptions(MaxDirtyAgeMs: 0).Validate(), "invalid age");
var deferredHour = new CacheOptions(Drain: DrainAlgorithm.Deferred, MaxDirtyAgeMs: 3600000);
Check(CacheOptions.Decode(deferredHour.Encode()) == deferredHour, "deferred one-hour wire roundtrip");
Reject(() => new CacheOptions(MaxDirtyAgeMs: 3600001).Validate(), "age above one hour");
var rw = new byte[WriteCacheState.ReadWriteWireSize];
BinaryPrimitives.WriteUInt32LittleEndian(rw, 3);
BinaryPrimitives.WriteUInt32LittleEndian(rw.AsSpan(4), 288);
BinaryPrimitives.WriteUInt32LittleEndian(rw.AsSpan(8), 256 | 512 | 1);
BinaryPrimitives.WriteUInt64LittleEndian(rw.AsSpan(24), 8192UL << 20);
BinaryPrimitives.WriteUInt64LittleEndian(rw.AsSpan(32), 8192UL << 20);
BinaryPrimitives.WriteUInt64LittleEndian(rw.AsSpan(56), 8000UL << 20);
new CacheOptions().Encode().CopyTo(rw, 160);
BinaryPrimitives.WriteUInt64LittleEndian(rw.AsSpan(264), 42);
var confirmed = WriteCacheState.DecodeReadWrite(rw);
Check(confirmed.Operational && confirmed.RuntimeStatus == "Active", "live routing plus allocation confirms Active");
Check(!(confirmed with { Flags = 257 }).Operational, "enabled without routing cannot report Active");
Check(!(confirmed with { ReservedBytes = 0 }).Operational, "enabled without RAM cannot report Active");
Check(!(confirmed with { LastError = -1 }).Operational, "enabled but faulted cannot report Active");
Check(CacheConfiguration.FromState(confirmed).Options == confirmed.Options, "pause/resume preserves policies");
Reject(() => WriteCacheState.DecodeReadWrite(rw.AsSpan(0, 287)), "short read/write state");
BinaryPrimitives.WriteUInt64LittleEndian(rw.AsSpan(208), 9000UL << 20);
Reject(() => WriteCacheState.DecodeReadWrite(rw), "clean plus dirty exceeds payload");
new CacheConfiguration(64, CachePreset.Strict).Validate(false);
Reject(() => new CacheConfiguration().Validate(false), "fast preset requires risk acceptance");
Reject(() => new CacheConfiguration(0).Validate(true), "zero configuration budget");
new CacheConfiguration(8192).Validate(true);
Reject(() => new CacheConfiguration(131073).Validate(true), "oversized configuration budget");
Reject(() => new CacheConfiguration(64, (CachePreset)99).Validate(true), "unknown preset");
Check(MemoryBudget.RequiredSystemHeadroom(8UL << 30) == 2UL << 30, "8 GiB host keeps 2 GiB application/OS headroom");
Check(MemoryBudget.RequiredSystemHeadroom(32UL << 30) == 8UL << 30, "larger host keeps 25% application/OS headroom");
var profile = new SavedConfiguration(1, "Q:", "test-device-identity", 200L << 30, new(), true);
var existingProfile = new SavedConfiguration(1, "Q:", "test-device-identity", 200L << 30,
    new CacheConfiguration(2048, CachePreset.Strict, false) { Options = new(Drain: DrainAlgorithm.Eager, Parallelism: 4) }, false);
var existingJson = System.Text.Json.JsonSerializer.Serialize(existingProfile);
var existingRoundTrip = System.Text.Json.JsonSerializer.Deserialize<SavedConfiguration>(existingJson)!;
existingRoundTrip.Validate();
Check(existingRoundTrip == existingProfile && existingRoundTrip.Configuration.Options.Drain == DrainAlgorithm.Eager,
    "saved Strict/Eager profile survives new defaults exactly");
var diskLabel = new DiskDescription(1, "Test disk", 200L << 30, "test", ["Q:"], false, false).Display;
Check(diskLabel.Contains("Q:") && diskLabel.Contains("PhysicalDrive1") && diskLabel.Contains("200 GiB"), "disk label contains volume, physical drive and human-readable capacity");
Check(new DiskDescription(0, "Boot", 100L << 30, "boot", ["C:"], true, true).Display.Contains("[boot/system]"), "boot disk is labelled, not hidden");
Check(new DiskDescription(0, "Paging", 100L << 30, "paging", ["C:"], false, false, true).Display.Contains("[paging]"), "paging disk is labelled, not hidden");
ActivationSafety.ValidateTarget(0);
ActivationSafety.ValidateTarget(1);
Reject(() => ActivationSafety.ValidateTarget(-1), "invalid usage-path count");
profile.Validate();
Reject(() => (profile with { Version = 2 }).Validate(), "unknown profile version");
Reject(() => (profile with { Volume = @"Q:\folder" }).Validate(), "profile requires volume not path");
Reject(() => (profile with { Instance = "" }).Validate(), "profile requires disk identity");
Reject(() => (profile with { Bytes = 0 }).Validate(), "profile requires disk size");
Reject(() => (profile with { Configuration = null! }).Validate(), "profile requires configuration");
Reject(() => (profile with { VolatileFlushAccepted = false }).Validate(), "saved fast profile requires acknowledgement");
Check(s.Enabled && s.LastError == unchecked((int)0xC000009A), "flags and signed NTSTATUS");
Check(s.DeviceBytes == 200L << 30 && s.WrittenBytes == 9L << 30, "64-bit byte counters");
Check(s.QueueMemoryBytes == 3L << 30 && s.MaxQueueBytes == 4L << 30, "cache budgets above 2 GiB");
Check(s.PagingPathCount == 2, "paging/hibernation/dump path count");
Check((uint)WriteCacheAction.EnablePaging == 12, "legacy paging enable control ABI remains compatible");
Reject(() => CacheStatistics.Decode(data.AsSpan(0, 183)), "short response");
data[0] = 0;
Reject(() => CacheStatistics.Decode(data), "incompatible version");
Check(DevicePath.Normalize("D:") == @"\\.\D:", "volume normalization");
Check(DevicePath.Normalize(@"\\.\PhysicalDrive1") == @"\\.\PhysicalDrive1", "disk normalization");
foreach (var invalid in new[] { @"D:\file.bin", @"\\server\share", "PhysicalDrive-1", "D:\\", "PhysicalDrive1\n" })
    Reject(() => DevicePath.Normalize(invalid), "reject non-device path " + invalid);
Console.WriteLine("All protocol/path regression checks passed.");
var cacheData = new byte[WriteCacheState.WireSize];
BinaryPrimitives.WriteUInt32LittleEndian(cacheData, 1);
BinaryPrimitives.WriteUInt32LittleEndian(cacheData.AsSpan(4), 128);
BinaryPrimitives.WriteUInt32LittleEndian(cacheData.AsSpan(8), 3);
BinaryPrimitives.WriteInt32LittleEndian(cacheData.AsSpan(12), unchecked((int)0xC0000185));
ulong[] values = [200UL << 30, 4UL << 30, 3UL << 30, 1UL << 30, 4096, 2UL << 30, 123, 9UL << 30, 8UL << 30, 19, 123456, 2, 7, 1UL << 30];
for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt64LittleEndian(cacheData.AsSpan(16 + i * 8), values[i]);
var writeState = WriteCacheState.Decode(cacheData);
Check(writeState.Enabled && writeState.Faulted && writeState.LastError == unchecked((int)0xC0000185), "write-cache flags and error ABI");
Check(writeState.BudgetBytes == 4UL << 30 && writeState.AcceptedBytes == 9UL << 30 && writeState.Flushes == 7 && writeState.ThrottleWaits == 19, "write-cache 64-bit offsets");
Reject(() => WriteCacheState.Decode(cacheData.AsSpan(0, 127)), "short write-cache snapshot");
cacheData[0] = 2;
Reject(() => WriteCacheState.Decode(cacheData), "write-cache version");
cacheData[0] = 1;
BinaryPrimitives.WriteUInt64LittleEndian(cacheData.AsSpan(32), 5UL << 30);
Reject(() => WriteCacheState.Decode(cacheData), "cache reserved exceeds budget");
Console.WriteLine("Write-cache ABI and bounds regression checks passed.");
var nextState = writeState with { AcceptedBytes = writeState.AcceptedBytes + (4UL << 20), DrainedBytes = writeState.DrainedBytes + (2UL << 20), ThrottleWaits = writeState.ThrottleWaits + 3 };
var rates = CacheTelemetry.Between(writeState, nextState, TimeSpan.FromSeconds(2));
Check(rates.AcceptedMiBPerSecond == 2 && rates.DrainedMiBPerSecond == 1 && rates.NewThrottleWaits == 3 && rates.FillFraction == 0.5, "telemetry rates and fill");
var resetRates = CacheTelemetry.Between(nextState, writeState, TimeSpan.FromSeconds(1));
Check(resetRates.CountersReset && resetRates.AcceptedMiBPerSecond == 0 && resetRates.DrainedMiBPerSecond == 0, "counter reset avoids unsigned underflow");
Reject(() => CacheTelemetry.Between(writeState, nextState, TimeSpan.Zero), "zero sample interval");
Console.WriteLine("Telemetry regression checks passed.");
var readRates = CacheTelemetry.Between(writeState, writeState with { ReadHitBytes = 3UL << 20, ReadMissBytes = 1UL << 20 }, TimeSpan.FromSeconds(2));
Check(readRates.ReadMiBPerSecond == 2, "read graph includes RAM hits and lower-device misses");
Check(CacheTelemetry.Between(writeState with { ReadHitBytes = 1 }, writeState, TimeSpan.FromSeconds(1)).CountersReset, "read counter reset avoids underflow");
Check(writeState.CoalescedBytes == 0, "legacy accepted/drained/dirty conservation");
Check((writeState with { AcceptedBytes = writeState.AcceptedBytes + (2UL << 30) }).CoalescedBytes == 2UL << 30, "coalesced bytes are not drained bytes");
Check((writeState with { AcceptedBytes = 0 }).CoalescedBytes == 0, "coalesced counter reset avoids underflow");
Check((writeState with { AcceptedBytes = writeState.AcceptedBytes + (2UL << 30), DiscardedBytes = 1UL << 30 }).CoalescedBytes == 1UL << 30, "discarded bytes are not coalesced bytes");
var extendedData = new byte[WriteCacheState.ExtendedWireSize];
data = new byte[WriteCacheState.WireSize];
BinaryPrimitives.WriteUInt32LittleEndian(extendedData, 2);
BinaryPrimitives.WriteUInt32LittleEndian(extendedData.AsSpan(4), 160);
BinaryPrimitives.WriteUInt64LittleEndian(extendedData.AsSpan(128), 4096);
var extendedState = WriteCacheState.DecodeExtended(extendedData);
Check(extendedState.ExtendedCountersAvailable && extendedState.DiscardedBytes == 4096, "extended snapshot counters");
Reject(() => WriteCacheState.DecodeExtended(extendedData.AsSpan(0, 159)), "short extended snapshot");
Check(!(writeState with { Flags = 0 }).UnsafeDefer && (writeState with { Flags = 32 }).FlushPolicy == "UNSAFE-DEFER", "flush policy flag");
var diagnosticsBytes = new byte[CacheDiagnostics.WireSize];
BinaryPrimitives.WriteUInt32LittleEndian(diagnosticsBytes, 1);
BinaryPrimitives.WriteUInt32LittleEndian(diagnosticsBytes.AsSpan(4), CacheDiagnostics.WireSize);
ulong[] diagnosticValues = [9, 4, 7, 3, 2, 8, 1, 6, 0x2d4804];
for (var i = 0; i < diagnosticValues.Length; i++) BinaryPrimitives.WriteUInt64LittleEndian(diagnosticsBytes.AsSpan(8 + i * 8), diagnosticValues[i]);
var diagnostics = CacheDiagnostics.Decode(diagnosticsBytes);
Check(diagnostics.DeferredFlushes == 4 && diagnostics.DeferredWriteThroughWrites == 3 && diagnostics.LastBarrierCode == 0x2d4804, "diagnostics ABI offsets");
Check(diagnostics.Attribution is null, "V1 attribution is unavailable, not zero");
var attributionBytes = new byte[CacheDiagnostics.AttributionWireSize];
diagnosticsBytes.CopyTo(attributionBytes, 0);
BinaryPrimitives.WriteUInt32LittleEndian(attributionBytes, 2);
BinaryPrimitives.WriteUInt32LittleEndian(attributionBytes.AsSpan(4), CacheDiagnostics.AttributionWireSize);
ulong[] attributionValues = [11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 4, 4, 0, 512, 1536];
for (var index = 0; index < attributionValues.Length; index++)
    BinaryPrimitives.WriteUInt64LittleEndian(attributionBytes.AsSpan(CacheDiagnostics.WireSize + index * 8), attributionValues[index]);
Check(CacheDiagnostics.Decode(attributionBytes).Attribution ==
    new CacheAttribution(11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 4, 4, 0, 512, 1536), "V2 attribution offsets");
var usageBytes = new byte[CacheDiagnostics.UsageWireSize];
attributionBytes.CopyTo(usageBytes, 0);
BinaryPrimitives.WriteUInt32LittleEndian(usageBytes, 3);
BinaryPrimitives.WriteUInt32LittleEndian(usageBytes.AsSpan(4), CacheDiagnostics.UsageWireSize);
BinaryPrimitives.WriteUInt64LittleEndian(usageBytes.AsSpan(216), 2);
BinaryPrimitives.WriteUInt64LittleEndian(usageBytes.AsSpan(224), 3);
BinaryPrimitives.WriteUInt64LittleEndian(usageBytes.AsSpan(232), 4);
Check(CacheDiagnostics.Decode(usageBytes).UsagePaths == new CacheUsagePaths(2, 3, 4),
    "V3 paging/hibernation/dump usage-path offsets");
Check(CacheDiagnostics.Decode(usageBytes).UsageActivity is null,
    "V3 usage lifecycle is unavailable, not zero");
var activityBytes = new byte[CacheDiagnostics.UsageActivityWireSize];
usageBytes.CopyTo(activityBytes, 0);
BinaryPrimitives.WriteUInt32LittleEndian(activityBytes, 4);
BinaryPrimitives.WriteUInt32LittleEndian(activityBytes.AsSpan(4), CacheDiagnostics.UsageActivityWireSize);
for (var group = 0; group < 7; group++)
for (var type = 0; type < 3; type++)
    BinaryPrimitives.WriteUInt64LittleEndian(activityBytes.AsSpan(240 + group * 24 + type * 8),
        (ulong)(group * 10 + type + 1));
var activity = CacheDiagnostics.Decode(activityBytes);
Check(activity.UsagePaths == new CacheUsagePaths(2, 3, 4) && activity.UsageActivity?.Paging ==
    new CacheUsageActivity(1, 11, 21, 31, 41, 51, 61) && activity.UsageActivity.Dump ==
    new CacheUsageActivity(3, 13, 23, 33, 43, 53, 63),
    "V4 usage lifecycle offsets and V3 prefix");
Check(activity.PagingIo is null, "V4 paging I/O activity is unavailable, not zero");
var pagingIoBytes = new byte[CacheDiagnostics.PagingIoWireSize];
activityBytes.CopyTo(pagingIoBytes, 0);
BinaryPrimitives.WriteUInt32LittleEndian(pagingIoBytes, 5);
BinaryPrimitives.WriteUInt32LittleEndian(pagingIoBytes.AsSpan(4), CacheDiagnostics.PagingIoWireSize);
ulong[] pagingIoValues = [71, 72, 73, 74, 4, 0x43, 77, 78, 79];
for (var index = 0; index < pagingIoValues.Length; index++)
    BinaryPrimitives.WriteUInt64LittleEndian(pagingIoBytes.AsSpan(CacheDiagnostics.UsageActivityWireSize + index * 8),
        pagingIoValues[index]);
var pagingIo = CacheDiagnostics.Decode(pagingIoBytes);
Check(pagingIo.UsageActivity == activity.UsageActivity && pagingIo.PagingIo ==
    new CachePagingIo(71, 72, 73, 74, 4, 0x43, 77, 78, 79),
    "V5 paging I/O offsets and V4 prefix");
Check(pagingIo.PagingProgress is null, "V5 paging progress is unavailable, not zero");
var pagingProgressBytes = new byte[CacheDiagnostics.PagingProgressWireSize];
pagingIoBytes.CopyTo(pagingProgressBytes, 0);
BinaryPrimitives.WriteUInt32LittleEndian(pagingProgressBytes, 6);
BinaryPrimitives.WriteUInt32LittleEndian(pagingProgressBytes.AsSpan(4), CacheDiagnostics.PagingProgressWireSize);
ulong[] pagingProgressValues = [81, 82, 83, 84, 85, 86];
for (var index = 0; index < pagingProgressValues.Length; index++)
    BinaryPrimitives.WriteUInt64LittleEndian(pagingProgressBytes.AsSpan(CacheDiagnostics.PagingIoWireSize + index * 8),
        pagingProgressValues[index]);
var pagingProgress = CacheDiagnostics.Decode(pagingProgressBytes);
Check(pagingProgress.PagingIo == pagingIo.PagingIo && pagingProgress.PagingProgress ==
    new CachePagingProgress(81, 82, 83, 84, 85, 86),
    "V6 paging progress offsets and V5 prefix");
var pagingRouteBytes = new byte[CacheDiagnostics.PagingRouteWireSize];
pagingProgressBytes.CopyTo(pagingRouteBytes, 0);
BinaryPrimitives.WriteUInt32LittleEndian(pagingRouteBytes, 7);
BinaryPrimitives.WriteUInt32LittleEndian(pagingRouteBytes.AsSpan(4), CacheDiagnostics.PagingRouteWireSize);
ulong[] pagingRouteValues = [11, 10, 1, 22, 21, 1, 3];
for (var index = 0; index < pagingRouteValues.Length; index++)
    BinaryPrimitives.WriteUInt64LittleEndian(pagingRouteBytes.AsSpan(CacheDiagnostics.PagingProgressWireSize + index * 8),
        pagingRouteValues[index]);
var pagingRoute = CacheDiagnostics.Decode(pagingRouteBytes);
Check(pagingRoute.PagingProgress == pagingProgress.PagingProgress && pagingRoute.PagingRoute ==
    new CachePagingRoute(11, 10, 1, 22, 21, 1, 3), "V7 routed paging offsets and V6 prefix");
Reject(() => CacheDiagnostics.Decode(pagingProgressBytes.AsSpan(0, 527)), "short paging progress diagnostics");
Reject(() => CacheDiagnostics.Decode(pagingIoBytes.AsSpan(0, 479)), "short paging I/O diagnostics");
Reject(() => CacheDiagnostics.Decode(activityBytes.AsSpan(0, 407)), "short usage lifecycle");
Reject(() => CacheDiagnostics.Decode(attributionBytes.AsSpan(0, 215)), "short attribution");
attributionBytes[0] = 1;
Reject(() => CacheDiagnostics.Decode(attributionBytes), "V1 cannot claim V2 length");
attributionBytes[0] = 2;
BinaryPrimitives.WriteUInt64LittleEndian(attributionBytes.AsSpan(176), 10);
Reject(() => CacheDiagnostics.Decode(attributionBytes), "unknown attribution reason");
Reject(() => CacheDiagnostics.Decode(diagnosticsBytes.AsSpan(0, 79)), "short diagnostics");
diagnosticsBytes[0] = 2;
Reject(() => CacheDiagnostics.Decode(diagnosticsBytes), "diagnostics version");
diagnosticsBytes[0] = 1;
BinaryPrimitives.WriteUInt64LittleEndian(diagnosticsBytes.AsSpan(16), 10);
Reject(() => CacheDiagnostics.Decode(diagnosticsBytes), "deferred count exceeds requests");
Console.WriteLine("Flush-policy/diagnostics protocol regression checks passed.");
if (OperatingSystem.IsWindows())
{
    Check(DeviceFilters.Plan(["one", "two"], true).SequenceEqual(["one", "two", "qcachelab"]), "append lab filter");
    Check(DeviceFilters.Plan(["one", "QCACHELAB", "two"], true).SequenceEqual(["one", "QCACHELAB", "two"]), "idempotent registration");
    Check(DeviceFilters.Plan(["one", "QCACHELAB", "two"], false).SequenceEqual(["one", "two"]), "remove only lab filter");
    Check(DeviceFilters.Plan([], false).Length == 0, "empty removal");
    Console.WriteLine("Filter-list regression checks passed (no device changes).");
}

static void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); }
static void Reject(Action action, string label)
{
    try
    {
        action();
    }
    catch (Exception e) when (e is InvalidDataException or ArgumentException) { return; }
    throw new Exception("FAIL: " + label);
}
