using System.Buffers.Binary;
using System.Text;

namespace WadSmith.Core.Quake;

/// <summary>
/// A Quake PAK file (id1/PAK0.PAK, PAK1.PAK): "PACK", the table's offset and length, then a table of 64-byte entries — a
/// 56-byte path and the file's offset and size. Read in place, never changed.
/// </summary>
public sealed class PakArchive
{
    private readonly string _path;
    private readonly Dictionary<string, (long Offset, int Size)> _files = new(StringComparer.OrdinalIgnoreCase);

    private PakArchive(string path) => _path = path;

    /// <summary>The paths of the files inside, in their order ("maps/e1m1.bsp", "gfx/palette.lmp").</summary>
    public IReadOnlyList<string> Names { get; private set; } = [];

    /// <summary>Whether the file at <paramref name="path"/> starts with a PAK file's signature.</summary>
    public static bool IsPak(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return f.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4 && head.SequenceEqual("PACK"u8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static PakArchive Open(string path)
    {
        using var f = File.OpenRead(path);
        var head = new byte[12];
        if (f.ReadAtLeast(head, 12, throwOnEndOfStream: false) < 12 || !head.AsSpan(0, 4).SequenceEqual("PACK"u8)) throw new CorruptDataException($"{Path.GetFileName(path)} is not a Quake PAK file.");
        var tableAt = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4));
        var tableSize = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(8));
        if (tableAt < 12 || tableSize < 0 || tableSize % 64 != 0 || tableAt + (long)tableSize > f.Length) throw new CorruptDataException($"{Path.GetFileName(path)}'s table of files lies outside it.");
        var table = new byte[tableSize];
        f.Position = tableAt;
        f.ReadExactly(table);
        var pak = new PakArchive(path);
        var names = new List<string>(tableSize / 64);
        for (var i = 0; i < tableSize; i += 64)
        {
            var name = Encoding.ASCII.GetString(table, i, 56).Split('\0')[0];
            var offset = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(i + 56));
            var size = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(i + 60));
            if (offset < 0 || size < 0 || offset + (long)size > f.Length) throw new CorruptDataException($"{Path.GetFileName(path)}: {name} runs past the end of the file.");
            pak._files[name] = (offset, size);
            names.Add(name);
        }

        pak.Names = names;
        return pak;
    }

    /// <summary>The bytes of the file <paramref name="name"/> inside, or null when there is none.</summary>
    public byte[]? Read(string name)
    {
        if (!_files.TryGetValue(name, out var at)) return null;
        using var f = File.OpenRead(_path);
        f.Position = at.Offset;
        var data = new byte[at.Size];
        f.ReadExactly(data);
        return data;
    }
}
