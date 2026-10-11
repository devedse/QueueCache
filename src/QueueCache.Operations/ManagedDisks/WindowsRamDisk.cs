using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using QueueCache.Management;

namespace QueueCache.Operations.ManagedDisks;

public sealed record RamDiskAllocationFailureProof(Guid BootEpoch, Guid ResourceId, ulong CompletedInjections, ulong AllocatedSlabs);

/// <summary>Bounded control of the installed in-tree provider; handles do not own RAM lifetime.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRamDisk : IDisposable
{
    private readonly SafeFileHandle handle;
    public int PortNumber { get; }
    private WindowsRamDisk(SafeFileHandle handle, int port) { this.handle = handle; PortNumber = port; }

    public static WindowsRamDisk Connect()
    {
        // The adapter is authenticated by the exact versioned service protocol.
        // Disk handles and volume IOCTLs are never used for service discovery.
        for (var port = 0; port < 64; ++port)
        {
            var handle = CreateFileW($"\\\\.\\Scsi{port}:", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); continue; }
            var adapter = new WindowsRamDisk(handle, port);
            try { adapter.Capabilities(); return adapter; }
            catch (Win32Exception) { adapter.Dispose(); }
            catch { adapter.Dispose(); throw; }
        }
        throw new IOException("The QueueCache RAM disk provider is unavailable. Install the matching package and restart Windows.");
    }

    public RamDiskSnapshot Capabilities() => Send(RamDiskSnapshot.Request(RamDiskAction.Capabilities), capabilities: true);
    /// <summary>Native proof of request-local developer failures; no persistent fault setting.</summary>
    public RamDiskAllocationFailureProof AllocationFailureProof()
    { var capabilities = Capabilities(); return new(capabilities.BootEpoch, capabilities.ResourceId, capabilities.WriteGeneration, capabilities.Transfers); }
    /// <summary>Only the named new creation fails, after actual allocated slabs; existing resources are untouched.</summary>
    public void CreateWithAllocationFailure(Guid resource, ulong capacity, uint sector, uint afterSlabs)
    {
        var wire = RamDiskSnapshot.Request(RamDiskAction.DeveloperCreateAllocationFailure, resource: resource,
            capacity: capacity, sector: sector, offset: afterSlabs);
        Call(wire, wire.Length, wire.Length);
        throw new IOException("The requested native allocation failure unexpectedly succeeded.");
    }
    /// <summary>Authoritative qcache cold/hybrid startup epoch; sleep/hibernate and service restarts do not advance it.</summary>
    public RamDiskSnapshot StartupSession() => Send(RamDiskSnapshot.Request(RamDiskAction.StartupSession), capabilities: true);
    public IReadOnlyList<RamDiskSnapshot> Enumerate()
    {
        var disks = new List<RamDiskSnapshot>();
        for (uint slot = 0; slot < RamDiskSnapshot.MaximumDisks; ++slot)
        {
            try { disks.Add(Send(RamDiskSnapshot.Request(RamDiskAction.Enumerate, slot: slot))); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1168) { /* Explicit STATUS_NOT_FOUND. */ }
        }
        return disks;
    }
    public RamDiskSnapshot Create(Guid resource, ulong capacity, uint sector, bool direct = false) =>
        Send(RamDiskSnapshot.Request(RamDiskAction.Create, resource: resource, capacity: capacity, sector: sector,
            flags: direct ? RamDiskFlags.Direct : RamDiskFlags.None));
    public RamDiskSnapshot Query(RamDiskSnapshot expected) => Send(RamDiskSnapshot.Request(RamDiskAction.Query, expected));
    public RamDiskSnapshot Publish(RamDiskSnapshot expected) => Send(RamDiskSnapshot.Request(RamDiskAction.Publish, expected));
    public RamDiskSnapshot Freeze(RamDiskSnapshot expected, Guid operation) => Send(RamDiskSnapshot.Request(RamDiskAction.Freeze, expected, freezeOwner: operation));
    public RamDiskSnapshot Thaw(RamDiskSnapshot expected, Guid operation) => Send(RamDiskSnapshot.Request(RamDiskAction.Thaw, expected, freezeOwner: operation));
    public RamDiskSnapshot SetReadOnly(RamDiskSnapshot expected, bool readOnly) =>
        Send(RamDiskSnapshot.Request(RamDiskAction.SetReadOnly, expected, flags: readOnly ? RamDiskFlags.ReadOnly : RamDiskFlags.None));
    /// <summary>Null when the installed provider predates statistics.</summary>
    public RamDiskStatistics? Statistics(RamDiskSnapshot expected)
    {
        if (!(statisticsSupported ??= Capabilities().Flags.HasFlag(RamDiskFlags.StatisticsSupported)))
            return null;
        var wire = RamDiskSnapshot.Request(RamDiskAction.Statistics, expected);
        Array.Resize(ref wire, RamDiskSnapshot.WireSize + RamDiskStatistics.WireSize);
        var returned = Call(wire, RamDiskSnapshot.WireSize, wire.Length);
        if (returned != wire.Length) throw new InvalidDataException("Incomplete RAM disk statistics reply.");
        RamDiskSnapshot.Decode(wire).RequireSameCreation(expected);
        return RamDiskStatistics.Decode(wire.AsSpan(RamDiskSnapshot.WireSize));
    }
    private bool? statisticsSupported;
    /// <summary>Null when the installed provider predates the physical map.</summary>
    public RamPhysicalMap? PhysicalMap(RamDiskSnapshot expected, ulong spanPages)
    {
        if (!(physicalMapSupported ??= Capabilities().Flags.HasFlag(RamDiskFlags.PhysicalMapSupported)))
            return null;
        var wire = RamDiskSnapshot.Request(RamDiskAction.PhysicalMap, expected, offset: spanPages);
        Array.Resize(ref wire, RamDiskSnapshot.WireSize + RamPhysicalMap.WireSize);
        var returned = Call(wire, RamDiskSnapshot.WireSize, wire.Length);
        if (returned != wire.Length) throw new InvalidDataException("Incomplete RAM disk physical map reply.");
        RamDiskSnapshot.Decode(wire).RequireSameCreation(expected);
        return RamPhysicalMap.Decode(wire.AsSpan(RamDiskSnapshot.WireSize));
    }
    private bool? physicalMapSupported;
    public RamDiskSnapshot SetTiming(RamDiskSnapshot expected, bool enabled) =>
        Send(RamDiskSnapshot.Request(RamDiskAction.SetTiming, expected, flags: enabled ? RamDiskFlags.Timing : RamDiskFlags.None));
    /// <summary>Null only for the explicit unsupported-action response of an older provider.</summary>
    public RamCoordination? Coordination(RamDiskSnapshot expected)
    {
        var wire = RamDiskSnapshot.Request(RamDiskAction.Coordination, expected);
        Array.Resize(ref wire, RamDiskSnapshot.WireSize + RamCoordination.WireSize);
        int returned;
        try { returned = Call(wire, RamDiskSnapshot.WireSize, wire.Length); }
        // Earlier providers reject unknown actions with INVALID_PARAMETER (87).
        // This request has a fixed validated shape; identity/transport faults are
        // distinct errors and must propagate rather than become missing counters.
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 1 or 50 or 87) { return null; }
        if (returned != wire.Length) throw new InvalidDataException("Incomplete RAM coordination reply.");
        RamDiskSnapshot.Decode(wire).RequireSameCreation(expected);
        return RamCoordination.Decode(wire.AsSpan(RamDiskSnapshot.WireSize));
    }
    public RamDiskSnapshot SetCoordination(RamDiskSnapshot expected, bool enabled) =>
        Send(RamDiskSnapshot.Request(RamDiskAction.SetCoordination, expected, offset: enabled ? 1UL : 0));
    public void Remove(RamDiskSnapshot expected)
    {
        var request = RamDiskSnapshot.Request(RamDiskAction.Remove, expected);
        Call(request, RamDiskSnapshot.WireSize, request.Length);
    }

    public ILogicalDisk Storage(RamDiskSnapshot expected, Guid freezeOwner = default) => new LogicalStorage(this, expected, freezeOwner);
    private RamDiskSnapshot Send(byte[] request, bool capabilities = false)
    {
        Call(request, RamDiskSnapshot.WireSize, request.Length);
        var result = RamDiskSnapshot.Decode(request, capabilities);
        return result;
    }
    private int Call(byte[] buffer, int inputLength, int outputLength)
    {
        if (!DeviceIoControl(handle, RamDiskSnapshot.ServiceIoctl, buffer, (uint)inputLength,
                buffer, (uint)outputLength, out var returned, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, "RAM disk provider operation failed: " + new Win32Exception(error).Message);
        }
        if (returned < RamDiskSnapshot.WireSize || returned > outputLength)
            throw new InvalidDataException("Incomplete native RAM disk operation reply.");
        return checked((int)returned);
    }
    public void Dispose() => handle.Dispose();

    private sealed class LogicalStorage(WindowsRamDisk adapter, RamDiskSnapshot expected, Guid freezeOwner) : ILogicalDisk
    {
        public ulong CapacityBytes => expected.CapacityBytes;
        public uint SectorBytes => expected.SectorBytes;
        public ValueTask<int> ReadAsync(ulong offset, Memory<byte> buffer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var length = Math.Min(buffer.Length, RamDiskSnapshot.MaximumTransferBytes);
            var wire = RamDiskSnapshot.Request(RamDiskAction.Read, expected, offset: offset, transferBytes: length, freezeOwner: freezeOwner);
            var returned = adapter.Call(wire, RamDiskSnapshot.WireSize, wire.Length);
            RamDiskSnapshot.Decode(wire).RequireSameCreation(expected);
            if (returned != wire.Length) throw new EndOfStreamException("Incomplete native RAM disk transfer.");
            wire.AsMemory(RamDiskSnapshot.WireSize, length).CopyTo(buffer);
            return ValueTask.FromResult(length);
        }
        public ValueTask WriteAsync(ulong offset, ReadOnlyMemory<byte> buffer, CancellationToken token)
        {
            var written = 0;
            while (written < buffer.Length)
            {
                token.ThrowIfCancellationRequested();
                var length = Math.Min(buffer.Length - written, RamDiskSnapshot.MaximumTransferBytes);
                var wire = RamDiskSnapshot.Request(RamDiskAction.Write, expected, offset: checked(offset + (ulong)written), transferBytes: length);
                buffer.Span.Slice(written, length).CopyTo(wire.AsSpan(RamDiskSnapshot.WireSize));
                adapter.Call(wire, wire.Length, RamDiskSnapshot.WireSize);
                RamDiskSnapshot.Decode(wire).RequireSameCreation(expected);
                written += length;
            }
            return ValueTask.CompletedTask;
        }
        public ValueTask FlushAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); adapter.Query(expected).RequireSameCreation(expected);
            // Synchronous native transfer replies fence copied RAM bytes; no persistence is implied.
            return ValueTask.CompletedTask;
        }
    }
    /// <summary>Installed RAM in 4 KiB pages: the span a physical map is drawn over (address holes can push
    /// the highest page above it; the provider then widens the span).</summary>
    public static ulong InstalledMemoryPages() => GetPhysicallyInstalledSystemMemory(out var kib) ? kib / 4 : 0;
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, [In] byte[] input, uint inputLength,
        [Out] byte[] output, uint outputLength, out uint returned, IntPtr overlapped);
}
