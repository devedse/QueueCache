using Microsoft.Win32;
using System.Runtime.Versioning;

namespace QueueCache.Desktop.Services;

public sealed record WindowsStatusState(bool Enabled, bool Suppressed);

/// <summary>The machine's Windows status-message policy, independent of desktop.json.</summary>
public interface IWindowsStatusPolicy
{
    WindowsStatusState Read();
    void SetEnabled(bool enabled);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsStatusPolicy : IWindowsStatusPolicy
{
    private readonly RegistryKey hive;
    private readonly string path;

    public WindowsStatusPolicy() : this(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System") { }

    // Tests use an owned temporary HKCU key, never the machine's policy.
    internal WindowsStatusPolicy(RegistryKey hive, string path) { this.hive = hive; this.path = path; }

    public WindowsStatusState Read()
    {
        using var key = hive.OpenSubKey(path);
        return new(Flag(key, "VerboseStatus"), Flag(key, "DisableStatusMessages"));
    }

    public void SetEnabled(bool enabled)
    {
        using var key = hive.CreateSubKey(path, writable: true) ?? throw new IOException("Windows status policy could not be opened for writing.");
        key.SetValue("VerboseStatus", enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    private static bool Flag(RegistryKey? key, string name) => key?.GetValue(name) switch
    {
        null => false,
        int value when key!.GetValueKind(name) == RegistryValueKind.DWord && value is 0 or 1 => value == 1,
        _ => throw new InvalidDataException($"Windows policy {name} is not a DWORD with value 0 or 1.")
    };
}

internal sealed class UnavailableWindowsStatusPolicy : IWindowsStatusPolicy
{
    public WindowsStatusState Read() => throw new PlatformNotSupportedException("Windows status messages are available on Windows only.");
    public void SetEnabled(bool enabled) => throw new PlatformNotSupportedException("Windows status messages are available on Windows only.");
}
