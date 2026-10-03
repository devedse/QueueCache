using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using QueueCache.Management;

namespace QueueCache.Operations.ManagedDisks;

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
    public RamDiskSnapshot Create(Guid resource, ulong capacity, uint sector) =>
        Send(RamDiskSnapshot.Request(RamDiskAction.Create, resource: resource, capacity: capacity, sector: sector));
    public RamDiskSnapshot Query(RamDiskSnapshot expected) => Send(RamDiskSnapshot.Request(RamDiskAction.Query, expected));
    public RamDiskSnapshot Publish(RamDiskSnapshot expected) => Send(RamDiskSnapshot.Request(RamDiskAction.Publish, expected));
    public RamDiskSnapshot Freeze(RamDiskSnapshot expected, Guid operation) => Send(RamDiskSnapshot.Request(RamDiskAction.Freeze, expected, freezeOwner: operation));
    public RamDiskSnapshot Thaw(RamDiskSnapshot expected, Guid operation) => Send(RamDiskSnapshot.Request(RamDiskAction.Thaw, expected, freezeOwner: operation));
    public RamDiskSnapshot SetReadOnly(RamDiskSnapshot expected, bool readOnly) =>
        Send(RamDiskSnapshot.Request(RamDiskAction.SetReadOnly, expected, flags: readOnly ? RamDiskFlags.ReadOnly : RamDiskFlags.None));
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
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RAM disk provider operation failed.");
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
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, [In] byte[] input, uint inputLength,
        [Out] byte[] output, uint outputLength, out uint returned, IntPtr overlapped);
}
