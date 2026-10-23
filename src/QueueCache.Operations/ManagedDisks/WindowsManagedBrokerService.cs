using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace QueueCache.Operations.ManagedDisks;

public sealed record ManagedBrokerObservation(uint State, int ProcessId, DateTime StartedUtc)
{
    public bool IsRunning => State == 4 && ProcessId > 0 && StartedUtc != default;
}

/// <summary>SCM host in the installed qcache binary. Stopping the broker preserves owned storage.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsManagedBrokerService
{
    public const string ServiceName = "QueueCache.ManagedDisks";
    private const uint OwnProcess = 0x10, Stopped = 1, StartPending = 2, StopPending = 3, Running = 4;
    // SERVICE_CONTROL_* codes. Running accepts STOP | SHUTDOWN | PRESHUTDOWN (0x105).
    internal const uint ControlStop = 1, ControlInterrogate = 4, ControlShutdown = 5, ControlPreshutdown = 15;
    private static readonly object statusGate = new(), logGate = new();
    private static readonly ServiceMain main = Main;
    private static readonly Handler handler = Handle;
    private static readonly TaskCompletionSource<uint> stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static IntPtr statusHandle;
    private static Status status;
    private static int exitCode;

    public static int Run()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true)
            throw new UnauthorizedAccessException("The managed broker must run as LocalSystem through Windows Service Control Manager.");
        var table = new[] { new ServiceEntry { Name = ServiceName, Main = main }, new ServiceEntry() };
        Check(StartServiceCtrlDispatcherW(table));
        GC.KeepAlive(main); GC.KeepAlive(handler);
        return exitCode;
    }
    private static void Main(uint count, IntPtr arguments)
    {
        statusHandle = RegisterServiceCtrlHandlerExW(ServiceName, handler, IntPtr.Zero);
        if (statusHandle == IntPtr.Zero) { exitCode = Marshal.GetLastWin32Error(); return; }
        try { RunAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { Log(ex.ToString()); exitCode = 1; }
        finally { Report(Stopped, exitCode == 0 ? 0U : 1066U); }
    }
    private static async Task RunAsync()
    {
        Report(StartPending);
        using var heartbeatDone = new CancellationTokenSource();
        var heartbeat = HeartbeatAsync(heartbeatDone.Token);
        try
        {
            using var provider = WindowsRamDisk.Connect();
            var session = provider.StartupSession();
            using var engine = new WindowsManagedDiskEngine(session.BootEpoch, session.WriteGeneration);
            await engine.InitializeAsync(new ServiceProgress());
            Report(Running);
            using var serving = new CancellationTokenSource();
            // Startup restores refused only because RAM was short are retried meanwhile (#12).
            var retry = engine.RetryStartupAsync(serving.Token);
            var endpoint = new WindowsManagedDiskBroker(engine).RunAsync(serving.Token);
            var completed = await Task.WhenAny(endpoint, stopping.Task);
            if (completed == endpoint) { serving.Cancel(); await retry; await endpoint; return; }
            var control = await stopping.Task;
            Report(StopPending);
            // Cancel precommit work and await its independent cleanup before disposing
            // broker handles. Committed saves and Windows formatting finish their actual outcome.
            serving.Cancel();
            await endpoint;
            await retry;
            if (SavesBeforeStopping(control))
            {
                // No time limit (#22): a save is given up only when it makes no progress for StallLimit.
                using var watchdog = new ShutdownSaveWatchdog(StallLimit);
                await engine.AttemptShutdownSavesAsync(watchdog, watchdog.Token);
            }
        }
        finally { heartbeatDone.Cancel(); await heartbeat; }
    }
    /// <summary>Stop, shutdown and preshutdown stop the broker. Until October 2026 the preshutdown code was
    /// written as 16 (TIMECHANGE), so Windows' preshutdown control went unanswered: every shutdown waited
    /// for the service (event 7043) and image saves ran only in the short SHUTDOWN phase.</summary>
    internal static bool Stops(uint control) => control is ControlStop or ControlShutdown or ControlPreshutdown;
    /// <summary>Windows is shutting down: attempt the configured shutdown saves. Preshutdown gives the service
    /// its configured 120 s; a plain SHUTDOWN only arrives if preshutdown was not delivered.</summary>
    internal static bool SavesBeforeStopping(uint control) => control is ControlShutdown or ControlPreshutdown;
    private static uint Handle(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        if (Stops(control)) { stopping.TrySetResult(control); return 0; }
        if (control == ControlInterrogate) { lock (statusGate) _ = SetServiceStatus(statusHandle, ref status); return 0; }
        return 120; // ERROR_CALL_NOT_IMPLEMENTED
    }
    private static void Report(uint state, uint error = 0)
    {
        lock (statusGate)
        {
            status = new() { Type = OwnProcess, State = state,
                Accepted = state == Running ? 0x105U : 0U, Win32Error = error, SpecificError = error == 1066 ? 1U : 0U,
                Checkpoint = state is StartPending or StopPending ? status.Checkpoint + 1 : 0,
                WaitHint = state is StartPending or StopPending ? 20000U : 0U };
            Check(SetServiceStatus(statusHandle, ref status));
        }
    }
    private static async Task HeartbeatAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(token))
                lock (statusGate)
                    if (status.State is StartPending or StopPending) { status.Checkpoint++; Check(SetServiceStatus(statusHandle, ref status)); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    internal static void Log(string message)
    {
        lock (logGate)
        {
            try
            {
                var path = Path.Combine(ManagedDiskHostProtection.CatalogDirectory, "service.log");
                if (File.Exists(path) && new FileInfo(path).Length > (4 << 20)) File.Move(path, path + ".previous", overwrite: true);
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Trace.WriteLine(message); }
        }
    }
    private sealed class ServiceProgress : IProgress<ManagedDiskProgress>
    { public void Report(ManagedDiskProgress value) => Log($"{value.Stage}: {value.Message}"); }

    public static void AuthenticateServer(NamedPipeClientStream pipe)
    {
        Check(GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid));
        using var manager = Manager(1);
        using var service = Open(manager, 4);
        var current = Query(service);
        if (current.State != Running || current.ProcessId == 0 || current.ProcessId != pid)
            throw new UnauthorizedAccessException("The management endpoint is not the running QueueCache Windows service.");
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        Check(OpenProcessToken(process, 8, out var token));
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true)
                throw new UnauthorizedAccessException("The managed service endpoint must be owned by LocalSystem.");
        // PID/state are checked again after opening the process token to reject service restarts.
        var again = Query(service);
        if (again.State != Running || again.ProcessId != pid) throw new IOException("The managed service restarted; reconnect and refresh.");
    }
    public static ManagedBrokerObservation Observe()
    {
        using var manager = Manager(1); using var service = Open(manager, 4);
        var current = Query(service);
        if (current.ProcessId == 0) return new(current.State, 0, default);
        using var process = Process.GetProcessById(checked((int)current.ProcessId));
        var started = process.StartTime.ToUniversalTime(); var again = Query(service);
        if (again.State != current.State || again.ProcessId != current.ProcessId)
            throw new IOException("The broker changed while its process identity was being observed.");
        return new(current.State, checked((int)current.ProcessId), started);
    }
    public static void Install(string executable)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable) || executable.Contains('"')) throw new ArgumentException("A valid installed qcache executable is required.");
        ManagedDiskHostProtection.CreateProtectedDirectory(ManagedDiskHostProtection.CatalogDirectory);
        using var manager = Manager(3);
        using var service = OpenServiceW(manager, ServiceName, 0xF01FF);
        var command = $"\"{executable}\" --managed-service-host";
        var dependencies = "qcachelab\0qcramdisk\0\0";
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1060) throw new Win32Exception(error);
            using var created = CreateServiceW(manager, ServiceName, "QueueCache managed disks", 0xF01FF, OwnProcess, 2, 1,
                command, null, IntPtr.Zero, dependencies, "LocalSystem", null);
            if (created.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            Configure(created);
        }
        else
        {
            if (Query(service).State != Stopped) throw new IOException("Stop the managed service after its update preflight before changing the installed service.");
            Check(ChangeServiceConfigW(service, OwnProcess, 2, 1, command, null, IntPtr.Zero, dependencies, "LocalSystem", null, "QueueCache managed disks"));
            Configure(service);
        }
    }
    /// <summary>How long Windows waits for the broker at shutdown. Saving a large image to a slow disk can
    /// take many minutes (#22), so this is a backstop, not a time limit: the broker finishes as soon as its
    /// saves do, and gives a save up when it makes no progress for <see cref="StallLimit"/>.</summary>
    internal const uint PreshutdownTimeoutMs = 3 * 60 * 60 * 1000;
    internal static readonly TimeSpan StallLimit = TimeSpan.FromMinutes(3);
    private static void Configure(ServiceHandle service)
    {
        var delayed = 1U; Check(ChangeServiceConfig2W(service, 3, ref delayed));
        var preshutdown = PreshutdownTimeoutMs; Check(ChangeServiceConfig2W(service, 7, ref preshutdown));
    }
    /// <summary>Cancels the shutdown saves when no progress was reported for the stall limit, and logs each
    /// phase and every tenth of its progress.</summary>
    internal sealed class ShutdownSaveWatchdog : IProgress<ManagedDiskProgress>, IDisposable
    {
        private readonly CancellationTokenSource stalled = new();
        private readonly System.Diagnostics.Stopwatch quiet = System.Diagnostics.Stopwatch.StartNew();
        private readonly Timer timer;
        private readonly TimeSpan limit;
        private readonly Action<string> log;
        private string? phase;
        private int tenth = -1;
        public ShutdownSaveWatchdog(TimeSpan limit, TimeSpan? checkEvery = null, Action<string>? log = null)
        {
            this.limit = limit;
            this.log = log ?? Log;
            var period = checkEvery ?? TimeSpan.FromSeconds(10);
            timer = new Timer(_ => Check(), null, period, period);
        }
        public CancellationToken Token => stalled.Token;
        public void Report(ManagedDiskProgress value)
        {
            lock (quiet)
            {
                quiet.Restart();
                var current = value is { TotalBytes: > 0 and var total, CompletedBytes: { } done } ? (int)Math.Min(10UL, 10 * done / total) : -1;
                if (value.Message == phase && current == tenth)
                    return;
                if (value.Message != phase)
                    tenth = -1;
                phase = value.Message;
                if (current == tenth)
                    return;
                tenth = current;
            }
            log(value.TotalBytes is > 0 ? $"Shutdown save: {value.Message} {10 * Math.Max(tenth, 0)}%" : "Shutdown save: " + value.Message);
        }
        private void Check()
        {
            bool expired;
            lock (quiet) expired = quiet.Elapsed > limit;
            if (expired && !stalled.IsCancellationRequested)
            {
                log($"Shutdown save: no progress for {limit.TotalMinutes:0} minutes; giving it up so Windows can shut down.");
                stalled.Cancel();
            }
        }
        public void Dispose() { timer.Dispose(); stalled.Dispose(); }
    }
    public static void Start()
    {
        using var manager = Manager(1); using var service = Open(manager, 0x14);
        if (!StartServiceW(service, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != 1056) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public static async Task StopAsync(CancellationToken token = default)
    {
        using var manager = Manager(1); using var service = OpenServiceW(manager, ServiceName, 0x24);
        if (service.IsInvalid) { var error = Marshal.GetLastWin32Error(); if (error == 1060) return; throw new Win32Exception(error); }
        if (Query(service).State == Stopped) return;
        if (!ControlService(service, 1, out _) && Marshal.GetLastWin32Error() != 1062) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(3));
        while (Query(service).State != Stopped) await Task.Delay(250, deadline.Token);
    }
    public static void Remove()
    {
        using var manager = Manager(1); using var service = OpenServiceW(manager, ServiceName, 0x10004);
        if (service.IsInvalid) { var error = Marshal.GetLastWin32Error(); if (error == 1060) return; throw new Win32Exception(error); }
        if (Query(service).State != Stopped) throw new IOException("Stop the managed broker before removing its service registration.");
        Check(DeleteService(service)); // Definitions, journals and images are preserved.
    }
    public static async Task RequireUpdateReadyAsync(CancellationToken token = default)
    {
        WindowsProviderInstallation.RequireNoLiveDisks();
        if (Directory.Exists(ManagedDiskHostProtection.CatalogDirectory))
            foreach (var record in new ManagedDiskStore(ManagedDiskHostProtection.CatalogDirectory).List())
                if (record.Runtime is not null && record.Runtime.State != ManagedDiskState.Stopped)
                    throw new IOException($"Managed resource {record.ResourceId} is {record.Runtime.State}. Stop or reconcile every managed resource before updating/uninstalling; images and RAM are retained.");
        await StopAsync(token);
        WindowsProviderInstallation.RequireNoLiveDisks();
        if (Directory.Exists(ManagedDiskHostProtection.CatalogDirectory))
            foreach (var record in new ManagedDiskStore(ManagedDiskHostProtection.CatalogDirectory).List())
                if (record.Runtime is not null && record.Runtime.State != ManagedDiskState.Stopped)
                    throw new IOException("An operation completed or requires recovery while stopping the broker. Reconcile/stop that resource before replacing files.");
    }
    private static ServiceHandle Manager(uint access)
    { var handle = OpenSCManagerW(null, null, access); if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error()); return handle; }
    private static ServiceHandle Open(ServiceHandle manager, uint access)
    { var handle = OpenServiceW(manager, ServiceName, access); if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error()); return handle; }
    private static ProcessStatus Query(ServiceHandle service)
    { Check(QueryServiceStatusEx(service, 0, out var value, (uint)Marshal.SizeOf<ProcessStatus>(), out _)); return value; }
    private static void Check(bool ok) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    { private ServiceHandle() : base(true) { } protected override bool ReleaseHandle() => CloseServiceHandle(handle); }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ServiceMain(uint count, IntPtr arguments);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint Handler(uint control, uint type, IntPtr data, IntPtr context);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ServiceEntry
    { [MarshalAs(UnmanagedType.LPWStr)] public string? Name; public ServiceMain? Main; }
    [StructLayout(LayoutKind.Sequential)] private struct Status
    { public uint Type, State, Accepted, Win32Error, SpecificError, Checkpoint, WaitHint; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessStatus
    { public uint Type, State, Accepted, Win32Error, SpecificError, Checkpoint, WaitHint, ProcessId, Flags; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcherW([In] ServiceEntry[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr RegisterServiceCtrlHandlerExW(string name, Handler handler, IntPtr context);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetServiceStatus(IntPtr handle, ref Status status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenServiceW(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle CreateServiceW(ServiceHandle manager, string name, string display, uint access, uint type, uint start, uint error, string binary, string? group, IntPtr tag, string dependencies, string user, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfigW(ServiceHandle service, uint type, uint start, uint error, string binary, string? group, IntPtr tag, string dependencies, string user, string? password, string display);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfig2W(ServiceHandle service, uint level, ref uint value);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartServiceW(ServiceHandle service, uint count, IntPtr arguments);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ControlService(ServiceHandle service, uint control, out Status status);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(ServiceHandle service, uint level, out ProcessStatus status, uint bytes, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteService(ServiceHandle service);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
