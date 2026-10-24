using Microsoft.Win32;
using QueueCache.Desktop.Services;
using QueueCache.Desktop.ViewModels;
using static Test;

internal static class WindowsStatusTests
{
    public static void Run()
    {
        var policy = new StatusPolicyFixture();
        var model = new WindowsStatusViewModel(policy);
        var settings = new SettingsViewModel(new MemorySettingsStore(), windowsStatus: model);
        var changed = 0;
        settings.Changed += (_, _) => changed++;
        Check(model.Enabled == false && model.CanChange && model.State == "Off in Windows.", "status toggle reads the Windows state");
        model.Enabled = true;
        Check(policy.Value.Enabled && policy.Writes == 1 && model.Enabled == true && changed == 0,
            "status edits change Windows and read back without saving a desktop preference");

        policy.Value = new(false, false);
        model.Refresh();
        Check(model.Enabled == false && policy.Writes == 1, "an external policy edit changes the UI without writing it back");
        policy.Value = new(true, true);
        model.Refresh();
        Check(model.Enabled == true && model.State.StartsWith("Blocked by Windows policy", StringComparison.Ordinal),
            "suppression is shown even when detailed messages are enabled");
        model.Enabled = false;
        Check(policy.Value == new WindowsStatusState(false, true), "changing detailed messages preserves the separate suppression policy");

        policy.WriteError = new UnauthorizedAccessException("Access denied.");
        model.Enabled = true;
        Check(model.Enabled == false && model.Error!.Contains("Access denied"), "a refused write reports the error and restores the actual flag");
        policy.WriteError = null;
        policy.IgnoreWrites = true;
        model.Enabled = true;
        Check(model.Enabled == false && model.Error!.Contains("did not keep"), "read-back detects a policy that did not keep the requested change");
        policy.IgnoreWrites = false;
        model.Enabled = true;
        Check(model.Enabled == true && model.Error is null, "a successful retry clears the write error");

        policy.ReadError = new IOException("Policy read failed.");
        model.Refresh();
        Check(model.Enabled is null && !model.CanChange && model.State.Contains("Policy read failed"), "a failed read is unavailable, never a fabricated off state");
        policy.ReadError = new InvalidDataException("Invalid policy type.");
        model.Refresh();
        Check(model.Enabled is null && !model.CanChange && model.State.Contains("Invalid policy type"), "malformed Windows policy data is reported without escaping the refresh callback");
        policy.ReadError = null;
        model.Refresh();
        Check(model.Enabled == true && model.CanChange, "a subsequent refresh recovers the actual state");
        var create = new CreateDiskViewModel(new DiskFixture(), new HashSet<char>(), model);
        Check(ReferenceEquals(create.WindowsStatus, settings.WindowsStatus), "creation and Settings can expose the same Windows setting");

        if (OperatingSystem.IsWindows())
            RegistryAdapter();
        Console.WriteLine("Windows status policy contracts passed.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RegistryAdapter()
    {
        var path = @"Software\QueueCache-Tests\Status-" + Guid.NewGuid().ToString("N");
        try
        {
            var policy = new WindowsStatusPolicy(Registry.CurrentUser, path);
            Check(policy.Read() == new WindowsStatusState(false, false), "absent policy flags use Windows' normal-message default");
            policy.SetEnabled(true);
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: true)!;
            Check(key.GetValueKind("VerboseStatus") == RegistryValueKind.DWord && (int)key.GetValue("VerboseStatus")! == 1,
                "the adapter writes the actual DWORD policy flag");
            key.SetValue("DisableStatusMessages", 1, RegistryValueKind.DWord);
            Check(policy.Read() == new WindowsStatusState(true, true), "the adapter reads suppression from Windows");
            key.SetValue("VerboseStatus", 0, RegistryValueKind.DWord);
            Check(policy.Read() == new WindowsStatusState(false, true), "an external registry write is visible on the next read");
            policy.SetEnabled(true);
            Check((int)key.GetValue("DisableStatusMessages")! == 1, "writes leave the separate suppression flag untouched");
            key.SetValue("VerboseStatus", "1", RegistryValueKind.String);
            var model = new WindowsStatusViewModel(policy);
            Check(model.Enabled is null && !model.CanChange && model.State.StartsWith("Unavailable", StringComparison.Ordinal),
                "invalid registry types are unavailable instead of being interpreted as a flag");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }
}

internal sealed class StatusPolicyFixture : IWindowsStatusPolicy
{
    public WindowsStatusState Value { get; set; } = new(false, false);
    public Exception? ReadError { get; set; }
    public Exception? WriteError { get; set; }
    public bool IgnoreWrites { get; set; }
    public int Writes { get; private set; }
    public int Reads { get; private set; }
    public WindowsStatusState Read() { Reads++; return ReadError is null ? Value : throw ReadError; }
    public void SetEnabled(bool enabled)
    {
        Writes++;
        if (WriteError is not null) throw WriteError;
        if (!IgnoreWrites) Value = Value with { Enabled = enabled };
    }
}
