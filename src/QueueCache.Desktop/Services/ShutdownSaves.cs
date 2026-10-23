using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using QueueCache.Desktop.ViewModels;

namespace QueueCache.Desktop.Services;

/// <summary>Saving images in RAM while Windows shuts down (#22). The broker saves them too, but only after
/// the session has ended, when nothing can be shown. So when Windows asks the app to close and an image that
/// saves at shutdown has unsaved changes, the app saves it first and holds the shutdown with a reason that
/// Windows shows full screen. Signing out asks the same question and saves too.</summary>
internal static partial class ShutdownSaves
{
    public static IReadOnlyList<VirtualDiskViewModel> Pending(IEnumerable<VirtualDiskViewModel> disks) =>
        disks.Where(d => d.IsImage && d.HasUnsavedChanges && d.CanSave && d.Record.Definition.SaveDuringShutdown).ToArray();

    /// <summary>The short text Windows shows next to QueueCache while it holds the shutdown.</summary>
    public static string Reason(VirtualDiskViewModel disk)
    {
        var step = disk.OperationText.StartsWith("Verifying", StringComparison.Ordinal) ? "checking" : "copying";
        return Percent().Match(disk.OperationText) is { Success: true } percent
            ? $"Saving {disk.Letter}: to its image ({step}, {percent.Value})"
            : $"Saving {disk.Letter}: to its image";
    }

    [GeneratedRegex(@"\d+(\.\d+)?\s?%$")]
    private static partial Regex Percent();

    [SupportedOSPlatform("windows")]
    public static void Hold(IntPtr window, string reason) => _ = ShutdownBlockReasonCreate(window, reason);
    [SupportedOSPlatform("windows")]
    public static void Release(IntPtr window) => _ = ShutdownBlockReasonDestroy(window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr window, string reason);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr window);
}
