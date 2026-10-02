using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace QueueCache.Operations;

/// <summary>A saved cache task for one volume, named by its GUID (<see cref="VolumeId"/>) so it follows the volume
/// rather than its letter. <see cref="Bytes"/> is the volume size; <see cref="Instance"/> is the PnP identity of
/// the disk that holds it.</summary>
public sealed record SavedConfiguration(int Version, string Volume, string Instance, long Bytes,
    CacheConfiguration Configuration, bool VolatileFlushAccepted, string VolumeId)
{
    public const int CurrentVersion = 2;

    /// <summary>
    /// Validates the persisted schema and its self-contained values. Matching the
    /// saved identity to the volume currently mounted at this letter happens during restore.
    /// </summary>
    public void Validate()
    {
        if (Version != CurrentVersion || Volume is null || Volume.Length != 2 || !char.IsAsciiLetter(Volume[0]) || Volume[1] != ':' ||
            string.IsNullOrWhiteSpace(Instance) || Instance.Length > 4096 || Bytes <= 0 || Configuration is null ||
            VolumeId is null || !Guid.TryParseExact(VolumeId, "B", out _))
            throw new InvalidDataException("Invalid or unsupported saved configuration.");
        Configuration.Validate(VolatileFlushAccepted);
    }

    /// <summary>True when this profile belongs to the volume with this GUID.</summary>
    public bool Matches(string volumeId) => string.Equals(VolumeId, volumeId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Restore-time identity check against the volume now mounted at <see cref="Volume"/>:
    /// its disk's PnP instance, its GUID and its size.</summary>
    public void CheckIdentity(string instance, string volumeId, long volumeBytes)
    {
        if (!string.Equals(instance, Instance, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Saved identity does not match this volume's disk. Refusing to select another disk.");
        if (!Matches(volumeId) || volumeBytes != Bytes)
            throw new IOException("Saved identity does not match this volume. Refusing to select another volume.");
    }
}
public sealed record RestoreResult(string Volume, bool Applied, string Detail);

/// <summary>Machine profiles use HKLM (administrator-writable), never user-writable startup scripts.
/// One value per volume, named by a hash of the volume GUID.</summary>
[SupportedOSPlatform("windows")]
public static class SavedConfigurations
{
    private const string KeyPath = @"SOFTWARE\QueueCache\Profiles";

    internal static string ValueName(string volumeId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("VOLUME|" + volumeId.ToUpperInvariant())));

    public static void Remove(DiskTarget target)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(ValueName(target.VolumeId), throwOnMissingValue: false);
    }

    public static void Save(DiskTarget target, CacheConfiguration configuration, bool acceptVolatileFlush)
    {
        configuration.Validate(acceptVolatileFlush);
        if (target.VolumeId.Length == 0)
            throw new InvalidOperationException("The volume GUID is required to save a profile.");
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.CreateSubKey(KeyPath, writable: true);
        var profile = new SavedConfiguration(SavedConfiguration.CurrentVersion, $"{target.Letter}:", target.Instance, target.Bytes,
            configuration, acceptVolatileFlush, target.VolumeId);
        profile.Validate();
        key.SetValue(ValueName(target.VolumeId), JsonSerializer.Serialize(profile), RegistryValueKind.String);
        key.Flush();
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

    public static bool IsSaved(string volumeId) => List().Any(profile => profile.Matches(volumeId));

    public static async Task<IReadOnlyList<RestoreResult>> RestoreAsync(IProgress<string>? progress = null, CancellationToken token = default)
    {
        var results = new List<RestoreResult>();
        foreach (var profile in List())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var target = await DiskTarget.InspectAsync(profile.Volume, token);
                profile.CheckIdentity(target.Instance, target.VolumeId, target.Bytes);
                await Task.Run(() => ConfigurationManager.Apply(target, profile.Configuration, profile.VolatileFlushAccepted, progress), token);
                results.Add(new(profile.Volume, true, "Applied matching saved configuration."));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { results.Add(new(profile.Volume, false, ex.Message)); }
        }
        return results;
    }
}
