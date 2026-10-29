using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

public sealed record WorkingSetLimits(ulong MinimumBytes, ulong MaximumBytes, uint Flags);
public sealed record TelemetryResidency(WorkingSetLimits Original, WorkingSetLimits Applied,
    WorkingSetLimits? Restored = null);

/// <summary>Protect a bounded observer working set during explicit system-memory pressure.
/// Only the telemetry process is affected; workload processes keep normal paging behavior.</summary>
public sealed class TelemetryWorkingSet : IDisposable
{
    public const ulong MinimumBytes = 64UL << 20;
    private const uint HardMinimum = 1, SoftMinimum = 2, HardMaximum = 4, SoftMaximum = 8;
    private readonly Func<WorkingSetLimits> read;
    private readonly Action<WorkingSetLimits> write;
    public TelemetryResidency Evidence { get; private set; }
    private bool disposed;

    public static WorkingSetLimits Requested(WorkingSetLimits original)
    {
        var minimum = Math.Max(original.MinimumBytes, MinimumBytes);
        if ((original.Flags & HardMaximum) != 0 && original.MaximumBytes <= minimum)
            throw new InvalidOperationException("The telemetry process's existing hard maximum cannot accommodate its resident observer minimum.");
        // Windows rejects equal minimum/maximum values (ERROR_INVALID_PARAMETER).
        // Leave room above the protected minimum; preserve an existing hard cap.
        var maximum = (original.Flags & HardMaximum) != 0 ? original.MaximumBytes :
            Math.Max(original.MaximumBytes, checked(minimum + MinimumBytes));
        return new(minimum, maximum,
            (original.Flags & ~SoftMinimum) | HardMinimum);
    }

    // The same transaction is exercised with a fake OS adapter on every host.
    public TelemetryWorkingSet(Func<WorkingSetLimits> read, Action<WorkingSetLimits> write)
    {
        this.read = read; this.write = write;
        var original = read();
        var requested = Requested(original);
        Evidence = new(original, requested);
        try
        {
            write(requested);
            var actual = read();
            if (!Equivalent(requested, actual))
                throw new IOException("Windows did not apply the telemetry process's resident working set minimum.");
            Evidence = new(original, actual);
        }
        catch (Exception error)
        {
            try { Restore(); }
            catch (Exception restoration) { throw new AggregateException(error, restoration); }
            throw;
        }
    }

    public static void Validate(TelemetryResidency evidence)
    {
        if (!Equivalent(Requested(evidence.Original), evidence.Applied) || evidence.Restored is null ||
            !Equivalent(evidence.Original, evidence.Restored))
            throw new InvalidDataException("The telemetry process's applied/restored resident working set evidence is incomplete.");
    }

    private static bool Equivalent(WorkingSetLimits a, WorkingSetLimits b) =>
        a.MinimumBytes == b.MinimumBytes && a.MaximumBytes == b.MaximumBytes &&
        (a.Flags & (HardMinimum | HardMaximum)) == (b.Flags & (HardMinimum | HardMaximum));

    private void Restore()
    {
        // Zero flags mean "leave enforcement unchanged" to Set, whereas Get may
        // omit the explicit disable bits. Restore the original enforcement too.
        var original = Evidence.Original;
        var flags = original.Flags;
        if ((flags & (HardMinimum | SoftMinimum)) == 0) flags |= SoftMinimum;
        if ((flags & (HardMaximum | SoftMaximum)) == 0) flags |= SoftMaximum;
        write(original with { Flags = flags });
        var restored = read();
        if (!Equivalent(original, restored)) throw new IOException("Could not restore the telemetry process's working set limits.");
        Evidence = Evidence with { Restored = restored };
    }

    public void Dispose()
    {
        if (disposed) return;
        Restore();
        disposed = true;
    }

    [SupportedOSPlatform("windows")]
    public static TelemetryWorkingSet ProtectCurrentProcess() => new(ReadCurrent, WriteCurrent);

    [SupportedOSPlatform("windows")]
    public static WorkingSetLimits ReadCurrent()
    {
        if (!GetProcessWorkingSetSizeEx(GetCurrentProcess(), out var minimum, out var maximum, out var flags))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new((ulong)minimum, (ulong)maximum, flags);
    }

    [SupportedOSPlatform("windows")]
    private static void WriteCurrent(WorkingSetLimits limits)
    {
        using var privilege = new ProcessPrivilege("SeIncreaseWorkingSetPrivilege", "to keep the verification observer resident during memory pressure");
        if (!SetProcessWorkingSetSizeEx(GetCurrentProcess(), checked((nuint)limits.MinimumBytes),
                checked((nuint)limits.MaximumBytes), limits.Flags))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"Could not set telemetry working set minimum={limits.MinimumBytes}, maximum={limits.MaximumBytes}, flags={limits.Flags}.");
    }

    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessWorkingSetSizeEx(nint process, out nuint minimum, out nuint maximum, out uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSizeEx(nint process, nuint minimum, nuint maximum, uint flags);
}
