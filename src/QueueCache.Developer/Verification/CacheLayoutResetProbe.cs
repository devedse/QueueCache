using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using QueueCache.Management;

namespace QueueCache.Developer.Verification;

/// <summary>Exercise the explicit reset boundary without reallocating buffers or changing policies.</summary>
[SupportedOSPlatform("windows")]
public static class CacheLayoutResetProbe
{
    public static object Run(CacheDevice device, Action<string, object> record, ulong order = 0)
    {
        CacheLayoutSnapshot Snapshot() => new(device.GetWriteCacheState(), device.GetPerformance(), device.GetDiagnostics());
        static int Refused(Action action, int expected)
        {
            try { action(); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == expected) { return ex.NativeErrorCode; }
            throw new InvalidDataException("Free-order reset guard unexpectedly accepted the command.");
        }

        var populated = Snapshot();
        record("populated", populated);
        CacheLayoutEvidence.ValidateTransition(populated.State.Generation, populated, true);
        if (populated.State.OccupiedSlots == 0)
            throw new InvalidDataException("The occupied-cache refusal needs resident data.");
        var busyError = Refused(() => device.Control(WriteCacheAction.LabResetFreeOrder), 170);
        var refused = Snapshot();
        record("refused", refused);
        if (populated.State != refused.State || populated.Diagnostics.Attribution != refused.Diagnostics.Attribution)
            throw new InvalidDataException("Refused reset changed cache state or lower-I/O attribution.");

        device.Control(WriteCacheAction.DropClean);
        var empty = Snapshot();
        record("empty", empty);
        CacheLayoutEvidence.ValidateReset(empty, empty);
        var budgetError = Refused(() => device.Control(WriteCacheAction.LabResetFreeOrder, budgetBytes: 1), 87);
        var valueError = Refused(() => device.Control(WriteCacheAction.LabResetFreeOrder, value: 4), 87);
        var start = Stopwatch.GetTimestamp();
        device.Control(WriteCacheAction.LabResetFreeOrder, value: order);
        var controlMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var reset = Snapshot();
        record("reset", reset);
        CacheLayoutEvidence.ValidateReset(empty, reset);
        // This is control-call wall time, including dispatch/locking, not isolated kernel CPU time.
        return new { Order = order, BusyError = busyError, BudgetError = budgetError, ValueError = valueError, ControlMilliseconds = controlMilliseconds };
    }
}
