using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace QueueCache.Operations;

/// <summary>A saved cache task for one volume. Version 2 (volume filter) names the volume by its GUID
/// (<see cref="VolumeId"/>) and records the volume size. Version 1 (disk filter) named only the disk and
/// recorded its size; it is still restored for the volume at its letter and then rewritten as version 2.</summary>
public sealed record SavedConfiguration(int Version, string Volume, string Instance, long Bytes,
    CacheConfiguration Configuration, bool VolatileFlushAccepted, string? VolumeId = null)
{
    /// <summary>
    /// Validates the persisted schema and its self-contained values. Matching the
    /// saved identity to the volume currently mounted at this letter happens during restore.
    /// </summary>
    public void Validate()
    {
        if (Version is not (1 or 2) || Volume is null || Volume.Length != 2 || !char.IsAsciiLetter(Volume[0]) || Volume[1] != ':' ||
            string.IsNullOrWhiteSpace(Instance) || Instance.Length > 4096 || Bytes <= 0 || Configuration is null ||
            (Version == 2) != (VolumeId is not null) || (VolumeId is not null && !Guid.TryParseExact(VolumeId, "B", out _)))
            throw new InvalidDataException("Invalid or unsupported saved configuration.");
        Configuration.Validate(VolatileFlushAccepted);
    }

    /// <summary>True when this profile belongs to the given volume.</summary>
    public bool Matches(string volume, string instance, string volumeId) => VolumeId is not null
        ? string.Equals(VolumeId, volumeId, StringComparison.OrdinalIgnoreCase)
        : string.Equals(Volume, volume, StringComparison.OrdinalIgnoreCase) &&
          string.Equals(Instance, instance, StringComparison.OrdinalIgnoreCase);

    /// <summary>Restore-time identity check against the volume now mounted at <see cref="Volume"/>:
    /// its disk's PnP instance, its GUID and its size (the disk size for a version-1 profile).</summary>
    public void CheckIdentity(string instance, string volumeId, long volumeBytes, long diskBytes)
    {
        if (!string.Equals(instance, Instance, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Saved identity does not match this volume's disk. Refusing to select another disk.");
        if (VolumeId is not null
                ? !string.Equals(volumeId, VolumeId, StringComparison.OrdinalIgnoreCase) || volumeBytes != Bytes
                // Version 1 recorded the disk size (disk filter); a volume-size record is also accepted.
                : volumeBytes != Bytes && diskBytes != Bytes)
            throw new IOException("Saved identity does not match this volume. Refusing to select another volume.");
    }
}
public sealed record RestoreResult(string Volume, bool Applied, string Detail);

/// <summary>Machine profiles use HKLM (administrator-writable), never user-writable startup scripts.
/// One value per volume (version 2, named by the volume GUID); version-1 values are named by the disk.</summary>
[SupportedOSPlatform("windows")]
public static class SavedConfigurations
{
    private const string KeyPath = @"SOFTWARE\QueueCache\Profiles";

    internal static string VolumeValueName(string volumeId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("VOLUME|" + volumeId.ToUpperInvariant())));
    internal static string LegacyValueName(string instance) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instance.ToUpperInvariant())));

    /// <summary>Removes this volume's saved profile, including a version-1 profile saved for it.</summary>
    public static void Remove(DiskTarget target)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(KeyPath, writable: true);
        if (key is null)
            return;
        key.DeleteValue(VolumeValueName(target.VolumeId), throwOnMissingValue: false);
        RemoveLegacy(key, target);
    }

    public static void Save(DiskTarget target, CacheConfiguration configuration, bool acceptVolatileFlush)
    {
        configuration.Validate(acceptVolatileFlush);
        if (target.VolumeId.Length == 0)
            throw new InvalidOperationException("The volume GUID is required to save a profile.");
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.CreateSubKey(KeyPath, writable: true);
        var profile = new SavedConfiguration(2, $"{target.Letter}:", target.Instance, target.Bytes, configuration,
            acceptVolatileFlush, target.VolumeId);
        profile.Validate();
        key.SetValue(VolumeValueName(target.VolumeId), JsonSerializer.Serialize(profile), RegistryValueKind.String);
        RemoveLegacy(key, target);
        key.Flush();
    }

    // A version-1 value named by this volume's disk is replaced only when it was saved for this volume.
    private static void RemoveLegacy(RegistryKey key, DiskTarget target)
    {
        var legacy = LegacyValueName(target.Instance);
        if (key.GetValue(legacy) is string json && JsonSerializer.Deserialize<SavedConfiguration>(json) is { Version: 1 } old &&
            string.Equals(old.Volume, $"{target.Letter}:", StringComparison.OrdinalIgnoreCase))
            key.DeleteValue(legacy, throwOnMissingValue: false);
    }

    public static IReadOnlyList<SavedConfiguration> List()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(KeyPath);
        if (key is null)
            return [];
        var profiles = new List<SavedConfiguration>();
        foreach (var name in key.GetValueNames())
        {
            if (key.GetValueKind(name) != RegistryValueKind.String || key.GetValue(name) is not string json || json.Length > 16384)
                throw new InvalidDataException("Invalid saved configuration.");
            var profile = JsonSerializer.Deserialize<SavedConfiguration>(json) ?? throw new InvalidDataException("Missing profile.");
            profile.Validate();
            profiles.Add(profile);
        }
        return profiles;
    }

    public static bool IsSaved(string volume, string instance, string volumeId) =>
        List().Any(profile => profile.Matches(volume, instance, volumeId));

    public static async Task<IReadOnlyList<RestoreResult>> RestoreAsync(IProgress<string>? progress = null, CancellationToken token = default)
    {
        var results = new List<RestoreResult>();
        foreach (var profile in List())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var target = await DiskTarget.InspectAsync(profile.Volume, token);
                profile.CheckIdentity(target.Instance, target.VolumeId, target.Bytes, target.DiskBytes);
                await Task.Run(() => ConfigurationManager.Apply(target, profile.Configuration, profile.VolatileFlushAccepted, progress), token);
                // A version-1 profile is rewritten for this volume once it has been applied.
                if (profile.Version == 1)
                    Save(target, profile.Configuration, profile.VolatileFlushAccepted);
                results.Add(new(profile.Volume, true, profile.Version == 1
                    ? "Applied matching saved configuration (converted to a per-volume profile)."
                    : "Applied matching saved configuration."));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { results.Add(new(profile.Volume, false, ex.Message)); }
        }
        return results;
    }
}
