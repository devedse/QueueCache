using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using QueueCache.Operations;

namespace QueueCache.Developer.Verification;

public sealed record NativeTraceSymbol(string ModulePath, string FileSha256, string Pdb, string PdbSha256, Guid Signature, int Age);

/// <summary>Preflight matches native PDB GUID/age to the actual loaded module's PE debug record.</summary>
public static class NativeTraceSymbols
{
    public static NativeTraceSymbol[] Capture(string directory)
    {
        RamReadAttribution.RequireSymbols(directory);
        var loaded = LoadedDriverInspection.Capture();
        if (!loaded.Available || loaded.Modules.Count != 2 || loaded.Modules.Any(m => m.FileSha256 is null || m.FileError is not null))
            throw new InvalidDataException("Actual loaded filter/provider identity is unavailable.");
        return loaded.Modules.Select(module =>
        {
            using var file = File.OpenRead(module.FilePath);
            using var pe = new PEReader(file);
            var entry = pe.ReadDebugDirectory().SingleOrDefault(e => e.Type == DebugDirectoryEntryType.CodeView);
            if (entry.Type != DebugDirectoryEntryType.CodeView) throw new InvalidDataException("Native module has no single CodeView record.");
            var codeView = pe.ReadCodeViewDebugDirectoryData(entry);
            var name = Path.GetFileName(codeView.Path);
            if (name is not ("qcachelab.pdb" or "qcramdisk.pdb")) throw new InvalidDataException("Unexpected native PDB identity.");
            var path = Path.Combine(directory, name); var pdb = File.ReadAllBytes(path);
            var signature = ReadPdbIdentity(pdb);
            if (signature.Guid != codeView.Guid || signature.Age != codeView.Age)
                throw new InvalidDataException("PDB GUID/age does not match loaded native module: " + name);
            return new NativeTraceSymbol(module.ModulePath, module.FileSha256!, name,
                Convert.ToHexString(SHA256.HashData(pdb)), signature.Guid, signature.Age);
        }).ToArray();
    }

    public static (Guid Guid, int Age) ReadPdbIdentity(byte[] data)
    {
        if (data.Length < 56 || !data.AsSpan(0, 32).SequenceEqual("Microsoft C/C++ MSF 7.00\r\n\u001aDS\0\0\0"u8))
            throw new InvalidDataException("Expected native MSF 7 PDB.");
        int Number(int offset)
        {
            if (offset < 0 || offset > data.Length - 4) throw new InvalidDataException("PDB directory bounds.");
            return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        }
        var page = Number(32); var directoryBytes = Number(44); var map = Number(52);
        if (page is < 512 or > 65536 || (page & (page - 1)) != 0 || directoryBytes < 12 || directoryBytes > 16 * 1024 * 1024 ||
            map < 0 || (long)map * page > data.Length - page) throw new InvalidDataException("Invalid PDB geometry.");
        var pages = (directoryBytes + page - 1) / page;
        if (pages > page / 4) throw new InvalidDataException("Unsupported PDB directory map.");
        var directory = new byte[pages * page];
        for (var i = 0; i < pages; i++)
        {
            var block = Number(map * page + i * 4);
            if (block < 0 || (long)block * page > data.Length - page) throw new InvalidDataException("PDB block bounds.");
            data.AsSpan(block * page, page).CopyTo(directory.AsSpan(i * page));
        }
        int D(int offset)
        {
            if (offset < 0 || offset > directoryBytes - 4) throw new InvalidDataException("PDB stream directory bounds.");
            return BinaryPrimitives.ReadInt32LittleEndian(directory.AsSpan(offset, 4));
        }
        var streams = D(0);
        if (streams < 2 || streams > (directoryBytes - 4) / 4) throw new InvalidDataException("Missing PDB information stream.");
        var firstBytes = D(4); var infoBytes = D(8);
        if (firstBytes < -1 || infoBytes < 28) throw new InvalidDataException("Invalid PDB information stream.");
        var firstPages = firstBytes <= 0 ? 0 : (int)(((long)firstBytes + page - 1) / page);
        var infoBlock = D(4 + streams * 4 + firstPages * 4);
        if (infoBlock < 0 || (long)infoBlock * page > data.Length - page) throw new InvalidDataException("PDB information block bounds.");
        var offset = infoBlock * page;
        return (new Guid(data.AsSpan(offset + 12, 16)), Number(offset + 8));
    }
}
