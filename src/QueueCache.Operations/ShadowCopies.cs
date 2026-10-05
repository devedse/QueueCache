using System.Runtime.Versioning;

namespace QueueCache.Operations;

/// <summary>Creates and deletes client-accessible shadow copies (Win32_ShadowCopy) for verification scenarios.</summary>
[SupportedOSPlatform("windows")]
public static class ShadowCopies
{
    public sealed record ShadowCopy(Guid Id, string DeviceObject);

    /// <summary>Returns the new shadow copy, or the Win32_ShadowCopy.Create code as <paramref name="failure"/>
    /// (4 = Windows does not snapshot this file system).</summary>
    public static ShadowCopy? Create(string root, out uint? failure)
    {
        failure = null;
        var created = PowerShell($"$ErrorActionPreference='Stop'; $r=Invoke-CimMethod -ClassName Win32_ShadowCopy -MethodName Create -Arguments @{{Volume='{root}'; Context='ClientAccessible'}}; " +
            "if ($r.ReturnValue -ne 0) { \"FAILED $($r.ReturnValue)\" } else { $s=Get-CimInstance Win32_ShadowCopy | Where-Object ID -eq $r.ShadowID; \"$($r.ShadowID)|$($s.DeviceObject)\" }").Trim();
        if (created.StartsWith("FAILED ", StringComparison.Ordinal) && uint.TryParse(created[7..], out var code))
        {
            failure = code;
            return null;
        }
        var parts = created.Split('|');
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var id) || !parts[1].StartsWith(@"\\?\GLOBALROOT\Device\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Unexpected shadow copy identity: " + created);
        return new(id, parts[1]);
    }

    public static void Delete(Guid id) =>
        PowerShell($"$ErrorActionPreference='Stop'; Get-CimInstance Win32_ShadowCopy | Where-Object ID -eq '{{{id}}}' | Remove-CimInstance");

    /// <summary>The same file inside a shadow copy (path is absolute on the snapshotted volume).</summary>
    public static string PathIn(ShadowCopy copy, string path) => copy.DeviceObject + Path.GetFullPath(path)[2..];

    internal static string PowerShell(string command)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(command);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Cannot start PowerShell.");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            process.Kill(true);
            throw new TimeoutException("PowerShell did not finish within five minutes.");
        }
        if (process.ExitCode != 0)
            throw new IOException($"PowerShell failed ({process.ExitCode}): {error.GetAwaiter().GetResult().Trim()}");
        return output;
    }
}
