using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using QueueCache.Management;

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
/// <param name="MemoryShort">Refused only because too little RAM was available at that moment; trying again later can succeed.</param>
public sealed record RestoreResult(string Volume, bool Applied, string Detail, string? VolumeId = null, bool MemoryShort = false);

public enum ProfileRestoreState { Restored, WaitingForMemory, NotRestored }
/// <summary>What the broker's startup restore did with one saved profile in this Windows session
/// (#12). <see cref="RetryUntilUtc"/> is set while it waits for RAM.</summary>
public sealed record ProfileRestoreStatus(string Volume, string VolumeId, ProfileRestoreState State, string Detail,
    DateTime UpdatedUtc, DateTime? RetryUntilUtc = null)
{
    /// <summary>Text for a volume list: null once the profile was restored.</summary>
    public string? Describe() => State switch
    {
        ProfileRestoreState.WaitingForMemory => $"waiting for free RAM, retrying until {RetryUntilUtc?.ToLocalTime():HH:mm}",
        ProfileRestoreState.NotRestored => "not restored at startup: " + Detail,
        _ => null
    };
}

/// <summary>Machine profiles use HKLM (administrator-writable), never user-writable startup scripts.
/// One value per volume, named by a hash of the volume GUID.</summary>
[SupportedOSPlatform("windows")]
public static class SavedConfigurations
{
    private const string KeyPath = @"SOFTWARE\QueueCache\Profiles";
    private const string RestoreKeyPath = @"SOFTWARE\QueueCache\ProfileRestore";

    internal static string ValueName(string volumeId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("VOLUME|" + volumeId.ToUpperInvariant())));

    public static void Remove(DiskTarget target)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(ValueName(target.VolumeId), throwOnMissingValue: false);
        ForgetRestore(target.VolumeId);
    }

    public static void Save(DiskTarget target, CacheConfiguration configuration, bool acceptVolatileFlush)
    {
        using var ownershipGate = ManagedDisks.ManagedDiskHostProtection.EnterPolicyGate();
        if (ManagedDisks.ManagedDiskHostProtection.OwnerOfVolume(target.VolumeId) is not null)
            throw new IOException("This volume belongs to a managed disk. Use disk cache/startup settings; a second generic startup profile is refused.");
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
        ForgetRestore(target.VolumeId); // A startup result describes the profile that was replaced.
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
    internal static void RemoveManagedVolume(string volumeId)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(KeyPath, true);
        key?.DeleteValue(ValueName(volumeId), false); key?.Flush();
        ForgetRestore(volumeId);
    }

    /// <param name="include">Restores only these profiles (a retry); null restores every profile.</param>
    /// <param name="onlyIfNoCache">A retry: leave a volume alone whose cache was set while the restore waited.</param>
    public static async Task<IReadOnlyList<RestoreResult>> RestoreAsync(IProgress<string>? progress = null, CancellationToken token = default,
        Func<SavedConfiguration, bool>? include = null, bool onlyIfNoCache = false)
    {
        var results = new List<RestoreResult>();
        foreach (var profile in List().Where(p => include?.Invoke(p) ?? true))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (ManagedDisks.ManagedDiskHostProtection.OwnerOfVolume(profile.VolumeId) is not null)
                    throw new IOException("Startup ownership belongs to the managed-disk broker; generic restore will not apply a duplicate profile.");
                var target = await DiskTarget.InspectAsync(profile.Volume, token);
                profile.CheckIdentity(target.Instance, target.VolumeId, target.Bytes);
                if (onlyIfNoCache && HasCache(target))
                {
                    results.Add(new(profile.Volume, false, "Its cache was set while the restore waited for RAM, so the saved profile was not applied.", profile.VolumeId));
                    continue;
                }
                await Task.Run(() => ConfigurationManager.Apply(target, profile.Configuration, profile.VolatileFlushAccepted, progress), token);
                results.Add(new(profile.Volume, true, "Applied matching saved configuration.", profile.VolumeId));
            }
            catch (OperationCanceledException) { throw; }
            catch (InsufficientMemoryForCacheException ex) { results.Add(new(profile.Volume, false, ex.Message, profile.VolumeId, MemoryShort: true)); }
            catch (Exception ex) { results.Add(new(profile.Volume, false, ex.Message, profile.VolumeId)); }
        }
        return results;
    }
    private static bool HasCache(DiskTarget target)
    {
        using var device = new CacheDevice(target.Device);
        return device.GetWriteCacheState().BudgetBytes != 0;
    }

    /// <summary>The broker's startup restore results of this Windows session (HKLM, one value per profile).</summary>
    public static IReadOnlyList<ProfileRestoreStatus> ReadRestoreStatus()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(RestoreKeyPath);
        if (key is null)
            return [];
        var statuses = new List<ProfileRestoreStatus>();
        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name) is not string json || json.Length > 16384)
                continue;
            ProfileRestoreStatus? status;
            try { status = JsonSerializer.Deserialize<ProfileRestoreStatus>(json); }
            catch (JsonException) { continue; } // Diagnostic only: an unreadable result is skipped.
            if (status is { VolumeId: not null, Volume: not null, Detail: not null } && ValueName(status.VolumeId) == name)
                statuses.Add(status);
        }
        return statuses;
    }
    internal static void RecordRestore(ProfileRestoreStatus status)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.CreateSubKey(RestoreKeyPath, writable: true);
        key.SetValue(ValueName(status.VolumeId), JsonSerializer.Serialize(status), RegistryValueKind.String);
        key.Flush();
    }
    /// <summary>A new Windows session starts without the previous session's results.</summary>
    internal static void ClearRestoreStatus()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        machine.DeleteSubKeyTree(RestoreKeyPath, throwOnMissingSubKey: false);
    }
    private static void ForgetRestore(string volumeId)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(RestoreKeyPath, writable: true);
        key?.DeleteValue(ValueName(volumeId), throwOnMissingValue: false);
    }
}
