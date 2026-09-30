using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace QueueCache.Operations;

public sealed record RemovalVolumeExtent(string Instance, int DiskNumber, long Start, long Length);

public sealed record RemovalRelationQuery(string Instance, uint Filter, uint ConfigurationManagerResult, string[]? Devices);

/// <summary>Resolve at most one parent, with explicit ownership and removal relations.
/// A shared adapter, unknown descendant or external removal relation is never selected.</summary>
internal static class DeviceRemovalScope
{
    private const string StorageAdapterClass = "{4d36e97b-e325-11ce-bfc1-08002be10318}";
    private const string DiskClass = "{4d36e967-e325-11ce-bfc1-08002be10318}";
    private const string VolumeClass = "{71a27cdd-812a-11d0-bec7-08002be2092f}";
    private const uint Present = 0x100, EjectRelations = 4, RemovalRelations = 8;
    internal static void ValidateVolumeExtent(int diskNumber, long diskBytes, (int Number, long Start, long Length) extent)
    {
        if (diskBytes <= 0 || extent.Number != diskNumber || extent.Start < 0 || extent.Length <= 0 ||
            extent.Length > diskBytes || extent.Start > diskBytes - extent.Length)
            throw new NotSupportedException("A related volume does not belong entirely to the selected physical disk.");
    }

    internal sealed record Scope(string Instance, string[] Members, RemovalRelationQuery[] Relations, RemovalVolumeExtent[] Volumes);

    internal static bool IsDedicatedAdapter(string classGuid, uint capabilities, string[] children, string disk) =>
        classGuid.Equals(StorageAdapterClass, StringComparison.OrdinalIgnoreCase) && (capabilities & 2) != 0 &&
        children.Length == 1 && children[0].Equals(disk, StringComparison.OrdinalIgnoreCase);

    internal static void ValidateRelations(IReadOnlySet<string> owned, IEnumerable<string> related)
    {
        var outside = related.Where(id => !owned.Contains(id)).ToArray();
        if (outside.Length > 0)
            throw new NotSupportedException("Windows removal relations include devices outside this disk's verified scope: " + string.Join(", ", outside));
    }

