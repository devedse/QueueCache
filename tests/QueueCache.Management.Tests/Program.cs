using System.Buffers.Binary;
using QueueCache.Management;
using QueueCache.Operations;

// Dependency-free protocol regression checks. No driver or disk writes required.
var perfWire = new byte[CachePerformance.WireSize];
BinaryPrimitives.WriteUInt32LittleEndian(perfWire, 2);
BinaryPrimitives.WriteUInt32LittleEndian(perfWire.AsSpan(4), CachePerformance.WireSize);
BinaryPrimitives.WriteUInt64LittleEndian(perfWire.AsSpan(8), 10_000_000);
BinaryPrimitives.WriteUInt64LittleEndian(perfWire.AsSpan(8 + 8 * 8), 2);
var perf = CachePerformance.Decode(perfWire);
Check(perf.PhaseName == "Waiting for write capacity" && perf.Milliseconds(10_000) == 1, "performance wire phase and QPC conversion");
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
BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(176), 4L << 30);
var s = CacheStatistics.Decode(data);
new CacheConfiguration().Validate(true);
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
var rw = new byte[WriteCacheState.ReadWriteWireSize];
BinaryPrimitives.WriteUInt32LittleEndian(rw, 3); BinaryPrimitives.WriteUInt32LittleEndian(rw.AsSpan(4), 288);
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
var profile = new SavedConfiguration(1, "Q:", "test-device-identity", 200L << 30, new(), true);
var diskLabel = new DiskDescription(1, "Test disk", 200L << 30, "test", ["Q:"], false, false).Display;
Check(diskLabel.Contains("Q:") && diskLabel.Contains("PhysicalDrive1") && diskLabel.Contains("200 GiB"), "disk label contains volume, physical drive and human-readable capacity");
Check(new DiskDescription(0, "Boot", 100L << 30, "boot", ["C:"], true, true).Display.Contains("[boot/system]"), "boot disk is labelled, not hidden");
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
cacheData[0] = 2; Reject(() => WriteCacheState.Decode(cacheData), "write-cache version"); cacheData[0] = 1;
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
Reject(() => CacheDiagnostics.Decode(diagnosticsBytes.AsSpan(0, 79)), "short diagnostics");
diagnosticsBytes[0] = 2; Reject(() => CacheDiagnostics.Decode(diagnosticsBytes), "diagnostics version"); diagnosticsBytes[0] = 1;
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
    try { action(); } catch (Exception e) when (e is InvalidDataException or ArgumentException) { return; }
    throw new Exception("FAIL: " + label);
}
