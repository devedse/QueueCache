using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using QueueCache.Management;

namespace QueueCache.Operations.ManagedDisks;

/// <summary>Pins paths and records durable host dependencies before image operations.</summary>
[SupportedOSPlatform("windows")]
public static class ManagedDiskHostProtection
{
    private const string Dependencies = @"SOFTWARE\QueueCache\ManagedDiskHosts";
    private const string VolumeOwnership = @"SOFTWARE\QueueCache\ManagedDiskVolumes";
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdminSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier InstallerSid = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    public static string CatalogDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "QueueCache", "ManagedDisks");
    public static void CreateProtectedDirectory(string path)
    {
        ManagedDiskPaths.ValidateLocalDirectory(path);
        if (Directory.Exists(path)) { ValidateWritableDirectory(path); return; }
        var parent = Path.GetDirectoryName(path) ?? throw new ArgumentException("Missing destination parent.");
        if (!Directory.Exists(parent)) CreateProtectedDirectory(parent);
        using var pinned = Pin(parent, writableDirectory: false);
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        security.SetOwner(SystemSid);
        foreach (var sid in new[] { SystemSid, AdminSid })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
        ValidateWritableDirectory(path);
    }
    public static IDisposable Pin(string directory, bool writableDirectory, bool trustedDirectory = true)
    {
        ManagedDiskPaths.ValidateLocalDirectory(directory);
        var handles = new List<SafeFileHandle>();
        try
        {
            var current = new DirectoryInfo(directory);
            while (current is not null)
            {
                var attributes = current.Attributes;
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Compressed | FileAttributes.Encrypted)) != 0)
                    throw new IOException("Managed images cannot use reparse, compressed or encrypted directories.");
                if (trustedDirectory) ValidatePermissions(current, writableDirectory && handles.Count == 0);
                var handle = CreateFileW(current.FullName, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not pin an image directory."); }
                handles.Add(handle);
                var resolved = new StringBuilder(32768);
                if (GetFinalPathNameByHandleW(handle, resolved, (uint)resolved.Capacity, 0) == 0 ||
                    !resolved.ToString().TrimEnd('\\').Equals((@"\\?\" + current.FullName).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("An image directory's final handle path differs from its selected path.");
                current = current.Parent;
            }
            return new Pins(handles);
        }
        catch { foreach (var h in handles) h.Dispose(); throw; }
    }
    public static void ValidateWritableDirectory(string path) { using var pins = Pin(path, true); }
    private static void ValidatePermissions(DirectoryInfo directory, bool writes)
    {
        var acl = directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !Trusted(owner))
            throw new IOException("Managed image directories require an administrator/SYSTEM owner: " + directory.FullName);
        var unsafeRights = FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        if (writes) unsafeRights |= FileSystemRights.Write;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                !Trusted((SecurityIdentifier)rule.IdentityReference) && (rule.FileSystemRights & unsafeRights) != 0)
                throw new IOException("An unprivileged account can modify or replace the managed image directory: " + directory.FullName);
    }
    private static bool Trusted(SecurityIdentifier sid) => sid == SystemSid || sid == AdminSid || sid == InstallerSid;
    public static string HostVolume(string path)
    {
        var mount = new StringBuilder(32768); var volume = new StringBuilder(1024);
        if (!GetVolumePathNameW(path, mount, (uint)mount.Capacity) || !GetVolumeNameForVolumeMountPointW(mount.ToString(), volume, (uint)volume.Capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resolve the image host volume.");
        if (WindowsDiskStorage.FileSystem(volume.ToString()) != "NTFS") throw new NotSupportedException("Managed image host storage must be local NTFS.");
        if (OwnerOfVolume(VolumeIds.Parse(volume.ToString())) is not null)
            throw new IOException("Managed image/checkpoint storage cannot be nested on another managed disk.");
        using var physical = WindowsDiskStorage.Open(@"\\.\PhysicalDrive" + WindowsDiskStorage.VolumeDisk(volume.ToString()), false);
        if (physical.Serial().StartsWith("QC", StringComparison.Ordinal))
            throw new IOException("A managed image cannot depend on a RAM disk.");
        using var cache = CacheDevice.OpenVolumeName(volume.ToString());
        var state = cache.GetWriteCacheState();
        if (state.Enabled && state.UnsafeDefer) throw new IOException("A managed image cannot depend on a host volume with volatile Fast caching. Use Strict or disable that host cache.");
        return VolumeIds.Parse(volume.ToString());
    }
    public static void Register(Guid resource, IEnumerable<string> paths)
    {
        using var gate = EnterPolicyGate();
        var ids = paths.Select(HostVolume).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using var key = Registry.LocalMachine.CreateSubKey(Dependencies, true);
        key.SetValue(resource.ToString("N"), ids, RegistryValueKind.MultiString); key.Flush();
    }
    public static void RegisterAdditionalHost(Guid resource, string directory)
    {
        using var gate = EnterPolicyGate(); var host = HostVolume(directory);
        using var key = Registry.LocalMachine.CreateSubKey(Dependencies, true);
        var existing = key.GetValue(resource.ToString("N")) as string[] ?? [];
        key.SetValue(resource.ToString("N"), existing.Append(host).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), RegistryValueKind.MultiString);
        key.Flush();
    }
    public static void Unregister(Guid resource)
    {
        using var gate = EnterPolicyGate(); using var key = Registry.LocalMachine.OpenSubKey(Dependencies, true);
        key?.DeleteValue(resource.ToString("N"), false); key?.Flush();
        using var volumes = Registry.LocalMachine.OpenSubKey(VolumeOwnership, true);
        volumes?.DeleteValue(resource.ToString("N"), false); volumes?.Flush();
    }
    public static Guid? OwnerOfVolume(string volumeId)
    {
        using var key = Registry.LocalMachine.OpenSubKey(VolumeOwnership);
        Guid? owner = null;
        if (key is null) return null;
        foreach (var name in key.GetValueNames())
        {
            if (!Guid.TryParseExact(name, "N", out var resource) || key.GetValueKind(name) != RegistryValueKind.MultiString ||
                key.GetValue(name) is not string[] ids || ids.Any(id => !Guid.TryParseExact(id, "B", out _)))
                throw new InvalidDataException("Managed volume associations require recovery.");
            if (!ids.Contains(volumeId, StringComparer.OrdinalIgnoreCase)) continue;
            if (owner is not null && owner != resource) throw new IOException("More than one managed resource claims this volume; reconcile ownership first.");
            owner = resource;
        }
        return owner;
    }
    public static void RegisterVolume(Guid resource, string volume)
    {
        using var gate = EnterPolicyGate(); var id = VolumeIds.Parse(volume);
        var owner = OwnerOfVolume(id);
        if (owner is not null && owner != resource) throw new IOException("This volume belongs to another remembered managed resource.");
        using var key = Registry.LocalMachine.CreateSubKey(VolumeOwnership, true);
        var prior = key.GetValue(resource.ToString("N")) as string[] ?? [];
        key.SetValue(resource.ToString("N"), prior.Append(id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), RegistryValueKind.MultiString);
        key.Flush();
        // Migrate startup ownership only after this managed association is durable.
        SavedConfigurations.RemoveManagedVolume(id);
    }
    internal static IDisposable EnterPolicyGate() => ConfigurationGate.Enter("QueueCache.ManagedImageHosts");
    public static void ValidateCacheChange(string volumeId, CacheConfiguration configuration)
    {
        if (!configuration.Enabled || configuration.Preset != CachePreset.Fast) return;
        using var key = Registry.LocalMachine.OpenSubKey(Dependencies);
        if (key is null) return;
        foreach (var name in key.GetValueNames())
        {
            if (!Guid.TryParseExact(name, "N", out _) || key.GetValueKind(name) != RegistryValueKind.MultiString || key.GetValue(name) is not string[] hosts)
                throw new InvalidDataException("Managed image host dependency catalog requires recovery.");
            if (hosts.Contains(volumeId, StringComparer.OrdinalIgnoreCase))
                throw new IOException("This volume hosts a remembered managed image/checkpoint. Its cache must remain Strict or disabled until the managed resource is removed.");
        }
    }
    private sealed class Pins(List<SafeFileHandle> handles) : IDisposable { public void Dispose() { foreach (var handle in handles) handle.Dispose(); } }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder name, uint size, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumePathNameW(string path, StringBuilder name, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeNameForVolumeMountPointW(string mount, StringBuilder name, uint size);
}
