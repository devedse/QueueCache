using System.Text.Json;

namespace QueueCache.Desktop.Services;

public enum AppTheme { System, Light, Dark }

/// <summary>Per-user preferences of the desktop app. Kept apart from cache and disk configuration,
/// which belong to the driver and the managed-disk service.</summary>
public sealed record DesktopSettings(double UpdateSeconds = 1, AppTheme Theme = AppTheme.System, bool KeepRunningInTray = true)
{
    public static readonly double[] UpdateChoices = [0.5, 1, 2, 5, 10];
}

public interface IDesktopSettingsStore
{
    DesktopSettings Load();
    void Save(DesktopSettings settings);
}

public sealed class DesktopSettingsStore(string path) : IDesktopSettingsStore
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QueueCache", "desktop.json");

    public DesktopSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(path)) ?? new();
            return DesktopSettings.UpdateChoices.Contains(settings.UpdateSeconds) && Enum.IsDefined(settings.Theme) ? settings : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(DesktopSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>Settings that are never written (tests, previews).</summary>
public sealed class MemorySettingsStore(DesktopSettings? initial = null) : IDesktopSettingsStore
{
    private DesktopSettings current = initial ?? new();
    public DesktopSettings Load() => current;
    public void Save(DesktopSettings settings) => current = settings;
}
