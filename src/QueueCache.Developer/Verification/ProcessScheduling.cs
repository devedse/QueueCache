using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace QueueCache.Developer.Verification;

/// <summary>Settings for an owned measurement child only; no driver/thread boost or parent changes.</summary>
public sealed record ProcessScheduling(ProcessPriorityClass Cpu, uint MemoryPriority)
{
    public void Validate()
    {
        if (Cpu is not (ProcessPriorityClass.Normal or ProcessPriorityClass.BelowNormal) || MemoryPriority is < 1 or > 5)
            throw new ArgumentException("Measurement scheduling permits Normal/BelowNormal CPU and memory priority 1..5.");
    }

    public ProcessScheduling Apply(Process process)
    {
        Validate();
        process.PriorityClass = Cpu;
        var memory = MemoryPriority;
        if (!SetProcessInformation(process.SafeHandle, 0 /* ProcessMemoryPriority */, ref memory, sizeof(uint)))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Set owned measurement memory priority");
        if (!GetProcessInformation(process.SafeHandle, 0, out var actualMemory, sizeof(uint)))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Read owned measurement memory priority");
        process.Refresh();
        var actual = new ProcessScheduling(process.PriorityClass, actualMemory);
        if (actual != this) throw new IOException($"Scheduling mismatch: expected {this}, actual {actual}.");
        return actual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(SafeProcessHandle process, int informationClass, ref uint information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(SafeProcessHandle process, int informationClass, out uint information, uint size);
}
