using System.Diagnostics;
using System.Runtime.Versioning;
using System.Xml.Linq;

namespace QueueCache.Desktop.Services;

/// <summary>"Start QueueCache when you sign in": the installer's scheduled task, turned on or off.</summary>
public interface ISignInTask
{
    /// <summary>Whether the task runs at sign-in; null when it is not installed.</summary>
    bool? IsEnabled();
    void SetEnabled(bool enabled);
}

/// <summary>The QueueCache-SignIn task registered by setup (Install-Driver.ps1). The app runs elevated,
/// so it may change the task; it never creates one (reinstalling restores it).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSignInTask : ISignInTask
{
    private const string Name = "QueueCache-SignIn";

    public bool? IsEnabled()
    {
        var (code, output) = Run("/Query", "/TN", Name, "/XML");
        if (code != 0)
            return null;
        // Locale-independent: Settings/Enabled is absent when the task is enabled.
        var settings = XDocument.Parse(output).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Settings");
        var enabled = settings?.Elements().FirstOrDefault(e => e.Name.LocalName == "Enabled")?.Value;
        return enabled is null || !enabled.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        var (code, output) = Run("/Change", "/TN", Name, enabled ? "/ENABLE" : "/DISABLE");
        if (code != 0)
            throw new IOException("Could not change the sign-in task: " + output.Trim());
    }

    private static (int Code, string Output) Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("schtasks did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            process.Kill();
            return (-1, "schtasks did not answer.");
        }
        return (process.ExitCode, output.Result + error.Result);
    }
}

/// <summary>For tests and other platforms.</summary>
public sealed class MemorySignInTask(bool? enabled = true) : ISignInTask
{
    public bool? Enabled { get; private set; } = enabled;
    public bool? IsEnabled() => Enabled;
    public void SetEnabled(bool enabled) => Enabled = Enabled is null ? throw new IOException("Not installed.") : enabled;
}
