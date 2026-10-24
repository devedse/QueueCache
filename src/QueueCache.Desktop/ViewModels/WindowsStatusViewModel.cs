using CommunityToolkit.Mvvm.ComponentModel;
using QueueCache.Desktop.Services;

namespace QueueCache.Desktop.ViewModels;

/// <summary>Every refresh and edit reads Windows; there is no saved QueueCache preference.</summary>
public sealed partial class WindowsStatusViewModel : ObservableObject
{
    private readonly IWindowsStatusPolicy policy;
    private bool refreshing;

    public WindowsStatusViewModel(IWindowsStatusPolicy? policy = null)
    {
        this.policy = policy ?? new UnavailableWindowsStatusPolicy();
        Refresh();
    }

    [ObservableProperty] private bool? enabled;
    [ObservableProperty] private bool canChange;
    [ObservableProperty] private string state = "";
    [ObservableProperty] private string? error;

    public void Refresh()
    {
        try { Apply(policy.Read()); }
        catch (Exception ex) when (Expected(ex))
        {
            refreshing = true;
            try { Enabled = null; CanChange = false; State = "Unavailable: " + ex.Message; }
            finally { refreshing = false; }
        }
    }

    partial void OnEnabledChanged(bool? value)
    {
        if (refreshing || value is null)
            return;
        try
        {
            policy.SetEnabled(value.Value);
            var actual = policy.Read();
            Apply(actual);
            Error = actual.Enabled == value.Value ? null : "Windows did not keep the change. Check the Windows policy for detailed status messages.";
        }
        catch (Exception ex) when (Expected(ex))
        {
            Error = "Could not change Windows status messages: " + ex.Message;
            Refresh();
        }
    }

    private void Apply(WindowsStatusState value)
    {
        refreshing = true;
        try
        {
            Enabled = value.Enabled;
            CanChange = true;
            State = value.Suppressed ? "Blocked by Windows policy: status messages are suppressed, even when detailed messages are enabled."
                : value.Enabled ? "On in Windows." : "Off in Windows.";
        }
        finally { refreshing = false; }
    }

    private static bool Expected(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException
        or InvalidOperationException or PlatformNotSupportedException;
}
