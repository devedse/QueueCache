using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace QueueCache.Operations;

public sealed record DiskDescription(int Number, string Name, long Bytes, string Instance, string[] Volumes, bool IsBoot, bool IsSystem)
{
    public string Device => $"PhysicalDrive{Number}";
    public double SizeGiB => Bytes / 1073741824.0;
    public string Display => $"{string.Join(", ", Volumes)} · {Device} · {SizeGiB:0.##} GiB · {Name}" + (IsBoot || IsSystem ? " [boot/system]" : "");
}

[SupportedOSPlatform("windows")]
public static class DiskCatalog
{
    public static async Task<IReadOnlyList<DiskDescription>> ListAsync(CancellationToken token = default)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; $items=@(Get-Disk | ForEach-Object { $d=$_; $c=Get-CimInstance Win32_DiskDrive -Filter ('Index='+$d.Number); [pscustomobject]@{Number=[int]$d.Number;Name=$d.FriendlyName;Bytes=[long]$d.Size;Instance=$c.PNPDeviceID;Volumes=@(Get-Partition -DiskNumber $d.Number | Where-Object DriveLetter | ForEach-Object { \"$($_.DriveLetter):\" });IsBoot=[bool]$d.IsBoot;IsSystem=[bool]$d.IsSystem} }); ConvertTo-Json -InputObject $items -Compress -Depth 3");
        using var process = Process.Start(start) ?? throw new IOException("Cannot enumerate disks.");
        var output = process.StandardOutput.ReadToEndAsync(token); var error = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw; }
        if (process.ExitCode != 0) throw new IOException(await error);
        return JsonSerializer.Deserialize<DiskDescription[]>(await output) ?? throw new IOException("Invalid disk inventory.");
    }
}