    [SupportedOSPlatform("windows")]
    internal static Scope Resolve(uint diskNode, string disk, uint diskCapabilities, int diskNumber, long diskBytes)
    {
        if (!ClassGuid(diskNode).Equals(DiskClass, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("The selected device is no longer a disk devnode.");
        var root = disk;
        // A storage adapter may represent exactly one disk (e.g. VirtIO block).
        // Do not walk through a bridge or select a controller with another child.
        if (CM_Get_Parent(out var parent, diskNode, 0) == 0)
        {
            var classGuid = ClassGuid(parent);
            if (classGuid.Equals(StorageAdapterClass, StringComparison.OrdinalIgnoreCase))
            {
                var parentId = Id(parent);
                var children = Children(parent);
                if (IsDedicatedAdapter(classGuid, Capabilities(parent), children, disk))
                    root = parentId;
            }
        }
        if (root.Equals(disk, StringComparison.OrdinalIgnoreCase) && (diskCapabilities & 6) == 0)
            throw new NotSupportedException("Windows does not identify this disk as removable or ejectable.");
        var volumes = new Dictionary<string, RemovalVolumeExtent>(StringComparer.OrdinalIgnoreCase);
        void VerifyVolume(string instance)
        {
            var extent = DiskTarget.ReadVolumeInstanceExtent(instance);
            ValidateVolumeExtent(diskNumber, diskBytes, extent);
            volumes[instance] = new(instance, extent.Number, extent.Start, extent.Length);
        }
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        var pending = new Queue<string>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var current))
        {
            if (owned.Count > 256)
                throw new NotSupportedException("The removal device tree is too large to verify as one disk.");
            Locate(current, out var currentNode);
            foreach (var child in Children(currentNode))
            {
                Locate(child, out var node);
                var classGuid = ClassGuid(node);
                if (!(child.Equals(disk, StringComparison.OrdinalIgnoreCase) && classGuid.Equals(DiskClass, StringComparison.OrdinalIgnoreCase)) &&
                    !classGuid.Equals(VolumeClass, StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("The removal device tree includes an unknown or additional device.");
                if (classGuid.Equals(VolumeClass, StringComparison.OrdinalIgnoreCase))
                    VerifyVolume(child);
                if (owned.Add(child)) pending.Enqueue(child);
            }
        }
        if (!owned.Contains(disk))
            throw new NotSupportedException("The removal device tree no longer contains the selected disk.");
        var evidence = new List<RemovalRelationQuery>();
        pending = new Queue<string>(owned);
        while (pending.TryDequeue(out var member))
        {
            if (owned.Count > 256) throw new NotSupportedException("The removal relations are too large to verify as one disk.");
            foreach (var kind in new[] { EjectRelations, RemovalRelations })
            {
                var query = Relations(member, kind);
                evidence.Add(query);
                // CR_NO_SUCH_VALUE is the absence of this optional relation
                // property, not a failed device lookup. Preserve the API result
                // and null Devices in the preview instead of inventing a count.
                if (query.Devices is not null)
                {
                    foreach (var related in query.Devices.Where(id => !owned.Contains(id)))
                    {
                        Locate(related, out var relatedNode);
                        if (!ClassGuid(relatedNode).Equals(VolumeClass, StringComparison.OrdinalIgnoreCase))
                            throw new NotSupportedException("Removal relations include a device outside the selected disk.");
                        VerifyVolume(related);
                        // Volume-manager devnodes need not be physical disk children.
                        // Native extents, never instance-name parsing, establish ownership.
                        if (Children(relatedNode).Length != 0)
                            throw new NotSupportedException("A related volume has unverified child devices.");
                        if (owned.Add(related)) pending.Enqueue(related);
                    }
                    ValidateRelations(owned, query.Devices);
                }
            }
        }
        return new(root, owned.Order(StringComparer.OrdinalIgnoreCase).ToArray(), evidence.ToArray(), volumes.Values.OrderBy(v => v.Instance, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static string[] Children(uint parent)
    {
        var children = new List<string>();
        var result = CM_Get_Child(out var node, parent, 0);
        while (result == 0)
        {
            if (children.Count >= 256) throw new IOException("The removal device tree has too many children.");
            children.Add(Id(node));
            result = CM_Get_Sibling(out node, node, 0);
        }
        if (result != 0x0D) Check(result, "device children");
        return children.ToArray();
    }

    internal static RemovalRelationQuery DecodeRelations(string instance, uint kind, uint result, char[]? buffer)
    {
        if (result == 0x25) return new(instance, kind, result, null); // Optional property absent.
        Check(result, $"relations {kind} for {instance}");
        if (buffer is null || buffer.Length == 0 || buffer[^1] != '\0' ||
            buffer.Length > 1 && buffer[^2] != '\0')
            throw new IOException("Windows returned unterminated removal relations.");
        return new(instance, kind, 0, new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries));
    }
    private static RemovalRelationQuery Relations(string instance, uint kind)
    {
        var flags = kind | Present;
        var result = CM_Get_Device_ID_List_SizeW(out var length, instance, flags);
        if (result == 0x25) return DecodeRelations(instance, kind, result, null);
        Check(result, $"relation size {kind} for {instance}");
        if (length == 0 || length > 65536)
            throw new IOException("Windows returned an invalid removal relation length.");
        var buffer = new char[length];
        result = CM_Get_Device_ID_ListW(instance, buffer, length, flags);
        return DecodeRelations(instance, kind, result, buffer);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid format, uint id)
    {
        public Guid Format = format;
        public uint Id = id;
    }
    private static string ClassGuid(uint node)
    {
        var key = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 10);
        uint bytes = 16;
        Check(CM_Get_ClassGuid(node, ref key, out var type, out var value, ref bytes, 0), $"class GUID for node {node}");
        if (type != 13 || bytes != 16) throw new IOException("Windows returned an invalid removal device class GUID.");
        return value.ToString("B");
    }
    private static uint Capabilities(uint node)
    {
        uint bytes = 4;
        Check(CM_Get_Capabilities(node, 0x10, out var type, out var value, ref bytes, 0), $"capabilities for node {node}");
        if (type != 4 || bytes != 4) throw new IOException("Windows returned invalid removal capabilities.");
        return value;
    }
    private static string Id(uint node)
    {
        var value = new StringBuilder(256);
        Check(CM_Get_Device_IDW(node, value, (uint)value.Capacity, 0));
        return value.ToString();
    }
    private static void Locate(string id, out uint node) => Check(CM_Locate_DevNodeW(out node, id, 0));
    private static void Check(uint result, string? operation = null)
    {
        if (result != 0) throw new IOException($"Windows removal topology could not be verified ({operation ?? "device identity"}, Configuration Manager {result}).");
    }
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Child(out uint child, uint node, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Sibling(out uint sibling, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_IDW(uint node, StringBuilder id, uint length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint length, string instance, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_ID_ListW(string instance,
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2, SizeParamIndex = 2)] char[] buffer, uint length, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW", ExactSpelling = true)]
    private static extern uint CM_Get_ClassGuid(uint node, ref PropertyKey key, out uint type, out Guid value, ref uint length, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Registry_PropertyW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Capabilities(uint node, uint property, out uint type, out uint value, ref uint length, uint flags);
}
